//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What the seeded generator is asked for.</summary>
/// <param name="Rows">The grid's rows.</param>
/// <param name="Columns">The grid's columns.</param>
/// <param name="Seed">The seed; the same seed and library give the same level.</param>
/// <param name="EmptyRatio">
/// The share of cells left without a room, from 0 up to but not including 1:
/// <c>floor(ratio × cells)</c> cells are left empty, and at least one room is
/// always placed.
/// </param>
public sealed record LevelGeneratorOptions(int Rows, int Columns, ulong Seed, double EmptyRatio = 0)
{
    /// <summary>The group size <c>ssmap layout</c> uses when <c>-group</c> is not given.</summary>
    public const int DefaultGroupSize = 3;

    /// <summary>
    /// The share of the occupied cells large rooms cover (<c>-large</c>),
    /// from 0 up to but not including 1; 0 by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>large</b> room is one taller than its cell
    /// (<see cref="RoomDefinition.Height"/> above
    /// <see cref="RoomDefinition.CellSize"/>); every other room, a low
    /// hallway included, is <b>standard</b>. Large rooms stand only in
    /// groups of adjacent large rooms (the rooms design, 17.9), each group
    /// reached through standard rooms.
    /// </para>
    /// <para>
    /// At 0 the large rooms are dropped before anything is drawn, so a
    /// library that gains tall rooms still generates, seed for seed, the
    /// levels it generated before it had them, and a library without any
    /// generates exactly what it always did.
    /// </para>
    /// </remarks>
    public double LargeShare { get; init; }

    /// <summary>The most large rooms one group holds (<c>-group</c>), 1 or more; <see cref="DefaultGroupSize"/> by default.</summary>
    public int GroupSize { get; init; } = DefaultGroupSize;

    /// <summary>
    /// The tallest room the level may place, in units (<c>-max-height</c>),
    /// or null for no limit: a room taller than this is dropped from every
    /// candidate list, standard or large, before anything is drawn.
    /// </summary>
    public float? MaxHeight { get; init; }
}

/// <summary>An entity budget a generated level must keep within.</summary>
/// <param name="Budget">
/// The most edicts the level may have: its one worldspawn and every
/// placement's edicts. <c>ssmap layout</c> uses the link's
/// <c>cap - reserve</c> unless told otherwise, so a generated level never
/// eats into the reserve.
/// </param>
/// <param name="RoomEdicts">
/// What one placement of each room costs, in the library order of the rooms
/// the generator is given (<see cref="EntityTally.Edicts"/> of the room's
/// <see cref="RoomEntityCounts.Tally"/>).
/// </param>
public sealed record LayoutEntityBudget(int Budget, IReadOnlyList<int> RoomEdicts)
{
    /// <summary>
    /// The edicts the level has whatever rooms it places, besides its one
    /// worldspawn: the library's own entities (the sun and the other
    /// library-wide singletons), which the link writes once per level. 0 by
    /// default; <c>ssmap layout</c> counts them from the pack.
    /// </summary>
    public int LevelEdicts { get; init; }
}

/// <summary>What the generator is asked for about transition rooms (the rooms design, 11.2).</summary>
/// <param name="Roles">Each room's role, in the library order of the rooms the generator is given.</param>
/// <remarks>
/// A library with role rooms places exactly one up room and one down room
/// per level, unless <see cref="NoUp"/> or <see cref="NoDown"/> switches the
/// role off (the top and bottom levels of a run), and never a role room
/// anywhere else. A library without role rooms ignores all of this.
/// </remarks>
public sealed record LayoutTransitions(IReadOnlyList<RoomRole> Roles)
{
    /// <summary>The level holds no up room (<c>up: none</c>).</summary>
    public bool NoUp { get; init; }

    /// <summary>The level holds no down room (<c>down: none</c>).</summary>
    public bool NoDown { get; init; }

    /// <summary>
    /// The fewest doors on the shortest path between the up and down rooms
    /// (<c>-transition-distance</c>), so the player crosses the level; 0 for
    /// no minimum. Only read when the level has both.
    /// </summary>
    public int MinDistance { get; init; }
}

/// <summary>
/// Makes a valid level of a library's rooms from a seed: every shared wall
/// between two rooms has a socket on both sides or on neither, and every
/// room can be reached from every other.
/// </summary>
/// <remarks>
/// <para>
/// <b>How.</b> Everything random comes from one <see cref="SplitMix64"/>
/// sequence, in a fixed order, so a seed and a library (its rooms in library
/// order) always give the same level.
/// </para>
/// <list type="number">
/// <item><b>The empty cells.</b> The cells are shuffled, and cells are
/// emptied in that order, skipping any whose removal would split the
/// remaining cells into two groups with no shared wall between them, until
/// <c>floor(ratio × cells)</c> are empty. A connected set of cells always
/// has one it can lose (a leaf of any spanning tree), so the count is
/// always reached.</item>
/// <item><b>The doors every level must have.</b> A random spanning tree over
/// the occupied cells (the shared walls in shuffled order, each kept if it
/// joins two groups not yet joined) picks the walls that must be doorways.
/// Requiring those makes every room reachable by construction: the tree
/// touches every room, and a tree wall with a socket on both sides is a
/// joint.</item>
/// <item><b>The rooms.</b> A backtracking search fills the occupied cells
/// row by row from the south-west, trying each room and rotation of the
/// library in a per-cell shuffled order: a tree wall must have a socket; a
/// wall shared with an already-placed neighbour must have a socket exactly
/// when the neighbour has one there; a wall on the grid's edge or onto an
/// empty cell may have one or not (it is capped). A search that runs out of
/// its step budget gives up on that tree and draws another.</item>
/// </list>
/// <para>
/// <b>Transition rooms</b> (<see cref="LayoutTransitions"/>) come from a
/// <b>second</b> <see cref="SplitMix64"/> sequence, seeded from the seed and
/// a fixed constant (<see cref="RoleStream"/>), created only when the library
/// has role rooms and the level keeps a role. After each tree is drawn it
/// picks the role cells among the occupied ones (the down cell at least the
/// minimum number of tree doors from the up cell), and the fill then offers
/// only a role's rooms in its cell and only ordinary rooms elsewhere; a
/// level whose joints bring the two closer than the minimum is refused like
/// a failed fill, and the next tree is drawn. The first sequence's draws are
/// untouched by any of it: a library without role rooms never creates the
/// second sequence, offers the candidates it always offered, and gets the
/// level it always got from a seed.
/// </para>
/// <para>
/// <b>Large rooms</b> (the rooms design, 17.9, one-cell rooms only since the
/// owner dropped multi-cell rooms, D30): a room taller than its cell stands
/// only in a group of adjacent large rooms, and only when
/// <see cref="LevelGeneratorOptions.LargeShare"/> asks for some. The groups
/// come from a <b>third</b> sequence (<see cref="AreaStream"/>), grown after
/// the empty cells and before any tree (<see cref="PlaceLargeGroups"/>); the
/// trees then take a wall touching a large room only where it has a socket,
/// the role cells and the fill take only the cells left, and the fill holds
/// every room it places to the large rooms around it on all four sides.
/// Without a large share nothing of this runs: large rooms are dropped from
/// the candidates before any draw, the third sequence is never created,
/// and every list and draw is what it was before heights existed.
/// </para>
/// <para>
/// This replaces the sample's exhaustive enumerator for drawing levels: the
/// enumerator can only permute a fixed set of rooms on a full 3x3 grid,
/// where this places any of the library's rooms, as often as it likes, on
/// any grid, with empty cells.
/// </para>
/// </remarks>
public static class LevelGenerator
{
    /// <summary>How many spanning trees are tried before the generator gives up.</summary>
    public const int Attempts = 64;

