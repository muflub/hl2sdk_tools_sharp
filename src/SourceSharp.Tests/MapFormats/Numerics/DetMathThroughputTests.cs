//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapFormats.Numerics;

/// <summary>
/// What each <see cref="DetMath"/> and <see cref="DetMathF"/> function costs a
/// call, printed beside the platform's own for scale.
/// </summary>
/// <remarks>
/// <para>
/// A harness, not a gate: the numbers are in the output (run with
/// <c>--logger "console;verbosity=detailed"</c>), and the only assertion is a
/// floor a working build clears by orders of magnitude on any machine, as
/// <c>BspSurfaceThroughputTests</c> does for the tracer. A per-machine
/// threshold would fail on a slow CI runner for reasons that have nothing to
/// do with the code.
/// </para>
/// <para>
/// The arguments are the ranges the compile calls each function over: log on
/// the floats in (0, 1) the detail-prop Gaussian takes it of, sin and cos on
/// angles within a few turns, pow on a gamma of 2.2 over [0, 1]. Each set is
/// fixed, so two runs time the same work.
/// </para>
/// </remarks>
[Collection(ThroughputCollection.Name)]
public sealed class DetMathThroughputTests
{
    /// <summary>A call slower than this is a broken build, not a slow machine.</summary>
    private const double CeilingMicroseconds = 5000;

    private const int Count = 2048;

    private readonly ITestOutputHelper _output;

    /// <summary>Takes the runner's output sink.</summary>
    /// <param name="output">Where the measurements are written.</param>
    public DetMathThroughputTests(ITestOutputHelper output) => _output = output;

    private static double[] Uniform(int seed, double lo, double hi)
    {
        Random random = new(seed);
        double[] values = new double[Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = lo + (random.NextDouble() * (hi - lo));
        }

        return values;
    }

    /// <summary>The floats in (0, 1) a Gaussian draw takes the log of.</summary>
    private static double[] GaussianRadii()
    {
        Random random = new(5);
        double[] values = new double[Count];
        for (int i = 0; i < values.Length; i++)
        {
            float v1 = (2 * (float)random.NextDouble()) - 1;
            float v2 = (2 * (float)random.NextDouble()) - 1;
            float rsq = (v1 * v1) + (v2 * v2);
            values[i] = rsq is > 0 and <= 1 ? rsq : 0.5f;
        }

        return values;
    }

    /// <summary>
    /// The best of several timed passes over <paramref name="values"/>, in
    /// nanoseconds a call, after a warm-up pass.
    /// </summary>
    private double Time(string name, double[] values, Func<double, double> f)
    {
        double sink = 0;
        foreach (double v in values)
        {
            sink += f(v);
        }

        double best = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long start = Stopwatch.GetTimestamp();
            foreach (double v in values)
            {
                sink += f(v);
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        double ns = best / values.Length * 1e9;
        _output.WriteLine($"{name,-28} {ns,12:F1} ns/call  (best of 5 over {values.Length}; checksum {sink:G6})");
        Assert.True(ns < CeilingMicroseconds * 1000, $"{name} took {ns:F0} ns a call");
        return ns;
    }

    [Fact]
    public void DoubleFunctionsAreTimed()
    {
        double[] radii = GaussianRadii();
        double[] angles = Uniform(9, -20, 20);
        double[] bases = Uniform(11, 0.001, 1);

        Time("Math.Log (platform)", radii, Math.Log);
        Time("DetMath.Log", radii, DetMath.Log);
        Time("Math.Sin (platform)", angles, Math.Sin);
        Time("DetMath.Sin", angles, DetMath.Sin);
        Time("DetMath.Cos", angles, DetMath.Cos);
        Time("Math.Pow (platform)", bases, b => Math.Pow(b, 2.2));
        Time("DetMath.Pow", bases, b => DetMath.Pow(b, 2.2));
        Time("DetMath.PowToSingle", bases, b => DetMath.PowToSingle(b, 2.2));
        Time("DetMath.SinToSingle", angles, a => DetMath.SinToSingle(a));
    }

    [Fact]
    public void FloatFunctionsAreTimed()
    {
        double[] angles = Uniform(9, -20, 20);
        double[] bases = Uniform(11, 0.001, 1);

        Time("MathF.Sin (platform)", angles, a => MathF.Sin((float)a));
        Time("DetMathF.Sin", angles, a => DetMathF.Sin((float)a));
        Time("DetMathF.Pow", bases, b => DetMathF.Pow((float)b, 2.2f));
    }

    /// <summary>
    /// A Gaussian draw, the per-sample caller of <see cref="DetMath.Log"/>:
    /// two uniform draws, a rejection loop, and one log a pair.
    /// </summary>
    [Fact]
    public void AGaussianDrawIsTimed()
    {
        StockRandomStream uniform = new();
        uniform.SetSeed(1);
        GaussianRandomStream gaussian = new();
        float sink = 0;
        for (int i = 0; i < Count; i++)
        {
            sink += gaussian.RandomFloat(ref uniform, 0, 1);
        }

        double best = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Count; i++)
            {
                sink += gaussian.RandomFloat(ref uniform, 0, 1);
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        double ns = best / Count * 1e9;
        _output.WriteLine($"{"GaussianRandomStream draw",-28} {ns,12:F1} ns/call  (best of 5 over {Count}; checksum {sink:G6})");
        Assert.True(ns < CeilingMicroseconds * 1000, $"a draw took {ns:F0} ns");
    }
}
