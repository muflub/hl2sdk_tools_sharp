//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Where a room's geometry lives inside its grid cell.
/// </summary>
public enum RoomFacing : byte
{
    /// <summary>The room's own +x face, the cell face at x = cell size.</summary>
    PositiveX = 0,

    /// <summary>The room's own -x face, the cell face at x = 0.</summary>
    NegativeX = 1,

    /// <summary>The room's own +y face, the cell face at y = cell size.</summary>
    PositiveY = 2,

    /// <summary>The room's own -y face, the cell face at y = 0.</summary>
    NegativeY = 3,
}

/// <summary>
/// The one door shape a room library ships: the socket kit.
/// </summary>
/// <param name="Width">The opening's width along the face it sits on.</param>
/// <param name="Height">The opening's height.</param>
/// <param name="Depth">How deep the socket's hardware reaches into the room.</param>
/// <remarks>
/// <para>
/// Rooms join only at standard door sockets, so the
/// opening in a room's shell is not free-form — it is this rectangle, at the
/// centre of one of the cell's four vertical faces. The linter refuses a room
/// whose opening is any other size or anywhere else, and the linker matches
/// sockets by geometry: a socket on one face of the shared wall meeting the
/// same socket on the other side is a join, and the two sockets' plug brushes
/// touch inside the wall, which is what makes the sealed pair stay sealed
/// before the door is placed.
/// </para>
/// <para>
/// <see cref="Depth"/> reaches inward from the cell face on both sides of the
/// shared wall, so the two plugs meet at the wall's mid-plane when the rooms
/// are whole-cell placements.
/// </para>
/// </remarks>
public readonly record struct SocketKit(float Width, float Height, float Depth)
{
    /// <summary>The RPG's standard socket: a 96×96 opening, 8 units deep.</summary>
    public static SocketKit Standard { get; } = new(96, 96, 8);

    /// <summary>The opening's rectangle on the unit cell <c>[0,1]²</c> of a face.</summary>
    /// <remarks>
    /// Always centred: <c>(0.5 - w/2c, 0.5 - h/2c)</c> to
    /// <c>(0.5 + w/2c, 0.5 + h/2c)</c>, first axis along the face, second up.
    /// </remarks>
    public (float U0, float V0, float U1, float V1) OpeningUnit(float cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellSize, 0f);
        float halfU = Width / (2 * cellSize);
        float halfV = Height / (2 * cellSize);
        return (0.5f - halfU, 0.5f - halfV, 0.5f + halfU, 0.5f + halfV);
    }

    /// <summary>Validates the kit as a socket: positive, finite, and it fits in a face.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not a positive finite number.</exception>
    /// <exception cref="ArgumentException">The opening does not fit inside one face.</exception>
    /// <remarks>
    /// Every comparison against NaN is false, so a plain "not positive" check
    /// lets a NaN through; the finite test is what refuses it.
    /// </remarks>
    public void Validate()
    {
        RoomNumbers.RequirePositiveFinite(Width, nameof(Width));
        RoomNumbers.RequirePositiveFinite(Height, nameof(Height));
        RoomNumbers.RequirePositiveFinite(Depth, nameof(Depth));
        if (Width > 2_000_000f || Height > 2_000_000f)
        {
            throw new ArgumentException("The socket kit is larger than any cell can hold.", nameof(Width));
        }
    }
}

/// <summary>
/// One opening in a room's shell, on one cell face.
/// </summary>
/// <param name="Facing">Which cell face the opening is in, room-local.</param>
/// <param name="Name">The socket's name, unique in the room.</param>
/// <remarks>
/// In a room library a socket is found from its door plug
/// (<see cref="RoomLibraryVmf"/>): a world brush filling the kit's plug box
/// on a wall makes that wall a socket. That the plug really is the kit's
/// trigger-surfaced hardware, and that the room holds no other, is the
/// linter's job, see <see cref="RoomLinter"/>.
/// </remarks>
public readonly record struct RoomSocket(RoomFacing Facing, string Name)
{
    /// <summary>Validates the socket.</summary>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The facing is not one of four.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A socket needs a name.", nameof(Name));
        }

        if ((uint)Facing > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(Facing), Facing, "A room has four cell faces.");
        }
    }
}

