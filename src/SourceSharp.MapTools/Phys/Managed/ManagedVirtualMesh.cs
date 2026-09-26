//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <c>CreateVirtualMesh</c> with <c>buildOuterHull</c>: the only part of a virtual mesh that is
/// serialised, its packed bounding hull (LUMP_PHYSDISP's per-displacement blob).
/// </summary>
/// <remarks>
/// Mirrors the reference mesh-hull path
/// (<c>CreateMeshBoundingHull</c>, <c>CreateBoundingSurfaceFromRange</c>, <c>SerializeToBuffer</c>)
/// and (<c>LedgeCanBePacked</c>, <c>CreatePackedHullFromLedges</c>,
/// <c>PackLedgeIntoBuffer</c>, <c>BuildVertMap</c>). The hull itself is the ledge soup compile's
/// root convex hull over one two-sided triangle ledge per mesh triangle.
/// </remarks>
internal static class ManagedVirtualMesh
{
    /// <summary>Builds the packed hull of a mesh, as <c>CreateMeshBoundingHull</c> does.</summary>
    /// <param name="mesh">The mesh (HL units).</param>
    /// <param name="build">The cook arithmetic.</param>
    /// <returns>The <c>virtualmeshhull_t</c> bytes, or null when the mesh has no triangles.</returns>
    public static byte[]? BuildPackedHull(VirtualMeshSource mesh, IIvpBuild build)
    {
        int triangleCount = mesh.Indices.Length / 3;
        if (triangleCount == 0)
        {
            return null;
        }

        int indexCount = mesh.Indices.Length;
        IvpCompactLedge? hull = BoundingHull(mesh, 0, indexCount, build);
        if (hull is null)
        {
            return null;
        }

        if (CanBePacked(hull, mesh))
        {
            return Pack(mesh, [hull]);
        }

        // "too big to pack to 8-bits, split in two"
        IvpCompactLedge? h0 = BoundingHull(mesh, 0, indexCount / 2, build);
        IvpCompactLedge? h1 = BoundingHull(mesh, indexCount / 2, indexCount / 2, build);
        if (h0 is null || h1 is null)
        {
            return null;
        }

        return Pack(mesh, [h0, h1]);
    }

    /// <summary>
    /// <c>CreateBoundingSurfaceFromRange</c>: one point-soup ledge per triangle of the range, compiled
    /// with <c>build_root_convex_hull</c>; the root's hull ledge.
    /// </summary>
    private static IvpCompactLedge? BoundingHull(VirtualMeshSource mesh, int firstIndex, int indexCount, IIvpBuild build)
    {
        int lastIndex = firstIndex + indexCount;
        int firstTriangle = firstIndex / 3;
        int lastTriangle = lastIndex / 3;
        var ledges = new List<IvpCompactLedge>();
        Span<(float X, float Y, float Z)> tri = stackalloc (float, float, float)[3];
        for (int i = firstTriangle; i < lastTriangle; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                Vec3 v = mesh.Vertices[mesh.Indices[(i * 3) + k]];
                tri[k] = (v.X, v.Y, v.Z);
            }

            IvpCompactLedge? ledge = build.ConvexFromVertsFast(tri);
            if (ledge is not null)
            {
                ledges.Add(ledge);
            }
        }

        byte[]? surface = build.Compile(ledges, buildRootConvexHull: true);
        if (surface is null)
        {
            return null;
        }

