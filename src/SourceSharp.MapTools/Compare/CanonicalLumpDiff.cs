using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// The geometry lumps, compared as multisets of canonical keys.
/// </summary>
/// <remarks>
/// <para>
/// plan_maptools.md 1 requires that anything assigning an index the BSP stores
/// -- plane numbers, vertex welds, node and leaf numbering -- commits in the
/// stock insertion order, and the reason that rule needs enforcing is that
/// nothing in the FORMAT requires it: a map with its planes in a different
/// order is the same map. So the instrument that judges "is this the same map"
/// must not be able to be fooled by the order, and must not be fooled INTO a
/// difference by it either.
/// </para>
/// <para>
/// Every key here is built from VALUES, never from indices. A face's plane is
/// the plane's normal and distance, not its plane number; a face's material is
/// the material's NAME, resolved through texinfo, texdata and the string table,
/// not a texinfo index. That is what makes the keys survive a reordering of
/// any of those lumps.
/// </para>
/// </remarks>
internal static class CanonicalLumpDiff
{
    /// <summary>LUMP_PLANES as a multiset of (normal, distance).</summary>
    internal static void Planes(BspData a, BspData b, LumpDiffBuilder into, DiffOptions options)
    {
        into.Note =
            "compared as a multiset of (normal, distance): plane NUMBERS are assigned by vbsp's "
            + "insertion order and are not a property of the map";
        Compare(PlaneKeys(a, options), PlaneKeys(b, options), into, options);
    }

    private static List<string> PlaneKeys(BspData bsp, DiffOptions options)
    {
        List<string> keys = [];
        BspLumpData lump = bsp[BspLump.Planes];
        if (!BspStructView.Fits<DPlane>(lump))
        {
            return keys;
        }

        foreach (DPlane plane in BspStructView.As<DPlane>(lump))
        {
            keys.Add(PlaneKey(plane, options.GeometryEpsilon));
        }

        return keys;
    }

    private static string PlaneKey(DPlane plane, float? epsilon) => string.Concat(
        "n=",
        CanonicalKey.Vector(plane.Normal, epsilon),
        " d=",
        CanonicalKey.Float(plane.Dist, epsilon));

    /// <summary>
    /// A face lump as a multiset of (plane, side, material, mapping, vertex
    /// ring).
    /// </summary>
    /// <param name="a">Map A.</param>
    /// <param name="b">Map B.</param>
    /// <param name="lump">Which face lump: FACES, FACES_HDR or ORIGINALFACES.</param>
    /// <param name="into">The verdict being built.</param>
    /// <param name="options">The caller's epsilon and report bounds.</param>
    internal static void Faces(
        BspData a,
        BspData b,
        BspLump lump,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        into.Note =
            "compared as a multiset of (plane value, side, material name, mapping, vertex ring); "
            + "the ring is rotated to its own smallest vertex so a face that starts at a "
            + "different edge is the same face";
        Compare(FaceKeys(a, lump, options), FaceKeys(b, lump, options), into, options);
    }

    private static List<string> FaceKeys(BspData bsp, BspLump lump, DiffOptions options)
    {
        List<string> keys = [];
        BspLumpData faceLump = bsp[lump];
        if (!BspStructView.Fits<DFace>(faceLump))
        {
            return keys;
        }

        FaceGeometry geometry = new(bsp);
        float? epsilon = options.GeometryEpsilon;
        List<Vec3> ring = [];

        foreach (DFace face in BspStructView.As<DFace>(faceLump))
        {
            geometry.Ring(face, ring);

            StringBuilder key = new();
            key.Append("plane=").Append(geometry.PlaneKey(face.PlaneNum, epsilon));
            key.Append(" side=").Append(face.Side.ToString(CultureInfo.InvariantCulture));
            key.Append(" tex=").Append(geometry.TexInfoKey(face.TexInfo, epsilon));
            key.Append(" ring=").Append(CanonicalKey.Ring(ring, epsilon));
            keys.Add(key.ToString());
        }

        return keys;
    }

    /// <summary>
    /// The leaf/cluster partition: which leaves share a cluster, with neither
    /// leaf numbers nor cluster numbers in the key.
    /// </summary>
    /// <param name="a">Map A.</param>
    /// <param name="b">Map B.</param>
    /// <param name="into">The verdict being built.</param>
    /// <param name="options">The caller's report bounds.</param>
    /// <remarks>
    /// <para>
    /// A partition is a set of sets, so the key for a cluster is its members'
    /// sorted keys and the lump's key set is the multiset of those. Renumbering
    /// every cluster leaves this identical, which is the point; moving one leaf
    /// from one cluster to another changes exactly two keys.
    /// </para>
    /// <para>
    /// Leaves with a negative cluster -- solid leaves, which vvis never
    /// numbers -- are collected into one bucket of their own rather than
    /// dropped, so a solid leaf appearing or vanishing is still visible.
    /// </para>
    /// </remarks>
    internal static void LeafPartition(BspData a, BspData b, LumpDiffBuilder into, DiffOptions options)
    {
        into.Note =
            "compared as the leaf/cluster PARTITION: each cluster's key is its members' sorted "
            + "keys, so renumbering clusters or leaves changes nothing";
        Compare(ClusterKeys(a), ClusterKeys(b), into, options);
    }

