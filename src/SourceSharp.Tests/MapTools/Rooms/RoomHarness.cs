//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room/link tests' compile harness: in-memory materials, a room built by
/// <see cref="RoomModel"/>, and the two compiles a level needs.
/// </summary>
/// <remarks>
/// Same shape as <c>UnitMap</c> — no disk, no game install, milliseconds — with
/// the two materials the room model names plus a trigger, which is what the
/// socket plugs are made of.
/// </remarks>
internal static class RoomHarness
{
    public const string Plain = "unit/plain";
    public const string Trigger = "unit/trigger";

    /// <summary>The standard kit, sized for the test cell so compiles stay small.</summary>
    public const float Cell = 256f;

    public static SocketKit Kit { get; } = new(96f, 96f, 16f);

    /// <summary>Player clip: <c>CONTENTS_PLAYERCLIP</c>, not solid.</summary>
    public const string PlayerClip = "unit/playerclip";

    public static async Task<VbspContext> ContextAsync(VbspOptions? options = null, int degree = 1, IReadOnlyDictionary<string, byte[]>? extraFiles = null)
    {
        InMemoryFileSystem files = new();
        foreach ((string path, byte[] bytes) in extraFiles ?? new Dictionary<string, byte[]>())
        {
            files.AddFile(path, bytes);
        }

        files.AddText(
            $"materials/{PlayerClip}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%playerClip\" \"1\"\n}\n");
        if (extraFiles?.ContainsKey($"materials/{Plain}.vmt") != true)
        {
            files.AddText(
                $"materials/{Plain}.vmt",
                "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        }
        files.AddText(
            $"materials/{Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        ContentFileSystem content = new([mount]);

        return new VbspContext(options ?? VbspOptions.Default, content) { MapBase = "roomtest" };
    }

    /// <summary>A room definition for the test cell, with the listed faces open.</summary>
    public static RoomDefinition Room(string name, params RoomFacing[] open)
    {
        List<RoomSocket> sockets = [];
        foreach (RoomFacing facing in open)
        {
            sockets.Add(new RoomSocket(facing, facing.ToString()));
        }

        return new RoomDefinition(name, Cell, Kit, sockets);
    }

    /// <summary>
    /// The room VMF with its plugs written as the harness's trigger material —
    /// the plug material the model names is a game path the in-memory mount
    /// cannot have, and the compile flag is what matters.
    /// </summary>
    public static VmfDocument BuildRoomModel(RoomDefinition definition, string shell = Plain, string plug = Trigger)
    {
        VmfDocument document = RoomModel.Build(definition, Kit.Depth, shell);
        foreach (VmfChunk chunk in document.Chunks)
        {
            RenumberMaterials(chunk, RoomModel.PlugMaterial, plug);
        }

        // The leak reporter walks from a player start; every room's cell center
        // is (cell/2, cell/2, cell/2), room-local.
        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "900000");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", RoomModel.Tuple(definition.CellSize / 2f, definition.CellSize / 2f, definition.CellSize / 2f + 1f));
        document.Chunks.Add(start);
        return document;
    }

    private static void RenumberMaterials(VmfChunk chunk, string from, string to)
    {
        foreach (VmfKey key in chunk.Keys)
        {
            if (string.Equals(key.Name, "material", StringComparison.Ordinal)
                && string.Equals(key.Value, from, StringComparison.Ordinal))
            {
                key.Value = to;
            }
        }

        foreach (VmfChunk child in chunk.Chunks)
        {
            RenumberMaterials(child, from, to);
        }
    }

    /// <summary>Compiles a document to a BSP with its portals.</summary>
    public static async Task<VbspResult> CompileAsync(VmfDocument document, VbspContext context)
    {
        MapFile map = await MapFileLoader.LoadAsync(context, document, CancellationToken.None);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context, CancellationToken.None);
    }

    /// <summary>The leaf clusters a compiled map's vis produced, in leaf order.</summary>
    public static List<short> Clusters(BspData bsp)
    {
        List<short> result = [];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(bsp[BspLump.Leafs]))
        {
            result.Add(leaf.Cluster);
        }

        return result;
    }

