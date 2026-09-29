//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The clearance grid on shapes whose free space is known in closed form,
/// and against the direct box sweep for many agent sizes: every voxel's
/// answer for every size equals testing the agent's swept box against the
/// brushes (a voxel of edge 16 at index i spans [16i, 16i + 16], so a box of
/// half-width r swept over it spans [16i − r, 16i + 16 + r] across and
/// [16k, 16k + 16 + h] up).
/// </summary>
public sealed class NavClearanceBuilderTests
{
    private const int Player = Nav3dFormat.PlayerSolidMask;

    private const int Npc = Nav3dFormat.NpcSolidMask;

    private static readonly NavRegion Cell = new(0, 0, 0, 16, 16, 16, 16);

    private static NavBrush Floor => NavBrush.Box(new Vec3(-64, -64, -64), new Vec3(320, 320, 16), 1);

    /// <summary>The agent sizes every exactness fact tries: the two defaults, tall, wide, tiny, odd, and one wider than most rooms.</summary>
    public static IReadOnlyList<(float Width, float Height, int Mask)> Agents { get; } =
    [
        (32, 72, Player), (32, 32, Npc), (32, 128, Player), (80, 72, Npc), (4, 4, Player), (0, 0, Npc), (24.5f, 50.25f, Player),
        (13, 100, Npc), (200, 16, Player),
    ];

    private static NavGrid Build(params NavBrush[] brushes) =>
        NavClearanceBuilder.Build(new NavGeometry { Brushes = brushes }, Cell, NavSettings.Default);

    private static bool Fits(NavGrid grid, int x, int y, int z, float width, float height, int mask = Player) =>
        NavSweep.GridFits(grid, [], x, y, z, width, height, mask);

    /// <summary>Every voxel, every agent: the grid's answer is the direct sweep's.</summary>
    private static void AssertExact(NavGeometry geometry, NavRegion region, NavSettings? settings = null)
    {
        NavGrid grid = NavClearanceBuilder.Build(geometry, region, settings ?? NavSettings.Default);
        IReadOnlyList<NavBrush> overhang = NavClearanceBuilder.OverhangBrushes(geometry);
        foreach ((float width, float height, int mask) in Agents)
        {
            bool[] direct = NavSweep.Classify(geometry.Brushes, region, width, height, mask);
            for (int z = 0; z < region.SizeZ; z++)
            {
                for (int y = 0; y < region.SizeY; y++)
                {
                    for (int x = 0; x < region.SizeX; x++)
                    {
                        bool fits = NavSweep.GridFits(grid, overhang, x, y, z, width, height, mask);
                        Assert.True(direct[region.Index(x, y, z)] == fits,
                            $"agent {width} x {height} mask 0x{mask:x}, voxel {x} {y} {z}: direct {direct[region.Index(x, y, z)]}, grid {fits}");
                    }
                }
            }
        }
    }

    /// <summary>A ramp rising at 45° along +x from x = 64 to 192, z = 16 to 144: <c>z ≤ x − 48</c>.</summary>
    private static NavBrush Ramp()
    {
        float r = MathF.Sqrt(0.5f);
        return NavBrush.FromPlanes(
        [
            (new Vec3(0, 0, -1), -16f),
            (new Vec3(1, 0, 0), 192f),
            (new Vec3(-r, 0, r), -48f * r),
            (new Vec3(0, 1, 0), 320f),
            (new Vec3(0, -1, 0), 64f),
        ], 1)!;
    }

    /// <summary>A slab whose underside slopes down to the east: <c>z ≥ 224 − (x − 64) / 2</c> over x from 64 to 192, up to 240.</summary>
    private static NavBrush SlopedCeiling()
    {
        float r = 1f / MathF.Sqrt(1.25f);
        return NavBrush.FromPlanes(
        [
            (new Vec3(0, 0, 1), 240f),
            (new Vec3(1, 0, 0), 192f),
            (new Vec3(-1, 0, 0), -64f),
            (new Vec3(0, 1, 0), 200f),
            (new Vec3(0, -1, 0), -40f),
            (new Vec3(-0.5f * r, 0, -r), -256f * r),
        ], 1)!;
    }

