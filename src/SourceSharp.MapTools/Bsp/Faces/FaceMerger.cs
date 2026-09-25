using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Joining two coplanar faces that share an edge back into one
/// </summary>
/// <remarks>
/// <para>
/// This is what undoes the fragmentation the BSP split caused. Two faces merge
/// when they are on the same plane, carry the same material, contents,
/// smoothing groups and overlays, share a whole edge, and the polygon that
/// results is still convex. The convexity test is the interesting half: at each
/// end of the shared edge the two faces' outgoing edges are compared, and the
/// shared vertex is DROPPED when they are collinear within
/// <see cref="ContinuousEpsilon"/>, which is why merging reduces the vertex
/// count and not just the face count.
/// </para>
/// <para>
/// A merged face is appended to the END of the node's list so that it is
/// visited again and can merge further (<c>MergeFaceList</c>,
///). The two originals stay in the list forever with
/// <see cref="Face.Merged"/> set.
/// </para>
/// </remarks>
public sealed class FaceMerger
{
    /// <summary><c>CONTINUOUS_EPSILON</c>.</summary>
    public const double ContinuousEpsilon = 0.001;

    /// <summary>
    /// <c>EQUAL_EPSILON</c>, whose comment says it is
    /// there for <c>faces.c</c>.
    /// </summary>
    public const double EqualEpsilon = 0.001;

    private readonly FaceBuildContext _context;

