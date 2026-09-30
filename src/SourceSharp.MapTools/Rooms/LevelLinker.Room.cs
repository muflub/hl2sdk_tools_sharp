//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// Plans one placement: the room's checked, turned data (stored with the
    /// room, or computed now) moved to the placement's cell. Pure: reads only
    /// this room, writes only the returned plan, so any number of rooms plan
    /// concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What depends on the room alone (the checks, the plug census) and on
    /// the room and its turn (the turned geometry) is
    /// <see cref="RoomLinkData"/>, which the room compile stores in the pack;
    /// a room without it (an older pack, a room built in memory) gets it
    /// computed here, per placement, as the link always did, so the checks
    /// throw the same messages in the same order. What is left is the
    /// placement's: the translation of the vertices, the plane distances and
    /// the texture offsets, and which sockets the level joints.
    /// </para>
    /// </remarks>
    /// <param name="placement">The placement.</param>
    /// <param name="linkedModels">Per brush model of the room, its linked index or -1 (<see cref="PlanModels"/>); null for a room with none.</param>
    private static RoomPlan PlanRoom(ResolvedPlacement placement, int[]? linkedModels)
    {
        RoomObject room = placement.Room;
        BspData bsp = room.Bsp;
        RoomTransform transform = new(placement.Instance.Placement, room.Definition.CellSize);
        int rotation = placement.Instance.Placement.NormalizedRotation;
        RoomLinkData? stored = StoredLink(room);

        // A lit room links its bake over its compile: the faces' styles and
        // lightmap offsets of the placement's stored turn, vrad's vertex
        // normals (turned like every direction) and the map flags. The
        // checks and the census below read only what the bake leaves as
        // compiled.
        RoomLighting? lighting = room.LightingOfCompile;
        RoomLightingPayload? payload = lighting?.For(rotation);
        if (lighting is not null)
        {
            bsp = LitOverlay(bsp, lighting, payload!);
        }

        RoomLinkShared shared = stored?.Shared ?? ComputeShared(room);
        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        byte[][] ownRows = new byte[room.Vis.ClusterCount][];
        for (int c = 0; c < ownRows.Length; c++)
        {
            ownRows[c] = room.Vis.Pvs(c).ToArray();
        }

        RoomAreaPortals? areaPortals = RoomAreaPortalsOf(room);
        RoomLinkGeometry geometry = GeometryFor(room, rotation);
        RoomModelLayout? models = LayoutModels(room, rotation, linkedModels, geometry.Vertices);

        // Only the placement's translation is left to do to the geometry.
        Vec3[] vertices = new Vec3[geometry.Vertices.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            vertices[v] = transform.Translate(geometry.Vertices[v]);
        }

        Vec3 translation = transform.Apply(Vec3.Zero);
        DPlane[] planes = TranslatePlanes(geometry.PlanePairs, geometry.PlaneSwapped, translation);
        TexInfo[] texInfos = TranslateTexInfos((TexInfo[])geometry.TexInfos.Clone(), translation);

        RoomPlan plan = new()
        {
            Placement = placement,
            Bsp = bsp,
            Transform = transform,
            Geometry = geometry,
            OwnRows = ownRows,
            ClusterCount = room.Vis.ClusterCount,
            Vertices = vertices,
            TransformedPlanes = planes,
            PlaneSwapped = geometry.PlaneSwapped,
            TexInfos = texInfos,
            Leafs = leafs,
            EdgeCount = BspStructView.Count<DEdge>(bsp[BspLump.Edges]),
            FaceCount = BspStructView.Count<DFace>(bsp[BspLump.Faces]),
            LeafFaceCount = BspStructView.Count<ushort>(bsp[BspLump.LeafFaces]),
            SurfEdgeCount = BspStructView.Count<int>(bsp[BspLump.SurfEdges]),
            OrigFaceCount = BspStructView.Count<DFace>(bsp[BspLump.OriginalFaces]),
            VertNormalCount = BspStructView.Count<Vec3>(bsp[BspLump.VertNormals]),
            VertNormalIndexCount = BspStructView.Count<ushort>(bsp[BspLump.VertNormalIndices]),
            PrimCount = BspStructView.Count<DPrimitive>(bsp[BspLump.Primitives]),
            PrimIndexCount = BspStructView.Count<ushort>(bsp[BspLump.PrimIndices]),
            PrimVertCount = BspStructView.Count<Vec3>(bsp[BspLump.PrimVerts]),
            Occlusion = bsp[BspLump.Occlusion].Length == 0 ? null : OcclusionLump.Read(bsp[BspLump.Occlusion]),
            FacesVersion = bsp[BspLump.Faces].Version,
            LeafsVersion = bsp[BspLump.Leafs].Version,
            LightingLength = bsp[BspLump.Lighting].Length,
            DoorVisibility = stored?.Doors ?? RoomDoorVisibility.Compute(room, shared),
            Models = models,
            Lighting = lighting,
            Lit = payload,
            VertNormals = lighting is null ? geometry.VertNormals : [.. lighting.VertNormals.Select(n => TurnDirection(n, rotation))],
            FaceVertexStarts = lighting is null ? null : FaceVertexStarts(bsp),
            Overlays = RoomOverlaysOf(room),
            Water = RoomWaterOf(room),
            AreaPortals = areaPortals,
            AreaLumps = areaPortals is null ? null : RoomAreaPortals.Lumps(room.Definition.Name, bsp),
        };

        ApplyCensus(plan, shared);
        return plan;
    }

    /// <summary>Per face, where its vertex-normal run starts, and the total after the last face.</summary>
    private static int[] FaceVertexStarts(BspData bsp)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        int[] starts = new int[faces.Length + 1];
        for (int f = 0; f < faces.Length; f++)
        {
            starts[f + 1] = starts[f] + faces[f].NumEdges;
        }

        return starts;
    }

    /// <summary>
    /// Refuses detail props (and any other game-lump content but the static
    /// props the room's compile described): the game lumps are carried only
    /// when every room's are all zeros, except the static prop lump, which
    /// the link rebuilds for the level (<see cref="WritePropsAsync"/>).
    /// </summary>
    /// <remarks>
    /// vbsp always writes the static-prop and detail-prop game lumps, even
    /// empty; empty is every count zero, which for these formats is every byte
    /// zero. Relocating detail props would mean moving their origins and
    /// angles, merging their dictionaries and renumbering their leaves, which
    /// the link does not do yet, so a room with any is refused by name rather
    /// than having them silently dropped. Static props are carried when the
    /// room's compile left their data with the room
    /// (<see cref="RoomObject.Props"/>): the models' hulls and the keys vbsp
    /// consumed are not in the lump, so a room without it is refused
    /// (<see cref="RefuseUndescribedProps"/>).
    /// </remarks>
    private static void RefuseGameLumpContent(RoomObject room)
    {
        string name = room.Definition.Name;
        int staticProps = GameLumpId.MakeId(GameLumpId.StaticProps);
        foreach (GameLumpEntry entry in room.Bsp.GameLumps)
        {
            if (entry.Id == staticProps)
            {
                if (room.StaticProps is null)
                {
                    RefuseUndescribedProps(room);
                }

                continue;
            }

            if (entry.Data.Span.ContainsAnyExcept((byte)0))
            {
                throw new LinkException(
                    $"room {name} has content in game lump '{entry.IdString()}' (static or detail props);"
                    + " the relocation carries only empty game lumps");
            }
        }
    }

    /// <summary>Refuses displacement collision (displacements themselves are refused by lump).</summary>
    private static void RefuseDisplacementCollision(string name, BspData bsp)
    {
        ReadOnlySpan<byte> disp = bsp[BspLump.PhysDisp].Data.Span;
        if (disp.Length != 0 && (disp.Length != 2 || BinaryPrimitives.ReadUInt16LittleEndian(disp) != 0))
        {
            throw new LinkException($"room {name} carries displacement collision, which the relocation refuses");
        }
    }

    /// <summary>
    /// Reads a room's pak file: the archive, or null when the lump is empty;
    /// a pak that is not a zip is refused, since the link could neither carry
    /// its files nor tell whether it holds any.
    /// </summary>
    /// <remarks>
    /// The files themselves are carried: the link merges every placed room's
    /// into the level's one pak (<see cref="LevelPakFiles"/>).
    /// </remarks>
    private static async Task<ZipArchiveReader?> ReadPakAsync(RoomObject room, CancellationToken cancellationToken)
    {
        BspLumpData pak = room.Bsp[BspLump.PakFile];
        if (pak.Length == 0)
        {
            return null;
        }

        try
        {
            return await ZipArchiveReader.ParseAsync(pak.Data, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidZipException exception)
        {
            throw new LinkException($"room {room.Definition.Name}'s pak file is not a zip: {exception.Message}");
        }
    }

    /// <summary>The room's planes through the transform, flip pairs kept as pairs.</summary>
    /// <remarks>
    /// <para>
    /// <c>n' = R·n, d' = d + n'·t</c>: for a moved point <c>p' = R·p + t</c>,
    /// <c>n'·p' = n·p + n'·t</c>, so it is the ROTATED normal that meets the
    /// translation. The permutation picks components and the dot is an
    /// integer times an integer, so the relocated plane is exactly
    /// representable — the same input byte-yields on any thread (I14).
    /// </para>
    /// <para>
    /// The type is re-derived from the moved normal (a quarter turn makes an
    /// x plane a y plane), and each pair's flip is rebuilt as the exact
    /// negation of the moved even plane. The engine reads an axial plane
    /// (type 0..2) by its coordinate alone, as if its normal were the positive
    /// axis, which is why the format keeps the positive half of an axial pair
    /// first. A turn can bring the negative half to the front; such a pair is
    /// stored swapped and reported in the returned flags, so every reference
    /// to it is remapped: a face, brush side or occluder takes the other index
    /// of the pair (same oriented plane), and a node keeps the even index and
    /// swaps its children.
    /// </para>
    /// </remarks>
    internal static (DPlane[] Planes, bool[] Swapped) TransformPlanes(DPlane[] planes, RoomTransform transform)
    {
        (DPlane[] pairs, bool[] swapped) = RotatePlanes(planes, transform.Placement.NormalizedRotation);
        return (TranslatePlanes(pairs, swapped, transform.Apply(Vec3.Zero)), swapped);
    }

    /// <summary>
    /// The room's texture and lightmap axes through the transform: the axis
    /// rotates like a normal and its offset absorbs the translation.
    /// </summary>
    /// <remarks>
    /// A face's texture coordinate is <c>s = v·p + w</c>. For the moved point
    /// <c>p' = R·p + t</c>, keeping <c>s</c> unchanged needs <c>v' = R·v</c>
    /// and <c>w' = w - v'·t</c>. That is not only texture alignment: the
    /// faces carry their lightmap extents in luxels
    /// (<c>LightmapTextureMinsInLuxels</c>), computed from the room-local
    /// axes, and they stay right only if every vertex keeps its s and t.
    /// </remarks>
    internal static TexInfo[] TransformTexInfos(TexInfo[] infos, RoomTransform transform) =>
        TranslateTexInfos(RotateTexInfos(infos, transform.Placement.NormalizedRotation), transform.Apply(Vec3.Zero));

    /// <summary>Quarter-turn of a normal about +z: 0 identity, 1 (-y,x,z), 2 (-x,-y,z), 3 (y,-x,z).</summary>
    internal static Vec3 ApplyNormal(Vec3 n, int rotation) => rotation switch
    {
        0 => n,
        1 => new Vec3(-n.Y, n.X, n.Z),
        2 => new Vec3(-n.X, -n.Y, n.Z),
        _ => new Vec3(n.Y, -n.X, n.Z),
    };

    /// <summary>A room-local brush's box, from its axial sides.</summary>
    /// <remarks>
    /// vbsp gives every brush its six axial bevel sides, so the axial planes
    /// among a brush's sides bound it exactly. A brush missing one reads as
    /// unbounded on that side, which makes it too big to be a plug.
    /// </remarks>
    private static Box BrushBox(DBrush brush, ReadOnlySpan<DBrushSide> sides, ReadOnlySpan<DPlane> planes)
    {
        float[] lo = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
        float[] hi = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        for (int s = 0; s < brush.NumSides; s++)
        {
            DPlane plane = planes[sides[brush.FirstSide + s].PlaneNum];
            for (int axis = 0; axis < 3; axis++)
            {
                float component = axis switch { 0 => plane.Normal.X, 1 => plane.Normal.Y, _ => plane.Normal.Z };
                if (component == 1f && IsAxial(plane.Normal, axis))
                {
                    hi[axis] = Math.Min(hi[axis], plane.Dist);
                }
                else if (component == -1f && IsAxial(plane.Normal, axis))
                {
                    lo[axis] = Math.Max(lo[axis], -plane.Dist);
                }
            }
        }

        return new Box(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
    }

    private static bool IsAxial(Vec3 n, int axis) => axis switch
    {
        0 => n.Y == 0 && n.Z == 0,
        1 => n.X == 0 && n.Z == 0,
        _ => n.X == 0 && n.Y == 0,
    };

    /// <summary>
    /// Takes, for each jointed socket, what stripping its plug touches: the
    /// plug brushes, the solid leaves the plug made (to carve), the plug's
    /// drawn faces, and the open clusters facing the joint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The census itself is per socket and the room's alone
    /// (<see cref="SocketCensus"/>, made by <c>CensusSocket</c> at room
    /// compile time or on the fly): a plug brush is a brush with a
    /// trigger-surfaced side whose box lies in the socket's kit plug box —
    /// the same two tests the linter's seal census uses, applied per brush.
    /// A leaf to carve is any solid leaf whose box overlaps the plug box's
    /// interior: vbsp does not give the plug a leaf of its own (the solid
    /// space behind a wall is split only where a plane needs it, so the plug
    /// usually shares one leaf with the wall bands and the void beyond),
    /// which is why the doorway has to be cut out of those leaves rather than
    /// one leaf flipped to empty. The level only chooses the sockets, in its
    /// joints' order, which is the order the carves are made in.
    /// </para>
    /// <para>
    /// A jointed socket no open leaf faces is refused: its door edge would be
    /// empty, and a joint that joins nothing is a broken room, not a door.
    /// </para>
    /// </remarks>
    private static void ApplyCensus(RoomPlan plan, RoomLinkShared shared)
    {
        RoomDefinition definition = plan.Placement.Room.Definition;
        foreach ((string socketName, _) in plan.Placement.Instance.Joints)
        {
            int socket = SocketIndex(definition, socketName);
            SocketCensus census = shared.Sockets[socket];
            if (census.Facing.Length == 0)
            {
                throw new LinkException(
                    $"room {definition.Name}'s jointed socket \"{socketName}\" faces no open leaf of the room,"
                    + " so the joint would join nothing");
            }

            plan.JointFacing[socketName] = census.Facing;
            plan.StrippedBrushes.UnionWith(census.StrippedBrushes);
            foreach (int leaf in census.CarveLeaves)
            {
                plan.Carves.Add(new PlugCarve(leaf, socket, census.Facing[0]));
            }

            plan.StrippedFaces.UnionWith(census.StrippedFaces);
        }

        MarkPlugOriginalFaces(plan);

        // An omitted brush model's brushes leave the brush lump with the
        // plugs: nothing the level keeps names them.
        HashSet<int> dropped = plan.StrippedBrushes;
        if (plan.Models is { } models)
        {
            dropped = [.. plan.StrippedBrushes];
            for (int m = 0; m < models.Linked.Length; m++)
            {
                RoomRange brushes = models.Source.Models[m].Brushes;
                for (int b = brushes.First; models.Linked[m] < 0 && b < brushes.End; b++)
                {
                    dropped.Add(b);
                }
            }
        }

        (plan.BrushMap, plan.KeptBrushCount, _) =
            KeptBrushes(BspStructView.As<DBrush>(plan.Bsp[BspLump.Brushes]), dropped);
    }

    /// <summary>The linked index of a kept room brush: its kept index plus the room's brush base.</summary>
    /// <param name="map">The room's kept-brush map (<see cref="KeptBrushes"/>).</param>
    /// <param name="brushBase">The room's first linked brush.</param>
    /// <param name="roomBrush">The room-local brush a reference names.</param>
    /// <param name="room">The room's name, for the refusal.</param>
    /// <exception cref="LinkException">
    /// The room names a brush it does not have, or a stripped plug: the
    /// census says nothing in the level reaches the plug any more, so a
    /// reference to one is a room the relocation does not understand, and
    /// writing it as some other brush would be silently wrong.
    /// </exception>
    internal static int LinkedBrush(int[] map, int brushBase, int roomBrush, string room)
    {
        bool inRange = (uint)roomBrush < (uint)map.Length;
        int kept = inRange ? map[roomBrush] : -1;
        if (kept < 0)
        {
            throw new LinkException(
                $"room {room} names brush {roomBrush}, which "
                + (inRange ? "is a stripped plug" : $"it does not have ({map.Length} brushes)"));
        }

        return brushBase + kept;
    }

    /// <summary>
    /// Numbers the brushes a placement keeps: every brush but the stripped
    /// plugs, in the room's own order, with no gaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stripped plug brush is in no leaf's brush list and has no collision
    /// ledge once its doorway is jointed, so nothing a trace, the physics or
    /// the renderer reads can reach it. Keeping it in the brush lump anyway
    /// (as an empty, contents-0 brush) cost a quarter of a stress level's
    /// brushes against the loader's <c>MAX_MAP_BRUSHES</c> (8192), which is
    /// what bound the largest square layout. So the link drops it, with its
    /// sides, and renumbers every reference to a kept brush through this map:
    /// the leaves' brush lists and the collision ledges' client data.
    /// </para>
    /// <para>
    /// The kept brushes keep their room order, so the linked brush lump is
    /// still every room's brushes in layout order, only without the holes,
    /// and the output stays a pure function of the layout.
    /// </para>
    /// </remarks>
    /// <param name="brushes">The room's brushes.</param>
    /// <param name="stripped">The room-local plug brushes the level strips.</param>
    /// <returns>
    /// Per room brush, its index among the kept ones, or -1 when stripped;
    /// how many are kept; and how many sides those have.
    /// </returns>
    internal static (int[] Map, int Brushes, int Sides) KeptBrushes(ReadOnlySpan<DBrush> brushes, IReadOnlySet<int> stripped)
    {
        int[] map = new int[brushes.Length];
        int kept = 0, sides = 0;
        for (int b = 0; b < brushes.Length; b++)
        {
            if (stripped.Contains(b))
            {
                map[b] = -1;
                continue;
            }

            map[b] = kept++;
            sides += brushes[b].NumSides;
        }

        return (map, kept, sides);
    }

    /// <summary>The index of the first socket of that name, as the census has always looked it up.</summary>
    private static int SocketIndex(RoomDefinition definition, string name)
    {
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            if (definition.Sockets[s].Name == name)
            {
                return s;
            }
        }

        throw new InvalidOperationException($"room {definition.Name} has no socket \"{name}\"");
    }

    /// <summary>
    /// The plug's original faces: the ones its stripped drawn faces were cut
    /// from, each with the room-local texinfo of such a drawn face.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An original face's own texinfo cannot say whether it is a plug face,
    /// because it is not a texinfo of the compile. vbsp compacts the texinfo
    /// table after the faces are written, renumbering the drawn faces, the
    /// brush sides and the water data but not the original faces; so a room
    /// whose compaction dropped or folded
    /// a texinfo (a player clip's or a grate's side, say) carries original
    /// faces that name the old table, past the end of the new one or at an
    /// unrelated entry. The drawn face's <c>OrigFace</c> link is kept by the
    /// compile and is exact, so the plug's drawn faces name its original
    /// faces, and lend them the texinfo the nodraw copy is made from.
    /// </para>
    /// <para>
    /// A plug original face no drawn face came from is not found, and does
    /// not need to be: it never drew in the room either.
    /// </para>
    /// </remarks>
    private static void MarkPlugOriginalFaces(RoomPlan plan)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(plan.Bsp[BspLump.Faces]);
        foreach (int f in plan.StrippedFaces.Order())
        {
            DFace face = faces[f];
            if (face.OrigFace >= 0)
            {
                plan.StrippedOrigFaces.TryAdd(face.OrigFace, face.TexInfo);
            }
        }
    }

    private static bool IsTriggerBrush(DBrush brush, ReadOnlySpan<DBrushSide> sides, ReadOnlySpan<TexInfo> texInfos)
    {
        for (int s = 0; s < brush.NumSides; s++)
        {
            short texInfo = sides[brush.FirstSide + s].TexInfo;
            if (texInfo >= 0 && (texInfos[texInfo].Flags & (int)SurfaceFlags.Trigger) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The trigger-surfaced drawn faces whose every vertex lies in the plug box.</summary>
    private static void MarkPlugFaces(BspData bsp, Box plug, HashSet<int> into)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<Vec3> vertices = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        for (int f = 0; f < faces.Length; f++)
        {
            DFace face = faces[f];
            if (face.TexInfo < 0 || (texInfos[face.TexInfo].Flags & (int)SurfaceFlags.Trigger) == 0)
            {
                continue;
            }

            bool inside = face.NumEdges > 0;
            for (int e = 0; e < face.NumEdges && inside; e++)
            {
                int se = surfEdges[face.FirstEdge + e];
                Vec3 v = vertices[se >= 0 ? edges[se].V[0] : edges[-se].V[1]];
                inside = new Box(v, v).ContainsWithin(plug, RoomLinter.CellEpsilon);
            }

            if (inside)
            {
                into.Add(f);
            }
        }
    }

    /// <summary>One solid leaf a jointed plug made, and the doorway to cut out of it.</summary>
    /// <param name="Leaf">The room-local leaf index.</param>
    /// <param name="Socket">The jointed socket's index in the definition, whose turned plug box (<see cref="RoomLinkGeometry.PlugBoxes"/>) the doorway is.</param>
    /// <param name="Cluster">The room-local cluster the doorway joins.</param>
    internal readonly record struct PlugCarve(int Leaf, int Socket, int Cluster);

    /// <summary>A room's link-time facts: its moved structs, counts, and assigned bases.</summary>
    internal sealed class RoomPlan
    {
        public required ResolvedPlacement Placement { get; init; }
        public required BspData Bsp { get; init; }
        public required RoomTransform Transform { get; init; }

        /// <summary>The room's geometry turned for this placement, which the assembly moves to the cell.</summary>
        public required RoomLinkGeometry Geometry { get; init; }

        public required byte[][] OwnRows { get; init; }
        public required int ClusterCount { get; init; }
        public required Vec3[] Vertices { get; init; }
        public required DPlane[] TransformedPlanes { get; init; }
        public required bool[] PlaneSwapped { get; init; }
        public required TexInfo[] TexInfos { get; init; }
        public required DLeaf[] Leafs { get; init; }
        public required int EdgeCount { get; init; }
        public required int FaceCount { get; init; }
        public required int LeafFaceCount { get; init; }
        public required int LightingLength { get; init; }
        public required int SurfEdgeCount { get; init; }
        public required int OrigFaceCount { get; init; }
        public required int VertNormalCount { get; init; }
        public required int VertNormalIndexCount { get; init; }
        public required int PrimCount { get; init; }
        public required int PrimIndexCount { get; init; }
        public required int PrimVertCount { get; init; }
        public required OcclusionLump? Occlusion { get; init; }
        public required int FacesVersion { get; init; }
        public required int LeafsVersion { get; init; }

        /// <summary>The room's door visibility, stored with the room or worked out now (<see cref="RoomDoorVisibility"/>).</summary>
        public required RoomDoorVisibility DoorVisibility { get; init; }

        /// <summary>The room's brush models as this placement links them, or null for a room with only the world.</summary>
        public RoomModelLayout? Models { get; init; }

        /// <summary>The room's base lighting, or null for an unlit room (<see cref="RoomObject.LightingOfCompile"/>).</summary>
        public RoomLighting? Lighting { get; init; }

        /// <summary>The stored turn this placement takes (<see cref="RoomLighting.For"/>), or null for an unlit room.</summary>
        public RoomLightingPayload? Lit { get; init; }

        /// <summary>The room's vertex normals turned for this placement: its bake's when lit, else its compile's.</summary>
        public required Vec3[] VertNormals { get; init; }

        /// <summary>
        /// Per room face, where its run of vertex-normal indices starts (one
        /// past the last face, the total), for a lit room: a brush model's
        /// runs are found from its faces, since its compile had no normals.
        /// Null for an unlit room.
        /// </summary>
        public int[]? FaceVertexStarts { get; init; }

        /// <summary>For a lit level, per room vertex normal, the level's (<see cref="InternNormals"/>); null otherwise.</summary>
        public int[]? NormalMap;

        /// <summary>Where this placement's HDR lightmaps start in the level's HDR lighting lump.</summary>
        public int LightBaseHdr;
        /// <summary>The room's overlays (<see cref="RoomOverlays"/>), or null for a room with none.</summary>
        public RoomOverlays? Overlays { get; init; }

        /// <summary>The room's water (<see cref="RoomWater"/>), or null for a room with none.</summary>
        public RoomWater? Water { get; init; }

        /// <summary>
        /// Per room leaf water data record, the level's (<see cref="PlanWaterData"/>);
        /// null for a room without water.
        /// </summary>
        public int[]? WaterMap;

        /// <summary>How many water overlays every placement before this one has: its water overlay 0's offset from the first id.</summary>
        public int WaterOverlayBase;

        /// <summary>Whether this is the library's skybox room, placed below the grid (<see cref="SkyboxOf"/>).</summary>
        public bool IsSkybox => Placement.Instance.Placement.Level != 0;

        /// <summary>The room's areas and area portals (<see cref="RoomAreaPortals"/>), or null for a room with none.</summary>
        public RoomAreaPortals? AreaPortals { get; init; }

        /// <summary>The room's three area lumps, checked (<see cref="RoomAreaPortals.Lumps"/>), or null for a room without area portals.</summary>
        public RoomAreaLumps? AreaLumps { get; init; }

        /// <summary>
        /// Per room area, the level area it became (<see cref="PlanAreas"/>),
        /// area 0 staying 0; null when no placed room has an area portal and
        /// every room's area 1 is the level's one open area.
        /// </summary>
        public int[]? AreaMap;

        /// <summary>The linked number of the room's portal 0 (its first is 1 past it): the portal numbers of every placement before this one.</summary>
        public int PortalBase;

        /// <summary>How many of the room's nodes the placement keeps: all but an omitted brush model's.</summary>
        public int KeptNodeCount => Models is { } m ? RoomModelLayout.KeptCount(m.OmittedNodes, NodeCount) : NodeCount;

        /// <summary>How many of the room's leaves the placement keeps.</summary>
        public int KeptLeafCount => Models is { } m ? RoomModelLayout.KeptCount(m.OmittedLeaves, Leafs.Length) : Leafs.Length;

        /// <summary>How many of the room's leaf-face entries the placement keeps.</summary>
        public int KeptLeafFaceCount => Models is { } m ? RoomModelLayout.KeptCount(m.OmittedLeafFaces, LeafFaceCount) : LeafFaceCount;

        /// <summary>The room's world faces: all its faces when it has no brush model.</summary>
        public int WorldFaceCount => Models?.WorldFaces ?? FaceCount;

        /// <summary>The room's node count.</summary>
        public int NodeCount => BspStructView.Count<DNode>(Bsp[BspLump.Nodes]);

        /// <summary>The linked index of a room node the placement keeps.</summary>
        public int LinkedNode(int roomNode) =>
            NodeBase + (Models is { } m ? RoomModelLayout.Kept(m.OmittedNodes, roomNode) : roomNode);

        /// <summary>The linked index of a room leaf the placement keeps.</summary>
        public int LinkedLeaf(int roomLeaf) =>
            LeafBase + (Models is { } m ? RoomModelLayout.Kept(m.OmittedLeaves, roomLeaf) : roomLeaf);

        /// <summary>A node's child reference, rebased: a node through <see cref="LinkedNode"/>, a leaf through <see cref="LinkedLeaf"/>.</summary>
        public int LinkedChild(int child) => child >= 0 ? LinkedNode(child) : -(LinkedLeaf(~child) + 1);

        /// <summary>The linked offset of a room leaf-face entry the placement keeps.</summary>
        /// <remarks>
        /// A leaf with no faces may name any offset, one inside an omitted
        /// run included; it takes the place that run would have had.
        /// </remarks>
        public int LinkedLeafFace(int roomOffset) =>
            LeafFaceBase + (Models is { } m ? RoomModelLayout.KeptOrAt(m.OmittedLeafFaces, roomOffset) : roomOffset);

        /// <summary>
        /// The linked index of a room face the placement keeps, or of the
        /// face-range start a node of the room names: a world face after the
        /// room's world face base, a brush model's after its model's first
        /// linked face (the model lump's faces follow every room's world).
        /// </summary>
        /// <param name="roomFace">The room face.</param>
        /// <param name="owner">The brush model the face or node belongs to, or null for the world's.</param>
        public int LinkedFace(int roomFace, RoomBrushModel? owner = null)
        {
            if (Models is not { } m || (owner is null && roomFace < m.WorldFaces))
            {
                return FaceBase + roomFace;
            }

            owner ??= m.Owner(x => x.Faces, roomFace)!;
            return m.LinkedFaceStart[owner.Model - 1] + (roomFace - owner.Faces.First);
        }

        /// <summary>Per jointed socket, the open room-local clusters facing it.</summary>
        public Dictionary<string, int[]> JointFacing { get; } = new(StringComparer.Ordinal);

        /// <summary>The room-local plug brushes of jointed sockets.</summary>
        public HashSet<int> StrippedBrushes { get; } = [];

        /// <summary>The room-local drawn faces of jointed plugs.</summary>
        public HashSet<int> StrippedFaces { get; } = [];

        /// <summary>
        /// Per room brush, its index among the brushes this placement keeps
        /// (<see cref="KeptBrushes"/>), or -1 for a stripped plug; the linked
        /// index is that plus <see cref="BrushBase"/>.
        /// </summary>
        public int[] BrushMap = [];

        /// <summary>The brushes this placement adds to the link: all but the stripped plugs.</summary>
        public int KeptBrushCount;

        /// <summary>
        /// The linked index of a room brush this placement keeps, for a
        /// reference that names one (a leaf's brush list, a ledge's client
        /// data): <see cref="LevelLinker.LinkedBrush(int[], int, int, string)"/>.
        /// </summary>
        public int LinkedBrush(int roomBrush) =>
            LevelLinker.LinkedBrush(BrushMap, BrushBase, roomBrush, Placement.Room.Definition.Name);

        /// <summary>
        /// The room-local original faces of jointed plugs, each with the
        /// room-local texinfo of a stripped drawn face cut from it (an
        /// original face's own texinfo is not one of the compile's).
        /// </summary>
        public Dictionary<int, short> StrippedOrigFaces { get; } = [];

        /// <summary>The solid leaves jointed plugs made, with the doorway each yields.</summary>
        public List<PlugCarve> Carves { get; } = [];

        public int VertexBase;
        public int EdgeBase;
        public int FaceBase;
        public int BrushBase;
        public int LeafFaceBase;
        public int NodeBase;
        public int LeafBase;
        public int LightBase;
        public int ClusterBase;
        public int SurfEdgeBase;
        public int OrigFaceBase;
        public int PrimBase;
        public int PrimIndexBase;
        public int PrimVertBase;
        public int VertNormalBase;
        public int OccluderBase;
        public int OccluderPolyBase;
        public int OccluderVertexBase;

        /// <summary>The linked id of the room's overlay 0: the overlays of every placement before this one.</summary>
        public int OverlayBase;
        public int VertexNormalIndexBase;

        /// <summary>
        /// Per room plane pair, the linked even index of the shared pair that
        /// holds it (<see cref="LinkPlanes"/>); set when the assembly interns
        /// the room's planes.
        /// </summary>
        public int[] PlanePairs = [];

        /// <summary>
        /// Per room plane pair, whether the shared pair's even half is the
        /// flip of this room's stored even half (a non-axial pair another
        /// room brought in the opposite order).
        /// </summary>
        public bool[] PlanePairFlipped = [];

        /// <summary>Per room texinfo, the shared texinfo with its content (<see cref="LinkTextures"/>).</summary>
        public int[] TexInfoMap = [];

        /// <summary>Per room string-table entry, the shared entry naming the same string.</summary>
        public int[] StringMap = [];

        /// <summary>
        /// The linked index of the plane a face, brush side or occluder of
        /// this room names: the same oriented plane, wherever its pair was
        /// stored and whichever half of the shared pair holds it.
        /// </summary>
        /// <remarks>
        /// The room's plane <c>p</c> is half <c>p &amp; 1</c> of its pair. The
        /// relocation stored the pair swapped (<see cref="PlaneSwapped"/>) when
        /// a turn brought its negative axial half to the front, and the shared
        /// table may hold the pair flipped (<see cref="PlanePairFlipped"/>);
        /// each moves the same oriented plane to the other half, so the half
        /// is the parity of all three.
        /// </remarks>
        public int PlaneRef(int roomPlane)
        {
            int pair = roomPlane >> 1;
            bool other = PlaneSwapped[pair] != PlanePairFlipped[pair];
            return PlanePairs[pair] + ((roomPlane & 1) ^ (other ? 1 : 0));
        }

        /// <summary>
        /// <see cref="PlaneRef(int)"/> for a structure of the room, in its
        /// entity's own frame when <paramref name="local"/> (an
        /// origin-relative brush model's: turned, never moved).
        /// </summary>
        public int PlaneRef(int roomPlane, bool local)
        {
            if (!local)
            {
                return PlaneRef(roomPlane);
            }

            int pair = roomPlane >> 1;
            bool other = PlaneSwapped[pair] != Models!.LocalPlanePairFlipped[pair];
            return Models.LocalPlanePairs[pair] + ((roomPlane & 1) ^ (other ? 1 : 0));
        }

        /// <summary><see cref="NodePlane(int)"/> in an origin-relative model's own frame when <paramref name="local"/>.</summary>
        public (int Plane, bool FlipChildren) NodePlane(int roomPlane, bool local)
        {
            if (!local)
            {
                return NodePlane(roomPlane);
            }

            int pair = roomPlane >> 1;
            return (Models!.LocalPlanePairs[pair], ((roomPlane & 1) == 1) != PlaneSwapped[pair] != Models.LocalPlanePairFlipped[pair]);
        }

        /// <summary><see cref="TexInfoRef(int)"/> in an origin-relative model's own frame when <paramref name="local"/>.</summary>
        public int TexInfoRef(int roomTexInfo, bool local) =>
            local ? Remap(Models!.LocalTexInfoMap, roomTexInfo) : TexInfoRef(roomTexInfo);

        /// <summary>
        /// The linked plane a node of this room splits on, always the even
        /// (positive-first) half, and whether the node's children must swap
        /// because that half is the flip of the plane the node was built on.
        /// </summary>
        public (int Plane, bool FlipChildren) NodePlane(int roomPlane)
        {
            int pair = roomPlane >> 1;
            return (PlanePairs[pair], ((roomPlane & 1) == 1) != PlaneSwapped[pair] != PlanePairFlipped[pair]);
        }

        /// <summary>The linked texinfo a face or brush side of this room names (<see cref="Remap"/>).</summary>
        public int TexInfoRef(int roomTexInfo) => Remap(TexInfoMap, roomTexInfo);
    }
}
