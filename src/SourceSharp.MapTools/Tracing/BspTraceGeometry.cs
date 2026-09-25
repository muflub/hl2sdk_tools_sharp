using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// One interior node of the BSP tree, with its splitting plane INLINED.
/// </summary>
/// <remarks>
/// <para>
/// Thirty-two bytes, the same size as the <c>dnode_t</c> this is built from,
/// and that is where most of this tracer's speed comes from. Stock's node test
/// reads <c>dnodes[node]</c>, takes <c>planenum</c> out of it, and then reads
/// <c>dplanes[planenum]</c> -- a second, data-dependent load into a 20-byte
/// struct in a completely different array, once per node, on a walk that
/// touches tens of nodes per ray. Copying four floats into the node at load
/// time costs 32 bytes per node once and removes that dependent miss from
/// every ray forever.
/// </para>
/// <para>
/// THE AXIAL SHORTCUT IS KEPT, and this comment used to say the opposite. Stock
/// reads the ray component directly when <c>plane.type &lt;= PLANE_Z</c>
/// (<c>bsplib.cpp:3673</c>) instead of taking two dot products, and the first
/// draft of this port dropped it on the argument that a predictable branch
/// costs more than six multiplies. Measured on 324,000 of dm_lockdown's
/// leaf-ambient rays, putting it back is worth 5.94 Mray/s against 5.26 -- a
/// 1.13x that the argument had no way to see. The lesson is the ordinary one:
/// the descent loop, not the surface tests, is where a leaf-ambient ray spends
/// its time (stock's full run enters only 2.19 nodes and 2.25 leaves per ray),
/// so anything per-node is worth more than it looks.
/// </para>
/// <para>
/// The axis is classified from the NORMAL -- exact equality against (1,0,0),
/// (0,1,0), (0,0,1) -- and not from the lump's <c>type</c> field, which is what
/// stock reads. That is the safer of the two and gives the same answer on any
/// well-formed map: an exactly axial normal makes the shortcut arithmetically
/// identical to the dot product, since <c>1*x + 0*y + 0*z</c> is exactly
/// <c>x</c> in IEEE-754 for every finite input, signed zeros included. Trusting
/// <c>type</c> instead would take the shortcut on a plane whose normal is only
/// nearly axial, or is axial but NEGATIVE, and silently return a different
/// number from the dot product. The two classifications agreeing on a real map
/// is a fact rather than an assumption -- see
/// <c>BspTraceGeometryTests</c>.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct TraceNode
{
    /// <summary>The splitting plane's normal, x.</summary>
    public float Nx;

    /// <summary>The splitting plane's normal, y.</summary>
    public float Ny;

    /// <summary>The splitting plane's normal, z.</summary>
    public float Nz;

    /// <summary>The splitting plane's distance from the origin.</summary>
    public float Dist;

    /// <summary>
    /// The front child, in <c>dnode_t</c>'s own encoding: negative is the leaf
    /// <c>-(index + 1)</c>.
    /// </summary>
    public int Child0;

    /// <summary>The back child, same encoding.</summary>
    public int Child1;

    /// <summary>
    /// Where this node's candidate faces start in the tracer's node-face list.
    /// </summary>
    public int FirstFace;

    /// <summary>
    /// Solid candidate count in bits 0-14, sky count in bits 15-29, and the
    /// plane's axis in bits 30-31: 0, 1 or 2 for an exactly axial normal, and
    /// 3 for a general plane that needs the dot products.
    /// </summary>
    /// <remarks>
    /// Three fields in one int so the node stays 32 bytes with the axis in it.
    /// Fifteen bits is not a guess at the counts: <c>dnode_t.numfaces</c> is a
    /// <c>ushort</c>, so a node cannot name more than 65,535 faces, and the
    /// filtered halves of that are each checked against 0x7FFF at build time
    /// rather than truncated.
    /// </remarks>
    public int Packed;
}

