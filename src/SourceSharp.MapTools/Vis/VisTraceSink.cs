using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Where a <c>-trace</c> run's line strip is collected
/// </summary>
/// <remarks>
/// <para>
/// Stock keeps one global <c>CUtlVector&lt;Vector&gt;</c> behind a mutex and
/// takes the FIRST trace any thread finds, discarding every later one
/// That makes the answer depend on which worker won,
/// which is fine for a debugging aid whose job is to draw ONE route and is
/// preserved here rather than improved -- a trace that concatenated every route
/// would not be the feature stock has.
/// </para>
/// <para>
/// The one stock behaviour NOT reproduced is what its list walk reads. The walk
/// starts at the head frame and follows <c>next</c> pointers into
/// <c>RecursiveLeafFlow</c>'s stack locals; the chain happens to be correct
/// because <c>stack.next = NULL</c> is re-set on every loop iteration
/// Before the recursion, and nothing here depends on that
/// accident -- the chain is passed down explicitly instead.
/// </para>
/// </remarks>
internal sealed class VisTraceSink
{
    private readonly Lock _gate = new();
    private List<Vec3>? _points;

    /// <summary>Sets up a trace between two clusters.</summary>
    /// <param name="start">The cluster to trace from.</param>
    /// <param name="stop">The cluster to trace to.</param>
    internal VisTraceSink(int start, int stop)
    {
        Start = start;
        Stop = stop;
    }

    /// <summary>The cluster the trace starts in.</summary>
    internal int Start { get; }

    /// <summary>The cluster that ends the trace when the flood reaches it.</summary>
    internal int Stop { get; }

    /// <summary>The captured strip, or null when no route was found.</summary>
    internal IReadOnlyList<Vec3>? Points
    {
        get
        {
            lock (_gate)
            {
                return _points;
            }
        }
    }

    /// <summary>Records one route, if none has been recorded yet.</summary>
    /// <param name="portals">The map's portals, for the cluster centres.</param>
    /// <param name="chain">
    /// The windings from the base portal down to the frame that reached
    /// <see cref="Stop"/>, in order.
    /// </param>
    internal void Capture(PortalSet portals, IReadOnlyList<Vec3[]> chain)
    {
        lock (_gate)
        {
            if (_points is not null)
            {
                return;
            }

            List<Vec3> points = [ClusterCenter(portals, Start)];

            foreach (Vec3[] winding in chain)
            {
                // -- a fan from the winding's centre to each
                // point, then the ring, then back to the centre. Drawn as a
                // polyline, that is a portal you can see in the editor.
                Vec3 mid = WindingCenter(winding);
                points.Add(mid);

                foreach (Vec3 point in winding)
                {
                    points.Add(point);
                    points.Add(mid);
                }

                foreach (Vec3 point in winding)
                {
                    points.Add(point);
                }

                points.Add(winding[0]);
                points.Add(mid);
            }

            points.Add(ClusterCenter(portals, Stop));
            _points = points;
        }
    }

    /// <summary><c>WindingCenter</c>.</summary>
    /// <param name="winding">The winding.</param>
    /// <returns>The mean of its points.</returns>
    /// <remarks>
    /// The scale is formed as <c>1.0 / numpoints</c> in DOUBLE and then stored
    /// in a float before the multiply, which is not the same as dividing each
    /// component by the count.
    /// </remarks>
    internal static Vec3 WindingCenter(ReadOnlySpan<Vec3> winding)
    {
        Vec3 sum = default;
        foreach (Vec3 point in winding)
        {
            sum += point;
        }

        float scale = (float)(1.0 / winding.Length);
        return sum * scale;
    }

    /// <summary><c>ClusterCenter</c>.</summary>
    /// <param name="portals">The map's portals.</param>
    /// <param name="cluster">A cluster index.</param>
    /// <returns>The midpoint of the bounds of every portal winding in it.</returns>
    internal static Vec3 ClusterCenter(PortalSet portals, int cluster)
    {
        float minX = 99999f;
        float minY = 99999f;
        float minZ = 99999f;
        float maxX = -99999f;
        float maxY = -99999f;
        float maxZ = -99999f;

        foreach (int portal in portals.ClusterPortals(cluster))
        {
            foreach (Vec3 point in portals.Winding(portal))
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                minZ = Math.Min(minZ, point.Z);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
                maxZ = Math.Max(maxZ, point.Z);
            }
        }

        return new Vec3(minX + maxX, minY + maxY, minZ + maxZ) * 0.5f;
    }
}
