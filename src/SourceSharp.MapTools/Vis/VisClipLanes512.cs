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
/// One frame's separating planes for one ordering, as four columns, for the
/// <see cref="VisSeparatorPath.Vector512"/> separator path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VisSeparatorMemo"/>'s counterpart. That one keeps a
/// <see cref="Vec3"/> and a distance per plane and is filled a source edge
/// or two at a time as a clip reaches the end of it; this one keeps every
/// normal X, then every Y, every Z and every distance, and
/// <see cref="VisClipLanes512.ClipToSeparators"/> fills it to the end at
/// the first clip that needs any plane of it. The planes, their order and
/// their bits are the same in both; the facts compare the two lists plane
/// for plane.
/// </para>
/// <para>
/// Columns, because the clip tests a target against up to eight consecutive
/// planes at once, one plane per lane (<see cref="VisClipLanes512.FirstBehind8"/>),
/// and with columns each component of those eight planes is one load.
/// </para>
/// </remarks>
internal struct VisSeparatorColumns
{
    /// <summary>
    /// The planes, as four columns of <see cref="Stride"/> floats each: every
    /// plane's normal X, then every Y, every Z, and every distance.
    /// </summary>
    /// <remarks>
    /// Columns rather than a <see cref="Vec3"/> per plane because the clip
    /// tests a target against up to eight consecutive planes at once, one
    /// plane per lane (see <see cref="VisClipLanes512.FirstBehind8"/>), and with
    /// columns each component of those eight planes is one load.
    /// </remarks>
    internal float[] Planes;

    /// <summary>
    /// The length of each column: room for every plane the pairing can
    /// produce, and then a whole batch more, so a batch that starts at the
    /// last plane can load eight lanes without running into the next column.
    /// </summary>
    internal int Stride;

    /// <summary>How many planes are derived so far.</summary>
    internal int Count;

    /// <summary>
    /// The next source edge to derive from; the source winding's length once
    /// every plane is in the list.
    /// </summary>
    internal int NextEdge;

    /// <summary>
    /// How many times <see cref="VisClipLanes512.DeriveEdgeRun"/> has added
    /// to this list: the derivation's work count, which the facts pin.
    /// </summary>
    internal int Derivations;

    /// <summary>Starts an empty list over the given storage.</summary>
    /// <param name="planes">At least <c>4 * <paramref name="stride"/></c> floats.</param>
    /// <param name="stride">
    /// <see cref="StrideFor"/> of <c>source.Length * pass.Length</c>, which is
    /// as many planes as a derivation can produce.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="stride"/> is not a whole number of batches, or
    /// <paramref name="planes"/> is shorter than four columns.
    /// </exception>
    internal VisSeparatorColumns(float[] planes, int stride)
    {
        if (stride < BatchPadding || stride % BatchPadding != 0 || planes.Length < 4L * stride)
        {
            throw new ArgumentException(
                $"four columns of {stride} need a stride of whole batches and {4L * stride} floats, got {planes.Length}",
                nameof(planes));
        }

        Planes = planes;
        Stride = stride;
        Count = 0;
        NextEdge = 0;
        Derivations = 0;
    }

    /// <summary>
    /// How far past the last plane a column must reach: one whole batch, the
    /// widest the clip loads at once.
    /// </summary>
    internal const int BatchPadding = 8;

    /// <summary>
    /// The column length for a list of up to <paramref name="room"/> planes:
    /// at least <c>room + 8</c>, rounded up to a whole number of batches.
    /// </summary>
    /// <param name="room">The most planes the list can hold.</param>
    /// <returns>The stride.</returns>
    internal static int StrideFor(int room) => (room + (2 * BatchPadding) - 1) & ~(BatchPadding - 1);

    /// <summary>Plane <paramref name="index"/>'s normal, read back out of the columns.</summary>
    /// <param name="index">A plane below <see cref="Count"/>.</param>
    /// <returns>The normal.</returns>
    internal readonly Vec3 Normal(int index) =>
        new(Planes[index], Planes[Stride + index], Planes[(2 * Stride) + index]);

