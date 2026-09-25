using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The lumps vrad's lighting path reads, materialised once.
/// </summary>
/// <remarks>
/// <para>
/// Stock reaches these through twelve file-scope arrays declared in
/// <c>bsplib.h</c> and filled by <c>LoadBSPFile</c>. Every function in
/// <c>lightmap.cpp</c> and the patch half of <c>vrad.cpp</c> indexes them
/// directly, which is why none of those functions can be called twice on two
/// maps and why none of them can be tested without a map on disk. Collecting
/// them into one argument is the whole of the structural change: the bodies
/// below are line-for-line ports.
/// </para>
/// <para>
/// Arrays rather than spans because this outlives a stack frame and is read
/// from worker threads; the copy is one pass over lumps that are a few hundred
/// kilobytes on a real map, against a lighting solve measured in seconds.
/// </para>
/// <para>
/// <b><see cref="Faces"/> is the LDR or HDR face lump, chosen once.</b> Stock's
/// <c>g_pFaces</c> (<c>vrad.cpp:2221-2234</c>) is a pointer switched at load,
/// and every later read goes through it -- so an HDR compile of a map whose HDR
/// face lump is empty lights the LDR faces. Reproduced by
/// <see cref="Load"/> taking the range rather than by a flag read later.
/// </para>
/// </remarks>
public sealed class LightGeometry
{
    private readonly int[] _texDataStringTable;
    private readonly byte[] _texDataStringData;

    private LightGeometry(
        DFace[] faces,
        DPlane[] planes,
        Vec3[] vertexes,
        DEdge[] edges,
        int[] surfEdges,
        TexInfo[] texInfos,
        DTexData[] texDatas,
        DModel[] models,
        DNode[] nodes,
        LeafInfo[] leaves,
        ushort[] leafFaces,
        int clusterCount,
        ComplianceOptions compliance,
        int[] texDataStringTable,
        byte[] texDataStringData,
        int areaCount)
    {
        _texDataStringTable = texDataStringTable;
        _texDataStringData = texDataStringData;
        AreaCount = areaCount;
        Faces = faces;
        Planes = planes;
        Vertexes = vertexes;
        Edges = edges;
        SurfEdges = surfEdges;
        TexInfos = texInfos;
        TexDatas = texDatas;
        Models = models;
        Nodes = nodes;
        Leaves = leaves;
        LeafFaces = leafFaces;
        ClusterCount = clusterCount;
        Compliance = compliance;
    }

    /// <summary><c>g_pFaces</c>: the face lump this compile lights.</summary>
    public DFace[] Faces { get; }

    /// <summary><c>dplanes</c>.</summary>
    public DPlane[] Planes { get; }

    /// <summary><c>dvertexes</c>, as bare positions.</summary>
    public Vec3[] Vertexes { get; }

    /// <summary><c>dedges</c>.</summary>
    public DEdge[] Edges { get; }

    /// <summary><c>dsurfedges</c>.</summary>
    public int[] SurfEdges { get; }

    /// <summary><c>texinfo</c>.</summary>
    public TexInfo[] TexInfos { get; }

    /// <summary><c>dtexdata</c>.</summary>
    public DTexData[] TexDatas { get; }

    /// <summary><c>dmodels</c>.</summary>
    public DModel[] Models { get; }

    /// <summary><c>dnodes</c>.</summary>
    public DNode[] Nodes { get; }

    /// <summary><c>dleafs</c>, reduced to what the lighting path reads.</summary>
    public LeafInfo[] Leaves { get; }

    /// <summary><c>dleaffaces</c>.</summary>
    public ushort[] LeafFaces { get; }

    /// <summary><c>dvis-&gt;numclusters</c>, or 0 when the map has no vis.</summary>
    public int ClusterCount { get; }

    /// <summary>Which quirks this compile reproduces.</summary>
    public ComplianceOptions Compliance { get; }

    /// <summary>
    /// Whether every <c>VectorNormalize</c> in the lighting path reproduces
    /// stock's reciprocal-square-root estimate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One seam for the whole path, because a MIXTURE is neither answer: a
    /// phong normal normalised exactly and then fed to a bump basis normalised
    /// approximately gives a basis matching no tool.
    /// </para>
    /// <para>
    /// Bound to <see cref="StockQuirk.VradVectorNormalise"/>: vrad's own
    /// <c>VectorNormalize</c> sites (<c>lightmap.cpp:322, 1153, 2192</c>,
    /// <c>bumpvects.cpp:52-54</c>), all reaching <c>vector.h:2239</c>'s
    /// <c>rsqrtss</c> plus one Newton-Raphson step on <c>PLATFORM_INTEL</c>.
    /// </para>
    /// </remarks>
    public bool StockNormalise => Compliance.Emulates(StockQuirk.VradVectorNormalise);

    /// <summary>
    /// True when the four-wide lighting maths takes stock's reciprocal
    /// estimates (<see cref="StockQuirk.GatherReciprocalEstimate"/>).
    /// </summary>
    public bool StockEstimates => Compliance.Emulates(StockQuirk.GatherReciprocalEstimate);

