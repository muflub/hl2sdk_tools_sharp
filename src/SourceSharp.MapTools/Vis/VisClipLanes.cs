//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// One frame's separating planes for one ordering, derived on demand.
/// </summary>
/// <remarks>
/// <para>
/// The frame used to derive every plane of both orderings the first time a
/// candidate could use them. On 2fort that derived 2.37 billion planes and
/// the separator clips only ever reached 1.91 billion of them: a clip stops
/// at the first plane that empties the target, and when every candidate of a
/// frame stops early, the planes after the furthest one reached were built
/// for nothing. Worse, 42 million frames built the whole reverse list and
/// never ran a single reverse clip, because every candidate died in the
/// forward one.
/// </para>
/// <para>
/// So the list is filled one source EDGE at a time, and only when a clip has
/// used every plane already in it. The planes come out in exactly the order
/// a full derivation would give them -- edge-major, pass vertex minor -- so
/// the clip sees the same sequence whether the planes were derived for it or
/// for an earlier candidate. The edge is the unit because the lane-parallel
/// derivation (<see cref="VisClipLanes.DeriveEdge"/>) works on one edge
/// against all of the pass winding's vertices at once.
/// </para>
/// </remarks>
internal struct VisSeparatorMemo
{
    /// <summary>The planes' normals; room for every plane the pairing can produce.</summary>
    internal Vec3[] Normals;

    /// <summary>The planes' distances, same length as <see cref="Normals"/>.</summary>
    internal float[] Distances;

    /// <summary>How many planes are derived so far.</summary>
    internal int Count;

    /// <summary>
    /// The next source edge to derive from; the source winding's length once
    /// every plane is in the list.
    /// </summary>
    internal int NextEdge;

    /// <summary>Starts an empty list over the given storage.</summary>
    /// <param name="normals">
    /// Room for <c>source.Length * pass.Length</c> normals, which is as many
    /// as a derivation can produce.
    /// </param>
    /// <param name="distances">The same room for distances.</param>
    internal VisSeparatorMemo(Vec3[] normals, float[] distances)
    {
        Normals = normals;
        Distances = distances;
        Count = 0;
        NextEdge = 0;
    }
}

/// <summary>
/// <see cref="VisClip"/>'s two hot loops, four lanes at a time, producing
/// the scalar code's bits exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these two loops.</b> A CPU trace of 2fort put 98.9 % of vvis in
/// the portal flow, and within it the separating-plane derivation and the
/// chop that applies each plane were two thirds of the time. Both are the
/// same shape: one small winding (real portals are quads, and a chopped
/// winding averages four points) tested against one plane, or one plane
/// candidate tested against one small winding. Four points is one
/// <see cref="Vector128{T}"/>, so the per-point loop becomes one iteration and
/// its data-dependent branches become lane masks.
/// </para>
/// <para>
/// <b>Why the bits cannot move.</b> Every separating plane decides which
/// portals see each other, so a last-bit difference in a distance can flip
/// an epsilon comparison and change the PVS. Each lane therefore performs the
/// scalar code's operations in the scalar code's order on the scalar code's
/// widths: <c>((x*nx + y*ny) + z*nz) - d</c> in single precision, the
/// reciprocal square root widened to double, square-rooted and divided there,
/// and narrowed back. Lane-wise add, subtract, multiply, divide and square
/// root are correctly rounded IEEE operations on SSE and on AdvSimd alike, and
/// the JIT does not contract a multiply and an add into an FMA, so a lane
/// computes exactly what the scalar statement does. Nothing here uses an
/// estimate, a horizontal add, <see cref="Vector128.Dot{T}"/> or a
/// fused multiply-add, each of which would round differently.
/// <see cref="Vector128{T}"/> throughout rather than <c>Sse</c>, so arm64
/// runs the same code.
/// </para>
/// <para>
/// <b>The epsilon compares in single precision here, and that is exact.</b>
/// The scalar code compares a <see cref="float"/> against the double
/// <see cref="VisClip.OnVisEpsilon"/>, promoting. For the three comparisons
/// used, the promoted comparison and a single-precision one against
/// <see cref="EpsilonSingle"/> agree for every float, NaN included:
/// <c>0.01f</c> lies just BELOW <c>0.01</c> and the next float up lies above
/// it, so <c>x &gt; 0.01</c> is <c>x &gt; 0.01f</c>, <c>x &lt; -0.01</c> is
/// <c>x &lt; -0.01f</c>, and <c>x &lt; 0.01</c> is <c>x &lt;= 0.01f</c>
/// (the degeneracy test, which is the one case where the two constants do
/// differ). <c>VisClipLanesTests</c> checks that on every float around the
/// boundaries.
/// </para>
/// <para>
/// <b>Compliance.</b> None of this arithmetic has an estimate-based variant:
/// vvis's clipper uses an exact square root and divide in both compliance
/// modes, so there is no <c>-compliance stock</c> path to reproduce here.
/// </para>
/// </remarks>
[SkipLocalsInit]
internal static class VisClipLanes
{
    /// <summary>
    /// <c>0.01f</c>: the single-precision comparison that agrees with the
    /// promoted comparison against <see cref="VisClip.OnVisEpsilon"/>, as the
    /// type's remarks explain.
    /// </summary>
    internal const float EpsilonSingle = 0.01f;

