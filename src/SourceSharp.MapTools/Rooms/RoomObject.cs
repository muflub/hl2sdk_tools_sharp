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
    /// The room's brush entities as the link carries them
    /// (<see cref="RoomBrushModels"/>: per brush model its class, the runs
    /// of the room's lumps it owns, its conditions and its collision per
    /// turn), or null: made by the room compile for a room whose compile has
    /// brush models besides the world, and stored by the pack in its own
    /// section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Props"/>, not only a shortcut: which brushes are an
    /// entity's is not in the compiled lumps, so a room with brush models and
    /// none of this bound to its compile (<see cref="BrushModelsOfCompile"/>)
    /// is refused by the link, naming the room.
    /// </remarks>
    internal RoomBrushModels? BrushModels { get; init; }

    /// <summary>The room's brush models while they describe this compile, else null.</summary>
    internal RoomBrushModels? BrushModelsOfCompile => BrushModels is { } models && models.IsFor(this) ? models : null;

    /// <summary>
    /// What the room brings to its level's transitions and spawn
    /// (<see cref="RoomTransit"/>: its role, transition volume, fold
    /// trigger, arrival and spawn points), or null: made by the library
    /// compile from the room's VMF for a room with a role or spawn points,
    /// and stored by the pack in its own section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Props"/>, not only a shortcut: the points of interest
    /// are not in the compile at all, so a level placing a room whose compile
    /// has a transition volume and none of this bound to it is refused by
    /// name (a pack written before transitions).
    /// </remarks>
    internal RoomTransit? Transit { get; init; }

    /// <summary>The room's transition data while it describes this compile, else null.</summary>
    internal RoomTransit? TransitOfCompile => Transit is { } transit && transit.IsFor(this) ? transit : null;

    /// <summary>
    /// The room's base lighting (<see cref="RoomLighting"/>: what vrad gave
    /// its faces, leaves, lights and props, once or per quarter turn), or
    /// null: baked by a library compile that lights its rooms
    /// (<see cref="RoomLibraryCompileSettings.Lighting"/>) and stored by the
    /// pack in its own section.
    /// </summary>
    /// <remarks>
    /// A room without it links unlit, as every room did before the bake
    /// existed; a level may not mix the two (<see cref="LevelLinker"/>).
    /// </remarks>
    internal RoomLighting? Lighting { get; init; }

    /// <summary>The room's lighting while it describes this compile, else null.</summary>
    internal RoomLighting? LightingOfCompile => Lighting is { } lighting && lighting.IsFor(this) ? lighting : null;

    /// <summary>
    /// The room's door light (<see cref="RoomDoorLight"/>: what its lights,
    /// surfaces and sky send out through each opening, and its answer to
    /// light entering through each), or null: recorded by a library compile
    /// that lights its rooms, after the base bake, and stored by the pack in
    /// its own section.
    /// </summary>
    /// <remarks>
    /// A lit room without it links with its base alone, as PR 9 linked
    /// every lit room: a jointed neighbour's light does not reach it, and
    /// its own light does not reach its neighbours.
    /// </remarks>
    internal RoomDoorLight? DoorLight { get; init; }

    /// <summary>The room's door light while it describes this compile, else null.</summary>
    internal RoomDoorLight? DoorLightOfCompile => DoorLight is { } door && door.IsFor(this) ? door : null;

    /// <summary>
    /// The room's <c>env_cubemap</c> samples and the names its compile made
    /// after them (<see cref="RoomCubemaps"/>: the samples per turn, the
    /// patched texdata strings and packed files), or null: made by the room
    /// compile for a room with samples, and stored by the pack in its own
    /// section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Props"/>, not only a shortcut: the samples' origins
    /// as the loader read them are not in the lump, which holds them
    /// truncated, so a room with samples and none of this bound to its
    /// compile (<see cref="CubemapsOfCompile"/>) is refused by the link,
    /// naming the room.
    /// </remarks>
    internal RoomCubemaps? Cubemaps { get; init; }

    /// <summary>The room's cubemap data while it describes this compile, else null.</summary>
    internal RoomCubemaps? CubemapsOfCompile => Cubemaps is { } cubemaps && cubemaps.IsFor(this) ? cubemaps : null;

    /// <summary>
    /// The room's overlays as the link carries them (<see cref="RoomOverlays"/>:
    /// every record's origin and basis at each quarter turn), or null: made
    /// by the room compile for a room whose compile wrote overlays, and
    /// stored by the pack in its own section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Props"/>, not only a shortcut: it is what says the room
    /// was held to the pack's overlay rules when it was compiled, so a room
    /// whose lump has overlays and none of this bound to its compile
    /// (<see cref="OverlaysOfCompile"/>) is refused by the link, naming the room.
    /// </remarks>
    internal RoomOverlays? Overlays { get; init; }

    /// <summary>The room's overlays while they describe this compile, else null.</summary>
    internal RoomOverlays? OverlaysOfCompile => Overlays is { } overlays && overlays.IsFor(this) ? overlays : null;

    /// <summary>
    /// The room's displacements as the link carries them (<see cref="RoomDisplacements"/>:
    /// every start position and vertex vector at each quarter turn), or
    /// null: made by the room compile for a room whose compile wrote
    /// displacements, and stored by the pack in its own section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Overlays"/>, not only a shortcut: it is what says the
    /// room was held to the pack's displacement rules when it was compiled,
    /// so a room whose lumps have displacements and none of this bound to
    /// its compile (<see cref="DisplacementsOfCompile"/>) is refused by the
    /// link, naming the room.
    /// </remarks>
    internal RoomDisplacements? Displacements { get; init; }

    /// <summary>The room's displacements while they describe this compile, else null.</summary>
    internal RoomDisplacements? DisplacementsOfCompile => Displacements is { } displacements && displacements.IsFor(this) ? displacements : null;

    /// <summary>
    /// The room's areas and area portals as the link carries them
    /// (<see cref="RoomAreaPortals"/>: the lumps checked, the clip vertices
    /// at each quarter turn, the portal numbers), or null: made by the room
    /// compile for a room whose compile has area portals, and stored by the
    /// pack in its own section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Overlays"/>, not only a shortcut: it is what says the
    /// room was held to the pack's area portal rules when it was compiled,
    /// so a room whose lumps have area portals and none of this bound to its
    /// compile (<see cref="AreaPortalsOfCompile"/>) is refused by the link,
    /// naming the room.
    /// </remarks>
    internal RoomAreaPortals? AreaPortals { get; init; }

    /// <summary>The room's area portals while they describe this compile, else null.</summary>
    internal RoomAreaPortals? AreaPortalsOfCompile => AreaPortals is { } portals && portals.IsFor(this) ? portals : null;

    /// <summary>
    /// The room's water as the link carries it (<see cref="RoomWater"/>: its
    /// water data counted, its fluids' convexes and water overlays at each
    /// quarter turn), or null: made by the room compile for a room whose
    /// compile has water, and stored by the pack in its own section.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Overlays"/>, not only a shortcut: it is what says the
    /// room was held to the pack's water rules when it was compiled, so a
    /// room whose compile has water and none of this bound to it
    /// (<see cref="WaterOfCompile"/>) is refused by the link, naming the room.
    /// </remarks>
    internal RoomWater? Water { get; init; }

    /// <summary>The room's water while it describes this compile, else null.</summary>
    internal RoomWater? WaterOfCompile => Water is { } water && water.IsFor(this) ? water : null;

    /// <summary>
    /// The room's part of its level's map (<see cref="RoomMapView"/>: its
    /// floors, doors and markers, room-local), or null: made by the library
    /// compile from the room's compile and VMF, and stored by the pack in its
    /// own section. A level placing a room without it links without a map.
    /// </summary>
    internal RoomMapView? MapView { get; init; }

    /// <summary>The room's map while it describes this compile, else null.</summary>
    internal RoomMapView? MapViewOfCompile => MapView is { } view && view.IsFor(this) ? view : null;

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
    private readonly Dictionary<string, int> _sources = new(StringComparer.Ordinal);
    private readonly List<RoomLibraryOptions> _sourceOptions = [];

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

    /// <summary>
    /// The name of the library's skybox room (<see cref="RoomLibrarySplit.Skybox"/>),
    /// one of <see cref="Rooms"/>, or null when the library has none: read
    /// from the pack's library section (<see cref="RoomLibrarySkybox"/>) by
    /// whoever loads the rooms. The link places it below every level's grid
    /// as its own area; a level may not place it itself.
    /// </summary>
    public string? SkyboxRoom { get; set; }

    /// <summary>
    /// Which of the level's libraries, in level order (<see cref="SourceOf"/>),
    /// the level's sun came from: 0 for a library that is not a level's
    /// combination, and for a combination whose first library has a sun or
    /// no library has one; else the earliest library with one, which fills
    /// the first library's gap (the rooms design's D29,
    /// <see cref="LevelLibraries.Combine"/>).
    /// </summary>
    /// <remarks>
    /// The link reads it to take the level's sun world lights from a room
    /// baked under that sun (<c>LevelLinker.PlanLighting</c>): the sun
    /// entity the level writes and the direct sunlight its lightmaps hold
    /// must be one sun, and only that library's rooms were baked under it.
    /// </remarks>
    public int SunSource { get; set; }

    /// <summary>The rooms, in insertion order.</summary>
    public IReadOnlyCollection<RoomObject> Rooms => _rooms.Values;

    /// <summary>
    /// The names the rooms are held under, in insertion order: each room's
    /// own name (<see cref="Add(RoomObject)"/>), or the name it was added by
    /// (<see cref="Add(string, RoomObject, int)"/>), such as a room of a
    /// combined pack added by its name within its library.
    /// </summary>
    public IReadOnlyCollection<string> Names => _rooms.Keys;

    /// <summary>Adds or replaces a room.</summary>
    /// <param name="room">The compiled room; its kit and cell must match the library's.</param>
    /// <exception cref="ArgumentException">The room belongs to another kit or cell size.</exception>
    public void Add(RoomObject room)
    {
        RequireGrid(room);
        _rooms[room.Definition.Name] = room;
    }

    /// <summary>Refuses a room built for another kit or cell size than the library's.</summary>
    private void RequireGrid(RoomObject room)
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
    }

    /// <summary>
    /// Adds or replaces a room under a name of the level's rather than its
    /// own: a room of a level of several libraries, under its qualified
    /// name (<c>base.corner</c>, <see cref="LevelLibraries.Combine"/>).
    /// </summary>
    /// <param name="name">The name the level places it by.</param>
    /// <param name="room">The compiled room; its kit and cell must match the library's.</param>
    /// <param name="source">Which of the level's libraries it comes from, in level order (<see cref="SourceOf"/>).</param>
    /// <exception cref="ArgumentException">The room belongs to another kit or cell size, or the source is not a library's.</exception>
    /// <remarks>
    /// The room keeps its own definition, and with it the name it was
    /// compiled under (<see cref="RoomDefinition.Name"/>), which is also the
    /// map name its packed files are named after. Only the level's lookup
    /// uses <paramref name="name"/>, so two libraries' rooms of one name are
    /// two rooms of the level.
    /// </remarks>
    public void Add(string name, RoomObject room, int source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(source);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(source, Math.Max(_sourceOptions.Count, 1));
        RequireGrid(room);
        _rooms[name] = room;
        _sources[name] = source;
    }

    /// <summary>
    /// Declares the libraries a level of several draws from, in level order,
    /// by their settings: the name-valued keys of each (which stay with its
    /// rooms, the rooms design's 17.4) are read from here. Call before
    /// adding their rooms (<see cref="Add(string, RoomObject, int)"/>).
    /// </summary>
    /// <param name="options">Each library's settings, in level order.</param>
    public void SetSources(IReadOnlyList<RoomLibraryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sourceOptions.Clear();
        _sourceOptions.AddRange(options);
    }

    /// <summary>
    /// Which of the level's libraries a room comes from, in level order: 0
    /// for every room of a library that is not a level's combination (and
    /// for a name it does not hold).
    /// </summary>
    /// <param name="name">The name the level places the room by.</param>
    /// <returns>The library's index.</returns>
    public int SourceOf(string name) => _sources.GetValueOrDefault(name);

    /// <summary>
    /// The name-valued keys a room's library adds to the built-in table, for
    /// reading the room's names: its own library's in a combination, else
    /// this library's (<see cref="RoomLibraryOptions.NameKeySet"/>).
    /// </summary>
    /// <param name="name">The name the level places the room by.</param>
    /// <returns>The keys, or null when its library adds none.</returns>
    public IReadOnlySet<string>? NameKeysOf(string name) =>
        _sourceOptions.Count == 0 ? Options.NameKeySet : _sourceOptions[SourceOf(name)].NameKeySet;

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
