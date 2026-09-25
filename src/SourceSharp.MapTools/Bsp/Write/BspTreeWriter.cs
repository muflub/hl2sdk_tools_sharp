using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

using TreeNode = SourceSharp.MapTools.Bsp.Tree.BspNode;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// Called for every face <c>EmitFace</c> writes whose original side carries
/// overlays: <c>Overlay_AddFaceToLists</c> and
/// <c>OverlayTransition_AddFaceToLists</c>.
/// </summary>
/// <remarks>
/// Phase 3g's. The write stage only knows WHEN it happens: the face's final
/// index and the side it came from, at the moment it is numbered.
/// </remarks>
internal interface IOverlayFaceSink
{
    /// <summary>A face with overlays on its side was emitted.</summary>
    /// <param name="faceIndex">Its LUMP_FACES index.</param>
    /// <param name="side">The side it came from.</param>
    void AddFace(int faceIndex, MapBrushSide side);

    /// <summary>A face with water overlays on its side was emitted.</summary>
    /// <param name="faceIndex">Its LUMP_FACES index.</param>
    /// <param name="side">The side it came from.</param>
    void AddWaterFace(int faceIndex, MapBrushSide side);
}

/// <summary>
/// <c>WriteBSP</c> and everything it calls: numbering the tree into LUMP_NODES
/// and LUMP_LEAFS, the faces into LUMP_FACES / ORIGINALFACES / FACEIDS, their
/// edges into LUMP_EDGES / SURFEDGES, and the leaf face and brush lists
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbering is a pure function of the tree walk.</b> <c>EmitDrawNode_r</c>
/// is a pre-order walk, children 0 then 1, that numbers a node before its
/// children and a leaf the moment it is reached. Every index the BSP stores
/// — node, leaf, face, edge, origface — is assigned here, serially, in that
/// order. Nothing about this stage is parallel, by design (plan §1: index
/// assignment stays serial).
/// </para>
/// <para>
/// One instance per compile, because two of its tables outlive a model:
/// <c>pOrigFaceSideList</c> is a static that is cleared once
/// (<c>FindOrigFace</c>'s <c>bClear</c>) and never
/// again, and the edge table is per map.
/// </para>
/// </remarks>
internal sealed class BspTreeWriter
{
    private readonly BspWriteState _state;
    private readonly MapFile _map;
    private readonly WindingArena _windings;
    private readonly TexInfoTable _texInfos;
    private readonly Dictionary<MapBrush, int> _brushIndex = new(ReferenceEqualityComparer.Instance);

    // pOrigFaceSideList[MAX_MAP_PLANES] and side_t::next/origIndex, kept as
    // stock keeps them. side->next is ONE link per side, overwritten each time
    // the side is pushed onto a plane's list, so a side that reaches two
    // planes' lists relinks the first chain into the second. Reproduced by
    // keeping the link per side rather than per (plane, side).
    private readonly Dictionary<int, MapBrushSide> _origFaceSideList = [];
    private readonly Dictionary<MapBrushSide, MapBrushSide?> _sideNext = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MapBrushSide, int> _sideOrigIndex = new(ReferenceEqualityComparer.Instance);

    internal BspTreeWriter(BspWriteState state, MapFile map, TexInfoTable texInfos)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(texInfos);
        _state = state;
        _map = map;
        _windings = state.Faces.Windings;
        _texInfos = texInfos;

