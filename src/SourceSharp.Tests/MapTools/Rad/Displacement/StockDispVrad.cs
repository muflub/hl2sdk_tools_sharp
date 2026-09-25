using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// Lane 4e's stock references: the 18 p3f-t displacement maps through stock
/// x64 vvis <c>-threads 1</c> and stock vrad <c>-threads 1 -verbose</c> in four
/// modes, under <c>P4E_STOCK_DIR</c> (<c>~/.cache/maptools/ref/p4e</c>).
/// </summary>
/// <remarks>
/// Layout (made by <c>~/.cache/maptools/lanes/p4e/p4e-stockref.sh</c>):
/// <c>vis/&lt;n&gt;.bsp</c> is the input (stock vbsp from ref/p3f-t, then vvis);
/// <c>rad</c>, <c>b0</c> (<c>-bounce 0</c>), <c>fast</c> (<c>-fast</c>) and
/// <c>fastb0</c> (<c>-fast -bounce 0</c>) each hold vrad's <c>&lt;n&gt;.bsp</c>
/// and its log <c>&lt;n&gt;.log</c>.
/// </remarks>
internal static partial class StockDispVrad
{
    internal const string DirectoryVariable = "P4E_STOCK_DIR";

    internal static readonly string[] Names =
    [
        "p3f_corner_only", "p3f_grid_mixed", "p3f_grid_same", "p3f_half_edge", "p3f_half_edge_mixed",
        "p3f_lm8_clamp", "p3f_offsets", "p3f_p2_blend", "p3f_p2_flat", "p3f_p3_bump", "p3f_p4_random",
        "p3f_rotated", "p3f_slope", "p3f_smooth_mintess", "p3f_strip", "p3f_swap", "p3f_tags_flags", "p3f_wall",
    ];

    internal static TheoryData<string> Maps()
    {
        TheoryData<string> data = [];
        foreach (string n in Names)
        {
            data.Add(n);
        }

        return data;
    }

    internal static string Dir =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } d
            ? d
            : throw new InvalidOperationException($"no {DirectoryVariable}");

    internal static string? SkipReason() =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 }
            ? null
            : $"no {DirectoryVariable}: lane 4e's stock displacement corpus is absent";

    internal static string Input(string name) => Path.Combine(Dir, "vis", name + ".bsp");

    internal static string Output(string mode, string name) => Path.Combine(Dir, mode, name + ".bsp");

    internal static string Log(string mode, string name) => Path.Combine(Dir, mode, name + ".log");

    internal static VradOptions Options(VradOptions? options = null) =>
        (options ?? VradOptions.Default) with { Compliance = ComplianceOptions.Stock };

    internal static async Task<RadWorld> LightAsync(
        string name, VradOptions? options = null, CompileParallelism? parallelism = null, ComplianceOptions? compliance = null)
    {
        BspData bsp = await StockRadWorld.LoadBspAsync(Input(name));
        IRayTracer tracer = StockRadWorld.Tracer(bsp, hdr: false);
        CompileParallelism p = parallelism ?? CompileParallelism.Default;
        VradOptions o = Options(options);
        if (compliance is not null)
        {
            o = o with { Compliance = compliance };
        }

        RadWorld world = await RadWorld.StartAsync(
            bsp,
            DirectLightingSettings.FromVrad(o, hdr: false),
            new TextureLightTable(new RadLightFile(), name),
            tracer,
            p,
            CancellationToken.None);
        await world.LightFacesAsync(tracer, p, CancellationToken.None);
        await world.BuildDisplacementHashAsync(p, CancellationToken.None);
        return world;
    }

    internal static DFace[] Faces(BspData bsp) =>
        MemoryMarshal.Cast<byte, DFace>(bsp[BspLump.Faces].Data.Span).ToArray();

    /// <summary>A decoded ColorRGBExp32 luxel: r, g, b times 2^exponent.</summary>
    internal static Vec3 Decode(ReadOnlySpan<byte> lighting, int at)
    {
        double scale = Math.Pow(2.0, (sbyte)lighting[at + 3]);
        return new Vec3((float)(lighting[at] * scale), (float)(lighting[at + 1] * scale), (float)(lighting[at + 2] * scale));
    }

    /// <summary>
    /// <c>VectorToColorRGBExp32</c> (<c>color_conversion.cpp:566</c>), the
    /// test's copy (4f owns the encoder).
    /// </summary>
    internal static uint Encode(Vec3 v)
    {
        float max = v.X > v.Y ? (v.X > v.Z ? v.X : v.Z) : (v.Y > v.Z ? v.Y : v.Z);
        int exponent = max == 0.0f ? 0 : (int)((BitConverter.SingleToUInt32Bits(max) & 0x7F800000) >> 23) - (7 + 127);
        float scalar = BitConverter.UInt32BitsToSingle((uint)(127 - exponent) << 23);
        byte r = (byte)(int)(v.X * scalar);
        byte g = (byte)(int)(v.Y * scalar);
        byte b = (byte)(int)(v.Z * scalar);
        return r | ((uint)g << 8) | ((uint)b << 16) | ((uint)(byte)(sbyte)exponent << 24);
    }

    internal static StockDispLog ReadLog(string mode, string name)
    {
        string text = File.ReadAllText(Log(mode, name));
        int Int(Regex r) => int.Parse(r.Match(text).Groups[1].Value, CultureInfo.InvariantCulture);

        Match disp = DisplacementLine().Match(text);
        return new StockDispLog(
            Int(FacesLine()),
            Int(DisplacementsLine()),
            int.Parse(disp.Groups[1].Value, CultureInfo.InvariantCulture),
            disp.Groups[2].Value,
            BeforeLine().Match(text) is { Success: true } b ? int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
            AfterLine().Match(text) is { Success: true } a ? int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture) : -1);
    }

    [GeneratedRegex(@"(\d+) faces\r?\n")]
    private static partial Regex FacesLine();

    [GeneratedRegex(@"(\d+) Displacements")]
    private static partial Regex DisplacementsLine();

    [GeneratedRegex(@"(\d+) Square Feet \[([\d.]+) Square Inches\]")]
    private static partial Regex DisplacementLine();

    [GeneratedRegex(@"(\d+) patches before subdivision")]
    private static partial Regex BeforeLine();

    [GeneratedRegex(@"(\d+) patches after subdivision")]
    private static partial Regex AfterLine();
}

internal sealed record StockDispLog(
    int Faces, int Displacements, int SquareFeet, string SquareInches, int PatchesBefore, int PatchesAfter);

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockDispTheoryAttribute : TheoryAttribute
{
    public StockDispTheoryAttribute() => Skip = StockDispVrad.SkipReason();
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockDispFactAttribute : FactAttribute
{
    public StockDispFactAttribute() => Skip = StockDispVrad.SkipReason();
}
