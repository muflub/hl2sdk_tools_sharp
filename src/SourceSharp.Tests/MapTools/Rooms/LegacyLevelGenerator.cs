//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A frozen copy of <see cref="LevelGenerator"/> as it was before its
/// candidates became <c>int</c> indices, its fill became iterative and its
/// empty-cell pass stopped flood-filling the whole grid per removal.
/// </summary>
/// <remarks>
/// <para>
/// It exists only so <c>LevelGeneratorEquivalenceTests</c> can compare the
/// rewritten generator with the one every checked-in seeded level was made
/// by, level for level and refusal for refusal, over many seeds, shapes and
/// libraries. A seed names a level in files people keep, so the rewrite had
/// to be a pure change of cost: the same draws from the sequence in the same
/// order, the same backtracking order, the same step count at which a tree is
/// given up.
/// </para>
/// <para>
/// Do not "fix" or tidy this copy: its value is that it is the old code
/// verbatim, bar its name, the header lines it does not need and
/// <c>EmptyCells</c> being internal so a fact can compare that pass alone. If the
/// generator's output is ever meant to change, that change retires this
/// copy and the facts that compare against it, and regenerates the samples.
/// </para>
/// </remarks>
internal static class LegacyLevelGenerator
{
    /// <summary>How many spanning trees are tried before the generator gives up.</summary>
    private const int Attempts = 64;

    /// <summary>How many placements one tree's search may try.</summary>
    private const int StepBudget = 200_000;

    private const int East = 1, West = 2, North = 4, South = 8;

