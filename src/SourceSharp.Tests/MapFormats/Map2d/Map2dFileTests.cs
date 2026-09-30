//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Map2d;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Map2d;

/// <summary>
/// The <c>.map2d</c> file, version 1: a level round-trips through the writer
/// and the reader, the bytes are pinned, the extent and the point width follow
/// the contents, the writer refuses what no reader could draw, and the reader
/// refuses a damaged or stale file with a message that names what is wrong.
/// </summary>
public sealed class Map2dFileTests
{
    /// <summary>
    /// Two placements side by side: the first a floor with a hole and a step
    /// in the hole, the second a floor; a door each way across the joint and a
    /// closed one; a spawn and an author's labelled marker.
    /// </summary>
    internal static Map2dLevel Sample() => new()
    {
        MapChecksum = 0x12345678u,
        CellSize = 256,
        Columns = 2,
        Rows = 1,
        Rooms =
        [
            new Map2dRoom(0, 0, 0, 256, "hall", "Great hall"),
            new Map2dRoom(1, 0, 1, 384, "tower", string.Empty),
        ],
        Rings =
        [
            new Map2dRing(0, 16, 16, false, [new(16, 16), new(240, 16), new(240, 240), new(16, 240)]),
            new Map2dRing(0, 16, 16, true, [new(64, 64), new(64, 128), new(128, 128), new(128, 64)]),
            new Map2dRing(0, 32, 32, false, [new(64, 64), new(128, 64), new(128, 128), new(64, 128)]),
            new Map2dRing(1, 16, 40, false, [new(272, 16), new(496, 16), new(496, 240), new(272, 240)]),
        ],
        Doors =
        [
            new Map2dDoor(0, "east", true, 1, 256, 80, 256, 176, 16, 240),
            new Map2dDoor(1, "south", true, 0, 256, 176, 256, 80, 16, 240),
            new Map2dDoor(1, "north", false, -1, 512, 80, 512, 176, 16, 240),
        ],
        Markers =
        [
            new Map2dMarker("spawn", string.Empty, 0, 128.5f, 200, 16, 90),
            new Map2dMarker("shop", "Ye olde shoppe", 1, 400, 100, 16, 270),
        ],
    };

    /// <summary>Every section survives the writer and the reader: the same rooms, rings, doors, markers and header.</summary>
    [Fact]
    public void ALevelRoundTripsThroughEverySection()
    {
        Map2dLevel sample = Sample();
        Map2dLevel read = Map2dReader.Read(Map2dWriter.Write(sample));

        Assert.Equal(sample.MapChecksum, read.MapChecksum);
        Assert.Equal(sample.CellSize, read.CellSize);
        Assert.Equal((sample.Columns, sample.Rows), (read.Columns, read.Rows));
        Assert.Equal(sample.Rooms.AsEnumerable(), read.Rooms.AsEnumerable());
        Assert.Equal(sample.Doors.AsEnumerable(), read.Doors.AsEnumerable());
        Assert.Equal(sample.Markers.AsEnumerable(), read.Markers.AsEnumerable());
        Assert.Equal(sample.Rings.AsEnumerable(), read.Rings.AsEnumerable());
        Assert.NotEqual(sample.Rings[0], sample.Rings[0] with { Points = [.. sample.Rings[0].Points.Reverse()] });
        Assert.NotEqual(sample.Rings[0], sample.Rings[0] with { IsHole = true });

        Assert.Equal(Map2dWriter.Write(sample), Map2dWriter.Write(read));
    }

