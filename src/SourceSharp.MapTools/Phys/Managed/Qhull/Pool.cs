//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

// Ported from Qhull 2.6 (1999/04/19), Copyright (c) 1993-1999 The Geometry Center,
// University of Minnesota; modified 2026-09 by the SourceSharp port (Claude, lane p8a)
// to C# for a managed collision cooker; original source: http://www.qhull.org
// (2.6 archived at http://www.geom.uiuc.edu/software/qhull/). See COPYING.txt.

namespace SourceSharp.MapTools.Phys.Managed.Qhull;

/// <summary>
/// A qhull object that a <see cref="QhPool"/> can hand out again: <see cref="Reset"/> makes it
/// indistinguishable from a freshly constructed one (qhull's memset after qh_memalloc).
/// </summary>
internal interface IQhPooled
{
    /// <summary>Puts every field back to what the constructor leaves.</summary>
    void Reset();
}

/// <summary>
/// Storage reused across the builds of one <see cref="QhullSession"/>: the role qhull's own
/// qh_memalloc / qh_memfree freelists play for facets, vertices, ridges, merges and sets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand out in order, never reuse within a build.</b> qhull returns an object to its freelist
/// the moment it is freed and may hand it straight out again. The port cannot do that safely:
/// its object graph is garbage collected, so a "freed" facet may still be reachable from a set
/// or a merge that qhull would never read again but that the port has not cleared. Instead
/// every object handed out during one build is new to that build, and <see cref="Begin"/>
/// makes the whole previous build's storage available again at once. Nothing outlives a build
/// except the copied-out <see cref="QhullResult"/>, which holds no pooled object, so this is
/// safe; and a build sees exactly what fresh allocation would give it, so the facets it
/// produces (ids, order, normals) are the same bits as an unpooled build's.
/// </para>
/// <para>
/// <b>Reset on the way out, not on the way in.</b> An object is reset when it is taken, so a
/// build that ended in an exception (a qhull error, a bug, an injected fault) leaves nothing
/// that the next build can see: whatever state the half-finished build left in its objects is
/// wiped the moment the next build takes them. That is what makes the pool unpoisonable
/// without any cleanup path on failure.
/// </para>
/// <para>
/// <b>Bounded.</b> A pool keeps at most <see cref="RetainLimit"/> objects of each kind and
/// never keeps a set array bigger than it has to (see <see cref="QSet{T}.Reuse"/>); beyond the
/// limit a build still gets fresh objects, it just does not keep them. A pool therefore holds
/// at most what the biggest hull its thread has built needed, capped, and it goes when its
/// owner (the cooker's per-thread context) goes: nothing grows with the number of hulls, maps
/// or compiles. Retaining up to the high-water mark is the point: the hulls one thread cooks
/// are similar in size, so after the first few every build runs without allocating a facet,
/// vertex, ridge, merge or set.
/// </para>
/// <para>
/// Not thread-safe; one pool per thread of work (the cooker keeps one per thread in its
/// <see cref="IvpCookContext"/>), so concurrent compiles never share one.
/// </para>
/// </remarks>
internal sealed class QhPool
{
    /// <summary>
    /// The most objects of one kind a pool keeps between builds. A prop hull of a few hundred
    /// points uses a few thousand sets and under a thousand facets, so this keeps every hull a
    /// cooker meets in practice allocation-free while bounding what one pathological hull can
    /// leave behind.
    /// </summary>
    internal const int RetainLimit = 1 << 14;

    /// <summary>
    /// The largest set array a reused set keeps when the set is asked for much less (see
    /// <see cref="QSet{T}.Reuse"/>). 64 slots covers every neighbor, vertex and ridge set of a
    /// 3-d hull; the rare big sets (a partition's point list, a hash table) are sized per build.
    /// </summary>
    internal const int SetSlackLimit = 64;

    private readonly Slots<Facet> facets = new();
    private readonly Slots<Vertex> vertices = new();
    private readonly Slots<Ridge> ridges = new();
    private readonly Slots<MergeT> merges = new();
    private readonly List<double[]> points = new();
    private int pointCur;
    private int pointLen = -1;

    private readonly SetSlots<Facet> facetSets = new();
    private readonly SetSlots<Vertex> vertexSets = new();
    private readonly SetSlots<Ridge> ridgeSets = new();
    private readonly SetSlots<MergeT> mergeSets = new();
    private readonly SetSlots<double[]> pointSets = new();

    /// <summary>qsort's temporary storage for merges, kept between builds.</summary>
    internal MergeT?[]? SortMergeScratch;

    /// <summary>qsort's temporary storage for vertices, kept between builds.</summary>
    internal Vertex?[]? SortVertexScratch;

    /// <summary>
    /// Test seam: called with the number of facets taken so far in this build, before each
    /// facet is handed out, so a fact can abort a build part-way (as a cancelled or failed
    /// cook would) and check the pool is still fit for the next build. Null in production.
    /// </summary>
    internal Action<int>? FacetTaken;

    /// <summary>How many objects of each kind this pool holds, for the facts that check reuse and the bounds.</summary>
    internal (int Facets, int Vertices, int Ridges, int Merges, int Points, int Sets) Retained =>
        (facets.Count, vertices.Count, ridges.Count, merges.Count, points.Count,
            facetSets.Count + vertexSets.Count + ridgeSets.Count + mergeSets.Count + pointSets.Count);