/// <summary>
/// One room of a library: a sealed box built to occupy exactly one grid cell.
/// </summary>
/// <param name="Name">The room's name, unique in its library file.</param>
/// <param name="CellSize">The edge the box is built for; a whole-cell placement.</param>
/// <param name="Kit">The door kit every opening is cut from.</param>
/// <param name="Sockets">The openings in the shell.</param>
/// <remarks>
/// The rules a room must satisfy for the linker's shortcuts to be sound — its
/// brushes lie inside its own cells, nothing crosses a cell face except the
/// fixed kit, the shell is sealed except at the registered openings, the
/// interior is convex-enough to be reached from every corner without leaving
/// the cell — are enforced by <see cref="RoomLinter"/>, not assumed: every
/// shortcut the linker takes is unsound without them.
/// </remarks>
public sealed record RoomDefinition(string Name, float CellSize, SocketKit Kit, IReadOnlyList<RoomSocket> Sockets)
{
    /// <summary>Validates the definition.</summary>
    /// <exception cref="ArgumentException">The name is blank or two sockets share a face.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cell size is not a positive finite number.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A room needs a name.", nameof(Name));
        }

        RoomNumbers.RequirePositiveFinite(CellSize, nameof(CellSize));
        Kit.Validate();
        HashSet<RoomFacing> seen = [];
        foreach (RoomSocket socket in Sockets)
        {
            socket.Validate();
            if (!seen.Add(socket.Facing))
            {
                throw new ArgumentException(
                    string.Concat("Two sockets on the same face: ", socket.Facing.ToString(), "."),
                    nameof(Sockets));
            }
        }
    }
}

/// <summary>
/// Where one room of the library stands in the level.
/// </summary>
/// <param name="Room">The library name of the room to place.</param>
/// <param name="CellX">The cell column; the placement's x translation is <c>CellX × cell size</c>.</param>
/// <param name="CellY">The cell row.</param>
/// <param name="Rotation">
/// Quarter turns about +z, counter-clockwise seen from above: 0, 1, 2 or 3.
/// A count, not degrees; anything outside 0..3 is refused rather than
/// reduced, because a layout that says 90 or 4 almost certainly meant
/// something other than what the reduction would give it.
/// </param>
/// <remarks>
/// Translations are whole cells and rotations are quarter turns, which is what
/// makes the transforms exact — <see cref="RoomTransform"/> permutes and negates
/// components, so no float drifts and the assembly's bytes cannot depend on
/// thread count (invariant I4).
/// </remarks>
public readonly record struct RoomPlacement(string Room, int CellX, int CellY, int Rotation)
{
    /// <summary>The room's name as the layout spells it.</summary>
    internal string RawRoom => Room;

    /// <summary>Validates the placement.</summary>
    /// <exception cref="ArgumentException">The room name is blank.</exception>
    /// <exception cref="LinkException">The rotation is not a quarter-turn count 0..3.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Room))
        {
            throw new ArgumentException("A placement names a room.", nameof(Room));
        }

        if ((uint)Rotation > 3)
        {
            throw new LinkException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"placement rotation {Rotation} is not a quarter-turn count (0, 1, 2 or 3)"));
        }
    }

    /// <summary>The rotation reduced to 0..3.</summary>
    /// <remarks>
    /// Equal to <see cref="Rotation"/> for every placement that passed
    /// <see cref="Validate"/>; the reduction only keeps the transforms total
    /// for a caller that builds a <see cref="RoomTransform"/> directly.
    /// </remarks>
    public int NormalizedRotation => ((Rotation % 4) + 4) % 4;
}

/// <summary>
/// One room's entry in a level layout: where it stands and who it joins.
/// </summary>
/// <param name="Placement">The whole-cell placement.</param>
/// <param name="Joints">The socket pairs, this room's socket to the neighbour's.</param>
/// <param name="Capped">Sockets of this room that face the void and are capped shut.</param>
public sealed record RoomInstance(RoomPlacement Placement, IReadOnlyList<(string Socket, string NeighborSocket)> Joints, IReadOnlyList<string> Capped)
{
    /// <summary>Validates the instance.</summary>
    public void Validate()
    {
        Placement.Validate();
        foreach ((string socket, string neighbor) in Joints)
        {
            if (string.IsNullOrWhiteSpace(socket) || string.IsNullOrWhiteSpace(neighbor))
            {
                throw new ArgumentException("A joint names both of its sockets.", nameof(Joints));
            }
        }
    }
}

/// <summary>
/// A level: rooms of one library on the shared grid, joined at their sockets.
/// </summary>
/// <param name="Name">The level's name; becomes the map's base name.</param>
/// <param name="CellSize">The grid's edge — the library is built for one size.</param>
/// <param name="Kit">The library's door kit.</param>
/// <param name="Rooms">The placements, in link order.</param>
/// <remarks>
/// The linker's view of a level, with every joint and cap spelled out. A
/// level file (<see cref="LevelYaml"/>) never spells them: its
/// <see cref="LevelGrid.ToLayout"/> derives them from the geometry. Two
/// instances may not share a cell, and every socket of every instance must
/// be jointed to a neighbour's socket or explicitly capped — the linter's
/// "matched or capped" rule (§10b first table), enforced at link time
/// because only the layout knows its neighbours.
/// </remarks>
public sealed record LevelLayout(string Name, float CellSize, SocketKit Kit, IReadOnlyList<RoomInstance> Rooms)
{
    /// <summary>
    /// The level grid's columns, or null when the layout was built without a
    /// grid: what tells a reference to an empty cell from one off the grid
    /// (the missing-neighbour warning says which). Without it only a
    /// negative column is off the grid.
    /// </summary>
    public int? Columns { get; init; }

