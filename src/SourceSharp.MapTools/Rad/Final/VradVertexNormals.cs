//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>
/// Vrad's <c>SaveVertexNormals</c>: the smoothed
/// per-corner normals <c>PairEdges</c> computed, de-duplicated into
/// <c>LUMP_VERTNORMALS</c> with one <c>LUMP_VERTNORMALINDICES</c> entry per
/// face corner.
/// </summary>
/// <remarks>
/// <para>
/// This REPLACES what vbsp wrote. vbsp's normals are one flat face normal per
/// face (<c>Bsp.Faces.VertexNormals</c>); these are the phong corner normals
/// the engine uses for bumped and smoothed surfaces, which is why the lump
/// usually shrinks -- every flat room collapses to its six wall normals.
/// </para>
/// <para>
/// <c>CNormalList::FindOrAddNormal</c> buckets normals on an
/// 8x8x8 grid and compares with <c>Vector::operator==</c>, i.e. float
/// equality per component. The grid is only an accelerator: equal vectors
/// always land in the same bucket (the bucket is a function of the value, and
/// <c>-0 == 0</c> maps to the same cell), so the output is exactly "first
/// occurrence of each value under float equality", which is what this
/// computes with an exact-key dictionary. A NaN component never compares
/// equal, so such a normal is always appended -- as stock does.
/// </para>
/// </remarks>
public static class VradVertexNormals
{
    /// <summary><c>MAX_MAP_VERTNORMALS</c>.</summary>
    public const int MaxVertNormals = 256000;

    /// <summary><c>MAX_MAP_VERTNORMALINDICES</c>.</summary>
    public const int MaxVertNormalIndices = 256000;

    /// <summary>Collects the corner normals of every face, in face order.</summary>
    /// <param name="geometry">The faces.</param>
    /// <param name="neighbours">Their corner normals.</param>
    /// <returns>The unique normals and the per-corner indices.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MapCompileException">Either array overflowed its <c>MAX_MAP_*</c> (stock's <c>Error</c>).</exception>
    public static (Vec3[] Normals, ushort[] Indices) Save(LightGeometry geometry, FaceNeighbours neighbours)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(neighbours);

        List<Vec3> normals = new(128);
        List<ushort> indices = [];
        Dictionary<(float X, float Y, float Z), int> seen = [];

        for (int f = 0; f < geometry.Faces.Length; f++)
        {
            ReadOnlySpan<Vec3> corners = neighbours.CornerNormals(f);
            int edges = geometry.Faces[f].NumEdges;

            for (int j = 0; j < edges; j++)
            {
                // Every face has corner normals after PairEdges; a
                // face without them would contribute the zero vector.
                Vec3 n = j < corners.Length ? corners[j] : Vec3.Zero;

                if (indices.Count == MaxVertNormalIndices)
                {
                    throw new MapCompileException("g_numvertnormalindices == MAX_MAP_VERTNORMALINDICES");
                }

                indices.Add((ushort)FindOrAdd(normals, seen, n));
            }
        }

        if (normals.Count > MaxVertNormals)
        {
            throw new MapCompileException("g_numvertnormals > MAX_MAP_VERTNORMALS");
        }

        return ([.. normals], [.. indices]);
    }

    /// <summary>The two lumps' bytes.</summary>
    /// <param name="normals">The unique normals.</param>
    /// <param name="indices">The per-corner indices.</param>
    /// <returns><c>LUMP_VERTNORMALS</c> and <c>LUMP_VERTNORMALINDICES</c>.</returns>
    public static (byte[] Normals, byte[] Indices) ToLumps(ReadOnlySpan<Vec3> normals, ReadOnlySpan<ushort> indices)
    {
        byte[] n = new byte[normals.Length * 12];
        for (int i = 0; i < normals.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(n.AsSpan(i * 12), normals[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(n.AsSpan((i * 12) + 4), normals[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(n.AsSpan((i * 12) + 8), normals[i].Z);
        }

        byte[] x = new byte[indices.Length * 2];
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(x.AsSpan(i * 2), indices[i]);
        }

        return (n, x);
    }

    private static int FindOrAdd(List<Vec3> normals, Dictionary<(float, float, float), int> seen, Vec3 n)
    {
        if (float.IsNaN(n.X) || float.IsNaN(n.Y) || float.IsNaN(n.Z))
        {
            normals.Add(n);
            return normals.Count - 1;
        }

        // -0 == 0 under float equality: fold the sign of zero into the key.
        (float, float, float) key = (n.X + 0f, n.Y + 0f, n.Z + 0f);
        if (seen.TryGetValue(key, out int index))
        {
            return index;
        }

        normals.Add(n);
        seen[key] = normals.Count - 1;
        return normals.Count - 1;
    }
}
