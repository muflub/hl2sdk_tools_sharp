//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's area portal data on its own (the rooms design, 4.11 and 15.2's
/// area portals row): what a compile has, the checks on its three lumps and
/// its portal numbers, the section and its refusals, the refusal of a
/// portal in a socket's plug box or named as furniture, the rebase of a
/// portal's number, and the link's limits on the totals.
/// </summary>
public sealed class RoomAreaPortalsTests
{
    /// <summary>
    /// A compile without area portals (one area, the reserved listing, no
    /// portal number) has no data; one with a portal has it, its clip
    /// vertices turned four ways; one whose only portal sealed nothing (a
    /// number, no listing) has it too, since its number is still one of the
    /// level's, and so does one with clip vertices alone.
    /// </summary>
    [Fact]
    public void ACompileHasDataOnlyWithAreaPortals()
    {
        Assert.Null(RoomAreaPortals.Build("r", Bsp(portal: false)));

        RoomAreaPortals built = RoomAreaPortals.Build("r", Bsp())!;
        Assert.Equal(3, built.AreaCount);
        Assert.Equal(3, built.ListingCount);
        Assert.Equal(1, built.PortalNumbers);
        Assert.Equal(8, built.ClipVertCount);
        Assert.Equal(4, built.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(RoomTransform.Rotate(Verts[0], turn), built.ClipVerts(turn)[0]);
            Assert.Equal(RoomTransform.Rotate(Verts[7], turn), built.ClipVerts(turn)[7]);
            Assert.Equal(built.ClipVerts(turn), built.WithTurnZeroOnly().ClipVerts(turn));
        }

        RoomAreaPortals numbered = RoomAreaPortals.Build("r", Bsp(portal: false, entities: [Portal("1")]))!;
        Assert.Equal(2, numbered.AreaCount);
        Assert.Equal(1, numbered.PortalNumbers);
        Assert.Equal(0, numbered.ClipVertCount);

        BspData stray = Bsp(portal: false);
        Set(stray, BspLump.ClipPortalVerts, Verts);
        Assert.Equal(8, RoomAreaPortals.Build("r", stray)!.ClipVertCount);
    }

    /// <summary>Each listing's own area is the one whose run holds it; a listing no run holds has none.</summary>
    [Fact]
    public void AListingsSourceIsTheAreaWhoseRunHoldsIt()
    {
        RoomAreaLumps lumps = RoomAreaPortals.Lumps("r", Bsp());
        Assert.Equal(1, lumps.SourceOf(1));
        Assert.Equal(2, lumps.SourceOf(2));
        Assert.Equal(0, lumps.SourceOf(3));
    }

    /// <summary>
    /// The three lumps are refused, naming the room, when they are not what
    /// vbsp writes: no reserved area or listing, or one that is not empty;
    /// an area's run out of order; runs that do not cover the listings; a
    /// listing naming an area, portal, plane or vertices the room lacks.
    /// </summary>
    [Fact]
    public void LumpsVbspWouldNotWriteAreRefused()
    {
        const string Reserved = "room r has 3 areas and 3 area portal listings; vbsp writes the reserved area 0 and listing 0 empty, and at least one area after them.";
        Assert.Equal(Reserved, Refused(bsp => Set(bsp, BspLump.Areas, [new DArea { NumAreaPortals = 1 }, Area(1, 1), Area(2, 1)])));
        Assert.Equal(Reserved, Refused(bsp => Set(bsp, BspLump.AreaPortals, [Listing(1, 1, 0), Listing(1, 2, 0), Listing(1, 1, 4)])));
        Assert.Equal(
            "room r has 1 areas and 3 area portal listings; vbsp writes the reserved area 0 and listing 0 empty, and at least one area after them.",
            Refused(bsp => Set(bsp, BspLump.Areas, [default(DArea)])));
        Assert.Equal(
            "room r's area 2 lists area portals from 1 (1 of them); vbsp lists each area's portals after the previous area's, from 1.",
            Refused(bsp => Set(bsp, BspLump.Areas, [default, Area(1, 1), Area(1, 1)])));
        Assert.Equal(
            "room r's areas list 1 area portals but the lump holds 2; vbsp lists every portal from one area.",
            Refused(bsp => Set(bsp, BspLump.Areas, [default, Area(1, 1), Area(2, 0)])));
        Assert.Equal(
            "room r's area portal listing 1 names area 3, portal 1, plane 1 and clip vertices 0 to 4; the room has 3 areas, 2 planes and 8 clip vertices.",
            Refused(bsp => Set(bsp, BspLump.AreaPortals, [default, Listing(1, 3, 0), Listing(1, 1, 4)])));
        Assert.Equal(
            "room r's area portal listing 2 names area 1, portal 0, plane 1 and clip vertices 4 to 8; the room has 3 areas, 2 planes and 8 clip vertices.",
            Refused(bsp => Set(bsp, BspLump.AreaPortals, [default, Listing(1, 2, 0), Listing(0, 1, 4) with { PlaneNum = 1 }])));
        Assert.Equal(
            "room r's area portal listing 1 names area 2, portal 1, plane 2 and clip vertices 0 to 4; the room has 3 areas, 2 planes and 8 clip vertices.",
            Refused(bsp => Set(bsp, BspLump.AreaPortals, [default, Listing(1, 2, 0) with { PlaneNum = 2 }, Listing(1, 1, 4)])));
        Assert.Equal(
            "room r's area portal listing 2 names area 1, portal 1, plane 1 and clip vertices 6 to 10; the room has 3 areas, 2 planes and 8 clip vertices.",
            Refused(bsp => Set(bsp, BspLump.AreaPortals, [default, Listing(1, 2, 0), Listing(1, 1, 6)])));

        static string Refused(Action<BspData> edit)
        {
            BspData bsp = Bsp();
            edit(bsp);
            return Assert.Throws<LinkException>(() => RoomAreaPortals.Build("r", bsp)).Message;
        }
    }

