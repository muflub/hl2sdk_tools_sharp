//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <see cref="StockQuirk.BoxBevelWindingBounds"/>: whether a box bevel is
/// placed at the brush's winding bounds or at the corner its planes make.
/// </summary>
/// <remarks>
/// <para>
/// The shape is a wedge: a slab 256 wide and <c>h</c> high at one end,
/// sloping down to a thin edge at y = 1000 over a length <c>L</c>. It has no
/// face looking along +y, so the loader gives it a box bevel there, at
/// <c>maxs.y</c>. The thin edge is where the sloping top meets the flat
/// bottom, at a glancing angle, and a winding vertex on it carries the
/// clipping's rounding divided by the sine of that angle: with L = 384 and
/// h = 16, the windings put the edge at 1000.03125; with h = 4, at 1000.125.
/// All of it is exact IEEE arithmetic under the Correct policy, so the
/// numbers are the same on every CPU.
/// </para>
/// <para>
/// The plane table snaps a distance within 0.01 of an integer onto it, so
/// 1000.03125 is a plane of its own, where the edge's planes meet at 1000
/// exactly.
/// </para>
/// </remarks>
public class BoxBevelWindingBoundsTests
{
    /// <summary>
    /// <b>Stock's side of the quirk.</b> The bevel is at the winding bound,
    /// 1000.03125, past the table's snap.
    /// </summary>
    [Fact]
    public async Task StockSidePutsTheWedgesBevelWhereItsWindingsEnd()
    {
        MapFile map = await LoadAsync(StockSide, height: 16, neighbour: false);
        MapBrush wedge = map.Brushes[0];

        Plane bevel = PlusYBevel(map, wedge);
        Assert.Equal(wedge.Maxs.Y, bevel.Dist);
        Assert.True(bevel.Dist - 1000f > 0.01f, $"the winding bound {bevel.Dist} should be past the snap");
    }

    /// <summary>
    /// <b>Correct's side.</b> The bevel is at 1000, where the top, bottom and
    /// end planes meet; the brush's bounds are unchanged.
    /// </summary>
    [Fact]
    public async Task CorrectPutsTheWedgesBevelWhereItsPlanesMeet()
    {
        MapFile map = await LoadAsync(ComplianceOptions.Correct, height: 16, neighbour: false);
        MapBrush wedge = map.Brushes[0];

        Assert.Equal(1000f, PlusYBevel(map, wedge).Dist);
        Assert.True(wedge.Maxs.Y - 1000f > 0.01f);
    }

    /// <summary>
    /// What the difference does: a box beyond the thin edge has its -y face
    /// on y = 1000. Under Correct the wedge's bevel is the other half of that
    /// face's plane pair, so the split heuristic counts the wedge as facing
    /// that plane, as it does the box.
    /// </summary>
    [Fact]
    public async Task UnderCorrectTheBevelSharesItsNeighboursFacePlane()
    {
        MapFile map = await LoadAsync(ComplianceOptions.Correct, height: 16, neighbour: true);

        Assert.Equal(NeighbourFace(map) & ~1, PlusYBevelNumber(map, map.Brushes[0]) & ~1);
    }

    /// <summary>
    /// On stock's side the bevel is a plane of its own, 0.03 from the face,
    /// and the wedge does not face the plane the box does.
    /// </summary>
    [Fact]
    public async Task OnTheStockSideTheBevelIsAPlaneOfItsOwn()
    {
        MapFile map = await LoadAsync(StockSide, height: 16, neighbour: true);

        Assert.NotEqual(NeighbourFace(map) & ~1, PlusYBevelNumber(map, map.Brushes[0]) & ~1);
    }

    /// <summary>
    /// At h = 4 the winding vertex is an eighth of a unit along the edge from
    /// its corner, further than the 0.1 incidence band, yet it is under a
    /// thousandth from each of its planes, so Correct still finds the corner.
    /// </summary>
    [Fact]
    public async Task AVertexATenthOfAUnitAlongItsEdgeStillFindsItsCorner()
    {
        MapFile stock = await LoadAsync(StockSide, height: 4, neighbour: false);
        Assert.True(PlusYBevel(stock, stock.Brushes[0]).Dist - 1000f > 0.1f);

        MapFile correct = await LoadAsync(ComplianceOptions.Correct, height: 4, neighbour: false);
        Assert.Equal(1000f, PlusYBevel(correct, correct.Brushes[0]).Dist);
    }

    /// <summary>
    /// A brush whose every side clipped away has no corner to solve, and
    /// Correct leaves its box bevels where stock puts them, at the bounds
    /// the empty windings left.
    /// </summary>
    [Fact]
    public async Task ABrushWithNoWindingsKeepsStocksBevels()
    {
        MapFile correct = await LoadAsync(ComplianceOptions.Correct, height: 16, neighbour: false, inside: true);
        MapFile stock = await LoadAsync(StockSide, height: 16, neighbour: false, inside: true);

        List<Plane> bevels = BoxBevels(correct, correct.Brushes[0]);
        Assert.NotEmpty(bevels);
        Assert.Equal(BoxBevels(stock, stock.Brushes[0]), bevels);

        for (int i = 0; i < correct.Brushes[0].SideCount; i++)
        {
            Assert.True(correct.BrushSides[correct.Brushes[0].FirstSide + i].Winding.IsNull);
        }
    }

    /// <summary>Three axial planes meet at their corner, exactly.</summary>
    [Fact]
    public void ThreeAxialPlanesMeetAtTheirCorner()
    {
        Plane[] planes = [new(new Vec3(1, 0, 0), 1), new(new Vec3(0, 1, 0), 2), new(new Vec3(0, 0, 1), 3)];

        Assert.True(MapFile.NearestCorner(planes, new Vec3(1.01f, 1.99f, 3.002f), out double x, out double y, out double z));
        Assert.Equal((1d, 2d, 3d), (x, y, z));
    }

