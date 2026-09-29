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
using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomPropHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The pieces of the static props feature on their own: the pose turn
/// against the flatten's key turn, the <c>PROP</c> pack section, the
/// lighting file names, the <c>room_needs</c> evaluation and the socket
/// furniture rule.
/// </summary>
public sealed class RoomStaticPropsTests
{
    /// <summary>The four quarter turns, as turn counts.</summary>
    public static TheoryData<int> Turns => new() { 0, 1, 2, 3 };

    // ---- the turn ----------------------------------------------------------------------------

    /// <summary>
    /// A prop's pose turned for the link is, float for float, what vbsp reads
    /// back from the keys the flatten writes for the same entity (the rooms
    /// design, 5.9): the origin and the lighting origin through the
    /// placement's transform, the yaw by the flatten's formula, a negative
    /// zero written as zero; at turn 0 the angles as authored.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void APoseTurnsAsTheFlattenTurnsItsKeys(int turns)
    {
        (string Origin, string Angles)[] cases =
        [
            ("64 64 16", "0 30 0"),
            ("12.5 200.25 17.75", "-0 33.3 -0"),
            ("0 -0 0", "15 -90 7.5"),
            ("255.9 0.1 128", "0 359.9 0"),
        ];

        RoomPlacement placement = new("hub", 2, 3, turns);
        RoomTransform transform = new(placement, RoomHarness.Cell);
        foreach ((string origin, string angles) in cases)
        {
            VmfChunk entity = Entity("prop_static", 1, Vec3.Zero);
            entity.Keys.Single(k => k.Name == "origin").Value = origin;
            entity.AddKey("angles", angles);
            VmfChunk moved = VmfPlacement.MoveEntity(entity, QuarterTurn.Of(transform));

            Box bounds = new(new Vec3(1, 2, 3), new Vec3(4, 6, 8));
            RoomPropPose pose = new(Read(entity, "origin"), Read(entity, "angles"), Read(entity, "origin"), bounds);
            RoomPropPose turned = RoomStaticProps.Turn(pose, turns, lightingOrigin: true);
            Assert.Equal(Bits(Read(moved, "origin")), Bits(RoomStaticProps.Unsigned(transform.Translate(turned.Origin))));
            Assert.Equal(Bits(Read(moved, "origin")), Bits(RoomStaticProps.Unsigned(transform.Translate(turned.LightingOrigin))));
            Assert.Equal(Bits(Read(moved, "angles")), Bits(turned.Angles));
            Assert.Equal(LevelLinker.RotateBox(bounds.Mins, bounds.Maxs, turns), turned.Bounds);
        }

        // Without a lighting origin, the record's value is not a position and does not move.
        RoomPropPose unlit = new(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(7, 8, 9), default);
        Assert.Equal(new Vec3(7, 8, 9), RoomStaticProps.Turn(unlit, turns, lightingOrigin: false).LightingOrigin);
    }

    // ---- the section -------------------------------------------------------------------------

    /// <summary>
    /// A room's props go through their pack section and back unchanged, with
    /// each codec, bound to the BSP they are read with; a section of a
    /// revision this build does not read is absent.
    /// </summary>
    [Fact]
    public async Task TheSectionRoundTripsWithEachCodec()
    {
        RoomObject hub = await HubWithPropsAsync();
        RoomStaticProps props = hub.StaticProps!;
        Assert.Equal(2, props.Props.Count);
        Assert.Equal(4, props.TurnCount);
        foreach (RoomLinkCodec codec in new[] { RoomLinkCodec.None, RoomLinkCodec.Deflate, RoomLinkCodec.Brotli })
        {
            RoomPackSectionData section = props.ToSection(codec);
            Assert.Equal(RoomStaticProps.SectionTag, section.Tag);
            RoomStaticProps read = RoomStaticProps.Read(section.Bytes.ToArray(), hub.Definition, hub.Bsp)!;
            Assert.True(read.IsFor(hub));
            Assert.Equal(props.Props, read.Props, RecordEquals);
            Assert.Equal(props.Hulls.Count, read.Hulls.Count);
            for (int m = 0; m < props.Hulls.Count; m++)
            {
                Assert.Equal(props.Hulls[m].Select(h => h.ToArray()), read.Hulls[m].Select(h => h.ToArray()));
            }

            for (int turn = 0; turn < 4; turn++)
            {
                Assert.Equal(props.Poses(turn), read.Poses(turn));
            }
        }

        Assert.Null(RoomStaticProps.Read(null, hub.Definition, hub.Bsp));
        RoomLinkSections.Writer future = new();
        future.Int(RoomStaticProps.Revision + 1);
        Assert.Null(RoomStaticProps.Read(RoomLinkSections.Encode(future.ToArray(), RoomLinkCodec.None), hub.Definition, hub.Bsp));
    }

