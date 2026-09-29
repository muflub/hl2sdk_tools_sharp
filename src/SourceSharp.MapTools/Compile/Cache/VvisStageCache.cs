//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The whole vvis stage from the store: the portal text, the BSP lumps vvis
/// reads, the options that change its output and the fog radius it may take
/// from the entities. A light or entity edit changes none of them, so the
/// recompile skips vis entirely.
/// </summary>
/// <remarks>
/// <para>
/// A row holds what vvis leaves behind: the visibility, leaf and
/// water-distance lumps it writes, and the uncompressed PVS/PAS rows and
/// counters its <see cref="VisResult"/> reports. A replay writes the same
/// lumps and returns a result with the same rows; only the work counters
/// (how much flow the build did) read as zero.
/// </para>
/// <para>
/// Not used with <c>-trace</c>, whose result is a debugging run, not the
/// stage's product.
/// </para>
/// </remarks>
public sealed class VvisStageCache
{
    /// <summary>The stage name the rows carry.</summary>
    public const string StageName = "vvis";

    // The lumps vvis reads (VisLeaves, the water distances, the emptiness check).
    private static ReadOnlySpan<BspLump> InputLumps =>
    [
        BspLump.Nodes, BspLump.Faces, BspLump.Leafs, BspLump.LeafFaces,
        BspLump.Edges, BspLump.SurfEdges, BspLump.Vertexes, BspLump.TexInfo,
    ];

    private readonly ICacheStore _store;
    private readonly CachePolicy _policy;
    private readonly IReadOnlyList<string> _contextTags;
    private readonly CacheRunCounters _counters;

    /// <summary>
    /// The run's generation stamp (Unix milliseconds) every row and blob this
    /// seam stages carries; the time the seam was built unless the chain sets
    /// it.
    /// </summary>
    /// <remarks>
    /// The chain gives all of a compile's seams its one start stamp and
    /// records it as the store's generation on commit, so the GC can tell
    /// which rows the newest <see cref="CachePolicy.GenerationsKept"/>
    /// compiles made (<see cref="CacheCollector"/>). Seams built each with
    /// their own clock reading would scatter one compile over several stamps.
    /// </remarks>
    public long CreatedAtMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Builds the seam over one store for one run.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="policy">Size/mode policy.</param>
    /// <param name="contextTags">The host's opaque tags, folded verbatim.</param>
    /// <param name="counters">The run's report counters.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public VvisStageCache(ICacheStore store, CachePolicy policy, IReadOnlyList<string> contextTags, CacheRunCounters counters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contextTags);
        ArgumentNullException.ThrowIfNull(counters);

