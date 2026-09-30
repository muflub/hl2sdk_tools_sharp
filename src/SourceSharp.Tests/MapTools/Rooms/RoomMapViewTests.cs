//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using SourceSharp.RoomContracts;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's part of its level's map, the <c>MAPV</c> section (the rooms
/// design, 18.3): its markers read and refused, its doors, the section
/// written and read back and refused when damaged, the empty-room warning,
/// and the floors of a real compile.
/// </summary>
public sealed class RoomMapViewTests
{
    private static VmfDocument Room(params VmfChunk[] entities)
    {
        VmfDocument document = new();
        document.Chunks.Add(new VmfChunk(MapFileLoader.WorldChunk));
        foreach (VmfChunk entity in entities)
        {
            document.Chunks.Add(entity);
        }

        return document;
    }

    private static VmfChunk Poi(Vec3 at, params (string Key, string Value)[] keys) => RoomPoiTests.PoiEntity(at, keys);

    private static RoomMapView Sample() => new(
        "Great hall",
        [
            new MapPolygon(16, 16, [new(16, 16), new(240, 16), new(240, 240), new(16, 240)], [[new(64, 64), new(64, 128), new(128, 128), new(128, 64)]]),
            new MapPolygon(32, 48, [new(64, 64), new(128, 64), new(128, 128)], []),
        ],
        RoomMapView.DoorsOf(RoomHarness.Hub()),
        [new RoomMapMarker("shop", "Shop", new Vec3(100, 50, 16), 90), new RoomMapMarker("chest", string.Empty, new Vec3(1, 2, 3), 0)]);

    /// <summary>
    /// An <c>info_poi</c> with a <c>map_marker</c> is a marker at its origin
    /// and yaw with its label; one without stays navigation-only.
    /// </summary>
    [Fact]
    public void MarkersAreThePointsWithAMapMarker()
    {
        IReadOnlyList<RoomMapMarker> markers = RoomMapView.MarkersOf(Room(
            Poi(new Vec3(10, 20, 16), (LevelMap.MarkerKey, "shop"), (LevelMap.LabelKey, "Ye olde shoppe"), ("angles", "0 450 0")),
            Poi(new Vec3(30, 40, 16), ("poi_type", "cover")),
            Poi(new Vec3(50, 60, 16), (LevelMap.MarkerKey, "chest_2"))));
        Assert.Equal(
            [new RoomMapMarker("shop", "Ye olde shoppe", new Vec3(10, 20, 16), 90), new RoomMapMarker("chest_2", string.Empty, new Vec3(50, 60, 16), 0)],
            markers);
    }

