//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A room's navigation as pack sections: it round-trips under every codec,
/// it is read from the pack by the placed rooms and turns alone, and a
/// broken section is refused.
/// </summary>
public sealed class RoomNavSectionTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private RoomNav WithPoi() => RoomNavBuilder.Build(
        fixture.Definition("corner"), fixture.Room("corner").Bsp,
        [new AuthoredPoi("1", new Vec3(128, 128, 16), 45, true, 8, "cover", "x", "cxry_a", ["standing"]),
            new AuthoredPoi("2", new Vec3(120, 100, 16), 0, false, 0, "custom", "", null, ["standing", "flyer"])],
        RoomRole.Down, NavRoomsFixture.Settings);

    private static void AssertSame(RoomNav expected, RoomNav actual)
    {
        Assert.Equal((expected.CellSize, expected.VoxelSize, expected.CellVoxels, expected.FloorNormalZ, expected.Turn, expected.Role),
            (actual.CellSize, actual.VoxelSize, actual.CellVoxels, actual.FloorNormalZ, actual.Turn, actual.Role));
        Assert.Equal(expected.Agents, actual.Agents);
        Assert.Equal(expected.Sockets, actual.Sockets);
        Assert.Equal(expected.Pois, actual.Pois);
        for (int a = 0; a < expected.AgentData.Count; a++)
        {
            Assert.Equal(expected.AgentData[a].Nodes, actual.AgentData[a].Nodes);
            Assert.Equal(expected.AgentData[a].Leaves, actual.AgentData[a].Leaves);
            for (int s = 0; s < expected.Sockets.Count; s++)
            {
                Assert.Equal(expected.AgentData[a].Sockets[s].Portal, actual.AgentData[a].Sockets[s].Portal);
                Assert.Equal(expected.AgentData[a].Sockets[s].Capped, actual.AgentData[a].Sockets[s].Capped);
            }
        }
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("deflate:6", 1)]
    [InlineData("brotli:9", 3)]
    public void ASectionRoundTripsUnderEveryCodecAndTurn(string codec, int turn)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        RoomNav nav = WithPoi().Turned(turn);
        byte[] bytes = RoomNavSection.Write(nav, compression);

        // The link sections' framing: codec byte, int64 decoded length.
        Assert.Equal((byte)compression.Codec, bytes[0]);
        byte[] raw = NavCompression.Decompress(compression.Codec, bytes.AsSpan(9), (int)BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1)));
        Assert.Equal((RoomNavSection.Revision, turn), (BinaryPrimitives.ReadInt32BigEndian(raw), (int)raw[4]));
        AssertSame(nav, RoomNavSection.Read(bytes)!);
        Assert.Equal(bytes, RoomNavSection.Write(RoomNavSection.Read(bytes)!, compression));
    }

    /// <summary>
    /// A room's section bytes, raw and under each codec, pinned by hash (on
    /// Microsoft's runtime, whose zlib-ng every OS shares; see
    /// <c>Nav3dFileTests.TheBytesArePinned</c>): the same room gives the same
    /// section on every run, thread count and, CI proves, OS.
    /// </summary>
    [Theory]
    [InlineData("none", 31139, "69c72cb6")]
    [InlineData("deflate:6", 9662, "a1c1bded")]
    [InlineData("brotli:9", 6608, "11c9ae1b")]
    public void ASectionsBytesArePinned(string codec, int length, string sha256Prefix)
    {
        Assert.True(NavCompression.TryParse(codec, out NavCompression compression));
        byte[] bytes = RoomNavSection.Write(fixture.Nav("east"), compression);
        Assert.Equal((length, sha256Prefix), (bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..8]));
    }

    [Fact]
    public void TheTagsNameTheTurns()
    {
        Assert.Equal(["NVR0", "NVR1", "NVR2", "NVR3"], Enumerable.Range(0, 4).Select(RoomNavSection.Tag));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomNavSection.Tag(4));
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "short", "at least 9 bytes" },
        { "length", "claims a payload of -1 bytes" },
        { "turn", "at turn 7" },
        { "codec", "codec 9" },
        { "cut", "cut short" },
        { "trailing", "after its last agent" },
        { "cells", "voxels a side" },
        { "role", "with role 9" },
        { "agents", "with 0 agents" },
        { "facing", "socket facing 5" },
        { "count", "claims" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void ABrokenSectionIsRefused(string fault, string expected)
    {
        byte[] section = RoomNavSection.Write(fixture.Nav("east"), NavCompression.None);
        byte[] raw = section[9..];
        void Rewrap()
        {
            section = new byte[9 + raw.Length];
            BinaryPrimitives.WriteInt64BigEndian(section.AsSpan(1), raw.Length);
            raw.CopyTo(section, 9);
        }

        // The payload: revision (4), turn (1), cell (4), voxel (4), cells (4), floor (4), role (1), agent count (1).
        switch (fault)
        {
            case "short": section = section[..5]; break;
            case "length": BinaryPrimitives.WriteInt64BigEndian(section.AsSpan(1), -1); break;
            case "turn": raw[4] = 7; Rewrap(); break;
            case "codec": section[0] = 9; break;
            case "cut": raw = raw[..^3]; Rewrap(); break;
            case "trailing": raw = [.. raw, 0]; Rewrap(); break;
            case "cells": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(13), 500); Rewrap(); break;
            case "role": raw[21] = 9; Rewrap(); break;
            case "agents": raw[22] = 0; Rewrap(); break;
            case "facing":
                {
                    // After three agents of name, width, height and mask: the socket count, then its facing.
                    int at = 23;
                    for (int a = 0; a < 3; a++)
                    {
                        at += 2 + BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(at)) + 12;
                    }

                    raw[at + 1] = 5;
                    Rewrap();
                    break;
                }

            case "count":
                {
                    int at = 23;
                    for (int a = 0; a < 3; a++)
                    {
                        at += 2 + BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(at)) + 12;
                    }

                    at += 1 + 1 + 2 + BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(at + 2));
                    BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(at), int.MaxValue);
                    Rewrap();
                    break;
                }

            default: throw new InvalidOperationException(fault);
        }

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => RoomNavSection.Read(section));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVoxelOutsideTheCellOrATreeOutOfShapeIsRefused()
    {
        RoomNav nav = fixture.Nav("east");
        RoomNav badPortal = nav with
        {
            AgentData = [.. nav.AgentData.Select(a => a with { Sockets = [new RoomNavSocket([new NavVoxel(16, 0, 0)], [])] })],
        };
        Assert.Contains("outside a cell", Assert.Throws<InvalidDataException>(() =>
            RoomNavSection.Read(RoomNavSection.Write(badPortal, NavCompression.None))).Message, StringComparison.Ordinal);
        RoomNav badTree = nav with { AgentData = [.. nav.AgentData.Select(a => a with { Nodes = [Nav3dFormat.Node(Nav3dNodeKind.Free, 999999)] })] };
        Assert.Throws<InvalidDataException>(() => RoomNavSection.Read(RoomNavSection.Write(badTree, NavCompression.None)));
    }

    [Fact]
    public void PackSectionsAreTurnZeroOrAllFourTurns()
    {
        RoomNav nav = fixture.Nav("corner");
        Assert.Equal(["NVR0"], RoomNavPack.Sections(nav, new RoomNavPackOptions { StoreAllTurns = false }).Select(s => s.Tag));
        IReadOnlyList<RoomPackSectionData> all = RoomNavPack.Sections(nav, new RoomNavPackOptions());
        Assert.Equal(["NVR0", "NVR1", "NVR2", "NVR3"], all.Select(s => s.Tag));
        for (int t = 0; t < 4; t++)
        {
            AssertSame(nav.Turned(t), RoomNavSection.Read(all[t].Bytes.Span)!);
        }

        Assert.Throws<ArgumentException>(() => RoomNavPack.Sections(nav.Turned(1), new RoomNavPackOptions()));
    }

    /// <summary>A section of a revision this build does not know reads as absent, as a link section's does.</summary>
    [Fact]
    public void ASectionOfAnotherRevisionReadsAsAbsent()
    {
        byte[] section = RoomNavSection.Write(fixture.Nav("east"), NavCompression.None);
        BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(9), RoomNavSection.Revision + 1);
        Assert.Null(RoomNavSection.Read(section));
        Assert.Null(RoomNavPack.FromSections("east", tag => tag == "NVR0" ? new ArraySegment<byte>(section) : (ArraySegment<byte>?)null));
    }

    /// <summary>
    /// A room's navigation sections follow each turn's link sections, so a
    /// link at one turn reads one run: <c>LNKA</c>, then per turn
    /// <c>GEO</c><i>r</i>, <c>COL</c><i>r</i> and <c>NVR</c><i>r</i>.
    /// </summary>
    [Fact]
    public void NavigationSectionsFollowTheirTurnsLinkSections()
    {
        static RoomPackSectionData S(string tag) => new(tag, new byte[] { 1 });
        IReadOnlyList<RoomPackSectionData> link = [S("LNKA"), S("GEO0"), S("COL0"), S("GEO1"), S("COL1"), S("GEO2"), S("COL2"), S("GEO3"), S("COL3")];
        IReadOnlyList<RoomPackSectionData> nav = [S("NVR0"), S("NVR1"), S("NVR2"), S("NVR3")];
        Assert.Equal(
            ["LNKA", "GEO0", "COL0", "NVR0", "GEO1", "COL1", "NVR1", "GEO2", "COL2", "NVR2", "GEO3", "COL3", "NVR3"],
            RoomNavPack.Interleave(link, nav).Select(s => s.Tag));
        Assert.Equal(["NVR0", "NVR1"], RoomNavPack.Interleave([], [S("NVR0"), S("NVR1")]).Select(s => s.Tag));
        Assert.Same(link, RoomNavPack.Interleave(link, []));
    }

    /// <summary>
    /// The link loads a room's navigation through the pack's own requests
    /// (<see cref="RoomPackRequest.Navigation"/>): at the turns placed, the
    /// stored turn when the pack has it and turn 0 turned when not; none when
    /// not asked for or when the pack has none; and the pack's id from its
    /// <c>CMPL</c> section beside a <c>LENT</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePlacedRoomsNavigationIsLoadedAtItsTurns(bool allTurns)
    {
        Guid id = Guid.Parse("12345678-1234-8234-8234-123456789abc");
        RoomNavPackOptions options = new() { StoreAllTurns = allTurns };
        RoomObject corner = fixture.Room("corner") with { Nav = RoomNavTurns.Of(fixture.Nav("corner")) };
        RoomObject east = fixture.Room("east") with { Nav = RoomNavTurns.Of(fixture.Nav("east")) };
        List<RoomPackItem> items =
        [
            await RoomPackItem.CreateAsync(corner, options),
            await RoomPackItem.CreateAsync(east, options),
            await RoomPackItem.CreateAsync(fixture.Room("hall")),
        ];
        Assert.Equal(allTurns ? 4 : 1, items[0].Extra.Count(s => s.Tag.StartsWith("NVR", StringComparison.Ordinal)));
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(id), new RoomPackSectionData("LENT", new byte[] { 0, 0, 0, 0 })], items, pack, CancellationToken.None);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.Equal(["CMPL", "LENT"], index.LibrarySections.Select(s => s.Tag));
        Assert.Equal(id, await RoomNavPack.ReadPackIdAsync(pack, index));

        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(pack, index,
        [
            new RoomPackRequest("corner", [3, 1]) { Navigation = true },
            new RoomPackRequest("east", [0]) { Navigation = true },
            new RoomPackRequest("hall", [2]) { Navigation = true },
        ]);
        RoomNavTurns turns = loaded[0].Nav!;
        Assert.Equal((allTurns, allTurns, false), (turns.Has(3), turns.Has(1), turns.Has(2)));
        Assert.Equal(!allTurns, turns.Has(0));
        AssertSame(fixture.Nav("corner").Turned(3), turns.At(3));
        AssertSame(fixture.Nav("corner").Turned(1), turns.At(1));
        AssertSame(fixture.Nav("corner"), turns.Base);
        AssertSame(fixture.Nav("east"), loaded[1].Nav!.At(0));
        Assert.Null(loaded[2].Nav);

        IReadOnlyList<RoomObject> without = await RoomPack.LoadRoomsAsync(pack, index, [new RoomPackRequest("corner", [3])]);
        Assert.Null(without[0].Nav);

        using MemoryStream old = new();
        await RoomPack.SaveAsync(items, old);
        old.Position = 0;
        Assert.Null(await RoomNavPack.ReadPackIdAsync(old, await RoomPack.ReadIndexAsync(old)));
    }

    [Fact]
    public async Task ASectionThisBuildCannotDecodeIsAPackProblemNamingTheRoom()
    {
        byte[] section = RoomNavSection.Write(fixture.Nav("east"), NavCompression.None);
        section[0] = 9;
        RoomPackItem item = await RoomPackItem.CreateAsync(fixture.Room("east"));
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([], [item with { Extra = [.. item.Extra, new(RoomNavSection.Tag(0), section)] }], pack, CancellationToken.None);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() =>
            RoomPack.LoadRoomsAsync(pack, index, [new RoomPackRequest("east", [0]) { Navigation = true }]));
        Assert.Contains("room pack entry \"east\"'s \"NVR0\" section", refused.Message, StringComparison.Ordinal);

        byte[] wrongTurn = RoomNavSection.Write(fixture.Nav("east").Turned(1), NavCompression.None);
        using MemoryStream pack2 = new();
        await RoomPack.SaveAsync([], [item with { Extra = [.. item.Extra, new(RoomNavSection.Tag(0), wrongTurn)] }], pack2, CancellationToken.None);
        pack2.Position = 0;
        RoomPackIndex index2 = await RoomPack.ReadIndexAsync(pack2);
        Assert.Contains("holds turn 1", (await Assert.ThrowsAsync<LinkException>(() =>
            RoomPack.LoadRoomsAsync(pack2, index2, [new RoomPackRequest("east", [0]) { Navigation = true }]))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnsAreDerivedFromAnyHeldTurn()
    {
        RoomNavTurns turns = RoomNavTurns.Of(fixture.Nav("corner").Turned(2));
        Assert.False(turns.Has(0));
        AssertSame(fixture.Nav("corner"), turns.Base);
        Assert.True(turns.Has(0));
        Assert.Same(turns.At(1), turns.At(-3));
        Assert.Throws<ArgumentNullException>(() => RoomNavTurns.Of(null!));
    }

    [Fact]
    public async Task ReadingOneSectionNeedsASeekableStream()
    {
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(Guid.Empty)], [new RoomPackItem("east", new byte[] { 1 })], pack, CancellationToken.None);
        using NonSeekable forward = new(pack.ToArray());
        RoomPackIndex index = await RoomPack.ReadIndexAsync(forward);
        await Assert.ThrowsAsync<NotSupportedException>(() => RoomPack.ReadSectionAsync(forward, index, index.LibrarySections[0]));
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
