//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomWaterHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Water on the level map: a water surface is not ground a player stands
/// on, and a floor under water is still floor; water through a joined door
/// links to the flattened compile's map at every turn.
/// </summary>
public sealed class LevelMapWaterTests
{
    /// <summary>
    /// The hub's pool: no polygon at the water's surface (z 56), and the
    /// floor under the water (z 16) is floor like the rest of the room's.
    /// </summary>
    [Fact]
    public async Task AWaterSurfaceIsNotFloorAndTheFloorUnderItIs()
    {
        RoomLibrary rooms = await CompileAsync(PoolLibrary());
        RoomMapView view = rooms.Get("hub").MapViewOfCompile!;
        Assert.DoesNotContain(view.Polygons, p => p.ZLow <= 56 && p.ZHigh >= 56);
        MapPolygon floor = Assert.Single(view.Polygons, p => (p.ZLow, p.ZHigh) == (16, 16));

        // The pool's centre is inside the floor's outer ring and in none of its holes.
        MapPoint centre = new(72, 72);
        Assert.True(Inside(floor.Outer, centre));
        Assert.DoesNotContain(floor.Holes, hole => Inside(hole, centre));
    }

    /// <summary>The levels of the hub and the other room joined through their water doors, at each turn.</summary>
    public static TheoryData<int> Turns => [0, 1, 2, 3];

    /// <summary>
    /// Water through a joined door: the linked level map is the flattened
    /// compile's, byte for byte, at every turn; the doorway's floor, under
    /// water, is the door's on both sides, and its water surface is on
    /// neither.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task WaterThroughADoorMapsAsItFlattens(int turns)
    {
        VmfDocument library = SocketLibrary();
        RoomLibrary rooms = await CompileSocketsAsync(library);
        string[] grid = turns switch
        {
            0 => ["hub, other"],
            1 => ["other@90", "hub@90"],
            2 => ["other@180, hub@180"],
            _ => ["hub@270", "other@270"],
        };
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", grid), "wet");
        LevelLayout layout = level.ToLayout(name => rooms.Find(name)?.Definition, rooms.CellSize, rooms.Kit);
        Map2dLevel linked = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get).Build(1);
        BspData flat = await CompileFlatAsync(library, level);
        Assert.Equal(Map2dWriter.Write(linked), Map2dWriter.Write(LevelMapBuilder.FromCompile(flat, 1, level, [library])));
        Assert.DoesNotContain(linked.Rings, r => r.ZLow <= SocketLevel && r.ZHigh >= SocketLevel);
        Assert.Contains(linked.Doors, d => d.Open);
    }

    private static bool Inside(IReadOnlyList<MapPoint> ring, MapPoint p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y)
                && p.X < ring[j].X + ((double)(ring[i].X - ring[j].X) * (p.Y - ring[j].Y) / (ring[i].Y - ring[j].Y)))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
