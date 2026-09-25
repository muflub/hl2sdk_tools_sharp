using System.Buffers.Binary;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// One behaviour per fact for the decompiled IVP building blocks. Each fact cites the SDK 2013
/// <c>vphysics.so</c> function (Ghidra address) it was read from.
/// </summary>
public class IvpBuildingBlockTests
{
    private static IvpPoint<double> H(double x, double y, double z, double w) => new(x, y, z, w);

    [Fact]
    public void AddHalfspaceDropsANewPlaneWhenAParallelOneIsTighter()
    {
        // 0017edf0: dot > 0.9999 and existing.w < new.w -> the new plane is not added.
        var soup = new List<IvpPoint<double>> { H(1, 0, 0, 1) };
        IvpHalfspaceSoup<double, CorrectPrecision>.AddHalfspace(soup, H(1, 0, 0, 2));
        Assert.Single(soup);
        Assert.Equal(1.0, soup[0].W);
    }

    [Fact]
    public void AddHalfspaceReplacesLooserParallelPlanesAndAppendsTheNewOne()
    {
        var soup = new List<IvpPoint<double>> { H(1, 0, 0, 3), H(0, 1, 0, 1) };
        IvpHalfspaceSoup<double, CorrectPrecision>.AddHalfspace(soup, H(1, 0, 0, 2));
        Assert.Equal(2, soup.Count);
        Assert.Equal((0.0, 1.0), (soup[0].X, soup[0].Y));
        Assert.Equal((1.0, 2.0), (soup[1].X, soup[1].W));
    }

    [Fact]
    public void AddHalfspaceKeepsAnEqualParallelPlaneOnlyOnce()
    {
        // existing.w < new.w is strict: an equal plane replaces the old one.
        var soup = new List<IvpPoint<double>> { H(0, 0, 1, 5) };
        IvpHalfspaceSoup<double, CorrectPrecision>.AddHalfspace(soup, H(0, 0, 1, 5));
        Assert.Single(soup);
    }