        for (int i = 0; i < map.Brushes.Count; i++)
        {
            _brushIndex[map.Brushes[i]] = i;
        }
    }

    /// <summary>Phase 3g's overlay face lists; null writes none.</summary>
    internal IOverlayFaceSink? Overlays { get; init; }

    /// <summary>Warnings raised while emitting.</summary>
    internal IList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary><c>c_nofaces</c>, for the verbose log.</summary>
    internal int NodesWithoutFaces { get; private set; }

    /// <summary><c>c_facenodes</c>, for the verbose log.</summary>
    internal int NodesWithFaces { get; private set; }

    /// <summary>
    /// <c>WriteBSP</c> for one model, up to the point where it would call
    /// <c>EmitAreaPortals</c>, the displacement faces and the water volumes
    /// (the driver calls those, in that order).
    /// </summary>
    /// <param name="headNode">The model's tree.</param>
    /// <param name="leafFaceList">The detail faces <c>MergeDetailTree</c> produced.</param>
    /// <param name="isWorld">Whether this is model 0: submodels get cluster -1.</param>
    /// <returns>The value for <c>dmodel_t::headnode</c>.</returns>
    internal int WriteTree(TreeNode headNode, Face? leafFaceList, bool isWorld)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        NodesWithoutFaces = 0;
        NodesWithFaces = 0;

        // GetEdge2_InitOptimizedList
        _state.Edges.ResetLookup();

        // EmitLeafFaces: the detail faces are numbered FIRST, before any node.
        for (Face? f = leafFaceList; f is not null; f = f.Next)
        {
            EmitFace(f, onNode: false);
        }

        return EmitDrawNode(headNode, isWorld);
    }

    /// <summary><c>EmitFace</c>.</summary>
    /// <param name="f">The face.</param>
    /// <param name="onNode">Whether it lies on a node (false for detail and displacement faces).</param>
    internal void EmitFace(Face f, bool onNode)
    {
        ArgumentNullException.ThrowIfNull(f);

        // set initial output number
        f.OutputNumber = -1;

        // degenerated
        if (f.NumPoints < 3)
        {
            return;
        }

        // not a final face
        if (f.IsDead)
        {
            return;
        }

        // don't emit NODRAW faces for runtime
        if ((_texInfos[f.TexInfo].Flags & (int)SurfaceFlags.NoDraw) != 0)
        {
            // keep NODRAW terrain surfaces though
            if (f.DispInfo == -1)
            {
                return;
            }

            Diagnostics.Add(new CompileDiagnostic(
                WriteCodes.NoDrawTerrain, DiagnosticSeverity.Warning, "NODRAW on terrain surface!"));
        }

        MapBrushSide original = f.OriginalFace
            ?? throw new InvalidOperationException("EmitFace: a face with no original side");

        // save output number so leaffaces can use
        f.OutputNumber = _state.DrawFaces.Count;

        if (_state.DrawFaces.Count >= WriteLimits.MaxMapFaces)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded, $"Too many faces in map, max = {WriteLimits.MaxMapFaces}");
        }

        // Save the correlation between dfaces and faces -- since dfaces doesnt
        // have worldcraft face id. hammerfaceid is an unsigned short.
        _state.FaceIds.Add(new DFaceId { HammerFaceId = unchecked((ushort)original.Id) });

        DFace df = default;
        df.PlaneNum = (ushort)f.PlaneNumber;
        df.OnNode = (byte)(onNode ? 1 : 0);
        df.Side = (byte)(f.PlaneNumber & 1);
        df.TexInfo = (short)f.TexInfo;
        df.DispInfo = (short)f.DispInfo;
        df.SmoothingGroups = f.SmoothingGroups;

        // save the original "side"/face data
        df.OrigFace = FindOrCreateOrigFace(f);
        df.SurfaceFogVolumeId = -1;

        // edge info
        df.FirstEdge = _state.SurfEdges.Count;
        df.NumEdges = (short)f.NumPoints;

        // UNDONE: Nodraw faces have no winding
        df.Area = f.Winding.IsNull ? 0f : _windings.Area(f.Winding);

        df.FirstPrimId = (ushort)f.FirstPrimId;
        df.SetNumPrims((ushort)f.NumPrims);
        df.SetDynamicShadowsEnabled(original.DynamicShadowsEnabled);

        // Styles and the lightmap fields are vrad's; stock's dfaces is a
        // zeroed global and vbsp never writes them, so they are zero here.
        int faceIndex = _state.DrawFaces.Count;
        _state.DrawFaces.Add(df);
        _state.FaceNodes.Add(f.FogVolumeLeaf);

        // save off points -- as edges
        for (int i = 0; i < f.NumPoints; i++)
        {
            int e = _state.Edges.GetEdge(
                f.VertexNumbers[i],
                f.VertexNumbers[(i + 1) % f.NumPoints],
                f,
                _state.Faces.Options.NoShare);

            AddSurfEdge(e);
        }

        // Create overlay face lists.
        if (Overlays is not null)
        {
            if (original.OverlayIds.Count > 0)
            {
                Overlays.AddFace(faceIndex, original);
            }

            if (original.WaterOverlayIds.Count > 0)
            {
                Overlays.AddWaterFace(faceIndex, original);
            }
        }
    }

    /// <summary><c>EmitDrawNode_r</c>.</summary>
    private int EmitDrawNode(TreeNode node, bool isWorld)
    {
        if (node.IsLeaf)
        {
            EmitLeaf(node, isWorld);
            return -_state.Leafs.Count;
        }

        // emit a node
        if (_state.Nodes.Count == WriteLimits.MaxMapNodes)
        {
            throw new MapCompileException(WriteCodes.Internal, "MAX_MAP_NODES");
        }

        int index = _state.Nodes.Count;
        node.DiskId = index;

        if ((node.PlaneNumber & 1) != 0)
        {
            throw new MapCompileException(WriteCodes.Internal, "WriteDrawNodes_r: odd planenum");
        }

        DNode n = default;
        n.Mins = ToShorts(node.Mins);
        n.Maxs = ToShorts(node.Maxs);
        n.PlaneNum = node.PlaneNumber;
        n.FirstFace = (ushort)_state.DrawFaces.Count;

        // Read HERE, before SetNodeAreaIndices_R runs at the end of
        // EmitAreaPortals: a node's area in the file is whatever the tree held
        // at this moment, and on a freshly built node that is 0.
        n.Area = (short)node.Area;

        _state.Nodes.Add(n);

        Face? faces = _state.Faces.Lists.FacesOf(node);
        if (faces is null)
        {
            NodesWithoutFaces++;
        }
        else
        {
            NodesWithFaces++;
        }

        for (Face? f = faces; f is not null; f = f.Next)
        {
            EmitFace(f, onNode: true);
        }

        DNode written = _state.Nodes[index];
        written.NumFaces = (ushort)(_state.DrawFaces.Count - written.FirstFace);

        // recursively output the other nodes
        for (int i = 0; i < 2; i++)
        {
            TreeNode child = node.Children[i]!;

            if (child.IsLeaf)
            {
                written.Children[i] = -(_state.Leafs.Count + 1);
                _state.Nodes[index] = written;
                EmitLeaf(child, isWorld);
            }
            else
            {
                written.Children[i] = _state.Nodes.Count;
                _state.Nodes[index] = written;
                EmitDrawNode(child, isWorld);
            }

            written = _state.Nodes[index];
        }

        return index;
    }

    /// <summary><c>EmitLeaf</c>.</summary>
    private void EmitLeaf(TreeNode node, bool isWorld)
    {
        // emit a leaf
        if (_state.Leafs.Count >= WriteLimits.MaxMapLeafs)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded, $"Too many BSP leaves, max = {WriteLimits.MaxMapLeafs}");
        }

        node.DiskId = _state.Leafs.Count;

        DLeaf leaf = default;

        // Submodels don't have clusters.
        leaf.Cluster = (short)(isWorld ? node.Cluster : -1);
        leaf.Contents = node.Contents;

        // By default, assume the leaf can see the skybox. VRAD will do the
        // actual computation to see if it really can see the skybox.
        leaf.SetAreaFlags(node.Area, LeafFlags.Sky);

        leaf.Mins = ToShorts(node.Mins);
        leaf.Maxs = ToShorts(node.Maxs);

        // write the leafbrushes: the detail fragments MergeDetailTree
        // prepended(AddBrushToLeaf) come first, then the
        // leaf's own brushlist.
        leaf.FirstLeafBrush = (ushort)_state.LeafBrushes.Count;
        for (BspBrush? b = _state.Faces.Lists.DetailBrushesOf(node); b is not null; b = b.Next)
        {
            AddLeafBrush(leaf.FirstLeafBrush, b);
        }

        for (BspBrush? b = node.BrushList; b is not null; b = b.Next)
        {
            AddLeafBrush(leaf.FirstLeafBrush, b);
        }

        leaf.NumLeafBrushes = (ushort)(_state.LeafBrushes.Count - leaf.FirstLeafBrush);

        // write the leaffaces
        if ((leaf.Contents & (int)BrushContents.Solid) == 0)
        {
            leaf.FirstLeafFace = (ushort)_state.LeafFaces.Count;

            for (Portal? p = node.Portals; p is not null;)
            {
                int s = ReferenceEquals(p.BackNode, node) ? 1 : 0;
                Face? f = _state.Faces.Lists.PortalFaceOf(p, s);

                if (f is not null)
                {
                    EmitMarkFace(leaf.FirstLeafFace, f);
                }

                p = p.NextAt(s);
            }

            // emit the detail faces
            for (LeafFace? l = _state.Faces.Lists.LeafFacesOf(node); l is not null; l = l.Next)
            {
                EmitMarkFace(leaf.FirstLeafFace, l.Face);
            }

            leaf.NumLeafFaces = (ushort)(_state.LeafFaces.Count - leaf.FirstLeafFace);
        }

        // "no leaffaces in solids": stock returns before writing
        // firstleafface, so a solid leaf keeps the zero of a cleared global.
        _state.Leafs.Add(leaf);
        _state.LeafNodes.Add(node);
    }

    /// <summary><c>EmitMarkFace</c>.</summary>
    private void EmitMarkFace(int firstLeafFace, Face f)
    {
        while (f.Merged is not null)
        {
            f = f.Merged;
        }

        if (f.Split[0] is not null)
        {
            EmitMarkFace(firstLeafFace, f.Split[0]!);
            EmitMarkFace(firstLeafFace, f.Split[1]!);
            return;
        }

        int faceNumber = f.OutputNumber;
        if (faceNumber == -1)
        {
            return; // degenerate face
        }

        if (faceNumber < 0 || faceNumber >= _state.DrawFaces.Count)
        {
            throw new MapCompileException(WriteCodes.Internal, "Bad leafface");
        }

        for (int i = firstLeafFace; i < _state.LeafFaces.Count; i++)
        {
            if (_state.LeafFaces[i] == faceNumber)
            {
                return; // merged out face
            }
        }

        if (_state.LeafFaces.Count >= WriteLimits.MaxMapLeafFaces)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded,
                $"Too many detail brush faces, max = {WriteLimits.MaxMapLeafFaces}");
        }

        _state.LeafFaces.Add((ushort)faceNumber);
    }

    private void AddLeafBrush(int first, BspBrush b)
    {
        if (_state.LeafBrushes.Count >= WriteLimits.MaxMapLeafBrushes)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded,
                $"Too many brushes in one leaf, max = {WriteLimits.MaxMapLeafBrushes}");
        }

        int brushNumber = _brushIndex[b.Original!];

        for (int i = first; i < _state.LeafBrushes.Count; i++)
        {
            if (_state.LeafBrushes[i] == brushNumber)
            {
                return;
            }
        }

        _state.LeafBrushes.Add((ushort)brushNumber);
    }

    /// <summary><c>FindOrCreateOrigFace</c>.</summary>
    private int FindOrCreateOrigFace(Face f)
    {
        // check for an original face
        if (f.OriginalFace is null)
        {
            return -1;
        }

        int index = FindOrigFace(f);
        return index == -1 ? CreateOrigFace(f) : index;
    }

    /// <summary><c>FindOrigFace</c>.</summary>
    private int FindOrigFace(Face f)
    {
        _origFaceSideList.TryGetValue(f.PlaneNumber, out MapBrushSide? side);

        for (; side is not null; side = _sideNext.GetValueOrDefault(side))
        {
            if (ReferenceEquals(side, f.OriginalFace))
            {
                return _sideOrigIndex[side];
            }
        }

        // original face not found in list
        return -1;
    }

    /// <summary><c>CreateOrigFace</c>.</summary>
    private int CreateOrigFace(Face f)
    {
        // not a real face!
        if (f.Winding.IsNull)
        {
            return -1;
        }

        // get the original face -- the "side"
        MapBrushSide side = f.OriginalFace!;

        // get the original face winding
        if (side.Winding.IsNull)
        {
            return -1;
        }

        if (_state.OrigFaces.Count >= WriteLimits.MaxMapFaces)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded, $"Too many faces in map, max = {WriteLimits.MaxMapFaces}");
        }

        int origIndex = _state.OrigFaces.Count;

        // add side to plane list
        _sideNext[side] = _origFaceSideList.GetValueOrDefault(f.PlaneNumber);
        _origFaceSideList[f.PlaneNumber] = side;
        _sideOrigIndex[side] = origIndex;

        ReadOnlySpan<Vec3> points = _windings.Points(side.Winding);

        DFace of = default;

        // set original face to -1 -- it is an origianl face!
        of.OrigFace = -1;
        of.PlaneNum = (ushort)side.PlaneNumber;
        of.OnNode = (byte)((side.Contents & (int)BrushContents.Detail) != 0 ? 0 : 1);
        of.Side = (byte)(side.PlaneNumber & 1);
        of.FirstEdge = _state.SurfEdges.Count;
        of.NumEdges = (short)points.Length;
        of.TexInfo = (short)side.TexInfo;
        of.DispInfo = (short)f.DispInfo;
        _state.OrigFaces.Add(of);

        // save the vertices
        Span<int> indices = points.Length <= 128 ? stackalloc int[128] : new int[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            indices[i] = _state.Vertices.GetVertexNumber(points[i]);
        }

        // save off points -- as edges
        for (int i = 0; i < points.Length; i++)
        {
            int e0 = indices[i];
            int e1 = indices[(i + 1) % points.Length];

            // look for matching edges first: the first j >= firstmodeledge
            // Stock's linear scan would stop at, from
            // the edge table's pair index instead of an O(numedges) walk
            int j = _state.Edges.FindReverseEdge(e0, e1, f.Contents, _state.FirstModelEdge);

            if (j != -1)
            {
                // set back edge
                _state.Edges.ShareEdge(j, f);
                AddSurfEdge(-j);
            }
            else
            {
                _state.Edges.AddEdge(e0, e1, f);
                AddSurfEdge(_state.Edges.Count - 1);
            }
        }

        return origIndex;
    }

    private void AddSurfEdge(int e)
    {
        if (_state.SurfEdges.Count >= WriteLimits.MaxMapSurfEdges)
        {
            throw new MapCompileException(WriteCodes.LimitExceeded,
                "Too much brush geometry in bsp, numsurfedges == MAX_MAP_SURFEDGES");
        }

        _state.SurfEdges.Add(e);
    }

    // VECTOR_COPY into a short[3]: a C float-to-short conversion, which
    // truncates toward zero.
    private static ShortArray3 ToShorts(Vec3 v)
    {
        ShortArray3 s = default;
        s[0] = (short)(int)v.X;
        s[1] = (short)(int)v.Y;
        s[2] = (short)(int)v.Z;
        return s;
    }
}
