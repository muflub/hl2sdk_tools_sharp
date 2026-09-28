//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A typed block of bytes in a pack, as the writer takes it: a four-character tag and its bytes.</summary>
/// <param name="Tag">Four printable ASCII characters naming what the bytes are, for example <see cref="RoomPack.RoomSection"/>.</param>
/// <param name="Bytes">The section's bytes.</param>
public readonly record struct RoomPackSectionData(string Tag, ReadOnlyMemory<byte> Bytes);

/// <summary>One room of a pack as the writer takes it: its name and its room container bytes.</summary>
/// <param name="Name">The room's name, which the pack's index lists it under.</param>
/// <param name="Room">
/// The room's container, exactly as <see cref="RoomObjectStore.SaveAsync"/> writes
/// it: the room's <see cref="RoomPack.RoomSection"/>.
/// </param>
public sealed record RoomPackItem(string Name, ReadOnlyMemory<byte> Room)
{
    /// <summary>
    /// Further sections of the room, after its <see cref="RoomPack.RoomSection"/>:
    /// the link work done ahead for it (<see cref="CreateAsync"/>), or none.
    /// </summary>
    internal IReadOnlyList<RoomPackSectionData> Extra { get; init; } = [];

    /// <summary>
    /// The pack item for a compiled room: its container, and the link work
    /// that depends only on the room and its turn, done now so that every
    /// link of a level placing it does not do it again.
    /// </summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The item, named for the room.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// What <c>ssmap room</c> packs. The link work is the room's own
    /// <c>Link</c> when the library compile already did it (it does, on the
    /// room's thread), or is done here. A room the link would refuse gets
    /// none, and is packed with its container alone: a level that places it
    /// is refused at link time with the message it always got. See
    /// <see cref="RoomPack"/> for the sections this adds.
    /// </para>
    /// <para>
    /// The bytes are a function of the room: the same room gives the same
    /// item at any thread count.
    /// </para>
    /// </remarks>
    public static async Task<RoomPackItem> CreateAsync(RoomObject room, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        using MemoryStream container = new();
        await RoomObjectStore.SaveAsync(room, container, cancellationToken).ConfigureAwait(false);
        RoomLinkData? link = room.Link is { } stored && stored.IsFor(room)
            ? stored
            : await LevelLinker.TryPrecomputeAsync(room, cancellationToken).ConfigureAwait(false);
        return new RoomPackItem(room.Definition.Name, container.ToArray())
        {
            Extra = link is null ? [] : RoomLinkSections.Write(link),
        };
    }
}

/// <summary>A room a link wants from a pack, and the quarter turns it is placed at.</summary>
/// <param name="Name">The room's name, as the level spells it.</param>
/// <param name="Rotations">
/// The quarter turns the level places it at, each 0 to 3 (any integer is
/// reduced as a placement reduces it); only these turns' link sections are
/// read.
/// </param>
public sealed record RoomPackRequest(string Name, IReadOnlyCollection<int> Rotations);

/// <summary>Where one section sits in a pack.</summary>
/// <param name="Tag">What the section is.</param>
/// <param name="Offset">Where its bytes start, from the start of the pack.</param>
/// <param name="Length">How many bytes it is.</param>
public readonly record struct RoomPackSection(string Tag, long Offset, long Length);

/// <summary>One room of a pack's index: its name and its sections.</summary>
public sealed class RoomPackEntry
{
    internal RoomPackEntry(string name, IReadOnlyList<RoomPackSection> sections)
    {
        Name = name;
        Sections = sections;
    }

    /// <summary>The room's name.</summary>
    public string Name { get; }

    /// <summary>The room's sections, in pack order; the first is always its <see cref="RoomPack.RoomSection"/>.</summary>
    public IReadOnlyList<RoomPackSection> Sections { get; }

    /// <summary>The room's container section.</summary>
    public RoomPackSection Room => Sections[0];

    /// <summary>Finds one of the room's sections by tag.</summary>
    /// <param name="tag">The section's tag.</param>
    /// <returns>The section, or null when the room has none of that tag.</returns>
    public RoomPackSection? Find(string tag)
    {
        foreach (RoomPackSection section in Sections)
        {
            if (section.Tag == tag)
            {
                return section;
            }
        }

        return null;
    }
}

/// <summary>A pack's index: the library's sections, and every room it holds and where, in pack order.</summary>
public sealed class RoomPackIndex
{
    private readonly Dictionary<string, RoomPackEntry> _byName;

