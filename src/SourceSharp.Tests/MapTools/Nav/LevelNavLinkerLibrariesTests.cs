//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A level's navigation stitched from rooms of several libraries (the
/// rooms design's D25): rooms of different libraries may differ in every
/// setting but the grid, the header takes the first placed library's, and
/// rooms of one library are held to one another as before.
/// </summary>
public sealed class LevelNavLinkerLibrariesTests(NavRoomsFixture fixture) : IClassFixture<NavRoomsFixture>
{
    private Nav3dLevel Link(LevelLayout layout, Func<string, RoomNav> navOf, Func<string, int>? libraryOf) =>
        LevelNavLinker.Link(layout, 2, 1, (room, turn) => navOf(room).Turned(turn), null, Guid.Empty, libraryOf);

    /// <summary>Another library's room with other step heights links; the header is the first placed library's, wherever it is placed.</summary>
    [Fact]
    public void AnotherLibrarysSettingsAreCarriedUnderTheFirsts()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));
        RoomNav steps = fixture.Nav("west") with { StepHeight = 12 };
        RoomNav Nav(string room) => room == "west" ? steps : fixture.Nav(room);

        Nav3dLevel level = Link(layout, Nav, room => room == "west" ? 1 : 0);
        Assert.Equal(fixture.Nav("east").StepHeight, level.StepHeight);

        // The first library placed second still gives the header.
        Nav3dLevel swapped = Link(layout, Nav, room => room == "west" ? 0 : 1);
        Assert.Equal(12, swapped.StepHeight);

        // One library, as before: refused.
        Assert.Contains("traversal limits", Assert.Throws<LinkException>(() => Link(layout, Nav, null)).Message, StringComparison.Ordinal);
        Assert.Contains("traversal limits", Assert.Throws<LinkException>(() => Link(layout, Nav, _ => 1)).Message, StringComparison.Ordinal);
    }

    /// <summary>Rooms of different libraries on different voxel grids are refused, naming the room.</summary>
    [Fact]
    public void AnotherGridIsRefusedAcrossLibraries()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));
        RoomNav coarse = RoomNavBuilder.Build(fixture.Definition("west"), fixture.Room("west").Bsp, [], RoomRole.None,
            NavRoomsFixture.Settings with { VoxelSize = 32 });
        Assert.Equal(
            "room \"west\"'s navigation was built on another voxel grid than the level's; a level's navigation is one grid.",
            Assert.Throws<LinkException>(() => Link(layout, room => room == "west" ? coarse : fixture.Nav(room), room => room == "west" ? 1 : 0)).Message);
    }

    /// <summary>A level of one library links through the overload with no libraries to the same file.</summary>
    [Fact]
    public void OneLibraryLinksAsBefore()
    {
        LevelLayout layout = fixture.Layout(("east", 0, 0, 0), ("west", 1, 0, 0));
        Assert.Equal(
            Nav3dWriter.Write(fixture.Link(layout, 2, 1)),
            Nav3dWriter.Write(Link(layout, fixture.Nav, _ => 0)));
    }
}