    /// <summary>
    /// Transposes a winding into three coordinate columns, padded to a whole
    /// number of lanes with copies of the first point.
    /// </summary>
    /// <param name="winding">At most <see cref="VisClip.MaxPointsOnWinding"/> points.</param>
    /// <param name="xs">The X column; room for <see cref="VisClip.MaxPointsOnWinding"/>.</param>
    /// <param name="ys">The Y column.</param>
    /// <param name="zs">The Z column.</param>
    /// <returns>The padded length, a multiple of four.</returns>
    /// <remarks>
    /// Padding with the FIRST point rather than zeros or a mask: a copy of a
    /// real point classifies exactly as that point does, so it can never add
    /// a front or a back the winding does not have, and the classifier needs
    /// no per-call lane mask. The one place the padding is read as a value is
    /// the chop's wrap slot, <c>dists[n]</c>, which the chop sets to
    /// <c>dists[0]</c> anyway.
    /// </remarks>
    internal static int Transpose(ReadOnlySpan<Vec3> winding, Span<float> xs, Span<float> ys, Span<float> zs)
    {
        int n = winding.Length;
        int padded = (n + 3) & ~3;
        if (n == 0)
        {
            return 0;
        }

        if ((uint)padded > (uint)xs.Length || (uint)padded > (uint)ys.Length || (uint)padded > (uint)zs.Length)
        {
            throw new ArgumentException("the columns are too short for the winding", nameof(xs));
        }

        ref float x = ref MemoryMarshal.GetReference(xs);
        ref float y = ref MemoryMarshal.GetReference(ys);
        ref float z = ref MemoryMarshal.GetReference(zs);

        // Four points per store, assembled in registers. Written one float at
        // a time instead, the classifier's first vector load of each column
        // spans four separate stores, which no x86 core forwards: the load
        // waits for all four to retire, and on 2fort that wait was four
        // percent of the whole flow.
        Vec3 first = winding[0];
        for (int i = 0; i < n; i += 4)
        {
            Vec3 p0 = winding[i];
            Vec3 p1 = i + 1 < n ? winding[i + 1] : first;
            Vec3 p2 = i + 2 < n ? winding[i + 2] : first;
            Vec3 p3 = i + 3 < n ? winding[i + 3] : first;
            Vector128.Create(p0.X, p1.X, p2.X, p3.X).StoreUnsafe(ref x, (nuint)i);
            Vector128.Create(p0.Y, p1.Y, p2.Y, p3.Y).StoreUnsafe(ref y, (nuint)i);
            Vector128.Create(p0.Z, p1.Z, p2.Z, p3.Z).StoreUnsafe(ref z, (nuint)i);
        }

        return padded;
    }

