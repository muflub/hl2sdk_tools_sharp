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
/// The voxeliser on shapes whose free space is known in closed form: every
/// voxel's class and flags are asserted against the Minkowski rule worked by
/// hand (a voxel of edge 16 at index i spans [16i, 16i + 16], so the
/// standing agent swept over it spans [16i − 16, 16i + 32] across and
/// [16k, 16k + 88] up).
/// </summary>
public sealed class NavVoxeliserTests
{
    internal static readonly NavAgentSpec Standing = new("standing", 32, 72, Nav3dFormat.PlayerSolidMask);

    internal static readonly NavAgentSpec Flyer = new("flyer", 32, 32, Nav3dFormat.NpcSolidMask);

    private static readonly NavRegion Cell = new(0, 0, 0, 16, 16, 16, 16);

    private static NavBrush Floor => NavBrush.Box(new Vec3(-64, -64, -64), new Vec3(320, 320, 16), 1);

    private static bool Free(NavVoxelGrid grid, int x, int y, int z) => (grid[x, y, z] & NavVoxelGrid.FreeBit) != 0;

    private static Nav3dLeafFlags Flags(NavVoxelGrid grid, int x, int y, int z) => (Nav3dLeafFlags)(grid[x, y, z] & 0xFF);

    [Fact]
    public void AnOpenFloorIsBlockedInItsSlabAndAFloorOnTopAndOpenAbove()
    {
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor], Cell, Standing, 0.7f);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.False(Free(grid, x, y, 0));
                Assert.Equal(Nav3dLeafFlags.Floor, Flags(grid, x, y, 1));
                for (int z = 2; z < 16; z++)
                {
                    Assert.Equal(Nav3dLeafFlags.None, Flags(grid, x, y, z));
                    Assert.True(Free(grid, x, y, z));
                }
            }
        }
    }

    [Fact]
    public void APillarBlocksItsFootprintGrownByTheAgentsHalfWidthAndWallsItsSides()
    {
        NavBrush pillar = NavBrush.Box(new Vec3(96, 96, 16), new Vec3(160, 160, 256), 1);
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor, pillar], Cell, Standing, 0.7f);

        // Blocked where 16i − 16 < 160 and 16i + 32 > 96: i from 5 to 10, both ways.
        for (int z = 1; z < 16; z++)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    bool inside = x is >= 5 and <= 10 && y is >= 5 and <= 10;
                    Assert.Equal(!inside, Free(grid, x, y, z));
                }
            }
        }

        Assert.Equal(Nav3dLeafFlags.Floor | Nav3dLeafFlags.Wall | Nav3dLeafFlags.SidePositiveX, Flags(grid, 4, 7, 1));
        Assert.Equal(Nav3dLeafFlags.Wall | Nav3dLeafFlags.SideNegativeY, Flags(grid, 7, 11, 5));
        Assert.Equal(Nav3dLeafFlags.None, Flags(grid, 4, 4, 5));
    }

    [Fact]
    public void AStepIsAWallFromBelowAndAFloorOnTop()
    {
        NavBrush step = NavBrush.Box(new Vec3(128, -64, 16), new Vec3(320, 320, 48), 1);
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor, step], Cell, Standing, 0.7f);

        // Over the step (16i + 32 > 128: i from 7), blocked below k = 3 (16k < 48).
        for (int x = 7; x < 16; x++)
        {
            Assert.False(Free(grid, x, 3, 2));
            Assert.Equal(Nav3dLeafFlags.Floor, Flags(grid, x, 3, 3));
        }

        Assert.Equal(Nav3dLeafFlags.Floor | Nav3dLeafFlags.Wall | Nav3dLeafFlags.SidePositiveX, Flags(grid, 6, 3, 1));
        Assert.Equal(Nav3dLeafFlags.Wall | Nav3dLeafFlags.SidePositiveX, Flags(grid, 6, 3, 2));
        Assert.Equal(Nav3dLeafFlags.None, Flags(grid, 6, 3, 3));
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

    [Fact]
    public void AWalkableSlopeIsBlockedExactlyUnderItsPlaneAndAFloorOnIt()
    {
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor, Ramp()], Cell, Standing, 0.7f);

        // The swept box clears the slope when 16k − (16i + 32) ≥ −48, that is k ≥ i − 1;
        // it clears the ramp's bounds when i ≤ 2, i ≥ 13 or k ≥ 9.
        for (int x = 0; x < 16; x++)
        {
            for (int z = 1; z < 16; z++)
            {
                bool blocked = x is >= 3 and <= 12 && z < x - 1 && z < 9;
                Assert.True(blocked != Free(grid, x, 5, z), $"voxel {x} 5 {z}");
            }
        }

        // The lowest free voxel over the ramp stands on it: a floor, not a wall.
        for (int x = 4; x <= 10; x++)
        {
            Nav3dLeafFlags flags = Flags(grid, x, 5, x - 1);
            Assert.True((flags & Nav3dLeafFlags.Floor) != 0, $"voxel {x} 5 {x - 1} is {flags}");
            Assert.True((flags & Nav3dLeafFlags.Wall) == 0, $"voxel {x} 5 {x - 1} is {flags}");
        }
    }

    [Fact]
    public void ASlopeSteeperThanTheLimitIsAWall()
    {
        // The same 45° ramp, judged with a floor threshold of 0.8 (about 37°).
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor, Ramp()], Cell, Standing, 0.8f);
        Nav3dLeafFlags flags = Flags(grid, 6, 5, 5);
        Assert.True((flags & Nav3dLeafFlags.Wall) != 0, flags.ToString());
        Assert.True((flags & Nav3dLeafFlags.Floor) == 0, flags.ToString());
    }

    [Fact]
    public void ACeilingGapLowerThanTheStandingAgentShutsItOutButLetsTheFlyerIn()
    {
        // 64 units between the floor's top (16) and the ceiling's bottom (80).
        NavBrush ceiling = NavBrush.Box(new Vec3(-64, -64, 80), new Vec3(320, 320, 320), 1);
        NavVoxelGrid standing = NavVoxeliser.Classify([Floor, ceiling], Cell, Standing, 0.7f);
        NavVoxelGrid flyer = NavVoxeliser.Classify([Floor, ceiling], Cell, Flyer, 0.7f);
        Assert.DoesNotContain(standing.Cells.ToArray(), code => (code & NavVoxelGrid.FreeBit) != 0);

        // The flyer's box (32 tall) fits at k = 1 (16..80) and k = 2 (32..80, touching).
        Assert.Equal(Nav3dLeafFlags.Floor, Flags(flyer, 8, 8, 1));
        Assert.Equal(Nav3dLeafFlags.Ceiling, Flags(flyer, 8, 8, 2));
        Assert.False(Free(flyer, 8, 8, 3));
    }

    [Fact]
    public void PlayerClipBlocksThePlayerMaskAndNotTheNpcMask()
    {
        NavBrush clip = NavBrush.Box(new Vec3(96, 96, 16), new Vec3(160, 160, 256), 0x10000);
        NavVoxelGrid standing = NavVoxeliser.Classify([Floor, clip], Cell, Standing, 0.7f);
        NavVoxelGrid flyer = NavVoxeliser.Classify([Floor, clip], Cell, Flyer, 0.7f);
        Assert.False(Free(standing, 8, 8, 4));
        Assert.True(Free(flyer, 8, 8, 4));
    }

    [Fact]
    public void TheRingBeyondTheBoxIsClassifiedToo()
    {
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor], Cell, Standing, 0.7f);
        Assert.True(grid.IsBlockedWithRing(-1, 5, 0));
        Assert.False(grid.IsBlockedWithRing(16, 5, 3));
        Assert.True(grid.IsBlockedWithRing(5, 5, -1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VoxelisingTurnedBrushesEqualsTurningTheGrid(int turns)
    {
        NavBrush pillar = NavBrush.Box(new Vec3(40, 150, 16), new Vec3(90, 200, 256), 1);
        NavBrush[] brushes = [Floor, Ramp(), pillar];
        NavVoxelGrid grid = NavVoxeliser.Classify(brushes, Cell, Standing, 0.7f);
        NavVoxelGrid turned = NavVoxeliser.Classify([.. brushes.Select(b => b.Turned(turns, 256))], Cell, Standing, 0.7f);
        Assert.Equal(NavOctree.Turn(grid.Cells, 16, turns), turned.Cells.ToArray());
    }

    [Fact]
    public void BrushesTheAgentDoesNotCollideWithAreIgnoredAndCancellationIsObserved()
    {
        NavBrush water = NavBrush.Box(new Vec3(0, 0, 16), new Vec3(256, 256, 256), 0x20);
        NavVoxelGrid grid = NavVoxeliser.Classify([Floor, water], Cell, Standing, 0.7f);
        Assert.True(Free(grid, 8, 8, 5));

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NavVoxeliser.Classify([Floor], Cell, Standing, 0.7f, cancelled.Token));
    }

    [Fact]
    public void AnEmptyOrZeroRegionIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavVoxeliser.Classify([Floor], Cell with { SizeX = 0 }, Standing, 0.7f));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavVoxeliser.Classify([Floor], Cell with { VoxelSize = 0 }, Standing, 0.7f));
    }
}
