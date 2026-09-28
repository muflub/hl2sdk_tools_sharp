//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The corner-point loop exactly as the reference runs it, kept only to hold the optimised
/// <see cref="IvpHalfspaceSoup{T, TP}.CornerPoints"/> to it: every triple <c>i &lt; j &lt; k</c>
/// in order, solved by <see cref="IvpHalfspaceSoup{T, TP}.Intersect"/>, a front-to-back scan of
/// the whole soup for the inside test, and an oldest-first merge on insertion.
/// </summary>
/// <remarks>
/// This is the code the cooker shipped before the loop was optimised, unchanged but for its name.
/// It is O(n^4) in the plane count; do not call it from anything but a fact.
/// </remarks>
/// <typeparam name="T">IVP_DOUBLE.</typeparam>
/// <typeparam name="TP">The precision policy.</typeparam>
internal static class IvpCornerPointsReference<T, TP>
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <summary>The reference corner points.</summary>
    /// <param name="soup">The halfspaces.</param>
    /// <param name="merge">Merge distance, IVP units.</param>
    /// <returns>The corner points, in discovery order.</returns>
    public static List<IvpPoint<T>> CornerPoints(List<IvpPoint<T>> soup, T merge)
    {
        T merge2 = merge * merge;
        T outside = T.CreateTruncating(-1.0e-4f);
        var points = new List<IvpPoint<T>>();
        int n = soup.Count;
        for (int i = 0; i + 1 < n; i++)
        {
            for (int j = i + 1; j + 1 < n; j++)
            {
                for (int k = j + 1; k < n; k++)
                {
                    if (!IvpHalfspaceSoup<T, TP>.Intersect(soup[i], soup[j], soup[k], out T px, out T py, out T pz))
                    {
                        continue;
                    }

                    bool inside = true;
                    foreach (IvpPoint<T> h in soup)
                    {
                        T v = ((h.Z * pz) + h.W) + ((h.Y * py) + (h.X * px));
                        if (outside > v)
                        {
                            inside = false;
                            break;
                        }
                    }

                    if (inside)
                    {
                        InsertMerged(new IvpPoint<T>(px, py, pz, T.Zero), points, merge2);
                    }
                }
            }
        }

        // Second pass: drop later points within the merge distance of an earlier one. The inner
        // index runs from the list end as it stood when the outer point was taken.
        for (int i = 0; i < points.Count; i++)
        {
            IvpPoint<T> p = points[i];
            for (int j = points.Count - 1; j > i; j--)
            {
                if (j >= points.Count)
                {
                    continue;
                }

                IvpPoint<T> q = points[j];
                T dx = p.X - q.X;
                T dy = p.Y - q.Y;
                T dz = p.Z - q.Z;
                if (((dx * dx) + (dy * dy)) + (dz * dz) < merge2)
                {
                    points.RemoveAt(points.LastIndexOf(q));
                }
            }
        }

        return points;
    }

    private static void InsertMerged(IvpPoint<T> p, List<IvpPoint<T>> points, T merge2)
    {
        foreach (IvpPoint<T> q in points)
        {
            T dx = p.X - q.X;
            T dy = p.Y - q.Y;
            T dz = p.Z - q.Z;
            if (((dx * dx) + (dy * dy)) + (dz * dz) < merge2)
            {
                return;
            }
        }

        points.Add(p);
    }
}
