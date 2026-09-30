//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>
/// The <c>.nav3d</c> file, version 2: a level round-trips through the writer
/// and the reader byte for byte under every codec, the bytes are pinned, the
/// reader derives neighbours, components, points' leaves and obstacles'
/// leaves at load, answers point and clearance queries without allocating,
/// and a broken file is refused with a message.
/// </summary>
public sealed class Nav3dFileTests
{
    /// <summary>The overhanging brush of the sample: a slab over cell 0's east column whose underside slopes up to the east, z ≥ x/2 + 32.</summary>
    internal static float[] Overhang()
    {
        float r = 1f / MathF.Sqrt(1.25f);
        return
        [
            1, 0, 0, 48, -1, 0, 0, 0,
            0, 1, 0, 16, 0, -1, 0, 0,
            0, 0, 1, 64,
            0.5f * r, 0, -r, -32f * r,
        ];
    }

    /// <summary>The sample's clearance records: blocked, an open one, and one with a dynamic corner and the brush.</summary>
    internal static (byte[] Blocked, byte[] Open, byte[] Door) Records() => (
        Nav3dClearance.BlockedRecord.ToArray(),
        Nav3dClearance.Encode([new(float.NegativeInfinity, 40), new(4, float.NegativeInfinity)], [], []),
        Nav3dClearance.Encode([new(float.NegativeInfinity, 40)], [new(0, new Nav3dCorner(float.NegativeInfinity, float.NegativeInfinity))], [0]));

    /// <summary>
    /// A two-cell level by hand: cells of 32 units, two voxels a side, two
    /// columns and one row. Cell 0 has three voxel columns with leaves (one
    /// two voxels tall; one a floor voxel under a voxel an overhang and a door
    /// cover; one a water ladder on a player-only floor); cell 1 one column,
    /// its floor 18 above cell 0's. One jump link joins the ledge.
    /// </summary>
    internal static Nav3dLevel Sample()
    {
        (byte[] blocked, byte[] open, byte[] door) = Records();
        uint o = (uint)blocked.Length;
        uint d = o + (uint)open.Length;
        const Nav3dLeafFlags Floor = Nav3dLeafFlags.GroundedPlayer | Nav3dLeafFlags.GroundedNpc | Nav3dLeafFlags.WalkablePlayer
            | Nav3dLeafFlags.WalkableNpc;
        Nav3dLeaf[] leaves =
        [
            new(0, 2, Floor, 256, o, o, 0, 0),
            new(0, 1, Floor, 256, o, o, 0, 0),
            new(1, 1, Nav3dLeafFlags.None, 256, d, d, 0, 0),
            new(0, 1, Nav3dLeafFlags.Water | Nav3dLeafFlags.Ladder | Nav3dLeafFlags.GroundedPlayer | Nav3dLeafFlags.WalkablePlayer, 768, o, 0, 2.5f, 0),
            new(0, 2, Floor, 256, o, o, 18, 18),
        ];
        return new Nav3dLevel
        {
            CellSize = 32,
            VoxelSize = 16,
            CellVoxels = 2,
            Columns = 2,
            Rows = 1,
            Origin = new Vec3(0, 0, 0),
            FloorNormalZ = 0.7f,
            StepHeight = 18,
            JumpHeight = 56,
            JumpDistance = 100,
            Cells = [new Nav3dCell("a", 0, Nav3dRoomRole.Up, 1, 0), new Nav3dCell("b", 3, Nav3dRoomRole.None, 4, 2)],
            Doors = [new Nav3dDoor(0, 0, true, "east", 1), new Nav3dDoor(1, 2, true, "west", 0)],
            Pois =
            [
                new Nav3dPoi(new Vec3(8, 8, 0), 90, 0, "arrival", "", "c0r0_start", 0, 1, -1,
                    Nav3dPoiFlags.Arrival | Nav3dPoiFlags.HasFacing, Nav3dRoomRole.Up),
                new Nav3dPoi(new Vec3(32, 8, 0), 0, 0, "door", "", "east", 0, 3, 0,
                    Nav3dPoiFlags.Door | Nav3dPoiFlags.Joined | Nav3dPoiFlags.HasFacing, Nav3dRoomRole.Up),
            ],
            Presets = [new Nav3dPreset("standing", 8, 20, Nav3dClipClass.Player), new Nav3dPreset("crawler", 4, 4, Nav3dClipClass.Npc)],
            Roots = [0, 4],
            ColumnStarts = [0, 1, 3, 3, 4, 5, 5, 5, 5],
            Leaves = leaves,
            Clearance = [.. blocked, .. open, .. door],
            Obstacles = [new Nav3dObstacle("c0r0_gate", "func_door", 0, 12, Nav3dObstacleKind.Door, new Vec3(16, 0, 16), new Vec3(32, 16, 32))],
            Brushes = [Overhang()],
            Jumps = [new Nav3dJump(1, 4, 18.5f, 1, Nav3dDirection.East, 1)],
            SpawnPoi = 0,
            UpArrivalPoi = 0,
            DownArrivalPoi = -1,
            LevelId = Guid.Parse("0f1e2d3c-4b5a-8978-8685-f4e3d2c1b0a9"),
            PackId = Guid.Parse("01234567-89ab-8def-8123-456789abcdef"),
        };
    }

