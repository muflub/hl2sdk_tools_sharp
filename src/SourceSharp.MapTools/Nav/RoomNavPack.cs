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
    /// placement's turn instead of turning turn 0 itself. On by default.
    /// </summary>
    /// <remarks>
    /// Turning is a lossless permutation and cheap, but not free, and the
    /// owner's rule is that disk is cheap and link time decides, so the four
    /// turns are stored. With the clearance grid the difference is inside
    /// the noise: on the 256-room stress library's 16x16 level, five
    /// interleaved cold <c>ssmap link</c> runs took a median 1.43 s from a
    /// pack with the four turns and 1.50 s from one with turn 0 alone (4-core
    /// machine, other work running), and the navigation sections are 16.2 MB
    /// against 4.1 MB raw. The link gives the same file either way apart
    /// from the ids (the pack id covers the pack options), which a fact
    /// checks.
    /// </remarks>
    public bool StoreAllTurns { get; init; } = true;

    /// <summary>How each section's payload is stored: Brotli at quality 5 by default.</summary>
    /// <remarks>
    /// Measured on the same library's 1,024 navigation sections (256 rooms,
    /// four turns): stored raw they are 16.2 MB, Deflate 6 1.44 MB, Brotli 5
    /// 0.98 MB (94% smaller), Brotli 11 0.91 MB but 26 s to encode. Reading
    /// every section back took 20 ms raw and 35 ms from Brotli 5 in a warm
    /// process, so a link, which reads one turn per placed room, spends a
    /// few milliseconds more decoding, and the five-run link medians were
    /// 1.43 s raw and 1.47 s from Brotli 5: a tie inside the noise of a
    /// 1.2-1.6 s link. With link time tied, the fifteenfold smaller pack
    /// (27.1 MB to 11.9 MB whole) decides. Version 1's octree sections were
    /// stored raw because they were then the fastest to link.
    /// </remarks>
    public NavCompression Compression { get; init; } = new(NavCodec.Brotli, 5);
}

/// <summary>
/// A room's navigation at the turns a link or a pack has it: turn 0 as the
/// room was built, or the turns read from a pack, each other turn derived
/// on first use by turning one that is there.
/// </summary>
/// <remarks>
/// Belongs to one room object: a library compile makes it from the room's
/// turn-0 build, a pack load from the room's <c>NVR</c><i>r</i> sections.
/// A turn derived here is kept for the object's life, so a level placing a
/// room at one turn many times turns it once; nothing is shared between
/// rooms or compiles.
/// </remarks>
public sealed class RoomNavTurns
{
    private readonly RoomNav?[] _turns = new RoomNav?[4];
    private readonly Lock _gate = new();

    private RoomNavTurns()
    {
    }

    /// <summary>The navigation of a room as built, at turn 0.</summary>
    /// <param name="nav">The navigation, at any turn.</param>
    /// <returns>The turns, holding that one.</returns>
    public static RoomNavTurns Of(RoomNav nav)
    {
        ArgumentNullException.ThrowIfNull(nav);
        RoomNavTurns turns = new();
        turns._turns[nav.Turn] = nav;
        return turns;
    }

    /// <summary>The navigation at turn 0: the room as authored.</summary>
    public RoomNav Base => At(0);

    /// <summary>Whether a turn is held as read or built, rather than derived.</summary>
    /// <param name="turn">The turn, 0 to 3.</param>
    /// <returns>True when held.</returns>
    public bool Has(int turn)
    {
        lock (_gate)
        {
            return _turns[((turn % 4) + 4) % 4] is not null;
        }
    }

    /// <summary>The navigation at a turn, derived from a held turn when not held.</summary>
    /// <param name="turn">Counter-clockwise quarter turns.</param>
    /// <returns>The navigation at that turn.</returns>
    public RoomNav At(int turn)
    {
        int r = ((turn % 4) + 4) % 4;
        lock (_gate)
        {
            if (_turns[r] is { } held)
            {
                return held;
            }

            RoomNav from = _turns.First(t => t is not null)!;
            RoomNav turned = from.Turned(r - from.Turn);
            _turns[r] = turned;
            return turned;
        }
    }

