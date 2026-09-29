//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>What a link is asked to do beyond linking: the entity budget's settings, and whether brushes fold.</summary>
/// <remarks>
/// A value, like every other option of the libraries: two links in one
/// process may use different settings, and nothing is kept between them.
/// </remarks>
public sealed record LevelLinkOptions
{
    /// <summary>
    /// The edicts left to the game at runtime, overriding the library's
    /// (<see cref="RoomLibrary.Options"/>); null to take the library's, or
    /// <see cref="EntityClassTable.DefaultReserve"/> when the library sets none.
    /// </summary>
    /// <remarks><c>ssmap link -entity-reserve N</c>. From 0 to <see cref="EntityClassTable.EdictCap"/>.</remarks>
    public int? EntityReserve { get; init; }

    /// <summary>The class table entities are counted with; null for <see cref="EntityClassTable.Default"/>.</summary>
    public EntityClassTable? EntityClasses { get; init; }

    /// <summary>
    /// Emit the Source Sharp mod's entity classes (<c>logic_room</c>) where
    /// they apply, rather than their stock fallbacks: <c>ssmap link
    /// -mod-entities</c>. Off by default, so a map runs on any Source game.
    /// </summary>
    /// <remarks>
    /// A pack holds each room as authored, so one pack links in both modes.
    /// With the flag the linked worldspawn records the mode and the
    /// contract's version (<see cref="RoomContracts.ModEntityContract"/>);
    /// without it the map carries neither key, and a level that uses no
    /// room-local names links to the bytes it always did.
    /// </remarks>
    public bool ModEntities { get; init; }

    /// <summary>
    /// Merge touching axis-aligned box brushes of the world into larger
    /// boxes (<c>LinkBrushFold</c>): on by default, off with <c>ssmap link
    /// -nofold</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rooms meet cell to cell, so a level is full of pairs that are one box
    /// in two brushes: floors and ceilings across every shared boundary,
    /// walls and jambs back to back. The engine loads at most 8192 brushes,
    /// and without the fold that is the limit a large level reaches first.
    /// Two boxes merge only when their union is exactly a box, their
    /// contents are identical and every side that coalesces has the same
    /// material and surface flags, so traces and physics meet the same
    /// solid with the same surfaces.
    /// </para>
    /// <para>
    /// What the fold does remove is the seam between the two boxes: a trace
    /// can no longer start in one and "leave" into the other, or stop on the
    /// face between them. Off, the link writes the rooms' brushes as they
    /// were compiled (less the jointed plugs), byte for byte.
    /// </para>
    /// </remarks>
    public bool FoldBrushes { get; init; } = true;

    /// <summary>No override: the library's reserve and the shipped class table.</summary>
    public static LevelLinkOptions Default { get => new(); }
}

/// <summary>One room's share of a level's entities.</summary>
/// <param name="Room">The room's name.</param>
/// <param name="Placements">How many times the level places it.</param>
/// <param name="Each">What one placement brings (<see cref="EntityTally"/>).</param>
public readonly record struct RoomEntityShare(string Room, int Placements, EntityTally Each)
{
    /// <summary>
    /// What all its placements bring together, when they do not all bring
    /// <see cref="Each"/>: after the naming resolver, one placement of a room
    /// may drop what another keeps (<c>room_needs</c>) or get entities the
    /// other does not (a written flag). Null when every placement brings
    /// <see cref="Each"/>, which is then the first placement's.
    /// </summary>
    public EntityTally? Summed { get; init; }

    /// <summary>The edicts all its placements bring.</summary>
    public long Edicts => Summed?.Edicts ?? (long)Placements * Each.Edicts;

    /// <summary>The entity-list entries all its placements bring.</summary>
    public long Listed => Summed?.Listed ?? (long)Placements * Each.Listed;
}

/// <summary>A level's entity budget, as the link found it: its totals, the budget, and what it warned of.</summary>
/// <remarks>
/// Returned with the linked level (<see cref="LinkedLevel.EntityBudget"/>)
/// so a host prints it as it likes; <c>ssmap link</c> prints
/// <see cref="Warnings"/> and then <see cref="Headroom"/>.
/// </remarks>
public sealed class LevelEntityReport
{
    internal LevelEntityReport(long edicts, long listed, int reserve, IReadOnlyList<RoomEntityShare> rooms, IReadOnlyList<string> warnings)
    {
        Edicts = edicts;
        Listed = listed;
        Reserve = reserve;
        Rooms = rooms;
        Warnings = warnings;
    }

    /// <summary>The level's edict estimate: the one worldspawn, the library's entities and every placement's edicts.</summary>
    public long Edicts { get; }

    /// <summary>The linked entity lump's entities: the one worldspawn, the library's entities and every placement's listed entities.</summary>
    public long Listed { get; }

    /// <summary>
    /// What the library's own entities bring (the sun and the controllers
    /// from the pack's library section): once per level, whatever it places.
    /// </summary>
    public EntityTally Library { get; init; }

