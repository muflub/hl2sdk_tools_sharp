//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The detail prop transforms and the two detail sections on their own (the
/// rooms design, 4.4 and 15.2's detail props row): the pose turns, the
/// lump's checks and counts, and each section's round trip and refusals.
/// </summary>
public sealed class RoomDetailPropsTests
{
    // ---- the transforms ----------------------------------------------------------------------

    /// <summary>
    /// A room's detail props at each quarter turn: every origin turned about
    /// +z exactly; every angle turned as the flatten turns an entity's
    /// (the yaw plus 90 a turn, kept in [0, 360), pitch and roll without a
    /// negative zero); turn 0 is the compile's own, a negative zero and a
    /// negative yaw kept; a room without detail props has none.
    /// </summary>
    [Fact]
    public void PosesTurnWithTheRoom()
    {
        BspData bsp = Bsp(Prop(new Vec3(16, 8, 24), new Vec3(-0f, -30, 5)), Prop(new Vec3(1, 2, 3), new Vec3(10, 350, -0f)));
        RoomDetailProps built = RoomDetailProps.Build("r", bsp)!;
        Assert.Equal(2, built.Count);
        Assert.Equal(4, built.TurnCount);
        Assert.Null(RoomDetailProps.Build("r", Bsp()));
        Assert.Null(RoomDetailProps.Build("r", new BspData()));

        Assert.Equal(new Vec3(16, 8, 24), built.Origins(0)[0]);
        Assert.Equal(new Vec3(-8, 16, 24), built.Origins(1)[0]);
        Assert.Equal(new Vec3(-16, -8, 24), built.Origins(2)[0]);
        Assert.Equal(new Vec3(8, -16, 24), built.Origins(3)[0]);

        Assert.True(float.IsNegative(built.Angles(0)[0].X));
        Assert.Equal(-30f, built.Angles(0)[0].Y);
        Assert.Equal(new Vec3(0, 60, 5), built.Angles(1)[0]);
        Assert.False(float.IsNegative(built.Angles(1)[0].X));
        Assert.Equal(new Vec3(0, 150, 5), built.Angles(2)[0]);
        Assert.Equal(new Vec3(0, 240, 5), built.Angles(3)[0]);
        Assert.Equal(new Vec3(10, 80, 0), built.Angles(1)[1]);
        Assert.False(float.IsNegative(built.Angles(1)[1].Z));

        // Stored once, the link turns turn 0 to the same floats.
        RoomDetailProps once = built.WithTurnZeroOnly();
        Assert.Equal(1, once.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(built.Origins(turn), once.Origins(turn));
            Assert.Equal(built.Angles(turn), once.Angles(turn));
        }
    }

    // ---- the lump ----------------------------------------------------------------------------

    /// <summary>
    /// The lump's counts without parsing it (the capacity check's), its
    /// content test (the refusal's), and what vbsp never writes refused by
    /// name: a type past the four, a record naming an entry its dictionary
    /// does not hold (a model's the model names, a sprite's the sprites), a
    /// leaf past the room's, and a lump that cannot be read.
    /// </summary>
    [Fact]
    public void TheLumpIsCountedAndChecked()
    {
        BspData bsp = Bsp(Prop(new Vec3(1, 2, 3), default), Prop(new Vec3(4, 5, 6), default, type: 1));
        Assert.Equal(2, RoomDetailProps.CountOf(bsp));
        Assert.Equal(2, LevelLinker.LinkCounts.Of(bsp, 0).DetailProps);
        Assert.True(RoomDetailProps.HasContent(bsp));
        Assert.Equal(0, RoomDetailProps.CountOf(Bsp()));
        Assert.False(RoomDetailProps.HasContent(Bsp()));
        Assert.Equal(0, RoomDetailProps.CountOf(new BspData()));
        Assert.Equal(0, RoomDetailProps.CountOf(WithLump(new byte[] { 1, 0 })));

        Assert.Equal("room r's detail prop 0 is of type 4; vbsp writes 0 to 3.", Refusal(Bsp(Prop(default, default, type: 4))));
        Assert.Equal("room r's detail prop 0 names model 1; its dictionary holds 1.", Refusal(Bsp(Prop(default, default, model: 1))));
        Assert.Equal("room r's detail prop 1 names sprite 1; its dictionary holds 1.", Refusal(Bsp(Prop(default, default), Prop(default, default, type: 2, model: 1))));
        Assert.Equal("room r's detail prop 0 is in leaf 3; the room has 3.", Refusal(Bsp(Prop(default, default, leaf: 3))));
        Assert.StartsWith("room r's detail prop lump cannot be read: ", Refusal(WithLump(new byte[] { 1, 0, 0, 0 })), StringComparison.Ordinal);

        static string Refusal(BspData bsp) => Assert.Throws<LinkException>(() => RoomDetailProps.Build("r", bsp)).Message;
    }