    private static List<string> ClusterKeys(BspData bsp)
    {
        BspLumpData lump = bsp[BspLump.Leafs];
        Dictionary<int, List<string>> clusters = [];

        // dleaf_t is 56 bytes at LUMP version 0 and 32 at version 1
        // (bspfile.h:799 and :826), and nothing else in the file says which.
        if (lump.Version == 0)
        {
            if (!BspStructView.Fits<DLeafVersion0>(lump))
            {
                return [];
            }

            foreach (DLeafVersion0 leaf in BspStructView.As<DLeafVersion0>(lump))
            {
                Add(clusters, leaf.Cluster, LeafKey(
                    leaf.Contents, leaf.AreaFlags, leaf.Mins, leaf.Maxs,
                    leaf.NumLeafFaces, leaf.NumLeafBrushes, leaf.LeafWaterDataId));
            }
        }
        else
        {
            if (!BspStructView.Fits<DLeaf>(lump))
            {
                return [];
            }

            foreach (DLeaf leaf in BspStructView.As<DLeaf>(lump))
            {
                Add(clusters, leaf.Cluster, LeafKey(
                    leaf.Contents, leaf.AreaFlags, leaf.Mins, leaf.Maxs,
                    leaf.NumLeafFaces, leaf.NumLeafBrushes, leaf.LeafWaterDataId));
            }
        }

        List<string> keys = [];
        foreach ((int cluster, List<string> members) in clusters)
        {
            members.Sort(StringComparer.Ordinal);
            keys.Add(string.Concat(
                cluster < 0 ? "solid{" : "cluster{",
                string.Join('|', members),
                "}"));
        }

        return keys;
    }

    private static void Add(Dictionary<int, List<string>> clusters, short cluster, string key)
    {
        // Every negative cluster is the same bucket: vvis leaves solid leaves
        // at -1 and there is no second meaning for a negative here.
        int bucket = cluster < 0 ? -1 : cluster;
        if (!clusters.TryGetValue(bucket, out List<string>? members))
        {
            members = [];
            clusters[bucket] = members;
        }

        members.Add(key);
    }

    private static string LeafKey(
        int contents,
        ushort areaFlags,
        ShortArray3 mins,
        ShortArray3 maxs,
        ushort leafFaces,
        ushort leafBrushes,
        short waterData) => string.Create(
        CultureInfo.InvariantCulture,
        $"c={contents:X8} af={areaFlags:X4} min=({mins[0]},{mins[1]},{mins[2]}) "
        + $"max=({maxs[0]},{maxs[1]},{maxs[2]}) f={leafFaces} b={leafBrushes} w={waterData}");

    /// <summary>
    /// Compares two key lists as multisets and records what each has that the
    /// other does not.
    /// </summary>
    private static void Compare(
        List<string> a,
        List<string> b,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string key in a)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        int common = 0;
        List<string> onlyInB = [];
        foreach (string key in b)
        {
            if (counts.TryGetValue(key, out int n) && n > 0)
            {
                counts[key] = n - 1;
                common++;
            }
            else
            {
                onlyInB.Add(key);
            }
        }

        List<string> onlyInA = [];
        foreach ((string key, int n) in counts)
        {
            for (int i = 0; i < n; i++)
            {
                onlyInA.Add(key);
            }
        }

        // Sorted so two runs of the same comparison list the same samples: an
        // instrument whose report depends on dictionary iteration order cannot
        // be diffed against its own previous run.
        onlyInA.Sort(StringComparer.Ordinal);
        onlyInB.Sort(StringComparer.Ordinal);

        int max = Math.Max(0, options.MaxReportedItems);
        into.Set = new SetDifference(
            onlyInA.Count,
            onlyInB.Count,
            [.. onlyInA.Take(max)],
            [.. onlyInB.Take(max)],
            common);

        into.AddCount(onlyInA.Count + onlyInB.Count);
    }
}