    /// <summary>The edict cap, <see cref="EntityClassTable.EdictCap"/>.</summary>
    public int Cap => EntityClassTable.EdictCap;

    /// <summary>The edicts left to the game at runtime.</summary>
    public int Reserve { get; }

    /// <summary>The edicts the map may use without eating into the reserve: <see cref="Cap"/> less <see cref="Reserve"/>.</summary>
    public int Budget => Cap - Reserve;

    /// <summary>Each room the level places, by what it costs, most edicts first (ties by name, ordinal).</summary>
    public IReadOnlyList<RoomEntityShare> Rooms { get; }

    /// <summary>The warnings, each a whole sentence: the reserve eaten into, the entity handles passed.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The headroom line the link always prints.</summary>
    public string Headroom => LevelEntityBudget.HeadroomLine(Edicts, Budget, Reserve, Cap, Listed);
}

/// <summary>
/// The entity budget of a linked level: totals the placed rooms' entity
/// counts (<see cref="RoomEntityCounts"/>), warns when the map eats into the
/// runtime reserve, refuses a map over the edict cap, and reports the
/// headroom.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The engine networks at most 2048 edicts (decision D7 of the
/// rooms design makes that a top priority), and at runtime the game's own
/// players, bots, weapons, projectiles, ragdolls and pickups draw from the
/// same cap. Rooms repeat, so a room's entities are multiplied by its
/// placements, and a generated level can pass the cap without any one room
/// being large. So the link budgets <c>cap − reserve</c> for the map, where
/// the reserve is what the game needs at runtime: 512 by default, set per
/// library (<see cref="RoomLibraryOptions.EntityReserveKey"/>) and per link
/// (<see cref="LevelLinkOptions.EntityReserve"/>, which wins).
/// </para>
/// <para>
/// <b>What is counted.</b> A level's edicts are its one worldspawn, the
/// library's own entities (the sun and the other singletons of the pack's
/// library section, once per level), plus every placement's edicts,
/// each classified by the class table (<see cref="EntityClassTable"/>:
/// every class it does not name is an edict, which over-counts rather than
/// under-counts). Its entity list is the one worldspawn and the library's
/// entities plus every placement's entities that reach the lump
/// (everything but the compile-only classes, which the link strips).
/// When the room-local naming resolver runs (a room uses names, or the
/// link writes the mod's classes), the level is budgeted again from what it
/// left: entities dropped by <c>room_needs</c>, folded or merged are gone, and
/// what the linker wrote for a placement (flags, a <c>logic_room</c> or its
/// stock fallback) is counted with it.
/// </para>
/// <para>
/// <b>What it does.</b> Over the cap: refused, naming the most expensive
/// rooms by what all their placements bring. Past the budget (into the
/// reserve): a warning naming them the same way. Always: the headroom line.
/// The entity list is held to the 8192 entities a map may carry
/// (<see cref="MapFile.MaxMapEntities"/>; vbsp refuses more, so the
/// flattened level would not compile) and warned of past the server's
/// entity handles (<see cref="EntityClassTable.EntityHandles"/>, believed
/// 4096). It runs in <c>LevelLinker.CheckCapacity</c>, before any room is
/// planned: it needs a few numbers per room, not the rooms' lumps.
/// </para>
/// </remarks>
public static class LevelEntityBudget
{
    /// <summary>How many rooms a warning or refusal names, most expensive first.</summary>
    public const int NamedRooms = 5;

    /// <summary>The headroom line: <c>map entities {n} / budget {b} (reserve {r}, cap {c}); {m} entities in the entity list</c>.</summary>
    /// <param name="edicts">The map's edicts.</param>
    /// <param name="budget">The budget.</param>
    /// <param name="reserve">The reserve.</param>
    /// <param name="cap">The cap.</param>
    /// <param name="listed">The entity list.</param>
    /// <returns>The line.</returns>
    public static string HeadroomLine(long edicts, int budget, int reserve, int cap, long listed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"map entities {edicts} / budget {budget} (reserve {reserve}, cap {cap}); {listed} entities in the entity list");

