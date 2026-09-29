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
    /// plugs stripped (their brushes dropped, their faces drawn nodraw), and
    /// the lumps that exist once per map merged: <paramref name="mergedPak"/>
    /// is the rooms' packed files (<see cref="LevelPakFiles"/>'s merge), or
    /// null when no room packs one; <paramref name="cubemaps"/> the level's
    /// cubemap samples and each placement's renamed patch strings, or null
    /// when no room has a sample; <paramref name="areas"/> the level's areas
    /// (<see cref="PlanAreas"/>), or null when no room has an area portal,
    /// and <paramref name="areaWarnings"/> where a portal that seals nothing
    /// once linked is reported.
    /// </summary>
    /// <returns>The map, and how many brushes the fold removed (0 when it did not run).</returns>
    private static (BspData Map, int FoldedBrushes) Assemble(
        RoomPlan[] plans,
        LevelLayout layout,
        byte[] visibilityLump,
        VbspContext context,
        EntityClassTable classes,
        LevelNaming naming,
        LevelSingletons singletons,
        string? mapVersion,
        bool foldBrushes,
        byte[]? mergedPak,
        LevelCubemaps? cubemaps,
        List<(int Placement, string ClassName)> droppedFurniture,
        LevelLightStyles styles,
        List<(int Leaf, int Placement, int Cluster)> doorways,
        LevelAreas? areas,
        List<string> areaWarnings,
        CancellationToken cancellationToken)
    {
        float cell = layout.CellSize;
        RoomPlan first = plans[0];
        RequireAgreement(plans);

        // The shared tables: [0],[1] the null pair, then every room's moved
        // planes, strings, texdatas and texinfos in layout order, each found
        // by content and added only when no earlier room brought it; then
        // the top tree's cell-face planes; the carve planes and the nodraw
        // copies last, as they are made.
        LinkPlanes planes = new();
        LinkTextures textures = new();
        for (int p = 0; p < plans.Length; p++)
        {
            InternRoomTables(plans[p], planes, textures, cubemaps?.At(p));
        }

        List<Plane> topPlanes = [];
        List<DNode> top = BuildTopNodes(layout, cell, topPlanes);
        for (int i = 0; i < top.Count; i++)
        {
            // BuildTopNodes numbered its planes as pairs from 0; each takes
            // its shared pair's index now.
            DNode node = top[i];
            Plane split = topPlanes[node.PlaneNum / 2];
            (int even, bool flipped) = planes.Intern(split.Normal, split.Dist);
            node.PlaneNum = even;
            node.Children = Orient(node.Children, flipped);
            top[i] = node;
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
            roomNodeCounts[r] = plan.KeptNodeCount;
            RoomModelLayout? models = plan.Models;
            for (int i = 0; i < roomNodes.Length; i++)
            {
                // A brush model's node: skipped when the level omits the
                // model; in its entity's own frame when origin-relative.
                RoomBrushModel? owner = models?.Owner(m => m.Nodes, i);
                if (owner is not null && models!.Linked[owner.Model - 1] < 0)
                {
                    continue;
                }

                bool local = owner is { OriginRelative: true };
                DNode n = roomNodes[i];
                DNode shifted = n;
                (int planeNum, bool flip) = plan.NodePlane(n.PlaneNum, local);
                shifted.PlaneNum = planeNum;
                IntArray2 children = default;
                for (int side = 0; side < 2; side++)
                {
                    children[side ^ (flip ? 1 : 0)] = plan.LinkedChild(n.Children[side]);
                }

                shifted.Children = children;
                shifted.FirstFace = (ushort)plan.LinkedFace(n.FirstFace, owner);
                if (n.Area > 0)
                {
                    // A node wholly in one of the room's areas is in the
                    // level area that one became; a mixed node (-1) stays
                    // mixed, and one the flood never set stays 0.
                    shifted.Area = (short)LevelArea(plan, n.Area);
                }

                Box box = local ? plan.Geometry.NodeBoxes[i] : plan.Transform.TranslateBox(plan.Geometry.NodeBoxes[i]);
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
        // Linked brush indices, held as ints until the fold (which may
        // renumber them) is done; a leaf brush entry is a ushort in the lump.
        List<int> leafBrushes = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<ushort> roomLeafBrushes = BspStructView.As<ushort>(plan.Bsp[BspLump.LeafBrushes]);
            RoomModelLayout? models = plan.Models;
            for (int l = 0; l < plan.Leafs.Length; l++)
            {
                RoomBrushModel? owner = models?.Owner(m => m.Leaves, l);
                if (owner is not null && models!.Linked[owner.Model - 1] < 0)
                {
                    continue;
                }

                DLeaf leaf = plan.Leafs[l];
                DLeaf shifted = leaf;
                if (shifted.Cluster >= 0)
                {
                    shifted.Cluster = (short)(leaf.Cluster + plan.ClusterBase);
                }

                shifted.FirstLeafFace = (ushort)plan.LinkedLeafFace(leaf.FirstLeafFace);
                if (plan.AreaMap is not null)
                {
                    shifted.SetAreaFlags(LevelArea(plan, leaf.GetArea()), leaf.GetFlags());
                }

                int start = leafBrushes.Count;
                for (int b = 0; b < leaf.NumLeafBrushes; b++)
                {
                    int brush = roomLeafBrushes[leaf.FirstLeafBrush + b];
                    if (!plan.StrippedBrushes.Contains(brush))
                    {
                        leafBrushes.Add(plan.LinkedBrush(brush));
                    }
                }

                Limit(plan, "leaf brushes", leafBrushes.Count, ushort.MaxValue + 1);
                shifted.FirstLeafBrush = (ushort)(leafBrushes.Count == start ? 0 : start);
                shifted.NumLeafBrushes = (ushort)(leafBrushes.Count - start);
                Box box = owner is { OriginRelative: true } ? plan.Geometry.LeafBoxes[l] : plan.Transform.TranslateBox(plan.Geometry.LeafBoxes[l]);
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

                ReadOnlySpan<ushort> roomDists = BspStructView.As<ushort>(dists);
                for (int l = 0; l < roomDists.Length; l++)
                {
                    if (plan.Models is not { } models || RoomModelLayout.Kept(models.OmittedLeaves, l) >= 0)
                    {
                        leafMinDist.Add(roomDists[l]);
                    }
                }
            }
        }

        // The doorways: every solid leaf a jointed plug made is split so the
        // plug's box inside it becomes an empty leaf of the facing cluster.
        for (int r = 0; r < plans.Length; r++)
        {
            CarvePlugs(plans[r], roomNodeCounts[r], nodes, leafs, planes, leafMinDist, doorways, r, plans[r].AreaMap);
        }

        Limit(plans[^1], "leaves", leafs.Count, ushort.MaxValue + 1);
        Limit(plans[^1], "planes", planes.Count, ushort.MaxValue + 1);
        cancellationToken.ThrowIfCancellationRequested();

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

        // Leaf faces: every kept leaf's run, each entry the linked face it
        // names; an omitted brush model's runs are left out.
        List<ushort> leafFaces = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<ushort> roomLeafFaces = BspStructView.As<ushort>(plan.Bsp[BspLump.LeafFaces]);
            for (int i = 0; i < roomLeafFaces.Length; i++)
            {
                if (plan.Models is not { } models || RoomModelLayout.Kept(models.OmittedLeafFaces, i) >= 0)
                {
                    leafFaces.Add((ushort)plan.LinkedFace(roomLeafFaces[i]));
                }
            }
        }

        // The rooms' texinfos are all in the shared table already, so the
        // nodraw copies of the plug faces' texinfos go after every room's.
        List<TexInfo> texInfos = textures.TexInfos;
        Dictionary<int, int> nodrawOf = [];
        int NoDraw(RoomPlan plan, int linkedTexInfo)
        {
            if (!nodrawOf.TryGetValue(linkedTexInfo, out int copy))
            {
                // A stripped plug's face stays in the face list (renumbering
                // faces would touch every node, leaf and primitive range), but
                // it is now a sheet of trigger material hanging in an open
                // doorway: drawn nodraw, it is gone for the renderer. The
                // copy is shared like any texinfo: every doorway of one
                // material in one alignment is one nodraw texinfo.
                TexInfo hidden = texInfos[linkedTexInfo];
                hidden.Flags |= (int)SurfaceFlags.NoDraw;
                copy = textures.InternTexInfo(hidden);
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

        // Drawn faces: every placement's world faces, then every kept brush
        // model's (the model lump's own ranges follow the world's). A face
        // keeps its lightmap offset, so the lighting is the rooms' bytes in
        // room order whatever the face order.
        List<DFace> faces = [];
        List<byte> lighting = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DFace> roomFaces = BspStructView.As<DFace>(plan.Bsp[BspLump.Faces]);
            for (int f = 0; f < plan.WorldFaceCount; f++)
            {
                faces.Add(ShiftFace(plan, roomFaces[f], plan.StrippedFaces.Contains(f), NoDraw, original: false));
            }

            lighting.AddRange(plan.Bsp[BspLump.Lighting].Data.Span);
        }

        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DFace> roomFaces = BspStructView.As<DFace>(plan.Bsp[BspLump.Faces]);
            foreach (RoomBrushModel model in KeptModels(plan))
            {
                for (int f = model.Faces.First; f < model.Faces.End; f++)
                {
                    faces.Add(ShiftFace(plan, roomFaces[f], stripped: false, NoDraw, original: false, model.OriginRelative));
                }
            }
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

                bool local = plan.Models is { } models && models.IsLocal(m => m.OrigFaces, f);
                origFaces.Add(ShiftFace(plan, face, stripped, NoDraw, original: true, local));
            }
        }

        // Edges: an origin-relative brush model's name the untranslated
        // copies of their vertices, after the room's own.
        List<DEdge> edges = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DEdge> roomEdges = BspStructView.As<DEdge>(plan.Bsp[BspLump.Edges]);
            for (int e = 0; e < roomEdges.Length; e++)
            {
                DEdge edge = roomEdges[e];
                DEdge shifted = edge;
                if (plan.Models is { } models && models.IsLocal(m => m.Edges, e))
                {
                    int copies = plan.VertexBase + plan.Vertices.Length;
                    shifted.V[0] = (ushort)(copies + models.LocalVertex[edge.V[0]]);
                    shifted.V[1] = (ushort)(copies + models.LocalVertex[edge.V[1]]);
                }
                else
                {
                    shifted.V[0] = (ushort)(edge.V[0] + plan.VertexBase);
                    shifted.V[1] = (ushort)(edge.V[1] + plan.VertexBase);
                }

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

        // Brushes: every room's in its own order, less the stripped plugs
        // and their sides (KeptBrushes), each kept brush's sides copied as
        // one run right after the previous brush's. A stripped plug is in no
        // leaf and has no ledge, so nothing that reads the map reaches it;
        // dropping it here is what keeps a large level under the loader's
        // brush cap.
        List<DBrush> brushes = [];
        List<DBrushSide> brushSides = [];
        List<bool> foldable = [];
        foreach (RoomPlan plan in plans)
        {
            ReadOnlySpan<DBrushSide> roomSides = BspStructView.As<DBrushSide>(plan.Bsp[BspLump.BrushSides]);
            ReadOnlySpan<DBrush> roomBrushes = BspStructView.As<DBrush>(plan.Bsp[BspLump.Brushes]);
            for (int b = 0; b < roomBrushes.Length; b++)
            {
                if (plan.BrushMap[b] < 0)
                {
                    continue;
                }

                // A brush model's brushes are its entity's: never folded
                // into the world's boxes, and in the entity's own frame when
                // the model is origin-relative.
                RoomBrushModel? owner = plan.Models?.Owner(m => m.Brushes, b);
                bool local = owner is { OriginRelative: true };
                DBrush shifted = roomBrushes[b];
                shifted.FirstSide = brushSides.Count;
                foreach (DBrushSide side in roomSides.Slice(roomBrushes[b].FirstSide, roomBrushes[b].NumSides))
                {
                    DBrushSide moved = side;
                    moved.PlaneNum = (ushort)plan.PlaneRef(side.PlaneNum, local);
                    if (side.TexInfo >= 0)
                    {
                        moved.TexInfo = (short)plan.TexInfoRef(side.TexInfo, local);
                    }

                    brushSides.Add(moved);
                }

                brushes.Add(shifted);
                foldable.Add(owner is null);
            }
        }

        // The fold: touching box brushes of the world become one box
        // (LinkBrushFold); a brush entity's brushes are its model's and never
        // fold. Leaf brush runs and ledge client data follow the new
        // numbering.
        int[]? brushMap = null;
        int folded = 0;
        if (foldBrushes)
        {
            BrushFoldResult fold = LinkBrushFold.Fold(
                brushes, brushSides, planes.Planes, texInfos, [.. foldable]);
            brushes = [.. fold.Brushes];
            brushSides = [.. fold.Sides];
            brushMap = fold.Map;
            folded = fold.Removed;
            RemapLeafBrushes(leafs, leafBrushes, brushMap);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Exact totals: the capacity check counted the kept brushes before
        // any room was planned, but with the fold on it leaves the brush
        // caps to here, where the folded totals are known.
        LoaderLimit(
            plans[^1].Placement.Room.Definition.Name,
            plans[^1].Placement.Instance.Placement.CellX,
            plans[^1].Placement.Instance.Placement.CellY,
            "brushes",
            brushes.Count,
            BspLimits.Caps.First(c => c.Lump == BspLump.Brushes).Max,
            "MAX_MAP_BRUSHES");
        LoaderLimit(
            plans[^1].Placement.Room.Definition.Name,
            plans[^1].Placement.Instance.Placement.CellX,
            plans[^1].Placement.Instance.Placement.CellY,
            "brush sides",
            brushSides.Count,
            BspLimits.Caps.First(c => c.Lump == BspLump.BrushSides).Max,
            "MAX_MAP_BRUSHSIDES");

        // Face ids and macro textures: ids are opaque, macro names index the
        // shared string table (0xFFFF is "none" and stays 0xFFFF; so does a
        // name outside the room's table, which names nothing). Both are one
        // entry per drawn face, so they follow the faces' order: every world
        // run, then every kept brush model's.
        List<DFaceId> faceIds = [];
        List<FaceMacroTextureInfo> macroTextures = [];
        foreach (bool modelPass in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (RoomPlan plan in plans)
            {
                ReadOnlySpan<DFaceId> roomIds = BspStructView.As<DFaceId>(plan.Bsp[BspLump.FaceIds]);
                ReadOnlySpan<FaceMacroTextureInfo> roomMacros = BspStructView.As<FaceMacroTextureInfo>(plan.Bsp[BspLump.FaceMacroTextureInfo]);
                foreach (RoomRange run in FaceRuns(plan, modelPass))
                {
                    faceIds.AddRange(Clip(roomIds, run));
                    foreach (FaceMacroTextureInfo macro in Clip(roomMacros, run))
                    {
                        FaceMacroTextureInfo shifted = macro;
                        if (macro.MacroTextureNameId != 0xFFFF)
                        {
                            shifted.MacroTextureNameId = (ushort)Remap(plan.StringMap, macro.MacroTextureNameId);
                        }

                        macroTextures.Add(shifted);
                    }
                }
            }
        }

        // Vert normals: phong normals rotate with the room but never
        // translate, so the turned normals are already final. The engine
        // walks the vertex-normal indices in face order, one run per face,
        // so they follow the faces: every world's, then every kept brush
        // model's run.
        // A lit level's normals are its rooms' bakes', each distinct normal
        // once (InternNormals), since the rooms' own would pass the 16-bit
        // index a large level; an unlit level's are carried as they were.
        List<Vec3> vertNormals = [];
        List<ushort> vertNormalIndices = [];
        if (first.Lighting is not null)
        {
            vertNormals = InternNormals(plans);
        }
        else
        {
            foreach (RoomPlan plan in plans)
            {
                vertNormals.AddRange(plan.VertNormals);
            }
        }

        foreach (bool modelPass in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (RoomPlan plan in plans)
            {
                ReadOnlySpan<ushort> roomIndices = BspStructView.As<ushort>(plan.Bsp[BspLump.VertNormalIndices]);
                foreach (RoomRange run in VertNormalRuns(plan, modelPass, roomIndices.Length))
                {
                    foreach (ushort index in roomIndices.Slice(run.First, run.Count))
                    {
                        vertNormalIndices.Add((ushort)(plan.NormalMap is { } map ? map[index] : index + plan.VertNormalBase));
                    }
                }
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
            for (int v = 0; v < plan.Geometry.PrimVerts.Length; v++)
            {
                Vec3 vertex = plan.Geometry.PrimVerts[v];
                primVerts.Add(plan.Models is { } models && models.LocalPrimVerts[v] ? vertex : plan.Transform.Translate(vertex));
            }
        }

        // Areas: without an area portal in any room, one open area. Every
        // room is then areas {0, 1} with no portal (a room with more carries
        // its portal data, and the level's areas are planned), so the
        // room-local area numbers are already the level's: 0 for the void, 1
        // for every room's open space. The room with the most areas supplies
        // the two lumps. With portals, the lumps are written from the plan
        // (WriteAreas) once the planes are interned.
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
                if (shifted.Area > 0)
                {
                    shifted.Area = LevelArea(plan, shifted.Area);
                }

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

        // Then every kept brush model, in link order: its tree's head, its
        // faces' linked range, its bounds turned and, for a
        // world-coordinate model, moved (an origin-relative model's bounds
        // are its entity's own frame, and the entity's origin places it).
        List<DModel> modelLump =
        [
            new DModel
            {
                Mins = world.Mins,
                Maxs = world.Maxs,
                Origin = Vec3.Zero,
                HeadNode = 0,
                FirstFace = 0,
                NumFaces = plans.Sum(p => p.WorldFaceCount),
            },
        ];
        foreach (RoomPlan plan in plans)
        {
            foreach (RoomBrushModel model in KeptModels(plan))
            {
                DModel room = BspStructView.As<DModel>(plan.Bsp[BspLump.Models])[model.Model];
                Box bounds = plan.Models!.Turn[model.Model - 1].Bounds;
                if (!model.OriginRelative)
                {
                    bounds = plan.Transform.TranslateBox(bounds);
                }

                modelLump.Add(new DModel
                {
                    Mins = bounds.Mins,
                    Maxs = bounds.Maxs,
                    Origin = Vec3.Zero,
                    HeadNode = plan.LinkedChild(room.HeadNode),
                    FirstFace = plan.LinkedFace(model.Faces.First, model),
                    NumFaces = model.Faces.Count,
                });
            }
        }

        (byte[]? physCollide, byte[]? physDisp) = MergeCollision(plans, context.Options.Compliance, brushMap, cancellationToken);

        BspData linked = new() { FileVersion = first.Bsp.FileVersion };
        linked[BspLump.Entities] = MergeEntities(plans, classes, naming, mapVersion, singletons, droppedFurniture, styles);
        linked.SetLump(BspLump.Planes, Bytes(planes.Planes));
        linked.SetLump(BspLump.TexData, Bytes(textures.TexDatas));
        linked.SetLump(
            BspLump.Vertexes,
            plans.SelectMany(p => MemoryMarshal.AsBytes(p.Vertices.AsSpan()).ToArray()
                .Concat(MemoryMarshal.AsBytes((p.Models?.LocalVertices ?? []).AsSpan()).ToArray())).ToArray());
        linked.SetLump(BspLump.Visibility, visibilityLump);
        linked.SetLump(BspLump.Nodes, Bytes(nodes));
        linked.SetLump(BspLump.TexInfo, Bytes(texInfos));
        linked.SetLump(BspLump.Faces, Bytes(faces), first.FacesVersion);
        linked.SetLump(BspLump.Lighting, lighting.ToArray());
        linked.SetLump(BspLump.Leafs, Bytes(leafs), first.LeafsVersion);
        linked.SetLump(BspLump.Edges, Bytes(edges));
        linked.SetLump(BspLump.Models, Bytes(modelLump));
        linked.SetLump(BspLump.LeafFaces, Bytes(leafFaces));
        linked.SetLump(BspLump.LeafBrushes, Bytes(leafBrushes.ConvertAll(b => (ushort)b)));
        linked.SetLump(BspLump.Brushes, Bytes(brushes));
        linked.SetLump(BspLump.BrushSides, Bytes(brushSides));
        linked.SetLump(BspLump.TexDataStringData, textures.StringData.ToArray());
        linked.SetLump(BspLump.TexDataStringTable, Bytes(textures.StringTable));
        linked.SetLump(BspLump.SurfEdges, Bytes(surfEdges));
        linked.SetLump(BspLump.FaceIds, Bytes(faceIds));
        linked.SetLump(BspLump.FaceMacroTextureInfo, Bytes(macroTextures));
        linked.SetLump(BspLump.OriginalFaces, Bytes(origFaces));
        linked.SetLump(BspLump.VertNormals, Bytes(vertNormals));
        linked.SetLump(BspLump.VertNormalIndices, Bytes(vertNormalIndices));
        linked.SetLump(BspLump.Primitives, Bytes(prims));
        linked.SetLump(BspLump.PrimIndices, Bytes(primIndices));
        linked.SetLump(BspLump.PrimVerts, Bytes(primVerts));
        if (areas is null)
        {
            linked[BspLump.Areas] = areaSource.Bsp[BspLump.Areas];
            linked[BspLump.AreaPortals] = areaSource.Bsp[BspLump.AreaPortals];
        }
        else
        {
            WriteAreas(linked, plans, areas, areaWarnings);
        }

        linked[BspLump.Occlusion] = occlusion.Write();
        // The rooms' files merged, or, when no room packs one, the first
        // room's empty pak as the link always wrote it.
        if (mergedPak is not null)
        {
            linked.SetLump(BspLump.PakFile, mergedPak);
        }
        else
        {
            linked[BspLump.PakFile] = first.Bsp[BspLump.PakFile];
        }
        // The rooms' cubemap samples at their linked positions, when any
        // room has some; a level without them writes no lump, as before.
        if (cubemaps is not null)
        {
            linked.SetLump(BspLump.Cubemaps, cubemaps.Lump());
        }

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

        // Overlays: every placement's, rebased and moved; a level whose rooms
        // have none carries neither lump, as before overlays were carried.
        if (LinkOverlays(plans) is { } overlays)
        {
            linked.SetLump(BspLump.Overlays, overlays.Overlays, overlays.Version);
            linked.SetLump(BspLump.OverlayFades, overlays.Fades);
        }

        return (linked, folded);
    }

    /// <summary>
    /// Renumbers every leaf's brush run through the fold's map, each run
    /// once however many leaves share it (a carve's solid fragments share
    /// their leaf's), dropping a repeat within a run: two constituents of one
    /// merged box in one leaf are one brush there now.
    /// </summary>
    /// <remarks>
    /// The runs keep their order, so the lump is the same list with its
    /// entries renamed and its repeats removed, and a leaf's run keeps the
    /// order its brushes had (the first constituent's place).
    /// </remarks>
    internal static void RemapLeafBrushes(List<DLeaf> leafs, List<int> leafBrushes, int[] map)
    {
        List<int> remapped = new(leafBrushes.Count);
        Dictionary<(int First, int Count), (int First, int Count)> runs = [];
        HashSet<int> inRun = [];
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.NumLeafBrushes == 0)
            {
                continue;
            }

            (int, int) old = (leaf.FirstLeafBrush, leaf.NumLeafBrushes);
            if (!runs.TryGetValue(old, out (int First, int Count) run))
            {
                int start = remapped.Count;
                inRun.Clear();
                for (int i = 0; i < leaf.NumLeafBrushes; i++)
                {
                    int brush = map[leafBrushes[leaf.FirstLeafBrush + i]];
                    if (inRun.Add(brush))
                    {
                        remapped.Add(brush);
                    }
                }

                run = (start, remapped.Count - start);
                runs[old] = run;
            }

            leaf.FirstLeafBrush = (ushort)run.First;
            leaf.NumLeafBrushes = (ushort)run.Count;
            leafs[l] = leaf;
        }

        leafBrushes.Clear();
        leafBrushes.AddRange(remapped);
    }

    /// <summary>One drawn or original face through its room's bases.</summary>
    /// <remarks>
    /// <c>FirstEdge</c> is an index into the SURFEDGE lump, so it shifts by
    /// the surfedge base (the edge base is for what the surfedges hold).
    /// <c>LightOfs</c> is a byte offset in which 0 is the first luxel and -1
    /// is "unlit"; only the latter is kept as is.
    /// </remarks>
    /// <param name="plan">The face's placement.</param>
    /// <param name="face">The room's face.</param>
    /// <param name="stripped">Whether it is a jointed plug's face, drawn nodraw.</param>
    /// <param name="noDraw">The nodraw copy of a linked texinfo.</param>
    /// <param name="original">Whether it is an original face (no primitives, no original face of its own).</param>
    /// <param name="local">Whether it is an origin-relative brush model's, in its entity's own frame.</param>
    private static DFace ShiftFace(RoomPlan plan, DFace face, bool stripped, Func<RoomPlan, int, int> noDraw, bool original, bool local = false)
    {
        DFace shifted = face;
        shifted.PlaneNum = (ushort)plan.PlaneRef(face.PlaneNum, local);
        if (face.TexInfo >= 0)
        {
            int texInfo = plan.TexInfoRef(face.TexInfo, local);
            shifted.TexInfo = (short)(stripped && texInfo >= 0 ? noDraw(plan, texInfo) : texInfo);
        }

        shifted.FirstEdge = face.FirstEdge + plan.SurfEdgeBase;
        // A lit room's bake lights its drawn faces only; its original faces
        // keep what its compile wrote, as vrad leaves a map's (their offsets
        // name nothing in the level's lightmaps, where the bake's blocks are).
        shifted.LightOfs = face.LightOfs < 0 ? -1 : face.LightOfs + (original && plan.Lighting is not null ? 0 : plan.LightBase);
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
    /// The planes are world-space axial planes with positive normals, shared
    /// with every other plane of the map (<see cref="LinkPlanes"/>); the solid copies keep the full brush run, which is exact because
    /// brush tests are per brush, and each copy is carved again if another
    /// jointed plug of the room reaches into it.
    /// </para>
    /// </remarks>
    private static void CarvePlugs(
        RoomPlan plan,
        int roomNodeCount,
        List<DNode> nodes,
        List<DLeaf> leafs,
        LinkPlanes planes,
        List<ushort>? leafMinDist,
        List<(int Leaf, int Placement, int Cluster)>? doorways = null,
        int placement = -1,
        int[]? areaMap = null)
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
            int linkedLeaf = plan.LinkedLeaf(roomLeaf);
            int head = CarveLeaf(plan.ClusterBase, plan.Leafs, linkedLeaf, leafPlugs, nodes, leafs, planes, leafMinDist, doorways, placement, areaMap);
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
    /// <param name="planes">The linked planes; the chain's planes join them as shared pairs.</param>
    /// <param name="leafMinDist">The per-leaf water distances, extended for every fragment, or null.</param>
    /// <param name="doorways">Receives each doorway leaf made, with <paramref name="placement"/> and the room cluster it joins; or null.</param>
    /// <param name="placement">The placement the leaf is of, for <paramref name="doorways"/>.</param>
    /// <param name="areaMap">Per room area, the level area it became (<see cref="PlanAreas"/>); null when the room's area numbers are the level's.</param>
    /// <returns>The child reference that replaces the leaf: a node index, or the leaf itself.</returns>
    internal static int CarveLeaf(
        int clusterBase,
        DLeaf[] roomLeafs,
        int linkedLeaf,
        List<(Box Plug, int Cluster)> plugs,
        List<DNode> nodes,
        List<DLeaf> leafs,
        LinkPlanes planes,
        List<ushort>? leafMinDist,
        List<(int Leaf, int Placement, int Cluster)>? doorways = null,
        int placement = -1,
        int[]? areaMap = null)
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
                int fragmentRef = CarveLeaf(clusterBase, roomLeafs, fragment, others, nodes, leafs, planes, leafMinDist, doorways, placement, areaMap);

                int node = nodes.Count;
                int inward = bound == 0 ? 0 : 1;
                IntArray2 children = default;
                children[1 - inward] = fragmentRef;
                children[inward] = -(linkedLeaf + 1); // filled by the next step or left as the doorway
                (int plane, bool flipped) = planes.Intern(Axis(axis), at);
                if (flipped)
                {
                    inward = 1 - inward;
                }

                nodes.Add(new DNode
                {
                    PlaneNum = plane,
                    Children = Orient(children, flipped),
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
                pendingSide = inward;
                current = inside;
            }
        }

        // What is left is the doorway: the original leaf's index, empty.
        DLeaf doorway = template;
        doorway.Contents = 0;
        doorway.Cluster = (short)(clusterBase + cluster);
        int area = OpenArea(roomLeafs, cluster);
        doorway.SetAreaFlags(areaMap is not null && area > 0 && area < areaMap.Length ? areaMap[area] : area, template.GetFlags());
        doorway.FirstLeafBrush = 0;
        doorway.NumLeafBrushes = 0;
        doorway.FirstLeafFace = 0;
        doorway.NumLeafFaces = 0;
        doorway.Mins = Short3(door.Mins);
        doorway.Maxs = Short3(door.Maxs);
        leafs[linkedLeaf] = doorway;

        // Recorded for what the doorway takes from its facing side (a lit
        // level's leaf ambient, the rooms design 9.4).
        doorways?.Add((linkedLeaf, placement, cluster));
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

    /// <summary>The brush models a placement keeps, in model order; none for a room with only the world.</summary>
    private static IEnumerable<RoomBrushModel> KeptModels(RoomPlan plan) =>
        plan.Models is { } models ? models.Source.Models.Where((_, i) => models.Linked[i] >= 0) : [];

    /// <summary>
    /// The runs of a placement's drawn faces one pass of the face-order
    /// lumps takes: its world faces, or its kept brush models' faces.
    /// </summary>
    /// <remarks>
    /// A room with only the world takes its lumps whole, as the link always
    /// did, whatever their length.
    /// </remarks>
    private static IEnumerable<RoomRange> FaceRuns(RoomPlan plan, bool models) =>
        models ? KeptModels(plan).Select(m => m.Faces)
        : plan.Models is null ? [new RoomRange(0, int.MaxValue)]
        : [new RoomRange(0, plan.WorldFaceCount)];

    /// <summary>
    /// The runs of a placement's vertex-normal indices one pass takes: the
    /// world's (every index before the first brush model's run, or all of
    /// them), or its kept brush models'. A room whose compile wrote none has
    /// none.
    /// </summary>
    private static IEnumerable<RoomRange> VertNormalRuns(RoomPlan plan, bool models, int length)
    {
        if (length == 0)
        {
            return [];
        }

        // A lit room's runs come from its faces: its compile, which the brush
        // models' runs were read from, had no normals.
        if (plan.FaceVertexStarts is { } starts)
        {
            return models
                ? [.. KeptModels(plan).Select(m => new RoomRange(starts[m.Faces.First], starts[m.Faces.End] - starts[m.Faces.First]))]
                : [new RoomRange(0, starts[plan.WorldFaceCount])];
        }

        if (models)
        {
            return KeptModels(plan).Select(m => m.VertNormalIndices);
        }

        int world = plan.Models is { } layout && layout.Source.Models.Count > 0
            ? layout.Source.Models.Min(m => m.VertNormalIndices.Count > 0 ? m.VertNormalIndices.First : length)
            : length;
        return [new RoomRange(0, world)];
    }

    /// <summary>The part of a per-face lump a run covers; nothing past the lump's end (a lump vbsp left short).</summary>
    private static T[] Clip<T>(ReadOnlySpan<T> items, RoomRange run)
    {
        int first = Math.Min(run.First, items.Length);
        return items.Slice(first, (int)Math.Min((long)run.Count, items.Length - first)).ToArray();
    }

    private static byte[] Bytes<T>(List<T> items)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(items)).ToArray();
}
