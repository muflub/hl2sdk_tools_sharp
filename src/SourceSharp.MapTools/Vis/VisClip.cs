using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// What <see cref="VisClip.ChopWinding"/> did to a winding.
/// </summary>
/// <remarks>
/// Stock returns a pointer and encodes three outcomes in it: the input pointer
/// unchanged, a freshly allocated winding, or NULL. Two of those are geometry
/// and one is bookkeeping, so they are separated here -- the caller decides
/// which buffer to keep, and the chop stays a pure function.
/// </remarks>
public enum VisChopResult
{
    /// <summary>
    /// The winding is unchanged: either it was entirely in front of the plane
    /// Or the result would have needed more than
    /// <see cref="VisClip.MaxPointsOnFixedWinding"/> points and stock fell back
 /// To the original.
    /// </summary>
    /// <remarks>
    /// The two are one outcome on purpose. The fallback is not an error path in
    /// stock: it silently keeps a winding that is LARGER than the true clipped
    /// one, which makes the answer more conservative and never less. Splitting
    /// them would invite someone to "fix" the second.
    /// </remarks>
    Unchanged,

    /// <summary>The winding was clipped, and the result is in the output buffer.</summary>
    Clipped,

    /// <summary>
    /// The winding is entirely behind the plane and is gone --
    /// Stock's NULL return.
    /// </summary>
    Empty,
}

/// <summary>
/// The two geometric predicates the portal flow is built out of:
/// <c>ChopWinding</c> and <c>ClipToSeperators</c>
///.
/// </summary>
/// <remarks>
/// <para>
/// These are pure functions over spans, with no allocation and no shared state,
/// which is what lets a fact drive one of them directly. That matters more here
/// than anywhere else in vvis: every bit the tool ever sets is set immediately
/// after one of these says a sight line survived (and
///), so an error in either is an error in the answer, and an error
/// in either is invisible in aggregate -- a PVS that is slightly too large
/// looks exactly like a PVS that is correct.
/// </para>
/// <para>
/// <b>Deliberately NOT written on <c>WindingArena</c>.</b> That arena is
/// polylib, whose windings hold up to 64 points, whose epsilon is polylib's and
/// whose chop has no fallback. vvis's stack windings are a different type with
/// a different cap, a different epsilon and a fallback that keeps the original
/// -- <c>winding_t</c> in the reference implementation is its own struct for exactly that
/// reason. Sharing the polylib implementation here would be sharing a name, not
/// an algorithm.
/// </para>
/// <para>
/// <b><see cref="SkipLocalsInitAttribute"/>, and every buffer below is written
/// before it is read.</b> C# zeroes a <c>stackalloc</c> unless told not to, and
/// these two functions are the whole cost of vvis: 2fort's flow makes 1.3
/// BILLION separator clips, each of which zeroes 288 bytes of ping-pong
/// windings and then zeroes 520 more bytes of side and distance tables in every
/// chop it performs. None of that zeroing is ever read -- <c>dists</c> and
/// <c>sides</c> are filled for indices <c>0..n</c> before any index is read,
/// and a ping-pong buffer is only read back over the range the chop that wrote
/// it reported. So the memset is pure loss, and it is loss multiplied by a
/// billion.
/// </para>
/// </remarks>
[SkipLocalsInit]
public static class VisClip
{
    /// <summary>
    /// <c>MAX_POINTS_ON_WINDING</c>: the cap on
    /// a winding read from the portal file.
    /// </summary>
    public const int MaxPointsOnWinding = 64;

    /// <summary>
    /// <c>MAX_POINTS_ON_FIXED_WINDING</c>: the
    /// cap on a winding produced by a chop.
    /// </summary>
    /// <remarks>
    /// Twelve, and the input may have sixty-four, so a chop that would produce
    /// a thirteenth point gives up and returns the original. See
    /// <see cref="VisChopResult.Unchanged"/>.
    /// </remarks>
    public const int MaxPointsOnFixedWinding = 12;

    /// <summary>
    /// <c>ON_VIS_EPSILON</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="double"/>, and that is not a transcription choice.</b>
    /// The C++ spells it <c>0.01</c> with no <c>f</c>, so every
    /// <c>float &lt; ON_VIS_EPSILON</c> in the reference implementation promotes its left
    /// side to double and compares against 0.010000000000000000208, not against
    /// <c>0.01f</c> = 0.0099999997764825821.
    /// </para>
    /// <para>
    /// For the <c>&gt;</c> comparisons that makes no difference, because no
    /// float lies strictly between the two values. For the <c>&lt;</c>
    /// comparison it decides the case exactly at
    /// <c>0.01f</c>: in double that is below the epsilon and the separator is
    /// skipped, in float it is not below and the separator is used. So the
    /// constant is held in double here and every comparison is written to
    /// promote, which is what C does.
    /// </para>
    /// </remarks>
    public const double OnVisEpsilon = 0.01;

