using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// The gather kernels on hand-built groups, with ray answers supplied by
/// hand: collect, then replay with chosen bits.
/// </summary>
public sealed class GatherTests
{
    private static readonly LeafInfo[] OneLeaf = [new LeafInfo(0, 0, 0, 0, new Vec3(-1e4f, -1e4f, -1e4f), new Vec3(1e4f, 1e4f, 1e4f), 0, 0)];

    private static DirectLightGatherer Gatherer(
        DirectLight light, bool stock = false, float sunExtent = 0, SkyCameras? cameras = null, bool fast = false)
    {
        LightTestMap map = LightBox.Map();
        LightGeometry g = Geometry.Load(map);
        DirectLightingSettings settings = new()
        {
            Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct,
            Fast = fast,
        };
        return new DirectLightGatherer([light], sunExtent, settings, new CompiledBspTree(g), OneLeaf, cameras ?? SkyCameras.None);
    }

    private static SampleGroup Floor(params Vec3[] points)
    {
        SampleGroup group = new() { Count = points.Length, NormalCount = 1 };
        for (int lane = 0; lane < 4; lane++)
        {
            group.Points[lane] = points[Math.Min(lane, points.Length - 1)];
            group.Normal(0, lane) = new Vec3(0, 0, 1);
        }

        return group;
    }

    /// <summary>Runs a gather twice: collecting, then replaying every ray as <paramref name="blocked"/>.</summary>
    private static (GatherOutput Output, LightRayLog Rays, int Vis, int Sky) Run(
        DirectLightGatherer gatherer, SampleGroup group, bool blocked = false, bool skyHit = true)
    {
        LightRayLog rays = new();
        GatherOutput output = new();
        gatherer.Gather(gatherer.Lights[0], group, [], rays, output);
        int vis = rays.VisibilityCount;
        int sky = rays.SkyCount;
        ulong[] bits = new ulong[Math.Max(1, (vis + 63) / 64)];
        if (blocked)
        {
            Array.Fill(bits, ulong.MaxValue);
        }

        HitId[] hits = new HitId[sky];
        Array.Fill(hits, skyHit ? new HitId(TraceId.Sky, 0.5f) : new HitId(TraceId.Opaque, 0.5f));
        rays.BeginReplay(bits, 0, hits, 0);
        gatherer.Gather(gatherer.Lights[0], group, [], rays, output);
        Assert.True(rays.ReplayComplete);
        return (output, rays, vis, sky);
    }

    private static DirectLight Point(Vec3 origin, float constant = 1, float linear = 0, float quadratic = 0) =>
        new() { Type = EmitType.Point, Origin = origin, ConstantAttn = constant, LinearAttn = linear, QuadraticAttn = quadratic };

    [Fact]
    public void WithPadCallsOneLightsLanesAreAPacketOfTheirOwn()
    {
        // Stock traces one TestLine's four lanes as one Trace4Rays packet;
        // with PadCalls the traced lanes of a call are padded to four, so the
        // next call's rays never share its packet.
        DirectLightGatherer g = Gatherer(Point(new Vec3(0, 0, 100)));
        LightRayLog rays = new() { PadCalls = true };
        GatherOutput output = new();
        bool[] needed = [true, false, true, false];
        g.Gather(g.Lights[0], Floor(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 0, 0)), needed, rays, output);
        Assert.Equal(4, rays.VisibilityCount);
        Assert.Equal(rays.VisibilityRays()[1], rays.VisibilityRays()[3]);