    internal RoomPackIndex(
        IReadOnlyList<RoomPackSection> librarySections, IReadOnlyList<RoomPackEntry> entries, long indexEnd, long? start)
    {
        LibrarySections = librarySections;
        Entries = entries;
        IndexEnd = indexEnd;
        Start = start;
        _byName = new Dictionary<string, RoomPackEntry>(entries.Count, StringComparer.Ordinal);
        foreach (RoomPackEntry entry in entries)
        {
            _byName.Add(entry.Name, entry);
        }
    }

    /// <summary>The sections that belong to the whole library rather than to one room: none in this build.</summary>
    public IReadOnlyList<RoomPackSection> LibrarySections { get; }

    /// <summary>The rooms, in the order the pack holds them (the library's).</summary>
    public IReadOnlyList<RoomPackEntry> Entries { get; }

    /// <summary>Where the index ends and the first section's bytes start, from the start of the pack.</summary>
    public long IndexEnd { get; }

    /// <summary>
    /// Where the pack starts in the stream it was read from, when that
    /// stream can seek; null when it cannot, and sections are then reached by
    /// reading forward from <see cref="IndexEnd"/>.
    /// </summary>
    internal long? Start { get; }

    /// <summary>Finds a room by its exact name.</summary>
    /// <param name="name">The room's name, as a level spells it.</param>
    /// <returns>The entry, or null when the pack has no such room.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public RoomPackEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out RoomPackEntry? entry) ? entry : null;
    }
}

/// <summary>
/// The <c>.roompack</c> file format: every compiled room of a library in one
/// file, with an index in front so a link reads only the rooms its level
/// places.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout, version 1.</b> Every integer is big-endian, as in the room
/// container, so the bytes do not depend on the writer's byte order. A tag
/// is four printable ASCII characters, stored as they read.
/// </para>
/// <list type="table">
/// <listheader><term>Bytes</term><description>What</description></listheader>
/// <item><term>8</term><description>The magic, <c>SSRPAK01</c> in ASCII (<see cref="Magic"/>).</description></item>
/// <item><term>4</term><description><c>int32</c> format version (<see cref="Version"/>).</description></item>
/// <item><term>4</term><description><c>int32</c> library section count, 0 to <see cref="MaxSections"/> (0 in this build).</description></item>
/// <item><term>4</term><description><c>int32</c> room count, 0 to <see cref="MaxRooms"/>.</description></item>
/// <item><term>20 per library section</term><description>
/// The library section table: tag, <c>int64</c> offset from the start of the pack, <c>int64</c> length.
/// </description></item>
/// <item><term>per room</term><description>
/// The room index, in library order: <c>int32</c> name length in bytes (1 to
/// <see cref="MaxNameBytes"/>), the name in UTF-8, <c>int32</c> section count
/// (1 to <see cref="MaxSections"/>), then per section its tag, <c>int64</c>
/// offset and <c>int64</c> length. The first section is the room's
/// <see cref="RoomSection"/>, exactly the bytes
/// <see cref="RoomObjectStore.SaveAsync"/> writes for it (the room container).
/// A room <c>ssmap room</c> packs then has the link work done ahead for it
/// (<see cref="RoomPackItem.CreateAsync"/>): <c>LNKA</c>, what depends on
/// the room alone, then per quarter turn <i>r</i> its turned geometry
/// (<c>GEO</c><i>r</i>) and world collision (<c>COL</c><i>r</i>), and
/// optionally its turned entities (<c>ENT</c><i>r</i>); <c>RoomLinkSections</c>
/// gives their layout and why each is stored or not. They are optional: a
/// room the link would refuse has none, and a pack written before them
/// links to the same bytes, the link computing the same data on the fly.
/// </description></item>
/// <item><term>the rest</term><description>
/// Every section's bytes, back to back with nothing between: the library
/// sections in table order, then each room's sections, room by room in index
/// order.
/// </description></item>
/// </list>
/// <para>
/// <b>Room to grow.</b> A room is precompiled so that a link does as little
/// as it can, and the link wants more per room than the container: the
/// checks, plug census and turned geometry, collision and entities are the
/// link sections today; door-to-door visibility and lighting baked
/// per turn are further candidates; and some tables belong to the library,
/// not a room (a shared plane, texdata or texinfo table). The format has a
/// place for each without a redesign: a room's further sections follow its
/// container under their own tags, and the library's sections have a table
/// of their own, empty today. A reader looks sections up by tag and ignores
/// tags it does not know, so a pack that gains an optional section is still
/// read by an older build, and that is why the link sections did not
/// change <see cref="Version"/>; a change an older build must not read
/// around (a different container, a section it cannot ignore) raises it.
/// </para>
/// <para>
/// <b>One layout per set of rooms.</b> The writer puts the rooms in the
/// order it is given them (<c>ssmap room</c> gives the library's) and every
/// section's bytes back to back straight after the index, and so the reader
/// can insist on exactly that: the first section starts where the index
/// ends, each next one where the one before it ends, and the last one ends
/// where the file does. There is no padding, alignment or free space to
/// vary, so a pack is a function of its rooms: the same library and build
/// give the same bytes at any thread count and on every run, as each room's
/// container already does. A reader that finds anything else (an offset
/// that overlaps, a gap, trailing bytes) refuses the file rather than
/// guessing which part to believe.
/// </para>
/// <para>
/// <b>Index in front.</b> A link places a few rooms of a large library, so
/// the reader takes the header and index and then seeks straight to each
/// section it needs; nothing else of the file is read. The cost is that the
/// writer must know every section's length before it writes the first byte,
/// which it does: rooms are compiled first and the pack is written once, in
/// one replace of the file (<see cref="Io.IFileSystem.ReplaceAsync"/>), so a
/// cancelled or failed run never leaves a partial pack.
/// </para>
/// <para>
/// <b>Names.</b> Unique ignoring case (the library's own rule,
/// <see cref="RoomLibraryVmf"/>), valid by <see cref="RoomNames"/>, and
/// looked up exactly. An entry's container must hold the room its index
/// entry names; a pack whose index and containers disagree is refused.
/// </para>
/// <para>
/// <b>Versions.</b> Version 1 is the only one. A pack of any other version is
/// refused with the version it carries and the one this build reads, as the
/// room container does; the containers inside carry their own version and
/// are checked by <see cref="RoomObjectStore.LoadAsync"/>.
/// </para>
/// </remarks>
public static class RoomPack
{
    /// <summary>The pack's eight magic bytes, as they read in the file.</summary>
    public const string Magic = "SSRPAK01";

