//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;
using SourceSharp.MapTools.Vis;

using Xunit;
using Xunit.Abstractions;

using Bounds = SourceSharp.MapGen.Catalog.Bounds;
using KitPoint = SourceSharp.MapGen.Point;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3x3 rooms sample, rearranged: for every level of the default subset
/// (<see cref="Rooms3x3Permutations.DefaultCases"/>), the level file linked
/// from the compiled rooms (<c>ssmap link level.yaml</c>) is checked against
/// the same level file flattened into one VMF (<c>ssmap link --flatten</c>)
/// and compiled whole by the ordinary vbsp path.
/// </summary>
/// <remarks>
/// <para>
/// The two BSPs are not byte-equal and are not meant to be: the whole-map
/// compile builds one tree over all nine rooms, the link hangs nine room trees
/// under a grid of cell planes. So the maps are compared in what a game can
/// observe, one fact per property:
/// </para>
/// <list type="bullet">
/// <item><b>Loader validation</b> passes on both, and neither has a node plane
/// the engine's axial shortcut would misread.</item>
/// <item><b>Point contents</b> are equal at every point of a lattice over the
/// level and a margin around it — empty, solid and grate leaves — and the
/// doorways are open exactly where the layout joints them.</item>
/// <item><b>Each kind's feature</b> reads as its material says in both maps:
/// its leaf contents at its centre, and which of the solid, player and shot
/// masks a segment through it stops at. The player clip and the detail crate
/// leave their leaves empty, so only a trace sees them.</item>
/// <item><b>Line traces</b> between room centres and between seeded random open
/// points stop at the same fraction in both maps for the solid, player and
/// shot masks, and each map's tree gives the same answer as its bare brush
/// list.</item>
/// <item><b>Reachability</b>: every room reaches every other through open,
/// player-passable space, as the layout's joints say, in both maps; and a
/// standing player's hull, not just a point, fits all the way from every
/// room to every other.</item>
/// <item><b>Visibility</b>: the linked PVS, composed through the doorways,
/// lies within the door-graph replay and keeps every room's own rows, and
/// it keeps every sight line of the monolithic map, each of which the real
/// vvis there keeps too.</item>
/// <item><b>Collision</b>: the world collision holds the same convexes, in the
/// same contents classes, at the same places, and every room has some.</item>
/// <item><b>Faces</b>: every plane draws the same area in the same material,
/// except the doorway surfaces of jointed sockets, which the linked map lacks
/// by exactly the documented amount.</item>
/// <item><b>Entities</b>: the same entities at the same places, facing the
/// same way.</item>
/// </list>
/// <para>
/// The default subset is the sample level, its three turns and twelve seeded
/// levels, some with empty cells; each pair is built once and shared by
/// every fact (<see cref="Rooms3x3Fixture"/>). The same checks run over
/// any number of arrangements, up to all of them, in the opt-in sweep
/// (<see cref="Rooms3x3SweepFactAttribute"/>).
/// </para>
/// <para>
/// Building this class found a linker bug: a room whose compile folded a
/// texinfo away (the player clip's, the grate's) carries original faces
/// whose texinfo is past its table, and the plug census read it —
/// <c>LevelLinkerRelocationTests.AStaleOriginalFaceTexinfoDoesNotDecideWhichFacesAreThePlug</c>.
/// </para>
/// </remarks>
public sealed class Rooms3x3EquivalenceTests(Rooms3x3Fixture fixture, ITestOutputHelper output) : IClassFixture<Rooms3x3Fixture>
{
    private const int MaskSolid = (int)(BrushContents.Solid | BrushContents.Moveable | BrushContents.Window
        | BrushContents.Monster | BrushContents.Grate);

    private const int MaskPlayerSolid = MaskSolid | (int)BrushContents.PlayerClip;

    private const int MaskShot = (int)(BrushContents.Solid | BrushContents.Moveable | BrushContents.Monster
        | BrushContents.Window | BrushContents.Debris | BrushContents.Hitbox);

    /// <summary>The lattice's pitch: half the 16-unit grid every brush lies on.</summary>
    private const float Pitch = 8f;

    /// <summary>
    /// The lattice starts half a pitch off the grid, so no sample lies on a
    /// brush plane, and a margin of 12 beyond the level on every side.
    /// </summary>
    private const float LatticeStart = -12f;

    private static readonly int Across = (int)(((3 * Rooms3x3Kit.CellSize) - (2 * LatticeStart)) / Pitch) + 1;

    private static readonly int Up = (int)((Rooms3x3Kit.CellSize - (2 * LatticeStart)) / Pitch) + 1;

    public static TheoryData<string> Cases => Rooms3x3Fixture.CaseNames;

    // ---- the subset itself ---------------------------------------------------

    /// <summary>
    /// The default subset is the four turns, then the twelve seeded levels;
    /// all are valid, some leave cells empty, and between them every kind of
    /// room is placed.
    /// </summary>
    [Fact]
    public void TheDefaultSubsetIsTheTurnsAndTheSeeds()
    {
        Assert.Equal(16, Rooms3x3Fixture.Cases.Count);
        Assert.All(Rooms3x3Fixture.Cases, c => Assert.True(c.Arrangement.IsValid(), c.Name));
        Assert.Equal(
            ["rooms3x3", "rooms3x3_turn1", "rooms3x3_turn2", "rooms3x3_turn3"],
            Rooms3x3Fixture.Cases.Take(4).Select(c => c.Name));
        Assert.Equal(Rooms3x3Permutations.Seeds.Select(s => s.Name), Rooms3x3Fixture.Cases.Skip(4).Select(c => c.Name));
        Assert.Contains(Rooms3x3Fixture.Cases, c => c.Arrangement.Cells.Any(cell => cell is null));
        Assert.Equal(
            Rooms3x3Kit.Kinds.Select(k => k.Name).Order(),
            Rooms3x3Fixture.Cases.SelectMany(c => c.Arrangement.Cells).OfType<Rooms3x3Cell>().Select(c => c.Kind).Distinct().Order());
    }

    /// <summary>
    /// The comparison can tell arrangements apart: the sample level linked,
    /// against the monolithic map of the same level with only the centre
    /// cross turned a quarter (same sockets, so still valid), disagrees on
    /// point contents — and only inside the centre cell, where the pillar
    /// moved. A criterion that passed here would pass anything.
    /// </summary>
    [Fact]
    public async Task OneTurnedRoomIsSeenByThePointContents()
    {
        Rooms3x3Pair pair = await fixture.PairAsync(Rooms3x3Permutations.LevelName);
        List<Rooms3x3Cell?> cells = [.. Rooms3x3Permutations.Canonical.Cells];
        Rooms3x3Cell cross = cells[4]!.Value;
        Assert.Equal("cross", cross.Kind);
        cells[4] = cross with { Rotation = (cross.Rotation + 1) % 4 };
        Rooms3x3Arrangement turned = new(cells);
        Assert.True(turned.IsValid());
        LevelProbe other = new((await fixture.MonolithicAsync(turned, "turned_cross")).Bsp!);

        int differ = 0;
        foreach (Vec3 p in Lattice())
        {
            if (pair.LinkedProbe.Contents(p) != other.Contents(p))
            {
                differ++;
                Assert.InRange(p.X, Rooms3x3Kit.CellSize, 2 * Rooms3x3Kit.CellSize);
                Assert.InRange(p.Y, Rooms3x3Kit.CellSize, 2 * Rooms3x3Kit.CellSize);
            }
        }

        Assert.True(differ > 0, "turning the centre room changed no point's contents");
    }

