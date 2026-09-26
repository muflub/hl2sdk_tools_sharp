//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The three-point fast path: the three-point soup becomes a flat two-sided ledge, two triangles
/// back to back, unless its area is essentially zero.
/// </summary>
/// <remarks>
/// The reference implementation cooks a unit triangle once and caches it in a global,
/// Then copies that ledge and overwrites its three points. The cached
/// topology is a constant, so it is written here directly (verified against the
/// Reference <c>ConvexFromVerts</c> of three points); no state survives a call.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpTriangleLedge<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>Builds the ledge, or null when |cross|^2 &lt; 1e-12.</summary>
    /// <param name="p1">First point.</param>
    /// <param name="p2">Second point.</param>
    /// <param name="p3">Third point.</param>
    /// <returns>The ledge.</returns>
    public static IvpCompactLedge? Build(IvpPoint<T> p1, IvpPoint<T> p2, IvpPoint<T> p3)
    {
        T ax = p2.X - p1.X, ay = p2.Y - p1.Y, az = p2.Z - p1.Z;
        T bx = p3.X - p1.X, by = p3.Y - p1.Y, bz = p3.Z - p1.Z;
        T cx = (az * by) - (ay * bz);
        T cy = (bz * ax) - (az * bx);
        T cz = (ay * bx) - (ax * by);
        T len2 = ((cy * cy) + (cx * cx)) + (cz * cz);
        if (T.CreateTruncating(1.0e-12f) > len2)
        {
            return null;
        }

        IvpCompactLedge ledge = IvpCompactLedge.Create(2, 3);

        // The cached unit-triangle ledge: tri 0 pierces 1 and back; edges (start, opposite).
        ledge.SetTriangleWord(0, 0u | (1u << 12));
        ledge.SetEdgeWord(0, 0, 0u | (4u << 16));
        ledge.SetEdgeWord(0, 1, 1u | (5u << 16));
        ledge.SetEdgeWord(0, 2, 2u | (3u << 16));
        ledge.SetTriangleWord(1, 1u);
        ledge.SetEdgeWord(1, 0, 1u | ((uint)(-4 & 0x7fff) << 16));
        ledge.SetEdgeWord(1, 1, 0u | ((uint)(-3 & 0x7fff) << 16));
        ledge.SetEdgeWord(1, 2, 2u | ((uint)(-5 & 0x7fff) << 16));
        ledge.SetPoint(0, float.CreateTruncating(p1.X), float.CreateTruncating(p1.Y), float.CreateTruncating(p1.Z));
        ledge.SetPoint(1, float.CreateTruncating(p2.X), float.CreateTruncating(p2.Y), float.CreateTruncating(p2.Z));
        ledge.SetPoint(2, float.CreateTruncating(p3.X), float.CreateTruncating(p3.Y), float.CreateTruncating(p3.Z));
        return ledge;
    }
}