    /// <summary>Plane <paramref name="index"/>'s distance.</summary>
    /// <param name="index">A plane below <see cref="Count"/>.</param>
    /// <returns>The distance.</returns>
    internal readonly float Distance(int index) => Planes[(3 * Stride) + index];
}
/// <summary>
/// The <see cref="VisSeparatorPath.Vector512"/> separator clip: the planes
/// in columns, the target tested against eight of them at once, and a
/// frame's whole list derived four source edges at a time in 512-bit
/// registers. The same planes, the same chops and the same bits as
/// <see cref="VisClipLanes"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second path rather than a replacement.</b> This path is faster
/// on a CPU that runs 512-bit double-precision square root and divide at
/// full width (Zen 5: 4.6 % less wall time on 2fort at 16 threads) and slower
/// on one that does not (an Ice Lake-class Xeon: about 10 % more). The flow
/// picks one per compile (see <see cref="VisSeparatorPath"/> and
/// <see cref="VisSeparatorPaths.Resolve"/>); both are kept exact, and the
/// facts hold them to each other and to the scalar <see cref="VisClip"/>.
/// </para>
/// <para>
/// <b>Why the bits cannot move.</b> Exactly as for <see cref="VisClipLanes"/>,
/// whose remarks give the argument in full: every lane performs the scalar
/// code's operations in its order at its widths -- single precision up to
/// the squared length, the reciprocal square root widened to double and
/// narrowed back, no estimate, no horizontal add, no fused multiply-add --
/// and compares against <see cref="VisClipLanes.EpsilonSingle"/>, which is
/// exact for the three comparisons used. A lane of a 512-bit register rounds
/// as a lane of a 128-bit one does, on hardware and in the runtime's software
/// fallback alike.
/// </para>
/// <para>
/// <b>What is different, and why none of it shows in the answer.</b> Three
/// things. The list is derived in full at the first clip that needs it
/// rather than an edge at a time; a clip reads the planes from the front and
/// stops where it stops, so planes derived early are the planes the lazy path
/// would have derived when the walk reached them, and planes never reached
/// are never read. The target is tested against a batch of consecutive
/// planes, skipping to the first with a point behind it; every plane before
/// that one would have left the target unchanged, so the one-at-a-time walk
/// passes over them with the same winding in hand (see
/// <see cref="ClipToSeparators"/>). And four edges are derived in the four
/// quarters of one register, which never mix (see <see cref="DeriveEdgeRun"/>).
/// </para>
/// </remarks>
[SkipLocalsInit]
internal static class VisClipLanes512
{
    /// <summary>
    /// <see cref="VisClip.ChopWinding"/> through <see cref="FirstBehind8"/> or
    /// <see cref="FirstBehind4"/>, as a batch of one plane.
    /// </summary>
    /// <param name="input">The winding to clip; at most <see cref="VisClip.MaxPointsOnWinding"/> points.</param>
    /// <param name="normal">The clipping plane's normal.</param>
    /// <param name="distance">The clipping plane's distance.</param>
    /// <param name="wide">True for the eight-lane batch, false for the four-lane one.</param>
    /// <param name="output">Where a clipped result goes; must not overlap <paramref name="input"/>.</param>
    /// <param name="outputCount">How many points the result has.</param>
    /// <returns>Which of the three outcomes happened.</returns>
    /// <exception cref="ArgumentException">A winding or buffer is out of range.</exception>
    /// <remarks>
    /// The flow does not call this: its separator clip hands the batch as many
    /// planes as the memo holds. It exists so a fact can hold the batch
    /// classifier and the cut to the scalar chop on arbitrary windings, one
    /// call against one call -- every winding length to the cap, points
    /// exactly on each epsilon, NaN -- with the plane in the batch's first
    /// lane and the other lanes ignored, which is the same code path the
    /// flow's batches take for their first plane.
    /// </remarks>
    internal static VisChopResult ChopWinding(
        ReadOnlySpan<Vec3> input,
        Vec3 normal,
        float distance,
        bool wide,
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

        const int stride = VisSeparatorColumns.BatchPadding;
        Span<float> planes = stackalloc float[4 * stride];
        planes.Clear();
        planes[0] = normal.X;
        planes[stride] = normal.Y;
        planes[2 * stride] = normal.Z;
        planes[3 * stride] = distance;

        Span<float> lanes = stackalloc float[VisClip.MaxPointsOnWinding * BatchLanes];
        Span<float> dists = stackalloc float[VisClip.MaxPointsOnWinding + 1];
        Span<int> sides = stackalloc int[VisClip.MaxPointsOnWinding + 1];

        bool front;
        int hit = wide
            ? FirstBehind8(input, planes, stride, 0, 1, flip: false, lanes, out front)
            : FirstBehind4(input, planes, stride, 0, 1, flip: false, lanes, out front);

        if (hit < 0)
        {
            outputCount = input.Length;
            return VisChopResult.Unchanged;
        }

        if (!front)
        {
            outputCount = 0;
            return VisChopResult.Empty;
        }

        for (int k = 0; k < input.Length; k++)
        {
            dists[k] = lanes[(k * BatchLanes) + hit];
        }

        return VisClipLanes.Cut(input, normal.X, normal.Y, normal.Z, distance, dists, sides, output, out outputCount);
    }
    /// <summary>
    /// Clips a winding by one frame's separating planes, deriving the whole
    /// list first if nothing has derived it yet.
    /// </summary>
    /// <param name="memo">The frame's list for this ordering; filled in place on first use.</param>
    /// <param name="source">The near portal of the ordering the list is derived from.</param>
    /// <param name="pass">The middle portal of that ordering.</param>
    /// <param name="target">The far portal, the one being clipped.</param>
    /// <param name="flipClip">True for the reversed ordering's clip, stock's second call.</param>
    /// <param name="wideBatch">
    /// True to test the target against eight planes at a time
    /// (<see cref="FirstBehind8"/>), false for four
    /// (<see cref="FirstBehind4"/>). The flow passes whether the CPU has
    /// 256-bit vectors; the facts pass both.
    /// </param>
    /// <param name="result">
    /// Where the surviving winding is written; at least
    /// <see cref="VisClip.MaxPointsOnWinding"/> points. Overlapping
    /// <paramref name="target"/> is allowed.
    /// </param>
    /// <param name="resultCount">How many points survived.</param>
    /// <returns>False when the target was clipped away entirely.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> is too small, or <paramref name="target"/>
    /// has more points than a vis winding may.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The same function as <see cref="VisClip.ClipToSeparatorPlanes"/> over
    /// the full <see cref="VisClip.BuildSeparators"/> list, and as
    /// <see cref="VisClipLanes.ClipToSeparators(ref VisSeparatorMemo, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, ReadOnlySpan{Vec3}, bool, Span{Vec3}, out int)"/>:
    /// same planes, same order, same chops, same bits.
    /// </para>
    /// <para>
    /// <b>Why the whole list at once.</b> On 2fort the forward list of a frame
    /// is derived to its end in 87 % of the frames that derive it at all, and
    /// the reverse list in 99 % of those that use it, so deriving lazily saves
    /// about one edge in twenty -- and pays for it with a derivation call, its
    /// setup and one square-root chain's latency per edge or pair of edges.
    /// Real portals are quads, and one <see cref="DeriveEdgeRun"/> covers a
    /// quad's whole list with one chain. A frame whose candidates all die in
    /// the forward clip still never derives the reverse list: that list is
    /// derived by the first reverse clip, and there is none.
    /// </para>
    /// <para>
    /// <b>Why a batch of planes, and why it is the same clip.</b> On 2fort the
    /// separator clips apply 2.85 billion planes, and four in five of them
    /// find the whole target in front and change nothing. Applied one at a
    /// time, each of those costs a plane load, a lane classification of the
    /// target, and the loop's own bookkeeping -- around forty cycles to learn
    /// nothing. So the clip asks a different question of a batch of up to
    /// eight consecutive planes, one plane per lane: which is the FIRST of
    /// them with some point of the target behind it? Every plane before that
    /// one leaves the target unchanged (nothing behind is exactly the
    /// <see cref="VisChopResult.Unchanged"/> outcome), so the one-at-a-time
    /// walk would have passed over them with the same winding in hand, and
    /// the batch may skip them. The first plane with a point behind is then
    /// applied exactly as before -- empty when no point is in front of it, a
    /// cut otherwise -- and after a cut the next batch starts at the plane
    /// after it, against the new winding. Planes after the first one with a
    /// point behind are never judged on the old winding: their lanes were
    /// computed, and are thrown away.
    /// </para>
    /// <para>
    /// Each lane computes what the scalar chop computes for its plane:
    /// <c>((x*nx + y*ny) + z*nz) - d</c> in single precision, in that order,
    /// with the flip applied to the plane first as <c>0 - n</c> and a negated
    /// distance, and the epsilon compared in single precision. The distances
    /// of the plane that cuts are those same lanes, so the cut interpolates
    /// with the scalar chop's bits.
    /// </para>
    /// </remarks>
    internal static bool ClipToSeparators(
        ref VisSeparatorColumns memo,
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        ReadOnlySpan<Vec3> target,
        bool flipClip,
        bool wideBatch,
        Span<Vec3> result,
        out int resultCount)
    {
        if (result.Length < VisClip.MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a clip needs {VisClip.MaxPointsOnWinding} points of room, got {result.Length}",
                nameof(result));
        }