    /// <summary>The only pack version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The file extension <c>ssmap room</c> writes and <c>ssmap link</c> looks for.</summary>
    public const string Extension = ".roompack";

    /// <summary>The tag of a room's first section: its room container.</summary>
    public const string RoomSection = "ROOM";

    /// <summary>The most rooms a pack may hold: far above any library, low enough that a lying count fails fast.</summary>
    public const int MaxRooms = 1 << 20;

    /// <summary>The most sections the library, or one room, may have.</summary>
    public const int MaxSections = 64;

    /// <summary>The longest room name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 1024;

    // magic + version + library section count + room count
    private const int HeaderBytes = 20;

    // tag + offset + length
    private const int SectionBytes = 4 + 8 + 8;

    /// <summary>How many bytes are skipped or collected per read on a stream that cannot seek.</summary>
    private const int ChunkBytes = 1 << 16;

    /// <summary>Writes a pack of rooms, in the order given.</summary>
    /// <param name="rooms">The rooms and their container bytes.</param>
    /// <param name="w">The stream to write to, positioned where the pack starts; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once every byte is in the stream.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// Too many rooms, a name that is not a room name or is too long, or two
    /// names equal ignoring case.
    /// </exception>
    public static Task SaveAsync(IReadOnlyList<RoomPackItem> rooms, Stream w, CancellationToken cancellationToken = default) =>
        SaveAsync([], rooms, w, cancellationToken);

