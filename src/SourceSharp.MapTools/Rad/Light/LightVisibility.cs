using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Per-light PVS: <c>GetVisCache</c>, <c>SetDLightVis</c>,
/// <c>MergeDLightVis</c> and <c>PVSCheck</c> (<c>lightmap.cpp:2308, 1012,
/// 1022</c> and <c>vrad.h:372</c>).
/// </summary>
/// <remarks>
/// <para>
/// The single cheapest optimisation in vrad and the one most easily got
/// wrong. Every light carries a bit per cluster, and a sample whose cluster is
/// clear is skipped before any ray is cast -- so a map's lighting cost is
/// roughly (lights x clusters they reach) rather than (lights x samples).
/// </para>
/// <para>
/// <b>A negative cluster means VISIBLE, not hidden.</b> <c>PVSCheck</c>
/// (<c>vrad.h:372-384</c>) returns 1 for any cluster below zero, and stock's
/// comment says why: <c>PointInLeaf</c> still reports -1 for points that are
/// really in the world, and the alternative to assuming visibility is black
/// samples. So the failure mode is "lit when it should not be", which is
/// invisible, rather than "black", which is a bug report.
/// </para>
/// <para>
/// <b>A map with no vis data is all-visible.</b> <c>GetVisCache</c>
/// (<c>:2311-2315</c>) fills the row with 0xFF when <c>visdatasize</c> is
/// zero, and does the same for a negative cluster. A <c>-fast</c> vvis run or
/// a leaked map therefore lights every sample against every light, which is
/// why an unvis'd map takes so long rather than looking wrong.
/// </para>
/// </remarks>
public sealed class LightVisibility
{
    private readonly VisibilityLump? _vis;
    private readonly byte[] _visData;
    private readonly int _rowBytes;

    private LightVisibility(VisibilityLump? vis, byte[] visData, int clusterCount)
    {
        _vis = vis;
        _visData = visData;
        ClusterCount = clusterCount;

        // lightmap.cpp:1016 sizes a light's PVS as (numclusters / 8) + 1,
        // which is ONE MORE BYTE than GetVisCache fills at :2313's
        // (numclusters + 7) / 8 whenever the count is a multiple of eight. The
        // larger of the two is used here so neither write runs off the end.
        _rowBytes = clusterCount > 0 ? (clusterCount / 8) + 1 : 0;
    }

    /// <summary>How many vis clusters the map has.</summary>
    public int ClusterCount { get; }

    /// <summary>Whether the map has vis data at all.</summary>
    public bool HasVisibility => _vis is not null && _visData.Length > 0;

    /// <summary>
    /// How many bytes one light's PVS occupies: <c>(numclusters / 8) + 1</c>.
    /// </summary>
    public int RowBytes => _rowBytes;

    /// <summary>
    /// Reads the visibility lump.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The PVS accessor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    public static LightVisibility Load(BspData bsp) => Load(bsp, []);

