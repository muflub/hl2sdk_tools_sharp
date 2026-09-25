using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// <c>ComputeVisibleBrushSides</c>' per-brush <c>GetListOfCutBrushes</c>
/// (<c>detail.cpp:503-529</c>) answered from one laid-out copy of the list
/// (plan 3p). It must name the same brushes in the same order.
/// </summary>
public class CutBrushScanTests
{
    [Fact]
    public void TheScanAgreesWithGetListOfCutBrushesOnRandomBrushes()
    {
        List<BspBrush> brushes = RandomBrushes(new Random(20260922), 300);
        BspBrush head = Link(brushes);
        DetailFaces.CutBrushScan scan = new(head);
        int nonEmpty = 0;

        for (int i = 0; i < brushes.Count; i++)
        {
            List<BspBrush> expected = DetailFaces.GetListOfCutBrushes(brushes[i], head);
            List<BspBrush> actual = scan.CutBrushesOf(i);
            Assert.Equal(expected, actual);
            nonEmpty += expected.Count > 0 ? 1 : 0;
        }

        // the random boxes overlap often enough to exercise the filters
        Assert.InRange(nonEmpty, 50, brushes.Count);
    }

    [Fact]
    public void BoxesThatOnlyTouchStillCut()
    {
        // BrushBoxOverlap's tests are strict (detail.cpp:329-330): a shared
        // face is an overlap.
        List<BspBrush> brushes = [Box(0, 0, 0, 16, 16, 16, 1), Box(16, 0, 0, 32, 16, 16, 1)];
        BspBrush head = Link(brushes);

        Assert.Equal([brushes[1]], new DetailFaces.CutBrushScan(head).CutBrushesOf(0));
    }

    [Fact]
    public void AnOpaqueBrushIsNotCutByATransparentOne()
    {
        List<BspBrush> brushes = [Box(0, 0, 0, 16, 16, 16, 1), Box(8, 0, 0, 24, 16, 16, BrushCsg.TransparentContents)];
        BspBrush head = Link(brushes);

        Assert.Empty(new DetailFaces.CutBrushScan(head).CutBrushesOf(0));
    }

    private static List<BspBrush> RandomBrushes(Random random, int count)
    {
        int[] contents = [1, 1, 1, BrushCsg.TransparentContents, 0x10000 /* CONTENTS_PLAYERCLIP: not visible */, 1 | 0x8000000];
        List<BspBrush> brushes = [];
        for (int i = 0; i < count; i++)
        {
            float x = random.Next(0, 64) * 16f;
            float y = random.Next(0, 64) * 16f;
            float z = random.Next(0, 8) * 16f;
            brushes.Add(Box(x, y, z, x + (random.Next(1, 8) * 16f), y + (random.Next(1, 8) * 16f), z + (random.Next(1, 4) * 16f),
                contents[random.Next(contents.Length)]));
        }

        return brushes;
    }

    private static BspBrush Box(float x0, float y0, float z0, float x1, float y1, float z1, int contents) =>
        new(6)
        {
            Mins = new Vec3(x0, y0, z0),
            Maxs = new Vec3(x1, y1, z1),
            Original = new MapBrush { Contents = contents },
        };

    private static BspBrush Link(List<BspBrush> brushes)
    {
        for (int i = 0; i + 1 < brushes.Count; i++)
        {
            brushes[i].Next = brushes[i + 1];
        }

        return brushes[0];
    }
}
