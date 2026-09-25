using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>ComputeAmbientFromSphericalSamples</c>' projection,
/// <c>AddEmitSurfaceLights</c>, <c>Engine_WorldLightAngle</c> and
/// <c>IsLeafAmbientSurfaceLight</c>.
/// </summary>
public sealed class AmbientCubeTests
{
    private static DWorldLight SurfaceLight(Vec3 origin, Vec3 normal, float intensity, float radius = 0)
    {
        DWorldLight wl = default;
        wl.Origin = origin;
        wl.Normal = normal;
        wl.Intensity = new Vec3(intensity, intensity, intensity);
        wl.Radius = radius;
        wl.Type = (int)EmitType.Surface;
        wl.Flags = (int)WorldLightFlags.InAmbientCube;
        return wl;
    }

    private static Vec3[] AddOne(DWorldLight light, Vec3 start, float visible)
    {
        Vec3[] cube = new Vec3[6];
        AmbientCube.AddEmitSurfaceLights([light], [0], [visible], start, cube, ComplianceOptions.Correct);
        return cube;
    }

    [Fact]
    public void UniformRadianceProjectsToTheSameColourOnEverySide()
    {
        // Each side is a cosine-weighted mean normalised by its own weight.
        Vec3[] rad = [.. Enumerable.Repeat(new Vec3(0.5f, 0.25f, 0.125f), VertexNormals.Count)];
        Vec3[] cube = new Vec3[6];

        AmbientCube.Project(rad, cube);

        Assert.All(cube, c => Assert.Equal(0.5f, c.X, 5));
    }

    [Fact]
    public void LightFromAboveReachesOnlyTheSidesThatFaceIt()
    {
        // Only directions with a positive dot against +z carry light: the -z
        // side sees none of it.
        Vec3[] rad = new Vec3[VertexNormals.Count];
        ReadOnlySpan<Vec3> anorms = VertexNormals.All;
        for (int i = 0; i < rad.Length; i++)
        {
            rad[i] = anorms[i].Z > 0 ? new Vec3(1, 1, 1) : Vec3.Zero;
        }

        Vec3[] cube = new Vec3[6];
        AmbientCube.Project(rad, cube);

        Assert.Equal((1.0f, 0.0f), (MathF.Round(cube[4].X, 5), cube[5].X));
    }

    [Fact]
    public void AnInvisibleBakedLightAddsNothing()
    {
        //:111-113: TestLine found the segment blocked.
        Vec3[] cube = AddOne(SurfaceLight(new Vec3(0, 0, 100), new Vec3(0, 0, -1), 10), Vec3.Zero, 0.0f);

        Assert.All(cube, c => Assert.Equal(Vec3.Zero, c));
    }

    [Fact]
    public void AVisibleBakedLightAddsInverseSquareOnTheFacingSide()
    {
        // A light 10 above, facing down: 10 * (1/100) * (1 * 1) on +z.
        Vec3[] cube = AddOne(SurfaceLight(new Vec3(0, 0, 10), new Vec3(0, 0, -1), 10), Vec3.Zero, 1.0f);

        Assert.Equal((0.1f, 0.0f), (MathF.Round(cube[4].X, 6), cube[5].X));
    }

    [Fact]
    public void ALightFacingAwayAddsNothing()
    {
        // Engine_WorldLightAngle: behind the emitting surface.
        Vec3[] cube = AddOne(SurfaceLight(new Vec3(0, 0, 10), new Vec3(0, 0, 1), 10), Vec3.Zero, 1.0f);

        Assert.All(cube, c => Assert.Equal(Vec3.Zero, c));
    }

    [Fact]
    public void ALightBeyondItsRadiusAddsNothing()
    {
        // Engine_WorldLightDistanceFalloff,:82-86.
        Vec3[] cube = AddOne(SurfaceLight(new Vec3(0, 0, 10), new Vec3(0, 0, -1), 10, radius: 5), Vec3.Zero, 1.0f);

        Assert.All(cube, c => Assert.Equal(Vec3.Zero, c));
    }

    [Fact]
    public void AGrazingLightBelowTheEpsilonIsDropped()
    {
        // dot2 <= ON_EPSILON / 10 (a double 0.01): 0.005 is dropped.
        float angle = AmbientCube.WorldLightAngle(new Vec3(-0.005f, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 0, 0));

        Assert.Equal(0f, angle);
    }

    [Fact]
    public void ADimSurfaceLightGoesInTheCubes()
    {
        //:197: intensity * InvRSquared(0,0,512) < 0.005 -> 1000 * 1/262144 < 0.005.
        DWorldLight wl = SurfaceLight(Vec3.Zero, new Vec3(0, 0, 1), 1000);

        Assert.True(LeafAmbientSurfaceLights.IsAmbientCubeLight(in wl, stockEstimate: false));
    }

    [Fact]
    public void ABrightSurfaceLightStaysALight()
    {
        DWorldLight wl = SurfaceLight(Vec3.Zero, new Vec3(0, 0, 1), 2000);

        Assert.False(LeafAmbientSurfaceLights.IsAmbientCubeLight(in wl, stockEstimate: false));
    }

    [Fact]
    public void AStyledSurfaceLightNeverGoesInTheCubes()
    {
        //:191, wl->style != 0.
        DWorldLight wl = SurfaceLight(Vec3.Zero, new Vec3(0, 0, 1), 1);
        wl.Style = 1;

        Assert.False(LeafAmbientSurfaceLights.IsAmbientCubeLight(in wl, stockEstimate: false));
    }

    [Fact]
    public void OnlySurfaceLightsGoInTheCubes()
    {
        DWorldLight wl = SurfaceLight(Vec3.Zero, new Vec3(0, 0, 1), 1);
        wl.Type = (int)EmitType.Point;

        Assert.False(LeafAmbientSurfaceLights.IsAmbientCubeLight(in wl, stockEstimate: false));
    }
}
