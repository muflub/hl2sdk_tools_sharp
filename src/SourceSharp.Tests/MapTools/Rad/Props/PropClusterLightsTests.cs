//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

/// <summary>
/// The per-cluster light lists prop lighting walks instead of the whole light
/// list: each must hold exactly the lights the full walk keeps, in light
/// order, so the samples a point plans do not change.
/// </summary>
public sealed class PropClusterLightsTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _ambient;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="ambient">The leaf-ambient map the points stand in.</param>
    public PropClusterLightsTests(AmbientFixture ambient) => _ambient = ambient;

    /// <summary>The static-prop lists keep style 0 only, whatever the light's type.</summary>
    [Fact]
    public void StaticListsKeepOnlyStyleZero()
    {
        PropLight[] lights =
        [
            Light(EmitType.Point, 0, 0xFF),
            Light(EmitType.Point, 1, 0xFF),
            Light(EmitType.SkyAmbient, 0, 0xFF),
            Light(EmitType.Spotlight, 32, 0xFF),
            Light(EmitType.SkyLight, 0, 0xFF),
        ];

        PropClusterLights lists = new(lights, 8, static l => l.Style == 0);

        Assert.Equal([0, 2, 4], lists.For(3));
        Assert.Equal([0, 2, 4], lists.For(-1));
    }

    /// <summary>
    /// The detail-prop lists keep every style but drop the ambient sky, as
    /// the detail pass's walk does.
    /// </summary>
    [Fact]
    public void DetailListsKeepEveryStyleButNotTheAmbientSky()
    {
        PropLight[] lights =
        [
            Light(EmitType.Point, 0, 0xFF),
            Light(EmitType.SkyAmbient, 0, 0xFF),
            Light(EmitType.Point, 5, 0xFF),
            Light(EmitType.SkyLight, 0, 0xFF),
        ];

        PropClusterLights lists = PropClusterLights.ForDetailProps(_ambient.Ldr, lights);

        Assert.Equal([0, 2, 3], lists.For(0));
        Assert.Equal([0, 2, 3], lists.For(-1));
    }

    /// <summary>
    /// The static-prop factory filters by style and sizes the lists by the
    /// map's clusters.
    /// </summary>
    [Fact]
    public void StaticFactoryFiltersByStyleAndSizesByTheMap()
    {
        PropLight[] lights = [Light(EmitType.Point, 0, 0xFF), Light(EmitType.Point, 2, 0xFF), Light(EmitType.SkyAmbient, 0, 0xFF)];

        PropClusterLights lists = PropClusterLights.ForStaticProps(_ambient.Ldr, lights);

        Assert.Equal(PropClusterLights.CountClusters(_ambient.Ldr), lists.ClusterCount);
        Assert.Equal([0, 2], lists.For(0));
    }

    /// <summary>The cluster count is one past the highest cluster any leaf is in.</summary>
    [Fact]
    public void ClusterCountIsOnePastTheHighestLeafCluster()
    {
        int max = -1;
        foreach (DLeaf leaf in _ambient.Ldr.Leaves)
        {
            max = Math.Max(max, (int)leaf.Cluster);
        }

        Assert.True(max >= 1, "the fixture map should have more than one cluster");
        Assert.Equal(max + 1, PropClusterLights.CountClusters(_ambient.Ldr));
    }

    /// <summary>
    /// A cluster's list holds the lights whose PVS bit for it is set, in
    /// light order, and a cluster no light sees gets an empty list.
    /// </summary>
    [Fact]
    public void ClusterListsFollowThePvsBitsInLightOrder()
    {
        // Cluster c is bit (c & 7) of byte (c >> 3); 16 clusters, two bytes.
        PropLight[] lights =
        [
            LightRow(EmitType.Point, 0, 0b0000_0101, 0x00), // clusters 0, 2
            LightRow(EmitType.Point, 0, 0b0000_0100, 0x80), // clusters 2, 15
            LightRow(EmitType.Point, 1, 0xFF, 0xFF),        // everywhere, but style 1
            LightRow(EmitType.Point, 0, 0b0000_0001, 0x80), // clusters 0, 15
            LightRow(EmitType.SkyLight, 0, 0x00, 0x01),     // cluster 8
        ];

        PropClusterLights lists = new(lights, 16, static l => l.Style == 0);

        Assert.Equal([0, 3], lists.For(0));
        Assert.Empty(lists.For(1));
        Assert.Equal([0, 1], lists.For(2));
        Assert.Equal([4], lists.For(8));
        Assert.Equal([1, 3], lists.For(15));
        Assert.Empty(lists.For(7));
        Assert.Equal([0, 1, 3, 4], lists.For(-1));
        Assert.Equal([0, 1, 3, 4], lists.For(-2));
    }

    /// <summary>
    /// A negative cluster never reads a PVS row, so a kept light with no row
    /// at all is still listed; a non-negative one reads it and throws as the
    /// full walk threw.
    /// </summary>
    [Fact]
    public void NegativeClusterSkipsThePvsAndShortRowsThrowAsBefore()
    {
        PropLight[] lights = [new PropLight { Type = EmitType.Point, Pvs = [] }];
        PropClusterLights lists = new(lights, 4, static _ => true);

        Assert.Equal([0], lists.For(-1));
        Assert.Throws<IndexOutOfRangeException>(() => lists.For(0));
    }

    /// <summary>
    /// Lists are built lazily, one cluster on first use, and a second ask
    /// returns the same list; a cluster past the count is refused.
    /// </summary>
    [Fact]
    public void ListsAreBuiltLazilyAndKept()
    {
        PropLight[] lights = [Light(EmitType.Point, 0, 0xFF), Light(EmitType.Point, 0, 0x02)];
        PropClusterLights lists = new(lights, 8, static _ => true);

        Assert.Equal((0, 0L), lists.Built());
        Assert.Same(lists.For(-1), lists.For(-1));
        Assert.Equal((0, 0L), lists.Built());

        int[] first = lists.For(1);
        Assert.Equal((1, 2L), lists.Built());
        Assert.Same(first, lists.For(1));

        Assert.Single(lists.For(4));
        Assert.Equal((2, 3L), lists.Built());

        Assert.Throws<ArgumentOutOfRangeException>(() => lists.For(8));
    }

    /// <summary>Workers asking for the same clusters at once all get the same contents.</summary>
    [Fact]
    public void ConcurrentAsksAgree()
    {
        List<PropLight> lights = [];
        for (int i = 0; i < 64; i++)
        {
            byte[] row = new byte[32];
            for (int b = 0; b < row.Length; b++)
            {
                row[b] = (byte)((i * 37) + (b * 11));
            }

            lights.Add(new PropLight { Type = EmitType.Point, Style = i % 3 == 0 ? 1 : 0, Pvs = row });
        }

        PropClusterLights reference = new(lights, 256, static l => l.Style == 0);
        int[][] expected = [.. Enumerable.Range(0, 256).Select(reference.For)];

        for (int round = 0; round < 4; round++)
        {
            PropClusterLights shared = new(lights, 256, static l => l.Style == 0);
            System.Threading.Tasks.Parallel.For(0, 4096, i =>
            {
                int c = (i * 7) % 256;
                Assert.Equal(expected[c], shared.For(c));
            });

            Assert.Equal(256, shared.Built().Clusters);
        }
    }

    /// <summary>The constructor refuses null arguments and a negative cluster count.</summary>
    [Fact]
    public void ConstructorValidates()
    {
        PropLight[] lights = [Light(EmitType.Point, 0, 0xFF)];
        Assert.Throws<ArgumentNullException>(() => new PropClusterLights(null!, 1, static _ => true));
        Assert.Throws<ArgumentNullException>(() => new PropClusterLights(lights, 1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropClusterLights(lights, -1, static _ => true));
        Assert.Throws<ArgumentNullException>(() => PropClusterLights.CountClusters(null!));
    }

    /// <summary>
    /// A point's planned direct samples through the cluster lists are the
    /// samples the full walk over every light planned: the same samples, in
    /// the same order, with the same segments in the batch, over points in
    /// several clusters and outside every cluster, with lights out of some
    /// clusters' PVS and lights of other styles.
    /// </summary>
    /// <param name="stock">Whether directions are normalised as stock does.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannedSamplesMatchTheFullLightWalk(bool stock)
    {
        AmbientScene scene = _ambient.Ldr;
        int clusters = PropClusterLights.CountClusters(scene);
        int rowBytes = Math.Max((clusters + 7) >> 3, 1);

        List<PropLight> lights = [];
        EmitType[] types = [EmitType.Point, EmitType.Spotlight, EmitType.Surface, EmitType.SkyLight, EmitType.SkyAmbient];
        for (int i = 0; i < 20; i++)
        {
            byte[] row = new byte[rowBytes];
            for (int c = 0; c < clusters; c++)
            {
                // Each light sees a different, partial set of clusters; every
                // fifth sees them all and every seventh none.
                bool sees = i % 5 == 0 || (i % 7 != 6 && ((c * 3) + i) % 4 != 0);
                if (sees)
                {
                    row[c >> 3] |= (byte)(1 << (c & 7));
                }
            }

            lights.Add(new PropLight
            {
                Type = types[i % types.Length],
                Style = i % 4 == 3 ? 1 + (i % 3) : 0,
                Origin = new Vec3(100 + (i * 17), -80 + (i * 9), 60 + (i * 5)),
                Normal = new Vec3(0.2f, -0.1f, -0.97f),
                Intensity = new Vec3(1 + i, 2, 3),
                StopDot = 0.8f,
                StopDot2 = 0.4f,
                Exponent = 1,
                QuadraticAttn = 1,
                Pvs = row,
            });
        }

        KdRayTracer tracer = KdRayTracer.Build(
        [
            new TracedTriangle(TraceId.Opaque, new Vec3(-4000, -4000, -16), new Vec3(4000, -4000, -16), new Vec3(0, 4000, -16), 0),
            new TracedTriangle(TraceId.Opaque, new Vec3(330, -200, -50), new Vec3(330, 200, -50), new Vec3(330, 0, 400), 0),
            new TracedTriangle(TraceId.Sky, new Vec3(-4000, -4000, 1500), new Vec3(4000, -4000, 1500), new Vec3(0, 4000, 1500), 0),
        ]);
        ComplianceOptions compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct;
        PropLightSampler sampler = new(tracer, compliance, sunAngularExtent: 0.05f);
        PropClusterLights lists = PropClusterLights.ForStaticProps(scene, lights);

        // A point at the centre of every leaf: most land in a cluster, the
        // solid leaves' in none.
        HashSet<int> seen = [];
        bool skippedByPvs = false;
        foreach (DLeaf leaf in scene.Leaves)
        {
            Vec3 point = new(
                (leaf.Mins[0] + leaf.Maxs[0]) * 0.5f,
                (leaf.Mins[1] + leaf.Maxs[1]) * 0.5f,
                (leaf.Mins[2] + leaf.Maxs[2]) * 0.5f);
            Vec3 normal = new(0, 0.6f, 0.8f);
            int cluster = DetailPropLighting.ClusterFromPoint(scene, point);
            seen.Add(cluster < 0 ? -1 : cluster);

            TestLineBatch oldLines = sampler.CreateBatch();
            List<(PendingPropSample, Vec3)> oldSamples = [];
            OldPlanDirect(scene, point, normal, PropGatherFlags.None, -1, lights, sampler, stock, oldLines, oldSamples);

            TestLineBatch newLines = sampler.CreateBatch();
            List<(PendingPropSample, Vec3)> newSamples = [];
            int first = StaticPropLighting.PlanDirect(
                scene, point, normal, PropGatherFlags.None, -1, lights, lists, sampler, stock, newLines, newSamples);

            Assert.Equal(0, first);
            Assert.Equal(oldSamples, newSamples);
            Assert.Equal(oldLines.Count, newLines.Count);

            oldLines.Trace(CancellationToken.None);
            newLines.Trace(CancellationToken.None);
            for (int k = 0; k < oldLines.Count; k++)
            {
                Assert.Equal(oldLines.IsBlocked(k), newLines.IsBlocked(k));
                Assert.Equal(oldLines.Payload(k), newLines.Payload(k));
            }

            int styleZero = lights.Count(l => l.Style == 0);
            skippedByPvs |= cluster >= 0 && newSamples.Count < styleZero;
        }

        // The fixture must reach every path: several clusters, no cluster,
        // and lights a point's cluster cannot see.
        Assert.Contains(-1, seen);
        Assert.True(seen.Count(c => c >= 0) >= 2, "points should land in at least two clusters");
        Assert.True(skippedByPvs, "some point should skip a style-0 light by its PVS");
    }

    /// <summary>
    /// The planning walk as it was before the cluster lists, kept here as the
    /// independent reference: every light, style 0 only, the PVS bit tested
    /// for a non-negative cluster, the sample planned from the fudged point.
    /// </summary>
    private static void OldPlanDirect(
        AmbientScene scene,
        Vec3 position,
        Vec3 normal,
        PropGatherFlags flags,
        int skipId,
        IReadOnlyList<PropLight> lights,
        PropLightSampler sampler,
        bool stockNormalise,
        TestLineBatch lines,
        List<(PendingPropSample, Vec3)> samples)
    {
        int cluster = DetailPropLighting.ClusterFromPoint(scene, position);
        for (int i = 0; i < lights.Count; i++)
        {
            PropLight dl = lights[i];
            if (dl.Style != 0)
            {
                continue;
            }

            if (cluster >= 0 && (dl.Pvs[cluster >> 3] & (1 << (cluster & 7))) == 0)
            {
                continue;
            }

            Vec3 adjusted;
            if (dl.Type != EmitType.SkyAmbient)
            {
                Vec3 fudge;
                if (dl.Type == EmitType.SkyLight)
                {
                    fudge = new Vec3(-dl.Normal.X, -dl.Normal.Y, -dl.Normal.Z);
                }
                else
                {
                    fudge = dl.Origin - position;
                    fudge = stockNormalise ? fudge.NormaliseLikeStock().Normalised : fudge.Normalise().Normalised;
                }

                fudge = new Vec3(fudge.X * 4.0f, fudge.Y * 4.0f, fudge.Z * 4.0f);
                adjusted = position + fudge;
            }
            else
            {
                adjusted = position + new Vec3(normal.X * 4.0f, normal.Y * 4.0f, normal.Z * 4.0f);
            }

            PendingPropSample s = sampler.Plan(dl, adjusted, normal, lines, flags | PropGatherFlags.ForceFast, 0.0f, skipId);
            samples.Add((s, dl.Intensity));
        }
    }

    private static PropLight Light(EmitType type, int style, byte pvs) =>
        new() { Type = type, Style = style, Pvs = [pvs] };

    private static PropLight LightRow(EmitType type, int style, byte low, byte high) =>
        new() { Type = type, Style = style, Pvs = [low, high] };
}
