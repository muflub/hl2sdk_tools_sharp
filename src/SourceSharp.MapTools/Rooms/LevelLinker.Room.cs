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
    /// Validates one room's compile against the relocation set and reads the
    /// structs the assembly patches. Pure: reads only this room, writes only
    /// the returned plan, so any number of rooms plan concurrently.
    /// </summary>
    private static RoomPlan PlanRoom(ResolvedPlacement placement)
    {
        RoomObject room = placement.Room;
        BspData bsp = room.Bsp;
        string name = room.Definition.Name;
        RoomTransform transform = new(placement.Instance.Placement, room.Definition.CellSize);

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

        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
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
        byte[][] ownRows = new byte[room.Vis.ClusterCount][];
        for (int c = 0; c < ownRows.Length; c++)
        {
            ownRows[c] = room.Vis.Pvs(c).ToArray();
        }

        // Geometry that needs no cross-room base is moved here.
        Vec3[] vertexSource = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]).ToArray();
        Vec3[] vertices = new Vec3[vertexSource.Length];
        for (int v = 0; v < vertexSource.Length; v++)
        {
            vertices[v] = transform.Apply(vertexSource[v]);
        }

        (DPlane[] planes, bool[] swapped) = TransformPlanes(BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(), transform);
        TexInfo[] texInfos = TransformTexInfos(BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray(), transform);

        RoomPlan plan = new()
        {
            Placement = placement,
            Bsp = bsp,
            Transform = transform,
            OwnRows = ownRows,
            ClusterCount = room.Vis.ClusterCount,
            Vertices = vertices,
            TransformedPlanes = planes,
            PlaneSwapped = swapped,
            TexInfos = texInfos,
            Leafs = leafs,
            EdgeCount = BspStructView.Count<DEdge>(bsp[BspLump.Edges]),
            TexDataCount = BspStructView.Count<DTexData>(bsp[BspLump.TexData]),
            FaceCount = BspStructView.Count<DFace>(bsp[BspLump.Faces]),
            BrushCount = BspStructView.Count<DBrush>(bsp[BspLump.Brushes]),
            BrushSideCount = BspStructView.Count<DBrushSide>(bsp[BspLump.BrushSides]),
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
            StringTableCount = BspStructView.Count<int>(bsp[BspLump.TexDataStringTable]),
            StringDataLength = bsp[BspLump.TexDataStringData].Length,
        };

        CensusPlugs(plan);
        return plan;
    }

    /// <summary>
    /// Refuses area portals: a room's areas are collapsed into the level's one
    /// open area (see <c>Assemble</c>), and a portal inside a room would need
    /// its own areas, and a portal at every joint, to mean anything.
    /// </summary>
    /// <remarks>
    /// vbsp writes area 0 (the reserved "no area") and one area per sealed
    /// region, and portal 0 as a reserved empty entry. A room with no
    /// <c>func_areaportal</c> has exactly areas 0 and 1 and that one portal,
    /// and that is the only shape the linker accepts. The collapse is what
    /// makes the linked level networkable: two rooms left in two areas with
    /// no area portal between them are two worlds to the server, and it never
    /// sends the entities of one to a client standing in the other, whatever
    /// the PVS says.
    /// </remarks>
    private static void RefuseAreaPortals(string name, BspData bsp)
    {
        int areas = BspStructView.Count<DArea>(bsp[BspLump.Areas]);
        int portals = BspStructView.Count<DAreaPortal>(bsp[BspLump.AreaPortals]);
        if (areas > 2 || portals > 1)
        {
            throw new LinkException(
                $"room {name} has {areas} areas and {portals} area portals; a linkable room has no area portal"
                + " (areas 0 and 1 and the reserved portal 0), because its areas are merged into the level's one");
        }
    }

    /// <summary>
    /// Refuses static and detail props (and any other game-lump content): the
    /// game lumps are carried only when every room's are all zeros.
    /// </summary>
    /// <remarks>
    /// vbsp always writes the static-prop and detail-prop game lumps, even
    /// empty; empty is every count zero, which for these formats is every byte
    /// zero. Relocating real props would mean moving their origins and angles,
    /// merging their model dictionaries and renumbering the leaf lists they
    /// carry — none of which the linker does — so a room with any is refused
    /// by name rather than having its props silently dropped.
    /// </remarks>
    private static void RefuseGameLumpContent(string name, BspData bsp)
    {
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
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
    /// Refuses a room whose pak file holds a file: the linked map carries one
    /// pak, and merging archives (and the materials they may patch) is not
    /// something the relocation does.
    /// </summary>
    private static async Task RefusePackedFilesAsync(RoomObject room, CancellationToken cancellationToken)
    {
        BspLumpData pak = room.Bsp[BspLump.PakFile];
        if (pak.Length == 0)
        {
            return;
        }

        ZipArchiveReader archive;
        try
        {
            archive = await ZipArchiveReader.ParseAsync(pak.Data, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidZipException exception)
        {
            throw new LinkException($"room {room.Definition.Name}'s pak file is not a zip: {exception.Message}");
        }

        if (archive.Entries.Count != 0)
        {
            throw new LinkException(
                $"room {room.Definition.Name}'s pak file holds {archive.Entries.Count} files;"
                + " the relocation carries only an empty pak");
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
        if (planes.Length % 2 != 0)
        {
            throw new LinkException("the planes lump holds an odd number of planes, so a flip pair is missing");
        }

        Vec3 translation = transform.Apply(Vec3.Zero);
        int rotation = transform.Placement.NormalizedRotation;
        bool[] swapped = new bool[planes.Length / 2];
        for (int i = 0; i < planes.Length; i += 2)
        {
            Vec3 normal = ApplyNormal(planes[i].Normal, rotation);
            float dist = planes[i].Dist + Vec3.Dot(normal, translation);
            Plane moved = new(normal, dist);
            DPlane even = new() { Normal = normal, Dist = dist, Type = (int)moved.Type };
            DPlane odd = new() { Normal = -normal, Dist = -dist, Type = (int)moved.Type };
            bool negativeAxial = moved.Type switch
            {
                PlaneType.X => normal.X < 0,
                PlaneType.Y => normal.Y < 0,
                PlaneType.Z => normal.Z < 0,
                _ => false,
            };

            swapped[i / 2] = negativeAxial;
            planes[i] = negativeAxial ? odd : even;
            planes[i + 1] = negativeAxial ? even : odd;
        }

        return (planes, swapped);
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
    internal static TexInfo[] TransformTexInfos(TexInfo[] infos, RoomTransform transform)
    {
        Vec3 t = transform.Apply(Vec3.Zero);
        int rotation = transform.Placement.NormalizedRotation;
        for (int i = 0; i < infos.Length; i++)
        {
            for (int row = 0; row < 2; row++)
            {
                MoveAxis(ref infos[i].TextureVecsTexelsPerWorldUnits, row, rotation, t);
                MoveAxis(ref infos[i].LightmapVecsLuxelsPerWorldUnits, row, rotation, t);
            }
        }

        return infos;
    }

    private static void MoveAxis(ref FloatArray8 vecs, int row, int rotation, Vec3 t)
    {
        int at = row * 4;
        Vec3 axis = ApplyNormal(new Vec3(vecs[at], vecs[at + 1], vecs[at + 2]), rotation);
        vecs[at] = axis.X;
        vecs[at + 1] = axis.Y;
        vecs[at + 2] = axis.Z;
        vecs[at + 3] -= Vec3.Dot(axis, t);
    }

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
    /// Finds what stripping each jointed socket's plug touches: the plug
    /// brushes, the solid leaves the plug made (to carve), the plug's drawn
    /// faces, and the open clusters facing the joint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plug brush is a brush with a trigger-surfaced side whose box lies in
    /// the socket's kit plug box — the same two tests the linter's seal census
    /// uses, applied per brush. A leaf to carve is any solid leaf whose box
    /// overlaps the plug box's interior: vbsp does not give the plug a leaf of
    /// its own (the solid space behind a wall is split only where a plane
    /// needs it, so the plug usually shares one leaf with the wall bands and
    /// the void beyond), which is why the doorway has to be cut out of those
    /// leaves rather than one leaf flipped to empty.
    /// </para>
    /// <para>
    /// A jointed socket no open leaf faces is refused: its door edge would be
    /// empty, and a joint that joins nothing is a broken room, not a door.
    /// </para>
    /// </remarks>
    private static void CensusPlugs(RoomPlan plan)
    {
        RoomObject room = plan.Placement.Room;
        RoomDefinition definition = room.Definition;
        BspData bsp = plan.Bsp;
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);

        foreach ((string socketName, _) in plan.Placement.Instance.Joints)
        {
            RoomSocket socket = definition.Sockets.First(s => s.Name == socketName);
            Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);

            int[] facing = Facing(plan.Leafs, plug);
            if (facing.Length == 0)
            {
                throw new LinkException(
                    $"room {definition.Name}'s jointed socket \"{socketName}\" faces no open leaf of the room,"
                    + " so the joint would join nothing");
            }

            plan.JointFacing[socketName] = facing;

            for (int b = 0; b < brushes.Length; b++)
            {
                if (IsTriggerBrush(brushes[b], sides, texInfos)
                    && BrushBox(brushes[b], sides, planes).ContainsWithin(plug, RoomLinter.CellEpsilon))
                {
                    plan.StrippedBrushes.Add(b);
                }
            }

            for (int l = 0; l < plan.Leafs.Length; l++)
            {
                DLeaf leaf = plan.Leafs[l];
                if ((leaf.Contents & (int)BrushContents.Solid) != 0
                    && BoxOf(leaf).Overlaps(plug, RoomLinter.CellEpsilon))
                {
                    plan.Carves.Add(new PlugCarve(l, plug, facing[0]));
                }
            }

            MarkPlugFaces(plan, plug);
        }

        MarkPlugOriginalFaces(plan);
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
    private static void MarkPlugFaces(RoomPlan plan, Box plug)
    {
        BspData bsp = plan.Bsp;
        HashSet<int> into = plan.StrippedFaces;
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
    /// <param name="Plug">The plug box, room-local.</param>
    /// <param name="Cluster">The room-local cluster the doorway joins.</param>
    internal readonly record struct PlugCarve(int Leaf, Box Plug, int Cluster);

    /// <summary>A room's link-time facts: its moved structs, counts, and assigned bases.</summary>
    internal sealed class RoomPlan
    {
        public required ResolvedPlacement Placement { get; init; }
        public required BspData Bsp { get; init; }
        public required RoomTransform Transform { get; init; }
        public required byte[][] OwnRows { get; init; }
        public required int ClusterCount { get; init; }
        public required Vec3[] Vertices { get; init; }
        public required DPlane[] TransformedPlanes { get; init; }
        public required bool[] PlaneSwapped { get; init; }
        public required TexInfo[] TexInfos { get; init; }
        public required DLeaf[] Leafs { get; init; }
        public required int EdgeCount { get; init; }
        public required int TexDataCount { get; init; }
        public required int FaceCount { get; init; }
        public required int BrushCount { get; init; }
        public required int BrushSideCount { get; init; }
        public required int LeafFaceCount { get; init; }
        public required int LightingLength { get; init; }
        public required int StringTableCount { get; init; }
        public required int StringDataLength { get; init; }
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

        /// <summary>Per jointed socket, the open room-local clusters facing it.</summary>
        public Dictionary<string, int[]> JointFacing { get; } = new(StringComparer.Ordinal);

        /// <summary>The room-local plug brushes of jointed sockets.</summary>
        public HashSet<int> StrippedBrushes { get; } = [];

        /// <summary>The room-local drawn faces of jointed plugs.</summary>
        public HashSet<int> StrippedFaces { get; } = [];

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
        public int TexInfoBase;
        public int TexDataBase;
        public int PlaneBase;
        public int FaceBase;
        public int BrushBase;
        public int BrushSideBase;
        public int LeafFaceBase;
        public int NodeBase;
        public int LeafBase;
        public int LightBase;
        public int StringTableBase;
        public int StringDataBase;
        public int ClusterBase;
        public int SurfEdgeBase;
        public int OrigFaceBase;
        public int PrimBase;
        public int PrimIndexBase;
        public int PrimVertBase;
        public int VertNormalBase;
        public int OccluderPolyBase;
        public int OccluderVertexBase;
        public int VertexNormalIndexBase;

        /// <summary>
        /// The linked index of the plane a face, brush side or occluder of
        /// this room names: the same oriented plane, wherever its pair was
        /// stored.
        /// </summary>
        public int PlaneRef(int roomPlane) =>
            PlaneBase + (PlaneSwapped[roomPlane >> 1] ? roomPlane ^ 1 : roomPlane);

        /// <summary>
        /// The linked plane a node of this room splits on, always the even
        /// (positive-first) half, and whether the node's children must swap
        /// because that half is the flip of the plane the node was built on.
        /// </summary>
        public (int Plane, bool FlipChildren) NodePlane(int roomPlane) =>
            (PlaneBase + (roomPlane & ~1), ((roomPlane & 1) == 1) != PlaneSwapped[roomPlane >> 1]);
    }
}