        if (target.Length > VisClip.MaxPointsOnWinding)
        {
            throw new ArgumentException(
                $"a vis winding holds at most {VisClip.MaxPointsOnWinding} points, got {target.Length}",
                nameof(target));
        }

        // The whole list, four edges per derivation, before the first plane
        // is read; a later clip of the same frame finds it complete.
        while (memo.NextEdge < source.Length)
        {
            int run = Math.Min(4, source.Length - memo.NextEdge);
            memo.Count = DeriveEdgeRun(source, pass, memo.NextEdge, run, memo.Planes, memo.Stride, memo.Count);
            memo.NextEdge += run;
            memo.Derivations++;
        }

        Span<Vec3> ping = stackalloc Vec3[VisClip.MaxPointsOnFixedWinding];
        Span<Vec3> pong = stackalloc Vec3[VisClip.MaxPointsOnFixedWinding];
        Span<float> lanes = stackalloc float[VisClip.MaxPointsOnWinding * BatchLanes];
        Span<float> dists = stackalloc float[VisClip.MaxPointsOnWinding + 1];
        Span<int> sides = stackalloc int[VisClip.MaxPointsOnWinding + 1];
        bool usePing = true;
        int width = wideBatch ? 8 : 4;

        scoped ReadOnlySpan<Vec3> current = target;
        int p = 0;
        while (p < memo.Count)
        {
            int available = memo.Count - p;
            bool front;
            int hit = wideBatch
                ? FirstBehind8(current, memo.Planes, memo.Stride, p, available, flipClip, lanes, out front)
                : FirstBehind4(current, memo.Planes, memo.Stride, p, available, flipClip, lanes, out front);

            if (hit < 0)
            {
                // Nothing behind any plane of the batch: all unchanged.
                p += Math.Min(available, width);
                continue;
            }

            p += hit;
            if (!front)
            {
                resultCount = 0;
                return false;
            }

            int n = current.Length;
            for (int k = 0; k < n; k++)
            {
                dists[k] = lanes[(k * BatchLanes) + hit];
            }

            Vec3 planeNormal = memo.Normal(p);
            float planeDist = memo.Distance(p);
            if (flipClip)
            {
                planeNormal = VisClip.Negate(planeNormal);
                planeDist = -planeDist;
            }

            Span<Vec3> destination = usePing ? ping : pong;
            if (VisClipLanes.Cut(current, planeNormal.X, planeNormal.Y, planeNormal.Z, planeDist, dists, sides, destination, out int count)
                == VisChopResult.Clipped)
            {
                current = destination[..count];
                usePing = !usePing;
            }

            p++;
        }