    /// <summary>The reserve a link uses: the link's, else the library's, else <see cref="EntityClassTable.DefaultReserve"/>.</summary>
    /// <param name="options">The link's options.</param>
    /// <param name="library">The library's settings.</param>
    /// <returns>The reserve.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The chosen reserve is not from 0 to the cap.</exception>
    public static int ReserveFor(LevelLinkOptions options, RoomLibraryOptions library)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(library);
        int reserve = options.EntityReserve ?? library.EntityReserve ?? EntityClassTable.DefaultReserve;
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reserve, EntityClassTable.EdictCap);
        return reserve;
    }

    /// <summary>Budgets a level from its placements' counts.</summary>
    /// <param name="placements">Every placement's room name and counts, in layout order.</param>
    /// <param name="reserve">The reserve (<see cref="ReserveFor"/>).</param>
    /// <param name="table">The class table.</param>
    /// <returns>The report, with any warnings.</returns>
    /// <exception cref="LinkException">The map's edicts pass the cap, or its entity list passes <see cref="MapFile.MaxMapEntities"/>.</exception>
    public static LevelEntityReport Check(IEnumerable<(string Room, RoomEntityCounts Counts)> placements, int reserve, EntityClassTable table) =>
        Check(placements, reserve, table, null);

    /// <summary>
    /// Budgets a level from its placements' counts and the library's own
    /// entities, which the level carries once whatever it places.
    /// </summary>
    /// <param name="placements">Every placement's room name and counts, in layout order.</param>
    /// <param name="reserve">The reserve (<see cref="ReserveFor"/>).</param>
    /// <param name="table">The class table.</param>
    /// <param name="library">
    /// The library-wide entities by class (the sun and the controllers of the
    /// pack's library section), or null for none. They are the level's, not a
    /// room's: counted once, with the worldspawn, and never named among the
    /// most expensive rooms.
    /// </param>
    /// <returns>The report, with any warnings.</returns>
    /// <exception cref="LinkException">The map's edicts pass the cap, or its entity list passes <see cref="MapFile.MaxMapEntities"/>.</exception>
    public static LevelEntityReport Check(
        IEnumerable<(string Room, RoomEntityCounts Counts)> placements, int reserve, EntityClassTable table, RoomEntityCounts? library)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reserve, EntityClassTable.EdictCap);

        // Per room: how often it is placed, what the first placement brings,
        // and what all of them bring. A room's compiled counts are the same
        // object for every placement, so the tally is made once per counts
        // object; the naming resolver's per-placement counts are tallied each.
        Dictionary<string, (int Placements, EntityTally Each, EntityTally Sum, bool Uneven)> rooms = new(StringComparer.Ordinal);
        Dictionary<RoomEntityCounts, EntityTally> tallies = new(ReferenceEqualityComparer.Instance);
        long edicts = 1, listed = 1; // the level's one worldspawn
        EntityTally level = library?.Tally(table) ?? default;
        edicts += level.Edicts;
        listed += level.Listed;
        foreach ((string room, RoomEntityCounts counts) in placements)
        {
            if (!tallies.TryGetValue(counts, out EntityTally tally))
            {
                tallies[counts] = tally = counts.Tally(table);
            }

            (int placed, EntityTally each, EntityTally sum, bool uneven) = rooms.TryGetValue(room, out var share)
                ? share
                : (0, tally, default, false);
            rooms[room] = (
                placed + 1,
                each,
                new EntityTally(sum.Edicts + tally.Edicts, sum.ServerOnly + tally.ServerOnly, sum.CompileOnly + tally.CompileOnly),
                uneven || tally != each);
            edicts += tally.Edicts;
            listed += tally.Listed;
        }

        List<RoomEntityShare> shares = [.. rooms
            .Select(r => new RoomEntityShare(r.Key, r.Value.Placements, r.Value.Each) { Summed = r.Value.Uneven ? r.Value.Sum : null })
            .OrderByDescending(r => r.Edicts)
            .ThenBy(r => r.Room, StringComparer.Ordinal)];

        int cap = EntityClassTable.EdictCap;
        int budget = cap - reserve;
        if (edicts > cap)
        {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"map entities {edicts} exceed the cap of {cap} edicts; most expensive rooms: {Costliest(shares, s => s.Edicts)}"));
        }

        if (listed > MapFile.MaxMapEntities)
        {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"map entities: the entity list of {listed} exceeds the {MapFile.MaxMapEntities} entities a map may hold;"
                + $" most expensive rooms: {Costliest(shares, s => s.Listed)}"));
        }

        List<string> warnings = [];
        if (edicts > budget)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"map entities {edicts} / budget {budget} (reserve {reserve}, cap {cap}): the level uses {edicts - budget} of the reserve;"
                + $" most expensive rooms: {Costliest(shares, s => s.Edicts)}"));
        }

        if (listed > EntityClassTable.EntityHandles)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"map entities: the entity list of {listed} is past the {EntityClassTable.EntityHandles} entity handles"
                + $" a Source 2013 server is believed to have; check the game's limit."));
        }

        return new LevelEntityReport(edicts, listed, reserve, shares, warnings) { Library = level };
    }

    /// <summary>
    /// The most expensive rooms by <paramref name="cost"/>, as
    /// <c>{room} x{placements} = {cost}</c>, joined by commas: at most
    /// <see cref="NamedRooms"/>, rooms that cost nothing left out; "(none)"
    /// when no room costs anything.
    /// </summary>
    private static string Costliest(List<RoomEntityShare> shares, Func<RoomEntityShare, long> cost)
    {
        StringBuilder text = new();
        foreach (RoomEntityShare share in shares
            .Where(s => cost(s) > 0)
            .OrderByDescending(cost)
            .ThenBy(s => s.Room, StringComparer.Ordinal)
            .Take(NamedRooms))
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(CultureInfo.InvariantCulture, $"{share.Room} x{share.Placements} = {cost(share)}");
        }

        // Only a reserve of the whole cap is passed by the worldspawn alone.
        return text.Length == 0 ? "(none)" : text.ToString();
    }
}
