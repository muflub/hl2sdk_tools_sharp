//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's door visibility in the pack (<see cref="RoomDoorVisibility"/>,
/// the <c>DVIS</c> section): what the room compile stores is what the link
/// would work out, it round-trips under every codec and as four turned
/// payloads, a damaged section is refused naming the room, and packs of
/// version 3 (which have none) and version 4 (which promise it) are handled
/// as the pack's remarks say.
/// </summary>
public sealed class RoomDoorVisibilityTests(RoomLinkDataFixture fixture) : IClassFixture<RoomLinkDataFixture>
{
    /// <summary>What the room compile stores is what the link works out on the fly, for every room.</summary>
    [Fact]
    public async Task TheStoredDoorVisibilityIsWhatTheLinkWorksOut()
    {
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(room, CancellationToken.None))!;
            Assert.NotNull(data.Doors);
            Assert.True(data.Doors.SameAs(RoomDoorVisibility.Compute(room, LevelLinker.ComputeShared(room))), room.Definition.Name);
            Assert.Equal(room.ClusterCount, data.Doors.ClusterCount);
            Assert.Equal(room.Definition.Sockets.Count, data.Doors.SocketCount);
        }
    }

    /// <summary>
    /// The section decodes to what was written, under each codec, stored
    /// once or as four turned payloads; the four hold each turn's boxes
    /// turned exactly as the link would turn them.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 4)]
    [InlineData(2, 4)]
    public void TheSectionRoundTrips(int codecByte, int rotations)
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomDoorVisibility doors = RoomDoorVisibility.Compute(hub, LevelLinker.ComputeShared(hub));
        byte[] section = doors.ToSection((RoomLinkCodec)codecByte, rotations);
        Assert.Equal((byte)codecByte, section[0]);

        RoomDoorVisibility read = RoomDoorVisibility.Read(section, hub)!;
        Assert.True(doors.SameAs(read));
        Assert.Equal(rotations == 4, read.TurnedBoxes is not null);
        for (int turn = 0; turn < 4; turn++)
        {
            for (int c = 0; c < hub.ClusterCount; c++)
            {
                Box local = doors.ClusterBoxes[c];
                Assert.Equal(LevelLinker.RotateBox(local.Mins, local.Maxs, turn), read.TurnedBox(c, turn));
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => doors.ToSection(RoomLinkCodec.None, 2));
    }

    /// <summary>
    /// A level links to the same bytes whether its rooms' door visibility
    /// holds the boxes once (turned at link) or four times turned, and
    /// whether it was stored or worked out at link: the storage choice can
    /// change on measurement alone.
    /// </summary>
    [Fact]
    public async Task OnceAndFourTurnedPayloadsLinkToTheSameBytes()
    {
        byte[] computed = await LinkBytesAsync(fixture.Library);
        foreach (int rotations in new[] { 1, 4 })
        {
            List<RoomObject> rooms = [];
            foreach (RoomObject room in fixture.Library.Rooms)
            {
                RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(room, CancellationToken.None))!;
                RoomDoorVisibility read = RoomDoorVisibility.Read(data.Doors!.ToSection(RoomLinkCodec.None, rotations), room)!;
                rooms.Add(room with { Link = data.WithDoors(read) });
            }

            byte[] linked = await LinkBytesAsync(RoomHarness.Library([.. rooms]));
            Assert.True(computed.AsSpan().SequenceEqual(linked), $"{rotations} payload(s)");
        }
    }

    /// <summary>A section of a revision this build does not know reads as absent, and the link works the data out.</summary>
    [Fact]
    public void AnUnknownRevisionReadsAsAbsent()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkSections.Writer w = new();
        w.Int(RoomDoorVisibility.Revision + 1);
        w.Int(1);
        Assert.Null(RoomDoorVisibility.Read(RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None), hub));
        Assert.Null(RoomDoorVisibility.Read(null, hub));
    }

    /// <summary>A damaged section is refused, naming the room and what is wrong, rather than linked.</summary>
    [Theory]
    [InlineData("codec", "room hub: section DVIS uses codec 9, which this build does not read.")]
    [InlineData("length", "room hub: section DVIS decodes to")]
    [InlineData("rotations", "room pack entry \"hub\": its \"DVIS\" section holds 2 rotations; a section holds 1 or 4.")]
    [InlineData("clusters", "room pack entry \"hub\": its \"DVIS\" section holds 99 clusters; the room has")]
    [InlineData("sockets", "room pack entry \"hub\": its \"DVIS\" section holds 9 sockets; the room has 4.")]
    [InlineData("stray", "room pack entry \"hub\": its \"DVIS\" section holds socket 0 seen by a cluster past the room's")]
    [InlineData("self", "room pack entry \"hub\": its \"DVIS\" section holds socket 0 through itself.")]
    [InlineData("flag", "room pack entry \"hub\": its \"DVIS\" section holds a flag byte of 2.")]
    [InlineData("box", "room pack entry \"hub\": its \"DVIS\" section holds cluster 0's box with its mins past its maxs.")]
    [InlineData("tail", "room pack entry \"hub\": its \"DVIS\" section holds 1 bytes after its end.")]
    [InlineData("cut", "room pack entry \"hub\": its \"DVIS\" section is truncated.")]
    [InlineData("turns", "room pack entry \"hub\": its \"DVIS\" section holds turn 1's relations, which differ from turn 0's.")]
    public void ADamagedSectionIsRefused(string damage, string message)
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomDoorVisibility doors = RoomDoorVisibility.Compute(hub, LevelLinker.ComputeShared(hub));
        int clusters = hub.ClusterCount, sockets = hub.Definition.Sockets.Count;
        byte[] section = damage switch
        {
            "codec" => With(doors.ToSection(), 0, 9),
            "length" => WithLength(doors.ToSection(), 1),
            "rotations" => Payload(w => { w.Int(2); Body(w, doors, clusters, sockets); }),
            "clusters" => Payload(w => { w.Int(1); Body(w, doors, 99, sockets); }),
            "sockets" => Payload(w => { w.Int(1); Body(w, doors, clusters, 9); }),
            "stray" => Payload(w => { w.Int(1); Body(w, doors, clusters, sockets, stray: true); }),
            "self" => Payload(w => { w.Int(1); Body(w, doors, clusters, sockets, self: true); }),
            "flag" => Payload(w => { w.Int(1); Body(w, doors, clusters, sockets, flag: 2); }),
            "box" => Payload(w => { w.Int(1); Body(w, doors, clusters, sockets, inverted: true); }),
            "tail" => Payload(w => { w.Int(1); Body(w, doors, clusters, sockets); w.Byte(0); }),
            "cut" => Cut(doors.ToSection()),
            "turns" => Payload(w =>
            {
                w.Int(4);
                Body(w, doors, clusters, sockets);
                Body(w, doors, clusters, sockets, self: false, flipFirstSees: true);
                Body(w, doors, clusters, sockets);
                Body(w, doors, clusters, sockets);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        LinkException refused = Assert.Throws<LinkException>(() => RoomDoorVisibility.Read(section, hub));
        Assert.StartsWith(message, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version 3 pack, written before the door visibility, still links,
    /// and to the same bytes as the same rooms packed as version 4: the link
    /// works the door visibility out from what the pack does hold.
    /// </summary>
    [Fact]
    public async Task AVersionThreePackLinksToTheSameBytesAsVersionFour()
    {
        byte[] current = await PackAsync(dropDoors: false);
        byte[] old = await PackAsync(dropDoors: true);
        BinaryPrimitives.WriteInt32BigEndian(old.AsSpan(8), RoomPack.OldestReadVersion);

        RoomLibrary fromCurrent = await LoadAsync(current);
        RoomLibrary fromOld = await LoadAsync(old);
        Assert.All(fromCurrent.Rooms, r => Assert.NotNull(r.Link!.Doors));
        Assert.All(fromOld.Rooms, r => Assert.Null(r.Link!.Doors));
        byte[] current4 = await LinkBytesAsync(fromCurrent);
        byte[] old3 = await LinkBytesAsync(fromOld);
        byte[] computed = await LinkBytesAsync(fixture.Library);
        Assert.True(current4.AsSpan().SequenceEqual(old3));
        Assert.True(computed.AsSpan().SequenceEqual(old3));
    }

    /// <summary>
    /// A version 4 pack promises the door visibility of every room with link
    /// sections; one without it is a damaged pack, refused naming the room.
    /// </summary>
    [Fact]
    public async Task AVersionFourPackWithoutADoorVisibilityIsRefused()
    {
        byte[] pack = await PackAsync(dropDoors: true);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LoadAsync(pack));
        Assert.Equal(
            "room pack entry \"hub\" has link sections but no \"DVIS\" section, which every linkable room of a version 4 pack holds;"
            + " the pack is damaged, recompile the library with ssmap room.",
            refused.Message);
    }

    /// <summary>
    /// A room whose link data came from a version 3 pack (no door
    /// visibility) is packed with its door visibility worked out, so the
    /// version 4 pack it goes into keeps its promise.
    /// </summary>
    [Fact]
    public async Task APackItemFromLinkDataWithoutDoorsStillStoresThem()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        RoomLinkData withoutDoors = new(data.Definition, data.Bsp, data.Vis, data.Shared, [.. Enumerable.Range(0, 4).Select(data.Rotation)]);
        RoomPackItem item = await RoomPackItem.CreateAsync(hub with { Link = withoutDoors });
        RoomPackSectionData section = item.Extra.Single(s => s.Tag == RoomDoorVisibility.SectionTag);
        Assert.True(data.Doors!.SameAs(RoomDoorVisibility.Read(section.Bytes.ToArray(), hub)!));
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<byte[]> LinkBytesAsync(RoomLibrary library)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = 2 };
        LinkedLevel linked = await LevelLinker.LinkAsync(fixture.Layout, library, context);
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }

    /// <summary>The fixture's rooms as a pack, as <c>ssmap room</c> writes it, with or without the <c>DVIS</c> sections.</summary>
    private async Task<byte[]> PackAsync(bool dropDoors)
    {
        List<RoomPackItem> items = [];
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            RoomPackItem item = await RoomPackItem.CreateAsync(room);
            items.Add(dropDoors ? item with { Extra = [.. item.Extra.Where(s => s.Tag != RoomDoorVisibility.SectionTag)] } : item);
        }

        using MemoryStream pack = new();
        await RoomPack.SaveAsync(items, pack);
        return pack.ToArray();
    }

    private static async Task<RoomLibrary> LoadAsync(byte[] pack)
    {
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(stream, index, [.. index.Entries.Select(e => e.Name)]);
        return RoomHarness.Library([.. rooms]);
    }

    private static byte[] Payload(Action<RoomLinkSections.Writer> write)
    {
        RoomLinkSections.Writer w = new();
        w.Int(RoomDoorVisibility.Revision);
        write(w);
        return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
    }

    /// <summary>One payload as <see cref="RoomDoorVisibility.ToSection"/> writes it, with the damage asked for.</summary>
    private static void Body(
        RoomLinkSections.Writer w,
        RoomDoorVisibility doors,
        int clusters,
        int sockets,
        bool stray = false,
        bool self = false,
        byte flag = 1,
        bool inverted = false,
        bool flipFirstSees = false)
    {
        w.Int(clusters);
        w.Int(sockets);
        for (int s = 0; s < doors.SocketCount; s++)
        {
            for (int word = 0; word < doors.Words; word++)
            {
                ulong value = doors.Sees[s][word];
                if (s == 0 && word == doors.Words - 1 && stray)
                {
                    value |= 1UL << 63;
                }

                if (s == 0 && word == 0 && flipFirstSees)
                {
                    value ^= 1;
                }

                w.Int((int)(value >> 32));
                w.Int((int)value);
            }
        }

        for (int i = 0; i < doors.Through.Length; i++)
        {
            w.Byte(i == 0 && self ? (byte)1 : doors.Through[i] ? (byte)1 : (byte)0);
        }

        for (int c = 0; c < doors.ClusterCount; c++)
        {
            w.Byte(c == 0 && doors.HasBox[c] ? flag : doors.HasBox[c] ? (byte)1 : (byte)0);
            if (doors.HasBox[c])
            {
                Box box = doors.ClusterBoxes[c];
                w.Box(c == 0 && inverted ? new Box(box.Maxs + new Vec3(1, 1, 1), box.Mins) : box);
            }
        }
    }

    private static byte[] With(byte[] bytes, int offset, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[offset] = value;
        return copy;
    }

    private static byte[] WithLength(byte[] bytes, long delta)
    {
        byte[] copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(1), BinaryPrimitives.ReadInt64BigEndian(copy.AsSpan(1)) + delta);
        return copy;
    }

    /// <summary>The section cut short inside its payload, its recorded length cut to match.</summary>
    private static byte[] Cut(byte[] bytes)
    {
        byte[] copy = bytes[..^3];
        BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(1), copy.Length - 9);
        return copy;
    }
}