    public static TheoryData<string> Codecs => ["none", "deflate:1", "deflate:6", "deflate:9", "brotli:1", "brotli:5", "brotli:11"];

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ALevelRoundTripsByteForByteUnderEveryCodec(string codec)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        byte[] bytes = Nav3dWriter.Write(Sample(), compression);
        Nav3dReader nav = Nav3dReader.Open(bytes);
        Assert.Equal(compression.Codec, nav.Codec);
        Assert.Equal(Nav3dWriter.Write(Sample()), Nav3dWriter.Write(nav.ToLevel()));
        Assert.Equal(bytes, Nav3dWriter.Write(nav.ToLevel(), compression));
    }

    /// <summary>
    /// The exact bytes, raw and under each codec, pinned by hash: the file is
    /// the same on every run and thread count by construction, and the pins
    /// make CI's Windows and macOS runners prove the codecs agree across
    /// operating systems too. The Deflate pin is Microsoft's runtime's, which
    /// carries its own zlib-ng on every OS: a distribution's packaged runtime
    /// that links the system zlib writes other (equally valid) Deflate bytes,
    /// so this one row fails there, as it should, while Brotli and raw agree.
    /// </summary>
    [Theory]
    [InlineData("none", 1048, "b87e3523")]
    [InlineData("deflate:6", 524, "5ed880ab")]
    [InlineData("brotli:9", 502, "929a3979")]
    public void TheBytesArePinned(string codec, int length, string sha256Prefix)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        byte[] bytes = Nav3dWriter.Write(Sample(), compression);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
        Assert.Equal((length, sha256Prefix), (bytes.Length, hash));
    }

    [Fact]
    public void TheHeaderAndRecordsReadBack()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.Equal((Nav3dFormat.CubeVersion, 32f, 16f, 2, 2, 1, 0.7f), (nav.Version, nav.CellSize, nav.VoxelSize, nav.CellVoxels, nav.Columns, nav.Rows, nav.FloorNormalZ));
        Assert.Equal((18f, 56f, 100f), (nav.StepHeight, nav.JumpHeight, nav.JumpDistance));
        Assert.Equal((2, 2, 2, 2, 5, 1, 1), (nav.PresetCount, nav.CellCount, nav.PoiCount, nav.DoorCount, nav.LeafCount, nav.ObstacleCount, nav.JumpCount));
        Assert.Equal(new Nav3dPreset("standing", 8, 20, Nav3dClipClass.Player), nav.Preset(0));
        Assert.Equal(0, nav.FindPreset("standing"));
        Assert.Equal(1, nav.FindPreset("crawler"));
        Assert.Equal(-1, nav.FindPreset("flyer"));
        Assert.Equal(Sample().Pois[0], nav.Poi(0));
        Assert.Equal("arrival"u8.ToArray(), nav.PoiTypeUtf8(0).ToArray());
        Assert.Equal(Nav3dPoiFlags.Door | Nav3dPoiFlags.Joined | Nav3dPoiFlags.HasFacing, nav.PoiFlags(1));
        Assert.Equal(Sample().Doors[1], nav.Door(1));
        Assert.Equal(Sample().Cells[1], nav.Cell(1));
        Assert.Equal(Sample().Leaves[3], nav.Leaf(3));
        Assert.Equal(Sample().Obstacles[0], nav.Obstacle(0));
        Assert.Equal(Sample().Jumps[0], nav.Jump(0));
        Assert.Equal((0, 4), (nav.CellRoot(0), nav.CellRoot(1)));
        Assert.Null(nav.String(Nav3dFormat.NoString));
        Assert.Equal(0, nav.StringUtf8(Nav3dFormat.NoString).Length);
        Assert.Equal(3f, nav.Leaf(3).CostMultiplier);
        Assert.Equal(1, nav.Leaf(2).ZHi);
        Assert.True(nav.Leaf(3).IsGrounded(Nav3dClipClass.Player));
        Assert.False(nav.Leaf(3).IsGrounded(Nav3dClipClass.Npc));
        Assert.Equal(2.5f, nav.Leaf(3).FloorZ(Nav3dClipClass.Player));
        Assert.Equal(0u, nav.Leaf(3).Clearance(Nav3dClipClass.Npc));
    }

    [Fact]
    public void APointFindsItsLeafInItsColumnsRuns()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.Equal(0, nav.FindLeaf(new Vec3(8, 8, 31)));
        Assert.Equal(1, nav.FindLeaf(new Vec3(16, 0, 0)));
        Assert.Equal(2, nav.FindLeaf(new Vec3(20, 5, 16)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(5, 20, 5)));
        Assert.Equal(3, nav.FindLeaf(new Vec3(20, 20, 5)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(20, 20, 20)));
        Assert.Equal(4, nav.FindLeaf(new Vec3(40, 5, 20)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(50, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(-1, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(64, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(5, 5, 32)));
        Assert.Equal(-1, nav.FindLeaf(5, 0, 0, 0));
        Assert.Equal((0, 1, 1), nav.LeafColumn(3));
        Assert.Equal((1, 0, 0), nav.LeafColumn(4));
        Assert.Equal((new Vec3(16, 0, 16), new Vec3(32, 16, 32)), nav.LeafBounds(2));
        Assert.Equal(-1, nav.LeafBelow(0));
        Assert.Equal(1, nav.LeafBelow(2));
        Assert.Equal(-1, nav.LeafBelow(3));
    }

    /// <summary>
    /// Neighbours are derived at load from the columns: overlapping runs side
    /// by side (across the cell face into the next room: through its door),
    /// touching runs one above the other; east, north, west, south, then up
    /// and down.
    /// </summary>
    [Fact]
    public void NeighboursAreDerivedFromTheColumnsAcrossCellFacesToo()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        static List<Nav3dNeighbour> Of(Nav3dReader nav, int leaf)
        {
            List<Nav3dNeighbour> list = [];
            foreach (Nav3dNeighbour n in nav.Neighbours(leaf))
            {
                list.Add(n);
            }

            return list;
        }

        Assert.Equal([new(1, Nav3dDirection.East, false), new(2, Nav3dDirection.East, false)], Of(nav, 0));
        Assert.Equal(
            [new(4, Nav3dDirection.East, true), new(3, Nav3dDirection.North, false), new(0, Nav3dDirection.West, false), new(2, Nav3dDirection.Up, false)],
            Of(nav, 1));
        Assert.Equal([new(4, Nav3dDirection.East, true), new(0, Nav3dDirection.West, false), new(1, Nav3dDirection.Down, false)], Of(nav, 2));
        Assert.Equal([new(1, Nav3dDirection.South, false)], Of(nav, 3));
        Assert.Equal([new(1, Nav3dDirection.West, true), new(2, Nav3dDirection.West, true)], Of(nav, 4));
        Nav3dReader.NeighbourList list = nav.Neighbours(1);
        Assert.Equal(4, list.Count);
        Assert.Equal(new Nav3dNeighbour(3, Nav3dDirection.North, false), list[1]);
    }

    /// <summary>
    /// The clearance queries against the direct separating-axis test on the
    /// sample's own geometry: the corners and the overhanging brush answer
    /// exactly as testing the swept box does, for many sizes.
    /// </summary>
    [Fact]
    public void ClearanceQueriesAnswerAsTheBoxTestDoes()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        NavBrush overhang = NavBrush.FromPlaneFloats(Overhang(), 1)!;

        // Leaf 2 is voxel (1, 0, 1) of cell 0: [16, 32] × [0, 16] × [16, 32],
        // under a 40-high ceiling corner and the overhang.
        foreach (float width in new[] { 0f, 2f, 7.5f, 8f, 8.002f, 20f })
        {
            foreach (float height in new[] { 0f, 4f, 7.999f, 8f, 8.002f, 12f, 30f })
            {
                double r = width * 0.5;
                NavBox swept = new(16 - r, 0 - r, 16, 32 + r, 16 + r, 32 + height);
                bool corners = !(32 + height > 40 + NavBrush.Epsilon);
                bool expected = corners && !overhang.Overlaps(swept);
                Assert.True(expected == nav.Passable(2, 1, width, height, Nav3dClipClass.Player), $"{width} x {height}");
            }
        }

        // The head room is the overhang's for the width, the corners' 40 at most.
        double underOverhang = overhang.GrowthThreshold(new NavBox(16, 0, 16, 32, 16, 32), NavGrowth.Upward, out _);
        Assert.True(underOverhang > 8 + NavBrush.Epsilon);
        Assert.Equal(Math.Min(underOverhang, 40 - 32 + NavBrush.Epsilon), nav.VerticalClearance(2, 1, 0, Nav3dClipClass.Player), 9);
        Assert.True(nav.VerticalClearance(2, 1, 8, Nav3dClipClass.Player) < nav.VerticalClearance(2, 1, 0, Nav3dClipClass.Player));
        Assert.Equal(double.NegativeInfinity, nav.VerticalClearance(2, 0, 0, Nav3dClipClass.Player));

        // Leaf 1: a 4-unit wall gap and the 40 ceiling: 8.002 wide does not fit, 20 high fits, 30 does not.
        Assert.True(nav.Passable(1, 0, 8.002f, 20, Nav3dClipClass.Player));
        Assert.False(nav.Passable(1, 0, 8.004f, 1, Nav3dClipClass.Player));
        Assert.False(nav.Passable(1, 0, 1, 24.002f, Nav3dClipClass.Player));
        Assert.Equal(4 + NavBrush.Epsilon, nav.HorizontalClearance(1, 0, 0, Nav3dClipClass.Player) / 2, 9);
        Assert.Equal(40 + NavBrush.Epsilon - 16, nav.VerticalClearance(1, 0, 8, Nav3dClipClass.Player), 9);
        Assert.Equal(double.NegativeInfinity, nav.VerticalClearance(1, 0, 9, Nav3dClipClass.Player));
        Assert.Equal(double.NegativeInfinity, nav.HorizontalClearance(1, 0, 25, Nav3dClipClass.Player));
        Assert.Equal(double.NegativeInfinity, nav.HorizontalClearance(1, 1, 0, Nav3dClipClass.Player));
        Assert.False(nav.Passable(1, 1, 1, 1, Nav3dClipClass.Player));

        // The NPC class's record of leaf 3 blocks everything.
        Assert.True(nav.Passable(3, 0, 1, 1, Nav3dClipClass.Player));
        Assert.False(nav.Passable(3, 0, 0, 0, Nav3dClipClass.Npc));
        Assert.Equal(-1, nav.FitTop(3, 0, 0, Nav3dClipClass.Npc));

        // A fit is a prefix of the run: leaf 0 (voxels 0 and 1) under the 40 ceiling.
        Assert.Equal(1, nav.FitTop(0, 8, 8, Nav3dClipClass.Player));
        Assert.Equal(0, nav.FitTop(0, 8, 20, Nav3dClipClass.Player));
        Assert.Equal(-1, nav.FitTop(0, 8, 30, Nav3dClipClass.Player));
        Assert.Equal(-1, nav.FitTop(0, 9, 1, Nav3dClipClass.Player));
        Assert.Equal(1, nav.FitTop(2, 0, 8, Nav3dClipClass.Player));
        Assert.Equal(-1, nav.FitTop(2, 0, 9, Nav3dClipClass.Player));
    }

    [Fact]
    public void ADynamicObstacleBlocksOnlyWhileTheRuntimeSaysSo()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.True(nav.Passable(2, 1, 0, 0, Nav3dClipClass.Player));
        Assert.True(nav.Passable(2, 1, 0, 0, Nav3dClipClass.Player, [false]));
        Assert.False(nav.Passable(2, 1, 0, 0, Nav3dClipClass.Player, [true]));
        Assert.Equal(double.NegativeInfinity, nav.VerticalClearance(2, 1, 0, Nav3dClipClass.Player, [true]));
        Assert.Equal(double.NegativeInfinity, nav.HorizontalClearance(2, 1, 0, Nav3dClipClass.Player, [true]));
        Assert.Equal(-1, nav.FitTop(2, 0, 0, Nav3dClipClass.Player, [true]));
        Assert.Equal([2], nav.ObstacleLeaves(0).ToArray());
    }

    [Fact]
    public void StandingIsAFloorUnderTheBottomVoxelOrARimTooTightToSinkThrough()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.True(nav.Standable(0, 0, 8, 8, Nav3dClipClass.Player));
        Assert.False(nav.Standable(0, 1, 8, 8, Nav3dClipClass.Player));
        Assert.False(nav.Standable(0, 0, 9, 8, Nav3dClipClass.Player));

        // Leaf 2 sits on leaf 1: a point sinks, an agent too wide for leaf 1 rests on its rim.
        Assert.False(nav.Standable(2, 1, 0, 1, Nav3dClipClass.Player));
        Assert.False(nav.Standable(2, 1, 8, 8.5f, Nav3dClipClass.Player));
        Assert.False(nav.Standable(3, 0, 1, 1, Nav3dClipClass.Npc));
        Assert.Equal(18f, nav.StepUp(1, 4, Nav3dClipClass.Player));
        Assert.Equal(-18f, nav.StepUp(4, 1, Nav3dClipClass.Npc));
        Assert.True(float.IsNaN(nav.StepUp(2, 4, Nav3dClipClass.Player)));
        Assert.True(float.IsNaN(nav.StepUp(3, 4, Nav3dClipClass.Npc)));
    }

    /// <summary>Components per preset are derived at load: the crawler fits everywhere but leaf 3 (NPC-blocked), the standing preset not in leaf 2.</summary>
    [Fact]
    public void ComponentsArePerPresetAndDerivedAtLoad()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.Equal(1, nav.ComponentCount(0));
        Assert.Equal([0, 0, -1, 0, 0], Enumerable.Range(0, 5).Select(l => nav.Component(0, l)));
        Assert.Equal(1, nav.ComponentCount(1));
        Assert.Equal([0, 0, 0, -1, 0], Enumerable.Range(0, 5).Select(l => nav.Component(1, l)));
        Assert.Equal([0], nav.Jumps(1).ToArray());
        Assert.Equal([0], nav.Jumps(4).ToArray());
        Assert.Equal(0, nav.Jumps(0).Length);
        Assert.Equal((0, 1), (nav.PoiLeaf(0), nav.PoiLeaf(1)));

        // The door point stands on cell 0's east face: by position alone it is
        // cell 1's, but its leaf is found in its own cell.
        Assert.Equal(4, nav.FindLeaf(nav.PoiPosition(1)));
    }

    /// <summary>After load, the hot queries read bytes and derived arrays only: none of them allocates.</summary>
    [Fact]
    public void TheHotQueriesDoNotAllocate()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        bool[] blocking = [true];
        long sum = 0;
        void Run()
        {
            for (int i = 0; i < 200; i++)
            {
                sum += nav.FindLeaf(new Vec3(20, 5, 16)) + nav.FindLeaf(0, 1, 0, 0);
                foreach (Nav3dNeighbour neighbour in nav.Neighbours(1))
                {
                    sum += neighbour.Leaf;
                }

                sum += nav.Passable(2, 1, 4, 4, Nav3dClipClass.Player) ? 1 : 0;
                sum += nav.Passable(2, 1, 4, 4, Nav3dClipClass.Player, blocking) ? 1 : 0;
                sum += nav.Standable(0, 0, 8, 8, Nav3dClipClass.Player) ? 1 : 0;
                sum += (long)nav.VerticalClearance(2, 1, 4, Nav3dClipClass.Player) + (long)nav.HorizontalClearance(1, 0, 4, Nav3dClipClass.Player);
                sum += nav.FitTop(0, 8, 8, Nav3dClipClass.Player) + nav.Component(0, 1) + nav.Jumps(1).Length + nav.ObstacleLeaves(0).Length;
                sum += nav.Leaf(1).ZLo + nav.LeafColumn(3).X + nav.LeafBelow(2) + (int)nav.StepUp(1, 4, Nav3dClipClass.Player);
                sum += nav.PoiTypeUtf8(0).Length + nav.PoiLeaf(1) + nav.ClearanceRecord(2, Nav3dClipClass.Npc).Length;
            }
        }

        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0, sum);
    }

    [Fact]
    public void TheSpawnIsOneCallAndTheLevelIdMatchesTheMapsKey()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.True(nav.TryGetSpawn(out Vec3 position, out float yaw));
        Assert.Equal((new Vec3(8, 8, 0), 90f), (position, yaw));
        Assert.Equal((0, 0, -1), (nav.SpawnPoi, nav.UpArrivalPoi, nav.DownArrivalPoi));
        Assert.True(nav.MatchesMap("0f1e2d3c-4b5a-8978-8685-f4e3d2c1b0a9"));
        Assert.False(nav.MatchesMap("01234567-89ab-8def-8123-456789abcdef"));
        Assert.False(nav.MatchesMap("not an id"));
        Assert.Equal(Guid.Parse("01234567-89ab-8def-8123-456789abcdef"), nav.PackId);
        Nav3dReader none = Nav3dReader.Open(Nav3dWriter.Write(new Nav3dLevel
        {
            CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 1, Rows = 1, Cells = [new Nav3dCell(null, 0, 0, 0, 0)], Roots = [-1],
        }));
        Assert.False(none.TryGetSpawn(out _, out _));
        Assert.False(none.MatchesMap(Guid.Empty.ToString()));
        Assert.Equal(-1, none.FindLeaf(new Vec3(1, 1, 1)));
    }

    [Fact]
    public void TheLevelIdIsStoredInRfcOrderAtAFixedOffset()
    {
        byte[] bytes = Nav3dWriter.Write(Sample());
        Assert.Equal(Convert.FromHexString("0f1e2d3c4b5a89788685f4e3d2c1b0a9"), bytes.AsSpan(Nav3dFormat.EnvelopeBytes + 104, 16).ToArray());
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "magic", "magic is missing" },
        { "version", "this build reads version 3" },
        { "codec", "codec 9 is not one" },
        { "stored", "stored bytes" },
        { "truncated", "stored bytes" },
        { "directory", "section directory runs past" },
        { "section", "lies outside the file" },
        { "duplicate", "two \"STRS\" sections" },
        { "missing", "has no \"STRS\" section" },
        { "grid", "describes no valid grid" },
        { "sizes", "sizes are out of range" },
        { "root", "has root 70" },
        { "roots", "placed cells own" },
        { "columns", "not a partition of the 5 leaves" },
        { "run", "overlapping its column's last" },
        { "record", "clearance record at 5 is out of range or malformed" },
        { "overhang", "lists an overhanging brush but runs 2 voxels" },
        { "brush", "brush plane starts are not a partition" },
        { "jump", "jump link 0 names leaves" },
        { "obstacle", "obstacle 0 names a cell out of range" },
        { "poi", "point of interest 1 names a cell or door out of range" },
        { "door", "door 0 names a cell, direction or facing door" },
        { "spawn", "names no point of interest" },
        { "strings", "string table must start with the empty string" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void ABrokenFileIsRefusedSayingWhatIsWrong(string fault, string expected)
    {
        byte[] bytes = Nav3dWriter.Write(Sample());
        int image = Nav3dFormat.EnvelopeBytes;
        int Entry(string tag)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(image + 4));
            for (int i = 0; i < count; i++)
            {
                int at = image + Nav3dFormat.HeaderBytes + (i * 16);
                if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == tag)
                {
                    return at;
                }
            }

            throw new InvalidOperationException(tag);
        }

        int At(string tag) => image + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Entry(tag) + 8));
        switch (fault)
        {
            case "magic": bytes[0] = (byte)'X'; break;
            case "version": bytes[8] = 1; break;
            case "codec": bytes[12] = 9; break;
            case "stored": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 5); break;
            case "truncated": bytes = bytes[..^4]; break;
            case "directory": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 4), 100000); break;
            case "section": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Entry("POIS") + 12), 100000); break;
            case "duplicate": "STRS"u8.CopyTo(bytes.AsSpan(Entry("CELL"))); break;
            case "missing": "XXXX"u8.CopyTo(bytes.AsSpan(Entry("STRS"))); break;
            case "grid": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 20), 500); break;
            case "sizes": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 8), 99); break;
            case "root": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(At("ROOT")), 70); break;
            case "roots": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(At("ROOT") + 4), -1); break;
            case "columns": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("COLS") + 4), 9); break;
            case "run": bytes[At("LEAF") + (2 * Nav3dFormat.LeafRecordBytes)] = 0; break;
            case "record": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("LEAF") + 8), 5); break;
            case "overhang": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("LEAF") + 8), 40); break;
            case "brush": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("BRSI") + 4), 2); break;
            case "jump": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("JUMP")), 4); break;
            case "obstacle": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(At("DYNO") + 8), 9); break;
            case "poi": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(At("POIS") + Nav3dFormat.PoiRecordBytes + 40), 7); break;
            case "door": bytes[At("DOOR") + 4] = 7; break;
            case "spawn": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 68), 5); break;
            case "strings": bytes[At("STRS")] = (byte)'x'; break;
            default: throw new InvalidOperationException(fault);
        }

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(bytes));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInconsistentLevelIsRefusedByTheWriter()
    {
        Nav3dLevel level = Sample();
        Nav3dLevel With(Func<Nav3dLevel, Nav3dLevel> change) => change(level);
        Nav3dLevel Copy(Nav3dLevel l) => new()
        {
            CellSize = l.CellSize, VoxelSize = l.VoxelSize, CellVoxels = l.CellVoxels, Columns = l.Columns, Rows = l.Rows,
            Cells = l.Cells, Doors = l.Doors, Pois = l.Pois, Presets = l.Presets, Roots = l.Roots, ColumnStarts = l.ColumnStarts,
            Leaves = l.Leaves, Clearance = l.Clearance, Obstacles = l.Obstacles, Brushes = l.Brushes, Jumps = l.Jumps,
        };

        _ = Nav3dWriter.Write(Copy(level));
        List<Nav3dLevel> broken =
        [
            new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1 },
            new Nav3dLevel { CellSize = 0, VoxelSize = 16, CellVoxels = 2, Columns = 1, Rows = 1 },
            new Nav3dLevel { CellSize = 1, VoxelSize = 1, CellVoxels = 0, Columns = 1, Rows = 1 },
            new Nav3dLevel { CellSize = 1, VoxelSize = 1, CellVoxels = 200, Columns = 1, Rows = 1 },
        ];
        foreach (Func<Nav3dLevel, Nav3dLevel> change in new Func<Nav3dLevel, Nav3dLevel>[]
        {
            l => new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 1, Rows = 1, Cells = [l.Cells[0]], Roots = [0],
                Presets = [.. Enumerable.Range(0, 33).Select(i => new Nav3dPreset($"a{i}", 1, 1, 0))] },
            l => new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = l.Cells, Roots = [-1, -1], SpawnPoi = 3 },
            l => new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = l.Cells, Roots = [-1, -1], Doors = [new Nav3dDoor(5, 0, false, "x", -1)] },
            l => new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = l.Cells, Roots = [-1, -1], Pois = [l.Pois[0] with { Cell = 9 }] },
            l => new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = l.Cells, Roots = [-1, -1], Obstacles = [l.Obstacles[0] with { Cell = 9 }] },
        })
        {
            broken.Add(With(change));
        }

        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 2], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = [0, 1], Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = [0, 3, 1, 3, 4, 5, 5, 5, 5], Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = [.. level.Leaves.Select(x => x with { Height = 3 })], Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = [], Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = [.. level.Clearance, 1], Obstacles = level.Obstacles, Brushes = level.Brushes });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = [[1, 0, 0, 1]] });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes, Jumps = [new Nav3dJump(4, 1, 0, 1, 0, 1)] });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes, Jumps = [new Nav3dJump(1, 4, 0, 0, 0, 1)] });
        broken.Add(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1, Cells = level.Cells, Roots = [0, 4], ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles, Brushes = level.Brushes, Presets = [new Nav3dPreset("a\0b", 1, 1, 0)] });
        foreach (Nav3dLevel bad in broken)
        {
            Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(bad));
        }
    }
}
