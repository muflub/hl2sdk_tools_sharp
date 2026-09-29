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

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door light at pack time (<see cref="RoomDoorLight"/>): the room
/// opened at its doorways, what the bake records for a room with and
/// without lit props, its section and the damaged sections it refuses, the
/// pack that carries it, and the settings and cache key that switch it.
/// </summary>
public sealed class RoomDoorLightTests(LitRoomsFixture fixture) : IClassFixture<LitRoomsFixture>
{
    // ---- the open room ---------------------------------------------------------------------------

    /// <summary>
    /// The opened room keeps the compile's faces in order: its plug brushes
    /// cast no shadow (no contents), its plug faces point at copies of their
    /// texinfo and texdata that reflect nothing, and every other brush and
    /// face is the compile's.
    /// </summary>
    [Fact]
    public void OpeningARoomClearsItsPlugsAndDarkensTheirFaces()
    {
        RoomObject hub = fixture.Lit.Get("hub");
        RoomLinkShared census = LevelLinker.ComputeShared(hub);
        BspData open = RoomDoorLight.Open(hub, census);
        HashSet<int> plugBrushes = [.. census.Sockets.SelectMany(s => s.StrippedBrushes)];
        HashSet<int> plugFaces = [.. census.Sockets.SelectMany(s => s.StrippedFaces)];
        Assert.NotEmpty(plugBrushes);
        Assert.NotEmpty(plugFaces);

        DBrush[] before = BspStructView.As<DBrush>(hub.Bsp[BspLump.Brushes]).ToArray();
        DBrush[] after = BspStructView.As<DBrush>(open[BspLump.Brushes]).ToArray();
        for (int b = 0; b < before.Length; b++)
        {
            Assert.Equal(plugBrushes.Contains(b) ? 0 : before[b].Contents, after[b].Contents);
        }

        DFace[] faces = BspStructView.As<DFace>(hub.Bsp[BspLump.Faces]).ToArray();
        DFace[] opened = BspStructView.As<DFace>(open[BspLump.Faces]).ToArray();
        TexInfo[] texInfo = BspStructView.As<TexInfo>(open[BspLump.TexInfo]).ToArray();
        DTexData[] texData = BspStructView.As<DTexData>(open[BspLump.TexData]).ToArray();
        int oldTexInfos = BspStructView.Count<TexInfo>(hub.Bsp[BspLump.TexInfo]);
        Assert.Equal(faces.Length, opened.Length);
        for (int f = 0; f < faces.Length; f++)
        {
            if (plugFaces.Contains(f))
            {
                Assert.True(opened[f].TexInfo >= oldTexInfos);
                Assert.Equal(Vec3.Zero, texData[texInfo[opened[f].TexInfo].TexData].Reflectivity);
            }
            else
            {
                Assert.Equal(faces[f].TexInfo, opened[f].TexInfo);
            }
        }

        // The room's own compile is untouched.
        Assert.Equal(before.Length, BspStructView.Count<DBrush>(hub.Bsp[BspLump.Brushes]));
        Assert.DoesNotContain(plugBrushes, b => BspStructView.As<DBrush>(hub.Bsp[BspLump.Brushes])[b].Contents == 0);
    }

    // ---- what a bake records -----------------------------------------------------------------------