    /// <summary>
    /// Turn 0 alone, turned by the link, is every turn's poses exactly: the
    /// once-against-four-times rule of the rooms design (15.9), on the data.
    /// </summary>
    [Fact]
    public async Task TurnZeroTurnedIsEveryStoredTurn()
    {
        RoomStaticProps props = (await HubWithPropsAsync()).StaticProps!;
        RoomStaticProps once = props.WithTurnZeroOnly();
        Assert.Equal(1, once.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(props.Poses(turn), once.Poses(turn));
        }
    }

    /// <summary>
    /// A section that does not fit the room it sits with is refused as
    /// damaged, naming the room and the section: another model or prop
    /// count, a turn count other than 1 or 4, bytes after its end.
    /// </summary>
    [Fact]
    public async Task ASectionThatDoesNotFitItsRoomIsRefused()
    {
        RoomObject hub = await HubWithPropsAsync();
        byte[] Payload(Action<RoomLinkSections.Writer> write)
        {
            RoomLinkSections.Writer w = new();
            w.Int(RoomStaticProps.Revision);
            write(w);
            return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
        }

        LinkException models = Assert.Throws<LinkException>(() => RoomStaticProps.Read(Payload(w => w.Int(5)), hub.Definition, hub.Bsp));
        Assert.Equal("room pack entry \"hub\": its \"PROP\" section holds 5 prop models; the room has 2.", models.Message);

        BspData bare = new();
        LinkException props = Assert.Throws<LinkException>(() => RoomStaticProps.Read(Payload(w => { w.Int(0); w.Int(3); }), hub.Definition, bare));
        Assert.Equal("room pack entry \"hub\": its \"PROP\" section holds 3 props; the room has 0.", props.Message);

        LinkException turns = Assert.Throws<LinkException>(() => RoomStaticProps.Read(Payload(w => { w.Int(0); w.Int(0); w.Int(2); }), hub.Definition, bare));
        Assert.Equal("room pack entry \"hub\": its \"PROP\" section holds 2 turns of poses; a section holds 1 or 4.", turns.Message);

        LinkException trailing = Assert.Throws<LinkException>(() => RoomStaticProps.Read(Payload(w => { w.Int(0); w.Int(0); w.Int(1); w.Byte(9); }), hub.Definition, bare));
        Assert.Equal("room pack entry \"hub\": its \"PROP\" section holds 1 bytes after its end.", trailing.Message);

        RoomStaticProps? empty = RoomStaticProps.Read(Payload(w => { w.Int(0); w.Int(0); w.Int(4); }), hub.Definition, bare);
        Assert.Empty(empty!.Props);
    }

    // ---- lighting files ----------------------------------------------------------------------

    /// <summary>Only vrad's own spelling of a static prop lighting file names a prop.</summary>
    [Theory]
    [InlineData("sp_0.vhv", 0, false)]
    [InlineData("sp_17.vhv", 17, false)]
    [InlineData("sp_hdr_3.vhv", 3, true)]
    [InlineData("sp_03.vhv", -1, false)]
    [InlineData("sp_.vhv", -1, false)]
    [InlineData("sp_hdr_.vhv", -1, false)]
    [InlineData("sp_1a.vhv", -1, false)]
    [InlineData("sp_-1.vhv", -1, false)]
    [InlineData("maps/sp_1.vhv", -1, false)]
    [InlineData("sp_1.vtf", -1, false)]
    [InlineData("sp_99999999999.vhv", -1, false)]
    public void APropLightingFileIsNamedAsVradNamesIt(string name, int prop, bool hdr)
    {
        Assert.Equal(prop < 0 ? null : (prop, hdr), LevelPakFiles.PropLightingFile(name));
    }

    // ---- room_needs and socket furniture -----------------------------------------------------