        current.CopyTo(result);
        resultCount = current.Length;
        return true;
    }

    /// <summary>
    /// Every separating plane <see cref="VisClip.BuildSeparators"/> would
    /// derive, through <see cref="DeriveEdgeRun"/>, four edges at a time:
    /// what <see cref="ClipToSeparators"/> puts in a list at first use.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="planes">
    /// Four columns of <paramref name="stride"/> floats, as
    /// <see cref="VisSeparatorColumns.Planes"/> holds them.
    /// </param>
    /// <param name="stride">
    /// The column length; at least <c>source.Length * pass.Length</c>.
    /// </param>
    /// <returns>How many planes there are.</returns>
    internal static int BuildSeparators(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        Span<float> planes,
        int stride)
    {
        int found = 0;
        for (int i = 0; i < source.Length; i += 4)
        {
            found = DeriveEdgeRun(source, pass, i, Math.Min(4, source.Length - i), planes, stride, found);
        }

        return found;
    }

    /// <summary>
    /// How many floats <see cref="FirstBehind8"/> and
    /// <see cref="FirstBehind4"/> write per target point: one per lane of the
    /// wider batch.
    /// </summary>
    internal const int BatchLanes = 8;

    /// <summary>
    /// Which of up to eight consecutive planes is the first to have some point
    /// of <paramref name="points"/> behind it, and whether any point is in
    /// front of that one.
    /// </summary>
    /// <param name="points">The winding being clipped.</param>
    /// <param name="planes">The memo's columns.</param>
    /// <param name="stride">The memo's column length.</param>
    /// <param name="p">The first plane of the batch.</param>
    /// <param name="available">
    /// How many planes from <paramref name="p"/> are in the list; lanes past
    /// it are computed on whatever the column holds and ignored.
    /// </param>
    /// <param name="flip">True to test against each plane reversed, <c>flipClip</c>.</param>
    /// <param name="lanes">
    /// Where each point's eight distances go, point-major:
    /// <c>lanes[k * 8 + j]</c> is point <c>k</c> against plane <c>p + j</c>.
    /// </param>
    /// <param name="front">Whether some point is in front of the plane returned.</param>
    /// <returns>
    /// The lane of the first plane with a point behind it, or -1 when no plane
    /// of the batch has one.
    /// </returns>
    /// <exception cref="ArgumentException">A buffer is out of range.</exception>
    /// <remarks>
    /// See <see cref="ClipToSeparators"/>
    /// for why the batch gives the one-at-a-time answer and each lane the
    /// scalar chop's bits.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FirstBehind8(
        ReadOnlySpan<Vec3> points,
        ReadOnlySpan<float> planes,
        int stride,
        int p,
        int available,
        bool flip,
        Span<float> lanes,
        out bool front)
    {
        CheckBatch(points, planes, stride, p, available, lanes, 8);

        ref float column = ref MemoryMarshal.GetReference(planes);
        Vector256<float> nx = Vector256.LoadUnsafe(ref column, (nuint)p);
        Vector256<float> ny = Vector256.LoadUnsafe(ref column, (nuint)(stride + p));
        Vector256<float> nz = Vector256.LoadUnsafe(ref column, (nuint)((2 * stride) + p));
        Vector256<float> d = Vector256.LoadUnsafe(ref column, (nuint)((3 * stride) + p));
        if (flip)
        {
            // VisClip.Negate's 0 - n, and the distance's unary minus -- a
            // sign flip, which is what XOR with -0 is, including on a zero.
            nx = Vector256<float>.Zero - nx;
            ny = Vector256<float>.Zero - ny;
            nz = Vector256<float>.Zero - nz;
            d ^= Vector256.Create(-0.0f);
        }

        Vector256<float> above = Vector256.Create(VisClipLanes.EpsilonSingle);
        Vector256<float> below = Vector256.Create(-VisClipLanes.EpsilonSingle);
        Vector256<float> behind = Vector256<float>.Zero;
        Vector256<float> ahead = Vector256<float>.Zero;
        ref float o = ref MemoryMarshal.GetReference(lanes);
        for (int k = 0; k < points.Length; k++)
        {
            Vec3 q = points[k];
            Vector256<float> dot =
                (Vector256.Create(q.X) * nx)
                + (Vector256.Create(q.Y) * ny)
                + (Vector256.Create(q.Z) * nz);
            dot -= d;
            dot.StoreUnsafe(ref o, (nuint)(k * BatchLanes));
            behind |= Vector256.LessThan(dot, below);
            ahead |= Vector256.GreaterThan(dot, above);
        }

        uint mask = behind.ExtractMostSignificantBits() & ((1u << Math.Min(available, 8)) - 1);
        if (mask == 0)
        {
            front = false;
            return -1;
        }

        int hit = BitOperations.TrailingZeroCount(mask);
        front = ((ahead.ExtractMostSignificantBits() >> hit) & 1) != 0;
        return hit;
    }

    /// <summary>
    /// <see cref="FirstBehind8"/> four planes at a time, for a CPU without
    /// 256-bit vectors.
    /// </summary>
    /// <param name="points">The winding being clipped.</param>
    /// <param name="planes">The memo's columns.</param>
    /// <param name="stride">The memo's column length.</param>
    /// <param name="p">The first plane of the batch.</param>
    /// <param name="available">How many planes from <paramref name="p"/> are in the list.</param>
    /// <param name="flip">True to test against each plane reversed.</param>
    /// <param name="lanes">
    /// Where each point's distances go, at the same eight-float stride as
    /// <see cref="FirstBehind8"/>; the upper four of each are not written.
    /// </param>
    /// <param name="front">Whether some point is in front of the plane returned.</param>
    /// <returns>The lane of the first plane with a point behind it, or -1.</returns>
    /// <exception cref="ArgumentException">A buffer is out of range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FirstBehind4(
        ReadOnlySpan<Vec3> points,
        ReadOnlySpan<float> planes,
        int stride,
        int p,
        int available,
        bool flip,
        Span<float> lanes,
        out bool front)
    {
        CheckBatch(points, planes, stride, p, available, lanes, 4);

        ref float column = ref MemoryMarshal.GetReference(planes);
        Vector128<float> nx = Vector128.LoadUnsafe(ref column, (nuint)p);
        Vector128<float> ny = Vector128.LoadUnsafe(ref column, (nuint)(stride + p));
        Vector128<float> nz = Vector128.LoadUnsafe(ref column, (nuint)((2 * stride) + p));
        Vector128<float> d = Vector128.LoadUnsafe(ref column, (nuint)((3 * stride) + p));
        if (flip)
        {
            nx = Vector128<float>.Zero - nx;
            ny = Vector128<float>.Zero - ny;
            nz = Vector128<float>.Zero - nz;
            d ^= Vector128.Create(-0.0f);
        }

        Vector128<float> above = Vector128.Create(VisClipLanes.EpsilonSingle);
        Vector128<float> below = Vector128.Create(-VisClipLanes.EpsilonSingle);
        Vector128<float> behind = Vector128<float>.Zero;
        Vector128<float> ahead = Vector128<float>.Zero;
        ref float o = ref MemoryMarshal.GetReference(lanes);
        for (int k = 0; k < points.Length; k++)
        {
            Vec3 q = points[k];
            Vector128<float> dot =
                (Vector128.Create(q.X) * nx)
                + (Vector128.Create(q.Y) * ny)
                + (Vector128.Create(q.Z) * nz);
            dot -= d;
            dot.StoreUnsafe(ref o, (nuint)(k * BatchLanes));
            behind |= Vector128.LessThan(dot, below);
            ahead |= Vector128.GreaterThan(dot, above);
        }

        uint mask = behind.ExtractMostSignificantBits() & ((1u << Math.Min(available, 4)) - 1);
        if (mask == 0)
        {
            front = false;
            return -1;
        }

        int hit = BitOperations.TrailingZeroCount(mask);
        front = ((ahead.ExtractMostSignificantBits() >> hit) & 1) != 0;
        return hit;
    }

    /// <summary>
    /// The bounds a batch needs, checked once so the lane loads can go
    /// unchecked: the batch's loads stay inside each column, and every point
    /// has a row of lanes.
    /// </summary>
    private static void CheckBatch(
        ReadOnlySpan<Vec3> points, ReadOnlySpan<float> planes, int stride, int p, int available, Span<float> lanes, int width)
    {
        if (p < 0 || available <= 0 || p + width > stride || planes.Length < 4L * stride
            || lanes.Length < (long)points.Length * BatchLanes)
        {
            throw new ArgumentException("a batch of planes is out of range of its columns or its lanes", nameof(p));
        }
    }

    /// <summary>
    /// Checks that a plane list's four columns fit their array and have room
    /// for <paramref name="adding"/> more planes. The derivations then cut
    /// each column to <paramref name="stride"/>, so a write past one column's
    /// end is caught rather than landing in the next column.
    /// </summary>
    /// <param name="planes">The columns, end to end.</param>
    /// <param name="stride">The column length.</param>
    /// <param name="found">How many planes are already in the list.</param>
    /// <param name="adding">The most planes the caller can add.</param>
    /// <exception cref="ArgumentException">
    /// The columns do not fit <paramref name="planes"/>, or have no room for
    /// <paramref name="adding"/> more planes.
    /// </exception>
    private static void CheckColumns(ReadOnlySpan<float> planes, int stride, int found, int adding)
    {
        if (stride < 0 || found < 0 || planes.Length < 4L * stride || (long)found + adding > stride)
        {
            throw new ArgumentException(
                $"columns of {stride} cannot take {adding} planes after {found}", nameof(planes));
        }
    }

    /// <summary>
    /// <see cref="VisClipLanes.DeriveEdge"/> for up to four consecutive source edges at
    /// once, one per 128-bit quarter of a 512-bit register: the planes of edge
    /// <paramref name="i"/>, then of edge <c>i + 1</c>, and so on, exactly as
    /// that many <see cref="VisClipLanes.DeriveEdge"/> calls in that order would write them.
    /// </summary>
    /// <param name="source">The near portal of the ordering.</param>
    /// <param name="pass">The middle portal of the ordering.</param>
    /// <param name="i">The first edge's first vertex.</param>
    /// <param name="edges">
    /// How many edges, one to four; <c>i + edges</c> must not pass the
    /// source's vertex count (the last edge may wrap to vertex 0).
    /// </param>
    /// <param name="planes">
    /// The list's four columns of <paramref name="stride"/> floats, as
    /// <see cref="VisSeparatorColumns.Planes"/> holds them.
    /// </param>
    /// <param name="stride">The column length.</param>
    /// <param name="found">How many planes are already in the list.</param>
    /// <returns>How many planes are in the list afterwards.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="edges"/> is not one to four, or the run of edges goes
    /// past the source's last vertex.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The columns have no room for every plane the edges could add.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why four edges.</b> On 2fort the forward list of a frame is derived
    /// to its end in 87 % of the frames that derive it at all, and the reverse
    /// list in 99 % of those that use it, so deriving a frame's list lazily
    /// an edge pair at a time saves only about one edge in twenty -- and pays
    /// for it with a derivation call, its setup, and one square-root chain's
    /// latency for every two edges. Real portals are quads: one call here
    /// covers a quad's whole list with one chain, which is why this path
    /// derives the list in full at the first clip that needs it (see
    /// <see cref="ClipToSeparators"/>).
    /// </para>
    /// <para>
    /// <b>Why every lane still computes what the scalar code does.</b> As in
    /// <see cref="VisClipLanes.DeriveEdgePair"/>: the quarters never mix, each lane is one
    /// (edge, pass vertex) pair running <see cref="VisClipLanes.DeriveEdge"/>'s arithmetic
    /// in its order and at its widths, the source-side scan gives each
    /// quarter a mask that leaves out that edge's own two endpoints, and the
    /// scan ends only when every live lane of every quarter has decided,
    /// which cannot change a lane's answer because a lane ignores every point
    /// after the one that decided it. A quarter past <paramref name="edges"/>
    /// is dead from the start and never written.
    /// </para>
    /// <para>
    /// <b>The order the planes come out in.</b> Edge-major: when the pass
    /// winding has more than four points each chunk of four pass vertices
    /// yields planes of every edge, so the planes of the second and later
    /// edges are held back, per edge, and written after all of the first
    /// edge's, then the second's, and so on.
    /// </para>
    /// <para>
    /// The flow takes this path only where
    /// <see cref="VisSeparatorPaths.Resolve"/> chose it, or a host forced it.
    /// Where the CPU has no 512-bit vectors the runtime runs
    /// <see cref="Vector512{T}"/> as narrower operations in software: slower,
    /// and exact all the same, so the facts run it on every machine.
    /// </para>
    /// </remarks>
    internal static int DeriveEdgeRun(
        ReadOnlySpan<Vec3> source,
        ReadOnlySpan<Vec3> pass,
        int i,
        int edges,
        Span<float> planes,
        int stride,
        int found)
    {
        int n = source.Length;
        if (edges < 1 || edges > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(edges), edges, "a run is one to four edges");
        }

        if (i < 0 || i + edges > n)
        {
            throw new ArgumentOutOfRangeException(nameof(i), i, "the run of edges goes past the source's last vertex");
        }

        CheckColumns(planes, stride, found, edges * pass.Length);
        Span<float> xs = planes[..stride];
        Span<float> ys = planes.Slice(stride, stride);
        Span<float> zs = planes.Slice(2 * stride, stride);
        Span<float> ds = planes.Slice(3 * stride, stride);

        // Quarter q is edge (e_q, e_q + 1 wrapped), e_q = i + q; a quarter
        // past the run repeats the last edge and is dead.
        Span<int> ends = stackalloc int[2 * 16];
        Span<float> setup = stackalloc float[6 * 16];
        for (int q = 0; q < 4; q++)
        {
            int e = i + Math.Min(q, edges - 1);
            int l = e + 1 == n ? 0 : e + 1;
            Vec3 origin = source[e];
            Vec3 v1 = source[l] - origin;
            for (int lane = 0; lane < 4; lane++)
            {
                int at = (q * 4) + lane;
                setup[at] = v1.X;
                setup[16 + at] = v1.Y;
                setup[32 + at] = v1.Z;
                setup[48 + at] = origin.X;
                setup[64 + at] = origin.Y;
                setup[80 + at] = origin.Z;
                ends[at] = e;
                ends[16 + at] = l;
            }
        }

        ref float s0 = ref MemoryMarshal.GetReference(setup);
        Vector512<float> v1x = Vector512.LoadUnsafe(ref s0, 0);
        Vector512<float> v1y = Vector512.LoadUnsafe(ref s0, 16);
        Vector512<float> v1z = Vector512.LoadUnsafe(ref s0, 32);
        Vector512<float> ox = Vector512.LoadUnsafe(ref s0, 48);
        Vector512<float> oy = Vector512.LoadUnsafe(ref s0, 64);
        Vector512<float> oz = Vector512.LoadUnsafe(ref s0, 80);
        ref int e0 = ref MemoryMarshal.GetReference(ends);
        Vector512<int> startLanes = Vector512.LoadUnsafe(ref e0, 0);
        Vector512<int> endLanes = Vector512.LoadUnsafe(ref e0, 16);
        Vector512<float> above = Vector512.Create(VisClipLanes.EpsilonSingle);
        Vector512<float> below = Vector512.Create(-VisClipLanes.EpsilonSingle);
        Vector512<float> signBit = Vector512.Create(-0.0f);
        Vector512<int> laneIndex = Vector512.Create(0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3);
        Vector512<int> quarterLive = Vector512.LessThan(
            Vector512.Create(0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3), Vector512.Create(edges));

        Span<float> lanes = stackalloc float[64];

        // The planes of edges after the first, held until every plane of the
        // edges before them has been written. Only a pass winding of more
        // than four points needs this; a shorter one has a single chunk,
        // whose lanes are already in edge-major order.
        Span<float> held = stackalloc float[3 * 4 * VisClip.MaxPointsOnWinding];
        Span<int> heldCount = stackalloc int[3];
        heldCount.Clear();
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
            Vector256<float> hx = Vector256.Create(qx, qx);
            Vector256<float> hy = Vector256.Create(qy, qy);
            Vector256<float> hz = Vector256.Create(qz, qz);
            Vector512<float> px = Vector512.Create(hx, hx);
            Vector512<float> py = Vector512.Create(hy, hy);
            Vector512<float> pz = Vector512.Create(hz, hz);

            // v2 = pass[j] - source[edge's origin]; n = cross(v1, v2).
            Vector512<float> v2x = px - ox;
            Vector512<float> v2y = py - oy;
            Vector512<float> v2z = pz - oz;
            Vector512<float> nx = (v1y * v2z) - (v1z * v2y);
            Vector512<float> ny = (v1z * v2x) - (v1x * v2z);
            Vector512<float> nz = (v1x * v2y) - (v1y * v2x);

            Vector512<float> length = (nx * nx) + (ny * ny) + (nz * nz);
            Vector512<float> live = Vector512.AndNot(
                (Vector512.LessThan(laneIndex, Vector512.Create(width)) & quarterLive).AsSingle(),
                Vector512.LessThanOrEqual(length, above));
            if (live == Vector512<float>.Zero)
            {
                continue;
            }

            // Two independent square-root-and-divide chains of eight lanes.
            Vector512<double> one = Vector512.Create(1.0);
            Vector512<float> scale = Vector512.Narrow(
                one / Vector512.Sqrt(Vector512.WidenLower(length)),
                one / Vector512.Sqrt(Vector512.WidenUpper(length)));
            nx *= scale;
            ny *= scale;
            nz *= scale;
            Vector512<float> dist = (px * nx) + (py * ny) + (pz * nz);

            // The source-side scan, each quarter skipping its own edge's two
            // endpoints.
            Vector512<float> decided = Vector512<float>.Zero;
            Vector512<float> flip = Vector512<float>.Zero;
            for (int k = 0; k < n; k++)
            {
                Vector512<int> at = Vector512.Create(k);
                Vector512<float> eligible = ~(Vector512.Equals(at, startLanes) | Vector512.Equals(at, endLanes)).AsSingle();

                Vec3 s = source[k];
                Vector512<float> d =
                    (Vector512.Create(s.X) * nx)
                    + (Vector512.Create(s.Y) * ny)
                    + (Vector512.Create(s.Z) * nz)
                    - dist;
                Vector512<float> isAbove = Vector512.GreaterThan(d, above);
                Vector512<float> fresh = Vector512.AndNot(
                    (isAbove | Vector512.LessThan(d, below)) & eligible, decided);
                flip |= isAbove & fresh;
                decided |= fresh;

                if (Vector512.AndNot(live, decided) == Vector512<float>.Zero)
                {
                    break;
                }
            }

            live &= decided;
            if (live == Vector512<float>.Zero)
            {
                continue;
            }

            Vector512<float> fx = Vector512.ConditionalSelect(flip, Vector512<float>.Zero - nx, nx);
            Vector512<float> fy = Vector512.ConditionalSelect(flip, Vector512<float>.Zero - ny, ny);
            Vector512<float> fz = Vector512.ConditionalSelect(flip, Vector512<float>.Zero - nz, nz);
            Vector512<float> fd = Vector512.ConditionalSelect(flip, dist ^ signBit, dist);

            // The pass-side test, as DeriveEdge's (whose remarks say why a
            // lane need not skip its own vertex).
            Vector512<float> behind = Vector512<float>.Zero;
            Vector512<float> ahead = Vector512<float>.Zero;
            for (int k = 0; k < pass.Length; k++)
            {
                Vec3 q = pass[k];
                Vector512<float> d =
                    (Vector512.Create(q.X) * fx)
                    + (Vector512.Create(q.Y) * fy)
                    + (Vector512.Create(q.Z) * fz)
                    - fd;
                behind |= Vector512.LessThan(d, below);
                ahead |= Vector512.GreaterThan(d, above);
            }

            ulong bits = Vector512.AndNot(live & ahead, behind).ExtractMostSignificantBits();
            if (bits == 0)
            {
                continue;
            }

            ref float lane = ref MemoryMarshal.GetReference(lanes);
            fx.StoreUnsafe(ref lane, 0);
            fy.StoreUnsafe(ref lane, 16);
            fz.StoreUnsafe(ref lane, 32);
            fd.StoreUnsafe(ref lane, 48);

            if (single)
            {
                // One chunk: lane order is edge-major already.
                do
                {
                    int j = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    xs[found] = lanes[j];
                    ys[found] = lanes[16 + j];
                    zs[found] = lanes[32 + j];
                    ds[found] = lanes[48 + j];
                    found++;
                }
                while (bits != 0);
                continue;
            }

            // The first edge's lanes are written now; each later edge's are
            // held in its own run.
            ulong first = bits & 0xF;
            while (first != 0)
            {
                int j = BitOperations.TrailingZeroCount(first);
                first &= first - 1;
                xs[found] = lanes[j];
                ys[found] = lanes[16 + j];
                zs[found] = lanes[32 + j];
                ds[found] = lanes[48 + j];
                found++;
            }

            ulong later = bits >> 4;
            while (later != 0)
            {
                int bit = BitOperations.TrailingZeroCount(later);
                later &= later - 1;
                int j = 4 + bit;
                int edge = bit >> 2;
                int at = (edge * 4 * VisClip.MaxPointsOnWinding) + heldCount[edge];
                held[at] = lanes[j];
                held[at + VisClip.MaxPointsOnWinding] = lanes[16 + j];
                held[at + (2 * VisClip.MaxPointsOnWinding)] = lanes[32 + j];
                held[at + (3 * VisClip.MaxPointsOnWinding)] = lanes[48 + j];
                heldCount[edge]++;
            }
        }

        for (int edge = 0; edge < 3; edge++)
        {
            int count = heldCount[edge];
            if (count == 0)
            {
                continue;
            }

            int at = edge * 4 * VisClip.MaxPointsOnWinding;
            held.Slice(at, count).CopyTo(xs[found..]);
            held.Slice(at + VisClip.MaxPointsOnWinding, count).CopyTo(ys[found..]);
            held.Slice(at + (2 * VisClip.MaxPointsOnWinding), count).CopyTo(zs[found..]);
            held.Slice(at + (3 * VisClip.MaxPointsOnWinding), count).CopyTo(ds[found..]);
            found += count;
        }

        return found;
    }
}
