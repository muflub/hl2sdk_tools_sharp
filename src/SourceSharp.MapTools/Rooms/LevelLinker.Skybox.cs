//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// Where a level's skybox room stands, or null when its library has
    /// none: the library's skybox room (<see cref="RoomLibrary.SkyboxRoom"/>)
    /// one cell below the grid, under the level's south-west cell, unturned,
    /// with no joint and no cap.
    /// </summary>
    /// <param name="layout">The level, as its file places its rooms.</param>
    /// <param name="library">The rooms, and which is the skybox.</param>
    /// <returns>The skybox's placement, or null.</returns>
    /// <exception cref="LinkException">The level places the skybox room itself, or the library lacks it.</exception>
    /// <remarks>
    /// <para>
    /// <b>Below the grid</b> (open point O11, as recommended). A skybox is a
    /// sealed box the engine draws from its <c>sky_camera</c>, wherever it
    /// stands; below the grid it stands clear of every cell a level can
    /// place a room in, whatever the level's size, without widening the
    /// grid the top tree splits. Its place is a function of the layout alone
    /// (the lowest column and row any room stands in), so the link and the
    /// flatten put it in the same place.
    /// </para>
    /// <para>
    /// <b>Beside the layout, not in it.</b> The skybox is added to the
    /// placements the link carries (the last, after every room of the level)
    /// but never to the level's layout: the grid's logic (joints,
    /// neighbours, reachability, furniture, transitions, names, the door
    /// flows) is the layout's, and a room below a cell shares that cell's
    /// column and row. Where the link keys placements by cell it leaves the
    /// skybox out (<see cref="GridCells"/>), and the top tree sends
    /// everything below the grid's floor to it (<see cref="Assemble"/>).
    /// </para>
    /// </remarks>
    internal static RoomInstance? SkyboxOf(LevelLayout layout, RoomLibrary library)
    {
        if (library.SkyboxRoom is not { } name)
        {
            return null;
        }

        foreach (RoomInstance room in layout.Rooms)
        {
            if (string.Equals(room.Placement.Room, name, StringComparison.Ordinal))
            {
                throw new LinkException(
                    $"level {layout.Name} places the skybox room {name} at cell ({room.Placement.CellX}, {room.Placement.CellY});"
                    + " the link places the skybox below the grid itself.");
            }
        }

        _ = library.Get(name);
        return new RoomInstance(SkyboxPlacement(layout, name), [], []);
    }

    /// <summary>
    /// Where the skybox room stands in a level: one cell below the level's
    /// lowest column and row, unturned (<see cref="SkyboxOf"/>); one rule for
    /// the link and the flatten.
    /// </summary>
    /// <param name="layout">The level, as its file places its rooms.</param>
    /// <param name="name">The skybox room's name.</param>
    /// <returns>The placement.</returns>
    public static RoomPlacement SkyboxPlacement(LevelLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        (int minx, int miny, _, _) = Extent(layout);
        return new RoomPlacement(name, minx, miny, 0) { Level = -1 };
    }

    /// <summary>
    /// The placements that stand in the grid, by cell: every placement on
    /// the grid's level, the skybox below it left out (it shares its cell's
    /// column and row, and would take the room there).
    /// </summary>
    private static Dictionary<(int X, int Y), ResolvedPlacement> GridCells(ResolvedPlacement[] resolved)
    {
        Dictionary<(int X, int Y), ResolvedPlacement> byCell = new(resolved.Length);
        foreach (ResolvedPlacement placement in resolved)
        {
            if (placement.Instance.Placement.Level == 0)
            {
                byCell[(placement.Instance.Placement.CellX, placement.Instance.Placement.CellY)] = placement;
            }
        }

        return byCell;
    }

    /// <summary>The placements of a layout's rooms, the skybox, when there is one, not among them.</summary>
    private static ResolvedPlacement[] OnGrid(ResolvedPlacement[] resolved) =>
        resolved.Length > 0 && resolved[^1].Instance.Placement.Level != 0 ? resolved[..^1] : resolved;
}