    /// <summary>
    /// Every point's signed distance from a plane, and whether any point is
    /// in front of it and whether any is behind it.
    /// </summary>
    /// <param name="xs">The X column, as <see cref="Transpose"/> wrote it.</param>
    /// <param name="ys">The Y column.</param>
    /// <param name="zs">The Z column.</param>
    /// <param name="padded">The padded length.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <param name="distance">The plane's distance.</param>
    /// <param name="dists">Where each point's distance goes; room for <paramref name="padded"/>.</param>
    /// <returns>Bit 0: some point is in front. Bit 1: some point is behind.</returns>
    /// <remarks>
    /// The lane form of <see cref="VisClip.ChopWinding"/>'s first loop.
    /// That loop counts fronts and backs, but only ever asks whether each
    /// count is zero, so two bits say everything it needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Classify(
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        ReadOnlySpan<float> zs,
        int padded,
        Vec3 normal,
        float distance,
        Span<float> dists)
    {
        if ((uint)padded > (uint)xs.Length || (uint)padded > (uint)ys.Length
            || (uint)padded > (uint)zs.Length || (uint)padded > (uint)dists.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(padded));
        }

        ref float x = ref MemoryMarshal.GetReference(xs);
        ref float y = ref MemoryMarshal.GetReference(ys);
        ref float z = ref MemoryMarshal.GetReference(zs);
        ref float o = ref MemoryMarshal.GetReference(dists);

        Vector128<float> nx = Vector128.Create(normal.X);
        Vector128<float> ny = Vector128.Create(normal.Y);
        Vector128<float> nz = Vector128.Create(normal.Z);
        Vector128<float> d = Vector128.Create(distance);
        Vector128<float> above = Vector128.Create(EpsilonSingle);
        Vector128<float> below = Vector128.Create(-EpsilonSingle);

        Vector128<float> front = Vector128<float>.Zero;
        Vector128<float> back = Vector128<float>.Zero;
        for (nuint i = 0; i < (nuint)padded; i += 4)
        {
            // Vec3.Dot(point, normal) and then the subtraction, as the scalar
            // chop's two statements: ((x*nx + y*ny) + z*nz) - d.
            Vector128<float> dot =
                (Vector128.LoadUnsafe(ref x, i) * nx)
                + (Vector128.LoadUnsafe(ref y, i) * ny)
                + (Vector128.LoadUnsafe(ref z, i) * nz);
            dot -= d;
            dot.StoreUnsafe(ref o, i);
            front |= Vector128.GreaterThan(dot, above);
            back |= Vector128.LessThan(dot, below);
        }

        int flags = 0;
        if (front != Vector128<float>.Zero)
        {
            flags |= 1;
        }

        if (back != Vector128<float>.Zero)
        {
            flags |= 2;
        }

        return flags;
    }

    /// <summary>
    /// <see cref="VisClip.ChopWinding"/> through the lane classifier: the same
    /// contract, the same result, the same bits.
    /// </summary>
    /// <param name="input">The winding to clip; at most <see cref="VisClip.MaxPointsOnWinding"/> points.</param>
    /// <param name="normal">The clipping plane's normal.</param>
    /// <param name="distance">The clipping plane's distance.</param>
    /// <param name="output">Where a clipped result goes; must not overlap <paramref name="input"/>.</param>
    /// <param name="outputCount">How many points the result has.</param>
    /// <returns>Which of the three outcomes happened.</returns>
    /// <exception cref="ArgumentException">A winding or buffer is out of range.</exception>
    /// <remarks>
    /// The flow does not call this: its separator clip keeps the target
    /// transposed across planes rather than transposing per chop. It exists
    /// so a fact can hold the classifier to the scalar chop on arbitrary
    /// windings, one call against one call.
    /// </remarks>
    internal static VisChopResult ChopWinding(
        ReadOnlySpan<Vec3> input,
        Vec3 normal,
        float distance,
        Span<Vec3> output,
        out int outputCount)
    {
        if (input.Length > VisClip.MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a vis winding holds at most {VisClip.MaxPointsOnWinding} points, got {input.Length}",
                nameof(input));
        }

        if (output.Length < VisClip.MaxPointsOnFixedWinding)
        {
            throw new ArgumentException(
                $"a chop needs {VisClip.MaxPointsOnFixedWinding} points of room, got {output.Length}",
                nameof(output));
        }

        Span<float> xs = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> ys = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> zs = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> dists = stackalloc float[VisClip.MaxPointsOnWinding + 1];
        Span<int> sides = stackalloc int[VisClip.MaxPointsOnWinding + 1];

        int padded = Transpose(input, xs, ys, zs);
        return Chop(input, xs, ys, zs, padded, normal, distance, dists, sides, output, out outputCount);
    }

    /// <summary>
    /// Clips a winding by one frame's separating planes, deriving them as the
    /// clip reaches them.
    /// </summary>
    /// <param name="memo">The frame's list for this ordering; extended in place.</param>
    /// <param name="source">The near portal of the ordering the list is derived from.</param>
    /// <param name="pass">The middle portal of that ordering.</param>
    /// <param name="target">The far portal, the one being clipped.</param>
    /// <param name="flipClip">True for the reversed ordering's clip, stock's second call.</param>
    /// <param name="result">
    /// Where the surviving winding is written; at least
    /// <see cref="VisClip.MaxPointsOnWinding"/> points. Overlapping
    /// <paramref name="target"/> is allowed.
    /// </param>
    /// <param name="resultCount">How many points survived.</param>
    /// <returns>False when the target was clipped away entirely.</returns>
    /// <exception cref="ArgumentException"><paramref name="result"/> is too small.</exception>
    /// <remarks>
    /// <para>
    /// The same function as <see cref="VisClip.ClipToSeparatorPlanes"/> over
    /// the full <see cref="VisClip.BuildSeparators"/> list: same planes, same
    /// order, same chops, same bits. Two things differ, and neither is visible
    /// in the answer. The planes are derived only as far as some clip has
    /// needed them (see <see cref="VisSeparatorMemo"/>), and the target is
    /// held transposed so each plane is one lane-parallel classification.
    /// </para>
    /// <para>
    /// The transposition is paid once per call and once per chop that
    /// actually cuts. Most chops do not: on 2fort 73 % of them find the whole
    /// winding in front and leave it unchanged, so the transposed columns
    /// serve plane after plane.
    /// </para>
    /// </remarks>
    internal static bool ClipToSeparators(
        ref VisSeparatorMemo memo,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<Vec3> target,
        bool flipClip,
        Span<Vec3> result,
        out int resultCount) =>
        ClipToSeparators(
            ref memo, source, pass, target, flipClip, Vector256.IsHardwareAccelerated, result, out resultCount);

    /// <summary>
    /// <see cref="ClipToSeparators(ref VisSeparatorMemo, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, bool, Span{Vec3}, out int)"/>
    /// with the derivation's grain chosen by the caller.
    /// </summary>
    /// <param name="memo">The frame's list for this ordering; extended in place.</param>
    /// <param name="source">The near portal of the ordering the list is derived from.</param>
    /// <param name="pass">The middle portal of that ordering.</param>
    /// <param name="target">The far portal, the one being clipped.</param>
    /// <param name="flipClip">True for the reversed ordering's clip.</param>
    /// <param name="pairEdges">
    /// True to extend the list two source edges at a time
    /// (<see cref="DeriveEdgePair"/>) wherever two remain, false for one at a
    /// time. The flow passes whether the CPU has 256-bit vectors; the facts
    /// pass both.
    /// </param>
    /// <param name="result">Where the surviving winding is written.</param>
    /// <param name="resultCount">How many points survived.</param>
    /// <returns>False when the target was clipped away entirely.</returns>
    /// <remarks>
    /// <para>
    /// <b>Deriving a second edge before the clip needs it cannot change the
    /// clip.</b> The list is the same list either way -- edge-major, pass
    /// vertex minor, each plane's bits the scalar code's -- and the clip walks
    /// it from the front, stopping at the first plane that empties the target
    /// or at its end. Planes the pair put in the list early are simply there
    /// when the walk reaches them, exactly as the one-edge grain would have
    /// put them there at that moment; and if the walk stops first, they are
    /// never read (nor would the next edge have been derived). So which planes
    /// a clip applies, and in what order, does not depend on the grain; only
    /// how far past the furthest clip the list may run does, by at most one
    /// edge.
    /// </para>
    /// <para>
    /// That one edge is the price, and it is small: on 2fort at four threads
    /// the pairs derived 1.146 billion edges where one at a time derived
    /// 1.114 billion, 2.9 % more, because a clip that reaches one edge of a
    /// frame's list nearly always goes on to the next. A pair costs little
    /// more than one edge alone, so the grain is a net gain -- vvis 12 % less
    /// CPU -- where the CPU runs a 256-bit register as one; where it does
    /// not, the pair buys no overlap and would only pay the price.
    /// </para>
    /// </remarks>
    internal static bool ClipToSeparators(
        ref VisSeparatorMemo memo,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<Vec3> target,
        bool flipClip,
        bool pairEdges,
        Span<Vec3> result,
        out int resultCount)
    {
        if (result.Length < VisClip.MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a clip needs {VisClip.MaxPointsOnWinding} points of room, got {result.Length}",
                nameof(result));
        }

        Span<Vec3> ping = stackalloc Vec3[VisClip.MaxPointsOnFixedWinding];
        Span<Vec3> pong = stackalloc Vec3[VisClip.MaxPointsOnFixedWinding];
        Span<float> xs = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> ys = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> zs = stackalloc float[VisClip.MaxPointsOnWinding];
        Span<float> dists = stackalloc float[VisClip.MaxPointsOnWinding + 1];
        Span<int> sides = stackalloc int[VisClip.MaxPointsOnWinding + 1];
        bool usePing = true;

        scoped ReadOnlySpan<Vec3> current = target;
        int padded = Transpose(current, xs, ys, zs);

        for (int p = 0; ; p++)
        {
            while (p == memo.Count)
            {
                if (memo.NextEdge >= source.Length)
                {
                    current.CopyTo(result);
                    resultCount = current.Length;
                    return true;
                }

                if (pairEdges && memo.NextEdge + 1 < source.Length)
                {
                    memo.Count = DeriveEdgePair(source, pass, memo.NextEdge, memo.Normals, memo.Distances, memo.Count);
                    memo.NextEdge += 2;
                }
                else
                {
                    memo.Count = DeriveEdge(source, pass, memo.NextEdge, memo.Normals, memo.Distances, memo.Count);
                    memo.NextEdge++;
                }
            }

            Vec3 planeNormal = memo.Normals[p];
            float planeDist = memo.Distances[p];

            if (flipClip)
            {
                planeNormal = VisClip.Negate(planeNormal);
                planeDist = -planeDist;
            }

            // The classification is inlined here rather than reached through
            // Chop: this is the loop the separator clip spends its time in,
            // and a call that takes the plane as a Vec3 argument spills it
            // and reloads it as one wider load the store cannot forward to --
            // measured at a tenth of the whole flow on 2fort. Only a plane
            // that actually cuts (a fifth of them) pays for a call.
            int flags = Classify(xs, ys, zs, padded, planeNormal, planeDist, dists);

            if ((flags & 2) == 0)
            {
                // Nothing behind the plane: unchanged, and still transposed.
                continue;
            }

            if ((flags & 1) == 0)
            {
                resultCount = 0;
                return false;
            }

            Span<Vec3> destination = usePing ? ping : pong;
            if (Cut(current, planeNormal.X, planeNormal.Y, planeNormal.Z, planeDist, dists, sides, destination, out int count)
                == VisChopResult.Clipped)
            {
                current = destination[..count];
                usePing = !usePing;
                padded = Transpose(current, xs, ys, zs);
            }
        }
    }

    /// <summary>
    /// Every separating plane <see cref="VisClip.BuildSeparators"/> would
    /// derive, through <see cref="DeriveEdge"/>.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="normals">Room for <c>source.Length * pass.Length</c> normals.</param>
    /// <param name="distances">The same room for distances.</param>
    /// <returns>How many planes there are.</returns>
    /// <remarks>
    /// The flow derives lazily and never calls this. It is the whole-list
    /// form a fact compares against the scalar derivation.
    /// </remarks>
    internal static int BuildSeparators(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        Span<Vec3> normals,
        Span<float> distances)
    {
        int found = 0;
        for (int i = 0; i < source.Length; i++)
        {
            found = DeriveEdge(source, pass, i, normals, distances, found);
        }

        return found;
    }

    /// <summary>
    /// The separating planes formed from one edge of
    /// <paramref name="source"/> and each vertex of <paramref name="pass"/>,
    /// four vertices at a time.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="i">The edge's first vertex.</param>
    /// <param name="normals">Where the plane normals go.</param>
    /// <param name="distances">Where the plane distances go.</param>
    /// <param name="found">How many planes are already in the lists.</param>
    /// <returns>How many planes are in the lists afterwards.</returns>
    /// <remarks>
    /// <para>
    /// The lane form of <see cref="VisClip"/>'s <c>TrySeparator</c> over
    /// <c>j</c>, with each lane one pass vertex. Every lane does that
    /// function's arithmetic in its order; the differences are all in the
    /// control flow, and none of them changes a result:
    /// </para>
    /// <list type="bullet">
    /// <item>The source-side scan stops, per lane, at the first point off the
    /// candidate plane; here each lane records the first point that decided
    /// it and ignores later ones, and the loop ends when every lane still
    /// alive has decided.</item>
    /// <item>The pass-side scan stops at the first point behind the plane; its
    /// result is only whether any point (other than the lane's own vertex) is
    /// behind and whether any is in front, and that does not depend on the
    /// order the points are visited or on stopping early, so the lanes visit
    /// all of them.</item>
    /// <item>A lane past the end of the pass winding computes on a copy of
    /// the chunk's first vertex and is discarded.</item>
    /// </list>
    /// <para>
    /// The surviving lanes are written in lane order, which is ascending
    /// <c>j</c> -- the order the scalar loop finds them in.
    /// </para>
    /// </remarks>
    internal static int DeriveEdge(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        int i,
        Span<Vec3> normals,
        Span<float> distances,
        int found)
    {
        int l = i + 1 == source.Length ? 0 : i + 1;
        Vec3 origin = source[i];
        Vec3 v1 = source[l] - origin;

        Vector128<float> v1x = Vector128.Create(v1.X);
        Vector128<float> v1y = Vector128.Create(v1.Y);
        Vector128<float> v1z = Vector128.Create(v1.Z);
        Vector128<float> above = Vector128.Create(EpsilonSingle);
        Vector128<float> below = Vector128.Create(-EpsilonSingle);
        Vector128<float> signBit = Vector128.Create(-0.0f);
        Vector128<int> laneIndex = Vector128.Create(0, 1, 2, 3);

        Span<float> lanes = stackalloc float[16];

        for (int j0 = 0; j0 < pass.Length; j0 += 4)
        {
            int width = Math.Min(4, pass.Length - j0);
            Vec3 q0 = pass[j0];
            Vec3 q1 = width > 1 ? pass[j0 + 1] : q0;
            Vec3 q2 = width > 2 ? pass[j0 + 2] : q0;
            Vec3 q3 = width > 3 ? pass[j0 + 3] : q0;
            Vector128<float> px = Vector128.Create(q0.X, q1.X, q2.X, q3.X);
            Vector128<float> py = Vector128.Create(q0.Y, q1.Y, q2.Y, q3.Y);
            Vector128<float> pz = Vector128.Create(q0.Z, q1.Z, q2.Z, q3.Z);

            // v2 = pass[j] - source[i]; n = cross(v1, v2), in TrySeparator's
            // operand order.
            Vector128<float> v2x = px - Vector128.Create(origin.X);
            Vector128<float> v2y = py - Vector128.Create(origin.Y);
            Vector128<float> v2z = pz - Vector128.Create(origin.Z);
            Vector128<float> nx = (v1y * v2z) - (v1z * v2y);
            Vector128<float> ny = (v1z * v2x) - (v1x * v2z);
            Vector128<float> nz = (v1x * v2y) - (v1y * v2x);

            // The squared-length degeneracy quirk: rejected when
            // (double)length < 0.01, which for a float is length <= 0.01f.
            // NaN is not rejected there and is not rejected here.
            Vector128<float> length = (nx * nx) + (ny * ny) + (nz * nz);
            Vector128<float> live = Vector128.AndNot(
                Vector128.LessThan(laneIndex, Vector128.Create(width)).AsSingle(),
                Vector128.LessThanOrEqual(length, above));
            if (live == Vector128<float>.Zero)
            {
                continue;
            }

            Vector128<float> scale = ReciprocalSqrt(length, Vector256.IsHardwareAccelerated);
            nx *= scale;
            ny *= scale;
            nz *= scale;
            Vector128<float> dist = (px * nx) + (py * ny) + (pz * nz);

            // Which side of the candidate the source is on, decided per lane
            // by the first source point (other than the edge's own two) that
            // is off the plane.
            Vector128<float> decided = Vector128<float>.Zero;
            Vector128<float> flip = Vector128<float>.Zero;
            for (int k = 0; k < source.Length; k++)
            {
                if (k == i || k == l)
                {
                    continue;
                }

                Vec3 s = source[k];
                Vector128<float> d =
                    (Vector128.Create(s.X) * nx)
                    + (Vector128.Create(s.Y) * ny)
                    + (Vector128.Create(s.Z) * nz)
                    - dist;
                Vector128<float> isAbove = Vector128.GreaterThan(d, above);
                Vector128<float> fresh = Vector128.AndNot(isAbove | Vector128.LessThan(d, below), decided);
                flip |= isAbove & fresh;
                decided |= fresh;

                if (Vector128.AndNot(live, decided) == Vector128<float>.Zero)
                {
                    break;
                }
            }

            // A source planar with the candidate is not a separator.
            live &= decided;
            if (live == Vector128<float>.Zero)
            {
                continue;
            }

            // The flip: Negate is 0 - x, the distance's is a unary minus --
            // a sign flip, which is what XOR with -0 is, including on a zero.
            Vector128<float> fx = Vector128.ConditionalSelect(flip, Vector128<float>.Zero - nx, nx);
            Vector128<float> fy = Vector128.ConditionalSelect(flip, Vector128<float>.Zero - ny, ny);
            Vector128<float> fz = Vector128.ConditionalSelect(flip, Vector128<float>.Zero - nz, nz);
            Vector128<float> fd = Vector128.ConditionalSelect(flip, dist ^ signBit, dist);

            // Every other pass point on the positive side, at least one of
            // them strictly.
            //
            // "Other": the scalar loop skips k == j. The lanes do not need to,
            // because for its own vertex a lane's distance is exactly zero
            // (or NaN), which is neither behind nor ahead. The distance is
            // Dot(pass[j], n') - d' where d' was computed as Dot(pass[j], n)
            // in the same operation order; a flip negates n' as 0 - n and d'
            // as a sign flip, and negating every input of a correctly rounded
            // sum of products negates its result exactly (only the sign of a
            // zero can differ). So the two terms are equal, their difference
            // is +-0 -- or NaN when they are infinite -- and both comparisons
            // are false. Dropping the per-lane mask saves three instructions
            // in the innermost loop of the derivation; the degenerate and NaN
            // derivation facts hold it to the scalar answer.
            Vector128<float> behind = Vector128<float>.Zero;
            Vector128<float> ahead = Vector128<float>.Zero;
            for (int k = 0; k < pass.Length; k++)
            {
                Vec3 q = pass[k];
                Vector128<float> d =
                    (Vector128.Create(q.X) * fx)
                    + (Vector128.Create(q.Y) * fy)
                    + (Vector128.Create(q.Z) * fz)
                    - fd;
                behind |= Vector128.LessThan(d, below);
                ahead |= Vector128.GreaterThan(d, above);
            }

            uint bits = Vector128.AndNot(live & ahead, behind).ExtractMostSignificantBits();
            if (bits == 0)
            {
                continue;
            }

            ref float lane = ref MemoryMarshal.GetReference(lanes);
            fx.StoreUnsafe(ref lane, 0);
            fy.StoreUnsafe(ref lane, 4);
            fz.StoreUnsafe(ref lane, 8);
            fd.StoreUnsafe(ref lane, 12);
            do
            {
                int j = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                normals[found] = new Vec3(lanes[j], lanes[4 + j], lanes[8 + j]);
                distances[found] = lanes[12 + j];
                found++;
            }
            while (bits != 0);
        }

        return found;
    }

    /// <summary>
    /// <see cref="DeriveEdge"/> for two consecutive source edges at once, one
    /// per 128-bit half of a 256-bit register: the planes of edge
    /// <paramref name="i"/> and then those of edge <c>i + 1</c>, exactly as
    /// two calls in that order would write them.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="i">The first edge's first vertex; <c>i + 1</c> must be a vertex too.</param>
    /// <param name="normals">Where the plane normals go.</param>
    /// <param name="distances">Where the plane distances go.</param>
    /// <param name="found">How many planes are already in the lists.</param>
    /// <returns>How many planes are in the lists afterwards.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="i"/> is the source's last vertex, which has no second
    /// edge after it.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why two edges.</b> One edge's derivation is a single dependency
    /// chain -- cross product, squared length, a double square root, a double
    /// divide, the narrowing, the distance, then the source-side scan that
    /// needs the finished normal -- and on 2fort that chain was where
    /// <see cref="DeriveEdge"/> spent its time: the square root and the divide
    /// alone are forty cycles of latency with nothing independent to overlap
    /// them. Two edges' chains ARE independent, so running them side by side
    /// in the two halves of one register costs one chain's latency for two
    /// edges' planes, and the source- and pass-side scans are one loop for
    /// both.
    /// </para>
    /// <para>
    /// <b>Why every lane still computes what the scalar code does.</b> The
    /// halves never mix: each lane is one (edge, pass vertex) pair, and runs
    /// <see cref="DeriveEdge"/>'s arithmetic on it in its order and at its
    /// widths -- single precision up to the squared length, the reciprocal
    /// square root widened to double and narrowed back, no fused multiply-add.
    /// The two places the edges differ are handled per half: the source-side
    /// scan skips a DIFFERENT pair of points for each edge (its own two
    /// endpoints), so each source point comes with a mask that leaves out the
    /// half it belongs to; and the loop ends only when every live lane of BOTH
    /// halves has decided, which cannot change a lane's answer because a lane
    /// ignores every point after the one that decided it.
    /// </para>
    /// <para>
    /// <b>The order the planes come out in.</b> Edge-major, as a full
    /// derivation lists them: when the pass winding has more than four points
    /// the loop visits it four vertices at a time and each chunk yields planes
    /// of both edges, so the second edge's planes are held back in a local
    /// buffer and written after every one of the first edge's.
    /// </para>
    /// <para>
    /// The caller (<see cref="ClipToSeparators(ref VisSeparatorMemo, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, bool, bool, Span{Vec3}, out int)"/>) takes this only where the
    /// CPU has 256-bit vectors; elsewhere the software form of
    /// <see cref="Vector256{T}"/> is two 128-bit halves anyway and gives no
    /// overlap the out-of-order core would not already find in two
    /// <see cref="DeriveEdge"/> calls. It is exact on every CPU either way,
    /// and the facts run it everywhere.
    /// </para>
    /// </remarks>
    internal static int DeriveEdgePair(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        int i,
        Span<Vec3> normals,
        Span<float> distances,
        int found)
    {
        int n = source.Length;
        if ((uint)i >= (uint)(n - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(i), i, "an edge pair needs a vertex after the first edge's");
        }

        // Edge A is (i, i+1); edge B is (i+1, l2).
        int m = i + 1;
        int l2 = m + 1 == n ? 0 : m + 1;
        Vec3 originA = source[i];
        Vec3 originB = source[m];
        Vec3 v1a = originB - originA;
        Vec3 v1b = source[l2] - originB;

        Vector256<float> v1x = Vector256.Create(Vector128.Create(v1a.X), Vector128.Create(v1b.X));
        Vector256<float> v1y = Vector256.Create(Vector128.Create(v1a.Y), Vector128.Create(v1b.Y));
        Vector256<float> v1z = Vector256.Create(Vector128.Create(v1a.Z), Vector128.Create(v1b.Z));
        Vector256<float> ox = Vector256.Create(Vector128.Create(originA.X), Vector128.Create(originB.X));
        Vector256<float> oy = Vector256.Create(Vector128.Create(originA.Y), Vector128.Create(originB.Y));
        Vector256<float> oz = Vector256.Create(Vector128.Create(originA.Z), Vector128.Create(originB.Z));
        Vector256<float> above = Vector256.Create(EpsilonSingle);
        Vector256<float> below = Vector256.Create(-EpsilonSingle);
        Vector256<float> signBit = Vector256.Create(-0.0f);
        Vector256<int> laneIndex = Vector256.Create(0, 1, 2, 3, 0, 1, 2, 3);
        Vector128<float> allLanes = Vector128<float>.AllBitsSet;

        Span<float> lanes = stackalloc float[32];

        // Edge B's planes, held until every one of edge A's has been written.
        // Only a pass winding of more than four points needs this; a shorter
        // one has a single chunk, whose B planes go straight after its A ones.
        Span<Vec3> heldNormals = stackalloc Vec3[VisClip.MaxPointsOnWinding];
        Span<float> heldDistances = stackalloc float[VisClip.MaxPointsOnWinding];
        int held = 0;
        bool single = pass.Length <= 4;

        for (int j0 = 0; j0 < pass.Length; j0 += 4)
        {
            int width = Math.Min(4, pass.Length - j0);
            Vec3 q0 = pass[j0];
            Vec3 q1 = width > 1 ? pass[j0 + 1] : q0;
            Vec3 q2 = width > 2 ? pass[j0 + 2] : q0;
            Vec3 q3 = width > 3 ? pass[j0 + 3] : q0;
            Vector128<float> qx = Vector128.Create(q0.X, q1.X, q2.X, q3.X);
            Vector128<float> qy = Vector128.Create(q0.Y, q1.Y, q2.Y, q3.Y);
            Vector128<float> qz = Vector128.Create(q0.Z, q1.Z, q2.Z, q3.Z);
            Vector256<float> px = Vector256.Create(qx, qx);
            Vector256<float> py = Vector256.Create(qy, qy);
            Vector256<float> pz = Vector256.Create(qz, qz);

            // v2 = pass[j] - source[edge's origin]; n = cross(v1, v2).
            Vector256<float> v2x = px - ox;
            Vector256<float> v2y = py - oy;
            Vector256<float> v2z = pz - oz;
            Vector256<float> nx = (v1y * v2z) - (v1z * v2y);
            Vector256<float> ny = (v1z * v2x) - (v1x * v2z);
            Vector256<float> nz = (v1x * v2y) - (v1y * v2x);

            Vector256<float> length = (nx * nx) + (ny * ny) + (nz * nz);
            Vector256<float> live = Vector256.AndNot(
                Vector256.LessThan(laneIndex, Vector256.Create(width)).AsSingle(),
                Vector256.LessThanOrEqual(length, above));
            if (live == Vector256<float>.Zero)
            {
                continue;
            }

            // Two independent square-root-and-divide chains, one per edge.
            Vector256<double> one = Vector256.Create(1.0);
            Vector256<float> scale = Vector256.Narrow(
                one / Vector256.Sqrt(Vector256.WidenLower(length)),
                one / Vector256.Sqrt(Vector256.WidenUpper(length)));
            nx *= scale;
            ny *= scale;
            nz *= scale;
            Vector256<float> dist = (px * nx) + (py * ny) + (pz * nz);

            // The source-side scan, each half skipping its own edge's two
            // endpoints: A skips i and i+1, B skips i+1 and l2.
            Vector256<float> decided = Vector256<float>.Zero;
            Vector256<float> flip = Vector256<float>.Zero;
            for (int k = 0; k < n; k++)
            {
                bool skipA = k == i || k == m;
                bool skipB = k == m || k == l2;
                if (skipA && skipB)
                {
                    continue;
                }

                Vector256<float> eligible = Vector256.Create(
                    skipA ? Vector128<float>.Zero : allLanes,
                    skipB ? Vector128<float>.Zero : allLanes);

                Vec3 s = source[k];
                Vector256<float> d =
                    (Vector256.Create(s.X) * nx)
                    + (Vector256.Create(s.Y) * ny)
                    + (Vector256.Create(s.Z) * nz)
                    - dist;
                Vector256<float> isAbove = Vector256.GreaterThan(d, above);
                Vector256<float> fresh = Vector256.AndNot(
                    (isAbove | Vector256.LessThan(d, below)) & eligible, decided);
                flip |= isAbove & fresh;
                decided |= fresh;

                if (Vector256.AndNot(live, decided) == Vector256<float>.Zero)
                {
                    break;
                }
            }

            live &= decided;
            if (live == Vector256<float>.Zero)
            {
                continue;
            }

            Vector256<float> fx = Vector256.ConditionalSelect(flip, Vector256<float>.Zero - nx, nx);
            Vector256<float> fy = Vector256.ConditionalSelect(flip, Vector256<float>.Zero - ny, ny);
            Vector256<float> fz = Vector256.ConditionalSelect(flip, Vector256<float>.Zero - nz, nz);
            Vector256<float> fd = Vector256.ConditionalSelect(flip, dist ^ signBit, dist);

            // The pass-side test, as DeriveEdge's (whose remarks say why a
            // lane need not skip its own vertex); the pass winding is the same
            // for both edges.
            Vector256<float> behind = Vector256<float>.Zero;
            Vector256<float> ahead = Vector256<float>.Zero;
            for (int k = 0; k < pass.Length; k++)
            {
                Vec3 q = pass[k];
                Vector256<float> d =
                    (Vector256.Create(q.X) * fx)
                    + (Vector256.Create(q.Y) * fy)
                    + (Vector256.Create(q.Z) * fz)
                    - fd;
                behind |= Vector256.LessThan(d, below);
                ahead |= Vector256.GreaterThan(d, above);
            }

            uint bits = Vector256.AndNot(live & ahead, behind).ExtractMostSignificantBits();
            if (bits == 0)
            {
                continue;
            }

            ref float lane = ref MemoryMarshal.GetReference(lanes);
            fx.StoreUnsafe(ref lane, 0);
            fy.StoreUnsafe(ref lane, 8);
            fz.StoreUnsafe(ref lane, 16);
            fd.StoreUnsafe(ref lane, 24);

            // Lanes 0-3 are edge A's, in ascending pass vertex: written now.
            uint bitsA = bits & 0xF;
            while (bitsA != 0)
            {
                int j = BitOperations.TrailingZeroCount(bitsA);
                bitsA &= bitsA - 1;
                normals[found] = new Vec3(lanes[j], lanes[8 + j], lanes[16 + j]);
                distances[found] = lanes[24 + j];
                found++;
            }

            // Lanes 4-7 are edge B's: written after A's last chunk.
            uint bitsB = bits >> 4;
            while (bitsB != 0)
            {
                int j = 4 + BitOperations.TrailingZeroCount(bitsB);
                bitsB &= bitsB - 1;
                if (single)
                {
                    normals[found] = new Vec3(lanes[j], lanes[8 + j], lanes[16 + j]);
                    distances[found] = lanes[24 + j];
                    found++;
                }
                else
                {
                    heldNormals[held] = new Vec3(lanes[j], lanes[8 + j], lanes[16 + j]);
                    heldDistances[held] = lanes[24 + j];
                    held++;
                }
            }
        }

        if (held > 0)
        {
            heldNormals[..held].CopyTo(normals[found..]);
            heldDistances[..held].CopyTo(distances[found..]);
            found += held;
        }

        return found;
    }

    /// <summary>
    /// <c>(float)(1.0 / Math.Sqrt(length))</c> in each lane.
    /// </summary>
    /// <param name="length">Four squared lengths.</param>
    /// <param name="wide">
    /// True to do all four lanes in one 256-bit register, false for two
    /// 128-bit halves. The caller passes whether the CPU has 256-bit vectors;
    /// the facts pass both.
    /// </param>
    /// <returns>Four reciprocal lengths, narrowed to single precision.</returns>
    /// <remarks>
    /// Widened exactly, square-rooted and divided in double, narrowed with
    /// round-to-nearest: the scalar code's arithmetic, and correctly rounded
    /// at every step on every instruction set, so both shapes give the same
    /// bits. The wide one is fewer instructions on the derivation's critical
    /// path where the CPU has it; the narrow one is what arm64 runs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<float> ReciprocalSqrt(Vector128<float> length, bool wide)
    {
        if (wide)
        {
            Vector256<double> both = Vector256.Create(Vector128.WidenLower(length), Vector128.WidenUpper(length));
            both = Vector256.Create(1.0) / Vector256.Sqrt(both);
            return Vector128.Narrow(both.GetLower(), both.GetUpper());
        }

        Vector128<double> one = Vector128.Create(1.0);
        return Vector128.Narrow(
            one / Vector128.Sqrt(Vector128.WidenLower(length)),
            one / Vector128.Sqrt(Vector128.WidenUpper(length)));
    }

    /// <summary>
    /// One chop of a transposed winding: <see cref="Classify"/>, and when the
    /// plane actually cuts, <see cref="VisClip.Split"/> on the distances it
    /// computed.
    /// </summary>
    private static VisChopResult Chop(
        ReadOnlySpan<Vec3> input,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        ReadOnlySpan<float> zs,
        int padded,
        Vec3 normal,
        float distance,
        Span<float> dists,
        Span<int> sides,
        Span<Vec3> output,
        out int outputCount)
    {
        int flags = Classify(xs, ys, zs, padded, normal, distance, dists);

        if ((flags & 2) == 0)
        {
            outputCount = input.Length;
            return VisChopResult.Unchanged;
        }

        if ((flags & 1) == 0)
        {
            outputCount = 0;
            return VisChopResult.Empty;
        }

        return Cut(input, normal.X, normal.Y, normal.Z, distance, dists, sides, output, out outputCount);
    }

    /// <summary>
    /// The chop of a winding the classifier found on both sides of the
    /// plane: the sides from the distances, then <see cref="VisClip.Split"/>.
    /// </summary>
    /// <remarks>
    /// Kept out of line on purpose, so the unchanged and empty outcomes --
    /// four chops in five -- never set up its arguments. The normal comes
    /// in as three floats rather than a <see cref="Vec3"/>: the calling
    /// convention packs a Vec3 argument into registers by storing its fields
    /// and reloading two of them as one wider load, a store-forwarding stall
    /// on every call. Internal rather than private so the
    /// <see cref="VisSeparatorPath.Vector512"/> clip
    /// (<see cref="VisClipLanes512"/>) cuts with this same code.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static VisChopResult Cut(
        ReadOnlySpan<Vec3> input,
        float normalX,
        float normalY,
        float normalZ,
        float distance,
        Span<float> dists,
        Span<int> sides,
        Span<Vec3> output,
        out int outputCount)
    {
        int n = input.Length;
        for (int k = 0; k < n; k++)
        {
            sides[k] = VisClip.Side(dists[k]);
        }

        Vec3 normal = new(normalX, normalY, normalZ);
        return VisClip.Split(input, in normal, distance, dists, sides, output, out outputCount);
    }
}
