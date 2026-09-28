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
        Assert.Equal((18f, 56f, 100f, 2f, 1.5f), (settings.StepHeight, settings.JumpHeight, settings.JumpDistance, settings.WaterCost, settings.LadderCost));
        Assert.Equal((Nav3dClipClass.Player, Nav3dClipClass.Npc), (settings.Agents[0].ClipClass, settings.Agents[1].ClipClass));
    }

    [Fact]
    public void TheKeysSetTheVoxelTheSlopeAndTheAgents()
    {
        NavSettings settings = NavSettings.FromLibrary(Library(
            ("nav_voxel_size", "8"), ("nav_max_slope", "60"), ("nav_agents", "big 48 96 npc;tiny 8 8 0x0202400B"),
            ("nav_step_height", "12"), ("nav_jump_height", "0"), ("nav_jump_distance", "64"), ("nav_cost_water", "4"), ("nav_cost_ladder", "0.5")))!;
        Assert.Equal(8f, settings.VoxelSize);
        Assert.Equal(0.5f, settings.FloorNormalZ, 6);
        Assert.Equal([new NavAgentSpec("big", 48, 96, Nav3dFormat.NpcSolidMask), new NavAgentSpec("tiny", 8, 8, Nav3dFormat.NpcSolidMask)], settings.Agents);
        Assert.Equal(-1, settings.PlayerAgent);
        Assert.Equal(32, settings.CellVoxels(256));
        Assert.Equal((12f, 0f, 64f, 4f, 0.5f), (settings.StepHeight, settings.JumpHeight, settings.JumpDistance, settings.WaterCost, settings.LadderCost));
        Assert.Equal((256, 1024, 128, 512), (settings.Cost(false, false), settings.Cost(true, false), settings.Cost(false, true), settings.Cost(true, true)));
    }

    /// <summary>Presets are optional: the grid does not depend on them, so an empty list builds a library with none.</summary>
    [Fact]
    public void AnEmptyPresetListIsNoPresets()
    {
        Assert.Empty(NavSettings.FromLibrary(Library(("nav_agents", " ; ")))!.Agents);
        Assert.Empty(NavSettings.ParseAgents(string.Empty));
        Assert.Equal(-1, (NavSettings.Default with { Agents = [] }).PlayerAgent);
    }

    [Fact]
    public void ACostIsTheFlagsMultipliersInFixedPointAndNeverZero()
    {
        NavSettings settings = NavSettings.Default;
        Assert.Equal((256, 512, 384, 768), (settings.Cost(false, false), settings.Cost(true, false), settings.Cost(false, true), settings.Cost(true, true)));
        Assert.Equal(1, (settings with { WaterCost = 0.0001f }).Cost(true, false));
        Assert.Equal(ushort.MaxValue, (settings with { WaterCost = 255, LadderCost = 255 }).Cost(true, true));
    }

    [Fact]
    public void NavZeroTurnsItOff() => Assert.Null(NavSettings.FromLibrary(Library(("nav", " 0 "))));

    [Theory]
    [InlineData("nav_voxel_size", "0", "not a positive number")]
    [InlineData("nav_voxel_size", "x", "not a positive number")]
    [InlineData("nav_max_slope", "90", "above 0 and below 90")]
    [InlineData("nav_max_slope", "0", "above 0 and below 90")]
    [InlineData("nav_agents", "a 1", "not \"name width height [class]\"")]
    [InlineData("nav_agents", "a-b 1 2", "letters, digits and _")]
    [InlineData("nav_agents", "a 1 2; a 3 4", "two nav agents are named \"a\"")]
    [InlineData("nav_agents", "a 0 2", "positive width and height")]
    [InlineData("nav_agents", "a 1 2 wobble", "a class is player, npc, or a number equal to one of their contents masks")]
    [InlineData("nav_agents", "a 1 2 0", "a class is player, npc, or a number equal to one of their contents masks")]
    [InlineData("nav_agents", "a 1 2 0x9", "a class is player, npc, or a number equal to one of their contents masks")]
    [InlineData("nav_step_height", "-1", "not 0 or more units")]
    [InlineData("nav_jump_height", "x", "not 0 or more units")]
    [InlineData("nav_jump_distance", "NaN", "not 0 or more units")]
    [InlineData("nav_cost_water", "0", "not a multiplier above 0 and at most 255")]
    [InlineData("nav_cost_ladder", "300", "not a multiplier above 0 and at most 255")]
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
