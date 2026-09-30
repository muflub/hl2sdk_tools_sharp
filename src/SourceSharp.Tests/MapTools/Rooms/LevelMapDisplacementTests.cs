//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Map2d;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomDisplacementHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Displacements on the level map: a room's displaced ground is floor by
/// its surface's walkable triangles, its steep parts are not, and a level of
/// rooms with displacements links to the flattened compile's map byte for
/// byte at every turn.
/// </summary>
/// <remarks>
/// The rooms are the displacement harness's: the hub's two neighbouring
/// patches (a few units of rolling ground on a slab whose top is at z 24),
/// and the other room's power 3 patch beside a ridge (<see cref="Ridge"/>)
/// whose flanks are too steep to stand on.
/// </remarks>
public sealed class LevelMapDisplacementTests
{
    /// <summary>The ridge patch's brush, room-local in the other room: its top at z 20.</summary>
    private static readonly Box RidgeBox = new(new Vec3(160, 32, 16), new Vec3(224, 96, 20));

    /// <summary>The ridge's height over its patch: 40 units along the middle column, 1 elsewhere.</summary>
    private static float RidgeHeight(int row, int column) => column == 2 ? 40 : 1;

    /// <summary>The harness patches and the other room's ridge.</summary>
    private static (int Room, VmfChunk Solid)[] Ridge =>
    [
        .. Patches,
        (1, Patch(PatchBrush + 3, RidgeBox, offsets: false, heights: RidgeHeight)),
    ];

    /// <summary>The quarter turns, as level placements.</summary>
    public static TheoryData<int> Turns => [0, 90, 180, 270];

    /// <summary>
    /// A room's map holds its displaced ground at the surface's heights, not
    /// at its base faces': the hub's patches (their base faces at z 24) map
    /// as one piece of floor from z 25 up (to x 193: a vertex on the east
    /// edge is shifted half a unit out by its offset, and halves round up),
    /// and nothing is at z 24 over them;
    /// the ridge leaves floor either side of it, and none on its flanks (the
    /// ridge's crest, at x 192 of the room, is inside no floor polygon at
    /// z 21, the ground beside it).
    /// </summary>
    [Fact]
    public async Task ARoomsMapHoldsItsDisplacedGround()
    {
        RoomLibrary rooms = await CompileAsync(Library(Ridge), cook: false);
        RoomMapView hub = rooms.Get("hub").MapViewOfCompile!;
        MapPolygon patches = Assert.Single(hub.Polygons, p => p.ZLow > 24);
        Assert.Equal((64, 64, 193, 176), Bounds(patches.Outer));
        Assert.DoesNotContain(hub.Polygons, p => p.ZLow <= 24 && p.ZHigh >= 24);

        RoomMapView other = rooms.Get("other").MapViewOfCompile!;
        MapPolygon[] beside = [.. other.Polygons.Where(p => (p.ZLow, p.ZHigh) == (21, 21))];
        Assert.Equal([(160, 32, 176, 96), (208, 32, 224, 96)], beside.Select(p => Bounds(p.Outer)).Order().ToList());
        Assert.DoesNotContain(other.Polygons, p => p.ZHigh >= 60);
    }

    /// <summary>
    /// A level of the hub beside the other room, both at one turn: the
    /// linked map is <c>ssmap map2d</c> of the flattened compile cut by the
    /// level file, byte for byte, with the displaced ground in it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task DisplacedGroundMapsAsItFlattensAtEveryTurn(int turn)
    {
        await AssertMapsAsItFlattens(RoomPropHarness.Level($"hub@{turn}, other@{turn}"));
    }

    /// <summary>
    /// A level of mixed turns with each room placed three times: the same
    /// equivalence, and each of the hub's three placements has its own
    /// patches' ground.
    /// </summary>
    [Fact]
    public async Task DisplacedGroundMapsAsItFlattensAtMixedTurns()
    {
        Map2dLevel map = await AssertMapsAsItFlattens(RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other@90"));
        int[] hubs = [.. map.Rooms.Select((room, p) => (room, p)).Where(x => x.room.Name == "hub").Select(x => x.p)];
        Assert.Equal(3, hubs.Length);
        Assert.Equal(hubs, map.Rings.Where(r => r.ZLow > 24 && r.ZLow < 30).Select(r => r.Placement).Distinct().Order().ToArray());
    }

    private static async Task<Map2dLevel> AssertMapsAsItFlattens(LevelGrid level)
    {
        VmfDocument library = Library(Ridge);
        RoomLibrary rooms = await CompileAsync(library, cook: false);
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Map2dLevel linked = LevelMapBuilder.Plan(layout, level.Columns, level.Rows, rooms.Get).Build(1);
        BspData flat = await CompileFlatAsync(library, level, cook: false);
        Assert.Equal(Map2dWriter.Write(linked), Map2dWriter.Write(LevelMapBuilder.FromCompile(flat, 1, level, [library])));

        // The displaced ground is there: the hub's patches, and the ground beside the ridge.
        Assert.Contains(linked.Rings, r => r.ZLow > 24 && r.ZLow < 30);
        Assert.Contains(linked.Rings, r => (r.ZLow, r.ZHigh) == (21, 21));
        Assert.DoesNotContain(linked.Rings, r => r.ZHigh >= 60);
        return linked;
    }

    private static (int, int, int, int) Bounds(IReadOnlyList<MapPoint> ring) =>
        (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));
}
