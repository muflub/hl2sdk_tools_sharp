using System.Collections.Concurrent;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Final;

/// <summary>
/// The p4f stock corpus: the unified catalogue (<c>VVIS_STOCK_DIR</c>) as
/// input, and stock vrad outputs under <c>P4F_STOCK_DIR</c>.
/// </summary>
/// <remarks>
/// <para>
/// Recipe: run stock x64 <c>vrad.exe -threads 1 -verbose
/// -bounce 0</c>, in a scratch directory, on a plain-named copy of each map's
/// <c>.stockvis.bsp</c> (or
/// <c>.bsp</c> when vvis could not process it) into <c>b0/</c>, with
/// <c>-game</c> the maptools worktree's <c>tools/mapgame</c>, whose search
/// path has no <c>lights.rad</c>. <c>p4c_texlights</c> comes from
/// <c>P4C_STOCK_DIR/in</c> with its <c>.rad</c> copied beside it.
/// </para>
/// </remarks>
internal static class VradStockReference
{
    internal const string DirectoryVariable = "P4F_STOCK_DIR";

    /// <summary>Every map the gate runs: catmaps-all (30) plus p4c's texlight fixture.</summary>
    internal static readonly string[] Names =
    [
        "l0_micro_brush", "l0_nonaxial_wedge", "l0_overlapping_cubes", "l0_unit_cube",
        "l1_areaportal", "l1_corridor_farz", "l1_corridor_radius_override", "l1_func_detail",
        "l1_hint_skip", "l1_leak", "l1_long_corridor", "l1_open_arena", "l1_sealed_room",
        "l1_three_doors_almost_in_a_line", "l1_three_doors_in_a_line", "l1_tool_textures",
        "l1_two_rooms_and_a_door", "l1_vvis_fast", "l1_water_and_fog_leaves", "l1_water_volume",
        "l2_areaportal_between_pools", "l2_detail_and_hint_in_a_corridor", "l3_arena_144_pillars",
        "l3_corridor_64_rooms", "ss_sandbox", "x0_areaportal_in_water", "x0_corner_cut",
        "x0_octahedron", "x0_octahedron_seam", "x0_pyramid", "p4c_texlights",
        "l1_default_cubemap", "l1_detail_props", "l1_detail_type_material", "l1_env_cubemap",
        "l1_missing_prop_model", "l1_overlay", "l1_static_prop", "l1_static_prop_solid_types",
        "l1_water_overlay", "l2_cubemap_on_water_and_patch", "l2_props_overlays_detail_cubemap",
    ];

    /// <summary>
    /// The content each reference was compiled against, from its
    /// <c>&lt;n&gt;.gameinfo</c> sidecar (p3j's provenance rule), mounted once
    /// per distinct gameinfo for the whole run. A missing sidecar fails the
    /// fact: there is no default content.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<ContentFileSystem>>> Games = new();

    /// <summary>
    /// p4e's displacement maps: input <c>P4E_STOCK_DIR/vis</c>, stock outputs
    /// in <c>P4E_STOCK_DIR/{b0,rad}</c>.
    /// </summary>
    internal static readonly string[] DisplacementNames =
    [
        "p3f_corner_only", "p3f_grid_mixed", "p3f_grid_same", "p3f_half_edge", "p3f_half_edge_mixed",
        "p3f_lm8_clamp", "p3f_offsets", "p3f_p2_blend", "p3f_p2_flat", "p3f_p3_bump", "p3f_p4_random",
        "p3f_rotated", "p3f_slope", "p3f_smooth_mintess", "p3f_strip", "p3f_swap", "p3f_tags_flags", "p3f_wall",
    ];

    internal const string DisplacementVariable = "P4E_STOCK_DIR";

    private static readonly ConcurrentDictionary<string, Lazy<Task<(BspData Map, RadResult Result)>>> Compiled = new();