    // ---- the sweep ---------------------------------------------------------------

    /// <summary>
    /// Every check of this class over the arrangements
    /// <c>SSMAP_ROOMS3X3_SWEEP</c> asks for, one pair at a time, none kept
    /// (<see cref="Rooms3x3SweepFactAttribute"/>).
    /// </summary>
    [Rooms3x3SweepFact]
    public async Task EveryArrangementInTheSweepMatchesItsMonolithicMap()
    {
        int requested = Rooms3x3SweepFactAttribute.Requested(
            Environment.GetEnvironmentVariable(Rooms3x3SweepFactAttribute.Variable))!.Value;
        IReadOnlyList<Rooms3x3Arrangement> all = Rooms3x3Fixture.All;
        foreach (int index in SweepIndices(all.Count, requested))
        {
            Rooms3x3Pair pair = await fixture.BuildAsync(new Rooms3x3Case($"arrangement_{index}", "the sweep", all[index]));
            await AllChecksAsync(pair, fixture.Library);
        }
    }

    /// <summary>Which arrangements a sweep of <paramref name="requested"/> visits: evenly spread, or all.</summary>
    internal static IEnumerable<int> SweepIndices(int total, int requested) =>
        requested >= total
            ? Enumerable.Range(0, total)
            : Enumerable.Range(0, requested).Select(i => (int)((long)i * total / requested));

    /// <summary>The sweep's choice of arrangements, and its reading of the variable.</summary>
    [Fact]
    public void TheSweepVisitsWhatItIsAskedFor()
    {
        Assert.Equal([0, 1, 2], SweepIndices(3, int.MaxValue));
        Assert.Equal([0, 1, 2], SweepIndices(3, 3));
        Assert.Equal([0, 25, 50, 75], SweepIndices(100, 4));
        Assert.Equal(int.MaxValue, Rooms3x3SweepFactAttribute.Requested("all"));
        Assert.Equal(int.MaxValue, Rooms3x3SweepFactAttribute.Requested(" ALL "));
        Assert.Equal(250, Rooms3x3SweepFactAttribute.Requested("250"));
        Assert.Null(Rooms3x3SweepFactAttribute.Requested(null));
        Assert.Null(Rooms3x3SweepFactAttribute.Requested("0"));
        Assert.Null(Rooms3x3SweepFactAttribute.Requested("-3"));
        Assert.Null(Rooms3x3SweepFactAttribute.Requested("yes"));
    }

    /// <summary>Every criterion of this class, on one pair.</summary>
    internal static async Task AllChecksAsync(Rooms3x3Pair pair, RoomLibrary library)
    {
        await BothMapsPassTheLoaderValidatorAsync(pair);
        PointContentsAgreeEverywhereCheck(pair);
        KitFeaturesStopTheMasksTheyShouldCheck(pair);
        DoorwaysAreOpenExactlyWhereTheLayoutJointsThemCheck(pair);
        TracesStopAtTheSameFractionCheck(pair);
        EveryRoomReachesExactlyTheRoomsItsDoorsJoinCheck(pair);
        TheLinkedPvsIsWithinTheDoorGraphAndKeepsEverySightLineCheck(pair, library);
        WorldCollisionHoldsTheSameConvexesCheck(pair);
        FacesDifferOnlyByTheStrippedDoorwaySurfacesCheck(pair);
        EntitiesAreTheSameCheck(pair);
    }

    // ---- 1. loader validation --------------------------------------------------

    /// <summary>Both maps pass the loader's rules, and no node plane is a negative axial plane.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task BothMapsPassTheLoaderValidator(string name) =>
        await BothMapsPassTheLoaderValidatorAsync(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="BothMapsPassTheLoaderValidator"/>, for any pair.</summary>
    internal static async Task BothMapsPassTheLoaderValidatorAsync(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        ValidationReport linked = await BspValidator.CheckAsync(pair.Linked.Bsp, CancellationToken.None);
        ValidationReport whole = await BspValidator.CheckAsync(pair.Monolithic.Bsp!, CancellationToken.None);
        Assert.True(linked.ErrorCount == 0, $"{name} linked: {string.Join("; ", linked.Diagnostics)}");
        Assert.True(whole.ErrorCount == 0, $"{name} monolithic: {string.Join("; ", whole.Diagnostics)}");
        Assert.Empty(pair.LinkedProbe.NegativeAxialNodePlanes());
        Assert.Empty(pair.MonolithicProbe.NegativeAxialNodePlanes());
    }

    // ---- 2. point contents -----------------------------------------------------

    /// <summary>
    /// Every lattice point has the same contents in both maps, and the lattice
    /// meets every contents kind the kit uses.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PointContentsAgreeEverywhere(string name) =>
        PointContentsAgreeEverywhereCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="PointContentsAgreeEverywhere"/>, for any pair.</summary>
    internal static void PointContentsAgreeEverywhereCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        List<string> differ = [];
        HashSet<int> kinds = [];
        foreach (Vec3 p in Lattice())
        {
            int linked = pair.LinkedProbe.Contents(p);
            int whole = pair.MonolithicProbe.Contents(p);
            kinds.Add(whole);
            if (linked != whole && differ.Count < 10)
            {
                differ.Add($"({p.X} {p.Y} {p.Z}) linked 0x{linked:x} monolithic 0x{whole:x}");
            }
        }

        Assert.True(differ.Count == 0, $"{name}: contents differ at {string.Join(", ", differ)}");

        // Empty, solid and grate leaves are what a point can report here, the
        // grate only where a corner room stands; the clip and detail brushes
        // leave their leaves empty and are met by traces instead
        // (KitFeaturesStopTheMasksTheyShould).
        bool corner = pair.Case.Arrangement.Placed.Any(c => pair.Case.Arrangement.KindAt(c.X, c.Y).Name == "corner");
        Assert.Equal(
            corner
                ? [0, (int)BrushContents.Solid, (int)(BrushContents.Grate | BrushContents.Translucent)]
                : [0, (int)BrushContents.Solid],
            kinds.Order());
    }

    /// <summary>
    /// Each room kind's feature reads the same in both maps and as its
    /// material says: at its centre the pillar and the step are solid leaves,
    /// the grate a grate leaf, the player clip and the detail crate empty
    /// leaves (a clip or detail brush does not fill its leaf); and a segment
    /// straight through it is stopped by exactly the masks its contents are
    /// in — the clip only by the player mask, the grate by all but the shot
    /// mask, the solid ones by all three.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task KitFeaturesStopTheMasksTheyShould(string name) =>
        KitFeaturesStopTheMasksTheyShouldCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="KitFeaturesStopTheMasksTheyShould"/>, for any pair.</summary>
    internal static void KitFeaturesStopTheMasksTheyShouldCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;
        Rooms3x3Arrangement arrangement = pair.Case.Arrangement;