    internal void Add(RoomNav nav)
    {
        lock (_gate)
        {
            _turns[nav.Turn] = nav;
        }
    }
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

    /// <summary>
    /// A room's link sections and navigation sections in one pack order: the
    /// shared link section, then per turn that turn's link sections followed
    /// by its <c>NVR</c><i>r</i>, so everything a link reads for a room at one
    /// turn lies in one run of the pack.
    /// </summary>
    /// <param name="link">The link sections, in their own order (<c>LNKA</c>, then per turn).</param>
    /// <param name="nav">The navigation sections (<see cref="Sections"/>).</param>
    /// <returns>The sections in pack order.</returns>
    public static IReadOnlyList<RoomPackSectionData> Interleave(
        IReadOnlyList<RoomPackSectionData> link, IReadOnlyList<RoomPackSectionData> nav)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(nav);
        if (nav.Count == 0)
        {
            return link;
        }

        List<RoomPackSectionData> ordered = new(link.Count + nav.Count);
        List<RoomPackSectionData> pending = [.. nav];
        int TurnOf(string tag) => tag.Length == 4 && tag[3] is >= '0' and <= '3' && tag != RoomLinkSections.SharedTag ? tag[3] - '0' : -1;
        foreach (RoomPackSectionData section in link)
        {
            int turn = TurnOf(section.Tag);

            // Before the first link section of a later turn, every earlier
            // turn's navigation.
            for (int i = 0; i < pending.Count && turn >= 0;)
            {
                if (TurnOf(pending[i].Tag) < turn)
                {
                    ordered.Add(pending[i]);
                    pending.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            ordered.Add(section);
        }

        ordered.AddRange(pending);
        return ordered;
    }

    /// <summary>
    /// A room's navigation from the sections a pack load read for it: every
    /// <c>NVR</c><i>r</i> present and of a revision this build reads.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="section">The bytes of one of the room's sections, by tag, or null when not read or not there.</param>
    /// <returns>The navigation, or null when no readable section was read.</returns>
    /// <exception cref="LinkException">A section this build cannot decode (an unknown codec, a damaged payload), naming the room.</exception>
    public static RoomNavTurns? FromSections(string room, Func<string, ArraySegment<byte>?> section)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(section);
        RoomNavTurns? turns = null;
        for (int t = 0; t < 4; t++)
        {
            string tag = RoomNavSection.Tag(t);
            if (section(tag) is not { } bytes)
            {
                continue;
            }

            RoomNav? nav;
            try
            {
                nav = RoomNavSection.Read(bytes);
            }
            catch (InvalidDataException exception)
            {
                throw new LinkException($"room pack entry \"{room}\"'s \"{tag}\" section: {exception.Message}");
            }

            if (nav is null)
            {
                continue;
            }

            if (nav.Turn != t)
            {
                throw new LinkException($"room pack entry \"{room}\"'s \"{tag}\" section holds turn {nav.Turn}.");
            }

            if (turns is null)
            {
                turns = RoomNavTurns.Of(nav);
            }
            else
            {
                turns.Add(nav);
            }
        }

        return turns;
    }

    /// <summary>The tags a link reads for a room's navigation at a turn: its own section, and turn 0's to turn when the pack lacks it.</summary>
    /// <param name="entry">The room's pack entry.</param>
    /// <param name="turn">The turn, 0 to 3.</param>
    /// <returns>The sections to read.</returns>
    public static IEnumerable<RoomPackSection> SectionsFor(RoomPackEntry entry, int turn)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Find(RoomNavSection.Tag(turn)) is { } own)
        {
            yield return own;
        }
        else if (turn != 0 && entry.Find(RoomNavSection.Tag(0)) is { } zero)
        {
            yield return zero;
        }
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
}
