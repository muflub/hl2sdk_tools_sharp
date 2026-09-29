//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomAreaPortalHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Door portals (the rooms design, 4.11 "area portals as doors", open point
/// O10): a library that asks for them gets an area portal in every joint of
/// its levels, in the link and the flatten alike, each following the
/// joint's kept door when there is one; a library that does not keeps its
/// joints open.
/// </summary>
public sealed class LevelLinkerDoorPortalTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static LevelGrid Line(int rotation) => rotation % 180 == 0
        ? RoomPropHarness.Level($"hub, split@{rotation}, hub")
        : RoomPropHarness.Level("hub", $"split@{rotation}", "hub");

    /// <summary>
    /// The split room between two hubs with door portals, at every quarter
    /// turn: every room's areas stay apart (four areas: the hubs and the
    /// split room's halves), the room's own portal keeps number 1 and the
    /// two joints' portals are numbered 2 and 3 after it; the linked and the
    /// flattened maps make the same partition, list the same portals by
    /// outline between the same areas, and carry the same portal entities
    /// (<c>StartOpen</c>, no door to follow); the link's door portals lie
    /// on the cell faces, the flattened ones on a face of the thin brush
    /// astride them; and each door portal costs the level one entity.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task DoorPortalsDivideEveryJointAtEveryRotation(int rotation)
    {
        VmfDocument library = WithDoorPortals(Library());
        LevelGrid level = Line(rotation);
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(5, Areas(linked.Bsp).Length);
        Assert.Equal(7, Listings(linked.Bsp).Length);
        Assert.Equal([1, 2, 3], PortalNumbers(linked.Bsp));
        Assert.Equal(PortalNumbers(flat), PortalNumbers(linked.Bsp));
        Dictionary<int, int> names = SamePartition(linked.Bsp, flat, level);
        Assert.Equal(4, names.Count);
        Assert.Equal(Portals(flat), Portals(linked.Bsp, names));
        Assert.Equal(PortalEntities(flat), PortalEntities(linked.Bsp));
        Assert.Equal(["portalnumber=2 | StartOpen=1 | classname=func_areaportal", "portalnumber=3 | StartOpen=1 | classname=func_areaportal"], PortalEntities(linked.Bsp).Skip(1));
        NodeAreasHold(linked.Bsp);

        List<RoomInstance> rooms = [.. linked.Plan.Layout.Rooms];
        Box author = Placed(Doorway, rooms[1].Placement);
        List<Box> faces = [];
        List<Box> slabs = [];
        foreach (RoomInstance room in rooms)
        {
            foreach ((string socket, _) in room.Joints)
            {
                (int axis, _, Box face) = LevelDoorPortals.Doorway(Definition(room.Placement.Room), room.Placement, socket);
                Vec3 reach = axis == 0 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                faces.Add(face);
                slabs.Add(new Box(face.Mins - reach, face.Maxs + reach));
            }
        }

        OnPortalFace(linked.Bsp, [author, .. faces]);
        OnPortalFace(flat, [author, .. slabs]);

        LinkedLevel open = await LinkAsync(await CompileAsync(Library()), level);
        Assert.Equal(open.EntityBudget!.Edicts + 2, linked.EntityBudget!.Edicts);
        Assert.Equal(EntityLump.Parse(linked.Bsp[BspLump.Entities]).Count, linked.EntityBudget.Listed);
        Assert.Empty(linked.AreaWarnings);
    }

    /// <summary>
    /// A door portal follows the joint's kept socket furniture when it is a
    /// named door: the hub's <c>func_door</c> on its east socket, named
    /// room-locally, makes the joint east of it target the door's level
    /// name, in the link and the flatten alike; the other joint, where no
    /// door is kept, starts open. A level of rooms without an author portal
    /// gets door portals too.
    /// </summary>
    [Fact]
    public async Task ADoorPortalFollowsTheKeptDoor()
    {
        VmfChunk door = RoomBrushHarness.Door(
            700500, new Vec3(224, 80, 16), new Vec3(232, 176, 120), ("targetname", "cxry_door"), ("room_socket", "east"));
        VmfDocument library = WithDoorPortals(Library(false, (0, door)));
        LevelGrid level = RoomPropHarness.Level("hub, split, hub");
        LinkedLevel linked = await LinkAsync(await CompileAsync(library), level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(
            ["portalnumber=1 | target=c0r0_door | classname=func_areaportal", "portalnumber=2 | StartOpen=1 | classname=func_areaportal"],
            PortalEntities(linked.Bsp));
        Assert.Equal(PortalEntities(flat), PortalEntities(linked.Bsp));
        Assert.Equal(4, Areas(linked.Bsp).Length);
        Assert.Equal(Portals(flat), Portals(linked.Bsp, SamePartition(linked.Bsp, flat, level)));
    }

    /// <summary>
    /// A level of more rooms than a map holds areas, with door portals, is
    /// refused naming the room whose area crossed <c>MAX_MAP_AREAS</c>:
    /// every room is its own area, so a sixteen by sixteen grid of hubs
    /// brings the 256th with its last room; the same level with its joints
    /// open is one area.
    /// </summary>
    [Fact]
    public async Task DoorPortalsSpendAnAreaPerRoom()
    {
        RoomLibrary rooms = await CompileAsync(WithDoorPortals(Library()));
        string row = string.Join(", ", Enumerable.Repeat("hub", 16));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => LinkAsync(rooms, RoomPropHarness.Level([.. Enumerable.Repeat(row, 16)])));
        Assert.Equal("room hub at cell (15, 15) pushes the link to 257 areas; the engine loads at most 256 (MAX_MAP_AREAS).", refused.Message);

        LinkedLevel open = await LinkAsync(await CompileAsync(Library()), RoomPropHarness.Level([.. Enumerable.Repeat(row, 16)]));
        Assert.Equal(2, Areas(open.Bsp).Length);
    }

    /// <summary>
    /// A linked level with door portals passes the loader checks with no
    /// error, and is the same bytes at one thread and at many.
    /// </summary>
    [Fact]
    public async Task ALevelWithDoorPortalsChecksAndIsDeterministic()
    {
        RoomLibrary rooms = await CompileAsync(WithDoorPortals(Library()));
        LevelGrid level = RoomPropHarness.Level("hub, split, split@180, hub", "hub, hub@90, hub, hub@270");
        LinkedLevel linked = await LinkAsync(rooms, level, 1);
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
        byte[] one = await BytesAsync(linked);
        Assert.Equal(one, await BytesAsync(await LinkAsync(rooms, level, 8)));
    }

    // ---- the plan, on its own ------------------------------------------------------------------

    /// <summary>
    /// A level's door portals are its joints, each once, from its earlier
    /// placement in link order, in the order that placement lists its
    /// joints; each follows the door the socket furniture rule keeps: the
    /// side with furniture, else the earlier side; a side without a door
    /// gives none.
    /// </summary>
    [Fact]
    public void ThePlanListsEachJointOnceAndFollowsTheKeptDoor()
    {
        // Link order is row by row from the south-west: 0 (0, 0), 1 (1, 0), 2 (0, 1), 3 (1, 1).
        LevelLayout layout = RoomPropHarness.Level("hub, hub", "hub, hub").ToLayout(
            _ => RoomPropHarness.Hub, RoomHarness.Cell, RoomHarness.WalkableKit);
        Dictionary<(int, string), int> furniture = new() { [(1, "west")] = 0, [(1, "north")] = 1, [(3, "south")] = 2 };
        int? FurnitureOf(int p, string s) => furniture.TryGetValue((p, s), out int v) ? v : null;
        IReadOnlyList<LevelDoorPortal> plan = LevelDoorPortals.Plan(layout, _ => RoomPropHarness.Hub, FurnitureOf, (p, s) => $"door{p}{s}");

        Assert.Equal(
            [
                "0:east-1:west door1west",
                "0:north-2:south door0north",
                "1:north-3:south door3south",
                "2:east-3:west door2east",
            ],
            plan.Select(d => $"{d.Placement}:{d.Socket}-{d.Neighbour}:{d.NeighbourSocket} {d.Target}"));
    }

    /// <summary>
    /// A placement's door on a socket is the first of a door class with that
    /// <c>room_socket</c>, a name and no <c>room_needs</c>; a room-local name
    /// is resolved for the placement (a neighbour's offset turned with it),
    /// a global one kept; anything else gives none.
    /// </summary>
    [Fact]
    public void ADoorsNameIsTheFirstNamedDoorOnTheSocket()
    {
        RoomPlacement at = new("r", 2, 3, 1);
        static Func<string, string?> E(params (string Key, string Value)[] keys) =>
            key => keys.FirstOrDefault(k => k.Key == key).Value;

        Assert.Null(LevelDoorPortals.DoorName([], "east", at));
        Assert.Null(LevelDoorPortals.DoorName([E(("classname", "func_brush"), ("room_socket", "east"), ("targetname", "a"))], "east", at));
        Assert.Null(LevelDoorPortals.DoorName([E(("classname", "func_door"), ("room_socket", "west"), ("targetname", "a"))], "east", at));
        Assert.Null(LevelDoorPortals.DoorName([E(("classname", "func_door"), ("room_socket", "east"))], "east", at));
        Assert.Null(LevelDoorPortals.DoorName([E(("classname", "func_door"), ("room_socket", "east"), ("targetname", "a"), ("room_needs", "east"))], "east", at));
        Assert.Equal("gate", LevelDoorPortals.DoorName(
            [E(("classname", "func_door"), ("room_socket", "west"), ("targetname", "a")), E(("classname", "func_door_rotating"), ("room_socket", "east"), ("targetname", "gate"))],
            "east",
            at));
        Assert.Equal("c2r3_door", LevelDoorPortals.DoorName([E(("classname", "func_door"), ("room_socket", "east"), ("targetname", "cxry_door"))], "east", at));
        Assert.Equal("c2r4_door", LevelDoorPortals.DoorName([E(("classname", "func_door"), ("room_socket", "east"), ("targetname", "cx+1ry_door"))], "east", at));
    }

    /// <summary>
    /// A joint's doorway is its socket's plug box face on the cell face, in
    /// world coordinates, at every turn; the door portal's entities, linked
    /// and flattened, carry the same keys, the flattened one a brush of the
    /// area portal tool a unit either side of the face; and the outline is
    /// the rectangle's corners in the order vbsp's hull walk leaves them.
    /// </summary>
    [Fact]
    public void ADoorwayIsThePlugFaceOnTheCellFace()
    {
        RoomDefinition hub = RoomPropHarness.Hub;
        (int axis, int sign, Box face) = LevelDoorPortals.Doorway(hub, new RoomPlacement("hub", 1, 0, 0), "east");
        Assert.Equal((0, 1), (axis, sign));
        Assert.Equal(new Box(new Vec3(512, 80, 16), new Vec3(512, 176, 240)), face);
        (axis, sign, face) = LevelDoorPortals.Doorway(hub, new RoomPlacement("hub", 1, 0, 1), "east");
        Assert.Equal((1, 1), (axis, sign));
        Assert.Equal(new Box(new Vec3(336, 256, 16), new Vec3(432, 256, 240)), face);
        (axis, sign, face) = LevelDoorPortals.Doorway(hub, new RoomPlacement("hub", 1, 0, 2), "east");
        Assert.Equal((0, -1), (axis, sign));
        Assert.Equal(256, face.Mins.X);

        LevelDoorPortal open = new(0, "east", 1, "west", null);
        LevelDoorPortal door = open with { Target = "c0r0_door" };
        Assert.Equal(["portalnumber", "StartOpen", "classname"], LevelDoorPortals.LinkedEntity(open, 4).Pairs.Select(p => p.Key));
        Assert.Equal("4", LevelDoorPortals.LinkedEntity(open, 4).Get("portalnumber"));
        Assert.Equal("c0r0_door", LevelDoorPortals.LinkedEntity(door, 5).Get("target"));
        Assert.Null(LevelDoorPortals.LinkedEntity(door, 5).Get("StartOpen"));

        LevelLayout layout = RoomPropHarness.Level("hub, hub").ToLayout(_ => hub, RoomHarness.Cell, RoomHarness.WalkableKit);
        VmfChunk flat = LevelDoorPortals.FlatEntity(door, layout, _ => hub);
        Assert.Equal("func_areaportal", flat.GetValue("classname"));
        Assert.Equal("c0r0_door", flat.GetValue("target"));
        Assert.Null(flat.GetValue("StartOpen"));
        Assert.Null(flat.GetValue("id"));
        VmfChunk solid = Assert.Single(flat.GetChunks(MapFileLoader.SolidChunk));
        Assert.Equal(new Box(new Vec3(255, 80, 16), new Vec3(257, 176, 240)), VmfPlacement.Bounds(solid));
        Assert.All(solid.GetChunks(MapFileLoader.SideChunk), s => Assert.Equal(LevelDoorPortals.Material, s.GetValue("material")));
        Assert.Equal("1", LevelDoorPortals.FlatEntity(open, layout, _ => hub).GetValue("StartOpen"));

        Vec3[] corners = [new(256, 80, 16), new(256, 176, 16), new(256, 176, 240), new(256, 80, 240)];
        IReadOnlyList<Vec3> hull = AreaPortalGeometry.Hull(corners, new Vec3(1, 0, 0));
        Assert.Equal(4, hull.Count);
        Assert.Equal(corners.Select(v => v.ToString()).Order(), hull.Select(v => v.ToString()).Order());
    }

    private static RoomDefinition Definition(string name) => name == "split" ? Split : RoomPropHarness.Hub;
}
