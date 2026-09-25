using System.Buffers.Binary;
using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The parts of the reference <c>CPhysicsCollision</c>
/// the cooker needs, over the IVP builders: convexes from planes or points, and a
/// collide from convexes, serialised as a <c>VPHY</c> blob.
/// </summary>
/// <remarks>
/// Mirrors the reference wrapper's cook path and was checked against stock output:
/// <c>ConvexFromPlanes</c>, <c>ConvexFromVerts</c> with
/// <c>ConvexFromVertsFast</c> and <c>RebuildConvexFromPlanes</c>,
/// <c>ConvertConvexToCollideParams</c>, and <c>CollideWrite</c>/<c>SerializeToBuffer</c>.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpCooker<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary><c>ConvexFromPlanes</c>: outward HL planes to a compact ledge.</summary>
    /// <param name="planes">Planes (nx, ny, nz, dist).</param>
    /// <param name="mergeDistance">Merge distance, HL units.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The ledge, or null.</returns>
    public static IvpCompactLedge? ConvexFromPlanes(
        ReadOnlySpan<(float X, float Y, float Z, float Distance)> planes, float mergeDistance, IvpCookContext context)
    {
        List<IvpPoint<T>> soup = IvpHalfspaceSoup<T, TP>.FromHlPlanes(planes, mergeDistance, out T merge);
        return HalfspacesToLedge(soup, merge, context);
    }

    /// <summary>: halfspaces to a ledge through their corner points.</summary>
    private static IvpCompactLedge? HalfspacesToLedge(List<IvpPoint<T>> soup, T merge, IvpCookContext context)
    {
        List<IvpPoint<T>> points = IvpHalfspaceSoup<T, TP>.CornerPoints(soup, merge);
        return IvpPointSoup<T, TP>.ToCompactLedge(points, context);
    }

    /// <summary>
    /// <c>ConvexFromVerts</c>: the hull of the points, then rebuilt from its own triangle planes
    /// with a 0.01 m merge "to remove interior coplanar verts"; the rebuilt ledge wins when there
    /// is one.
    /// </summary>
    /// <param name="points">HL points.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The ledge, or null.</returns>
    public static IvpCompactLedge? ConvexFromVerts(ReadOnlySpan<(float X, float Y, float Z)> points, IvpCookContext context)
    {
        IvpCompactLedge? fast = ConvexFromVertsFast(points, context);
        if (fast is null)
        {
            return null;
        }

        return RebuildFromPlanes(fast, T.CreateTruncating(0.01f), context) ?? fast;
    }

    /// <summary>
    /// <c>ConvexFromVertsFast</c> (also the point soup behind <c>PolysoupAddTriangle</c>,
    /// <c>BBoxToConvex</c> and the virtual mesh's bounding hull): the points converted with
    /// <c>ConvertPositionToIVP</c> and handed to <c>convert_pointsoup_to_compact_ledge</c>.
    /// </summary>
    /// <param name="points">HL points.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The ledge, or null.</returns>
    public static IvpCompactLedge? ConvexFromVertsFast(ReadOnlySpan<(float X, float Y, float Z)> points, IvpCookContext context)
    {
        const float Scale = IvpHalfspaceSoup<T, TP>.HlToIvp;
        var soup = new List<IvpPoint<T>>(points.Length);
        foreach ((float x, float y, float z) in points)
        {
            // ConvertPositionToIVP: (x, -z, y) * 0.0254f, each product rounded to float first.
            soup.Add(new IvpPoint<T>(
                T.CreateTruncating(x * Scale),
                T.CreateTruncating(-(z * Scale)),
                T.CreateTruncating(y * Scale),
                T.Zero));
        }

        return IvpPointSoup<T, TP>.ToCompactLedge(soup, context);
    }

    /// <summary> <c>RebuildConvexFromPlanes</c>.</summary>
    private static IvpCompactLedge? RebuildFromPlanes(IvpCompactLedge ledge, T merge, IvpCookContext context)
    {
        var soup = new List<IvpPoint<T>>(ledge.TriangleCount);
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            (float ax, float ay, float az) = ledge.Point(ledge.EdgeStart(t, 0));
            (float bx, float by, float bz) = ledge.Point(ledge.NextStart(t, 0));
            (float cx, float cy, float cz) = ledge.Point(ledge.PrevStart(t, 0));
            IvpPoint<T> h = CalcHesse(
                (T.CreateTruncating(ax), T.CreateTruncating(ay), T.CreateTruncating(az)),
                (T.CreateTruncating(bx), T.CreateTruncating(by), T.CreateTruncating(bz)),
                (T.CreateTruncating(cx), T.CreateTruncating(cy), T.CreateTruncating(cz)));
            T len = T.Sqrt(((h.X * h.X) + (h.Y * h.Y)) + (h.Z * h.Z));
            if (!(float.CreateTruncating(len) > 1.0e-6f))
            {
                continue;
            }

            T x = h.X, y = h.Y, z = h.Z, w = h.W;
            TP.NormizeHesse(ref x, ref y, ref z, ref w);
            IvpHalfspaceSoup<T, TP>.AddHalfspace(soup, new IvpPoint<T>(x, y, z, w));
        }

        return HalfspacesToLedge(soup, merge, context);
    }

    /// <summary>
    /// <c>IVP_U_Hesse::calc_hesse</c> as emitted (stock, TF2):
    /// with u = b - p, v = c - p: n = (v x u) grouped per component as stock does, and
    /// w = -((p.x*n.x + n.y*p.y) + n.z*p.z).
    /// </summary>
    /// <param name="p">Base point.</param>
    /// <param name="b">Second point.</param>
    /// <param name="c">Third point.</param>
    /// <returns>The plane.</returns>
    public static IvpPoint<T> CalcHesse((T X, T Y, T Z) p, (T X, T Y, T Z) b, (T X, T Y, T Z) c)
    {
        T uy = b.Y - p.Y, uz = b.Z - p.Z, ux = b.X - p.X;
        T vx = c.X - p.X, vy = c.Y - p.Y, vz = c.Z - p.Z;
        T nx = (uz * vy) - (uy * vz);
        T ny = (vz * ux) - (uz * vx);
        T nz = (uy * vx) - (ux * vy);
        T w = -(((p.X * nx) + (ny * p.Y)) + (nz * p.Z));
        return new IvpPoint<T>(nx, ny, nz, w);
    }

    /// <summary>
    /// <c>ConvertConvexToCollideParams</c>: the ledges become one compact surface; the result is the
    /// <c>VPHY</c> serialisation (<c>CPhysCollideCompactSurface::SerializeToBuffer</c>).
    /// </summary>
    /// <param name="ledges">Convexes, in order (null entries are skipped, as stock does).</param>
    /// <param name="buildOuterConvexHull">Build a root hull over more than one convex.</param>
    /// <param name="dragAxisAreas">The orthographic areas written into the header.</param>
    /// <param name="context">Scratch.</param>
    /// <returns>The blob, or null when there is nothing to cook.</returns>
    public static byte[]? ConvertConvexToCollide(
        IReadOnlyList<IvpCompactLedge?> ledges, bool buildOuterConvexHull, (float X, float Y, float Z) dragAxisAreas, IvpCookContext context)
    {
        var valid = new List<IvpCompactLedge>(ledges.Count);
        foreach (IvpCompactLedge? l in ledges)
        {
            if (l is not null)
            {
                valid.Add(l);
            }
        }

        if (valid.Count == 0)
        {
            return null;
        }

        byte[]? surface = IvpLedgeSoup<T, TP>.Compile(valid, buildOuterConvexHull, context);
        if (surface is null)
        {
            return null;
        }

        // dummy[0] (the collide index) = 0, dummy[2] = 'IVPS'.
        BinaryPrimitives.WriteInt32LittleEndian(surface.AsSpan(0x24), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(surface.AsSpan(0x2c), 0x53505649u);
        return VphyWriter.Serialize(surface, dragAxisAreas);
    }
}

