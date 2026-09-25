using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The first-order approximation: <c>BasePortalVis</c> and <c>SimpleFlood</c>
/// (<c>src/utils/vvis/flow.cpp:714</c> and <c>:684</c>).
/// </summary>
/// <remarks>
/// <para>
/// For each memory portal this decides which other portals are in front of it
/// AND have it in front of them (<c>portalfront</c>), then floods through the
/// cluster graph from its own neighbour, keeping only portals reachable by a
/// chain of such portals (<c>portalflood</c>). Everything after this only ever
/// REMOVES bits, so this is the ceiling on what any later stage can claim.
/// </para>
/// <para>
/// Each portal is completely independent of every other, so this stage is
/// trivially parallel and trivially deterministic.
/// </para>
/// </remarks>
internal sealed class VisBaseFlow
{
    private readonly PortalSet _portals;
    private readonly VisPortalState _state;
    private readonly bool _useRadius;
    private readonly double _radiusSquared;
    private long _rays;

    /// <summary>
    /// Portal-pair front tests this flow has performed, summed over every
    /// portal it was handed. One <see cref="Interlocked"/> add per portal, so
    /// it costs the stage nothing; it exists because this is the pass P12
    /// anomaly 3 claimed <c>-fast</c> skips and stock proves it does NOT
    /// (<c>all.c:19955</c>), and a measured zero where stock casts is the
    /// defect the tests guard.
    /// </summary>
    internal long BaseRays => Volatile.Read(ref _rays);

    /// <summary>Prepares the stage.</summary>
    /// <param name="portals">The map's memory portals.</param>
    /// <param name="state">Where the bit vectors go.</param>
    /// <param name="useRadius">Whether radial vis is on.</param>
    /// <param name="radiusSquared">
    /// <c>g_VisRadius</c>, already squared, as a <see cref="double"/> -- which
    /// is the type stock keeps it in (<c>vvis.cpp:55</c>).
    /// </param>
    internal VisBaseFlow(PortalSet portals, VisPortalState state, bool useRadius, double radiusSquared)
    {
        _portals = portals;
        _state = state;
        _useRadius = useRadius;
        _radiusSquared = radiusSquared;
    }

    /// <summary>
    /// Computes one portal's <c>portalfront</c> and <c>portalflood</c>.
    /// </summary>
    /// <param name="portalIndex">A memory-portal index.</param>
    /// <param name="scratch">
    /// The worker's <c>portalfront</c> vector and flood stack, reused between
    /// items.
    /// </param>
    /// <param name="context">The worker, for cancellation.</param>
    internal void Run(int portalIndex, VisFloodScratch scratch, WorkerContext context)
    {
        Span<ulong> front = scratch.Front;
        front.Clear();
        Vec3 normal = _portals.Normal(portalIndex);
        float distance = _portals.Distance(portalIndex);
        ReadOnlySpan<Vec3> own = _portals.Winding(portalIndex);
        Vec3 origin = _portals.Origin(portalIndex);

        int count = _portals.Count;
        for (int j = 0; j < count; j++)
        {
            context.ThrowIfShouldStop();

            if (j == portalIndex)
            {
                continue;
            }

            // flow.cpp:750-758 -- any point of the other winding strictly in
            // front of THIS plane. The loop stops at the first one, and running
            // to the end means "no points on front".
            ReadOnlySpan<Vec3> other = _portals.Winding(j);
            if (!AnyPointInFront(other, normal, distance))
            {
                continue;
            }

            // flow.cpp:763-771 -- and any point of this winding strictly BEHIND
            // the other plane. Note the asymmetry: front uses `d > eps`, this
            // one uses `d < -eps`. It is not a transcription slip; the two
            // planes point at each other.
            if (!AnyPointBehind(own, _portals.Normal(j), _portals.Distance(j)))
            {
                continue;
            }

            if (_useRadius && !WithinRadius(other, origin))
            {
                continue;
            }

            BitVectorOps.SetBit(front, j);
        }

        // Every j except the portal itself was tested above, however the test
        // ended: this is the cast count, not the hit count.
        Interlocked.Add(ref _rays, count - 1);

        SimpleFlood(portalIndex, scratch, context);
        _state.SetMightSeeCount(portalIndex, _state.CountFlood(portalIndex));
    }

