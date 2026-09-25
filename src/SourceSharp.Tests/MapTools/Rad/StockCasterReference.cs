using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>One triangle as stock's dumper printed it, to two decimal places.</summary>
/// <param name="Index">Its position in its source's run.</param>
/// <param name="V0">First vertex.</param>
/// <param name="V1">Second vertex.</param>
/// <param name="V2">Third vertex.</param>
internal readonly record struct StockCasterTriangle(int Index, Vec3 V0, Vec3 V1, Vec3 V2);

/// <summary>What one of vrad's load calls contributed, as stock recorded it.</summary>
/// <param name="Name">The source: world-brush, sky, displacement, static-prop.</param>
/// <param name="Colour">
/// The <c>TRACE_ID</c> class stock's dumper painted it: opaque, sky or
/// staticprop. Brushes and displacements share <c>opaque</c>, which is exactly
/// why the run's POSITION in the file is what separates them.
/// </param>
/// <param name="Count">How many triangles.</param>
/// <param name="Min">Lower corner of their bounding box.</param>
/// <param name="Max">Upper corner.</param>
/// <param name="SumX">Sum of every vertex's x, as printed.</param>
/// <param name="SumY">Sum of every vertex's y.</param>
/// <param name="SumZ">Sum of every vertex's z.</param>
/// <param name="Area">
/// The total surface area of the run's triangles, in square units.
/// </param>
/// <param name="Head">The first triangles of the run, in order.</param>
/// <param name="Tail">The last triangles of the run, in order.</param>
internal sealed record StockCasterSourceRun(
    string Name,
    string Colour,
    int Count,
    Vec3 Min,
    Vec3 Max,
    double SumX,
    double SumY,
    double SumZ,
    double Area,
    IReadOnlyList<StockCasterTriangle> Head,
    IReadOnlyList<StockCasterTriangle> Tail)
{
    /// <summary>
    /// How far a managed coordinate sum may legitimately sit from
    /// <see cref="SumX"/> and its siblings.
    /// </summary>
    /// <remarks>
    /// Stock's dumper prints <c>%5.2f</c>, so each of the run's
    /// <c>3 * Count</c> coordinates was rounded by at most half of 0.01 before
    /// it was summed. The bound is therefore derived rather than chosen, and it
    /// is tight: nothing about the port's arithmetic is being given slack, only
    /// the printing.
    /// </remarks>
    public double SumTolerance => 3.0 * Count * 0.005;
}

/// <summary>One stock vrad run: its arguments, and what it loaded.</summary>
/// <param name="Tag">The run's short name in the fixture.</param>
/// <param name="Arguments">The command line it was taken with.</param>
/// <param name="Sources">Its four source runs, in stock's add order.</param>
internal sealed record StockCasterRun(
    string Tag,
    string Arguments,
    IReadOnlyList<StockCasterSourceRun> Sources)
{
    /// <summary>One source by name.</summary>
    /// <param name="name">world-brush, sky, displacement or static-prop.</param>
    /// <returns>That source's run.</returns>
    /// <exception cref="InvalidOperationException">The fixture has no such source.</exception>
    public StockCasterSourceRun Source(string name)
    {
        foreach (StockCasterSourceRun source in Sources)
        {
            if (source.Name == name)
            {
                return source;
            }
        }

        throw new InvalidOperationException(
            $"the fixture's \"{Tag}\" run has no \"{name}\" source, only "
            + string.Join(", ", Sources.Select(s => s.Name)));
    }

    /// <summary>Every source's triangles added up.</summary>
    public int TotalTriangles => Sources.Sum(s => s.Count);
}

/// <summary>
/// Stock vrad's own shadow-caster set, reduced to what a gate can check.
/// </summary>
/// <remarks>
/// <para>
/// NOT A GOLDEN OUTPUT OF THIS PORT. The numbers come from stock's
/// <c>-dumptrace</c> (<c>vrad.cpp:2282</c>, <c>WriteRTEnv</c> at
/// <c>vrad.cpp:1309</c>), which writes every triangle in
/// <c>g_RtEnv.OptimizedTriangleList</c> before the KD build destroys the
/// vertices. <c>Fixtures/README-casters.md</c> carries the commands.
/// </para>
/// <para>
/// THE GATE THE PLAN ASKED FOR DOES NOT EXIST. §4b names stock's
/// <c>Total triangle count:</c> as the number to match. That line is
/// <c>bsplib.cpp:2962</c> -- <c>sum over dfaces of numedges - 2</c>, a BSP-lump
/// statistic. Measured on this map it reads 19,216 both with and without
/// <c>-StaticPropPolys</c>, while the acceleration structure it supposedly
/// describes goes from 0.41 s to 1.27 s and the caster set from 42,933
/// triangles to 118,211. Stock prints its caster count nowhere, so this
/// fixture is what replaces it.
/// </para>
/// </remarks>
internal static class StockCasterReference
{
    private const string FixtureName = "stock-lockdown-casters.txt";

