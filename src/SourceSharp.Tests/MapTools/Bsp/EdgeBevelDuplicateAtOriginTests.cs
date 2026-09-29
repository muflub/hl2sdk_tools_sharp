//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <see cref="StockQuirk.EdgeBevelDuplicateAtOrigin"/>: whether the edge-bevel
/// pass gives a slanted face far from the origin a bevel on top of itself.
/// </summary>
/// <remarks>
/// <para>
/// The shape is a wall slab: 300 units long, 16 thick and 16 high, turned 30
/// degrees about z, with its plane points written to three decimals as
/// Hammer writes an off-grid brush. A slab needs no edge bevels at all: its
/// slanted edges are horizontal, and the planes through them along the three
/// axes are its own top, bottom and side faces. Every candidate the pass
/// tries is therefore a duplicate of one of the slab's faces, and the only
/// question is whether the pass sees that.
/// </para>
/// <para>
/// A candidate's normal comes from a winding edge, the face's from three map
/// points, and they differ by a few millionths. Stock compares the two
/// planes' distances at the origin, so at the origin the slab gets no bevel
/// and 3000 units out it gets several.
/// </para>
/// </remarks>
public class EdgeBevelDuplicateAtOriginTests
{
    /// <summary>
    /// <b>Stock's side of the quirk.</b> 3000 units out, the slab gets edge
    /// bevels, and each of them lies on one of the slab's own faces (facing
    /// the same way, every vertex of the face within 0.1 of it) while being
    /// 0.01 or more from that face at the origin, which is why stock's test
    /// let it through.
    /// </summary>
    [Fact]
    public async Task StockSideGivesAFarSlantedSlabBevelsOnItsOwnFaces()
    {
        MapFile map = await LoadAsync(
            ComplianceOptions.Correct.Flipping(StockQuirk.EdgeBevelDuplicateAtOrigin), FarAway);

        Assert.True(map.EdgeBevels > 0, "the far slab should get duplicate bevels on stock's side");

        List<(Plane Bevel, Plane Face)> duplicates = Duplicates(map);
        Assert.Equal(map.EdgeBevels, duplicates.Count);
        Assert.All(duplicates, d => Assert.True(
            MathF.Abs(d.Bevel.Dist - d.Face.Dist) >= 0.01f,
            $"bevel {d.Bevel} and face {d.Face} should be 0.01 or more apart at the origin"));
    }

    /// <summary>
    /// <b>Correct's side.</b> The same slab gets no edge bevel: every
    /// candidate is found lying on a face where the face is.
    /// </summary>
    [Fact]
    public async Task CorrectGivesTheFarSlabNoBevelOnItsOwnFaces()
    {
        MapFile map = await LoadAsync(ComplianceOptions.Correct, FarAway);

        Assert.Equal(0, map.EdgeBevels);
        Assert.Empty(Duplicates(map));
    }

    /// <summary>
    /// The lever arm is the defect: centred on the origin, the same slab gets
    /// no edge bevel under either side of the quirk, because there the
    /// distances stock compares are the distances where the faces are.
    /// </summary>
    [Fact]
    public async Task NearTheOriginStocksOwnTestAlreadyCatchesThem()
    {
        Assert.Equal(0, (await LoadAsync(ComplianceOptions.Correct, AtTheOrigin)).EdgeBevels);
        Assert.Equal(0, (await LoadAsync(
            ComplianceOptions.Correct.Flipping(StockQuirk.EdgeBevelDuplicateAtOrigin), AtTheOrigin)).EdgeBevels);
    }

    /// <summary>
    /// A real edge bevel is still added. The octahedron's twelve edges each
    /// need one, no face of it is near any of them, and both policies add all
    /// twelve.
    /// </summary>
    [Fact]
    public async Task BothPoliciesKeepTheOctahedronsTwelveBevels()
    {
        foreach (ComplianceOptions policy in new[]
            {
                ComplianceOptions.Correct,
                ComplianceOptions.Correct.Flipping(StockQuirk.EdgeBevelDuplicateAtOrigin),
            })
        {
            (_, MapFile map) = await CsgFixture.LoadAsync(
                EdgeBevelShapes.Document(onTheSeam: true), new() { Compliance = policy });
            Assert.Equal(12, map.EdgeBevels);
        }
    }

