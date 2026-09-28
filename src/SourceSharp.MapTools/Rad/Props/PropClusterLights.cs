//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Rad.Props;

/// <summary>
/// For each cluster, the lights a prop point in that cluster samples: the
/// indices into the pass's light list, in list order, of the lights a pass's
/// filter keeps and whose PVS holds the cluster.
/// </summary>
/// <remarks>
/// <para>
/// WHY. Prop lighting plans one direct sample for every light that passes two
/// tests -- a per-pass filter on the light itself (static props: style 0;
/// detail props: not the ambient sky) and the light's PVS row holding the
/// point's cluster -- and the reference implementation runs both tests over
/// the whole light list for every point. A static-prop pass lights every
/// vertex of every prop, and on a real map (2fort: 1,740 lights) most lights
/// fail for any one point, so the walk over the full list was a measurable
/// share of the whole compile. Both tests depend on the point only through
/// its cluster, so their answer is computed once a cluster here and the point
/// walks only the lights that pass.
/// </para>
/// <para>
/// NOTHING CHANGES A BYTE. A list holds exactly the indices the full walk
/// would have kept, in the same ascending order, so the samples are planned,
/// traced and summed in the order they always were. The tests are the same
/// tests in the same order: the light filter first, the PVS bit only for a
/// light the filter kept, and a negative cluster (a point outside the
/// visible world) skips the PVS test and keeps every light the filter keeps.
/// A light whose PVS row is too short for a cluster throws the same
/// <see cref="IndexOutOfRangeException"/> the full walk threw, when the
/// first point in that cluster asks.
/// </para>
/// <para>
/// BUILT LAZILY, ONE CLUSTER ON FIRST USE. Only the clusters that hold a prop
/// point are ever asked for, often a small part of the map, and a list costs
/// four bytes a visible light, so the clusters no point lands in cost one
/// null reference each. The filter itself runs once, eagerly, into the list a
/// negative cluster gets, and each cluster's list is that list narrowed by
/// the PVS bit. Workers ask concurrently: a list is published with a
/// compare-exchange, and two workers that raced on the same cluster built the
/// same contents, so whichever copy wins is the right answer and the other is
/// dropped.
/// </para>
/// <para>
/// MEMORY is bounded by clusters-with-a-point times visible lights. Measured
/// on 2fort's static-prop pass (2,680 clusters, 1,740 style-0 lights, two
/// million light points): 1,332 clusters were asked for, holding 136,849
/// indices together -- about 550 KB, an average of 103 lights a cluster where
/// the full walk visited 1,740. Built eagerly, every cluster would have cost
/// about twice that, and the full clusters-by-lights table (what an eager
/// dense layout would bound) almost 19 MB. It belongs to
/// one pass: the pass builds it and drops it when it returns, so nothing
/// outlives the compile.
/// </para>
/// </remarks>
internal sealed class PropClusterLights
{
    private readonly IReadOnlyList<PropLight> _lights;
    private readonly int[] _kept;
    private readonly int[]?[] _byCluster;

    /// <summary>Filters the lights and prepares an empty slot for each cluster.</summary>
    /// <param name="lights">The pass's lights, in list order.</param>
    /// <param name="clusterCount">How many clusters a point can be in; see <see cref="CountClusters"/>.</param>
    /// <param name="keep">The per-light test the pass applies before the PVS test.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> or <paramref name="keep"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clusterCount"/> is negative.</exception>
    public PropClusterLights(IReadOnlyList<PropLight> lights, int clusterCount, Func<PropLight, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentOutOfRangeException.ThrowIfNegative(clusterCount);

        List<int> kept = [];
        for (int i = 0; i < lights.Count; i++)
        {
            if (keep(lights[i]))
            {
                kept.Add(i);
            }
        }

        _lights = lights;
        _kept = [.. kept];
        _byCluster = new int[]?[clusterCount];
    }

    /// <summary>How many clusters this holds a slot for.</summary>
    public int ClusterCount => _byCluster.Length;

    /// <summary>
    /// The lists a static-prop pass walks: style-0 lights only, as the
    /// reference implementation lights a prop vertex from the base style
    /// alone.
    /// </summary>
    /// <param name="scene">The pass's map, whose leaves give the cluster count.</param>
    /// <param name="lights">The pass's lights.</param>
    /// <returns>The lists.</returns>
    public static PropClusterLights ForStaticProps(AmbientScene scene, IReadOnlyList<PropLight> lights) =>
        new(lights, CountClusters(scene), static l => l.Style == 0);

    /// <summary>
    /// The lists a detail-prop pass walks: every style (each sums into its
    /// own lightstyle colour), but not the ambient sky, whose light a detail
    /// prop takes from the leaf-ambient gather instead.
    /// </summary>
    /// <param name="scene">The pass's map, whose leaves give the cluster count.</param>
    /// <param name="lights">The pass's lights.</param>
    /// <returns>The lists.</returns>
    public static PropClusterLights ForDetailProps(AmbientScene scene, IReadOnlyList<PropLight> lights) =>
        new(lights, CountClusters(scene), static l => l.Type != EmitType.SkyAmbient);

    /// <summary>
    /// One more than the highest cluster any leaf of the map is in: every
    /// cluster a point lookup can return is below it.
    /// </summary>
    /// <param name="scene">The map.</param>
    /// <returns>The count; 0 for a map with no clustered leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    public static int CountClusters(AmbientScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        int max = -1;
        foreach (DLeaf leaf in scene.Leaves)
        {
            max = Math.Max(max, (int)leaf.Cluster);
        }

        return max + 1;
    }

    /// <summary>
    /// The indices, ascending, of the lights a point in
    /// <paramref name="cluster"/> samples.
    /// </summary>
    /// <param name="cluster">The point's cluster; any negative value means no PVS test.</param>
    /// <returns>The list. Callers must not write to it: it is shared by every point in the cluster.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cluster"/> is not below <see cref="ClusterCount"/>.</exception>
    /// <exception cref="IndexOutOfRangeException">A kept light's PVS row does not reach <paramref name="cluster"/>.</exception>
    public int[] For(int cluster)
    {
        if (cluster < 0)
        {
            return _kept;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, _byCluster.Length);
        int[]? list = Volatile.Read(ref _byCluster[cluster]);
        if (list is not null)
        {
            return list;
        }

        list = Build(cluster);
        return Interlocked.CompareExchange(ref _byCluster[cluster], list, null) ?? list;
    }

    /// <summary>
    /// How many cluster lists have been built and how many indices they hold
    /// together; the facts read it to check the lists stay lazy.
    /// </summary>
    /// <returns>The built lists and their total length.</returns>
    internal (int Clusters, long Indices) Built()
    {
        int clusters = 0;
        long indices = 0;
        for (int c = 0; c < _byCluster.Length; c++)
        {
            if (Volatile.Read(ref _byCluster[c]) is { } list)
            {
                clusters++;
                indices += list.Length;
            }
        }

        return (clusters, indices);
    }

    private int[] Build(int cluster)
    {
        int index = cluster >> 3;
        int bit = 1 << (cluster & 7);
        List<int> visible = [];
        foreach (int i in _kept)
        {
            if ((_lights[i].Pvs[index] & bit) != 0)
            {
                visible.Add(i);
            }
        }

        return [.. visible];
    }
}