    private const int SideFront = 0;
    private const int SideBack = 1;
    private const int SideOn = 2;

    /// <summary>
    /// Clips a winding by a plane, keeping the front side
    /// </summary>
    /// <param name="input">The winding to clip; at most
    /// <see cref="MaxPointsOnWinding"/> points.</param>
    /// <param name="normal">The clipping plane's normal.</param>
    /// <param name="distance">The clipping plane's distance.</param>
    /// <param name="output">
    /// Where a clipped result goes; at least
    /// <see cref="MaxPointsOnFixedWinding"/> points, and it MUST NOT overlap
    /// <paramref name="input"/> -- the fallback path returns
    /// <see cref="VisChopResult.Unchanged"/> after having already written some
    /// of the output.
    /// </param>
    /// <param name="outputCount">
    /// How many points the result has: the length of
    /// <paramref name="input"/> when the result is
    /// <see cref="VisChopResult.Unchanged"/>, the number written to
    /// <paramref name="output"/> when it is <see cref="VisChopResult.Clipped"/>,
    /// and zero when it is <see cref="VisChopResult.Empty"/>.
    /// </param>
    /// <returns>Which of the three outcomes happened.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> has more than <see cref="MaxPointsOnWinding"/>
    /// points, or <paramref name="output"/> is too small.
    /// </exception>
    public static VisChopResult ChopWinding(
        ReadOnlySpan<Vec3> input,
        Vec3 normal,
        float distance,
        Span<Vec3> output,
        out int outputCount)
    {
        if (input.Length > MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a vis winding holds at most {MaxPointsOnWinding} points, got {input.Length}",
                nameof(input));
        }

        if (output.Length < MaxPointsOnFixedWinding)
        {
            throw new ArgumentException(
                $"a chop needs {MaxPointsOnFixedWinding} points of room, got {output.Length}",
                nameof(output));
        }

        // One past the end, for the sides[i] = sides[0] wrap.
        Span<float> dists = stackalloc float[MaxPointsOnWinding + 1];
        Span<int> sides = stackalloc int[MaxPointsOnWinding + 1];
        int front = 0;
        int back = 0;

        int n = input.Length;
        for (int i = 0; i < n; i++)
        {
            // Two statements, not one expression: the dot is
            // formed first and the plane distance subtracted afterwards. Vec3.Dot
            // is longhand float arithmetic and RyuJIT does not contract on its
            // own, so nothing here fuses into an FMA -- which would change the
            // last bit of a dot that sits on the epsilon and, through the
            // side classification below, change which portals see each other.
            float dot = Vec3.Dot(input[i], normal);
            dot -= distance;
            dists[i] = dot;

            if (dot > OnVisEpsilon)
            {
                sides[i] = SideFront;
                front++;
            }
            else if (dot < -OnVisEpsilon)
            {
                sides[i] = SideBack;
                back++;
            }
            else
            {
                sides[i] = SideOn;
            }
        }

        if (back == 0)
        {
            // -- completely on the front side.
            outputCount = n;
            return VisChopResult.Unchanged;
        }

        if (front == 0)
        {
            // -- nothing survives.
            outputCount = 0;
            return VisChopResult.Empty;
        }

        sides[n] = sides[0];
        dists[n] = dists[0];

        int np = 0;
        for (int i = 0; i < n; i++)
        {
            Vec3 p1 = input[i];

            if (np == MaxPointsOnFixedWinding)
            {
                outputCount = n;
                return VisChopResult.Unchanged;
            }

            if (sides[i] == SideOn)
            {
                output[np++] = p1;
                continue;
            }

            if (sides[i] == SideFront)
            {
                output[np++] = p1;
            }

            if (sides[i + 1] == SideOn || sides[i + 1] == sides[i])
            {
                continue;
            }

            if (np == MaxPointsOnFixedWinding)
            {
                outputCount = n;
                return VisChopResult.Unchanged;
            }

            // Is `w->points[(i+1)%w->numpoints]`, and i is already
            // known to be below n, so the remainder can only wrap on the last
            // edge. Written as the wrap it is rather than as a division: this
            // line is inside the innermost loop of the innermost function of
            // vvis, and an integer `%` by a value the JIT cannot see is an
            // idiv -- tens of cycles to compute a number that is i+1 every time
            // but once.
            Vec3 p2 = input[i + 1 == n ? 0 : i + 1];
            float dot = dists[i] / (dists[i] - dists[i + 1]);

            // The axis-aligned special case: on an axis where
            // the normal is exactly +-1 the split point's coordinate IS the
            // plane distance, so it is assigned rather than interpolated. Not an
            // optimisation -- it is what keeps a cut against an axial portal
            // plane landing exactly on the plane instead of a rounding away from
            // it, which is what the surrounding epsilon comparisons are sized
            // for.
            output[np++] = new Vec3(
                Axis(normal.X, distance, p1.X, p2.X, dot),
                Axis(normal.Y, distance, p1.Y, p2.Y, dot),
                Axis(normal.Z, distance, p1.Z, p2.Z, dot));
        }

