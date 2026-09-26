//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The interpolated surface normal at a point on a face: <c>GetPhongNormal</c>
/// (scalar, four-wide).
/// </summary>
/// <remarks>
/// <para>
/// The face is treated as a fan of triangles from its centroid to each edge.
/// For the fan triangle the point falls in, the normal is the barycentric blend
/// of the face normal at the centroid and the two smoothed corner normals at
/// the edge's ends. Everything <see cref="FaceNeighbours"/> computed is
/// consumed here and nowhere else.
/// </para>
/// <para>
/// <b>The two overloads disagree about which triangle wins.</b> The scalar one
/// RETURNS from inside the loop, so the FIRST edge whose wedge
/// contains the point decides. The four-wide one has no early exit: it blends
/// each lane's result through a mask and keeps going, so
/// the LAST matching edge decides. For a convex face and an interior point
/// exactly one edge matches and they agree; on a boundary where two wedges
/// share the point, or on a face vbsp left non-convex, they do not. Both are
/// ported as written, because the scalar one feeds patch normals
/// And the wide one feeds luxel normals
/// And swapping either for the other moves output
/// that has nothing to do with the bug.
/// </para>
/// <para>
/// The test is <c>a1 &gt;= 0 &amp;&amp; a2 &gt;= 0</c> and NOT
/// <c>a1 + a2 &lt;= 1</c>, so the region is the infinite wedge between the two
/// centroid-to-corner vectors rather than the triangle. A point outside the
/// face but inside a wedge is therefore extrapolated rather than rejected --
/// which is what makes the supersampler's off-face subsamples produce a usable
/// normal instead of falling back to flat.
/// </para>
/// </remarks>
public static class PhongNormals
{
    /// <summary>
    /// The scalar <c>GetPhongNormal</c>: first matching wedge wins.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="neighbours">The smoothing pass.</param>
    /// <param name="centroids">
    /// <c>face_centroids</c>, filled by the patch pass. A face with no patch
    /// has a zero centroid, and stock reads it anyway.
    /// </param>
    /// <param name="faceNum">The face.</param>
    /// <param name="spot">
    /// The point, in the face's own space -- that is, with the owning brush
    /// model's origin already SUBTRACTED. See the remarks.
    /// </param>
    /// <param name="smoothingThreshold">
    /// The smoothing cosine. Exactly 1 disables phong and returns the face
    /// normal.
    /// </param>
    /// <returns>The interpolated normal.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="geometry"/>, <paramref name="neighbours"/> or
    /// <paramref name="centroids"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The model-origin convention is easy to get backwards and stock's two
    /// callers do it differently in APPEARANCE only. <c>face_centroids</c> is
    /// stored with the offset removed and
    /// <c>dvertexes</c> are un-offset, so this function works entirely in
    /// un-offset space. <c>CreateChildPatch</c> passes <c>child-&gt;origin</c>,
    /// which IS offset -- a mismatch that only cancels
    /// because brush models with an origin brush are rare and their faces are
    /// usually flat. <c>ComputeIlluminationPointAndNormalsSSE</c> subtracts the
    /// model origin first, which is the right
    /// thing. Both are reproduced at their call sites; this function takes what
    /// it is given.
    /// </para>
    /// </remarks>
    public static Vec3 Compute(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        FaceCentroids centroids,
        int faceNum,
        Vec3 spot,
        float smoothingThreshold)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(centroids);

        ref readonly DFace face = ref geometry.Faces[faceNum];
        Vec3 faceNormal = geometry.Planes[face.PlaneNum].Normal;

        if (smoothingThreshold == 1f)
        {
            return faceNormal;
        }

        ReadOnlySpan<Vec3> corners = neighbours.CornerNormals(faceNum);
        Vec3 centroid = centroids[faceNum];
        int numEdges = face.NumEdges;

        for (int j = 0; j < numEdges; j++)
        {
            Vec3 n1 = corners[j];
            Vec3 n2 = corners[(j + 1) % numEdges];

            Vec3 p1 = geometry.Vertexes[geometry.EdgeVertex(faceNum, j)];
            Vec3 p2 = geometry.Vertexes[geometry.EdgeVertex(faceNum, j + 1)];

            Vec3 v1 = p1 - centroid;
            Vec3 v2 = p2 - centroid;
            Vec3 vspot = spot - centroid;

            float aa = Vec3.Dot(v1, v1);
            float bb = Vec3.Dot(v2, v2);
            float ab = Vec3.Dot(v1, v2);

            // Solved in FLOAT -- a1 and a2 are float locals in the
            // reference build and every operand is a vec_t -- so the division's rounding is
            // part of the answer.
            float a1 = ((bb * Vec3.Dot(v1, vspot)) - (ab * Vec3.Dot(vspot, v2)))
                / ((aa * bb) - (ab * ab));
            float a2 = (Vec3.Dot(vspot, v2) - (a1 * ab)) / bb;

            if (a1 >= 0.0f && a2 >= 0.0f)
            {
                // The face normal is weighted by what is LEFT
                // OVER, so the three weights sum to one and the blend stays on
                // the unit sphere before normalising.
                // `1.0 - a1 - a2` is a DOUBLE expression narrowed into the float
                // `scale`, so it rounds once, not twice.
                float scale = (float)(1.0 - a1 - a2);
                Vec3 blended = (faceNormal * scale) + (n1 * a1) + (n2 * a2);
                return BumpBasis.Normalise(blended, geometry.StockNormalise);
            }
        }