    /// <summary>
    /// The portal numbers a room's entities carry are counted when they are
    /// 1, 2, ... once each on area portal entities (windows too), and
    /// refused, naming the room, otherwise.
    /// </summary>
    [Fact]
    public void PortalNumbersAreCountedAndChecked()
    {
        Assert.Equal(0, RoomAreaPortals.PortalNumbersOf("r", [Entity(("classname", "info_target"))]));
        Assert.Equal(2, RoomAreaPortals.PortalNumbersOf("r", [Portal("2"), Portal("1", "func_areaportalwindow")]));
        Assert.Equal(
            "room r has a info_target with a \"portalnumber\"; vbsp numbers only area portal entities.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.PortalNumbersOf("r", [Entity(("classname", "info_target"), ("portalnumber", "1"))])).Message);
        Assert.Equal(
            "room r has an area portal whose \"portalnumber\" is \"one\"; vbsp numbers a map's portals 1, 2, ... once each.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.PortalNumbersOf("r", [Portal("one")])).Message);
        Assert.Equal(
            "room r has an area portal whose \"portalnumber\" is \"0\"; vbsp numbers a map's portals 1, 2, ... once each.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.PortalNumbersOf("r", [Portal("0")])).Message);
        Assert.Equal(
            "room r has an area portal whose \"portalnumber\" is \"1\"; vbsp numbers a map's portals 1, 2, ... once each.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.PortalNumbersOf("r", [Portal("1"), Portal("1")])).Message);
        Assert.Equal(
            "room r's area portals are numbered up to 3 but there are 2; vbsp numbers a map's portals 1, 2, ... without a gap.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.PortalNumbersOf("r", [Portal("1"), Portal("3")])).Message);
    }

    /// <summary>
    /// The section reads back what it wrote, at four turns and at one (turn
    /// 0, the link turning it, to the same vertices); it is refused as
    /// damaged when it does not fit the room's lumps, counts impossible
    /// portal numbers, holds a turn count other than 1 or 4, or has bytes
    /// after its end; an absent section, or one of a revision this build
    /// does not read, is none.
    /// </summary>
    [Fact]
    public void TheSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp();
        RoomAreaPortals built = RoomAreaPortals.Build("r", bsp)!;
        RoomAreaPortals read = RoomAreaPortals.Read(Payload(built.ToSection()), "r", bsp)!;
        RoomAreaPortals once = RoomAreaPortals.Read(Payload(built.WithTurnZeroOnly().ToSection()), "r", bsp)!;
        Assert.Equal(4, read.TurnCount);
        Assert.Equal(1, once.TurnCount);
        Assert.Equal((3, 3, 1, 8), (read.AreaCount, read.ListingCount, read.PortalNumbers, read.ClipVertCount));
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(built.ClipVerts(turn), read.ClipVerts(turn));
            Assert.Equal(built.ClipVerts(turn), once.ClipVerts(turn));
        }

        Assert.True(read.IsFor(new RoomObject(RoomHarness.Hub(), bsp, null!, null!, [])));
        Assert.False(read.IsFor(new RoomObject(RoomHarness.Hub(), Bsp(), null!, null!, [])));

        Assert.Null(RoomAreaPortals.Read(null, "r", bsp));
        byte[] revised = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(revised.AsSpan(9), 99);
        Assert.Null(RoomAreaPortals.Read(revised, "r", bsp));

        Assert.Equal(
            "room pack entry \"r\": its \"APRT\" section holds 3 areas, 3 area portal listings and 8 clip vertices; the room has 2, 1 and 0.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.Read(Payload(built.ToSection()), "r", Bsp(portal: false))).Message);

        byte[] numbers = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(numbers.AsSpan(9 + 16), -1);
        Assert.Equal(
            "room pack entry \"r\": its \"APRT\" section holds -1 portal numbers.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.Read(numbers, "r", bsp)).Message);

        byte[] twoTurns = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(twoTurns.AsSpan(9 + 20), 2);
        Assert.Equal(
            "room pack entry \"r\": its \"APRT\" section holds 2 turns of clip vertices; a section holds 1 or 4.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.Read(twoTurns, "r", bsp)).Message);

        byte[] section = Payload(built.ToSection()).ToArray();
        byte[] longer = [.. section, 0];
        BinaryPrimitives.WriteInt64BigEndian(longer.AsSpan(1), BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)) + 1);
        Assert.Equal(
            "room pack entry \"r\": its \"APRT\" section holds 1 bytes after its end.",
            Assert.Throws<LinkException>(() => RoomAreaPortals.Read(longer, "r", bsp)).Message);
    }

    /// <summary>
    /// An area portal whose brush reaches into a socket's plug box is
    /// refused with the rooms design's text (15.4, 4.11 socket), its own
    /// class named; one that only touches the plug box's face, and any other
    /// entity there, is not; one named as socket furniture is refused.
    /// </summary>
    [Fact]
    public void APortalInAPlugBoxOrAsFurnitureIsRefused()
    {
        RoomDefinition split = RoomAreaPortalHarness.Split;
        Box east = RoomLinter.SealBox(split, split.Sockets[0], split.CellSize);
        Box inPlug = new(new Vec3(east.Mins.X - 8, 104, 16), new Vec3(east.Mins.X + 4, 152, 112));
        Box touching = new(new Vec3(east.Mins.X - 8, 104, 16), new Vec3(east.Mins.X, 152, 112));

        Assert.Null(RoomAreaPortals.Problem(split, Room()));
        Assert.Null(RoomAreaPortals.Problem(split, Room(RoomAreaPortalHarness.Portal(5, touching))));
        Assert.Null(RoomAreaPortals.Problem(split, Room(RoomAreaPortalHarness.Portal(5, inPlug, "func_detail"))));
        Assert.Equal(
            "room split: func_areaportal 5 lies in socket \"east\"'s plug box.",
            RoomAreaPortals.Problem(split, Room(RoomAreaPortalHarness.Portal(5, inPlug))));
        Assert.Equal(
            "room split: func_areaportalwindow 6 lies in socket \"east\"'s plug box.",
            RoomAreaPortals.Problem(split, Room(RoomAreaPortalHarness.Portal(6, inPlug, "func_areaportalwindow"))));
        Assert.Equal(
            "room split: func_areaportal 7 has room_socket; an area portal is built into its room's world and cannot be socket furniture.",
            RoomAreaPortals.Problem(split, Room(RoomAreaPortalHarness.Portal(7, touching, keys: ("room_socket", "east")))));

        static VmfDocument Room(params VmfChunk[] entities)
        {
            VmfDocument document = RoomHarness.BuildRoomModel(RoomAreaPortalHarness.Split);
            foreach (VmfChunk entity in entities)
            {
                document.Chunks.Add(entity);
            }

            return document;
        }
    }

    /// <summary>
    /// A placement's portal base shifts <c>portalnumber</c> and nothing
    /// else; the first room (base 0) keeps the key's text as written; a key
    /// that is not a number is refused naming the room.
    /// </summary>
    [Fact]
    public void PortalNumbersShiftByThePlacementsBase()
    {
        RoomTransform at = new(new RoomPlacement("r", 1, 0, 1), 256);
        BspEntity portal = Entity(("classname", "func_areaportal"), ("portalnumber", "2"), ("StartOpen", "1"), ("target", "door"));
        BspEntity moved = LevelLinker.MoveEntity(portal, at, "r", portalBase: 3);
        Assert.Equal("5", moved.Get("portalnumber"));
        Assert.Equal("1", moved.Get("StartOpen"));
        Assert.Equal("door", moved.Get("target"));
        Assert.Equal([.. portal.Pairs.Select(p => p.Key)], moved.Pairs.Select(p => p.Key));

        Assert.Equal("02", LevelLinker.MoveEntity(Entity(("classname", "func_areaportal"), ("portalnumber", "02")), at, "r").Get("portalnumber"));
        Assert.Equal(
            "room attic has an entity whose \"portalnumber\" holds \"first\", not a portal number",
            Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(
                Entity(("classname", "func_areaportal"), ("portalnumber", "first")), at, "attic", portalBase: 1)).Message);
    }

    /// <summary>
    /// The link's totals of area portals are refused past the format's
    /// fields and the loader's cap, naming the room and cell, and pass at
    /// the limits themselves.
    /// </summary>
    [Fact]
    public void AreaPortalTotalsAreHeldToTheFormat()
    {
        LevelLinker.AreaPortalLimits("r", 2, 3, ushort.MaxValue, 1024, ushort.MaxValue + 1);
        Assert.Equal(
            "room r at cell (2, 3) pushes the link to 65536 area portal numbers; the format carries at most 65535.",
            Assert.Throws<LinkException>(() => LevelLinker.AreaPortalLimits("r", 2, 3, 65536, 1, 0)).Message);
        Assert.Equal(
            "room r at cell (2, 3) pushes the link to 1025 area portal listings; the engine loads at most 1024 (MAX_MAP_AREAPORTALS).",
            Assert.Throws<LinkException>(() => LevelLinker.AreaPortalLimits("r", 2, 3, 0, 1025, 0)).Message);
        Assert.Equal(
            "room r at cell (2, 3) pushes the link to 65537 clip portal vertices; the format carries at most 65536.",
            Assert.Throws<LinkException>(() => LevelLinker.AreaPortalLimits("r", 2, 3, 0, 1, 65537)).Message);
    }

    /// <summary>A portal's clip vertices: a 48 x 96 rectangle on x 120, then the same on x 136.</summary>
    private static Vec3[] Verts =>
    [
        new(120, 104, 16), new(120, 152, 16), new(120, 152, 112), new(120, 104, 112),
        new(136, 104, 16), new(136, 152, 16), new(136, 152, 112), new(136, 104, 112),
    ];

    private static DArea Area(int first, int count) => new() { FirstAreaPortal = first, NumAreaPortals = count };

    private static DAreaPortal Listing(ushort key, ushort other, ushort first) =>
        new() { PortalKey = key, OtherArea = other, FirstClipPortalVert = first, ClipPortalVerts = 4, PlaneNum = other == 2 ? 0 : 1 };

    /// <summary>
    /// A compile's lumps: with <paramref name="portal"/>, two areas and one
    /// portal between them listed from both, its clip vertices, its entity;
    /// without, one area and the reserved listing, and
    /// <paramref name="entities"/> as the entity lump.
    /// </summary>
    private static BspData Bsp(bool portal = true, BspEntity[]? entities = null)
    {
        BspData bsp = new();
        Set(bsp, BspLump.Planes, [new DPlane { Normal = new Vec3(1, 0, 0), Dist = 120 }, new DPlane { Normal = new Vec3(-1, 0, 0), Dist = -120 }]);
        if (portal)
        {
            Set(bsp, BspLump.Areas, [default, Area(1, 1), Area(2, 1)]);
            Set(bsp, BspLump.AreaPortals, [default, Listing(1, 2, 0), Listing(1, 1, 4)]);
            Set(bsp, BspLump.ClipPortalVerts, Verts);
        }
        else
        {
            Set(bsp, BspLump.Areas, [default, Area(1, 0)]);
            Set(bsp, BspLump.AreaPortals, [default(DAreaPortal)]);
        }

        bsp[BspLump.Entities] = EntityLump.Write([Entity(("classname", "worldspawn")), .. entities ?? (portal ? [Portal("1")] : [])]);
        return bsp;
    }

    private static void Set<T>(BspData bsp, BspLump lump, T[] items)
        where T : unmanaged =>
        bsp.SetLump(lump, MemoryMarshal.AsBytes(items.AsSpan()).ToArray());

    private static BspEntity Portal(string number, string classname = "func_areaportal") =>
        Entity(("classname", classname), ("portalnumber", number));

    private static ArraySegment<byte> Payload(RoomPackSectionData section) => section.Bytes.ToArray();

    private static BspEntity Entity(params (string Key, string Value)[] pairs)
    {
        BspEntity entity = new();
        foreach ((string key, string value) in pairs)
        {
            entity.Pairs.Add(new BspKeyValue(key, value));
        }

        return entity;
    }
}
