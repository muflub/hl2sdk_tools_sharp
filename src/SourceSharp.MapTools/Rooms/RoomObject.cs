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
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One compiled room: the object the linker links.
/// </summary>
/// <remarks>
/// <para>
/// A room object is a room's BSP — compiled by the very tools a normal map goes
/// through — plus the two things only the room's author knows: which leaf
/// clusters are the interior (so the linker can keep an interior's visibility
/// inside the room) and which cluster each door's plug put at its opening (so
/// the door graph can walk through that door). Everything else — planes, faces,
/// texdata, portals — came out of <c>Vbsp</c> and <c>Vvis</c> and is relocated
/// by <c>LevelLinker</c> at its placement.
/// </para>
/// <para>
/// §10a seam: <see cref="InputKeys"/> are the cache keys' raw material. They are
/// opaque strings here — the cache package decides how they combine; the room
/// side guarantees only that equal keys mean a byte-equal room object, so the
/// linker never needs to see inside.
/// </para>
/// </remarks>
public sealed record RoomObject(
    RoomDefinition Definition,
    BspData Bsp,
    VisResult Vis,
    RoomLintReport Lint,
    IReadOnlyList<string> InputKeys)
{
    /// <summary>The room's BSP, with its visibility lump.</summary>
    public BspData Compiled { get; init; } = Bsp;

    /// <summary>The interior leaf-clusters, room-local.</summary>
    public IReadOnlyList<int> InteriorClusters => Lint.InteriorClusters;

    /// <summary>Each socket's plug cluster, room-local, in socket order.</summary>
    public IReadOnlyList<int> SealClusters => Lint.SealClusters;

    /// <summary>The number of clusters the room's own vis produced.</summary>
    public int ClusterCount => Vis.ClusterCount;

    /// <summary>
    /// The link work done ahead for this room, or null: set by the library
    /// compile and by a room pack that stores it, and used by
    /// <see cref="LevelLinker"/> in place of computing it per placement.
    /// </summary>
    /// <remarks>
    /// Only ever a shortcut: the linker uses it only when it still describes
    /// this room's own compile (<see cref="RoomLinkData.IsFor"/>), and
    /// computes the same data on the fly otherwise, so a room without it (an
    /// older pack, a room built in memory) links to the same bytes.
    /// </remarks>
    internal RoomLinkData? Link { get; init; }

    /// <summary>
    /// The room's entity counts as the pack stores them, or null: set by a
    /// room pack that has them, and used by the link's entity budget in
    /// place of parsing the room's entity lump.
    /// </summary>
    /// <remarks>
    /// Only ever a shortcut, like <see cref="Link"/>: used only while it
    /// still describes this room's own compile
    /// (<see cref="RoomEntityCounts.IsFor"/>); otherwise the room is
    /// counted afresh, to the same numbers.
    /// </remarks>
    internal RoomEntityCounts? EntityCounts { get; init; }

    /// <summary>The room's entity counts: the stored ones while they describe this compile, else counted now.</summary>
    internal RoomEntityCounts CountEntities() =>
        EntityCounts is { } stored && stored.IsFor(this) ? stored : RoomEntityCounts.Of(Bsp);

    /// <summary>
    /// The room's names per quarter turn (<see cref="RoomNameTurn"/>), or
    /// null: made by the room compile and stored by the pack, and used by
    /// the link in place of reading the room's entities for names again.
    /// </summary>
    /// <remarks>
    /// Only ever a shortcut, like <see cref="Link"/>: used only while it still
    /// describes this room's own compile (<see cref="RoomNameTables.IsFor"/>);
    /// otherwise the names are read from the room's entity lump, to the same
    /// tables.
    /// </remarks>
    internal RoomNameTables? Names { get; init; }

    /// <summary>
    /// What the naming rule warned of when the room was compiled (a
    /// placeholder after the start of a value, a local name no entity of
    /// the room defines), each a whole sentence; empty when the room has no
    /// names from its compile.
    /// </summary>
    public IReadOnlyList<string> NameWarnings => Names?.Turn(0)?.Warnings ?? [];

    /// <summary>A turn's names: the stored ones while they describe this compile, else read from the entity lump now.</summary>
    /// <param name="turn">The quarter turn, 0 to 3.</param>
    /// <param name="nameKeys">Name-valued keys the library adds, for a room whose names are read now.</param>
    internal RoomNameTurn NamesFor(int turn, IReadOnlySet<string>? nameKeys) =>
        Names is { } stored && stored.IsFor(this) && stored.Turn(turn) is { } names
            ? names
            : RoomNameAnalysis.Analyse(Definition.Name, Bsp, nameKeys)[turn];

    /// <summary>
    /// The room's static props as the link carries them
    /// (<see cref="RoomStaticProps"/>: model hulls, the keys vbsp consumed,
    /// poses per turn), or null: made by the room compile for a room whose
    /// compile emitted props, and stored by the pack in its own section.
    /// </summary>
    /// <remarks>
    /// Unlike the other stored work this is not only a shortcut: it holds
    /// what the compiled lump does not (the models' hulls, <c>room_needs</c>,
    /// socket furniture), so a room whose lump has props and that has none of
    /// this bound to its compile (<see cref="StaticProps"/>) is refused by
    /// the link, naming the room.
    /// </remarks>
    internal RoomStaticProps? Props { get; init; }

    /// <summary>The room's static props while they describe this compile, else null.</summary>
    internal RoomStaticProps? StaticProps => Props is { } props && props.IsFor(this) ? props : null;

    /// <summary>
    /// The room's navigation, or null: built beside the link work by a
    /// library compile whose library builds navigation, and read by a pack
    /// load that asks for it (<see cref="RoomPackRequest.Navigation"/>), at
    /// the turns asked for. It is not part of the room container; the pack
    /// stores it in its own sections (<see cref="Nav.RoomNavSection"/>).
    /// </summary>
    public Nav.RoomNavTurns? Nav { get; init; }
}

