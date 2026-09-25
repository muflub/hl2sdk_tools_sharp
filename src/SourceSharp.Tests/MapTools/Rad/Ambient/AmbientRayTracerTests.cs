using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Rad.Ambient;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Ambient;

/// <summary>
/// <c>CLightSurface::FindIntersection</c> with the displacement clip,
/// <c>CastRayInLeaf</c>.
/// <c>TestLine</c> and <c>CLeafSampler</c>, on the committed fixture room:
/// 768 x 768 x 384, displacement floor 8..~32 up, lit ceiling over x &lt; 0,
/// sky ceiling over x &gt; 0, texlight panel at x -256..-128, z 376..384.
/// </summary>
public sealed class AmbientRayTracerTests : IClassFixture<AmbientFixture>
{
    private readonly AmbientFixture _fixture;

    /// <summary>Takes the shared fixture.</summary>
    /// <param name="fixture">The loaded map.</param>
    public AmbientRayTracerTests(AmbientFixture fixture) => _fixture = fixture;

    private AmbientHit Trace(Vec3 start, Vec3 delta) =>
        _fixture.Ldr.Tracer.Trace(start, delta, _fixture.Ldr.Tracer.Displacements.CreateScratch());

    [Fact]
    public void ARayDownHitsTheDisplacementsBaseFace()
    {
        AmbientHit hit = Trace(new Vec3(-300, 300, 200), new Vec3(0, 0, -400));

        Assert.NotEqual(-1, _fixture.Ldr.Faces[hit.Surface].DispInfo);
    }

    [Fact]
    public void ADisplacementHitHasALuxel()
    {
        AmbientHit hit = Trace(new Vec3(-300, 300, 200), new Vec3(0, 0, -400));

        Assert.True(hit.HasLuxel);
    }

    [Fact]
    public void ARayUpThroughTheSkyHalfFindsASkyFaceWithNoLuxel()
    {
        //: a sky face is taken on a node, m_HitFrac
        // stays at 1 and m_bHasLuxel stays false.
        AmbientHit hit = Trace(new Vec3(300, 300, 200), new Vec3(0, 0, 400));

        Assert.Equal(
            (true, false, 1.0f),
            ((_fixture.Ldr.TexInfo[_fixture.Ldr.Faces[hit.Surface].TexInfo].Flags & RayAmbientLighting.SurfSky) != 0,
             hit.HasLuxel,
             hit.Fraction));
    }

    [Fact]
    public void ARayUpUnderTheLitHalfHitsTheCeilingAtItsHeight()
    {
        // From z = 200 to z = 600: the ceiling underside is z = 384, 184/400 along.
        AmbientHit hit = Trace(new Vec3(-300, 300, 200), new Vec3(0, 0, 400));

        Assert.Equal(0.46f, hit.Fraction, 4);
    }

    [Fact]
    public void ARayUpIntoThePanelHitsThePanelFirst()
    {
        // The panel's underside is z = 376 at x -256..-128.
        AmbientHit hit = Trace(new Vec3(-200, 0, 200), new Vec3(0, 0, 400));

        Assert.Equal(0.44f, hit.Fraction, 4);
    }

    [Fact]
    public void TestLineIsBlockedByAWall()
    {
        float[] f = new float[1];
        _fixture.Visibility.FractionsVisible(new Vec3(0, 0, 200), [new Vec3(1000, 0, 200)], f);

        Assert.Equal(0f, f[0]);
    }

    [Fact]
    public void TestLineIsClearAcrossTheRoom()
    {
        float[] f = new float[1];
        _fixture.Visibility.FractionsVisible(new Vec3(-300, 300, 200), [new Vec3(-300, -300, 200)], f);

        Assert.Equal(1f, f[0]);
    }

    [Fact]
    public void TestLineIsBlockedByTheDetailPillar()
    {
        // The func_detail pillar spans x 96..160, y -160..-96, z 48..320.
        float[] f = new float[1];
        _fixture.Visibility.FractionsVisible(new Vec3(128, -300, 200), [new Vec3(128, 300, 200)], f);

        Assert.Equal(0f, f[0]);
    }

    [Fact]
    public void EverySamplePositionIsInsideItsLeaf()
    {
        // GenerateLeafSamplePosition's plane test: DIST_EPSILON inside every
        // boundary plane, or the bounding-box centre fallback.
        AmbientScene scene = _fixture.Ldr;
        List<LeafPlane> planes = [];
        int inside = 0;
        for (int leaf = 0; leaf < scene.Leaves.Length; leaf++)
        {
            if ((scene.Leaves[leaf].Contents & 1) != 0)
            {
                continue;
            }

            LeafBoundaryPlanes.Gather(leaf, scene.Nodes, scene.Planes, scene.Parents, planes);
            LeafSampler sampler = new(scene, scene.Tracer.Displacements.CreateScratch());
            Vec3 p = sampler.Generate(leaf, planes);
            if (planes.TrueForAll(pl => Vec3.Dot(pl.Normal, p) - pl.Dist >= LeafSampler.DistEpsilon))
            {
                inside++;
            }
        }

        Assert.True(inside > 0, "no leaf produced an inside sample");
    }

    [Fact]
    public void TheSamplerIsAPureFunctionOfTheLeaf()
    {
        // CLeafSampler is a local seeded zero per leaf:528): two samplers
        // draw the same positions.
        AmbientScene scene = _fixture.Ldr;
        int leaf = Enumerable.Range(0, scene.Leaves.Length).First(l => (scene.Leaves[l].Contents & 1) == 0);
        List<LeafPlane> planes = [];
        LeafBoundaryPlanes.Gather(leaf, scene.Nodes, scene.Planes, scene.Parents, planes);

        Vec3 a = new LeafSampler(scene, scene.Tracer.Displacements.CreateScratch()).Generate(leaf, planes);
        Vec3 b = new LeafSampler(scene, scene.Tracer.Displacements.CreateScratch()).Generate(leaf, planes);

        Assert.Equal(a, b);
    }

    [Fact]
    public void AVersionZeroLeafLumpReadsAsTheSameLeaves()
    {
        // dleaf_version_0_t carries a 24-byte ambient cube after the version-1
        // fields; ReadLeaves drops it and keeps everything else.
        DLeaf[] v1 = AmbientScene.ReadLeaves(_fixture.Bsp);
        DLeafVersion0[] v0 = new DLeafVersion0[v1.Length];
        for (int i = 0; i < v1.Length; i++)
        {
            v0[i].Contents = v1[i].Contents;
            v0[i].Cluster = v1[i].Cluster;
            v0[i].AreaFlags = v1[i].AreaFlags;
            v0[i].Mins = v1[i].Mins;
            v0[i].Maxs = v1[i].Maxs;
            v0[i].FirstLeafFace = v1[i].FirstLeafFace;
            v0[i].NumLeafFaces = v1[i].NumLeafFaces;
            v0[i].FirstLeafBrush = v1[i].FirstLeafBrush;
            v0[i].NumLeafBrushes = v1[i].NumLeafBrushes;
            v0[i].LeafWaterDataId = v1[i].LeafWaterDataId;
        }

        BspData copy = new();
        copy.SetLump(BspLump.Leafs, System.Runtime.InteropServices.MemoryMarshal.AsBytes<DLeafVersion0>(v0).ToArray(), 0);

        Assert.Equal(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes<DLeaf>(v1).ToArray(),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes<DLeaf>(AmbientScene.ReadLeaves(copy)).ToArray());
    }
}
