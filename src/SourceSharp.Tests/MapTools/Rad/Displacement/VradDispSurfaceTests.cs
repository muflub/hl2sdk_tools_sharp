using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Displacement;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.Tests.MapTools.Disp;

using Xunit;

using static SourceSharp.Tests.MapTools.Rad.Displacement.DispTestSurfaces;

namespace SourceSharp.Tests.MapTools.Rad.Displacement;

/// <summary>
/// <c>CVRADDispColl</c> (<c>vrad_dispcoll.cpp</c>): creation, the sample
/// radii, and the (u, v) to surface point and normal maps.
/// </summary>
public sealed class VradDispSurfaceTests
{
    [Fact]
    public void CreateCopiesTheCoresVerticesBitForBit()
    {
        CoreDispInfo core = Core(Crease);
        VradDispSurface s = VradDispSurface.Create(core, Tex(), new DirectLightingSettings(), false);
        for (int i = 0; i < core.Size; i++)
        {
            Assert.True(DispFixtures.BitEqual(core.Vert(i), s.Verts[i]), $"vertex {i}");
        }
    }

    [Fact]
    public void CreateCopiesTheCoresVertexNormals()
    {
        CoreDispInfo core = Core(Crease);
        VradDispSurface s = VradDispSurface.Create(core, Tex(), new DirectLightingSettings(), false);
        for (int i = 0; i < core.Size; i++)
        {
            Assert.True(DispFixtures.BitEqual(core.Normal(i), s.VertNormals[i]), $"normal {i}");
        }
    }

    [Fact]
    public void CreateCopiesBumpSetZerosLuxelCoordinates()
    {
        CoreDispInfo core = Core(Crease);
        VradDispSurface s = VradDispSurface.Create(core, Tex(), new DirectLightingSettings(), false);
        for (int i = 0; i < core.Size; i++)
        {
            Assert.Equal(core.LuxelCoord(0, i), s.LuxelCoords[i]);
        }
    }

    [Fact]
    public void TheParentFaceIsTheSurfaceHandle()
    {
        CoreDispInfo core = Core();
        core.Surface.Handle = 37;
        Assert.Equal(37, VradDispSurface.Create(core, Tex(), new DirectLightingSettings(), false).ParentFace);
    }

    [Fact]
    public void TheSampleRadiusIsTwoPointTwoLuxelDiagonals()
    {
        // vrad_dispcoll.cpp:104: sqrt(w^2 + h^2) * 2.2 with w = h = 16.
        (float width, float r2, _, _) = VradDispSurface.SampleRadii(Tex(16), new DirectLightingSettings());
        float r = (float)Math.Sqrt(16f * 16f * 2f) * 2.2f;
        Assert.Equal(16.0f, width);
        Assert.Equal(r * r, r2);
    }

    [Fact]
    public void TheSampleRadiusIsClampedToMaxDispSampleSize()
    {
        (_, float r2, _, _) = VradDispSurface.SampleRadii(Tex(16), new DirectLightingSettings { MaxDispSampleSize = 10 });
        Assert.Equal(100.0f, r2);
    }

    [Fact]
    public void ThePatchRadiusIsLuxelSizeTimesDispChopTimesTwoPointTwo()
    {
        (_, _, float pr2, bool clamped) = VradDispSurface.SampleRadii(Tex(16), new DirectLightingSettings());
        float r = 16.0f * 8.0f * 2.2f;
        Assert.Equal(r * r, pr2);
        Assert.False(clamped);
    }

    [Fact]
    public void ThePatchRadiusIsClampedAndFlagged()
    {
        (_, _, float pr2, bool clamped) = VradDispSurface.SampleRadii(
            Tex(16), new DirectLightingSettings { MaxDispPatchRadius = 100 });
        Assert.Equal(10000.0f, pr2);
        Assert.True(clamped);
    }

    [Fact]
    public void UvZeroIsTheFirstVertex()
    {
        VradDispSurface s = Surface(Crease);
        Vec3 p = default;
        Assert.True(s.DispUVToSurfPoint(0, 0, 0, ref p));
        Assert.Equal(s.Verts[0], p);
    }

    [Fact]
    public void UvOneLandsWithinAMillionthOfTheLastVertex()
    {
        // The (width - 1.000001) scale keeps u = 1 inside the last cell.
        VradDispSurface s = Surface(Crease);
        Vec3 p = default;
        Assert.True(s.DispUVToSurfPoint(1, 1, 0, ref p));
        Assert.True(Near(p, s.Verts[s.Size - 1], 1e-3f), p.ToString());
    }

    [Fact]
    public void AUvPastTheEdgeLeavesThePointUntouched()
    {
        VradDispSurface s = Surface();
        Vec3 p = new(7, 8, 9);
        Assert.False(s.DispUVToSurfPoint(1.0001f, 0.5f, 1.0f, ref p));
        Assert.Equal(new Vec3(7, 8, 9), p);
    }

