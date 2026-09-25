using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>AddSampleToList</c>, <c>CompressAmbientSampleList</c>,
/// <c>Mod_LeafAmbientColorAtPos</c> and <c>CubeDeltaGammaSpace</c>.
/// </summary>
public sealed class AmbientSampleListTests
{
    private static Vec3[] Uniform(float v) => [.. Enumerable.Repeat(new Vec3(v, v, v), 6)];

    private static List<AmbientSample> FilledTo(int count, Func<int, Vec3> position, Func<int, float> colour)
    {
        List<AmbientSample> list = [];
        for (int i = 0; i < count; i++)
        {
            AmbientSampleList.Add(list, position(i), Uniform(colour(i)), ComplianceOptions.Stock);
        }

        return list;
    }

    [Fact]
    public void UpToSixteenSamplesAreAllKept()
    {
        List<AmbientSample> list = FilledTo(16, i => new Vec3(i * 10, 0, 0), _ => 0.5f);

        Assert.Equal(16, list.Count);
    }

    [Fact]
    public void TheSeventeenthEvictsOne()
    {
        List<AmbientSample> list = FilledTo(17, i => new Vec3(i * 10, 0, 0), _ => 0.5f);

        Assert.Equal(16, list.Count);
    }

    [Fact]
    public void TheEarlierOfTheNearestPairIsEvictedAndTheLastFillsItsHole()
    {
        // Sixteen samples 100 apart, then one 1 unit from sample 5. Samples 5
        // and 16 are each other's nearest; the tie goes to the EARLIER index,
        // so sample 5 goes and the last element -- the newcomer -- moves into
        // its slot (FastRemove).
        List<AmbientSample> list = FilledTo(16, i => new Vec3(i * 100, 0, 0), _ => 0.5f);
        AmbientSampleList.Add(list, new Vec3(501, 0, 0), Uniform(0.5f), ComplianceOptions.Stock);

        Assert.Equal(new Vec3(501, 0, 0), list[5].Position);
    }

    [Fact]
    public void ColourDifferenceMakesASampleWorthKeeping()
    {
        // 50 units from sample 5: same-coloured, that pair scales to 5 and
        // beats the 100-apart pairs (scaled 10), so sample 5 would go. A
        // different colour scales it by 0.1 + 0.9 * maxDC = 1.0 (clamped) to
        // 50, so a same-colour pair is closest instead and sample 5 stays.
        List<AmbientSample> list = FilledTo(16, i => new Vec3(i * 100, 0, 0), _ => 0.5f);
        AmbientSampleList.Add(list, new Vec3(550, 0, 0), Uniform(5.0f), ComplianceOptions.Stock);

        Assert.Contains(list, s => s.Position == new Vec3(500, 0, 0));
    }

    [Fact]
    public void StockNeverUsesTheTieBreak()
    {
        //:375 compares totalDC against nearestNeighborTotal, which is never
        // assigned:330): of two samples tied on distance, stock evicts the
        // EARLIER one whatever their colour variation.
        List<AmbientSample> list = TiedPair();
        AmbientSampleList.Add(list, new Vec3(10000, 0, 0), Uniform(0.0f), ComplianceOptions.Stock);

        Assert.DoesNotContain(list, s => s.Position == new Vec3(0, 0, 0));
    }

    [Fact]
    public void CorrectBreaksADistanceTieOnColourVariation()
    {
        // The evident intent: on an exact distance tie, evict the sample with
        // the smaller totalDC -- the one that adds least colour information.
        // Sample 1 matches its neighbours exactly, so it is the one to go.
        List<AmbientSample> list = TiedPair();
        AmbientSampleList.Add(list, new Vec3(10000, 0, 0), Uniform(0.0f), ComplianceOptions.Correct);

        Assert.DoesNotContain(list, s => s.Position == new Vec3(1, 0, 0));
    }

    [Fact]
    public void AUniformlyLitLeafCompressesToOneSample()
    {
        // Every sample is reconstructible from the others to 0 gamma units.
        List<AmbientSample> list = FilledTo(8, i => new Vec3(i * 50, 0, 0), _ => 0.25f);
        AmbientSampleList.Compress(list);

        Assert.Single(list);
    }

    [Fact]
    public void ADistinctSampleSurvivesCompression()
    {
        // One bright sample among dark ones cannot be reconstructed.
        List<AmbientSample> list = FilledTo(4, i => new Vec3(i * 500, 0, 0), i => i == 2 ? 0.9f : 0.01f);
        AmbientSampleList.Compress(list);

        Assert.Contains(list, s => s.Cube[0].X == 0.9f);
    }

    [Fact]
    public void ReconstructionIsAnInverseSquareWeightedMean()
    {
        // Weights 1/(d^2+1): distance 0 weighs 1, distance 1 weighs 1/2, so
        // (1*1 + 4*0.5) / 1.5 = 2.
        List<AmbientSample> list = [];
        AmbientSampleList.Add(list, new Vec3(0, 0, 0), Uniform(1.0f), ComplianceOptions.Stock);
        AmbientSampleList.Add(list, new Vec3(1, 0, 0), Uniform(4.0f), ComplianceOptions.Stock);
        Span<Vec3> cube = stackalloc Vec3[6];

        AmbientSampleList.ColorAtPosition(cube, new Vec3(0, 0, 0), CollectionsMarshal.AsSpan(list), -1);

        Assert.Equal(2.0f, cube[0].X, 5);
    }

    [Fact]
    public void ReconstructionSkipsTheNamedSample()
    {
        List<AmbientSample> list = [];
        AmbientSampleList.Add(list, new Vec3(0, 0, 0), Uniform(1.0f), ComplianceOptions.Stock);
        AmbientSampleList.Add(list, new Vec3(1, 0, 0), Uniform(4.0f), ComplianceOptions.Stock);
        Span<Vec3> cube = stackalloc Vec3[6];

        AmbientSampleList.ColorAtPosition(cube, new Vec3(0, 0, 0), CollectionsMarshal.AsSpan(list), 0);

        Assert.Equal(4.0f, cube[0].X);
    }

    [Fact]
    public void GammaDeltaOfIdenticalCubesIsZero()
    {
        Assert.Equal(0, AmbientSampleList.CubeDeltaGammaSpace(Uniform(0.3f), Uniform(0.3f)));
    }

    [Fact]
    public void GammaDeltaIsTheLargestChannelDifference()
    {
        Vec3[] a = Uniform(0.0f);
        Vec3[] b = Uniform(0.0f);
        b[3] = new Vec3(0, 1.0f, 0);

        Assert.Equal(255, AmbientSampleList.CubeDeltaGammaSpace(a, b));
    }

    /// <summary>
    /// Sixteen samples: a pair one unit apart whose colours differ by less than
    /// the 1e-4 threshold -- so their scaled distances tie exactly -- but whose
    /// totalDC differ (sample 0 is 5e-5 off everything, sample 1 matches the
    /// rest), plus fourteen spread samples.
    /// </summary>
    private static List<AmbientSample> TiedPair()
    {
        List<AmbientSample> list = [];
        AmbientSampleList.Add(list, new Vec3(0, 0, 0), Uniform(0.00005f), ComplianceOptions.Stock);
        AmbientSampleList.Add(list, new Vec3(1, 0, 0), Uniform(0.0f), ComplianceOptions.Stock);
        for (int i = 0; i < 14; i++)
        {
            AmbientSampleList.Add(list, new Vec3(0, (i + 1) * 1000, 0), Uniform(0.0f), ComplianceOptions.Stock);
        }

        return list;
    }
}
