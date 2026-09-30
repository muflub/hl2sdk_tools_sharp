//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>Three cooked rooms, compiled once for the link-data facts, and a level that places them at all four turns.</summary>
public sealed class RoomLinkDataFixture : IAsyncLifetime
{
    /// <summary>The rooms, compiled with the managed cooker and without link data.</summary>
    internal RoomLibrary Library { get; private set; } = null!;

    /// <summary>A level placing the rooms at every quarter turn, with joints and caps.</summary>
    internal LevelLayout Layout { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Library = await RoomHarness.LibraryAsync(
            cook: true,
            RoomHarness.Hub(),
            RoomHarness.Room("end", RoomFacing.PositiveX),
            RoomHarness.Room("hall", RoomFacing.PositiveX, RoomFacing.NegativeX));
        Layout = RoomHarness.AutoLayout(
            "turns",
            Library,
            ("hub", 1, 1, 0),
            ("hall", 2, 1, 0),
            ("end", 3, 1, 2),
            ("hall", 1, 2, 1),
            ("end", 1, 3, 3),
            ("end", 0, 1, 0));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The link work a room compile does ahead (<see cref="RoomLinkData"/>) and
/// the pack sections that carry it: the split of each move into a turn and
/// a translation changes no bit, the stored data is what the link computes,
/// every section round-trips under every codec, compression is
/// deterministic, damaged sections are refused, and a level links to the
/// same bytes from a pack with the sections, with some of them, or with none.
/// </summary>
public sealed class RoomLinkDataTests(RoomLinkDataFixture fixture) : IClassFixture<RoomLinkDataFixture>
{
    /// <summary>The pinned Deflate bytes of <see cref="TheCodecBytesArePinned"/>: length, then SHA-256.</summary>
    /// <remarks>
    /// Microsoft's .NET 10 runtime, the one CI runs and CLAUDE.md asks for:
    /// it ships its own zlib-ng, and Linux and Windows agree on these bytes.
    /// A distribution-packaged runtime (Ubuntu's 10.0.12, for one) links the
    /// system's zlib instead and writes different, equally valid, Deflate
    /// bytes (6105 of them here), so this fact fails there by design: a pack
    /// written with Deflate on such a runtime is not the pack CI's runtime
    /// writes. Brotli's bytes agree on both.
    /// </remarks>
    private const string PinnedDeflate = "6093:081FA0B37B190BA6FBD607E181F54E3297A0A39215FC5F27564F57C996955C18";

    /// <summary>The pinned Brotli bytes of <see cref="TheCodecBytesArePinned"/>: length, then SHA-256.</summary>
    private const string PinnedBrotli = "2090:1E0323E3971086167CE7B6DD603D07D27D82D65371D634E684EB315CE343AAC8";

    /// <summary>Every per-turn part, stored or not by default.</summary>
    private const RoomLinkParts All = RoomLinkParts.Geometry | RoomLinkParts.Collision | RoomLinkParts.Entities;

    private static readonly float[] Coordinates = [0f, -0f, 1.5f, -3.25f, 1e-7f, -1e-7f, 123456.78f, -98765.43f, 0.1f, 255.99f, 16777216f];

    // ---- the split changes no bit ---------------------------------------------

    /// <summary>
    /// A point turned by <see cref="RoomTransform.Rotate"/> and then moved by
    /// <see cref="RoomTransform.Translate"/> is bit for bit the point
    /// <see cref="RoomTransform.Apply"/> gives, negative zero and fractions
    /// included, at every turn and on cells either side of the origin.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TranslatingATurnedPointIsApplyingTheTransform(int rotation)
    {
        foreach (float cellSize in new[] { 256f, 96.5f })
        {
            foreach ((int cx, int cy) in new[] { (0, 0), (3, -2), (-7, 5) })
            {
                RoomTransform transform = new(new RoomPlacement("r", cx, cy, rotation), cellSize);
                foreach (float x in Coordinates)
                {
                    foreach (float y in Coordinates)
                    {
                        Vec3 p = new(x, y, -x);
                        AssertBits(transform.Apply(p), transform.Translate(RoomTransform.Rotate(p, rotation)));
                    }
                }
            }
        }
    }

    /// <summary>
    /// A box turned by <see cref="LevelLinker.RotateBox"/> and moved by
    /// <see cref="RoomTransform.TranslateBox"/> is bit for bit the box
    /// <see cref="LevelLinker.MoveBox"/> gives from all eight corners.
    /// </summary>
    [Fact]
    public void TranslatingATurnedBoxIsMovingTheBox()
    {
        Random random = new(1234);
        for (int i = 0; i < 2000; i++)
        {
            Vec3 a = new(Pick(random), Pick(random), Pick(random));
            Vec3 b = new(Pick(random), Pick(random), Pick(random));
            int rotation = i % 4;
            RoomTransform transform = new(new RoomPlacement("r", random.Next(-9, 9), random.Next(-9, 9), rotation), 256f);
            Box moved = LevelLinker.MoveBox(transform, a, b);
            Box split = transform.TranslateBox(LevelLinker.RotateBox(a, b, rotation));
            AssertBits(moved.Mins, split.Mins);
            AssertBits(moved.Maxs, split.Maxs);
        }

        static float Pick(Random random) => random.Next(4) == 0
            ? Coordinates[random.Next(Coordinates.Length)]
            : (float)((random.NextDouble() - 0.5) * 4000);
    }

    /// <summary>
    /// The planes and texture axes through the split (turn, then translate)
    /// are bit for bit what the one-step move this build replaced gives,
    /// over a real room's lumps at every turn.
    /// </summary>
    [Fact]
    public void TheSplitPlanesAndAxesMatchTheOneStepMove()
    {
        RoomObject hub = fixture.Library.Get("hub");
        DPlane[] planes = BspStructView.As<DPlane>(hub.Bsp[BspLump.Planes]).ToArray();
        TexInfo[] infos = BspStructView.As<TexInfo>(hub.Bsp[BspLump.TexInfo]).ToArray();
        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomTransform transform = new(new RoomPlacement("hub", 2, -3, rotation), RoomHarness.Cell);
            (DPlane[] expected, bool[] expectedSwapped) = OneStepPlanes((DPlane[])planes.Clone(), transform);
            (DPlane[] actual, bool[] swapped) = LevelLinker.TransformPlanes((DPlane[])planes.Clone(), transform);
            Assert.Equal(expectedSwapped, swapped);
            Assert.True(Bytes(expected).SequenceEqual(Bytes(actual)), $"planes at turn {rotation}");

            TexInfo[] oneStep = OneStepTexInfos((TexInfo[])infos.Clone(), transform);
            TexInfo[] split = LevelLinker.TransformTexInfos((TexInfo[])infos.Clone(), transform);
            Assert.True(Bytes(oneStep).SequenceEqual(Bytes(split)), $"texinfos at turn {rotation}");
        }
    }

    /// <summary>A plane lump of odd length is refused when it is turned, as it was when it was moved.</summary>
    [Fact]
    public void AnOddPlaneLumpIsRefused()
    {
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.RotatePlanes(new DPlane[3], 1));
        Assert.Contains("odd number of planes", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A collision convex through the split is byte for byte the one-step move this build replaced.</summary>
    [Fact]
    public void TheSplitLedgeMoveMatchesTheOneStepMove()
    {
        RoomObject hall = fixture.Library.Get("hall");
        LevelLinker.RoomCollide collide = LevelLinker.ReadRoomCollide(hall.Bsp, "hall");
        List<IvpCompactLedge> ledges = IvpCollideQueries.Leaves(IvpCollideQueries.Surface(collide.Solids[0].Blob));
        Assert.NotEmpty(ledges);
        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomTransform transform = new(new RoomPlacement("hall", -4, 7, rotation), RoomHarness.Cell);
            foreach (IvpCompactLedge ledge in ledges)
            {
                IvpCompactLedge oneStep = new((byte[])ledge.Bytes.Clone());
                OneStepLedge(oneStep, transform);
                IvpCompactLedge split = new((byte[])ledge.Bytes.Clone());
                LevelLinker.MoveLedge(split, transform);
                Assert.Equal(oneStep.Bytes, split.Bytes);
            }
        }
    }

    /// <summary>An entity through the split (turn its keys, then move its origin) reads as the one-step move gave it.</summary>
    [Fact]
    public void TheSplitEntityMoveMatchesTheOneStepMove()
    {
        BspEntity light = new();
        foreach ((string key, string value) in new[]
        {
            ("classname", "light_spot"), ("origin", "10.5 -20.25 30"), ("angles", "-45 300.5 0"),
            ("angle", "90"), ("Angle", "-1"), ("ANGLES", "0 -2 0"), ("_light", "1 2 3"),
        })
        {
            light.Pairs.Add(new BspKeyValue(key, value));
        }

        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomTransform transform = new(new RoomPlacement("r", 3, -1, rotation), 256f);
            BspEntity expected = OneStepEntity(light, transform);
            BspEntity actual = LevelLinker.MoveEntity(light, transform, "r");
            Assert.Equal(expected.Pairs, actual.Pairs);
        }
    }

