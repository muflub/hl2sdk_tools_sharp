//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a room's names say about it, as <c>ssmap rooms</c> lists them: its
/// local names, the neighbours it reaches for by authored direction (the
/// ones a level must place for nothing to be dropped), the flags and hub it
/// uses, its <c>room_needs</c> conditions, and what the room compile warned of.
/// </summary>
/// <remarks>
/// Read from the room's turn-0 names (<c>NAM0</c>), which are in the room's
/// own frame: a direction here is the one the author wrote, whatever turn
/// a level places the room at.
/// </remarks>
public sealed class RoomNameSummary
{
    private RoomNameSummary(
        IReadOnlyList<string> localNames,
        IReadOnlyDictionary<RoomDirection, int> neighbours,
        IReadOnlyList<string> linkerNames,
        IReadOnlyList<string> needs,
        IReadOnlyList<string> warnings)
    {
        LocalNames = localNames;
        NeighbourReferences = neighbours;
        LinkerNames = linkerNames;
        Needs = needs;
        Warnings = warnings;
    }

    /// <summary>The room's own local names as written (<c>cxry_door</c>), distinct, in ordinal order.</summary>
    public IReadOnlyList<string> LocalNames { get; }

    /// <summary>How many keys name a neighbour's entity, by the authored direction of the neighbour.</summary>
    public IReadOnlyDictionary<RoomDirection, int> NeighbourReferences { get; }

    /// <summary>The flags and hub the room names or places, as written (<c>cxry_has_east</c>, <c>cx+1ry_has_north</c>, <c>cxry_room</c>), distinct, in ordinal order.</summary>
    public IReadOnlyList<string> LinkerNames { get; }

    /// <summary>Every <c>room_needs</c> key's conditions, as a key would spell them (<c>east,!joined_west</c>), in entity order.</summary>
    public IReadOnlyList<string> Needs { get; }

    /// <summary>What the room compile warned of, each a whole sentence.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Whether the room uses no names at all.</summary>
    public bool IsEmpty => LocalNames.Count == 0 && NeighbourReferences.Count == 0 && LinkerNames.Count == 0 && Needs.Count == 0;

    /// <summary>
    /// The most edicts the linker can write because of this room, per
    /// placement, before any fold: a bare <c>logic_branch</c> for each flag
    /// it names (the classes the default table counts as edicts), and,
    /// without <c>-mod-entities</c>, a branch for each of the twelve tests
    /// and a relay for each of the eight channels of a <c>logic_room</c> it
    /// names (with the flag, the <c>logic_room</c> itself is server-only and
    /// costs no edict).
    /// </summary>
    /// <param name="modEntities">Whether the level is linked with <c>-mod-entities</c>.</param>
    /// <returns>The bound; what <c>ssmap layout</c> adds to the room's compiled edicts.</returns>
    /// <remarks>
    /// A bound, not a prediction: a flag the room places itself is written
    /// only once, a flag may fold away, and a hub rarely uses every test. A
    /// layout that never passes its budget with the bound never passes it
    /// linked, which is the direction the budget errs in.
    /// </remarks>
    public int WrittenEdictsBound(bool modEntities)
    {
        int written = 0;
        foreach (string name in LinkerNames)
        {
            RoomNameGrammar.TryParseLocal(name, out LocalName local);
            written += RoomLinkerNames.Parse(local.Rest).Kind switch
            {
                LinkerNameKind.Has or LinkerNameKind.Joined => 1,
                LinkerNameKind.Room when !modEntities => RoomDirections.Count + RoomDirections.SideCount + LogicRoom.Channels,
                _ => 0,
            };
        }

        return written;
    }

    /// <summary>A placeholder as written for an authored offset: <c>cxry_</c>, <c>cx+1ry-1_</c>.</summary>
    public static string Placeholder(int dx, int dy) =>
        "cx" + Offset(dx) + "ry" + Offset(dy) + "_";

    /// <summary>The summary of a room's turn-0 names.</summary>
    internal static RoomNameSummary Of(RoomNameTurn names)
    {
        SortedSet<string> local = new(StringComparer.Ordinal);
        SortedSet<string> owned = new(StringComparer.Ordinal);
        SortedDictionary<RoomDirection, int> neighbours = [];
        foreach (NamePairRef pair in names.Pairs)
        {
            foreach (NameSegment segment in pair.Segments)
            {
                if (segment.Literal is not null)
                {
                    continue;
                }

                string written = Placeholder(segment.Dx, segment.Dy) + segment.Rest;
                if (RoomLinkerNames.Parse(segment.Rest).Kind != LinkerNameKind.None)
                {
                    owned.Add(written);
                }
                else if (pair.IsTargetName && segment.Dx == 0 && segment.Dy == 0)
                {
                    local.Add(written);
                }

                if ((segment.Dx, segment.Dy) != (0, 0) && Direction(segment.Dx, segment.Dy) is RoomDirection direction)
                {
                    neighbours[direction] = neighbours.GetValueOrDefault(direction) + 1;
                }
            }
        }

        List<string> needs = [];
        foreach (NeedsRef key in names.Needs)
        {
            needs.Add(string.Join(',', key.Conditions.Select(c =>
                (c.Need.Negated ? "!" : string.Empty)
                + (c.Need.Joined ? RoomLinkerNames.JoinedPrefix : string.Empty)
                + RoomDirections.Name(c.Need.Direction))));
        }

        return new RoomNameSummary([.. local], neighbours, [.. owned], needs, [.. names.Warnings]);
    }

    /// <summary>The listing lines for a room: one per kind of name it uses, indented as <c>ssmap rooms</c> indents a room's details.</summary>
    /// <returns>The lines, each ending in a line feed; empty for a room that uses no names.</returns>
    public string Describe()
    {
        StringBuilder text = new();
        if (LocalNames.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"  local names: {string.Join(", ", LocalNames)}\n");
        }

        if (NeighbourReferences.Count > 0)
        {
            text.Append("  neighbour references: ")
                .Append(string.Join(", ", NeighbourReferences.Select(n => string.Create(CultureInfo.InvariantCulture, $"{RoomDirections.Name(n.Key)} ({n.Value})"))))
                .Append("; dropped with a warning where the level leaves that cell empty\n");
        }

        if (LinkerNames.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"  flags and hub: {string.Join(", ", LinkerNames)}\n");
        }

        foreach (string needs in Needs)
        {
            text.Append(CultureInfo.InvariantCulture, $"  room_needs: {needs}\n");
        }

        foreach (string warning in Warnings)
        {
            text.Append(CultureInfo.InvariantCulture, $"  warning: {warning}\n");
        }

        return text.ToString();
    }

    private static string Offset(int d) => d switch
    {
        > 0 => "+1",
        < 0 => "-1",
        _ => string.Empty,
    };

    /// <summary>The direction an authored offset names, or null for (0, 0).</summary>
    private static RoomDirection? Direction(int dx, int dy)
    {
        for (int d = 0; d < RoomDirections.Count; d++)
        {
            if (RoomDirections.Offset((RoomDirection)d) == (dx, dy))
            {
                return (RoomDirection)d;
            }
        }

        return null;
    }
}