/// <summary>
/// <c>compactsurfaceheader_t</c> + the surface.
/// </summary>
internal static class VphyWriter
{
    /// <summary>The header's size in bytes.</summary>
    public const int HeaderSize = 28;

    /// <summary>Serialises a compact surface.</summary>
    /// <param name="surface">The IVP compact surface, byte_size long.</param>
    /// <param name="dragAxisAreas">Orthographic areas.</param>
    /// <returns>VPHY header followed by the surface.</returns>
    public static byte[] Serialize(ReadOnlySpan<byte> surface, (float X, float Y, float Z) dragAxisAreas)
    {
        byte[] blob = new byte[HeaderSize + surface.Length];
        Span<byte> h = blob;
        h[0] = (byte)'V';
        h[1] = (byte)'P';
        h[2] = (byte)'H';
        h[3] = (byte)'Y';
        BinaryPrimitives.WriteInt16LittleEndian(h[4..], 0x0100);
        BinaryPrimitives.WriteInt16LittleEndian(h[6..], 0); // COLLIDE_POLY
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], surface.Length);
        BinaryPrimitives.WriteSingleLittleEndian(h[12..], dragAxisAreas.X);
        BinaryPrimitives.WriteSingleLittleEndian(h[16..], dragAxisAreas.Y);
        BinaryPrimitives.WriteSingleLittleEndian(h[20..], dragAxisAreas.Z);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], 0); // axisMapSize: "not yet supported"
        surface.CopyTo(blob.AsSpan(HeaderSize));
        return blob;
    }
}