    /// <summary>
    /// <c>room_needs</c> on a record: every condition must hold; a neighbour
    /// condition looks at the cell its authored direction turns to, a joined
    /// one at the placement's joined sides in its own frame, and <c>!</c>
    /// negates either; no condition always holds.
    /// </summary>
    [Fact]
    public void NeedsHoldWhereEveryConditionDoes()
    {
        static List<RoomNeed> Parse(string value)
        {
            Assert.True(RoomNeeds.TryParse(value, out List<RoomNeed> needs, out _));
            return needs;
        }

        HashSet<(int X, int Y)> occupied = [(1, 0), (0, 1), (1, 1)];
        Assert.True(RoomNeeds.Hold([], 0, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.True(RoomNeeds.Hold(Parse("east"), 0, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.True(RoomNeeds.Hold(Parse("east"), 1, 0, 0, occupied.Contains, JoinedMask.None)); // east turned is north
        Assert.False(RoomNeeds.Hold(Parse("east"), 2, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.True(RoomNeeds.Hold(Parse("northeast"), 0, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.False(RoomNeeds.Hold(Parse("northeast"), 2, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.True(RoomNeeds.Hold(Parse("!west"), 0, 0, 0, occupied.Contains, JoinedMask.None));
        Assert.False(RoomNeeds.Hold(Parse("east, !north"), 0, 0, 0, occupied.Contains, JoinedMask.None));
        JoinedMask east = RoomDirections.JoinedBit(RoomDirection.East);
        Assert.True(RoomNeeds.Hold(Parse("joined_east"), 3, 5, 5, occupied.Contains, east));
        Assert.False(RoomNeeds.Hold(Parse("!joined_east"), 3, 5, 5, occupied.Contains, east));
        Assert.False(RoomNeeds.Hold(Parse("joined_west"), 0, 0, 0, occupied.Contains, east));
    }

    /// <summary>
    /// The furniture rule (open point O5): dropped at a cap; at a joint kept
    /// when the other side has none, by the higher priority, and on a tie by
    /// the earlier placement in link order.
    /// </summary>
    [Fact]
    public void FurnitureIsKeptByPriorityThenLinkOrderAndDroppedAtACap()
    {
        RoomDefinition hub = Hub;
        LevelLayout layout = new("f", RoomHarness.Cell, RoomHarness.WalkableKit,
        [
            new RoomInstance(new RoomPlacement("hub", 0, 0, 0), [("east", "west")], ["west", "north", "south"]),
            new RoomInstance(new RoomPlacement("hub", 1, 0, 0), [("west", "east")], ["east", "north", "south"]),
        ]);

        bool Keeps(int placement, string socket, Func<int, string, int?> furniture) =>
            SocketFurniture.Keeps(layout, _ => hub, placement, socket, furniture);

        int? Both(int placement, string socket) => 0;
        int? OnlyFirst(int placement, string socket) => placement == 0 ? 0 : null;
        int? SecondHigher(int placement, string socket) => placement == 1 ? 5 : 1;

        Assert.False(Keeps(0, "west", Both));
        Assert.False(Keeps(1, "east", Both));
        Assert.True(Keeps(0, "east", Both));
        Assert.False(Keeps(1, "west", Both));
        Assert.True(Keeps(0, "east", OnlyFirst));
        Assert.True(Keeps(1, "west", (p, _) => p == 1 ? 0 : null));
        Assert.False(Keeps(0, "east", SecondHigher));
        Assert.True(Keeps(1, "west", SecondHigher));
    }

    /// <summary>
    /// The regions outside a cell that are not a socket's doorway: a point
    /// in the doorway beyond the socket is in none, a point beyond any other
    /// face, past the doorway's depth or beside its opening is in one.
    /// </summary>
    [Theory]
    [InlineData(264f, 128f, 100f, false)]
    [InlineData(250f, 128f, 100f, false)]
    [InlineData(280f, 128f, 100f, true)]
    [InlineData(264f, 60f, 100f, true)]
    [InlineData(264f, 190f, 100f, true)]
    [InlineData(264f, 128f, 250f, true)]
    [InlineData(-4f, 128f, 100f, true)]
    [InlineData(128f, 260f, 100f, true)]
    [InlineData(128f, -4f, 100f, true)]
    [InlineData(128f, 128f, -4f, true)]
    [InlineData(128f, 128f, 260f, true)]
    public void TheDoorwayIsTheOnlyWayOutOfTheCell(float x, float y, float z, bool outside)
    {
        RoomDefinition hub = Hub;
        IReadOnlyList<(Vec3 Normal, float Dist)[]> regions = RoomStaticProps.OutsideDoorway(hub, hub.Sockets.Single(s => s.Name == "east"));
        Vec3 p = new(x, y, z);
        Assert.Equal(outside, regions.Any(region => region.All(plane => Vec3.Dot(plane.Normal, p) <= plane.Dist)));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static async Task<RoomObject> HubWithPropsAsync() =>
        (await CompileAsync(Library(
            (0, Prop(700, BoxModel, new Vec3(64, 64, 16), "0 30 0", (RoomNeeds.Key, "east, !joined_north"), ("disableshadows", "1"))),
            (0, Prop(701, BarModel, new Vec3(236, 128, 100), "0 0 0", (RoomStaticProps.SocketKey, "east"), (RoomStaticProps.PriorityKey, "3")))))).Get("hub");

    private static readonly RecordComparer RecordEquals = new();

    private sealed class RecordComparer : IEqualityComparer<RoomProp>
    {
        public bool Equals(RoomProp? a, RoomProp? b) =>
            a is not null && b is not null && a.Id == b.Id && a.Socket == b.Socket && a.Priority == b.Priority && a.Needs.SequenceEqual(b.Needs);

        public int GetHashCode(RoomProp obj) => obj.Id;
    }

    private static Vec3 Read(VmfChunk entity, string key)
    {
        MapEntity parsed = new();
        parsed.SetKeyValue(key, entity.GetValue(key)!);
        return parsed.GetVectorForKey(key);
    }

    private static (int, int, int) Bits(Vec3 v) =>
        (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
}
