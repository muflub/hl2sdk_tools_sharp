using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The memory portals vvis works on, and the per-cluster lists that index them:
/// stock's <c>portals</c> and <c>leafs</c> arrays after <c>LoadPortals</c>
/// </summary>
/// <remarks>
/// <para>
/// Stock's <c>leaf_t</c> is a vector of pointers per cluster and its
/// <c>portal_t</c> carries the four bit vectors inline. Neither survives the
/// no-statics rule or the arena rule, so this type holds the IMMUTABLE half --
/// the geometry, which is read by every worker and written by none -- and the
/// per-portal bit vectors live in <see cref="VisPortalState"/>, which one
/// computation owns.
/// </para>
/// <para>
/// The split is not tidiness. It is what lets two maps be vised concurrently in
/// one process, and what makes it a compile error to accidentally mutate the
/// shared geometry from a worker.
/// </para>
/// <para>
/// Arrays of components rather than an array of portal structs, in the order
/// the candidate loop reads them (never touches a
/// winding). That is a layout choice, not an optimisation pass -- the 2b work
/// is elsewhere.
/// </para>
/// </remarks>
public sealed class PortalSet
{
    private readonly Vec3[] _normals;
    private readonly float[] _distances;
    private readonly int[] _leaves;
    private readonly Vec3[] _origins;
    private readonly float[] _radii;

    // The windings, end to end: portal i owns _points[_pointStart[i] .. +_pointCount[i]].
    private readonly Vec3[] _points;
    private readonly int[] _pointStart;
    private readonly int[] _pointCount;

    // The per-cluster portal lists, end to end, in memory-portal index order --
    // which is the order stock's AddToTail produces, because it walks file
    // portals in order and appends the forward portal before the backward one.
    private readonly int[] _clusterPortals;
    private readonly int[] _clusterStart;
    private readonly int[] _clusterCount;

    private PortalSet(
        int clusterCount,
        Vec3[] normals,
        float[] distances,
        int[] leaves,
        Vec3[] origins,
        float[] radii,
        Vec3[] points,
        int[] pointStart,
        int[] pointCount,
        int[] clusterPortals,
        int[] clusterStart,
        int[] clusterCount2)
    {
        ClusterCount = clusterCount;
        _normals = normals;
        _distances = distances;
        _leaves = leaves;
        _origins = origins;
        _radii = radii;
        _points = points;
        _pointStart = pointStart;
        _pointCount = pointCount;
        _clusterPortals = clusterPortals;
        _clusterStart = clusterStart;
        _clusterCount = clusterCount2;
    }

    /// <summary>
    /// How many vis clusters the portal file declares -- stock's
    /// <c>portalclusters</c>.
    /// </summary>
    public int ClusterCount { get; }

    /// <summary>
    /// How many MEMORY portals there are: twice the file's portal count, which
    /// is stock's <c>g_numportals * 2</c> and the width of every bit vector in
    /// vvis.
    /// </summary>
    public int Count => _leaves.Length;

    /// <summary>
    /// How many portals the FILE held -- stock's <c>g_numportals</c>, which is
    /// the number the catalogue's declarations and vbsp's console output count.
    /// </summary>
    public int FilePortalCount => _leaves.Length / 2;

    /// <summary>
    /// The plane normal of one portal, already pointing into its neighbour.
    /// </summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The normal.</returns>
    public Vec3 Normal(int portal) => _normals[portal];

    /// <summary>The plane distance of one portal.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The distance.</returns>
    public float Distance(int portal) => _distances[portal];

    /// <summary>
    /// The cluster on the far side of one portal -- stock's
    /// <c>portal_t::leaf</c>, which is the NEIGHBOUR and not the owner.
    /// </summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>A cluster index.</returns>
    public int Leaf(int portal) => _leaves[portal];

    /// <summary>
    /// The centre of one portal's bounding sphere (<c>SetPortalSphere</c>,
    ///).
    /// </summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The centre.</returns>
    public Vec3 Origin(int portal) => _origins[portal];

    /// <summary>The radius of one portal's bounding sphere.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>The radius.</returns>
    public float Radius(int portal) => _radii[portal];

    /// <summary>One portal's winding.</summary>
    /// <param name="portal">A memory-portal index.</param>
    /// <returns>Its points, in order.</returns>
    public ReadOnlySpan<Vec3> Winding(int portal) =>
        _points.AsSpan(_pointStart[portal], _pointCount[portal]);

    /// <summary>The portals filed under one cluster.</summary>
    /// <param name="cluster">A cluster index.</param>
    /// <returns>Memory-portal indices, ascending.</returns>
    public ReadOnlySpan<int> ClusterPortals(int cluster) =>
        _clusterPortals.AsSpan(_clusterStart[cluster], _clusterCount[cluster]);

