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
/// The linker's cost in the size of the level: the passes that run before
/// any room is planned stay linear in the room count, so a level far past
/// the format's limits is refused in moments rather than after minutes.
/// </summary>
/// <remarks>
/// The budgets are generous (tens of seconds for work that takes well under
/// one) so a loaded machine never fails them; what they catch is a pass
/// that is quadratic in the rooms, which at these sizes takes minutes.
/// </remarks>
public sealed class LevelLinkerScaleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checking the joints of a 400 x 400 level (160,000 rooms, 638,400
    /// joints) finds each joint's neighbour by its cell, not by a scan of
    /// every room: a scan is ~5·10¹⁰ comparisons, minutes of work before the
    /// level can even be measured against the format's limits.
    /// </summary>
    [Fact]
    public async Task CheckingTheJointsIsLinearInTheRooms()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        LevelLayout layout = HubGrid(library, 400);

        Task check = Task.Run(() => LevelLinker.ValidateJoints(layout, library));
        Assert.True(await Task.WhenAny(check, Task.Delay(Budget)) == check, "the joint check did not finish within its budget");
        await check;
    }

    /// <summary>A grid of hubs, every one turned the same way, with joints wherever two meet.</summary>
    internal static LevelLayout HubGrid(RoomLibrary library, int size)
    {
        LevelCell?[] cells = new LevelCell?[size * size];
        Array.Fill(cells, new LevelCell("hub", 0));
        LevelGrid grid = new("grid", "rooms.vmf", size, size, cells);
        return grid.ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
    }
}
