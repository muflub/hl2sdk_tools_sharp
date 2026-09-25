using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The per-worker slab that stands in for stock's <c>pstack_t</c>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>pstack_t</c> is a local variable of
/// <c>RecursiveLeafFlow</c> and it is about 8.7 KB, because its
/// <c>mightsee</c> is sized for <c>MAX_PORTALS</c> (65,536 bits) whatever the
/// map actually has. Stock gets away with that on a 1 MB Windows thread stack
/// only because real maps never recurse more than about a hundred deep -- which
/// is itself the empirical bound, since a map that went deeper would have
/// crashed stock for the last twenty-five years. A <c>stackalloc</c> of the
/// same shape in .NET would have the same ceiling and no way to report hitting
/// it: a managed stack overflow is not an exception, it is the process.
/// </para>
/// <para>
/// So the frames live on the heap, one array per depth, allocated the first
/// time that depth is reached and kept for the life of the worker. Sized from
/// the map's REAL portal count rather than <c>MAX_PORTALS</c>, which on a
/// typical map is a fiftieth of stock's frame.
/// </para>
/// <para>
/// <b>One array per depth, never one array resized.</b> The flow passes each
/// frame's storage to its child as a span, and a span into an array that a
/// deeper frame then reallocated would be a span into the old copy. Growing by
/// appending to the list cannot do that.
/// </para>
/// </remarks>
internal sealed class VisFrameStack
{
    /// <summary>Where one frame's chopped <c>pass</c> winding starts.</summary>
    /// <remarks>
    /// Stock's frame holds three windings behind a free list,
    /// which is the same budget arrived at the other way round. Here each has a
    /// fixed role, so there is no allocator and no "already free" error to hit.
    /// </remarks>
    internal const int PassOffset = 0;

    /// <summary>Where one frame's chopped <c>source</c> winding starts.</summary>
    internal const int SourceOffset = VisClip.MaxPointsOnFixedWinding;

    /// <summary>Where one frame's separator-clip output starts.</summary>
    internal const int ClipOffset = 2 * VisClip.MaxPointsOnFixedWinding;

    /// <summary>
    /// How many points one frame's winding storage holds.
    /// </summary>
    /// <remarks>
    /// The first two are chop outputs, which stock caps at
    /// <see cref="VisClip.MaxPointsOnFixedWinding"/>. The third is the
    /// separator clip's output, and that one needs the FULL
    /// <see cref="VisClip.MaxPointsOnWinding"/>: when no separating plane
    /// clips anything, <c>ClipToSeperators</c> hands back the target winding
    /// untouched, and the target can be a portal's own winding of up to
    /// sixty-four points.
    /// </remarks>
    private const int PointsPerFrame = ClipOffset + VisClip.MaxPointsOnWinding;

    /// <summary>
    /// How many separating planes one frame caches for one ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A derivation can produce at most <c>source.Length * pass.Length</c>
    /// planes, and both windings can in principle be a whole portal of
    /// <see cref="VisClip.MaxPointsOnWinding"/> points -- 4,096 planes, 64 KB a
    /// frame, and the cache would cost more than it saved on the one map that
    /// had such a portal. So the cache takes the frames it fits and the flow
    /// derives the rest inline: 256 covers a sixteen-by-sixteen pairing, and
    /// real portals are quads.
    /// </para>
    /// <para>
    /// This is only the CAP. What is actually allocated is
    /// <c>source.Length * pass.Length</c> planes per depth per ordering, which
    /// for the quads real portals are made of is sixteen -- 320 bytes, not the
    /// 4 KB the cap would reserve. See <see cref="SeparatorNormals"/> for what
    /// reserving the cap cost on a small map.
    /// </para>
    /// </remarks>
    internal const int MaxCachedSeparators = 256;

    private readonly List<ulong[]> _mightSee = [];
    private readonly List<Vec3[]> _windings = [];
    private readonly List<Vec3[]> _separatorNormals = [];
    private readonly List<float[]> _separatorDistances = [];
    private readonly int _words;

    /// <summary>Creates a stack for one worker.</summary>
    /// <param name="portalCount">The map's memory-portal count.</param>
    internal VisFrameStack(int portalCount) => _words = BitVector.WordsFor(portalCount);

    /// <summary>One frame's <c>mightsee</c>.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <returns>A vector of the map's portal width.</returns>
    internal ulong[] MightSee(int depth)
    {
        while (_mightSee.Count <= depth)
        {
            _mightSee.Add(new ulong[_words]);
        }

        return _mightSee[depth];
    }

    /// <summary>One frame's three winding buffers, end to end.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <returns>
    /// Storage at <see cref="PassOffset"/>, <see cref="SourceOffset"/> and
    /// <see cref="ClipOffset"/>.
    /// </returns>
    internal Vec3[] Windings(int depth)
    {
        while (_windings.Count <= depth)
        {
            _windings.Add(new Vec3[PointsPerFrame]);
        }

        return _windings[depth];
    }

    /// <summary>
    /// One frame's separator-memo normals for one ordering.
    /// </summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <param name="ordering">
    /// Zero for the source-then-pass derivation, one for the reverse -- the two
 /// Calls stock makes.
    /// </param>
    /// <param name="minimum">
    /// How many planes this frame could possibly derive, which is
    /// <c>source.Length * pass.Length</c>.
    /// </param>
    /// <returns>At least <paramref name="minimum"/> normals.</returns>
    /// <remarks>
    /// <b>Sized to the frame, not to the cap.</b> Handing out
    /// <see cref="MaxCachedSeparators"/> every time allocates 4 KB per depth
    /// per ordering per worker whatever the windings are, and on a map small
    /// enough that the whole compile is 30 ms that is 1,216 arrays and 4.9 MB
    /// of garbage for nothing: it measured 50 % slower on
    /// <c>l1_open_arena</c>. Real portals are quads, so the frames that
    /// actually occur want sixteen planes. The array is kept and reused at that
    /// depth, and only grows when a frame there needs more.
    /// </remarks>
    internal Vec3[] SeparatorNormals(int depth, int ordering, int minimum)
    {
        int slot = (depth * 2) + ordering;
        while (_separatorNormals.Count <= slot)
        {
            _separatorNormals.Add([]);
        }

        Vec3[] held = _separatorNormals[slot];
        if (held.Length < minimum)
        {
            held = new Vec3[minimum];
            _separatorNormals[slot] = held;
        }

        return held;
    }

    /// <summary>One frame's separator-memo distances for one ordering.</summary>
    /// <param name="depth">The recursion depth, from one.</param>
    /// <param name="ordering">Zero or one, as for <see cref="SeparatorNormals"/>.</param>
    /// <param name="minimum">How many planes this frame could derive.</param>
    /// <returns>At least <paramref name="minimum"/> distances.</returns>
    internal float[] SeparatorDistances(int depth, int ordering, int minimum)
    {
        int slot = (depth * 2) + ordering;
        while (_separatorDistances.Count <= slot)
        {
            _separatorDistances.Add([]);
        }

        float[] held = _separatorDistances[slot];
        if (held.Length < minimum)
        {
            held = new float[minimum];
            _separatorDistances[slot] = held;
        }

        return held;
    }

    /// <summary>The deepest frame this worker has ever needed.</summary>
    internal int HighWaterMark => Math.Max(_mightSee.Count - 1, 0);
}
