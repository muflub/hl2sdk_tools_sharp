//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// Works out a room's <see cref="RoomLinkData"/>, all four turns, or
    /// returns null when the room is one the link would refuse.
    /// </summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The link data, bound to <paramref name="room"/>'s compile; or null.</returns>
    /// <remarks>
    /// <para>
    /// The room compile calls this for every room it compiles, on the
    /// room's own thread, so the work runs side by side like the compiles
    /// do. It is a pure function of the room, so the result, and the pack
    /// bytes it becomes, are the same at any thread count.
    /// </para>
    /// <para>
    /// A room the link refuses (a second model, a water leaf, a pak that is
    /// not a zip, collision or entities it cannot read, ...) gets no link
    /// data rather than failing its compile: it is still packed as before,
    /// and a level that places it is refused at link time with the same
    /// message, from the same point of the link, because the link then
    /// computes on the fly and meets the same problem. Only a
    /// <see cref="LinkException"/> means that; anything else is a bug and
    /// is thrown.
    /// </para>
    /// </remarks>
    internal static async Task<RoomLinkData?> TryPrecomputeAsync(RoomObject room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        try
        {
            await ReadPakAsync(room, cancellationToken).ConfigureAwait(false);
            RoomLinkShared shared = ComputeShared(room);
            RoomLinkRotation?[] rotations = new RoomLinkRotation?[4];
            for (int rotation = 0; rotation < 4; rotation++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RoomLinkEntities entities = ComputeEntities(room, rotation);
                if (entities.Items.Any(e => e.Error is not null))
                {
                    return null;
                }

                rotations[rotation] = new RoomLinkRotation(
                    ComputeGeometry(room, rotation), ComputeCollision(room, rotation), entities);
            }

            return new RoomLinkData(
                room.Definition, room.Bsp, room.Vis, shared, rotations, RoomDoorVisibility.Compute(room, shared));
        }
        catch (Exception exception) when (exception is LinkException or InvalidBspException)
        {
            // InvalidBspException: an entity lump that does not parse, which
            // the link meets (and reports) at the same point on the fly.
            return null;
        }
    }

    /// <summary>
    /// The link data that depends on the room alone: every check the link
    /// makes of one room, then the plug census of every socket.
    /// </summary>
    /// <exception cref="LinkException">The room's compile carries something the relocation refuses.</exception>
    /// <remarks>
    /// The checks run in the order the link has always made them, and throw
    /// the same messages, because a room without stored link data is checked
    /// here at link time.
    /// </remarks>
    internal static RoomLinkShared ComputeShared(RoomObject room)
    {
        BspData bsp = room.Bsp;
        string name = room.Definition.Name;
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (bsp[i].Length != 0 && !CarriedLumps.Contains((BspLump)i))
            {
                throw new LinkException(
                    $"room {name} carries lump {(BspLump)i} ({i}), which the relocation does not understand");
            }
        }

        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        if (models.Length != 1)
        {
            throw new LinkException($"room {name} has {models.Length} models; a linkable room is one world model");
        }

        if (models[0].HeadNode != 0)
        {
            throw new LinkException($"room {name}'s world model starts at node {models[0].HeadNode}, not 0");
        }

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        foreach (DLeaf leaf in leafs)
        {
            if (leaf.LeafWaterDataId != -1)
            {
                throw new LinkException($"room {name} has a water leaf, which the relocation refuses");
            }
        }

        RefuseAreaPortals(name, bsp);
        RefuseGameLumpContent(name, bsp);
        RefuseDisplacementCollision(name, bsp);

        // The vis has to number the compile's own leaves before its rows are
        // shifted into anyone else's range.
        RoomObjectChecks.CheckVis(name, bsp, room.Vis);

        List<SocketCensus> sockets = new(room.Definition.Sockets.Count);
        foreach (RoomSocket socket in room.Definition.Sockets)
        {
            sockets.Add(CensusSocket(room, leafs, socket));
        }

        return new RoomLinkShared(sockets);
    }

    /// <summary>
    /// A room's geometry turned by one quarter turn, not yet moved to a cell.
    /// </summary>
    /// <exception cref="LinkException">The plane lump holds an odd number of planes.</exception>
    internal static RoomLinkGeometry ComputeGeometry(RoomObject room, int rotation)
    {
        BspData bsp = room.Bsp;
        (DPlane[] pairs, bool[] swapped) = RotatePlanes(BspStructView.As<DPlane>(bsp[BspLump.Planes]), rotation);

        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        Box[] nodeBoxes = new Box[nodes.Length];
        for (int n = 0; n < nodes.Length; n++)
        {
            nodeBoxes[n] = RotateBox(ToVec(nodes[n].Mins), ToVec(nodes[n].Maxs), rotation);
        }

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        Box[] leafBoxes = new Box[leafs.Length];
        for (int l = 0; l < leafs.Length; l++)
        {
            leafBoxes[l] = RotateBox(ToVec(leafs[l].Mins), ToVec(leafs[l].Maxs), rotation);
        }

        List<Box> occluders = [];
        if (bsp[BspLump.Occlusion].Length != 0)
        {
            foreach (DOccluderData occluder in OcclusionLump.Read(bsp[BspLump.Occlusion]).Occluders)
            {
                occluders.Add(RotateBox(occluder.Mins, occluder.Maxs, rotation));
            }
        }

        RoomDefinition definition = room.Definition;
        Box[] plugs = new Box[definition.Sockets.Count];
        for (int s = 0; s < plugs.Length; s++)
        {
            Box plug = RoomLinter.SealBox(definition, definition.Sockets[s], definition.CellSize);
            plugs[s] = RotateBox(plug.Mins, plug.Maxs, rotation);
        }

        DModel model = BspStructView.As<DModel>(bsp[BspLump.Models])[0];
        return new RoomLinkGeometry
        {
            Rotation = rotation,
            Vertices = RotateAll(BspStructView.As<Vec3>(bsp[BspLump.Vertexes]), rotation),
            PlanePairs = pairs,
            PlaneSwapped = swapped,
            TexInfos = RotateTexInfos(BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray(), rotation),
            NodeBoxes = nodeBoxes,
            LeafBoxes = leafBoxes,
            VertNormals = RotateAll(BspStructView.As<Vec3>(bsp[BspLump.VertNormals]), rotation),
            PrimVerts = RotateAll(BspStructView.As<Vec3>(bsp[BspLump.PrimVerts]), rotation),
            OccluderBoxes = [.. occluders],
            ModelBox = RotateBox(model.Mins, model.Maxs, rotation),
            PlugBoxes = plugs,
        };
    }

    /// <summary>
    /// A room's world collision read out of its collision lump, every leaf
    /// convex turned by one quarter turn; null when the room has no
    /// collision lump.
    /// </summary>
    /// <exception cref="LinkException">The collision lump is not one the merge understands (<see cref="ReadRoomCollide"/>).</exception>
    internal static RoomLinkCollision? ComputeCollision(RoomObject room, int rotation)
    {
        if (room.Bsp[BspLump.PhysCollide].Length == 0)
        {
            return null;
        }

        RoomCollide collide = ReadRoomCollide(room.Bsp, room.Definition.Name);
        List<RoomLinkSolid> solids = new(collide.Solids.Count);
        foreach ((int contents, byte[] blob) in collide.Solids)
        {
            List<byte[]> ledges = [];
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
            {
                RotateLedge(ledge, rotation);
                ledges.Add(ledge.Bytes);
            }

            solids.Add(RoomLinkSolid.Of(contents, ledges));
        }

        return new RoomLinkCollision(collide.Materials, collide.VirtualTerrain, solids);
    }

    /// <summary>
    /// A room's entity lump parsed, each entity turned by one quarter turn:
    /// its yaw turned and its origin turned, with the origin's translation
    /// left to the link.
    /// </summary>
    /// <remarks>
    /// A key that cannot be read is held as the entity's
    /// <see cref="RoomLinkEntity.Error"/> rather than thrown; see there.
    /// </remarks>
    internal static RoomLinkEntities ComputeEntities(RoomObject room, int rotation)
    {
        string name = room.Definition.Name;
        List<RoomLinkEntity> items = [];
        foreach (BspEntity entity in EntityLump.Parse(room.Bsp[BspLump.Entities]))
        {
            List<RoomLinkPair> pairs = new(entity.Pairs.Count);
            if (string.Equals(entity.ClassName, "worldspawn", StringComparison.Ordinal))
            {
                foreach (BspKeyValue pair in entity.Pairs)
                {
                    pairs.Add(new RoomLinkPair(pair.Key, pair.Value, default));
                }

                Box? extent = null;
                string? error = null;
                if (entity.Get(WorldMinsKey) is { } mins && entity.Get(WorldMaxsKey) is { } maxs)
                {
                    try
                    {
                        Vec3 lo = ParseVec(mins, WorldMinsKey, name);
                        Vec3 hi = ParseVec(maxs, WorldMaxsKey, name);
                        extent = RotateBox(lo, hi, rotation);
                    }
                    catch (LinkException exception)
                    {
                        error = exception.Message;
                    }
                }

                items.Add(new RoomLinkEntity(true, pairs, extent, error));
                continue;
            }

            try
            {
                items.Add(new RoomLinkEntity(false, TurnEntity(entity, rotation, name), null, null));
            }
            catch (LinkException exception)
            {
                items.Add(new RoomLinkEntity(false, [], null, exception.Message));
            }
        }

        return new RoomLinkEntities(items);
    }

    /// <summary>
    /// One key of a moved entity with its turn applied: the origin turned
    /// (and moved at link), a yaw turned by <paramref name="yawTurns"/> (the
    /// room's turn, or 0 for the sun; <see cref="TurnEntity"/> decides),
    /// anything else as written.
    /// </summary>
    private static RoomLinkPair TurnPair(BspKeyValue pair, int turns, int yawTurns, string room)
    {
        if (IsKey(pair.Key, "origin"))
        {
            return new RoomLinkPair(pair.Key, null, RoomTransform.Rotate(ParseVec(pair.Value, "origin", room), turns));
        }

        string value = pair.Value;
        if (yawTurns != 0 && IsKey(pair.Key, "angles"))
        {
            Vec3 angles = ParseVec(value, "angles", room);
            value = FormatVec(new Vec3(angles.X, TurnYaw(angles.Y, yawTurns), angles.Z));
        }
        else if (yawTurns != 0 && IsKey(pair.Key, "angle"))
        {
            float yaw = ParseFloat(value, "angle", room);
            value = yaw is -1f or -2f ? value : Format(TurnYaw(yaw, yawTurns));
        }

        return new RoomLinkPair(pair.Key, value, default);
    }

    /// <summary>
    /// The link data a placement uses: the room's stored data when it still
    /// describes the room, else null and the caller computes on the fly.
    /// </summary>
    private static RoomLinkData? StoredLink(RoomObject room) =>
        room.Link is { } link && link.IsFor(room) ? link : null;

    /// <summary>The turned geometry of a placement: stored, or computed now.</summary>
    private static RoomLinkGeometry GeometryFor(RoomObject room, int rotation) =>
        StoredLink(room)?.Rotation(rotation)?.Geometry ?? ComputeGeometry(room, rotation);

    /// <summary>The turned collision of a placement: stored, or computed now.</summary>
    private static RoomLinkCollision? CollisionFor(RoomObject room, int rotation) =>
        StoredLink(room)?.Rotation(rotation)?.Collision ?? ComputeCollision(room, rotation);

    /// <summary>The turned entities of a placement: stored, or computed now.</summary>
    private static RoomLinkEntities EntitiesFor(RoomObject room, int rotation) =>
        StoredLink(room)?.Rotation(rotation)?.Entities ?? ComputeEntities(room, rotation);

    /// <summary>
    /// The plug census of one socket: the census <c>PlanRoom</c> made for a
    /// jointed socket, made for any socket and without refusing one that
    /// faces nothing (the link refuses that only if the level joints it).
    /// </summary>
    private static SocketCensus CensusSocket(RoomObject room, ReadOnlySpan<DLeaf> leafs, RoomSocket socket)
    {
        RoomDefinition definition = room.Definition;
        BspData bsp = room.Bsp;
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);

        int[] facing = Facing(leafs, plug);

        List<int> stripped = [];
        for (int b = 0; b < brushes.Length; b++)
        {
            if (IsTriggerBrush(brushes[b], sides, texInfos)
                && BrushBox(brushes[b], sides, planes).ContainsWithin(plug, RoomLinter.CellEpsilon))
            {
                stripped.Add(b);
            }
        }

        List<int> carves = [];
        for (int l = 0; l < leafs.Length; l++)
        {
            DLeaf leaf = leafs[l];
            if ((leaf.Contents & (int)BrushContents.Solid) != 0
                && BoxOf(leaf).Overlaps(plug, RoomLinter.CellEpsilon))
            {
                carves.Add(l);
            }
        }

        HashSet<int> faces = [];
        MarkPlugFaces(bsp, plug, faces);
        return new SocketCensus(facing, [.. stripped], [.. carves], [.. faces.Order()]);
    }

    private static Vec3[] RotateAll(ReadOnlySpan<Vec3> points, int rotation)
    {
        Vec3[] turned = new Vec3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            turned[i] = RoomTransform.Rotate(points[i], rotation);
        }

        return turned;
    }

    /// <summary>
    /// The rotation half of <see cref="TransformPlanes"/>: per flip pair, the
    /// even plane's normal turned with its type, its room-local distance, and
    /// whether the pair is stored swapped.
    /// </summary>
    internal static (DPlane[] Pairs, bool[] Swapped) RotatePlanes(ReadOnlySpan<DPlane> planes, int rotation)
    {
        if (planes.Length % 2 != 0)
        {
            throw new LinkException("the planes lump holds an odd number of planes, so a flip pair is missing");
        }

        DPlane[] pairs = new DPlane[planes.Length / 2];
        bool[] swapped = new bool[planes.Length / 2];
        for (int i = 0; i < planes.Length; i += 2)
        {
            Vec3 normal = ApplyNormal(planes[i].Normal, rotation);
            PlaneType type = new Plane(normal, 0).Type;
            pairs[i / 2] = new DPlane { Normal = normal, Dist = planes[i].Dist, Type = (int)type };
            swapped[i / 2] = type switch
            {
                PlaneType.X => normal.X < 0,
                PlaneType.Y => normal.Y < 0,
                PlaneType.Z => normal.Z < 0,
                _ => false,
            };
        }

        return (pairs, swapped);
    }

    /// <summary>
    /// The translation half of <see cref="TransformPlanes"/>: each turned pair
    /// moved by the placement's translation, <c>d' = d + n'·t</c>, written as
    /// its two halves in stored order.
    /// </summary>
    internal static DPlane[] TranslatePlanes(DPlane[] pairs, bool[] swapped, Vec3 translation)
    {
        DPlane[] planes = new DPlane[pairs.Length * 2];
        for (int p = 0; p < pairs.Length; p++)
        {
            Vec3 normal = pairs[p].Normal;
            float dist = pairs[p].Dist + Vec3.Dot(normal, translation);
            DPlane even = new() { Normal = normal, Dist = dist, Type = pairs[p].Type };
            DPlane odd = new() { Normal = -normal, Dist = -dist, Type = pairs[p].Type };
            planes[2 * p] = swapped[p] ? odd : even;
            planes[(2 * p) + 1] = swapped[p] ? even : odd;
        }

        return planes;
    }

    /// <summary>The rotation half of <see cref="TransformTexInfos"/>: every axis turned, in place; the offsets are left.</summary>
    internal static TexInfo[] RotateTexInfos(TexInfo[] infos, int rotation)
    {
        for (int i = 0; i < infos.Length; i++)
        {
            for (int row = 0; row < 2; row++)
            {
                RotateAxis(ref infos[i].TextureVecsTexelsPerWorldUnits, row, rotation);
                RotateAxis(ref infos[i].LightmapVecsLuxelsPerWorldUnits, row, rotation);
            }
        }

        return infos;
    }

    /// <summary>The translation half of <see cref="TransformTexInfos"/>: each turned axis's offset takes the translation, in place.</summary>
    internal static TexInfo[] TranslateTexInfos(TexInfo[] infos, Vec3 translation)
    {
        for (int i = 0; i < infos.Length; i++)
        {
            for (int row = 0; row < 2; row++)
            {
                OffsetAxis(ref infos[i].TextureVecsTexelsPerWorldUnits, row, translation);
                OffsetAxis(ref infos[i].LightmapVecsLuxelsPerWorldUnits, row, translation);
            }
        }

        return infos;
    }

    private static void RotateAxis(ref FloatArray8 vecs, int row, int rotation)
    {
        int at = row * 4;
        Vec3 axis = ApplyNormal(new Vec3(vecs[at], vecs[at + 1], vecs[at + 2]), rotation);
        vecs[at] = axis.X;
        vecs[at + 1] = axis.Y;
        vecs[at + 2] = axis.Z;
    }

    private static void OffsetAxis(ref FloatArray8 vecs, int row, Vec3 t)
    {
        int at = row * 4;
        vecs[at + 3] -= Vec3.Dot(new Vec3(vecs[at], vecs[at + 1], vecs[at + 2]), t);
    }

    /// <summary>The rotation half of <see cref="MoveLedge"/>: a ledge's points turned in IVP's axes, in place.</summary>
    internal static void RotateLedge(IvpCompactLedge ledge, int turns)
    {
        if (turns == 0)
        {
            return;
        }

        Span<byte> bytes = ledge.Bytes;
        for (int p = 0; p < ledge.PointCount; p++)
        {
            int o = ledge.PointOffset + (16 * p);
            float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[o..]);
            float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(o + 8)..]);
            (float rx, float rz) = TurnIvp(x, z, turns);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[o..], rx);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 8)..], rz);
        }
    }

    /// <summary>
    /// The translation half of <see cref="MoveLedge"/>: a turned ledge's
    /// points moved by the placement's translation on IVP's x and z, in place.
    /// </summary>
    internal static void TranslateLedge(IvpCompactLedge ledge, RoomTransform transform)
    {
        Vec3Ivp t = IvpTranslation(transform);
        Span<byte> bytes = ledge.Bytes;
        for (int p = 0; p < ledge.PointCount; p++)
        {
            int o = ledge.PointOffset + (16 * p);
            float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[o..]);
            float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(o + 8)..]);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[o..], x + t.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 8)..], z + t.Z);
        }
    }
}
