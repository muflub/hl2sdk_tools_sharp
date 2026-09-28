//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Validation;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// Writes the linked map: every room's structs in layout order with their
    /// indices shifted by the bases, the top tree above them, the jointed
    /// plugs stripped, and the lumps that exist once per map merged.
    /// </summary>
    private static BspData Assemble(
        RoomPlan[] plans,
        LevelLayout layout,
        byte[] visibilityLump,
        VbspContext context,
        EntityClassTable classes,
        LevelNaming naming,
        string? mapVersion,
        CancellationToken cancellationToken)
    {
        float cell = layout.CellSize;
        RoomPlan first = plans[0];
        RequireAgreement(plans);

        // Planes: [0],[1] the null pair; every room's transformed planes in
        // layout order; the top tree's cell-face planes; the carve planes.
        List<DPlane> planes = [new(), new()];
        foreach (RoomPlan plan in plans)
        {
            planes.AddRange(plan.TransformedPlanes);
        }

        List<Plane> topPlanes = [];
        List<DNode> top = BuildTopNodes(layout, cell, topPlanes, planes.Count);
        foreach (Plane p in topPlanes)
        {
            AddPlanePair(planes, p.Normal, p.Dist);
        }

        // Nodes: the top tree first (so model 0's head node is 0), then every
        // room's subtree with its children rebased and its bounds moved.
        List<DNode> nodes = [.. top];
        int[] roomNodeCounts = new int[plans.Length];
        for (int r = 0; r < plans.Length; r++)
        {
            RoomPlan plan = plans[r];
            plan.NodeBase = nodes.Count;
            ReadOnlySpan<DNode> roomNodes = BspStructView.As<DNode>(plan.Bsp[BspLump.Nodes]);
            roomNodeCounts[r] = roomNodes.Length;
            for (int i = 0; i < roomNodes.Length; i++)
            {
                DNode n = roomNodes[i];
                DNode shifted = n;
                (int planeNum, bool flip) = plan.NodePlane(n.PlaneNum);
                shifted.PlaneNum = planeNum;
                IntArray2 children = default;
                for (int side = 0; side < 2; side++)
                {
                    int child = n.Children[side];
                    children[side ^ (flip ? 1 : 0)] = child >= 0
                        ? child + plan.NodeBase
                        : -(plan.LeafBase + ~child + 1);
                }

                shifted.Children = children;
                shifted.FirstFace = (ushort)(n.FirstFace + plan.FaceBase);
                Box box = plan.Transform.TranslateBox(plan.Geometry.NodeBoxes[i]);
                shifted.Mins = Short3(box.Mins);
                shifted.Maxs = Short3(box.Maxs);
                nodes.Add(shifted);
            }
        }

        // Top nodes reference room roots (their positive children) and the
        // shared solid leaf (leaf 0, negative child -1); room bases are only
        // known now, so the second pass fills them.
        FillTopChildren(nodes, top.Count, plans, layout);
        cancellationToken.ThrowIfCancellationRequested();

        // Leafs: the shared solid at index 0 — the void outside every room,
        // bounded by the grid — then every room's leaves with clusters, leaf
        // faces, bounds and brush runs rebased. The brush runs are rebuilt
        // rather than copied, because a stripped plug brush leaves every run
        // it was in.
        (int minx, int miny, int maxx, int maxy) = Extent(layout);
        List<DLeaf> leafs = [new DLeaf
        {
            Contents = (int)BrushContents.Solid,
            Cluster = -1,
            AreaFlags = 0,
            Mins = Short3(new Vec3(minx * cell, miny * cell, 0)),
            Maxs = Short3(new Vec3((maxx + 1) * cell, (maxy + 1) * cell, cell)),
            LeafWaterDataId = -1,
        }];
        List<ushort> leafBrushes = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<ushort> roomLeafBrushes = BspStructView.As<ushort>(plan.Bsp[BspLump.LeafBrushes]);
            for (int l = 0; l < plan.Leafs.Length; l++)
            {
                DLeaf leaf = plan.Leafs[l];
                DLeaf shifted = leaf;
                if (shifted.Cluster >= 0)
                {
                    shifted.Cluster = (short)(leaf.Cluster + plan.ClusterBase);
                }

                shifted.FirstLeafFace = (ushort)(leaf.FirstLeafFace + plan.LeafFaceBase);
                int start = leafBrushes.Count;
                for (int b = 0; b < leaf.NumLeafBrushes; b++)
                {
                    int brush = roomLeafBrushes[leaf.FirstLeafBrush + b];
                    if (!plan.StrippedBrushes.Contains(brush))
                    {
                        leafBrushes.Add((ushort)(brush + plan.BrushBase));
                    }
                }

                Limit(plan, "leaf brushes", leafBrushes.Count, ushort.MaxValue + 1);
                shifted.FirstLeafBrush = (ushort)(leafBrushes.Count == start ? 0 : start);
                shifted.NumLeafBrushes = (ushort)(leafBrushes.Count - start);
                Box box = plan.Transform.TranslateBox(plan.Geometry.LeafBoxes[l]);
                shifted.Mins = Short3(box.Mins);
                shifted.Maxs = Short3(box.Maxs);
                leafs.Add(shifted);
            }
        }

        // LeafMinDistToWater: vvis rewrote it as one ushort per leaf; the
        // shared solid leaf gets 65535, which is "no water".
        List<ushort>? leafMinDist = null;
        if (plans.Any(p => p.Bsp[BspLump.LeafMinDistToWater].Length > 0))
        {
            leafMinDist = [ushort.MaxValue];
            foreach (RoomPlan plan in plans)
            {
                BspLumpData dists = plan.Bsp[BspLump.LeafMinDistToWater];
                if (dists.Length != plan.Leafs.Length * sizeof(ushort))
                {
                    throw new LinkException(
                        $"room {plan.Placement.Room.Definition.Name}'s LeafMinDistToWater holds {dists.Length} bytes for {plan.Leafs.Length} leaves");
                }

                leafMinDist.AddRange(BspStructView.As<ushort>(dists));
            }
        }

        // The doorways: every solid leaf a jointed plug made is split so the
        // plug's box inside it becomes an empty leaf of the facing cluster.
        for (int r = 0; r < plans.Length; r++)
        {
            CarvePlugs(plans[r], roomNodeCounts[r], nodes, leafs, planes, leafMinDist);
        }

        Limit(plans[^1], "leaves", leafs.Count, ushort.MaxValue + 1);
        Limit(plans[^1], "planes", planes.Count, ushort.MaxValue + 1);

        // The node total up front counted the top tree at its floor and no
        // carve chains (LinkTotals); here both are built, so the exact total
        // is held to the loader's cap. It names the last room, as the other
        // totals checked after every room's part is in do.
        LoaderLimit(
            plans[^1].Placement.Room.Definition.Name,
            plans[^1].Placement.Instance.Placement.CellX,
            plans[^1].Placement.Instance.Placement.CellY,
            "nodes",
            nodes.Count,
            BspLimits.Caps.First(c => c.Lump == BspLump.Nodes).Max,
            "MAX_MAP_NODES");
        cancellationToken.ThrowIfCancellationRequested();

        List<ushort> leafFaces = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (ushort face in BspStructView.As<ushort>(plan.Bsp[BspLump.LeafFaces]))
            {
                leafFaces.Add((ushort)(face + plan.FaceBase));
            }
        }

        // Texinfos first, so the nodraw copies of the plug faces' texinfos
        // go after every room's.
        List<TexInfo> texInfos = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (TexInfo info in plan.TexInfos)
            {
                TexInfo shifted = info;
                shifted.TexData += plan.TexDataBase;
                texInfos.Add(shifted);
            }
        }

        Dictionary<int, int> nodrawOf = [];
        int NoDraw(RoomPlan plan, int linkedTexInfo)
        {
            if (!nodrawOf.TryGetValue(linkedTexInfo, out int copy))
            {
                // A stripped plug's face stays in the face list (renumbering
                // faces would touch every node, leaf and primitive range), but
                // it is now a sheet of trigger material hanging in an open
                // doorway: drawn nodraw, it is gone for the renderer.
                TexInfo hidden = texInfos[linkedTexInfo];
                hidden.Flags |= (int)SurfaceFlags.NoDraw;
                copy = texInfos.Count;
                texInfos.Add(hidden);
                LoaderLimit(
                    plan.Placement.Room.Definition.Name,
                    plan.Placement.Instance.Placement.CellX,
                    plan.Placement.Instance.Placement.CellY,
                    "texinfos",
                    texInfos.Count,
                    BspLimits.Caps.First(c => c.Lump == BspLump.TexInfo).Max,
                    "MAX_MAP_TEXINFO");
                nodrawOf[linkedTexInfo] = copy;
            }

            return copy;
        }

        List<DFace> faces = [];
        List<byte> lighting = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DFace> roomFaces = BspStructView.As<DFace>(plan.Bsp[BspLump.Faces]);
            for (int f = 0; f < roomFaces.Length; f++)
            {
                faces.Add(ShiftFace(plan, roomFaces[f], plan.StrippedFaces.Contains(f), NoDraw, original: false));
            }

            lighting.AddRange(plan.Bsp[BspLump.Lighting].Data.Span);
        }

        List<DFace> origFaces = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DFace> roomFaces = BspStructView.As<DFace>(plan.Bsp[BspLump.OriginalFaces]);
            for (int f = 0; f < roomFaces.Length; f++)
            {
                // A stripped plug's original face takes the texinfo of a drawn
                // face cut from it, so its nodraw copy is made from a texinfo
                // that exists: its own is not the compile's (MarkPlugOriginalFaces).
                // Every other original face's texinfo is relocated as it came.
                DFace face = roomFaces[f];
                bool stripped = plan.StrippedOrigFaces.TryGetValue(f, out short drawnTexInfo);
                if (stripped)
                {
                    face.TexInfo = drawnTexInfo;
                }

                origFaces.Add(ShiftFace(plan, face, stripped, NoDraw, original: true));
            }
        }

        List<DEdge> edges = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DEdge edge in BspStructView.As<DEdge>(plan.Bsp[BspLump.Edges]))
            {
                DEdge shifted = edge;
                shifted.V[0] = (ushort)(edge.V[0] + plan.VertexBase);
                shifted.V[1] = (ushort)(edge.V[1] + plan.VertexBase);
                edges.Add(shifted);
            }
        }

        // Surfedges: the signed edge indices the faces' runs point into.
        List<int> surfEdges = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (int se in BspStructView.As<int>(plan.Bsp[BspLump.SurfEdges]))
            {
                surfEdges.Add(se >= 0 ? se + plan.EdgeBase : -(-se + plan.EdgeBase));
            }
        }

        List<DBrush> brushes = [];
        List<DBrushSide> brushSides = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DBrushSide side in BspStructView.As<DBrushSide>(plan.Bsp[BspLump.BrushSides]))
            {
                DBrushSide shifted = side;
                shifted.PlaneNum = (ushort)plan.PlaneRef(side.PlaneNum);
                if (side.TexInfo >= 0)
                {
                    shifted.TexInfo = (short)(side.TexInfo + plan.TexInfoBase);
                }

                brushSides.Add(shifted);
            }

            ReadOnlySpan<DBrush> roomBrushes = BspStructView.As<DBrush>(plan.Bsp[BspLump.Brushes]);
            for (int b = 0; b < roomBrushes.Length; b++)
            {
                DBrush shifted = roomBrushes[b];
                shifted.FirstSide += plan.BrushSideBase;

                // Unreachable from any leaf now, and empty for any tool that
                // walks the brush lump itself: the doorway holds nothing.
                if (plan.StrippedBrushes.Contains(b))
                {
                    shifted.Contents = 0;
                }

                brushes.Add(shifted);
            }
        }

        List<DTexData> texDatas = [];
        List<int> stringTable = [];
        List<byte> stringData = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DTexData data in BspStructView.As<DTexData>(plan.Bsp[BspLump.TexData]))
            {
                DTexData shifted = data;
                shifted.NameStringTableId += plan.StringTableBase;
                texDatas.Add(shifted);
            }

            // The table's entries are int byte offsets into the room's OWN
            // string data; the data is concatenated too, so every entry
            // shifts by the preceding rooms' bytes.
            foreach (int entry in BspStructView.As<int>(plan.Bsp[BspLump.TexDataStringTable]))
            {
                stringTable.Add(entry + plan.StringDataBase);
            }

            stringData.AddRange(plan.Bsp[BspLump.TexDataStringData].Data.Span);
        }

        // Face ids and macro textures: ids are opaque, macro names index the
        // shared string table (0xFFFF is "none" and stays 0xFFFF).
        List<DFaceId> faceIds = [];
        List<FaceMacroTextureInfo> macroTextures = [];
        foreach (RoomPlan plan in plans)
        {
            faceIds.AddRange(BspStructView.As<DFaceId>(plan.Bsp[BspLump.FaceIds]));
            foreach (FaceMacroTextureInfo macro in BspStructView.As<FaceMacroTextureInfo>(plan.Bsp[BspLump.FaceMacroTextureInfo]))
            {
                FaceMacroTextureInfo shifted = macro;
                if (macro.MacroTextureNameId != 0xFFFF)
                {
                    shifted.MacroTextureNameId = (ushort)(macro.MacroTextureNameId + plan.StringTableBase);
                }

                macroTextures.Add(shifted);
            }
        }

        // Vert normals: phong normals rotate with the room but never
        // translate, so the turned normals are already final.
        List<Vec3> vertNormals = [];
        List<ushort> vertNormalIndices = [];
        foreach (RoomPlan plan in plans)
        {
            vertNormals.AddRange(plan.Geometry.VertNormals);

            foreach (ushort index in BspStructView.As<ushort>(plan.Bsp[BspLump.VertNormalIndices]))
            {
                vertNormalIndices.Add((ushort)(index + plan.VertNormalBase));
            }
        }

        // Primitives: the ranges shift; the indices do not — they number the
        // face's own vertices (or the primitive's own), never the lump — and
        // the primitive vertices are world geometry, moved.
        List<DPrimitive> prims = [];
        List<ushort> primIndices = [];
        List<Vec3> primVerts = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DPrimitive prim in BspStructView.As<DPrimitive>(plan.Bsp[BspLump.Primitives]))
            {
                DPrimitive shifted = prim;
                shifted.FirstIndex = (ushort)(prim.FirstIndex + plan.PrimIndexBase);
                shifted.FirstVert = (ushort)(prim.FirstVert + plan.PrimVertBase);
                prims.Add(shifted);
            }

            primIndices.AddRange(BspStructView.As<ushort>(plan.Bsp[BspLump.PrimIndices]));
            foreach (Vec3 vertex in plan.Geometry.PrimVerts)
            {
                primVerts.Add(plan.Transform.Translate(vertex));
            }
        }

        // Areas: one open area. Every room is areas {0, 1} with no portal
        // (PlanRoom refused anything else), so the room-local area numbers
        // are already the level's: 0 for the void, 1 for every room's open
        // space. The room with the most areas supplies the two lumps.
        RoomPlan areaSource = plans.MaxBy(p => BspStructView.Count<DArea>(p.Bsp[BspLump.Areas]))!;

        // Occlusion: rebased polygons and vertex indices, moved boxes.
        OcclusionLump occlusion = new();
        foreach (RoomPlan plan in plans)
        {
            if (plan.Occlusion is not { } roomOcclusion)
            {
                continue;
            }

            for (int o = 0; o < roomOcclusion.Occluders.Count; o++)
            {
                DOccluderData shifted = roomOcclusion.Occluders[o];
                shifted.FirstPoly += plan.OccluderPolyBase;
                Box box = plan.Transform.TranslateBox(plan.Geometry.OccluderBoxes[o]);
                shifted.Mins = box.Mins;
                shifted.Maxs = box.Maxs;
                occlusion.Occluders.Add(shifted);
            }

            foreach (DOccluderPolyData poly in roomOcclusion.Polys)
            {
                DOccluderPolyData shifted = poly;
                shifted.FirstVertexIndex += plan.OccluderVertexBase;
                shifted.PlaneNum = plan.PlaneRef(poly.PlaneNum);
                occlusion.Polys.Add(shifted);
            }

            foreach (int vertexIndex in roomOcclusion.VertexIndices)
            {
                occlusion.VertexIndices.Add(vertexIndex + plan.VertexBase);
            }
        }

        // Models: one merged world model hanging on the top root, bounded by
        // the rooms' own world bounds as placed.
        Box world = first.Transform.TranslateBox(first.Geometry.ModelBox);
        foreach (RoomPlan plan in plans)
        {
            world = Union(world, plan.Transform.TranslateBox(plan.Geometry.ModelBox));
        }

        DModel[] models =
        [
            new DModel
            {
                Mins = world.Mins,
                Maxs = world.Maxs,
                Origin = Vec3.Zero,
                HeadNode = 0,
                FirstFace = 0,
                NumFaces = faces.Count,
            },
        ];

        (byte[]? physCollide, byte[]? physDisp) = MergeCollision(plans, context.Options.Compliance, cancellationToken);

        BspData linked = new() { FileVersion = first.Bsp.FileVersion };
        linked[BspLump.Entities] = MergeEntities(plans, classes, naming, mapVersion);
        linked.SetLump(BspLump.Planes, Bytes(planes));
        linked.SetLump(BspLump.TexData, Bytes(texDatas));
        linked.SetLump(BspLump.Vertexes, plans.SelectMany(p => MemoryMarshal.AsBytes(p.Vertices.AsSpan()).ToArray()).ToArray());
        linked.SetLump(BspLump.Visibility, visibilityLump);
        linked.SetLump(BspLump.Nodes, Bytes(nodes));
        linked.SetLump(BspLump.TexInfo, Bytes(texInfos));
        linked.SetLump(BspLump.Faces, Bytes(faces), first.FacesVersion);
        linked.SetLump(BspLump.Lighting, lighting.ToArray());
        linked.SetLump(BspLump.Leafs, Bytes(leafs), first.LeafsVersion);
        linked.SetLump(BspLump.Edges, Bytes(edges));
        linked.SetLump(BspLump.Models, MemoryMarshal.AsBytes(models.AsSpan()).ToArray());
        linked.SetLump(BspLump.LeafFaces, Bytes(leafFaces));
        linked.SetLump(BspLump.LeafBrushes, Bytes(leafBrushes));
        linked.SetLump(BspLump.Brushes, Bytes(brushes));
        linked.SetLump(BspLump.BrushSides, Bytes(brushSides));
        linked.SetLump(BspLump.TexDataStringData, stringData.ToArray());
        linked.SetLump(BspLump.TexDataStringTable, Bytes(stringTable));
        linked.SetLump(BspLump.SurfEdges, Bytes(surfEdges));
        linked.SetLump(BspLump.FaceIds, Bytes(faceIds));
        linked.SetLump(BspLump.FaceMacroTextureInfo, Bytes(macroTextures));
        linked.SetLump(BspLump.OriginalFaces, Bytes(origFaces));
        linked.SetLump(BspLump.VertNormals, Bytes(vertNormals));
        linked.SetLump(BspLump.VertNormalIndices, Bytes(vertNormalIndices));
        linked.SetLump(BspLump.Primitives, Bytes(prims));
        linked.SetLump(BspLump.PrimIndices, Bytes(primIndices));
        linked.SetLump(BspLump.PrimVerts, Bytes(primVerts));
        linked[BspLump.Areas] = areaSource.Bsp[BspLump.Areas];
        linked[BspLump.AreaPortals] = areaSource.Bsp[BspLump.AreaPortals];
        linked[BspLump.Occlusion] = occlusion.Write();
        linked[BspLump.PakFile] = first.Bsp[BspLump.PakFile];
        linked[BspLump.MapFlags] = first.Bsp[BspLump.MapFlags];
        foreach (GameLumpEntry entry in first.Bsp.GameLumps)
        {
            linked.GameLumps.Add(entry);
        }

        if (leafMinDist is not null)
        {
            linked.SetLump(BspLump.LeafMinDistToWater, Bytes(leafMinDist));
        }

        if (physCollide is not null)
        {
            linked.SetLump(BspLump.PhysCollide, physCollide);
        }

        if (physDisp is not null)
        {
            linked.SetLump(BspLump.PhysDisp, physDisp);
        }

        return linked;
    }

    /// <summary>One drawn or original face through its room's bases.</summary>
    /// <remarks>
    /// <c>FirstEdge</c> is an index into the SURFEDGE lump, so it shifts by
    /// the surfedge base (the edge base is for what the surfedges hold).
    /// <c>LightOfs</c> is a byte offset in which 0 is the first luxel and -1
    /// is "unlit"; only the latter is kept as is.
    /// </remarks>
    private static DFace ShiftFace(RoomPlan plan, DFace face, bool stripped, Func<RoomPlan, int, int> noDraw, bool original)
    {
        DFace shifted = face;
        shifted.PlaneNum = (ushort)plan.PlaneRef(face.PlaneNum);
        if (face.TexInfo >= 0)
        {
            int texInfo = face.TexInfo + plan.TexInfoBase;
            shifted.TexInfo = (short)(stripped ? noDraw(plan, texInfo) : texInfo);
        }

        shifted.FirstEdge = face.FirstEdge + plan.SurfEdgeBase;
        shifted.LightOfs = face.LightOfs < 0 ? -1 : face.LightOfs + plan.LightBase;
        if (original)
        {
            shifted.OrigFace = -1;
            return shifted;
        }

        shifted.OrigFace = face.OrigFace >= 0 ? face.OrigFace + plan.OrigFaceBase : face.OrigFace;
        if (face.GetNumPrims() > 0)
        {
            shifted.FirstPrimId = (ushort)(face.FirstPrimId + plan.PrimBase);
        }

        return shifted;
    }

    /// <summary>
    /// Refuses rooms whose per-map lumps cannot be merged into one: the face
    /// and leaf layouts must agree (they are one lump each), and so must the
    /// map flags (one word for the whole map).
    /// </summary>
    private static void RequireAgreement(RoomPlan[] plans)
    {
        RoomPlan first = plans[0];
        foreach (RoomPlan plan in plans)
        {
            if (plan.FacesVersion != first.FacesVersion || plan.LeafsVersion != first.LeafsVersion)
            {
                throw new LinkException(
                    $"room {plan.Placement.Room.Definition.Name}'s Faces/Leafs lump versions "
                    + $"({plan.FacesVersion}/{plan.LeafsVersion}) disagree with "
                    + $"{first.Placement.Room.Definition.Name}'s ({first.FacesVersion}/{first.LeafsVersion})");
            }

            if (!plan.Bsp[BspLump.MapFlags].Data.Span.SequenceEqual(first.Bsp[BspLump.MapFlags].Data.Span))
            {
                throw new LinkException(
                    $"room {plan.Placement.Room.Definition.Name}'s map flags disagree with"
                    + $" {first.Placement.Room.Definition.Name}'s; the linked map has one set");
            }
        }
    }

    /// <summary>
    /// Cuts every jointed doorway of one room out of the solid leaves its
    /// plugs made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A leaf the plug shares with wall bands and void is replaced by a short
    /// chain of nodes on the planes of the doorway box (only the planes the
    /// leaf actually crosses): at each, the side outside the doorway becomes a
    /// copy of the solid leaf with its brush run, and the side inside goes on
    /// down the chain. What is left at the end is the doorway itself, and it
    /// reuses the original leaf's index: empty contents, no brushes, no faces,
    /// the facing cluster and the room's open area. Every node that named the
    /// leaf now names the chain's head.
    /// </para>
    /// <para>
    /// The planes are world-space axial planes with positive normals, added as
    /// pairs; the solid copies keep the full brush run, which is exact because
    /// brush tests are per brush, and each copy is carved again if another
    /// jointed plug of the room reaches into it.
    /// </para>
    /// </remarks>
    private static void CarvePlugs(
        RoomPlan plan, int roomNodeCount, List<DNode> nodes, List<DLeaf> leafs, List<DPlane> planes, List<ushort>? leafMinDist)
    {
        Dictionary<int, List<(Box, int)>> byLeaf = [];
        foreach (PlugCarve carve in plan.Carves)
        {
            Box plugWorld = plan.Transform.TranslateBox(plan.Geometry.PlugBoxes[carve.Socket]);
            if (!byLeaf.TryGetValue(carve.Leaf, out List<(Box, int)>? list))
            {
                byLeaf[carve.Leaf] = list = [];
            }

            list.Add((plugWorld, carve.Cluster));
        }

        foreach ((int roomLeaf, List<(Box, int)> leafPlugs) in byLeaf.OrderBy(kv => kv.Key))
        {
            int linkedLeaf = plan.LeafBase + roomLeaf;
            int head = CarveLeaf(plan.ClusterBase, plan.Leafs, linkedLeaf, leafPlugs, nodes, leafs, planes, leafMinDist);
            if (head == -(linkedLeaf + 1))
            {
                continue;
            }

            Span<DNode> roomNodes = CollectionsMarshal.AsSpan(nodes).Slice(plan.NodeBase, roomNodeCount);
            for (int n = 0; n < roomNodes.Length; n++)
            {
                IntArray2 children = roomNodes[n].Children;
                for (int side = 0; side < 2; side++)
                {
                    if (children[side] == -(linkedLeaf + 1))
                    {
                        children[side] = head;
                    }
                }

                roomNodes[n].Children = children;
            }
        }
    }

    /// <summary>Splits one solid leaf around the first plug that reaches into it.</summary>
    /// <param name="clusterBase">The room's first linked cluster.</param>
    /// <param name="roomLeafs">The room's own leaves, where the doorway's open area is looked up.</param>
    /// <param name="linkedLeaf">The linked index of the leaf to carve.</param>
    /// <param name="plugs">The world-space plug boxes that may reach into it, and their room-local clusters.</param>
    /// <param name="nodes">The linked nodes; the chain is appended.</param>
    /// <param name="leafs">The linked leaves; the solid fragments are appended.</param>
    /// <param name="planes">The linked planes; the chain's planes are appended as pairs.</param>
    /// <param name="leafMinDist">The per-leaf water distances, extended for every fragment, or null.</param>
    /// <returns>The child reference that replaces the leaf: a node index, or the leaf itself.</returns>
    internal static int CarveLeaf(
        int clusterBase,
        DLeaf[] roomLeafs,
        int linkedLeaf,
        List<(Box Plug, int Cluster)> plugs,
        List<DNode> nodes,
        List<DLeaf> leafs,
        List<DPlane> planes,
        List<ushort>? leafMinDist)
    {
        DLeaf template = leafs[linkedLeaf];
        Box current = BoxOf(template);
        int hit = plugs.FindIndex(p => current.Overlaps(p.Plug, RoomLinter.CellEpsilon));
        if (hit < 0)
        {
            return -(linkedLeaf + 1);
        }

        (Box plug, int cluster) = plugs[hit];
        Box door = new(
            new Vec3(Math.Max(current.Mins.X, plug.Mins.X), Math.Max(current.Mins.Y, plug.Mins.Y), Math.Max(current.Mins.Z, plug.Mins.Z)),
            new Vec3(Math.Min(current.Maxs.X, plug.Maxs.X), Math.Min(current.Maxs.Y, plug.Maxs.Y), Math.Min(current.Maxs.Z, plug.Maxs.Z)));

        int head = -(linkedLeaf + 1);
        int pendingNode = -1;
        int pendingSide = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            for (int bound = 0; bound < 2; bound++)
            {
                // bound 0: the doorway's low face, inside is the front;
                // bound 1: its high face, inside is the back.
                float at = Component(bound == 0 ? door.Mins : door.Maxs, axis);
                bool crosses = bound == 0
                    ? Component(current.Mins, axis) < at - RoomLinter.CellEpsilon
                    : Component(current.Maxs, axis) > at + RoomLinter.CellEpsilon;
                if (!crosses)
                {
                    continue;
                }

                Box outside = bound == 0 ? WithMax(current, axis, at) : WithMin(current, axis, at);
                Box inside = bound == 0 ? WithMin(current, axis, at) : WithMax(current, axis, at);

                int fragment = leafs.Count;
                DLeaf copy = template;
                copy.Mins = Short3(outside.Mins);
                copy.Maxs = Short3(outside.Maxs);
                leafs.Add(copy);
                leafMinDist?.Add(leafMinDist[linkedLeaf]);
                List<(Box, int)> others = [.. plugs.Where((_, i) => i != hit)];
                int fragmentRef = CarveLeaf(clusterBase, roomLeafs, fragment, others, nodes, leafs, planes, leafMinDist);

                int node = nodes.Count;
                IntArray2 children = default;
                children[bound == 0 ? 1 : 0] = fragmentRef;
                children[bound == 0 ? 0 : 1] = -(linkedLeaf + 1); // filled by the next step or left as the doorway
                nodes.Add(new DNode
                {
                    PlaneNum = AddPlanePair(planes, Axis(axis), at),
                    Children = children,
                    Mins = Short3(current.Mins),
                    Maxs = Short3(current.Maxs),
                    Area = -1,
                });

                if (pendingNode < 0)
                {
                    head = node;
                }
                else
                {
                    DNode parent = nodes[pendingNode];
                    IntArray2 parentChildren = parent.Children;
                    parentChildren[pendingSide] = node;
                    parent.Children = parentChildren;
                    nodes[pendingNode] = parent;
                }

                pendingNode = node;
                pendingSide = bound == 0 ? 0 : 1;
                current = inside;
            }
        }

        // What is left is the doorway: the original leaf's index, empty.
        DLeaf doorway = template;
        doorway.Contents = 0;
        doorway.Cluster = (short)(clusterBase + cluster);
        doorway.SetAreaFlags(OpenArea(roomLeafs, cluster), template.GetFlags());
        doorway.FirstLeafBrush = 0;
        doorway.NumLeafBrushes = 0;
        doorway.FirstLeafFace = 0;
        doorway.NumLeafFaces = 0;
        doorway.Mins = Short3(door.Mins);
        doorway.Maxs = Short3(door.Maxs);
        leafs[linkedLeaf] = doorway;
        return head;
    }

    /// <summary>The area of the room's open leaves in a cluster: where the doorway's space belongs.</summary>
    private static int OpenArea(DLeaf[] roomLeafs, int cluster)
    {
        foreach (DLeaf leaf in roomLeafs)
        {
            if (leaf.Cluster == cluster && (leaf.Contents & (int)BrushContents.Solid) == 0)
            {
                return leaf.GetArea();
            }
        }

        return 0;
    }

    /// <summary>Appends a plane and its flip; returns the even index.</summary>
    private static int AddPlanePair(List<DPlane> planes, Vec3 normal, float dist)
    {
        int type = (int)new Plane(normal, dist).Type;
        int index = planes.Count;
        planes.Add(new DPlane { Normal = normal, Dist = dist, Type = type });
        planes.Add(new DPlane { Normal = -normal, Dist = -dist, Type = type });
        return index;
    }

    private static Vec3 Axis(int axis) => axis switch
    {
        0 => new Vec3(1, 0, 0),
        1 => new Vec3(0, 1, 0),
        _ => new Vec3(0, 0, 1),
    };

    private static float Component(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static Vec3 With(Vec3 v, int axis, float value) => axis switch
    {
        0 => new Vec3(value, v.Y, v.Z),
        1 => new Vec3(v.X, value, v.Z),
        _ => new Vec3(v.X, v.Y, value),
    };

    private static Box WithMin(Box box, int axis, float value) => new(With(box.Mins, axis, value), box.Maxs);

    private static Box WithMax(Box box, int axis, float value) => new(box.Mins, With(box.Maxs, axis, value));

    private static Box Union(Box a, Box b) =>
        new(
            new Vec3(Math.Min(a.Mins.X, b.Mins.X), Math.Min(a.Mins.Y, b.Mins.Y), Math.Min(a.Mins.Z, b.Mins.Z)),
            new Vec3(Math.Max(a.Maxs.X, b.Maxs.X), Math.Max(a.Maxs.Y, b.Maxs.Y), Math.Max(a.Maxs.Z, b.Maxs.Z)));

    private static Vec3 ToVec(ShortArray3 s) => new(s[0], s[1], s[2]);

    private static byte[] Bytes<T>(List<T> items)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(items)).ToArray();
}
