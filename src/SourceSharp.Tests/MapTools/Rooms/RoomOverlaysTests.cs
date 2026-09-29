//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The overlay transforms and the overlay section on their own (the rooms
/// design, 4.9 and 15.2's overlays row): the basis transform, the packed
/// <c>BasisU</c>, the record rebase, the accessor's keys, and the section's
/// refusals.
/// </summary>
public sealed class RoomOverlaysTests
{
    /// <summary>
    /// A pose turned by quarter turns: the origin and both directions turned
    /// about +z, the directions' zeros unsigned as the flatten writes them;
    /// turn 0 is the pose itself, a negative zero kept, as the flatten
    /// leaves an unturned overlay's keys as written.
    /// </summary>
    [Fact]
    public void APoseTurnsItsOriginAndBasis()
    {
        RoomOverlayPose pose = new(new Vec3(10, 20, 30), new Vec3(1, -0f, 0), new Vec3(0, -0f, 1));
        Assert.Equal(pose, RoomOverlays.Turn(pose, 0));
        Assert.True(float.IsNegative(RoomOverlays.Turn(pose, 0).BasisU.Y));

        RoomOverlayPose quarter = RoomOverlays.Turn(pose, 1);
        Assert.Equal(new Vec3(-20, 10, 30), quarter.Origin);
        Assert.Equal(new Vec3(0, 1, 0), quarter.BasisU);
        Assert.False(float.IsNegative(quarter.BasisU.X));
        Assert.Equal(new Vec3(0, 0, 1), quarter.BasisNormal);
        Assert.False(float.IsNegative(quarter.BasisNormal.X));

        Assert.Equal(new Vec3(-10, -20, 30), RoomOverlays.Turn(pose, 2).Origin);
        Assert.Equal(new Vec3(-1, 0, 0), RoomOverlays.Turn(pose, 2).BasisU);
        Assert.Equal(new Vec3(20, -10, 30), RoomOverlays.Turn(pose, 3).Origin);
        Assert.Equal(new Vec3(0, -1, 0), RoomOverlays.Turn(pose, 3).BasisU);
    }

    /// <summary>A record's pose reads <c>BasisU</c> from the <c>z</c> of its first three UV points.</summary>
    [Fact]
    public void ARecordsPoseReadsTheBasisFromItsUvPoints()
    {
        DOverlay record = Record();
        RoomOverlayPose pose = RoomOverlays.PoseOf(record);
        Assert.Equal(new Vec3(64, 32, 16), pose.Origin);
        Assert.Equal(new Vec3(0, 1, 0), pose.BasisU);
        Assert.Equal(new Vec3(0, 0, 1), pose.BasisNormal);
    }

    /// <summary>
    /// A record linked: the id and texinfo rebased, the origin the turned
    /// point plus the translation with its zeros unsigned, the basis the
    /// pose's (<c>BasisU</c> back in the UV points' <c>z</c>), the UV points'
    /// <c>x</c> and <c>y</c>, the handedness flag, the extents and the render
    /// order the room's, and the faces the linked ones in order, a face the
    /// level does not draw left out.
    /// </summary>
    [Fact]
    public void ARecordIsRebasedMovedAndTurned()
    {
        DOverlay record = Record(2);
        RoomOverlayPose pose = RoomOverlays.Turn(RoomOverlays.PoseOf(record), 1);
        DOverlay linked = LevelLinker.LinkOverlay(record, pose, new Vec3(512, 0, -0f), 5, 9, f => f == 4 ? -1 : f + 100);

        Assert.Equal(7, linked.Id);
        Assert.Equal(9, linked.TexInfo);
        Assert.Equal(new Vec3(480, 64, 16), linked.Origin);
        Assert.Equal(new Vec3(-1, 0, 0), new Vec3(linked.UvPoints[0].Z, linked.UvPoints[1].Z, linked.UvPoints[2].Z));
        Assert.Equal(new Vec3(0, 0, 1), linked.BasisNormal);
        for (int p = 0; p < 4; p++)
        {
            Assert.Equal(record.UvPoints[p].X, linked.UvPoints[p].X);
            Assert.Equal(record.UvPoints[p].Y, linked.UvPoints[p].Y);
        }

        Assert.Equal(1f, linked.UvPoints[3].Z);
        Assert.Equal(record.U[1], linked.U[1]);
        Assert.Equal(record.V[1], linked.V[1]);
        Assert.Equal(2, linked.GetRenderOrder());
        Assert.Equal(2, linked.GetFaceCount());
        Assert.Equal(103, linked.Faces[0]);
        Assert.Equal(105, linked.Faces[1]);
        Assert.Equal(0, linked.Faces[2]);

        // A zero the translation leaves on the origin is written unsigned.
        DOverlay atZero = LevelLinker.LinkOverlay(
            record, pose with { Origin = new Vec3(-0f, 0, -0f) }, new Vec3(-0f, 0, -0f), 0, 9, f => f);
        Assert.False(float.IsNegative(atZero.Origin.X));
        Assert.False(float.IsNegative(atZero.Origin.Z));
    }