    /// <summary>Generates a level.</summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="options">The grid, the seed and the empty share.</param>
    /// <param name="name">The level's name.</param>
    /// <param name="library">The library as the level file should name it.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentException">The options are out of range, or there are no rooms.</exception>
    /// <exception cref="LinkException">No valid level could be made from these rooms on this grid.</exception>
    public static LevelGrid Generate(
        IReadOnlyList<RoomDefinition> rooms, LevelGeneratorOptions options, string name, string library)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Columns, 1);
        if ((long)options.Rows * options.Columns > LevelYaml.MaxCells)
        {
            throw new ArgumentException(
                $"a {options.Rows}x{options.Columns} grid has more than {LevelYaml.MaxCells} cells", nameof(options));
        }

        if (!(options.EmptyRatio >= 0 && options.EmptyRatio < 1))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the empty ratio {options.EmptyRatio} is not in [0, 1)"),
                nameof(options));
        }

        if (rooms.Count == 0)
        {
            throw new ArgumentException("a level needs at least one room to place", nameof(rooms));
        }

        int columns = options.Columns;
        int cellCount = options.Rows * columns;
        SplitMix64 random = new(options.Seed);

        bool[] occupied = EmptyCells(random, options.Rows, columns, (int)Math.Floor(options.EmptyRatio * cellCount));
        int placed = occupied.Count(o => o);

        // Every (room, rotation) and the world walls it has sockets on. A room
        // with no socket can only stand alone.
        List<(int Room, int Rotation, int Mask)> candidates = [];
        for (int r = 0; r < rooms.Count; r++)
        {
            if (rooms[r].Sockets.Count == 0 && placed > 1)
            {
                continue;
            }

            for (int rotation = 0; rotation < 4; rotation++)
            {
                candidates.Add((r, rotation, Mask(rooms[r], rotation)));
            }
        }

        if (candidates.Count == 0)
        {
            throw new LinkException(
                $"none of the library's {rooms.Count} room(s) has a socket, so {placed} rooms cannot be joined.");
        }

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            HashSet<(int, int)> tree = SpanningTree(random, options.Rows, columns, occupied);
            (int Room, int Rotation, int Mask)[]?[] order = new (int, int, int)[]?[cellCount];
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (occupied[cell])
                {
                    List<(int, int, int)> shuffled = [.. candidates];
                    random.Shuffle(shuffled);
                    order[cell] = [.. shuffled];
                }
            }

            int[] chosen = new int[cellCount];
            int[] masks = new int[cellCount];
            int steps = 0;
            if (Fill(0))
            {
                LevelCell?[] cells = new LevelCell?[cellCount];
                for (int cell = 0; cell < cellCount; cell++)
                {
                    if (occupied[cell])
                    {
                        (int room, int rotation, _) = order[cell]![chosen[cell]];
                        cells[cell] = new LevelCell(rooms[room].Name, rotation);
                    }
                }

                return new LevelGrid(name, library, options.Rows, columns, cells);
            }

            bool Fill(int cell)
            {
                if (cell == cellCount)
                {
                    return true;
                }

                if (!occupied[cell])
                {
                    return Fill(cell + 1);
                }

                int x = cell % columns, y = cell / columns;
                (int, int, int)[] tries = order[cell]!;
                for (int i = 0; i < tries.Length; i++)
                {
                    if (++steps > StepBudget)
                    {
                        return false;
                    }

                    int mask = tries[i].Item3;
                    if (Fits(mask, x, y))
                    {
                        chosen[cell] = i;
                        masks[cell] = mask;
                        if (Fill(cell + 1))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            bool Fits(int mask, int x, int y)
            {
                foreach ((int side, int dx, int dy, int back) in Sides())
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= columns || ny >= options.Rows || !occupied[(ny * columns) + nx])
                    {
                        continue;
                    }

                    int here = (y * columns) + x, there = (ny * columns) + nx;
                    bool has = (mask & side) != 0;
                    if (tree.Contains((Math.Min(here, there), Math.Max(here, there))) && !has)
                    {
                        return false;
                    }

                    // West and south neighbours are placed before this cell.
                    if (there < here && has != ((masks[there] & back) != 0))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        throw new LinkException(string.Create(CultureInfo.InvariantCulture,
            $"no level of {options.Rows}x{columns} cells with every room reachable was found from the library's"
            + $" {rooms.Count} room(s) with seed {options.Seed} after {Attempts} tries; the rooms' sockets may not allow one."));
    }

    /// <summary>The four walls: the socket bit, the step to the neighbour, and the neighbour's bit facing back.</summary>
    /// <remarks>A fresh array per call rather than a static one: an array's elements are writable, and MapTools holds no mutable static state.</remarks>
    private static (int Side, int Dx, int Dy, int Back)[] Sides() =>
        [(East, 1, 0, West), (West, -1, 0, East), (North, 0, 1, South), (South, 0, -1, North)];

    /// <summary>The world walls a room has sockets on, turned.</summary>
    private static int Mask(RoomDefinition room, int rotation)
    {
        RoomTransform transform = new(new RoomPlacement(room.Name, 0, 0, rotation), 1f);
        int mask = 0;
        foreach (RoomSocket socket in room.Sockets)
        {
            mask |= transform.WorldNormal(socket.Facing) switch
            {
                (0, 1) => East,
                (0, _) => West,
                (1, 1) => North,
                _ => South,
            };
        }

        return mask;
    }

    /// <summary>Which cells hold a room: all but <paramref name="empty"/>, the rest kept joined.</summary>
    internal static bool[] EmptyCells(SplitMix64 random, int rows, int columns, int empty)
    {
        int count = rows * columns;
        bool[] occupied = Enumerable.Repeat(true, count).ToArray();
        empty = Math.Min(empty, count - 1);
        List<int> order = [.. Enumerable.Range(0, count)];
        random.Shuffle(order);

        int removed = 0;
        bool progress = true;
        while (removed < empty && progress)
        {
            progress = false;
            foreach (int cell in order)
            {
                if (removed == empty)
                {
                    break;
                }

                if (!occupied[cell])
                {
                    continue;
                }

                occupied[cell] = false;
                if (Joined(occupied, rows, columns))
                {
                    removed++;
                    progress = true;
                }
                else
                {
                    occupied[cell] = true;
                }
            }
        }

        return occupied;
    }

    /// <summary>Whether the occupied cells are one group through shared walls.</summary>
    private static bool Joined(bool[] occupied, int rows, int columns)
    {
        int start = Array.IndexOf(occupied, true);
        if (start < 0)
        {
            return false;
        }

        bool[] seen = new bool[occupied.Length];
        Stack<int> stack = new([start]);
        seen[start] = true;
        int reached = 1;
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            int x = at % columns, y = at / columns;
            foreach ((_, int dx, int dy, _) in Sides())
            {
                int nx = x + dx, ny = y + dy;
                int next = (ny * columns) + nx;
                if (nx >= 0 && ny >= 0 && nx < columns && ny < rows && occupied[next] && !seen[next])
                {
                    seen[next] = true;
                    reached++;
                    stack.Push(next);
                }
            }
        }

        return reached == occupied.Count(o => o);
    }

    /// <summary>A random spanning tree of the occupied cells: the walls that must be doorways, as cell pairs (low, high).</summary>
    private static HashSet<(int, int)> SpanningTree(SplitMix64 random, int rows, int columns, bool[] occupied)
    {
        List<(int, int)> walls = [];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int here = (y * columns) + x;
                if (!occupied[here])
                {
                    continue;
                }

                if (x + 1 < columns && occupied[here + 1])
                {
                    walls.Add((here, here + 1));
                }

                if (y + 1 < rows && occupied[here + columns])
                {
                    walls.Add((here, here + columns));
                }
            }
        }

        random.Shuffle(walls);
        int[] parent = [.. Enumerable.Range(0, occupied.Length)];
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        HashSet<(int, int)> tree = [];
        foreach ((int a, int b) in walls)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                parent[ra] = rb;
                tree.Add((a, b));
            }
        }

        return tree;
    }
}
