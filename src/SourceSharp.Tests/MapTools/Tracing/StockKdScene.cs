//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// A recorded KD-tree scene: the triangles, the rays, the tree STOCK built
/// from them, and the answers stock gave.
/// </summary>
/// <param name="Triangles">The scene, in the order the tree indexes it.</param>
/// <param name="Rays">The rays.</param>
/// <param name="NodeChildren">Stock's node words, one per node.</param>
/// <param name="NodeSplitBits">Stock's splitting planes, as raw bits.</param>
/// <param name="Indices">Stock's triangle index list.</param>
/// <param name="Min">Stock's scene lower bound.</param>
/// <param name="Max">Stock's scene upper bound.</param>
/// <param name="TriangleNormals">Each triangle's plane normal after conversion.</param>
/// <param name="TriangleD">Each triangle's plane distance.</param>
/// <param name="TriangleEdges">Each triangle's six projected edge coefficients.</param>
/// <param name="TriangleCoordSelect">Each triangle's two projection axes.</param>
/// <param name="StockHitId">Stock's hit TRIANGLE INDEX per ray, or -1.</param>
/// <param name="StockDistance">Stock's hit distance per ray.</param>
/// <remarks>
/// Produced by compiling UNCHANGED and
/// linking it against a small driver -- the whole tracer, not an extract, so
/// the tree below is the reference build's SAH and not a reading of it.
/// <c>Fixtures/README-kd-scene.md</c> carries the command.
/// </remarks>
internal sealed record StockKdScene(
    TracedTriangle[] Triangles,
    Ray[] Rays,
    int[] NodeChildren,
    int[] NodeSplitBits,
    int[] Indices,
    Vec3 Min,
    Vec3 Max,
    Vec3[] TriangleNormals,
    float[] TriangleD,
    float[] TriangleEdges,
    int[] TriangleCoordSelect,
    int[] StockHitId,
    float[] StockDistance)
{
    /// <summary>How many rays the set holds.</summary>
    public int RayCount => Rays.Length;

    /// <summary>
    /// Loads the committed scene from THIS worktree.
    /// </summary>
    /// <returns>The scene, the tree and stock's answers.</returns>
    /// <exception cref="InvalidOperationException">A file is missing or short.</exception>
    public static StockKdScene Load() => Load("kd-scene");

    /// <summary>
    /// Loads one of the committed scenes by name.
    /// </summary>
    /// <param name="prefix">
    /// <c>kd-scene</c> for the general-position one, <c>kd-boxes</c> for the
    /// axis-aligned one.
    /// </param>
    /// <returns>The scene, the tree and stock's answers.</returns>
    /// <exception cref="InvalidOperationException">A file is missing or short.</exception>
    /// <remarks>
    /// TWO SCENES, because one cannot reach all of stock's rules. Random
    /// triangles in general position never make
    /// <c>ClassifyAgainstAxisSplit</c>'s <c>minc == maxc</c> true, never leave
    /// one side of a split wholly empty, and never reach the depth cap.
    /// mutation-testing the build against that scene alone left four of
    /// stock's rules unverified, each mutation staying green because it never
    /// reached its subject. A room full of axis-aligned boxes, which is what a
    /// MAP's brush geometry is, reaches them.
    /// </remarks>
    public static StockKdScene Load(string prefix)
    {
        string dir = StockRaySet.FixtureDirectory();
        byte[] tri = File.ReadAllBytes(Path.Combine(dir, prefix + ".tris.bin"));
        byte[] ray = File.ReadAllBytes(Path.Combine(dir, prefix + ".rays.bin"));
        byte[] tree = File.ReadAllBytes(Path.Combine(dir, prefix + ".tree.bin"));
        byte[] ans = File.ReadAllBytes(Path.Combine(dir, prefix + ".answers.bin"));

        int ntris = BitConverter.ToInt32(tri, 0);
        TracedTriangle[] triangles = new TracedTriangle[ntris];
        for (int i = 0; i < ntris; i++)
        {
            int o = 4 + (i * 36);
            triangles[i] = new TracedTriangle(
                i,
                new Vec3(
                    BitConverter.ToSingle(tri, o),
                    BitConverter.ToSingle(tri, o + 4),
                    BitConverter.ToSingle(tri, o + 8)),
                new Vec3(
                    BitConverter.ToSingle(tri, o + 12),
                    BitConverter.ToSingle(tri, o + 16),
                    BitConverter.ToSingle(tri, o + 20)),
                new Vec3(
                    BitConverter.ToSingle(tri, o + 24),
                    BitConverter.ToSingle(tri, o + 28),
                    BitConverter.ToSingle(tri, o + 32)),
                0);
        }

        int nrays = BitConverter.ToInt32(ray, 0);
        Ray[] rays = new Ray[nrays];
        for (int i = 0; i < nrays; i++)
        {
            int o = 4 + (i * 24);
            float sx = BitConverter.ToSingle(ray, o);
            float sy = BitConverter.ToSingle(ray, o + 4);
            float sz = BitConverter.ToSingle(ray, o + 8);
            rays[i] = new Ray(
                sx,
                sy,
                sz,
                BitConverter.ToSingle(ray, o + 12) - sx,
                BitConverter.ToSingle(ray, o + 16) - sy,
                BitConverter.ToSingle(ray, o + 20) - sz,
                1.0f);
        }

        int p = 0;
        int nnodes = BitConverter.ToInt32(tree, p);
        p += 4;
        int[] children = new int[nnodes];
        int[] splits = new int[nnodes];
        for (int i = 0; i < nnodes; i++)
        {
            children[i] = BitConverter.ToInt32(tree, p);
            splits[i] = BitConverter.ToInt32(tree, p + 4);
            p += 8;
        }

        int nindices = BitConverter.ToInt32(tree, p);
        p += 4;
        int[] indices = new int[nindices];
        for (int i = 0; i < nindices; i++)
        {
            indices[i] = BitConverter.ToInt32(tree, p);
            p += 4;
        }

        Vec3 min = new(
            BitConverter.ToSingle(tree, p),
            BitConverter.ToSingle(tree, p + 4),
            BitConverter.ToSingle(tree, p + 8));
        Vec3 max = new(
            BitConverter.ToSingle(tree, p + 12),
            BitConverter.ToSingle(tree, p + 16),
            BitConverter.ToSingle(tree, p + 20));
        p += 24;

        int ntri2 = BitConverter.ToInt32(tree, p);
        p += 4;
        Vec3[] normals = new Vec3[ntri2];
        float[] d = new float[ntri2];
        float[] edges = new float[ntri2 * 6];
        int[] coordSelect = new int[ntri2 * 2];
        for (int i = 0; i < ntri2; i++)
        {
            normals[i] = new Vec3(
                BitConverter.ToSingle(tree, p),
                BitConverter.ToSingle(tree, p + 4),
                BitConverter.ToSingle(tree, p + 8));
            d[i] = BitConverter.ToSingle(tree, p + 12);
            p += 20;   // normal, d, and the triangle id which is not kept here
            for (int e = 0; e < 6; e++)
            {
                edges[(i * 6) + e] = BitConverter.ToSingle(tree, p + (e * 4));
            }

            p += 24;
            coordSelect[i * 2] = tree[p];
            coordSelect[(i * 2) + 1] = tree[p + 1];
            p += 2;
        }

        int nans = BitConverter.ToInt32(ans, 0);
        if (nans != nrays)
        {
            throw new InvalidOperationException(
                $"the answer file holds {nans} answers and the ray file {nrays} rays: the pair "
                + "was not made in one run");
        }

        int[] hitId = new int[nans];
        float[] dist = new float[nans];
        for (int i = 0; i < nans; i++)
        {
            hitId[i] = BitConverter.ToInt32(ans, 4 + (i * 4));
            dist[i] = BitConverter.ToSingle(ans, 4 + (nans * 4) + (i * 4));
        }

        return new StockKdScene(
            triangles, rays, children, splits, indices, min, max,
            normals, d, edges, coordSelect, hitId, dist);
    }
}
