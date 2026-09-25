using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The shared edge table and the per-vertex index that makes finding a
/// reusable edge cheap
/// (<c>AddEdge</c>/<c>GetEdge2</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two faces that meet along an edge store ONE edge between them, and each
/// names it with a signed index into LUMP_SURFEDGES: positive for the face that
/// created it and walks it forwards, negative for the face on the other side
/// which walks it backwards. That is what <see cref="GetEdge"/> returns.
/// </para>
/// <para>
/// <b>An edge is shared by at most two faces, and only between faces of the
/// same contents.</b> Both conditions are in one <c>if</c>
/// The candidate must be the exact reverse of the edge
/// being asked for, its first face's contents must match, and its second slot
/// must still be free. A third face along the same line gets its own edge.
/// </para>
/// <para>
/// Called by <c>WriteBSP</c>, not by this stage — but it lives in
/// And it is the other half of what the vertex weld exists
/// for, so it is here.
/// </para>
/// </remarks>
public sealed class EdgeTable
{
    /// <summary><c>MAX_MAP_EDGES</c>.</summary>
    public const int MaxMapEdges = 256000;

    private readonly List<DEdge> _edges = [];
    private readonly List<Face?[]> _edgeFaces = [];
    private readonly Dictionary<int, List<int>> _vertexEdges = [];

    // Every edge ever emitted, chained by its stored (v[0], v[1]) pair in
    // ascending index order: _pairChain maps the pair to its first and last
    // edge, _pairNext links each edge to the next one with the same pair.
    // It is never reset -- FindReverseEdge filters by index instead -- so it
    // answers the whole table exactly as a linear scan of dedges would.
    private readonly Dictionary<uint, (int Head, int Tail)> _pairChain = [];
    private readonly List<int> _pairNext = [];
    private readonly FaceCounters _counters;

    /// <summary>Creates an empty edge table.</summary>
    /// <param name="counters">Where <c>c_tryedges</c> is kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="counters"/> is null.</exception>
    public EdgeTable(FaceCounters counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        _counters = counters;
    }

    /// <summary><c>numedges</c>.</summary>
    public int Count => _edges.Count;

    /// <summary><c>dedges</c>, in emission order.</summary>
    public IReadOnlyList<DEdge> Edges => _edges;

    /// <summary>
    /// The one or two faces that use an edge (<c>edgefaces</c>).
    /// </summary>
    /// <param name="edge">The edge index.</param>
    /// <returns>Slot 0 is the face that created it, slot 1 the face that shares it.</returns>
    public IReadOnlyList<Face?> FacesOf(int edge) => _edgeFaces[edge];

    /// <summary>
    /// Records a second face on an existing edge: <c>edgefaces[j][1] = f</c>,
    /// as <c>CreateOrigFace</c> does after its own linear search
    /// </summary>
    /// <param name="edge">The edge index.</param>
    /// <param name="face">The face walking it backwards.</param>
    /// <exception cref="InvalidOperationException">The edge already has a second face.</exception>
    public void ShareEdge(int edge, Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (_edgeFaces[edge][1] is not null)
        {
            throw new InvalidOperationException($"edge {edge} already has two faces");
        }

        _edgeFaces[edge][1] = face;
    }

    /// <summary>
    /// Clears the per-vertex index
    /// (<c>GetEdge2_InitOptimizedList</c>).
    /// </summary>
    /// <remarks>
    /// Stock clears only this, not <c>dedges</c> — it is called once per model
    /// by <c>BeginBSPFile</c>/<c>WriteBSP</c>, so the edges accumulate across
    /// models while the lookup index does not. That is correct: a brush model's
    /// vertices are renumbered from zero, so an index from the previous model
    /// would name the wrong vertex.
    /// </remarks>
    public void ResetLookup() => _vertexEdges.Clear();

    /// <summary>
    /// Emits a new edge(<c>AddEdge</c>).
    /// </summary>
    /// <param name="v1">The first vertex.</param>
    /// <param name="v2">The second.</param>
    /// <param name="face">The face creating it.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="InvalidOperationException">The table is full.</exception>
    public int AddEdge(int v1, int v2, Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (_edges.Count >= MaxMapEdges)
        {
            throw new InvalidOperationException($"Too many edges in map, max == {MaxMapEdges}");
        }

        int index = _edges.Count;

        ListFor(v1).Add(index);
        ListFor(v2).Add(index);

        // Stock re-sorts both lists with an insertion sort after every single
        // append(IntSort). Appending an index that is larger
        // than every index already there leaves a sorted list sorted, so the
        // sort is a no-op on every call -- it is reproduced by simply not
        // needing to happen, and the order the lists end up in is identical.
        _edges.Add(new DEdge { V = MakeEdge(v1, v2) });
        _edgeFaces.Add([face, null]);
        Chain(PairKey((ushort)v1, (ushort)v2), index);

        return index;
    }

