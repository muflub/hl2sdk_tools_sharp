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
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>Random point, spot and surface lights and groups near them, for the gather facts.</summary>
internal static class RandomLights
{
    internal static Vec3 Unit(Random r)
    {
        while (true)
        {
            Vec3 v = new((float)((r.NextDouble() * 2) - 1), (float)((r.NextDouble() * 2) - 1), (float)((r.NextDouble() * 2) - 1));
            float l2 = v.LengthSquared();
            if (l2 is > 0.01f and <= 1.0f)
            {
                return v.Normalise().Normalised;
            }
        }
    }

    // A unit vector at angle acos(cos) from the unit axis a.
    internal static Vec3 AtAngle(Random r, Vec3 a, double cos)
    {
        Vec3 t = Vec3.Cross(a, Unit(r)).Normalise().Normalised;
        double sin = Math.Sqrt(Math.Max(0.0, 1.0 - (cos * cos)));
        return new Vec3(
            (float)((a.X * cos) + (t.X * sin)),
            (float)((a.Y * cos) + (t.Y * sin)),
            (float)((a.Z * cos) + (t.Z * sin)));
    }

    /// <summary>A light of the type with every parameter the gather reads drawn at random, awkward values included.</summary>
    internal static DirectLight Standard(Random r, EmitType type)
    {
        DirectLight light = new()
        {
            Type = type,
            Origin = new Vec3((float)(r.NextDouble() - 0.5) * 1024, (float)(r.NextDouble() - 0.5) * 1024, (float)(r.NextDouble() - 0.5) * 1024),
            Intensity = new Vec3((float)r.NextDouble() * 500, (float)r.NextDouble() * 500, r.Next(10) == 0 ? -40 : (float)r.NextDouble() * 500),
            ConstantAttn = r.Next(3) == 0 ? 0 : (float)r.NextDouble(),
            LinearAttn = r.Next(2) == 0 ? 0 : (float)r.NextDouble() * 0.1f,
            QuadraticAttn = r.Next(3) == 0 ? 0 : (float)r.NextDouble() * 0.01f,
            CapDist = r.Next(4) == 0 ? (float)r.NextDouble() * 300 : 1.0e22f,
        };

        if (r.Next(2) == 0)
        {
            light.StartFadeDistance = (float)r.NextDouble() * 200;
            light.EndFadeDistance = light.StartFadeDistance + ((float)r.NextDouble() * 400) - 50;
        }

        if (type is EmitType.Spotlight or EmitType.Surface)
        {
            light.Normal = Unit(r);
        }

        if (type == EmitType.Spotlight)
        {
            // Cones from a few degrees to beyond a hemisphere; now and then an
            // inner cone equal to the outer one (a zero-width fringe).
            light.StopDot2 = (float)Math.Cos(r.NextDouble() * Math.PI * 0.6);
            light.StopDot = r.Next(6) == 0 ? light.StopDot2 : light.StopDot2 + ((1.0f - light.StopDot2) * (float)r.NextDouble());
            light.Exponent = r.Next(4) switch
            {
                0 => 0,
                1 => 1,
                2 => (float)r.NextDouble() * 5,
                _ => r.Next(3) == 0 ? -(float)r.NextDouble() * 2 : (float)r.NextDouble() * 1.7f,
            };
        }

        return light;
    }

