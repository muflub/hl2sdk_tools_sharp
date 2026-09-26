//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Sews displacement normals across neighbours so lighting has no seams:
/// <c>SmoothNeighboringDispSurfNormals</c> and its three passes,
/// </summary>
/// <remarks>
/// <para>
/// vrad's, not vbsp's: nothing vbsp writes contains a normal. It lives with
/// the displacement core because it is pure <see cref="CoreDispInfo"/>
/// arithmetic over the neighbour tables this lane builds, and vrad's lane
/// reuses it unchanged.
/// </para>
/// <para>
/// ORDER MATTERS and is stock's: T-junctions, then corners, then edges
/// Each pass over the list in index order and
/// each writing the averaged normal into every participant before the next
/// vertex is visited — so a later average reads earlier averages, and the
/// result depends on list order. Serial by construction.
/// </para>
/// </remarks>
public static class DispNormalSmoother
{
    /// <summary>
    /// How close a neighbour's corner must be to count as the same point:
    /// <c>FindNeighborCornerVert</c>'s <c>0.1f</c>.
    /// </summary>
    public const float CornerMatchDistance = 0.1f;

    /// <summary>
    /// The cap on <see cref="GetAllNeighbors"/>: its <c>int[512]</c>,
    /// </summary>
    public const int MaxNeighbors = 512;

    /// <summary>
    /// All three passes: <c>SmoothNeighboringDispSurfNormals</c>,
    /// </summary>
    /// <param name="list">Every displacement, in LUMP_DISPINFO order, created.</param>
    /// <param name="stockNormalise">
    /// Reproduce stock's <c>VectorNormalize</c> estimate. See
    /// <see cref="Options.StockQuirk.DispVertNormalise"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    public static void SmoothNeighboringDispSurfNormals(
        IReadOnlyList<CoreDispInfo> list, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(list);

