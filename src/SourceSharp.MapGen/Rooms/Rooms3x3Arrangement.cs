//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapGen.Catalog;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// Where a room stands: a grid cell and a count of quarter turns.
/// </summary>
/// <param name="CellX">The column.</param>
/// <param name="CellY">The row.</param>
/// <param name="Rotation">Quarter turns counter-clockwise seen from above, 0 to 3.</param>
/// <remarks>
/// <para>
/// This is the layout format's placement, written out a second time on
/// purpose. The linker has its own transform; the monolithic reference is
/// built through this one, so a linker that turned rooms the wrong way would
/// disagree with the reference instead of agreeing with itself.
/// </para>
/// <para>
/// The convention: a turn is about the cell's own vertical centre line, so
/// the turned room still fills <c>[0, cell]²</c> of its cell, and room-local
/// +x goes to world +y. Written as the point map it is — quarter turns only
/// permute and negate — so every coordinate stays on the 16-unit grid.
/// </para>
/// </remarks>
public readonly record struct Rooms3x3Placement(int CellX, int CellY, int Rotation)
{
    /// <summary>The room unmoved and unturned.</summary>
    public static Rooms3x3Placement Identity => new(0, 0, 0);

    /// <summary>A room-local point, in world space.</summary>
    /// <param name="p">The point.</param>
    public Point Apply(Point p)
    {
        const float c = Rooms3x3Kit.CellSize;
        (float x, float y) = (((Rotation % 4) + 4) % 4) switch
        {
            0 => (p.X, p.Y),
            1 => (c - p.Y, p.X),
            2 => (c - p.X, c - p.Y),
            _ => (p.Y, c - p.X),
        };

        return new Point(x + (CellX * c), y + (CellY * c), p.Z);
    }

    /// <summary>A room-local box, in world space: still a box, because the turns are quarter turns.</summary>
    /// <param name="box">The box.</param>
    public Bounds Apply(Bounds box)
    {
        Point a = Apply(box.Mins);
        Point b = Apply(box.Maxs);
        return new Bounds(
            new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }
}

/// <summary>One cell of an arrangement: the room kind and its turns.</summary>
/// <param name="Kind">The room kind's name.</param>
/// <param name="Rotation">Quarter turns, 0 to 3.</param>
public readonly record struct Rooms3x3Cell(string Kind, int Rotation);

/// <summary>
/// A 3x3 level: which room kind stands in each cell, and how it is turned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Joints are derived, never chosen.</b> Two neighbouring rooms are
/// jointed exactly when both have a socket on the wall they share. That is
/// the only reading under which a VMF of "the same setup" is unambiguous: the
/// doorway is open in the reference exactly where the layout joints it. A
/// socket facing the outside of the grid is capped, and stays a plugged
/// doorway in both maps.
/// </para>
/// <para>
/// <b>Validity.</b> An arrangement is valid when its sockets line up — no
/// shared wall has a socket on one side and a plain wall on the other, which
/// would be a door that opens onto a wall — and when the joints connect all
/// nine rooms into one level. Both are properties of the socket sets alone,
/// so the enumeration can decide them without compiling anything.
/// </para>
/// </remarks>
public sealed class Rooms3x3Arrangement : IEquatable<Rooms3x3Arrangement>
{
    /// <summary>The grid's edge in cells.</summary>
    public const int Size = 3;

    private readonly Rooms3x3Cell[] _cells;

    /// <summary>An arrangement from its nine cells, row by row from (0, 0): x fastest.</summary>
    /// <param name="cells">Nine cells.</param>
    public Rooms3x3Arrangement(IReadOnlyList<Rooms3x3Cell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count != Size * Size)
        {
            throw new ArgumentException($"a 3x3 arrangement has nine cells, not {cells.Count}", nameof(cells));
        }

        foreach (Rooms3x3Cell cell in cells)
        {
            _ = Rooms3x3Kit.Kind(cell.Kind);
            if ((uint)cell.Rotation > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(cells), cell.Rotation, "a rotation is 0 to 3 quarter turns");
            }
        }

        _cells = [.. cells];
    }

    /// <summary>The cells, row by row from (0, 0), x fastest.</summary>
    public IReadOnlyList<Rooms3x3Cell> Cells => _cells;

    /// <summary>The cell at a grid position.</summary>
    /// <param name="x">The column, 0 to 2.</param>
    /// <param name="y">The row, 0 to 2.</param>
    public Rooms3x3Cell this[int x, int y] => _cells[(y * Size) + x];

    /// <summary>The placement of the room at a grid position.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    public Rooms3x3Placement Placement(int x, int y) => new(x, y, this[x, y].Rotation);

    /// <summary>The world faces the room at a cell has sockets on.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    public IEnumerable<KitSide> WorldSockets(int x, int y)
    {
        Rooms3x3Cell cell = this[x, y];
        return Rooms3x3Kit.Kind(cell.Kind).Sockets.Select(s => Rooms3x3Kit.Turn(s, cell.Rotation));
    }

    /// <summary>Whether a room has a socket on a world face.</summary>
    private bool HasWorldSocket(int x, int y, KitSide worldSide) => WorldSockets(x, y).Contains(worldSide);

    private static bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;

    /// <summary>
    /// The room-local sockets of a cell that are jointed: both it and the
    /// neighbour it faces have a socket on the shared wall.
    /// </summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    public IReadOnlyList<(KitSide Mine, KitSide Theirs)> Joints(int x, int y)
    {
        Rooms3x3Cell cell = this[x, y];
        List<(KitSide, KitSide)> joints = [];
        foreach (KitSide local in Rooms3x3Kit.Kind(cell.Kind).Sockets)
        {
            KitSide world = Rooms3x3Kit.Turn(local, cell.Rotation);
            (int dx, int dy) = Rooms3x3Kit.Step(world);
            int nx = x + dx, ny = y + dy;
            if (!Inside(nx, ny) || !HasWorldSocket(nx, ny, Rooms3x3Kit.Opposite(world)))
            {
                continue;
            }

            Rooms3x3Cell other = this[nx, ny];
            KitSide theirs = Rooms3x3Kit.Kind(other.Kind).Sockets
                .First(s => Rooms3x3Kit.Turn(s, other.Rotation) == Rooms3x3Kit.Opposite(world));
            joints.Add((local, theirs));
        }

        return joints;
    }

    /// <summary>The room-local sockets of a cell that face the outside of the grid, and are capped.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    public IReadOnlyList<KitSide> Caps(int x, int y)
    {
        HashSet<KitSide> jointed = [.. Joints(x, y).Select(j => j.Mine)];
        return [.. Rooms3x3Kit.Kind(this[x, y].Kind).Sockets.Where(s => !jointed.Contains(s))];
    }

    /// <summary>
    /// Whether every shared wall has a socket on both sides or on neither.
    /// </summary>
    public bool SocketsLineUp()
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (x + 1 < Size && HasWorldSocket(x, y, KitSide.East) != HasWorldSocket(x + 1, y, KitSide.West))
                {
                    return false;
                }

                if (y + 1 < Size && HasWorldSocket(x, y, KitSide.North) != HasWorldSocket(x, y + 1, KitSide.South))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Which cells each cell reaches through jointed doors: a component
    /// number per cell, numbered in row order from 0.
    /// </summary>
    public int[] Components()
    {
        int[] component = Enumerable.Repeat(-1, Size * Size).ToArray();
        int next = 0;
        for (int start = 0; start < component.Length; start++)
        {
            if (component[start] >= 0)
            {
                continue;
            }

            Stack<int> stack = new([start]);
            component[start] = next;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int x = at % Size, y = at / Size;
                foreach ((KitSide local, _) in Joints(x, y))
                {
                    (int dx, int dy) = Rooms3x3Kit.Step(Rooms3x3Kit.Turn(local, this[x, y].Rotation));
                    int neighbour = ((y + dy) * Size) + x + dx;
                    if (component[neighbour] < 0)
                    {
                        component[neighbour] = next;
                        stack.Push(neighbour);
                    }
                }
            }

            next++;
        }

        return component;
    }

    /// <summary>Whether the arrangement is a level: sockets line up and every room is reachable.</summary>
    public bool IsValid() => SocketsLineUp() && Components().All(c => c == 0);

    /// <summary>
    /// The same level turned a quarter counter-clockwise about the grid's
    /// centre: each room moves to the turned cell and takes one more turn.
    /// </summary>
    public Rooms3x3Arrangement Turned()
    {
        Rooms3x3Cell[] cells = new Rooms3x3Cell[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                // (x, y) about the centre (1, 1) by a quarter turn: (2 - y, x).
                int nx = Size - 1 - y, ny = x;
                Rooms3x3Cell cell = this[x, y];
                cells[(ny * Size) + nx] = cell with { Rotation = (cell.Rotation + 1) % 4 };
            }
        }

        return new Rooms3x3Arrangement(cells);
    }

    /// <summary>
    /// The layout JSON <c>ssmap link</c> reads for this arrangement: every room
    /// in row order, its derived joints, and its capped sockets.
    /// </summary>
    /// <param name="name">The level's name.</param>
    public string LayoutJson(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Rooms3x3Kit.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            Rooms3x3Kit.WriteGrid(writer);
            writer.WriteStartArray("rooms");
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("room", this[x, y].Kind);
                    writer.WriteNumber("cellX", x);
                    writer.WriteNumber("cellY", y);
                    writer.WriteNumber("rotation", this[x, y].Rotation);
                    writer.WriteStartArray("joints");
                    foreach ((KitSide mine, KitSide theirs) in Joints(x, y))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("socket", Rooms3x3Kit.SocketName(mine));
                        writer.WriteString("neighborSocket", Rooms3x3Kit.SocketName(theirs));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteStartArray("capped");
                    foreach (KitSide cap in Caps(x, y))
                    {
                        writer.WriteStringValue(Rooms3x3Kit.SocketName(cap));
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The same level as ONE map: every room's brushes and entities placed
    /// and turned in world space, the plugs of jointed sockets left out (the
    /// doorway is open) and the plugs of capped ones kept.
    /// </summary>
    /// <remarks>
    /// This is what the linked map is checked against. It is built from the
    /// kit's own brush lists through <see cref="Rooms3x3Placement"/>, sharing
    /// nothing with the linker, and compiled by the ordinary vbsp path.
    /// </remarks>
    public string MonolithicVmf()
    {
        VmfMap map = new();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                HashSet<KitSide> open = [.. Joints(x, y).Select(j => j.Mine)];
                Rooms3x3Kit.Place(map, Rooms3x3Kit.Kind(this[x, y].Kind), Placement(x, y), open);
            }
        }

        return map.Write();
    }

    /// <summary>A compact, stable spelling: <c>kind@turns</c> per cell in row order.</summary>
    public override string ToString()
    {
        StringBuilder text = new();
        for (int i = 0; i < _cells.Length; i++)
        {
            if (i > 0)
            {
                text.Append(i % Size == 0 ? " / " : " ");
            }

            text.Append(_cells[i].Kind).Append('@').Append(_cells[i].Rotation.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <inheritdoc/>
    public bool Equals(Rooms3x3Arrangement? other) => other is not null && _cells.SequenceEqual(other._cells);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Rooms3x3Arrangement);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (Rooms3x3Cell cell in _cells)
        {
            hash.Add(cell);
        }

        return hash.ToHashCode();
    }
}