    /// <summary>
    /// A group whose lanes lie within <paramref name="spread"/> of a point in a
    /// random direction from the light; normals flat or scattered, bumped or not.
    /// Padding lanes copy the last real one, as the face gather pads them.
    /// </summary>
    internal static SampleGroup GroupAround(Random r, DirectLight light, int count, float spread)
    {
        Vec3 dir = light.Type == EmitType.Point || r.Next(3) == 0
            ? Unit(r)
            : AtAngle(r, light.Normal, (r.NextDouble() * 2) - 1);
        Vec3 centre = light.Origin + (dir * (float)((r.NextDouble() * 1500) + 0.5));
        bool flat = r.Next(2) == 0;
        Vec3 flatNormal = Unit(r);
        SampleGroup group = new() { Count = count, NormalCount = r.Next(2) == 0 ? 1 : BumpBasis.LightmapCount };
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            int src = Math.Min(lane, count - 1);
            if (src < lane)
            {
                group.Points[lane] = group.Points[src];
                for (int n = 0; n < BumpBasis.LightmapCount; n++)
                {
                    group.Normal(n, lane) = group.Normal(n, src);
                }

                continue;
            }

            group.Points[lane] = centre + (Unit(r) * (float)(r.NextDouble() * spread));
            group.Normal(0, lane) = flat ? flatNormal : Unit(r);
            for (int n = 1; n < BumpBasis.LightmapCount; n++)
            {
                group.Normal(n, lane) = Unit(r);
            }
        }

        return group;
    }

    /// <summary>Copies lane <c>count - 1</c> into the padding lanes.</summary>
    internal static void Pad(SampleGroup group)
    {
        for (int lane = group.Count; lane < SampleGroup.Lanes; lane++)
        {
            group.Points[lane] = group.Points[group.Count - 1];
            for (int n = 0; n < BumpBasis.LightmapCount; n++)
            {
                group.Normal(n, lane) = group.Normal(n, group.Count - 1);
            }
        }
    }
}

/// <summary>
/// <see cref="DeadLightCull"/>: a (group, light) record is left out only when
/// the uncut gather would emit no ray and leave every <c>dot * falloff</c>
/// zero, under either policy's arithmetic.
/// </summary>
public sealed class DeadLightCullTests
{
    private static readonly LeafInfo[] OneLeaf = [new LeafInfo(0, 0, 0, 0, new Vec3(-1e4f, -1e4f, -1e4f), new Vec3(1e4f, 1e4f, 1e4f), 0, 0)];

    private static readonly CompiledBspTree Tree = new(Geometry.Load(LightBox.Map()));

    private static DirectLightGatherer Gatherer(DirectLight light, bool stock, bool keep = false)
    {
        DirectLightingSettings settings = new()
        {
            Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct,
            KeepDeadLights = keep,
        };
        return new DirectLightGatherer([light], 0, settings, Tree, OneLeaf, SkyCameras.None);
    }

    private static bool Culled(DirectLight light, SampleGroup group, GatherFlags flags = GatherFlags.None) =>
        DeadLightCull.IsDead(LightCullShape.Of(light), group, SampleBounds.Of(group), flags);

    /// <summary>
    /// The uncut gather: emit, then resolve with every ray answered visible.
    /// Returns the rays it asked for and the resolved output.
    /// </summary>
    private static (int Rays, GatherOutput Output) Uncut(
        DirectLightGatherer g, SampleGroup group, bool[] needed, GatherFlags flags = GatherFlags.None)
    {
        LightRayLog rays = new() { PadCalls = true };
        GatherOutput output = new();
        g.Emit(g.Lights[0], group, needed, rays, output, flags, 0.0f);
        int count = rays.TotalCount;
        ulong[] bits = new ulong[Math.Max(1, (rays.VisibilityCount + 63) / 64)];
        rays.BeginReplay(bits, 0, new HitId[rays.SkyCount], 0);
        g.Resolve(rays, output);
        Assert.True(rays.ReplayComplete);
        return (count, output);
    }

