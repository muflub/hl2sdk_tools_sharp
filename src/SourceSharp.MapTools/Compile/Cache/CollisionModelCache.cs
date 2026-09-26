//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Options;

using System.Globalization;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The store-backed per-model cooked-collision cache:
/// content-hash keys, self-checking replay, report counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key.</b> <c>vbsp.collision.model/world</c> or <c>/brush</c> under the shared
/// <see cref="CacheKey"/> fold: the tool identity with the cooker inside
/// (two builds of <c>vphysics.so</c> cook one shape differently), the semantic
/// digest of everything THIS model's cook reads
/// (<see cref="CollisionModelKey.OfModel"/>), the option digest (compliance,
/// no-virtual-mesh, merge/shrink constants), and the host's opaque context
/// tags. Never mtimes, never paths, and for a brush model no model or brush
/// number: an identical brush entity hits wherever the BSP numbered it.
/// </para>
/// <para>
/// <b>Brush numbers.</b> The cook stores each convex's absolute brush index
/// as its game data. A brush model's row keeps the lowest brush number it
/// was cooked at (the <c>base</c> blob); a replay moves the game data by the
/// difference (<see cref="CollisionGameData.Rebase"/>) and checks every
/// moved number lands in the model's current brush set, else misses.
/// </para>
/// <para>
/// <b>Poisoning.</b> Every blob read back is re-hashed against its
/// content-address key before a byte is trusted; the record's parts, stage,
/// tool identity and context tags are compared against the freshly computed
/// key; a model-0 row must carry the world extras and a brush row must not.
/// Anything suspect counts as a miss — a dropped cache is a miss, never a
/// wrong hit — and the corruption counter records that a row existed and lied.
/// </para>
/// </remarks>
public sealed class CollisionModelCache : ICollisionModelCache
{
    /// <summary>The stage-name prefix these rows carry (the model index completes it).</summary>
    public const string StageName = "vbsp.collision.model";

    private readonly ICacheStore _store;
    private readonly CachePolicy _policy;
    private readonly string _cookerIdentity;
    private readonly IReadOnlyList<string> _contextTags;
    private readonly CacheRunCounters _counters;
    private readonly long _createdAtMs;

    /// <summary>Builds the seam over one store for one run.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="policy">Size/mode policy.</param>
    /// <param name="cookerIdentity">The cooker this run cooks with (part of the tool identity).</param>
    /// <param name="contextTags">The host's opaque tags (preset etc.), folded verbatim.</param>
    /// <param name="counters">The run's report counters.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CollisionModelCache(
        ICacheStore store,
        CachePolicy policy,
        string cookerIdentity,
        IReadOnlyList<string> contextTags,
        CacheRunCounters counters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(cookerIdentity);
        ArgumentNullException.ThrowIfNull(contextTags);
        ArgumentNullException.ThrowIfNull(counters);

        _store = store;
        _policy = policy;
        _cookerIdentity = cookerIdentity;
        _contextTags = contextTags;
        _counters = counters;
        _createdAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <inheritdoc />
    public async ValueTask<CachedCollisionModel?> TryGetAsync(
        PhysCollisionInput input,
        int modelIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_policy.Reads || !_store.IsUsable)
        {
            _counters.SkipRead();
            return null;
        }

        CacheKey key = KeyOf(input, modelIndex);
        CacheRecord? record = await _store.LookupAsync(key.Digest, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _counters.Miss(modelIndex);
            return null;
        }

        CachedCollisionModel? model = await ReplayAsync(record, input, modelIndex, key, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        {
            _counters.Corrupt();
            _counters.Miss(modelIndex);
            return null;
        }

        _counters.Hit(modelIndex, model.Bytes, record.CostMs);
        return model;
    }

    /// <inheritdoc />
    public async ValueTask CookedAsync(
        PhysCollisionInput input,
        int modelIndex,
        CachedCollisionModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);

        if (!_policy.Writes || !_store.IsUsable || model.Bytes > _policy.MaxBlobBytes)
        {
            _counters.SkipWrite();
            return;
        }