    /// <summary>A marker that is not one is refused at pack time, each way with its own message naming the entity.</summary>
    [Theory]
    [InlineData("Shop", null, "info_poi 4242 has map_marker \"Shop\"; a marker kind is a lower-case letter, then lower-case letters, digits or underscores, 32 characters at most.")]
    [InlineData("2nd", null, "info_poi 4242 has map_marker \"2nd\"; a marker kind is a lower-case letter, then lower-case letters, digits or underscores, 32 characters at most.")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", null, "info_poi 4242 has map_marker \"abcdefghijklmnopqrstuvwxyzabcdefg\"; a marker kind is a lower-case letter, then lower-case letters, digits or underscores, 32 characters at most.")]
    [InlineData("spawn", null, "info_poi 4242 has map_marker \"spawn\", which the linker writes itself (spawn, arrival, exit_up, exit_down).")]
    [InlineData("exit_down", null, "info_poi 4242 has map_marker \"exit_down\", which the linker writes itself (spawn, arrival, exit_up, exit_down).")]
    [InlineData("shop", "12345678901234567890123456789012345678901234567890123456789012345", "info_poi 4242 has map_label \"12345678901234567890123456789012345678901234567890123456789012345\"; a label is at most 64 bytes of UTF-8, without a NUL.")]
    [InlineData(null, "Shop", "info_poi 4242 has map_label but no map_marker; only a marker is drawn on the level map.")]
    public void AMarkerThatIsNotOneIsRefused(string? kind, string? label, string message)
    {
        List<(string, string)> keys = [];
        if (kind is not null)
        {
            keys.Add((LevelMap.MarkerKey, kind));
        }

        if (label is not null)
        {
            keys.Add((LevelMap.LabelKey, label));
        }

        Assert.Equal(message, Assert.Throws<RoomLintException>(() => RoomMapView.MarkersOf(Room(Poi(new Vec3(1, 1, 1), [.. keys])))).Message);
    }

    /// <summary>A label of exactly 64 bytes is one, in any script (bytes, not characters, count).</summary>
    [Fact]
    public void ALabelIsCountedInBytes()
    {
        Assert.True(LevelMap.IsLabel(new string('a', 64)));
        Assert.False(LevelMap.IsLabel(new string('é', 33)));
        Assert.True(LevelMap.IsLabel(new string('é', 32)));
        Assert.False(LevelMap.IsLabel("a\0b"));
        Assert.False(LevelMap.IsLabel(null));
        Assert.True(LevelMap.IsKind("a"));
        Assert.False(LevelMap.IsKind(string.Empty));
        Assert.False(LevelMap.IsKind("a-b"));
    }

    /// <summary>
    /// A room's doors are its sockets' openings: a segment across each on the
    /// cell face, in socket order, spanning the kit's width and height.
    /// </summary>
    [Fact]
    public void TheDoorsAreTheOpeningsOnTheCellFaces()
    {
        IReadOnlyList<RoomMapDoor> doors = RoomMapView.DoorsOf(RoomHarness.Hub());
        Assert.Equal(
            [
                new RoomMapDoor(0, 256, 80, 256, 176, 80, 176),
                new RoomMapDoor(1, 0, 80, 0, 176, 80, 176),
                new RoomMapDoor(2, 80, 256, 176, 256, 80, 176),
                new RoomMapDoor(3, 80, 0, 176, 0, 80, 176),
            ],
            doors);
    }

    /// <summary>
    /// The section reads back what it wrote; an absent section, or one of a
    /// revision this build does not read, is none.
    /// </summary>
    [Fact]
    public void TheSectionRoundTrips()
    {
        RoomMapView sample = Sample();
        RoomPackSectionData section = sample.ToSection();
        Assert.Equal(RoomMapView.SectionTag, section.Tag);
        RoomMapView read = RoomMapView.Read(section.Bytes.ToArray(), RoomHarness.Hub(), null)!;
        Assert.Equal(sample.Label, read.Label);
        Assert.Equal(sample.Doors, read.Doors);
        Assert.Equal(sample.Markers, read.Markers);
        Assert.Equal(sample.Polygons.Count, read.Polygons.Count);
        for (int i = 0; i < sample.Polygons.Count; i++)
        {
            Assert.Equal((sample.Polygons[i].ZLow, sample.Polygons[i].ZHigh), (read.Polygons[i].ZLow, read.Polygons[i].ZHigh));
            Assert.Equal(sample.Polygons[i].Outer, read.Polygons[i].Outer);
            Assert.Equal(sample.Polygons[i].Holes.Count, read.Polygons[i].Holes.Count);
            for (int h = 0; h < sample.Polygons[i].Holes.Count; h++)
            {
                Assert.Equal(sample.Polygons[i].Holes[h], read.Polygons[i].Holes[h]);
            }
        }

        Assert.Equal(section.Bytes.ToArray(), read.ToSection().Bytes.ToArray());
        Assert.Null(RoomMapView.Read(null, RoomHarness.Hub(), null));
        byte[] revised = section.Bytes.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(revised.AsSpan(9), 99);
        Assert.Null(RoomMapView.Read(revised, RoomHarness.Hub(), null));
    }

    /// <summary>A damaged section is refused, naming the room, the section and what does not fit.</summary>
    [Fact]
    public void ADamagedSectionIsRefused()
    {
        byte[] section = Sample().ToSection().Bytes.ToArray();
        RoomDefinition hub = RoomHarness.Hub();
        string Refusal(byte[] bytes, RoomDefinition? definition = null) =>
            Assert.Throws<LinkException>(() => RoomMapView.Read(bytes, definition ?? hub, null)).Message;

        byte[] turns = (byte[])section.Clone();
        BinaryPrimitives.WriteInt32BigEndian(turns.AsSpan(9 + 4), 4);
        Assert.Equal("room pack entry \"hub\": its \"MAPV\" section holds 4 rotations; a room's map is stored once.", Refusal(turns));

        Assert.Equal(
            "room pack entry \"r\": its \"MAPV\" section holds 4 doors; the room has 1 sockets.",
            Refusal(section, RoomHarness.Room("r", RoomFacing.PositiveX)));

        // The first polygon's outer ring turned clockwise: its first point swapped with its second.
        byte[] turned = (byte[])section.Clone();
        int ring = 9 + 12 + 4 + "Great hall".Length + 4 + 12 + 4;
        byte[] first = turned[ring..(ring + 8)];
        turned.AsSpan(ring + 8, 8).CopyTo(turned.AsSpan(ring));
        first.CopyTo(turned.AsSpan(ring + 8));
        Assert.Equal(
            "room pack entry \"hub\": its \"MAPV\" section holds polygon 0 whose ring 0 is not a counter-clockwise outer ring of whole units.",
            Refusal(turned));

        byte[] kind = (byte[])section.Clone();
        int at = kind.AsSpan().IndexOf("shop"u8);
        "Shop"u8.CopyTo(kind.AsSpan(at));
        Assert.Equal(
            "room pack entry \"hub\": its \"MAPV\" section holds marker 0 of kind \"Shop\", which is not an author's marker.",
            Refusal(kind));

        byte[] longer = [.. section, 0];
        BinaryPrimitives.WriteInt64BigEndian(longer.AsSpan(1), BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)) + 1);
        Assert.Equal("room pack entry \"hub\": its \"MAPV\" section holds 1 bytes after its end.", Refusal(longer));
    }

    /// <summary>
    /// A room with no floor gets the pack-time warning; a room with some does
    /// not; a room whose cell size is not whole gets no map at all.
    /// </summary>
    [Fact]
    public void AnEmptyRoomIsWarnedAboutAndAFractionalCellHasNoMap()
    {
        RoomMapView empty = new(string.Empty, [], [], []);
        Assert.Equal(
            "room \"r\" has no walkable floor (no drawn face whose normal points up at least 0.7); a player cannot stand in it, and the level map shows it empty.",
            empty.EmptyWarning("r"));
        Assert.Null(Sample().EmptyWarning("r"));

        RoomDefinition fractional = new("r", 256.5f, RoomHarness.Kit, []);
        Assert.Null(RoomMapView.Build(fractional, new BspData(), Room(), [], string.Empty));
        Assert.False(RoomMapView.IsWhole(float.NaN));
        Assert.True(RoomMapView.IsWhole(256));
    }

    /// <summary>
    /// A room's compile gives its floor: the hub's interior at the floor's top,
    /// its plugs' floor the doors', its markers and label as given; bound to
    /// that compile, and the same section from a second compile of the room.
    /// </summary>
    [Fact]
    public async Task ACompiledRoomsFloorIsItsInterior()
    {
        RoomDefinition hub = RoomHarness.Hub();
        RoomLibrary library = await RoomHarness.LibraryAsync(false, hub);
        RoomObject room = library.Get("hub");
        RoomMapView view = room.MapViewOfCompile!;
        MapPolygon floor = Assert.Single(view.Polygons);
        Assert.Equal((16, 16), (floor.ZLow, floor.ZHigh));
        Assert.Equal([new(16, 16), new(240, 16), new(240, 240), new(16, 240)], floor.Outer.ToList());
        Assert.Empty(floor.Holes);
        Assert.Equal(4, view.Doors.Count);
        Assert.Empty(view.Markers);
        Assert.Equal(string.Empty, view.Label);

        RoomObject again = (await RoomHarness.LibraryAsync(false, hub)).Get("hub");
        Assert.Equal(view.ToSection().Bytes.ToArray(), again.MapViewOfCompile!.ToSection().Bytes.ToArray());
        Assert.Null((room with { Bsp = again.Bsp }).MapViewOfCompile);
    }
}