/// <summary>
/// Resolves a face's plane, material and vertex ring out of the lumps that
/// carry them.
/// </summary>
/// <remarks>
/// Built once per lump comparison and held on the stack of that call. It caches
/// the spans it walks, which is what makes 6,500 faces' rings cheap, and it
/// holds no state that outlives the comparison.
/// </remarks>
internal sealed class FaceGeometry
{
    private readonly ReadOnlyMemory<byte> _planes;
    private readonly ReadOnlyMemory<byte> _texInfo;
    private readonly ReadOnlyMemory<byte> _vertexes;
    private readonly ReadOnlyMemory<byte> _edges;
    private readonly ReadOnlyMemory<byte> _surfEdges;
    private readonly ImmutableArray<string> _texDataNames;

    internal FaceGeometry(BspData bsp)
    {
        _planes = bsp[BspLump.Planes].Data;
        _texInfo = bsp[BspLump.TexInfo].Data;
        _vertexes = bsp[BspLump.Vertexes].Data;
        _edges = bsp[BspLump.Edges].Data;
        _surfEdges = bsp[BspLump.SurfEdges].Data;
        _texDataNames = [.. BspNames.TexDataNames(bsp)];
    }

    /// <summary>The plane at <paramref name="planeNum"/>, as a canonical key.</summary>
    /// <param name="planeNum">The face's plane number.</param>
    /// <param name="epsilon">The caller's epsilon, or null.</param>
    /// <returns>The key, or <c>&lt;bad&gt;</c> when the index is out of range.</returns>
    internal string PlaneKey(ushort planeNum, float? epsilon)
    {
        ReadOnlySpan<DPlane> planes = MemoryMarshal.Cast<byte, DPlane>(_planes.Span);
        if (planeNum >= planes.Length)
        {
            return "<bad>";
        }

        DPlane plane = planes[planeNum];
        return string.Concat(
            CanonicalKey.Vector(plane.Normal, epsilon), "/", CanonicalKey.Float(plane.Dist, epsilon));
    }

    /// <summary>
    /// The texinfo at <paramref name="index"/>, as the material NAME plus the
    /// mapping, so no index survives into the key.
    /// </summary>
    /// <param name="index">The face's texinfo index.</param>
    /// <param name="epsilon">The caller's epsilon, or null.</param>
    /// <returns>The key, or <c>&lt;bad&gt;</c> when the index is out of range.</returns>
    internal string TexInfoKey(short index, float? epsilon)
    {
        ReadOnlySpan<TexInfo> texInfos = MemoryMarshal.Cast<byte, TexInfo>(_texInfo.Span);
        if (index < 0 || index >= texInfos.Length)
        {
            return "<bad>";
        }

        TexInfo info = texInfos[index];
        string material = info.TexData >= 0 && info.TexData < _texDataNames.Length
            ? _texDataNames[info.TexData]
            : "<bad>";

        StringBuilder key = new(material);
        key.Append(CultureInfo.InvariantCulture, $"|flags={info.Flags:X8}|s=");
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
            {
                key.Append(',');
            }

            key.Append(CanonicalKey.Float(info.TextureVecsTexelsPerWorldUnits[i], epsilon));
        }

        key.Append("|l=");
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
            {
                key.Append(',');
            }

            key.Append(CanonicalKey.Float(info.LightmapVecsLuxelsPerWorldUnits[i], epsilon));
        }

        return key.ToString();
    }

    /// <summary>
    /// Fills <paramref name="ring"/> with the face's vertices, in winding
    /// order.
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="ring">A list to fill; it is cleared first.</param>
    /// <remarks>
    /// A surfedge is a SIGNED index into the edge lump: positive takes the
    /// edge's first vertex, negative its second, which is how one edge serves
    /// two faces wound opposite ways (<c>bspfile.h:480</c>).
    /// </remarks>
    internal void Ring(DFace face, List<Vec3> ring)
    {
        ring.Clear();

        ReadOnlySpan<int> surfEdges = MemoryMarshal.Cast<byte, int>(_surfEdges.Span);
        ReadOnlySpan<DEdge> edges = MemoryMarshal.Cast<byte, DEdge>(_edges.Span);
        ReadOnlySpan<Vec3> vertexes = MemoryMarshal.Cast<byte, Vec3>(_vertexes.Span);

        for (int i = 0; i < face.NumEdges; i++)
        {
            int at = face.FirstEdge + i;
            if (at < 0 || at >= surfEdges.Length)
            {
                return;
            }

            int surfEdge = surfEdges[at];
            int edge = Math.Abs(surfEdge);
            if (edge >= edges.Length)
            {
                return;
            }

            DEdge pair = edges[edge];
            ushort vertex = surfEdge >= 0 ? pair.V[0] : pair.V[1];
            if (vertex >= vertexes.Length)
            {
                return;
            }

            ring.Add(vertexes[vertex]);
        }
    }
}