    /// <summary>Loads every run in the committed fixture, keyed by tag.</summary>
    /// <returns>base, spp, ts and sppts.</returns>
    /// <exception cref="InvalidOperationException">
    /// The checkout root or the fixture could not be found, or the fixture is
    /// malformed. Thrown rather than skipped: it is a committed file, so its
    /// absence means the layout moved, and a parity fact that skips itself is
    /// how a real divergence stays hidden.
    /// </exception>
    public static IReadOnlyDictionary<string, StockCasterRun> Load() =>
        Parse(File.ReadAllLines(FixturePath()));

    /// <summary>Where the committed fixture sits in THIS worktree.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">It is not there.</exception>
    public static string FixturePath()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The stock caster "
                + "reference is located relative to the tree this binary was built from, so that "
                + "a worktree compares against ITS OWN fixture and not an enclosing checkout's.");
        }

        string path = Path.Combine(
            root, "src", "sourcesharp", "managed", "SourceSharp.Tests",
            "MapTools", "Rad", "Fixtures", FixtureName);

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"{path} is missing. It is a COMMITTED file, not build output.");
    }

    private static IReadOnlyDictionary<string, StockCasterRun> Parse(string[] lines)
    {
        Dictionary<string, StockCasterRun> runs = [];
        string? tag = null;
        string arguments = string.Empty;
        List<StockCasterSourceRun> sources = [];

        string sourceName = string.Empty;
        string colour = string.Empty;
        int count = 0;
        Vec3 min = default;
        Vec3 max = default;
        double[] sums = new double[3];
        double area = 0;
        List<StockCasterTriangle> head = [];
        List<StockCasterTriangle> tail = [];
        bool inSource = false;

        void FlushSource()
        {
            if (!inSource)
            {
                return;
            }

            sources.Add(new StockCasterSourceRun(
                sourceName, colour, count, min, max, sums[0], sums[1], sums[2], area, head,
                tail));
            head = [];
            tail = [];
            inSource = false;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (f[0])
            {
                case "run":
                    tag = f[1];
                    arguments = string.Empty;
                    sources = [];
                    break;

                case "args":
                    arguments = string.Join(' ', f.Skip(1));
                    break;

                case "source":
                    FlushSource();
                    sourceName = f[1];
                    colour = f[2];
                    count = int.Parse(f[3], CultureInfo.InvariantCulture);
                    inSource = true;
                    break;

                case "mins":
                    min = ReadVec(f, 1);
                    break;

                case "maxs":
                    max = ReadVec(f, 1);
                    break;

                case "sums":
                    for (int i = 0; i < 3; i++)
                    {
                        sums[i] = double.Parse(f[1 + i], CultureInfo.InvariantCulture);
                    }

                    break;

                case "area":
                    area = double.Parse(f[1], CultureInfo.InvariantCulture);
                    break;

                case "head":
                case "tail":
                    (f[0] == "head" ? head : tail).Add(new StockCasterTriangle(
                        int.Parse(f[1], CultureInfo.InvariantCulture),
                        ReadVec(f, 2),
                        ReadVec(f, 5),
                        ReadVec(f, 8)));
                    break;

                case "end":
                    FlushSource();
                    runs[tag ?? throw new InvalidOperationException("\"end\" before any \"run\"")] =
                        new StockCasterRun(tag, arguments, sources);
                    tag = null;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"unrecognised line in the stock caster fixture: \"{line}\"");
            }
        }

        return runs.Count > 0
            ? runs
            : throw new InvalidOperationException("the stock caster fixture holds no runs");
    }

    private static Vec3 ReadVec(string[] fields, int at) => new(
        float.Parse(fields[at], CultureInfo.InvariantCulture),
        float.Parse(fields[at + 1], CultureInfo.InvariantCulture),
        float.Parse(fields[at + 2], CultureInfo.InvariantCulture));
}
