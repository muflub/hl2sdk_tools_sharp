using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Validation;

/// <content>
/// The index-bounds rules. Almost every one of these is a place where the
/// engine dereferences a number straight out of the file: some are guarded by a
/// <c>Host_Error</c> and some are not guarded at all, and the ones that are not
/// are the reason this instrument exists.
/// </content>
public static partial class BspValidator
{
    /// <summary>
    /// The fields of a leaf that the rules need, read at whichever stride the
    /// LEAFS lump version calls for.
    /// </summary>
    /// <remarks>
    /// <c>dleaf_t</c> is 32 bytes at lump version 1 and <c>dleaf_version_0_t</c>
 /// Is 56, and nothing but that
    /// version field says which. Reading the wrong one shifts every leaf after
    /// the first, so the choice is made once, here.
    /// </remarks>
    private readonly record struct LeafFields(
        int Contents,
        short Cluster,
        ushort FirstLeafFace,
        ushort NumLeafFaces,
        ushort FirstLeafBrush,
        ushort NumLeafBrushes);

    private static LeafFields[] ReadLeaves(BspData bsp)
    {
        BspLumpData lump = bsp[BspLump.Leafs];
        if (lump.IsEmpty || lump.Version is not (0 or 1))
        {
            return [];
        }

        if (lump.Version == 0)
        {
            ReadOnlySpan<DLeafVersion0> leaves = BspStructView.As<DLeafVersion0>(
                lump.Data.Span[..(lump.Length - (lump.Length % Size<DLeafVersion0>()))]);

            LeafFields[] result = new LeafFields[leaves.Length];
            for (int i = 0; i < leaves.Length; i++)
            {
                DLeafVersion0 leaf = leaves[i];
                result[i] = new LeafFields(
                    leaf.Contents,
                    leaf.Cluster,
                    leaf.FirstLeafFace,
                    leaf.NumLeafFaces,
                    leaf.FirstLeafBrush,
                    leaf.NumLeafBrushes);
            }

            return result;
        }

        ReadOnlySpan<DLeaf> current = BspStructView.As<DLeaf>(
            lump.Data.Span[..(lump.Length - (lump.Length % Size<DLeaf>()))]);

        LeafFields[] fields = new LeafFields[current.Length];
        for (int i = 0; i < current.Length; i++)
        {
            DLeaf leaf = current[i];
            fields[i] = new LeafFields(
                leaf.Contents,
                leaf.Cluster,
                leaf.FirstLeafFace,
                leaf.NumLeafFaces,
                leaf.FirstLeafBrush,
                leaf.NumLeafBrushes);
        }

        return fields;
    }