    /// <summary>A level past the detail props a map holds is refused naming the room and cell that crossed it.</summary>
    [Fact]
    public void ALevelPastTheDetailPropCapIsRefused()
    {
        LevelLinker.LinkTotals totals = new();
        totals.Add(new LevelLinker.LinkCounts { DetailProps = 65000 }, "a", 0, 0);
        LinkException refused = Assert.Throws<LinkException>(() => totals.Add(new LevelLinker.LinkCounts { DetailProps = 536 }, "b", 1, 0));
        Assert.Equal("room b at cell (1, 0) pushes the link to 65536 detail props; the format carries at most 65535.", refused.Message);

        LevelLinker.LinkTotals full = new();
        full.Add(new LevelLinker.LinkCounts { DetailProps = 65535 }, "a", 0, 0);
    }

    // ---- the DPRP section ----------------------------------------------------------------------

    /// <summary>
    /// The detail prop section round-trips (four turns, one turn, and a
    /// compressed payload), is absent for a section of a revision this
    /// build does not read, and is refused when it does not fit its room:
    /// another prop count, a turn count other than 1 or 4, a payload cut
    /// short or with bytes after its end.
    /// </summary>
    [Fact]
    public void TheDetailPropSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp(Prop(new Vec3(16, 8, 24), new Vec3(0, 45, 0)), Prop(new Vec3(1, 2, 3), new Vec3(5, 10, 0)));
        RoomDetailProps built = RoomDetailProps.Build("r", bsp)!;
        foreach (RoomDetailProps stored in new[] { built, built.WithTurnZeroOnly() })
        {
            foreach (RoomLinkCodec codec in new[] { RoomLinkCodec.None, RoomLinkCodec.Deflate })
            {
                RoomDetailProps read = RoomDetailProps.Read(stored.ToSection(codec).Bytes.ToArray(), "r", bsp)!;
                Assert.Equal(stored.TurnCount, read.TurnCount);
                for (int turn = 0; turn < 4; turn++)
                {
                    Assert.Equal(built.Origins(turn), read.Origins(turn));
                    Assert.Equal(built.Angles(turn), read.Angles(turn));
                }
            }
        }

        Assert.Null(RoomDetailProps.Read(null, "r", bsp));
        Assert.Null(RoomDetailProps.Read(Payload(w => w.Int(RoomDetailProps.Revision + 1)), "r", bsp));

        Assert.Equal(
            "room pack entry \"r\": its \"DPRP\" section holds 3 detail props; the room has 2.",
            Refusal(w => { w.Int(RoomDetailProps.Revision); w.Int(3); w.Int(4); }));
        Assert.Equal(
            "room pack entry \"r\": its \"DPRP\" section holds 2 turns of detail props; a section holds 1 or 4.",
            Refusal(w => { w.Int(RoomDetailProps.Revision); w.Int(2); w.Int(2); }));
        Assert.Equal(
            "room pack entry \"r\": its \"DPRP\" section is truncated.",
            Refusal(w => { w.Int(RoomDetailProps.Revision); w.Int(2); w.Int(1); w.Structs<Vec3>([default, default], counted: false); }));
        Assert.Equal(
            "room pack entry \"r\": its \"DPRP\" section holds 1 bytes after its end.",
            Refusal(w => { w.Int(RoomDetailProps.Revision); w.Int(2); w.Int(1); w.Structs<Vec3>(new Vec3[4], counted: false); w.Byte(0); }));
        Assert.Equal(
            "room pack entry \"r\": its \"DPRP\" section holds 2 detail props; the room has 0.",
            Assert.Throws<LinkException>(() => RoomDetailProps.Read(built.ToSection().Bytes.ToArray(), "r", Bsp())).Message);

