//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The reference for a linked level: the same level as ONE ordinary VMF,
/// for vbsp to compile whole.
/// </summary>
/// <remarks>
/// <para>
/// A linked map is checked against "a VMF of the exact same setup". This is
/// that VMF, made from the same two inputs the link is: the level file and
/// the room library. Every placed room's brushes and entities are copied out
/// of the library, moved from its library cell to its level cell and turned,
/// and written into one map. The plugs of JOINED sockets are left out, so the
/// doorways are open, as the link opens them; the plugs of capped sockets
/// are kept, so those doorways stay walls, as the link keeps them.
/// </para>
/// <para>
/// <b>Why a flattened VMF rather than the level compiled with its rooms as
/// <c>func_instance</c>s:</b> an instance cannot leave out one of its own
/// brushes, so a whole-map compile of instanced rooms would keep every plug
/// and no doorway would open. Writing the merged map out also makes the
/// reference a file anyone can open, compile with stock vbsp, and diff.
/// </para>
/// <para>
/// <b>Deterministic:</b> rooms in link order (row by row from the
/// south-west), each room's brushes and entities in library order, numbers
/// written shortest-round-trip, and every <c>id</c> renumbered from 1 in
/// document order, so the same level and library give the same bytes.
/// </para>
/// <para>
/// The rules are the link's: the level places only rooms the library has,
/// and a player must be able to reach every room
/// (<see cref="RoomLinter.CheckReachable"/>). The move is the placement's
/// <see cref="RoomTransform"/>, the one the linker relocates compiled rooms
/// by, applied to the room-local VMF the library split gave (and that
/// <c>ssmap room</c> compiles).
/// </para>
/// </remarks>
public static class LevelFlattener
{
    /// <summary>Flattens a level into one VMF.</summary>
    /// <param name="level">The level.</param>
    /// <param name="library">The room library VMF the level names.</param>
    /// <returns>The whole level as one map.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">The library cannot be split into rooms.</exception>
    /// <exception cref="LinkException">The level places a room the library does not have.</exception>
    /// <exception cref="ArgumentException">The level places no room.</exception>
    /// <exception cref="RoomLintException">A player could not reach every room.</exception>
    public static VmfDocument Flatten(LevelGrid level, VmfDocument library)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(library);

        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        Dictionary<string, LibraryRoom> byName = new(StringComparer.Ordinal);
        foreach (LibraryRoom room in rooms)
        {
            byName[room.Definition.Name] = room;
        }

        RoomDefinition first = rooms[0].Definition;
        LevelLayout layout = level.ToLayout(
            name => byName.TryGetValue(name, out LibraryRoom? room) ? room.Definition : null,
            first.CellSize,
            first.Kit);
        layout.Validate();
        RoomLinter.CheckReachable(layout, name => byName[name].Definition);

        VmfChunk world = library.GetChunk(MapFileLoader.WorldChunk)!;
        VmfDocument flat = new();
        if (library.GetChunk("versioninfo") is { } version)
        {
            flat.Chunks.Add(VmfPlacement.Clone(version));
        }

        // The library's own settings (its entity reserve) are left out, as
        // the split leaves them out of every room, so the flattened map's
        // worldspawn is the linked map's.
        VmfChunk flatWorld = new(world.Name);
        foreach (VmfKey key in world.Keys)
        {
            if (!RoomLibraryOptions.IsLibraryKey(key.Name))
            {
                flatWorld.AddKey(key.Name, key.Value);
            }
        }

        flat.Chunks.Add(flatWorld);
        List<VmfChunk> entities = [];
        List<PlacedSides> placedSides = [];
        foreach (RoomInstance instance in layout.Rooms)
        {
            LibraryRoom room = byName[instance.Placement.Room];
            QuarterTurn turn = QuarterTurn.Of(new RoomTransform(instance.Placement, layout.CellSize));
            List<Box> opened = [.. instance.Joints.Select(j => RoomLinter.SealBox(
                room.Definition, room.Definition.Sockets.First(s => s.Name == j.Socket), room.Definition.CellSize))];
            PlacedSides placed = new();
            placedSides.Add(placed);

            VmfChunk roomWorld = room.Document.GetChunk(MapFileLoader.WorldChunk)!;
            foreach (VmfChunk solid in roomWorld.GetChunks(MapFileLoader.SolidChunk))
            {
                Box box = VmfPlacement.Bounds(solid);
                if (opened.Any(plug => RoomLibraryVmf.Same(box, plug)))
                {
                    continue;
                }

                VmfChunk moved = VmfPlacement.MoveSolid(solid, turn);
                placed.AddSides(moved);
                flatWorld.Children.Add(moved);
            }

            foreach (VmfChunk entity in room.Document.GetChunks(MapFileLoader.EntityChunk))
            {
                VmfChunk moved = VmfPlacement.MoveEntity(entity, turn);
                foreach (VmfChunk solid in moved.GetChunks(MapFileLoader.SolidChunk))
                {
                    placed.AddSides(solid);
                }

                placed.Entities.Add(moved);
                entities.Add(moved);
            }
        }