    /// <summary>
    /// Reads the lumps a lighting pass needs out of a compiled map.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="range">Which face lump to light.</param>
    /// <param name="compliance">Which quirks to reproduce.</param>
    /// <returns>The materialised lumps.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bsp"/> or <paramref name="compliance"/> is null.
    /// </exception>
    public static LightGeometry Load(
        BspData bsp,
        VradLightingRange range,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(compliance);

        // vrad.cpp:2221-2234. The HDR lump is written only when it DIFFERS
        // from the LDR one, so "HDR was asked for" is not enough.
        bool useHdrFaces = range != VradLightingRange.Ldr && !bsp[BspLump.FacesHdr].IsEmpty;
        BspLump faceLump = useHdrFaces ? BspLump.FacesHdr : BspLump.Faces;

        LeafInfo[] leaves = ReadLeaves(bsp);

        return new LightGeometry(
            BspStructView.As<DFace>(bsp[faceLump]).ToArray(),
            BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray(),
            BspStructView.As<Vec3>(bsp[BspLump.Vertexes]).ToArray(),
            BspStructView.As<DEdge>(bsp[BspLump.Edges]).ToArray(),
            BspStructView.As<int>(bsp[BspLump.SurfEdges]).ToArray(),
            BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray(),
            BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray(),
            BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray(),
            BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray(),
            leaves,
            BspStructView.As<ushort>(bsp[BspLump.LeafFaces]).ToArray(),
            ReadClusterCount(bsp),
            compliance,
            BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray(),
            bsp[BspLump.TexDataStringData].Data.ToArray(),
            bsp[BspLump.Areas].Length / 8);
    }

    /// <summary>
    /// <c>EdgeVertex</c> (<c>lightmap.cpp:123</c>): the vertex at one corner of
    /// a face, with the index wrapped.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <param name="edge">The corner, which may be -1 or past the last.</param>
    /// <returns>The vertex index.</returns>
    /// <remarks>
    /// <para>
    /// The wrap is asymmetric and that is deliberate: a negative index is
    /// brought forward by ONE period (<c>edge += numedges</c>, so -5 on a
    /// 4-edge face stays negative) while a large one is reduced modulo. Both
    /// callers only ever pass <c>j</c> and <c>j+1</c>, so the asymmetry is
    /// unreachable; it is reproduced rather than tidied because a tidy version
    /// would answer differently for inputs stock never produces and a test
    /// might.
    /// </para>
    /// <para>
    /// The sign of the surfedge picks WHICH end: a negative surfedge means the
    /// edge is traversed backwards, so the second vertex is the face's corner.
    /// </para>
    /// </remarks>
    public int EdgeVertex(int faceNum, int edge)
    {
        ref readonly DFace face = ref Faces[faceNum];
        if (edge < 0)
        {
            edge += face.NumEdges;
        }
        else if (edge >= face.NumEdges)
        {
            edge %= face.NumEdges;
        }

        int k = SurfEdges[face.FirstEdge + edge];
        return k < 0 ? Edges[-k].V[1] : Edges[k].V[0];
    }

    /// <summary>
    /// <c>WindingFromFace</c> (<c>vrad.cpp:366</c>): a face's polygon, offset
    /// into a brush model's in-use position.
    /// </summary>
    /// <param name="arena">Where the winding is allocated.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="origin">The owning entity's origin.</param>
    /// <returns>The winding, with colinear points removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    /// <remarks>
    /// Not the same walk as <see cref="EdgeVertex"/>, even though both read
    /// surfedges: this one does not wrap, and it takes <c>v[1]</c> of the
    /// negated edge where <see cref="EdgeVertex"/> takes the same. They agree,
    /// and stock still spells both out; so does this.
    /// </remarks>
    public Winding WindingFromFace(WindingArena arena, int faceNum, Vec3 origin)
    {
        ArgumentNullException.ThrowIfNull(arena);

        ref readonly DFace face = ref Faces[faceNum];
        Winding w = arena.Alloc(face.NumEdges);
        w = arena.SetCount(w, face.NumEdges);
        Span<Vec3> points = arena.Points(w);

        for (int i = 0; i < face.NumEdges; i++)
        {
            int se = SurfEdges[face.FirstEdge + i];
            int v = se < 0 ? Edges[-se].V[1] : Edges[se].V[0];
            points[i] = Vertexes[v] + origin;
        }

        return arena.RemoveColinearPoints(w);
    }

    /// <summary><c>numareas</c>: the AREAS lump's record count (8 bytes each).</summary>
    public int AreaCount { get; }