    private static int Size<T>()
        where T : unmanaged =>
        System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    /// <summary>
    /// A lump's bytes truncated to a whole number of <typeparamref name="T"/>,
    /// so that a lump which already failed
    /// <see cref="BspRuleCodes.LumpElementSize"/> can still be walked instead of
    /// stopping the whole report.
    /// </summary>
    private static ReadOnlySpan<T> View<T>(BspData bsp, BspLump lump)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = bsp[lump].Data.Span;
        return BspStructView.As<T>(bytes[..(bytes.Length - (bytes.Length % Size<T>()))]);
    }

    private static void CheckIndices(
        BspData bsp,
        Counts counts,
        Findings findings,
        CancellationToken cancellationToken)
    {
        LeafFields[] leaves = ReadLeaves(bsp);

        CheckFaceIndices(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckEdgeIndices(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckNodeIndices(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckLeafIndices(bsp, counts, leaves, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckBrushIndices(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckModelIndices(bsp, counts, findings);
        CheckTexDataIndices(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckOverlayIndices(bsp, counts, findings);
    }

    /// <summary>
    /// <see cref="BspRuleCodes.FaceTexInfo"/>,
    /// <see cref="BspRuleCodes.PlaneIndex"/> for faces, and
    /// <see cref="BspRuleCodes.LeafFaceSurface"/>.
    /// </summary>
    private static void CheckFaceIndices(BspData bsp, Counts counts, Findings findings)
    {
        foreach ((BspLump lump, int count) in
            new[] { (BspLump.Faces, counts.Faces), (BspLump.FacesHdr, counts.FacesHdr) })
        {
            if (count == 0)
            {
                continue;
            }

            ReadOnlySpan<DFace> faces = View<DFace>(bsp, lump);

            // -- "Mod_LoadFaces: bad texinfo
            // number". The one index bound in the face loader that IS guarded.
            int badTexInfo = 0;
            string firstTexInfo = string.Empty;

            // 1911 -- out2->plane = lh.GetMap()->planes + planenum, unguarded.
            int badPlane = 0;
            string firstPlane = string.Empty;

            for (int i = 0; i < faces.Length; i++)
            {
                DFace face = faces[i];
                if (face.TexInfo < 0 || face.TexInfo >= counts.TexInfo)
                {
                    badTexInfo++;
                    if (badTexInfo == 1)
                    {
                        firstTexInfo =
                            $"{lump} face {i} has texinfo {face.TexInfo}, outside the "
                            + $"{counts.TexInfo}-entry TexInfo lump";
                    }
                }

                if (face.PlaneNum >= counts.Planes)
                {
                    badPlane++;
                    if (badPlane == 1)
                    {
                        firstPlane =
                            $"{lump} face {i} has plane {face.PlaneNum}, outside the "
                            + $"{counts.Planes}-entry Planes lump";
                    }
                }
            }

            if (badTexInfo > 0)
            {
                findings.AddRepeated(BspRuleCodes.FaceTexInfo, firstTexInfo, badTexInfo);
            }

            if (badPlane > 0)
            {
                findings.AddRepeated(BspRuleCodes.PlaneIndex, firstPlane, badPlane);
            }
        }

        // -- "Mod_LoadMarksurfaces: bad
        // surface number". The entries are unsigned, so only the upper bound
        // exists.
        ReadOnlySpan<ushort> leafFaces = View<ushort>(bsp, BspLump.LeafFaces);
        int badLeafFace = 0;
        string firstLeafFace = string.Empty;
        for (int i = 0; i < leafFaces.Length; i++)
        {
            if (leafFaces[i] >= counts.Faces)
            {
                badLeafFace++;
                if (badLeafFace == 1)
                {
                    firstLeafFace =
                        $"leafface {i} names face {leafFaces[i]}, outside the "
                        + $"{counts.Faces}-entry Faces lump";
                }
            }
        }

        if (badLeafFace > 0)
        {
            findings.AddRepeated(BspRuleCodes.LeafFaceSurface, firstLeafFace, badLeafFace);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.SurfEdgeEdge"/> and
    /// <see cref="BspRuleCodes.EdgeVertex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither is guarded. is
    /// <c>out[i] = pedges[edge].v[index]</c> with <c>edge</c> the magnitude of
    /// the surfedge straight out of the file, and the vertex index it produces
    /// is used to subscript the vertex array with no test either. A map that
    /// breaks these does not get a message, it gets a read outside the lump.
    /// </para>
    /// <para>
    /// <b>Both are reachability-scoped, because that is the engine's scope.</b>
    /// The only two places that ever dereference an edge both go through a
    /// surfedge: for the render verts, and
    /// For a displacement's four corner points
    /// (<c>pSurfEdges[pFaces-&gt;firstedge+j]</c>, then
    /// <c>pVerts[pEdges[eIndex].v[...]]</c>). Nothing walks the Edges lump for
    /// its own sake — <c>Mod_LoadEdges</c> copies it whole and checks only the
    /// lump's modulus and count. So an edge no surfedge names is data no load
    /// path reads, and calling it out would be validating past the engine.
    /// That is not a hypothetical: the tool's own edge table carries such
    /// entries. Stock reserves edge 0 as the error slot and never emits it —
    /// <c>BeginBSPFile</c> sets <c>numedges = 1</c> because "edge 0 is unused
    /// Because 0 cannot be sign-inverted"
    /// — and an aggressive cull pass leaves removed edges behind with both
    /// endpoints at 0xffff, the sentinel the edge table uses for "no valid
    /// endpoint". A validator that checked dead edges would reject a map the
    /// tool itself built and the engine loads without complaint.
    /// </para>
    /// </remarks>
    private static void CheckEdgeIndices(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<int> surfEdges = View<int>(bsp, BspLump.SurfEdges);
        int badSurfEdge = 0;
        string firstSurfEdge = string.Empty;
        for (int i = 0; i < surfEdges.Length; i++)
        {
            int edge = Math.Abs(surfEdges[i]);
            if (edge >= counts.Edges)
            {
                badSurfEdge++;
                if (badSurfEdge == 1)
                {
                    firstSurfEdge =
                        $"surfedge {i} is {surfEdges[i]}, naming edge {edge} outside the "
                        + $"{counts.Edges}-entry Edges lump";
                }
            }
        }

        if (badSurfEdge > 0)
        {
            findings.AddRepeated(BspRuleCodes.SurfEdgeEdge, firstSurfEdge, badSurfEdge);
        }

        ReadOnlySpan<DEdge> edges = View<DEdge>(bsp, BspLump.Edges);

        // The reachability set is the edge magnitudes the surfedge lump
        // actually names; a value of 0 names nothing (it is the tool's "no
        // edge" placeholder), and an out-of-range magnitude is already the
        // SurfEdgeEdge finding above, not something to chase endpoints for.
        bool[] referenced = new bool[edges.Length];
        for (int i = 0; i < surfEdges.Length; i++)
        {
            int edge = Math.Abs(surfEdges[i]);
            if (edge is > 0 && edge < edges.Length)
            {
                referenced[edge] = true;
            }
        }

        int badVertex = 0;
        string firstVertex = string.Empty;
        for (int i = 0; i < edges.Length; i++)
        {
            if (!referenced[i])
            {
                continue;
            }

            DEdge edge = edges[i];
            for (int end = 0; end < 2; end++)
            {
                if (edge.V[end] >= counts.Vertexes)
                {
                    badVertex++;
                    if (badVertex == 1)
                    {
                        firstVertex =
                            $"edge {i} endpoint {end} is vertex {edge.V[end]}, outside the "
                            + $"{counts.Vertexes}-entry Vertexes lump";
                    }
                }
            }
        }

        if (badVertex > 0)
        {
            findings.AddRepeated(BspRuleCodes.EdgeVertex, firstVertex, badVertex);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.NodeChildren"/> and
    /// <see cref="BspRuleCodes.PlaneIndex"/> for nodes.
    /// </summary>
    /// <remarks>
    /// Tests only the SIGN of a child:
    /// non-negative is a node index, negative is the leaf <c>-1 - p</c>
    /// Neither branch is bounded, and the plane
 /// Is <c>planes + p</c> with no test at all.
    /// </remarks>
    private static void CheckNodeIndices(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<DNode> nodes = View<DNode>(bsp, BspLump.Nodes);

        int badChild = 0;
        string firstChild = string.Empty;
        int badPlane = 0;
        string firstPlane = string.Empty;

        for (int i = 0; i < nodes.Length; i++)
        {
            DNode node = nodes[i];

            if (node.PlaneNum < 0 || node.PlaneNum >= counts.Planes)
            {
                badPlane++;
                if (badPlane == 1)
                {
                    firstPlane =
                        $"node {i} has plane {node.PlaneNum}, outside the {counts.Planes}-entry "
                        + "Planes lump";
                }
            }

            for (int child = 0; child < 2; child++)
            {
                int value = node.Children[child];
                bool bad = value >= 0
                    ? value >= counts.Nodes
                    : (-1 - value) >= counts.Leafs;

                if (bad)
                {
                    badChild++;
                    if (badChild == 1)
                    {
                        firstChild = value >= 0
                            ? $"node {i} child {child} is node {value}, outside the "
                              + $"{counts.Nodes}-entry Nodes lump"
                            : $"node {i} child {child} is leaf {-1 - value}, outside the "
                              + $"{counts.Leafs}-entry Leafs lump";
                    }
                }
            }
        }

        if (badPlane > 0)
        {
            findings.AddRepeated(BspRuleCodes.PlaneIndex, firstPlane, badPlane);
        }

        if (badChild > 0)
        {
            findings.AddRepeated(BspRuleCodes.NodeChildren, firstChild, badChild);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.LeafCluster"/> and
    /// <see cref="BspRuleCodes.LeafRuns"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Does not bound a leaf's cluster; it
    /// GROWS the map's cluster count to fit it. The bound that matters is the
    /// Other way round: subscripts the
    /// visibility lump's own offset table by cluster, so a cluster the vis lump
    /// does not have a row for reads past that table.
    /// </para>
    /// <para>
    /// A map with no visibility lump has not been through vvis and is not
    /// checked -- that is a normal intermediate state, not a broken map.
    /// </para>
    /// </remarks>
    private static void CheckLeafIndices(
        BspData bsp,
        Counts counts,
        LeafFields[] leaves,
        Findings findings)
    {
        int numClusters = VisibilityClusterCount(bsp);

        int badCluster = 0;
        string firstCluster = string.Empty;
        int badRun = 0;
        string firstRun = string.Empty;

        for (int i = 0; i < leaves.Length; i++)
        {
            LeafFields leaf = leaves[i];

            if (numClusters >= 0 && leaf.Cluster >= numClusters)
            {
                badCluster++;
                if (badCluster == 1)
                {
                    firstCluster =
                        $"leaf {i} is in cluster {leaf.Cluster}, but the Visibility lump only "
                        + $"has rows for {numClusters} clusters";
                }
            }

            if (leaf.FirstLeafFace + leaf.NumLeafFaces > counts.LeafFaces)
            {
                badRun++;
                if (badRun == 1)
                {
                    firstRun =
                        $"leaf {i}'s leafface run is {leaf.FirstLeafFace}.."
                        + $"{leaf.FirstLeafFace + leaf.NumLeafFaces}, past the "
                        + $"{counts.LeafFaces}-entry LeafFaces lump";
                }
            }

            if (leaf.FirstLeafBrush + leaf.NumLeafBrushes > counts.LeafBrushes)
            {
                badRun++;
                if (badRun == 1)
                {
                    firstRun =
                        $"leaf {i}'s leafbrush run is {leaf.FirstLeafBrush}.."
                        + $"{leaf.FirstLeafBrush + leaf.NumLeafBrushes}, past the "
                        + $"{counts.LeafBrushes}-entry LeafBrushes lump";
                }
            }
        }

        if (badCluster > 0)
        {
            findings.AddRepeated(BspRuleCodes.LeafCluster, firstCluster, badCluster);
        }

        if (badRun > 0)
        {
            findings.AddRepeated(BspRuleCodes.LeafRuns, firstRun, badRun);
        }
    }

    /// <summary>
    /// The number of clusters the visibility lump has rows for, or -1 when
    /// there is no readable visibility lump to compare against.
    /// </summary>
    private static int VisibilityClusterCount(BspData bsp)
    {
        BspLumpData vis = bsp[BspLump.Visibility];
        if (vis.IsEmpty || vis.Length < sizeof(int))
        {
            return -1;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(vis.Data.Span);
    }

    /// <summary>
    /// <see cref="BspRuleCodes.BrushSideRun"/>,
    /// <see cref="BspRuleCodes.BrushSideTexInfo"/>,
    /// <see cref="BspRuleCodes.PlaneIndex"/> for brush sides, and the
    /// leafbrush bound.
    /// </summary>
    private static void CheckBrushIndices(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<DBrush> brushes = View<DBrush>(bsp, BspLump.Brushes);
        int badRun = 0;
        string firstRun = string.Empty;
        for (int i = 0; i < brushes.Length; i++)
        {
            DBrush brush = brushes[i];

            // -- the loader walks
            // in[firstbrushside + j] for j < numsides with nothing bounding it.
            if (brush.FirstSide < 0
                || brush.NumSides < 0
                || (long)brush.FirstSide + brush.NumSides > counts.BrushSides)
            {
                badRun++;
                if (badRun == 1)
                {
                    firstRun =
                        $"brush {i}'s side run is {brush.FirstSide}.."
                        + $"{(long)brush.FirstSide + brush.NumSides}, past the "
                        + $"{counts.BrushSides}-entry BrushSides lump";
                }
            }
        }

        if (badRun > 0)
        {
            findings.AddRepeated(BspRuleCodes.BrushSideRun, firstRun, badRun);
        }

        ReadOnlySpan<DBrushSide> sides = View<DBrushSide>(bsp, BspLump.BrushSides);
        int badTexInfo = 0;
        string firstTexInfo = string.Empty;
        int badPlane = 0;
        string firstPlane = string.Empty;

        for (int i = 0; i < sides.Length; i++)
        {
            DBrushSide side = sides[i];

            // -- "Bad brushside texinfo" on
            // t >= map_texinfo.Size(). A NEGATIVE texinfo is legal and becomes
 // SURFACE_INDEX_INVALID; the comment there says vbsp writes
            // -1 and wonders why, so -1 is not a defect.
            if (side.TexInfo >= counts.TexInfo)
            {
                badTexInfo++;
                if (badTexInfo == 1)
                {
                    firstTexInfo =
                        $"brush side {i} has texinfo {side.TexInfo}, outside the "
                        + $"{counts.TexInfo}-entry TexInfo lump";
                }
            }

            // 805 -- &map_planes[pInputSide->planenum], unguarded.
            if (side.PlaneNum >= counts.Planes)
            {
                badPlane++;
                if (badPlane == 1)
                {
                    firstPlane =
                        $"brush side {i} has plane {side.PlaneNum}, outside the "
                        + $"{counts.Planes}-entry Planes lump";
                }
            }
        }

        if (badTexInfo > 0)
        {
            findings.AddRepeated(BspRuleCodes.BrushSideTexInfo, firstTexInfo, badTexInfo);
        }

        if (badPlane > 0)
        {
            findings.AddRepeated(BspRuleCodes.PlaneIndex, firstPlane, badPlane);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.ModelHeadNode"/>.
    /// </summary>
    /// <remarks>
    /// -- <c>Sys_Error("Inline model
    /// %i has bad firstnode", i )</c> on <c>firstnode &gt;=
    /// m_worldBrushData.numnodes</c>. One of the few bounds the engine states
    /// out loud.
    /// </remarks>
    private static void CheckModelIndices(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<DModel> models = View<DModel>(bsp, BspLump.Models);
        int bad = 0;
        string first = string.Empty;
        for (int i = 0; i < models.Length; i++)
        {
            DModel model = models[i];
            if (model.HeadNode < 0 || model.HeadNode >= counts.Nodes)
            {
                bad++;
                if (bad == 1)
                {
                    first =
                        $"model {i} has head node {model.HeadNode}, outside the "
                        + $"{counts.Nodes}-entry Nodes lump";
                }
            }
        }

        if (bad > 0)
        {
            findings.AddRepeated(BspRuleCodes.ModelHeadNode, first, bad);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.TexDataStringIndex"/>.
    /// </summary>
    /// <remarks>
    /// Reads <c>&amp;pStringData[
    /// pStringTable[ in-&gt;nameStringTableID ] ]</c> and then treats it as a C
    /// string. The two <c>Assert</c>s above it are compiled out of a release
    /// build, so in a shipped engine this is two unbounded subscripts.
    /// </remarks>
    private static void CheckTexDataIndices(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<DTexData> texData = View<DTexData>(bsp, BspLump.TexData);
        ReadOnlySpan<int> table = View<int>(bsp, BspLump.TexDataStringTable);

        int bad = 0;
        string first = string.Empty;
        for (int i = 0; i < texData.Length; i++)
        {
            int id = texData[i].NameStringTableId;
            if (id < 0 || id >= counts.TexDataStringTable)
            {
                bad++;
                if (bad == 1)
                {
                    first =
                        $"texdata {i} names string table entry {id}, outside the "
                        + $"{counts.TexDataStringTable}-entry TexDataStringTable lump";
                }

                continue;
            }

            int offset = table[id];
            if (offset < 0 || offset >= counts.TexDataStringData)
            {
                bad++;
                if (bad == 1)
                {
                    first =
                        $"texdata {i} resolves to byte {offset} of the "
                        + $"{counts.TexDataStringData}-byte TexDataStringData lump";
                }
            }
        }

        if (bad > 0)
        {
            findings.AddRepeated(BspRuleCodes.TexDataStringIndex, first, bad);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.OverlayFaces"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sizes a vector from
    /// <c>GetFaceCount()</c> and then reads <c>pOverlayIn-&gt;aFaces[iFace]</c>
    /// for every one of them. <c>aFaces</c> is a fixed
    /// <c>int[OVERLAY_BSP_FACE_COUNT]</c> inside the on-disk struct
    /// (and 256 for a water overlay at
 ///), so a count above it reads the NEXT overlay's bytes as
    /// face indices. The face indices themselves go to
    /// <c>SurfaceHandleFromIndex</c> unbounded.
    /// </para>
    /// <para>
 /// The render-order check is
    /// deliberately NOT transcribed. <c>GetRenderOrder()</c> is
    /// <c>m_nFaceCountAndRenderOrder &gt;&gt; 14</c> and
    /// <c>OVERLAY_NUM_RENDER_ORDERS</c> is <c>1 &lt;&lt; 2</c>
 /// So the value is
    /// always 0..3 and the test can never fail whatever the file says. A rule
    /// that cannot fire is not a rule.
    /// </para>
    /// </remarks>
    private static void CheckOverlayIndices(BspData bsp, Counts counts, Findings findings)
    {
        int bad = 0;
        string first = string.Empty;

        ReadOnlySpan<DOverlay> overlays = View<DOverlay>(bsp, BspLump.Overlays);
        for (int i = 0; i < overlays.Length; i++)
        {
            DOverlay overlay = overlays[i];
            int faceCount = overlay.GetFaceCount();
            if (faceCount > BspLimits.OverlayFaceCount)
            {
                bad++;
                if (bad == 1)
                {
                    first =
                        $"overlay {i} claims {faceCount} faces, above the "
                        + $"{BspLimits.OverlayFaceCount} its fixed face array holds";
                }

                continue;
            }

            for (int f = 0; f < faceCount; f++)
            {
                int face = overlay.Faces[f];
                if (face < 0 || face >= counts.Faces)
                {
                    bad++;
                    if (bad == 1)
                    {
                        first =
                            $"overlay {i} face {f} is {face}, outside the {counts.Faces}-entry "
                            + "Faces lump";
                    }
                }
            }
        }

        ReadOnlySpan<DWaterOverlay> waterOverlays = View<DWaterOverlay>(bsp, BspLump.WaterOverlays);
        for (int i = 0; i < waterOverlays.Length; i++)
        {
            DWaterOverlay overlay = waterOverlays[i];
            int faceCount = overlay.GetFaceCount();
            if (faceCount > BspLimits.WaterOverlayFaceCount)
            {
                bad++;
                if (bad == 1)
                {
                    first =
                        $"water overlay {i} claims {faceCount} faces, above the "
                        + $"{BspLimits.WaterOverlayFaceCount} its fixed face array holds";
                }

                continue;
            }

            for (int f = 0; f < faceCount; f++)
            {
                int face = overlay.Faces[f];
                if (face < 0 || face >= counts.Faces)
                {
                    bad++;
                    if (bad == 1)
                    {
                        first =
                            $"water overlay {i} face {f} is {face}, outside the "
                            + $"{counts.Faces}-entry Faces lump";
                    }
                }
            }
        }

        if (bad > 0)
        {
            findings.AddRepeated(BspRuleCodes.OverlayFaces, first, bad);
        }
    }
}
