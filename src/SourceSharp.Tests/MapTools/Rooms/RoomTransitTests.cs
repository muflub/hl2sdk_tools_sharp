//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.TransitHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's transition data read from its VMF (the rooms design, 11.3):
/// the role, the transition volume, the hallway fold and its conditions, the
/// arrival and spawn points; every refusal with its 15.4 text; and the
/// section's bytes.
/// </summary>
public sealed class RoomTransitTests
{
    private static RoomTransit? Read(RoomRole role, params VmfChunk[] entities)
    {
        VmfDocument room = RoomHarness.BuildRoomModel(Up);
        foreach (VmfChunk entity in entities)
        {
            room.Chunks.Add(entity);
        }

        return RoomTransit.FromVmf(Up, role, room);
    }

    private static string Refusal(RoomRole role, params VmfChunk[] entities) =>
        Assert.Throws<RoomLintException>(() => Read(role, entities)).Message;

    /// <summary>
    /// The up room's data: its role, the volume's id and centre, no fold (a
    /// button fires it), the arrival and the spawn point, room-local.
    /// </summary>
    [Fact]
    public void AnUpRoomsDataIsReadFromItsVmf()
    {
        RoomTransit transit = Read(RoomRole.Up, UpEntities)!;
        Assert.Equal((RoomRole.Up, 103, new Vec3(128, 128, 48), -1), (transit.Role, transit.VolumeId, transit.VolumeCentre, transit.FoldId));
        Assert.Equal(new TransitPoint(UpArrival, 0), transit.Arrival);
        Assert.Equal([new TransitPoint(UpSpawn, 270)], transit.Spawns);
    }

    /// <summary>
    /// The down room's hallway folds: a <c>trigger_once</c> whose only output
    /// is the transition, with no filter, around the volume, and the only
    /// thing firing it; the fold's centre is the trigger's.
    /// </summary>
    [Fact]
    public void AHallwayAroundTheVolumeFolds()
    {
        RoomTransit transit = Read(RoomRole.Down, DownEntities)!;
        Assert.Equal((201, RoomTransit.Centre(Hallway)), (transit.FoldId, transit.FoldCentre));
    }

    /// <summary>Every condition of the fold, broken one at a time: the hallway then stays a caller and nothing folds.</summary>
    [Theory]
    [InlineData("filter")]
    [InlineData("second output")]
    [InlineData("not around")]
    [InlineData("second caller")]
    [InlineData("not a trigger_once")]
    public void AHallwayFoldsOnlyWhenEveryConditionHolds(string broken)
    {
        (string, string)[] keys = broken == "filter" ? [("filtername", "players")] : [];
        (string, string)[] outputs = broken == "second output"
            ? [("OnStartTouch", Out(RoomTransit.VolumeName, "Transition")), ("OnStartTouch", Out("lamp", "TurnOn"))]
            : [("OnStartTouch", Out(RoomTransit.VolumeName, "Transition"))];
        Box box = broken == "not around" ? new Box(new Vec3(96, 96, 16), new Vec3(160, 160, 64)) : Hallway;
        string classname = broken == "not a trigger_once" ? "trigger_multiple" : "trigger_once";
        List<VmfChunk> entities = [Poi(200, "arrival", DownArrival, 90), Brush(classname, 201, box, keys, outputs), VolumeEntity(202)];
        if (broken == "second caller")
        {
            entities.Add(Brush("func_button", 203, new Box(new Vec3(200, 40, 16), new Vec3(216, 56, 64)), [],
                ("OnPressed", Out(RoomTransit.VolumeName, "Transition"))));
        }

        Assert.Equal(-1, Read(RoomRole.Down, [.. entities])!.FoldId);
    }

    /// <summary>
    /// An ordinary room has data only when it has spawn points, and then only
    /// those; a room with neither a role nor spawn points has none, so its
    /// pack entry is what it was.
    /// </summary>
    [Fact]
    public void AnOrdinaryRoomHasDataOnlyForItsSpawnPoints()
    {
        Assert.Null(Read(RoomRole.None, Point("info_player_start", 300, new Vec3(128, 64, 16))));
        RoomTransit plain = Read(RoomRole.None, PlainEntities)!;
        Assert.Equal((RoomRole.None, -1, (TransitPoint?)null), (plain.Role, plain.VolumeId, plain.Arrival));
        Assert.Equal([new TransitPoint(PlainSpawn, 45)], plain.Spawns);
    }