    /// <summary>
    /// Performs <c>LoadPortals</c>'s doubling and sphere fitting over an
    /// already-parsed portal file.
    /// </summary>
    /// <param name="file">The parsed <c>.prt</c>.</param>
    /// <returns>The memory portals and their per-cluster lists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <exception cref="InvalidPortalFileException">
    /// A portal names a cluster outside the declared count. Stock's own bound
    /// check is off by one and lets <c>leafnum == portalclusters</c> through to
    /// index one past the end of its leaf array; the
    /// reader in <c>SourceSharp.MapFormats</c> reproduces that check exactly,
    /// so the value that stock would corrupt memory with arrives here and is
    /// refused here instead.
    /// </exception>
    public static PortalSet FromPortalFile(PortalFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return FromMemoryPortals(file.ClusterCount, file.ToMemoryPortals());
    }

    /// <summary>
    /// Builds the set from memory portals that have already been doubled.
    /// </summary>
    /// <param name="clusterCount">The portal file's cluster count.</param>
    /// <param name="portals">
    /// Two entries per file portal, forward then backward, as
    /// <see cref="PortalFile.ToMemoryPortals"/> yields them.
    /// </param>
    /// <returns>The memory portals and their per-cluster lists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="portals"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="clusterCount"/> is negative.
    /// </exception>
    /// <exception cref="InvalidPortalFileException">
    /// A portal names a cluster outside the declared count.
    /// </exception>
    public static PortalSet FromMemoryPortals(int clusterCount, IReadOnlyList<MemoryPortal> portals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusterCount);
        ArgumentNullException.ThrowIfNull(portals);

        int count = portals.Count;
        Vec3[] normals = new Vec3[count];
        float[] distances = new float[count];
        int[] leaves = new int[count];
        Vec3[] origins = new Vec3[count];
        float[] radii = new float[count];
        int[] pointStart = new int[count];
        int[] pointCount = new int[count];
        int[] clusterCounts = new int[clusterCount];

        int totalPoints = 0;
        for (int i = 0; i < count; i++)
        {
            MemoryPortal portal = portals[i];
            if ((uint)portal.OwningCluster >= (uint)clusterCount)
            {
                throw new InvalidPortalFileException(
                    $"portal {i} is filed under cluster {portal.OwningCluster} of {clusterCount}");
            }

            if ((uint)portal.Leaf >= (uint)clusterCount)
            {
                throw new InvalidPortalFileException(
                    $"portal {i} leads to cluster {portal.Leaf} of {clusterCount}");
            }

            clusterCounts[portal.OwningCluster]++;
            totalPoints += portal.Points.Count;
        }

        Vec3[] points = new Vec3[totalPoints];
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            MemoryPortal portal = portals[i];
            normals[i] = portal.Normal;
            distances[i] = portal.Distance;
            leaves[i] = portal.Leaf;
            pointStart[i] = at;
            pointCount[i] = portal.Points.Count;

            for (int j = 0; j < portal.Points.Count; j++)
            {
                points[at + j] = portal.Points[j];
            }

            (origins[i], radii[i]) = PortalSphere(points.AsSpan(at, portal.Points.Count));
            at += portal.Points.Count;
        }

        int[] clusterStart = new int[clusterCount];
        int running = 0;
        for (int c = 0; c < clusterCount; c++)
        {
            clusterStart[c] = running;
            running += clusterCounts[c];
        }

        int[] fill = new int[clusterCount];
        int[] clusterPortals = new int[count];
        for (int i = 0; i < count; i++)
        {
            int owner = portals[i].OwningCluster;
            clusterPortals[clusterStart[owner] + fill[owner]] = i;
            fill[owner]++;
        }

        return new PortalSet(
            clusterCount, normals, distances, leaves, origins, radii,
            points, pointStart, pointCount, clusterPortals, clusterStart, clusterCounts);
    }

    /// <summary>
    /// <c>SetPortalSphere</c>: the
    /// centre is the mean of the points and the radius the farthest of them.
    /// </summary>
    /// <param name="winding">The portal's points.</param>
    /// <returns>The centre and radius.</returns>
    /// <exception cref="ArgumentException"><paramref name="winding"/> is empty.</exception>
    /// <remarks>
    /// Not a bounding sphere in the tight sense: it is the mean, which is what
    /// the two early-out tests in <c>RecursiveLeafFlow</c> (
    /// ) are calibrated against, so fitting a smaller one would
    /// change the answer.
    /// </remarks>
    public static (Vec3 Origin, float Radius) PortalSphere(ReadOnlySpan<Vec3> winding)
    {
        if (winding.Length == 0)
        {
            throw new ArgumentException("a portal winding has at least one point", nameof(winding));
        }

        // -- accumulate, then divide each component by the
        // point count. The divide is per component and in float, which is why
        // this is not written as a multiply by a reciprocal.
        float x = 0f;
        float y = 0f;
        float z = 0f;
        for (int i = 0; i < winding.Length; i++)
        {
            x += winding[i].X;
            y += winding[i].Y;
            z += winding[i].Z;
        }

        Vec3 total = new(x / winding.Length, y / winding.Length, z / winding.Length);

        float best = 0f;
        for (int i = 0; i < winding.Length; i++)
        {
            // VectorLength is FastSqrt, which is ::sqrtf on every platform this
            // tree builds, so MathF.Sqrt is the same function.
            float r = (winding[i] - total).Length();
            if (r > best)
            {
                best = r;
            }
        }

        return (total, best);
    }
}