    [Fact]
    public void ThreeParallelPlanesHaveNoIntersection()
    {
        // 00200a60 via real_invert 00200850: |det| below the build's epsilon fails.
        bool ok = IvpHalfspaceSoup<double, CorrectPrecision>.Intersect(H(1, 0, 0, 1), H(1, 0, 0, 2), H(0, 1, 0, 1), out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void ThreeAxisPlanesMeetAtTheCorner()
    {
        // Inward normals, value n.p + w: the plane x >= 1 is (1,0,0,-1).
        bool ok = IvpHalfspaceSoup<double, CorrectPrecision>.Intersect(H(1, 0, 0, -1), H(0, 1, 0, -2), H(0, 0, 1, -3), out double x, out double y, out double z);
        Assert.True(ok);
        Assert.Equal((1.0, 2.0, 3.0), (x, y, z));
    }

    [Fact]
    public void ACubesHalfspacesHaveEightCorners()
    {
        List<IvpPoint<float>> soup = IvpHalfspaceSoup<float, StockPrecision>.FromHlPlanes(
            [(1, 0, 0, 16), (-1, 0, 0, 16), (0, 1, 0, 16), (0, -1, 0, 16), (0, 0, 1, 16), (0, 0, -1, 16)], 0f, out float merge);
        List<IvpPoint<float>> corners = IvpHalfspaceSoup<float, StockPrecision>.CornerPoints(soup, merge);
        Assert.Equal(8, corners.Count);
        Assert.All(corners, p => Assert.Equal(0.4064f, MathF.Abs(p.X)));
    }

    [Fact]
    public void ThePlaneConversionFlipsToInwardAndSwapsYAndZ()
    {
        // ConvexFromPlanes 0011f8a0 / convert.h ConvertPlaneToIVP(-n, -d): k = (-nx, nz, -ny),
        // hesse = 0.0254f * d.
        List<IvpPoint<float>> soup = IvpHalfspaceSoup<float, StockPrecision>.FromHlPlanes([(0, 1, 0, 10)], 0f, out _);
        Assert.Equal((0f, 0f, -1f), (soup[0].X, soup[0].Y, soup[0].Z));
        Assert.Equal(0.0254f * 10, soup[0].W);
    }

    [Fact]
    public void ThreePointsMakeATwoSidedLedge()
    {
        // 00183d20: the cached unit-triangle ledge with its points replaced.
        IvpCompactLedge? ledge = IvpTriangleLedge<float, StockPrecision>.Build(
            new IvpPoint<float>(0, 0, 0, 0), new IvpPoint<float>(1, 0, 0, 0), new IvpPoint<float>(0, 1, 0, 0));
        Assert.NotNull(ledge);
        Assert.Equal(2, ledge.TriangleCount);
        Assert.Equal(3, ledge.PointCount);
        Assert.Equal((1, 0), (ledge.Opposite(0, 1).Tri, ledge.Opposite(1, 1).Tri));
        Assert.Equal(1u, (ledge.TriangleWord(0) >> 12) & 0xfff);
    }

    [Fact]
    public void ThreeCollinearPointsMakeNoLedge()
    {
        // |cross|^2 < 1e-12 -> null.
        Assert.Null(IvpTriangleLedge<float, StockPrecision>.Build(
            new IvpPoint<float>(0, 0, 0, 0), new IvpPoint<float>(1, 0, 0, 0), new IvpPoint<float>(2, 0, 0, 0)));
    }

    [Fact]
    public void TheTemplateAppendsATwinOfPointZero()
    {
        // 00182f50 treats "found at index 0" as "not found": -0.0 == +0.0, so a twin of point 0
        // is appended again (and never referenced).
        var unique = new List<IvpPoint<float>> { new(0f, 1, 2, 0), new(5, 5, 5, 0), new(-0f, 1, 2, 0) };
        IvpTemplatePolygon<float> t = IvpTemplatePolygon<float>.Build(unique, []);
        Assert.Equal(3, t.Points.Count);
    }

    [Fact]
    public void TheTemplateSkipsATwinOfALaterPoint()
    {
        var unique = new List<IvpPoint<float>> { new(9, 9, 9, 0), new(0f, 1, 2, 0), new(-0f, 1, 2, 0) };
        IvpTemplatePolygon<float> t = IvpTemplatePolygon<float>.Build(unique, []);
        Assert.Equal(2, t.Points.Count);
    }

    [Fact]
    public void CvttMatchesTheX86IntegerIndefinite()
    {
        Assert.Equal(int.MinValue, IvpVector.CvttToInt32(double.NaN));
        Assert.Equal(int.MinValue, IvpVector.CvttToInt32(3e9));
        Assert.Equal(-2, IvpVector.CvttToInt32(-2.9));
        Assert.Equal(251, IvpVector.CvttToInt32(251.99));
    }

    [Fact]
    public void TheJoggleOptionRoundTripsThroughSixSignificantDigits()
    {
        // 00183ed0 sprintf("%G") then qhull's strtod.
        Assert.Equal(1e-12, double.Parse(IvpPointSoup<double, CorrectPrecision>.FormatG(9.999999960041972e-13), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2.4e-12, double.Parse(IvpPointSoup<double, CorrectPrecision>.FormatG(2.3999999e-12), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheVphyHeaderIsTwentyEightBytes()
    {
        byte[] blob = VphyWriter.Serialize(new byte[48], (0.5f, 0.25f, 1f));
        Assert.Equal("VPHY", System.Text.Encoding.ASCII.GetString(blob, 0, 4));
        Assert.Equal(0x100, BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6)));
        Assert.Equal(48, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(16)));
        Assert.Equal(76, blob.Length);
    }

    [Fact]
    public void StockNormizeLeavesAShortVectorAlone()
    {
        // 001ff790: s < 1e-10f -> false, vector unchanged.
        float x = 1e-6f, y = 0, z = 0;
        Assert.False(StockPrecision.NormizeFloatPoint(ref x, ref y, ref z));
        Assert.Equal(1e-6f, x);
    }

    [Fact]
    public void CorrectNormizeAcceptsWhatStockRejects()
    {
        // TF2 00208dc0 compares against 1e-19 in double.
        float x = 1e-6f, y = 0, z = 0;
        Assert.True(CorrectPrecision.NormizeFloatPoint(ref x, ref y, ref z));
        Assert.Equal(1f, x, 6);
    }

    [Fact]
    public void TheBitHackInverseSquareRootIsWithinTenToTheMinusFourteen()
    {
        // TF2's isqrt: four Newton steps from the exponent guess (measured worst 7.8e-15 relative
        // over these inputs); the cooker's TF2 byte-exact goldens pin the exact bits.
        foreach (double s in new[] { 0.25, 2.0, 3.0, 1e-8, 12345.678 })
        {
            double exact = 1.0 / Math.Sqrt(s);
            double rel = Math.Abs(CorrectPrecision.IsqrtDouble(s) - exact) / exact;
            Assert.True(rel < 1e-14, $"{s}: relative error {rel}");
        }
    }

    [Fact]
    public void TheBitHackInverseSquareRootIsNotCorrectlyRounded()
    {
        // Four steps are not enough to converge: for 3 the result is ~2.5e-15 relative off, so the
        // correct cooker must reproduce the steps, not call 1/sqrt.
        Assert.NotEqual(1.0 / Math.Sqrt(3.0), CorrectPrecision.IsqrtDouble(3.0));
    }

    [Fact]
    public void AStockRsqrtIsCloseButNotAlwaysExact()
    {
        // rsqrtss + one Newton step: within about 1e-7 relative, and implementation-defined.
        float f = StockPrecision.RsqrtNewton(2f);
        Assert.InRange(f, 0.70710665f, 0.70710695f);
    }
}
