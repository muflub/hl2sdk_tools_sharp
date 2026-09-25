using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The four-wide point-light gather must give the scalar lane's bits
/// (<c>GatherSampleStandardLightSSE</c>, <c>lightmap.cpp:1836</c>).
/// </summary>
public sealed class PointLightFourTests
{
    private static readonly LeafInfo[] OneLeaf = [new LeafInfo(0, 0, 0, 0, new Vec3(-1e4f, -1e4f, -1e4f), new Vec3(1e4f, 1e4f, 1e4f), 0, 0)];

    private static DirectLightGatherer Gatherer(DirectLight light, bool scalar)
    {
        LightGeometry g = Geometry.Load(LightBox.Map());
        DirectLightingSettings settings = new() { Compliance = ComplianceOptions.Correct };
        return new DirectLightGatherer([light], 0, settings, new CompiledBspTree(g), OneLeaf, SkyCameras.None)
        {
            ScalarStandardLights = scalar,
        };
    }

    private static Vec3 RandomVector(Random r, float scale) =>
        new((float)(r.NextDouble() - 0.5) * scale, (float)(r.NextDouble() - 0.5) * scale, (float)(r.NextDouble() - 0.5) * scale);

    private static (int[] Tape, Ray[] Rays) Emit(DirectLightGatherer g, SampleGroup group, bool[] needed, GatherFlags flags)
    {
        LightRayLog rays = new();
        g.Emit(g.Lights[0], group, needed, rays, new GatherOutput(), flags, 0.0f);
        int[] tape = new int[rays.Tape.Length];
        for (int i = 0; i < tape.Length; i++)
        {
            tape[i] = rays.Tape.ReadInt();
        }

        return (tape, rays.VisibilityRays().ToArray());
    }

    [Fact]
    public void FourWideMatchesTheScalarLaneBitForBit()
    {
        Random r = new(4242);
        for (int c = 0; c < 4000; c++)
        {
            DirectLight light = new()
            {
                Type = EmitType.Point,
                Origin = RandomVector(r, 512),
                ConstantAttn = r.Next(3) == 0 ? 0 : (float)r.NextDouble(),
                LinearAttn = r.Next(2) == 0 ? 0 : (float)r.NextDouble() * 0.1f,
                QuadraticAttn = r.Next(3) == 0 ? 0 : (float)r.NextDouble() * 0.01f,
                CapDist = r.Next(3) == 0 ? (float)r.NextDouble() * 300 : 1.0e22f,
            };

            if (r.Next(2) == 0)
            {
                light.StartFadeDistance = (float)r.NextDouble() * 200;
                light.EndFadeDistance = light.StartFadeDistance + ((float)r.NextDouble() * 300) - 50;
            }

            int count = r.Next(1, 5);
            SampleGroup group = new() { Count = count, NormalCount = r.Next(2) == 0 ? 1 : BumpBasis.LightmapCount };
            bool[] needed = new bool[SampleGroup.Lanes];
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                // Now and then a sample sits on the light itself: a zero distance.
                group.Points[lane] = r.Next(40) == 0 ? light.Origin : RandomVector(r, 512);
                for (int n = 0; n < BumpBasis.LightmapCount; n++)
                {
                    group.Normal(n, lane) = RandomVector(r, 2).Normalise().Normalised;
                }

                needed[lane] = r.Next(4) != 0;
            }

            GatherFlags flags = r.Next(8) == 0 ? GatherFlags.IgnoreNormals : GatherFlags.None;
            (int[] scalarTape, Ray[] scalarRays) = Emit(Gatherer(light, scalar: true), group, needed, flags);
            (int[] fourTape, Ray[] fourRays) = Emit(Gatherer(light, scalar: false), group, needed, flags);
            Assert.Equal(scalarTape, fourTape);
            Assert.Equal(scalarRays, fourRays);
        }
    }
}

/// <summary>
/// The per-cluster light lists: a group tests only the lights some lane's
/// cluster can see, which is every light it would not skip on the PVS test
/// (<c>lightmap.cpp:2499-2510</c>), in list order.
/// </summary>
public sealed class LightsReachingTests
{
    // Four clusters in four leaves; light k sees the clusters in its mask.
    private static DirectLightGatherer Gatherer(params int[] clusterMasks)
    {
        LeafInfo[] leaves = [.. Enumerable.Range(0, 4).Select(c => new LeafInfo(0, c, 0, 0, Vec3.Zero, Vec3.Zero, 0, 0))];
        DirectLight[] lights = [.. clusterMasks.Select(m => new DirectLight { Type = EmitType.Point, Pvs = [(byte)m] })];
        LightGeometry g = Geometry.Load(LightBox.Map());
        return new DirectLightGatherer(lights, 0, new DirectLightingSettings(), new CompiledBspTree(g), leaves, SkyCameras.None);
    }

    [Fact]
    public void OneClusterGetsExactlyTheLightsThatSeeIt()
    {
        DirectLightGatherer g = Gatherer(0b0001, 0b0010, 0b0011, 0b1000);
        Assert.Equal([0, 2], g.LightsReaching([0, 0, 0, 0], []).ToArray());
    }

    [Fact]
    public void MixedClustersGetTheUnionInListOrder()
    {
        DirectLightGatherer g = Gatherer(0b0001, 0b0010, 0b0011, 0b1000, 0b0100);
        Assert.Equal([0, 1, 2, 3], g.LightsReaching([3, 1, 0, 1], []).ToArray());
    }

    [Fact]
    public void AnUnknownClusterGetsEveryLight()
    {
        // PvsCheck passes a negative cluster (LightVisibility.PvsCheck).
        DirectLightGatherer g = Gatherer(0b0001, 0b0010);
        Assert.Equal([0, 1], g.LightsReaching([0, -1, 0, 0], []).ToArray());
    }

    [Fact]
    public void AClusterNoLightSeesGetsNone()
    {
        DirectLightGatherer g = Gatherer(0b0001, 0b0010);
        Assert.Empty(g.LightsReaching([2, 2, 2, 2], []).ToArray());
    }
}