    /// <summary>How many placements one tree's search may try.</summary>
    /// <remarks>
    /// <para>
    /// A fixed number, not one that grows with the grid, and deliberately
    /// so. A search that succeeds places every occupied cell at least once,
    /// so it takes at least one step per cell: a budget below the cell count
    /// could never succeed. The largest grid there is, though, is
    /// <see cref="LevelYaml.MaxCells"/> (4,096) cells, which
    /// <see cref="CheckOptions"/> enforces before any search, and 200,000 is
    /// about 49 steps per cell even there. The relationship is pinned by
    /// <see cref="StepBudgetCoversTheLargestGrid"/>, which stops the build if
    /// <see cref="LevelYaml.MaxCells"/> is ever raised past this budget.
    /// </para>
    /// <para>
    /// Scaling the budget with the cells was the alternative, and was not
    /// taken: any scaled budget that is identical to this one for every grid
    /// up to 4,096 cells differs from it only on grids the generator refuses
    /// anyway, so it would be code no level can reach. And the budget is also
    /// what bounds how long a hopeless tree is searched before the next is
    /// drawn; scaling it silently with a raised cap would change which tree
    /// a seed's level comes from, and so the level, for every grid near the
    /// old cap. Raising <see cref="LevelYaml.MaxCells"/> past 200,000 is a
    /// change to the generator's output, and should be made as one.
    /// </para>
    /// </remarks>
    public const int StepBudget = 200_000;

    /// <summary>
    /// <see cref="StepBudget"/> less <see cref="LevelYaml.MaxCells"/>: a
    /// compile-time proof that the budget covers one step per cell of the
    /// largest grid.
    /// </summary>
    /// <remarks>
    /// A constant of an unsigned type: were the cap ever raised past the
    /// budget, the difference would be negative, and a negative constant
    /// does not convert to <c>uint</c> (CS0221), so the build stops at the
    /// line that explains why, not at a level that quietly never succeeds.
    /// </remarks>
    internal const uint StepBudgetCoversTheLargestGrid = StepBudget - LevelYaml.MaxCells;

    /// <summary>
    /// What the transition sequence's seed is the level's seed exclusive-or'd
    /// with: a fixed constant (the first 64 bits of the golden ratio's
    /// fraction, reversed), so the role cells are a function of the seed and
    /// never share a draw with the main sequence.
    /// </summary>
    public const ulong RoleStream = 0x7F4A7C159E3779B9UL;

    /// <summary>
    /// What the area sequence's seed is the level's seed exclusive-or'd
    /// with: the first 64 bits of the fractional part of √2, so the large
    /// groups are a function of the seed and never share a draw with the
    /// main or the role sequence (the rooms design, 17.9).
    /// </summary>
    /// <remarks>
    /// Its own sequence so that a level with large rooms leaves every draw
    /// of the steps that do not involve them alone: the empty cells come
    /// from the main sequence before the groups are grown, and the role
    /// cells from theirs; and a level without large rooms never creates it.
    /// </remarks>
    public const ulong AreaStream = 0x6A09E667F3BCC908UL;

    private const int East = 1, West = 2, North = 4, South = 8;

