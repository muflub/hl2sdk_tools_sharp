//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomHeightHarness;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// Navigation of rooms of their own heights (the rooms design, 17.6 and
/// 17.11): a tall room's columns run to its ceiling, its section is
/// revision 3 and a cube's stays revision 2, a level placing it is a
/// version 3 <c>.nav3d</c> with each cell's height, and the stitched grid is
/// the flattened level's, run for run, at every turn.
/// </summary>
public sealed class RoomNavHeightsTests
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A tall room's navigation: its columns are its height in voxels and
    /// its free runs reach its ceiling's underside; a cube's are its edge;
    /// its door points are the cube's, on the floor; and it turns as a cube
    /// room does, keeping its height.
    /// </summary>
    [Fact]
    public async Task ATallRoomsColumnsRunToItsCeiling()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(Library());
        RoomNav hub = Build(rooms.Get("hub"));
        RoomNav tall = Build(rooms.Get("tall"));
        RoomNav mid = Build(rooms.Get("mid"));

        Assert.Equal((16, 16, false), (hub.CellVoxels, hub.ColumnVoxels, hub.IsShaped));
        Assert.Equal((16, 32, true), (tall.CellVoxels, tall.ColumnVoxels, tall.IsShaped));
        Assert.Equal(24, mid.ColumnVoxels);
        Assert.Equal(15, hub.Columns.Runs.Max(r => r.ZLo + r.Height));
        Assert.Equal(31, tall.Columns.Runs.Max(r => r.ZLo + r.Height));
        Assert.Equal(23, mid.Columns.Runs.Max(r => r.ZLo + r.Height));
        Assert.Equal(hub.SocketData.Select(s => s.DoorPoint), tall.SocketData.Select(s => s.DoorPoint));

        RoomNav turned = tall.Turned(3);
        Assert.Equal((3, 32), (turned.Turn, turned.ColumnVoxels));
        Assert.Equal(tall.Columns.Runs.Length, turned.Columns.Runs.Length);
    }

    /// <summary>
    /// A shaped room's section is revision 3, its columns' height after its
    /// edge, and reads back the same; a cube room's is revision 2 as
    /// before; a revision 3 section that says a cube's height is damage.
    /// </summary>
    [Fact]
    public async Task AShapedRoomsSectionIsRevisionThree()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(Library());
        RoomNav hub = Build(rooms.Get("hub"));
        RoomNav tall = Build(rooms.Get("tall"));
        byte[] cube = RoomNavSection.Write(hub, NavCompression.None);
        byte[] shaped = RoomNavSection.Write(tall, NavCompression.None);
        Assert.Equal(RoomNavSection.Revision, BinaryPrimitives.ReadInt32BigEndian(cube.AsSpan(9)));
        Assert.Equal(RoomNavSection.ShapedRevision, BinaryPrimitives.ReadInt32BigEndian(shaped.AsSpan(9)));

        RoomNav back = RoomNavSection.Read(shaped)!;
        Assert.Equal(32, back.ColumnVoxels);
        Assert.Equal(shaped, RoomNavSection.Write(back, NavCompression.None));
        Assert.Equal(16, RoomNavSection.Read(cube)!.ColumnVoxels);

        // revision @9, turn @13, cell @14, voxel @18, edge @22, columns @26
        byte[] lying = [.. shaped];
        BinaryPrimitives.WriteInt32BigEndian(lying.AsSpan(26), 16);
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => RoomNavSection.Read(lying));
        Assert.Equal(
            "a room nav section of revision 3 with columns 16 voxels tall; a shaped room's are 1 to 255, and not its 16 voxels a side.",
            refused.Message);
        BinaryPrimitives.WriteInt32BigEndian(lying.AsSpan(26), 20);
        Assert.Throws<InvalidDataException>(() => RoomNavSection.Read(lying));
    }

    /// <summary>
    /// The library's navigation settings hold a shaped room's height to the
    /// grid, with the 17.3 texts: a whole number of voxels, at most 255.
    /// </summary>
    [Fact]
    public void AHeightIsHeldToTheVoxelGrid()
    {
        NavSettings settings = NavSettings.Default;
        Assert.Equal(16, settings.ColumnVoxels(Hub));
        Assert.Equal(32, settings.ColumnVoxels(Shaped("tall", Tall)));
        RoomLibraryException off = Assert.Throws<RoomLibraryException>(() => settings.ColumnVoxels(Shaped("tall", 520)));
        Assert.Equal("room tall: room_height 520 is not a whole number of navigation voxels (16 units each).", off.Message);
        RoomLibraryException high = Assert.Throws<RoomLibraryException>(() => settings.ColumnVoxels(Shaped("tall", 4096)));
        Assert.Equal("room tall: room_height 4096 is taller than 4080, the most navigation describes (255 voxels).", high.Message);
        Assert.Equal(255, settings.ColumnVoxels(Shaped("tall", 4080)));
    }

    /// <summary>
    /// A level placing a tall room between two cubes, at every turn: the
    /// file is version 3 with each cell's height; the tall cell has leaves
    /// up to its ceiling and the cubes none above their own; the rooms are
    /// one component for the standing agent; a level of cubes is version 2;
    /// the file reads back to the level it was written from, and to the
    /// same bytes; and the stitched grid is the flattened level's, run for
    /// run and record for record, each cell to its own height.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelWithATallRoomIsVersionThreeAndMatchesItsFlattenedLevel(int rotation)
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        Dictionary<string, RoomNav> navs = rooms.Rooms.ToDictionary(r => r.Definition.Name, Build, StringComparer.Ordinal);
        LevelGrid level = Level($"hub, tall@{rotation}, mid@{rotation}");
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        Nav3dLevel linked = LevelNavLinker.Link(layout, 3, 1, (room, turn) => navs[room].Turned(turn), null, Guid.Empty);
        byte[] file = Nav3dWriter.Write(linked);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal([16, 32, 24], linked.CellHeights);

        Nav3dReader nav = Nav3dReader.Open(file);
        Assert.Equal((3, 32), (nav.Version, nav.TallestCell));
        Assert.Equal([16, 32, 24], Enumerable.Range(0, 3).Select(nav.CellHeight));
        Assert.True(nav.FindLeaf(new Vec3(256 + 128, 128, 400)) >= 0);
        Assert.Equal(-1, nav.FindLeaf(new Vec3(128, 128, 400)));
        Assert.Equal(-1, nav.FindLeaf(1, 8, 8, 32));
        Assert.True(nav.TryVoxel(new Vec3(128, 128, 400), out _, out _, out _, out _, out int z) && z == 25);
        Assert.False(nav.TryVoxel(new Vec3(128, 128, 520), out _, out _, out _, out _, out _));
        Assert.Equal(1, nav.ComponentCount(0));
        Assert.Equal(file, Nav3dWriter.Write(nav.ToLevel()));
        Assert.Contains("cells of their own heights: 16 to 32 voxels up", NavInspector.Describe(nav), StringComparison.Ordinal);

        Nav3dLevel cubes = LevelNavLinker.Link(
            RoomPropHarness.Layout(rooms, Level("hub, hub")), 2, 1, (room, turn) => navs[room].Turned(turn), null, Guid.Empty);
        Assert.Null(cubes.CellHeights);
        byte[] cubeFile = Nav3dWriter.Write(cubes);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(cubeFile.AsSpan(8)));
        Assert.Equal(16, Nav3dReader.Open(cubeFile).CellHeight(0));
        Assert.DoesNotContain("own heights", NavInspector.Describe(Nav3dReader.Open(cubeFile)), StringComparison.Ordinal);

        await SameAsFlattenedAsync(library, level, layout, nav);
    }

    /// <summary>
    /// A cell's height out of range, or a leaf above its cell's height, is
    /// refused by the writer and by the reader, naming what is wrong.
    /// </summary>
    [Fact]
    public async Task ACellHeightOutOfRangeIsRefused()
    {
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(Library());
        Dictionary<string, RoomNav> navs = rooms.Rooms.ToDictionary(r => r.Definition.Name, Build, StringComparer.Ordinal);
        Nav3dLevel level = LevelNavLinker.Link(
            RoomPropHarness.Layout(rooms, Level("hub, tall, ~")), 3, 1, (room, turn) => navs[room].Turned(turn), null, Guid.Empty);
        Assert.Equal([16, 32, 0], level.CellHeights);

        Assert.Equal(
            "cell 1 is 256 voxels tall; a placed cell is 1 to 255, an empty one 0. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(With(level, 16, 256, 0))).Message);
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(With(level, 16, 32, 4)));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(With(level, 16, 20, 0)));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(With(level, 16, 32)));

        byte[] file = Nav3dWriter.Write(level);
        int heights = Directory(file, Nav3dFormat.CellHeightsTag);
        byte[] zero = [.. file];
        BinaryPrimitives.WriteInt32LittleEndian(zero.AsSpan(Nav3dFormat.EnvelopeBytes + heights + 4), 0);
        Assert.Equal("cell 1 is 0 voxels tall; a placed cell is 1 to 255, an empty one 0.", Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(zero)).Message);
        byte[] low = [.. file];
        BinaryPrimitives.WriteInt32LittleEndian(low.AsSpan(Nav3dFormat.EnvelopeBytes + heights + 4), 20);
        Assert.Contains("leaving its cell", Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(low)).Message, StringComparison.Ordinal);
        byte[] cube = [.. file];
        BinaryPrimitives.WriteInt32LittleEndian(cube.AsSpan(8), 2);
        Assert.Contains("leaving its cell", Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(cube)).Message, StringComparison.Ordinal);

        static Nav3dLevel With(Nav3dLevel level, params int[] heights) => new()
        {
            CellSize = level.CellSize, VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, CellHeights = heights,
            Columns = level.Columns, Rows = level.Rows, Origin = level.Origin, FloorNormalZ = level.FloorNormalZ,
            StepHeight = level.StepHeight, JumpHeight = level.JumpHeight, JumpDistance = level.JumpDistance,
            Cells = level.Cells, Doors = level.Doors, Pois = level.Pois, Presets = level.Presets, Roots = level.Roots,
            ColumnStarts = level.ColumnStarts, Leaves = level.Leaves, Clearance = level.Clearance, Obstacles = level.Obstacles,
            Brushes = level.Brushes, Jumps = level.Jumps,
        };
    }

    /// <summary>A room's navigation at turn 0 with the default settings.</summary>
    private static RoomNav Build(RoomObject room) => RoomNavBuilder.Build(room.Definition, room.Bsp, [], RoomRole.None, NavSettings.Default);

    /// <summary>The image offset of a section of a raw file.</summary>
    private static int Directory(byte[] file, string tag)
    {
        ReadOnlySpan<byte> image = file.AsSpan(Nav3dFormat.EnvelopeBytes);
        int count = BinaryPrimitives.ReadInt32LittleEndian(image[4..]);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = image.Slice(Nav3dFormat.HeaderBytes + (i * Nav3dFormat.DirectoryEntryBytes), Nav3dFormat.DirectoryEntryBytes);
            if (System.Text.Encoding.ASCII.GetString(entry[..4]) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            }
        }

        throw new InvalidOperationException($"no {tag} section");
    }

    /// <summary>
    /// The stitched grid against the one built straight from the flattened
    /// level's compile, each placed cell over its own height: the same runs,
    /// floors, costs and clearance records, column by column.
    /// </summary>
    private static async Task SameAsFlattenedAsync(VmfDocument library, LevelGrid level, LevelLayout layout, Nav3dReader nav)
    {
        BspData flat = await CompileFlatAsync(library, level);
        NavGeometry whole = NavGeometry.FromBsp(flat);
        IReadOnlyList<NavBrush> overhang = NavClearanceBuilder.OverhangBrushes(whole);
        Nav3dLevel stitched = nav.ToLevel();
        int n = nav.CellVoxels;
        int runs = 0;
        foreach (RoomInstance room in layout.Rooms)
        {
            int cell = (room.Placement.CellY * level.Columns) + room.Placement.CellX;
            int nz = nav.CellHeight(cell);
            NavRegion region = new(
                nav.Origin.X + (room.Placement.CellX * nav.CellSize), nav.Origin.Y + (room.Placement.CellY * nav.CellSize), nav.Origin.Z,
                n, n, nz, nav.VoxelSize);
            NavGrid grid = NavClearanceBuilder.Build(whole, region, NavSettings.Default);
            NavColumns columns = NavColumns.Of(grid);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    List<string> expected = [];
                    foreach (NavRun run in columns.Column(x, y))
                    {
                        expected.Add(Rooms3x3NavTests.Describe(run.ZLo, run.Height, run.Key.Flags, run.Key.Cost, run.Key.PlayerFloorZ, run.Key.NpcFloorZ,
                            Rooms3x3NavTests.Record(grid.Records[run.Key.PlayerRecord], i => Rooms3x3NavTests.ObstacleKey(whole.Obstacles[i]), b => overhang[b].PlaneFloats()),
                            Rooms3x3NavTests.Record(grid.Records[run.Key.NpcRecord], i => Rooms3x3NavTests.ObstacleKey(whole.Obstacles[i]), b => overhang[b].PlaneFloats())));
                    }

                    List<string> actual = [];
                    int last = -1;
                    for (int z = 0; z < nz; z++)
                    {
                        int l = nav.FindLeaf(cell, x, y, z);
                        if (l < 0 || l == last)
                        {
                            continue;
                        }

                        last = l;
                        Nav3dLeaf leaf = nav.Leaf(l);
                        actual.Add(Rooms3x3NavTests.Describe(leaf.ZLo, leaf.Height, leaf.Flags, leaf.Cost, leaf.PlayerFloorZ, leaf.NpcFloorZ,
                            Rooms3x3NavTests.Record(nav.ClearanceRecord(l, Nav3dClipClass.Player), i => Rooms3x3NavTests.ObstacleKey(nav.Obstacle(i)), b => stitched.Brushes[b]),
                            Rooms3x3NavTests.Record(nav.ClearanceRecord(l, Nav3dClipClass.Npc), i => Rooms3x3NavTests.ObstacleKey(nav.Obstacle(i)), b => stitched.Brushes[b])));
                    }

                    string where = string.Create(CultureInfo.InvariantCulture, $"{room.Placement.Room} at ({room.Placement.CellX}, {room.Placement.CellY}), column {x} {y}");
                    Assert.True(expected.SequenceEqual(actual),
                        $"{where}:\n  whole-map {string.Join("\n            ", expected)}\n  stitched  {string.Join("\n            ", actual)}");
                    runs += actual.Count;
                }
            }
        }

        Assert.Equal(nav.LeafCount, runs);
    }
}
