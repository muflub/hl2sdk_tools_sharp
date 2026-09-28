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
/// The <c>.nav3d</c> file: a level round-trips through the writer and the
/// reader byte for byte under every codec, the bytes are pinned, the reader
/// answers point, neighbour and spawn queries from the bytes, and a broken
/// file is refused with a message.
/// </summary>
public sealed class Nav3dFileTests
{
    /// <summary>
    /// A two-cell level by hand: cell 0 is one free leaf of the whole cell
    /// (two voxels a side), cell 1 is split, its low-x half free in two
    /// leaves and the rest blocked; one door link across the cell face.
    /// </summary>
    internal static Nav3dLevel Sample()
    {
        uint Free(uint leaf) => Nav3dFormat.Node(Nav3dNodeKind.Free, leaf);
        uint blocked = Nav3dFormat.Node(Nav3dNodeKind.Blocked, 0);
        Nav3dLeaf[] leaves =
        [
            new(0, 0, 0, 1, Nav3dLeafFlags.Floor | Nav3dLeafFlags.Door, 0, 0),
            new(2, 0, 0, 0, Nav3dLeafFlags.Floor | Nav3dLeafFlags.Door | Nav3dLeafFlags.SidePositiveX, 0, 1),
            new(2, 1, 0, 0, Nav3dLeafFlags.Floor | Nav3dLeafFlags.SidePositiveX, 0, 1),
        ];
        uint[] nodes =
        [
            Free(0),
            Nav3dFormat.Node(Nav3dNodeKind.Inner, 2),
            Free(1), blocked, Free(2), blocked, blocked, blocked, blocked, blocked,
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
            Cells = [new Nav3dCell("a", 0, Nav3dRoomRole.Up, 1, 0), new Nav3dCell("b", 3, Nav3dRoomRole.None, 4, 2)],
            Doors = [new Nav3dDoor(0, 0, true, "east", 1), new Nav3dDoor(1, 2, true, "west", 0)],
            Pois =
            [
                new Nav3dPoi(new Vec3(8, 8, 0), 90, 0, "arrival", "", "c0r0_start", 0, 1, -1,
                    Nav3dPoiFlags.Arrival | Nav3dPoiFlags.HasFacing, Nav3dRoomRole.Up),
                new Nav3dPoi(new Vec3(32, 8, 0), 0, 0, "door", "", "east", 0, 1, 0,
                    Nav3dPoiFlags.Door | Nav3dPoiFlags.Joined | Nav3dPoiFlags.HasFacing, Nav3dRoomRole.Up),
            ],
            Agents =
            [
                new Nav3dAgent("standing", new Vec3(-16, -16, 0), new Vec3(16, 16, 72), Nav3dFormat.PlayerSolidMask)
                {
                    Roots = [0, 1],
                    Nodes = nodes,
                    Leaves = leaves,
                    AdjacencyStart = [0, 1, 3, 4],
                    Adjacency = [1 | Nav3dFormat.ThroughDoorBit, 0 | Nav3dFormat.ThroughDoorBit, 2, 1],
                    Links = [new Nav3dDoorLink(0, 1, 0)],
                    Components = [new Nav3dComponent(3, 10)],
                    PoiLeaves = [0, 0],
                },
            ],
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
    [InlineData("none", 724, "a5aba52b")]
    [InlineData("deflate:6", 394, "53243cd0")]
    [InlineData("brotli:9", 375, "6a550f96")]
    public void TheBytesArePinned(string codec, int length, string sha256Prefix)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        byte[] bytes = Nav3dWriter.Write(Sample(), compression);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
        Assert.Equal((length, sha256Prefix), (bytes.Length, hash));
    }

    [Fact]
    public void TheHeaderReadsBack()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.Equal((Nav3dFormat.Version, 32f, 16f, 2, 1, 2, 1, 0.7f), (nav.Version, nav.CellSize, nav.VoxelSize, nav.CellVoxels, nav.OctreeDepth, nav.Columns, nav.Rows, nav.FloorNormalZ));
        Assert.Equal((1, 2, 2, 2), (nav.AgentCount, nav.CellCount, nav.PoiCount, nav.DoorCount));
        Assert.Equal("standing", nav.AgentName(0));
        Assert.Equal((new Vec3(-16, -16, 0), new Vec3(16, 16, 72)), nav.AgentBox(0));
        Assert.Equal(Nav3dFormat.PlayerSolidMask, nav.AgentContentsMask(0));
        Assert.Equal(0, nav.FindAgent("standing"));
        Assert.Equal(-1, nav.FindAgent("flyer"));
        Assert.Equal(Sample().Pois[0], nav.Poi(0));
        Assert.Equal("arrival"u8.ToArray(), nav.PoiTypeUtf8(0).ToArray());
        Assert.Equal(Nav3dPoiFlags.Door | Nav3dPoiFlags.Joined | Nav3dPoiFlags.HasFacing, nav.PoiFlags(1));
        Assert.Equal(Sample().Doors[1], nav.Door(1));
        Assert.Equal(Sample().Cells[1], nav.Cell(1));
        Assert.Equal(new Nav3dComponent(3, 10), nav.Component(0, 0));
        Assert.Equal(new Nav3dDoorLink(0, 1, 0), nav.Link(0, 0));
        Assert.Equal(0, nav.PoiLeaf(0, 1));
        Assert.Null(nav.String(Nav3dFormat.NoString));
        Assert.Equal(0, nav.StringUtf8(Nav3dFormat.NoString).Length);
    }

