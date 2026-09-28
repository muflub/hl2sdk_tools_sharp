//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One placed room of a level grid: which room, and how it is turned.</summary>
/// <param name="Room">The room's library name.</param>
/// <param name="Rotation">Quarter turns counter-clockwise seen from above, 0 to 3.</param>
/// <param name="Line">Where the level file names it, for messages; 0 when it was not read from a file.</param>
/// <param name="Column">The column on that line.</param>
public sealed record LevelCell(string Room, int Rotation, int Line = 0, int Column = 0)
{
    /// <summary>Where the cell was written, as a message prefix (<c>line 3, column 7: </c>), or empty.</summary>
    public string Where => Line > 0
        ? string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}: ")
        : string.Empty;
}

/// <summary>
/// A level as a grid: rows and columns of cells, each holding one room of
/// the library, turned, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is what a level file says (<see cref="LevelYaml"/>) and what the
/// seeded generator makes (<see cref="LevelGenerator"/>). Cell <c>(x, y)</c>
/// is column <c>x</c> from the west and row <c>y</c> from the south; a room
/// there stands in the world box <c>[x·cell, (x+1)·cell] × [y·cell,
/// (y+1)·cell]</c>, turned about its cell's centre.
/// </para>
/// <para>
/// <b>Joints are implicit.</b> <see cref="ToLayout"/> joins two sockets
/// exactly when they face each other across a shared wall: the room on one
/// side has a socket on that wall, turned, and so does the room on the
/// other. Every other socket — facing an empty cell, the grid's edge, or a
/// neighbour's plain wall — is capped, its plug kept as a wall. There is no
/// way to name a joint or a cap by hand, so a level cannot disagree with its
/// own geometry.
/// </para>
/// </remarks>
public sealed class LevelGrid
{
    private readonly LevelCell?[] _cells;

    /// <summary>A grid from its cells.</summary>
    /// <param name="name">The level's name; the linked map's base name.</param>
    /// <param name="library">The room library VMF the level's rooms come from, as the level file wrote it.</param>
    /// <param name="rows">How many rows, south to north.</param>
    /// <param name="columns">How many columns, west to east.</param>
    /// <param name="cells">The cells, row by row from the south-west, x fastest; null where there is no room.</param>
    /// <exception cref="ArgumentException">The cell count is not rows × columns, or a rotation is not 0 to 3.</exception>
    public LevelGrid(string name, string library, int rows, int columns, IReadOnlyList<LevelCell?> cells)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        if (cells.Count != (long)rows * columns)
        {
            throw new ArgumentException($"a {rows}x{columns} grid has {rows * columns} cells, not {cells.Count}", nameof(cells));
        }

        foreach (LevelCell? cell in cells)
        {
            if (cell is not null && (uint)cell.Rotation > 3)
            {
                throw new ArgumentException($"rotation {cell.Rotation} is not a quarter-turn count (0 to 3)", nameof(cells));
            }
        }

        Name = name;
        Library = library;
        Rows = rows;
        Columns = columns;
        _cells = [.. cells];
    }

    /// <summary>The level's name.</summary>
    public string Name { get; }

    /// <summary>The room library the level's rooms come from, as written.</summary>
    public string Library { get; }

    /// <summary>The row count.</summary>
    public int Rows { get; }

    /// <summary>The column count.</summary>
    public int Columns { get; }

    /// <summary>The cells, row by row from the south-west, x fastest.</summary>
    public IReadOnlyList<LevelCell?> Cells => _cells;

    /// <summary>The cell at a column and row, or null for no room.</summary>
    /// <param name="x">The column, from the west.</param>
    /// <param name="y">The row, from the south.</param>
    public LevelCell? this[int x, int y] => _cells[(y * Columns) + x];

    /// <summary>Every placed room with its cell, row by row from the south-west: the link order.</summary>
    public IEnumerable<(int X, int Y, LevelCell Cell)> Placed
    {
        get
        {
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    if (this[x, y] is { } cell)
                    {
                        yield return (x, y, cell);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The level as the linker takes it: every placed room with its joints
    /// derived from the geometry and every other socket capped.
    /// </summary>
    /// <param name="rooms">The library's rooms by name; null for a name it lacks.</param>
    /// <param name="cellSize">The library's grid.</param>
    /// <param name="kit">The library's door kit.</param>
    /// <returns>The layout, in link order.</returns>
    /// <exception cref="LinkException">The level places a room the library does not have.</exception>
    public LevelLayout ToLayout(Func<string, RoomDefinition?> rooms, float cellSize, SocketKit kit)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        List<RoomInstance> instances = [];
        foreach ((int x, int y, LevelCell cell) in Placed)
        {
            RoomDefinition definition = Definition(rooms, cell);
            RoomTransform transform = new(new RoomPlacement(cell.Room, x, y, cell.Rotation), cellSize);
            List<(string, string)> joints = [];
            List<string> capped = [];
            foreach (RoomSocket socket in definition.Sockets)
            {
                (int axis, int sign) = transform.WorldNormal(socket.Facing);
                int nx = x + (axis == 0 ? sign : 0);
                int ny = y + (axis == 1 ? sign : 0);
                string? theirs = null;
                if (nx >= 0 && ny >= 0 && nx < Columns && ny < Rows && this[nx, ny] is { } other)
                {
                    RoomDefinition neighbour = Definition(rooms, other);
                    RoomTransform otherTransform = new(new RoomPlacement(other.Room, nx, ny, other.Rotation), cellSize);
                    foreach (RoomSocket candidate in neighbour.Sockets)
                    {
                        if (otherTransform.WorldNormal(candidate.Facing) == (axis, -sign))
                        {
                            theirs = candidate.Name;
                        }
                    }
                }

                if (theirs is null)
                {
                    capped.Add(socket.Name);
                }
                else
                {
                    joints.Add((socket.Name, theirs));
                }
            }

            instances.Add(new RoomInstance(new RoomPlacement(cell.Room, x, y, cell.Rotation), joints, capped));
        }

        return new LevelLayout(Name, cellSize, kit, instances) { Columns = Columns, Rows = Rows };
    }

    private static RoomDefinition Definition(Func<string, RoomDefinition?> rooms, LevelCell cell) =>
        rooms(cell.Room)
        ?? throw new LinkException($"{cell.Where}the level places room \"{cell.Room}\", which is not in the room library.");
}
