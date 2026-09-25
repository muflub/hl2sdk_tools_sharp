using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellSize, 0f);
        Kit = kit;
        CellSize = cellSize;
    }

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