    [Fact]
    public void APointFindsItsLeafByDescendingTheCellsTree()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Assert.Equal(0, nav.FindLeaf(0, new Vec3(31, 31, 31)));
        Assert.Equal(1, nav.FindLeaf(0, new Vec3(32, 0, 0)));
        Assert.Equal(2, nav.FindLeaf(0, new Vec3(40, 20, 5)));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(50, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(40, 5, 20)));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(-1, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(64, 5, 5)));
        Assert.Equal(-1, nav.FindLeaf(0, new Vec3(5, 5, 32)));
        Assert.Equal(-1, nav.FindLeaf(0, 5, 0, 0, 0));
        Assert.Equal(new Vec3(32, 16, 0), nav.LeafMins(0, 2));
        Assert.Equal(16f, nav.LeafSize(0, 2));
        Assert.Equal(32f, nav.LeafSize(0, 0));
    }

    [Fact]
    public void NeighboursComeInLeafOrderWithTheDoorBitAndWithoutAllocating()
    {
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(Sample()));
        Nav3dReader.NeighbourList list = nav.Neighbours(0, 1);
        Assert.Equal(2, list.Count);
        Assert.Equal(new Nav3dNeighbour(0, true), list[0]);
        Assert.Equal(new Nav3dNeighbour(2, false), list[1]);
        List<Nav3dNeighbour> seen = [];
        foreach (Nav3dNeighbour neighbour in nav.Neighbours(0, 1))
        {
            seen.Add(neighbour);
        }

        Assert.Equal([new Nav3dNeighbour(0, true), new Nav3dNeighbour(2, false)], seen);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            foreach (Nav3dNeighbour neighbour in nav.Neighbours(0, 1))
            {
                sum += neighbour.Leaf;
            }

            sum += nav.FindLeaf(0, new Vec3(40, 20, 5));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(4000, sum);
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
    }

    [Fact]
    public void TheLevelIdIsStoredInRfcOrderAtAFixedOffset()
    {
        byte[] bytes = Nav3dWriter.Write(Sample());
        Assert.Equal(Convert.FromHexString("0f1e2d3c4b5a89788685f4e3d2c1b0a9"), bytes.AsSpan(Nav3dFormat.EnvelopeBytes + 72, 16).ToArray());
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "magic", "magic is missing" },
        { "version", "this build reads version" },
        { "codec", "codec 9 is not one" },
        { "stored", "stored bytes" },
        { "truncated", "stored bytes" },
        { "directory", "section directory runs past" },
        { "section", "lies outside the file" },
        { "duplicate", "two \"STRS\" sections" },
        { "missing", "has no \"STRS\" section" },
        { "grid", "describes no valid grid" },
        { "node", "points outside its nodes or leaves" },
        { "adjacency", "adjacency entry 0 names leaf" },
        { "root", "has root 70" },
        { "spawn", "names no point of interest" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void ABrokenFileIsRefusedSayingWhatIsWrong(string fault, string expected)
    {
        byte[] bytes = Nav3dWriter.Write(Sample());
        int image = Nav3dFormat.EnvelopeBytes;
        int Section(string tag, uint index)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(image + 4));
            for (int i = 0; i < count; i++)
            {
                int at = image + Nav3dFormat.HeaderBytes + (i * 16);
                if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == tag && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4)) == index)
                {
                    return at;
                }
            }

            throw new InvalidOperationException(tag);
        }

        uint Offset(string tag, uint index) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Section(tag, index) + 8));
        switch (fault)
        {
            case "magic": bytes[0] = (byte)'X'; break;
            case "version": bytes[8] = 99; break;
            case "codec": bytes[12] = 9; break;
            case "stored": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 5); break;
            case "truncated": bytes = bytes[..^4]; break;
            case "directory": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 4), 100000); break;
            case "section": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Section("POIS", Nav3dFormat.LevelIndex) + 12), 100000); break;
            case "duplicate": "STRS"u8.CopyTo(bytes.AsSpan(Section("CELL", Nav3dFormat.LevelIndex))); break;
            case "missing": "XXXX"u8.CopyTo(bytes.AsSpan(Section("STRS", Nav3dFormat.LevelIndex))); break;
            case "grid": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 24), 5); break;
            case "node": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(image + (int)Offset("NODE", 0) + 4), Nav3dFormat.Node(Nav3dNodeKind.Inner, 9)); break;
            case "adjacency": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(image + (int)Offset("ADJN", 0)), 77); break;
            case "root": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + (int)Offset("ROOT", 0)), 70); break;
            case "spawn": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(image + 60), 5); break;
            default: throw new InvalidOperationException(fault);
        }

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => Nav3dReader.Open(bytes));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInconsistentLevelIsRefusedByTheWriter()
    {
        Nav3dLevel level = Sample();
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel { CellSize = 32, VoxelSize = 16, CellVoxels = 2, Columns = 2, Rows = 1 }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel { CellSize = 0, VoxelSize = 16, CellVoxels = 2, Columns = 1, Rows = 1 }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel { CellSize = 1, VoxelSize = 1, CellVoxels = 0, Columns = 1, Rows = 1 }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel
        {
            CellSize = 1, VoxelSize = 1, CellVoxels = 1, Columns = 1, Rows = 1, Cells = [new Nav3dCell(null, 0, 0, 0, 0)],
            Agents = [.. Enumerable.Range(0, 33).Select(i => new Nav3dAgent($"a{i}", default, default, 1) { Roots = [-1] })],
        }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel
        {
            CellSize = level.CellSize, VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, Columns = level.Columns, Rows = level.Rows,
            Cells = level.Cells, SpawnPoi = 3,
        }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel
        {
            CellSize = level.CellSize, VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, Columns = level.Columns, Rows = level.Rows,
            Cells = level.Cells, Doors = [new Nav3dDoor(5, 0, false, "x", -1)],
        }));
        Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(new Nav3dLevel
        {
            CellSize = level.CellSize, VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, Columns = level.Columns, Rows = level.Rows,
            Cells = level.Cells, Pois = [level.Pois[0] with { Cell = 9 }],
        }));
        foreach (Func<Nav3dAgent, Nav3dAgent> breakAgent in new Func<Nav3dAgent, Nav3dAgent>[]
        {
            a => a with { Roots = [0] },
            a => a with { AdjacencyStart = [0, 1] },
            a => a with { PoiLeaves = [] },
            a => a with { Roots = [0, 40] },
            a => a with { Components = [] },
            a => a with { Name = "a\0b" },
        })
        {
            Nav3dLevel broken = new()
            {
                CellSize = level.CellSize, VoxelSize = level.VoxelSize, CellVoxels = level.CellVoxels, Columns = level.Columns,
                Rows = level.Rows, Cells = level.Cells, Doors = level.Doors, Pois = level.Pois, Agents = [breakAgent(level.Agents[0])],
            };
            Assert.Throws<ArgumentException>(() => Nav3dWriter.Write(broken));
        }
    }

    [Fact]
    public void TheDepthHoldsTheCellAndNodeWordsPackTheirKind()
    {
        Assert.Equal([0, 1, 2, 2, 3, 4, 4], new[] { 1, 2, 3, 4, 5, 9, 16 }.Select(Nav3dFormat.DepthFor));
        Assert.Throws<ArgumentOutOfRangeException>(() => Nav3dFormat.DepthFor(0));
        uint word = Nav3dFormat.Node(Nav3dNodeKind.Free, 12345);
        Assert.Equal((Nav3dNodeKind.Free, 12345u), (Nav3dFormat.KindOf(word), Nav3dFormat.PayloadOf(word)));
        Assert.Equal(Nav3dNodeKind.Outside, Nav3dFormat.KindOf(Nav3dFormat.Node(Nav3dNodeKind.Outside, 0)));
        Assert.Equal(8, new Nav3dLeaf(0, 0, 0, 1, 0, 0, 0).Voxels);
    }
}