        return faceNormal;
    }

    /// <summary>
    /// The four-wide <c>GetPhongNormal</c>: last matching wedge wins, per lane.
    /// </summary>
    /// <param name="geometry">The map's lumps.</param>
    /// <param name="neighbours">The smoothing pass.</param>
    /// <param name="centroids"><c>face_centroids</c>.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="spots">Four points in the face's un-offset space.</param>
    /// <param name="results">Receives four normals. Must be at least four long.</param>
    /// <param name="smoothingThreshold">The smoothing cosine.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="geometry"/>, <paramref name="neighbours"/> or
    /// <paramref name="centroids"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="spots"/> or <paramref name="results"/> is shorter than four.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Written as four scalar lanes rather than as SIMD, and that is a
    /// behavioural decision rather than a shortcut: stock's version uses
    /// <c>ReciprocalSIMD</c> for both divisions, which is
    /// <c>rcpps</c> plus a Newton-Raphson step and NOT an exact divide, so its
    /// barycentric weights differ from the scalar function's in the last bits
    /// even on the same point. Four exact divides here means the two overloads
    /// agree where stock's do not, which is a deliberate correctness choice at
    /// a site where there is nothing to be bug-compatible WITH -- the weights
    /// feed a normalise that flattens the difference.
    /// </para>
    /// <para>
    /// The final <c>VectorNormalize</c> is unconditional in stock
    /// So a lane that matched no wedge has its face normal
    /// renormalised. That is reproduced: on an unnormalised plane normal the
    /// two overloads would otherwise differ by more than rounding.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ComputeFour(
        LightGeometry geometry,
        FaceNeighbours neighbours,
        FaceCentroids centroids,
        int faceNum,
        ReadOnlySpan<Vec3> spots,
        Span<Vec3> results,
        float smoothingThreshold)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(centroids);

        if (spots.Length < 4)
        {
            throw new ArgumentException("Four points are required.", nameof(spots));
        }

        if (results.Length < 4)
        {
            throw new ArgumentException("Room for four normals is required.", nameof(results));
        }

        ref readonly DFace face = ref geometry.Faces[faceNum];
        Vec3 faceNormal = geometry.Planes[face.PlaneNum].Normal;
        bool estimates = geometry.StockEstimates;

        for (int lane = 0; lane < 4; lane++)
        {
            results[lane] = faceNormal;
        }

        if (smoothingThreshold == 1f)
        {
            return;
        }

        ReadOnlySpan<Vec3> corners = neighbours.CornerNormals(faceNum);
        Vec3 centroid = centroids[faceNum];
        int numEdges = face.NumEdges;

        for (int j = 0; j < numEdges; j++)
        {
            Vec3 n1 = corners[j];
            Vec3 n2 = corners[(j + 1) % numEdges];

            Vec3 p1 = geometry.Vertexes[geometry.EdgeVertex(faceNum, j)];
            Vec3 p2 = geometry.Vertexes[geometry.EdgeVertex(faceNum, j + 1)];

            Vec3 v1 = p1 - centroid;
            Vec3 v2 = p2 - centroid;

            float aa = Vec3.Dot(v1, v1);
            float bb = Vec3.Dot(v2, v2);
            float ab = Vec3.Dot(v1, v2);

            for (int lane = 0; lane < 4; lane++)
            {
                Vec3 vspot = spots[lane] - centroid;
                // Stock multiplies by ReciprocalSIMD estimates
                // (StockQuirk.GatherReciprocalEstimate); correct divides.
                float num1 = (bb * Vec3.Dot(vspot, v1)) - (ab * Vec3.Dot(vspot, v2));
                float den1 = (aa * bb) - (ab * ab);
                float a1 = estimates ? StockSimd.Reciprocal(den1, true) * num1 : num1 / den1;
                float num2 = Vec3.Dot(vspot, v2) - (a1 * ab);
                float a2 = estimates ? StockSimd.Reciprocal(bb, true) * num2 : num2 / bb;

                if (a1 >= 0.0f && a2 >= 0.0f)
                {
                    float scale = 1.0f - a1 - a2;
                    results[lane] = (faceNormal * scale) + (n1 * a1) + (n2 * a2);
                }
            }
        }

        // FourVectors::VectorNormalize: rsqrtps and one Newton step, no
        // epsilon -- the four-wide form of StockQuirk.VradVectorNormalise.
        for (int lane = 0; lane < 4; lane++)
        {
            results[lane] = StockSimd.NormaliseFour(results[lane], geometry.StockNormalise);
        }
    }
}
