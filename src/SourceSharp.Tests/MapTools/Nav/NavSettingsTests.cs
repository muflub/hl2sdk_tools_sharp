//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>The library's navigation settings, from its worldspawn keys.</summary>
public sealed class NavSettingsTests
{
    private static VmfDocument Library(params (string Key, string Value)[] keys)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        foreach ((string key, string value) in keys)
        {
            world.AddKey(key, value);
        }

        document.Chunks.Add(world);
        return document;
    }

    [Fact]
    public void NoKeysAreTheDefaults()
    {
        NavSettings settings = NavSettings.FromLibrary(Library())!;
        Assert.Equal(16f, settings.VoxelSize);
        Assert.Equal(0.7f, settings.FloorNormalZ);
        Assert.Equal(
            [new NavAgentSpec("standing", 32, 72, Nav3dFormat.PlayerSolidMask), new NavAgentSpec("flyer", 32, 32, Nav3dFormat.NpcSolidMask)],
            settings.Agents);
        Assert.Equal(0, settings.PlayerAgent);
        Assert.Equal(16, settings.CellVoxels(256));
        Assert.Equal(NavSettings.Default.Agents, settings.Agents);
        Assert.Equal(new Vec3(-16, -16, 0), settings.Agents[0].Mins);
        Assert.Equal(new Vec3(16, 16, 72), settings.Agents[0].Maxs);
        Assert.NotNull(NavSettings.FromLibrary(new VmfDocument()));
    }

    [Fact]
    public void TheKeysSetTheVoxelTheSlopeAndTheAgents()
    {
        NavSettings settings = NavSettings.FromLibrary(Library(
            ("nav_voxel_size", "8"), ("nav_max_slope", "60"), ("nav_agents", "big 48 96 npc;tiny 8 8 0x9")))!;
        Assert.Equal(8f, settings.VoxelSize);
        Assert.Equal(0.5f, settings.FloorNormalZ, 6);
        Assert.Equal([new NavAgentSpec("big", 48, 96, Nav3dFormat.NpcSolidMask), new NavAgentSpec("tiny", 8, 8, 9)], settings.Agents);
        Assert.Equal(-1, settings.PlayerAgent);
        Assert.Equal(32, settings.CellVoxels(256));
    }

    [Fact]
    public void NavZeroTurnsItOff() => Assert.Null(NavSettings.FromLibrary(Library(("nav", " 0 "))));

    [Theory]
    [InlineData("nav_voxel_size", "0", "not a positive number")]
    [InlineData("nav_voxel_size", "x", "not a positive number")]
    [InlineData("nav_max_slope", "90", "above 0 and below 90")]
    [InlineData("nav_max_slope", "0", "above 0 and below 90")]
    [InlineData("nav_agents", "a 1", "not \"name width height [mask]\"")]
    [InlineData("nav_agents", "a-b 1 2", "letters, digits and _")]
    [InlineData("nav_agents", "a 1 2; a 3 4", "two nav agents are named \"a\"")]
    [InlineData("nav_agents", "a 0 2", "positive width and height")]
    [InlineData("nav_agents", "a 1 2 wobble", "a mask is player, npc or a non-zero number")]
    [InlineData("nav_agents", "a 1 2 0", "a mask is player, npc or a non-zero number")]
    [InlineData("nav_agents", " ; ", "names 0 agents")]
    public void AMalformedKeyIsRefusedNamingIt(string key, string value, string expected)
    {
        RoomLibraryException refused = Assert.Throws<RoomLibraryException>(() => NavSettings.FromLibrary(Library((key, value))));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyAgentsOrAVoxelThatDoesNotFitTheCellAreRefused()
    {
        string many = string.Join(';', Enumerable.Range(0, 33).Select(i => $"a{i} 1 1"));
        Assert.Throws<RoomLibraryException>(() => NavSettings.ParseAgents(many));
        Assert.Contains("does not divide", Assert.Throws<RoomLibraryException>(() => (NavSettings.Default with { VoxelSize = 24 }).CellVoxels(256)).Message, StringComparison.Ordinal);
        Assert.Contains("at most 128", Assert.Throws<RoomLibraryException>(() => (NavSettings.Default with { VoxelSize = 1 }).CellVoxels(256)).Message, StringComparison.Ordinal);
        Assert.Throws<RoomLibraryException>(() => (NavSettings.Default with { VoxelSize = 512 }).CellVoxels(256));
    }
}