    /// <summary>Creates a merger over one compile's face stage.</summary>
    /// <param name="context">The stage's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public FaceMerger(FaceBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Whether two faces carry the same set of overlay ids
    /// (<c>OverlaysAreEqual</c>).
    /// </summary>
    /// <param name="f1">One face.</param>
    /// <param name="f2">The other.</param>
    /// <returns>True when neither has an overlay the other lacks.</returns>
    /// <remarks>
    /// Compares counts and then containment one way only, which is sound
    /// because equal counts plus one-way containment is set equality — unless
    /// one list holds a duplicate, which the overlay loader does not produce.
    /// </remarks>
    public static bool OverlaysAreEqual(Face f1, Face f2)
    {
        ArgumentNullException.ThrowIfNull(f1);
        ArgumentNullException.ThrowIfNull(f2);

        List<int> a = f1.OriginalFace!.OverlayIds;
        List<int> b = f2.OriginalFace!.OverlayIds;

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (int id in a)
        {
            if (!b.Contains(id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a face came off a water or slime brush
    /// (<c>FaceOnWaterBrush</c>).
    /// </summary>
    /// <param name="face">The face.</param>
    /// <returns>True when its original side carries water or slime contents.</returns>
    public static bool FaceOnWaterBrush(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        MapBrushSide? side = face.OriginalFace;

        if (side is null)
        {
            return false;
        }

        return (side.Contents & MapFileLoader.MaskWater) != 0;
    }

    /// <summary>
    /// Merges two coplanar windings across a shared edge
    /// (<c>TryMergeWinding</c>).
    /// </summary>
    /// <param name="w1">The first polygon.</param>
    /// <param name="w2">The second.</param>
    /// <param name="planeNormal">The plane both lie in.</param>
    /// <returns>
    /// The merged polygon, or <see cref="Winding.Null"/> when they share no
    /// edge or the result would be concave. Neither input is freed.
    /// </returns>
    public Winding TryMergeWinding(Winding w1, Winding w2, Vec3 planeNormal)
    {
        WindingArena arena = _context.Windings;
        Span<Vec3> f1 = arena.Points(w1);
        Span<Vec3> f2 = arena.Points(w2);

        // find a common edge: an edge of f1 that appears reversed in f2
        int i;
        int j = 0;
        Vec3 p1 = default;
        Vec3 p2 = default;

        for (i = 0; i < f1.Length; i++)
        {
            p1 = f1[i];
            p2 = f1[(i + 1) % f1.Length];

            for (j = 0; j < f2.Length; j++)
            {
                Vec3 p3 = f2[j];
                Vec3 p4 = f2[(j + 1) % f2.Length];

                int k;

                for (k = 0; k < 3; k++)
                {
                    if (Math.Abs(p1[k] - p4[k]) > EqualEpsilon)
                    {
                        break;
                    }

                    if (Math.Abs(p2[k] - p3[k]) > EqualEpsilon)
                    {
                        break;
                    }
                }

                if (k == 3)
                {
                    break;
                }
            }

            if (j < f2.Length)
            {
                break;
            }
        }

        if (i == f1.Length)
        {
            return Winding.Null;    // no matching edges
        }

        // check the slope of the lines that meet at each end of the shared
        // edge: collinear means the shared vertex can go, convex-the-wrong-way
        // means the merge is refused
        Vec3 back = f1[((i + f1.Length) - 1) % f1.Length];
        Vec3 delta = p1 - back;
        Vec3 normal = Vec3.Cross(planeNormal, delta);
        normal = Normalise(normal);

        back = f2[(j + 2) % f2.Length];
        delta = back - p1;
        double dot = Vec3.Dot(delta, normal);

        if (dot > ContinuousEpsilon)
        {
            return Winding.Null;    // not a convex polygon
        }

        bool keep1 = dot < -ContinuousEpsilon;

        back = f1[(i + 2) % f1.Length];
        delta = back - p2;
        normal = Vec3.Cross(planeNormal, delta);
        normal = Normalise(normal);

        back = f2[((j + f2.Length) - 1) % f2.Length];
        delta = back - p2;
        dot = Vec3.Dot(delta, normal);

        if (dot > ContinuousEpsilon)
        {
            return Winding.Null;    // not a convex polygon
        }

        bool keep2 = dot < -ContinuousEpsilon;

        // build the new polygon
        Winding merged = arena.Alloc(f1.Length + f2.Length);
        Span<Vec3> output = arena.Storage(merged);
        int count = 0;

        // Re-fetch: Alloc can move the slab, which invalidates the spans above.
        f1 = arena.Points(w1);
        f2 = arena.Points(w2);

        for (int k = (i + 1) % f1.Length; k != i; k = (k + 1) % f1.Length)
        {
            if (k == (i + 1) % f1.Length && !keep2)
            {
                continue;
            }

            output[count++] = f1[k];
        }

        for (int l = (j + 1) % f2.Length; l != j; l = (l + 1) % f2.Length)
        {
            if (l == (j + 1) % f2.Length && !keep1)
            {
                continue;
            }

            output[count++] = f2[l];
        }

        return arena.SetCount(merged, count);
    }

    /// <summary>
    /// Merges two faces if everything about them allows it
    /// (<c>TryMerge</c>).
    /// </summary>
    /// <param name="f1">One face.</param>
    /// <param name="f2">The other.</param>
    /// <param name="planeNormal">Their shared plane's normal.</param>
    /// <returns>The merged face, or null. Neither input is freed.</returns>
    public Face? TryMerge(Face f1, Face f2, Vec3 planeNormal)
    {
        ArgumentNullException.ThrowIfNull(f1);
        ArgumentNullException.ThrowIfNull(f2);

        if (f1.Winding.IsNull || f2.Winding.IsNull)
        {
            return null;
        }

        if (f1.TexInfo != f2.TexInfo)
        {
            return null;
        }

        if (f1.PlaneNumber != f2.PlaneNumber)
        {
            return null;    // on front and back sides
        }

        if (f1.Contents != f2.Contents)
        {
            return null;
        }

        if (f1.OriginalFace!.SmoothingGroups != f2.OriginalFace!.SmoothingGroups)
        {
            return null;
        }

        if (!OverlaysAreEqual(f1, f2))
        {
            return null;
        }

        if (_context.Options.NoMergeWater && (FaceOnWaterBrush(f1) || FaceOnWaterBrush(f2)))
        {
            return null;
        }

        Winding merged = TryMergeWinding(f1.Winding, f2.Winding, planeNormal);

        if (merged.IsNull)
        {
            return null;
        }

        _context.Counters.Merged++;

        Face face = _context.Faces.NewFaceFromFace(f1);
        face.Winding = merged;

        f1.Merged = face;
        f2.Merged = face;

        return face;
    }

    /// <summary>
    /// Merges every pair it can in one node's face list
    /// (<c>MergeFaceList</c>).
    /// </summary>
    /// <param name="head">The head of the list.</param>
    /// <returns>The head afterwards, which is unchanged unless it was null.</returns>
    /// <remarks>
    /// <para>
    /// The inner loop runs from the head only as far as <c>f1</c>, so each
    /// unordered pair is tried once, and the outer loop walks the list
    /// including the faces the merges appended — which is the whole point of
    /// appending rather than prepending.
    /// </para>
    /// <para>
    /// <b>The head never changes.</b> A merged face goes on the tail, so the
    /// caller's list pointer stays valid, and the <c>face_t **pList</c> stock
    /// passes is only ever dereferenced for reading. The return value here says
    /// so rather than relying on it.
    /// </para>
    /// </remarks>
    public Face? MergeFaceList(Face? head)
    {
        for (Face? f1 = head; f1 is not null; f1 = f1.Next)
        {
            if (f1.IsDead)
            {
                continue;
            }

            for (Face? f2 = head; f2 is not null && !ReferenceEquals(f2, f1); f2 = f2.Next)
            {
                if (f2.IsDead)
                {
                    continue;
                }

                Plane plane = _context.Planes[f1.PlaneNumber];
                Face? merged = TryMerge(f1, f2, plane.Normal);

                if (merged is null)
                {
                    continue;
                }

                // add merged to the end of the face list so it will be checked
                // against all the faces again
                Face end = head!;

                while (end.Next is not null)
                {
                    end = end.Next;
                }

                merged.Next = null;
                end.Next = merged;
                break;
            }
        }

        return head;
    }

    /// <summary>
    /// <c>VectorNormalize</c> as the merge test calls it
    ///.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stock's <c>VectorNormalize</c> is <c>rsqrtss</c> plus one
    /// Newton-Raphson step, and it is reproduced here unconditionally rather
    /// than behind a <c>StockQuirk</c>. The reason is the rule: a quirk needs a
    /// concrete defect, and an approximate reciprocal square root is a
    /// precision choice, not a defect. It earns a quirk in
    /// <c>BaseWindingForPlane</c> only because a 65536-unit winding is clipped
    /// there with an epsilon of exactly zero, so the estimate's last bits
    /// decide whether a sliver survives. Nothing here amplifies it: the
    /// estimate scales the vector by 1 + O(1e-7) and leaves its DIRECTION
    /// untouched, and the only use of the result is a dot product tested
    /// against 0.001.
    /// </para>
    /// <para>
    /// A zero-length input cannot divide by zero, because stock adds
    /// <c>1.0e-10f</c> to the squared length before the estimate. The result is
    /// then a vector of length about 1e-5 times the input, i.e. zero, and the
    /// dot that follows is zero: the shared vertex is kept.
    /// </para>
    /// </remarks>
    private static Vec3 Normalise(Vec3 v) => v.NormaliseLikeStock().Normalised;
}