    /// <summary>Two planes meet in a line, not a corner.</summary>
    [Fact]
    public void FewerThanThreePlanesHaveNoCorner()
    {
        Plane[] planes = [new(new Vec3(1, 0, 0), 1), new(new Vec3(0, 1, 0), 2)];

        Assert.False(MapFile.NearestCorner(planes, new Vec3(1, 2, 3), out _, out _, out _));
    }

    /// <summary>
    /// Three vertical planes share the z direction, so their normals are
    /// dependent and they meet in no single point.
    /// </summary>
    [Fact]
    public void ThreePlanesThatShareADirectionHaveNoCorner()
    {
        Plane[] planes =
        [
            new(new Vec3(1, 0, 0), 1),
            new(new Vec3(0, 1, 0), 2),
            new(new Vec3(0.6f, 0.8f, 0), 2.2f),
        ];

        Assert.False(MapFile.NearestCorner(planes, new Vec3(1, 2, 3), out _, out _, out _));
    }

    /// <summary>
    /// Four planes, two of them a hair apart: two corners, and the one
    /// nearest the vertex is chosen, whichever order the planes come in.
    /// </summary>
    [Fact]
    public void TheNearestOfSeveralCornersWins()
    {
        Plane[] planes =
        [
            new(new Vec3(1, 0, 0), 1),
            new(new Vec3(0, 1, 0), 2),
            new(new Vec3(0, 0, 1), 3),
            new(new Vec3(0, 0, 1), 3.05f),
        ];

        Assert.True(MapFile.NearestCorner(planes, new Vec3(1, 2, 3.04f), out _, out _, out double high));
        Assert.Equal(3.05f, (float)high);

        Assert.True(MapFile.NearestCorner([.. planes.Reverse()], new Vec3(1, 2, 3.01f), out _, out _, out double low));
        Assert.Equal(3d, low);
    }

    private static ComplianceOptions StockSide =>
        ComplianceOptions.Correct.Flipping(StockQuirk.BoxBevelWindingBounds);

    private static Plane PlusYBevel(MapFile map, MapBrush brush) =>
        map.Planes[PlusYBevelNumber(map, brush)];

    private static int PlusYBevelNumber(MapFile map, MapBrush brush)
    {
        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[brush.FirstSide + i];
            if (side.Bevel && map.Planes[side.PlaneNumber].Normal == new Vec3(0, 1, 0))
            {
                return side.PlaneNumber;
            }
        }

        Assert.Fail("the wedge should have a +y box bevel");
        return -1;
    }

    private static List<Plane> BoxBevels(MapFile map, MapBrush brush)
    {
        List<Plane> bevels = [];
        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[brush.FirstSide + i];
            if (side.Bevel && map.Planes[side.PlaneNumber].Type < PlaneType.AnyX)
            {
                bevels.Add(map.Planes[side.PlaneNumber]);
            }
        }

        return bevels;
    }

    /// <summary>The neighbour box's -y face.</summary>
    private static int NeighbourFace(MapFile map)
    {
        MapBrush box = map.Brushes[1];
        for (int i = 0; i < box.SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[box.FirstSide + i];
            Plane plane = map.Planes[side.PlaneNumber];
            if (!side.Bevel && plane.Normal.Y == -1f)
            {
                Assert.Equal(-1000f, plane.Dist);
                return side.PlaneNumber;
            }
        }

        Assert.Fail("the box should have a -y face");
        return -1;
    }

    /// <summary>
    /// The wedge from x = 872 to 1128, sloping from height <paramref name="height"/>
    /// at y = 616 to its thin edge at y = 1000, and, when asked, a box beyond
    /// the edge from y = 1000 to 1100. With <paramref name="inside"/>, every
    /// face is wound the other way, so every normal faces in and every
    /// winding clips away.
    /// </summary>
    private static async Task<MapFile> LoadAsync(
        ComplianceOptions compliance, float height, bool neighbour, bool inside = false)
    {
        const float x0 = 872;
        const float x1 = 1128;
        const float y0 = 616;
        const float y1 = 1000;

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");

        VmfChunk wedge = new(MapFileLoader.SolidChunk);
        wedge.AddKey("id", "2");

        // Sloping top, bottom, +x, -x, -y: each wound as UnitMap.Box winds the
        // face it stands in for, so every normal faces out. The +x and -x
        // points need only lie on those planes.
        (float, float, float)[][] faces =
        [
            [(x0, y1, 0), (x1, y1, 0), (x1, y0, height)],
            [(x0, y0, 0), (x1, y0, 0), (x1, y1, 0)],
            [(x1, y1, height), (x1, y1, 0), (x1, y0, 0)],
            [(x0, y1, 0), (x0, y1, height), (x0, y0, height)],
            [(x0, y0, height), (x1, y0, height), (x1, y0, 0)],
        ];

        foreach ((float, float, float)[] face in faces)
        {
            if (inside)
            {
                UnitMap.Add(wedge, UnitMap.Plain, face[2], face[1], face[0]);
            }
            else
            {
                UnitMap.Add(wedge, UnitMap.Plain, face[0], face[1], face[2]);
            }
        }

        world.Children.Add(wedge);

        if (neighbour)
        {
            world.Children.Add(UnitMap.Box(UnitMap.Plain, (x0, y1, 0), (x1, y1 + 100, 16), 3));
        }

        document.Chunks.Add(world);

        (_, MapFile map) = await CsgFixture.LoadAsync(document, new() { Compliance = compliance });
        Assert.Equal(neighbour ? 2 : 1, map.BrushCount);
        return map;
    }
}
