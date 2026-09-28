//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapGen.Rooms;

/// <summary>Why an arrangement is in the verified set.</summary>
/// <param name="Name">A short, stable name for the arrangement: a file base name and a test case name.</param>
/// <param name="Reason">Which rules of the subset chose it.</param>
/// <param name="Arrangement">The arrangement.</param>
public sealed record Rooms3x3Case(string Name, string Reason, Rooms3x3Arrangement Arrangement);

/// <summary>
/// The rearrangements of the sample level: every valid arrangement of its nine
/// rooms, for the exhaustive sweep, and the seeded levels the default test
/// run verifies.
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
/// <see cref="ValidCount"/> of them, and the opt-in sweep checks any number
/// of them up to all.
/// </para>
/// <para>
/// <b>The enumeration</b> is a backtracking search in a fixed order — cells row
/// by row from (0, 0), kinds in <see cref="Rooms3x3Kit.Kinds"/> order,
/// rotations 0 to 3 — that refuses a room as soon as it disagrees with its
/// already-placed west or south neighbour. The order is part of the contract:
/// the sweep names arrangements by their position in it.
/// </para>
/// <para>
/// <b>The default subset</b>, in this order:
/// </para>
/// <list type="number">
/// <item>the sample level itself and its three whole-level quarter turns — the
/// same level, every room moved to another cell and turned;</item>
/// <item>the <see cref="Seeds"/>: 3x3 levels drawn by the seeded generator
/// <c>ssmap layout</c> runs (<see cref="LevelGenerator"/>) from the sample
/// library, with fixed seeds. These are not permutations of the nine rooms:
/// the generator places any room of the library as often as it likes, and
/// some of the seeds leave cells empty, so the run covers levels the sample
/// level's rooms could never make.</item>
/// </list>
/// </remarks>
public static class Rooms3x3Permutations
{
    /// <summary>How many valid arrangements the sample level's rooms have.</summary>
    public const int ValidCount = 85_088;

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
        .Select(c => c!.Value)
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
                    Rooms3x3Cell?[] cells = new Rooms3x3Cell?[cellCount];
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
    /// The seeded levels of the default run: name, seed, and the share of
    /// the nine cells left empty (a quarter leaves two).
    /// </summary>
    public static IReadOnlyList<(string Name, ulong Seed, double EmptyRatio)> Seeds { get; } =
    [
        ("seed_1", 1, 0), ("seed_2", 2, 0), ("seed_3", 3, 0), ("seed_4", 4, 0),
        ("seed_5", 5, 0), ("seed_6", 6, 0), ("seed_7", 7, 0), ("seed_8", 8, 0),
        ("seed_9", 9, 0.25), ("seed_10", 10, 0.25), ("seed_11", 11, 0.25), ("seed_12", 12, 0.25),
    ];

    /// <summary>The seeded levels checked into the sample folder, by name: two full grids, two with empty cells.</summary>
    public static IReadOnlyList<string> SampleSeeds { get; } = ["seed_1", "seed_2", "seed_9", "seed_10"];

    /// <summary>A seeded level of the sample library, as <c>ssmap layout rooms.vmf -rows 3 -columns 3</c> writes it.</summary>
    /// <param name="name">The level's name: one of <see cref="Seeds"/>.</param>
    /// <param name="library">The library as the level file names it.</param>
    public static LevelGrid Seeded(string name, string library)
    {
        (string _, ulong seed, double empty) = Seeds.Single(s => s.Name == name);
        return LevelGenerator.Generate(
            [.. Rooms3x3Kit.Kinds.Select(Rooms3x3Kit.Definition)],
            new LevelGeneratorOptions(Rooms3x3Arrangement.Size, Rooms3x3Arrangement.Size, seed, empty),
            name,
            library);
    }

    /// <summary>
    /// The default subset, as named cases: see the type's remarks for the
    /// two rules and their order.
    /// </summary>
    public static IReadOnlyList<Rooms3x3Case> DefaultCases()
    {
        List<Rooms3x3Case> cases = [];
        Rooms3x3Arrangement turned = Canonical;
        for (int turns = 0; turns < 4; turns++)
        {
            cases.Add(new Rooms3x3Case(TurnName(turns), $"the sample level turned {turns} quarter(s)", turned));
            turned = turned.Turned();
        }

        foreach ((string name, ulong seed, double empty) in Seeds)
        {
            cases.Add(new Rooms3x3Case(
                name,
                string.Create(CultureInfo.InvariantCulture, $"generated with seed {seed}, {empty:0.##} of the cells empty"),
                Rooms3x3Arrangement.FromLevel(Seeded(name, Rooms3x3Kit.LibraryFile))));
        }

        return cases;
    }

    /// <summary>The file base name of the sample level turned a number of quarters.</summary>
    /// <param name="turns">0 to 3.</param>
    public static string TurnName(int turns) => turns == 0 ? LevelName : $"{LevelName}_turn{turns}";
}