        // Lane 0 clear, lane 2 blocked; the padding's answers are skipped.
        rays.BeginReplay(new ulong[] { 0b1110 }, 0, Array.Empty<HitId>(), 0);
        g.Gather(g.Lights[0], Floor(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 0, 0)), needed, rays, output);
        Assert.True(output.Dot[0] > 0f);
        Assert.Equal(0f, output.Dot[2]);
        Assert.True(rays.ReplayComplete);
    }

    [Fact]
    public void APointLightOverheadHasUnitDot()
    {
        (GatherOutput o, _, int vis, _) = Run(Gatherer(Point(new Vec3(0, 0, 100))), Floor(Vec3.Zero));
        Assert.Equal(1f, o.Dot[0]);
        Assert.Equal(1, vis);
    }

    [Fact]
    public void AnInverseSquareFalloffIsOneOverDistanceSquared()
    {
        // lightmap.cpp:1885-1890: 1 / (d*d*q + l*d + c).
        (GatherOutput o, _, _, _) = Run(Gatherer(Point(new Vec3(0, 0, 10), 0, 0, 1)), Floor(Vec3.Zero));
        Assert.Equal(0.01f, o.Falloff[0]);
    }

    [Fact]
    public void TheFalloffDistanceIsClampedToOneUnit()
    {
        // :1875 dist = max(dist, 1).
        (GatherOutput o, _, _, _) = Run(Gatherer(Point(new Vec3(0, 0, 0.5f), 0, 0, 1)), Floor(Vec3.Zero));
        Assert.Equal(1f, o.Falloff[0]);
    }

    [Fact]
    public void ABlockedRayZeroesTheDot()
    {
        (GatherOutput o, _, _, _) = Run(Gatherer(Point(new Vec3(0, 0, 100))), Floor(Vec3.Zero), blocked: true);
        Assert.Equal(0f, o.Dot[0]);
    }

    [Fact]
    public void ALightBehindTheSampleAsksForNoRay()
    {
        (GatherOutput o, _, int vis, _) = Run(Gatherer(Point(new Vec3(0, 0, -100))), Floor(Vec3.Zero));
        Assert.Equal(0, vis);
        Assert.Equal(0f, o.Dot[0]);
    }

    [Fact]
    public void AtFortyFiveDegreesTheDotIsTheCosine()
    {
        (GatherOutput o, _, _, _) = Run(Gatherer(Point(new Vec3(100, 0, 100))), Floor(Vec3.Zero));
        Assert.Equal(MathF.Sqrt(0.5f), o.Dot[0], 6);
    }

    [Fact]
    public void ASpotOutsideItsOuterConeContributesNothingAndTracesNothing()
    {
        DirectLight spot = Point(new Vec3(100, 0, 100));
        spot.Type = EmitType.Spotlight;
        spot.Normal = new Vec3(0, 0, -1);
        spot.StopDot = MathF.Cos(10 * MathF.PI / 180);
        spot.StopDot2 = MathF.Cos(20 * MathF.PI / 180);
        (GatherOutput o, _, int vis, _) = Run(Gatherer(spot), Floor(Vec3.Zero));
        Assert.Equal(0, vis);
        Assert.Equal(0f, o.Dot[0] * o.Falloff[0]);
    }

    [Fact]
    public void ASpotInsideItsInnerConeIsFullStrengthTimesTheConeDot()
    {
        DirectLight spot = Point(new Vec3(0, 0, 100));
        spot.Type = EmitType.Spotlight;
        spot.Normal = new Vec3(0, 0, -1);
        spot.StopDot = MathF.Cos(30 * MathF.PI / 180);
        spot.StopDot2 = MathF.Cos(45 * MathF.PI / 180);
        (GatherOutput o, _, _, _) = Run(Gatherer(spot), Floor(Vec3.Zero));
        Assert.Equal(1f, o.Falloff[0]);
    }

    [Fact]
    public void ASpotsFringeInterpolatesBetweenTheCones()
    {
        // :1924-1943: mult = (dot2 - stopdot2) / (stopdot - stopdot2).
        DirectLight spot = Point(new Vec3(0, 0, 100));
        spot.Type = EmitType.Spotlight;
        spot.Normal = new Vec3(0.3f, 0, -1).Normalise().Normalised;
        spot.StopDot = 0.99f;
        spot.StopDot2 = 0.9f;
        (GatherOutput o, _, _, _) = Run(Gatherer(spot), Floor(Vec3.Zero));
        float dot2 = -Vec3.Dot(new Vec3(0, 0, 1), spot.Normal);
        float mult = (dot2 - 0.9f) / (0.99f - 0.9f);
        Assert.Equal(mult * dot2, o.Falloff[0], 5);
    }

    [Fact]
    public void AStockSpotExponentIsRoundedDownToAQuarter()
    {
        // StockQuirk.SpotExponentQuarterSteps: 1.3 behaves as 1.25.
        Assert.Equal(MathF.Pow(0.5f, 1.25f), StockSimd.FixedPointPow(0.5f, 1.3f), 6);
        Assert.NotEqual(MathF.Pow(0.5f, 1.3f), StockSimd.FixedPointPow(0.5f, 1.3f));
    }

    [Fact]
    public void AnIntegerExponentIsExactInFixedPoint() => Assert.Equal(0.125f, StockSimd.FixedPointPow(0.5f, 3));

    [Fact]
    public void ASurfaceLightFallsOffAsItsNormalDotOverDistanceSquared()
    {
        DirectLight surface = new()
        {
            Type = EmitType.Surface, Origin = new Vec3(0, 0, 10), Normal = new Vec3(0, 0, -1),
        };
        (GatherOutput o, LightRayLog rays, _, _) = Run(Gatherer(surface), Floor(Vec3.Zero));
        Assert.Equal(1f / 100f, o.Falloff[0]);

        // The ray ends DIST_EPSILON off the emitter (:1904-1906).
        Ray r = rays.VisibilityRays()[0];
        Assert.Equal(10f - LightConstants.DistEpsilon, r.OriginZ + r.DirectionZ, 5);
    }

    [Fact]
    public void BeyondTheHardFalloffEndNothingIsGathered()
    {
        DirectLight light = Point(new Vec3(0, 0, 100));
        light.StartFadeDistance = 10;
        light.EndFadeDistance = 50;
        (GatherOutput o, _, int vis, _) = Run(Gatherer(light), Floor(Vec3.Zero));
        Assert.Equal(0, vis);
        Assert.Equal(0f, o.Dot[0]);
    }

    [Fact]
    public void InsideTheFadeTheQuinticCurveScales()
    {
        // t = 1 - (d - start)/(end - start) = 0.5; 6t^5 - 15t^4 + 10t^3 = 0.5.
        DirectLight light = Point(new Vec3(0, 0, 30));
        light.StartFadeDistance = 10;
        light.EndFadeDistance = 50;
        (GatherOutput o, _, _, _) = Run(Gatherer(light), Floor(Vec3.Zero));
        Assert.Equal(0.5f, o.Falloff[0], 6);
    }

    [Fact]
    public void TheSunCastsTowardItsReverseDirection()
    {
        DirectLight sun = new() { Type = EmitType.SkyLight, Normal = new Vec3(0, 0, -1) };
        (GatherOutput o, LightRayLog rays, _, int sky) = Run(Gatherer(sun), Floor(Vec3.Zero));
        Assert.Equal(1, sky);
        Assert.Equal(LightConstants.MaxTraceLength, rays.SkyRays()[0].DirectionZ);
        Assert.Equal(1f, o.Dot[0]);
        Assert.Equal(10000f, o.SunAmount[0]);
    }

    [Fact]
    public void ASunRayHittingGeometryIsShadowed()
    {
        DirectLight sun = new() { Type = EmitType.SkyLight, Normal = new Vec3(0, 0, -1) };
        (GatherOutput o, _, _, _) = Run(Gatherer(sun), Floor(Vec3.Zero), skyHit: false);
        Assert.Equal(0f, o.Dot[0]);
    }

    [Fact]
    public void ASoftSunCastsThirtyRays()
    {
        DirectLight sun = new() { Type = EmitType.SkyLight, Normal = new Vec3(0, 0, -1) };
        (_, _, _, int sky) = Run(Gatherer(sun, sunExtent: 0.05f), Floor(Vec3.Zero));
        Assert.Equal(LightConstants.SunAreaLightSamples, sky);
    }

    [Fact]
    public void FastQuartersTheSoftSun()
    {
        DirectLight sun = new() { Type = EmitType.SkyLight, Normal = new Vec3(0, 0, -1) };
        (_, _, _, int sky) = Run(Gatherer(sun, sunExtent: 0.05f, fast: true), Floor(Vec3.Zero));
        Assert.Equal(LightConstants.SunAreaLightSamples / 4, sky);
    }

    [Fact]
    public void AnOpenSkyGivesTheAmbientUnitDot()
    {
        // :1823-1832: sum(frac * dot) / sum(dot) over the valid hemisphere,
        // with every ray reaching sky, is exactly 1.
        DirectLight ambient = new() { Type = EmitType.SkyAmbient };
        (GatherOutput o, _, _, int sky) = Run(Gatherer(ambient), Floor(Vec3.Zero));
        Assert.Equal(1f, o.Dot[0], 6);
        Assert.InRange(sky, 1, LightConstants.VertexNormalCount - 1);
    }

    [Fact]
    public void AClosedSkyGivesTheAmbientNothing()
    {
        DirectLight ambient = new() { Type = EmitType.SkyAmbient };
        (GatherOutput o, _, _, _) = Run(Gatherer(ambient), Floor(Vec3.Zero), skyHit: false);
        Assert.Equal(0f, o.Dot[0]);
    }

    [Fact]
    public void PaddingLanesCopyTheLastRealLaneAndAreNotTraced()
    {
        (GatherOutput o, _, int vis, _) = Run(Gatherer(Point(new Vec3(0, 0, 100))), Floor(Vec3.Zero, new Vec3(10, 0, 0)));
        Assert.Equal(2, vis);
        Assert.Equal(o.Dot[1], o.Dot[3]);
        Assert.Equal(o.Falloff[1], o.Falloff[2]);
    }

    [Fact]
    public void ABumpDotIsZeroedWhenTheFlatDotIs()
    {
        // :2051-2057.
        SampleGroup group = Floor(Vec3.Zero);
        group.NormalCount = 4;
        for (int n = 1; n < 4; n++)
        {
            group.Normal(n, 0) = new Vec3(0, 0, -1);
        }

        (GatherOutput o, _, _, _) = Run(Gatherer(Point(new Vec3(0, 0, -100))), group);
        for (int n = 1; n < 4; n++)
        {
            Assert.Equal(0f, o.Dot[n * 4]);
        }
    }

    [Fact]
    public void IgnoreNormalsUsesTheConstantDot()
    {
        LightRayLog rays = new();
        GatherOutput o = new();
        DirectLightGatherer g = Gatherer(Point(new Vec3(0, 0, -100)));
        g.Gather(g.Lights[0], Floor(Vec3.Zero), [], rays, o, GatherFlags.IgnoreNormals);
        Assert.Equal(LightConstants.ConstantDot, o.Dot[0]);
    }

    [Fact]
    public void TheStockEstimateIsNotAlwaysTheExactReciprocal()
    {
        // StockQuirk.GatherReciprocalEstimate: rcpss + one Newton step.
        int differ = 0;
        for (int i = 1; i < 2000; i++)
        {
            float x = i * 0.37f;
            if (StockSimd.Reciprocal(x, true) != 1.0f / x)
            {
                differ++;
            }
        }

        Assert.True(differ > 0);
    }

    [Fact]
    public void TheEstimateIsWithinAFewUlpOfTheReciprocal()
    {
        for (int i = 1; i < 200; i++)
        {
            float x = i * 1.7f;
            Assert.Equal(1.0f / x, StockSimd.Reciprocal(x, true), 1e-6f * (1.0f / x));
            Assert.Equal(1.0f / MathF.Sqrt(x), StockSimd.ReciprocalSqrt(x, true), 1e-6f / MathF.Sqrt(x));
        }
    }

    [Fact]
    public void TheExactFormsAreTheDivisions()
    {
        Assert.Equal(1.0f / 3.0f, StockSimd.Reciprocal(3.0f, false));
        Assert.Equal(1.0f / MathF.Sqrt(3.0f), StockSimd.ReciprocalSqrt(3.0f, false));
    }
}

