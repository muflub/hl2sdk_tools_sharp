//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapGen.Content;

/// <summary>
/// Simple closed meshes for stand-in models: boxes and capped cylinders, with
/// outward normals and a texture mapping per face.
/// </summary>
/// <remarks>
/// Each shape is one convex mesh, so vbsp's per-mesh collision hull is the
/// shape itself; a model built from several shapes gets several hulls, as a
/// real model with several material groups does.
/// </remarks>
public static class Primitives
{
    /// <summary>An axis-aligned box.</summary>
    /// <param name="material">The mesh's material slot.</param>
    /// <param name="min">One corner.</param>
    /// <param name="max">The opposite corner.</param>
    /// <returns>24 vertices (four per face, so each face has its own normal) and 12 triangles.</returns>
    public static MeshSpec Box(int material, Vec3 min, Vec3 max)
    {
        List<MeshVertex> vertices = [];
        List<int> triangles = [];

        // Each face: normal, then four corners counter-clockwise seen from outside.
        (Vec3 Normal, Vec3[] Corners)[] faces =
        [
            (new Vec3(1, 0, 0), [new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(max.X, min.Y, max.Z)]),
            (new Vec3(-1, 0, 0), [new(min.X, max.Y, min.Z), new(min.X, min.Y, min.Z), new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z)]),
            (new Vec3(0, 1, 0), [new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z)]),
            (new Vec3(0, -1, 0), [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z)]),
            (new Vec3(0, 0, 1), [new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)]),
            (new Vec3(0, 0, -1), [new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, min.Y, min.Z), new(min.X, min.Y, min.Z)]),
        ];

        foreach ((Vec3 normal, Vec3[] corners) in faces)
        {
            int first = vertices.Count;
            vertices.Add(new MeshVertex(corners[0], normal, 0, 1));
            vertices.Add(new MeshVertex(corners[1], normal, 1, 1));
            vertices.Add(new MeshVertex(corners[2], normal, 1, 0));
            vertices.Add(new MeshVertex(corners[3], normal, 0, 0));
            triangles.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return new MeshSpec(material, vertices, triangles);
    }

    /// <summary>A capped cylinder standing on the XY plane around the Z axis.</summary>
    /// <param name="material">The mesh's material slot.</param>
    /// <param name="radius">The radius.</param>
    /// <param name="bottom">The Z of the bottom cap.</param>
    /// <param name="top">The Z of the top cap.</param>
    /// <param name="sides">How many flat sides approximate the curve, at least 3.</param>
    /// <returns>The mesh: a side band with smooth normals and two flat caps.</returns>
    public static MeshSpec Cylinder(int material, float radius, float bottom, float top, int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 3);
        List<MeshVertex> vertices = [];
        List<int> triangles = [];

        // The band: a seam column at both ends so U runs 0..1.
        for (int i = 0; i <= sides; i++)
        {
            float a = 2 * MathF.PI * i / sides;
            Vec3 n = new(MathF.Cos(a), MathF.Sin(a), 0);
            float u = (float)i / sides;
            vertices.Add(new MeshVertex(new Vec3(n.X * radius, n.Y * radius, bottom), n, u, 1));
            vertices.Add(new MeshVertex(new Vec3(n.X * radius, n.Y * radius, top), n, u, 0));
        }

        for (int i = 0; i < sides; i++)
        {
            int b0 = i * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
            triangles.AddRange([b0, b1, t1, b0, t1, t0]);
        }

        // The caps: a fan around a centre vertex each.
        foreach ((float z, float nz) in new[] { (top, 1f), (bottom, -1f) })
        {
            int centre = vertices.Count;
            Vec3 normal = new(0, 0, nz);
            vertices.Add(new MeshVertex(new Vec3(0, 0, z), normal, 0.5f, 0.5f));
            for (int i = 0; i < sides; i++)
            {
                float a = 2 * MathF.PI * i / sides;
                vertices.Add(new MeshVertex(
                    new Vec3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, z), normal,
                    0.5f + (MathF.Cos(a) / 2), 0.5f + (MathF.Sin(a) / 2)));
            }

            for (int i = 0; i < sides; i++)
            {
                int a = centre + 1 + i, b = centre + 1 + ((i + 1) % sides);
                triangles.AddRange(nz > 0 ? [centre, a, b] : [centre, b, a]);
            }
        }

        return new MeshSpec(material, vertices, triangles);
    }
}