/// <summary>
/// The per-face numbers a candidate test reads, gathered into one cache line.
/// </summary>
/// <remarks>
/// Stock's candidate test touches four different arrays -- <c>g_pFaces</c>,
/// <c>dplanes</c>, <c>texinfo</c>, and <c>dtexdata</c> indirectly -- to answer
/// one "did the ray hit this face?". Every one of those is a dependent load
/// off the last. Sixty-four bytes gathered once at load time turns four
/// probable misses into one.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct TraceSurface
{
    /// <summary>The face's plane normal, x (<c>dplanes[face.planenum]</c>, NOT flipped by <c>face.side</c>).</summary>
    public float Nx;

    /// <summary>The face's plane normal, y.</summary>
    public float Ny;

    /// <summary>The face's plane normal, z.</summary>
    public float Nz;

    /// <summary>The face's plane distance.</summary>
    public float Dist;

    /// <summary>Lightmap s axis, x.</summary>
    public float Sx;

    /// <summary>Lightmap s axis, y.</summary>
    public float Sy;

    /// <summary>Lightmap s axis, z.</summary>
    public float Sz;

    /// <summary>Lightmap s offset.</summary>
    public float So;

    /// <summary>Lightmap t axis, x.</summary>
    public float Tx;

    /// <summary>Lightmap t axis, y.</summary>
    public float Ty;

    /// <summary>Lightmap t axis, z.</summary>
    public float Tz;

    /// <summary>Lightmap t offset.</summary>
    public float To;

    /// <summary>
    /// <c>face.m_LightmapTextureMinsInLuxels[0]</c>, as a float because stock
    /// compares it against one (<c>vraddetailprops.cpp:504</c>) and the int to
    /// float conversion is exact for every value a lightmap origin can hold.
    /// </summary>
    public float MinS;

    /// <summary><c>face.m_LightmapTextureMinsInLuxels[1]</c>, as a float.</summary>
    public float MinT;

    /// <summary><c>face.m_LightmapTextureSizeInLuxels[0]</c>, as a float.</summary>
    public float SizeS;

    /// <summary><c>face.m_LightmapTextureSizeInLuxels[1]</c>, as a float.</summary>
    public float SizeT;
}

/// <summary>
/// A map's BSP tree, flattened into the form
/// <see cref="BspSurfaceTracer"/> walks.
/// </summary>
/// <remarks>
/// <para>
/// Built once per map and shared by every worker: it is immutable after
/// construction, which is what lets the tracer itself hold no per-ray state
/// that is not on the stack. Stock cannot do that -- <c>CLightSurface</c> is
/// constructed per ray and carries <c>s_DispTested[iThread]</c>, an indexed
/// global that §8 already names as needing to become per-work-item state.
/// </para>
/// <para>
/// THE FILTERING DONE HERE IS NOT AN OPTIMISATION THAT CHANGES THE ANSWER.
/// Stock skips a leaf face when <c>dispinfo != -1</c> or <c>onNode</c>, and a
/// node face when <c>!onNode</c> or <c>dispinfo != -1</c>, per ray, per
/// candidate (<c>vraddetailprops.cpp:373-380, 415-424</c>). Doing it once at
/// load time visits exactly the same faces in exactly the same order.
/// </para>
/// <para>
/// SPLITTING THE NODE LIST INTO SOLID-THEN-SKY DOES NOT CHANGE THE ANSWER
/// EITHER, and that needs an argument rather than a claim. Stock scans a
/// node's faces once in file order; a sky face that passes only RECORDS itself
/// in <c>pSkySurface</c> and carries on, while an ordinary face that passes
/// returns immediately. So an ordinary hit beats a sky hit no matter which
/// came first in the file, the winner among ordinary faces is the FIRST that
/// passes, and the winner among sky faces is the LAST. Both orders survive the
/// split, because the split is stable.
/// </para>
/// </remarks>
public sealed class BspTraceGeometry
{
    private readonly TraceNode[] _nodes;
    private readonly TraceSurface[] _surfaces;
    private readonly int[] _nodeFaces;
    private readonly int[] _leafFaces;
    private readonly int[] _leafFaceStart;
    private readonly int[] _leafFaceCount;
    private readonly Vec3[] _skyPoints;
    private readonly int[] _skyStart;
    private readonly int[] _skyCount;

