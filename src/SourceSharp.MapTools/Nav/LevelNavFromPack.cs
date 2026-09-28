//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>What linking a level's navigation gave.</summary>
/// <param name="Nav">The level's navigation, or null when some placed room has none (or the link asked for none).</param>
/// <param name="PackId">The pack's id, or null for a pack written before packs had one.</param>
/// <param name="LevelId">The level's id, for the map's worldspawn and the navigation's header.</param>
/// <param name="Warning">Why there is no navigation when the link asked for it, or null.</param>
public sealed record LevelNavLink(Nav3dLevel? Nav, Guid? PackId, Guid LevelId, string? Warning);

/// <summary>The link's navigation step: what <c>ssmap link</c> and <c>ssmap nav</c> run over the rooms a pack load gave.</summary>
public static class LevelNavFromPack
{
    /// <summary>
    /// The link switches that shape a level's outputs, spelled as the level
    /// id's option input: one spelling for every host, so <c>ssmap link</c>
    /// and <c>ssmap nav</c> derive the same id for the same level.
    /// </summary>
    /// <param name="navigation">Whether the link writes navigation.</param>
    /// <param name="compression">How the navigation file is stored.</param>
    /// <returns>The option strings.</returns>
    public static IReadOnlyList<string> IdOptions(bool navigation, NavCompression compression) =>
        [navigation ? "nav" : "no-nav", $"nav-codec {compression.Codec}:{compression.Level}"];

    /// <summary>
    /// Stitches the level's navigation from the placed rooms' own
    /// (<see cref="RoomObject.Nav"/>, which a pack load reads when its
    /// requests ask for navigation, at the turns they place), and derives the
    /// level id.
    /// </summary>
    /// <param name="layout">The level's layout (<see cref="LevelGrid.ToLayout"/>).</param>
    /// <param name="columns">The grid's columns.</param>
    /// <param name="rows">The grid's rows.</param>
    /// <param name="rooms">The placed rooms by name.</param>
    /// <param name="packId">The pack's id (<see cref="RoomNavPack.ReadPackIdAsync"/>), or null.</param>
    /// <param name="levelFile">The level file's bytes, as read: an input of the level id.</param>
    /// <param name="options">The link options that shape the outputs (<see cref="IdOptions"/>): an input of the level id.</param>
    /// <param name="includeNavigation">False to derive the ids only.</param>
    /// <param name="cancellationToken">Cancels the stitch.</param>
    /// <returns>The navigation (or why there is none) and the ids.</returns>
    /// <exception cref="LinkException">Rooms built with different settings.</exception>
    public static LevelNavLink Link(
        LevelLayout layout,
        int columns,
        int rows,
        Func<string, RoomObject> rooms,
        Guid? packId,
        ReadOnlySpan<byte> levelFile,
        IReadOnlyList<string> options,
        bool includeNavigation = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        Guid levelId = RoomCompileIds.LevelId(packId ?? Guid.Empty, levelFile, options);
        if (!includeNavigation)
        {
            return new LevelNavLink(null, packId, levelId, null);
        }

        string[] missing = [.. layout.Rooms.Select(r => r.Placement.Room).Distinct(StringComparer.Ordinal)
            .Where(room => rooms(room).Nav is null).Order(StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            return new LevelNavLink(null, packId, levelId,
                $"the room pack holds no navigation for {string.Join(", ", missing.Select(m => $"\"{m}\""))};"
                + " the level is linked without a .nav3d (compile the library with a build that writes navigation)");
        }

        Nav3dLevel nav = LevelNavLinker.Link(
            layout, columns, rows, (room, turn) => rooms(room).Nav!.At(turn), packId, levelId, cancellationToken);
        return new LevelNavLink(nav, packId, levelId, null);
    }
}
