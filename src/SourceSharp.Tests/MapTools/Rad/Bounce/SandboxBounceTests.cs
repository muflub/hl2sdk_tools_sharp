//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary>
/// ss_sandbox, the corpus's largest bounce (74,842 patches, 31.8 M stock
/// transfers): the gate and the time and memory measurement, run alone under
/// a 12G cap. Set <c>P4D_SANDBOX=1</c> (and <c>P4D_THREADS</c> for the worker
/// count, default all) to run it.
/// </summary>
public sealed class SandboxBounceTests(ITestOutputHelper output)
{
    [SandboxBounceFact]
    public async Task SandboxBouncesLikeStock()
    {
        const string Name = StockBounceReference.LargeMap;
        StockBounce stock = StockBounceReference.Load()[Name];
        int threads = int.TryParse(Environment.GetEnvironmentVariable("P4D_THREADS"), out int t) ? t : Environment.ProcessorCount;
        CompileParallelism p = new() { MaxDegree = threads };

        Stopwatch clock = Stopwatch.StartNew();
        BspData bsp = await StockRadWorld.LoadBspAsync(StockVradReference.InputFor(Name)!);
        IRayTracer tracer = StockRadWorld.Tracer(bsp, hdr: false);
        RadWorld world = await RadWorld.StartAsync(
            bsp, StockRadWorld.Settings(hdr: false), await StockRadWorld.TexLightsAsync(Name, hdr: false),
            tracer, p, CancellationToken.None);
        await world.LightFacesAsync(tracer, p, CancellationToken.None);
        long lit = clock.ElapsedMilliseconds;
        long peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;

        using WorkQueue queue = new(p);
        clock.Restart();
        VisMatrix matrix = new(world.BounceContext());
        TransferSet transfers = await matrix.BuildAsync(tracer, queue, CancellationToken.None);
        long built = clock.ElapsedMilliseconds;
        long peakBuild = Process.GetCurrentProcess().PeakWorkingSet64;

        clock.Restart();
        IReadOnlyList<Vec3> energies = await new Radiosity(world.BounceContext(), transfers)
            .BounceAsync(queue, CancellationToken.None);
        long bounced = clock.ElapsedMilliseconds;
        long peakBounce = Process.GetCurrentProcess().PeakWorkingSet64;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"p4d-sandbox threads {threads}: light {lit} ms, transfers {built} ms, bounce {bounced} ms; "
            + $"peak RSS after light {peakBefore / 1024} kB, after transfers {peakBuild / 1024} kB, after bounce {peakBounce / 1024} kB; "
            + $"transfers {transfers.Total}/{stock.Transfers} max {transfers.Max}/{stock.MaxTransfers} "
            + $"rays {matrix.Statistics.Rays} blocked {matrix.Statistics.Blocked} chunks {matrix.Statistics.Chunks} "
            + $"bounces {energies.Count}/{stock.Bounces.Count} b1 {energies[0].X:F0},{energies[0].Y:F0},{energies[0].Z:F0}/"
            + $"{stock.Bounces[0].R},{stock.Bounces[0].G},{stock.Bounces[0].B}"));

        for (int i = 0; i < Math.Min(energies.Count, stock.Bounces.Count); i++)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"p4d-sandbox #{i + 1} {energies[i].X:F0} {energies[i].Y:F0} {energies[i].Z:F0} stock {stock.Bounces[i].R} {stock.Bounces[i].G} {stock.Bounces[i].B}"));
        }

        Assert.Equal(stock.Bounces.Count, energies.Count);
        Assert.Equal(stock.MaxTransfers, transfers.Max);
    }
}

/// <summary>Runs only when <c>P4D_SANDBOX=1</c> and the corpus is present.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SandboxBounceFactAttribute : FactAttribute
{
    /// <summary>Decides whether the measurement can run.</summary>
    public SandboxBounceFactAttribute() =>
        Skip = StockVradReference.SkipReason()
            ?? (Environment.GetEnvironmentVariable("P4D_SANDBOX") == "1"
                ? null
                : "set P4D_SANDBOX=1 to run the ss_sandbox bounce (needs a 12G cap)");
}