        outputCount = np;
        return VisChopResult.Clipped;
    }

    /// <summary>
    /// Clips <paramref name="target"/> by every separating plane formed from an
    /// edge of <paramref name="source"/> and a vertex of
    /// <paramref name="pass"/>.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="target">The far portal, the one being clipped.</param>
    /// <param name="flipClip">
    /// True when the ordering is pass, source, target rather than source, pass,
    /// target -- stock's second call.
    /// </param>
    /// <param name="result">
    /// Where the surviving winding is written; at least
    /// <see cref="MaxPointsOnWinding"/> points, because when no separating
    /// plane clips anything the target comes back untouched and the target may
    /// be a whole portal winding. It MUST NOT overlap
    /// <paramref name="source"/> or <paramref name="pass"/>; overlapping
    /// <paramref name="target"/> is allowed.
    /// </param>
    /// <param name="resultCount">How many points survived.</param>
    /// <returns>
    /// False when the target was clipped away entirely, which is stock's NULL
    /// and means the far portal cannot be seen through.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A winding is longer than <see cref="MaxPointsOnWinding"/>, or
    /// <paramref name="result"/> is too small.
    /// </exception>
    public static bool ClipToSeparators(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<Vec3> target,
        bool flipClip,
        Span<Vec3> result,
        out int resultCount)
    {
        if (result.Length < MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a clip needs {MaxPointsOnWinding} points of room, got {result.Length}",
                nameof(result));
        }

        // Two buffers to alternate between, so a chop never writes into the
        // winding it is reading. Stock gets this from the three winding slots in
        // its pstack_t; this function is not on the recursion path (it is
        // called and returns before RecursiveLeafFlow recurses), so the 288
        // bytes are free.
        Span<Vec3> ping = stackalloc Vec3[MaxPointsOnFixedWinding];
        Span<Vec3> pong = stackalloc Vec3[MaxPointsOnFixedWinding];
        bool usePing = true;

        // `scoped`, because it is reassigned to point into the stackallocs
        // above: the compiler otherwise infers its escape scope from `target`,
        // which is a parameter and therefore returnable.
        scoped ReadOnlySpan<Vec3> current = target;

        for (int i = 0; i < source.Length; i++)
        {
            // And the same wrap-not-a-division as in ChopWinding.
            int l = i + 1 == source.Length ? 0 : i + 1;
            Vec3 v1 = source[l] - source[i];

            for (int j = 0; j < pass.Length; j++)
            {
                if (!TrySeparator(source, pass, i, l, v1, j, out Vec3 planeNormal, out float planeDist))
                {
                    continue;
                }

                if (flipClip)
                {
                    planeNormal = Negate(planeNormal);
                    planeDist = -planeDist;
                }

                Span<Vec3> destination = usePing ? ping : pong;
                VisChopResult chopped = ChopWinding(
                    current, planeNormal, planeDist, destination, out int count);

                if (chopped == VisChopResult.Empty)
                {
                    resultCount = 0;
                    return false;
                }

                if (chopped == VisChopResult.Clipped)
                {
                    current = destination[..count];
                    usePing = !usePing;
                }
            }
        }

        current.CopyTo(result);
        resultCount = current.Length;
        return true;
    }

    /// <summary>
    /// Derives every separating plane <see cref="ClipToSeparators"/> would use,
    /// without clipping anything.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="normals">
    /// Where the plane normals go; room for
    /// <c>source.Length * pass.Length</c> is always enough.
    /// </param>
    /// <param name="distances">Where the plane distances go, same length.</param>
    /// <returns>
    /// How many planes survived, in the order <see cref="ClipToSeparators"/>
    /// would have applied them, or -1 when the buffers were too small.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="normals"/> and <paramref name="distances"/> differ in
    /// length.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The planes do not depend on the target.</b> That is the point of
    /// separating this out: in the portal flow <c>source</c> and <c>pass</c> are
    /// usually the SAME for every candidate a frame considers -- <c>pass</c> is
    /// the frame's own parameter, and <c>source</c> is the frame's parameter too
    /// whenever the base portal's sphere lies entirely behind the candidate's
    /// plane, which is the common case once the flow is a few clusters from
    /// where it started. Stock re-derives all of it per candidate, because its
    /// separators are locals inside <c>ClipToSeperators</c> and it has nowhere
    /// to put them.
    /// </para>
    /// <para>
    /// The planes are the PRE-<c>flipClip</c> ones. <c>flipClip</c> is applied
    /// after both side tests and is a property of
    /// which of the two calls this is rather than of the geometry, so one
    /// derivation serves both.
    /// </para>
    /// </remarks>
    public static int BuildSeparators(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        Span<Vec3> normals,
        Span<float> distances)
    {
        if (normals.Length != distances.Length)
        {
            throw new ArgumentException(
                "a separator list needs one distance per normal", nameof(distances));
        }

        int found = 0;
        for (int i = 0; i < source.Length; i++)
        {
            int l = i + 1 == source.Length ? 0 : i + 1;
            Vec3 v1 = source[l] - source[i];

            for (int j = 0; j < pass.Length; j++)
            {
                if (!TrySeparator(source, pass, i, l, v1, j, out Vec3 planeNormal, out float planeDist))
                {
                    continue;
                }

                if (found == normals.Length)
                {
                    return -1;
                }

                normals[found] = planeNormal;
                distances[found] = planeDist;
                found++;
            }
        }

        return found;
    }

    /// <summary>
    /// Clips a winding by separating planes <see cref="BuildSeparators"/> has
    /// already derived.
    /// </summary>
    /// <param name="normals">The plane normals, in application order.</param>
    /// <param name="distances">The plane distances.</param>
    /// <param name="target">The far portal, the one being clipped.</param>
    /// <param name="flipClip">
    /// True when the ordering is pass, source, target -- stock's second call at
    /// </param>
    /// <param name="result">
    /// Where the surviving winding is written; at least
    /// <see cref="MaxPointsOnWinding"/> points. It MUST NOT overlap
    /// <paramref name="normals"/>; overlapping <paramref name="target"/> is
    /// allowed.
    /// </param>
    /// <param name="resultCount">How many points survived.</param>
    /// <returns>False when the target was clipped away entirely.</returns>
    /// <exception cref="ArgumentException">
    /// The two plane arrays differ in length, or <paramref name="result"/> is
    /// too small.
    /// </exception>
    public static bool ClipToSeparatorPlanes(
        ReadOnlySpan<Vec3> normals,
        ReadOnlySpan<float> distances,
        ReadOnlySpan<Vec3> target,
        bool flipClip,
        Span<Vec3> result,
        out int resultCount)
    {
        if (normals.Length != distances.Length)
        {
            throw new ArgumentException(
                "a separator list needs one distance per normal", nameof(distances));
        }

        if (result.Length < MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a clip needs {MaxPointsOnWinding} points of room, got {result.Length}",
                nameof(result));
        }

        Span<Vec3> ping = stackalloc Vec3[MaxPointsOnFixedWinding];
        Span<Vec3> pong = stackalloc Vec3[MaxPointsOnFixedWinding];
        bool usePing = true;
        scoped ReadOnlySpan<Vec3> current = target;

        for (int p = 0; p < normals.Length; p++)
        {
            Vec3 planeNormal = normals[p];
            float planeDist = distances[p];

            if (flipClip)
            {
                planeNormal = Negate(planeNormal);
                planeDist = -planeDist;
            }

            Span<Vec3> destination = usePing ? ping : pong;
            VisChopResult chopped = ChopWinding(
                current, planeNormal, planeDist, destination, out int count);

            if (chopped == VisChopResult.Empty)
            {
                resultCount = 0;
                return false;
            }

            if (chopped == VisChopResult.Clipped)
            {
                current = destination[..count];
                usePing = !usePing;
            }
        }

        current.CopyTo(result);
        resultCount = current.Length;
        return true;
    }

    /// <summary>
    /// One candidate separating plane: the edge <paramref name="i"/>-
    /// <paramref name="l"/> of <paramref name="source"/> against vertex
    /// <paramref name="j"/> of <paramref name="pass"/>
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="i">The edge's first vertex.</param>
    /// <param name="l">The edge's second vertex, <paramref name="i"/> wrapped.</param>
    /// <param name="v1">The edge vector, hoisted out of the vertex loop.</param>
    /// <param name="j">Which vertex of <paramref name="pass"/>.</param>
    /// <param name="planeNormal">The plane's normal, before <c>flipClip</c>.</param>
    /// <param name="planeDist">The plane's distance, before <c>flipClip</c>.</param>
    /// <returns>False when this pair does not separate.</returns>
    /// <remarks>
    /// Extracted so the fused clip and <see cref="BuildSeparators"/> share ONE
    /// copy of the arithmetic. Two transcriptions of the same forty lines of
    /// float-exact geometry, kept in step by hand, is precisely the defect this
    /// file is written to avoid.
    /// </remarks>
    private static bool TrySeparator(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        int i,
        int l,
        Vec3 v1,
        int j,
        out Vec3 planeNormal,
        out float planeDist)
    {
        planeNormal = default;
        planeDist = 0f;

        Vec3 v2 = pass[j] - source[i];

        // Written out rather than Vec3.Cross so the operand
        // order is on the page: this is cross(v1, v2), where PlaneFromWinding
        // next door is cross(v2, v1).
        float nx = (v1.Y * v2.Z) - (v1.Z * v2.Y);
        float ny = (v1.Z * v2.X) - (v1.X * v2.Z);
        float nz = (v1.X * v2.Y) - (v1.Y * v2.X);

        // THE QUIRK: `length` here is the SQUARED length, and
        // it is compared against ON_VIS_EPSILON, which is the epsilon the rest
        // of the file uses on LINEAR distances. So the degeneracy test rejects
        // cross products shorter than 0.1, not shorter than 0.01. Faithful, and
        // load-bearing: raising it to a squared epsilon admits near-degenerate
        // separators that clip the target away, which REMOVES visibility that
        // stock keeps.
        float length = (nx * nx) + (ny * ny) + (nz * nz);
        if (length < OnVisEpsilon)
        {
            return false;
        }

        // `sqrt` on a float promotes to double, and the reciprocal
        // is formed in double before being narrowed back to vec_t. Doing it in
        // float instead double-rounds and moves the last bit of the normal.
        length = (float)(1.0 / Math.Sqrt(length));
        nx *= length;
        ny *= length;
        nz *= length;

        Vec3 normal = new(nx, ny, nz);
        float distance = Vec3.Dot(pass[j], normal);

        // -- which side of the candidate plane the source
        // portal is on. The loop STOPS at the first point that is off the plane,
        // and running to the end means the source is planar with the candidate,
        // which is not a separator.
        bool flipTest = false;
        int k;
        for (k = 0; k < source.Length; k++)
        {
            if (k == i || k == l)
            {
                continue;
            }

            float d = Vec3.Dot(source[k], normal) - distance;
            if (d < -OnVisEpsilon)
            {
                flipTest = false;
                break;
            }

            if (d > OnVisEpsilon)
            {
                flipTest = true;
                break;
            }
        }

        if (k == source.Length)
        {
            return false;
        }

        if (flipTest)
        {
            normal = Negate(normal);
            distance = -distance;
        }

        // -- every other point of pass must be on the positive
        // side, and at least one strictly so.
        int positive = 0;
        for (k = 0; k < pass.Length; k++)
        {
            if (k == j)
            {
                continue;
            }

            float d = Vec3.Dot(pass[k], normal) - distance;
            if (d < -OnVisEpsilon)
            {
                break;
            }

            if (d > OnVisEpsilon)
            {
                positive++;
            }
        }

        if (k != pass.Length)
        {
            return false;
        }

        if (positive == 0)
        {
            return false;
        }

        planeNormal = normal;
        planeDist = distance;
        return true;
    }

    /// <summary>
    /// <c>VectorSubtract(vec3_origin, v, v)</c>, which is how
 /// Spells a negation.
    /// </summary>
    /// <param name="v">The vector to negate.</param>
    /// <returns>Zero minus it.</returns>
    /// <remarks>
    /// <c>0f - x</c> rather than <c>-x</c>, so a zero component comes back as
    /// <c>+0</c> exactly as it does in stock. Nothing downstream can currently
    /// tell the two apart, and writing it the other way would be a difference
    /// nobody had checked.
    /// </remarks>
    public static Vec3 Negate(Vec3 v) => new(0f - v.X, 0f - v.Y, 0f - v.Z);

    private static float Axis(float normal, float distance, float a, float b, float dot)
    {
        if (normal == 1f)
        {
            return distance;
        }

        if (normal == -1f)
        {
            return -distance;
        }

        return a + (dot * (b - a));
    }
}