public sealed class SkyRecursionTests
{
    private static DirectLightGatherer WithCamera(bool stock)
    {
        // Two leaves in two areas: leaf 0 (area 0, the world) and leaf 1
        // (area 1, holding the sky camera). Nodes split on x = 1000.
        LightTestMap map = LightBox.Map();
        int plane = map.AddPlane(new Vec3(1, 0, 0), 1000);
        DNode node = new() { PlaneNum = plane };
        node.Children[0] = -2;
        node.Children[1] = -1;
        map.Nodes = [node];
        DLeaf world = new() { Cluster = 0, AreaFlags = 0 };
        DLeaf skybox = new() { Cluster = 0, AreaFlags = 1 };
        map.Leaves = [world, skybox];
        map.AreaCount = 2;
        map.Entities.Add(LightTestMap.Entity(("classname", "sky_camera"), ("origin", "2000 0 0"), ("scale", "16")));
        LightGeometry g = Geometry.Load(map);
        CompiledBspTree tree = new(g);
        SkyCameras cameras = SkyCameras.Build(map.Entities, tree, g.Leaves, g.AreaCount);
        DirectLightingSettings settings = new() { Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct };
        return new DirectLightGatherer([], 0, settings, tree, g.Leaves, cameras);
    }

    [Fact]
    public void ACameraClaimsTheAreaItStandsIn()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "SKY_CAMERA"), ("origin", "1 1 1"), ("scale", "16")));
        map.AreaCount = 1;
        LightGeometry g = Geometry.Load(map);
        SkyCameras cameras = SkyCameras.Build(map.Entities, new CompiledBspTree(g), g.Leaves, g.AreaCount);
        SkyCamera camera = Assert.Single(cameras.Cameras.ToArray());
        Assert.Equal((16f, 1f / 16f, 0), (camera.SkyToWorld, camera.WorldToSky, camera.Area));
        Assert.Equal(0, cameras.CameraInArea(0));
    }

    [Fact]
    public void ACameraWithNoScaleIsIgnored()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "sky_camera"), ("origin", "1 1 1")));
        LightGeometry g = Geometry.Load(map);
        Assert.Empty(SkyCameras.Build(map.Entities, new CompiledBspTree(g), g.Leaves, 1).Cameras.ToArray());
    }

    [Fact]
    public void ASkyRayFromTheWorldRecursesIntoTheSkybox()
    {
        DirectLightGatherer g = WithCamera(stock: false);
        LightRayLog rays = new();
        Vec3[] start = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0)];
        Vec3[] stop = [.. start.Select(s => s + new Vec3(0, 0, 1000))];
        Span<float> frac = stackalloc float[4];
        g.TestLineDoesHitSky(start, stop, 4, rays, frac);
        Assert.Equal((4, 4), (rays.SkyCount, rays.Sky2Count));
    }

    [Fact]
    public void AStockGroupRecursesByLaneZerosArea()
    {
        // Lane 0 is in the skybox's own area, so stock recurses for none of
        // the four (trace.cpp:397); correct recurses for the three in the world.
        Vec3[] start = [new(1500, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0)];
        Vec3[] stop = [.. start.Select(s => s + new Vec3(0, 0, 1000))];
        float[] frac = new float[4];

        LightRayLog stockRays = new();
        WithCamera(stock: true).TestLineDoesHitSky(start, stop, 4, stockRays, frac);
        LightRayLog correctRays = new();
        WithCamera(stock: false).TestLineDoesHitSky(start, stop, 4, correctRays, frac);

        Assert.Equal((4, 0), (stockRays.SkyCount, stockRays.Sky2Count));
        Assert.Equal((4, 4), (correctRays.SkyCount, correctRays.Sky2Count));

        HitId[] open = [.. Enumerable.Repeat(new HitId(TraceId.Sky, 0.5f), 4)];
        HitId[] blockedSkybox = [.. Enumerable.Repeat(new HitId(TraceId.Opaque, 0.5f), 4)];
        correctRays.BeginReplay(new ulong[1], 0, open, 0, blockedSkybox);
        WithCamera(stock: false).TestLineDoesHitSky(start, stop, 4, correctRays, frac);
        Assert.Equal([1f, 0f, 0f, 0f], frac);
    }

    private static (LightRayLog Rays, Vec3[] Start, Vec3[] Stop) DeferredTest(DirectLightGatherer g)
    {
        LightRayLog rays = new() { DeferRecursion = true };
        Vec3[] start = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0)];
        Vec3[] stop = [.. start.Select(s => s + new Vec3(0, 0, 1000))];
        g.TestLineDoesHitSky(start, stop, 4, rays, new float[4]);
        return (rays, start, stop);
    }

    [Fact]
    public void ADeferredSkyTestAsksForNoSkyboxRaysUntilItsFirstRaysAreAnswered()
    {
        (LightRayLog rays, _, _) = DeferredTest(WithCamera(stock: false));
        Assert.Equal((4, 0, 1), (rays.SkyCount, rays.Sky2Count, rays.Deferred.Length));
    }

    [Fact]
    public void AFullyOccludedDeferredTestTracesNoSkyboxRays()
    {
        // trace.cpp:387: stock recurses only when not every lane is occluded.
        DirectLightGatherer g = WithCamera(stock: false);
        (LightRayLog rays, _, _) = DeferredTest(g);
        rays.FirstStageAnswers().Hits.Span.Fill(new HitId(TraceId.Opaque, 0.5f));
        g.EmitDeferredRecursion(rays);
        Assert.Equal(0, rays.Sky2Count);
    }

    [Fact]
    public void ADeferredTestWithOneOpenLaneTracesEveryCamerasRays()
    {
        DirectLightGatherer g = WithCamera(stock: false);
        (LightRayLog rays, _, _) = DeferredTest(g);
        Span<HitId> hits = rays.FirstStageAnswers().Hits.Span;
        hits.Fill(new HitId(TraceId.Opaque, 0.5f));
        hits[2] = new HitId(TraceId.Sky, 0.5f);
        g.EmitDeferredRecursion(rays);
        Assert.Equal(4, rays.Sky2Count);
    }

    [Fact]
    public void DeferredAndEagerRecursionGiveTheSameFractions()
    {
        DirectLightGatherer g = WithCamera(stock: false);
        HitId[] first = [new(TraceId.Opaque, 0.5f), new(TraceId.Sky, 0.5f), new(TraceId.Sky, 0.5f), new(TraceId.Opaque, 0.5f)];
        HitId[] skybox = [new(TraceId.Opaque, 0.5f), new(TraceId.Opaque, 0.5f), new(TraceId.Sky, 0.5f), new(TraceId.Sky, 0.5f)];

        (LightRayLog eager, Vec3[] start, Vec3[] stop) = DeferredTest(g);
        eager = new LightRayLog();
        g.TestLineDoesHitSky(start, stop, 4, eager, new float[4]);
        eager.BeginReplay(new ulong[1], 0, first, 0, skybox);
        float[] eagerFraction = new float[4];
        g.TestLineDoesHitSky(start, stop, 4, eager, eagerFraction);

        (LightRayLog deferred, _, _) = DeferredTest(g);
        first.CopyTo(deferred.FirstStageAnswers().Hits.Span);
        g.EmitDeferredRecursion(deferred);
        skybox.CopyTo(deferred.SecondStageAnswers().Span);
        deferred.BeginResolve();
        float[] deferredFraction = new float[4];
        g.TestLineDoesHitSky(start, stop, 4, deferred, deferredFraction);

        Assert.Equal(eagerFraction, deferredFraction);
        Assert.Equal([0f, 0f, 1f, 0f], deferredFraction);
        Assert.True(deferred.ReplayComplete);
    }

    [Fact]
    public void OneCamerasSkyboxRaysArePaddedToAWholePacket()
    {
        DirectLightGatherer g = WithCamera(stock: false);
        LightRayLog rays = new();
        Vec3[] start = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, 0)];
        Vec3[] stop = [.. start.Select(s => s + new Vec3(0, 0, 1000))];
        g.TestLineDoesHitSky(start, stop, 3, rays, new float[4]);
        Assert.Equal((3, 4), (rays.SkyCount, rays.Sky2Count));
    }
}

