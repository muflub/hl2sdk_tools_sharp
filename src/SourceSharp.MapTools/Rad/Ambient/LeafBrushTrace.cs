using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// What a point-ray against one leaf's brushes found.
/// </summary>
/// <param name="Fraction">
/// How far along the ray the first opaque brush is, 1 when nothing blocked it,
/// and 0 when the ray STARTED inside a brush.
/// </param>
/// <param name="Normal">
/// The surface normal at the hit, meaningless when <paramref name="Fraction"/>
/// is 1.
/// </param>
/// <param name="StartSolid">Whether the ray began inside a brush.</param>
public readonly record struct LeafBrushHit(float Fraction, Vec3 Normal, bool StartSolid);

/// <summary>
/// <c>TraceLeafBrushes</c> and the point branch of <c>DM_ClipBoxToBrush</c>
/// (<c>trace.cpp:54</c> and <c>:189</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only ever called with a zero-extent box, so only the <c>ispoint</c> loop is
/// ported: <c>CastRayInLeaf</c> passes <c>vec3_origin</c> for mins and maxs and
/// <c>TraceLeafBrushes</c> hard-codes <c>trace.ispoint = true</c>. The box
/// branch differs in two ways that would be wrong to fold in -- it offsets each
/// plane by the box extent, and it does NOT skip bevel planes -- so writing one
/// loop for both would need a flag on the hottest line of the sampler to
/// reproduce a path nothing calls.
/// </para>
/// <para>
/// THE FIRST BLOCKING BRUSH WINS, not the nearest. <c>TraceLeafBrushes</c>
/// returns as soon as any brush in the leaf's list reports a hit, so the answer
/// depends on the leaf's brush ORDER. That is not a refinement worth making:
/// the caller only asks whether the fraction is 0, 1, or something between, and
/// which brush produced it never reaches the output.
/// </para>
/// </remarks>
public static class LeafBrushTrace
{
    /// <summary>
    /// The 1/32 unit epsilon every plane crossing is nudged by
    /// (<c>trace.cpp:37</c>, <c>DIST_EPSILON</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly representable, so the VALUE is not the interesting part. What is:
    /// it is spelled <c>(0.03125)</c> with no <c>f</c>, so it is a DOUBLE, and
    /// <c>(d1 - DIST_EPSILON)</c> is therefore a double subtraction of two
    /// floats -- exact -- where the same expression in float rounds. The
    /// division that follows is then double divided by float-promoted-to-double,
    /// and only the final assignment narrows.
    /// </para>
    /// <para>
    /// <b>That is not a detail.</b> Computing the whole expression in float
    /// moved the sample positions in 351 of a real map's 1,398 comparable
    /// leaves: a fraction one ulp out flips whether the six-axis probe reports
    /// <c>t == 1.0f</c>, a flipped probe rejects or accepts a candidate stock
    /// did the opposite with, and a rejected candidate consumes three more draws
    /// from the stream -- so the whole leaf desynchronises from there on.
    /// </para>
    /// <para>
    /// See <see cref="ClipToBrush"/> for where the widening is spelled out.
    /// </para>
    /// </remarks>
    public const double DistEpsilon = 0.03125;

    /// <summary>
    /// The same epsilon where a float is genuinely wanted: the leaf boundary
    /// test, whose comparison is <c>float &lt; DIST_EPSILON</c> and so is
    /// decided identically in either precision.
    /// </summary>
    public const float DistEpsilonSingle = 0.03125f;

    /// <summary>
    /// The sentinel <c>enterfrac</c> starts at (<c>trace.cpp:43</c>,
    /// <c>NEVER_UPDATED</c>).
    /// </summary>
    /// <remarks>
    /// -9999 rather than -1, and stock's comment says why: -1 caused epsilon
    /// trouble on shallow slopes. It has to stay negative for the
    /// <c>enterfrac &gt; NEVER_UPDATED</c> test at the end to mean "some plane
    /// was crossed".
    /// </remarks>
    private const float NeverUpdated = -9999.0f;

    /// <summary>
    /// <c>CONTENTS_SOLID | CONTENTS_MOVEABLE | CONTENTS_OPAQUE</c>
    /// (<c>bspflags.h:114</c>, <c>MASK_OPAQUE</c>).
    /// </summary>
    public const int MaskOpaque = 0x1 | 0x4000 | 0x80;

    /// <summary>
    /// Traces a point ray against the opaque brushes of one leaf.
    /// </summary>
    /// <param name="leaf">The leaf.</param>
    /// <param name="start">Where the ray starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="scene">The map's brushes, sides, planes and leaves.</param>
    /// <returns>The hit.</returns>
    public static LeafBrushHit Trace(
        int leaf, Vec3 start, Vec3 end, AmbientScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        ref readonly DLeaf leafData = ref scene.Leaves[leaf];

        for (int i = 0; i < leafData.NumLeafBrushes; i++)
        {
            int brushNum = scene.LeafBrushes[leafData.FirstLeafBrush + i];
            ref readonly DBrush brush = ref scene.Brushes[brushNum];
            if ((brush.Contents & MaskOpaque) == 0)
            {
                continue;
            }

            LeafBrushHit hit = ClipToBrush(in brush, start, end, scene);
            if (hit.Fraction != 1.0f || hit.StartSolid)
            {
                return hit.StartSolid
                    ? hit with { Fraction = 0.0f }
                    : hit;
            }
        }

        return new LeafBrushHit(1.0f, Vec3.Zero, StartSolid: false);
    }