        CacheKey key = KeyOf(input, modelIndex);
        Dictionary<string, string> blobs = [];
        if (modelIndex != 0)
        {
            // A brush row is replayable only if its brush numbers can be moved.
            SortedSet<int> brushes = CollisionModelKey.BrushesOf(input, modelIndex);
            if (!GameDataWithin(model.Solids, brushes))
            {
                _counters.SkipWrite();
                return;
            }

            byte[] baseBytes = new byte[4];
            BinaryPrimitivesWrite32(baseBytes, 0, brushes.Count > 0 ? brushes.Min : 0);
            string baseKey = CacheKey.HashBytes(baseBytes);
            await StageBlobAsync(baseKey, baseBytes, cancellationToken).ConfigureAwait(false);
            blobs["base"] = baseKey;
        }

        for (int i = 0; i < model.Solids.Count; i++)
        {
            string blobKey = CacheKey.HashBytes(model.Solids[i]);
            await StageBlobAsync(blobKey, model.Solids[i], cancellationToken).ConfigureAwait(false);
            blobs[$"solid{i}"] = blobKey;
        }

        string textKey = CacheKey.HashBytes(model.Text);
        await StageBlobAsync(textKey, model.Text, cancellationToken).ConfigureAwait(false);
        blobs["text"] = textKey;

        if (model.LeafWaterDataIds is { } water)
        {
            byte[] bytes = new byte[water.Length * 2];
            for (int i = 0; i < water.Length; i++)
            {
                BinaryPrimitivesWrite16(bytes, i * 2, water[i]);
            }

            string blobKey = CacheKey.HashBytes(bytes);
            await StageBlobAsync(blobKey, bytes, cancellationToken).ConfigureAwait(false);
            blobs["water"] = blobKey;
        }

        if (model.WorldPropList is { } props)
        {
            byte[] bytes = new byte[props.Length * 4];
            for (int i = 0; i < props.Length; i++)
            {
                BinaryPrimitivesWrite32(bytes, i * 4, props[i]);
            }

            string blobKey = CacheKey.HashBytes(bytes);
            await StageBlobAsync(blobKey, bytes, cancellationToken).ConfigureAwait(false);
            blobs["props"] = blobKey;
        }

        if (model.PhysDisp is { } disp)
        {
            string blobKey = CacheKey.HashBytes(disp);
            await StageBlobAsync(blobKey, disp, cancellationToken).ConfigureAwait(false);
            blobs["disp"] = blobKey;
        }

        await _store.PutAsync(
            new CacheRecord(
                key.Digest,
                key.Stage,
                key.ToolId,
                key.ContextTags,
                key.Parts,
                blobs,
                [],
                CostMs: model.CostMs,
                CreatedAtMs: _createdAtMs),
            cancellationToken).ConfigureAwait(false);