    [Fact]
    public void AUvPastTheEdgeLeavesTheNormalUntouched()
    {
        VradDispSurface s = Surface();
        Vec3 n = new(7, 8, 9);
        Assert.False(s.DispUVToSurfNormal(0.5f, -0.0001f, ref n));
        Assert.Equal(new Vec3(7, 8, 9), n);
    }

    [Fact]
    public void ThePushMovesThePointOneUnitAlongTheTriangleNormal()
    {
        VradDispSurface s = Surface();
        Vec3 p = default;
        s.DispUVToSurfPoint(0.3f, 0.6f, 1.0f, ref p);
        Assert.Equal(1.0f, p.Z, 5);
    }

    [Fact]
    public void AFlatSurfacesBlendedNormalIsUp()
    {
        VradDispSurface s = Surface();
        Vec3 n = default;
        s.DispUVToSurfNormal(0.37f, 0.81f, ref n);
        Assert.True(Near(n, new Vec3(0, 0, 1), 1e-6f), n.ToString());
    }

    [Fact]
    public void AnOddCellsUpperTriangleIgnoresItsFirstCorner()
    {
        // Cell (1, 0) has flattened index 1: odd, so TriTLToBR
        // (vrad_dispcoll.cpp:214). Past the diagonal (fracU + fracV > 1) the
        // point comes from corners (1,1), (2,1), (2,0) only: raising (1,0)
        // must not move it.
        VradDispSurface flat = Surface();
        VradDispSurface raised = Surface((x, y) => x == 1 && y == 0 ? 50.0f : 0.0f);
        float u = UvFor(flat, 1.8f);
        float v = UvFor(flat, 0.8f);
        Vec3 a = default;
        Vec3 b = default;
        flat.DispUVToSurfPoint(u, v, 0, ref a);
        raised.DispUVToSurfPoint(u, v, 0, ref b);
        Assert.Equal(a.Z, b.Z, 5);
    }

    [Fact]
    public void AnEvenCellsLowerTriangleIgnoresItsFarCorner()
    {
        // Cell (0, 0), even: TriBLToTR. With fracU >= fracV the point comes
        // from (0,0), (1,1), (1,0): raising (0,1) must not move it.
        VradDispSurface flat = Surface();
        VradDispSurface raised = Surface((x, y) => x == 0 && y == 1 ? 50.0f : 0.0f);
        float u = UvFor(flat, 0.7f);
        float v = UvFor(flat, 0.2f);
        Vec3 a = default;
        Vec3 b = default;
        flat.DispUVToSurfPoint(u, v, 0, ref a);
        raised.DispUVToSurfPoint(u, v, 0, ref b);
        Assert.Equal(a.Z, b.Z, 5);
    }

    [Fact]
    public void UnderCorrectTheCreaseBlendIsTheNormalisedMeanOfTheUnitNormals()
    {
        // p3f2's hand-over: halfway along the edge from the flank vertex (1,0)
        // to the crease vertex (2,0), with unit vertex normals, the blend is
        // normalise(n1 + n2) (vrad_dispcoll.cpp:361-378).
        VradDispSurface s = Surface(Crease, stockNormalMean: false);
        Vec3 n1 = s.VertNormals[Index(s, 1, 0)];
        Vec3 n2 = s.VertNormals[Index(s, 2, 0)];
        Assert.Equal(1.0f, n2.Length(), 5);

        Vec3 blend = default;
        s.DispUVToSurfNormal(UvFor(s, 1.5f), 0.0f, ref blend);
        Vec3 expected = (n1 + n2).Normalise().Normalised;
        Assert.True(Near(blend, expected, 1e-5f), $"{blend} vs {expected}");
    }

    [Fact]
    public void UnderStockTheCreaseBlendLeansTowardTheFlank()
    {
        // StockQuirk.DispVertexNormalMeanUnnormalised: the crease normal is
        // short, so its half weighs less and the blend leans to the flank.
        VradDispSurface s = Surface(Crease, stockNormalMean: true);
        Vec3 n1 = s.VertNormals[Index(s, 1, 0)];
        Vec3 n2 = s.VertNormals[Index(s, 2, 0)];
        Assert.True(n2.Length() < 0.99f, $"crease normal length {n2.Length()}");

        Vec3 blend = default;
        s.DispUVToSurfNormal(UvFor(s, 1.5f), 0.0f, ref blend);
        Vec3 meanOfUnits = (n1 + n2.Normalise().Normalised).Normalise().Normalised;
        Assert.False(Near(blend, meanOfUnits, 1e-3f), $"{blend} is the unit mean {meanOfUnits}");
        Assert.True(Vec3.Dot(blend, n1) > Vec3.Dot(meanOfUnits, n1), "the stock blend should lean toward the flank normal");
    }
}