    /// <summary>
    /// <c>TexDataStringTable_GetString</c>: a texdata's material name.
    /// </summary>
    /// <param name="stringId">The texdata's <c>nameStringTableID</c>.</param>
    /// <returns>
    /// The NUL-terminated string at that table entry's offset, or empty when
    /// the id or offset is out of range.
    /// </returns>
    public string TexDataName(int stringId)
    {
        if ((uint)stringId >= (uint)_texDataStringTable.Length)
        {
            return string.Empty;
        }

        int start = _texDataStringTable[stringId];
        if ((uint)start >= (uint)_texDataStringData.Length)
        {
            return string.Empty;
        }

        int end = Array.IndexOf(_texDataStringData, (byte)0, start);
        if (end < 0)
        {
            end = _texDataStringData.Length;
        }

        return System.Text.Encoding.Latin1.GetString(_texDataStringData, start, end - start);
    }

    /// <summary>
    /// <c>ValidDispFace</c> (<c>vrad.h:542</c>): whether a face is a
    /// displacement vrad will light as one.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>True when it has a dispinfo AND exactly four edges.</returns>
    /// <remarks>
    /// The four-edge test is not redundant. A displacement's base face is a
    /// quad by construction, but vbsp's face merging can leave a
    /// <c>dispinfo</c> on something that is no longer one, and stock then
    /// treats it as an ordinary face everywhere -- including in
    /// <see cref="FaceNeighbours"/>, where <c>bHasDisp</c> decides whether
    /// smoothing ignores the angle threshold.
    /// </remarks>
    public bool IsValidDispFace(int faceNum)
    {
        ref readonly DFace face = ref Faces[faceNum];
        return face.DispInfo != -1 && face.NumEdges == 4;
    }

    /// <summary>
    /// <c>IsSky</c> (<c>vrad.cpp:425</c>): whether a face's texinfo carries
    /// <c>SURF_SKY</c>.
    /// </summary>
    /// <param name="faceNum">The face.</param>
    /// <returns>True for a sky face.</returns>
    public bool IsSky(int faceNum) =>
        (TexInfos[Faces[faceNum].TexInfo].Flags & (int)SurfaceFlags.Sky) != 0;

    private static LeafInfo[] ReadLeaves(BspData bsp)
    {
        if (bsp[BspLump.Leafs].Version == 0)
        {
            ReadOnlySpan<DLeafVersion0> raw =
                BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]);
            LeafInfo[] leaves = new LeafInfo[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                leaves[i] = new LeafInfo(
                    raw[i].Contents,
                    raw[i].Cluster,
                    raw[i].GetArea(),
                    raw[i].GetFlags(),
                    new Vec3(raw[i].Mins[0], raw[i].Mins[1], raw[i].Mins[2]),
                    new Vec3(raw[i].Maxs[0], raw[i].Maxs[1], raw[i].Maxs[2]),
                    raw[i].FirstLeafFace,
                    raw[i].NumLeafFaces);
            }

            return leaves;
        }

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        LeafInfo[] result = new LeafInfo[leafs.Length];
        for (int i = 0; i < leafs.Length; i++)
        {
            result[i] = new LeafInfo(
                leafs[i].Contents,
                leafs[i].Cluster,
                leafs[i].GetArea(),
                leafs[i].GetFlags(),
                new Vec3(leafs[i].Mins[0], leafs[i].Mins[1], leafs[i].Mins[2]),
                new Vec3(leafs[i].Maxs[0], leafs[i].Maxs[1], leafs[i].Maxs[2]),
                leafs[i].FirstLeafFace,
                leafs[i].NumLeafFaces);
        }

        return result;
    }

    private static int ReadClusterCount(BspData bsp)
    {
        ReadOnlySpan<byte> vis = bsp[BspLump.Visibility].Data.Span;
        return vis.Length >= 4 ? BitConverter.ToInt32(vis[..4]) : 0;
    }
}

/// <summary>
/// One leaf, reduced to the fields the lighting path reads.
/// </summary>
/// <param name="Contents">The leaf's <c>CONTENTS_</c> mask.</param>
/// <param name="Cluster">Its vis cluster, or -1.</param>
/// <param name="Area">Its area index.</param>
/// <param name="Flags">Its <see cref="LeafFlags"/> as loaded.</param>
/// <param name="Mins">Its bounding box minimum, widened from shorts.</param>
/// <param name="Maxs">Its bounding box maximum.</param>
/// <param name="FirstLeafFace">The first entry of its LUMP_LEAFFACES run.</param>
/// <param name="NumLeafFaces">How long that run is.</param>
/// <remarks>
/// A record rather than the raw <see cref="DLeaf"/> because LUMP_LEAFS has two
/// on-disk shapes -- version 0 carries an ambient cube inline and is 56 bytes,
/// the current one is 32 -- and every caller below would otherwise branch on
/// the version. The flags are mutable state during lighting --
/// <c>BuildVisForLightEnvironment</c> (<c>lightmap.cpp:1344</c>) writes SKY and
/// SKY2D back into the lump -- so the sky pass carries its own flag array and
/// this record stays immutable.
/// </remarks>
public readonly record struct LeafInfo(
    int Contents,
    int Cluster,
    int Area,
    LeafFlags Flags,
    Vec3 Mins,
    Vec3 Maxs,
    int FirstLeafFace,
    int NumLeafFaces);