        Dictionary<string, (int Leaf, bool Solid, bool Player, bool Shot)> expected = new(StringComparer.Ordinal)
        {
            ["cross"] = ((int)BrushContents.Solid, true, true, true),
            ["tee"] = (0, false, true, false),
            ["corner"] = ((int)(BrushContents.Grate | BrushContents.Translucent), true, true, false),
            ["hall"] = (0, true, true, true),
            ["end"] = ((int)BrushContents.Solid, true, true, true),
        };

        HashSet<string> seen = [];
        foreach ((int x, int y) in arrangement.Placed)
        {
            {
                RoomKind kind = arrangement.KindAt(x, y);
                Rooms3x3Placement placement = arrangement.Placement(x, y);
                Bounds box = kind.Features[0].Box;
                float cy = (box.Mins.Y + box.Maxs.Y) / 2, cz = (box.Mins.Z + box.Maxs.Z) / 2;
                Vec3 centre = World(placement, new((box.Mins.X + box.Maxs.X) / 2, cy, cz));
                Vec3 before = World(placement, new(box.Mins.X - 8, cy, cz));
                Vec3 after = World(placement, new(box.Maxs.X + 8, cy, cz));
                (int leaf, bool solid, bool player, bool shot) = expected[kind.Name];

                foreach (LevelProbe probe in new[] { pair.LinkedProbe, pair.MonolithicProbe })
                {
                    string what = $"{name}: the {kind.Name} at cell ({x}, {y})";
                    Assert.True(leaf == probe.Contents(centre), $"{what}: leaf contents 0x{probe.Contents(centre):x}");
                    Assert.True(solid == probe.Trace(before, after, MaskSolid) < 1f, $"{what}: solid mask");
                    Assert.True(player == probe.Trace(before, after, MaskPlayerSolid) < 1f, $"{what}: player mask");
                    Assert.True(shot == probe.Trace(before, after, MaskShot) < 1f, $"{what}: shot mask");
                }

                seen.Add(kind.Name);
            }
        }