        string Refusal(Action<RoomLinkSections.Writer> write) =>
            Assert.Throws<LinkException>(() => RoomDetailProps.Read(Payload(write), "r", bsp)).Message;
    }

    // ---- the DPLT section ----------------------------------------------------------------------

    /// <summary>
    /// A pass's detail lighting as the bake keeps it: every prop's colour and
    /// count, the runs in prop order, and where each prop's run starts.
    /// </summary>
    [Fact]
    public void APassIsKeptAsVradGaveIt()
    {
        DetailObjectLump a = Prop(default, default), b = Prop(default, default), c = Prop(default, default);
        a.Lighting = new ColorRgbExp32 { R = 1, G = 2, B = 3, Exponent = 4 };
        a.LightStyleCount = 2;
        c.LightStyleCount = 1;
        c.LightStyles = 2;
        DetailPropLightstylesLump[] styles = [Style(32), Style(33), Style(5)];
        RoomDetailLight light = RoomDetailLight.From(new DetailPropLightingResult([a, b, c], styles, 0));
        Assert.Equal(3, light.Count);
        Assert.Equal(a.Lighting, light.Colors[0]);
        Assert.Equal([2, 0, 1], light.Counts);
        Assert.Equal(styles, light.Styles);
        Assert.Equal([0, 2, 2, 3], light.Starts());
    }

    /// <summary>
    /// The detail lighting section round-trips beside a lighting of one turn
    /// and of four, in each range; a room whose bake lit no detail prop has
    /// none; an absent or unread section, or an unlit room, leaves the
    /// lighting as it is; and a section that does not fit is refused: another
    /// prop count, another turn count or ranges than the room's lighting,
    /// counts that do not add up to the runs.
    /// </summary>
    [Fact]
    public void TheDetailLightingSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp(Prop(default, default), Prop(default, default));
        RoomDetailLight light = new(
            [new ColorRgbExp32 { R = 9, G = 8, B = 7, Exponent = -6 }, default], [1, 0], [Style(32)]);
        foreach (int turns in new[] { 1, 4 })
        {
            foreach ((bool ldr, bool hdr) in new[] { (true, false), (false, true), (true, true) })
            {
                RoomLighting bare = Lighting(bsp, turns, ldr, hdr, null);
                Assert.Null(RoomDetailLighting.ToSection(bare));
                Assert.False(RoomDetailLighting.Has(bare));
                RoomLighting lit = Lighting(bsp, turns, ldr, hdr, light);
                Assert.True(RoomDetailLighting.Has(lit));
                RoomLighting read = RoomDetailLighting.Read(bare, RoomDetailLighting.ToSection(lit)!.Value.Bytes.ToArray(), "r", bsp).Lighting!;
                Assert.All(read.Payloads, p =>
                {
                    foreach (RoomLightRange? range in new[] { p.Ldr, p.Hdr })
                    {
                        if (range is not null)
                        {
                            Assert.Equal(light.Colors, range.Detail!.Colors);
                            Assert.Equal(light.Counts, range.Detail.Counts);
                            Assert.Equal(light.Styles, range.Detail.Styles);
                        }
                    }
                });
            }
        }

        RoomLighting once = Lighting(bsp, 1, true, false, null);
        byte[] section = RoomDetailLighting.ToSection(Lighting(bsp, 1, true, false, light))!.Value.Bytes.ToArray();
        Assert.Same(once, RoomDetailLighting.Read(once, null, "r", bsp).Lighting);
        Assert.Null(RoomDetailLighting.Read(null, section, "r", bsp).Lighting);
        Assert.Same(once, RoomDetailLighting.Read(once, Payload(w => w.Int(RoomDetailLighting.Revision + 1)), "r", bsp).Lighting);

        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds the lighting of 2 detail props; the room has 1.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(once, section, "r", Bsp(Prop(default, default)))).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds 1 turns of detail prop lighting; the room's lighting holds 4.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(Lighting(bsp, 4, true, false, null), section, "r", bsp)).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds a range flag of 1; the room's lighting lit 3.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(Lighting(bsp, 1, true, true, null), section, "r", bsp)).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds 2 detail prop styles for runs of 1.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(once, Payload(w =>
            {
                w.Int(RoomDetailLighting.Revision);
                w.Int(2);
                w.Int(1);
                w.Byte(1);
                w.Structs<ColorRgbExp32>(new ColorRgbExp32[2], counted: false);
                w.Raw([1, 0]);
                w.Structs<DetailPropLightstylesLump>([Style(1), Style(2)]);
            }), "r", bsp)).Message);

        // A lighting with a lit range whose detail props were lit beside one
        // whose were not is not a bake's, and is not written.
        RoomLighting uneven = Lighting(bsp, 1, true, true, light);
        uneven = uneven.WithPayloads([uneven.Payloads[0] with { Hdr = uneven.Payloads[0].Hdr! with { Detail = null } }]);
        Assert.Throws<ArgumentException>(() => RoomDetailLighting.ToSection(uneven));
    }

    /// <summary>
    /// The section's door part round-trips: the detail receivers (centres,
    /// up vectors, and per socket which opening cells each prop sees) and
    /// each emitter's detail responses, attached to the room's door light
    /// read from its own section; a room without door light writes and
    /// attaches none; and a door part that does not fit the door light, or
    /// holds what no door light holds, is refused.
    /// </summary>
    [Fact]
    public void TheDoorPartRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp(Prop(default, default), Prop(default, default));
        RoomDetailLight light = new([default, default], [0, 0], []);
        RoomLighting lighting = Lighting(bsp, 1, true, false, light);
        DoorSeen seen = DoorSeen.Of([UInt128.One, UInt128.Zero]);
        DoorDetailReceivers receivers = new([new Vec3(1, 2, 3), new Vec3(4, 5, 6)], [new Vec3(0, 0, 1), new Vec3(0, 1, 0)], [seen, DoorSeen.Of([UInt128.Zero, UInt128.Zero])]);
        DoorResponseDetail reached = new(1, [(Half)0.25f, (Half)0.5f, (Half)0.75f]);
        RoomDoorLight door = Door([.. Enumerable.Range(0, DoorLightMath.EmitterCount).Select(e => new DoorResponseEmitter([], [], []) { Details = e == 3 ? [reached] : [] })], null, bsp);
        door = door.With(door.Ldr, null, receivers);

        byte[] section = RoomDetailLighting.ToSection(lighting, door)!.Value.Bytes.ToArray();
        (RoomLighting? read, DetailDoor? read2) = RoomDetailLighting.Read(Lighting(bsp, 1, true, false, null), section, "r", bsp);
        Assert.NotNull(read);
        DetailDoor part = read2!;
        RoomDoorLight bare = door.With(Strip(door.Ldr!), null, null);
        RoomDoorLight attached = RoomDetailLighting.AttachDoor(bare, part, "r")!;
        Assert.Equal(receivers.Centres, attached.Details!.Centres);
        Assert.Equal(receivers.Normals, attached.Details.Normals);
        Assert.Equal(receivers.Seen.Select(x => x.Masks()), attached.Details.Seen.Select(x => x.Masks()));
        Assert.Equal([1], attached.Ldr!.Responses[0][3].Details.Select(d => d.Prop));
        Assert.Equal(reached.Colour, attached.Ldr.Responses[0][3].Details[0].Colour);
        Assert.Empty(attached.Ldr.Responses[1]);
        Assert.Null(RoomDetailLighting.AttachDoor(null, part, "r"));

        (_, DetailDoor? none) = RoomDetailLighting.Read(Lighting(bsp, 1, true, false, null), RoomDetailLighting.ToSection(lighting)!.Value.Bytes.ToArray(), "r", bsp);
        Assert.Null(none);
        Assert.Same(bare, RoomDetailLighting.AttachDoor(bare, none, "r"));

        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds detail receivers for 2 sockets; the room's door light has 1.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.AttachDoor(Door([], null, bsp, sockets: 1), part, "r")).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds 0 detail response emitters at socket 0; the room's door light has 16.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.AttachDoor(bare, part with { Responses = [[[], []], null] }, "r")).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds detail responses for the HDR range, which the room's door light does not light otherwise.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.AttachDoor(bare, part with { Responses = [part.Responses[0], []] }, "r")).Message);

        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds 3 detail response emitters; a socket has none or 16.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(Lighting(bsp, 1, true, false, null), DoorPart(w => { w.Int(1); w.Int(3); }), "r", bsp)).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"DPLT\" section holds a response for detail prop 2; the room has 2.",
            Assert.Throws<LinkException>(() => RoomDetailLighting.Read(Lighting(bsp, 1, true, false, null), DoorPart(w =>
            {
                w.Int(1);
                w.Int(DoorLightMath.EmitterCount);
                w.Int(1);
                w.Int(2);
            }), "r", bsp)).Message);

        static DoorLightRange Strip(DoorLightRange range) =>
            range with { Responses = [.. range.Responses.Select(s => s.Select(e => e with { Details = [] }).ToArray())] };

        // A section of two props lit once in LDR, no runs, then a door part
        // without receivers whose LDR responses the writer gives.
        byte[] DoorPart(Action<RoomLinkSections.Writer> responses) => Payload(w =>
        {
            w.Int(RoomDetailLighting.Revision);
            w.Int(2);
            w.Int(1);
            w.Byte(1);
            w.Structs<ColorRgbExp32>(new ColorRgbExp32[2], counted: false);
            w.Raw([0, 0]);
            w.Int(0);
            w.Byte(1);
            w.Byte(0);
            responses(w);
        });
    }

    /// <summary>A door light of the given sockets, the first holding the given emitters and the others none, in LDR (and HDR when given).</summary>
    private static RoomDoorLight Door(DoorResponseEmitter[] first, DoorResponseEmitter[]? hdr, BspData bsp, int sockets = 2)
    {
        DoorResponseEmitter[][] Responses(DoorResponseEmitter[] emitters) => [emitters, .. Enumerable.Range(1, sockets - 1).Select(_ => Array.Empty<DoorResponseEmitter>())];
        DoorLightRange Range(DoorResponseEmitter[] emitters) => new([], [], Responses(emitters));
        return new RoomDoorLight([.. Enumerable.Range(0, sockets).Select(_ => Array.Empty<DoorReceiverFace>())], Range(first), hdr is null ? null : Range(hdr), bsp);
    }

    private static DetailPropLightstylesLump Style(byte style) =>
        new() { Lighting = new ColorRgbExp32 { R = style, G = 1, B = 2, Exponent = 0 }, Style = style };

    /// <summary>A lighting of the given turns and ranges, each range's detail lighting the one given.</summary>
    private static RoomLighting Lighting(BspData bsp, int turns, bool ldr, bool hdr, RoomDetailLight? detail)
    {
        RoomLightRange Range() => new([], [], [], [], [], [], [], []) { Detail = detail };
        RoomLightingPayload[] payloads = [.. Enumerable.Range(0, turns).Select(_ => new RoomLightingPayload(ldr ? Range() : null, hdr ? Range() : null))];
        return new RoomLighting(0, 0, [], [], false, [], 0, payloads, null, null, bsp);
    }

    private static DetailObjectLump Prop(Vec3 origin, Vec3 angles, byte type = 0, ushort model = 0, ushort leaf = 1)
    {
        DetailObjectLump prop = default;
        prop.Origin = origin;
        prop.Angles = angles;
        prop.Type = type;
        prop.DetailModel = model;
        prop.Leaf = leaf;
        return prop;
    }

    /// <summary>A room of three leaves whose detail prop lump holds the given props, with one model and one sprite.</summary>
    private static BspData Bsp(params DetailObjectLump[] props)
    {
        DetailPropLump lump = new();
        if (props.Length > 0)
        {
            lump.ModelNames.Add("models/a.mdl");
            lump.Sprites.Add(default);
        }

        lump.Props.AddRange(props);
        BspData bsp = new();
        bsp.SetLump(BspLump.Leafs, new byte[3 * Unsafe.SizeOf<DLeaf>()], 1);
        bsp.GameLumps.Add(lump.Write());
        return bsp;
    }

    private static BspData WithLump(byte[] bytes)
    {
        BspData bsp = Bsp();
        bsp.GameLumps[0] = bsp.GameLumps[0] with { Data = bytes };
        return bsp;
    }

    private static byte[] Payload(Action<RoomLinkSections.Writer> write)
    {
        RoomLinkSections.Writer w = new();
        write(w);
        return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
    }
}
