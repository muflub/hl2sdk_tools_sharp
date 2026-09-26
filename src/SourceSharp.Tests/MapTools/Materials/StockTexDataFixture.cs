//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// One material where this port and stock vbsp disagree.
/// </summary>
/// <param name="Material">The material name out of TEXDATA_STRING_DATA.</param>
/// <param name="Stock">What stock wrote.</param>
/// <param name="Ours">What this port computed.</param>
public readonly record struct TexDataMismatch(string Material, string Stock, string Ours)
{
    /// <inheritdoc />
    public override string ToString() => $"{Material}: stock {Stock}, ours {Ours}";
}

/// <summary>
/// Runs the whole TEXDATA differential once, for a class of facts to assert on.
/// </summary>
/// <remarks>
/// <para>
/// Every material <c>dm_lockdown</c> references is resolved out of the
/// installed game content and compared against what stock vbsp wrote into
/// that map's TEXDATA lump. Hundreds of real materials, real VPKs, real
/// patches — and the expected values were produced by the compiler this code
/// is a port of, which is the only source of truth that is not another
/// reading of the same specification.
/// </para>
/// <para>
/// The map's own pak lump is mounted FIRST, because vbsp writes the patched
/// materials it generates (water, <c>WorldVertexTransition</c> blends) into
/// it and nothing else on the disk has them.
/// </para>
/// </remarks>
public sealed class StockTexDataFixture : IAsyncLifetime
{
    private const string GameInfo = "hl2mp/gameinfo.txt";

    /// <summary>
    /// The value writes into both axis offsets of
    /// an overlay's texinfo.
    /// </summary>
    private const float OverlaySentinel = -99999.0f;

    private ContentFileSystem? _game;
    private ContentFileSystem? _all;
    private ArchiveContentMount? _pak;
    private FrozenDictionary<string, MaterialFacts>? _facts;

    /// <summary>Why the differential could not run, or null.</summary>
    public string? SkipReason { get; private set; } = InstalledGameContent.SkipReason;

    /// <summary>How many TEXDATA entries the map holds.</summary>
    public int Compared { get; private set; }

    /// <summary>How many matched stock on reflectivity AND dimensions.</summary>
    public int Matched { get; private set; }

    /// <summary>The materials whose reflectivity differed.</summary>
    public ImmutableArray<TexDataMismatch> ReflectivityMismatches { get; private set; } = [];

    /// <summary>The materials whose dimensions differed.</summary>
    public ImmutableArray<TexDataMismatch> DimensionMismatches { get; private set; } = [];

    /// <summary>The materials that did not resolve at all.</summary>
    public ImmutableArray<string> NotFound { get; private set; } = [];

    /// <summary>
    /// The TEXINFO differential: how many texinfo entries were compared.
    /// </summary>
    public int TexInfoCompared { get; private set; }

    /// <summary>How many texinfo entries' SURF_ flags matched exactly.</summary>
    public int TexInfoMatched { get; private set; }

    /// <summary>The distinct disagreements on TEXINFO flags.</summary>
    public ImmutableArray<TexDataMismatch> TexInfoMismatches { get; private set; } = [];

    /// <summary>
    /// How many texinfo entries were skipped as overlays rather than compared.
    /// </summary>
    public int TexInfoOverlaysSkipped { get; private set; }

    /// <summary>
    /// Whether ANY texinfo in the map carries <c>SURF_TRIGGER</c>.
    /// </summary>
    public bool AnyTexInfoHasTheTriggerBit { get; private set; }

    /// <summary>
    /// The reflectivity this port computed for one material.
    /// </summary>
    /// <param name="material">The material name.</param>
    /// <returns>Its reflectivity, or zero when it was not read.</returns>
    public Vec3 OurReflectivity(string material) =>
        _facts is not null &&
        _facts.TryGetValue(MaterialFactsReader.Normalize(material), out MaterialFacts? facts)
            ? facts.Reflectivity
            : Vec3.Zero;

    /// <summary>The VTF header reflectivity of a texture in the install.</summary>
    /// <param name="texture">A content path under <c>materials/</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The header's value.</returns>
    public async Task<Vec3> TextureReflectivityAsync(
        string texture,
        CancellationToken cancellationToken = default)
    {
        using IMemoryOwner<byte>? owner =
            await _all!.ReadAsync(VPath.Create(texture), cancellationToken);

        return owner is null ? Vec3.Zero : VtfFile.Parse(owner.Memory.ToArray()).Reflectivity;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (SkipReason is not null)
        {
            return;
        }

        BspData bsp = await CompiledMap.LoadDmLockdownAsync();
        ImmutableArray<StockTexData> stock = CompiledMap.TexData(bsp);

        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        ReadOnlyFileSystem guarded = new(host);

        GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
            guarded,
            host.ToVirtualPath(Path.Combine(InstalledGameContent.BaseDirectory, GameInfo)),
            host.ToVirtualPath(InstalledGameContent.BaseDirectory));

        _game = mounted.Content;

