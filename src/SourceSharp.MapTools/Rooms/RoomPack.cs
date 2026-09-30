//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Text;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;

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
    /// the link work done ahead for it and its navigation
    /// (<see cref="CreateAsync(RoomObject, RoomNavPackOptions, CancellationToken)"/>), or none.
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
    /// none, and is packed with its container, its entity counts and its
    /// names alone:
    /// a level that places it is refused at link time with the message it
    /// always got. Every room gets its entity counts
    /// (<see cref="RoomEntityCounts"/>). See <see cref="RoomPack"/> for the
    /// sections this adds.
    /// </para>
    /// <para>
    /// The bytes are a function of the room: the same room gives the same
    /// item at any thread count.
    /// </para>
    /// </remarks>
    public static Task<RoomPackItem> CreateAsync(RoomObject room, CancellationToken cancellationToken = default) =>
        CreateAsync(room, new RoomNavPackOptions(), cancellationToken);

    /// <summary>
    /// The pack item for a compiled room, with its navigation stored as the
    /// options say: the link sections of
    /// <see cref="CreateAsync(RoomObject, CancellationToken)"/>, and the
    /// room's <see cref="RoomObject.Nav"/> as <c>NVR</c><i>r</i> sections
    /// (<see cref="RoomNavSection"/>) when it has one.
    /// </summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="navigation">Which turns of the navigation to store, and how.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The item, named for the room.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Each turn's <c>NVR</c><i>r</i> goes right after that turn's link
    /// sections (<c>GEO</c><i>r</i>, <c>COL</c><i>r</i>, <c>ENT</c><i>r</i>),
    /// so a link placing the room at one turn reads the container, the shared
    /// section, the turn's link sections and its navigation as one run of
    /// bytes (<see cref="RoomPack.LoadRoomsAsync(Stream, RoomPackIndex, IReadOnlyList{RoomPackRequest}, CancellationToken)"/>).
    /// </remarks>
    public static async Task<RoomPackItem> CreateAsync(
        RoomObject room, RoomNavPackOptions navigation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(navigation);
        using MemoryStream container = new();
        await RoomObjectStore.SaveAsync(room, container, cancellationToken).ConfigureAwait(false);
        RoomLinkData? link = room.Link is { } stored && stored.IsFor(room)
            ? stored
            : await LevelLinker.TryPrecomputeAsync(room, cancellationToken).ConfigureAwait(false);

        // Link data read from a version 3 pack holds no door visibility; a
        // version 4 pack promises it for every room with link sections.
        if (link is { Doors: null })
        {
            link = link.WithDoors(RoomDoorVisibility.Compute(room, link.Shared));
        }
        IReadOnlyList<RoomPackSectionData> linkSections = link is null ? [] : RoomLinkSections.Write(link);
        IReadOnlyList<RoomPackSectionData> navSections = room.Nav is { } nav ? RoomNavPack.Sections(nav.Base, navigation) : [];

        // The names per turn (NAMr), after each turn's link sections so a
        // link placing the room at one turn still reads its sections in one
        // run. Every room has them, as it has counts: ssmap rooms lists a
        // room's names whatever the link will say of it.
        RoomNameTables names = room.Names is { } stored2 && stored2.IsFor(room) && stored2.IsComplete
            ? stored2
            : new RoomNameTables(RoomNameAnalysis.Analyse(room.Definition.Name, room.Bsp, null), room.Bsp);
        IReadOnlyList<RoomPackSectionData> turned = RoomNavPack.Interleave(linkSections, [.. names.Sections()]);

        // The counts first, straight after the container: every room has
        // them (the link's verdict does not matter to a count), and a link
        // reads them with the container and the link sections in one run.
        RoomPackSectionData counts = RoomEntityCounts.Of(room.Bsp).ToSection();

        // A shaped room's shape (17.6), right after the counts: the link
        // reads it before the room's container is checked. A cube room gets
        // none, so its entry is what it was before heights.
        IReadOnlyList<RoomPackSectionData> shape = RoomShape.ToSection(room.Definition) is { } shaped ? [shaped] : [];

        // The static props, when the room has any, right after the counts:
        // the link reads them for every placement whatever its turn (the
        // section holds all four), so with the container and the counts.
        IReadOnlyList<RoomPackSectionData> props = room.StaticProps is { } staticProps ? [staticProps.ToSection()] : [];

        // The brush models likewise: every placement reads them, whatever
        // its turn, so they follow the props.
        IReadOnlyList<RoomPackSectionData> brushModels = room.BrushModelsOfCompile is { } models ? [models.ToSection()] : [];

        // The transition data likewise, for a room with a role or spawn
        // points: a handful of points every placement reads. A room without
        // it gets no section, so its entry is what it was before transitions.
        IReadOnlyList<RoomPackSectionData> transit = room.TransitOfCompile is { } data ? [data.ToSection()] : [];

        // The cubemap samples likewise, for a room with any: every
        // placement reads them (the section holds all four turns), and a
        // room without samples gets no section, so its entry is what it was
        // before cubemaps were carried.
        IReadOnlyList<RoomPackSectionData> cubemaps = room.CubemapsOfCompile is { } samples ? [samples.ToSection()] : [];

        // The overlays likewise, for a room whose compile wrote any: every
        // placement reads them, whatever its turn (the section holds all
        // four). A room without them gets no section, so its entry is what
        // it was before overlays were carried.
        IReadOnlyList<RoomPackSectionData> overlays = room.OverlaysOfCompile is { } carried ? [carried.ToSection()] : [];

        // The base lighting likewise, for a room its library compile lit:
        // every placement reads it (all its stored turns are in the one
        // section, 1 or 4, and the link takes the one it places). A room
        // compiled unlit gets none, so an unlit library packs as before.
        IReadOnlyList<RoomPackSectionData> lighting = room.LightingOfCompile is { } baked ? [baked.ToSection()] : [];

        // And its door light, for a lit room whose library compile recorded
        // it (what leaves through each opening and what light entering one
        // does): the link adds each joint's light from it. None for an unlit
        // room, or one compiled with the door light off.
        IReadOnlyList<RoomPackSectionData> doorLight = room.DoorLightOfCompile is { } door ? [door.ToSection()] : [];

        // The area portals likewise, for a room whose compile has any. A
        // room without them gets no section, so its entry is what it was
        // before area portals were carried.
        IReadOnlyList<RoomPackSectionData> areaPortals = room.AreaPortalsOfCompile is { } portals ? [portals.ToSection()] : [];

        // The water likewise, for a room whose compile has any. A room
        // without it gets no section, so its entry is what it was before
        // water was carried.
        IReadOnlyList<RoomPackSectionData> water = room.WaterOfCompile is { } carriedWater ? [carriedWater.ToSection()] : [];

        // The room's part of the level map (MAPV, the rooms design, 18.3):
        // every placement reads it, whatever its turn (it is stored once and
        // turned at link). A room compiled without it (a room built outside
        // a library compile) gets none, and a level placing it links without
        // a map, saying so.
        IReadOnlyList<RoomPackSectionData> mapView = room.MapViewOfCompile is { } view ? [view.ToSection()] : [];
        return new RoomPackItem(room.Definition.Name, container.ToArray())
        {
            Extra = [counts, .. shape, .. props, .. brushModels, .. transit, .. cubemaps, .. overlays, .. lighting, .. doorLight, .. areaPortals, .. water, .. mapView, .. RoomNavPack.Interleave(turned, navSections)],
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
public sealed record RoomPackRequest(string Name, IReadOnlyCollection<int> Rotations)
{
    /// <summary>
    /// Whether to read the room's navigation too: the <c>NVR</c><i>r</i>
    /// section of each turn asked for (turn 0's when the pack stores no
    /// turned copy), into <see cref="RoomObject.Nav"/>. Off by default, so a
    /// link that writes no navigation reads not a byte of it.
    /// </summary>
    public bool Navigation { get; init; }
}

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
        IReadOnlyList<RoomPackSection> librarySections, IReadOnlyList<RoomPackEntry> entries, long indexEnd, long? start, int version = RoomPack.Version)
    {
        Version = version;
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

    /// <summary>
    /// The sections that belong to the whole library rather than to one
    /// room, looked up by tag and in any order: the pack's compile id
    /// (<see cref="RoomCompileIds.PackSection"/>, <c>CMPL</c>) when
    /// <c>ssmap room</c> wrote the pack, and the library-wide entities
    /// (<see cref="RoomLibraryEntities.SectionTag"/>) when the library has any.
    /// </summary>
    public IReadOnlyList<RoomPackSection> LibrarySections { get; }

    /// <summary>The rooms, in the order the pack holds them (the library's).</summary>
    public IReadOnlyList<RoomPackEntry> Entries { get; }

    /// <summary>
    /// The pack's format version: <see cref="RoomPack.Version"/>, or
    /// <see cref="RoomPack.OldestReadVersion"/> for an older pack this build
    /// still links (<see cref="RoomPack"/>'s remarks on versions).
    /// </summary>
    public int Version { get; }

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
/// <b>Layout, versions 4 and 5</b> (as versions 1 to 3's; see Versions below). Every integer is big-endian, as in the room
/// container, so the bytes do not depend on the writer's byte order. A tag
/// is four printable ASCII characters, stored as they read.
/// </para>
/// <list type="table">
/// <listheader><term>Bytes</term><description>What</description></listheader>
/// <item><term>8</term><description>The magic, <c>SSRPAK01</c> in ASCII (<see cref="Magic"/>).</description></item>
/// <item><term>4</term><description><c>int32</c> format version (<see cref="Version"/>).</description></item>
/// <item><term>4</term><description><c>int32</c> library section count, 0 to <see cref="MaxSections"/>: <c>ssmap room</c> writes the compile id (<see cref="RoomCompileIds.PackSection"/>, <c>CMPL</c>), then the library-wide entities (<see cref="RoomLibraryEntities.SectionTag"/>) when the library has any, then its settings (<see cref="RoomLibraryOptions.SectionTag"/>) when it makes any, then the name of its skybox room (<see cref="RoomLibrarySkybox.SectionTag"/>) when it has one.</description></item>
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
/// A room <c>ssmap room</c> packs then has its entity counts
/// (<c>ECNT</c>, <see cref="RoomEntityCounts"/>), when it is taller or lower
/// than its cell its shape (<c>SHAP</c>: its height, footprint and sockets'
/// cells, <see cref="RoomShape"/>), when its compile emitted static
/// props its props as the link carries them (<c>PROP</c>: the models' hulls,
/// the keys vbsp consumed and every prop's pose at all four turns,
/// <c>RoomStaticProps</c>), when its compile has brush models besides the
/// world its brush models (<c>BMOD</c>: the runs each owns, what keeps it
/// in a level, its bounds and collision at all four turns,
/// <c>RoomBrushModels</c>), when it has a transition role or spawn points
/// its transition data (<c>TRAN</c>: its role, transition volume, fold
/// trigger, arrival and spawn points, room-local, <c>RoomTransit</c>), when
/// its compile has <c>env_cubemap</c> samples its cubemaps (<c>CUBE</c>: the
/// samples at all four turns and the names its compile made after them,
/// <c>RoomCubemaps</c>), when its compile wrote overlays its overlays
/// (<c>OVLY</c>: every record's origin and basis at all four turns,
/// <c>RoomOverlays</c>), when its compile has area portals its areas and
/// portals (<c>APRT</c>: the clip vertices at all four turns and the portal
/// numbers, <c>RoomAreaPortals</c>), when its compile has water its water
/// (<c>WATR</c>: the water data counted, the fluids' convexes and the water
/// overlays at all four turns, <c>RoomWater</c>), and the link work done ahead for it
/// (<see cref="RoomPackItem.CreateAsync(RoomObject, RoomNavPackOptions, CancellationToken)"/>): <c>LNKA</c>, what depends on
/// the room alone, its door visibility (<c>DVIS</c>, <see cref="RoomDoorVisibility"/>),
/// then per quarter turn <i>r</i> its turned geometry
/// (<c>GEO</c><i>r</i>) and world collision (<c>COL</c><i>r</i>), and
/// optionally its turned entities (<c>ENT</c><i>r</i>); <c>RoomLinkSections</c>
/// gives their layout and why each is stored or not. They are optional: a
/// room the link would refuse has none, and a pack written before them
/// links to the same bytes, the link computing the same data on the fly.
/// Each turn's link sections are followed by that turn's names
/// (<c>NAM</c><i>r</i>, <see cref="RoomNameTurn"/>; every room has them, a
/// room the link refuses too, since <c>ssmap rooms</c> lists them), and when
/// the library builds navigation, by that turn's
/// <see cref="RoomNavSection"/> (<c>NVR</c><i>r</i>; all four
/// turns by default, <c>NVR0</c> alone with <c>-nav-turn0</c>).
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
/// of their own, which holds the library-wide entities when the library
/// has any (<see cref="RoomLibraryEntities.SectionTag"/>, the sun, fog and
/// the like from the gaps between cells). A reader looks sections up by tag
/// and ignores tags it does not know, so a pack that gains an optional
/// section is still read by an older build, and that is why neither the
/// link sections nor the library-wide entities section changed
/// <see cref="Version"/>; a change an older build must not read around (a
/// different container, a section it cannot ignore, a promise about the
/// rooms the link relies on, as versions 2 and 3's below) raises it.
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
/// <see cref="RoomLibraryVmf"/>), valid by <see cref="RoomNameTables"/>, and
/// looked up exactly. An entry's container must hold the room its index
/// entry names; a pack whose index and containers disagree is refused.
/// </para>
/// <para>
/// <b>Versions.</b> This build writes version 5 for a pack holding a shaped
/// room and version 4 for any other (<see cref="CubeVersion"/>), reads
/// versions 3 to 5, and refuses any other with the version it carries and
/// the newest it reads (<see cref="CheckVersion"/>), as the room container
/// does; the containers inside carry their
/// own version and are checked by <see cref="RoomObjectStore.LoadAsync"/>.
/// Versions 2 to 4 have the layout of version 1; what each adds is a
/// promise about the rooms. Version 2: the pack was built after the library's singletons were
/// checked (<see cref="RoomLibraryEntities.KeepInRoom"/>), so no room
/// carries a sun or an unnamed controller of its own, every room agrees
/// with the library's sun and sky (decision D3 of the rooms design: a room
/// that disagrees is refused when the pack is built), and the library's own
/// are in <see cref="RoomLibraryEntities.SectionTag"/>. Version 3: every
/// room's pak holds what vbsp packs for it, the default cubemaps built from
/// the library's sky included, and the link carries those files into the
/// level (<see cref="LevelPakFiles"/>). The rooms of a version 2 pack were
/// compiled with the default cubemaps left out (the link then refused any
/// packed file), so a level linked from one would silently lack the
/// defaults the same library recompiled gives it. An older pack makes a
/// promise short of this one and the link cannot check the difference (it
/// has the rooms' compiled lumps, not what their compile would have packed),
/// so it is refused with a message that says what it lacks and to recompile
/// the library, rather than read around. That is the kind of change the
/// paragraph above keeps a version for: tags an older build can skip never
/// raised it, a guarantee the link relies on does. Version 4: every room
/// the link carries (every room with link sections) has its door
/// visibility (<c>DVIS</c>), which the link composes the level's PVS from
/// (<see cref="LevelDoorVisibility"/>), so the link reads it rather than
/// working it out, and a version 4 room with link sections and no
/// <c>DVIS</c> is a damaged pack and refused. A version 3 pack still
/// links: its rooms promise everything but the door visibility, which is
/// a function of what the pack does hold (each room's own vvis and plug
/// census), so the link works it out per room and writes the same bytes
/// it writes from the same library packed as version 4. Version 5 (the rooms
/// design, 17.11): some room is shaped and carries <c>SHAP</c>, which an
/// older build would skip and link the room as a cube, its top tree's
/// bounds one cell tall; so a pack holding one takes the next version, and
/// an older build refuses it by its version check. A pack of cube rooms is
/// still written at version 4, its bytes what they were, and a <c>SHAP</c>
/// section in a pack of version 4 or older is refused as damage.
/// </para>
/// </remarks>
public static class RoomPack
{
    /// <summary>The pack's eight magic bytes, as they read in the file.</summary>
    public const string Magic = "SSRPAK01";

    /// <summary>
    /// The newest pack version this build writes and reads: the version of a
    /// pack holding a shaped room (<see cref="RoomShape"/>; <see cref="RoomPack"/>'s
    /// remarks on versions).
    /// </summary>
    public const int Version = 5;

    /// <summary>
    /// The version a pack of cube rooms is written at: version 4, as before
    /// heights, so such a pack keeps its bytes and every build that read it
    /// still does.
    /// </summary>
    public const int CubeVersion = 4;

    /// <summary>
    /// The one older version this build still reads: version 3, whose rooms
    /// lack only the door visibility a version 4 pack stores, which the link
    /// works out from the rooms themselves, to the same bytes.
    /// </summary>
    public const int OldestReadVersion = 3;

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
    /// sections.
    /// </summary>
    /// <param name="librarySections">
    /// The sections that belong to the whole library, in the order they are
    /// written: <c>ssmap room</c> writes the compile id
    /// (<see cref="RoomCompileIds.PackSection"/>), then
    /// <see cref="RoomLibraryEntities.SectionTag"/> when the library has
    /// library-wide entities.
    /// </param>
    /// <param name="rooms">The rooms and their container bytes.</param>
    /// <param name="w">The stream to write to, positioned where the pack starts; the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once every byte is in the stream.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// Too many rooms or sections, a tag that is not four printable ASCII
    /// characters or appears twice, a name that is not a room name or is too
    /// long, or two names equal ignoring case.
    /// </exception>
    /// <remarks>
    /// Public so a host that packs a library itself can write what
    /// <c>ssmap room</c> writes. Rooms' further sections stay internal until
    /// a build writes one.
    /// </remarks>
    public static async Task SaveAsync(
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

        // Version 5 only for a pack holding a shaped room; a pack of cubes
        // keeps version 4 and its bytes (the remarks on versions).
        bool shaped = sections.Any(room => room.Any(section => section.Tag == RoomShape.SectionTag));
        using MemoryStream header = new((int)Math.Min(indexEnd, int.MaxValue));
        header.Write(Encoding.ASCII.GetBytes(Magic));
        WriteInt32(header, shaped ? Version : CubeVersion);
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
        CheckVersion(version, Version);

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

        return new RoomPackIndex(library, entries, position, start, version);
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
        HashSet<string> navigation = new(StringComparer.Ordinal);
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

            if (request.Navigation)
            {
                navigation.Add(request.Name);
            }
        }

        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (string name in order)
        {
            (RoomPackEntry entry, bool[] turns) = rooms[name];
            wanted.Add((name, entry.Room));
            if (entry.Find(RoomEntityCounts.SectionTag) is { } counts)
            {
                wanted.Add((name, counts));
            }

            if (entry.Find(RoomShape.SectionTag) is { } shape)
            {
                // Only a version 5 pack holds shaped rooms: a shape in an
                // older one was not written by any build that wrote that
                // version, so the pack is damaged.
                if (index.Version < Version)
                {
                    throw new LinkException(
                        $"room pack entry \"{name}\" has a \"{RoomShape.SectionTag}\" section in a version {index.Version} pack;"
                        + $" only a version {Version} pack holds shaped rooms, so the pack is damaged; recompile the library with ssmap room.");
                }

                wanted.Add((name, shape));
            }

            if (entry.Find(RoomStaticProps.SectionTag) is { } props)
            {
                wanted.Add((name, props));
            }

            if (entry.Find(RoomBrushModels.SectionTag) is { } brushModels)
            {
                wanted.Add((name, brushModels));
            }

            if (entry.Find(RoomTransit.SectionTag) is { } transit)
            {
                wanted.Add((name, transit));
            }

            if (entry.Find(RoomCubemaps.SectionTag) is { } cubemaps)
            {
                wanted.Add((name, cubemaps));
            }

            if (entry.Find(RoomOverlays.SectionTag) is { } overlays)
            {
                wanted.Add((name, overlays));
            }

            if (entry.Find(RoomLighting.SectionTag) is { } lighting)
            {
                wanted.Add((name, lighting));
            }

            if (entry.Find(RoomDoorLight.SectionTag) is { } doorLight)
            {
                wanted.Add((name, doorLight));
            }

            if (entry.Find(RoomAreaPortals.SectionTag) is { } areaPortals)
            {
                wanted.Add((name, areaPortals));
            }

            if (entry.Find(RoomWater.SectionTag) is { } water)
            {
                wanted.Add((name, water));
            }

            // The room's map: a few hundred bytes every link reads with the
            // room, whether or not it writes a .map2d (the room is loaded
            // whole, as the combiner and the cache hand it on).
            if (entry.Find(RoomMapView.SectionTag) is { } mapView)
            {
                wanted.Add((name, mapView));
            }

            if (navigation.Contains(name))
            {
                HashSet<string> tags = new(StringComparer.Ordinal);
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    if (!turns[rotation])
                    {
                        continue;
                    }

                    foreach (RoomPackSection nav in RoomNavPack.SectionsFor(entry, rotation))
                    {
                        if (tags.Add(nav.Tag))
                        {
                            wanted.Add((name, nav));
                        }
                    }
                }
            }

            for (int rotation = 0; rotation < 4; rotation++)
            {
                if (turns[rotation] && entry.Find(RoomNameTurn.Tag(rotation)) is { } names)
                {
                    wanted.Add((name, names));
                }
            }

            if (entry.Find(RoomLinkSections.SharedTag) is { } shared)
            {
                wanted.Add((name, shared));
                if (entry.Find(RoomDoorVisibility.SectionTag) is { } doors)
                {
                    wanted.Add((name, doors));
                }
                else if (index.Version >= 4)
                {
                    // Version 4's promise: every room the link carries (a
                    // room with link sections) has its door visibility.
                    throw new LinkException(
                        $"room pack entry \"{name}\" has link sections but no \"{RoomDoorVisibility.SectionTag}\" section,"
                        + $" which every linkable room of a version {index.Version} pack holds; the pack is damaged, recompile the library with ssmap room.");
                }

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
            RoomShapeData? shape = RoomShape.Read(Section(name, RoomShape.SectionTag), name);
            try
            {
                room = await RoomObjectStore.LoadShapedAsync(container, shape, cancellationToken).ConfigureAwait(false);
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
            RoomEntityCounts? counts = RoomEntityCounts.Read(Section(name, RoomEntityCounts.SectionTag), name)?.For(room.Bsp);
            RoomNavTurns? nav = navigation.Contains(name) ? RoomNavPack.FromSections(name, tag => Section(name, tag)) : null;
            RoomNameTurn?[] turned = [.. Enumerable.Range(0, 4).Select(t => RoomNameTurn.Read(Section(name, RoomNameTurn.Tag(t)), name, t))];
            RoomNameTables? names = turned.Any(t => t is not null) ? new RoomNameTables(turned, room.Bsp) : null;
            RoomStaticProps? props = RoomStaticProps.Read(Section(name, RoomStaticProps.SectionTag), room.Definition, room.Bsp);
            RoomBrushModels? brushModels = RoomBrushModels.Read(Section(name, RoomBrushModels.SectionTag), room.Definition, room.Bsp);
            RoomTransit? transit = RoomTransit.Read(Section(name, RoomTransit.SectionTag), name, room.Bsp);
            RoomLighting? lighting = RoomLighting.Read(Section(name, RoomLighting.SectionTag), room.Definition, room.Bsp);
            RoomDoorLight? doorLight = RoomDoorLight.Read(Section(name, RoomDoorLight.SectionTag), room.Definition, room.Bsp, lighting);
            RoomCubemaps? cubemaps = RoomCubemaps.Read(Section(name, RoomCubemaps.SectionTag), name, room.Bsp);
            RoomOverlays? overlays = RoomOverlays.Read(Section(name, RoomOverlays.SectionTag), name, room.Bsp);
            RoomAreaPortals? areaPortals = RoomAreaPortals.Read(Section(name, RoomAreaPortals.SectionTag), name, room.Bsp);
            RoomWater? water = RoomWater.Read(Section(name, RoomWater.SectionTag), name, room.Bsp);
            RoomMapView? mapView = RoomMapView.Read(Section(name, RoomMapView.SectionTag), room.Definition, room.Bsp);
            loaded[name] = link is null && nav is null && counts is null && names is null && props is null && brushModels is null && transit is null
                && cubemaps is null && overlays is null
                && lighting is null && doorLight is null && areaPortals is null && water is null && mapView is null
                ? room
                : room with
                {
                    Link = link, Nav = nav, EntityCounts = counts, Names = names, Props = props, BrushModels = brushModels, Transit = transit,
                    Cubemaps = cubemaps,
                    Overlays = overlays,
                    Lighting = lighting,
                    DoorLight = doorLight,
                    AreaPortals = areaPortals,
                    Water = water,
                    MapView = mapView,
                };
        }

        return [.. requests.Select(request => loaded[request.Name])];
    }

    /// <summary>
    /// Reads the library's settings from a pack whose index was just read:
    /// its <see cref="RoomLibraryOptions.SectionTag"/> section, or
    /// <see cref="RoomLibraryOptions.None"/> when it has none.
    /// </summary>
    /// <param name="r">
    /// The pack. A stream that cannot seek must be where
    /// <see cref="ReadIndexAsync"/> left it, and is left past the section,
    /// where the room readers cannot start from: read a forward-only pack's
    /// rooms from a second pass.
    /// </param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The library's settings.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">The section is cut short or out of shape (<see cref="RoomLibraryOptions.Read"/>).</exception>
    /// <remarks>
    /// What <c>ssmap link</c> and <c>ssmap layout</c> read to learn the
    /// library's entity reserve without the library VMF.
    /// </remarks>
    public static async Task<RoomLibraryOptions> ReadLibraryOptionsAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await ReadLibrarySectionAsync(r, index, RoomLibraryOptions.SectionTag, cancellationToken).ConfigureAwait(false);
        return bytes is null ? RoomLibraryOptions.None : RoomLibraryOptions.Read(bytes);
    }

    /// <summary>
    /// Reads the library-wide entities from a pack whose index was just
    /// read: its <see cref="RoomLibraryEntities.SectionTag"/> section, or
    /// none when it has none.
    /// </summary>
    /// <param name="r">
    /// The pack. A stream that cannot seek must be where
    /// <see cref="ReadIndexAsync"/> left it, and is left past the section,
    /// as <see cref="ReadLibraryOptionsAsync"/> leaves it: read a
    /// forward-only pack's other sections from a second pass.
    /// </param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entities, in library order and as the library wrote them.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">The section is cut short or out of shape (<see cref="RoomLibraryEntities.ReadAsync"/>).</exception>
    /// <remarks>
    /// What <c>ssmap link</c> reads to write the level's singletons
    /// (<see cref="RoomLibrary.LibraryEntities"/>), and what <c>ssmap
    /// layout</c> and <c>ssmap rooms</c> read to count them.
    /// </remarks>
    public static async Task<IReadOnlyList<VmfChunk>> ReadLibraryEntitiesAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await ReadLibrarySectionAsync(r, index, RoomLibraryEntities.SectionTag, cancellationToken).ConfigureAwait(false);
        return bytes is null ? [] : await RoomLibraryEntities.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the name of the library's skybox room from a pack whose index
    /// was just read: its <see cref="RoomLibrarySkybox.SectionTag"/> section,
    /// or null when the library has no skybox.
    /// </summary>
    /// <param name="r">
    /// The pack, as for <see cref="ReadLibraryOptionsAsync"/>: a stream that
    /// cannot seek is read forward, in the order <c>ssmap room</c> writes the
    /// library sections (the skybox after the settings).
    /// </param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The skybox room's name, or null.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">The section is cut short or out of shape (<see cref="RoomLibrarySkybox.Read"/>).</exception>
    public static async Task<string?> ReadLibrarySkyboxAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await ReadLibrarySectionAsync(r, index, RoomLibrarySkybox.SectionTag, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : RoomLibrarySkybox.Read(bytes);
    }

    /// <summary>
    /// Reads a pack's namespaces from a pack whose index was just read: its
    /// <see cref="RoomPackNamespaces.SectionTag"/> section, held to the index
    /// (<see cref="RoomPackNamespaces.Check"/>), or null for a plain pack.
    /// </summary>
    /// <param name="r">
    /// The pack, as for <see cref="ReadLibraryOptionsAsync"/>: a stream that
    /// cannot seek is read forward, in the order the library sections are
    /// written (the namespaces last).
    /// </param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The namespaces in library order, or null when the pack has none (or a revision of them this build does not read).</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">The section is cut short, out of shape, or does not match the index.</exception>
    /// <remarks>
    /// What <c>ssmap link</c>, <c>ssmap rooms</c> and <c>ssmap layout</c>
    /// read to find a library's rooms in a combined pack, and what
    /// <c>ssmap roompack -only</c> reads to copy the namespaces it keeps.
    /// </remarks>
    public static async Task<IReadOnlyList<RoomPackNamespace>?> ReadNamespacesAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await ReadLibrarySectionAsync(r, index, RoomPackNamespaces.SectionTag, cancellationToken).ConfigureAwait(false);
        if (bytes is null || RoomPackNamespaces.Read(bytes) is not { } namespaces)
        {
            return null;
        }

        RoomPackNamespaces.Check(namespaces, index);
        return namespaces;
    }

    /// <summary>
    /// Reads a run of a pack's rooms whole, every section byte for byte, as
    /// items to write into another pack: what <c>ssmap roompack -only</c>
    /// copies of the namespaces it does not rebuild.
    /// </summary>
    /// <param name="r">The pack; it must be able to seek.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="first">The first room's index in the pack.</param>
    /// <param name="count">How many rooms, from <paramref name="first"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rooms, in pack order, each with every section it has in the pack, in its order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The run is not inside the pack.</exception>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    /// <exception cref="LinkException">The pack is cut short in a section.</exception>
    /// <remarks>
    /// The sections are not decoded, so a copied room is exactly the room the
    /// pack held, and a pack written from the items puts the same bytes under
    /// the same tags. The rooms' sections run back to back in the pack
    /// (<see cref="RoomPack"/>'s one layout), so the run is read in one piece
    /// and cut up.
    /// </remarks>
    public static async Task<IReadOnlyList<RoomPackItem>> ReadItemsAsync(
        Stream r, RoomPackIndex index, int first, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)first + count, index.Entries.Count, nameof(count));
        if (index.Start is not long start)
        {
            throw new NotSupportedException("copying rooms out of a room pack needs a stream that can seek.");
        }

        if (count == 0)
        {
            return [];
        }

        long from = index.Entries[first].Sections[0].Offset;
        RoomPackSection last = index.Entries[first + count - 1].Sections[^1];
        long length = last.Offset + last.Length - from;
        r.Seek(start + from, SeekOrigin.Begin);
        byte[] run = await ReadSectionAsync(r, new RoomPackSection(RoomSection, from, length), "the copied rooms", cancellationToken).ConfigureAwait(false);
        List<RoomPackItem> items = [];
        for (int i = first; i < first + count; i++)
        {
            RoomPackEntry entry = index.Entries[i];
            ReadOnlyMemory<byte> Slice(RoomPackSection section) => run.AsMemory((int)(section.Offset - from), (int)section.Length);
            items.Add(new RoomPackItem(entry.Name, Slice(entry.Sections[0]))
            {
                Extra = [.. entry.Sections.Skip(1).Select(s => new RoomPackSectionData(s.Tag, Slice(s)))],
            });
        }

        return items;
    }

    /// <summary>One library section's bytes, or null when the pack has no section of that tag.</summary>
    private static async Task<byte[]?> ReadLibrarySectionAsync(Stream r, RoomPackIndex index, string tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        RoomPackSection? found = null;
        foreach (RoomPackSection section in index.LibrarySections)
        {
            if (section.Tag == tag)
            {
                found = section;
            }
        }

        if (found is not { } wanted)
        {
            return null;
        }

        string what = $"the library's \"{wanted.Tag}\" section";
        if (index.Start is long start)
        {
            r.Seek(start + wanted.Offset, SeekOrigin.Begin);
        }
        else
        {
            await SkipAsync(r, wanted.Offset - index.IndexEnd, what, cancellationToken).ConfigureAwait(false);
        }

        return await ReadSectionAsync(r, wanted, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every room's entity counts from a pack whose index was just
    /// read, and nothing else of the rooms.
    /// </summary>
    /// <param name="r">The pack, as <see cref="ReadRoomBytesAsync"/> takes it.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The counts of every room that has a readable
    /// <see cref="RoomEntityCounts.SectionTag"/> section, by name; a room
    /// packed before the counts existed, or with a revision this build does
    /// not read, is missing.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">A section cut short or out of shape, naming its room.</exception>
    /// <remarks>
    /// What <c>ssmap rooms</c> lists and <c>ssmap layout</c> budgets with:
    /// a few bytes a room, read in pack order.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, RoomEntityCounts>> ReadEntityCountsAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (entry.Find(RoomEntityCounts.SectionTag) is { } section)
            {
                wanted.Add((entry.Name, section));
            }
        }

        Dictionary<(string, string), ArraySegment<byte>> read = await ReadSectionsAsync(r, index, wanted, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, RoomEntityCounts> counts = new(StringComparer.Ordinal);
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (read.TryGetValue((entry.Name, RoomEntityCounts.SectionTag), out ArraySegment<byte> bytes)
                && RoomEntityCounts.Read(bytes, entry.Name) is { } room)
            {
                counts[entry.Name] = room;
            }
        }

        return counts;
    }

    /// <summary>
    /// Reads every room's names as the room compile found them (its
    /// <c>NAM0</c> section: turn 0 is the room's own frame), for
    /// <c>ssmap rooms</c> to list.
    /// </summary>
    /// <param name="r">The pack; it must be able to seek.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The names of every room with a readable section, by name; a room
    /// packed before the names existed, or with a revision this build does
    /// not read, is missing.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LinkException">A section cut short or out of shape, naming its room.</exception>
    public static async Task<IReadOnlyDictionary<string, RoomNameSummary>> ReadNameSummariesAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        string tag = RoomNameTurn.Tag(0);
        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (entry.Find(tag) is { } section)
            {
                wanted.Add((entry.Name, section));
            }
        }

        Dictionary<(string, string), ArraySegment<byte>> read = await ReadSectionsAsync(r, index, wanted, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, RoomNameSummary> names = new(StringComparer.Ordinal);
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (read.TryGetValue((entry.Name, tag), out ArraySegment<byte> bytes)
                && RoomNameTurn.Read(bytes, entry.Name, 0) is { } turn)
            {
                names[entry.Name] = RoomNameSummary.Of(turn);
            }
        }

        return names;
    }

    /// <summary>
    /// How many turns each lit room of a pack stores its base lighting for
    /// (<see cref="RoomLighting.RotationCount"/>: 1, or 4 when sun or sky
    /// light reaches it), by name: what <c>ssmap rooms</c> shows. A room
    /// without a lighting section (an unlit library) is not listed.
    /// </summary>
    /// <param name="r">The pack.</param>
    /// <param name="index">Its index.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The counts, by room name.</returns>
    /// <exception cref="LinkException">A lighting section is damaged.</exception>
    public static async Task<IReadOnlyDictionary<string, int>> ReadLightingTurnsAsync(
        Stream r, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        List<(string Name, RoomPackSection Section)> wanted = [];
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (entry.Find(RoomLighting.SectionTag) is { } section)
            {
                wanted.Add((entry.Name, section));
            }
        }

        Dictionary<(string, string), ArraySegment<byte>> read = await ReadSectionsAsync(r, index, wanted, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, int> turns = new(StringComparer.Ordinal);
        foreach (RoomPackEntry entry in index.Entries)
        {
            if (read.TryGetValue((entry.Name, RoomLighting.SectionTag), out ArraySegment<byte> bytes)
                && RoomLighting.ReadRotationCount(bytes, entry.Name) is int count)
            {
                turns[entry.Name] = count;
            }
        }

        return turns;
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

    /// <summary>
    /// Reads one section's bytes, a library's or a room's, from a pack whose
    /// index was just read: the link's way to the sections beside a room's
    /// container (its navigation) and the library's (its compile id), read
    /// only when wanted.
    /// </summary>
    /// <param name="r">The pack; it must be able to seek.</param>
    /// <param name="index">The pack's index, read from <paramref name="r"/>.</param>
    /// <param name="section">The section, from <see cref="RoomPackIndex.LibrarySections"/> or an entry's <see cref="RoomPackEntry.Sections"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The section's bytes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    /// <exception cref="LinkException">The pack is cut short in the section.</exception>
    public static async Task<byte[]> ReadSectionAsync(
        Stream r, RoomPackIndex index, RoomPackSection section, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Start is not long start)
        {
            throw new NotSupportedException("reading one section of a room pack needs a stream that can seek.");
        }

        r.Seek(start + section.Offset, SeekOrigin.Begin);
        return await ReadSectionAsync(r, section, $"the \"{section.Tag}\" section", cancellationToken).ConfigureAwait(false);
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
    /// Refuses a pack version a build whose newest is
    /// <paramref name="newest"/> does not read: an older one whose promises
    /// fall short (with what it lacks and what to do), or any other outside
    /// <see cref="OldestReadVersion"/> to <paramref name="newest"/>.
    /// </summary>
    /// <param name="version">The version the pack carries.</param>
    /// <param name="newest">The newest version the reading build reads: <see cref="Version"/> for this build.</param>
    /// <exception cref="LinkException">The version is not read, naming it and <paramref name="newest"/>.</exception>
    /// <remarks>
    /// Parameterised by the newest version so a fact can hold the refusal a
    /// build that reads only version 4 gives a version 5 pack to its text:
    /// the check is the one every build ran since version 3, with its newest
    /// raised by one for heights.
    /// </remarks>
    internal static void CheckVersion(int version, int newest)
    {
        if (OlderVersion(version) is { } lacks)
        {
            // An older version whose rooms were never held to what this
            // build's link relies on: not read around.
            throw new LinkException(
                $"room pack version {version}; this build reads version {newest}. A version {version} pack was written before"
                + $" {lacks}; recompile the library with ssmap room.");
        }

        if (version < OldestReadVersion || version > newest)
        {
            throw new LinkException($"room pack version {version}; this build reads version {newest}.");
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
        ((byte)'E', (byte)'C', (byte)'N', (byte)'T') => RoomEntityCounts.SectionTag,
        ((byte)'D', (byte)'V', (byte)'I', (byte)'S') => RoomDoorVisibility.SectionTag,
        ((byte)'P', (byte)'R', (byte)'O', (byte)'P') => RoomStaticProps.SectionTag,
        ((byte)'B', (byte)'M', (byte)'O', (byte)'D') => RoomBrushModels.SectionTag,
        ((byte)'T', (byte)'R', (byte)'A', (byte)'N') => RoomTransit.SectionTag,
        ((byte)'C', (byte)'U', (byte)'B', (byte)'E') => RoomCubemaps.SectionTag,
        ((byte)'O', (byte)'V', (byte)'L', (byte)'Y') => RoomOverlays.SectionTag,
        ((byte)'L', (byte)'I', (byte)'T', (byte)'E') => RoomLighting.SectionTag,
        ((byte)'D', (byte)'L', (byte)'I', (byte)'T') => RoomDoorLight.SectionTag,
        ((byte)'A', (byte)'P', (byte)'R', (byte)'T') => RoomAreaPortals.SectionTag,
        ((byte)'S', (byte)'H', (byte)'A', (byte)'P') => RoomShape.SectionTag,
        ((byte)'W', (byte)'A', (byte)'T', (byte)'R') => RoomWater.SectionTag,
        ((byte)'M', (byte)'A', (byte)'P', (byte)'V') => RoomMapView.SectionTag,
        ((byte)'G', (byte)'E', (byte)'O', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.GeometryTag(tag[3] - '0'),
        ((byte)'C', (byte)'O', (byte)'L', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.CollisionTag(tag[3] - '0'),
        ((byte)'E', (byte)'N', (byte)'T', >= (byte)'0' and <= (byte)'3') => RoomLinkSections.EntitiesTag(tag[3] - '0'),
        ((byte)'N', (byte)'V', (byte)'R', >= (byte)'0' and <= (byte)'3') => RoomNavSection.Tag(tag[3] - '0'),
        ((byte)'N', (byte)'A', (byte)'M', >= (byte)'0' and <= (byte)'3') => RoomNameTurn.Tag(tag[3] - '0'),
        _ => null,
    };

    /// <summary>
    /// What an older pack version lacks that this build's link relies on,
    /// as the refusal says it, or null for a version that is not an older
    /// one of this format. Each names the first promise it misses: a
    /// version 1 pack misses both.
    /// </summary>
    private static string? OlderVersion(int version) => version switch
    {
        1 => "the library-wide singletons were checked when the pack is built",
        2 => "rooms packed their files (the default cubemaps built from the library's sky), which the link now carries",
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