    /// <summary>The level grid's rows, or null: as <see cref="Columns"/>.</summary>
    public int? Rows { get; init; }

    /// <summary>
    /// What the level file says about its transitions, or null when it says
    /// nothing (<see cref="LevelGrid.Transitions"/>): the link and the flatten
    /// apply the level rule of the rooms design (11.1) when this is set or a
    /// placed room has a transition role, and link as before otherwise.
    /// </summary>
    public LevelTransitions? Transitions { get; init; }

    /// <summary>Validates the layout's own shape.</summary>
    /// <exception cref="ArgumentException">The name is blank, the level places no room, or two rooms share a cell.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cell size is not a positive finite number.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A level needs a name.", nameof(Name));
        }

        RoomNumbers.RequirePositiveFinite(CellSize, nameof(CellSize));
        Kit.Validate();

        // A level of no rooms has no world model to hang anything on: every
        // later step (the grid extent, the first room's lump versions) would
        // index an empty list.
        if (Rooms.Count == 0)
        {
            throw new ArgumentException("A level places at least one room.", nameof(Rooms));
        }

        HashSet<(int, int)> cells = [];
        foreach (RoomInstance room in Rooms)
        {
            room.Validate();
            if (!cells.Add((room.Placement.CellX, room.Placement.CellY)))
            {
                throw new ArgumentException(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"two rooms are placed in cell ({room.Placement.CellX}, {room.Placement.CellY})."),
                    nameof(Rooms));
            }
        }
    }
}

/// <summary>
/// Everything about the level the linker knows before it copies a byte: the
/// resolved placements and the joint graph the visibility rows are built from.
/// </summary>
/// <param name="Layout">The validated layout.</param>
/// <param name="Rooms">The library entries, index-aligned with <see cref="LevelLayout.Rooms"/>.</param>
/// <param name="Planes">The grid's cell-face planes, index-aligned with the faces the door graph names.</param>
public sealed record LevelPlan(
    LevelLayout Layout,
    IReadOnlyList<ResolvedPlacement> Rooms,
    IReadOnlyList<Plane> Planes)
{
}

/// <summary>
/// One placement resolved against its library room: the room object, the exact
/// transform, and the joint graph edges.
/// </summary>
/// <param name="Instance">The layout entry.</param>
/// <param name="Room">The room object it names.</param>
/// <param name="Index">The placement's link order.</param>
/// <param name="Translation">The world translation, whole cells.</param>
/// <param name="Rotation">Quarter turns, normalised.</param>
/// <param name="SocketClusters">The socket seal clusters, room-local indices, in socket order.</param>
public sealed record ResolvedPlacement(
    RoomInstance Instance,
    RoomObject Room,
    int Index,
    Vec3 Translation,
    int Rotation,
    IReadOnlyList<int> SocketClusters)
{
    /// <summary>The room's sockets, room order.</summary>
    public IReadOnlyList<RoomSocket> Sockets => Room.Definition.Sockets;
}

/// <summary>The numeric checks the room records share.</summary>
internal static class RoomNumbers
{
    /// <summary>Refuses NaN, the infinities, zero and negatives.</summary>
    /// <param name="value">The number.</param>
    /// <param name="name">The parameter it came from.</param>
    /// <exception cref="ArgumentOutOfRangeException">The number is not positive and finite.</exception>
    public static void RequirePositiveFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}

/// <summary>
/// An exception from the room linter: one broken guarantee, named.
/// </summary>
public sealed class RoomLintException : Exception
{
    /// <summary>Names the broken guarantee and the geometry that broke it.</summary>
    /// <param name="message">Which guarantee, and where.</param>
    public RoomLintException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An exception from the linker: a level whose rooms cannot be linked.
/// </summary>
/// <remarks>
/// The linker never silently links a broken level — an unjointed, uncapped
/// socket, a room the library does not have, a placement that shares a cell,
/// a transform that is not whole-cell quarter-turn, a lump kind it cannot
/// relocate. Silence would deliver a level whose PVS is not a superset of
/// anything (§10b's premise).
/// </remarks>
public sealed class LinkException : Exception
{
    /// <summary>Names the refused condition.</summary>
    /// <param name="message">What cannot be linked, and why.</param>
    public LinkException(string message)
        : base(message)
    {
    }
}
