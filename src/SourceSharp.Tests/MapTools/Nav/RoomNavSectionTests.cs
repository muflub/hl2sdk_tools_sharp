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
        Assert.Equal((byte)compression.Codec, bytes[2]);
        Assert.Equal((byte)turn, bytes[3]);
        AssertSame(nav, RoomNavSection.Read(bytes));
        Assert.Equal(bytes, RoomNavSection.Write(RoomNavSection.Read(bytes), compression));
    }

    [Fact]
    public void TheTagsNameTheTurns()
    {
        Assert.Equal(["NVR0", "NVR1", "NVR2", "NVR3"], Enumerable.Range(0, 4).Select(RoomNavSection.Tag));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomNavSection.Tag(4));
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "short", "at least 8 bytes" },
        { "version", "this build reads version 1" },
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
        byte[] raw = section[8..];
        void Rewrap() => section = [.. section[..4], .. BitConverter.IsLittleEndian ? BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(raw.Length)) : BitConverter.GetBytes(raw.Length), .. raw];
        switch (fault)
        {
            case "short": section = section[..5]; break;
            case "version": section[1] = 2; break;
            case "turn": section[3] = 7; break;
            case "codec": section[2] = 9; break;
            case "cut": raw = raw[..^3]; Rewrap(); break;
            case "trailing": raw = [.. raw, 0]; Rewrap(); break;
            case "cells": BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(8), 500); Rewrap(); break;
            case "role": raw[16] = 9; Rewrap(); break;
            case "agents": raw[17] = 0; Rewrap(); break;
            case "facing":
                {
                    // After three agents of name, width, height and mask: the socket count, then its facing.
                    int at = 18;
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
                    int at = 18;
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
            AssertSame(nav.Turned(t), RoomNavSection.Read(all[t].Bytes.Span));
        }

        Assert.Throws<ArgumentException>(() => RoomNavPack.Sections(nav.Turned(1), new RoomNavPackOptions()));
    }

    /// <summary>
    /// The link reads the pack's id and the placed rooms' sections at their
    /// turns: the stored turn when the pack has it, turn 0 turned when not,
    /// and null when a placed room has no navigation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePlacedRoomsNavigationIsReadAtItsTurns(bool allTurns)
    {
        Guid id = Guid.Parse("12345678-1234-8234-8234-123456789abc");
        RoomNavPackOptions options = new() { StoreAllTurns = allTurns };
        List<RoomPackItem> items =
        [
            new("corner", new byte[] { 1 }) { Extra = RoomNavPack.Sections(fixture.Nav("corner"), options) },
            new("east", new byte[] { 2 }) { Extra = RoomNavPack.Sections(fixture.Nav("east"), options) },
            new("bare", new byte[] { 3 }),
        ];
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([RoomCompileIds.Section(id)], items, pack, CancellationToken.None);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);

        Assert.Equal(id, await RoomNavPack.ReadPackIdAsync(pack, index));
        IReadOnlyDictionary<(string Room, int Turn), RoomNav>? navs =
            await RoomNavPack.ReadAsync(pack, index, [("corner", 3), ("east", 0), ("corner", 3), ("corner", 1)]);
        Assert.NotNull(navs);
        Assert.Equal(3, navs.Count);
        AssertSame(fixture.Nav("corner").Turned(3), navs[("corner", 3)]);
        AssertSame(fixture.Nav("corner").Turned(1), navs[("corner", 1)]);
        Assert.Null(await RoomNavPack.ReadAsync(pack, index, [("east", 0), ("bare", 2)]));
        await Assert.ThrowsAsync<LinkException>(() => RoomNavPack.ReadAsync(pack, index, [("nowhere", 0)]));

        using MemoryStream old = new();
        await RoomPack.SaveAsync(items, old);
        old.Position = 0;
        Assert.Null(await RoomNavPack.ReadPackIdAsync(old, await RoomPack.ReadIndexAsync(old)));
    }

    [Fact]
    public async Task ASectionThisBuildCannotReadIsAPackProblemNamingTheRoom()
    {
        byte[] section = RoomNavSection.Write(fixture.Nav("east"), NavCompression.None);
        section[1] = 9;
        using MemoryStream pack = new();
        await RoomPack.SaveAsync([], [new RoomPackItem("east", new byte[] { 1 }) { Extra = [new(RoomNavSection.Tag(0), section)] }], pack, CancellationToken.None);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomNavPack.ReadAsync(pack, index, [("east", 0)]));
        Assert.Contains("room pack entry \"east\"'s \"NVR0\" section", refused.Message, StringComparison.Ordinal);
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