public sealed class LightRayLogTests
{
    [Fact]
    public void EndItemPadsBothKindsToWholePacketsWithTheLastRay()
    {
        LightRayLog log = new();
        log.TestLine(new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        log.TestLine(new Vec3(0, 0, 0), new Vec3(2, 0, 0));
        log.SkyOcclusion(new Vec3(0, 0, 0), new Vec3(3, 0, 0));
        log.EndItem();
        Assert.Equal((4, 4), (log.VisibilityCount, log.SkyCount));
        Assert.Equal(log.VisibilityRays()[1], log.VisibilityRays()[3]);
        Assert.Equal(log.SkyRays()[0], log.SkyRays()[3]);
    }

    [Fact]
    public void EndItemOnWholePacketsAddsNothing()
    {
        LightRayLog log = new();
        for (int i = 0; i < 4; i++)
        {
            log.TestLine(new Vec3(0, 0, 0), new Vec3(i + 1, 0, 0));
        }

        log.EndItem();
        Assert.Equal((4, 0), (log.VisibilityCount, log.SkyCount));
    }

    [Fact]
    public void SkipItemPaddingReadsTheNextItemFromTheNextPacket()
    {
        LightRayLog log = new();
        log.TestLine(new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        log.EndItem();
        log.TestLine(new Vec3(0, 0, 0), new Vec3(2, 0, 0));
        log.EndItem();
        log.BeginReplay(new ulong[] { 1UL << 4 }, 0, Array.Empty<HitId>(), 0);
        Assert.Equal(1f, log.TestLine(default, default));
        log.SkipItemPadding();
        Assert.Equal(0f, log.TestLine(default, default));
        log.SkipItemPadding();
        Assert.True(log.ReplayComplete);
    }

    [Fact]
    public void ARayIsTheWholeSegmentWithReachOne()
    {
        Ray r = LightRayLog.MakeRay(new Vec3(1, 2, 3), new Vec3(4, 6, 8));
        Assert.Equal((1f, 2f, 3f, 3f, 4f, 5f, 1f), (r.OriginX, r.OriginY, r.OriginZ, r.DirectionX, r.DirectionY, r.DirectionZ, r.MaxDistance));
    }

    [Fact]
    public void AHitPastTheSegmentsEndDoesNotBlock()
    {
        // trace.cpp:171-172: HitDistance < len. The tracer reports the
        // nearest hit on the whole line (raytrace.cpp:496 is commented out).
        Assert.False(LightRayLog.IsBlocking(new HitId(5, 2.03f)));
        Assert.True(LightRayLog.IsBlocking(new HitId(5, 0.99f)));
        Assert.False(LightRayLog.IsBlocking(HitId.Missed));
    }

    [Fact]
    public void CollectingAnswersVisibleAndRecords()
    {
        LightRayLog log = new();
        Assert.Equal(1f, log.TestLine(Vec3.Zero, new Vec3(1, 0, 0)));
        Assert.Equal(0f, log.SkyOcclusion(Vec3.Zero, new Vec3(0, 0, 1)));
        Assert.Equal((1, 1), (log.VisibilityCount, log.SkyCount));
    }

    [Fact]
    public void ReplayReadsTheBitsInOrder()
    {
        LightRayLog log = new();
        log.TestLine(Vec3.Zero, new Vec3(1, 0, 0));
        log.TestLine(Vec3.Zero, new Vec3(2, 0, 0));
        log.BeginReplay(new ulong[] { 0b10 }, 0, Array.Empty<HitId>(), 0);
        Assert.Equal(1f, log.TestLine(Vec3.Zero, new Vec3(1, 0, 0)));
        Assert.Equal(0f, log.TestLine(Vec3.Zero, new Vec3(2, 0, 0)));
        Assert.True(log.ReplayComplete);
    }

    [Fact]
    public void ReplayHonoursItsBase()
    {
        LightRayLog log = new();
        log.TestLine(Vec3.Zero, new Vec3(1, 0, 0));
        log.BeginReplay(new ulong[] { 1UL << 5 }, 5, Array.Empty<HitId>(), 0);
        Assert.Equal(0f, log.TestLine(Vec3.Zero, new Vec3(1, 0, 0)));
    }

    [Fact]
    public void ASkyHitIsNotOcclusion()
    {
        LightRayLog log = new();
        log.SkyOcclusion(Vec3.Zero, new Vec3(0, 0, 1));
        log.SkyOcclusion(Vec3.Zero, new Vec3(0, 0, 1));
        log.BeginReplay(new ulong[1], 0, new HitId[] { new(TraceId.Sky, 0.5f), new(TraceId.Opaque, 0.5f) }, 0);
        Assert.Equal(0f, log.SkyOcclusion(Vec3.Zero, new Vec3(0, 0, 1)));
        Assert.Equal(1f, log.SkyOcclusion(Vec3.Zero, new Vec3(0, 0, 1)));
    }

    [Fact]
    public void AReplayThatAsksForMoreThanWasRecordedThrows()
    {
        LightRayLog log = new();
        log.BeginReplay(new ulong[1], 0, Array.Empty<HitId>(), 0);
        Assert.Throws<InvalidOperationException>(() => log.TestLine(Vec3.Zero, Vec3.Zero));
    }

    [Fact]
    public void ResetReturnsToCollecting()
    {
        LightRayLog log = new();
        log.TestLine(Vec3.Zero, Vec3.Zero);
        log.BeginReplay(new ulong[1], 0, Array.Empty<HitId>(), 0);
        log.Reset();
        Assert.True(log.Collecting);
        Assert.Equal(0, log.VisibilityCount);
    }
}

public sealed class PointsInWindingTests
{
    private static readonly Vec3[] Square = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)];

    [Fact]
    public void AllFourInsideLeavesNoInvalidBits()
    {
        Vec3[] p = [new(1, 1, 0), new(5, 5, 0), new(9, 9, 0), new(2, 8, 0)];
        Assert.True(FaceLightJob.PointsInWinding(p, Square, out int invalid));
        Assert.Equal(0, invalid);
    }

    [Fact]
    public void OneOutsideSetsItsBit()
    {
        Vec3[] p = [new(1, 1, 0), new(50, 5, 0), new(9, 9, 0), new(2, 8, 0)];
        Assert.True(FaceLightJob.PointsInWinding(p, Square, out int invalid));
        Assert.Equal(0b10, invalid);
    }

    [Fact]
    public void AllOutsideIsFalse()
    {
        Vec3[] p = [new(-1, -1, 0), new(50, 5, 0), new(-9, 9, 0), new(2, 80, 0)];
        Assert.False(FaceLightJob.PointsInWinding(p, Square, out _));
    }
}