    /// <summary>
    /// The sample's bytes, pinned: the layout is the specification's, and a
    /// change to it is a new version, not an accident. The header's fields
    /// are where the document puts them.
    /// </summary>
    [Fact]
    public void TheSamplesBytesArePinned()
    {
        byte[] file = Map2dWriter.Write(Sample());
        Assert.Equal("SSMAP2D\0"u8.ToArray(), file[..8]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(96, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));
        Assert.Equal(
            [16, 16, 512, 240, 16, 240],
            Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(40 + (4 * i)))));
        Assert.Equal(
            [2, 4, 16, 3, 2],
            Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(64 + (4 * i)))));
        Assert.Equal(
            ["STRS", "ROOM", "POLY", "PNTS", "DOOR", "MARK"],
            Enumerable.Range(0, 6).Select(i => Encoding.ASCII.GetString(file, 96 + (12 * i), 4)));
        Assert.Equal(0, file.Length % 4);
        Assert.Equal(620, file.Length);
        Assert.Equal("3b374a3b0fabc110", Convert.ToHexStringLower(SHA256.HashData(file))[..16]);
    }

    /// <summary>
    /// The string table holds each string once, the empty string at offset 0,
    /// in the order the writer meets them: rooms, then doors, then markers.
    /// </summary>
    [Fact]
    public void TheStringTableHoldsEachStringOnceInFirstUseOrder()
    {
        byte[] file = Map2dWriter.Write(Sample());
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(96 + 4));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(96 + 8));
        Assert.Equal(
            "\0hall\0Great hall\0tower\0east\0south\0north\0spawn\0shop\0Ye olde shoppe\0",
            Encoding.UTF8.GetString(file, offset, length));
    }

    /// <summary>
    /// The points are <c>int16</c> pairs when every one fits and <c>int32</c>
    /// pairs when one does not, with the header's flag saying which; either
    /// reads back.
    /// </summary>
    [Fact]
    public void PointsAreShortWhenTheyFitAndWideWhenOneDoesNot()
    {
        Map2dLevel narrow = Sample();
        Map2dLevel wide = narrow with
        {
            Rings = [.. narrow.Rings, new Map2dRing(1, 0, 0, false, [new(40000, 0), new(40010, 0), new(40010, 10)])],
        };

        Assert.True(narrow.ShortPoints);
        Assert.False(wide.ShortPoints);
        byte[] file = Map2dWriter.Write(wide);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));
        Assert.Equal(new Map2dPoint(40010, 10), Map2dReader.Read(file).Rings[^1].Points[2]);
        Assert.Equal(-32768, Map2dReader.Read(Map2dWriter.Write(narrow with
        {
            Rings = [new Map2dRing(-1, 0, 0, false, [new(-32768, 0), new(0, 0), new(0, 32767)])],
            Rooms = [],
            Doors = [],
            Markers = [],
        })).Rings[0].Points[0].X);
    }

    /// <summary>
    /// The extent is the box the contents fill, floats widened outward to
    /// whole units, rings' bands and doors' and markers' z included; an empty
    /// map's is all zero.
    /// </summary>
    [Fact]
    public void TheExtentIsTheBoxTheContentsFill()
    {
        Assert.Equal(new Map2dExtent(16, 16, 512, 240, 16, 240), Sample().Extent);
        Assert.Equal(default, new Map2dLevel().Extent);
        Map2dLevel marker = new() { Markers = [new Map2dMarker("x", string.Empty, -1, -0.5f, 2.25f, -3.5f, 0)] };
        Assert.Equal(new Map2dExtent(-1, 2, 0, 3, -4, -3), marker.Extent);
    }

    /// <summary>A stale file (made for another compile of the map) is refused with both checksums; the right map reads it.</summary>
    [Fact]
    public void AStaleFileIsRefusedNamingBothChecksums()
    {
        byte[] file = Map2dWriter.Write(Sample());
        Assert.Equal(2, Map2dReader.Read(file, 0x12345678u).Rooms.Length);
        Assert.Equal(
            "the .map2d was made for the map with checksum 12345678, not this one (0000abcd): it is stale; link the level again.",
            Assert.Throws<InvalidDataException>(() => Map2dReader.Read(file, 0xabcdu)).Message);
    }

    /// <summary>The writer refuses what no reader could draw, naming it.</summary>
    [Fact]
    public void TheWriterRefusesWhatNoReaderCouldDraw()
    {
        Map2dLevel sample = Sample();
        Assert.Equal(
            "a ring has fewer than three points or its band upside down. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rings = [new Map2dRing(0, 0, 0, false, [new(0, 0), new(1, 1)])] })).Message);
        Assert.Equal(
            "a ring has fewer than three points or its band upside down. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rings = [sample.Rings[0] with { ZLow = 17 }] })).Message);
        Assert.Equal(
            "a ring names placement 2; the map has 2. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rings = [sample.Rings[0] with { Placement = 2 }] })).Message);
        Assert.Equal(
            "a door names placement -1; the map has 2. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Doors = [sample.Doors[0] with { Placement = -1 }] })).Message);
        Assert.Equal(
            "a door's neighbour names placement 5; the map has 2. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Doors = [sample.Doors[0] with { Neighbour = 5 }] })).Message);
        Assert.Equal(
            "a room has rotation 4; a placement turns 0 to 3 quarter turns. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rooms = [sample.Rooms[0] with { Rotation = 4 }, sample.Rooms[1]] })).Message);
        Assert.Equal(
            "a marker's kind is null or holds a NUL. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Markers = [sample.Markers[0] with { Kind = "a\0b" }] })).Message);
        Assert.Equal(
            "a marker has a coordinate that is not a finite number. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Markers = [sample.Markers[0] with { Yaw = float.NaN }] })).Message);
        Assert.Equal(
            "a door has a coordinate that is not a finite number. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Doors = [sample.Doors[0] with { X0 = float.PositiveInfinity }] })).Message);
        Assert.Equal(
            "a room has a coordinate that is not a finite number. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rooms = [sample.Rooms[0] with { Height = float.NaN }, sample.Rooms[1]] })).Message);
        Assert.Equal(
            "a room's label is null or holds a NUL. (Parameter 'level')",
            Assert.Throws<ArgumentException>(() => Map2dWriter.Write(sample with { Rooms = [sample.Rooms[0] with { Label = null! }, sample.Rooms[1]] })).Message);
    }

    /// <summary>The reader refuses a damaged file, each way with its own message.</summary>
    [Theory]
    [MemberData(nameof(Damages))]
    public void TheReaderRefusesADamagedFile(string damage, string message)
    {
        byte[] file = Map2dWriter.Write(Sample());
        byte[] broken = Damage(file, damage);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => Map2dReader.Read(broken)).Message);
    }

    /// <summary>The damages and the messages they get.</summary>
    public static TheoryData<string, string> Damages => new()
    {
        { "magic", "not a .map2d file: the magic is missing." },
        { "short", "the .map2d file is 40 bytes, shorter than its 96-byte header." },
        { "version", ".map2d version 2; this build reads version 1." },
        { "sections", "the .map2d header's size or section count is out of range." },
        { "flags", "the .map2d header has unknown flags 0x00000003." },
        { "grid", "the .map2d header's grid or counts are out of range." },
        { "offset", "the .map2d section \"ROOM\" lies outside the file or off a four-byte boundary." },
        { "duplicate", "the .map2d file has two \"STRS\" sections." },
        { "missing", "the .map2d file has no \"MARK\" section." },
        { "count", "the .map2d \"ROOM\" section is 24 bytes, which its counts do not allow." },
        { "strings", "the .map2d string table must start with the empty string and end with a NUL." },
        { "string", "string offset 2 is not the start of a string in the 66-byte string table." },
        { "rotation", "room 0 has rotation 7, not 0 to 3." },
        { "ring", "ring 0 has flags 0x00000000, band 16 to 16 and points 0 + 2 of 16; one is out of range." },
        { "placement", "ring 0 names placement 9; the file has 2." },
        { "door", "door 0 has unknown flags 0x00000004." },
        { "neighbour", "door 0's neighbour names placement -2; the file has 2." },
        { "marker", "marker 0 has a coordinate that is not a finite number." },
        { "extent", "the .map2d header's extent is not the box its contents fill." },
    };

    private static byte[] Damage(byte[] file, string damage)
    {
        byte[] b = (byte[])file.Clone();
        int Section(string tag)
        {
            for (int i = 0; i < 6; i++)
            {
                if (Encoding.ASCII.GetString(b, 96 + (12 * i), 4) == tag)
                {
                    return (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(96 + (12 * i) + 4));
                }
            }

            throw new InvalidOperationException(tag);
        }

        switch (damage)
        {
            case "magic":
                b[0] = (byte)'X';
                return b;
            case "short":
                return b[..40];
            case "version":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 2);
                return b;
            case "sections":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 65);
                return b;
            case "flags":
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 3);
                return b;
            case "grid":
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28), 0);
                return b;
            case "offset":
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96 + 12 + 4), (uint)b.Length);
                return b;
            case "duplicate":
                Encoding.ASCII.GetBytes("STRS", b.AsSpan(96 + 12));
                return b;
            case "missing":
                Encoding.ASCII.GetBytes("XXXX", b.AsSpan(96 + (5 * 12)));
                return b;
            case "count":
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96 + 12 + 8), 48);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(64), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96 + 12 + 8), 24);
                return b;
            case "strings":
                b[Section("STRS")] = (byte)'a';
                return b;
            case "string":
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Section("ROOM") + 16), 2);
                return b;
            case "rotation":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Section("ROOM") + 8), 7);
                return b;
            case "ring":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Section("POLY") + 20), 2);
                return b;
            case "placement":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Section("POLY")), 9);
                return b;
            case "door":
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Section("DOOR") + 8), 4);
                return b;
            case "neighbour":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Section("DOOR") + 12), -2);
                return b;
            case "marker":
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(Section("MARK") + 12), float.NaN);
                return b;
            case "extent":
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(48), 999);
                return b;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    /// <summary>A section the reader does not know is skipped: a later build may add one.</summary>
    [Fact]
    public void AnUnknownSectionIsSkipped()
    {
        byte[] file = Map2dWriter.Write(Sample());
        byte[] grown = new byte[file.Length + 12 + 4];

        // Header, then the six entries and a seventh, then the sections moved
        // on by the new entry's twelve bytes (plus four of padding at the end).
        file.AsSpan(0, 96 + (6 * 12)).CopyTo(grown);
        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(16), 7);
        int start = 96 + (6 * 12);
        file.AsSpan(start).CopyTo(grown.AsSpan(start + 12));
        for (int i = 0; i < 6; i++)
        {
            Span<byte> entry = grown.AsSpan(96 + (12 * i) + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, BinaryPrimitives.ReadUInt32LittleEndian(entry) + 12);
        }

        Encoding.ASCII.GetBytes("ZZZZ", grown.AsSpan(start));
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(start + 4), (uint)(grown.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(start + 8), 4);
        Assert.Equal(Sample().Markers.AsEnumerable(), Map2dReader.Read(grown).Markers.AsEnumerable());
    }

    /// <summary>
    /// The SVG preview is a function of the map: pinned text for the sample,
    /// floors by band (the step drawn after the floor), doors by state,
    /// markers with their kind and label, a room's label at its cell's
    /// centre, the y axis flipped, and text escaped.
    /// </summary>
    [Fact]
    public void TheSvgPreviewIsPinned()
    {
        string svg = Map2dSvg.Write(Sample());
        Assert.StartsWith(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-16 -16 560 288\" width=\"560\" height=\"288\">\n",
            svg,
            StringComparison.Ordinal);
        Assert.Contains("<g transform=\"matrix(1 0 0 -1 0 256)\">\n", svg, StringComparison.Ordinal);
        Assert.Contains(
            "<path class=\"floor\" fill=\"rgb(150,150,170)\" d=\"M16 16 L240 16 L240 240 L16 240 Z M64 64 L64 128 L128 128 L128 64 Z\"/>\n"
            + "<path class=\"floor\" fill=\"rgb(150,150,170)\" d=\"M272 16 L496 16 L496 240 L272 240 Z\"/>\n"
            + "<path class=\"floor\" fill=\"rgb(230,230,250)\" d=\"M64 64 L128 64 L128 128 L64 128 Z\"/>\n",
            svg,
            StringComparison.Ordinal);
        Assert.Contains("<line class=\"open\" x1=\"256\" y1=\"80\" x2=\"256\" y2=\"176\"/>\n", svg, StringComparison.Ordinal);
        Assert.Contains("<line class=\"closed\" x1=\"512\" y1=\"80\" x2=\"512\" y2=\"176\"/>\n", svg, StringComparison.Ordinal);
        Assert.Contains("<circle class=\"marker\" cx=\"128.5\" cy=\"200\" r=\"8\"/>\n", svg, StringComparison.Ordinal);
        Assert.Contains("<text x=\"138.5\" y=\"60\">spawn</text>\n", svg, StringComparison.Ordinal);
        Assert.Contains("<text x=\"410\" y=\"160\">shop: Ye olde shoppe</text>\n", svg, StringComparison.Ordinal);
        Assert.Contains("<text x=\"128\" y=\"128\" text-anchor=\"middle\">Great hall</text>\n", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>\n", svg, StringComparison.Ordinal);
        Assert.Equal("085c966a44f78669", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(svg)))[..16]);

        string escaped = Map2dSvg.Write(Sample() with { Markers = [new Map2dMarker("sign", "<a & \"b\">", -1, 0, 0, 0, 0)] });
        Assert.Contains(">sign: &lt;a &amp; &quot;b&quot;&gt;</text>", escaped, StringComparison.Ordinal);
    }

    /// <summary>A map made without a level file has no cells, so no room label is drawn; an empty map is a blank page.</summary>
    [Fact]
    public void AMapWithoutCellsDrawsNoRoomLabels()
    {
        string svg = Map2dSvg.Write(Sample() with { CellSize = 0, Columns = 0, Rows = 0 });
        Assert.DoesNotContain("Great hall", svg, StringComparison.Ordinal);
        Assert.Equal(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-32 -32 64 64\" width=\"64\" height=\"64\">\n",
            Map2dSvg.Write(new Map2dLevel()).Split('\n')[0] + "\n");
    }

    /// <summary>An empty map round-trips: every section present and empty but the string table's one NUL.</summary>
    [Fact]
    public void AnEmptyMapRoundTrips()
    {
        byte[] file = Map2dWriter.Write(new Map2dLevel());
        Map2dLevel read = Map2dReader.Read(file);
        Assert.Empty(read.Rooms);
        Assert.Empty(read.Rings);
        Assert.Empty(read.Doors);
        Assert.Empty(read.Markers);
    }
}