    // ---- stored equals computed -------------------------------------------------

    /// <summary>
    /// What a room compile stores, read back from its pack sections, is
    /// exactly what the link computes on the fly for the same room and turn:
    /// every geometry array bit for bit, every collision convex byte for
    /// byte, every entity key; and the census for every socket.
    /// </summary>
    [Fact]
    public async Task TheStoredDataEqualsTheLinkTimeComputationPerRotation()
    {
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(room, CancellationToken.None))!;
            Assert.NotNull(data);
            Assert.True(data.IsFor(room));
            RoomLinkData read = ReadBack(room, RoomLinkSections.Write(data, All))!;
            AssertSameShared(LevelLinker.ComputeShared(room), read.Shared);
            for (int rotation = 0; rotation < 4; rotation++)
            {
                RoomLinkRotation turn = read.Rotation(rotation)!;
                AssertSameGeometry(LevelLinker.ComputeGeometry(room, rotation), turn.Geometry!);
                AssertSameCollision(LevelLinker.ComputeCollision(room, rotation)!, turn.Collision!);
                AssertSameEntities(LevelLinker.ComputeEntities(room, rotation), turn.Entities!);
            }
        }
    }

    /// <summary>
    /// Every section round-trips under every codec: written and read back,
    /// the shared census and each turn's geometry, collision and entities
    /// are the data written; the sections come in pack order, one turn's
    /// three together.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EverySectionRoundTrips(int codecByte)
    {
        RoomLinkCodec codec = (RoomLinkCodec)codecByte;
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        IReadOnlyList<RoomPackSectionData> sections = RoomLinkSections.Write(data, All, codec);
        Assert.Equal(
            ["LNKA", "DVIS", "GEO0", "COL0", "ENT0", "GEO1", "COL1", "ENT1", "GEO2", "COL2", "ENT2", "GEO3", "COL3", "ENT3"],
            sections.Select(s => s.Tag));
        Assert.All(sections, s => Assert.Equal((byte)codec, s.Bytes.Span[0]));

        RoomLinkData read = ReadBack(hub, sections)!;
        AssertSameShared(data.Shared, read.Shared);
        Assert.True(data.Doors!.SameAs(read.Doors!));
        for (int rotation = 0; rotation < 4; rotation++)
        {
            AssertSameGeometry(data.Rotation(rotation)!.Geometry!, read.Rotation(rotation)!.Geometry!);
            AssertSameCollision(data.Rotation(rotation)!.Collision!, read.Rotation(rotation)!.Collision!);
            AssertSameEntities(data.Rotation(rotation)!.Entities!, read.Rotation(rotation)!.Entities!);
        }
    }

    /// <summary>
    /// A compressed section is a function of its payload: the same bytes when
    /// written again, and the same bytes from eight threads at once; its
    /// length prefix is the payload's, and it decompresses to exactly the
    /// uncompressed section's payload.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompressedSectionsAreDeterministic(int codecByte)
    {
        RoomLinkCodec codec = (RoomLinkCodec)codecByte;
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(fixture.Library.Get("hall"), CancellationToken.None))!;
        byte[][] once = [.. RoomLinkSections.Write(data, All, codec).Select(s => s.Bytes.ToArray())];
        string[] tags = [.. RoomLinkSections.Write(data, All).Select(s => s.Tag)];
        byte[][] raw = [.. RoomLinkSections.Write(data, All).Select(s => s.Bytes.ToArray())];
        byte[][][] parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => RoomLinkSections.Write(data, All, codec).Select(s => s.Bytes.ToArray()).ToArray())));
        foreach (byte[][] again in parallel.Append(RoomLinkSections.Write(data, All, codec).Select(s => s.Bytes.ToArray()).ToArray()))
        {
            Assert.Equal(once.Length, again.Length);
            for (int s = 0; s < once.Length; s++)
            {
                Assert.Equal(once[s], again[s]);
            }
        }

        for (int s = 0; s < once.Length; s++)
        {
            Assert.Equal(raw[s].Length - 9, BinaryPrimitives.ReadInt64BigEndian(once[s].AsSpan(1)));
            RoomLinkSections.Reader? reader = RoomLinkSections.Open(new ArraySegment<byte>(once[s]), "hall", tags[s]);
            Assert.NotNull(reader);
        }
    }

    /// <summary>
    /// The codecs' bytes are pinned: a fixed input compressed with Deflate at
    /// zlib level 9 and with Brotli at quality 11, window 22 hashes to the
    /// checked-in value, on every operating system CI runs. The runtime
    /// ships its own zlib-ng and Brotli, so the bytes are expected to agree
    /// everywhere; if they ever differ, that is a runtime change to decide
    /// on, not a golden to refresh.
    /// </summary>
    [Fact]
    public void TheCodecBytesArePinned()
    {
        byte[] input = new byte[8192];
        uint x = 0x12345678;
        for (int i = 0; i < input.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            input[i] = (byte)(i % 3 == 0 ? (int)(x & 0x0f) : i & 0xff);
        }

        byte[] deflate = RoomLinkSections.DeflateBytes(input);
        byte[] brotli = RoomLinkSections.BrotliBytes(input);
        Assert.Equal(PinnedDeflate, $"{deflate.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(deflate))}");
        Assert.Equal(PinnedBrotli, $"{brotli.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(brotli))}");

        byte[] section = RoomLinkSections.Encode(input, RoomLinkCodec.Deflate);
        Assert.Equal((byte)RoomLinkCodec.Deflate, section[0]);
        Assert.Equal(input.Length, BinaryPrimitives.ReadInt64BigEndian(section.AsSpan(1)));
        Assert.Equal(deflate, section[9..]);
    }

    /// <summary>
    /// A section of a revision this build does not know is taken as absent:
    /// the part is computed at link, and an unreadable <c>LNKA</c> leaves the
    /// room with no link data at all. A codec this build does not know is
    /// refused, naming the room, the section and the codec.
    /// </summary>
    [Fact]
    public async Task AnUnknownRevisionReadsAsAbsentAndAnUnknownCodecIsRefused()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        List<RoomPackSectionData> sections = [.. RoomLinkSections.Write(data, All)];

        LinkException refused = Assert.Throws<LinkException>(
            () => ReadBack(hub, [.. sections.Select(s => s.Tag == "GEO1" ? s with { Bytes = With(s.Bytes, 0, 9) } : s)]));
        Assert.Equal("room hub: section GEO1 uses codec 9, which this build does not read.", refused.Message);

        RoomLinkData? read = ReadBack(hub, [.. sections.Select(s => s.Tag == "ENT2" ? s with { Bytes = WithInt(s.Bytes, 9, 99) } : s)]);
        Assert.Null(read!.Rotation(2)!.Entities);
        Assert.NotNull(read.Rotation(2)!.Geometry);

        Assert.Null(ReadBack(hub, [.. sections.Select(s => s.Tag == "LNKA" ? s with { Bytes = WithInt(s.Bytes, 9, 2) } : s)]));
        Assert.Null(ReadBack(hub, [.. sections.Where(s => s.Tag != "LNKA")]));
        Assert.Null(ReadBack(hub, [.. sections.Where(s => s.Tag == "LNKA")])!.Rotation(0));
    }

    /// <summary>
    /// A section that does not fit the room it sits with, or is cut short, is
    /// refused, naming the room and the section, rather than linked.
    /// </summary>
    [Theory]
    [InlineData("LNKA", "cut")]
    [InlineData("LNKA", "sockets")]
    [InlineData("LNKA", "index")]
    [InlineData("GEO0", "count")]
    [InlineData("GEO0", "flag")]
    [InlineData("GEO0", "trailing")]
    [InlineData("COL1", "ledge")]
    [InlineData("ENT3", "cut")]
    [InlineData("GEO2", "deflate")]
    [InlineData("GEO2", "deflate-trailing")]
    [InlineData("GEO1", "brotli")]
    [InlineData("GEO1", "length")]
    [InlineData("GEO1", "short")]
    [InlineData("GEO3", "raw-length")]
    [InlineData("COL0", "terrain")]
    [InlineData("COL0", "utf8")]
    [InlineData("COL0", "solids")]
    public async Task ADamagedSectionIsRefused(string tag, string damage)
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        RoomLinkCodec codec = damage switch
        {
            "deflate" or "deflate-trailing" => RoomLinkCodec.Deflate,
            "brotli" or "length" or "short" => RoomLinkCodec.Brotli,
            _ => RoomLinkCodec.None,
        };
        List<RoomPackSectionData> sections = [.. RoomLinkSections.Write(data, All, codec)];
        int at = sections.FindIndex(s => s.Tag == tag);
        byte[] bytes = sections[at].Bytes.ToArray();
        int vertices = BspStructView.Count<Vec3>(hub.Bsp[BspLump.Vertexes]);
        int planePairs = BspStructView.Count<DPlane>(hub.Bsp[BspLump.Planes]) / 2;
        bytes = damage switch
        {
            "cut" => bytes[..(bytes.Length - 3)],
            "sockets" => WithInt(bytes, 13, 9),                                    // socket count
            "index" => WithInt(bytes, 21, 1 << 20),                              // socket 0's first facing cluster
            "count" => WithInt(bytes, 13, vertices + 1),                          // vertex count
            "flag" => With(bytes, 13 + 4 + (12 * vertices) + 4 + (20 * planePairs) + 4, 2), // first swap byte
            "trailing" => [.. bytes, 0],
            "ledge" => WithInt(bytes, MaterialsEnd(bytes) + 17 + 8, 0x7fffff04),  // first ledge's size word
            "deflate" => [.. bytes[..9], .. Enumerable.Repeat((byte)0xff, bytes.Length - 9)],
            "deflate-trailing" => WithLong(bytes, 1, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1)) - 1), // decodes one byte long
            "brotli" => [.. bytes[..9], .. Enumerable.Repeat((byte)0x5a, bytes.Length - 9)],
            "length" => WithLong(bytes, 1, -1),
            "short" => bytes[..7],
            "raw-length" => WithLong(bytes, 1, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1)) + 1),
            "terrain" => With(bytes, MaterialsEnd(bytes), 7),                     // the virtual-terrain flag
            "utf8" => With(bytes, 13 + 4 + 4, 0xff),                              // the first material name's first byte
            "solids" => WithInt(bytes, MaterialsEnd(bytes) + 1, -1),              // the solid count
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        sections[at] = sections[at] with { Bytes = bytes };

        LinkException refused = Assert.Throws<LinkException>(() => ReadBack(hub, sections));
        Assert.Matches($"^room (pack entry \"hub\": its \"{tag}\" section|hub: section {tag}) ", refused.Message);

        // Where a collision section's material names end: its virtual-terrain
        // flag; the solid count, the first solid's contents, ledge count and
        // byte length follow, then the first ledge (17 bytes on).
        static int MaterialsEnd(byte[] section)
        {
            int p = 13;
            int materials = BinaryPrimitives.ReadInt32BigEndian(section.AsSpan(p));
            Assert.True(materials > 0);
            p += 4;
            for (int m = 0; m < materials; m++)
            {
                p += 4 + BinaryPrimitives.ReadInt32BigEndian(section.AsSpan(p));
            }

            return p;
        }
    }

    /// <summary>
    /// Writing link data that lacks a turn or a part the writer is asked for,
    /// or an entity the link cannot move, or with a codec that does not
    /// exist, is refused: a pack never holds a section that says less than
    /// its tag promises.
    /// </summary>
    [Fact]
    public async Task WritingIncompleteLinkDataIsRefused()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        RoomLinkRotation?[] turns = [.. Enumerable.Range(0, 4).Select(data.Rotation)];

        RoomLinkRotation?[] missing = [.. turns];
        missing[2] = null;
        Assert.Throws<ArgumentException>(() => RoomLinkSections.Write(new RoomLinkData(hub.Definition, hub.Bsp, hub.Vis, data.Shared, missing)));

        RoomLinkRotation?[] noGeometry = [.. turns];
        noGeometry[1] = noGeometry[1]! with { Geometry = null };
        Assert.Throws<ArgumentException>(() => RoomLinkSections.Write(new RoomLinkData(hub.Definition, hub.Bsp, hub.Vis, data.Shared, noGeometry)));

        RoomLinkRotation?[] broken = [.. turns];
        broken[0] = broken[0]! with
        {
            Entities = new RoomLinkEntities([new RoomLinkEntity(false, [], null, "a key that is not a number")]),
        };
        Assert.Throws<ArgumentException>(() => RoomLinkSections.Write(new RoomLinkData(hub.Definition, hub.Bsp, hub.Vis, data.Shared, broken), All));

        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkSections.Encode([1, 2, 3], (RoomLinkCodec)7));
        Assert.Throws<ArgumentException>(() => new RoomLinkData(hub.Definition, hub.Bsp, hub.Vis, data.Shared, new RoomLinkRotation?[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkSections.GeometryTag(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkSections.CollisionTag(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomLinkSections.EntitiesTag(4));
    }

    /// <summary>
    /// With stored link data the link still refuses a jointed socket that
    /// faces no open leaf, with the message it gives when it works the
    /// census out itself: the level decides which sockets are jointed.
    /// </summary>
    [Fact]
    public async Task AStoredCensusStillRefusesAJointThatFacesNothing()
    {
        List<RoomObject> rooms = [];
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(room, CancellationToken.None))!;
            if (room.Definition.Name == "hall")
            {
                SocketCensus[] sockets = [.. data.Shared.Sockets.Select(s => s with { Facing = [] })];
                data = new RoomLinkData(room.Definition, room.Bsp, room.Vis, new RoomLinkShared(sockets), [.. Enumerable.Range(0, 4).Select(data.Rotation)]);
            }

            rooms.Add(room with { Link = data });
        }

        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkBytesAsync(RoomHarness.Library([.. rooms]), 1));
        Assert.Contains("room hall's jointed socket", refused.Message, StringComparison.Ordinal);
        Assert.Contains("faces no open leaf of the room", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room's pack item carries, by default, the shared census and each
    /// turn's geometry and collision, uncompressed, and not the entities (the
    /// measured choice, <see cref="RoomLinkSections.StoredParts"/>); a room
    /// without collision has no collision sections. The item uses the link
    /// data the library compile already worked out when it still fits.
    /// </summary>
    [Fact]
    public async Task APackItemStoresTheGeometryAndCollisionByDefault()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomPackItem item = await RoomPackItem.CreateAsync(hub);
        Assert.Equal(
            ["ECNT", "LNKA", "DVIS", "GEO0", "COL0", "NAM0", "GEO1", "COL1", "NAM1", "GEO2", "COL2", "NAM2", "GEO3", "COL3", "NAM3"],
            item.Extra.Select(s => s.Tag));
        Assert.All(item.Extra, s => Assert.Equal((byte)RoomLinkCodec.None, s.Bytes.Span[0]));

        RoomLinkData data = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        RoomPackItem reused = await RoomPackItem.CreateAsync(hub with { Link = data });
        Assert.Equal(item.Extra.Select(s => s.Bytes.ToArray()), reused.Extra.Select(s => s.Bytes.ToArray()));

        RoomObject bare = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.PhysCollide, Array.Empty<byte>()));
        RoomPackItem uncooked = await RoomPackItem.CreateAsync(bare);
        Assert.Equal(["ECNT", "LNKA", "DVIS", "GEO0", "NAM0", "GEO1", "NAM1", "GEO2", "NAM2", "GEO3", "NAM3"], uncooked.Extra.Select(s => s.Tag));
    }

    // ---- the link reads them --------------------------------------------------

    /// <summary>
    /// A level links to the same bytes from rooms with no link data (the
    /// fallback: every check, census and turn computed at link), from a pack
    /// with every section, from packs with only some of them, and from a pack
    /// with compressed sections; at one thread and at four.
    /// </summary>
    [Fact]
    public async Task APackWithTheSectionsLinksByteIdenticallyToOneWithout()
    {
        byte[] expected = await LinkBytesAsync(fixture.Library, 1);
        Assert.Equal(expected, await LinkBytesAsync(fixture.Library, 4));

        foreach ((RoomLinkParts parts, RoomLinkCodec codec) in new[]
        {
            (RoomLinkSections.StoredParts, RoomLinkCodec.None),
            (All, RoomLinkCodec.None),
            (RoomLinkParts.None, RoomLinkCodec.None),
            (RoomLinkParts.Geometry, RoomLinkCodec.None),
            (RoomLinkParts.Collision | RoomLinkParts.Entities, RoomLinkCodec.None),
            (All, RoomLinkCodec.Brotli),
            (All, RoomLinkCodec.Deflate),
        })
        {
            RoomLibrary packed = await PackedLibraryAsync(parts, codec, turnsOnly: true);
            Assert.All(packed.Rooms, r => Assert.NotNull(r.Link));
            byte[] serial = await LinkBytesAsync(packed, 1);
            byte[] parallel = await LinkBytesAsync(packed, 4);
            Assert.True(expected.AsSpan().SequenceEqual(serial), $"{parts} {codec}");
            Assert.True(expected.AsSpan().SequenceEqual(parallel), $"{parts} {codec} at 4 threads");
        }

        // A pack written before the sections: containers only, no link data.
        RoomLibrary old = await PackedLibraryAsync(parts: null, RoomLinkCodec.None, turnsOnly: false);
        Assert.All(old.Rooms, r => Assert.Null(r.Link));
        Assert.Equal(expected, await LinkBytesAsync(old, 1));
    }

    /// <summary>
    /// Link data that no longer describes its room (a room copied with a
    /// replaced compile, or given another room's data) is ignored: the link
    /// computes afresh and writes what the room without it writes.
    /// </summary>
    [Fact]
    public async Task StaleLinkDataIsIgnored()
    {
        RoomObject hub = fixture.Library.Get("hub");
        RoomObject hall = fixture.Library.Get("hall");
        RoomLinkData hallLink = (await LevelLinker.TryPrecomputeAsync(hall, CancellationToken.None))!;
        RoomLinkData hubLink = (await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None))!;
        RoomObject wrong = hub with { Link = hallLink };
        RoomObject copied = RoomHarness.WithLumps(hub with { Link = hubLink }, _ => { });
        Assert.False(hallLink.IsFor(wrong));
        Assert.False(hubLink.IsFor(copied));

        byte[] expected = await LinkBytesAsync(fixture.Library, 1);
        foreach (RoomObject stale in new[] { wrong, copied })
        {
            RoomLibrary library = RoomHarness.Library(stale, fixture.Library.Get("end"), hall);
            Assert.Equal(expected, await LinkBytesAsync(library, 1));
        }
    }

    /// <summary>
    /// A room the link refuses gets no link data and is packed with its
    /// container and entity counts alone, and a level placing it is refused at link with the
    /// message the link always gave.
    /// </summary>
    [Fact]
    public async Task ARoomTheLinkRefusesIsPackedWithoutLinkData()
    {
        RoomObject hub = fixture.Library.Get("hub");
        // World lights: a lump outside the relocation set, which a room's
        // compile never writes (displacements, which this fact used to use,
        // are carried since PR 15).
        RoomObject bad = RoomHarness.WithLumps(hub, bsp => bsp.SetLump(BspLump.WorldLights, new byte[16]));
        Assert.Null(await LevelLinker.TryPrecomputeAsync(bad, CancellationToken.None));
        RoomPackItem item = await RoomPackItem.CreateAsync(bad);
        Assert.Equal([RoomEntityCounts.SectionTag, "NAM0", "NAM1", "NAM2", "NAM3"], item.Extra.Select(s => s.Tag));

        RoomLibrary library = RoomHarness.Library(bad, fixture.Library.Get("end"), fixture.Library.Get("hall"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkBytesAsync(library, 1));
        Assert.Contains("carries lump WorldLights", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entity key the link cannot read is held, not thrown, when the turn
    /// is worked out, and the link reports it with the message it always
    /// gave; such a room gets no link data.
    /// </summary>
    [Fact]
    public async Task AnUnreadableEntityIsReportedAtLink()
    {
        RoomObject end = fixture.Library.Get("end");
        List<BspEntity> entities = EntityLump.Parse(end.Bsp[BspLump.Entities]);
        entities.Single(e => e.ClassName == "info_player_start").Pairs.Add(new BspKeyValue("angle", "north"));
        RoomObject bad = RoomHarness.WithLumps(end, bsp => bsp[BspLump.Entities] = EntityLump.Write(entities));

        Assert.Null(LevelLinker.ComputeEntities(bad, 0).Items.Single(e => !e.IsWorld).Error);
        Assert.Contains("\"north\"", LevelLinker.ComputeEntities(bad, 1).Items.Single(e => !e.IsWorld).Error, StringComparison.Ordinal);
        Assert.Null(await LevelLinker.TryPrecomputeAsync(bad, CancellationToken.None));

        RoomLibrary library = RoomHarness.Library(fixture.Library.Get("hub"), bad, fixture.Library.Get("hall"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => LinkBytesAsync(library, 1));
        Assert.Equal("room end has an entity whose \"angle\" holds \"north\", not a number", refused.Message);
    }

    /// <summary>
    /// A link reads only the turns it asks for: the shared census always,
    /// each asked turn's sections, no other turn's; turns asked twice are
    /// joined, any integer is reduced to a quarter turn, and a room asked for
    /// twice is one object. The names-only load reads every turn.
    /// </summary>
    [Fact]
    public async Task ALoadReadsOnlyTheTurnsAskedFor()
    {
        byte[] pack = await PackAsync(All, RoomLinkCodec.None);
        using MemoryStream stream = new(pack);
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Equal(
            ["ROOM", "LNKA", "DVIS", "GEO0", "COL0", "ENT0", "GEO1", "COL1", "ENT1", "GEO2", "COL2", "ENT2", "GEO3", "COL3", "ENT3"],
            index.Find("hub")!.Sections.Select(s => s.Tag));

        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(
            stream, index, [new RoomPackRequest("hub", [1]), new RoomPackRequest("end", [-2, 4]), new RoomPackRequest("hub", [7])]);
        Assert.Same(rooms[0], rooms[2]);
        Assert.Equal([false, true, false, true], Enumerable.Range(0, 4).Select(r => rooms[0].Link!.Rotation(r) is not null));
        Assert.Equal([true, false, true, false], Enumerable.Range(0, 4).Select(r => rooms[1].Link!.Rotation(r) is not null));

        IReadOnlyList<RoomObject> all = await RoomPack.LoadRoomsAsync(stream, index, ["hall"]);
        Assert.All(Enumerable.Range(0, 4), r => Assert.NotNull(all[0].Link!.Rotation(r)!.Geometry));

        // Forward only: every section read in pack order, once.
        using ForwardOnly forward = new(pack);
        RoomPackIndex forwardIndex = await RoomPack.ReadIndexAsync(forward);
        IReadOnlyList<RoomObject> read = await RoomPack.LoadRoomsAsync(
            forward, forwardIndex, [new RoomPackRequest("hall", [3]), new RoomPackRequest("hub", [0])]);
        Assert.NotNull(read[0].Link!.Rotation(3)!.Collision);
        Assert.NotNull(read[1].Link!.Rotation(0)!.Entities);
    }

    /// <summary>
    /// The index's section table is read whole per room, and a pack cut
    /// short inside it is still refused naming the entry the cut falls in.
    /// </summary>
    [Fact]
    public async Task AnIndexCutInsideASectionTableNamesTheEntry()
    {
        byte[] pack = await PackAsync(All, RoomLinkCodec.None);

        // header 20; entry 0: name length, "hub", section count, then 20 bytes per section.
        int cut = 20 + 4 + 3 + 4 + (2 * 20) + 7;
        using MemoryStream stream = new(pack[..cut]);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPack.ReadIndexAsync(stream));
        Assert.Contains("truncated in index entry 0 (\"hub\")'s section 2: wanted 20 bytes, got 7", refused.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<byte[]> LinkBytesAsync(RoomLibrary library, int degree)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        LinkedLevel linked = await LevelLinker.LinkAsync(fixture.Layout, library, context);
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }

    /// <summary>The fixture's rooms as a pack: with the given link sections, or (parts null) containers only.</summary>
    private async Task<byte[]> PackAsync(RoomLinkParts? parts, RoomLinkCodec codec)
    {
        List<RoomPackItem> items = [];
        foreach (RoomObject room in fixture.Library.Rooms)
        {
            RoomPackItem item = await RoomPackItem.CreateAsync(room);
            items.Add(parts is { } wanted
                ? item with { Extra = RoomLinkSections.Write((await LevelLinker.TryPrecomputeAsync(room, CancellationToken.None))!, wanted, codec) }
                : item with { Extra = [] });
        }

        using MemoryStream pack = new();
        await RoomPack.SaveAsync(items, pack);
        return pack.ToArray();
    }

    /// <summary>The fixture's rooms loaded back from a pack, as <c>ssmap link</c> loads them (only the placed turns).</summary>
    private async Task<RoomLibrary> PackedLibraryAsync(RoomLinkParts? parts, RoomLinkCodec codec, bool turnsOnly)
    {
        using MemoryStream stream = new(await PackAsync(parts, codec));
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        IEnumerable<IGrouping<string, int>> placed = fixture.Layout.Rooms
            .GroupBy(r => r.Placement.Room, r => r.Placement.Rotation);
        IReadOnlyList<RoomObject> rooms = await RoomPack.LoadRoomsAsync(
            stream,
            index,
            [.. placed.Select(g => new RoomPackRequest(g.Key, turnsOnly ? [.. g] : [0, 1, 2, 3]))]);
        return RoomHarness.Library([.. rooms]);
    }

    private static RoomLinkData? ReadBack(RoomObject room, IReadOnlyList<RoomPackSectionData> sections) =>
        RoomLinkSections.Read(room, tag => sections.FirstOrDefault(s => s.Tag == tag) is { Tag: not null } found
            ? new ArraySegment<byte>(found.Bytes.ToArray())
            : (ArraySegment<byte>?)null);

    private static byte[] With(ReadOnlyMemory<byte> bytes, int offset, byte value)
    {
        byte[] copy = bytes.ToArray();
        copy[offset] = value;
        return copy;
    }

    private static byte[] WithLong(ReadOnlyMemory<byte> bytes, int offset, long value)
    {
        byte[] copy = bytes.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static byte[] WithInt(ReadOnlyMemory<byte> bytes, int offset, int value)
    {
        byte[] copy = bytes.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static ReadOnlySpan<byte> Bytes<T>(T[] items)
        where T : unmanaged => MemoryMarshal.AsBytes(items.AsSpan());

    private static void AssertBits(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    private static void AssertSameShared(RoomLinkShared expected, RoomLinkShared actual)
    {
        Assert.Equal(expected.Sockets.Count, actual.Sockets.Count);
        for (int s = 0; s < expected.Sockets.Count; s++)
        {
            Assert.Equal(expected.Sockets[s].Facing, actual.Sockets[s].Facing);
            Assert.Equal(expected.Sockets[s].StrippedBrushes, actual.Sockets[s].StrippedBrushes);
            Assert.Equal(expected.Sockets[s].CarveLeaves, actual.Sockets[s].CarveLeaves);
            Assert.Equal(expected.Sockets[s].StrippedFaces, actual.Sockets[s].StrippedFaces);
        }

        Assert.Contains(expected.Sockets, s => s.Facing.Length > 0 && s.StrippedBrushes.Length > 0 && s.CarveLeaves.Length > 0);
    }

    private static void AssertSameGeometry(RoomLinkGeometry expected, RoomLinkGeometry actual)
    {
        Assert.Equal(expected.Rotation, actual.Rotation);
        Assert.True(Bytes(expected.Vertices).SequenceEqual(Bytes(actual.Vertices)), "vertices");
        Assert.True(Bytes(expected.PlanePairs).SequenceEqual(Bytes(actual.PlanePairs)), "plane pairs");
        Assert.Equal(expected.PlaneSwapped, actual.PlaneSwapped);
        Assert.True(Bytes(expected.TexInfos).SequenceEqual(Bytes(actual.TexInfos)), "texinfos");
        Assert.True(Bytes(expected.NodeBoxes).SequenceEqual(Bytes(actual.NodeBoxes)), "node boxes");
        Assert.True(Bytes(expected.LeafBoxes).SequenceEqual(Bytes(actual.LeafBoxes)), "leaf boxes");
        Assert.True(Bytes(expected.VertNormals).SequenceEqual(Bytes(actual.VertNormals)), "vertex normals");
        Assert.True(Bytes(expected.PrimVerts).SequenceEqual(Bytes(actual.PrimVerts)), "primitive vertices");
        Assert.True(Bytes(expected.OccluderBoxes).SequenceEqual(Bytes(actual.OccluderBoxes)), "occluder boxes");
        Assert.True(Bytes([expected.ModelBox]).SequenceEqual(Bytes([actual.ModelBox])), "model box");
        Assert.True(Bytes(expected.PlugBoxes).SequenceEqual(Bytes(actual.PlugBoxes)), "plug boxes");
        Assert.NotEmpty(actual.Vertices);
    }

    private static void AssertSameCollision(RoomLinkCollision expected, RoomLinkCollision actual)
    {
        Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.VirtualTerrain, actual.VirtualTerrain);
        Assert.Equal(expected.Solids.Count, actual.Solids.Count);
        for (int s = 0; s < expected.Solids.Count; s++)
        {
            Assert.Equal(expected.Solids[s].Contents, actual.Solids[s].Contents);
            Assert.Equal(expected.Solids[s].Starts, actual.Solids[s].Starts);
            Assert.Equal(expected.Solids[s].Ledges, actual.Solids[s].Ledges);
        }

        Assert.NotEmpty(actual.Solids);
    }

    private static void AssertSameEntities(RoomLinkEntities expected, RoomLinkEntities actual)
    {
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        for (int e = 0; e < expected.Items.Count; e++)
        {
            Assert.Equal(expected.Items[e].IsWorld, actual.Items[e].IsWorld);
            Assert.Equal(expected.Items[e].Extent, actual.Items[e].Extent);
            Assert.Equal(expected.Items[e].Pairs.Count, actual.Items[e].Pairs.Count);
            for (int p = 0; p < expected.Items[e].Pairs.Count; p++)
            {
                RoomLinkPair a = expected.Items[e].Pairs[p];
                RoomLinkPair b = actual.Items[e].Pairs[p];
                Assert.Equal(a.Key, b.Key);
                Assert.Equal(a.Value, b.Value);
                AssertBits(a.Origin, b.Origin);
                Assert.Equal(a.Component, b.Component);
            }
        }

        Assert.Contains(actual.Items, i => i.Pairs.Any(p => p.Value is null));
    }

    // ---- the one-step moves this build replaced, as references ----------------

    private static (DPlane[] Planes, bool[] Swapped) OneStepPlanes(DPlane[] planes, RoomTransform transform)
    {
        Vec3 translation = transform.Apply(Vec3.Zero);
        int rotation = transform.Placement.NormalizedRotation;
        bool[] swapped = new bool[planes.Length / 2];
        for (int i = 0; i < planes.Length; i += 2)
        {
            Vec3 normal = LevelLinker.ApplyNormal(planes[i].Normal, rotation);
            float dist = planes[i].Dist + Vec3.Dot(normal, translation);
            Plane moved = new(normal, dist);
            DPlane even = new() { Normal = normal, Dist = dist, Type = (int)moved.Type };
            DPlane odd = new() { Normal = -normal, Dist = -dist, Type = (int)moved.Type };
            bool negativeAxial = moved.Type switch
            {
                PlaneType.X => normal.X < 0,
                PlaneType.Y => normal.Y < 0,
                PlaneType.Z => normal.Z < 0,
                _ => false,
            };

            swapped[i / 2] = negativeAxial;
            planes[i] = negativeAxial ? odd : even;
            planes[i + 1] = negativeAxial ? even : odd;
        }

        return (planes, swapped);
    }

    private static TexInfo[] OneStepTexInfos(TexInfo[] infos, RoomTransform transform)
    {
        Vec3 t = transform.Apply(Vec3.Zero);
        int rotation = transform.Placement.NormalizedRotation;
        for (int i = 0; i < infos.Length; i++)
        {
            for (int row = 0; row < 2; row++)
            {
                Move(ref infos[i].TextureVecsTexelsPerWorldUnits, row);
                Move(ref infos[i].LightmapVecsLuxelsPerWorldUnits, row);
            }
        }

        return infos;

        void Move(ref FloatArray8 vecs, int row)
        {
            int at = row * 4;
            Vec3 axis = LevelLinker.ApplyNormal(new Vec3(vecs[at], vecs[at + 1], vecs[at + 2]), rotation);
            vecs[at] = axis.X;
            vecs[at + 1] = axis.Y;
            vecs[at + 2] = axis.Z;
            vecs[at + 3] -= Vec3.Dot(axis, t);
        }
    }

    private static void OneStepLedge(IvpCompactLedge ledge, RoomTransform transform)
    {
        int turns = transform.Placement.NormalizedRotation;
        float tx = transform.Apply(default).X * 0.0254f;
        float tz = transform.Apply(default).Y * 0.0254f;
        Span<byte> bytes = ledge.Bytes;
        for (int p = 0; p < ledge.PointCount; p++)
        {
            int o = ledge.PointOffset + (16 * p);
            float x = BinaryPrimitives.ReadSingleLittleEndian(bytes[o..]);
            float z = BinaryPrimitives.ReadSingleLittleEndian(bytes[(o + 8)..]);
            (float rx, float rz) = turns switch
            {
                0 => (x, z),
                1 => (-z, x),
                2 => (-x, -z),
                _ => (z, -x),
            };

            BinaryPrimitives.WriteSingleLittleEndian(bytes[o..], rx + tx);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 8)..], rz + tz);
        }
    }

    private static BspEntity OneStepEntity(BspEntity entity, RoomTransform transform)
    {
        int turns = transform.Placement.NormalizedRotation;
        BspEntity moved = new();
        foreach (BspKeyValue pair in entity.Pairs)
        {
            string value = pair.Value;
            if (Is(pair.Key, "origin"))
            {
                value = Vec(transform.Apply(Parse(value)));
            }
            else if (turns != 0 && Is(pair.Key, "angles"))
            {
                Vec3 angles = Parse(value);
                value = Vec(new Vec3(angles.X, Turn(angles.Y), angles.Z));
            }
            else if (turns != 0 && Is(pair.Key, "angle"))
            {
                float yaw = float.Parse(value, CultureInfo.InvariantCulture);
                value = yaw is -1f or -2f ? value : Turn(yaw).ToString(CultureInfo.InvariantCulture);
            }

            moved.Pairs.Add(new BspKeyValue(pair.Key, value));
        }

        return moved;

        static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

        static Vec3 Parse(string text)
        {
            float[] parts = [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => float.Parse(p, CultureInfo.InvariantCulture))];
            return new Vec3(parts[0], parts[1], parts[2]);
        }

        static string Vec(Vec3 v) => string.Create(
            CultureInfo.InvariantCulture, $"{v.X.ToString(CultureInfo.InvariantCulture)} {v.Y.ToString(CultureInfo.InvariantCulture)} {v.Z.ToString(CultureInfo.InvariantCulture)}");

        float Turn(float yaw)
        {
            float turned = (yaw + (90f * turns)) % 360f;
            return turned < 0 ? turned + 360f : turned;
        }
    }

    /// <summary>A stream that reads forward and cannot seek or say its length, like a pipe.</summary>
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
