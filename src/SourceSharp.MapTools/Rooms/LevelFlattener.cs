//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
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
    /// <summary>Flattens a level into one VMF, with stock entities.</summary>
    /// <param name="level">The level.</param>
    /// <param name="library">The room library VMF the level names.</param>
    /// <returns>The whole level as one map.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">The library cannot be split into rooms.</exception>
    /// <exception cref="LinkException">The level places a room the library does not have, or a resolved name is refused.</exception>
    /// <exception cref="ArgumentException">The level places no room.</exception>
    /// <exception cref="RoomLintException">A player could not reach every room, or a room's names break the naming rule.</exception>
    public static VmfDocument Flatten(LevelGrid level, VmfDocument library) => FlattenLevel(level, library, new LevelFlattenOptions()).Vmf;

    /// <summary>
    /// Flattens a level into one VMF, resolving the rooms' names with the
    /// one resolver the link uses, in the emission mode asked for.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="library">The room library VMF the level names.</param>
    /// <param name="options">The emission mode (<c>-mod-entities</c>).</param>
    /// <returns>The whole level as one map, and what resolving its names warned of.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">The library cannot be split into rooms.</exception>
    /// <exception cref="LinkException">The level places a room the library does not have, or a resolved name is refused.</exception>
    /// <exception cref="ArgumentException">The level places no room.</exception>
    /// <exception cref="RoomLintException">A player could not reach every room, or a room's names break the naming rule.</exception>
    /// <remarks>
    /// <para>
    /// <b>Names.</b> The placed rooms' entities go through the resolver the
    /// link runs (<c>LevelEntityResolver</c>), on their VMF keys and
    /// <c>connections</c>, before the VMF is written: local names resolved,
    /// <c>room_needs</c> applied (an entity it drops is left out of the VMF
    /// with its brushes, so vbsp never builds it), the flags and the
    /// <c>logic_room</c> or its fallback written, the logic folded. vbsp
    /// then compiles the same entities the link writes. An entity the
    /// resolver writes has no <c>id</c> and so no <c>hammerid</c>, as in the
    /// linked map.
    /// </para>
    /// <para>
    /// <b>Transitions.</b> A level with transitions (<see cref="LevelTransitions"/>,
    /// or a placed room with a role) is held to the link's level rule and
    /// gets the link's transition entities and spawn, from the rooms'
    /// transition data read from the library by the function the pack uses
    /// (<c>RoomTransit</c>); a transition volume the level drops is left out
    /// with its brushes.
    /// </para>
    /// <para>
    /// A level whose rooms use no names, flattened without
    /// <c>-mod-entities</c> and without transitions, is written exactly as
    /// before names existed.
    /// </para>
    /// </remarks>
    public static FlattenedLevel FlattenLevel(LevelGrid level, VmfDocument library, LevelFlattenOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        return FlattenLevel(level, [library], options);
    }

    /// <summary>
    /// Flattens a level into one VMF from each of the library VMFs it
    /// names: the one of a <c>library:</c> level, or those of a
    /// <c>libraries:</c> level in its order (the rooms design, 17.2).
    /// </summary>
    /// <param name="level">The level, as read (its cells are resolved here, <see cref="LevelLibraries.Resolve"/>).</param>
    /// <param name="libraries">The library VMFs, in the level's order.</param>
    /// <param name="options">The emission mode (<c>-mod-entities</c>).</param>
    /// <returns>The whole level as one map, and the warnings the link gives for it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLibraryException">A library cannot be split into rooms.</exception>
    /// <exception cref="LevelFileException">A cell or alias does not resolve (17.2's rules).</exception>
    /// <exception cref="LinkException">
    /// As for the overload for one library, and libraries the level places
    /// rooms of that are not compatible (17.5).
    /// </exception>
    /// <exception cref="ArgumentException">The level places no room, or the number of libraries is not the level's.</exception>
    /// <exception cref="RoomLintException">As for the overload for one library.</exception>
    /// <remarks>
    /// <para>
    /// A level of several libraries is flattened as the link links it: every
    /// room under its qualified name, the rooms' names read with their own
    /// library's name keys, and the level's singletons (its library
    /// entities, options and skybox: the first library's, each it lacks
    /// entirely taken from the earliest library that has it, D29). The worldspawn is the first placed
    /// room's library's, the one the link takes, with the first library's
    /// save counter. The warnings of the compatibility check and the
    /// singleton rule (<see cref="LevelLibraries.Check"/>) come first, as the
    /// link prints them; the link's line about a sunlit room baked under
    /// another sun is its alone, since nothing is baked here.
    /// </para>
    /// <para>
    /// A <c>library:</c> level is flattened exactly as before; only its
    /// aliases, when it has any, are replaced by the rooms they name.
    /// </para>
    /// </remarks>
    public static FlattenedLevel FlattenLevel(LevelGrid level, IReadOnlyList<VmfDocument> libraries, LevelFlattenOptions options)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(options);
        int count = level.Libraries?.Count ?? 1;
        if (libraries.Count != count)
        {
            throw new ArgumentException($"the level names {count} librar{(count == 1 ? "y" : "ies")}; {libraries.Count} VMFs were given", nameof(libraries));
        }

        RoomLibrarySplit[] splits = [.. libraries.Select(RoomLibraryVmf.SplitLibrary)];
        IReadOnlyList<string>[] names = [.. splits.Select(s => (IReadOnlyList<string>)[
            .. s.Rooms.Select(r => r.Definition.Name), .. s.Skybox is { } sky ? [sky.Definition.Name] : Array.Empty<string>()])];
        if (level.Libraries is not null || level.Aliases.Count > 0)
        {
            level = LevelLibraries.Resolve(level, names);
        }

        // Every room by the name the level places it by: its own for a
        // library: level, its qualified name for a level of several.
        string KeyOf(int source, string room) => level.Libraries is { } keys ? LevelLibraries.Qualified(keys[source].Key, room) : room;
        Dictionary<string, LibraryRoom> byName = new(StringComparer.Ordinal);
        Dictionary<string, int> sourceOf = new(StringComparer.Ordinal);
        for (int s = 0; s < splits.Length; s++)
        {
            foreach (LibraryRoom room in splits[s].Rooms)
            {
                byName[KeyOf(s, room.Definition.Name)] = room;
                sourceOf[KeyOf(s, room.Definition.Name)] = s;
            }
        }

        // Every library's skybox is the link's to place (below the grid),
        // never the level's, with the link's refusal; the first library's
        // is the level's.
        for (int s = 0; s < splits.Length; s++)
        {
            if (splits[s].Skybox is { } skyboxRoom
                && level.Placed.FirstOrDefault(p => p.Cell.Room == KeyOf(s, skyboxRoom.Definition.Name)) is { Cell: not null } placesSkybox)
            {
                throw new LinkException(
                    $"level {level.Name} places the skybox room {placesSkybox.Cell.Room} at cell ({placesSkybox.X}, {placesSkybox.Y});"
                    + " the link places the skybox below the grid itself.");
            }
        }

        // The compatibility check and the singleton rule, on what the VMFs
        // say, before anything is laid out (the link's refusals and lines).
        // The level's singletons are the link's (LevelLibraries.Singletons):
        // the first library's, each gap filled from the earliest later
        // library that has it (D29); a library: level's are its library's.
        List<string> libraryWarnings = [];
        int worldSource = 0;
        IReadOnlyList<VmfChunk> libraryEntities = splits[0].LibraryEntities;
        RoomLibraryOptions libraryOptions = RoomLibraryOptions.FromWorld(libraries[0].GetChunk(MapFileLoader.WorldChunk)!);
        int? skyboxSource = splits[0].Skybox is null ? null : 0;
        if (level.Libraries is { } listed)
        {
            RoomDefinition?[] firstPlaced = new RoomDefinition?[splits.Length];
            foreach ((_, _, LevelCell cell) in level.Placed)
            {
                firstPlaced[sourceOf[cell.Room]] ??= byName[cell.Room].Definition;
            }

            List<LevelLibraries.LibraryFacts> facts = [.. listed.Select((l, s) => LevelLibraries.FactsOf(l, libraries[s], splits[s], firstPlaced[s]))];
            libraryWarnings = LevelLibraries.Check(facts);
            LevelLibraries.LevelSingletonChoice singletonChoice = LevelLibraries.Singletons(facts);
            (libraryEntities, libraryOptions, skyboxSource) = (singletonChoice.Entities, singletonChoice.Options, singletonChoice.SkyboxSource);
            worldSource = Math.Max(0, Array.FindIndex(firstPlaced, d => d is not null));
        }

        VmfDocument library = libraries[0];
        LibraryRoom? levelSkybox = skyboxSource is int skyboxLibrary ? splits[skyboxLibrary].Skybox : null;
        RoomDefinition first = level.Placed.Select(p => byName.GetValueOrDefault(p.Cell.Room)?.Definition).FirstOrDefault(d => d is not null)
            ?? splits[worldSource].Rooms[0].Definition;
        string? skyboxKey = levelSkybox is null ? null : KeyOf(skyboxSource!.Value, levelSkybox.Definition.Name);
        LevelLayout layout = level.ToLayout(
            name => byName.TryGetValue(name, out LibraryRoom? room) ? room.Definition : null,
            first.CellSize,
            first.Kit);
        layout.Validate();

        RoomLinter.CheckReachable(layout, name => byName[name].Definition);

        // Water through a door, by the link's rule, from the libraries'
        // declarations (the link reads the same levels from the rooms'
        // compiles, which the room compile holds to the declarations).
        float? WaterAt(int placement, string socket) =>
            byName[layout.Rooms[placement].Placement.Room].WaterSockets.TryGetValue(socket, out RoomWaterSocket? water) ? water.Level : null;
        LevelWaterJoints.Check(layout, name => byName[name].Definition, WaterAt);

        // The level's transitions and spawn, by the link's rule, from the
        // same transition data the pack stores (read here from the library,
        // by the same function, once per room).
        Dictionary<string, RoomTransit?> transitOf = new(StringComparer.Ordinal);
        List<RoomTransit?> transits = [];
        foreach (RoomInstance instance in layout.Rooms)
        {
            LibraryRoom room = byName[instance.Placement.Room];
            if (!transitOf.TryGetValue(instance.Placement.Room, out RoomTransit? transit))
            {
                transitOf[instance.Placement.Room] = transit = RoomTransit.FromVmf(room.Definition, room.Role, room.Document);
            }

            transits.Add(transit);
        }

        LevelTransitionPlan? transitions = LevelTransitionPlan.Make(layout, transits, name => byName[name].Definition, options.ModEntities);

        VmfChunk world = libraries[worldSource].GetChunk(MapFileLoader.WorldChunk)!;
        string? mapVersion = worldSource == 0 ? null : RoomLibraryOptions.FromWorld(library.GetChunk(MapFileLoader.WorldChunk)!).MapVersion;
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
                // The level's save counter is the first library's, as the
                // link writes it, whichever library's worldspawn it is.
                flatWorld.AddKey(
                    key.Name,
                    mapVersion is not null && string.Equals(key.Name, RoomLibraryOptions.MapVersionKey, StringComparison.OrdinalIgnoreCase) ? mapVersion : key.Value);
            }
        }

        flat.Chunks.Add(flatWorld);

        // The library's own entities once, straight after the worldspawn and
        // never turned, as the link writes them (RoomLibraryEntities.ForFlatten).
        foreach (VmfChunk entity in libraryEntities)
        {
            flat.Chunks.Add(RoomLibraryEntities.ForFlatten(entity));
        }

        List<(LevelEntity Entity, string Room)> entities = [];
        List<PlacedSides> placedSides = [];
        RoomLibraryOptions[] sourceOptions = [.. libraries.Select(l => RoomLibraryOptions.FromWorld(l.GetChunk(MapFileLoader.WorldChunk)!))];
        List<ResolverRoom> resolverRooms = [];
        Dictionary<string, RoomNameTurn[]> roomNames = new(StringComparer.Ordinal);
        bool resolving = options.ModEntities || transitions is not null;

        // Socket furniture (static props and brush entities with
        // room_socket), by placement:
        // the link's rule decides which side of a joint keeps its pieces and
        // drops them at a cap (SocketFurniture), so both maps hold the same.
        List<Dictionary<string, int>> furniture = [.. layout.Rooms.Select(i => Furniture(byName[i.Placement.Room]))];
        int? FurnitureOf(int placement, string socket) =>
            furniture[placement].TryGetValue(socket, out int priority) ? priority : null;
        HashSet<VmfChunk> droppedFurniture = new(ReferenceEqualityComparer.Instance);

        foreach (RoomInstance instance in layout.Rooms)
        {
            LibraryRoom room = byName[instance.Placement.Room];
            int placementIndex = placedSides.Count;
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

            // A joined water socket's doorway is filled with its water where
            // its plug was (DoorwayWater), as the link carves it.
            foreach ((string socket, _) in instance.Joints)
            {
                if (room.WaterSockets.TryGetValue(socket, out RoomWaterSocket? water))
                {
                    RoomSocket found = room.Definition.Sockets.First(s => s.Name == socket);
                    flatWorld.Children.Add(DoorwayWater(RoomLinter.SealBox(room.Definition, found, room.Definition.CellSize), water, turn));
                }
            }

            // Points of interest are not entities of the map: the room
            // compile takes them out (RoomPois), so the reference does too.
            List<LevelEntity> roomEntities = [];
            foreach (VmfChunk entity in room.Document.GetChunks(MapFileLoader.EntityChunk))
            {
                if (RoomPois.IsPoi(entity))
                {
                    continue;
                }

                VmfChunk moved = VmfPlacement.MoveEntity(entity, turn);

                // Furniture the level does not keep stays in the resolver's
                // list, so every placement of a room lists the same entities
                // (its names are read once per room), and is left out of the
                // VMF where the entities are written.
                if (FurnitureSocket(entity, room.Definition) is { } socket
                    && !SocketFurniture.Keeps(layout, i => byName[layout.Rooms[i].Placement.Room].Definition, placementIndex, socket.Socket, FurnitureOf))
                {
                    droppedFurniture.Add(moved);
                }

                foreach (VmfChunk solid in moved.GetChunks(MapFileLoader.SolidChunk))
                {
                    placed.AddSides(solid);
                }

                placed.Entities.Add(moved);
                LevelEntity read = LevelEntity.FromVmf(moved, resolverRooms.Count, roomEntities.Count);
                entities.Add((read, instance.Placement.Room));
                roomEntities.Add(read);
            }

            // The room's names, read once per room from its entity list (a
            // placement's list differs from another's only in positions),
            // with its own library's name keys; known by the name the level
            // places it by, as the link knows it.
            string name = instance.Placement.Room;
            if (!roomNames.TryGetValue(name, out RoomNameTurn[]? turns))
            {
                roomNames[name] = turns = RoomNameAnalysis.Analyse(room.Definition.Name, roomEntities, sourceOptions[sourceOf.GetValueOrDefault(name)].NameKeySet);
            }

            RoomNameTurn named = turns[instance.Placement.NormalizedRotation];
            resolving |= !named.IsEmpty;
            RoomTransform transform = new(instance.Placement, layout.CellSize);
            resolverRooms.Add(new ResolverRoom
            {
                Room = name,
                Column = instance.Placement.CellX,
                Row = instance.Placement.CellY,
                Turns = instance.Placement.NormalizedRotation,
                Names = named,
                Entities = roomEntities,
                Joined = LevelLinker.JoinedSides(room.Definition, instance),
                CellCentre = LevelLinker.CellCentre(transform, layout.CellSize),
            });
        }

        // The door portals are planned from the rooms' entities as the room
        // compiles read them, before writing strips the furniture keys.
        IReadOnlyList<LevelDoorPortal> doors = libraryOptions.HasDoorPortals
            ? LevelDoorPortals.Plan(
                layout,
                name => byName[name].Definition,
                FurnitureOf,
                (p, socket) => LevelDoorPortals.DoorName(
                    placedSides[p].Entities.Select(e => (Func<string, string?>)e.GetValue), socket, layout.Rooms[p].Placement))
            : [];

        // The library's skybox below the grid (LevelLinker.SkyboxOf): its
        // brushes after every room's, its entities kept aside and written
        // after every room's, as the link writes them; never turned, never
        // resolved (it stands in no cell of the grid).
        List<VmfChunk> skyboxEntities = [];
        if (levelSkybox is { } skybox)
        {
            QuarterTurn below = QuarterTurn.Of(new RoomTransform(LevelLinker.SkyboxPlacement(layout, skyboxKey!), layout.CellSize));
            PlacedSides placed = new();
            foreach (VmfChunk solid in skybox.Document.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk))
            {
                VmfChunk moved = VmfPlacement.MoveSolid(solid, below);
                placed.AddSides(moved);
                flatWorld.Children.Add(moved);
            }

            foreach (VmfChunk entity in skybox.Document.GetChunks(MapFileLoader.EntityChunk))
            {
                if (RoomPois.IsPoi(entity))
                {
                    continue;
                }

                VmfChunk moved = VmfPlacement.MoveEntity(entity, below);
                foreach (VmfChunk solid in moved.GetChunks(MapFileLoader.SolidChunk))
                {
                    placed.AddSides(solid);
                }

                placed.Entities.Add(moved);
                skyboxEntities.Add(moved);
            }

            placedSides.Add(placed);
        }

        LevelResolution? resolution = null;
        if (resolving)
        {
            resolution = LevelEntityResolver.Resolve(
                resolverRooms,
                new LevelNamingOptions(options.ModEntities, libraryOptions.Folds, level.Columns, level.Rows, transitions));
            entities = [.. resolution.Entities.Select(e => (e, resolverRooms[e.Placement].Room))];
            foreach ((string key, string value) in resolution.WorldKeys)
            {
                flatWorld.AddKey(key, value);
            }
        }

        // One of each level-wide singleton, by the link's rule and after the
        // same naming (LevelSingletons), so both maps keep the same copies.
        LevelSingletons singletons = new(libraryEntities);
        foreach ((LevelEntity entity, string room) in entities)
        {
            if (entity.Payload is VmfChunk payload && droppedFurniture.Contains(payload))
            {
                continue;
            }

            if (singletons.Keep(room, entity.Placement, entity.ClassName, [.. entity.Pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value ?? string.Empty))]))
            {
                flat.Chunks.Add(WithoutFurnitureKeys(resolution is null ? (VmfChunk)entity.Payload! : Write(entity)));
            }
        }

        foreach (VmfChunk entity in skyboxEntities)
        {
            if (singletons.Keep(
                skyboxKey!,
                layout.Rooms.Count,
                entity.GetValue("classname") ?? string.Empty,
                [.. entity.Keys.Select(k => new KeyValuePair<string, string>(k.Name, k.Value))]))
            {
                flat.Chunks.Add(entity);
            }
        }

        // The library's door portals, one per joint, after every other
        // entity and in the link's order, each with its brush astride the
        // cell face (LevelDoorPortals), so vbsp numbers them as the link does.
        foreach (LevelDoorPortal door in doors)
        {
            flat.Chunks.Add(LevelDoorPortals.FlatEntity(door, layout, name => byName[name].Definition));
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

        return new FlattenedLevel(flat)
        {
            Warnings = [.. libraryWarnings, .. resolution?.Warnings ?? []],
            Notes = resolution?.Verbose ?? [],
        };
    }

    /// <summary>
    /// The water brush that fills a joined water socket's doorway in the
    /// flattened level: the socket's plug box up to the water's level (the
    /// whole box when the doorway is under water), every side the declared
    /// material, moved with the room.
    /// </summary>
    /// <remarks>
    /// The link carves the same box out of the plug's solid leaves and makes
    /// its lower part water of the facing room's record, with a surface and a
    /// fluid (the rooms design, 4.6); in the flattened level vbsp makes them
    /// from this brush, which meets the room's own water through the plug's
    /// inner face and the other room's doorway brush at the cell face. The
    /// sides facing the jambs, the sill and the waters meet solid or water
    /// and draw nothing; the top is the doorway's surface.
    /// </remarks>
    private static VmfChunk DoorwayWater(Box plug, RoomWaterSocket water, QuarterTurn turn)
    {
        Vec3 top = new(plug.Maxs.X, plug.Maxs.Y, Math.Min(plug.Maxs.Z, water.Level));
        Vec3 a = turn.Apply(plug.Mins), b = turn.Apply(top);
        Vec3 mins = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        Vec3 maxs = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        return RoomModel.Slab(water.Material, mins, maxs, 1);
    }

    /// <summary>
    /// A room's socket furniture: per socket its pieces name, the highest
    /// <c>socket_priority</c> among them (<see cref="SocketFurniture"/>).
    /// </summary>
    private static Dictionary<string, int> Furniture(LibraryRoom room)
    {
        Dictionary<string, int> sockets = new(StringComparer.Ordinal);
        foreach (VmfChunk entity in room.Document.GetChunks(MapFileLoader.EntityChunk))
        {
            if (FurnitureSocket(entity, room.Definition) is { } piece)
            {
                sockets[piece.Socket] = sockets.TryGetValue(piece.Socket, out int held) ? Math.Max(held, piece.Priority) : piece.Priority;
            }
        }

        return sockets;
    }

    /// <summary>
    /// The socket a static prop or a brush entity is furniture of, and its
    /// priority, when it names one of its room's sockets in
    /// <c>room_socket</c> (as the room compile reads it,
    /// <see cref="RoomStaticProps"/>, <see cref="RoomBrushModels"/>); else
    /// null. A key naming no socket, which <c>ssmap room</c> refuses, is not
    /// furniture here: the flatten reads the library, not the rooms' verdicts.
    /// </summary>
    private static (string Socket, int Priority)? FurnitureSocket(VmfChunk entity, RoomDefinition definition)
    {
        string? className = entity.GetValue("classname");
        if (!string.Equals(className, "prop_static", StringComparison.Ordinal)
            && !string.Equals(className, "static_prop", StringComparison.Ordinal)
            && !IsModelEntity(entity))
        {
            return null;
        }

        string? socket = entity.GetValue(RoomStaticProps.SocketKey);
        if (socket is null || !definition.Sockets.Any(s => string.Equals(s.Name, socket, StringComparison.Ordinal)))
        {
            return null;
        }

        return (socket, int.TryParse(entity.GetValue(RoomStaticProps.PriorityKey)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority) ? priority : 0);
    }

    /// <summary>
    /// Whether a VMF entity becomes a brush model of its own when compiled:
    /// it carries brushes and its class is not one vbsp consumes.
    /// </summary>
    private static bool IsModelEntity(VmfChunk entity) =>
        BrushEntityDirections.IsBrushEntity(entity) && !BrushEntityDirections.IsConsumed(entity.GetValue("classname"));

    /// <summary>
    /// A brush entity without its socket furniture keys (<c>room_socket</c>,
    /// <c>socket_priority</c>), which only the link reads: the link leaves
    /// them out of its entity lump (the rooms design, 6.4), so the
    /// flattened compile must not carry them either. Any other entity is
    /// returned as it is.
    /// </summary>
    private static VmfChunk WithoutFurnitureKeys(VmfChunk entity)
    {
        if (IsModelEntity(entity))
        {
            for (int i = entity.Children.Count - 1; i >= 0; i--)
            {
                if (entity.Children[i] is VmfKey key
                    && (string.Equals(key.Name, RoomStaticProps.SocketKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key.Name, RoomStaticProps.PriorityKey, StringComparison.OrdinalIgnoreCase)))
                {
                    entity.Children.RemoveAt(i);
                }
            }
        }

        return entity;
    }

    /// <summary>
    /// A resolved entity as a VMF entity: the room's own chunk with its keys
    /// and <c>connections</c> rewritten (its brushes and editor data kept),
    /// or a new chunk for one the resolver wrote. Outputs go into
    /// <c>connections</c> in order; every other key is written in reverse,
    /// because vbsp puts each key it reads at the front of the compiled
    /// entity (<see cref="LevelEntity.FromVmf"/>), so the compiled entity
    /// holds its keys in the order the resolver left them, as the link's does.
    /// </summary>
    private static VmfChunk Write(LevelEntity entity)
    {
        VmfChunk chunk = entity.Payload as VmfChunk ?? new VmfChunk(MapFileLoader.EntityChunk);
        List<VmfNode> kept = [.. chunk.Children.Where(n => n is VmfChunk c && !string.Equals(c.Name, MapFileLoader.ConnectionsChunk, StringComparison.OrdinalIgnoreCase))];
        chunk.Children.Clear();
        VmfChunk? connections = null;
        foreach (LevelPair pair in entity.Pairs)
        {
            if (RoomOutput.TryParse(pair.Value, out _))
            {
                connections ??= new VmfChunk(MapFileLoader.ConnectionsChunk);
                connections.AddKey(pair.Key, pair.Value!);
            }
            else
            {
                chunk.Children.Insert(0, new VmfKey(pair.Key, pair.Value!));
            }
        }

        if (connections is not null)
        {
            chunk.Children.Add(connections);
        }

        foreach (VmfNode node in kept)
        {
            chunk.Children.Add(node);
        }

        return chunk;
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

        /// <summary>
        /// Rewrites every <c>sides</c> list of the placement's entities to
        /// the renumbered ids: an entity's own, and each water overlay's
        /// inside its <c>overlaytransition</c> chunks.
        /// </summary>
        public void RenameSideLists()
        {
            foreach (VmfChunk entity in Entities)
            {
                Rename(entity);
                foreach (VmfChunk transition in entity.GetChunks(MapFileLoader.OverlayTransitionChunk))
                {
                    foreach (VmfChunk data in transition.GetChunks(MapFileLoader.OverlayDataChunk))
                    {
                        Rename(data);
                    }
                }
            }
        }

        private void Rename(VmfChunk chunk)
        {
            foreach (VmfKey key in chunk.Keys)
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

/// <summary>What a flatten is asked to do beyond flattening.</summary>
/// <remarks>A value, like every option of the libraries: nothing is kept between flattens.</remarks>
public sealed record LevelFlattenOptions
{
    /// <summary>
    /// Emit the Source Sharp mod's entity classes rather than their stock
    /// fallbacks (<c>ssmap link --flatten -mod-entities</c>), exactly as the
    /// link does with <see cref="LevelLinkOptions.ModEntities"/>.
    /// </summary>
    public bool ModEntities { get; init; }
}

/// <summary>A flattened level, and what resolving its names warned of.</summary>
/// <param name="Vmf">The whole level as one map.</param>
public sealed record FlattenedLevel(VmfDocument Vmf)
{
    /// <summary>The warnings, as the link gives them (<see cref="LinkedLevel.NameWarnings"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What only verbose output reports (<see cref="LinkedLevel.NameNotes"/>).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