    internal static TheoryData<string> Maps()
    {
        TheoryData<string> data = [];
        foreach (string name in Names.Concat(DisplacementNames))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The catalogue maps only: the corpora that exist for them alone (b0nx, b0both).</summary>
    internal static TheoryData<string> CatalogueMaps()
    {
        TheoryData<string> data = [];
        foreach (string name in Names)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Why a map cannot run here, or null.</summary>
    internal static string? MissingCorpus(string name) =>
        IsDisplacement(name) && Environment.GetEnvironmentVariable(DisplacementVariable) is not { Length: > 0 }
            ? $"no {DisplacementVariable}: p4e's displacement references are absent"
            : null;

    internal static bool IsDisplacement(string name) => name.StartsWith("p3f_", StringComparison.Ordinal);

    internal static string? SkipReason() =>
        Environment.GetEnvironmentVariable("VVIS_STOCK_DIR") is not { Length: > 0 }
            ? "no VVIS_STOCK_DIR: the unified stock catalogue is absent"
            : Environment.GetEnvironmentVariable(DirectoryVariable) is not { Length: > 0 }
                ? $"no {DirectoryVariable}: p4f's stock vrad references are absent"
                : null;

    internal static string InputFor(string name)
    {
        if (IsDisplacement(name))
        {
            return Path.Combine(Environment.GetEnvironmentVariable(DisplacementVariable)!, "vis", name + ".bsp");
        }

        string cat = Environment.GetEnvironmentVariable("VVIS_STOCK_DIR")!;
        foreach (string candidate in new[]
        {
            Path.Combine(cat, name + ".stockvis.bsp"),
            Path.Combine(cat, name + ".bsp"),
            Path.Combine(Environment.GetEnvironmentVariable("P4C_STOCK_DIR") ?? cat, "in", name + ".bsp"),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"no input for {name}");
    }

    /// <summary>
    /// <c>ComplianceOptions.Stock</c>, or with the quirks named in
    /// <c>P4F_FLIP_QUIRK</c> (comma-separated) flipped -- how each quirk's
    /// effect on the gate is measured.
    /// </summary>
    internal static ComplianceOptions Compliance()
    {
        ComplianceOptions c = ComplianceOptions.Stock;
        if (Environment.GetEnvironmentVariable("P4F_FLIP_QUIRK") is { Length: > 0 } flip)
        {
            foreach (string q in flip.Split(','))
            {
                c = c.Flipping(Enum.Parse<StockQuirk>(q));
            }
        }

        return c;
    }

    internal static string ReferenceFor(string mode, string name) =>
        IsDisplacement(name)
            ? Path.Combine(
                Environment.GetEnvironmentVariable(DisplacementVariable)!,
                mode switch { "full" => "rad", _ => mode },
                name + ".bsp")
            : Path.Combine(Environment.GetEnvironmentVariable(DirectoryVariable)!, mode, name + ".bsp");

    internal static async Task<BspData> LoadAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await BspFile.LoadAsync(stream);
    }

    /// <summary>The <c>.rad</c> files beside the reference, as the only content.</summary>
    internal static async Task<IContentFileSystem> ContentAsync(string mode, string name)
    {
        InMemoryFileSystem files = new();
        string rad = Path.Combine(Environment.GetEnvironmentVariable(DirectoryVariable)!, mode, name + ".rad");
        if (File.Exists(rad))
        {
            files.AddFile(name + ".rad", await File.ReadAllBytesAsync(rad));
        }

        DirectoryContentMount rads = await DirectoryContentMount.MountAsync(files, VPath.Empty);

        // A displacement reference's provenance is its input's: p4e copied
        // ref/p3f-t, whose sidecars name the content.
        string provenance = IsDisplacement(name)
            ? Environment.GetEnvironmentVariable("DISP_STOCK_DIR") is { Length: > 0 } disp
                ? disp
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/maptools/ref/p3f-t")
            : Path.GetDirectoryName(ReferenceFor(mode, name))!;
        string gameInfo = (await File.ReadAllTextAsync(Path.Combine(provenance, name + ".gameinfo"))).Trim();
        ContentFileSystem game = await Games.GetOrAdd(
            gameInfo,
            _ => new Lazy<Task<ContentFileSystem>>(async () =>
                (await SourceSharp.Tests.MapTools.Io.StockProvenance.MountHostAsync(provenance, name)).Content)).Value;
        return new ContentFileSystem([rads, .. game.Mounts], ownsMounts: false);
    }

    /// <summary>
    /// The managed compile of one map under <c>ComplianceOptions.Stock</c>,
    /// with the stock run's switches, cached per (mode, name, threads).
    /// </summary>
    internal static Task<(BspData Map, RadResult Result)> CompileAsync(
        string mode, string name, VradOptions options, int threads = 0) =>
        Compiled.GetOrAdd(
            $"{mode}/{name}/{threads}",
            _ => new Lazy<Task<(BspData, RadResult)>>(async () =>
            {
                BspData map = await LoadAsync(InputFor(name));
                RadResult result = await Vrad.LightAsync(map, new VradContext
                {
                    Options = options with { Compliance = Compliance() },
                    MapName = name,
                    Content = await ContentAsync(mode, name),
                    Parallelism = threads > 0 ? new CompileParallelism { MaxDegree = threads } : CompileParallelism.Default,
                });

                // A diagnostic aid: P4F_DUMP_DIR keeps every managed output for
                // lump-by-lump inspection outside the suite.
                if (Environment.GetEnvironmentVariable("P4F_DUMP_DIR") is { Length: > 0 } dump)
                {
                    Directory.CreateDirectory(Path.Combine(dump, mode));
                    await using FileStream file = File.Create(Path.Combine(dump, mode, $"{name}.t{threads}.bsp"));
                    await BspFile.SaveAsync(map, file, BspWriteMode.Canonical);
                }

                return (map, result);
            })).Value;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VradStockTheoryAttribute : TheoryAttribute
{
    public VradStockTheoryAttribute() => Skip = VradStockReference.SkipReason();
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class VradStockFactAttribute : FactAttribute
{
    public VradStockFactAttribute() => Skip = VradStockReference.SkipReason();
}