    /// <summary>The refusals of 11.3, each with its text.</summary>
    [Fact]
    public void ARoleRoomsRulesAreRefusedWithTheirText()
    {
        VmfChunk arrival = Poi(100, "arrival", UpArrival, 0);
        VmfChunk button = UpEntities[2];
        Assert.Equal(
            "room up: an up room needs exactly one trigger_room_transition named cxry_transition; it has 0.",
            Refusal(RoomRole.Up, arrival, button));
        Assert.Equal(
            "room up: a down room needs exactly one trigger_room_transition named cxry_transition; it has 2.",
            Refusal(RoomRole.Down, arrival, button, VolumeEntity(103), VolumeEntity(104)));
        Assert.Equal(
            "room up: an up room needs exactly one arrival point; it has 0.",
            Refusal(RoomRole.Up, button, VolumeEntity(103)));
        Assert.Equal(
            "room up: an up room needs exactly one arrival point; it has 2.",
            Refusal(RoomRole.Up, arrival, Poi(105, "arrival", UpSpawn, 0), button, VolumeEntity(103)));
        Assert.Equal("room up: nothing fires Transition at cxry_transition.", Refusal(RoomRole.Up, arrival, VolumeEntity(103)));
    }

    /// <summary>The refusals this build adds to 11.3's: a volume named otherwise, one without brushes, one in a room without a role.</summary>
    [Fact]
    public void AVolumeOutOfPlaceIsRefused()
    {
        VmfChunk arrival = Poi(100, "arrival", UpArrival, 0);
        VmfChunk misnamed = Brush(RoomTransit.VolumeClass, 103, Volume, [("targetname", "exit")]);
        Assert.Equal(
            "room up: trigger_room_transition 103 is named \"exit\"; the transition volume is named cxry_transition.",
            Refusal(RoomRole.Up, arrival, misnamed));
        VmfChunk point = Point(RoomTransit.VolumeClass, 103, new Vec3(128, 128, 48), ("targetname", RoomTransit.VolumeName));
        Assert.Equal(
            "room up: trigger_room_transition 103 has no brushes; the transition volume is a brush entity.",
            Refusal(RoomRole.Up, arrival, UpEntities[2], point));
        Assert.Equal(
            "room up: trigger_room_transition 103 is in a room without a room_role; only an up or down room has a transition volume.",
            Refusal(RoomRole.None, VolumeEntity(103)));
    }

    /// <summary>The input matches whatever its case, as the engine matches inputs; the target is the authored name exactly.</summary>
    [Theory]
    [InlineData("cxry_transition", "transition", true)]
    [InlineData("cxry_transition", "Transition", true)]
    [InlineData("cxry_transition", "ChangeLevel", false)]
    [InlineData("cx+1ry_transition", "Transition", false)]
    public void AnOutputFiresTheTransitionByItsAuthoredName(string target, string input, bool fires)
    {
        Assert.True(RoomOutput.TryParse(Out(target, input), out RoomOutput output));
        Assert.Equal(fires, RoomTransit.FiresTransition(output));
    }

    /// <summary>
    /// The section's bytes read back to the same data, bound to the compile
    /// they are read for; an unknown revision reads as absent and a damaged
    /// section is refused.
    /// </summary>
    [Fact]
    public void TheSectionRoundTrips()
    {
        BspData bsp = new();
        RoomTransit transit = Read(RoomRole.Down, [.. DownEntities, Poi(203, "spawn", new Vec3(40, 200, 16), 12.5f)])!;
        RoomPackSectionData section = transit.ToSection();
        Assert.Equal(RoomTransit.SectionTag, section.Tag);
        byte[] bytes = section.Bytes.ToArray();
        RoomTransit read = RoomTransit.Read(bytes, "down", bsp)!;
        Assert.Equal(
            (transit.Role, transit.VolumeId, transit.VolumeCentre, transit.FoldId, transit.FoldCentre, transit.Arrival),
            (read.Role, read.VolumeId, read.VolumeCentre, read.FoldId, read.FoldCentre, read.Arrival));
        Assert.Equal(transit.Spawns, read.Spawns);
        Assert.Null(RoomTransit.Read(null, "down", bsp));

        byte[] other = [.. bytes];
        other[9 + 3] = 99; // the revision's low byte, after the codec byte and the length
        Assert.Null(RoomTransit.Read(other, "down", bsp));

        byte[] damaged = [.. bytes];
        damaged[9 + 4] = 7; // the role
        Assert.Equal(
            "room pack entry \"down\": its \"TRAN\" section holds a role of 7.",
            Assert.Throws<LinkException>(() => RoomTransit.Read(damaged, "down", bsp)).Message);
    }

    /// <summary>A section with bytes after its end is refused as damaged.</summary>
    [Fact]
    public void ASectionWithTrailingBytesIsRefused()
    {
        RoomLinkSections.Writer w = new();
        w.Int(RoomTransit.Revision);
        w.Byte(0);
        w.Int(-1);
        w.Structs<Vec3>([default], counted: false);
        w.Int(-1);
        w.Structs<Vec3>([default], counted: false);
        w.Byte(0);
        w.Structs<Vec3>([default], counted: false);
        w.Structs<float>([0f], counted: false);
        w.Int(0);
        w.Byte(9);
        byte[] bytes = RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
        Assert.Equal(
            "room pack entry \"r\": its \"TRAN\" section holds 1 bytes after its end.",
            Assert.Throws<LinkException>(() => RoomTransit.Read(bytes, "r", new BspData())).Message);
    }
}