    private BspTraceGeometry(
        TraceNode[] nodes,
        TraceSurface[] surfaces,
        int[] nodeFaces,
        int[] leafFaces,
        int[] leafFaceStart,
        int[] leafFaceCount,
        Vec3[] skyPoints,
        int[] skyStart,
        int[] skyCount,
        int skippedDisplacements)
    {
        _nodes = nodes;
        _surfaces = surfaces;
        _nodeFaces = nodeFaces;
        _leafFaces = leafFaces;
        _leafFaceStart = leafFaceStart;
        _leafFaceCount = leafFaceCount;
        _skyPoints = skyPoints;
        _skyStart = skyStart;
        _skyCount = skyCount;
        SkippedDisplacementFaces = skippedDisplacements;
    }

    /// <summary>How many nodes the tree has.</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>How many leaves the tree has.</summary>
    public int LeafCount => _leafFaceStart.Length;

    /// <summary>How many faces the map has, displacements included.</summary>
    public int SurfaceCount => _surfaces.Length;

    /// <summary>
    /// The axis a node's plane was classified as: 0, 1, 2 for an exactly axial
    /// normal, 3 for a general plane.
    /// </summary>
    /// <param name="node">Which node.</param>
    /// <returns>0, 1, 2 or 3.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// There is no such node.
    /// </exception>
    public int NodeAxis(int node)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(node, _nodes.Length);
        return (int)((uint)_nodes[node].Packed >> 30);
    }

    /// <summary>
    /// A node's two children, in <c>dnode_t</c>'s own encoding: negative is
    /// the leaf <c>-(index + 1)</c>.
    /// </summary>
    /// <param name="node">Which node.</param>
    /// <returns>The front and back children.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such node.</exception>
    public (int Child0, int Child1) NodeChildren(int node)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(node, _nodes.Length);
        return (_nodes[node].Child0, _nodes[node].Child1);
    }

    /// <summary>
    /// Where a node's candidate faces are, and how the run splits into lit
    /// surfaces and sky.
    /// </summary>
    /// <param name="node">Which node.</param>
    /// <returns>The run's offset and its two lengths.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such node.</exception>
    public (int First, int Solid, int Sky) NodeFaceRun(int node)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(node, _nodes.Length);
        int packed = _nodes[node].Packed;
        return (_nodes[node].FirstFace, packed & 0x7FFF, (packed >> 15) & 0x7FFF);
    }

    /// <summary>Where a leaf's candidate faces are, and how many.</summary>
    /// <param name="leaf">Which leaf.</param>
    /// <returns>The run's offset and length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such leaf.</exception>
    public (int First, int Count) LeafFaceRun(int leaf)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaf);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leaf, _leafFaceStart.Length);
        return (_leafFaceStart[leaf], _leafFaceCount[leaf]);
    }

    /// <summary>One entry of the node candidate list.</summary>
    /// <param name="index">Which entry.</param>
    /// <returns>A face index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such entry.</exception>
    public int NodeCandidate(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _nodeFaces.Length);
        return _nodeFaces[index];
    }

    /// <summary>One entry of the leaf candidate list.</summary>
    /// <param name="index">Which entry.</param>
    /// <returns>A face index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such entry.</exception>
    public int LeafCandidate(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _leafFaces.Length);
        return _leafFaces[index];
    }

    /// <summary>
    /// How many bytes one node occupies, which is 32 because the splitting
    /// plane is stored inside it.
    /// </summary>
    /// <remarks>
    /// Published so a fact can pin it. The tracer's speed rests on the plane
    /// being in the node rather than behind a <c>planenum</c> index, and that
    /// decision has a size: put the plane back behind an index and this number
    /// changes, on any machine, without a stopwatch.
    /// </remarks>
    public int NodeStrideBytes => Unsafe.SizeOf<TraceNode>();

    /// <summary>
    /// How many bytes one face's gathered test data occupies, which is 64: a
    /// cache line, deliberately.
    /// </summary>
    public int SurfaceStrideBytes => Unsafe.SizeOf<TraceSurface>();

    /// <summary>
    /// How many leaf-face candidates survived the load-time filter, across
    /// every leaf.
    /// </summary>
    /// <remarks>
    /// Compare against the raw LUMP_LEAFFACES length: stock re-tests
    /// <c>dispinfo</c> and <c>onNode</c> for each of those, per ray. If this
    /// ever equals the raw count, the filtering has moved back into the walk.
    /// </remarks>
    public int LeafCandidateCount => _leafFaces.Length;

    /// <summary>
    /// How many node-face candidates survived the load-time filter.
    /// </summary>
    public int NodeCandidateCount => _nodeFaces.Length;

    /// <summary>
    /// How many winding points the precomputed sky pool holds.
    /// </summary>
    /// <remarks>
    /// Zero would mean the sky test is building windings per ray again, the
    /// way stock does.
    /// </remarks>
    public int SkyWindingPointCount => _skyPoints.Length;

    /// <summary>
    /// How many faces were dropped for being displacements.
    /// </summary>
    /// <remarks>
    /// NOT a diagnostic: it is the size of the hole this tracer has. Stock
    /// follows its brush walk with
    /// <c>StaticDispMgr()-&gt;ClipRayToDispInLeaf</c> in every leaf, and this
    /// class does not, because the displacement collision tree is lane 4b's.
    /// A map whose count here is non-zero cannot be compared against a stock
    /// run that HAS displacements -- see
    /// <see cref="BspSurfaceTracer"/>'s remarks -- so the number is published
    /// rather than swallowed.
    /// </remarks>
    public int SkippedDisplacementFaces { get; }

    /// <summary>The flattened nodes.</summary>
    /// <remarks>
    /// The arrays themselves, not spans over them. A span property would be
    /// rebuilt on every access, and these are read once per node on a walk that
    /// runs millions of times per map; the tracer takes a reference to each
    /// array once, in its constructor.
    /// </remarks>
    internal TraceNode[] Nodes => _nodes;

    /// <summary>The gathered per-face data.</summary>
    internal TraceSurface[] Surfaces => _surfaces;

    /// <summary>Node face candidates, solid then sky within each node's run.</summary>
    internal int[] NodeFaces => _nodeFaces;

    /// <summary>Leaf face candidates.</summary>
    internal int[] LeafFaces => _leafFaces;

    /// <summary>Where each leaf's candidate run starts.</summary>
    internal int[] LeafFaceStart => _leafFaceStart;

    /// <summary>How long each leaf's candidate run is.</summary>
    internal int[] LeafFaceCount => _leafFaceCount;

    /// <summary>
    /// Every sky face's winding, end to end, colinear points already removed.
    /// </summary>
    /// <remarks>
    /// Precomputed, where stock builds and frees one per candidate PER RAY
    /// (<c>vraddetailprops.cpp:527-533</c>: <c>WindingFromFace</c>, which
    /// allocates, walks surfedges, and runs <c>RemoveColinearPoints</c>, then
    /// <c>FreeWinding</c>). The geometry cannot change between rays, so that is
    /// an allocation and a colinearity pass per sky test that produce the same
    /// answer every time.
    /// </remarks>
    internal Vec3[] SkyPoints => _skyPoints;

    /// <summary>Where a face's sky winding starts in <see cref="SkyPoints"/>.</summary>
    internal int[] SkyStart => _skyStart;

    /// <summary>How many points a face's sky winding has; 0 for a non-sky face.</summary>
    internal int[] SkyCount => _skyCount;

    /// <summary>
    /// Flattens a loaded map's tree.
    /// </summary>
    /// <param name="bsp">The map. Read only; nothing is written back.</param>
    /// <returns>The flattened tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <exception cref="InvalidBspException">
    /// A face, node or leaf names an index the map does not have. Thrown rather
    /// than clamped: an out-of-range index is a corrupt or misread lump, and a
    /// tracer that silently traced against face 0 instead would produce
    /// lighting that is wrong everywhere and looks plausible.
    /// </exception>
    public static BspTraceGeometry Build(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<ushort> leafFaceList = BspStructView.As<ushort>(bsp[BspLump.LeafFaces]);

        TraceSurface[] surfaces = BuildSurfaces(faces, planes, texInfo);
        int[] faceFlags = FaceFlags(faces, texInfo);

        (int[] nodeFaces, TraceNode[] traceNodes) =
            BuildNodes(nodes, planes, faces, faceFlags);

        (int[] leafFaces, int[] leafStart, int[] leafCount) =
            BuildLeaves(bsp, faces, leafFaceList);

        (Vec3[] skyPoints, int[] skyStart, int[] skyCount) =
            BuildSkyWindings(bsp, faces, faceFlags);

        int displacements = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].DispInfo != -1)
            {
                displacements++;
            }
        }

        return new BspTraceGeometry(
            traceNodes,
            surfaces,
            nodeFaces,
            leafFaces,
            leafStart,
            leafCount,
            skyPoints,
            skyStart,
            skyCount,
            displacements);
    }

    private static (Vec3[] Points, int[] Start, int[] Count) BuildSkyWindings(
        BspData bsp, ReadOnlySpan<DFace> faces, int[] faceFlags)
    {
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);

        List<Vec3> points = [];
        int[] start = new int[faces.Length];
        int[] count = new int[faces.Length];
        List<Vec3> raw = [];

        for (int f = 0; f < faces.Length; f++)
        {
            if ((faceFlags[f] & SurfaceFlags.Sky) == 0 || faces[f].DispInfo != -1)
            {
                continue;
            }

            DFace face = faces[f];
            raw.Clear();

            // vrad.cpp:366-386, WindingFromFace, with origin (0,0,0) -- which
            // is the only origin CLightSurface ever passes.
            for (int i = 0; i < face.NumEdges; i++)
            {
                int se = surfEdges[face.FirstEdge + i];
                int v = se < 0 ? edges[-se].V[1] : edges[se].V[0];
                raw.Add(vertexes[v]);
            }

            start[f] = points.Count;
            RemoveColinearPoints(raw, points);
            count[f] = points.Count - start[f];
        }

        return (points.ToArray(), start, count);
    }

    /// <summary>
    /// <c>polylib.cpp:90</c>, <c>RemoveColinearPoints</c>, appended to a flat
    /// list instead of rewritten in place.
    /// </summary>
    /// <param name="source">The winding's points, in file order.</param>
    /// <param name="sink">Receives the points that survive.</param>
    /// <remarks>
    /// The two normalisations go through <see cref="Vec3.NormaliseLikeStock"/>
    /// rather than an exact one. That is not fussiness: the test is
    /// <c>Dot(v1, v2) &lt; 0.999</c> against an estimate produced by
    /// <c>rsqrtss</c> plus one Newton step, so a point sitting near the
    /// threshold is kept or dropped by the estimate's last bits, and an exact
    /// normalise would drop a different set of points on some faces.
    /// </remarks>
    private static void RemoveColinearPoints(List<Vec3> source, List<Vec3> sink)
    {
        int nump = source.Count;
        for (int i = 0; i < nump; i++)
        {
            int j = (i + 1) % nump;
            int k = (i + nump - 1) % nump;
            (Vec3 v1, _) = (source[j] - source[i]).NormaliseLikeStock();
            (Vec3 v2, _) = (source[i] - source[k]).NormaliseLikeStock();
            if (Vec3.Dot(v1, v2) < 0.999f)
            {
                sink.Add(source[i]);
            }
        }

        // polylib.cpp:113's early return is "nothing changed, leave the winding
        // alone". Appending only the survivors reaches the same winding in both
        // cases, so there is nothing to undo here.
    }

    /// <summary>
    /// Whether a face is a <c>SURF_SKY</c> one, so a caller can tell
    /// stock's <c>m_bHasLuxel</c> from a <see cref="HitId"/>.
    /// </summary>
    /// <param name="bsp">The map the hit came from.</param>
    /// <param name="face">A face index.</param>
    /// <returns>True when the face's texinfo carries <c>SURF_SKY</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    public static bool IsSkyFace(BspData bsp, int face)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        int ti = faces[face].TexInfo;
        return ti >= 0 && (texInfo[ti].Flags & SurfaceFlags.Sky) != 0;
    }

    private static int[] FaceFlags(
        ReadOnlySpan<DFace> faces, ReadOnlySpan<TexInfo> texInfo)
    {
        int[] flags = new int[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            int ti = faces[i].TexInfo;
            if (ti < 0 || ti >= texInfo.Length)
            {
                // Stock dereferences &texinfo[pFace->texinfo] unconditionally.
                // A face with no texinfo cannot be lit, and treating it as
                // SURF_NOLIGHT is what stock's own test would conclude one line
                // later; what it must NOT do is index out of bounds.
                flags[i] = SurfaceFlags.NoLight;
                continue;
            }

            flags[i] = texInfo[ti].Flags;
        }

        return flags;
    }

    /// <summary>
    /// Gathers the per-face numbers the candidate test reads.
    /// </summary>
    /// <param name="faces">The map's faces.</param>
    /// <param name="planes">The map's planes.</param>
    /// <param name="texInfo">The map's texinfos.</param>
    /// <returns>One gathered record per face.</returns>
    /// <remarks>
    /// <c>SURF_NOLIGHT</c> is folded into the rectangle rather than kept as a
    /// flag to branch on. Stock's test returns false for such a face before
    /// looking at anything else (<c>vraddetailprops.cpp:498</c>); a face given
    /// zero axes and a size of -1 here fails the same test arithmetically,
    /// because the point's s and t come out 0, 0 is not less than the mins of
    /// 0, and 0 is not less than or equal to a size of -1. Same answer, one
    /// fewer branch on a path that runs per candidate per ray.
    /// </remarks>
    private static TraceSurface[] BuildSurfaces(
        ReadOnlySpan<DFace> faces,
        ReadOnlySpan<DPlane> planes,
        ReadOnlySpan<TexInfo> texInfo)
    {
        TraceSurface[] surfaces = new TraceSurface[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            DFace face = faces[i];
            if (face.PlaneNum >= planes.Length)
            {
                throw new InvalidBspException(
                    $"face {i} names plane {face.PlaneNum}, and the map has {planes.Length}");
            }

            DPlane plane = planes[face.PlaneNum];
            ref TraceSurface s = ref surfaces[i];
            s.Nx = plane.Normal.X;
            s.Ny = plane.Normal.Y;
            s.Nz = plane.Normal.Z;
            s.Dist = plane.Dist;

            int ti = face.TexInfo;
            bool lit = ti >= 0
                && ti < texInfo.Length
                && (texInfo[ti].Flags & SurfaceFlags.NoLight) == 0;

            if (!lit)
            {
                s.SizeS = -1.0f;
                s.SizeT = -1.0f;
                continue;
            }

            TexInfo tex = texInfo[ti];
            s.Sx = tex.LightmapVecsLuxelsPerWorldUnits[0];
            s.Sy = tex.LightmapVecsLuxelsPerWorldUnits[1];
            s.Sz = tex.LightmapVecsLuxelsPerWorldUnits[2];
            s.So = tex.LightmapVecsLuxelsPerWorldUnits[3];
            s.Tx = tex.LightmapVecsLuxelsPerWorldUnits[4];
            s.Ty = tex.LightmapVecsLuxelsPerWorldUnits[5];
            s.Tz = tex.LightmapVecsLuxelsPerWorldUnits[6];
            s.To = tex.LightmapVecsLuxelsPerWorldUnits[7];

            s.MinS = face.LightmapTextureMinsInLuxels[0];
            s.MinT = face.LightmapTextureMinsInLuxels[1];
            s.SizeS = face.LightmapTextureSizeInLuxels[0];
            s.SizeT = face.LightmapTextureSizeInLuxels[1];
        }

        return surfaces;
    }

    private static (int[] NodeFaces, TraceNode[] Nodes) BuildNodes(
        ReadOnlySpan<DNode> nodes,
        ReadOnlySpan<DPlane> planes,
        ReadOnlySpan<DFace> faces,
        int[] faceFlags)
    {
        TraceNode[] traceNodes = new TraceNode[nodes.Length];
        List<int> nodeFaces = [];
        List<int> sky = [];

        for (int n = 0; n < nodes.Length; n++)
        {
            DNode node = nodes[n];
            if (node.PlaneNum < 0 || node.PlaneNum >= planes.Length)
            {
                throw new InvalidBspException(
                    $"node {n} names plane {node.PlaneNum}, and the map has {planes.Length}");
            }

            DPlane plane = planes[node.PlaneNum];
            ref TraceNode t = ref traceNodes[n];
            t.Nx = plane.Normal.X;
            t.Ny = plane.Normal.Y;
            t.Nz = plane.Normal.Z;
            t.Dist = plane.Dist;
            t.Child0 = node.Children[0];
            t.Child1 = node.Children[1];
            t.FirstFace = nodeFaces.Count;

            sky.Clear();
            int solid = 0;
            for (int i = 0; i < node.NumFaces; i++)
            {
                int f = node.FirstFace + i;
                if (f >= faces.Length)
                {
                    throw new InvalidBspException(
                        $"node {n} names face {f}, and the map has {faces.Length}");
                }

                // vraddetailprops.cpp:373-380, in stock's own order.
                if (faces[f].OnNode == 0 || faces[f].DispInfo != -1)
                {
                    continue;
                }

                if ((faceFlags[f] & SurfaceFlags.Sky) != 0)
                {
                    sky.Add(f);
                    continue;
                }

                nodeFaces.Add(f);
                solid++;
            }

            nodeFaces.AddRange(sky);
            // The axis, from the normal alone. See TraceNode's remarks for
            // why this is not dplane_t.type.
            int ax = 3;
            if (plane.Normal.X == 1.0f && plane.Normal.Y == 0.0f && plane.Normal.Z == 0.0f)
            {
                ax = 0;
            }
            else if (plane.Normal.X == 0.0f && plane.Normal.Y == 1.0f && plane.Normal.Z == 0.0f)
            {
                ax = 1;
            }
            else if (plane.Normal.X == 0.0f && plane.Normal.Y == 0.0f && plane.Normal.Z == 1.0f)
            {
                ax = 2;
            }

            if (solid > 0x7FFF || sky.Count > 0x7FFF)
            {
                throw new InvalidBspException(
                    $"node {n} has {solid} lit and {sky.Count} sky candidate faces, and the "
                    + "packed node carries 15 bits for each");
            }

            t.Packed = solid | (sky.Count << 15) | (ax << 30);
        }

        return (nodeFaces.ToArray(), traceNodes);
    }

    private static (int[] Faces, int[] Start, int[] Count) BuildLeaves(
        BspData bsp,
        ReadOnlySpan<DFace> faces,
        ReadOnlySpan<ushort> leafFaceList)
    {
        int leafCount;
        int[] first;
        int[] num;

        // dm_lockdown.bsp is BSP 19 with LUMP_LEAFS at version 0, so the two
        // leaf structs are not an either/or the port can defer: a real
        // committed map in this tree uses the older one.
        if (bsp[BspLump.Leafs].Version == 0)
        {
            ReadOnlySpan<DLeafVersion0> leaves =
                BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]);
            leafCount = leaves.Length;
            first = new int[leafCount];
            num = new int[leafCount];
            for (int i = 0; i < leafCount; i++)
            {
                first[i] = leaves[i].FirstLeafFace;
                num[i] = leaves[i].NumLeafFaces;
            }
        }
        else
        {
            ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
            leafCount = leaves.Length;
            first = new int[leafCount];
            num = new int[leafCount];
            for (int i = 0; i < leafCount; i++)
            {
                first[i] = leaves[i].FirstLeafFace;
                num[i] = leaves[i].NumLeafFaces;
            }
        }

        List<int> candidates = [];
        int[] start = new int[leafCount];
        int[] count = new int[leafCount];

        for (int l = 0; l < leafCount; l++)
        {
            start[l] = candidates.Count;
            for (int i = 0; i < num[l]; i++)
            {
                int slot = first[l] + i;
                if (slot >= leafFaceList.Length)
                {
                    throw new InvalidBspException(
                        $"leaf {l} names leafface {slot}, and the map has {leafFaceList.Length}");
                }

                int f = leafFaceList[slot];
                if (f >= faces.Length)
                {
                    throw new InvalidBspException(
                        $"leaf {l} names face {f}, and the map has {faces.Length}");
                }

                // vraddetailprops.cpp:415-424, in stock's own order.
                if (faces[f].DispInfo != -1 || faces[f].OnNode != 0)
                {
                    continue;
                }

                candidates.Add(f);
            }

            count[l] = candidates.Count - start[l];
        }

        return (candidates.ToArray(), start, count);
    }
}

/// <summary>
/// The <c>SURF_*</c> bits this tracer reads, from <c>bspflags.h</c>.
/// </summary>
/// <remarks>
/// Only the two stock's surface test looks at. The rest are not copied here
/// because an unused constant that drifts from the header is worse than a
/// missing one.
/// </remarks>
public static class SurfaceFlags
{
    /// <summary><c>SURF_SKY</c> (<c>bspflags.h</c>): the 2D skybox.</summary>
    public const int Sky = 0x0004;

    /// <summary><c>SURF_NOLIGHT</c>: the surface carries no lightmap.</summary>
    public const int NoLight = 0x0400;
}
