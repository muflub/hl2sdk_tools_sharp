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

        VmfChunk flatWorld = new(world.Name);
        foreach (VmfKey key in world.Keys)
        {
            flatWorld.AddKey(key.Name, key.Value);
        }

        flat.Chunks.Add(flatWorld);
        List<VmfChunk> entities = [];
        foreach (RoomInstance instance in layout.Rooms)
        {
            LibraryRoom room = byName[instance.Placement.Room];
            QuarterTurn turn = QuarterTurn.Of(new RoomTransform(instance.Placement, layout.CellSize));
            List<Box> opened = [.. instance.Joints.Select(j => RoomLinter.SealBox(
                room.Definition, room.Definition.Sockets.First(s => s.Name == j.Socket), room.Definition.CellSize))];

            VmfChunk roomWorld = room.Document.GetChunk(MapFileLoader.WorldChunk)!;
            foreach (VmfChunk solid in roomWorld.GetChunks(MapFileLoader.SolidChunk))
            {
                Box box = VmfPlacement.Bounds(solid);
                if (opened.Any(plug => RoomLibraryVmf.Same(box, plug)))
                {
                    continue;
                }

                flatWorld.Children.Add(VmfPlacement.MoveSolid(solid, turn));
            }

            // Points of interest are not entities of the map: the room
            // compile takes them out (RoomPois), so the reference does too.
            foreach (VmfChunk entity in room.Document.GetChunks(MapFileLoader.EntityChunk))
            {
                if (!RoomPois.IsPoi(entity))
                {
                    entities.Add(VmfPlacement.MoveEntity(entity, turn));
                }
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

        return flat;
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