        _store = store;
        _policy = policy;
        _contextTags = contextTags;
        _counters = counters;
    }

    /// <summary>Whether a run with these options may use the cache at all.</summary>
    /// <param name="options">The vvis options.</param>
    /// <returns>False under <c>-trace</c>.</returns>
    public static bool Applies(VvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Trace is null;
    }

    /// <summary>The digest of everything vvis reads.</summary>
    /// <param name="portalText">The <c>.prt</c> bytes vvis parses.</param>
    /// <param name="bsp">The map as vbsp left it.</param>
    /// <param name="options">The vvis options.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string InputDigest(ReadOnlySpan<byte> portalText, BspData bsp, VvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(options);

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] word = new byte[8];
        void Long(long v)
        {
            BinaryPrimitives.WriteInt64LittleEndian(word, v);
            sha.AppendData(word);
        }

        void Bytes(ReadOnlySpan<byte> data)
        {
            Long(data.Length);
            sha.AppendData(data);
        }

        void Str(string? text)
        {
            Long(text is null ? -1 : 1);
            Bytes(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        Str("vvis/1");
        Bytes(portalText);
        foreach (BspLump lump in InputLumps)
        {
            BspLumpData data = bsp[lump];
            Long((int)lump);
            Long(data.Version);
            Bytes(data.Data.Span);
        }

        Long(options.Fast ? 1 : 0);
        Long(options.NoSort ? 1 : 0);
        Long(options.Tighten ? 1 : 0);
        Long(options.RadiusOverride is float radius ? BitConverter.SingleToInt32Bits(radius) : long.MinValue);
        Str(OptionsDigest.Of(options.Compliance));

        // Without an override, the first fog controller's farz is the radius.
        Str(options.RadiusOverride is null ? FirstFogFarZ(bsp) : null);

        // Appended only when on, so every digest a compile without it ever
        // stored stays the key it was: -fastflow writes a different PVS, and
        // an exact compile must never be served one (or the reverse).
        if (options.FastFlow)
        {
            Str("fastflow");
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static string? FirstFogFarZ(BspData bsp)
    {
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (string.Equals(entity.ClassName, "env_fog_controller", StringComparison.OrdinalIgnoreCase))
            {
                return entity.Get("farz") ?? string.Empty;
            }
        }

        return null;
    }

    /// <summary>The full key under the shared <see cref="CacheKey"/> fold.</summary>
    /// <param name="inputDigest"><see cref="InputDigest"/>.</param>
    /// <returns>The key.</returns>
    internal CacheKey KeyOf(string inputDigest) => new()
    {
        Stage = StageName,
        ToolId = ToolIdentity.Current,
        SemanticDigest = inputDigest,
        OptionsDigest = string.Empty,
        DependencyDigest = string.Empty,
        ContextTags = _contextTags,
    };

    /// <summary>Replays vvis onto <paramref name="bsp"/>; null (and the map untouched) on a miss.</summary>
    /// <param name="inputDigest"><see cref="InputDigest"/> of this compile.</param>
    /// <param name="bsp">The map to write the vis lumps into.</param>
    /// <param name="portalCount">The portal count of this compile's portal file (a self-check).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The replayed result, or null.</returns>
    public async ValueTask<VisResult?> TryGetAsync(string inputDigest, BspData bsp, int portalCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputDigest);
        ArgumentNullException.ThrowIfNull(bsp);
        if (!_policy.Reads || !_store.IsUsable)
        {
            _counters.SkipRead();
            return null;
        }

        CacheKey key = KeyOf(inputDigest);
        CacheRecord? record = await _store.LookupAsync(key.Digest, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _counters.StageMiss(StageName);
            return null;
        }

        Dictionary<string, byte[]> blobs = [];
        bool intact = record.Stage == key.Stage
            && record.ToolId == key.ToolId
            && record.Key == key.Digest
            && record.Parts.SequenceEqual(key.Parts)
            && record.ContextTags.SequenceEqual(key.ContextTags);
        foreach (string role in (string[])["result", "visibility", "leafs", "water", "pvs", "pas"])
        {
            if (!intact)
            {
                break;
            }

            byte[]? data = record.Blobs.TryGetValue(role, out string? blobKey)
                ? await ReadCheckedAsync(blobKey, cancellationToken).ConfigureAwait(false)
                : null;
            if (data is null)
            {
                intact = false;
                break;
            }

            blobs[role] = data;
        }

        VisResult? result = intact ? Decode(blobs, portalCount) : null;
        if (result is null)
        {
            _counters.Corrupt();
            _counters.StageMiss(StageName);
            return null;
        }

        int leafVersion = BinaryPrimitives.ReadInt32LittleEndian(blobs["result"].AsSpan(52));
        bsp.SetLump(BspLump.Visibility, blobs["visibility"]);
        bsp[BspLump.Leafs] = new BspLumpData(blobs["leafs"], leafVersion, 0);
        bsp.SetLump(BspLump.LeafMinDistToWater, blobs["water"]);

        await CacheBlobReader.RenewAsync(_store, _policy, record, CreatedAtMs, cancellationToken).ConfigureAwait(false);
        _counters.StageHit(StageName, blobs.Values.Sum(b => (long)b.Length), record.CostMs);
        return result;
    }

    /// <summary>Stages this compile's vvis products (call after vvis wrote its lumps).</summary>
    /// <param name="inputDigest"><see cref="InputDigest"/>, taken before vvis ran.</param>
    /// <param name="bsp">The map as vvis left it.</param>
    /// <param name="result">vvis's result.</param>
    /// <param name="costMs">How long vvis took.</param>
    /// <param name="cancellationToken">Cancels the staging.</param>
    /// <returns>A task that completes when the row is staged.</returns>
    public async ValueTask StoreAsync(string inputDigest, BspData bsp, VisResult result, long costMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputDigest);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(result);
        if (!_policy.Writes || !_store.IsUsable)
        {
            _counters.SkipWrite();
            return;
        }

        byte[] pvs = new byte[result.ClusterCount * result.RowBytes];
        byte[] pas = new byte[pvs.Length];
        for (int c = 0; c < result.ClusterCount; c++)
        {
            result.Pvs(c).CopyTo(pvs.AsSpan(c * result.RowBytes));
            result.Pas(c).CopyTo(pas.AsSpan(c * result.RowBytes));
        }

        Dictionary<string, byte[]> payload = new()
        {
            ["result"] = EncodeResult(result, bsp[BspLump.Leafs].Version),
            ["visibility"] = bsp[BspLump.Visibility].Data.ToArray(),
            ["leafs"] = bsp[BspLump.Leafs].Data.ToArray(),
            ["water"] = bsp[BspLump.LeafMinDistToWater].Data.ToArray(),
            ["pvs"] = pvs,
            ["pas"] = pas,
        };

        if (payload.Values.Any(b => b.LongLength > _policy.MaxBlobBytes))
        {
            _counters.SkipWrite();
            return;
        }

        CacheKey key = KeyOf(inputDigest);
        Dictionary<string, string> blobs = [];
        long bytes = 0;
        foreach ((string role, byte[] data) in payload)
        {
            string blobKey = CacheKey.HashBytes(data);
            if (!await _store.HasBlobAsync(blobKey, cancellationToken).ConfigureAwait(false))
            {
                await _store.PutBlobAsync(blobKey, data, ToolIdentity.Current, CreatedAtMs, cancellationToken).ConfigureAwait(false);
            }

            blobs[role] = blobKey;
            bytes += data.Length;
        }

        await _store.PutAsync(
            new CacheRecord(key.Digest, key.Stage, key.ToolId, key.ContextTags, key.Parts, blobs, [], costMs, CreatedAtMs),
            cancellationToken).ConfigureAwait(false);
        _counters.Stored(bytes);
    }

    // The counters, 56 bytes: clusters, portals, row bytes, vis size, visible,
    // optimized, audible, radius flag, deepest (ints), radius squared (double
    // bits), the leaf lump version.
    private static byte[] EncodeResult(VisResult r, int leafVersion)
    {
        byte[] b = new byte[56];
        int[] ints = [r.ClusterCount, r.PortalCount, r.RowBytes, r.VisDataSize, r.TotalVisibleClusters,
            r.OptimizedClusters, r.TotalAudibleClusters, r.UsedRadius ? 1 : 0, r.DeepestFlow];
        for (int i = 0; i < ints.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4 * i), ints[i]);
        }

        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(36), r.VisRadiusSquared);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(52), leafVersion);
        return b;
    }

    private static VisResult? Decode(Dictionary<string, byte[]> blobs, int portalCount)
    {
        byte[] b = blobs["result"];
        if (b.Length != 56)
        {
            return null;
        }

        int Int(int i) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4 * i));
        int clusters = Int(0);
        int rowBytes = Int(2);
        if (Int(1) != portalCount
            || clusters < 0 || rowBytes != (clusters + 7) >> 3
            || blobs["pvs"].LongLength != (long)clusters * rowBytes
            || blobs["pas"].LongLength != (long)clusters * rowBytes
            || Int(3) != blobs["visibility"].Length)
        {
            return null;
        }

        return new VisResult(
            clusters, Int(1), rowBytes, blobs["pvs"], blobs["pas"], Int(3), Int(4), Int(5), Int(6),
            Int(7) != 0, BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(36)), Int(8), VisWorkCounters.Zero, trace: null);
    }

    private ValueTask<byte[]?> ReadCheckedAsync(string blobKey, CancellationToken cancellationToken) =>
        CacheBlobReader.ReadAsync(_store, _policy, blobKey, cancellationToken);
}
