//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>What linking a level's navigation from a pack gave.</summary>
/// <param name="Nav">The level's navigation, or null when the pack holds none for some placed room.</param>
/// <param name="PackId">The pack's id, or null for a pack written before packs had one.</param>
/// <param name="LevelId">The level's id, for the map's worldspawn and the navigation's header.</param>
/// <param name="Warning">Why there is no navigation, or null.</param>
public sealed record LevelNavLink(Nav3dLevel? Nav, Guid? PackId, Guid LevelId, string? Warning);

/// <summary>The link's navigation step, from a room pack: what <c>ssmap link</c> and <c>ssmap nav</c> run.</summary>
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
    /// Reads the pack's id and the placed rooms' navigation (and nothing
    /// else of the pack), derives the level id, and stitches the level's
    /// navigation.
    /// </summary>
    /// <param name="pack">The room pack; it must be able to seek.</param>
    /// <param name="index">The pack's index.</param>
    /// <param name="layout">The level's layout (<see cref="LevelGrid.ToLayout"/>).</param>
    /// <param name="columns">The grid's columns.</param>
    /// <param name="rows">The grid's rows.</param>
    /// <param name="levelFile">The level file's bytes, as read: an input of the level id.</param>
    /// <param name="options">The link options that shape the outputs: an input of the level id.</param>
    /// <param name="includeNavigation">False to derive the ids only, reading no navigation.</param>
    /// <param name="cancellationToken">Cancels the reads and the link.</param>
    /// <returns>The navigation (or why there is none) and the ids.</returns>
    /// <exception cref="LinkException">A navigation section this build cannot read, or rooms built with different settings.</exception>
    public static async Task<LevelNavLink> LinkAsync(
        Stream pack,
        RoomPackIndex index,
        LevelLayout layout,
        int columns,
        int rows,
        ReadOnlyMemory<byte> levelFile,
        IReadOnlyList<string> options,
        bool includeNavigation = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);
        Guid? packId = await RoomNavPack.ReadPackIdAsync(pack, index, cancellationToken).ConfigureAwait(false);
        Guid levelId = RoomCompileIds.LevelId(packId ?? Guid.Empty, levelFile.Span, options);
        if (!includeNavigation)
        {
            return new LevelNavLink(null, packId, levelId, null);
        }

        IReadOnlyDictionary<(string Room, int Turn), RoomNav>? navs = await RoomNavPack.ReadAsync(
            pack, index, layout.Rooms.Select(r => (r.Placement.Room, r.Placement.NormalizedRotation)), cancellationToken)
            .ConfigureAwait(false);
        if (navs is null)
        {
            string[] missing = [.. layout.Rooms.Select(r => r.Placement.Room).Distinct()
                .Where(room => index.Find(room)?.Find(RoomNavSection.Tag(0)) is null).Order(StringComparer.Ordinal)];
            return new LevelNavLink(null, packId, levelId,
                $"the room pack holds no navigation for {string.Join(", ", missing.Select(m => $"\"{m}\""))};"
                + " the level is linked without a .nav3d (compile the library with a build that writes navigation)");
        }

        Nav3dLevel nav = LevelNavLinker.Link(layout, columns, rows, (room, turn) => navs[(room, turn)], packId, levelId, cancellationToken);
        return new LevelNavLink(nav, packId, levelId, null);
    }
}
