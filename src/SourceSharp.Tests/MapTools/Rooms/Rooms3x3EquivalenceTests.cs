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
using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using Bounds = SourceSharp.MapGen.Catalog.Bounds;
using KitPoint = SourceSharp.MapGen.Point;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3x3 rooms sample, rearranged: for every arrangement of the default
/// subset (<see cref="Rooms3x3Permutations.DefaultCases"/>), the level linked
/// from the compiled rooms is checked against the same level compiled whole
/// from its monolithic VMF by the ordinary vbsp path.
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
/// player-passable space, as the layout's joints say, in both maps.</item>
/// <item><b>Visibility</b>: the linked PVS is exactly the door-graph replay,
/// and a superset of what the real vvis sees in the monolithic map.</item>
/// <item><b>Collision</b>: the world collision holds the same convexes, in the
/// same contents classes, at the same places, and every room has some.</item>
/// <item><b>Faces</b>: every plane draws the same area in the same material,
/// except the doorway surfaces of jointed sockets, which the linked map lacks
/// by exactly the documented amount.</item>
/// <item><b>Entities</b>: the same entities at the same places, facing the
/// same way.</item>
/// </list>
/// <para>
/// The default subset is 17 arrangements; each pair is built once and shared
/// by every fact (<see cref="Rooms3x3Fixture"/>). The same checks run over
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
public sealed class Rooms3x3EquivalenceTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
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

    /// <summary>The default subset is the documented 17, all valid, and the 4 turns come first.</summary>
    [Fact]
    public void TheDefaultSubsetIsSeventeenValidArrangements()
    {
        Assert.Equal(17, Rooms3x3Fixture.Cases.Count);
        Assert.All(Rooms3x3Fixture.Cases, c => Assert.True(c.Arrangement.IsValid(), c.Name));
        Assert.Equal(
            ["rooms3x3", "rooms3x3_turn1", "rooms3x3_turn2", "rooms3x3_turn3"],
            Rooms3x3Fixture.Cases.Take(4).Select(c => c.Name));
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
        List<Rooms3x3Cell> cells = [.. Rooms3x3Permutations.Canonical.Cells];
        Assert.Equal("cross", cells[4].Kind);
        cells[4] = cells[4] with { Rotation = (cells[4].Rotation + 1) % 4 };
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
        TheLinkedPvsIsTheDoorGraphAndCoversTheMonolithicPvsCheck(pair, library);
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

        // Empty, solid and grate leaves are what a point can report here; the
        // clip and detail brushes leave their leaves empty and are met by
        // traces instead (KitFeaturesStopTheMasksTheyShould).
        Assert.Equal([0, (int)BrushContents.Solid, (int)(BrushContents.Grate | BrushContents.Translucent)], kinds.Order());
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
        for (int y = 0; y < Rooms3x3Arrangement.Size; y++)
        {
            for (int x = 0; x < Rooms3x3Arrangement.Size; x++)
            {
                RoomKind kind = Rooms3x3Kit.Kind(arrangement[x, y].Kind);
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

        Assert.Equal(expected.Keys.Order(), seen.Order());
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
        for (int y = 0; y < Rooms3x3Arrangement.Size; y++)
        {
            for (int x = 0; x < Rooms3x3Arrangement.Size; x++)
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

        // A lined-up 3x3 level that connects nine rooms has at least eight joints,
        // counted once from each side.
        Assert.True(open >= 16, $"{name}: {open} jointed socket(s)");
        Assert.Equal(arrangement.Cells.Sum(c => Rooms3x3Kit.Kind(c.Kind).Sockets.Count), open + capped);
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
        List<Vec3> eyes = [.. RoomEyes()];
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
    /// in both maps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRoomReachesExactlyTheRoomsItsDoorsJoin(string name) =>
        EveryRoomReachesExactlyTheRoomsItsDoorsJoinCheck(await fixture.PairAsync(name));

    /// <summary>The check behind <see cref="EveryRoomReachesExactlyTheRoomsItsDoorsJoin"/>, for any pair.</summary>
    internal static void EveryRoomReachesExactlyTheRoomsItsDoorsJoinCheck(Rooms3x3Pair pair)
    {
        string name = pair.Case.Name;
        int[] expected = pair.Case.Arrangement.Components();

        foreach (LevelProbe probe in new[] { pair.LinkedProbe, pair.MonolithicProbe })
        {
            int[] component = Flood(probe);
            List<Vec3> eyes = [.. RoomEyes()];
            for (int a = 0; a < eyes.Count; a++)
            {
                for (int b = 0; b < eyes.Count; b++)
                {
                    int ca = component[Index(eyes[a])];
                    int cb = component[Index(eyes[b])];
                    Assert.True(ca >= 0, $"{name}: room {a}'s centre is not open");
                    Assert.True(
                        (ca == cb) == (expected[a] == expected[b]),
                        $"{name}: rooms {a} and {b} reach each other: {ca == cb}; the joints say {expected[a] == expected[b]}");
                }
            }
        }
    }

    // ---- 5. visibility -----------------------------------------------------------

    /// <summary>
    /// The linked PVS is exactly the door-graph replay of the rooms' own
    /// rows and the layout's joints, and it is a superset of what the real
    /// vvis sees in the monolithic map: every pair of monolithic clusters
    /// that see each other maps to linked clusters that do too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheLinkedPvsIsTheDoorGraphAndCoversTheMonolithicPvs(string name) =>
        TheLinkedPvsIsTheDoorGraphAndCoversTheMonolithicPvsCheck(await fixture.PairAsync(name), fixture.Library);

    /// <summary>The check behind <see cref="TheLinkedPvsIsTheDoorGraphAndCoversTheMonolithicPvs"/>, for any pair.</summary>
    internal static void TheLinkedPvsIsTheDoorGraphAndCoversTheMonolithicPvsCheck(Rooms3x3Pair pair, RoomLibrary library)
    {
        string name = pair.Case.Name;
        DoorGraphFacts.AssertDoorGraph(pair.Linked, pair.Layout, library);

        // Each monolithic open cluster, by the linked clusters of sample points
        // inside its own leaves: the centre and eight points a third of the
        // way to each corner, kept only where the monolithic walk agrees the
        // point is in that leaf.
        Dictionary<int, HashSet<int>> map = [];
        IReadOnlyList<DLeaf> leafs = pair.MonolithicProbe.Leafs;
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.Cluster < 0 || (leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            Vec3 lo = new(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]);
            Vec3 hi = new(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]);
            Vec3 mid = (lo + hi) * 0.5f;
            foreach (Vec3 corner in Corners(lo, hi).Prepend(mid))
            {
                Vec3 sample = mid + ((corner - mid) * (2f / 3f));
                if (pair.MonolithicProbe.Leaf(sample) != l)
                {
                    continue;
                }

                int linkedCluster = pair.LinkedProbe.Leafs[pair.LinkedProbe.Leaf(sample)].Cluster;
                if (linkedCluster >= 0)
                {
                    (map.TryGetValue(leaf.Cluster, out HashSet<int>? set) ? set : map[leaf.Cluster] = []).Add(linkedCluster);
                }
            }
        }

        Assert.NotEmpty(map);
        int pairs = 0;
        foreach ((int from, HashSet<int> fromLinked) in map)
        {
            foreach ((int to, HashSet<int> toLinked) in map)
            {
                if (!pair.MonolithicVis.CanSee(from, to))
                {
                    continue;
                }

                foreach (int a in fromLinked)
                {
                    foreach (int b in toLinked)
                    {
                        Assert.True(pair.Linked.Vis.CanSee(a, b), $"{name}: the monolithic map sees {from}->{to}, the linked {a}->{b} does not");
                        pairs++;
                    }
                }
            }
        }

        Assert.True(pairs > 0);
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

        for (int y = 0; y < Rooms3x3Arrangement.Size; y++)
        {
            for (int x = 0; x < Rooms3x3Arrangement.Size; x++)
            {
                string cell = string.Create(CultureInfo.InvariantCulture, $"cell {x} {y}");
                Assert.Contains(linked, c => c.EndsWith(cell, StringComparison.Ordinal));
            }
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
        Assert.Equal(10, whole.Count); // nine lights and the one player start
        Assert.Equal(whole, linked);
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

    /// <summary>The centre of each room at eye height, on the lattice, in row order.</summary>
    private static IEnumerable<Vec3> RoomEyes()
    {
        for (int y = 0; y < Rooms3x3Arrangement.Size; y++)
        {
            for (int x = 0; x < Rooms3x3Arrangement.Size; x++)
            {
                // 132 is the lattice point nearest the middle of a 256 cell.
                yield return new Vec3((x * Rooms3x3Kit.CellSize) + 132, (y * Rooms3x3Kit.CellSize) + 132, 84);
            }
        }
    }

    /// <summary>Components of the lattice's player-passable points, 6-connected; -1 where blocked.</summary>
    private static int[] Flood(LevelProbe probe)
    {
        int[] component = new int[Across * Across * Up];
        for (int k = 0; k < Up; k++)
        {
            for (int j = 0; j < Across; j++)
            {
                for (int i = 0; i < Across; i++)
                {
                    int index = i + (Across * j) + (Across * Across * k);
                    component[index] = (probe.Contents(At(i, j, k)) & MaskPlayerSolid) == 0 ? int.MaxValue : -1;
                }
            }
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
                    KitSide world = Rooms3x3Kit.Turn(mine, arrangement[x, y].Rotation);
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