        BlendTJuncs(list, stockNormalise);
        BlendCorners(list, stockNormalise);
        BlendEdges(list, stockNormalise);
    }

    /// <summary>
    /// Which of a displacement's four corners lies within
    /// <see cref="CornerMatchDistance"/> of a point:
    /// <c>FindNeighborCornerVert</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="test">The point.</param>
    /// <returns>The corner, or -1.</returns>
    /// <remarks>
    /// The CLOSEST corner, first one winning a tie (strict <c>&lt;</c>), and
    /// only then the distance test (<c>&lt;= 0.1</c>). Measured against the
    /// DISPLACED corners.
    /// </remarks>
    public static int FindNeighborCornerVert(CoreDispInfo disp, Vec3 test)
    {
        ArgumentNullException.ThrowIfNull(disp);

        int closest = 0;
        float closestDist = 1e24f;

        for (int corner = 0; corner < 4; corner++)
        {
            float dist = (disp.CornerPoint(corner) - test).Length();
            if (dist < closestDist)
            {
                closest = corner;
                closestDist = dist;
            }
        }

        return closestDist <= CornerMatchDistance ? closest : -1;
    }

    /// <summary>
    /// Every neighbour index, corners first then edges:
    /// <c>GetAllNeighbors</c>.
    /// </summary>
    /// <param name="disp">The displacement.</param>
    /// <returns>
    /// The list, not de-duplicated. In real data there is nothing to
    /// de-duplicate: <c>SetupCornerNeighbors</c> refuses a displacement that
    /// is already an edge neighbour, so only a
    /// hand-built table can list one twice.
    /// </returns>
    public static List<int> GetAllNeighbors(CoreDispInfo disp)
    {
        ArgumentNullException.ThrowIfNull(disp);

        List<int> neighbors = [];

        for (int corner = 0; corner < 4; corner++)
        {
            DispCornerNeighbors c = disp.CornerNeighbors(corner);
            for (int i = 0; i < c.NumNeighbors; i++)
            {
                if (neighbors.Count < MaxNeighbors)
                {
                    neighbors.Add(c.Neighbors[i]);
                }
            }
        }

        for (int edge = 0; edge < 4; edge++)
        {
            DispNeighbor e = disp.EdgeNeighbor(edge);
            for (int i = 0; i < 2; i++)
            {
                if (e.SubNeighbors[i].IsValid() && neighbors.Count < MaxNeighbors)
                {
                    neighbors.Add(e.SubNeighbors[i].Neighbor);
                }
            }
        }

        return neighbors;
    }

    /// <summary>
    /// Averages each corner's normal with every neighbour corner at the same
    /// point: <c>BlendCorners</c>.
    /// </summary>
    /// <param name="list">Every displacement.</param>
    /// <param name="stockNormalise">Stock's normalise estimate.</param>
    public static void BlendCorners(IReadOnlyList<CoreDispInfo> list, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(list);

        foreach (CoreDispInfo disp in list)
        {
            List<int> neighbors = GetAllNeighbors(disp);
            int[] nbCornerVerts = new int[neighbors.Count];

            for (int corner = 0; corner < 4; corner++)
            {
                int cornerVert = disp.PowerInfo.VertIndexToInt(disp.PowerInfo.CornerPointIndex(corner));
                Vec3 cornerPos = disp.Vert(cornerVert);

                Vec3 average = disp.Normal(cornerVert);

                for (int n = 0; n < neighbors.Count; n++)
                {
                    CoreDispInfo nb = list[neighbors[n]];
                    int nbCorner = FindNeighborCornerVert(nb, cornerPos);

                    if (nbCorner == -1)
                    {
                        nbCornerVerts[n] = -1;
                    }
                    else
                    {
                        int nbVert = nb.PowerInfo.VertIndexToInt(nb.PowerInfo.CornerPointIndex(nbCorner));
                        nbCornerVerts[n] = nbVert;
                        average += nb.Normal(nbVert);
                    }
                }

                average = Normalise(average, stockNormalise);
                disp.SetNormal(cornerVert, average);

                for (int n = 0; n < neighbors.Count; n++)
                {
                    if (nbCornerVerts[n] != -1)
                    {
                        list[neighbors[n]].SetNormal(nbCornerVerts[n], average);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Where one wide edge meets two narrow ones, averages the wide edge's
    /// midpoint with the two narrow corners at it: <c>BlendTJuncs</c>,
    /// </summary>
    /// <param name="list">Every displacement.</param>
    /// <param name="stockNormalise">Stock's normalise estimate.</param>
    public static void BlendTJuncs(IReadOnlyList<CoreDispInfo> list, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(list);

        foreach (CoreDispInfo disp in list)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                DispNeighbor e = disp.EdgeNeighbor(edge);
                int midPoint = disp.PowerInfo.VertIndexToInt(disp.PowerInfo.EdgeMidPoint(edge));

                if (!e.SubNeighbors[0].IsValid() || !e.SubNeighbors[1].IsValid())
                {
                    continue;
                }

                Vec3 midPos = disp.Vert(midPoint);

                CoreDispInfo nb1 = list[e.SubNeighbors[0].Neighbor];
                CoreDispInfo nb2 = list[e.SubNeighbors[1].Neighbor];

                int c1 = FindNeighborCornerVert(nb1, midPos);
                int c2 = FindNeighborCornerVert(nb2, midPos);

                if (c1 == -1 || c2 == -1)
                {
                    continue;
                }

                VertIndex v1 = nb1.PowerInfo.CornerPointIndex(c1);
                VertIndex v2 = nb2.PowerInfo.CornerPointIndex(c2);

                Vec3 average = disp.Normal(midPoint);
                average += nb1.Normal(v1);
                average += nb2.Normal(v2);

                average = Normalise(average, stockNormalise);
                disp.SetNormal(midPoint, average);
                nb1.SetNormal(v1, average);
                nb2.SetNormal(v2, average);
            }
        }
    }

    /// <summary>
    /// Averages every shared interior edge vertex with its neighbour's, then
    /// re-interpolates the vertices a lower-power neighbour has no partner for:
    /// <c>BlendEdges</c>.
    /// </summary>
    /// <param name="list">Every displacement.</param>
    /// <param name="stockNormalise">Stock's normalise estimate.</param>
    /// <remarks>
    /// The walk starts ON the first corner (<c>bTouchCorners</c>) and blends
    /// neither end: the first is consumed as <c>viPrevPos</c>, the last is
    /// skipped by <c>IsLastVert</c> — corners are <see cref="BlendCorners"/>'
    /// job. The in-between vertices are lerped between the two walked vertices
    /// on THIS displacement, <c>RemapVal</c>'d as floats, and renormalised.
    /// </remarks>
    public static void BlendEdges(IReadOnlyList<CoreDispInfo> list, bool stockNormalise)
    {
        ArgumentNullException.ThrowIfNull(list);

        foreach (CoreDispInfo disp in list)
        {
            for (int edge = 0; edge < 4; edge++)
            {
                for (int sub = 0; sub < 2; sub++)
                {
                    DispSubNeighbor s = disp.EdgeNeighbor(edge).SubNeighbors[sub];
                    if (!s.IsValid())
                    {
                        continue;
                    }

                    CoreDispInfo nb = list[s.Neighbor];
                    int edgeDim = DispTables.EdgeDims[edge];
                    int freeDim = edgeDim == 0 ? 1 : 0;

                    DispSubEdgeIterator it = default;
                    it.Start(disp, edge, sub, touchCorners: true);

                    it.Next();
                    VertIndex prev = it.VertIndex;

                    while (it.Next())
                    {
                        if (!it.IsLastVert())
                        {
                            Vec3 average = disp.Normal(it.VertIndex) + nb.Normal(it.NeighborVertIndex);
                            average = Normalise(average, stockNormalise);

                            disp.SetNormal(it.VertIndex, average);
                            nb.SetNormal(it.NeighborVertIndex, average);
                        }

                        int prevPos = prev[freeDim];
                        int curPos = it.VertIndex[freeDim];

                        for (int tween = prevPos + 1; tween < curPos; tween++)
                        {
                            float percent = RemapVal(tween, prevPos, curPos, 0, 1);
                            Vec3 a = disp.Normal(prev);
                            Vec3 b = disp.Normal(it.VertIndex);
                            Vec3 lerped = new(
                                a.X + ((b.X - a.X) * percent),
                                a.Y + ((b.Y - a.Y) * percent),
                                a.Z + ((b.Z - a.Z) * percent));

                            VertIndex tweenIndex = default(VertIndex)
                                .With(edgeDim, it.VertIndex[edgeDim])
                                .With(freeDim, tween);
                            disp.SetNormal(tweenIndex, Normalise(lerped, stockNormalise));
                        }

                        prev = it.VertIndex;
                    }
                }
            }
        }
    }

    /// <summary><c>RemapVal</c>.</summary>
    /// <param name="val">The value.</param>
    /// <param name="a">Input low.</param>
    /// <param name="b">Input high.</param>
    /// <param name="c">Output low.</param>
    /// <param name="d">Output high.</param>
    /// <returns><c>c + (d - c) * (val - a) / (b - a)</c>, or a step when <c>a == b</c>.</returns>
    public static float RemapVal(float val, float a, float b, float c, float d)
    {
        if (a == b)
        {
            return val >= b ? d : c;
        }

        return c + ((d - c) * (val - a) / (b - a));
    }

    private static Vec3 Normalise(Vec3 v, bool stock) =>
        stock ? v.NormaliseLikeStock().Normalised : v.Normalise().Normalised;
}
