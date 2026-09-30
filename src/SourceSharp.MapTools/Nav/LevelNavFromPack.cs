//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// A level's navigation, planned but not yet built: the ids the map needs
/// now, and the stitching, which can run after the map is written or beside
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two steps.</b> The level id is derived from the pack id, the level
/// file and the link options, none of which is navigation, so the map can be
/// stamped with it and written before a single leaf is stitched. The plan
/// holds everything the stitch needs and nothing else; <see cref="BuildAsync"/>
/// does the work on the thread pool and hands back the level, so a host can
/// write the map first and await the navigation afterwards (as
/// <c>ssmap link</c> does), or start the navigation first and write the map
/// while it runs. Either way the map never waits for navigation work.
/// </para>
/// <para>
/// <b>Nothing to leak.</b> The build holds only managed memory it allocates
/// itself: no file, no handle, no pooled buffer, nothing shared with another
/// build. A cancelled or failed build throws and leaves nothing behind, and
/// the plan can be built again. Writing the file is a separate step
/// (<see cref="WriteAsync"/>) through the file system's replace, so a
/// cancelled write leaves the previous file (or none), never half of one.
/// </para>
/// </remarks>
public sealed class LevelNavPlan
{
    private readonly LevelLayout? _layout;
    private readonly int _columns;
    private readonly int _rows;
    private readonly Func<string, RoomObject>? _rooms;
    private readonly Func<string, int>? _libraryOf;

    internal LevelNavPlan(
        Guid? packId, Guid levelId, string? warning, LevelLayout? layout, int columns, int rows, Func<string, RoomObject>? rooms, Func<string, int>? libraryOf = null)
    {
        _libraryOf = libraryOf;
        PackId = packId;
        LevelId = levelId;
        Warning = warning;
        _layout = layout;
        _columns = columns;
        _rows = rows;
        _rooms = rooms;
    }

    /// <summary>The pack's id, or null for a pack written before packs had one.</summary>
    public Guid? PackId { get; }

    /// <summary>The level's id, for the map's worldspawn and the navigation's header.</summary>
    public Guid LevelId { get; }

    /// <summary>Why there is no navigation when the link asked for it, or null.</summary>
    public string? Warning { get; }

    /// <summary>
    /// Whether <see cref="BuildAsync"/> builds a navigation: the link asked
    /// for one and every placed room has its own. When true, the map carries
    /// the ids (<see cref="RoomCompileIds.Stamp"/>); when false it does not,
    /// and is byte for byte the map a link without navigation writes.
    /// </summary>
    public bool WritesNavigation => _layout is not null;

    /// <summary>Stitches the level's navigation, on the thread pool.</summary>
    /// <param name="cancellationToken">Cancels the stitch between rooms and during the jump search.</param>
    /// <returns>The level's navigation.</returns>
    /// <exception cref="InvalidOperationException">The plan builds no navigation (<see cref="WritesNavigation"/> is false).</exception>
    /// <exception cref="LinkException">Rooms built with different settings.</exception>
    public Task<Nav3dLevel> BuildAsync(CancellationToken cancellationToken = default)
    {
        if (_layout is null || _rooms is null)
        {
            throw new InvalidOperationException("this plan builds no navigation: " + (Warning ?? "the link asked for none."));
        }

        LevelLayout layout = _layout;
        Func<string, RoomObject> rooms = _rooms;
        Guid? packId = PackId;
        Guid levelId = LevelId;
        int columns = _columns;
        int rows = _rows;
        Func<string, int>? libraryOf = _libraryOf;
        return Task.Run(
            () => LevelNavLinker.Link(layout, columns, rows, (room, turn) => rooms(room).Nav!.At(turn), packId, levelId, libraryOf, cancellationToken),
            cancellationToken);
    }