    /// <summary>
    /// A room's door light has receivers for every socket, and per stored
    /// turn of its base lighting the sources that leave by each socket and
    /// which cells each ambient sample sees; the hub, whose prop is lit,
    /// answers every socket with sixteen emitters that light the prop; the
    /// other room, which neither reflects nor holds a prop, answers with
    /// none. Its lamp leaves under its switchable style, the sun and the
    /// sky's stand-ins under style 0.
    /// </summary>
    [Fact]
    public async Task TheBakeRecordsWhatLeavesAndWhatAnswers()
    {
        RoomLibrary rooms = await fixture.DoorLitAsync();
        RoomObject hub = rooms.Get("hub"), other = rooms.Get("other");
        Assert.True(RoomDoorLight.NeedsResponses(hub, new RoomLightingSettings(LitRoomsFixture.Options)));
        Assert.False(RoomDoorLight.NeedsResponses(other, new RoomLightingSettings(LitRoomsFixture.Options)));
        Assert.False(RoomDoorLight.NeedsResponses(hub, new RoomLightingSettings(LitRoomsFixture.Options with { StaticPropLighting = false, Bounces = 0 })));

        foreach (RoomObject room in rooms.Rooms)
        {
            RoomDoorLight door = room.DoorLight!;
            Assert.True(door.IsFor(room));
            Assert.Null(door.Hdr);
            Assert.Same(door.Ldr, door.Range(false));
            Assert.Equal(room.Definition.Sockets.Count, door.Receivers.Length);
            Assert.All(door.Receivers, faces => Assert.NotEmpty(faces));
            DoorLightRange range = door.Ldr!;
            Assert.Equal(room.Lighting!.RotationCount, range.Captures.Length);
            Assert.Equal(room.Lighting.RotationCount, range.Ambient.Length);
            Assert.All(range.Captures, turn => Assert.Equal(room.Definition.Sockets.Count, turn.Length));
        }

        DoorLightRange hubRange = hub.DoorLight!.Ldr!;
        Assert.All(hubRange.Responses, socket =>
        {
            Assert.Equal(DoorLightMath.EmitterCount, socket.Length);
            Assert.All(socket, e => Assert.Single(e.Props));
        });
        Assert.All(other.DoorLight!.Ldr!.Responses, socket => Assert.Empty(socket));

        DoorSource[] leaving = other.DoorLight.Ldr!.Captures[0][0];
        Assert.Contains(leaving, s => s.Type == EmitType.Point && !s.StandIn && s.Style != 0);
        Assert.Contains(leaving, s => s.Type == EmitType.SkyLight && s.Style == 0);
        Assert.Contains(leaving, s => s.StandIn && s.Style == 0);
        Assert.All(leaving, s => Assert.NotEqual(UInt128.Zero, s.Cells));
    }

    // ---- the section ---------------------------------------------------------------------------------