    /// <summary>Starts a build: everything handed out before may be handed out again.</summary>
    internal void Begin()
    {
        facets.Begin();
        vertices.Begin();
        ridges.Begin();
        merges.Begin();
        pointCur = 0;
        facetSets.Begin();
        vertexSets.Begin();
        ridgeSets.Begin();
        mergeSets.Begin();
        pointSets.Begin();
    }

    /// <summary>A zeroed facet (qh_memalloc + memset); keeps its spare normal array.</summary>
    internal Facet NewFacet()
    {
        FacetTaken?.Invoke(facets.Cursor);
        return facets.Take();
    }

    /// <summary>A zeroed vertex.</summary>
    internal Vertex NewVertex() => vertices.Take();

    /// <summary>A zeroed ridge.</summary>
    internal Ridge NewRidge() => ridges.Take();

    /// <summary>A zeroed merge record.</summary>
    internal MergeT NewMerge() => merges.Take();

    /// <summary>A point-sized array of length <paramref name="len"/> (contents unspecified).</summary>
    internal double[] NewPoint(int len)
    {
        if (len != pointLen)
        {
            points.Clear();
            pointCur = 0;
            pointLen = len;
        }
        if (pointCur < points.Count)
            return points[pointCur++];
        var p = new double[len];
        if (points.Count < RetainLimit)
        {
            points.Add(p);
            pointCur++;
        }
        return p;
    }

    /// <summary>
    /// The set storage for element type <typeparamref name="T"/>. The port only ever builds sets
    /// of these five element types; any other is a programming error.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>This pool's sets of that type.</returns>
    internal SetSlots<T> Sets<T>()
        where T : class
    {
        if (typeof(T) == typeof(Facet))
            return (SetSlots<T>)(object)facetSets;
        if (typeof(T) == typeof(Vertex))
            return (SetSlots<T>)(object)vertexSets;
        if (typeof(T) == typeof(Ridge))
            return (SetSlots<T>)(object)ridgeSets;
        if (typeof(T) == typeof(MergeT))
            return (SetSlots<T>)(object)mergeSets;
        if (typeof(T) == typeof(double[]))
            return (SetSlots<T>)(object)pointSets;
        throw new NotSupportedException("qhull sets of " + typeof(T).Name + " are not pooled");
    }

    /// <summary>One kind of object, handed out in order and reset as it is taken.</summary>
    /// <typeparam name="T">The object type.</typeparam>
    private sealed class Slots<T>
        where T : class, IQhPooled, new()
    {
        private readonly List<T> items = new();
        private int cur;

        public int Count => items.Count;

        public int Cursor => cur;

        public void Begin() => cur = 0;

        public T Take()
        {
            if (cur < items.Count)
            {
                T item = items[cur++];
                item.Reset();
                return item;
            }
            var fresh = new T();
            if (items.Count < RetainLimit)
            {
                items.Add(fresh);
                cur++;
            }
            return fresh;
        }
    }

    /// <summary>The sets of one element type, handed out in order and cleared as they are taken.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class SetSlots<T>
        where T : class
    {
        private readonly List<QSet<T>> sets = new();
        private int cur;

        public int Count => sets.Count;

        public void Begin() => cur = 0;

        /// <summary>qh_setnew (<paramref name="setsize"/>) from this storage.</summary>
        /// <param name="setsize">The capacity asked for.</param>
        /// <returns>An empty set, every slot null.</returns>
        public QSet<T> Take(int setsize)
        {
            if (cur < sets.Count)
            {
                QSet<T> set = sets[cur++];
                set.Reuse(setsize, SetSlackLimit);
                return set;
            }
            var fresh = new QSet<T>(setsize);
            if (sets.Count < RetainLimit)
            {
                sets.Add(fresh);
                cur++;
            }
            return fresh;
        }
    }
}

/// <summary>
/// A reusable hull builder: the same results as <see cref="QhullBuilder"/>, with the
/// facets, vertices, ridges, merges, sets and point buffers of earlier builds reused. Not
/// thread-safe: use one session per thread.
/// </summary>
internal sealed class QhullSession
{
    /// <summary>This session's storage (internal for the facts that inspect reuse).</summary>
    internal QhPool Pool { get; } = new();

    /// <summary>As <see cref="QhullBuilder.Build"/>, reusing this session's storage.</summary>
    /// <param name="xyz">Point coordinates, three per point.</param>
    /// <param name="options">A qhull command starting with "qhull ".</param>
    /// <returns>The exit code and, on success, the facets.</returns>
    public QhullResult Build(ReadOnlySpan<double> xyz, string options) => QhullBuilder.BuildCore(xyz, options, Pool);

    /// <summary>As <see cref="QhullBuilder.BuildIvp"/>, reusing this session's storage.</summary>
    /// <param name="xyz">Point coordinates, three per point.</param>
    /// <returns>Every attempt's command and exit code, and the facets of the successful attempt.</returns>
    public QhullResult BuildIvp(ReadOnlySpan<double> xyz) => QhullBuilder.BuildIvpCore(xyz, Pool);
}