        // Every kind is met across the default subset (TheDefaultSubsetIsTheTurnsAndTheSeeds).
        Assert.NotEmpty(seen);
    }

    /// <summary>
    /// The middle of every jointed doorway is open and the middle of every
    /// capped one is solid, in both maps: the layout's joints are the only
    /// doors.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DoorwaysAreOpenExactlyWhereTheLayoutJointsThem(string name) =>
        DoorwaysAreOpenExactlyWhereTheLayoutJointsThemCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="DoorwaysAreOpenExactlyWhereTheLayoutJointsThem"/>, for any pair.</summary>
    internal static void DoorwaysAreOpenExactlyWhereTheLayoutJointsThemCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;
        Rooms3x3Arrangement arrangement = pair.Case.Arrangement;

        int open = 0, capped = 0;
        foreach ((int x, int y) in arrangement.Placed)
        {
            {
                Rooms3x3Placement placement = arrangement.Placement(x, y);
                foreach ((KitSide mine, _) in arrangement.Joints(x, y))
                {
                    Vec3 centre = Centre(placement.Apply(Rooms3x3Kit.PlugBox(mine)));
                    Assert.Equal(0, pair.LinkedProbe.Contents(centre));
                    Assert.Equal(0, pair.MonolithicProbe.Contents(centre));
                    open++;
                }

                foreach (KitSide cap in arrangement.Caps(x, y))
                {
                    Vec3 centre = Centre(placement.Apply(Rooms3x3Kit.PlugBox(cap)));
                    Assert.Equal((int)BrushContents.Solid, pair.LinkedProbe.Contents(centre));
                    Assert.Equal((int)BrushContents.Solid, pair.MonolithicProbe.Contents(centre));
                    capped++;
                }
            }
        }

        // A level that connects n rooms has at least n - 1 joints, counted
        // once from each side.
        int rooms = arrangement.Placed.Count();
        Assert.True(open >= 2 * (rooms - 1), $"{name}: {open} jointed socket(s) for {rooms} rooms");
        Assert.Equal(arrangement.Placed.Sum(c => arrangement.KindAt(c.X, c.Y).Sockets.Count), open + capped);
    }

    // ---- 3. traces ---------------------------------------------------------------

    /// <summary>
    /// Segments between every two room centres at eye height, and between
    /// seeded random open lattice points, stop at the same fraction in both
    /// maps under the solid, player and shot masks; in each map the tree walk
    /// finds the same brush the bare brush list does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TracesStopAtTheSameFraction(string name) =>
        TracesStopAtTheSameFractionCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="TracesStopAtTheSameFraction"/>, for any pair.</summary>
    internal static void TracesStopAtTheSameFractionCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        List<(Vec3, Vec3)> segments = [];
        List<Vec3> eyes = [.. RoomEyes(pair.Case.Arrangement)];
        for (int a = 0; a < eyes.Count; a++)
        {
            for (int b = a + 1; b < eyes.Count; b++)
            {
                segments.Add((eyes[a], eyes[b]));
            }
        }

        List<Vec3> open = [.. Lattice().Where(p => pair.MonolithicProbe.Contents(p) == 0)];
        Random random = new(0x3A3);
        for (int i = 0; i < 400; i++)
        {
            segments.Add((open[random.Next(open.Count)], open[random.Next(open.Count)]));
        }

        int blocked = 0, clear = 0;
        foreach ((Vec3 a, Vec3 b) in segments)
        {
            foreach (int mask in new[] { MaskSolid, MaskPlayerSolid, MaskShot })
            {
                float linked = pair.LinkedProbe.Trace(a, b, mask);
                float whole = pair.MonolithicProbe.Trace(a, b, mask);
                string what = $"{name}: ({a.X} {a.Y} {a.Z}) -> ({b.X} {b.Y} {b.Z}) mask 0x{mask:x}";
                Assert.True(Math.Abs(linked - whole) <= 1e-5f, $"{what}: linked {linked}, monolithic {whole}");
                Assert.True(linked == pair.LinkedProbe.TraceAllBrushes(a, b, mask), $"{what}: the linked tree misses a brush");
                Assert.True(whole == pair.MonolithicProbe.TraceAllBrushes(a, b, mask), $"{what}: the monolithic tree misses a brush");
                if (linked < 1f)
                {
                    blocked++;
                }
                else
                {
                    clear++;
                }
            }
        }

        // Both outcomes occur, so the comparison is not vacuous either way.
        Assert.True(blocked > 0 && clear > 0, $"{name}: {blocked} blocked, {clear} clear");
    }

    // ---- 4. reachability ---------------------------------------------------------

    /// <summary>
    /// Flooding the lattice's player-passable points from each room's centre
    /// reaches every room the layout's joints connect it to, and no other,
    /// in both maps; and, since a level is only valid when a player can
    /// reach every room, a standing player's hull flooded from any room's
    /// centre reaches every placed room, in both maps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRoomReachesExactlyTheRoomsItsDoorsJoin(string name) =>
        EveryRoomReachesExactlyTheRoomsItsDoorsJoinCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="EveryRoomReachesExactlyTheRoomsItsDoorsJoin"/>, for any pair.</summary>
    internal static void EveryRoomReachesExactlyTheRoomsItsDoorsJoinCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;
        Rooms3x3Arrangement arrangement = pair.Case.Arrangement;
        int[] components = arrangement.Components();
        int[] expected = [.. arrangement.Placed.Select(c => components[(c.Y * Rooms3x3Arrangement.Size) + c.X])];
        Assert.All(expected, c => Assert.Equal(0, c));

        foreach ((string map, LevelProbe probe) in new[] { ("linked", pair.LinkedProbe), ("monolithic", pair.MonolithicProbe) })
        {
            bool[] blocked = Blocked(probe);
            int[] component = Flood(blocked);
            List<Vec3> eyes = [.. RoomEyes(arrangement)];
            for (int a = 0; a < eyes.Count; a++)
            {
                for (int b = 0; b < eyes.Count; b++)
                {
                    int ca = component[Index(eyes[a])];
                    int cb = component[Index(eyes[b])];
                    Assert.True(ca >= 0, $"{name} {map}: room {a}'s centre is not open");
                    Assert.True(
                        (ca == cb) == (expected[a] == expected[b]),
                        $"{name} {map}: rooms {a} and {b} reach each other: {ca == cb}; the joints say {expected[a] == expected[b]}");
                }
            }

            int[] hull = HullFlood(blocked);
            List<(int I, int J, int K)> feet = [.. RoomFeet(arrangement)];
            int first = hull[NodeIndex(feet[0])];
            Assert.True(first >= 0, $"{name} {map}: a standing player does not fit at room 0's centre");
            for (int a = 0; a < feet.Count; a++)
            {
                Assert.True(
                    hull[NodeIndex(feet[a])] == first,
                    $"{name} {map}: a standing player cannot walk from room 0 to room {a}");
            }
        }
    }

    /// <summary>
    /// The hull check can fail: on a lattice whose only way between two
    /// rooms is a gap narrower than the player, points still flood through
    /// but the hull does not.
    /// </summary>
    [Fact]
    public void TheHullFloodStopsAtAGapNarrowerThanAPlayer()
    {
        // Everything blocked except two open boxes joined by a slot 3 points
        // (24 units) wide, the full height.
        bool[] blocked = new bool[Across * Across * Up];
        for (int index = 0; index < blocked.Length; index++)
        {
            int i = index % Across, j = index / Across % Across, k = index / (Across * Across);
            bool roomA = i is >= 10 and < 30 && j is >= 10 and < 30 && k is >= 2 and < 30;
            bool roomB = i is >= 40 and < 60 && j is >= 10 and < 30 && k is >= 2 and < 30;
            bool slot = i is >= 30 and < 40 && j is >= 18 and < 21 && k is >= 2 and < 30;
            blocked[index] = !(roomA || roomB || slot);
        }

        int[] points = Flood(blocked);
        int[] hull = HullFlood(blocked);
        int PointAt(int i, int j, int k) => i + (Across * j) + (Across * Across * k);
        Assert.Equal(points[PointAt(15, 15, 5)], points[PointAt(45, 15, 5)]);
        Assert.True(hull[NodeIndex((15, 15, 3))] >= 0);
        Assert.True(hull[NodeIndex((45, 15, 3))] >= 0);
        Assert.NotEqual(hull[NodeIndex((15, 15, 3))], hull[NodeIndex((45, 15, 3))]);

        // Widen the slot to exactly a player's width (5 points, 32 units) and it passes.
        for (int index = 0; index < blocked.Length; index++)
        {
            int i = index % Across, j = index / Across % Across, k = index / (Across * Across);
            if (i is >= 30 and < 40 && j is >= 18 and < 23 && k is >= 2 and < 30)
            {
                blocked[index] = false;
            }
        }

        hull = HullFlood(blocked);
        Assert.Equal(hull[NodeIndex((15, 15, 3))], hull[NodeIndex((45, 15, 3))]);
    }

    // ---- 5. visibility -----------------------------------------------------------

    /// <summary>
    /// The linked PVS lies within the door-graph replay of the rooms' own
    /// rows and the layout's joints and keeps each room's own rows, and it
    /// keeps every sight line of the monolithic map, the flattened level
    /// compiled whole: for every pair of sample points (nine in each of the
    /// monolithic map's open leaves) joined by a segment no structural solid
    /// crosses, the real vvis on the monolithic map sees the pair's clusters
    /// (so the sight line is one vvis keeps too) and so does the linked map.
    /// This is the proof that the door visibility is conservative, on every
    /// arrangement of the sample.
    /// </summary>
    /// <remarks>
    /// Why sight lines, not the monolithic rows themselves: vvis's rows are
    /// per cluster, and the monolithic map's clusters are not the linked
    /// map's (the whole-level compile cuts leaves across rooms that the room
    /// compiles never see). A monolithic cluster seeing another says only
    /// that some point of one sees some point of the other, so asking every
    /// linked cluster under the first to see every one under the second asks
    /// for the monolithic map's coarseness, not for what is visible; only
    /// the old closure, in which everything saw everything, ever met that.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheLinkedPvsIsWithinTheDoorGraphAndKeepsEverySightLine(string name) =>
        TheLinkedPvsIsWithinTheDoorGraphAndKeepsEverySightLineCheck(await fixture.PairAsync(name), fixture.Library);

    /// <summary>The check behind <see cref="TheLinkedPvsIsWithinTheDoorGraphAndKeepsEverySightLine"/>, for any pair.</summary>
    internal static void TheLinkedPvsIsWithinTheDoorGraphAndKeepsEverySightLineCheck(Rooms3x3Pair pair, RoomLibrary library)
    {
        DoorGraphFacts.AssertWithinDoorGraph(pair.Linked, pair.Layout, library);
        DoorGraphFacts.AssertKeepsEverySightLine(pair.Case.Name, pair.Linked, pair.LinkedProbe, pair.MonolithicProbe, pair.MonolithicVis);
    }

    /// <summary>
    /// The same checks on levels of the sample's rooms generated larger
    /// than 3 x 3, where lines through several doorways in a row decide what
    /// is visible: the linked PVS lies within the door graph, keeps every
    /// sight line of the flattened level compiled whole, each of which that
    /// level's vvis keeps too, and is the same bytes on one thread as on
    /// four. The counts go to the test output, for the record of how close
    /// the linked PVS comes to the monolithic one.
    /// </summary>
    [Theory]
    [InlineData(4, 4, 1ul, 0.0)]
    [InlineData(5, 4, 2ul, 0.1)]
    [InlineData(5, 5, 3ul, 0.2)]
    public async Task GeneratedLevelsKeepEverySightLine(int rows, int columns, ulong seed, double empty)
    {
        (LevelLayout layout, LinkedLevel linked, VbspResult monolithic, VisResult monolithicVis) =
            await fixture.GeneratedAsync(rows, columns, seed, empty, degree: 4);
        string name = $"{rows}x{columns} seed {seed}";
        (int visible, int graph) = DoorGraphFacts.AssertWithinDoorGraph(linked, layout, fixture.Library);
        DoorGraphFacts.SightLineCounts counts = DoorGraphFacts.AssertKeepsEverySightLine(
            name, linked, new LevelProbe(linked.Bsp), new LevelProbe(monolithic.Bsp!), monolithicVis);
        Assert.True(visible < graph, $"{name}: the door flow kept every pair of the door graph");

        (_, LinkedLevel serial, _, _) = await fixture.GeneratedAsync(rows, columns, seed, empty, degree: 1);
        Assert.True(
            linked.Bsp[BspLump.Visibility].Data.Span.SequenceEqual(serial.Bsp[BspLump.Visibility].Data.Span),
            $"{name}: the visibility differs between one thread and four");

        int clusters = linked.Vis.ClusterCount, monolithicClusters = monolithicVis.ClusterCount;
        output.WriteLine(
            $"{name}: linked {visible} of {clusters * clusters} cluster pairs ({linked.Vis.VisDataSize} bytes), door graph {graph};"
            + $" monolithic {monolithicVis.TotalVisibleClusters} of {monolithicClusters * monolithicClusters} ({monolithicVis.VisDataSize} bytes);"
            + $" {counts.Samples} samples: {counts.SightLines} sight lines, linked keeps {counts.LinkedPairs} sample pairs,"
            + $" monolithic {counts.MonolithicPairs}");
    }

    /// <summary>
    /// The sample turned a quarter, a half and three quarters as a whole
    /// sees what the unturned sample sees: every linked cluster, found by a
    /// point in one of its leaves turned with the level, sees exactly the
    /// clusters its counterpart sees. The flows run in each source room's
    /// frame, where a turned level is the same numbers.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheTurnedSampleSeesWhatTheSampleSees(int turns)
    {
        Rooms3x3Pair straight = await fixture.PairAsync(Rooms3x3Permutations.LevelName);
        Rooms3x3Pair turned = await fixture.PairAsync($"{Rooms3x3Permutations.LevelName}_turn{turns}");
        int clusters = straight.Linked.Vis.ClusterCount;
        Assert.Equal(clusters, turned.Linked.Vis.ClusterCount);

        // A point well inside one leaf of each linked cluster, and the
        // cluster the turned level has there.
        int[] map = Enumerable.Repeat(-1, clusters).ToArray();
        IReadOnlyList<DLeaf> leafs = straight.LinkedProbe.Leafs;
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.Cluster < 0 || map[leaf.Cluster] >= 0)
            {
                continue;
            }

            Vec3 centre = new(
                (leaf.Mins[0] + leaf.Maxs[0]) / 2f, (leaf.Mins[1] + leaf.Maxs[1]) / 2f, (leaf.Mins[2] + leaf.Maxs[2]) / 2f);
            if (straight.LinkedProbe.Leaf(centre) != l)
            {
                continue;
            }

            Vec3 point = centre;
            for (int t = 0; t < turns; t++)
            {
                point = new Vec3((3 * Rooms3x3Kit.CellSize) - point.Y, point.X, point.Z);
            }

            map[leaf.Cluster] = turned.LinkedProbe.Leafs[turned.LinkedProbe.Leaf(point)].Cluster;
        }

        Assert.All(map, m => Assert.True(m >= 0));
        Assert.Equal(clusters, map.Distinct().Count());
        for (int a = 0; a < clusters; a++)
        {
            for (int b = 0; b < clusters; b++)
            {
                Assert.True(
                    straight.Linked.Vis.CanSee(a, b) == turned.Linked.Vis.CanSee(map[a], map[b]),
                    $"cluster {a} seeing {b} unturned is {straight.Linked.Vis.CanSee(a, b)}; turned {turns}, {map[a]} seeing {map[b]} is not");
            }
        }

        Assert.Equal(straight.Linked.Vis.TotalVisibleClusters, turned.Linked.Vis.TotalVisibleClusters);
        Assert.Equal(straight.Linked.Vis.TotalAudibleClusters, turned.Linked.Vis.TotalAudibleClusters);
    }

    // ---- 6. collision ------------------------------------------------------------

    /// <summary>
    /// Both world collisions hold the same convexes: per contents class, the
    /// same multiset of convex bounds. The linked map drops the plug convexes
    /// of jointed sockets, which the monolithic map never had, and every room
    /// cell holds convexes of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WorldCollisionHoldsTheSameConvexes(string name) =>
        WorldCollisionHoldsTheSameConvexesCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="WorldCollisionHoldsTheSameConvexes"/>, for any pair.</summary>
    internal static void WorldCollisionHoldsTheSameConvexesCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        List<string> linked = Convexes(pair.Linked.Bsp);
        List<string> whole = Convexes(pair.Monolithic.Bsp!);
        Assert.NotEmpty(linked);
        List<string> onlyLinked = [.. linked.Except(whole)];
        List<string> onlyWhole = [.. whole.Except(linked)];
        Assert.True(
            onlyLinked.Count == 0 && onlyWhole.Count == 0 && linked.Count == whole.Count,
            $"{name}: {linked.Count} linked convexes, {whole.Count} monolithic; only linked: "
            + $"{string.Join("; ", onlyLinked.Take(8))}; only monolithic: {string.Join("; ", onlyWhole.Take(8))}");

        foreach ((int x, int y) in pair.Case.Arrangement.Placed)
        {
            string cell = string.Create(CultureInfo.InvariantCulture, $"cell {x} {y}");
            Assert.Contains(linked, c => c.EndsWith(cell, StringComparison.Ordinal));
        }
    }

    // ---- 7. faces ----------------------------------------------------------------

    /// <summary>
    /// Every plane draws the same area in the same material in both maps,
    /// except at jointed doorways, where the linked map is short by exactly
    /// the doorway's floor, ceiling and two jambs through both walls.
    /// </summary>
    /// <remarks>
    /// The documented limitation of stripping the plug at link time: in each
    /// room's own compile the doorway's jambs, floor and ceiling faced the
    /// solid plug, and vbsp emits no face between two solids, so the linked
    /// map has none there. The monolithic compile never had the plug and
    /// draws them. This fact asserts the difference is exactly that, and
    /// nothing else.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FacesDifferOnlyByTheStrippedDoorwaySurfaces(string name) =>
        FacesDifferOnlyByTheStrippedDoorwaySurfacesCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="FacesDifferOnlyByTheStrippedDoorwaySurfaces"/>, for any pair.</summary>
    internal static void FacesDifferOnlyByTheStrippedDoorwaySurfacesCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        Dictionary<(int, int, int, int, string), double> linked = pair.LinkedProbe.DrawnArea();
        Dictionary<(int, int, int, int, string), double> whole = pair.MonolithicProbe.DrawnArea();
        Dictionary<(int, int, int, int, string), double> missing = DoorwaySurfaces(pair.Case.Arrangement);
        Assert.NotEmpty(missing);

        List<string> wrong = [];
        foreach (var key in linked.Keys.Union(whole.Keys).Union(missing.Keys))
        {
            double deficit = whole.GetValueOrDefault(key) - linked.GetValueOrDefault(key);
            double expected = missing.GetValueOrDefault(key);
            if (Math.Abs(deficit - expected) > 0.5)
            {
                wrong.Add($"{key}: monolithic {whole.GetValueOrDefault(key)}, linked {linked.GetValueOrDefault(key)}, doorways {expected}");
            }
        }

        Assert.True(wrong.Count == 0, $"{name}: {string.Join("; ", wrong)}");
    }

    // ---- 8. entities -------------------------------------------------------------

    /// <summary>Both maps carry the same entities, at the same origins, facing the same way.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EntitiesAreTheSame(string name) =>
        EntitiesAreTheSameCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="EntitiesAreTheSame"/>, for any pair.</summary>
    internal static void EntitiesAreTheSameCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;

        List<string> linked = Entities(pair.Linked.Bsp);
        List<string> whole = Entities(pair.Monolithic.Bsp!);
        // A light in every room, and a player start in every end room.
        Rooms3x3Arrangement arrangement = pair.Case.Arrangement;
        Assert.Equal(
            arrangement.Placed.Count() + arrangement.Placed.Count(c => arrangement.KindAt(c.X, c.Y).Name == "end"),
            whole.Count);
        Assert.Equal(whole, linked);
    }

    // ---- 9. library singletons ------------------------------------------------------

    /// <summary>The four turns of the sample level, as quarter turns.</summary>
    public static TheoryData<int> Turns => new() { 0, 1, 2, 3 };

    /// <summary>
    /// The sample library with a sun and a fog controller in the gap beside
    /// its first cell (section 8 of the rooms design, the singletons row of
    /// its test matrix): at every turn of the sample level, the link and the
    /// flattened map's compile carry the same entities, each placed room's
    /// and exactly one sun and one fog, the sun's angles as authored however
    /// the rooms turn; and the link's budget counts the two once, its entity
    /// list being the linked lump's. The rooms are the fixture's compiled
    /// rooms: a library entity in the gaps changes no room.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ALibrarySunIsOneUnturnedEntityInBothMaps(int turns)
    {
        VmfDocument library = await VmfDocument.ParseAsync(fixture.LibraryVmf.ToBytes());
        library.Chunks.Add(GapEntity("light_environment", 990001, ("angles", "-45 30 0"), ("_light", "255 255 255 200"), ("_ambient", "40 40 60 80")));
        library.Chunks.Add(GapEntity("env_fog_controller", 990002, ("fogenable", "1"), ("fogcolor", "1 2 3")));
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        Assert.Equal(2, split.LibraryEntities.Count);

        RoomLibrary rooms = new(fixture.Library.Kit, fixture.Library.CellSize)
        {
            Options = fixture.Library.Options,
            LibraryEntities = split.LibraryEntities,
        };
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            rooms.Add(room);
        }

        string name = Rooms3x3Permutations.TurnName(turns);
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => rooms.Find(n)?.Definition, rooms.CellSize, rooms.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, rooms, fixture.Context(name));
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), fixture.Context(name));
        Assert.NotNull(whole.Bsp);

        List<string> wholeEntities = Entities(whole.Bsp!);
        Rooms3x3Arrangement arrangement = found.Arrangement;
        Assert.Equal(
            arrangement.Placed.Count() + arrangement.Placed.Count(c => arrangement.KindAt(c.X, c.Y).Name == "end") + 2,
            wholeEntities.Count);
        Assert.Equal(wholeEntities, Entities(linked.Bsp));
        foreach (BspData bsp in new[] { linked.Bsp, whole.Bsp! })
        {
            List<BspEntity> lump = [.. EntityLump.Parse(bsp[BspLump.Entities])];
            Assert.Equal("-45 30 0", Assert.Single(lump, e => e.ClassName == "light_environment").Get("angles"));
            Assert.Single(lump, e => e.ClassName == "env_fog_controller");
        }

        LevelEntityReport budget = linked.EntityBudget!;
        Assert.Equal(new EntityTally(2, 0, 0), budget.Library);
        Assert.Equal(EntityLump.Parse(linked.Bsp[BspLump.Entities]).Count, budget.Listed);

        static VmfChunk GapEntity(string classname, int id, params (string Key, string Value)[] keys)
        {
            VmfChunk entity = new(SourceSharp.MapTools.Bsp.MapFileLoader.EntityChunk);
            entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
            entity.AddKey("classname", classname);
            entity.AddKey("origin", "-128 128 128");
            foreach ((string key, string value) in keys)
            {
                entity.AddKey(key, value);
            }

            return entity;
        }
    }

    // ---- 10. static props ---------------------------------------------------------------

    /// <summary>
    /// The sample library with a static prop in the <c>tee</c> (section 4.3
    /// of the rooms design, the static props row of its test matrix and its
    /// <c>tee</c> fixture): at every turn of the sample level, the linked
    /// map and the flattened map's compile hold the same props (model,
    /// origin, angles, skin, solidity, flags, fades), one per placed
    /// <c>tee</c>; each linked prop's leaves are the leaves its hull touches
    /// in the linked tree; and the props cost the level no entity, its
    /// entity lump and budget being the fixture's link's. The <c>tee</c> is
    /// recompiled with the prop against a synthetic model; the other rooms
    /// are the fixture's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public async Task ATeesStaticPropIsTheSameInBothMaps(int turns)
    {
        VmfDocument library = await VmfDocument.ParseAsync(fixture.LibraryVmf.ToBytes());
        VmfChunk marker = library.GetChunks(SourceSharp.MapTools.Bsp.MapFileLoader.EntityChunk)
            .Single(e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity && e.GetValue(RoomLibraryVmf.NameKey) == "tee");
        Vec3 corner = VmfPlacement.Origin(marker)!.Value;
        VmfChunk prop = RoomPropHarness.Prop(990100, RoomPropHarness.BoxModel, new Vec3(192, 48, 16), "0 20 0", ("skin", "0"), ("solid", "6"));
        library.Chunks.Add(VmfPlacement.MoveEntity(prop, QuarterTurn.Translation(corner)));

        SourceSharp.MapTools.Io.InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
        {
            if (path.StartsWith("materials/", StringComparison.Ordinal))
            {
                disk.AddFile(path, bytes);
            }
        }

        foreach ((string path, byte[] bytes) in RoomPropHarness.Models())
        {
            disk.AddFile(path, bytes);
        }

        await using SourceSharp.MapTools.Io.ContentFileSystem mounted = new(
            [await SourceSharp.MapTools.Io.DirectoryContentMount.MountAsync(disk, SourceSharp.MapTools.Io.VPath.Empty)]);
        SourceSharp.MapTools.Bsp.VbspContext Context(string mapBase) => new(SourceSharp.MapTools.Options.VbspOptions.Default, mounted)
        {
            MapBase = mapBase,
            CollisionCooker = fixture.Context(mapBase).CollisionCooker,
        };

        LibraryRoom tee = RoomLibraryVmf.SplitLibrary(library).Rooms.Single(r => r.Definition.Name == "tee");
        RoomObject teeRoom = await RoomCompiler.CompileAsync(tee.Document, tee.Definition, Context("tee"));
        Assert.Single(teeRoom.StaticProps!.Props);
        RoomLibrary rooms = new(fixture.Library.Kit, fixture.Library.CellSize) { Options = fixture.Library.Options };
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            rooms.Add(room.Definition.Name == "tee" ? teeRoom : room);
        }

        string name = Rooms3x3Permutations.TurnName(turns);
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => rooms.Find(n)?.Definition, rooms.CellSize, rooms.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, rooms, fixture.Context(name));
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), Context(name));
        Assert.NotNull(whole.Bsp);

        List<string> props = RoomPropHarness.Observed(linked.Bsp);
        Assert.NotEmpty(props);
        Assert.Equal(layout.Rooms.Count(r => r.Placement.Room == "tee"), props.Count);
        Assert.Equal(RoomPropHarness.Observed(whole.Bsp!), props);

        StaticPropLump lump = RoomPropHarness.Props(linked.Bsp);
        SourceSharp.MapTools.Bsp.Props.BspTreeView tree = SourceSharp.MapTools.Bsp.Props.BspTreeView.FromBsp(linked.Bsp);
        List<Vec3[]> meshes = (await SourceSharp.MapTools.Bsp.Props.StaticPropEmitter.LoadMeshesAsync(Context(name), RoomPropHarness.BoxModel, [], CancellationToken.None))!;
        SourceSharp.MapTools.Bsp.Props.IStaticPropHull hull = (await new SourceSharp.MapTools.Bsp.Props.ManagedStaticPropCollision().BuildHullAsync(meshes))!;
        foreach (StaticProp placed in lump.Props)
        {
            List<ushort> walked = await SourceSharp.MapTools.Bsp.Props.StaticPropLeaves.ComputeAsync(tree, hull, placed.Origin, placed.Angles);
            Assert.Equal([.. walked.Select(l => (int)l)], RoomPropHarness.LeavesOf(lump, placed));
        }

        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Assert.Equal(pair.Linked.Bsp[BspLump.Entities].Data.ToArray(), linked.Bsp[BspLump.Entities].Data.ToArray());
        Assert.Equal(pair.Linked.EntityBudget!.Edicts, linked.EntityBudget!.Edicts);
        Assert.Equal(pair.Linked.EntityBudget.Listed, linked.EntityBudget.Listed);
    }

    // ---- helpers -----------------------------------------------------------------

    internal static IEnumerable<Vec3> Lattice()
    {
        for (int k = 0; k < Up; k++)
        {
            for (int j = 0; j < Across; j++)
            {
                for (int i = 0; i < Across; i++)
                {
                    yield return At(i, j, k);
                }
            }
        }
    }

    private static Vec3 At(int i, int j, int k) =>
        new(LatticeStart + (i * Pitch), LatticeStart + (j * Pitch), LatticeStart + (k * Pitch));

    private static int Index(Vec3 p) =>
        (int)((p.X - LatticeStart) / Pitch)
        + (Across * (int)((p.Y - LatticeStart) / Pitch))
        + (Across * Across * (int)((p.Z - LatticeStart) / Pitch));

    /// <summary>The centre of each placed room at eye height, on the lattice, in row order.</summary>
    private static IEnumerable<Vec3> RoomEyes(Rooms3x3Arrangement arrangement)
    {
        foreach ((int x, int y) in arrangement.Placed)
        {
            // 132 is the lattice point nearest the middle of a 256 cell.
            yield return new Vec3((x * Rooms3x3Kit.CellSize) + 132, (y * Rooms3x3Kit.CellSize) + 132, 84);
        }
    }

    /// <summary>
    /// Where a standing player's hull is placed in each placed room, as a
    /// hull node: centred on the room's middle, feet on the first lattice
    /// point above the floor (z 20, the floor's top being 16).
    /// </summary>
    private static IEnumerable<(int I, int J, int K)> RoomFeet(Rooms3x3Arrangement arrangement)
    {
        int perCell = (int)(Rooms3x3Kit.CellSize / Pitch);
        foreach ((int x, int y) in arrangement.Placed)
        {
            // The hull spans HullWide points from i: 116..148 around the middle 132.
            yield return ((x * perCell) + 16, (y * perCell) + 16, 4);
        }
    }

    /// <summary>Which lattice points a player cannot stand in: the leaf's contents, or a clip or detail brush the leaf lists.</summary>
    private static bool[] Blocked(LevelProbe probe)
    {
        bool[] blocked = new bool[Across * Across * Up];
        for (int k = 0; k < Up; k++)
        {
            for (int j = 0; j < Across; j++)
            {
                for (int i = 0; i < Across; i++)
                {
                    Vec3 p = At(i, j, k);
                    blocked[i + (Across * j) + (Across * Across * k)] =
                        (probe.Contents(p) & MaskPlayerSolid) != 0 || probe.InsideBrush(p, MaskPlayerSolid);
                }
            }
        }

        return blocked;
    }

    /// <summary>The standing player's hull in lattice points: 32 units is 4 pitches, so 5 points across; 72 is 9, so 10 up.</summary>
    private const int HullWide = (int)(PlayerHull.Width / Pitch) + 1;

    private const int HullTall = (int)(PlayerHull.Height / Pitch) + 1;

    private static int NodeIndex((int I, int J, int K) node) => node.I + (Across * node.J) + (Across * Across * node.K);

    /// <summary>
    /// Components of the positions a standing player's hull fits in,
    /// 6-connected; -1 where it does not fit. A node is the hull's low
    /// corner lattice point; the hull fits when none of the HullWide by
    /// HullWide by HullTall points it covers is blocked. The lattice is
    /// offset from the 16-unit grid every brush lies on, and no solid is
    /// thinner than 16, so a brush inside a hull always covers one of its
    /// points. A 3D prefix sum of the blocked points answers each node in
    /// constant time.
    /// </summary>
    private static int[] HullFlood(bool[] blocked)
    {
        int sx = Across + 1, sy = Across + 1;
        int[] sum = new int[sx * sy * (Up + 1)];
        int S(int i, int j, int k) => sum[i + (sx * j) + (sx * sy * k)];
        for (int k = 1; k <= Up; k++)
        {
            for (int j = 1; j <= Across; j++)
            {
                for (int i = 1; i <= Across; i++)
                {
                    int b = blocked[(i - 1) + (Across * (j - 1)) + (Across * Across * (k - 1))] ? 1 : 0;
                    sum[i + (sx * j) + (sx * sy * k)] = b
                        + S(i - 1, j, k) + S(i, j - 1, k) + S(i, j, k - 1)
                        - S(i - 1, j - 1, k) - S(i - 1, j, k - 1) - S(i, j - 1, k - 1)
                        + S(i - 1, j - 1, k - 1);
                }
            }
        }

        bool[] fits = new bool[Across * Across * Up];
        for (int k = 0; k + HullTall <= Up; k++)
        {
            for (int j = 0; j + HullWide <= Across; j++)
            {
                for (int i = 0; i + HullWide <= Across; i++)
                {
                    int i1 = i + HullWide, j1 = j + HullWide, k1 = k + HullTall;
                    int inside = S(i1, j1, k1) - S(i, j1, k1) - S(i1, j, k1) - S(i1, j1, k)
                        + S(i, j, k1) + S(i, j1, k) + S(i1, j, k) - S(i, j, k);
                    fits[i + (Across * j) + (Across * Across * k)] = inside == 0;
                }
            }
        }

        bool[] notFit = [.. fits.Select(f => !f)];
        return Flood(notFit);
    }

    /// <summary>Components of the lattice's unblocked points, 6-connected; -1 where blocked.</summary>
    private static int[] Flood(bool[] blocked)
    {
        int[] component = new int[Across * Across * Up];
        for (int index = 0; index < component.Length; index++)
        {
            component[index] = blocked[index] ? -1 : int.MaxValue;
        }

        int next = 0;
        Stack<int> stack = new();
        for (int start = 0; start < component.Length; start++)
        {
            if (component[start] != int.MaxValue)
            {
                continue;
            }

            component[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int i = at % Across, j = at / Across % Across, k = at / (Across * Across);
                foreach ((int di, int dj, int dk) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    int ni = i + di, nj = j + dj, nk = k + dk;
                    if (ni < 0 || nj < 0 || nk < 0 || ni >= Across || nj >= Across || nk >= Up)
                    {
                        continue;
                    }

                    int neighbour = ni + (Across * nj) + (Across * Across * nk);
                    if (component[neighbour] == int.MaxValue)
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

    /// <summary>A room-local point through the generator's placement.</summary>
    private static Vec3 World(Rooms3x3Placement placement, Vec3 local)
    {
        KitPoint p = placement.Apply(new KitPoint(local.X, local.Y, local.Z));
        return new Vec3(p.X, p.Y, p.Z);
    }

    private static Vec3 Centre(Bounds box) =>
        new((box.Mins.X + box.Maxs.X) / 2, (box.Mins.Y + box.Maxs.Y) / 2, (box.Mins.Z + box.Maxs.Z) / 2);

    private static IEnumerable<Vec3> Corners(Vec3 lo, Vec3 hi)
    {
        for (int b = 0; b < 8; b++)
        {
            yield return new Vec3((b & 1) == 0 ? lo.X : hi.X, (b & 2) == 0 ? lo.Y : hi.Y, (b & 4) == 0 ? lo.Z : hi.Z);
        }
    }

    /// <summary>
    /// The surfaces a jointed doorway draws in a whole-map compile and not in a
    /// link: through both rooms' walls, the floor and ceiling strips and the
    /// two jambs, keyed as <see cref="LevelProbe.DrawnArea"/> keys them.
    /// </summary>
    private static Dictionary<(int, int, int, int, string), double> DoorwaySurfaces(Rooms3x3Arrangement arrangement)
    {
        const float c = Rooms3x3Kit.CellSize;
        const float through = 2 * Rooms3x3Kit.Wall;
        Dictionary<(int, int, int, int, string), double> surfaces = [];
        void Add((int, int, int, int, string) key, double area) => surfaces[key] = surfaces.GetValueOrDefault(key) + area;

        for (int y = 0; y < Rooms3x3Arrangement.Size; y++)
        {
            for (int x = 0; x < Rooms3x3Arrangement.Size; x++)
            {
                foreach ((KitSide mine, _) in arrangement.Joints(x, y))
                {
                    // Each shared wall once: from the room west or south of it.
                    KitSide world = Rooms3x3Kit.Turn(mine, arrangement[x, y]!.Value.Rotation);
                    if (world is not (KitSide.East or KitSide.North))
                    {
                        continue;
                    }

                    Add((0, 0, 1, (int)Rooms3x3Kit.Wall, Rooms3x3Kit.FloorMaterial), through * Rooms3x3Kit.DoorWidth);
                    Add((0, 0, -1, -(int)(c - Rooms3x3Kit.Wall), Rooms3x3Kit.CeilingMaterial), through * Rooms3x3Kit.DoorWidth);
                    int along = (int)((world == KitSide.East ? y : x) * c);
                    (int ux, int uy) = world == KitSide.East ? (0, 1) : (1, 0);
                    Add((ux, uy, 0, along + (int)Rooms3x3Kit.DoorLow, Rooms3x3Kit.WallMaterial), through * Rooms3x3Kit.DoorHeight);
                    Add((-ux, -uy, 0, -(along + (int)Rooms3x3Kit.DoorHigh), Rooms3x3Kit.WallMaterial), through * Rooms3x3Kit.DoorHeight);
                }
            }
        }

        return surfaces;
    }

    /// <summary>
    /// The world collision's convexes, one line each: contents class, bounds
    /// to a tenth of a unit, and the cell the convex's centre is in, sorted.
    /// </summary>
    private static List<string> Convexes(BspData bsp)
    {
        List<string> convexes = [];
        IReadOnlyList<PhysCollideModel> models = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span);
        PhysCollideModel world = Assert.Single(models, m => m.ModelIndex == 0);
        (List<(int Index, int Contents)> statics, _, _) = LevelLinker.ParseKeyData(world.KeyText, "world");
        foreach ((int index, int contents) in statics)
        {
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(world.Solids[index])))
            {
                float[] lo = [float.MaxValue, float.MaxValue, float.MaxValue];
                float[] hi = [float.MinValue, float.MinValue, float.MinValue];
                for (int p = 0; p < ledge.PointCount; p++)
                {
                    (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, p);
                    float[] v = [x, y, z];
                    for (int a = 0; a < 3; a++)
                    {
                        lo[a] = Math.Min(lo[a], v[a]);
                        hi[a] = Math.Max(hi[a], v[a]);
                    }
                }

                int cx = (int)Math.Floor((lo[0] + hi[0]) / 2 / Rooms3x3Kit.CellSize);
                int cy = (int)Math.Floor((lo[1] + hi[1]) / 2 / Rooms3x3Kit.CellSize);

                // To a tenth of a unit, and with the sign of a zero dropped:
                // the metre-scaled points come back as -1e-8 as often as 0.
                string Tenth(float v) => (Math.Round(v, 1) + 0.0).ToString("0.0", CultureInfo.InvariantCulture);
                convexes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"0x{contents:x} ({Tenth(lo[0])} {Tenth(lo[1])} {Tenth(lo[2])})-({Tenth(hi[0])} {Tenth(hi[1])} {Tenth(hi[2])}) cell {cx} {cy}"));
            }
        }

        convexes.Sort(StringComparer.Ordinal);
        return convexes;
    }

    /// <summary>Every entity but the worldspawn: classname, origin and angles as numbers, sorted.</summary>
    private static List<string> Entities(BspData bsp)
    {
        List<string> entities = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string classname = entity.Get("classname") ?? string.Empty;
            if (classname == "worldspawn")
            {
                continue;
            }

            entities.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{classname} at {Numbers(entity.Get("origin"))} angles {Numbers(entity.Get("angles"))} light {entity.Get("_light")}"));
        }

        entities.Sort(StringComparer.Ordinal);
        return entities;
    }

    private static string Numbers(string? text) =>
        text is null
            ? "-"
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => float.Parse(t, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture)));
}
