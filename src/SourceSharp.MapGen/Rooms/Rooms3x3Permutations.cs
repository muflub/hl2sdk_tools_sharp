//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapGen.Rooms;

/// <summary>Why an arrangement is in the verified set.</summary>
/// <param name="Name">A short, stable name for the arrangement: a file base name and a test case name.</param>
/// <param name="Reason">Which rules of the subset chose it.</param>
/// <param name="Arrangement">The arrangement.</param>
public sealed record Rooms3x3Case(string Name, string Reason, Rooms3x3Arrangement Arrangement);

/// <summary>
/// The rearrangements of the sample level: every valid arrangement of its nine
/// rooms, and the deterministic subset the default test run verifies.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is permuted.</b> The sample level's nine rooms — one cross, two
/// tees, four corners, one hall and one end room — placed in any cell with any
/// rotation. That is 9!/(4!·2!) = 7,560 placements times 4^9 rotations, about
/// two billion candidates, and almost none of them are levels: a door that
/// opens onto its neighbour's wall is not a joint. The valid ones are those
/// whose sockets line up on every shared wall and whose joints connect all
/// nine rooms (<see cref="Rooms3x3Arrangement.IsValid"/>). There are
/// <see cref="ValidCount"/> of them.
/// </para>
/// <para>
/// <b>The enumeration</b> is a backtracking search in a fixed order — cells row
/// by row from (0, 0), kinds in <see cref="Rooms3x3Kit.Kinds"/> order,
/// rotations 0 to 3 — that refuses a room as soon as it disagrees with its
/// already-placed west or south neighbour. The order is part of the contract:
/// the subset below names arrangements by their position in it.
/// </para>
/// <para>
/// <b>The default subset</b>, in this order, each arrangement once (a rule that
/// picks one already chosen adds its reason to that case):
/// </para>
/// <list type="number">
/// <item>the sample level itself and its three whole-level quarter turns — the
/// same level, every room moved to another cell and turned;</item>
/// <item>for each kind and for each of two cell classes, the centre (1, 1) and
/// the corner (0, 0), the first valid arrangement in enumeration order with
/// that kind there — every kind in the most-connected cell and in a
/// least-connected one;</item>
/// <item><see cref="SampleCount"/> arrangements drawn from the whole
/// enumeration by a fixed-seed generator, so the run reaches arrangements the
/// rules above would never pick.</item>
/// </list>
/// </remarks>
public static class Rooms3x3Permutations
{
    /// <summary>How many valid arrangements the sample level's rooms have.</summary>
    public const int ValidCount = 85_088;

    /// <summary>The seed of the drawn sample.</summary>
    public const ulong Seed = 0x5EED_3A3A_0000_0009UL;

    /// <summary>How many arrangements the drawn sample adds.</summary>
    public const int SampleCount = 4;

    /// <summary>The sample level's name, and its files' base name.</summary>
    public const string LevelName = "rooms3x3";

    /// <summary>
    /// The sample level: a cross at the centre, the doors making one loop
    /// through the south-east quarter, and three sockets on the grid's edge
    /// capped.
    /// </summary>
    /// <remarks>
    /// Row by row, north at the top (world faces in brackets):
    /// <code>
    /// y=2   end    @3 (S)        hall  @1 (N cap, S)   corner @3 (S, E cap)
    /// y=1   tee    @3 (S, N, E)  cross @0 (all four)   tee    @1 (N, S, W)
    /// y=0   corner @1 (N, W cap) corner@0 (E, N)       corner @1 (N, W)
    /// </code>
    /// </remarks>
    public static Rooms3x3Arrangement Canonical { get; } = new(
    [
        new("corner", 1), new("corner", 0), new("corner", 1),
        new("tee", 3), new("cross", 0), new("tee", 1),
        new("end", 3), new("hall", 1), new("corner", 3),
    ]);