        _counters.Stored(model.Bytes);
    }

    /// <summary>
    /// Publishes the run's staged rows and blobs atomically (a compile killed
    /// before this point leaves the store exactly as it found it), and records
    /// this run as a live generation for the collector's roots.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!_store.IsUsable)
        {
            return default;
        }

        return _store.CommitAsync(cancellationToken);
    }

    /// <summary>Throws away everything staged but not committed.</summary>
    public void DiscardPending() => _store.DiscardStaged();

    /// <summary>The counters this seam has been feeding (the report's data).</summary>
    public CacheRunCounters Counters => _counters;

    /// <summary>Folds one model's key from everything its cook reads.</summary>
    internal CacheKey KeyOf(PhysCollisionInput input, int modelIndex) => new()
    {
        Stage = StageName + (modelIndex == 0 ? "/world" : "/brush"),
        ToolId = ToolIdentity.Of(_cookerIdentity),
        SemanticDigest = CollisionModelKey.OfModel(input, modelIndex),
        OptionsDigest = OptionsDigest.Of(new CollisionCookingOptions(
            input.Compliance,
            input.NoVirtualMesh,
            modelIndex == 0,
            PhysCollisionEmitter.VPhysicsMerge,
            PhysCollisionEmitter.VPhysicsShrink,
            PhysCollisionEmitter.MaxMass)),
        DependencyDigest = string.Empty,
        ContextTags = _contextTags,
    };

    /// <summary>The option subset the collision cook reads (a value type so the reflection fold is stable).</summary>
    internal readonly record struct CollisionCookingOptions(
        ComplianceOptions Compliance,
        bool NoVirtualMesh,
        bool World,
        float MergeDistance,
        float ShrinkDistance,
        float MaxMass);

    private async ValueTask<CachedCollisionModel?> ReplayAsync(
        CacheRecord record,
        PhysCollisionInput input,
        int modelNumber,
        CacheKey key,
        CancellationToken cancellationToken = default)
    {
        if (record.Stage != key.Stage
            || record.ToolId != key.ToolId
            || !CacheKey.LooksLikeDigest(record.Key)
            || record.Key != key.Digest
            || !SequenceEquals(record.Parts, key.Parts)
            || !SequenceEquals(record.ContextTags, key.ContextTags))
        {
            return null;
        }

        if (!record.Blobs.TryGetValue("text", out string? textKey))
        {
            return null;
        }

        byte[]? text = await ReadCheckedAsync(textKey, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        int solidCount = 0;
        while (record.Blobs.ContainsKey($"solid{solidCount}"))
        {
            solidCount++;
        }

        List<byte[]> solids = new(solidCount);
        for (int i = 0; i < solidCount; i++)
        {
            byte[]? blob = await ReadCheckedAsync(record.Blobs[$"solid{i}"], cancellationToken).ConfigureAwait(false);
            if (blob is null)
            {
                return null;
            }

            solids.Add(blob);
        }

        short[]? water = null;
        if (record.Blobs.TryGetValue("water", out string? waterKey))
        {
            byte[]? bytes = await ReadCheckedAsync(waterKey, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length % 2 != 0)
            {
                return null;
            }

            water = new short[bytes.Length / 2];
            for (int i = 0; i < water.Length; i++)
            {
                water[i] = BinaryPrimitivesRead16(bytes, i * 2);
            }
        }

        int[]? props = null;
        if (record.Blobs.TryGetValue("props", out string? propsKey))
        {
            byte[]? bytes = await ReadCheckedAsync(propsKey, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length % 4 != 0)
            {
                return null;
            }

            props = new int[bytes.Length / 4];
            for (int i = 0; i < props.Length; i++)
            {
                props[i] = BinaryPrimitivesRead32(bytes, i * 4);
            }
        }

        byte[]? disp = null;
        if (record.Blobs.TryGetValue("disp", out string? dispKey))
        {
            disp = await ReadCheckedAsync(dispKey, cancellationToken).ConfigureAwait(false);
            if (disp is null)
            {
                return null;
            }
        }

        // A world row must carry the world extras; a brush row must not. A
        // mismatch is a key collision or a corrupted record — either way, miss.
        bool world = modelNumber == 0;
        if (world != (water is not null || props is not null))
        {
            return null;
        }

        if (!world)
        {
            // Move the brush numbers to where this BSP put the model.
            if (!record.Blobs.TryGetValue("base", out string? baseKey))
            {
                return null;
            }

            byte[]? baseBytes = await ReadCheckedAsync(baseKey, cancellationToken).ConfigureAwait(false);
            if (baseBytes is not { Length: 4 })
            {
                return null;
            }

            SortedSet<int> brushes = CollisionModelKey.BrushesOf(input, modelNumber);
            int delta = (brushes.Count > 0 ? brushes.Min : 0) - BinaryPrimitivesRead32(baseBytes, 0);
            if (delta != 0)
            {
                for (int i = 0; i < solids.Count; i++)
                {
                    if (CollisionGameData.Rebase(solids[i], delta) is not { } moved)
                    {
                        return null;
                    }

                    solids[i] = moved;
                }
            }

            if (!GameDataWithin(solids, brushes))
            {
                return null;
            }
        }

        return new CachedCollisionModel(modelNumber, solids, text, water, props, disp);
    }

    /// <summary>Reads a blob and re-hashes it against its content-address key; null fails the read.</summary>
    private async ValueTask<byte[]?> ReadCheckedAsync(string blobKey, CancellationToken cancellationToken = default)
    {
        if (!CacheKey.LooksLikeDigest(blobKey))
        {
            return null;
        }

        byte[]? data = await _store.GetBlobAsync(blobKey, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return null;
        }

        return CacheKey.HashBytes(data) == blobKey ? data : null;
    }

    private async ValueTask StageBlobAsync(string blobKey, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!await _store.HasBlobAsync(blobKey, cancellationToken).ConfigureAwait(false))
        {
            await _store.PutBlobAsync(blobKey, data, _cookerIdentity, _createdAtMs, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // Whether every convex's game data names one of the model's brushes.
    private static bool GameDataWithin(IReadOnlyList<byte[]> solids, SortedSet<int> brushes)
    {
        foreach (byte[] solid in solids)
        {
            if (!CollisionGameData.TryRead(solid, out List<uint>? gameData))
            {
                return false;
            }

            foreach (uint brush in gameData!)
            {
                if (brush > int.MaxValue || !brushes.Contains((int)brush))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SequenceEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void BinaryPrimitivesWrite16(byte[] buffer, int offset, short value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), value);

    private static void BinaryPrimitivesWrite32(byte[] buffer, int offset, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static short BinaryPrimitivesRead16(byte[] buffer, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2));

    private static int BinaryPrimitivesRead32(byte[] buffer, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
}

/// <summary>
/// One run's cache counters (the cache report's data: what was reused
/// and why the rest was not, bytes, estimated time saved net of cache
/// overhead). The run report renders these; the chain's own rows add stage
/// hits when the stage cache lands.
/// </summary>
public sealed class CacheRunCounters
{
    private readonly Dictionary<int, int> _missesByModel = [];

    /// <summary>Models replayed from the store.</summary>
    public int Hits { get; private set; }

    /// <summary>Models cooked fresh.</summary>
    public int Misses { get; private set; }

    /// <summary>Rows that existed and failed a self-check (poisoned, corrupted or foreign).</summary>
    public int CorruptRows { get; private set; }

    /// <summary>Total blob bytes replayed.</summary>
    public long BytesReused { get; private set; }

    /// <summary>Total blob bytes stored this run.</summary>
    public long BytesStored { get; private set; }

    /// <summary>Model cooks skipped, in ms (the saved estimate; store cost is the run's own timing line).</summary>
    public long EstimatedSavedMs { get; private set; }

    /// <summary>Lookups skipped for posture (read-off).</summary>
    public int ReadSkips { get; private set; }

    /// <summary>Writes skipped for posture/size.</summary>
    public int WriteSkips { get; private set; }

    /// <summary>Which models missed, for <c>cache explain</c>.</summary>
    public IReadOnlyDictionary<int, int> MissesByModel => _missesByModel;

    /// <summary>Records a replayed model.</summary>
    /// <param name="modelIndex">The model.</param>
    /// <param name="bytes">Blob bytes replayed.</param>
    /// <param name="costMs">The producing run's measured cost, when recorded.</param>
    public void Hit(int modelIndex, long bytes, long costMs)
    {
        ArgumentNullException.ThrowIfNull(this);
        Hits++;
        BytesReused += bytes;
        EstimatedSavedMs += Math.Max(0, costMs);
    }

    /// <summary>Records a fresh cook.</summary>
    /// <param name="modelIndex">The model.</param>
    public void Miss(int modelIndex)
    {
        ArgumentNullException.ThrowIfNull(this);
        Misses++;
        _missesByModel[modelIndex] = _missesByModel.GetValueOrDefault(modelIndex) + 1;
    }

    /// <summary>Records a row that lied.</summary>
    public void Corrupt() => CorruptRows++;

    /// <summary>Records a stored product.</summary>
    /// <param name="bytes">Its size.</param>
    public void Stored(long bytes)
    {
        ArgumentNullException.ThrowIfNull(this);
        BytesStored += bytes;
    }

    /// <summary>Records a lookup skipped for posture.</summary>
    public void SkipRead() => ReadSkips++;

    /// <summary>Records a write skipped for posture or size.</summary>
    public void SkipWrite() => WriteSkips++;
}
