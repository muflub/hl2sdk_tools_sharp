//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>Three compiled rooms' containers, built once for the pack facts.</summary>
public sealed class RoomPackFixture : IAsyncLifetime
{
    /// <summary>The rooms' names, in the order the facts pack them.</summary>
    public IReadOnlyList<string> Names { get; } = ["hub", "end", "hall"];

    /// <summary>Each room's <c>.room</c> container, index for index with <see cref="Names"/>.</summary>
    public IReadOnlyList<byte[]> Containers { get; private set; } = [];

    /// <summary>The rooms as pack items.</summary>
    public IReadOnlyList<RoomPackItem> Items => [.. Names.Select((n, i) => new RoomPackItem(n, Containers[i]))];

    public async Task InitializeAsync()
    {
        RoomDefinition[] definitions =
        [
            RoomHarness.Hub(),
            RoomHarness.Room("end", RoomFacing.PositiveX),
            RoomHarness.Room("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        ];
        List<byte[]> containers = [];
        foreach (RoomDefinition definition in definitions)
        {
            RoomObject room = await RoomCompiler.CompileAsync(
                RoomHarness.BuildRoomModel(definition), definition, await RoomHarness.ContextAsync());
            using MemoryStream stream = new();
            await RoomObjectStore.SaveAsync(room, stream);
            containers.Add(stream.ToArray());
        }

        Containers = containers;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The <c>.roompack</c> format: it round-trips, a lookup reads only the room
/// asked for, it is a function of its rooms, it reads around sections a later
/// build adds, and every malformed pack is refused with a message that says
/// what is wrong.
/// </summary>
public sealed class RoomPackTests(RoomPackFixture fixture) : IClassFixture<RoomPackFixture>
{
    // ---- round trip ----------------------------------------------------------

    /// <summary>
    /// A pack reads back as the rooms written, in the order written: the
    /// index lists them, each container is the bytes given, and each loads
    /// as the room it was compiled from.
    /// </summary>
    [Fact]
    public async Task APackRoundTrips()
    {
        byte[] pack = await SaveAsync(fixture.Items);

        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(fixture.Names, index.Entries.Select(e => e.Name));
        Assert.Empty(index.LibrarySections);
        Assert.All(index.Entries, e => Assert.Equal([RoomPack.RoomSection], e.Sections.Select(s => s.Tag)));

        IReadOnlyList<byte[]> bytes = await RoomPack.ReadRoomBytesAsync(stream, index, fixture.Names);
        Assert.Equal(fixture.Containers, bytes);

        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(stream, index, ["hall", "hub"]);
        Assert.Equal(["hall", "hub"], rooms.Select(r => r.Definition.Name));
        using MemoryStream again = new();
        await RoomObjectStore.SaveAsync(rooms[1], again);
        Assert.Equal(fixture.Containers[0], again.ToArray());
    }

    /// <summary>The same rooms always make the same pack: nothing in it depends on when or how it was written.</summary>
    [Fact]
    public async Task APackIsAFunctionOfItsRooms()
    {
        Assert.Equal(await SaveAsync(fixture.Items), await SaveAsync(fixture.Items));
    }

    /// <summary>
    /// The header and index are laid out as the format says, byte for byte:
    /// the magic, the version, no library sections, the room count, then per
    /// room its name, one <c>ROOM</c> section, its offset and length, and the
    /// containers back to back after the index.
    /// </summary>
    [Fact]
    public async Task TheLayoutIsTheDocumentedOne()
    {
        byte[] pack = await SaveAsync([fixture.Items[1]]);

        Assert.Equal("SSRPAK01", Encoding.ASCII.GetString(pack, 0, 8));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(pack.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(pack.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(pack.AsSpan(16)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(pack.AsSpan(20)));
        Assert.Equal("end", Encoding.UTF8.GetString(pack, 24, 3));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(pack.AsSpan(27)));
        Assert.Equal("ROOM", Encoding.ASCII.GetString(pack, 31, 4));
        long indexEnd = 31 + 20;
        Assert.Equal(indexEnd, BinaryPrimitives.ReadInt64BigEndian(pack.AsSpan(35)));
        Assert.Equal(fixture.Containers[1].Length, BinaryPrimitives.ReadInt64BigEndian(pack.AsSpan(43)));
        Assert.Equal(fixture.Containers[1], pack[(int)indexEnd..]);
    }

    /// <summary>A pack of no rooms is a valid pack: a header and nothing else.</summary>
    [Fact]
    public async Task AnEmptyPackRoundTrips()
    {
        byte[] pack = await SaveAsync([]);
        Assert.Equal(20, pack.Length);
        using MemoryStream stream = new(pack);
        Assert.Empty((await RoomPack.ReadIndexAsync(stream)).Entries);
    }

    // ---- reading only what is needed ------------------------------------------

    /// <summary>
    /// Loading one room reads the header, the index and that room's
    /// container, and not one byte of the other rooms.
    /// </summary>
    [Fact]
    public async Task ALookupReadsOnlyTheIndexAndTheRoomAskedFor()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        using TapStream stream = new(new MemoryStream(pack));

        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(index.IndexEnd, stream.BytesRead);

        _ = await RoomPack.LoadRoomsAsync(stream, index, ["end"]);
        Assert.Equal(index.IndexEnd + fixture.Containers[1].Length, stream.BytesRead);
    }

    /// <summary>
    /// A stream that cannot seek is read forward once, in pack order, however
    /// the rooms are asked for: the rooms between are skipped, not loaded.
    /// </summary>
    [Fact]
    public async Task AStreamThatCannotSeekIsReadForwardInPackOrder()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        using TapStream stream = new(new MemoryStream(pack), seekable: false);

        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IReadOnlyList<byte[]> bytes = await RoomPack.ReadRoomBytesAsync(stream, index, ["hall", "hub", "hall"]);

        Assert.Equal([fixture.Containers[2], fixture.Containers[0], fixture.Containers[2]], bytes);
        Assert.Equal(pack.Length, stream.BytesRead);
    }

    /// <summary>
    /// On a stream that cannot seek, a container larger than one read chunk
    /// is collected as it arrives; one cut short is a truncation, not an
    /// allocation of what the index claims.
    /// </summary>
    [Fact]
    public async Task ALargeRoomOnAStreamThatCannotSeekIsCollectedAndATruncationIsNamed()
    {
        byte[] big = new byte[(1 << 16) + 100];
        fixture.Containers[0].CopyTo(big, 0);
        byte[] pack = await SaveAsync([new RoomPackItem("big", big)]);

        using (TapStream whole = new(new MemoryStream(pack), seekable: false))
        {
            RoomPackIndex index = await RoomPack.ReadIndexAsync(whole);
            Assert.Equal(big, (await RoomPack.ReadRoomBytesAsync(whole, index, ["big"]))[0]);
        }

        using TapStream cut = new(new MemoryStream(pack[..^10]), seekable: false);
        RoomPackIndex cutIndex = await RoomPack.ReadIndexAsync(cut);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadRoomBytesAsync(cut, cutIndex, ["big"]));
        Assert.Contains("truncated in room \"big\"", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>On a stream that cannot seek, a pack that stops before a wanted room says so.</summary>
    [Fact]
    public async Task AStreamThatCannotSeekAndStopsBeforeARoomIsATruncation()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        int cut = pack.Length - fixture.Containers[2].Length - 5;
        using TapStream stream = new(new MemoryStream(pack[..cut]), seekable: false);

        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadRoomBytesAsync(stream, index, ["hall"]));
        Assert.Contains("truncated before room \"hall\"", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A room the pack does not hold is named.</summary>
    [Fact]
    public async Task AMissingRoomIsNamed()
    {
        using MemoryStream stream = new(await SaveAsync(fixture.Items));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);

        Assert.Null(index.Find("attic"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.LoadRoomsAsync(stream, index, ["hub", "attic"]));
        Assert.Equal("the room pack has no room \"attic\".", refused.Message);
    }

    /// <summary>Names are looked up exactly: the library's rule makes names unique ignoring case, but a level spells them as they are.</summary>
    [Fact]
    public async Task NamesAreLookedUpExactly()
    {
        using MemoryStream stream = new(await SaveAsync(fixture.Items));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.NotNull(index.Find("hub"));
        Assert.Null(index.Find("HUB"));
    }

    // ---- room to grow -------------------------------------------------------

    /// <summary>
    /// A pack a later build writes, with a library section and a further
    /// section after a room's container, is still read by this one: the
    /// sections are listed under their tags, and the rooms load as before.
    /// </summary>
    [Fact]
    public async Task SectionsThisBuildDoesNotKnowAreReadAround()
    {
        RoomPackItem withRotations = fixture.Items[0] with { Extra = [new RoomPackSectionData("ROT4", new byte[] { 1, 2, 3 })] };
        using MemoryStream written = new();
        await RoomPack.SaveAsync(
            [new RoomPackSectionData("PLNS", new byte[] { 9, 9 })], [withRotations, fixture.Items[1]], written, CancellationToken.None);

        using TapStream stream = new(new MemoryStream(written.ToArray()));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);

        Assert.Equal(["PLNS"], index.LibrarySections.Select(s => s.Tag));
        RoomPackEntry hub = index.Find("hub")!;
        Assert.Equal(["ROOM", "ROT4"], hub.Sections.Select(s => s.Tag));
        Assert.Equal(3, hub.Find("ROT4")!.Value.Length);
        Assert.Null(hub.Find("NONE"));
        Assert.Equal(["hub", "end"], (await RoomPack.LoadRoomsAsync(stream, index, ["hub", "end"])).Select(r => r.Definition.Name));

        // Only the index and the two containers: the sections it does not know are never read.
        Assert.Equal(index.IndexEnd + fixture.Containers[0].Length + fixture.Containers[1].Length, stream.BytesRead);
    }

    // ---- refusals -----------------------------------------------------------

    /// <summary>A file that is not a pack is refused naming its first bytes.</summary>
    [Fact]
    public async Task AFileThatIsNotAPackIsRefused()
    {
        LinkException refused = await RefusedAsync(Encoding.ASCII.GetBytes("SSROOM01 and then some more bytes"));
        Assert.Equal("not a room pack: the file opens with bytes 5353524F4F4D3031, expected the SSRPAK01 magic.", refused.Message);
    }

    /// <summary>A pack of another version is refused naming both versions.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnotherVersionIsRefused(int version)
    {
        byte[] pack = await SaveAsync(fixture.Items);
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), version);
        Assert.Equal($"room pack version {version}; this build reads version 4.", (await RefusedAsync(pack)).Message);
        Assert.Equal(4, RoomPack.Version);
    }

    /// <summary>
    /// A version 3 pack is read: its rooms lack only the door visibility,
    /// which the link works out from them (<see cref="RoomDoorVisibilityTests"/>
    /// holds it to the same bytes); the index says which version it read.
    /// </summary>
    [Fact]
    public async Task AVersionThreePackIsRead()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 3);
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(3, index.Version);
        Assert.Equal(RoomPack.OldestReadVersion, index.Version);
    }

    /// <summary>
    /// A version 1 pack, written before the library-wide singletons were
    /// checked when the pack is built, is refused with what to do about it:
    /// its layout is version 4's, so it would read, but its rooms were never
    /// held to the library's sun and sky, and the link cannot check that.
    /// </summary>
    [Fact]
    public async Task AVersionOnePackIsRefusedWithWhatToDo()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 1);
        Assert.Equal(
            "room pack version 1; this build reads version 4. A version 1 pack was written before the library-wide singletons"
            + " were checked when the pack is built; recompile the library with ssmap room.",
            (await RefusedAsync(pack)).Message);
    }

    /// <summary>
    /// A version 2 pack, written before rooms packed their files, is refused
    /// with what to do about it: its rooms were compiled with the default
    /// cubemaps left out, so a level linked from it would silently lack the
    /// defaults the same library recompiled gives it.
    /// </summary>
    [Fact]
    public async Task AVersionTwoPackIsRefusedWithWhatToDo()
    {
        byte[] pack = await SaveAsync(fixture.Items);
        BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(8), 2);
        Assert.Equal(
            "room pack version 2; this build reads version 4. A version 2 pack was written before rooms packed their files"
            + " (the default cubemaps built from the library's sky), which the link now carries; recompile the library with ssmap room.",
            (await RefusedAsync(pack)).Message);
    }

    /// <summary>A pack cut short anywhere is refused as truncated, wherever the cut falls.</summary>
    [Theory]
    [InlineData(10, "truncated in the header")]
    [InlineData(22, "truncated in index entry 0")]
    [InlineData(40, "truncated in index entry 0 (\"hub\")'s section 0")]
    [InlineData(-1, "the file is truncated")]
    public async Task ATruncatedPackIsRefused(int cut, string expected)
    {
        byte[] pack = await SaveAsync(fixture.Items);
        LinkException refused = await RefusedAsync(pack[..(cut < 0 ? pack.Length + cut : cut)]);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Bytes after the last room are refused: the one layout ends where the last section does.</summary>
    [Fact]
    public async Task TrailingBytesAreRefused()
    {
        LinkException refused = await RefusedAsync([.. await SaveAsync(fixture.Items), 0]);
        Assert.Contains("bytes follow the last section", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Every inconsistency of the header or the index is refused, each with its own message.</summary>
    [Theory]
    [InlineData("rooms", "claims -1 rooms")]
    [InlineData("library", "claims 65 library sections")]
    [InlineData("name length", "has a name of 0 bytes")]
    [InlineData("name bytes", "has a name that is not UTF-8")]
    [InlineData("name rule", "the room name \".ub\" starts with '.'")]
    [InlineData("duplicate", "a second room named \"HUB\", ignoring case")]
    [InlineData("sections", "has 0 sections")]
    [InlineData("first tag", "starts with a \"ROT4\" section")]
    [InlineData("tag bytes", "has tag bytes 524F4F00")]
    [InlineData("length", "section is -1 bytes")]
    [InlineData("offset", "sections run back to back")]
    public async Task AnInconsistentIndexIsRefused(string fault, string expected)
    {
        byte[] pack = await SaveAsync([fixture.Items[0], fixture.Items[1]]);

        // The first entry starts at byte 20: name length, "hub", section count, section.
        const int Name = 24;
        const int Sections = 27;
        const int Tag = 31;
        switch (fault)
        {
            case "rooms":
                BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(16), -1);
                break;
            case "library":
                BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(12), RoomPack.MaxSections + 1);
                break;
            case "name length":
                BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(20), 0);
                break;
            case "name bytes":
                pack[Name] = 0xFF;
                break;
            case "name rule":
                pack[Name] = (byte)'.';
                break;
            case "duplicate":
                // The second entry's name, "end", becomes "HUB".
                int second = Tag + 20 + 4;
                Encoding.ASCII.GetBytes("HUB").CopyTo(pack, second);
                break;
            case "sections":
                BinaryPrimitives.WriteInt32BigEndian(pack.AsSpan(Sections), 0);
                break;
            case "first tag":
                Encoding.ASCII.GetBytes("ROT4").CopyTo(pack, Tag);
                break;
            case "tag bytes":
                pack[Tag + 3] = 0;
                break;
            case "length":
                BinaryPrimitives.WriteInt64BigEndian(pack.AsSpan(Tag + 12), -1);
                break;
            case "offset":
                BinaryPrimitives.WriteInt64BigEndian(pack.AsSpan(Tag + 4), 1000);
                break;
        }

        Assert.Contains(expected, (await RefusedAsync(pack)).Message, StringComparison.Ordinal);
    }

    /// <summary>Two sections of one tag in one room are refused.</summary>
    [Fact]
    public async Task ARepeatedSectionTagIsRefused()
    {
        RoomPackItem twice = fixture.Items[0] with { Extra = [new RoomPackSectionData("ROT4", new byte[] { 1 })] };
        using MemoryStream written = new();
        await RoomPack.SaveAsync([], [twice], written, CancellationToken.None);
        byte[] pack = written.ToArray();
        Encoding.ASCII.GetBytes("ROOM").CopyTo(pack, 31 + 20);

        Assert.Contains("index entry 0 (\"hub\") has two \"ROOM\" sections", (await RefusedAsync(pack)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A container that is not a room, one with bytes after the room, and
    /// one that holds another room than its entry names are each refused,
    /// naming the entry.
    /// </summary>
    [Theory]
    [InlineData("junk", "room pack entry \"hub\": not a room container")]
    [InlineData("trailing", "room pack entry \"hub\" has 2 bytes after its room container")]
    [InlineData("renamed", "room pack entry \"other\" holds room \"hub\"")]
    public async Task AContainerThatDoesNotMatchItsEntryIsRefused(string fault, string expected)
    {
        RoomPackItem item = fault switch
        {
            "junk" => new RoomPackItem("hub", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }),
            "trailing" => new RoomPackItem("hub", fixture.Containers[0].Concat(new byte[] { 0, 0 }).ToArray()),
            _ => new RoomPackItem("other", fixture.Containers[0]),
        };

        using MemoryStream stream = new(await SaveAsync([item]));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPack.LoadRoomsAsync(stream, index, [item.Name]));
        Assert.StartsWith(expected, refused.Message, StringComparison.Ordinal);
    }

    // ---- the writer's own refusals ------------------------------------------

    /// <summary>The writer refuses what the reader would: bad names, names equal ignoring case, and bad or repeated section tags.</summary>
    [Theory]
    [InlineData("name", "the room name \"../x\" starts with '.'")]
    [InlineData("duplicate", "two rooms of the pack are named \"HUB\"")]
    [InlineData("long", "bytes of UTF-8; a pack holds names of at most 1024")]
    [InlineData("tag", "a tag is four printable ASCII characters")]
    [InlineData("repeat", "has two \"ROOM\" sections")]
    [InlineData("many", "a pack allows 64")]
    public async Task TheWriterRefusesWhatTheReaderWould(string fault, string expected)
    {
        RoomPackItem hub = fixture.Items[0];
        IReadOnlyList<RoomPackItem> rooms = fault switch
        {
            "name" => [hub with { Name = "../x" }],
            "duplicate" => [hub, hub with { Name = "HUB" }],
            "long" => [hub with { Name = new string('a', RoomPack.MaxNameBytes + 1) }],
            "tag" => [hub with { Extra = [new RoomPackSectionData("ROT", ReadOnlyMemory<byte>.Empty)] }],
            "repeat" => [hub with { Extra = [new RoomPackSectionData("ROOM", ReadOnlyMemory<byte>.Empty)] }],
            _ => [hub with { Extra = [.. Enumerable.Range(0, RoomPack.MaxSections).Select(i => new RoomPackSectionData($"X{i:D3}", ReadOnlyMemory<byte>.Empty))] }],
        };

        using MemoryStream written = new();
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => RoomPack.SaveAsync(rooms, written));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> SaveAsync(IReadOnlyList<RoomPackItem> items)
    {
        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        return stream.ToArray();
    }

    private static async Task<LinkException> RefusedAsync(byte[] pack)
    {
        using MemoryStream stream = new(pack);
        return await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadIndexAsync(stream));
    }
}