    /// <summary>
    /// The first edge at or after <paramref name="firstEdge"/> that a face of
    /// <paramref name="contents"/> walking <paramref name="v0"/> to
    /// <paramref name="v1"/> can share: stored as <c>(v1, v0)</c>, first face
    /// of the same contents, second slot free. This is <c>CreateOrigFace</c>'s
    /// linear back-edge search (<c>for(j =
    /// firstmodeledge; j &lt; numedges; j++ )</c>) answered from an index.
    /// </summary>
    /// <param name="v0">The edge's first vertex, in the asking face's winding order.</param>
    /// <param name="v1">Its second.</param>
    /// <param name="contents">The asking face's contents.</param>
    /// <param name="firstEdge">The lowest index the search may return (<c>firstmodeledge</c>).</param>
    /// <returns>The edge index, or -1 when the scan would run off the end.</returns>
    /// <remarks>
    /// Stock compares an <c>int</c> vertex number against the stored
    /// <c>unsigned short</c>, so a vertex outside 0..65535 matches nothing; so
    /// it is here. Candidates are visited in ascending index order and the
    /// first one passing every test wins, which is exactly the edge the
    /// scan's <c>break</c> stops at (a matching edge whose second slot is
    /// taken is the scan's <c>continue</c>).
    /// </remarks>
    public int FindReverseEdge(int v0, int v1, int contents, int firstEdge)
    {
        if ((uint)v0 > ushort.MaxValue || (uint)v1 > ushort.MaxValue)
        {
            return -1;
        }

        if (!_pairChain.TryGetValue(PairKey((ushort)v1, (ushort)v0), out (int Head, int Tail) chain))
        {
            return -1;
        }

        for (int j = chain.Head; j != -1; j = _pairNext[j])
        {
            if (j < firstEdge)
            {
                continue;
            }

            Face?[] owners = _edgeFaces[j];
            if (owners[0]!.Contents == contents && owners[1] is null)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// The signed surfedge for a face's edge, reusing an existing one where the
    /// rules allow(<c>GetEdge2</c>).
    /// </summary>
    /// <param name="v1">The edge's first vertex, in this face's winding order.</param>
    /// <param name="v2">Its second.</param>
    /// <param name="face">The face asking.</param>
    /// <param name="noShare">Stock's <c>-noshare</c>: never reuse.</param>
    /// <returns>A non-negative index for a new edge, or the negated index of a shared one.</returns>
    /// <remarks>
    /// <b>Edge zero can never be shared.</b> The reuse path returns
    /// <c>-iEdge</c>, and the negation of zero is zero, which reads as "a new
    /// forward edge 0" — so a face that matched edge 0 would silently claim to
    /// own it. Stock avoids this by never letting anything use edge 0: the
    /// first edge emitted is index 0 and the file format reserves it
    /// Nothing here needs to special-case it because
    /// <c>WriteBSP</c> emits a dummy edge first, exactly as stock does.
    /// </remarks>
    public int GetEdge(int v1, int v2, Face face, bool noShare)
    {
        ArgumentNullException.ThrowIfNull(face);

        _counters.TryEdges++;

        if (!noShare && _vertexEdges.TryGetValue(v1, out List<int>? candidates))
        {
            // Check all edges connected to v1.
            foreach (int index in candidates)
            {
                DEdge edge = _edges[index];

                if (v1 == edge.V[1] && v2 == edge.V[0]
                    && _edgeFaces[index][0]!.Contents == face.Contents)
                {
                    if (_edgeFaces[index][1] is not null)
                    {
                        continue;
                    }

                    _edgeFaces[index][1] = face;
                    return -index;
                }
            }
        }

        return AddEdge(v1, v2, face);
    }

    private static UShortArray2 MakeEdge(int v1, int v2)
    {
        UShortArray2 pair = default;
        pair[0] = (ushort)v1;
        pair[1] = (ushort)v2;
        return pair;
    }

    private static uint PairKey(ushort v0, ushort v1) => ((uint)v0 << 16) | v1;

    private void Chain(uint key, int index)
    {
        _pairNext.Add(-1);

        ref (int Head, int Tail) chain =
            ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_pairChain, key, out bool exists);
        if (exists)
        {
            _pairNext[chain.Tail] = index;
            chain.Tail = index;
        }
        else
        {
            chain = (index, index);
        }
    }

    private List<int> ListFor(int vertex)
    {
        if (!_vertexEdges.TryGetValue(vertex, out List<int>? list))
        {
            list = [];
            _vertexEdges[vertex] = list;
        }

        return list;
    }
}