    public static async Task<VisResult> VisAsync(BspData bsp, PortalSet portals, int degree = 1)
    {
        VisContext context = new()
        {
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
        return await Vvis.ComputeAsync(bsp, portals, context, CancellationToken.None);
    }

    /// <summary>
    /// The kit a room library must use: the harness's door, but the full
    /// interior height, so it stands on the floor and a player walks through
    /// (<see cref="PlayerHull"/>). <see cref="Kit"/>'s centred 96-high door
    /// has its sill 64 units above the floor, which a library refuses.
    /// </summary>
    public static SocketKit WalkableKit { get; } = new(96f, Cell - 32f, 16f);

    /// <summary>
    /// A room on the walkable kit, with the listed faces open, its sockets
    /// named for their walls as a library names them (east, west, north,
    /// south).
    /// </summary>
    public static RoomDefinition WalkableRoom(string name, params RoomFacing[] open) =>
        new(name, Cell, WalkableKit, [.. open.Select(f => new RoomSocket(f, RoomLibraryVmf.WallName(f)))]);

    /// <summary>The gap a harness library leaves between two rooms' cells.</summary>
    public const float LibraryGap = 64f;

    /// <summary>
    /// A room library VMF of the given rooms: each room's model (<see cref="BuildRoomModel"/>)
    /// moved into its own cell along +x, <see cref="LibraryGap"/> apart, with an
    /// <c>info_room</c> at the cell's low corner stating the room's name,
    /// grid and kit.
    /// </summary>
    public static VmfDocument LibraryVmf(params RoomDefinition[] rooms)
    {
        VmfDocument library = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        library.Chunks.Add(world);
        List<VmfChunk> entities = [];
        for (int i = 0; i < rooms.Length; i++)
        {
            RoomDefinition room = rooms[i];
            Vec3 corner = new(i * (room.CellSize + LibraryGap), 0, 0);
            QuarterTurn move = QuarterTurn.Translation(corner);
            VmfDocument model = BuildRoomModel(room);
            foreach (VmfChunk solid in model.GetChunk(MapFileLoader.WorldChunk)!.GetChunks(MapFileLoader.SolidChunk))
            {
                world.Children.Add(VmfPlacement.MoveSolid(solid, move));
            }

            foreach (VmfChunk entity in model.GetChunks(MapFileLoader.EntityChunk))
            {
                entities.Add(VmfPlacement.MoveEntity(entity, move));
            }

            entities.Add(InfoRoom(room, corner));
        }

        foreach (VmfChunk entity in entities)
        {
            library.Chunks.Add(entity);
        }

        return library;
    }

    /// <summary>The <c>info_room</c> entity that marks a room of a library.</summary>
    public static VmfChunk InfoRoom(RoomDefinition room, Vec3 corner)
    {
        VmfChunk marker = new(MapFileLoader.EntityChunk);
        marker.AddKey("id", "800000");
        marker.AddKey("classname", RoomLibraryVmf.RoomEntity);
        marker.AddKey("origin", VmfPlacement.Format(corner));
        marker.AddKey(RoomLibraryVmf.NameKey, room.Name);
        marker.AddKey(RoomLibraryVmf.CellSizeKey, VmfPlacement.Format(room.CellSize));
        marker.AddKey(RoomLibraryVmf.DoorWidthKey, VmfPlacement.Format(room.Kit.Width));
        marker.AddKey(RoomLibraryVmf.DoorHeightKey, VmfPlacement.Format(room.Kit.Height));
        marker.AddKey(RoomLibraryVmf.WallDepthKey, VmfPlacement.Format(room.Kit.Depth));

        // A socket named other than its wall is named on the marker, as an
        // author would; the default name needs no key.
        foreach (RoomSocket socket in room.Sockets)
        {
            string wall = RoomLibraryVmf.WallName(socket.Facing);
            if (socket.Name != wall)
            {
                marker.AddKey(RoomLibraryVmf.SocketKeyPrefix + wall, socket.Name);
            }
        }

        return marker;
    }

    /// <summary>A level file's text for a grid of rows, north row first, each a list of cells.</summary>
    public static string LevelText(string library, params string[] rows)
    {
        int columns = rows[0].Split(',').Length;
        return $"library: {library}\nrows: {rows.Length}\ncolumns: {columns}\ngrid:\n"
            + string.Concat(rows.Select(r => $"  - [{r}]\n"));
    }

    /// <summary>The four-socket room every grid fact places.</summary>
    public static RoomDefinition Hub() =>
        Room("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    /// <summary>
    /// Compiles rooms into one library, with the managed cooker when
    /// <paramref name="cook"/> is set (what <c>ssmap room</c> does by
    /// default) and without one otherwise (<c>-cooker none</c>).
    /// </summary>
    public static async Task<RoomLibrary> LibraryAsync(bool cook, params RoomDefinition[] definitions)
    {
        VbspContext context = await ContextAsync();
        await using ManagedCollisionCooker? cooker = cook ? ManagedCollisionCooker.Create(ComplianceOptions.Correct) : null;
        context.CollisionCooker = cooker;
        RoomLibrary library = new(Kit, Cell);
        foreach (RoomDefinition definition in definitions)
        {
            library.Add(await RoomCompiler.CompileAsync(BuildRoomModel(definition), definition, context));
        }

        return library;
    }

    /// <summary>
    /// A layout whose joints are derived from the placements: every socket
    /// whose world direction (after the placement's turns) points at a
    /// neighbour holding a socket that faces back is jointed to it, and every
    /// other socket is capped.
    /// </summary>
    public static LevelLayout AutoLayout(string name, RoomLibrary library, params (string Room, int X, int Y, int Rotation)[] cells)
    {
        List<RoomInstance> rooms = [];
        foreach ((string room, int x, int y, int rotation) in cells)
        {
            RoomPlacement placement = new(room, x, y, rotation);
            RoomTransform transform = new(placement, Cell);
            List<(string, string)> joints = [];
            List<string> capped = [];
            foreach (RoomSocket socket in library.Get(room).Definition.Sockets)
            {
                (int axis, int sign) = transform.WorldNormal(socket.Facing);
                int nx = x + (axis == 0 ? sign : 0);
                int ny = y + (axis == 1 ? sign : 0);
                string? theirs = null;
                foreach ((string otherRoom, int ox, int oy, int orot) in cells)
                {
                    if (ox != nx || oy != ny)
                    {
                        continue;
                    }

                    RoomTransform other = new(new RoomPlacement(otherRoom, ox, oy, orot), Cell);
                    foreach (RoomSocket candidate in library.Get(otherRoom).Definition.Sockets)
                    {
                        if (other.WorldNormal(candidate.Facing) == (axis, -sign))
                        {
                            theirs = candidate.Name;
                        }
                    }
                }

                if (theirs is null)
                {
                    capped.Add(socket.Name);
                }
                else
                {
                    joints.Add((socket.Name, theirs));
                }
            }

            rooms.Add(new RoomInstance(placement, joints, capped));
        }

        return new LevelLayout(name, Cell, Kit, rooms);
    }

    /// <summary>A face's vertices, walked through its surfedges and edges.</summary>
    public static List<Vec3> FaceVertices(BspData bsp, DFace face)
    {
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<Vec3> vertices = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        List<Vec3> result = [];
        for (int e = 0; e < face.NumEdges; e++)
        {
            int se = surfEdges[face.FirstEdge + e];
            result.Add(vertices[se >= 0 ? edges[se].V[0] : edges[-se].V[1]]);
        }

        return result;
    }

    /// <summary>A placed room's cell, as a world box.</summary>
    public static Box CellBox(RoomPlacement placement) =>
        new(new Vec3(placement.CellX * Cell, placement.CellY * Cell, 0),
            new Vec3((placement.CellX + 1) * Cell, (placement.CellY + 1) * Cell, Cell));

    /// <summary>The centre of a socket's plug box, in world coordinates.</summary>
    public static Vec3 PlugCentre(RoomDefinition definition, RoomPlacement placement, string socket)
    {
        Box plug = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == socket), definition.CellSize);
        Vec3 centre = new((plug.Mins.X + plug.Maxs.X) / 2, (plug.Mins.Y + plug.Maxs.Y) / 2, (plug.Mins.Z + plug.Maxs.Z) / 2);
        return new RoomTransform(placement, Cell).Apply(centre);
    }

    /// <summary>The leaf the engine's walk puts a point in.</summary>
    public static DLeaf LeafAt(BspData bsp, Vec3 point) =>
        BspStructView.As<DLeaf>(bsp[BspLump.Leafs])[LevelLinker.PointInLeaf(bsp, point)];

    /// <summary>A copy of a room with some lumps of its compile replaced.</summary>
    public static RoomObject WithLumps(RoomObject room, Action<BspData> edit)
    {
        BspData bsp = new() { FileVersion = room.Bsp.FileVersion, MapRevision = room.Bsp.MapRevision };
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            bsp[i] = room.Bsp[i];
        }

        bsp.GameLumps.AddRange(room.Bsp.GameLumps);
        edit(bsp);
        return room with { Bsp = bsp, Compiled = bsp };
    }

    /// <summary>A library of the given rooms, on the harness grid.</summary>
    public static RoomLibrary Library(params RoomObject[] rooms)
    {
        RoomLibrary library = new(Kit, Cell);
        foreach (RoomObject room in rooms)
        {
            library.Add(room);
        }

        return library;
    }
}