    /// <summary>The comment lines a generated level file starts with: how it was made, and how to read the grid.</summary>
    /// <param name="options">What the generator was asked for.</param>
    /// <param name="level">What it made.</param>
    /// <returns>The lines, without their <c>#</c>.</returns>
    public static IReadOnlyList<string> Header(LevelGeneratorOptions options, LevelGrid level)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(level);
        // The large-room settings are named only when they were used, so a
        // level made without them keeps the header it always had.
        string large = options.LargeShare > 0
            ? string.Create(CultureInfo.InvariantCulture, $", large share {options.LargeShare}, groups of {options.GroupSize}")
            : string.Empty;
        string height = options.MaxHeight is float most
            ? string.Create(CultureInfo.InvariantCulture, $", rooms at most {most} tall")
            : string.Empty;
        return
        [
            string.Create(CultureInfo.InvariantCulture,
                $"generated by ssmap layout: seed {options.Seed}, {options.Rows} rows x {options.Columns} columns,"
                + $" {level.Cells.Count(c => c is null)} empty cell(s){large}{height}"),
            "the grid's first line is the north row; each line runs west to east",
        ];
    }

    /// <summary>
    /// Refuses options <see cref="Generate(IReadOnlyList{RoomDefinition}, LevelGeneratorOptions, string, string, LayoutEntityBudget)"/> would refuse for their own
    /// sake, whatever the library: a grid under one row or column or over
    /// <see cref="LevelYaml.MaxCells"/> cells, or an empty share outside
    /// <c>[0, 1)</c>.
    /// </summary>
    /// <remarks>
    /// Public so a host can check what it was asked for before it spends
    /// anything on the library: <c>ssmap layout</c> used to read and split a
    /// 22 MB library (2.5 s, 384 MB) before refusing a 512 x 512 grid that
    /// no library could fill. <see cref="Generate(IReadOnlyList{RoomDefinition}, LevelGeneratorOptions, string, string, LayoutEntityBudget)"/> runs the same check,
    /// with the same exceptions and messages, so a host that skips this call
    /// is refused all the same, only later.
    /// </remarks>
    /// <param name="options">The grid, the seed and the empty share.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The grid has no rows or no columns.</exception>
    /// <exception cref="ArgumentException">The grid has too many cells, or the empty share is out of range.</exception>
    public static void CheckOptions(LevelGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
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

        if (!(options.LargeShare >= 0 && options.LargeShare < 1))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the large share {options.LargeShare} is not in [0, 1)"),
                nameof(options));
        }

        if (options.GroupSize < 1)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"a group of {options.GroupSize} large rooms is not 1 or more"),
                nameof(options));
        }

        if (options.MaxHeight is float most && !(most > 0 && float.IsFinite(most)))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the most height {most} is not a positive number of units"),
                nameof(options));
        }
    }

    /// <summary>Generates a level.</summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="options">The grid, the seed and the empty share.</param>
    /// <param name="name">The level's name.</param>
    /// <param name="library">The library as the level file should name it.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentException">The options are out of range (<see cref="CheckOptions"/>), or there are no rooms.</exception>
    /// <exception cref="LinkException">No valid level could be made from these rooms on this grid.</exception>
    /// <remarks>
    /// <para>
    /// <b>Memory.</b> The per-cell shuffled candidate orders are the
    /// generator's only large allocation: occupied cells × candidates. A
    /// candidate is one <c>int</c>, its room's library index times four plus
    /// its rotation, and its socket mask is looked up from that; it used to
    /// be a (room, rotation, mask) tuple of 12 bytes, which at 64 x 64 cells
    /// and 1,024 rooms (4,096 candidates) was about 200 MB, and is now a
    /// third of that. The orders are allocated once and refilled for each
    /// tree rather than allocated per tree.
    /// </para>
    /// <para>
    /// The draws are untouched: each cell's order starts as the candidates in
    /// library order and is shuffled by the same Fisher–Yates over the same
    /// sequence, so each cell's order names the same (room, rotation) at
    /// every position as before, and every seed gives the level it always
    /// gave (the facts compare against a frozen copy of the old generator).
    /// </para>
    /// </remarks>
    public static LevelGrid Generate(
        IReadOnlyList<RoomDefinition> rooms, LevelGeneratorOptions options, string name, string library) =>
        Generate(rooms, options, name, library, budget: null);

    /// <summary>Generates a level that keeps within an entity budget.</summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="options">The grid, the seed and the empty share.</param>
    /// <param name="name">The level's name.</param>
    /// <param name="library">The library as the level file should name it.</param>
    /// <param name="budget">The entity budget, or null for none.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentException">
    /// As for the overload without a budget; or a budget below 0, or one
    /// whose costs are not one per room, each 0 or more.
    /// </exception>
    /// <exception cref="LinkException">
    /// No valid level within the budget could be made; or none could be,
    /// because even the cheapest rooms pass the budget.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>How.</b> The search skips a candidate whose edicts would take the
    /// level past the budget, as it skips one whose sockets do not fit: the
    /// edicts spent before a cell are the worldspawn's and those of the rooms
    /// already placed, and a candidate is taken only if they and its own
    /// cost stay within the budget. A skipped candidate is a step like any
    /// other, and the draws are not touched.
    /// </para>
    /// <para>
    /// <b>The same levels.</b> A budget that never turns a candidate away
    /// changes nothing: the search visits exactly what it visits without
    /// one, and the level is byte for byte the level of the overload without
    /// a budget. Only a level that would have passed the budget comes out
    /// different, and then it is the first level of the same draws that
    /// keeps within it. The costs are the rooms' stock-mode edicts; the
    /// conditional drops and folds that make a room cheaper in a given
    /// level are not known yet, so this is the worst case.
    /// </para>
    /// </remarks>
    public static LevelGrid Generate(
        IReadOnlyList<RoomDefinition> rooms, LevelGeneratorOptions options, string name, string library, LayoutEntityBudget? budget) =>
        Generate(rooms, options, name, library, budget, transitions: null);

    /// <summary>Generates a level that places a library's transition rooms, within an entity budget.</summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="options">The grid, the seed and the empty share.</param>
    /// <param name="name">The level's name.</param>
    /// <param name="library">The library as the level file should name it.</param>
    /// <param name="budget">The entity budget, or null for none.</param>
    /// <param name="transitions">The rooms' roles and the level's role settings, or null for a library without roles.</param>
    /// <returns>The level. Its <see cref="LevelGrid.Transitions"/> is not set: the maps above and below are the caller's.</returns>
    /// <exception cref="ArgumentException">As for the overload without transitions; or roles that are not one per room.</exception>
    /// <exception cref="LinkException">
    /// As for the overload without transitions; or the library has no room
    /// of a role the level keeps; or no level of the grid and seed places
    /// the up and down rooms the minimum distance apart; or a large share is
    /// asked for and no room is large, or no pass of the area sequence
    /// covers the share with groups; or every room with a socket is too tall
    /// for the fill (large, or past <see cref="LevelGeneratorOptions.MaxHeight"/>).
    /// </exception>
    public static LevelGrid Generate(
        IReadOnlyList<RoomDefinition> rooms,
        LevelGeneratorOptions options,
        string name,
        string library,
        LayoutEntityBudget? budget,
        LayoutTransitions? transitions)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(library);
        CheckOptions(options);

        if (rooms.Count == 0)
        {
            throw new ArgumentException("a level needs at least one room to place", nameof(rooms));
        }

        if (budget is not null
            && (budget.Budget < 0 || budget.LevelEdicts < 0 || budget.RoomEdicts is null || budget.RoomEdicts.Count != rooms.Count || budget.RoomEdicts.Any(c => c < 0)))
        {
            throw new ArgumentException(
                $"an entity budget is 0 or more, with level edicts of 0 or more and one cost of 0 or more for each of the {rooms.Count} room(s)", nameof(budget));
        }

        int rows = options.Rows;
        int columns = options.Columns;
        int cellCount = rows * columns;
        SplitMix64 random = new(options.Seed);

        bool[] occupied = EmptyCells(random, rows, columns, (int)Math.Floor(options.EmptyRatio * cellCount));
        int placed = occupied.Count(o => o);

        // Every (room, rotation), as room * 4 + rotation, and the world walls
        // it has sockets on, by that index. A room with no socket can only
        // stand alone. A room taller than -max-height is dropped outright;
        // a large room (taller than its cell) is never offered to the fill:
        // it stands only in a group, which only a large share asks for, and
        // never in a role cell (role rooms are the fill's). Dropping them
        // here, before any draw, is what keeps a library that gains tall
        // rooms generating the levels it generated without them.
        int[] maskOf = new int[checked(rooms.Count * 4)];
        List<int> candidateList = [];
        List<int> largeList = [];
        int droppedForHeight = 0;
        for (int r = 0; r < rooms.Count; r++)
        {
            if (rooms[r].Sockets.Count == 0 && placed > 1)
            {
                continue;
            }

            bool tooTall = options.MaxHeight is float most && rooms[r].Height > most;
            bool isLarge = IsLarge(rooms[r]);
            bool grouped = isLarge && !tooTall && options.LargeShare > 0 && RoleOf(transitions, rooms.Count, r) == RoomRole.None;
            if (tooTall || (isLarge && !grouped))
            {
                droppedForHeight++;
                continue;
            }

            for (int rotation = 0; rotation < 4; rotation++)
            {
                int candidate = (r * 4) + rotation;
                maskOf[candidate] = Mask(rooms[r], rotation);
                (grouped ? largeList : candidateList).Add(candidate);
            }
        }

        if (options.LargeShare > 0 && !rooms.Any(r => IsLarge(r) && !(options.MaxHeight is float most && r.Height > most)))
        {
            throw new LinkException(options.MaxHeight is float most
                ? string.Create(CultureInfo.InvariantCulture,
                    $"layout: -large asks for large areas, but no library of the level has a room taller than one cell and at most {most} tall (-max-height).")
                : "layout: -large asks for large areas, but no library of the level has a room taller than one cell.");
        }

        if (candidateList.Count == 0)
        {
            string limit = options.MaxHeight is float most
                ? string.Create(CultureInfo.InvariantCulture, $" and {most} (-max-height)")
                : string.Empty;
            throw new LinkException(droppedForHeight == 0
                ? $"none of the library's {rooms.Count} room(s) has a socket, so {placed} rooms cannot be joined."
                : $"none of the library's {rooms.Count} room(s) has a socket and stands no taller than its cell{limit},"
                    + $" so {placed} rooms cannot be joined; a room taller than its cell stands only in a group of large rooms (-large).");
        }

        int[] candidates = [.. candidateList];
        int[] large = [.. largeList];

        // The roles: which candidates are role rooms, and which cells must
        // hold one. Null for a library without roles, whose fill is exactly
        // what it always was.
        int[]? roleOf = null;
        int[]? cellRole = null;
        SplitMix64? roleDraws = null;
        bool wantUp = false, wantDown = false;
        int minDistance = 0;
        if (transitions is not null)
        {
            if (transitions.Roles is null || transitions.Roles.Count != rooms.Count || transitions.MinDistance < 0)
            {
                throw new ArgumentException(
                    $"the transitions give one role for each of the {rooms.Count} room(s) and a distance of 0 or more", nameof(transitions));
            }

            if (transitions.Roles.Any(r => r != RoomRole.None))
            {
                roleOf = new int[maskOf.Length];
                foreach (int candidate in candidates)
                {
                    roleOf[candidate] = (int)transitions.Roles[candidate / 4];
                }

                cellRole = new int[cellCount];
                wantUp = !transitions.NoUp;
                wantDown = !transitions.NoDown;
                foreach ((bool wanted, RoomRole role) in (ReadOnlySpan<(bool, RoomRole)>)[(wantUp, RoomRole.Up), (wantDown, RoomRole.Down)])
                {
                    if (wanted && !candidates.Any(c => roleOf[c] == (int)role))
                    {
                        string spelled = role == RoomRole.Up ? "up" : "down";
                        throw new LinkException(
                            $"the library has no {spelled} room with a socket, so a level of {placed} rooms cannot hold one; switch the role off ({spelled}: none) or add one.");
                    }
                }

                if (wantUp && wantDown && placed < 2)
                {
                    throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                        $"a level of {placed} room(s) cannot hold both an up and a down room; they stand in different cells."));
                }

                minDistance = wantUp && wantDown ? transitions.MinDistance : 0;
                roleDraws = wantUp || wantDown ? new SplitMix64(options.Seed ^ RoleStream) : null;
            }
        }

        bool distanceFailed = false;

        // The large groups, from their own sequence, before the tree: the
        // cells they take are fixed for every tree drawn after. Null when no
        // large share is asked for, and then nothing below differs from a
        // generator that never heard of large rooms.
        int[]? largeAt = null;
        bool[] fillable = occupied;
        int covered = 0;
        if (options.LargeShare > 0)
        {
            // A role room is a standard room, so the groups leave a cell for
            // each role the level keeps; the share can never take every
            // cell anyway (it is below 1 and rounded down).
            int roles = (wantUp ? 1 : 0) + (wantDown ? 1 : 0);
            int target = Math.Max(0, Math.Min((int)Math.Floor(options.LargeShare * placed), placed - roles));
            largeAt = PlaceLargeGroups(
                new SplitMix64(options.Seed ^ AreaStream), rooms, large, maskOf, occupied, rows, columns, target, options.GroupSize)
                ?? throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                    $"layout: no level of {rows}x{columns} with seed {options.Seed} covers {target} cells with large rooms"
                    + $" in groups of {options.GroupSize}; lower -large or grow the grid."));
            fillable = new bool[cellCount];
            for (int cell = 0; cell < cellCount; cell++)
            {
                fillable[cell] = occupied[cell] && largeAt[cell] < 0;
                covered += largeAt[cell] >= 0 ? 1 : 0;
            }
        }

        // Each candidate's edicts, by candidate, when there is a budget; and
        // the refusal of a level whose cheapest rooms already pass it, which
        // no search could make.
        int[]? costOf = null;
        long largeEdicts = 0;
        if (budget is not null)
        {
            int[] costs = new int[maskOf.Length];
            foreach (int candidate in candidates.Concat(large))
            {
                costs[candidate] = budget.RoomEdicts[candidate / 4];
            }

            costOf = costs;
            if (largeAt is not null)
            {
                foreach (int candidate in largeAt)
                {
                    largeEdicts += candidate >= 0 ? costs[candidate] : 0;
                }
            }

            long least = 1 + (long)budget.LevelEdicts + largeEdicts + ((long)(placed - covered) * candidates.Min(c => costs[c]));
            if (least > budget.Budget)
            {
                string included = budget.LevelEdicts == 0 ? "the worldspawn included" : "the worldspawn and the library's own entities included";
                throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                    $"no level of {rows}x{columns} cells keeps within the entity budget of {budget.Budget} edicts:"
                    + $" its {placed} room(s) bring at least {least}, {included}."));
            }
        }

        // One order per cell the fill fills, reused by every tree.
        int[]?[] order = new int[]?[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            if (fillable[cell])
            {
                order[cell] = new int[candidates.Length];
            }
        }

        int[] chosen = new int[cellCount];
        int[] masks = new int[cellCount];
        int[] next = new int[cellCount];
        long[]? spent = costOf is null ? null : new long[cellCount];

        // A placed large room is a neighbour the fill must agree with on
        // every side, the ones after it in cell order included.
        bool[]? fixedCells = null;
        if (largeAt is not null)
        {
            fixedCells = new bool[cellCount];
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (largeAt[cell] >= 0)
                {
                    fixedCells[cell] = true;
                    masks[cell] = maskOf[largeAt[cell]];
                }
            }
        }

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            int[] required = SpanningTree(random, rows, columns, occupied, largeAt, maskOf);
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (order[cell] is int[] tries)
                {
                    candidates.CopyTo(tries, 0);
                    random.Shuffle(tries);
                }
            }

            bool rolesPlaced = true;
            if (roleDraws is not null)
            {
                rolesPlaced = PickRoleCells(roleDraws, fillable, required, columns, wantUp, wantDown, minDistance, cellRole!);
                distanceFailed |= !rolesPlaced;
            }

            bool filled = rolesPlaced
                && Fill(fillable, order, required, maskOf, columns, chosen, masks, next, costOf, (budget?.Budget ?? 0) - (budget?.LevelEdicts ?? 0), spent, roleOf, cellRole, fixedCells, 1 + largeEdicts);

            // The tree's doors are a bound, not the level's: the fill may
            // join walls the tree left out, so the distance is measured again
            // on the level's own joints.
            if (filled && minDistance > 0 && RoleDistance(occupied, masks, rows, columns, cellRole!) < minDistance)
            {
                distanceFailed = true;
                filled = false;
            }

            if (filled)
            {
                LevelCell?[] cells = new LevelCell?[cellCount];
                for (int cell = 0; cell < cellCount; cell++)
                {
                    if (occupied[cell])
                    {
                        int candidate = fillable[cell] ? order[cell]![chosen[cell]] : largeAt![cell];
                        cells[cell] = new LevelCell(rooms[candidate / 4].Name, candidate % 4);
                    }
                }

                return new LevelGrid(name, library, rows, columns, cells);
            }
        }

        if (distanceFailed && minDistance > 0)
        {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"layout: no level of {rows}x{columns} with seed {options.Seed} places the up and down rooms at least {minDistance} doors apart."));
        }

        throw new LinkException(budget is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"no level of {rows}x{columns} cells with every room reachable was found from the library's"
                + $" {rooms.Count} room(s) with seed {options.Seed} after {Attempts} tries; the rooms' sockets may not allow one.")
            : string.Create(CultureInfo.InvariantCulture,
                $"no level of {rows}x{columns} cells with every room reachable and at most {budget.Budget} edicts was found"
                + $" from the library's {rooms.Count} room(s) with seed {options.Seed} after {Attempts} tries;"
                + $" the rooms' sockets or the entity budget may not allow one."));
    }

    /// <summary>
    /// The backtracking search for one tree: fills the occupied cells in cell
    /// order, each with the first candidate of its order that fits, backing
    /// up to the previous occupied cell's next candidate when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Iterative, with <paramref name="next"/> holding each cell's position in
    /// its order, where it used to recurse one level per cell: at the
    /// 4,096-cell cap that was a stack 4,096 frames deep, and a raised cap
    /// would have overflowed the thread's stack, which no host can catch.
    /// </para>
    /// <para>
    /// It visits exactly what the recursion visited, in the same order, and
    /// counts steps the same way: one per candidate tried, fitting or not,
    /// on any cell. The recursion gave up when a cell's try pushed the count
    /// past <see cref="StepBudget"/>: that level returned failure, and every
    /// level above it, on its own next try, found the count still past the
    /// budget and returned failure too, so the whole search failed at the
    /// first step past the budget. Returning at that step is the same
    /// outcome, and the steps the unwinding recursion still counted were
    /// never read.
    /// </para>
    /// </remarks>
    /// <para>
    /// With an entity budget (<paramref name="costOf"/>), a candidate must
    /// also keep the edicts spent so far within <paramref name="budget"/>:
    /// <paramref name="spent"/> holds, per cell, the edicts spent before it
    /// (the worldspawn's 1 and the cells placed before it), set when the
    /// search reaches the cell, and still right when it backs up to it,
    /// since only the cells after it have changed since.
    /// </para>
    /// <para>
    /// With large rooms placed, <paramref name="occupied"/> is the cells the
    /// fill fills (the occupied ones no large room stands in),
    /// <paramref name="fixedCells"/> marks the large rooms' cells, whose
    /// sockets <paramref name="masks"/> already holds, and
    /// <paramref name="firstSpent"/> is the worldspawn's edict plus the large
    /// rooms' own, which every level of this tree spends before the first
    /// cell is filled.
    /// </para>
    /// <returns>Whether every occupied cell was filled; <paramref name="chosen"/> then holds each one's position in its order.</returns>
    internal static bool Fill(
        bool[] occupied,
        int[]?[] order,
        int[] required,
        int[] maskOf,
        int columns,
        int[] chosen,
        int[] masks,
        int[] next,
        int[]? costOf = null,
        long budget = 0,
        long[]? spent = null,
        int[]? roleOf = null,
        int[]? cellRole = null,
        bool[]? fixedCells = null,
        long firstSpent = 1)
    {
        int cellCount = occupied.Length;
        int steps = 0;
        int cell = NextOccupied(occupied, 0);
        if (cell == cellCount)
        {
            return true;
        }

        next[cell] = 0;
        if (spent is not null)
        {
            spent[cell] = firstSpent;
        }

        while (true)
        {
            int[] tries = order[cell]!;
            bool placed = false;
            for (int i = next[cell]; i < tries.Length; i++)
            {
                if (++steps > StepBudget)
                {
                    return false;
                }

                int mask = maskOf[tries[i]];
                if (Fits(occupied, required, masks, columns, cell, mask, fixedCells)
                    && (costOf is null || spent![cell] + costOf[tries[i]] <= budget)
                    && (roleOf is null || roleOf[tries[i]] == cellRole![cell]))
                {
                    chosen[cell] = i;
                    masks[cell] = mask;
                    next[cell] = i + 1;
                    placed = true;
                    break;
                }
            }

            if (placed)
            {
                int from = cell;
                cell = NextOccupied(occupied, cell + 1);
                if (cell == cellCount)
                {
                    return true;
                }

                next[cell] = 0;
                if (spent is not null)
                {
                    spent[cell] = spent[from] + costOf![order[from]![chosen[from]]];
                }

                continue;
            }

            // Every candidate of this cell failed: back up to the previous
            // occupied cell, which resumes after the candidate it had.
            do
            {
                cell--;
            }
            while (cell >= 0 && !occupied[cell]);

            if (cell < 0)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Picks the role cells for one tree from the transition sequence: the
    /// occupied cells in one shuffled order, the up cell the first of them
    /// and the down cell the first other one at least
    /// <paramref name="minDistance"/> tree doors from it, trying the next up
    /// cell when none is; one draw per tree whatever the grid.
    /// </summary>
    /// <returns>Whether the cells were picked; false when no pair is far enough apart on this tree.</returns>
    internal static bool PickRoleCells(
        SplitMix64 draws, bool[] occupied, int[] required, int columns, bool wantUp, bool wantDown, int minDistance, int[] cellRole)
    {
        Array.Clear(cellRole);
        List<int> cells = [.. Enumerable.Range(0, occupied.Length).Where(c => occupied[c])];
        draws.Shuffle(cells);
        if (!(wantUp && wantDown))
        {
            cellRole[cells[0]] = (int)(wantUp ? RoomRole.Up : RoomRole.Down);
            return true;
        }

        foreach (int up in cells)
        {
            int[] doors = minDistance > 0 ? TreeDistances(required, columns, occupied.Length, up) : [];
            foreach (int down in cells)
            {
                if (down != up && (minDistance == 0 || doors[down] >= minDistance))
                {
                    cellRole[up] = (int)RoomRole.Up;
                    cellRole[down] = (int)RoomRole.Down;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Every cell's distance in doors from one cell along the tree's walls; -1 where the tree does not reach.</summary>
    private static int[] TreeDistances(int[] required, int columns, int cellCount, int from)
    {
        int[] distance = new int[cellCount];
        Array.Fill(distance, -1);
        distance[from] = 0;
        Queue<int> queue = new([from]);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach ((int bit, int step) in (ReadOnlySpan<(int, int)>)[(East, 1), (West, -1), (North, columns), (South, -columns)])
            {
                int there = at + step;
                if ((required[at] & bit) != 0 && distance[there] < 0)
                {
                    distance[there] = distance[at] + 1;
                    queue.Enqueue(there);
                }
            }
        }

        return distance;
    }

    /// <summary>
    /// The doors between the filled level's up and down cells: the shortest
    /// path through its joints, a joint being a shared wall with a socket on
    /// both sides (what <see cref="LevelGrid.ToLayout"/> joins).
    /// </summary>
    internal static int RoleDistance(bool[] occupied, int[] masks, int rows, int columns, int[] cellRole)
    {
        int up = Array.IndexOf(cellRole, (int)RoomRole.Up), down = Array.IndexOf(cellRole, (int)RoomRole.Down);
        int[] distance = new int[occupied.Length];
        Array.Fill(distance, -1);
        distance[up] = 0;
        Queue<int> queue = new([up]);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            int x = at % columns, y = at / columns;
            foreach ((int bit, int back, int dx, int dy) in (ReadOnlySpan<(int, int, int, int)>)[(East, West, 1, 0), (West, East, -1, 0), (North, South, 0, 1), (South, North, 0, -1)])
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= columns || ny >= rows)
                {
                    continue;
                }

                int there = (ny * columns) + nx;
                if (occupied[there] && distance[there] < 0 && (masks[at] & bit) != 0 && (masks[there] & back) != 0)
                {
                    distance[there] = distance[at] + 1;
                    queue.Enqueue(there);
                }
            }
        }

        return distance[down];
    }

    /// <summary>
    /// Generates a run of <paramref name="count"/> levels from consecutive
    /// seeds, chained through their map names (<c>ssmap layout -sequence</c>,
    /// the rooms design, 11.2).
    /// </summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="options">The grid, the first level's seed (level <i>i</i> takes seed + <i>i</i> − 1) and the empty share.</param>
    /// <param name="count">How many levels, 1 or more.</param>
    /// <param name="baseName">The levels' base name: level <i>i</i> is <c>&lt;base&gt;_&lt;i&gt;</c>, <i>i</i> padded to two digits or more.</param>
    /// <param name="library">The library as each level file should name it.</param>
    /// <param name="budget">The entity budget, or null for none.</param>
    /// <param name="roles">Each room's role, in library order.</param>
    /// <param name="minDistance">The fewest doors between a level's up and down rooms; 0 for no minimum.</param>
    /// <returns>
    /// The levels, top first: level <i>i</i>'s <c>down_map</c> is level
    /// <i>i</i> + 1's name and its <c>up_map</c> level <i>i</i> − 1's; the
    /// first says <c>up: none</c> and the last <c>down: none</c>.
    /// </returns>
    /// <exception cref="ArgumentException">As for <see cref="Generate(IReadOnlyList{RoomDefinition}, LevelGeneratorOptions, string, string, LayoutEntityBudget?, LayoutTransitions?)"/>, or a count below 1.</exception>
    /// <exception cref="LinkException">As for that overload, for the first level that cannot be made.</exception>
    public static IReadOnlyList<LevelGrid> GenerateSequence(
        IReadOnlyList<RoomDefinition> rooms,
        LevelGeneratorOptions options,
        int count,
        string baseName,
        string library,
        LayoutEntityBudget? budget,
        IReadOnlyList<RoomRole> roles,
        int minDistance = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        string[] names = [.. Enumerable.Range(1, count).Select(i => SequenceName(baseName, i, count))];
        List<LevelGrid> levels = new(count);
        for (int i = 0; i < count; i++)
        {
            LayoutTransitions transitions = new(roles) { NoUp = i == 0, NoDown = i == count - 1, MinDistance = minDistance };
            LevelGrid level = Generate(rooms, options with { Seed = unchecked(options.Seed + (ulong)i) }, names[i], library, budget, transitions);
            levels.Add(level.WithTransitions(new LevelTransitions
            {
                NoUp = i == 0,
                NoDown = i == count - 1,
                UpMap = i > 0 ? names[i - 1] : null,
                DownMap = i < count - 1 ? names[i + 1] : null,
            }));
        }

        return levels;
    }

    /// <summary>A sequence level's name: the base, an underscore and its 1-based number, at least two digits, padded to the widest.</summary>
    /// <param name="baseName">The base name.</param>
    /// <param name="index">The level's number, from 1.</param>
    /// <param name="count">How many levels the sequence has.</param>
    /// <returns>The name, such as <c>run_01</c>.</returns>
    public static string SequenceName(string baseName, int index, int count)
    {
        int width = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        return baseName + "_" + index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    /// <summary>Whether a room is large: taller than its cell. A room as tall as its cell, or lower (a low hallway), is standard.</summary>
    /// <param name="room">The room.</param>
    /// <returns>True for a large room.</returns>
    public static bool IsLarge(RoomDefinition room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.Height > room.CellSize;
    }

    /// <summary>A room's role, or none when the level has no transitions (or gives roles that do not match, which the caller refuses).</summary>
    private static RoomRole RoleOf(LayoutTransitions? transitions, int roomCount, int room) =>
        transitions?.Roles is { } roles && roles.Count == roomCount ? roles[room] : RoomRole.None;

    /// <summary>
    /// Whether a wall between two occupied cells can be a doorway given the
    /// large rooms already placed: a large room on either side must have a
    /// socket on it. Every wall is eligible when no large room is placed.
    /// </summary>
    private static bool Eligible(int[]? largeAt, int[]? maskOf, int a, int b, int bitOfA, int bitOfB) =>
        largeAt is null
        || ((largeAt[a] < 0 || (maskOf![largeAt[a]] & bitOfA) != 0) && (largeAt[b] < 0 || (maskOf![largeAt[b]] & bitOfB) != 0));

    /// <summary>
    /// Places the large groups (the rooms design, 17.9, step 2) from the area
    /// sequence: groups of adjacent large rooms until they cover
    /// <paramref name="target"/> cells, restarting with the sequence running
    /// on when a pass falls short, up to <see cref="Attempts"/> passes.
    /// </summary>
    /// <param name="area">The area sequence.</param>
    /// <param name="rooms">The rooms, in library order.</param>
    /// <param name="large">The large candidates, as room × 4 + rotation, in library order.</param>
    /// <param name="maskOf">Each candidate's world socket mask.</param>
    /// <param name="occupied">Which cells hold a room.</param>
    /// <param name="rows">The grid's rows.</param>
    /// <param name="columns">The grid's columns.</param>
    /// <param name="target">How many cells the groups cover, below the occupied count.</param>
    /// <param name="groupSize">The most rooms in one group.</param>
    /// <returns>Per cell, the large candidate standing in it or -1; null when no pass reached the target.</returns>
    /// <remarks>
    /// <para>
    /// <b>A group</b> starts at the next cell of a shuffled list of the
    /// occupied cells that is free and shares no wall with a group already
    /// placed, with the first large candidate of a shuffled order (it touches
    /// no large room, so any fits). It grows one room at a time, to at most
    /// <paramref name="groupSize"/> rooms: the free cells sharing a wall with
    /// the group and none with another group, in cell order, shuffled; for
    /// each in turn the large candidates, shuffled and then stably sorted by
    /// how many whole cells their height is from the group's first room's
    /// (so ties stay in shuffled order), the first that agrees with every
    /// large room it touches (a socket on both sides of a shared wall or on
    /// neither) and has a socket meeting one of the group's. That is the
    /// height-aware grouping: tall halls gather with halls of their own
    /// height, and a group is joined inside by construction.
    /// </para>
    /// <para>
    /// <b>Kept apart and reached.</b> No two groups share a wall, and a
    /// group is kept only if every occupied cell can still reach every other
    /// through walls that could be doorways (<see cref="Joined"/>): a wall
    /// touching a large room only where it has a socket. That is 17.9's rule
    /// that each group has a socket facing a free cell, strengthened: a
    /// group that walls off part of the grid is dropped as well, since no
    /// tree could then span the level. Some cell is always free (the target
    /// is below the occupied count), so every group is reached through
    /// standard rooms. A group that fails is undone and the pass moves on to
    /// the next start cell.
    /// </para>
    /// <para>
    /// Every draw is from <paramref name="area"/>, in an order fixed by the
    /// grid and the candidates alone, so the groups are a function of the
    /// seed, the rooms and the options, whatever thread runs it.
    /// </para>
    /// </remarks>
    internal static int[]? PlaceLargeGroups(
        SplitMix64 area,
        IReadOnlyList<RoomDefinition> rooms,
        int[] large,
        int[] maskOf,
        bool[] occupied,
        int rows,
        int columns,
        int target,
        int groupSize)
    {
        int cellCount = occupied.Length;
        int[] largeAt = new int[cellCount];
        int[] groupOf = new int[cellCount];
        Array.Fill(largeAt, -1);
        if (target <= 0)
        {
            return largeAt;
        }

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            Array.Fill(largeAt, -1);
            Array.Fill(groupOf, -1);
            int covered = 0, groups = 0;
            List<int> starts = [.. Enumerable.Range(0, cellCount).Where(c => occupied[c])];
            area.Shuffle(starts);
            foreach (int start in starts)
            {
                if (covered >= target)
                {
                    break;
                }

                if (largeAt[start] >= 0 || !Apart(groupOf, rows, columns, start, groups))
                {
                    continue;
                }

                int[] tries = [.. large];
                area.Shuffle(tries);
                int first = -1;
                foreach (int candidate in tries)
                {
                    if (Agrees(largeAt, maskOf, rows, columns, start, maskOf[candidate]))
                    {
                        first = candidate;
                        break;
                    }
                }

                if (first < 0)
                {
                    continue;
                }

                List<int> members = [start];
                largeAt[start] = first;
                groupOf[start] = groups;
                covered++;
                float firstHeight = rooms[first / 4].Height, cell = rooms[first / 4].CellSize;
                while (members.Count < groupSize && covered < target)
                {
                    List<int> frontier = [];
                    foreach (int member in members)
                    {
                        foreach (int there in Neighbours(rows, columns, member))
                        {
                            if (occupied[there] && largeAt[there] < 0 && !frontier.Contains(there) && Apart(groupOf, rows, columns, there, groups))
                            {
                                frontier.Add(there);
                            }
                        }
                    }

                    frontier.Sort();
                    area.Shuffle(frontier);
                    bool grown = false;
                    foreach (int there in frontier)
                    {
                        int[] order = [.. large];
                        area.Shuffle(order);
                        foreach (int candidate in order.OrderBy(c => HeightSteps(rooms[c / 4].Height, firstHeight, cell)))
                        {
                            int mask = maskOf[candidate];
                            if (Agrees(largeAt, maskOf, rows, columns, there, mask) && Meets(largeAt, groupOf, maskOf, rows, columns, there, mask, groups))
                            {
                                largeAt[there] = candidate;
                                groupOf[there] = groups;
                                members.Add(there);
                                covered++;
                                grown = true;
                                break;
                            }
                        }

                        if (grown)
                        {
                            break;
                        }
                    }

                    if (!grown)
                    {
                        break;
                    }
                }

                if (!Joined(occupied, largeAt, maskOf, rows, columns))
                {
                    foreach (int member in members)
                    {
                        largeAt[member] = -1;
                        groupOf[member] = -1;
                    }

                    covered -= members.Count;
                    continue;
                }

                groups++;
            }

            if (covered >= target)
            {
                return largeAt;
            }
        }

        return null;
    }

    /// <summary>How many whole cells of height lie between a room's height and the group's first room's: the grouping's sort key.</summary>
    internal static int HeightSteps(float height, float firstHeight, float cellSize) =>
        (int)Math.Floor(Math.Abs(height - firstHeight) / cellSize);

    /// <summary>A cell's neighbours on the grid, east, west, north, south.</summary>
    private static IEnumerable<int> Neighbours(int rows, int columns, int cell)
    {
        int x = cell % columns, y = cell / columns;
        if (x + 1 < columns)
        {
            yield return cell + 1;
        }

        if (x > 0)
        {
            yield return cell - 1;
        }

        if (y + 1 < rows)
        {
            yield return cell + columns;
        }

        if (y > 0)
        {
            yield return cell - columns;
        }
    }

    /// <summary>The wall bit from a cell towards one of its neighbours, and back.</summary>
    private static (int Bit, int Back) Towards(int columns, int cell, int there) =>
        (there - cell) switch
        {
            1 => (East, West),
            -1 => (West, East),
            _ when there > cell => (North, South),
            _ => (South, North),
        };

    /// <summary>Whether a cell shares a wall with no group but <paramref name="group"/> (every placed group, for a new group's number).</summary>
    private static bool Apart(int[] groupOf, int rows, int columns, int cell, int group)
    {
        foreach (int there in Neighbours(rows, columns, cell))
        {
            if (groupOf[there] >= 0 && groupOf[there] != group)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a mask at a cell agrees with every large room it touches: a socket on both sides of a shared wall, or on neither.</summary>
    private static bool Agrees(int[] largeAt, int[] maskOf, int rows, int columns, int cell, int mask)
    {
        foreach (int there in Neighbours(rows, columns, cell))
        {
            if (largeAt[there] >= 0)
            {
                (int bit, int back) = Towards(columns, cell, there);
                if (((mask & bit) != 0) != ((maskOf[largeAt[there]] & back) != 0))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a mask at a cell has a socket meeting one of the group's rooms across a shared wall.</summary>
    private static bool Meets(int[] largeAt, int[] groupOf, int[] maskOf, int rows, int columns, int cell, int mask, int group)
    {
        foreach (int there in Neighbours(rows, columns, cell))
        {
            if (groupOf[there] == group)
            {
                (int bit, int back) = Towards(columns, cell, there);
                if ((mask & bit) != 0 && (maskOf[largeAt[there]] & back) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether every occupied cell reaches every other through walls that
    /// could be doorways (<see cref="Eligible"/>): the condition for a
    /// spanning tree of the level to exist once the large rooms are placed.
    /// </summary>
    internal static bool Joined(bool[] occupied, int[] largeAt, int[] maskOf, int rows, int columns)
    {
        int first = Array.IndexOf(occupied, true);
        if (first < 0)
        {
            return true;
        }

        bool[] seen = new bool[occupied.Length];
        seen[first] = true;
        int reached = 1;
        Queue<int> queue = new([first]);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (int there in Neighbours(rows, columns, at))
            {
                (int bit, int back) = Towards(columns, at, there);
                if (occupied[there] && !seen[there] && Eligible(largeAt, maskOf, at, there, bit, back))
                {
                    seen[there] = true;
                    reached++;
                    queue.Enqueue(there);
                }
            }
        }

        return reached == occupied.Count(o => o);
    }

    /// <summary>The first occupied cell at or after <paramref name="cell"/>, or the cell count when there is none.</summary>
    private static int NextOccupied(bool[] occupied, int cell)
    {
        while (cell < occupied.Length && !occupied[cell])
        {
            cell++;
        }

        return cell;
    }

    /// <summary>
    /// Whether a candidate's socket mask fits a cell: every tree wall has a
    /// socket, and every wall shared with an already-placed neighbour has a
    /// socket exactly when the neighbour does.
    /// </summary>
    /// <remarks>
    /// The placed neighbours are the west and south ones, which come before
    /// the cell in cell order; the east and north neighbours are not placed
    /// yet, and a wall onto them only has to honour the tree. A wall on the
    /// grid's edge or onto an empty cell is free. The tree walls are the
    /// cell's <paramref name="required"/> bits, which only ever name walls
    /// between two occupied cells.
    /// <para>
    /// With large rooms placed (<paramref name="fixedCells"/>), a cell they
    /// stand in is a neighbour already placed on whichever side it is: the
    /// fill never fills it, and <paramref name="masks"/> holds its sockets
    /// from the start, so its east and north neighbours are held to it too.
    /// </para>
    /// </remarks>
    private static bool Fits(bool[] occupied, int[] required, int[] masks, int columns, int cell, int mask, bool[]? fixedCells = null)
    {
        if ((mask & required[cell]) != required[cell])
        {
            return false;
        }

        int x = cell % columns;
        if (x > 0 && (occupied[cell - 1] || (fixedCells?[cell - 1] ?? false)) && ((mask & West) != 0) != ((masks[cell - 1] & East) != 0))
        {
            return false;
        }

        if (fixedCells is not null)
        {
            if (x + 1 < columns && fixedCells[cell + 1] && ((mask & East) != 0) != ((masks[cell + 1] & West) != 0))
            {
                return false;
            }

            int north = cell + columns;
            if (north < occupied.Length && fixedCells[north] && ((mask & North) != 0) != ((masks[north] & South) != 0))
            {
                return false;
            }
        }

        int south = cell - columns;
        return south < 0 || !(occupied[south] || (fixedCells?[south] ?? false)) || ((mask & South) != 0) == ((masks[south] & North) != 0);
    }

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
    /// <remarks>
    /// <para>
    /// The cells are tried in one shuffled order, pass after pass, each
    /// emptied unless that would split the rest, until enough are empty.
    /// Whether a removal splits the rest used to be a flood fill of the whole
    /// grid, once per cell tried, pass after pass: on a one-row grid, where
    /// only the ends can go, that was thousands of passes of thousands of
    /// fills, over a minute and a half for a 4,096-cell row half emptied.
    /// </para>
    /// <para>
    /// It is now <see cref="OccupiedCells.CanEmpty"/>, which gives the same
    /// answer from the cell's neighbourhood or from the cut cells, recomputed
    /// only after a cell is actually emptied. The answer is a function of the
    /// occupied cells alone, so the cells emptied, and the order they are
    /// emptied in, are exactly what they were.
    /// </para>
    /// </remarks>
    internal static bool[] EmptyCells(SplitMix64 random, int rows, int columns, int empty)
    {
        int count = rows * columns;
        OccupiedCells cells = new(rows, columns);
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

                if (!cells.Occupied[cell])
                {
                    continue;
                }

                if (cells.CanEmpty(cell))
                {
                    cells.Empty(cell);
                    removed++;
                    progress = true;
                }
            }
        }

        return cells.Occupied;
    }

    /// <summary>
    /// A connected set of occupied grid cells that can say, cheaply, whether
    /// one of them can be emptied without splitting the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the neighbours decide.</b> Every other cell has a path to the
    /// cell being emptied; cut that path where it first reaches the cell and
    /// it ends at one of the cell's occupied neighbours without passing
    /// through it. So every group left holds one of those neighbours, and the
    /// rest stay one group exactly when the neighbours do. With no occupied
    /// neighbour the cell is the only one and nothing would remain, which is
    /// not a group (the old whole-grid flood fill said the same of an empty
    /// grid); with one, the cell is a leaf and the rest is untouched.
    /// </para>
    /// <para>
    /// <b>The quick test.</b> Two neighbours on adjacent sides (north and
    /// east, say) are joined if the diagonal cell between them is occupied:
    /// north, north-east, east is a path of shared walls. When those corner
    /// links join every occupied neighbour, the answer is yes without looking
    /// further. That is the usual case while the grid is still mostly full.
    /// </para>
    /// <para>
    /// <b>The cut cells.</b> Otherwise the answer is whether the cell is a
    /// cut cell (an articulation point) of the occupied cells: one whose
    /// removal splits them. All of them are found in one depth-first walk
    /// (Tarjan's low-link rule), which is kept until a cell is emptied. A
    /// cell that cannot be emptied changes nothing, so a whole pass of cells
    /// refused costs one walk, not one per cell; the walk is iterative, so a
    /// long thin set of cells cannot overflow the stack.
    /// </para>
    /// </remarks>
    internal sealed class OccupiedCells
    {
        private readonly int _rows, _columns;
        private readonly int[] _discovered, _low, _parent, _stackCell, _stackSide;
        private readonly bool[] _cut;
        private bool _cutsCurrent;

        /// <summary>Every cell of a grid occupied.</summary>
        public OccupiedCells(int rows, int columns)
        {
            _rows = rows;
            _columns = columns;
            int count = rows * columns;
            Occupied = new bool[count];
            Array.Fill(Occupied, true);
            _discovered = new int[count];
            _low = new int[count];
            _parent = new int[count];
            _stackCell = new int[count];
            _stackSide = new int[count];
            _cut = new bool[count];
        }

        /// <summary>Which cells are occupied, by cell index. Change it only through <see cref="Empty"/>.</summary>
        public bool[] Occupied { get; }

        /// <summary>How many times the cut cells were found, for the facts that show they are kept between removals.</summary>
        public int CutWalks { get; private set; }

        /// <summary>Whether an occupied cell can be emptied and leave the other occupied cells one group.</summary>
        public bool CanEmpty(int cell)
        {
            int x = cell % _columns, y = cell / _columns;
            Span<bool> present = stackalloc bool[4];
            int presentCount = 0;
            for (int side = 0; side < 4; side++)
            {
                present[side] = IsOccupied(x + Dx(side), y + Dy(side));
                presentCount += present[side] ? 1 : 0;
            }

            if (presentCount <= 1)
            {
                return presentCount == 1;
            }

            // Union the present sides through their occupied corners; with
            // four sides, a label per side is enough.
            Span<int> group = [0, 1, 2, 3];
            for (int side = 0; side < 4; side++)
            {
                int following = (side + 1) & 3;
                if (present[side] && present[following]
                    && IsOccupied(x + Dx(side) + Dx(following), y + Dy(side) + Dy(following)))
                {
                    int from = group[following], to = group[side];
                    for (int s = 0; s < 4; s++)
                    {
                        if (group[s] == from)
                        {
                            group[s] = to;
                        }
                    }
                }
            }

            int label = -1;
            bool locallyJoined = true;
            for (int side = 0; side < 4; side++)
            {
                if (present[side])
                {
                    if (label < 0)
                    {
                        label = group[side];
                    }
                    else if (group[side] != label)
                    {
                        locallyJoined = false;
                    }
                }
            }

            if (locallyJoined)
            {
                return true;
            }

            if (!_cutsCurrent)
            {
                FindCuts(cell);
                _cutsCurrent = true;
            }

            return !_cut[cell];
        }

        /// <summary>Empties a cell; the cut cells are found again when next needed.</summary>
        public void Empty(int cell)
        {
            Occupied[cell] = false;
            _cutsCurrent = false;
        }

        /// <summary>North, east, south, west: the sides in ring order, each followed by the one its corner joins it to.</summary>
        private static int Dx(int side) => side switch { 1 => 1, 3 => -1, _ => 0 };

        private static int Dy(int side) => side switch { 0 => 1, 2 => -1, _ => 0 };

        private bool IsOccupied(int x, int y) =>
            x >= 0 && y >= 0 && x < _columns && y < _rows && Occupied[(y * _columns) + x];

        /// <summary>
        /// Marks every cut cell of the occupied cells: an iterative
        /// depth-first walk from <paramref name="root"/>, where a cell other
        /// than the root cuts when some child's subtree reaches no higher
        /// than the cell itself, and the root cuts when it has two children.
        /// </summary>
        private void FindCuts(int root)
        {
            CutWalks++;
            Array.Clear(_discovered);
            Array.Clear(_cut);
            int time = 0, depth = 0, rootChildren = 0;
            _discovered[root] = _low[root] = ++time;
            _parent[root] = -1;
            _stackCell[depth] = root;
            _stackSide[depth] = 0;
            depth++;
            while (depth > 0)
            {
                int at = _stackCell[depth - 1];
                int side = _stackSide[depth - 1];
                if (side < 4)
                {
                    _stackSide[depth - 1] = side + 1;
                    int nx = (at % _columns) + Dx(side), ny = (at / _columns) + Dy(side);
                    if (!IsOccupied(nx, ny))
                    {
                        continue;
                    }

                    int next = (ny * _columns) + nx;
                    if (_discovered[next] == 0)
                    {
                        _parent[next] = at;
                        _discovered[next] = _low[next] = ++time;
                        _stackCell[depth] = next;
                        _stackSide[depth] = 0;
                        depth++;
                        rootChildren += at == root ? 1 : 0;
                    }
                    else if (next != _parent[at])
                    {
                        _low[at] = Math.Min(_low[at], _discovered[next]);
                    }

                    continue;
                }

                depth--;
                int parent = _parent[at];
                if (parent >= 0)
                {
                    _low[parent] = Math.Min(_low[parent], _low[at]);
                    if (parent != root && _low[at] >= _discovered[parent])
                    {
                        _cut[parent] = true;
                    }
                }
            }

            _cut[root] = rootChildren >= 2;
        }
    }

    /// <summary>
    /// A random spanning tree of the occupied cells, as the walls each cell
    /// must have a doorway on: a tree wall between two cells sets the facing
    /// bit on both.
    /// </summary>
    /// <remarks>
    /// The draws are the shuffle of the shared walls, listed in cell order
    /// (east wall, then north wall, per cell); the walls are then kept in
    /// shuffled order when they join two groups not yet joined. A set of
    /// wall pairs used to hold the result; per-cell masks are what the fill
    /// reads, and hold the same walls.
    /// <para>
    /// With large rooms placed (<paramref name="largeAt"/>), a wall touching
    /// one is listed only where that room has a socket (both rooms, for a
    /// wall between two), since only there can the level have a doorway;
    /// every other wall is listed as it always was, so without large rooms
    /// the list, and the draws, are exactly today's. The groups were placed
    /// so that the walls left still join every occupied cell
    /// (<see cref="Joined"/>), so the tree still spans them.
    /// </para>
    /// </remarks>
    private static int[] SpanningTree(SplitMix64 random, int rows, int columns, bool[] occupied, int[]? largeAt = null, int[]? maskOf = null)
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

                if (x + 1 < columns && occupied[here + 1] && Eligible(largeAt, maskOf, here, here + 1, East, West))
                {
                    walls.Add((here, here + 1));
                }

                if (y + 1 < rows && occupied[here + columns] && Eligible(largeAt, maskOf, here, here + columns, North, South))
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

        int[] required = new int[occupied.Length];
        foreach ((int a, int b) in walls)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                parent[ra] = rb;
                // North first: on a one-column grid a north neighbour is
                // also the next cell, and there are no east walls there.
                if (b - a == columns)
                {
                    required[a] |= North;
                    required[b] |= South;
                }
                else
                {
                    required[a] |= East;
                    required[b] |= West;
                }
            }
        }

        return required;
    }
}