    [Fact]
    public void AnOpenFloorIsSolidInItsSlabAWalkableFloorOnTopAndOpenAbove()
    {
        NavGrid grid = Build(Floor);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.True(grid[x, y, 0].IsSolid);
                NavVoxelKey top = grid[x, y, 1];
                Assert.Equal(Nav3dLeafFlags.GroundedPlayer | Nav3dLeafFlags.GroundedNpc | Nav3dLeafFlags.WalkablePlayer | Nav3dLeafFlags.WalkableNpc, top.Flags);
                Assert.Equal((16f, 16f), (top.PlayerFloorZ, top.NpcFloorZ));
                Assert.Equal(256, top.Cost);
                for (int z = 2; z < 16; z++)
                {
                    Assert.Equal(Nav3dLeafFlags.None, grid[x, y, z].Flags);
                    Assert.True(Fits(grid, x, y, z, 1000, 1000));
                }
            }
        }

        // Nothing but the floor below: every record is empty, so one record serves every open voxel.
        Assert.Equal(2, grid.Records.Count);
        Assert.Equal(Nav3dClearance.Encode([], [], []), grid.Records[1]);
    }

    [Fact]
    public void APillarBlocksItsFootprintGrownByTheAgentsHalfWidthAndItsCornerIsTheChebyshevGap()
    {
        NavBrush pillar = NavBrush.Box(new Vec3(96, 96, 16), new Vec3(160, 160, 256), 1);
        NavGrid grid = Build(Floor, pillar);

        // Blocked for the standing agent where 16i − 16 < 160 and 16i + 32 > 96: i from 5 to 10, both ways.
        for (int z = 1; z < 16; z++)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    bool inside = x is >= 5 and <= 10 && y is >= 5 and <= 10;
                    Assert.Equal(!inside, Fits(grid, x, y, z, 32, 72));
                }
            }
        }

        // Voxel (4, 7, 1) is 96 − 80 = 16 from the pillar in x and inside its
        // y range: one corner, width 16, blocking at any height; voxel (2, 2, 1)
        // is 96 − 48 = 48 away on both axes.
        Assert.Equal([new Nav3dCorner(16, float.NegativeInfinity)], NavRecord.Decode(grid.Record(4, 7, 1, Nav3dClipClass.Player)).Corners);
        Assert.Equal([new Nav3dCorner(48, float.NegativeInfinity)], NavRecord.Decode(grid.Record(2, 2, 1, Nav3dClipClass.Player)).Corners);
        Assert.True(Fits(grid, 4, 7, 1, 32, 1000));
        Assert.False(Fits(grid, 4, 7, 1, 32.004f, 1));
    }

    [Fact]
    public void AStepIsSolidBelowItsTopAndAFloorAtItsTop()
    {
        NavBrush step = NavBrush.Box(new Vec3(128, -64, 16), new Vec3(320, 320, 48), 1);
        NavGrid grid = Build(Floor, step);
        for (int x = 8; x < 16; x++)
        {
            Assert.True(grid[x, 3, 2].IsSolid);
            Assert.Equal(48f, grid[x, 3, 3].PlayerFloorZ);
            Assert.True((grid[x, 3, 3].Flags & Nav3dLeafFlags.WalkablePlayer) != 0);
        }

        // Beside the step, a standing agent's box reaches it: blocked in the
        // two voxels whose swept box overlaps it, free over it.
        Assert.True(Fits(grid, 6, 3, 1, 0, 72));
        Assert.False(Fits(grid, 7, 3, 1, 32, 72));
        Assert.True(Fits(grid, 7, 3, 3, 32, 72));
        Assert.Equal(16f, grid[7, 3, 1].PlayerFloorZ);
    }

    [Fact]
    public void AWalkableSlopeIsExactForEverySizeAndAWalkableFloorUnderEveryColumnOverIt()
    {
        NavGeometry geometry = new() { Brushes = [Floor, Ramp()] };
        AssertExact(geometry, Cell);
        NavGrid grid = NavClearanceBuilder.Build(geometry, Cell, NavSettings.Default);

        // The lowest free voxel of a column over the ramp stands on it: a
        // walkable floor at the ramp's top over the voxel's footprint, the
        // footprint's far edge (x1 − 48).
        for (int x = 5; x <= 10; x++)
        {
            int z = Enumerable.Range(0, 16).First(k => !grid[x, 5, k].IsSolid && grid[x, 5, k].PlayerRecord != NavRecordTable.BlockedIndex);
            NavVoxelKey key = grid[x, 5, z];
            Assert.True((key.Flags & Nav3dLeafFlags.WalkablePlayer) != 0, $"column {x}");
            Assert.Equal(((x + 1) * 16) - 48, key.PlayerFloorZ, 2);
        }
    }

    [Fact]
    public void ASlopeSteeperThanTheLimitIsASteepFloor()
    {
        // The same 45° ramp, judged with a floor threshold of 0.8 (about 37°).
        NavGrid grid = NavClearanceBuilder.Build(new NavGeometry { Brushes = [Floor, Ramp()] }, Cell, NavSettings.Default with { FloorNormalZ = 0.8f });
        int z = Enumerable.Range(0, 16).First(k => !grid[7, 5, k].IsSolid);
        Assert.True((grid[7, 5, z].Flags & Nav3dLeafFlags.GroundedPlayer) != 0);
        Assert.True((grid[7, 5, z].Flags & Nav3dLeafFlags.WalkablePlayer) == 0);
    }

    /// <summary>
    /// Under a sloped ceiling a wider agent has less head room: no staircase
    /// states that, so the brush is listed and tested directly, a voxel with
    /// it is a leaf of its own, and every size is still exact.
    /// </summary>
    [Fact]
    public void AnOverhangIsListedAndTestedDirectlyAndStaysExact()
    {
        NavGeometry geometry = new() { Brushes = [Floor, SlopedCeiling()] };
        AssertExact(geometry, Cell);
        NavGrid grid = NavClearanceBuilder.Build(geometry, Cell, NavSettings.Default);
        NavRecord record = NavRecord.Decode(grid.Record(8, 8, 10, Nav3dClipClass.Player));
        Assert.Equal([0], record.Brushes);
        Assert.True(grid.Records.HasBrushes(grid[8, 8, 10].PlayerRecord));
        NavColumns columns = NavColumns.Of(grid);
        Assert.All(columns.Column(8, 8).ToArray().Where(r => grid.Records.HasBrushes(r.Key.PlayerRecord)), r => Assert.Equal(1, r.Height));

        // With nothing nearer, even a far voxel lists it; a full-height wall
        // between them covers its box's corner, and it is left out.
        Assert.Equal([0], NavRecord.Decode(grid.Record(0, 8, 3, Nav3dClipClass.Player)).Brushes);
        NavGrid walled = NavClearanceBuilder.Build(
            new NavGeometry { Brushes = [Floor, NavBrush.Box(new Vec3(16, -64, 16), new Vec3(32, 320, 256), 1), SlopedCeiling()] }, Cell, NavSettings.Default);
        Assert.Empty(NavRecord.Decode(walled.Record(0, 8, 3, Nav3dClipClass.Player)).Brushes);
    }

    [Fact]
    public void ACeilingGapLowerThanTheStandingAgentShutsItOutButLetsTheFlyerIn()
    {
        // 64 units between the floor's top (16) and the ceiling's bottom (80).
        NavBrush ceiling = NavBrush.Box(new Vec3(-64, -64, 80), new Vec3(320, 320, 320), 1);
        NavGrid grid = Build(Floor, ceiling);
        Assert.False(Fits(grid, 8, 8, 1, 32, 72));
        Assert.True(Fits(grid, 8, 8, 1, 32, 32, Npc));
        Assert.True(Fits(grid, 8, 8, 2, 32, 32, Npc));
        Assert.False(Fits(grid, 8, 8, 3, 32, 32, Npc));
        Assert.True(Fits(grid, 8, 8, 1, 32, 48));
        Assert.False(Fits(grid, 8, 8, 1, 32, 48.01f));
        Assert.Equal([new Nav3dCorner(float.NegativeInfinity, 80)], NavRecord.Decode(grid.Record(8, 8, 1, Nav3dClipClass.Player)).Corners);
        AssertExact(new NavGeometry { Brushes = [Floor, ceiling] }, Cell);
    }

    /// <summary>The two clip classes are two worlds: player clip is solid for the player class alone, and its top a floor for it alone.</summary>
    [Fact]
    public void PlayerClipIsSolidForThePlayerClassAndMonsterClipForTheNpcClass()
    {
        NavBrush playerClip = NavBrush.Box(new Vec3(96, 96, 16), new Vec3(160, 160, 64), 0x10000);
        NavBrush monsterClip = NavBrush.Box(new Vec3(192, 96, 16), new Vec3(240, 160, 256), 0x20000);
        NavGrid grid = Build(Floor, playerClip, monsterClip);
        Assert.False(Fits(grid, 8, 8, 2, 0, 0));
        Assert.True(Fits(grid, 8, 8, 2, 0, 0, Npc));
        Assert.Equal(64f, grid[8, 8, 4].PlayerFloorZ);
        Assert.True((grid[8, 8, 4].Flags & Nav3dLeafFlags.GroundedNpc) == 0);
        Assert.True(Fits(grid, 13, 8, 5, 0, 0));
        Assert.False(Fits(grid, 13, 8, 5, 0, 0, Npc));
        Assert.NotEqual(grid[8, 8, 2].PlayerRecord, grid[8, 8, 2].NpcRecord);
        Assert.Equal([new Nav3dCorner(48, float.NegativeInfinity)], NavRecord.Decode(grid.Record(2, 2, 2, Nav3dClipClass.Player)).Corners);
        Assert.Equal([new Nav3dCorner(144, float.NegativeInfinity)], NavRecord.Decode(grid.Record(2, 2, 2, Nav3dClipClass.Npc)).Corners);
        AssertExact(new NavGeometry { Brushes = [Floor, playerClip, monsterClip] }, Cell);
    }

    [Fact]
    public void WaterAndLadderBrushesAreFlagsAndCostsNotSolid()
    {
        NavBrush water = NavBrush.Box(new Vec3(0, 0, 16), new Vec3(128, 256, 56), 0x20);
        NavBrush ladder = NavBrush.Box(new Vec3(200, 100, 16), new Vec3(204, 132, 200), Nav3dFormat.LadderContents);
        NavGrid grid = NavClearanceBuilder.Build(
            new NavGeometry { Brushes = [Floor, water, ladder], Ladders = [new SourceSharp.MapTools.Rooms.Box(new Vec3(100, 100, 16), new Vec3(110, 110, 30))] },
            Cell, NavSettings.Default);
        Assert.Equal(Nav3dLeafFlags.Water, grid[3, 3, 2].Flags);
        Assert.Equal(512, grid[3, 3, 2].Cost);
        Assert.True(Fits(grid, 3, 3, 2, 32, 72));
        Assert.Equal(Nav3dLeafFlags.None, grid[3, 3, 4].Flags & Nav3dLeafFlags.Water);
        Assert.Equal(Nav3dLeafFlags.Ladder, grid[12, 7, 5].Flags);
        Assert.Equal(384, grid[12, 7, 5].Cost);
        Assert.True((grid[6, 6, 1].Flags & Nav3dLeafFlags.Ladder) != 0);
        Assert.Equal(768, grid[6, 6, 1].Cost);
        Assert.True((grid[6, 6, 1].Flags & Nav3dLeafFlags.Water) != 0);
        Assert.Equal(256, grid[8, 12, 8].Cost);
    }

    [Fact]
    public void ARegionsBottomVoxelFindsTheSolidUnderItDirectly()
    {
        NavRegion above = Cell with { OriginZ = 16 };
        NavGrid grid = Build(Floor);
        NavGrid lifted = NavClearanceBuilder.Build(new NavGeometry { Brushes = [Floor] }, above, NavSettings.Default);
        Assert.Equal(grid[5, 5, 1], lifted[5, 5, 0]);
        NavGrid floating = NavClearanceBuilder.Build(new NavGeometry { Brushes = [Floor] }, Cell with { OriginZ = 64 }, NavSettings.Default);
        Assert.Equal(Nav3dLeafFlags.None, floating[5, 5, 0].Flags);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BuildingTurnedBrushesEqualsTurningTheColumns(int turns)
    {
        NavBrush pillar = NavBrush.Box(new Vec3(40, 150, 16), new Vec3(90, 200, 256), 1);
        NavBrush clip = NavBrush.Box(new Vec3(150, 30, 16), new Vec3(170, 60, 100), 0x10000);
        NavBrush[] brushes = [Floor, pillar, clip];
        NavGrid grid = Build(brushes);
        NavGrid turned = Build([.. brushes.Select(b => b.Turned(turns, 256))]);
        for (int z = 0; z < 16; z++)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    (int tx, int ty) = RoomNav.TurnColumn(x, y, 16, turns);
                    NavVoxelKey a = grid[x, y, z];
                    NavVoxelKey b = turned[tx, ty, z];
                    Assert.Equal(grid.Records[a.PlayerRecord], turned.Records[b.PlayerRecord]);
                    Assert.Equal(grid.Records[a.NpcRecord], turned.Records[b.NpcRecord]);
                    Assert.Equal((a.Flags, a.Cost, a.PlayerFloorZ, a.NpcFloorZ), (b.Flags, b.Cost, b.PlayerFloorZ, b.NpcFloorZ));
                }
            }
        }
    }

    [Fact]
    public void ADynamicObstacleIsOpenSpaceWithItsOwnCornersPrunedByTheStaticOnes()
    {
        NavBrush wall = NavBrush.Box(new Vec3(64, 0, 16), new Vec3(80, 256, 256), 1);
        NavObstacleSource door = new("func_door", "cxry_gate", 7, Nav3dObstacleKind.Door, [NavBrush.Box(new Vec3(160, 96, 16), new Vec3(176, 160, 128), 1)]);
        NavGrid grid = NavClearanceBuilder.Build(new NavGeometry { Brushes = [Floor, wall], Obstacles = [door] }, Cell, NavSettings.Default);

        // Inside the door: free for every agent in the grid, with the door's (−∞, −∞) corner.
        Assert.True(Fits(grid, 10, 7, 2, 32, 72));
        Assert.Equal([new Nav3dDynamicCorner(0, new Nav3dCorner(float.NegativeInfinity, float.NegativeInfinity))], NavRecord.Decode(grid.Record(10, 7, 2, Nav3dClipClass.Player)).Dynamics);

        // Beside it, the door's corner is its gap; farther than the wall, the wall dominates it.
        Assert.Equal([new Nav3dDynamicCorner(0, new Nav3dCorner(16, float.NegativeInfinity))], NavRecord.Decode(grid.Record(12, 7, 2, Nav3dClipClass.Player)).Dynamics);
        Assert.Empty(NavRecord.Decode(grid.Record(5, 7, 2, Nav3dClipClass.Player)).Dynamics);

        // Above the door's top it is below the voxel: no corner.
        Assert.Empty(NavRecord.Decode(grid.Record(10, 7, 9, Nav3dClipClass.Player)).Dynamics);
    }

    [Fact]
    public void BrushesNoClassCollidesWithAreIgnoredAndCancellationIsObserved()
    {
        NavBrush trigger = NavBrush.Box(new Vec3(0, 0, 16), new Vec3(256, 256, 256), 0x40000000);
        NavGrid grid = Build(Floor, trigger);
        Assert.True(Fits(grid, 8, 8, 5, 32, 72));

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NavClearanceBuilder.Build(new NavGeometry { Brushes = [Floor] }, Cell, NavSettings.Default, cancelled.Token));
    }

    [Fact]
    public void AnEmptyOrZeroRegionIsRefused()
    {
        NavGeometry geometry = new() { Brushes = [Floor] };
        Assert.Throws<ArgumentOutOfRangeException>(() => NavClearanceBuilder.Build(geometry, Cell with { SizeX = 0 }, NavSettings.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavClearanceBuilder.Build(geometry, Cell with { VoxelSize = 0 }, NavSettings.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavClearanceBuilder.Build(geometry, Cell with { SizeZ = 200 }, NavSettings.Default));
    }

    /// <summary>A threshold is stored as the float at or below it: an agent the grid says fits, fits.</summary>
    [Fact]
    public void ThresholdsRoundDownToTheFloatAtOrBelow()
    {
        Assert.Equal(16f, NavClearanceBuilder.Down(16.0));
        float down = NavClearanceBuilder.Down(0.1);
        Assert.True(down <= 0.1 && MathF.BitIncrement(down) > 0.1);
        Assert.Equal(new Nav3dCorner(float.NegativeInfinity, 40), NavClearanceBuilder.Normalise(-1, 40, 32));
        Assert.Equal(new Nav3dCorner(3, float.NegativeInfinity), NavClearanceBuilder.Normalise(3, 20, 32));
        Nav3dCorner touching = NavClearanceBuilder.Normalise(-0.0005, 32, 32);
        Assert.True(touching.Width <= -0.0005 && MathF.BitIncrement(touching.Width) > -0.0005);
        Assert.Equal(32f, touching.Top);
    }
}