/// <summary>
/// A named collection of room objects, on disk or in memory.
/// </summary>
/// <remarks>
/// One directory per library: a manifest (KeyValues) naming the rooms and the
/// kit they share, and per room a <c>.ssroom</c> sidecar holding the compiled
/// BSP, its vis, and the lint verdict. The linker loads a library whole; the
/// room compiler writes one.
/// </remarks>
public sealed class RoomLibrary
{
    private readonly Dictionary<string, RoomObject> _rooms = new(StringComparer.Ordinal);

    /// <summary>The kit every room of this library was built to.</summary>
    public SocketKit Kit { get; }

    /// <summary>The library's grid cell edge.</summary>
    public float CellSize { get; }

    /// <summary>Creates an empty library.</summary>
    /// <param name="kit">The shared door kit.</param>
    /// <param name="cellSize">The shared cell edge.</param>
    public RoomLibrary(SocketKit kit, float cellSize)
    {
        kit.Validate();
        RoomNumbers.RequirePositiveFinite(cellSize, nameof(cellSize));
        Kit = kit;
        CellSize = cellSize;
    }

    /// <summary>
    /// What the library sets for every level linked from it (its entity
    /// reserve): read from the pack's library section by whoever loads the
    /// rooms, <see cref="RoomLibraryOptions.None"/> until then.
    /// </summary>
    public RoomLibraryOptions Options { get; set; } = RoomLibraryOptions.None;

    /// <summary>
    /// The library-wide entities (the sun, fog and the other controllers
    /// from the gaps between cells), in library order and as the library
    /// wrote them: read from the pack's library section
    /// (<see cref="RoomPack.ReadLibraryEntitiesAsync"/>) by whoever loads the
    /// rooms, empty until then. The link writes each once, right after the
    /// worldspawn (<see cref="RoomLibraryEntities.ToLinked"/>), and counts
    /// them in the level's entity budget.
    /// </summary>
    public IReadOnlyList<VmfChunk> LibraryEntities { get; set; } = [];

    /// <summary>The rooms, in insertion order.</summary>
    public IReadOnlyCollection<RoomObject> Rooms => _rooms.Values;

    /// <summary>Adds or replaces a room.</summary>
    /// <param name="room">The compiled room; its kit and cell must match the library's.</param>
    /// <exception cref="ArgumentException">The room belongs to another kit or cell size.</exception>
    public void Add(RoomObject room)
    {
        ArgumentNullException.ThrowIfNull(room);
        RoomDefinition d = room.Definition;
        if (Math.Abs(d.CellSize - CellSize) > 0.001f || d.Kit != Kit)
        {
            throw new ArgumentException(
                $"room {d.Name} was built for cell {d.CellSize:0.###} kit {d.Kit},"
                + $" the library is cell {CellSize:0.###} kit {Kit}.",
                nameof(room));
        }

        _rooms[d.Name] = room;
    }

    /// <summary>Finds a room by name.</summary>
    /// <param name="name">The room's name.</param>
    /// <returns>The room, or null.</returns>
    public RoomObject? Find(string name) => _rooms.GetValueOrDefault(name);

    /// <summary>Finds a room, refusing when the library lacks it.</summary>
    /// <param name="name">The room's name.</param>
    /// <returns>The room.</returns>
    /// <exception cref="LinkException">No room of that name.</exception>
    public RoomObject Get(string name) =>
        Find(name) ?? throw new LinkException($"the room library has no room \"{name}\".");
}
