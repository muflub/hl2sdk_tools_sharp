using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The same parity gate as <see cref="BspSurfaceStockParityTests"/>, over a ray
/// set too large to commit.
/// </summary>
/// <remarks>
/// <para>
/// Set <c>SS_TRACE_RAY_SET</c> and <c>SS_TRACE_ANSWER_SET</c> to a pair the
/// oracle produced in one run -- <c>Fixtures/README-bsp-rays.md</c> has the
/// command -- and this compares every one of them. Without both, it skips,
/// because there is nothing to compare rather than nothing to find.
/// </para>
/// <para>
/// WHY A SKIPPING FACT IS ACCEPTABLE HERE AND NOT FOR THE COMMITTED SET. The
/// 8,100-ray fixture is tracked, so its absence means the tree is broken and
/// the fact THROWS. This one gates on an environment variable a person sets
/// deliberately; a skip is the honest answer to "you did not ask for this".
/// The committed set is the one that has to stay green on its own.
/// </para>
/// </remarks>
public sealed class BspSurfaceLargeSetParityTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Takes the runner's output sink.</summary>
    /// <param name="output">Where the ray count is written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public BspSurfaceLargeSetParityTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// Every ray of the large set hits the same face at the same fraction as
    /// stock.
    /// </summary>
    /// <returns>A task that completes when the comparison is done.</returns>
    [Fact]
    public async Task LargeSetAgreesWithStockWhenOneIsProvided()
    {
        string? rayPath = Environment.GetEnvironmentVariable("SS_TRACE_RAY_SET");
        string? answerPath = Environment.GetEnvironmentVariable("SS_TRACE_ANSWER_SET");
        if (string.IsNullOrEmpty(rayPath) || string.IsNullOrEmpty(answerPath))
        {
            _output.WriteLine(
                "skipped: set SS_TRACE_RAY_SET and SS_TRACE_ANSWER_SET to an oracle pair");
            return;
        }

        StockRaySet set = StockRaySet.Load(rayPath, answerPath);

        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        BspSurfaceTracer tracer = new(BspTraceGeometry.Build(bsp));

        HitId[] ours = new HitId[set.Count];
        tracer.TraceClosest(set.Rays, ours, RayTraceOptions.StockExact);

        int faceMismatch = 0;
        int fractionMismatch = 0;
        for (int i = 0; i < set.Count; i++)
        {
            if (ours[i].Surface != set.StockSurface[i])
            {
                faceMismatch++;
            }

            if (BitConverter.SingleToInt32Bits(ours[i].Fraction)
                != BitConverter.SingleToInt32Bits(set.StockFraction[i]))
            {
                fractionMismatch++;
            }
        }

        _output.WriteLine(
            $"{set.Count} rays: {faceMismatch} face mismatches, {fractionMismatch} fraction "
            + "mismatches against stock");

        Assert.Equal(0, faceMismatch);
        Assert.Equal(0, fractionMismatch);
    }
}