    /// <summary>Writes a level's navigation to a file, replacing any there.</summary>
    /// <param name="disk">The file system.</param>
    /// <param name="path">Where: <c>&lt;map&gt;.nav3d</c> beside the map, by convention.</param>
    /// <param name="level">The navigation.</param>
    /// <param name="compression">How the file's image is stored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The file's length.</returns>
    public static async Task<long> WriteAsync(
        IFileSystem disk, VPath path, Nav3dLevel level, NavCompression compression, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(level);
        byte[] bytes = Nav3dWriter.Write(level, compression);
        cancellationToken.ThrowIfCancellationRequested();
        await disk.ReplaceAsync(
            path, async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return bytes.Length;
    }
}

/// <summary>The link's navigation step: what <c>ssmap link</c> and <c>ssmap nav</c> run over the rooms a pack load gave.</summary>
public static class LevelNavFromPack
{
    /// <summary>
    /// How <c>ssmap link</c> stores a level's <c>.nav3d</c> unless told
    /// otherwise: Brotli at quality 5. The measured table is in
    /// <c>docs/nav3d-format.md</c>; the owner's rule for this one file is to
    /// compress when it matters, because the navigation is written after the
    /// map and a game loads it once, off the frame.
    /// </summary>
    public static NavCompression DefaultCompression => new(NavCodec.Brotli, 5);

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
    /// Plans the level's navigation: derives the level id, finds whether every
    /// placed room has navigation (<see cref="RoomObject.Nav"/>, which a pack
    /// load reads when its requests ask for it), and refuses a point of
    /// interest in a doorway the level caps. Cheap: nothing is stitched.
    /// </summary>
    /// <param name="layout">The level's layout (<see cref="LevelGrid.ToLayout"/>).</param>
    /// <param name="columns">The grid's columns.</param>
    /// <param name="rows">The grid's rows.</param>
    /// <param name="rooms">The placed rooms by name.</param>
    /// <param name="packId">The pack's id (<see cref="RoomNavPack.ReadPackIdAsync"/>), or null.</param>
    /// <param name="levelFile">The level file's bytes, as read: an input of the level id.</param>
    /// <param name="options">The link options that shape the outputs (<see cref="IdOptions"/>): an input of the level id.</param>
    /// <param name="includeNavigation">False to derive the ids only.</param>
    /// <param name="libraryOf">
    /// Which of the level's libraries a placed room comes from, for a level
    /// of several (<see cref="LevelNavLinker.Link(LevelLayout, int, int, Func{string, int, RoomNav}, Guid?, Guid, Func{string, int}?, CancellationToken)"/>);
    /// null for a level of one.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="LinkException">A point of interest stands in a capped doorway.</exception>
    public static LevelNavPlan Plan(
        LevelLayout layout,
        int columns,
        int rows,
        Func<string, RoomObject> rooms,
        Guid? packId,
        ReadOnlySpan<byte> levelFile,
        IReadOnlyList<string> options,
        bool includeNavigation = true,
        Func<string, int>? libraryOf = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        Guid levelId = RoomCompileIds.LevelId(packId ?? Guid.Empty, levelFile, options);
        if (!includeNavigation)
        {
            return new LevelNavPlan(packId, levelId, null, null, columns, rows, null);
        }

        string[] missing = [.. layout.Rooms.Select(r => r.Placement.Room).Distinct(StringComparer.Ordinal)
            .Where(room => rooms(room).Nav is null).Order(StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            return new LevelNavPlan(packId, levelId,
                $"the room pack holds no navigation for {string.Join(", ", missing.Select(m => $"\"{m}\""))};"
                + " the level is linked without a .nav3d (compile the library with a build that writes navigation)",
                null, columns, rows, null);
        }

        LevelNavLinker.CheckCappedDoorways(
            layout, [.. layout.Rooms.Select(r => rooms(r.Placement.Room).Nav!.At(r.Placement.NormalizedRotation))]);
        return new LevelNavPlan(packId, levelId, null, layout, columns, rows, rooms, libraryOf);
    }
}
