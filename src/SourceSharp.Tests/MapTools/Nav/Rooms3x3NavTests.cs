//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The navigation of the 3x3 sample's levels, every default case: stitched
/// from the rooms' precomputed navigation, it equals, run for run and record
/// for record, the grid built straight from the flattened level's
/// whole-map compile; every agent size fits exactly where a direct box sweep
/// of that compile says it does; its components are those version 1's
/// per-voxel face adjacency finds; and it reaches every room the level's own
/// reachability rule says it does.
/// </summary>
public sealed class Rooms3x3NavTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    public static TheoryData<string> Cases => Rooms3x3Fixture.CaseNames;

    private const int Standing = 0;

    private const int Flyer = 1;

    /// <summary>
    /// The sizes the exactness fact checks: the two default presets, and a
    /// tall, a wide and a tiny box no preset names, each for the class it
    /// would most likely be.
    /// </summary>
    public static IReadOnlyList<(string Name, float Width, float Height, Nav3dClipClass ClipClass)> Sizes =>
    [
        ("standing", 32, 72, Nav3dClipClass.Player),
        ("flyer", 32, 32, Nav3dClipClass.Npc),
        ("tall", 24, 150, Nav3dClipClass.Npc),
        ("wide", 90, 40, Nav3dClipClass.Npc),
        ("tiny", 6, 10, Nav3dClipClass.Player),
    ];

    /// <summary>Every library room's navigation at turn 0, built from the fixture's compiles.</summary>
    internal static Dictionary<string, RoomNav> RoomNavs(RoomLibrary library)
    {
        Dictionary<string, RoomNav> navs = new(StringComparer.Ordinal);
        foreach (RoomObject room in library.Rooms)
        {
            navs[room.Definition.Name] = RoomNavBuilder.Build(room.Definition, room.Bsp, [], RoomRole.None, NavSettings.Default);
        }

        return navs;
    }

    internal static Nav3dReader Stitch(LevelLayout layout, int columns, int rows, Dictionary<string, RoomNav> navs) =>
        Nav3dReader.Open(Nav3dWriter.Write(LevelNavLinker.Link(
            layout, columns, rows, (room, turn) => navs[room].Turned(turn), null, Guid.Empty)));

    /// <summary>A placed cell's voxels in level coordinates.</summary>
    private static NavRegion CellRegion(Nav3dReader nav, RoomInstance room) => new(
        nav.Origin.X + (room.Placement.CellX * nav.CellSize), nav.Origin.Y + (room.Placement.CellY * nav.CellSize), nav.Origin.Z,
        nav.CellVoxels, nav.CellVoxels, nav.CellVoxels, nav.VoxelSize);

    private static int CellOf(Rooms3x3Pair pair, RoomInstance room) => (room.Placement.CellY * pair.Level.Columns) + room.Placement.CellX;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPlacedRoomIsReachableForTheStandingPresetAsTheReachabilityRuleSays(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);

        // The level linked, so rule 6 held: the joints connect every room.
        RoomLinter.CheckReachable(pair.Layout, n => fixture.Library.Get(n).Definition);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        NavAgentStats stats = NavInspector.Stats(nav, Standing);
        Assert.Equal(pair.Layout.Rooms.Count, stats.RoomsInLargestComponent);

        // The rooms meet through doors: some leaf has a neighbour in another cell.
        int throughDoor = 0;
        for (int l = 0; l < nav.LeafCount; l++)
        {
            foreach (Nav3dNeighbour n in nav.Neighbours(l))
            {
                throughDoor += n.ThroughDoor ? 1 : 0;
            }
        }

        Assert.True(throughDoor > 0);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheFlyerReachesEverythingTheWalkerDoes(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        Nav3dPreset walker = nav.Preset(Standing);
        Nav3dPreset flyer = nav.Preset(Flyer);

        // Every voxel the walker fits in the flyer fits in, and the walker's
        // components each fall inside one flyer component.
        Dictionary<int, int> flyerComponentOf = [];
        for (int l = 0; l < nav.LeafCount; l++)
        {
            Nav3dLeaf leaf = nav.Leaf(l);
            for (int z = leaf.ZLo; z <= leaf.ZHi; z++)
            {
                if (!nav.Passable(l, z, walker.Width, walker.Height, walker.ClipClass))
                {
                    continue;
                }

                Assert.True(nav.Passable(l, z, flyer.Width, flyer.Height, flyer.ClipClass), $"leaf {l} voxel {z} is free for the walker only");
                int component = nav.Component(Standing, l);
                int flyerComponent = nav.Component(Flyer, l);
                Assert.Equal(flyerComponentOf.TryAdd(component, flyerComponent) ? flyerComponent : flyerComponentOf[component], flyerComponent);
            }
        }

        Assert.Equal(pair.Layout.Rooms.Count, NavInspector.Stats(nav, Flyer).RoomsInLargestComponent);
    }

    /// <summary>
    /// The stitched grid is the flattened level's: in every placed cell, every
    /// column's runs have the same bounds, flags, cost and floors, and each
    /// run's two clearance records are the same corners, the same dynamic
    /// corners (obstacles matched by class and bounds) and the same
    /// overhanging brushes (matched by planes), as the grid the builder makes
    /// from the whole-map compile of the same level. That is voxel for voxel,
    /// clearance included, because the runs partition the free voxels.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheStitchedGridEqualsTheFlattenedLevelsGridRunForRunAndRecordForRecord(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        Nav3dLevel stitched = nav.ToLevel();
        NavGeometry whole = NavGeometry.FromBsp(pair.Monolithic.Bsp!);
        IReadOnlyList<NavBrush> overhang = NavClearanceBuilder.OverhangBrushes(whole);
        int n = nav.CellVoxels;
        int runs = 0;
        foreach (RoomInstance room in pair.Layout.Rooms)
        {
            int cell = CellOf(pair, room);
            NavGrid grid = NavClearanceBuilder.Build(whole, CellRegion(nav, room), NavSettings.Default);
            NavColumns columns = NavColumns.Of(grid);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    string where = string.Create(CultureInfo.InvariantCulture, $"{name}, {room.Placement.Room} at ({room.Placement.CellX}, {room.Placement.CellY}), column {x} {y}");
                    List<string> expected = [];
                    foreach (NavRun run in columns.Column(x, y))
                    {
                        expected.Add(Describe(run.ZLo, run.Height, run.Key.Flags, run.Key.Cost, run.Key.PlayerFloorZ, run.Key.NpcFloorZ,
                            Record(grid.Records[run.Key.PlayerRecord], i => ObstacleKey(whole.Obstacles[i]), b => overhang[b].PlaneFloats()),
                            Record(grid.Records[run.Key.NpcRecord], i => ObstacleKey(whole.Obstacles[i]), b => overhang[b].PlaneFloats())));
                    }

                    List<string> actual = [];
                    int last = -1;
                    for (int z = 0; z < n; z++)
                    {
                        int l = nav.FindLeaf(cell, x, y, z);
                        if (l < 0 || l == last)
                        {
                            continue;
                        }

                        last = l;
                        Nav3dLeaf leaf = nav.Leaf(l);
                        actual.Add(Describe(leaf.ZLo, leaf.Height, leaf.Flags, leaf.Cost, leaf.PlayerFloorZ, leaf.NpcFloorZ,
                            Record(nav.ClearanceRecord(l, Nav3dClipClass.Player), i => ObstacleKey(nav.Obstacle(i)), b => stitched.Brushes[b]),
                            Record(nav.ClearanceRecord(l, Nav3dClipClass.Npc), i => ObstacleKey(nav.Obstacle(i)), b => stitched.Brushes[b])));
                    }

                    Assert.True(expected.SequenceEqual(actual),
                        $"{where}:\n  whole-map {string.Join("\n            ", expected)}\n  stitched  {string.Join("\n            ", actual)}");
                    runs += actual.Count;
                }
            }
        }

        // Every leaf of the level was one of the runs compared.
        Assert.Equal(nav.LeafCount, runs);
    }

    /// <summary>
    /// Any box, at any voxel of any placed cell, fits exactly where a direct
    /// sweep of that box against the flattened level's world brushes of its
    /// class says it does: the presets, and sizes no preset names.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryAgentSizeFitsExactlyWhereADirectSweepOfTheFlattenedLevelSaysItDoes(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        NavGeometry whole = NavGeometry.FromBsp(pair.Monolithic.Bsp!);
        int n = nav.CellVoxels;
        foreach ((string size, float width, float height, Nav3dClipClass clipClass) in Sizes)
        {
            int fits = 0;
            foreach (RoomInstance room in pair.Layout.Rooms)
            {
                int cell = CellOf(pair, room);
                NavRegion region = CellRegion(nav, room);
                bool[] free = NavSweep.Classify(whole.Brushes, region, width, height, Nav3dFormat.SolidMask(clipClass));
                for (int z = 0; z < n; z++)
                {
                    for (int y = 0; y < n; y++)
                    {
                        for (int x = 0; x < n; x++)
                        {
                            int l = nav.FindLeaf(cell, x, y, z);
                            bool actual = l >= 0 && nav.Passable(l, z, width, height, clipClass);
                            bool expected = free[region.Index(x, y, z)];
                            Assert.True(expected == actual,
                                $"{name}, {size} {width} x {height}, {room.Placement.Room} at ({room.Placement.CellX}, {room.Placement.CellY}), voxel {x} {y} {z}: sweep {expected}, grid {actual}");
                            fits += actual ? 1 : 0;
                        }
                    }
                }
            }

            Assert.True(fits > 0, $"{name}: {size} fits nowhere");
        }
    }

    /// <summary>
    /// The components the reader derives from runs are the ones version 1's
    /// logic finds: the direct sweep's free voxels of each preset, joined
    /// face to face, across cells wherever two placed cells' free voxels
    /// touch (which the kit allows only in a joined doorway). The two
    /// partitions of the free voxels are the same, component for component.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ComponentsAreVersionOnesFaceAdjacencyOfTheSweptVoxels(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        NavGeometry whole = NavGeometry.FromBsp(pair.Monolithic.Bsp!);
        int n = nav.CellVoxels;
        int sx = pair.Level.Columns * n;
        int sy = pair.Level.Rows * n;
        NavRegion level = new(nav.Origin.X, nav.Origin.Y, nav.Origin.Z, sx, sy, n, nav.VoxelSize);
        bool[] placed = new bool[pair.Level.Columns * pair.Level.Rows];
        foreach (RoomInstance room in pair.Layout.Rooms)
        {
            placed[CellOf(pair, room)] = true;
        }

        for (int p = 0; p < nav.PresetCount; p++)
        {
            Nav3dPreset preset = nav.Preset(p);
            bool[] free = NavSweep.Classify(whole.Brushes, level, preset.Width, preset.Height, Nav3dFormat.SolidMask(preset.ClipClass));
            bool Free(int x, int y, int z) =>
                x >= 0 && y >= 0 && z >= 0 && x < sx && y < sy && z < n && placed[((y / n) * pair.Level.Columns) + (x / n)]
                && free[level.Index(x, y, z)];

            int[] parent = new int[free.Length];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }

                return i;
            }

            for (int z = 0; z < n; z++)
            {
                for (int y = 0; y < sy; y++)
                {
                    for (int x = 0; x < sx; x++)
                    {
                        if (!Free(x, y, z))
                        {
                            continue;
                        }

                        foreach ((int dx, int dy, int dz) in new[] { (1, 0, 0), (0, 1, 0), (0, 0, 1) })
                        {
                            if (Free(x + dx, y + dy, z + dz))
                            {
                                int a = Find(level.Index(x, y, z));
                                int b = Find(level.Index(x + dx, y + dy, z + dz));
                                parent[Math.Max(a, b)] = Math.Min(a, b);
                            }
                        }
                    }
                }
            }

            // The partitions agree when the map each way is a function.
            Dictionary<int, int> v2OfV1 = [];
            Dictionary<int, int> v1OfV2 = [];
            int voxels = 0;
            for (int z = 0; z < n; z++)
            {
                for (int y = 0; y < sy; y++)
                {
                    for (int x = 0; x < sx; x++)
                    {
                        if (!placed[((y / n) * pair.Level.Columns) + (x / n)])
                        {
                            continue;
                        }

                        int cell = ((y / n) * pair.Level.Columns) + (x / n);
                        int l = nav.FindLeaf(cell, x % n, y % n, z);
                        bool inGrid = l >= 0 && nav.Passable(l, z, preset.Width, preset.Height, preset.ClipClass);
                        Assert.Equal(Free(x, y, z), inGrid);
                        if (!inGrid)
                        {
                            continue;
                        }

                        int v1 = Find(level.Index(x, y, z));
                        int v2 = nav.Component(p, l);
                        Assert.True(v2OfV1.TryAdd(v1, v2) || v2OfV1[v1] == v2, $"{name}, preset {p}, voxel {x} {y} {z}: one version 1 component is two here");
                        Assert.True(v1OfV2.TryAdd(v2, v1) || v1OfV2[v2] == v1, $"{name}, preset {p}, voxel {x} {y} {z}: two version 1 components are one here");
                        voxels++;
                    }
                }
            }

            Assert.Equal(nav.ComponentCount(p), v1OfV2.Count);
            Assert.True(voxels > 0);
        }
    }

    private static string ObstacleKey(NavObstacleSource o) =>
        string.Create(CultureInfo.InvariantCulture, $"{o.ClassName} {o.Bounds.Mins} {o.Bounds.Maxs}");

    private static string ObstacleKey(Nav3dObstacle o) =>
        string.Create(CultureInfo.InvariantCulture, $"{o.ClassName} {o.Mins} {o.Maxs}");

    private static string Describe(int zLo, int height, Nav3dLeafFlags flags, ushort cost, float playerFloor, float npcFloor, string player, string npc) =>
        string.Create(CultureInfo.InvariantCulture, $"z {zLo}+{height} {flags} cost {cost} floors {playerFloor:R}/{npcFloor:R} player {player} npc {npc}");

    /// <summary>A record as text with its obstacles and brushes by identity, not index: the two grids number them differently.</summary>
    private static string Record(ReadOnlySpan<byte> bytes, Func<int, string> obstacle, Func<int, float[]> brush)
    {
        NavRecord record = NavRecord.Decode(bytes);
        IEnumerable<string> corners = record.Corners.Select(c => string.Create(CultureInfo.InvariantCulture, $"({c.Width:R} {c.Top:R})"));
        IEnumerable<string> dynamics = record.Dynamics
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"[{obstacle(d.Obstacle)} ({d.Corner.Width:R} {d.Corner.Top:R})]"))
            .Order(StringComparer.Ordinal);
        IEnumerable<string> brushes = record.Brushes
            .Select(b => "{" + string.Join(" ", brush(b).Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) + "}")
            .Order(StringComparer.Ordinal);
        return string.Join(" ", corners.Concat(dynamics).Concat(brushes));
    }
}