    /// <summary>
    /// What leaving the record out relies on: no ray, and every product the
    /// face gather forms from it -- <c>mask * dot * falloff</c> in the direct
    /// gather, <c>falloff * dot</c> then times the intensity in the resample,
    /// with a lane masked out as <c>0 * falloff</c> -- is a zero.
    /// </summary>
    private static void AssertContributesNothing(DirectLight light, int rays, GatherOutput output, int normals)
    {
        Assert.Equal(0, rays);
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            float falloff = output.Falloff[lane];
            Assert.True(0.0f * falloff == 0.0f, $"lane {lane}: falloff {falloff} times a masked zero is not zero");
            for (int n = 0; n < normals; n++)
            {
                float f = falloff * output.Dot[(n * SampleGroup.Lanes) + lane];
                Assert.True(f == 0.0f, $"lane {lane} normal {n}: dot * falloff = {f}");
                Assert.True(f * light.Intensity.X == 0.0f && f * light.Intensity.Y == 0.0f && f * light.Intensity.Z == 0.0f);
            }
        }
    }

    private static bool[] RandomNeeded(Random r)
    {
        bool[] needed = new bool[SampleGroup.Lanes];
        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            needed[lane] = r.Next(5) != 0;
        }

        return needed;
    }

    [Theory]
    [InlineData(EmitType.Point, false)]
    [InlineData(EmitType.Point, true)]
    [InlineData(EmitType.Spotlight, false)]
    [InlineData(EmitType.Spotlight, true)]
    [InlineData(EmitType.Surface, false)]
    [InlineData(EmitType.Surface, true)]
    public void ARandomCullIsNeverWrong(EmitType type, bool stock)
    {
        Random r = new(((int)type * 7919) + (stock ? 1 : 0));
        int culled = 0;
        for (int c = 0; c < 20000; c++)
        {
            DirectLight light = RandomLights.Standard(r, type);
            SampleGroup group = RandomLights.GroupAround(r, light, r.Next(1, 5), r.Next(3) == 0 ? 300 : 20);
            if (!Culled(light, group))
            {
                continue;
            }

            culled++;
            (int rays, GatherOutput output) = Uncut(Gatherer(light, stock), group, RandomNeeded(r));
            AssertContributesNothing(light, rays, output, group.NormalCount);
        }

        // Most random pairs are dead one way or another; enough are culled
        // for the check above to mean something.
        Assert.True(culled > 3000, $"only {culled} culls");
    }

    /// <summary>Which boundary a <see cref="ACullAtTheMarginIsNeverWrong"/> case straddles.</summary>
    public enum Boundary
    {
        /// <summary>A spot's cone edge.</summary>
        Cone,

        /// <summary>A surface light's emitting plane.</summary>
        Plane,

        /// <summary>The hard fade distance.</summary>
        Fade,

        /// <summary>The samples' own normals.</summary>
        Behind,
    }

    [Theory]
    [InlineData(Boundary.Cone, false)]
    [InlineData(Boundary.Cone, true)]
    [InlineData(Boundary.Plane, false)]
    [InlineData(Boundary.Plane, true)]
    [InlineData(Boundary.Fade, false)]
    [InlineData(Boundary.Fade, true)]
    [InlineData(Boundary.Behind, false)]
    [InlineData(Boundary.Behind, true)]
    public void ACullAtTheMarginIsNeverWrong(Boundary boundary, bool stock)
    {
        // Samples placed k margins past the boundary, k log-uniform in
        // magnitude from 1e-6 (the scale of float rounding and of the
        // estimates' error) to 2, on either side: the cull must hold
        // everywhere, and must cull the far side beyond the margin.
        Random r = new(((int)boundary * 104729) + (stock ? 1 : 0));
        int culled = 0;
        int outside = 0;
        for (int c = 0; c < 8000; c++)
        {
            // |k| < 1 is inside the margin, k > 1 beyond it, k < 0 on the lit side.
            double k = Math.Pow(10, -6 + (r.NextDouble() * 6.3)) * (r.Next(4) == 0 ? -1 : 1);
            (DirectLight light, SampleGroup group) = AtBoundary(r, boundary, k);
            outside += k > 1.1 ? 1 : 0;
            if (!Culled(light, group))
            {
                continue;
            }

            culled++;
            (int rays, GatherOutput output) = Uncut(Gatherer(light, stock), group, RandomNeeded(r));
            AssertContributesNothing(light, rays, output, group.NormalCount);
        }

        Assert.True(culled > outside / 2, $"{culled} culls for {outside} cases past the margin");
    }

    // One group of tightly clustered samples at relative distance k of the
    // cull's margin from the boundary, on its far side.
    private static (DirectLight Light, SampleGroup Group) AtBoundary(Random r, Boundary boundary, double k)
    {
        const double margin = LightCullShape.CosineMargin;
        EmitType type = boundary switch
        {
            Boundary.Cone => EmitType.Spotlight,
            Boundary.Plane => EmitType.Surface,
            _ => r.Next(3) switch { 0 => EmitType.Point, 1 => EmitType.Spotlight, _ => EmitType.Surface },
        };
        DirectLight light = RandomLights.Standard(r, type);
        light.Intensity = new Vec3(100, 100, 100);
        if (boundary == Boundary.Behind)
        {
            // Finite, positive attenuation: the behind test's precondition.
            light.ConstantAttn = 1;
            light.CapDist = 1.0e22f;
            light.Exponent = Math.Abs(light.Exponent);
        }

        if (boundary == Boundary.Fade)
        {
            light.StartFadeDistance = (float)(r.NextDouble() * 200);
            light.EndFadeDistance = light.StartFadeDistance + 1 + (float)(r.NextDouble() * 400);
        }

        Vec3 axis = light.Normal;
        double dist = 1 + (r.NextDouble() * 1000);
        Vec3 dir = boundary switch
        {
            Boundary.Cone => RandomLights.AtAngle(r, axis, light.StopDot2 - (margin * k)),
            Boundary.Plane => RandomLights.AtAngle(r, axis, -margin * k),
            _ => RandomLights.Unit(r),
        };
        if (boundary == Boundary.Fade)
        {
            dist = light.EndFadeDistance + (Math.Abs(light.EndFadeDistance) * 1e-5 * k) + (1e-3 * k);
        }

        int count = r.Next(1, 5);
        SampleGroup group = new() { Count = count, NormalCount = r.Next(2) == 0 ? 1 : BumpBasis.LightmapCount };
        // The fade margin is absolute and small: keep that group tighter.
        float jitter = (float)(r.NextDouble() * (boundary == Boundary.Fade ? 1e-7 : 1e-4) * dist);
        for (int lane = 0; lane < count; lane++)
        {
            Vec3 p = light.Origin + (dir * (float)dist) + (RandomLights.Unit(r) * (float)(r.NextDouble() * jitter));
            group.Points[lane] = p;

            // Facing the light, except for the behind case, whose normal is
            // turned away from it by k margins.
            Vec3 toLight = (light.Origin - p).Normalise().Normalised;
            group.Normal(0, lane) = boundary == Boundary.Behind
                ? RandomLights.AtAngle(r, toLight, -margin * k)
                : toLight;
            for (int n = 1; n < BumpBasis.LightmapCount; n++)
            {
                group.Normal(n, lane) = RandomLights.Unit(r);
            }
        }

        RandomLights.Pad(group);
        return (light, group);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASpotPointingAwayIsCulledAndOneInsideIsNot(bool stock)
    {
        DirectLight spot = Spot(new Vec3(0, 0, 100), new Vec3(0, 0, 1), coneDegrees: 30);
        SampleGroup below = Group(new Vec3(0, 0, 0), new Vec3(0, 0, 1), spacing: 4);
        Assert.True(Culled(spot, below));
        (int rays, GatherOutput output) = Uncut(Gatherer(spot, stock), below, [true, true, true, true]);
        AssertContributesNothing(spot, rays, output, 1);

        spot.Normal = new Vec3(0, 0, -1);
        Assert.False(Culled(spot, below));
        Assert.True(Uncut(Gatherer(spot, stock), below, [true, true, true, true]).Rays > 0);
    }

    [Fact]
    public void ASphereBehindTheApexIsCulledThroughTheApexDistance()
    {
        // The light points away from a group right behind it: the nearest
        // point of the cone is its apex, 10 units off. The group's radius, 6,
        // is more than 10 sin(150 degrees) = 5 (the distance to the cone's
        // edge line), so only the apex distance proves it outside.
        DirectLight spot = Spot(new Vec3(0, 0, 10), new Vec3(0, 0, 1), coneDegrees: 30);
        SampleGroup group = new() { Count = 4, NormalCount = 1 };
        group.Points[0] = new Vec3(6, 0, 0);
        group.Points[1] = new Vec3(-6, 0, 0);
        group.Points[2] = new Vec3(0, 6, 0);
        group.Points[3] = new Vec3(0, -6, 0);
        for (int lane = 0; lane < 4; lane++)
        {
            // Facing the light, so only the cone can cull.
            group.Normal(0, lane) = (spot.Origin - group.Points[lane]).Normalise().Normalised;
        }

        SampleBounds bounds = SampleBounds.Of(group);
        Assert.True(bounds.Radius > 5.1);
        Assert.True(Culled(spot, group));
        (int rays, GatherOutput output) = Uncut(Gatherer(spot, stock: false), group, [true, true, true, true]);
        AssertContributesNothing(spot, rays, output, 1);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(1.5f)]
    [InlineData(-1.5f)]
    public void ASpotWhoseConeCannotBeTestedIsNotCulledByIt(float stopDot2)
    {
        // NaN; a cosine no direction's reaches (the test keeps to (-1, 1));
        // one every direction's exceeds.
        DirectLight spot = Spot(new Vec3(0, 0, 100), new Vec3(0, 0, 1), coneDegrees: 30);
        spot.StopDot2 = stopDot2;
        spot.StopDot = float.IsNaN(stopDot2) ? 0.9f : stopDot2;
        LightCullShape shape = LightCullShape.Of(spot);
        Assert.True(shape.Cullable);
        Assert.False(shape.HasCone);

        // Facing the light, off to the side: only the cone could cull it.
        SampleGroup group = Group(new Vec3(500, 0, 50), new Vec3(-1, 0, 0), spacing: 4);
        Assert.False(Culled(spot, group));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.1f)]
    [InlineData(5.0f)]
    public void ASpotWithAnUnusableAxisIsNotConeTested(float length)
    {
        DirectLight spot = Spot(new Vec3(0, 0, 100), new Vec3(0, 0, length), coneDegrees: 30);
        Assert.False(LightCullShape.Of(spot).HasCone);
        Assert.False(Culled(spot, Group(new Vec3(0, 0, 0), new Vec3(0, 0, 1), spacing: 4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverySamplePastTheHardFadeIsCulled(bool stock)
    {
        DirectLight point = Point(new Vec3(0, 0, 0));
        point.StartFadeDistance = 100;
        point.EndFadeDistance = 200;
        SampleGroup far = Group(new Vec3(300, 0, 0), new Vec3(-1, 0, 0), spacing: 4);
        Assert.True(Culled(point, far));
        (int rays, GatherOutput output) = Uncut(Gatherer(point, stock), far, [true, true, true, true]);
        AssertContributesNothing(point, rays, output, 1);

        // Straddling the fade distance: not culled.
        Assert.False(Culled(point, Group(new Vec3(199, 0, 0), new Vec3(-1, 0, 0), spacing: 4)));

        // No hard fade (end not beyond start), or an infinite one: never by distance.
        point.EndFadeDistance = 50;
        Assert.False(Culled(point, far));
        point.EndFadeDistance = float.PositiveInfinity;
        Assert.True(double.IsPositiveInfinity(LightCullShape.Of(point).FadeCull));
        Assert.False(Culled(point, far));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverySampleBehindASurfaceLightIsCulled(bool stock)
    {
        // Behind the emitter, the samples facing it: zero falloff, dots finite.
        DirectLight surface = Surface(new Vec3(0, 0, 100), new Vec3(0, 0, 1));
        SampleGroup below = Group(new Vec3(0, 0, 0), new Vec3(0, 0, 1), spacing: 4, bumped: true);
        Assert.True(Culled(surface, below));
        (int rays, GatherOutput output) = Uncut(Gatherer(surface, stock), below, [true, true, true, true]);
        AssertContributesNothing(surface, rays, output, BumpBasis.LightmapCount);

        // In front of it: lit.
        surface.Normal = new Vec3(0, 0, -1);
        Assert.False(Culled(surface, below));
    }

    [Fact]
    public void TheEmitterPlaneTestNeedsEveryNormalBounded()
    {
        // A bump normal so long its dot could overflow: zero times it might
        // not be zero, so the plane test stands aside.
        DirectLight surface = Surface(new Vec3(0, 0, 100), new Vec3(0, 0, 1));
        SampleGroup below = Group(new Vec3(0, 0, 0), new Vec3(0, 0, 1), spacing: 4, bumped: true);
        below.Normal(2, 1) = new Vec3(0, 0, 1e20f);
        Assert.False(SampleBounds.Of(below).NormalsBounded);
        Assert.True(SampleBounds.Of(below).Valid);
        Assert.False(Culled(surface, below));
    }

    [Theory]
    [InlineData(EmitType.Point, false)]
    [InlineData(EmitType.Point, true)]
    [InlineData(EmitType.Spotlight, false)]
    [InlineData(EmitType.Spotlight, true)]
    [InlineData(EmitType.Surface, false)]
    [InlineData(EmitType.Surface, true)]
    public void EverySampleFacingAwayIsCulled(EmitType type, bool stock)
    {
        // The light is above; every normal points down.
        DirectLight light = type switch
        {
            EmitType.Point => Point(new Vec3(0, 0, 100)),
            EmitType.Spotlight => Spot(new Vec3(0, 0, 100), new Vec3(0, 0, -1), coneDegrees: 60),
            _ => Surface(new Vec3(0, 0, 100), new Vec3(0, 0, -1)),
        };
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4, bumped: true);

        // Bump normals facing the light still add nothing: the resolve zeroes
        // them with the flat one.
        for (int lane = 0; lane < 4; lane++)
        {
            group.Normal(1, lane) = new Vec3(0, 0, 1);
        }

        Assert.True(Culled(light, group));
        (int rays, GatherOutput output) = Uncut(Gatherer(light, stock), group, [true, true, true, true]);
        AssertContributesNothing(light, rays, output, BumpBasis.LightmapCount);

        // One lane facing the light: not culled.
        group.Normal(0, 2) = new Vec3(0, 0, 1);
        Assert.False(Culled(light, group));

        // Ignoring normals, the dot is a constant: never culled as behind.
        group.Normal(0, 2) = new Vec3(0, 0, -1);
        Assert.False(Culled(light, group, GatherFlags.IgnoreNormals));
    }

    [Fact]
    public void APointLightWithNoAttenuationIsNotCulledBehindTheNormal()
    {
        // Every coefficient zero: the falloff is 1/0, and a zero dot times it
        // is NaN -- which the face gather would NOT skip. The cull must keep it.
        DirectLight point = Point(new Vec3(0, 0, 100));
        point.ConstantAttn = 0;
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        Assert.False(LightCullShape.Of(point).FalloffFinite);
        Assert.False(Culled(point, group));

        (_, GatherOutput output) = Uncut(Gatherer(point, stock: false), group, [true, true, true, true]);
        Assert.True(float.IsNaN(output.Dot[0] * output.Falloff[0]));
    }

    [Theory]
    [InlineData(-1.0f, 1.0f, 0.0f, 1.0e22f, 0.0f)]
    [InlineData(1.0f, -0.5f, 0.0f, 1.0e22f, 0.0f)]
    [InlineData(2.0e6f, 0.0f, 0.0f, 1.0e22f, 0.0f)]
    [InlineData(1.0e-25f, 0.0f, 0.0f, 1.0e22f, 0.0f)]
    [InlineData(1.0f, 0.0f, 0.0f, 0.5f, 0.0f)]
    [InlineData(1.0f, 0.0f, 0.0f, float.NaN, 0.0f)]
    [InlineData(1.0f, 0.0f, 0.0f, 1.0e22f, -1.0f)]
    [InlineData(1.0f, 0.0f, 0.0f, 1.0e22f, 2.0e6f)]
    [InlineData(1.0f, 0.0f, float.NaN, 1.0e22f, 0.0f)]
    public void AFalloffThatMightNotBeFiniteKeepsASpotBehindTheNormal(
        float constant, float linear, float quadratic, float capDist, float exponent)
    {
        // A negative, huge, NaN or vanishing coefficient; a cap under one
        // unit; a negative or huge exponent: each could make the falloff
        // infinite, and zero times infinity is not zero.
        DirectLight spot = Spot(new Vec3(0, 0, 100), new Vec3(0, 0, -1), coneDegrees: 60);
        spot.ConstantAttn = constant;
        spot.LinearAttn = linear;
        spot.QuadraticAttn = quadratic;
        spot.CapDist = capDist;
        spot.Exponent = exponent;
        Assert.False(LightCullShape.Of(spot).FalloffFinite);
        Assert.False(Culled(spot, Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4)));
    }

    [Fact]
    public void ASurfaceLightBehindTheNormalNeedsNoFiniteFalloff()
    {
        // The scalar lane returns before its falloff when the dot is zero.
        DirectLight surface = Surface(new Vec3(0, 0, 100), new Vec3(0, 0, -1));
        surface.ConstantAttn = 0;
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        Assert.True(Culled(surface, group));
        (int rays, GatherOutput output) = Uncut(Gatherer(surface, stock: true), group, [true, true, true, true]);
        AssertContributesNothing(surface, rays, output, 1);
    }

    [Fact]
    public void ASampleTooCloseToTheLightIsNotCulledBehindTheNormal()
    {
        DirectLight point = Point(new Vec3(0, 0, 0.005f));
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 0);
        Assert.False(Culled(point, group));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(2.0e7f)]
    public void AGroupOutOfRangeIsNeverCulled(float bad)
    {
        DirectLight point = Point(new Vec3(0, 0, 100));
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        Assert.True(Culled(point, group));
        group.Points[1] = new Vec3(0, bad, 0);
        Assert.False(SampleBounds.Of(group).Valid);
        Assert.False(Culled(point, group));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(11.0f)]
    public void AGroupWithAWildFlatNormalIsNeverCulled(float length)
    {
        DirectLight point = Point(new Vec3(0, 0, 100));
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        group.Normal(0, 3) = new Vec3(0, 0, -length);
        Assert.False(SampleBounds.Of(group).Valid);
        Assert.False(Culled(point, group));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AGroupWithABadCountIsNeverCulled(int count)
    {
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        group.Count = count;
        Assert.False(SampleBounds.Of(group).Valid);
        Assert.False(Culled(Point(new Vec3(0, 0, 100)), group));
    }

    [Theory]
    [InlineData(float.NaN, 1, 1)]
    [InlineData(1, float.PositiveInfinity, 1)]
    [InlineData(1, 1, float.NegativeInfinity)]
    public void ALightWithANonFiniteIntensityIsNeverCulled(float x, float y, float z)
    {
        // Zero times it is NaN, not nothing.
        foreach (EmitType type in new[] { EmitType.Point, EmitType.Spotlight, EmitType.Surface })
        {
            DirectLight light = type switch
            {
                EmitType.Point => Point(new Vec3(0, 0, 100)),
                EmitType.Spotlight => Spot(new Vec3(0, 0, 100), new Vec3(0, 0, 1), coneDegrees: 30),
                _ => Surface(new Vec3(0, 0, 100), new Vec3(0, 0, 1)),
            };
            SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
            Assert.True(Culled(light, group));

            light.Intensity = new Vec3(x, y, z);
            Assert.False(LightCullShape.Of(light).Cullable);
            Assert.False(Culled(light, group));
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(2.0e7f)]
    public void ALightOutOfRangeIsNeverCulled(float bad)
    {
        DirectLight point = Point(new Vec3(bad, 0, 100));
        Assert.False(LightCullShape.Of(point).Cullable);
        Assert.False(Culled(point, Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4)));
    }

    [Theory]
    [InlineData(EmitType.SkyLight)]
    [InlineData(EmitType.SkyAmbient)]
    public void SkyLightsAreNeverCulled(EmitType type)
    {
        DirectLight sky = new() { Type = type, Normal = new Vec3(0, 0, -1), Intensity = new Vec3(1, 1, 1) };
        Assert.False(LightCullShape.Of(sky).Cullable);
        Assert.False(Culled(sky, Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4)));
    }

    [Fact]
    public void ALightWithAFaceTracesFromTheWorldOrigin()
    {
        // The gather's source for such a light is the world origin, not its
        // own; the cull must reason about the same point.
        DirectLight point = Point(new Vec3(0, 0, -500));
        point.FaceNum = 3;
        Assert.Equal(new Vec3d(0, 0, 0), LightCullShape.Of(point).Source);

        // Below the world origin and facing down: behind, though the light's
        // own origin is in front.
        SampleGroup group = Group(new Vec3(0, 0, -100), new Vec3(0, 0, -1), spacing: 4);
        Assert.True(Culled(point, group));
        (int rays, GatherOutput output) = Uncut(Gatherer(point, stock: false), group, [true, true, true, true]);
        AssertContributesNothing(point, rays, output, 1);
    }

    [Fact]
    public void KeepDeadLightsTurnsTheCullOff()
    {
        DirectLight point = Point(new Vec3(0, 0, 100));
        SampleGroup group = Group(new Vec3(0, 0, 0), new Vec3(0, 0, -1), spacing: 4);
        SampleBounds bounds = SampleBounds.Of(group);
        Assert.True(Gatherer(point, stock: false).CannotLight(0, group, bounds, GatherFlags.None));
        Assert.False(Gatherer(point, stock: false, keep: true).CannotLight(0, group, bounds, GatherFlags.None));
    }

    [Fact]
    public void TheBoundsContainEveryRealLane()
    {
        Random r = new(99);
        for (int c = 0; c < 2000; c++)
        {
            SampleGroup group = RandomLights.GroupAround(r, Point(Vec3.Zero), r.Next(1, 5), (float)(r.NextDouble() * 500));
            SampleBounds b = SampleBounds.Of(group);
            Assert.True(b.Valid);
            for (int lane = 0; lane < group.Count; lane++)
            {
                Vec3 p = group.Points[lane];
                double dx = p.X - b.Centre.X;
                double dy = p.Y - b.Centre.Y;
                double dz = p.Z - b.Centre.Z;
                Assert.True(Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) <= b.Radius);
            }
        }
    }

    private static DirectLight Point(Vec3 origin) =>
        new() { Type = EmitType.Point, Origin = origin, Intensity = new Vec3(100, 100, 100), ConstantAttn = 1, CapDist = 1.0e22f };

    private static DirectLight Spot(Vec3 origin, Vec3 normal, double coneDegrees) =>
        new()
        {
            Type = EmitType.Spotlight,
            Origin = origin,
            Normal = normal,
            Intensity = new Vec3(100, 100, 100),
            ConstantAttn = 1,
            CapDist = 1.0e22f,
            StopDot2 = (float)Math.Cos(coneDegrees * Math.PI / 180),
            StopDot = (float)Math.Cos(coneDegrees * 0.5 * Math.PI / 180),
            Exponent = 2,
        };

    private static DirectLight Surface(Vec3 origin, Vec3 normal) =>
        new() { Type = EmitType.Surface, Origin = origin, Normal = normal, Intensity = new Vec3(100, 100, 100), ConstantAttn = 1, CapDist = 1.0e22f };

    // Four samples in a square around centre, all with one normal; bumped
    // groups get three more normals tilted towards it.
    private static SampleGroup Group(Vec3 centre, Vec3 normal, float spacing, bool bumped = false)
    {
        SampleGroup group = new() { Count = 4, NormalCount = bumped ? BumpBasis.LightmapCount : 1 };
        for (int lane = 0; lane < 4; lane++)
        {
            group.Points[lane] = centre + new Vec3((lane & 1) * spacing, (lane >> 1) * spacing, 0);
            group.Normal(0, lane) = normal;
            for (int n = 1; n < BumpBasis.LightmapCount; n++)
            {
                group.Normal(n, lane) = (normal + new Vec3(0.3f * n, -0.2f, 0.1f)).Normalise().Normalised;
            }
        }

        return group;
    }
}