    /// <summary>
    /// <see cref="SaveAsync(IReadOnlyList{RoomPackItem}, Stream, CancellationToken)"/>
    /// with library sections and the rooms' <see cref="RoomPackItem.Extra"/>
    /// sections: what a later build writes, for the facts that prove this
    /// one reads around them.
    /// </summary>
    internal static async Task SaveAsync(
        IReadOnlyList<RoomPackSectionData> librarySections,
        IReadOnlyList<RoomPackItem> rooms,
        Stream w,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(librarySections);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(w);
        if (rooms.Count > MaxRooms)
        {
            throw new ArgumentException($"a room pack holds at most {MaxRooms} rooms, not {rooms.Count}.", nameof(rooms));
        }

        CheckSections(librarySections, "the library");
        byte[][] names = new byte[rooms.Count][];
        List<RoomPackSectionData>[] sections = new List<RoomPackSectionData>[rooms.Count];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long indexEnd = HeaderBytes + ((long)librarySections.Count * SectionBytes);
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomPackItem room = rooms[i];
            ArgumentNullException.ThrowIfNull(room, nameof(rooms));
            ArgumentNullException.ThrowIfNull(room.Name, nameof(rooms));
            if (RoomNames.Problem(room.Name) is { } problem)
            {
                throw new ArgumentException($"the room name \"{room.Name}\" {problem}.", nameof(rooms));
            }

            if (!seen.Add(room.Name))
            {
                throw new ArgumentException($"two rooms of the pack are named \"{room.Name}\", ignoring case.", nameof(rooms));
            }

            names[i] = Encoding.UTF8.GetBytes(room.Name);
            if (names[i].Length > MaxNameBytes)
            {
                throw new ArgumentException(
                    $"the room name \"{room.Name}\" is {names[i].Length} bytes of UTF-8; a pack holds names of at most {MaxNameBytes}.",
                    nameof(rooms));
            }

            sections[i] = [new RoomPackSectionData(RoomSection, room.Room), .. room.Extra];
            CheckSections(sections[i], $"the room \"{room.Name}\"");
            indexEnd += 4 + names[i].Length + 4 + ((long)sections[i].Count * SectionBytes);
        }

        using MemoryStream header = new((int)Math.Min(indexEnd, int.MaxValue));
        header.Write(Encoding.ASCII.GetBytes(Magic));
        WriteInt32(header, Version);
        WriteInt32(header, librarySections.Count);
        WriteInt32(header, rooms.Count);
        long offset = indexEnd;
        foreach (RoomPackSectionData section in librarySections)
        {
            offset = WriteSection(header, section, offset);
        }

        for (int i = 0; i < rooms.Count; i++)
        {
            WriteInt32(header, names[i].Length);
            header.Write(names[i]);
            WriteInt32(header, sections[i].Count);
            foreach (RoomPackSectionData section in sections[i])
            {
                offset = WriteSection(header, section, offset);
            }
        }