    /// <summary>
    /// The section reads back to the same door light (written again, the
    /// same bytes), bound to the compile it is read with; a section of a
    /// later revision, or none, reads as no door light.
    /// </summary>
    [Theory]
    [InlineData("hub")]
    [InlineData("other")]
    public async Task TheSectionRoundTrips(string name)
    {
        RoomObject room = (await fixture.DoorLitAsync()).Get(name);
        byte[] section = room.DoorLight!.ToSection().Bytes.ToArray();
        Assert.Equal(RoomDoorLight.SectionTag, room.DoorLight.ToSection().Tag);
        Assert.Equal((byte)0, section[0]);
        Assert.Equal(section.Length - 9, BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)));
        Assert.Equal(RoomDoorLight.Revision, BinaryPrimitives.ReadInt32BigEndian(section.AsSpan(9)));

        RoomDoorLight read = RoomDoorLight.Read(section, room.Definition, room.Bsp, room.Lighting)!;
        Assert.Equal(section, read.ToSection().Bytes.ToArray());
        Assert.True(read.IsFor(room));
        Assert.False(read.IsFor(fixture.Lit.Get(name == "hub" ? "other" : "hub")));

        byte[] later = (byte[])section.Clone();
        BinaryPrimitives.WriteInt32BigEndian(later.AsSpan(9), RoomDoorLight.Revision + 1);
        Assert.Null(RoomDoorLight.Read(later, room.Definition, room.Bsp, room.Lighting));
        Assert.Null(RoomDoorLight.Read(null, room.Definition, room.Bsp, room.Lighting));
    }

    /// <summary>
    /// A section that does not fit its room is refused naming the room and
    /// the section: no base lighting, another socket count, a receiving face
    /// the room does not light, receiver codes that do not match the face's
    /// cells or are not 0, 1 or 2, an empty or oversized partial mask, other
    /// ranges or turns than the lighting's, a source of an unknown type or
    /// reaching a cell the opening lacks, a response count other than none or
    /// sixteen, and responses naming a face, leaf or prop the room lacks;
    /// and a truncated one.
    /// </summary>
    [Fact]
    public async Task ADamagedSectionIsRefusedByName()
    {
        RoomObject hub = (await fixture.DoorLitAsync()).Get("hub");
        RoomDoorLight door = hub.DoorLight!;
        DoorLightRange range = door.Ldr!;
        DoorReceiverFace first = door.Receivers[0][0];

        Assert.Contains("door light for a room with no base lighting", Refused(door, lighting: false));
        int sockets = door.Receivers.Length;
        Assert.Contains($"{sockets - 1} sockets; the room has {sockets}", Refused(With(receivers: door.Receivers[..^1])));
        Assert.Contains("receiving face 99999, which the room does not light", Refused(With(receivers: Receivers(first with { Face = 99999 }))));
        Assert.Contains("cells of a face", Refused(With(receivers: Receivers(first with { Seen = new DoorSeen([.. first.Seen.Codes, 0], first.Seen.Partial) }))));
        Assert.Contains("a receiver code of 3", Refused(With(receivers: Receivers(first with { Seen = new DoorSeen([3, .. first.Seen.Codes[1..]], first.Seen.Partial) }))));
        Assert.Contains("a partial mask naming no cell", Refused(With(receivers: Receivers(first with { Seen = Partial(first.Seen, UInt128.Zero) }))));
        Assert.Contains("a partial mask naming no cell or a cell the opening does not have", Refused(With(receivers: Receivers(first with { Seen = Partial(first.Seen, UInt128.MaxValue) }))));
        Assert.Contains("ranges 3; the room's lighting has others", Refused(With(hdr: range)));
        Assert.Contains("4 stored turns; the room's lighting stores 1", Refused(With(ldr: range with { Captures = [.. range.Captures, .. range.Captures, .. range.Captures, .. range.Captures], Ambient = [.. range.Ambient, .. range.Ambient, .. range.Ambient, .. range.Ambient] })));

        DoorSource source = range.Captures[0][0][0];
        Assert.Contains("a source of type 99", Refused(With(ldr: Sources(source with { Type = (EmitType)99 }))));
        Assert.Contains("a source reaching a cell the opening does not have", Refused(With(ldr: Sources(source with { Cells = UInt128.MaxValue }))));

        DoorResponseEmitter emitter = range.Responses[0][0];
        Assert.Contains("3 response emitters; a socket has none or 16", Refused(With(ldr: range with { Responses = [range.Responses[0][..3], .. range.Responses[1..]] })));
        Assert.Contains("response face 99999; the room has", Refused(With(ldr: Emitter(emitter with { Faces = [new DoorResponseFace(99999, [])] }))));
        Assert.Contains("a response sample in leaf 99999; the room has", Refused(With(ldr: Emitter(emitter with { Ambient = [new DoorResponseSample(99999, Vec3.Zero, new Half[RoomLighting.AmbientHalves])] }))));
        Assert.Contains("a response for prop 77, which the room's lighting did not light", Refused(With(ldr: Emitter(emitter with { Props = [new DoorResponseProp(77, [])] }))));

        byte[] section = door.ToSection().Bytes.ToArray();
        byte[] cut = section[..(section.Length - 10)];
        BinaryPrimitives.WriteInt64BigEndian(cut.AsSpan(1), cut.Length - 9);
        Assert.Equal("room pack entry \"hub\": its \"DLIT\" section is truncated.", Assert.Throws<LinkException>(() => RoomDoorLight.Read(cut, hub.Definition, hub.Bsp, hub.Lighting)).Message);

        RoomDoorLight With(DoorReceiverFace[][]? receivers = null, DoorLightRange? ldr = null, DoorLightRange? hdr = null) =>
            new(receivers ?? door.Receivers, ldr ?? range, hdr, hub.Bsp);

        DoorReceiverFace[][] Receivers(DoorReceiverFace face) => [[face, .. door.Receivers[0][1..]], .. door.Receivers[1..]];

        DoorLightRange Sources(DoorSource replaced) =>
            range with { Captures = [[[replaced, .. range.Captures[0][0][1..]], .. range.Captures[0][1..]]] };

        DoorLightRange Emitter(DoorResponseEmitter replaced) =>
            range with { Responses = [[replaced, .. range.Responses[0][1..]], .. range.Responses[1..]] };

        string Refused(RoomDoorLight damaged, bool lighting = true) => Assert.Throws<LinkException>(
            () => RoomDoorLight.Read(damaged.ToSection().Bytes.ToArray(), hub.Definition, hub.Bsp, lighting ? hub.Lighting : null)).Message;

        static DoorSeen Partial(DoorSeen seen, UInt128 mask)
        {
            byte[] codes = (byte[])seen.Codes.Clone();
            codes[0] = 2;
            List<UInt128> partial = [.. seen.Partial];
            partial.Insert(0, mask);
            if (seen.Codes[0] == 2)
            {
                partial.RemoveAt(1);
            }

            return new DoorSeen(codes, [.. partial]);
        }
    }

    // ---- the pack ------------------------------------------------------------------------------------

    /// <summary>
    /// A room lit with its door light has the section right after its base
    /// lighting in its pack entry, and the pack's reader attaches it, bound
    /// to the container's compile; a room lit without it has none and loads
    /// without it.
    /// </summary>
    [Fact]
    public async Task ThePackCarriesTheDoorLightSection()
    {
        RoomLibrary rooms = await fixture.DoorLitAsync();
        RoomPackItem hub = await RoomPackItem.CreateAsync(rooms.Get("hub"));
        RoomPackItem other = await RoomPackItem.CreateAsync(rooms.Get("other"));
        RoomPackItem baseOnly = await RoomPackItem.CreateAsync(fixture.Lit.Get("hub"));
        Assert.Equal(["LITE", "DLIT"], hub.Extra.SkipWhile(s => s.Tag != RoomLighting.SectionTag).Take(2).Select(s => s.Tag));
        Assert.DoesNotContain(baseOnly.Extra, s => s.Tag == RoomDoorLight.SectionTag);

        using MemoryStream pack = new();
        await RoomPack.SaveAsync([hub, other], pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index, ["other", "hub"]);
        Assert.Equal(rooms.Get("other").DoorLight!.ToSection().Bytes.ToArray(), loaded[0].DoorLightOfCompile!.ToSection().Bytes.ToArray());
        Assert.Equal(rooms.Get("hub").DoorLight!.ToSection().Bytes.ToArray(), loaded[1].DoorLightOfCompile!.ToSection().Bytes.ToArray());

        using MemoryStream plain = new();
        await RoomPack.SaveAsync([baseOnly], plain);
        plain.Position = 0;
        RoomPackIndex plainIndex = await RoomPack.ReadIndexAsync(plain);
        Assert.Null((await RoomPack.LoadRoomsAsync(plain, plainIndex, ["hub"]))[0].DoorLight);
    }

    // ---- the switch ----------------------------------------------------------------------------------

    /// <summary>
    /// The settings describe the door light, with its revision, when it is
    /// on (the default), so rooms lit with and without it have different
    /// cache keys and pack identities and a level mixing them is refused as
    /// lit differently.
    /// </summary>
    [Fact]
    public void TheSettingsAndTheCacheKeyCarryTheDoorLight()
    {
        RoomLightingSettings on = new(VradOptions.Default);
        RoomLightingSettings off = new(VradOptions.Default) { DoorLight = false };
        Assert.True(on.DoorLight);
        Assert.Contains($"|door:{RoomDoorLight.Revision}", on.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("|door:", off.Describe(), StringComparison.Ordinal);

        RoomCacheInputs unlit = new(VbspOptions.Default);
        Assert.NotEqual(
            RoomCacheKey.OptionsDigestOf(unlit with { Lighting = on }),
            RoomCacheKey.OptionsDigestOf(unlit with { Lighting = off }));
    }
}