    /// <summary>
    /// A named overlay's accessor moved with its room: <c>BasisOrigin</c>
    /// moved as a point, <c>BasisU</c>, <c>BasisV</c> and <c>BasisNormal</c>
    /// turned and written as the flatten writes them, <c>OverlayID</c> taken
    /// past the placement's overlay base, every other key as written; at turn
    /// 0 the directions are left as written. Another class's keys of those
    /// names are not an overlay's and are left alone, and an id that is not a
    /// number, or a basis origin that is not three, is refused naming the room.
    /// </summary>
    [Fact]
    public void AnAccessorsKeysMoveWithItsRoom()
    {
        RoomTransform turned = new(new RoomPlacement("r", 1, 0, 1), 256);
        BspEntity accessor = Entity(
            ("classname", RoomOverlays.AccessorClass), ("OverlayID", "1"), ("BasisOrigin", "64 32 16"),
            ("BasisU", "1 0 0"), ("BasisV", "0 1 0"), ("BasisNormal", "0 0 1"), ("uv0", "-16 -16 0"), ("sides", "12"));
        BspEntity moved = LevelLinker.MoveEntity(accessor, turned, "r", overlayBase: 4);
        Assert.Equal("5", moved.Get("OverlayID"));
        Assert.Equal("480 64 16", moved.Get("BasisOrigin"));
        Assert.Equal("0 1 0", moved.Get("BasisU"));
        Assert.Equal("-1 0 0", moved.Get("BasisV"));
        Assert.Equal("0 0 1", moved.Get("BasisNormal"));
        Assert.Equal("-16 -16 0", moved.Get("uv0"));
        Assert.Equal("12", moved.Get("sides"));
        Assert.Equal([.. accessor.Pairs.Select(p => p.Key)], moved.Pairs.Select(p => p.Key));

        RoomTransform unturned = new(new RoomPlacement("r", 1, 0, 0), 256);
        BspEntity signed = Entity(("classname", RoomOverlays.AccessorClass), ("OverlayID", "0"), ("BasisU", "1 -0 0"), ("BasisOrigin", "1 2 3"));
        BspEntity kept = LevelLinker.MoveEntity(signed, unturned, "r");
        Assert.Equal("1 -0 0", kept.Get("BasisU"));
        Assert.Equal("0", kept.Get("OverlayID"));
        Assert.Equal("257 2 3", kept.Get("BasisOrigin"));

        BspEntity other = Entity(("classname", "info_target"), ("OverlayID", "1"), ("BasisOrigin", "64 32 16"), ("BasisU", "1 0 0"));
        BspEntity left = LevelLinker.MoveEntity(other, turned, "r");
        Assert.Equal("64 32 16", left.Get("BasisOrigin"));
        Assert.Equal("1 0 0", left.Get("BasisU"));

        BspEntity broken = Entity(("classname", RoomOverlays.AccessorClass), ("OverlayID", "first"));
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(broken, turned, "attic", overlayBase: 2));
        Assert.Equal("room attic has an entity whose \"OverlayID\" holds \"first\", not an overlay id", refused.Message);