    /// <summary>
    /// Reads the visibility lump, counting clusters from the leaves when there
    /// is none.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="leaves">The map's leaves.</param>
    /// <returns>The PVS accessor.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <c>vrad.cpp:2245-2251</c>: a map with no vis data gets
    /// <c>dvis-&gt;numclusters = CountClusters()</c> -- the largest leaf cluster
    /// plus one, and at least one (<c>vrad.cpp:1735</c>). That count is what
    /// sizes every light's PVS and how many bytes <c>GetVisCache</c> fills
    /// with 0xFF, so without it a vis-less map's lights would see nothing.
    /// </remarks>
    public static LightVisibility Load(BspData bsp, ReadOnlySpan<LeafInfo> leaves)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        BspLumpData lump = bsp[BspLump.Visibility];
        VisibilityLump? vis = VisibilityLump.Read(lump);
        byte[] data = lump.Data.ToArray();
        int clusters = vis?.NumClusters ?? CountClusters(leaves);
        return new LightVisibility(vis, data, clusters);
    }

    /// <summary><c>CountClusters</c> (<c>vrad.cpp:1735</c>): the largest leaf cluster, plus one.</summary>
    /// <param name="leaves">The leaves.</param>
    /// <returns>At least 1.</returns>
    public static int CountClusters(ReadOnlySpan<LeafInfo> leaves)
    {
        int clusterCount = 0;
        foreach (LeafInfo leaf in leaves)
        {
            if (leaf.Cluster > clusterCount)
            {
                clusterCount = leaf.Cluster;
            }
        }

        return clusterCount + 1;
    }

    /// <summary>
    /// <c>PVSCheck</c> (<c>vrad.h:372</c>).
    /// </summary>
    /// <param name="pvs">A light's PVS row.</param>
    /// <param name="cluster">The cluster to test.</param>
    /// <returns>
    /// True when the light can reach that cluster -- including when the
    /// cluster is negative.
    /// </returns>
    public static bool PvsCheck(ReadOnlySpan<byte> pvs, int cluster)
    {
        if (cluster < 0)
        {
            return true;
        }

        int index = cluster >> 3;
        return index < pvs.Length && (pvs[index] & (1 << (cluster & 7))) != 0;
    }

    /// <summary>
    /// <c>GetVisCache</c> (<c>lightmap.cpp:2308</c>): one cluster's PVS row.
    /// </summary>
    /// <param name="cluster">The cluster, or a negative value for "unknown".</param>
    /// <param name="row">
    /// Receives the row. Must be at least <see cref="RowBytes"/> long.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="row"/> is too short.
    /// </exception>
    /// <remarks>
    /// Stock's <c>lastoffset</c> parameter is a one-entry memo that every
    /// caller passes -1 to, so it never hits; dropped rather than carried.
    /// </remarks>
    public void GetVisCache(int cluster, Span<byte> row)
    {
        if (row.Length < _rowBytes)
        {
            throw new ArgumentException(
                $"A PVS row needs {_rowBytes} bytes.", nameof(row));
        }

        // :2311-2315 and :2318-2324. Both the no-vis case and the negative
        // cluster fill (numclusters + 7) / 8 bytes with 0xFF -- note that is
        // the SHORTER of the two lengths, so the last byte of a row whose
        // cluster count is a multiple of 8 keeps whatever it held.
        if (!HasVisibility || cluster < 0 || _vis is null)
        {
            int fill = (ClusterCount + 7) >> 3;
            row[..Math.Min(fill, row.Length)].Fill(0xFF);
            return;
        }

        int offset = _vis.BitOffset(cluster, VisibilityLump.Pvs);
        if (offset == -1)
        {
            throw new InvalidBspException(
                "the visibility lump has no PVS row for a cluster the map uses; "
                + "stock calls this \"visofs == -1\" (lightmap.cpp:2332)");
        }

        _vis.DecompressRow(_visData.AsSpan(offset), row[.._rowBytes]);
    }

    /// <summary>
    /// <c>SetDLightVis</c> (<c>lightmap.cpp:1012</c>): give a light the PVS of
    /// one cluster.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="cluster">Its cluster.</param>
    /// <exception cref="ArgumentNullException"><paramref name="light"/> is null.</exception>
    public void SetLightVis(DirectLight light, int cluster)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.Pvs.Length < _rowBytes)
        {
            light.Pvs = new byte[_rowBytes];
        }

        GetVisCache(cluster, light.Pvs);
    }

    /// <summary>
    /// <c>MergeDLightVis</c> (<c>lightmap.cpp:1022</c>): OR another cluster's
    /// PVS into a light's.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="cluster">The cluster to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="light"/> is null.</exception>
    /// <remarks>
    /// A light with no PVS yet is SET rather than merged (<c>:1024-1027</c>),
    /// which is not the same as merging into zeros when the map has no vis
    /// data -- in that case set fills with 0xFF and merge would too, so they
    /// agree; the difference is only that set allocates.
    /// </remarks>
    public void MergeLightVis(DirectLight light, int cluster)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.Pvs.Length == 0)
        {
            SetLightVis(light, cluster);
            return;
        }

        Span<byte> row = _rowBytes <= 512 ? stackalloc byte[_rowBytes] : new byte[_rowBytes];
        GetVisCache(cluster, row);

        for (int i = 0; i < _rowBytes; i++)
        {
            light.Pvs[i] |= row[i];
        }
    }
}