    /// <summary>The slab 3000 units from the origin, perpendicular to its own length.</summary>
    private static (double X, double Y) FarAway =>
        (3000 * Math.Cos(Angle + (Math.PI / 2)), 3000 * Math.Sin(Angle + (Math.PI / 2)));

    private static (double X, double Y) AtTheOrigin => (0, 0);

    private const double Angle = Math.PI / 6;

    private static async Task<MapFile> LoadAsync(ComplianceOptions compliance, (double X, double Y) centre)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        world.Children.Add(Slab(centre.X, centre.Y));
        document.Chunks.Add(world);

        (_, MapFile map) = await CsgFixture.LoadAsync(document, new() { Compliance = compliance });
        Assert.Equal(1, map.BrushCount);
        return map;
    }

    /// <summary>
    /// Every edge bevel of the map's one brush, each with the face it lies
    /// on, found with the same measure Correct uses; a bevel on no face is
    /// left out, so the count says how many are duplicates.
    /// </summary>
    private static List<(Plane Bevel, Plane Face)> Duplicates(MapFile map)
    {
        MapBrush brush = map.Brushes[0];
        List<(Plane, Plane)> found = [];

        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = map.BrushSides[brush.FirstSide + i];
            Plane bevel = map.Planes[side.PlaneNumber];
            if (!side.Bevel || bevel.Type < PlaneType.AnyX)
            {
                continue;
            }

            for (int j = 0; j < brush.SideCount; j++)
            {
                MapBrushSide face = map.BrushSides[brush.FirstSide + j];
                Plane facePlane = map.Planes[face.PlaneNumber];
                if (face.Bevel
                    || MathF.Abs(facePlane.Normal.X - bevel.Normal.X) >= 0.01f
                    || MathF.Abs(facePlane.Normal.Y - bevel.Normal.Y) >= 0.01f
                    || MathF.Abs(facePlane.Normal.Z - bevel.Normal.Z) >= 0.01f)
                {
                    continue;
                }

                bool onIt = true;
                foreach (Vec3 p in map.Windings.Points(face.Winding))
                {
                    onIt &= MathF.Abs(Vec3.Dot(p, bevel.Normal) - bevel.Dist) < MapFile.BevelOnSideEpsilon;
                }

                if (onIt)
                {
                    found.Add((bevel, facePlane));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The slab, centred on (<paramref name="cx"/>, <paramref name="cy"/>),
    /// from z = -192 to -176, with plane points rounded to three decimals.
    /// </summary>
    private static VmfChunk Slab(double cx, double cy)
    {
        const double length = 300;
        const double thickness = 16;
        const double z0 = -192;
        const double z1 = -176;

        VmfChunk solid = new(MapFileLoader.SolidChunk);
        solid.AddKey("id", "2");

        double ux = Math.Cos(Angle);
        double uy = Math.Sin(Angle);
        double vx = -uy;
        double vy = ux;

        // Counter-clockwise seen from above.
        (double X, double Y)[] c =
        [
            (cx - (ux * length / 2) - (vx * thickness / 2), cy - (uy * length / 2) - (vy * thickness / 2)),
            (cx + (ux * length / 2) - (vx * thickness / 2), cy + (uy * length / 2) - (vy * thickness / 2)),
            (cx + (ux * length / 2) + (vx * thickness / 2), cy + (uy * length / 2) + (vy * thickness / 2)),
            (cx - (ux * length / 2) + (vx * thickness / 2), cy - (uy * length / 2) + (vy * thickness / 2)),
        ];

        // Top, bottom, then each vertical face from its clockwise-next
        // corner, the winding UnitMap.Box uses, so every normal faces out.
        UnitMap.Add(solid, UnitMap.Plain, P(c[3], z1), P(c[2], z1), P(c[1], z1));
        UnitMap.Add(solid, UnitMap.Plain, P(c[0], z0), P(c[1], z0), P(c[2], z0));
        for (int i = 0; i < 4; i++)
        {
            (double X, double Y) a = c[(i + 1) % 4];
            (double X, double Y) b = c[i];
            UnitMap.Add(solid, UnitMap.Plain, P(a, z1), P(a, z0), P(b, z0));
        }

        return solid;
    }

    private static (float X, float Y, float Z) P((double X, double Y) xy, double z) =>
        (Round3(xy.X), Round3(xy.Y), (float)z);

    private static float Round3(double v) =>
        float.Parse(v.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