        foreach (VmfChunk entity in entities)
        {
            flat.Chunks.Add(entity);
        }

        int next = 1;
        foreach (VmfChunk chunk in flat.Chunks)
        {
            Renumber(chunk, ref next);
        }

        foreach (PlacedSides placed in placedSides)
        {
            placed.RenameSideLists();
        }

        return flat;
    }

    /// <summary>The key that lists brush sides by id: <c>env_cubemap</c>, <c>info_overlay</c>, <c>info_no_dynamic_shadow</c> and the like.</summary>
    private const string SidesKey = "sides";

    /// <summary>
    /// One placement's brush sides by the id the library gave them, and its
    /// entities: what a <c>sides</c> list of that placement is rewritten
    /// through once every id has been renumbered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why per placement.</b> <c>env_cubemap</c>, <c>info_overlay</c> and
    /// <c>info_no_dynamic_shadow</c> name the sides they apply to by side
    /// id, in a space-separated <c>sides</c> list that vbsp resolves against
    /// the ids of the map it loads. <see cref="Renumber"/> gives every side
    /// a new id, so a list left as the library wrote it names the wrong
    /// sides or none. And a room placed twice repeats its library ids, so
    /// the old id alone does not say which copy is meant: an entity's list
    /// names sides of its own placement, the only ones it could name in the
    /// room it was authored in.
    /// </para>
    /// <para>
    /// <b>The side objects are the map.</b> Each moved side is recorded
    /// under its library id before the renumber; the renumber changes the
    /// id on that same object, so reading it back afterwards gives the new
    /// id without a second walk. Where a room repeats a side id (hand-built
    /// libraries can), the first side in document order wins, which is the
    /// one vbsp's own lookup finds first.
    /// </para>
    /// <para>
    /// <b>An id with no side in the placement is dropped</b>, as vbsp drops
    /// an id no side has: a joined plug's sides are left out of the flatten,
    /// and keeping their old number would let it name whichever side the
    /// renumber gave that number to. A token that is not a number is kept
    /// as written; vbsp ignores it either way.
    /// </para>
    /// </remarks>
    private sealed class PlacedSides
    {
        private readonly Dictionary<string, VmfChunk> _sides = new(StringComparer.Ordinal);

        /// <summary>The placement's moved entities, in document order.</summary>
        public List<VmfChunk> Entities { get; } = [];

        /// <summary>Records a moved brush's sides under their library ids.</summary>
        public void AddSides(VmfChunk solid)
        {
            foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
            {
                if (side.GetValue("id") is { } id)
                {
                    _sides.TryAdd(id.Trim(), side);
                }
            }
        }

        /// <summary>Rewrites every <c>sides</c> list of the placement's entities to the renumbered ids.</summary>
        public void RenameSideLists()
        {
            foreach (VmfChunk entity in Entities)
            {
                foreach (VmfKey key in entity.Keys)
                {
                    if (!string.Equals(key.Name, SidesKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    List<string> renamed = [];
                    foreach (string token in key.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        {
                            renamed.Add(token);
                        }
                        else if (_sides.TryGetValue(token, out VmfChunk? side))
                        {
                            renamed.Add(side.GetValue("id")!);
                        }
                    }

                    key.Value = string.Join(' ', renamed);
                }
            }
        }
    }

    /// <summary>Gives every chunk that has an <c>id</c> the next number, in document order.</summary>
    private static void Renumber(VmfChunk chunk, ref int next)
    {
        foreach (VmfKey key in chunk.Keys)
        {
            if (string.Equals(key.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                key.Value = (next++).ToString(CultureInfo.InvariantCulture);
                break;
            }
        }

        foreach (VmfChunk child in chunk.Chunks)
        {
            Renumber(child, ref next);
        }
    }
}
