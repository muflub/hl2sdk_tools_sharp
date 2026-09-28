//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One class's share of a room's entities.</summary>
/// <param name="ClassName">The class as the room's entity lump spells it; an empty string for an entity with no class.</param>
/// <param name="Count">How many of the room's entities have it.</param>
public readonly record struct RoomEntityClassCount(string ClassName, int Count);

/// <summary>A room's or a level's entities, sorted by what they cost (<see cref="EntityClassTable"/>).</summary>
/// <param name="Edicts">
/// Entities that take an edict: <see cref="EntityCost.Edict"/> and
/// <see cref="EntityCost.SpawnTransient"/>, the second counted because the
/// spawn's peak has to fit under the cap too.
/// </param>
/// <param name="ServerOnly">Entities in the server's entity list that take no edict.</param>
/// <param name="CompileOnly">Entities the link strips, which cost nothing.</param>
public readonly record struct EntityTally(int Edicts, int ServerOnly, int CompileOnly)
{
    /// <summary>The entities that reach the linked entity lump: everything but the compile-only ones.</summary>
    public int Listed => Edicts + ServerOnly;
}

/// <summary>
/// How many entities of each class a compiled room brings to a level: what
/// <c>ssmap room</c> counts once and stores in the pack, so that the link
/// budgets a level's entities from a few numbers per room rather than by
/// parsing every placed room's entity lump.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> Every entity of the room's compiled entity lump
/// except its worldspawn: the link merges every room's worldspawn into the
/// one the level has (<c>LevelLinker.MergeEntities</c>, which tells them
/// apart the same way, by the class <c>worldspawn</c> spelt exactly), and
/// the level counts that one once. So a level's entity list is 1 plus the
/// sum of its placements' counts.
/// </para>
/// <para>
/// <b>By class, not by cost.</b> The counts are kept per class, not as an
/// edict estimate, so the estimate is made with the table the link is
/// given (<see cref="Tally"/>): a library or mod that later classifies a
/// class differently changes the budget without a recompile, and a pack
/// never holds an estimate made by a table the reader no longer uses.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>ECNT</c>) follows the
/// link sections' conventions (<see cref="RoomLinkSections"/>): a codec
/// byte, the payload's decoded length (<c>int64</c>, big-endian), and a
/// payload that starts with an <c>int32</c> revision; a section of a codec
/// this build does not read, or one that decodes to another length than
/// it records, is refused naming the room; one of a revision it does not
/// know reads as absent, and the counts are computed from the lump. The
/// payload after the revision: the class count, then per class, in ordinal
/// order of the class name, the name (an <c>int32</c> byte length and
/// UTF-8) and its <c>int32</c> count. A room's counts do not change with a
/// quarter turn, so the section holds one payload (the rotation count of
/// the design's per-rotation sections is left out: it would always be 1).
/// </para>
/// <para>
/// <b>Its own tag, not in <c>LNKA</c>.</b> <c>LNKA</c> is written only for
/// a room the link would accept (its existence is that verdict), while
/// counts are wanted for every room: <c>ssmap rooms</c> lists them and
/// <c>ssmap layout</c> budgets with them whatever the link will say.
/// Raising <c>LNKA</c>'s revision would also have made every existing
/// pack's <c>LNKA</c> read as absent, sending those links back to the slow
/// path until a recompile; a new tag leaves them as fast as they were. And
/// <c>ssmap layout</c> and <c>ssmap rooms</c> read the counts of every room
/// of a library, which a tag of their own keeps to a few bytes a room.
/// </para>
/// <para>
/// <b>Binding.</b> Counts read from a pack or made by <see cref="Of"/>
/// remember the BSP they describe, and the linker uses them only for that
/// very BSP (<see cref="IsFor"/>); a room copied with its BSP replaced is
/// counted afresh, as the link's other stored work is.
/// </para>
/// </remarks>
public sealed class RoomEntityCounts
{
    /// <summary>The tag of a room's entity count section.</summary>
    public const string SectionTag = "ECNT";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    private readonly BspData? _bsp;

