using SourceSharp.MapGen;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>What stock vrad printed about one map's transfers and bounces.</summary>
/// <param name="Name">The map.</param>
/// <param name="Transfers"><c>total_transfer</c>.</param>
/// <param name="MaxTransfers"><c>max_transfer</c>.</param>
/// <param name="Bounces">
/// Each bounce's <c>added</c>, as the integers <c>%.0f</c> printed.
/// </param>
internal sealed record StockBounce(
    string Name,
    long Transfers,
    int MaxTransfers,
    IReadOnlyList<(long R, long G, long B)> Bounces);

/// <summary>
/// The committed record of stock's transfer and bounce lines
/// (<c>Fixtures/stock-vrad-bounce.txt</c>, made by <c>Fixtures/mkbounce.py</c>
/// from the corpus logs), and the managed run that is compared with it.
/// </summary>
/// <remarks>
/// The input maps come from the same place as p4c's gates
/// (<see cref="StockVradReference.InputFor"/>): <c>VVIS_STOCK_DIR</c> for the
/// catalogue, <c>P4C_STOCK_DIR</c> for <c>p4c_texlights</c>.
/// </remarks>
internal static class StockBounceReference
{
    private const string FixtureName = "stock-vrad-bounce.txt";

    /// <summary>The map every theory leaves out: it needs a 12G cap, not the suite's 4G.</summary>
    internal const string LargeMap = "ss_sandbox";

    internal static IReadOnlyDictionary<string, StockBounce> Load()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException($"no checkout root above {AppContext.BaseDirectory}");
        string path = Path.Combine(
            root, "src", "sourcesharp", "managed", "SourceSharp.Tests",
            "MapTools", "Rad", "Bounce", "Fixtures", FixtureName);
        return Parse(File.ReadAllLines(path));
    }

    /// <summary>Every map stock bounced whose input is present, bar <see cref="LargeMap"/>.</summary>
    internal static TheoryData<string> Maps()
    {
        TheoryData<string> data = [];
        foreach (string name in Load().Keys.Order(StringComparer.Ordinal))
        {
            if (name != LargeMap && StockVradReference.InputFor(name) is not null)
            {
                data.Add(name);
            }
        }

        if (data.Count == 0)
        {
            // xUnit refuses an empty theory; the attribute skips it anyway.
            data.Add("l1_sealed_room");
        }

        return data;
    }

    /// <summary>Lights a stock-compiled map and bounces it, under stock compliance.</summary>
    internal static async Task<RadWorld> BounceAsync(
        string name, CompileParallelism? parallelism = null, ComplianceOptions? compliance = null)
    {
        string input = StockVradReference.InputFor(name)
            ?? throw new InvalidOperationException($"no stock input for {name}");
        BspData bsp = await StockRadWorld.LoadBspAsync(input);
        IRayTracer tracer = StockRadWorld.Tracer(bsp, hdr: false);
        CompileParallelism p = parallelism ?? CompileParallelism.Default;
        DirectLightingSettings settings = StockRadWorld.Settings(hdr: false);
        if (compliance is not null)
        {
            settings = settings with { Compliance = compliance };
        }

        RadWorld world = await RadWorld.StartAsync(
            bsp, settings, await StockRadWorld.TexLightsAsync(name, hdr: false), tracer, p,
            CancellationToken.None);
        await world.LightFacesAsync(tracer, p, CancellationToken.None);
        await world.BounceAsync(tracer, p, CancellationToken.None);
        return world;
    }

    private static Dictionary<string, StockBounce> Parse(string[] lines)
    {
        Dictionary<string, StockBounce> maps = [];
        string? name = null;
        long total = 0;
        int max = 0;
        List<(long, long, long)> bounces = [];

        void Flush()
        {
            if (name is not null)
            {
                maps[name] = new StockBounce(name, total, max, bounces);
                bounces = [];
            }
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "map":
                    Flush();
                    name = parts[1];
                    break;
                case "transfers":
                    total = long.Parse(parts[1], CultureInfo.InvariantCulture);
                    max = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    break;
                case "bounce":
                    bounces.Add((
                        long.Parse(parts[2], CultureInfo.InvariantCulture),
                        long.Parse(parts[3], CultureInfo.InvariantCulture),
                        long.Parse(parts[4], CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new InvalidOperationException($"{FixtureName}: unknown record \"{parts[0]}\"");
            }
        }

        Flush();
        return maps.Count > 0 ? maps : throw new InvalidOperationException($"{FixtureName} holds no maps");
    }
}