        await w.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), cancellationToken).ConfigureAwait(false);
        foreach (RoomPackSectionData section in librarySections.Concat(sections.SelectMany(s => s)))
        {
            await w.WriteAsync(section.Bytes, cancellationToken).ConfigureAwait(false);
        }

        await w.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a pack's header and index, and nothing else of it.</summary>
    /// <param name="r">The pack, positioned where it starts; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The index; the stream is left just after it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="r"/> is null.</exception>
    /// <exception cref="LinkException">
    /// Not a pack, a version this build does not read, a truncated or
    /// inconsistent index, or (on a stream that knows its length) a file
    /// longer or shorter than its index says. Each message names what it saw.
    /// </exception>
    public static async Task<RoomPackIndex> ReadIndexAsync(Stream r, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        long? start = r.CanSeek ? r.Position : null;

        byte[] header = await ReadExactAsync(r, HeaderBytes, "the header", cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new LinkException(
                $"not a room pack: the file opens with bytes {Convert.ToHexString(header, 0, 8)}, expected the {Magic} magic.");
        }

        int version = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (version != Version)
        {
            throw new LinkException($"room pack version {version}; this build reads version {Version}.");
        }

        int libraryCount = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12));
        if (libraryCount is < 0 or > MaxSections)
        {
            throw new LinkException($"room pack header claims {libraryCount} library sections; a pack has 0 to {MaxSections}.");
        }

        int count = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16));
        if (count is < 0 or > MaxRooms)
        {
            throw new LinkException($"room pack header claims {count} rooms; a pack holds 0 to {MaxRooms}.");
        }

        long position = HeaderBytes;
        List<RoomPackSection> all = [];
        IReadOnlyList<RoomPackSection> library = await ReadSectionsAsync(r, libraryCount, "the library", cancellationToken)
            .ConfigureAwait(false);
        all.AddRange(library);
        position += (long)libraryCount * SectionBytes;

        List<RoomPackEntry> entries = new(Math.Min(count, 4096));
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string what = $"index entry {i}";
            int nameLength = await ReadInt32Async(r, what, cancellationToken).ConfigureAwait(false);
            if (nameLength is < 1 or > MaxNameBytes)
            {
                throw new LinkException($"room pack {what} has a name of {nameLength} bytes; a name is 1 to {MaxNameBytes}.");
            }

            byte[] nameBytes = await ReadExactAsync(r, nameLength, what, cancellationToken).ConfigureAwait(false);
            string name;
            try
            {
                name = strict.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                throw new LinkException($"room pack {what} has a name that is not UTF-8.");
            }

            if (RoomNames.Problem(name) is { } problem)
            {
                throw new LinkException($"room pack {what}: the room name \"{name}\" {problem}.");
            }

            if (!seen.Add(name))
            {
                throw new LinkException($"room pack {what}: a second room named \"{name}\", ignoring case.");
            }

            int sectionCount = await ReadInt32Async(r, what, cancellationToken).ConfigureAwait(false);
            if (sectionCount is < 1 or > MaxSections)
            {
                throw new LinkException(
                    $"room pack {what} (\"{name}\") has {sectionCount} sections; a room has 1 to {MaxSections}.");
            }

            IReadOnlyList<RoomPackSection> sections = await ReadSectionsAsync(r, sectionCount, $"{what} (\"{name}\")", cancellationToken)
                .ConfigureAwait(false);
            if (sections[0].Tag != RoomSection)
            {
                throw new LinkException(
                    $"room pack {what} (\"{name}\") starts with a \"{sections[0].Tag}\" section; a room's first is its \"{RoomSection}\".");
            }

            entries.Add(new RoomPackEntry(name, sections));
            all.AddRange(sections);
            position += 4 + nameLength + 4 + ((long)sectionCount * SectionBytes);
        }

        // The one layout the writer produces: back to back, from the end of
        // the index to the end of the file.
        long expected = position;
        foreach (RoomPackSection section in all)
        {
            if (section.Offset != expected)
            {
                throw new LinkException(
                    $"room pack index puts a \"{section.Tag}\" section at byte {section.Offset}; sections run back to back"
                    + $" from the end of the index, so it starts at byte {expected}.");
            }

            // No overflow: at most 2^6 + 2^26 sections of at most 2^31 bytes each.
            expected = section.Offset + section.Length;
        }

        if (start is long at && r.Length - at != expected)
        {
            throw new LinkException(
                $"room pack is {r.Length - at} bytes; its index says {expected}"
                + (r.Length - at < expected ? " (the file is truncated)." : " (bytes follow the last section)."));
        }

        return new RoomPackIndex(library, entries, position, start);
    }

    /// <summary>
    /// Reads the container bytes of the named rooms, and only those, from a
    /// pack whose index was just read.
    /// </summary>
    /// <param name="r">
    /// The pack. A seekable stream is sought to each room; one that cannot
    /// seek must be where <see cref="ReadIndexAsync"/> left it, and is read
    /// forward, the bytes between the rooms skipped.
    /// </param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="names">The rooms wanted; a name may repeat.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each named room's container bytes, in the order of <paramref name="names"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">A name the pack does not hold, or a room cut short.</exception>
    /// <remarks>
    /// The rooms are visited in pack order whatever order they are asked in,
    /// so a stream that cannot seek is still read once, front to back.
    /// </remarks>
    public static async Task<IReadOnlyList<byte[]>> ReadRoomBytesAsync(
        Stream r,
        RoomPackIndex index,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(names);

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (string name in names)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(names));
            RoomPackEntry entry = index.Find(name)
                ?? throw new LinkException($"the room pack has no room \"{name}\".");
            if (seen.Add(name))
            {
                wanted.Add((name, entry.Room));
            }
        }

        Dictionary<(string, string), ArraySegment<byte>> read = await ReadSectionsAsync(r, index, wanted, cancellationToken)
            .ConfigureAwait(false);
        return [.. names.Select(name => read[(name, RoomSection)] is { Offset: 0 } whole && whole.Count == whole.Array!.Length
            ? whole.Array
            : read[(name, RoomSection)].ToArray())];
    }

    /// <summary>
    /// Loads the named rooms from a pack whose index was just read, with
    /// the link work the pack stores for every quarter turn: the bytes
    /// <see cref="ReadRoomBytesAsync"/> reads, each through
    /// <see cref="RoomObjectStore.LoadAsync"/>.
    /// </summary>
    /// <param name="r">The pack, as <see cref="ReadRoomBytesAsync"/> takes it.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="names">The rooms wanted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rooms, in the order of <paramref name="names"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">
    /// A name the pack does not hold, a room cut short, a container the room
    /// store refuses, one with bytes after its last section, one that holds
    /// a different room than its index entry names, or a link section that
    /// does not fit its room.
    /// </exception>
    public static async Task<IReadOnlyList<RoomObject>> LoadRoomsAsync(
        Stream r,
        RoomPackIndex index,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(names);
        List<RoomPackRequest> requests = new(names.Count);
        foreach (string name in names)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(names));
            requests.Add(new RoomPackRequest(name, [0, 1, 2, 3]));
        }

        return await LoadRoomsAsync(r, index, requests, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the rooms a level places, with the link work the pack stores
    /// for the quarter turns it places them at, and nothing else of the pack.
    /// </summary>
    /// <param name="r">The pack, as <see cref="ReadRoomBytesAsync"/> takes it.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="requests">The rooms wanted and their turns; a room may repeat, and its turns are joined.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rooms, one per request, in request order; a repeated room is the same object.</returns>
    /// <exception cref="ArgumentNullException">An argument or a name is null.</exception>
    /// <exception cref="LinkException">As <see cref="LoadRoomsAsync(Stream, RoomPackIndex, IReadOnlyList{string}, CancellationToken)"/>.</exception>
    /// <remarks>
    /// <para>
    /// What <c>ssmap link</c> reads. A room's link work is one section for
    /// what depends on the room alone and a few per quarter turn
    /// (<c>RoomLinkSections</c>), and only the turns asked for are read: a
    /// room placed at one turn costs its container, the shared section and
    /// that turn's, which follow each other in the pack and are read in one
    /// go. A room whose pack has no link sections (a pack written before
    /// them) loads without, and the link computes the same on the fly.
    /// </para>
    /// <para>
    /// As with the containers, every section is read in pack order, so a
    /// stream that cannot seek is still read once, front to back.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<RoomObject>> LoadRoomsAsync(
        Stream r,
        RoomPackIndex index,
        IReadOnlyList<RoomPackRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(requests);

        // Per room, first-asked order: its entry and the turns wanted.
        List<string> order = [];
        Dictionary<string, (RoomPackEntry Entry, bool[] Turns)> rooms = new(StringComparer.Ordinal);
        foreach (RoomPackRequest request in requests)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(requests));
            ArgumentNullException.ThrowIfNull(request.Name, nameof(requests));
            ArgumentNullException.ThrowIfNull(request.Rotations, nameof(requests));
            if (!rooms.TryGetValue(request.Name, out (RoomPackEntry Entry, bool[] Turns) room))
            {
                RoomPackEntry entry = index.Find(request.Name)
                    ?? throw new LinkException($"the room pack has no room \"{request.Name}\".");
                rooms[request.Name] = room = (entry, new bool[4]);
                order.Add(request.Name);
            }

            foreach (int rotation in request.Rotations)
            {
                room.Turns[((rotation % 4) + 4) % 4] = true;
            }
        }

        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (string name in order)
        {
            (RoomPackEntry entry, bool[] turns) = rooms[name];
            wanted.Add((name, entry.Room));
            if (entry.Find(RoomLinkSections.SharedTag) is { } shared)
            {
                wanted.Add((name, shared));
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    if (!turns[rotation])
                    {
                        continue;
                    }

                    foreach (string tag in RoomLinkSections.RotationTags(rotation))
                    {
                        if (entry.Find(tag) is { } part)
                        {
                            wanted.Add((name, part));
                        }
                    }
                }
            }
        }

        Dictionary<(string, string), ArraySegment<byte>> read = await ReadSectionsAsync(r, index, wanted, cancellationToken)
            .ConfigureAwait(false);

        ArraySegment<byte>? Section(string name, string tag) =>
            read.TryGetValue((name, tag), out ArraySegment<byte> bytes) ? bytes : (ArraySegment<byte>?)null;

        Dictionary<string, RoomObject> loaded = new(StringComparer.Ordinal);
        foreach (string name in order)
        {
            ArraySegment<byte> bytes = read[(name, RoomSection)];
            using MemoryStream container = new(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
            RoomObject room;
            try
            {
                room = await RoomObjectStore.LoadAsync(container, cancellationToken).ConfigureAwait(false);
            }
            catch (LinkException exception)
            {
                throw new LinkException($"room pack entry \"{name}\": {exception.Message}");
            }

            if (container.Position != container.Length)
            {
                throw new LinkException(
                    $"room pack entry \"{name}\" has {container.Length - container.Position} bytes after its room container.");
            }

            if (room.Definition.Name != name)
            {
                throw new LinkException($"room pack entry \"{name}\" holds room \"{room.Definition.Name}\".");
            }

            RoomLinkData? link = RoomLinkSections.Read(room, tag => Section(name, tag));
            loaded[name] = link is null ? room : room with { Link = link };
        }

        return [.. requests.Select(request => loaded[request.Name])];
    }

    /// <summary>
    /// Reads the given sections, in pack order whatever order they are
    /// given in: sought to on a stream that can seek, reached by skipping
    /// forward on one that cannot.
    /// </summary>
    /// <returns>Each section's bytes, by room name and tag.</returns>
    private static async Task<Dictionary<(string, string), ArraySegment<byte>>> ReadSectionsAsync(
        Stream r,
        RoomPackIndex index,
        List<(string Name, RoomPackSection Section)> wanted,
        CancellationToken cancellationToken)
    {
        wanted.Sort((a, b) => a.Section.Offset.CompareTo(b.Section.Offset));
        Dictionary<(string, string), ArraySegment<byte>> read = new(wanted.Count);
        long position = index.IndexEnd;
        for (int first = 0; first < wanted.Count;)
        {
            // A run of one room's sections that follow each other in the file
            // (its container and its link sections usually do) is one read,
            // not one per section: a link reads a few hundred rooms, and the
            // calls, not the bytes, were much of the cost. Runs stop at the
            // room, so a room cut short is still reported as that room.
            (string name, RoomPackSection section) = wanted[first];
            int last = first;
            long end = section.Offset + section.Length;
            while (last + 1 < wanted.Count && wanted[last + 1].Section.Offset == end && wanted[last + 1].Name == name
                && wanted[last + 1].Section.Length <= int.MaxValue - (end - section.Offset))
            {
                last++;
                end += wanted[last].Section.Length;
            }

            string what = section.Tag == RoomSection ? $"room \"{name}\"" : $"room \"{name}\"'s \"{section.Tag}\" section";
            if (index.Start is long start)
            {
                r.Seek(start + section.Offset, SeekOrigin.Begin);
            }
            else
            {
                await SkipAsync(r, section.Offset - position, what, cancellationToken).ConfigureAwait(false);
            }

            byte[] run = await ReadSectionAsync(r, section with { Length = end - section.Offset }, what, cancellationToken)
                .ConfigureAwait(false);
            if (first == last)
            {
                read[(name, section.Tag)] = run;
            }
            else
            {
                for (int i = first; i <= last; i++)
                {
                    RoomPackSection part = wanted[i].Section;
                    read[(wanted[i].Name, part.Tag)] = new ArraySegment<byte>(run, (int)(part.Offset - section.Offset), (int)part.Length);
                }
            }

            position = end;
            first = last + 1;
        }

        return read;
    }

    private static void CheckSections(IReadOnlyList<RoomPackSectionData> sections, string owner)
    {
        if (sections.Count > MaxSections)
        {
            throw new ArgumentException($"{owner} has {sections.Count} sections; a pack allows {MaxSections}.", nameof(sections));
        }

        HashSet<string> tags = new(StringComparer.Ordinal);
        foreach (RoomPackSectionData section in sections)
        {
            if (!IsTag(section.Tag))
            {
                throw new ArgumentException($"{owner} has a section tagged \"{section.Tag}\"; a tag is four printable ASCII characters.", nameof(sections));
            }

            if (!tags.Add(section.Tag))
            {
                throw new ArgumentException($"{owner} has two \"{section.Tag}\" sections.", nameof(sections));
            }
        }
    }

    /// <summary>
    /// The tags this build writes, as the constant strings: an index of a
    /// large library names a few thousand sections, and decoding each tag
    /// into a new string was a measurable share of reading it.
    /// </summary>
    private static string? KnownTag(ReadOnlySpan<byte> tag) => (tag[0], tag[1], tag[2], tag[3]) switch
    {
        ((byte)'R', (byte)'O', (byte)'O', (byte)'M') => RoomSection,
        ((byte)'L', (byte)'N', (byte)'K', (byte)'A') => RoomLinkSections.SharedTag,
        ((byte)'G', (byte)'E', (byte)'O', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.GeometryTag(tag[3] - '0'),
        ((byte)'C', (byte)'O', (byte)'L', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.CollisionTag(tag[3] - '0'),
        ((byte)'E', (byte)'N', (byte)'T', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.EntitiesTag(tag[3] - '0'),
        _ => null,
    };

    private static bool IsTag(string? tag) => tag is { Length: 4 } && tag.All(c => c is >= '!' and <= '~');

    private static long WriteSection(Stream index, RoomPackSectionData section, long offset)
    {
        index.Write(Encoding.ASCII.GetBytes(section.Tag));
        WriteInt64(index, offset);
        WriteInt64(index, section.Bytes.Length);
        return offset + section.Bytes.Length;
    }

    private static async Task<IReadOnlyList<RoomPackSection>> ReadSectionsAsync(
        Stream r, int count, string owner, CancellationToken cancellationToken)
    {
        List<RoomPackSection> sections = new(count);
        HashSet<string> tags = new(StringComparer.Ordinal);

        // The whole table in one read (at most MaxSections entries): a room
        // with its link sections has six, and a read per entry made the
        // index of a large library a few thousand small reads. A table cut
        // short is still reported at the entry the cut falls in.
        byte[] table = new byte[count * SectionBytes];
        int filled = 0;
        while (filled < table.Length)
        {
            int got = await r.ReadAsync(table.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                throw new LinkException(
                    $"room pack is truncated in {owner}'s section {filled / SectionBytes}:"
                    + $" wanted {SectionBytes} bytes, got {filled % SectionBytes}.");
            }

            filled += got;
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> bytes = table.AsSpan(i * SectionBytes, SectionBytes);
            string tag = KnownTag(bytes[..4]) ?? Encoding.ASCII.GetString(bytes[..4]);
            if (!IsTag(tag))
            {
                throw new LinkException(
                    $"room pack: {owner}'s section {i} has tag bytes {Convert.ToHexString(bytes[..4])}; a tag is four printable ASCII characters.");
            }

            if (!tags.Add(tag))
            {
                throw new LinkException($"room pack: {owner} has two \"{tag}\" sections.");
            }

            long offset = BinaryPrimitives.ReadInt64BigEndian(bytes[4..]);
            long length = BinaryPrimitives.ReadInt64BigEndian(bytes[12..]);
            if (length is < 0 or > int.MaxValue)
            {
                throw new LinkException(
                    $"room pack: {owner}'s \"{tag}\" section is {length} bytes; a section is 0 to {int.MaxValue}.");
            }

            sections.Add(new RoomPackSection(tag, offset, length));
        }

        return sections;
    }

    /// <summary>
    /// One section's bytes. On a seekable stream the index was checked
    /// against the file's length, so the length is real and is allocated at
    /// once; on any other stream it is an untrusted number, and the bytes are
    /// collected as they arrive, so a lying index fails as a truncation
    /// instead of asking for gigabytes up front.
    /// </summary>
    private static async Task<byte[]> ReadSectionAsync(
        Stream r, RoomPackSection section, string what, CancellationToken cancellationToken)
    {
        if (r.CanSeek || section.Length <= ChunkBytes)
        {
            return await ReadExactAsync(r, (int)section.Length, what, cancellationToken).ConfigureAwait(false);
        }

        using MemoryStream collected = new();
        byte[] chunk = new byte[ChunkBytes];
        long left = section.Length;
        while (left > 0)
        {
            int read = await r.ReadAsync(chunk.AsMemory(0, (int)Math.Min(left, chunk.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new LinkException(
                    $"room pack is truncated in {what}: wanted {section.Length} bytes, got {section.Length - left}.");
            }

            collected.Write(chunk, 0, read);
            left -= read;
        }

        return collected.ToArray();
    }

    private static async Task SkipAsync(Stream r, long count, string what, CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[(int)Math.Min(Math.Max(count, 0), ChunkBytes)];
        long left = count;
        while (left > 0)
        {
            int read = await r.ReadAsync(scratch.AsMemory(0, (int)Math.Min(left, scratch.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new LinkException($"room pack is truncated before {what}.");
            }

            left -= read;
        }
    }

    private static async Task<int> ReadInt32Async(Stream r, string what, CancellationToken cancellationToken) =>
        BinaryPrimitives.ReadInt32BigEndian(await ReadExactAsync(r, 4, what, cancellationToken).ConfigureAwait(false));

    private static async Task<byte[]> ReadExactAsync(Stream r, int count, string what, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            int read = await r.ReadAsync(bytes.AsMemory(filled, count - filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new LinkException($"room pack is truncated in {what}: wanted {count} bytes, got {filled}.");
            }

            filled += read;
        }

        return bytes;
    }

    private static void WriteInt32(Stream w, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        w.Write(bytes);
    }

    private static void WriteInt64(Stream w, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        w.Write(bytes);
    }
}