    private RoomEntityCounts(IReadOnlyList<RoomEntityClassCount> classes, BspData? bsp)
    {
        Classes = classes;
        Entities = classes.Sum(c => c.Count);
        _bsp = bsp;
    }

    /// <summary>The room's entities by class, in ordinal order of the class, each class once.</summary>
    public IReadOnlyList<RoomEntityClassCount> Classes { get; }

    /// <summary>The room's entities, its worldspawn left out.</summary>
    public int Entities { get; }

    /// <summary>Counts a compiled room's entities.</summary>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The counts, bound to <paramref name="bsp"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    public static RoomEntityCounts Of(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        SortedDictionary<string, int> classes = new(StringComparer.Ordinal);
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            string name = entity.ClassName;
            if (string.Equals(name, "worldspawn", StringComparison.Ordinal))
            {
                continue;
            }

            classes[name] = classes.GetValueOrDefault(name) + 1;
        }

        return new RoomEntityCounts([.. classes.Select(c => new RoomEntityClassCount(c.Key, c.Value))], bsp);
    }

    /// <summary>
    /// Counts from a list of classes: what one placement brings once the
    /// naming resolver has dropped, folded, merged and written entities, so
    /// the link budgets the level it really writes. Bound to no BSP.
    /// </summary>
    /// <param name="classNames">Every entity's class, worldspawn left out.</param>
    /// <returns>The counts.</returns>
    internal static RoomEntityCounts FromClasses(IEnumerable<string> classNames)
    {
        SortedDictionary<string, int> classes = new(StringComparer.Ordinal);
        foreach (string name in classNames)
        {
            classes[name] = classes.GetValueOrDefault(name) + 1;
        }

        return new RoomEntityCounts([.. classes.Select(c => new RoomEntityClassCount(c.Key, c.Value))], null);
    }

    /// <summary>The room's entities sorted by what the table says each class costs.</summary>
    /// <param name="table">The class table.</param>
    /// <returns>The edicts, server-only and compile-only entities.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public EntityTally Tally(EntityClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        int edicts = 0, serverOnly = 0, compileOnly = 0;
        foreach (RoomEntityClassCount entry in Classes)
        {
            switch (table.Classify(entry.ClassName))
            {
                case EntityCost.ServerOnly:
                    serverOnly += entry.Count;
                    break;
                case EntityCost.CompileOnly:
                    compileOnly += entry.Count;
                    break;
                default:
                    edicts += entry.Count;
                    break;
            }
        }

        return new EntityTally(edicts, serverOnly, compileOnly);
    }

    /// <summary>Whether these are the counts of exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    internal bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The counts bound to another BSP: what the pack reader does once it has loaded the room they sit with.</summary>
    internal RoomEntityCounts For(BspData bsp) => new(Classes, bsp);

    /// <summary>The pack section holding these counts.</summary>
    /// <param name="codec">How to store the payload; none by default (a few bytes a room gain nothing from compression).</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Classes.Count);
        foreach (RoomEntityClassCount entry in Classes)
        {
            w.String(entry.ClassName);
            w.Int(entry.Count);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's counts from its section, unbound; or null when the section is
    /// absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The counts, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload out of shape: cut short, a
    /// negative count, a class twice or out of order, bytes after its end.
    /// </exception>
    internal static RoomEntityCounts? Read(ArraySegment<byte>? section, string room)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int count = r.Count("classes");
        List<RoomEntityClassCount> classes = new(count);
        string? previous = null;
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            string name = r.String();
            int n = r.Int();
            total += n;
            if (n < 1 || total > int.MaxValue)
            {
                throw r.Mismatch($"{n} entities of class \"{name}\"");
            }

            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw r.Mismatch($"class \"{name}\" after \"{previous}\"; the classes are distinct and in ordinal order");
            }

            classes.Add(new RoomEntityClassCount(name, n));
            previous = name;
        }

        r.End();
        return new RoomEntityCounts(classes, null);
    }
}
