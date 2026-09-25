using System.Buffers.Binary;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The <c>CPhysicsCollision</c> queries vbsp asks of a cooked collide: volumes (written into the
/// keydata as <c>"volume"</c> and used for mass), the axis-aligned bounds, and the extent along a
/// Direction. Mirrors the reference collision queries, with every float expression grouped as
/// The stock and TF2 builds emit it (this wrapper code is identical in both).
/// </summary>
internal static class IvpCollideQueries
{
    /// <summary><c>g_PhysicsUnits.unitScaleMetersInv</c> = 1/0.0254f.</summary>
    public const float IvpToHl = 1.0f / 0.0254f;

    /// <summary>
    /// <c>CPhysicsCollision::ConvexVolume</c> (stock): the tetrahedra from the ledge's first
    /// point to each triangle, in HL units. The reference implementation works on the scaled IVP axes and folds the
    /// axis swap into signs; this is that expression, operation for operation.
    /// </summary>
    /// <param name="ledge">The convex.</param>
    /// <returns>Cubic inches.</returns>
    public static float ConvexVolume(IvpCompactLedge ledge)
    {
        float s = IvpToHl;
        (float x0, float y0, float z0) = ledge.Point(ledge.EdgeStart(0, 0));
        float vx = x0 * s, vy = y0 * s, vz = z0 * s;
        float volume = 0f;
        int n = ledge.TriangleCount;
        for (int t = 0; t < n; t++)
        {
            (float ax, float ay, float az) = ledge.Point(ledge.EdgeStart(t, 0));
            (float bx, float by, float bz) = ledge.Point(ledge.EdgeStart(t, 1));
            (float cx, float cy, float cz) = ledge.Point(ledge.EdgeStart(t, 2));
            ax *= s;
            ay *= s;
            az *= s;
            bx *= s;
            by *= s;
            bz *= s;
            cx *= s;
            cy *= s;
            cz *= s;
            float aX = ax - vx;
            float negBy = vy - by;
            float bZ = bz - vz;
            float negCy = vy - cy;
            float bX = bx - vx;
            float cX = cx - vx;
            float cZ = cz - vz;
            float x = (bZ * negCy) - (cZ * negBy);
            float z = (negBy * cX) - (negCy * bX);
            float y = (cZ * bX) - (bZ * cX);
            float aZ = az - vz;
            float negAy = vy - ay;
            float sum = (y * negAy) + ((x * aX) + (z * aZ));
            volume += MathF.Abs(sum * 0.1666666716337204f);
        }

        return volume;
    }

    /// <summary>The compact surface inside a VPHY blob.</summary>
    /// <param name="vphy">The blob.</param>
    /// <returns>The surface's bytes.</returns>
    public static ReadOnlySpan<byte> Surface(ReadOnlySpan<byte> vphy) => vphy[VphyWriter.HeaderSize..];

    /// <summary>The leaf ledges of a compact surface's tree, left first (IVP <c>get_all_ledges</c>).</summary>
    /// <param name="surface">The compact surface.</param>
    /// <returns>Each leaf ledge, copied out.</returns>
    public static List<IvpCompactLedge> Leaves(ReadOnlySpan<byte> surface)
    {
        var leaves = new List<IvpCompactLedge>();
        foreach (int at in LeafOffsets(surface))
        {
            leaves.Add(LedgeAt(surface, at));
        }

        return leaves;
    }

    /// <summary>The byte offsets of the leaf ledges in a compact surface, left first.</summary>
    /// <param name="surface">The compact surface.</param>
    /// <returns>The offsets.</returns>
    public static List<int> LeafOffsets(ReadOnlySpan<byte> surface)
    {
        var offsets = new List<int>();
        int root = BinaryPrimitives.ReadInt32LittleEndian(surface[0x20..]);
        Collect(surface, root, offsets);
        return offsets;
    }

    /// <summary>A copy of the ledge at a byte offset (header size_div_16 bytes).</summary>
    /// <param name="surface">The compact surface.</param>
    /// <param name="at">The ledge's offset.</param>
    /// <returns>The copy.</returns>
    public static IvpCompactLedge LedgeAt(ReadOnlySpan<byte> surface, int at)
    {
        int size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(surface[(at + 8)..]) >> 8) * 16;
        return new IvpCompactLedge(surface.Slice(at, size).ToArray());
    }

    /// <summary>
    /// The root node's own ledge (<c>get_compact_hull</c>): the outer hull a multi-convex compile
    /// built, or -1 when the root is a leaf or has none.
    /// </summary>
    /// <param name="surface">The compact surface.</param>
    /// <returns>The hull ledge's offset, or -1.</returns>
    public static int RootHullOffset(ReadOnlySpan<byte> surface)
    {
        int root = BinaryPrimitives.ReadInt32LittleEndian(surface[0x20..]);
        int right = BinaryPrimitives.ReadInt32LittleEndian(surface[root..]);
        int ledge = BinaryPrimitives.ReadInt32LittleEndian(surface[(root + 4)..]);
        return right == 0 || ledge == 0 ? -1 : root + ledge;
    }

    private static void Collect(ReadOnlySpan<byte> surface, int node, List<int> into)
    {
        while (true)
        {
            int right = BinaryPrimitives.ReadInt32LittleEndian(surface[node..]);
            if (right == 0)
            {
                into.Add(node + BinaryPrimitives.ReadInt32LittleEndian(surface[(node + 4)..]));
                return;
            }

            Collect(surface, node + 28, into);
            node += right;
        }
    }

    /// <summary><c>CPhysicsCollision::CollideVolume</c> (SDK): the leaves' volumes summed.</summary>
    /// <param name="vphy">The blob.</param>
    /// <returns>Cubic inches.</returns>
    public static float CollideVolume(ReadOnlySpan<byte> vphy) => SurfaceVolume(Surface(vphy));

    /// <summary><see cref="CollideVolume"/> over a bare compact surface.</summary>
    /// <param name="surface">The compact surface.</param>
    /// <returns>Cubic inches.</returns>
    public static float SurfaceVolume(ReadOnlySpan<byte> surface)
    {
        float volume = 0f;
        foreach (IvpCompactLedge ledge in Leaves(surface))
        {
            volume = ConvexVolume(ledge) + volume;
        }

        return volume;
    }

    /// <summary>A ledge point in HL units: (x*s, z*s, -(y*s)) (the reference implementation's IVP-to-HL matrix at the origin).</summary>
    /// <param name="ledge">The ledge.</param>
    /// <param name="point">Point index.</param>
    /// <returns>The HL position.</returns>
    public static (float X, float Y, float Z) HlPoint(IvpCompactLedge ledge, int point)
    {
        (float x, float y, float z) = ledge.Point(point);
        return (x * IvpToHl, z * IvpToHl, -(y * IvpToHl));
    }

    /// <summary>
    /// <c>CPhysicsTrace::GetAABB</c> at the origin: the support points along the six axes, which are
    /// the per-axis extremes of the leaves' HL points.
    /// </summary>
    /// <param name="vphy">The blob.</param>
    /// <returns>Mins and maxs.</returns>
    public static ((float X, float Y, float Z) Mins, (float X, float Y, float Z) Maxs) CollideGetAabb(ReadOnlySpan<byte> vphy) =>
        SurfaceAabb(Surface(vphy));

    /// <summary><see cref="CollideGetAabb"/> over a bare compact surface.</summary>
    /// <param name="surface">The compact surface.</param>
    /// <returns>Mins and maxs.</returns>
    public static ((float X, float Y, float Z) Mins, (float X, float Y, float Z) Maxs) SurfaceAabb(ReadOnlySpan<byte> surface)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
        float x1 = -float.MaxValue, y1 = -float.MaxValue, z1 = -float.MaxValue;
        foreach (IvpCompactLedge ledge in Leaves(surface))
        {
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    (float x, float y, float z) = HlPoint(ledge, ledge.EdgeStart(t, e));
                    x0 = MathF.Min(x0, x);
                    y0 = MathF.Min(y0, y);
                    z0 = MathF.Min(z0, z);
                    x1 = MathF.Max(x1, x);
                    y1 = MathF.Max(y1, y);
                    z1 = MathF.Max(z1, z);
                }
            }
        }

        return ((x0, y0, z0), (x1, y1, z1));
    }

    /// <summary>
    /// <c>CPhysicsTrace::GetExtent</c> at the origin: the HL point that is furthest along
    /// <paramref name="direction"/> (for several leaves, the leaf whose support point has the
    /// strictly largest dot, starting from -1e6).
    /// </summary>
    /// <remarks>
    /// On an exact tie between points of one leaf the native library's pick depends on its SIMD
    /// vertex cache or hill-climb order; this returns the lowest-index point. Every caller in vbsp
    /// uses only the dot product with <paramref name="direction"/>, which ties do not change.
    /// </remarks>
    /// <param name="vphy">The blob.</param>
    /// <param name="direction">The direction (HL).</param>
    /// <returns>The extreme point.</returns>
    public static (float X, float Y, float Z) CollideGetExtent(ReadOnlySpan<byte> vphy, (float X, float Y, float Z) direction) =>
        SurfaceExtent(Surface(vphy), direction);

    /// <summary><see cref="CollideGetExtent"/> over a bare compact surface.</summary>
    /// <param name="surface">The compact surface.</param>
    /// <param name="direction">The direction (HL).</param>
    /// <returns>The extreme point.</returns>
    public static (float X, float Y, float Z) SurfaceExtent(ReadOnlySpan<byte> surface, (float X, float Y, float Z) direction)
    {
        List<IvpCompactLedge> leaves = Leaves(surface);
        (float X, float Y, float Z) best = (0f, 0f, 0f);
        float bestDot = -1e6f;
        foreach (IvpCompactLedge ledge in leaves)
        {
            (float X, float Y, float Z) p = Support(ledge, direction);
            float dot = ((p.X * direction.X) + (p.Y * direction.Y)) + (p.Z * direction.Z);
            if (leaves.Count == 1 || dot > bestDot)
            {
                bestDot = dot;
                best = p;
            }
        }

        return best;
    }

    private static (float X, float Y, float Z) Support(IvpCompactLedge ledge, (float X, float Y, float Z) d)
    {
        (float X, float Y, float Z) best = default;
        float bestDot = float.NegativeInfinity;
        for (int i = 0; i < ledge.PointCount; i++)
        {
            (float x, float y, float z) = HlPoint(ledge, i);
            float dot = ((x * d.X) + (y * d.Y)) + (z * d.Z);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = (x, y, z);
            }
        }

        return best;
    }
}