    /// <summary>
    /// <c>DM_ClipBoxToBrush</c>'s <c>ispoint</c> branch, against one brush.
    /// </summary>
    /// <param name="brush">The brush.</param>
    /// <param name="p1">Where the ray starts.</param>
    /// <param name="p2">Where it ends.</param>
    /// <param name="scene">The map's sides and planes.</param>
    /// <returns>The hit, with fraction 1 when this brush does not block.</returns>
    /// <remarks>
    /// <para>
    /// The clip is the classic enter/leave interval over the brush's planes:
    /// the ray is inside the brush between the LATEST entry and the EARLIEST
    /// exit, and that interval is non-empty only when
    /// <c>enterfrac &lt; leavefrac</c>.
    /// </para>
    /// <para>
    /// Note that the epsilon is applied ASYMMETRICALLY -- <c>d1 - eps</c> on
    /// entry and <c>d1 + eps</c> on exit -- so both ends are pulled towards the
    /// middle and the reported hit sits slightly outside the surface. Reproduced
    /// exactly: this is what keeps the sampler's six-axis probe from reporting
    /// a hit on the leaf's own bounding planes.
    /// </para>
    /// <para>
    /// Stock reports through an out-parameter it never resets between brushes,
    /// which is what lets <c>TraceLeafBrushes</c>'s early return work. This
    /// returns a value instead, and the caller re-implements that early return
    /// explicitly.
    /// </para>
    /// </remarks>
    private static LeafBrushHit ClipToBrush(
        ref readonly DBrush brush, Vec3 p1, Vec3 p2, AmbientScene scene)
    {
        if (brush.NumSides == 0)
        {
            return new LeafBrushHit(1.0f, Vec3.Zero, StartSolid: false);
        }

        float enterFrac = NeverUpdated;
        float leaveFrac = 1.0f;
        int clipPlane = -1;

        bool getOut = false;
        bool startOut = false;

        for (int i = 0; i < brush.NumSides; i++)
        {
            ref readonly DBrushSide side = ref scene.BrushSides[brush.FirstSide + i];

            // trace.cpp:283. Bevel planes are padding added for box traces and
            // are not real surfaces, so a point ray must not see them.
            if (side.Bevel == 1)
            {
                continue;
            }

            ref readonly DPlane plane = ref scene.Planes[side.PlaneNum];

            float dist = plane.Dist;
            float d1 = Vec3.Dot(p1, plane.Normal) - dist;
            float d2 = Vec3.Dot(p2, plane.Normal) - dist;

            if (d1 > 0 && d2 > 0)
            {
                return new LeafBrushHit(1.0f, Vec3.Zero, StartSolid: false);
            }

            if (d2 > 0)
            {
                getOut = true;
            }

            if (d1 > 0)
            {
                startOut = true;
            }

            if (d1 <= 0 && d2 <= 0)
            {
                continue;
            }

            // trace.cpp:305 and :316. DIST_EPSILON is a double, so the numerator
            // is a double subtraction and the division happens in double; only
            // the assignment to `f` narrows. The DENOMINATOR is a float
            // subtraction first, because both its operands are floats -- so it
            // is `(double)(d1 - d2)` and not `(double)d1 - (double)d2`.
            if (d1 > d2)
            {
                float f = (float)((d1 - DistEpsilon) / (double)(d1 - d2));
                if (f > enterFrac)
                {
                    enterFrac = f;
                    clipPlane = side.PlaneNum;
                }
            }
            else
            {
                float f = (float)((d1 + DistEpsilon) / (double)(d1 - d2));
                if (f < leaveFrac)
                {
                    leaveFrac = f;
                }
            }
        }

        if (!startOut)
        {
            // trace.cpp:324. The whole ray began inside this brush.
            // `allsolid` is set too when it also ends inside; nothing in this
            // lane's callers reads it, so it is not carried.
            _ = getOut;
            return new LeafBrushHit(1.0f, Vec3.Zero, StartSolid: true);
        }

        if (enterFrac < leaveFrac && enterFrac > NeverUpdated && enterFrac < 1.0f)
        {
            if (enterFrac < 0)
            {
                enterFrac = 0;
            }

            return new LeafBrushHit(
                enterFrac, scene.Planes[clipPlane].Normal, StartSolid: false);
        }

        return new LeafBrushHit(1.0f, Vec3.Zero, StartSolid: false);
    }
}