        _pak = ArchiveContentMount.Mount(
            PakArchive.Open(bsp[BspLump.PakFile].Data, "dm_lockdown.bsp/pak"));

        // ownsMounts: false -- _game owns its own, and _pak is disposed here.
        _all = new ContentFileSystem([_pak, .. _game.Mounts], ownsMounts: false);

        FrozenDictionary<string, MaterialFacts> facts = await MaterialFactsReader.ReadManyAsync(
            stock.Select(static s => s.Name),
            _all);

        _facts = facts;
        Compare(stock, facts);
        CompareTexInfo(bsp, stock, facts);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_all is not null)
        {
            await _all.DisposeAsync();
        }

        if (_pak is not null)
        {
            await _pak.DisposeAsync();
        }

        if (_game is not null)
        {
            await _game.DisposeAsync();
        }
    }

    private void Compare(
        ImmutableArray<StockTexData> stock,
        FrozenDictionary<string, MaterialFacts> facts)
    {
        ImmutableArray<TexDataMismatch>.Builder reflectivity = ImmutableArray.CreateBuilder<TexDataMismatch>();
        ImmutableArray<TexDataMismatch>.Builder dimensions = ImmutableArray.CreateBuilder<TexDataMismatch>();
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();

        int matched = 0;

        foreach (StockTexData entry in stock)
        {
            MaterialFacts ours = facts[MaterialFactsReader.Normalize(entry.Name)];

            if (!ours.Found)
            {
                missing.Add(entry.Name);
            }

            bool reflectivityAgrees = Same(entry.Reflectivity, ours.Reflectivity);
            bool dimensionsAgree = entry.Width == ours.Width && entry.Height == ours.Height;

            if (!reflectivityAgrees)
            {
                reflectivity.Add(new TexDataMismatch(
                    entry.Name, Show(entry.Reflectivity), Show(ours.Reflectivity)));
            }

            if (!dimensionsAgree)
            {
                dimensions.Add(new TexDataMismatch(
                    entry.Name,
                    $"{entry.Width}x{entry.Height}",
                    $"{ours.Width}x{ours.Height}"));
            }

            if (reflectivityAgrees && dimensionsAgree)
            {
                matched++;
            }
        }

        Compared = stock.Length;
        Matched = matched;
        ReflectivityMismatches = reflectivity.ToImmutable();
        DimensionMismatches = dimensions.ToImmutable();
        NotFound = missing.ToImmutable();
    }

    private void CompareTexInfo(
        BspData bsp,
        ImmutableArray<StockTexData> stock,
        FrozenDictionary<string, MaterialFacts> facts)
    {
        // texinfo_t::flags is assigned from the brush side's, which
        // copied straight out of textureref[].flags -- and vbsp sets a
        // SURF_ bit nowhere else in the program. So FindMiptex's output IS
        // this lump's flags field, for every side that came from a brush.
        Dictionary<string, TexDataMismatch> disagreements = [];
        int compared = 0;
        int matched = 0;
        int overlays = 0;

        foreach (TexInfo entry in CompiledMap.TexInfo(bsp))
        {
            if ((uint)entry.TexData >= (uint)stock.Length)
            {
                continue;
            }

            if (IsOverlay(entry))
            {
                // An overlay's texinfo does not come from FindMiptex at all:
                // the overlay path builds one with
                // `texInfo.flags = 0` and the -99999 sentinel in both axis
                // offsets. Comparing it against a material classification
                // would be comparing against a constant.
                overlays++;
                continue;
            }

            string name = stock[entry.TexData].Name;
            MaterialFacts ours = facts[MaterialFactsReader.Normalize(name)];

            if (!ours.Found)
            {
                // A material the installed content does not have says nothing
                // about the classifier.
                continue;
            }

            SurfaceFlags expected = (SurfaceFlags)entry.Flags;
            SurfaceFlags actual = MaterialSurfaceClassifier.Classify(ours).Flags;

            compared++;

            if (expected == actual)
            {
                matched++;
            }
            else
            {
                disagreements[name] = new TexDataMismatch(name, $"{expected}", $"{actual}");
            }
        }

        TexInfoCompared = compared;
        TexInfoMatched = matched;
        TexInfoOverlaysSkipped = overlays;
        TexInfoMismatches = [.. disagreements.Values];
        AnyTexInfoHasTheTriggerBit = CompiledMap.TexInfo(bsp)
            .Any(static t => ((SurfaceFlags)t.Flags & SurfaceFlags.Trigger) != 0);
    }

    private static bool IsOverlay(TexInfo entry) =>
        entry.TextureVecsTexelsPerWorldUnits[3] == OverlaySentinel &&
        entry.LightmapVecsLuxelsPerWorldUnits[3] == OverlaySentinel;

    private static bool Same(Vec3 a, Vec3 b) =>
        Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

    private static bool Same(float a, float b) =>
        a.Equals(b) || Math.Abs(a - b) <= 1e-6f;

    private static string Show(Vec3 v) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[{v.X:0.######} {v.Y:0.######} {v.Z:0.######}]");
}
