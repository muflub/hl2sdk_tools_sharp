//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The navigation of the 3x3 sample's levels: stitched from the rooms'
/// precomputed navigation, it reaches every room the level's own
/// reachability rule says it does, the flyer reaches everything the walker
/// does, and it matches, voxel for voxel, the navigation voxelised straight
/// from the flattened level's whole-map compile.
/// </summary>
public sealed class Rooms3x3NavTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    public static TheoryData<string> Cases => Rooms3x3Fixture.CaseNames;

    private const int Standing = 0;

    private const int Flyer = 1;

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

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPlacedRoomIsReachableForTheStandingHullAsTheReachabilityRuleSays(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);

        // The level linked, so rule 6 held: the joints connect every room.
        RoomLinter.CheckReachable(pair.Layout, n => fixture.Library.Get(n).Definition);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        NavAgentStats stats = NavInspector.Stats(nav, Standing);
        Assert.Equal(pair.Layout.Rooms.Count, stats.RoomsInLargestComponent);
        Assert.True(stats.DoorLinks > 0);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheFlyerReachesEverythingTheWalkerDoes(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));

        // Every voxel free for the walker is free for the flyer, and the
        // walker's components each fall inside one flyer component.
        Dictionary<uint, uint> flyerComponentOf = [];
        for (int cell = 0; cell < nav.CellCount; cell++)
        {
            for (int z = 0; z < nav.CellVoxels; z++)
            {
                for (int y = 0; y < nav.CellVoxels; y++)
                {
                    for (int x = 0; x < nav.CellVoxels; x++)
                    {
                        int walker = nav.FindLeaf(Standing, cell, x, y, z);
                        if (walker < 0)
                        {
                            continue;
                        }

                        int flyer = nav.FindLeaf(Flyer, cell, x, y, z);
                        Assert.True(flyer >= 0, $"cell {cell} voxel {x} {y} {z} is free for the walker only");
                        uint component = nav.Leaf(Standing, walker).Component;
                        uint flyerComponent = nav.Leaf(Flyer, flyer).Component;
                        Assert.Equal(flyerComponentOf.TryAdd(component, flyerComponent) ? flyerComponent : flyerComponentOf[component], flyerComponent);
                    }
                }
            }
        }

        Assert.Equal(pair.Layout.Rooms.Count, NavInspector.Stats(nav, Flyer).RoomsInLargestComponent);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheStitchedNavigationMatchesVoxelisingTheFlattenedLevel(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        Nav3dReader nav = Stitch(pair.Layout, pair.Level.Columns, pair.Level.Rows, RoomNavs(fixture.Library));
        List<NavBrush> brushes = NavBrush.FromBsp(pair.Monolithic.Bsp!);
        NavSettings settings = NavSettings.Default;
        int n = nav.CellVoxels;
        NavRegion level = new(0, 0, 0, pair.Level.Columns * n, pair.Level.Rows * n, n, settings.VoxelSize);
        for (int a = 0; a < settings.Agents.Count; a++)
        {
            NavVoxelGrid whole = NavVoxeliser.Classify(brushes, level, settings.Agents[a], settings.FloorNormalZ);
            int compared = 0;
            foreach (RoomInstance room in pair.Layout.Rooms)
            {
                int cell = (room.Placement.CellY * pair.Level.Columns) + room.Placement.CellX;
                for (int z = 0; z < n; z++)
                {
                    for (int y = 0; y < n; y++)
                    {
                        for (int x = 0; x < n; x++)
                        {
                            ushort expected = whole[(room.Placement.CellX * n) + x, (room.Placement.CellY * n) + y, z];
                            int leaf = nav.FindLeaf(a, cell, x, y, z);
                            ushort actual = leaf < 0
                                ? NavVoxelGrid.Blocked
                                : (ushort)(NavVoxelGrid.FreeBit | (ushort)(nav.Leaf(a, leaf).Flags & ~Nav3dLeafFlags.Door));
                            Assert.True(expected == actual,
                                $"{name}, agent {a}, room {room.Placement.Room} at ({room.Placement.CellX}, {room.Placement.CellY}),"
                                + $" voxel {x} {y} {z}: whole-map 0x{expected:x}, stitched 0x{actual:x}");
                            compared++;
                        }
                    }
                }
            }

            Assert.Equal(pair.Layout.Rooms.Count * n * n * n, compared);
        }
    }
}
