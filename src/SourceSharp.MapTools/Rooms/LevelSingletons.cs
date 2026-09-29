//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One link's or one flatten's level-wide singletons: the first copy of each
/// is kept, an equal later copy is dropped, and a different one is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entities.</b> The classes a level has one of per name
/// (<see cref="RoomLibraryEntities.IsLevelSingleton"/>): the sun, the fog,
/// tone map, shadow and post-process controllers, and
/// <c>water_lod_control</c>. The library's copies come first, in library
/// order; then every placement's, in link order. Two copies are the same
/// entity by <see cref="RoomLibraryEntities.IdentityOf"/> (the sun by class,
/// the rest by class and name) and equal by
/// <see cref="RoomLibraryEntities.Difference"/> (every key but the editor's
/// id and the position).
/// </para>
/// <para>
/// <b>Why at link as well as at pack time.</b> <c>ssmap room</c> already
/// drops a room's copy of a library entity, or refuses it (D3), so a pack
/// it wrote reaches the link with no room carrying one. Two cases are left
/// for the link. <c>water_lod_control</c> is added by vbsp itself to every
/// room compile with water, so only the compiled rooms carry it, and the
/// design's rule for it is the link's: keep the first, drop equal
/// duplicates, refuse different ones. And a pack written by a host that
/// packs rooms itself (<see cref="RoomPack.SaveAsync(IReadOnlyList{RoomPackSectionData}, IReadOnlyList{RoomPackItem}, Stream, CancellationToken)"/>)
/// never went through the split; the same rule keeps its level to one sun
/// rather than one per placement. A room's own named controller (per-room
/// fog) has a name of its own per placement once local names are resolved,
/// so it is never a duplicate; a global name placed twice is, and its
/// second, equal copy is dropped as the first already answers to that name.
/// </para>
/// <para>
/// <b>One rule for both maps.</b> The link runs it on the compiled rooms'
/// entities after they are moved and named, the flatten on the VMF entities
/// after the same naming, so both keep and drop the same copies
/// (section 5.9 of the rooms design: link and flatten must agree).
/// </para>
/// <para>
/// A new instance per link or flatten, dropped with it: nothing is kept
/// between levels.
/// </para>
/// </remarks>
internal sealed class LevelSingletons
{
    private readonly Dictionary<string, (string Against, IReadOnlyList<KeyValuePair<string, string>> Pairs)> _first = new(StringComparer.Ordinal);

    /// <summary>Starts a level with the library's entities as the first copies.</summary>
    /// <param name="library">The library-wide entities, in library order.</param>
    public LevelSingletons(IReadOnlyList<VmfChunk> library)
    {
        Library = library;
        foreach (VmfChunk entity in library)
        {
            List<KeyValuePair<string, string>> pairs = RoomLibraryEntities.PairsOf(entity);
            string classname = RoomLibraryEntities.LastValue(pairs, "classname") ?? string.Empty;
            _first.TryAdd(RoomLibraryEntities.IdentityOf(classname, RoomLibraryEntities.NameOf(pairs)), (RoomLibraryEntities.LibraryOwner, pairs));
        }
    }

    /// <summary>The library-wide entities, in library order: what the level writes once, before every room's.</summary>
    public IReadOnlyList<VmfChunk> Library { get; }

    /// <summary>The copies dropped so far: the placement each belonged to, and its class.</summary>
    public List<(int Placement, string ClassName)> Dropped { get; } = [];

    /// <summary>Whether a placement's entity stays in the level.</summary>
    /// <param name="room">The placed room's name, for messages.</param>
    /// <param name="placement">The placement's index in link order.</param>
    /// <param name="classname">The entity's class.</param>
    /// <param name="pairs">Its keys and outputs, as the level will carry them.</param>
    /// <returns>False for an equal later copy of a singleton, true otherwise.</returns>
    /// <exception cref="LinkException">A later copy that differs from the first, naming both owners and the key.</exception>
    public bool Keep(string room, int placement, string classname, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!RoomLibraryEntities.IsLevelSingleton(classname))
        {
            return true;
        }

        string identity = RoomLibraryEntities.IdentityOf(classname, RoomLibraryEntities.NameOf(pairs));
        if (!_first.TryGetValue(identity, out (string Against, IReadOnlyList<KeyValuePair<string, string>> Pairs) first))
        {
            _first[identity] = ($"room {room}'s", pairs);
            return true;
        }

        if (RoomLibraryEntities.Difference(pairs, first.Pairs) is { } difference)
        {
            throw new LinkException(RoomLibraryEntities.DiffersMessage(room, classname, first.Against, difference));
        }

        Dropped.Add((placement, classname));
        return false;
    }
}
