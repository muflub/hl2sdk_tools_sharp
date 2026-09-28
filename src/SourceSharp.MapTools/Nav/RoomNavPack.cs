//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>How <c>ssmap room</c> stores a room's navigation in the pack.</summary>
public sealed record RoomNavPackOptions
{
    /// <summary>
    /// Whether the pack also carries each room turned by one, two and three
    /// quarter turns (<c>NVR1</c> to <c>NVR3</c>), so the link reads a
    /// placement's turn instead of turning turn 0 itself. Off by default:
    /// see <c>docs/nav3d-format.md</c> for the measured trade.
    /// </summary>
    public bool StoreAllTurns { get; init; }

    /// <summary>How each section's payload is stored; none by default (the measured fastest to link).</summary>
    public NavCompression Compression { get; init; } = NavCompression.None;
}

/// <summary>A room's navigation in and out of a room pack.</summary>
public static class RoomNavPack
{
    /// <summary>The sections a room's navigation adds to its pack entry.</summary>
    /// <param name="nav">The room's navigation at turn 0.</param>
    /// <param name="options">How to store it.</param>
    /// <returns><c>NVR0</c>, and <c>NVR1</c> to <c>NVR3</c> when the turns are stored.</returns>
    public static IReadOnlyList<RoomPackSectionData> Sections(RoomNav nav, RoomNavPackOptions options)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(options);
        if (nav.Turn != 0)
        {
            throw new ArgumentException("a room's navigation is packed from turn 0.", nameof(nav));
        }

        List<RoomPackSectionData> sections = [new(RoomNavSection.Tag(0), RoomNavSection.Write(nav, options.Compression))];
        if (options.StoreAllTurns)
        {
            for (int t = 1; t < 4; t++)
            {
                sections.Add(new(RoomNavSection.Tag(t), RoomNavSection.Write(nav.Turned(t), options.Compression)));
            }
        }

        return sections;
    }

    /// <summary>The pack's id, from its <see cref="RoomCompileIds.PackSection"/> library section, or null.</summary>
    /// <param name="pack">The pack; it must be able to seek.</param>
    /// <param name="index">Its index.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The id, or null for a pack written before packs had one.</returns>
    public static async Task<Guid?> ReadPackIdAsync(Stream pack, RoomPackIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        foreach (RoomPackSection section in index.LibrarySections)
        {
            if (section.Tag == RoomCompileIds.PackSection)
            {
                return RoomCompileIds.FromBytes(
                    await RoomPack.ReadSectionAsync(pack, index, section, cancellationToken).ConfigureAwait(false));
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the navigation of the placed rooms at their turns, and nothing
    /// else of the pack: per wanted turn, its own section when the pack has
    /// it, else turn 0's, turned.
    /// </summary>
    /// <param name="pack">The pack; it must be able to seek.</param>
    /// <param name="index">Its index.</param>
    /// <param name="wanted">The rooms and turns the level places; repeats are read once.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The navigation by room and turn, or null when some wanted room has no navigation in the pack.</returns>
    /// <exception cref="LinkException">A room is not in the pack, or its navigation section is not one this build reads.</exception>
    public static async Task<IReadOnlyDictionary<(string Room, int Turn), RoomNav>?> ReadAsync(
        Stream pack,
        RoomPackIndex index,
        IEnumerable<(string Room, int Turn)> wanted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(wanted);
        Dictionary<(string, int), RoomNav> result = [];
        Dictionary<string, RoomNav> turnZero = new(StringComparer.Ordinal);
        foreach ((string room, int turn) in wanted.Distinct().OrderBy(w => w.Room, StringComparer.Ordinal).ThenBy(w => w.Turn))
        {
            RoomPackEntry entry = index.Find(room) ?? throw new LinkException($"the room pack has no room \"{room}\".");
            if (entry.Find(RoomNavSection.Tag(turn)) is { } own)
            {
                result[(room, turn)] = await ReadOneAsync(pack, index, own, room, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!turnZero.TryGetValue(room, out RoomNav? zero))
            {
                if (entry.Find(RoomNavSection.Tag(0)) is not { } section)
                {
                    return null;
                }

                zero = await ReadOneAsync(pack, index, section, room, cancellationToken).ConfigureAwait(false);
                turnZero[room] = zero;
            }

            result[(room, turn)] = zero.Turned(turn);
        }

        return result;
    }

    private static async Task<RoomNav> ReadOneAsync(
        Stream pack, RoomPackIndex index, RoomPackSection section, string room, CancellationToken cancellationToken)
    {
        byte[] bytes = await RoomPack.ReadSectionAsync(pack, index, section, cancellationToken).ConfigureAwait(false);
        try
        {
            return RoomNavSection.Read(bytes);
        }
        catch (InvalidDataException exception)
        {
            throw new LinkException($"room pack entry \"{room}\"'s \"{section.Tag}\" section: {exception.Message}");
        }
    }
}