    /// <summary>
    /// <c>SimpleFlood</c> (<c>flow.cpp:684-707</c>), with the recursion turned
    /// into an explicit stack.
    /// </summary>
    /// <param name="portalIndex">The portal whose flood set is being built.</param>
    /// <param name="scratch">The worker's scratch; its stack is emptied first.</param>
    /// <param name="context">The worker, for cancellation.</param>
    /// <remarks>
    /// <para>
    /// The result is identical to the recursion's, and provably so: the flood
    /// visits a portal only when its <c>portalfront</c> bit is set and its
    /// <c>portalflood</c> bit is not, and sets the flood bit before descending.
    /// That is a reachability search with a visited set, whose reached SET does
    /// not depend on the order it is explored in. Only the order changes, and
    /// nothing reads the order.
    /// </para>
    /// <para>
    /// Not an optimisation. The recursion is one C frame per portal flooded and
    /// a map may flood tens of thousands, which is far past what a .NET thread
    /// stack survives -- and a stack overflow is not an exception here, it is
    /// the process.
    /// </para>
    /// </remarks>
    private void SimpleFlood(int portalIndex, VisFloodScratch scratch, WorkerContext context)
    {
        Span<ulong> front = scratch.Front;
        Span<ulong> flood = _state.Flood(portalIndex);
        Stack<int> stack = scratch.Clusters;

        stack.Clear();
        stack.Push(_portals.Leaf(portalIndex));

        while (stack.Count > 0)
        {
            context.ThrowIfShouldStop();

            int cluster = stack.Pop();
            ReadOnlySpan<int> neighbours = _portals.ClusterPortals(cluster);
            for (int i = 0; i < neighbours.Length; i++)
            {
                int pnum = neighbours[i];
                if (!BitVectorOps.GetBit(front, pnum))
                {
                    continue;
                }

                if (BitVectorOps.GetBit(flood, pnum))
                {
                    continue;
                }

                BitVectorOps.SetBit(flood, pnum);
                stack.Push(_portals.Leaf(pnum));
            }
        }
    }

    private static bool AnyPointInFront(ReadOnlySpan<Vec3> winding, Vec3 normal, float distance)
    {
        for (int k = 0; k < winding.Length; k++)
        {
            float d = Vec3.Dot(winding[k], normal) - distance;
            if (d > VisClip.OnVisEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyPointBehind(ReadOnlySpan<Vec3> winding, Vec3 normal, float distance)
    {
        for (int k = 0; k < winding.Length; k++)
        {
            float d = Vec3.Dot(winding[k], normal) - distance;
            if (d < -VisClip.OnVisEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The radial-vis test (<c>flow.cpp:777-793</c>): is any point of the other
    /// winding inside the vis radius of this portal's centre?
    /// </summary>
    /// <param name="winding">The other portal's points.</param>
    /// <param name="origin">This portal's sphere centre.</param>
    /// <returns>True when the other portal is close enough to matter.</returns>
    /// <remarks>
    /// The squared distance is accumulated in FLOAT and only then widened to the
    /// double it is compared against, because that is what the C++ expression
    /// does: every operand is a <c>vec_t</c>, so the sum is a float, and the
    /// assignment to <c>double dist2</c> happens afterwards.
    /// </remarks>
    private bool WithinRadius(ReadOnlySpan<Vec3> winding, Vec3 origin)
    {
        // flow.cpp:780 -- 32000 squared, the seed for the minimum.
        double minimum = 1024000000.0;

        for (int k = 0; k < winding.Length; k++)
        {
            Vec3 segment = winding[k] - origin;
            float squared = (segment.X * segment.X) + (segment.Y * segment.Y) + (segment.Z * segment.Z);
            if (squared < minimum)
            {
                minimum = squared;
            }
        }

        return minimum <= _radiusSquared;
    }
}