        int hullAt = IvpCollideQueries.RootHullOffset(surface);
        return hullAt < 0 ? null : IvpCollideQueries.LedgeAt(surface, hullAt);
    }

    /// <summary><c>LedgeCanBePacked</c>.</summary>
    private static bool CanBePacked(IvpCompactLedge ledge, VirtualMeshSource mesh)
    {
        if (ledge.TriangleCount * 3 > 512)
        {
            return false;
        }

        VertMap map = BuildVertMap(mesh, ledge);
        return map.MaxRef - map.MinRef <= 255;
    }

    private sealed class VertMap(int size, int vertexCount)
    {
        public int[] Map { get; } = Enumerable.Repeat(-1, size).ToArray();

        public int MinRef { get; set; } = vertexCount;

        public int MaxRef { get; set; }
    }

    /// <summary>
    /// <c>BuildVertMap</c>: each ledge point referenced by a triangle, in triangle/edge order, mapped
    /// to the nearest mesh vertex (first minimum; float distance).
    /// </summary>
    private static VertMap BuildVertMap(VirtualMeshSource mesh, IvpCompactLedge ledge)
    {
        int maxIndex = -1;
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            for (int j = 0; j < 3; j++)
            {
                maxIndex = Math.Max(maxIndex, ledge.EdgeStart(t, j));
            }
        }

        var map = new VertMap(maxIndex + 1, mesh.Vertices.Length);
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            for (int j = 0; j < 3; j++)
            {
                int ivp = ledge.EdgeStart(t, j);
                if (map.Map[ivp] >= 0)
                {
                    continue;
                }

                (float hx, float hy, float hz) = IvpCollideQueries.HlPoint(ledge, ivp);
                int index = -1;
                float minDist = 1e24f;
                for (int k = 0; k < mesh.Vertices.Length; k++)
                {
                    Vec3 v = mesh.Vertices[k];
                    float dx = hx - v.X, dy = hy - v.Y, dz = hz - v.Z;
                    float dist = MathF.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
                    if (dist < minDist)
                    {
                        index = k;
                        minDist = dist;
                    }
                }

                map.Map[ivp] = index;
                map.MinRef = Math.Min(map.MinRef, index);
                map.MaxRef = Math.Max(map.MaxRef, index);
            }
        }

        return map;
    }

    /// <summary><c>EdgeIndex</c>: triangle t, edge j is index 4t + 1 + j in 4-byte edge units.</summary>
    private static int EdgeIndex(int tri, int edge) => (4 * tri) + 1 + edge;

    /// <summary><c>CreatePackedHullFromLedges</c>.</summary>
    private static byte[] Pack(VirtualMeshSource mesh, IvpCompactLedge[] ledges)
    {
        var buf = new List<byte> { (byte)ledges.Length, 0, 0, 0 };
        int headers = buf.Count;
        foreach (IvpCompactLedge l in ledges)
        {
            int tris = l.TriangleCount;
            buf.AddRange([(byte)tris, 0, (byte)((tris * 3) / 2), 0, 0]);
        }

        for (int i = 0; i < ledges.Length; i++)
        {
            PackLedge(buf, headers + (5 * i), ledges[i], mesh);
        }

        return [.. buf];
    }

    /// <summary><c>PackLedgeIntoBuffer</c>: virtual triangles and edges first, half the edges.</summary>
    private static void PackLedge(List<byte> buf, int header, IvpCompactLedge ledge, VirtualMeshSource mesh)
    {
        int n = ledge.TriangleCount;
        VertMap vertMap = BuildVertMap(mesh, ledge);
        buf[header + 4] = (byte)vertMap.MinRef;

        var triangleList = new List<int>();
        int[] triangleMap = Enumerable.Repeat(-1, n).ToArray();
        var edgeList = new List<int>();
        int[] edgeMap = Enumerable.Repeat(-1, n * 4).ToArray();

        bool TriVirtual(int t) => (ledge.TriangleWord(t) & 0x80000000u) != 0;
        bool EdgeVirtual(int t, int j) => (ledge.EdgeWord(t, j) & 0x80000000u) != 0;
        int Opposite(int t, int j)
        {
            (int ot, int oj) = ledge.Opposite(t, j);
            return EdgeIndex(ot, oj);
        }

        for (int i = 0; i < n; i++)
        {
            if (TriVirtual(i))
            {
                triangleMap[i] = triangleList.Count;
                triangleList.Add(i);
            }
        }

        buf[header + 1] = (byte)triangleList.Count;
        for (int i = 0; i < n; i++)
        {
            if (!TriVirtual(i))
            {
                triangleMap[i] = triangleList.Count;
                triangleList.Add(i);
            }
        }

        foreach (int t in triangleList)
        {
            for (int j = 0; j < 3; j++)
            {
                if (EdgeVirtual(t, j) && edgeMap[Opposite(t, j)] < 0)
                {
                    edgeMap[EdgeIndex(t, j)] = edgeList.Count;
                    edgeList.Add(EdgeIndex(t, j));
                }
            }
        }

        buf[header + 3] = (byte)edgeList.Count;
        foreach (int t in triangleList)
        {
            for (int j = 0; j < 3; j++)
            {
                int index = EdgeIndex(t, j);
                int opposite = Opposite(t, j);
                if (!EdgeVirtual(t, j) && edgeMap[opposite] < 0)
                {
                    edgeMap[index] = edgeList.Count;
                    edgeList.Add(index);
                }

                if (edgeMap[index] < 0)
                {
                    edgeMap[index] = edgeMap[opposite];
                }
            }
        }

        foreach (int t in triangleList)
        {
            int pierce = (int)((ledge.TriangleWord(t) >> 12) & 0xfff);
            buf.Add((byte)edgeMap[EdgeIndex(t, 0)]);
            buf.Add((byte)edgeMap[EdgeIndex(t, 1)]);
            buf.Add((byte)edgeMap[EdgeIndex(t, 2)]);
            buf.Add((byte)triangleMap[pierce]);
        }

        foreach (int e in edgeList)
        {
            int t = e / 4, j = (e % 4) - 1;
            int v0 = vertMap.Map[ledge.EdgeStart(t, j)] - vertMap.MinRef;
            int v1 = vertMap.Map[ledge.NextStart(t, j)] - vertMap.MinRef;
            buf.Add((byte)v0);
            buf.Add((byte)v1);
        }
    }
}