        BspEntity unplaced = Entity(("classname", RoomOverlays.AccessorClass), ("BasisOrigin", "here"));
        Assert.Equal(
            "room attic has an entity whose \"BasisOrigin\" is \"here\", not three numbers",
            Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(unplaced, turned, "attic")).Message);
    }

    /// <summary>
    /// The overlay section reads back what it wrote, at four turns and at
    /// one (turn 0, the link turning it, to the same poses); a room's section
    /// is refused as damaged when it does not fit the room's lump, holds a
    /// turn count other than 1 or 4, or has bytes after its end; an absent
    /// section, or one of a revision this build does not read, is none.
    /// </summary>
    [Fact]
    public void TheSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        BspData bsp = Bsp(Record(), Record(1));
        RoomOverlays built = RoomOverlays.Build("r", bsp)!;
        Assert.Equal(2, built.Count);
        Assert.Equal(4, built.TurnCount);
        Assert.Null(RoomOverlays.Build("r", new BspData()));

        RoomOverlays read = RoomOverlays.Read(Payload(built.ToSection()), "r", bsp)!;
        Assert.Equal(4, read.TurnCount);
        RoomOverlays once = RoomOverlays.Read(Payload(built.WithTurnZeroOnly().ToSection()), "r", bsp)!;
        Assert.Equal(1, once.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(built.Poses(turn), read.Poses(turn));
            Assert.Equal(built.Poses(turn), once.Poses(turn));
        }

        Assert.Null(RoomOverlays.Read(null, "r", bsp));
        byte[] revised = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(revised.AsSpan(9), 99);
        Assert.Null(RoomOverlays.Read(revised, "r", bsp));

        Assert.Equal(
            "room pack entry \"r\": its \"OVLY\" section holds 2 overlays; the room has 1.",
            Assert.Throws<LinkException>(() => RoomOverlays.Read(Payload(built.ToSection()), "r", Bsp(Record()))).Message);
        Assert.Equal(
            "room pack entry \"r\": its \"OVLY\" section holds 2 overlays; the room has 0.",
            Assert.Throws<LinkException>(() => RoomOverlays.Read(Payload(built.ToSection()), "r", new BspData())).Message);

        byte[] twoTurns = Payload(built.ToSection()).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(twoTurns.AsSpan(9 + 8), 2);
        Assert.Equal(
            "room pack entry \"r\": its \"OVLY\" section holds 2 turns of overlays; a section holds 1 or 4.",
            Assert.Throws<LinkException>(() => RoomOverlays.Read(twoTurns, "r", bsp)).Message);

        byte[] section = Payload(built.ToSection()).ToArray();
        byte[] longer = [.. section, 0];
        BinaryPrimitives.WriteInt64BigEndian(longer.AsSpan(1), BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)) + 1);
        Assert.Equal(
            "room pack entry \"r\": its \"OVLY\" section holds 1 bytes after its end.",
            Assert.Throws<LinkException>(() => RoomOverlays.Read(longer, "r", bsp)).Message);
    }

    /// <summary>A record as vbsp writes one: faces 3, 4 and 5, render order 2, <c>BasisU</c> +y, left-handed.</summary>
    private static DOverlay Record(int id = 0)
    {
        DOverlay record = default;
        record.Id = id;
        record.TexInfo = 3;
        record.Origin = new Vec3(64, 32, 16);
        record.BasisNormal = new Vec3(0, 0, 1);
        record.U[0] = 0;
        record.U[1] = 1;
        record.V[0] = 0;
        record.V[1] = 0.5f;
        record.UvPoints[0] = new Vec3(-16, -16, 0);
        record.UvPoints[1] = new Vec3(-16, 16, 1);
        record.UvPoints[2] = new Vec3(16, 16, 0);
        record.UvPoints[3] = new Vec3(16, -16, 1);
        record.Faces[0] = 3;
        record.Faces[1] = 4;
        record.Faces[2] = 5;
        record.FaceCountAndRenderOrder = (ushort)((2 << 14) | 3);
        return record;
    }

    private static BspData Bsp(params DOverlay[] records)
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Overlays, System.Runtime.InteropServices.MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        bsp.SetLump(BspLump.OverlayFades, new byte[records.Length * 8]);
        return bsp;
    }

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