    /// <summary>The sample level's rooms, kind by kind: what every arrangement rearranges.</summary>
    public static IReadOnlyDictionary<string, int> Multiset { get; } = Canonical.Cells
        .GroupBy(c => c.Kind, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>
    /// Every valid arrangement of the sample level's rooms, in enumeration order.
    /// </summary>
    /// <remarks>
    /// The search works on socket bitmasks (one bit per world face, per kind
    /// and rotation) rather than on <see cref="Rooms3x3Arrangement"/>, which
    /// only the valid results become: the tree has hundreds of thousands of
    /// nodes, and the line-up and connectivity rules are both a few bit tests.
    /// </remarks>
    public static IReadOnlyList<Rooms3x3Arrangement> All()
    {
        IReadOnlyList<RoomKind> kinds = Rooms3x3Kit.Kinds;
        int[,] masks = new int[kinds.Count, 4];
        int[] left = new int[kinds.Count];
        for (int k = 0; k < kinds.Count; k++)
        {
            left[k] = Multiset.GetValueOrDefault(kinds[k].Name);
            for (int rotation = 0; rotation < 4; rotation++)
            {
                foreach (KitSide side in kinds[k].Sockets)
                {
                    masks[k, rotation] |= Bit(Rooms3x3Kit.Turn(side, rotation));
                }
            }
        }

        List<Rooms3x3Arrangement> found = [];
        int cellCount = Rooms3x3Arrangement.Size * Rooms3x3Arrangement.Size;
        int[] kind = new int[cellCount];
        int[] turns = new int[cellCount];
        int[] placed = new int[cellCount];
        Place(0);
        return found;

        void Place(int index)
        {
            if (index == cellCount)
            {
                if (Connected(placed))
                {
                    Rooms3x3Cell[] cells = new Rooms3x3Cell[cellCount];
                    for (int i = 0; i < cellCount; i++)
                    {
                        cells[i] = new Rooms3x3Cell(kinds[kind[i]].Name, turns[i]);
                    }

                    found.Add(new Rooms3x3Arrangement(cells));
                }

                return;
            }

            int x = index % Rooms3x3Arrangement.Size;
            int y = index / Rooms3x3Arrangement.Size;
            for (int k = 0; k < kinds.Count; k++)
            {
                if (left[k] == 0)
                {
                    continue;
                }

                left[k]--;
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    int mask = masks[k, rotation];

                    // The west and south neighbours are already placed; the
                    // east and north ones check this room when their turn comes.
                    if (x > 0 && Has(mask, KitSide.West) != Has(placed[index - 1], KitSide.East))
                    {
                        continue;
                    }

                    if (y > 0 && Has(mask, KitSide.South) != Has(placed[index - Rooms3x3Arrangement.Size], KitSide.North))
                    {
                        continue;
                    }

                    kind[index] = k;
                    turns[index] = rotation;
                    placed[index] = mask;
                    Place(index + 1);
                }

                left[k]++;
            }
        }
    }

    private static int Bit(KitSide side) => 1 << (int)side;

    private static bool Has(int mask, KitSide side) => (mask & Bit(side)) != 0;

    /// <summary>
    /// Whether the joints of a lined-up grid reach every cell from (0, 0). On
    /// a lined-up grid a wall is a joint exactly when the room west or south
    /// of it has a socket on it, so one side's mask decides each wall.
    /// </summary>
    private static bool Connected(int[] masks)
    {
        const int size = Rooms3x3Arrangement.Size;
        int reached = 1;
        int before;
        do
        {
            before = reached;
            for (int i = 0; i < masks.Length; i++)
            {
                int x = i % size, y = i / size;
                if (x + 1 < size && Has(masks[i], KitSide.East) && (((reached >> i) | (reached >> (i + 1))) & 1) != 0)
                {
                    reached |= (1 << i) | (1 << (i + 1));
                }

                if (y + 1 < size && Has(masks[i], KitSide.North) && (((reached >> i) | (reached >> (i + size))) & 1) != 0)
                {
                    reached |= (1 << i) | (1 << (i + size));
                }
            }
        }
        while (before != reached);

        return reached == (1 << masks.Length) - 1;
    }

    /// <summary>
    /// The default subset, as named cases: see the type's remarks for the
    /// three rules and their order.
    /// </summary>
    /// <param name="all">The whole enumeration, as <see cref="All"/> returns it.</param>
    public static IReadOnlyList<Rooms3x3Case> DefaultCases(IReadOnlyList<Rooms3x3Arrangement> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (all.Count == 0)
        {
            throw new ArgumentException("the enumeration is empty, so there is nothing to draw from", nameof(all));
        }

        List<Rooms3x3Case> cases = [];
        void Add(string name, string reason, Rooms3x3Arrangement arrangement)
        {
            int at = cases.FindIndex(c => c.Arrangement.Equals(arrangement));
            if (at < 0)
            {
                cases.Add(new Rooms3x3Case(name, reason, arrangement));
            }
            else
            {
                cases[at] = cases[at] with { Reason = cases[at].Reason + "; " + reason };
            }
        }

        Rooms3x3Arrangement turned = Canonical;
        for (int turns = 0; turns < 4; turns++)
        {
            Add(TurnName(turns), $"the sample level turned {turns} quarter(s)", turned);
            turned = turned.Turned();
        }

        foreach ((string cell, int x, int y) in new[] { ("centre", 1, 1), ("corner", 0, 0) })
        {
            foreach (RoomKind kind in Rooms3x3Kit.Kinds)
            {
                Rooms3x3Arrangement? first = all.FirstOrDefault(a => a[x, y].Kind == kind.Name);
                if (first is not null)
                {
                    Add($"{kind.Name}_in_{cell}", $"the first arrangement with the {kind.Name} in the {cell}", first);
                }
            }
        }

        ulong state = Seed;
        for (int drawn = 0; drawn < SampleCount; drawn++)
        {
            int index = (int)(SplitMix(ref state) % (ulong)all.Count);
            Add($"drawn_{index}", $"arrangement {index} of {all.Count}, drawn with seed {Seed:X}", all[index]);
        }

        return cases;
    }

    /// <summary>The file base name of the sample level turned a number of quarters.</summary>
    /// <param name="turns">0 to 3.</param>
    public static string TurnName(int turns) => turns == 0 ? LevelName : $"{LevelName}_turn{turns}";

    /// <summary>
    /// SplitMix64: a whole-integer generator, so the drawn sample is the same
    /// on every runtime and CPU, which a floating-point or library generator
    /// does not promise.
    /// </summary>
    private static ulong SplitMix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
