//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The placeholder vertex normals vbsp writes
/// (<c>SaveVertexNormals</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are not vertex normals.</b> Every vertex of a face is given the
/// FACE's plane normal, and every face contributes exactly one entry to
/// <c>g_vertnormals</c> that all of its indices point at. Stock's own comment
/// says why: "this doesn't do an exhaustive vertex normal match because the
/// vrad does it. The result is that a little extra memory is wasted coming out
/// of vbsp, but it goes away after vrad."
/// </para>
/// <para>
/// So the lump vbsp writes is a correctly-shaped stand-in whose CONTENT is
/// replaced later. It still has to be the right shape: the index array is one
/// entry per surfedge of every face in order, which is what lets vrad
/// overwrite entries in place.
/// </para>
/// </remarks>
public static class VertexNormals
{
    /// <summary><c>MAX_MAP_VERTNORMALS</c>.</summary>
    public const int MaxVertNormals = 256000;

    /// <summary><c>MAX_MAP_VERTNORMALINDICES</c>.</summary>
    public const int MaxVertNormalIndices = 256000;

    /// <summary>
    /// Builds the two vertex-normal arrays from the emitted face lump
    /// (<c>SaveVertexNormals</c>).
    /// </summary>
    /// <param name="faces">LUMP_FACES, in order.</param>
    /// <param name="planes">LUMP_PLANES.</param>
    /// <returns>The normals and the per-surfedge indices into them.</returns>
    /// <exception cref="InvalidOperationException">Either array overflowed.</exception>
    public static (IReadOnlyList<Vec3> Normals, IReadOnlyList<int> Indices) Save(
        IReadOnlyList<DFace> faces,
        IReadOnlyList<DPlane> planes)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(planes);

        List<Vec3> normals = [];
        List<int> indices = [];

        foreach (DFace face in faces)
        {
            for (int j = 0; j < face.NumEdges; j++)
            {
                if (indices.Count == MaxVertNormalIndices)
                {
                    throw new InvalidOperationException(
                        $"g_numvertnormalindices == MAX_MAP_VERTNORMALINDICES ({MaxVertNormalIndices})");
                }

                indices.Add(normals.Count);
            }

            if (normals.Count == MaxVertNormals)
            {
                throw new InvalidOperationException(
                    $"g_numvertnormals == MAX_MAP_VERTNORMALS ({MaxVertNormals})");
            }

            normals.Add(planes[face.PlaneNum].Normal);
        }

        return (normals, indices);
    }
}
