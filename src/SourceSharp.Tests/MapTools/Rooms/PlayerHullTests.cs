//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A door kit admits a standing player (32 × 32 × 72) only when the door is
/// at least that wide and tall and stands on the floor.
/// </summary>
public sealed class PlayerHullTests
{
    [Fact]
    public void TheHullIsTheStandingPlayers()
    {
        Assert.Equal(32f, PlayerHull.Width);
        Assert.Equal(72f, PlayerHull.Height);
    }

    /// <summary>The sample's kit (a floor-to-ceiling door 96 wide) passes, and so does a door exactly a player's size.</summary>
    [Theory]
    [InlineData(96, 224, 16, 256)]
    [InlineData(32, 72, 92, 256)]
    public void ADoorAPlayerFitsThroughPasses(float width, float height, float depth, float cell) =>
        Assert.Null(PlayerHull.DoorProblem(new SocketKit(width, height, depth), cell));

    /// <summary>Too narrow, too low, or a sill above the floor, each named with the numbers that fix it.</summary>
    [Theory]
    [InlineData(24, 224, 16, 256, "is 24 wide; a standing player needs a door at least 32 wide")]
    [InlineData(96, 64, 96, 256, "is 64 tall; a standing player needs a door at least 72 tall")]
    [InlineData(96, 96, 16, 256, "puts the door's sill at z = 80 but the floor's top at z = 16 (the wall depth)")]
    [InlineData(96, 96, 16, 256, "a door stands on the floor when its height is cell size - 2 x wall depth = 224")]
    [InlineData(96, 240, 16, 256, "puts the door's sill at z = 8")]
    public void ADoorAPlayerCannotPassIsNamed(float width, float height, float depth, float cell, string problem) =>
        Assert.Contains(problem, PlayerHull.DoorProblem(new SocketKit(width, height, depth), cell), StringComparison.Ordinal);
}
