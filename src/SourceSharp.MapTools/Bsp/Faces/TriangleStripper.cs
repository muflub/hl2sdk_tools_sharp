//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The Xbox tristripper vbsp uses on water primitives
/// (<c>Stripify</c>).
/// </summary>
/// <remarks>
/// <para>
/// It takes a triangle list and returns ONE long strip with degenerate
/// triangles stitching the pieces together. Two things guide it: a greedy
/// search for the triangle with the fewest unused neighbours, so that isolated
/// triangles are left until last and long runs are found early; and a
/// simulated 18-entry post-transform vertex cache, used to choose which strip
/// to concatenate next.
/// </para>
/// <para>
/// <b>The whole mesh is stripped twice and the cheaper answer wins.</b> Once
/// with the SGI greedy lookahead on and once with it off
/// (<c>rgargmap</c>); <c>EstimateStripCost</c> then
/// picks between them, with ties going to the FIRST — the lookahead pass —
/// because the comparison is a strict <c>&lt;</c>.
/// </para>
/// <para>
/// Ported because <c>SubdivideFaceBySubdivSize</c> calls it and the indices it
/// produces go into LUMP_PRIMINDICES verbatim. Subdivision is a deprecated
/// feature — stock prints "Using subdivision on %s" as a warning — so this runs
/// on almost no maps, but when it runs its exact output is in the file.
/// </para>
/// </remarks>
public static class TriangleStripper
{
    /// <summary>The simulated post-transform vertex cache size (<c>CACHE_SIZE</c>).</summary>
    public const int CacheSize = 18;

    /// <summary>The longest strip either pass will build (<c>maxlen</c>).</summary>
    public const int MaxStripLength = 1024;

    /// <summary>
    /// Turns a triangle list into one long strip (<c>Stripify</c>,
    ///).
    /// </summary>
    /// <param name="triangles">Three indices per triangle.</param>
    /// <returns>The strip's indices, or an empty array for an empty input.</returns>
    /// <exception cref="ArgumentException"><paramref name="triangles"/> is not a multiple of three.</exception>
    public static ushort[] Stripify(IReadOnlyList<ushort> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);

        if (triangles.Count == 0)
        {
            return [];
        }

        if (triangles.Count % 3 != 0)
        {
            throw new ArgumentException(
                "a triangle list must hold three indices per triangle", nameof(triangles));
        }

        Stripper stripper = new(triangles);

        List<StripVerts> best = [];
        int bestCost = 0;

        // rgargmap: lookahead on, then off. Ties go to the
        // FIRST, because the cost comparison is a strict less-than.
        foreach (bool lookAhead in new[] { true, false })
        {
            List<StripVerts> strips = stripper.BuildStrips(MaxStripLength, lookAhead);
            int cost = EstimateStripCost(strips);

            if (bestCost == 0 || cost < bestCost)
            {
                best = strips;
                bestCost = cost;
            }
        }

        return CreateLongStrip(best);
    }

    /// <summary>
    /// A guess at how many indices a set of strips would cost once
    /// concatenated(<c>EstimateStripCost</c>).
    /// </summary>
    /// <param name="strips">The strips.</param>
    /// <returns>Their total length plus two stitching indices per join.</returns>
    public static int EstimateStripCost(List<StripVerts> strips)
    {
        int count = 0;

        foreach (StripVerts strip in strips)
        {
            count += strip.Length;
        }

        return count + ((strips.Count - 1) * 2);
    }

    /// <summary>
    /// Concatenates every strip into one, stitching with degenerates
    /// (<c>CStripper::CreateLongStrip</c>).
    /// </summary>
    /// <param name="strips">The strips, which are consumed.</param>
    /// <returns>The indices.</returns>
    internal static ushort[] CreateLongStrip(List<StripVerts> strips)
    {
        List<ushort> output = [];
        VertCache cache = new();

        // add first strip
        StripVerts first = strips[0];

        for (int i = 0; i < first.Length; i++)
        {
            output.Add(first.Indices[i]);
            cache.Add(1, first.Indices[i]);
        }

        strips.RemoveAt(0);

        while (strips.Count > 0)
        {
            int chosen = FindBestCachedStrip(strips, cache);
            StripVerts strip = strips[chosen];

            ushort lastVert = output[^1];
            ushort firstVert = strip.Indices[0];

            if (firstVert != lastVert)
            {
                // add degenerate from last strip, then from ours
                output.Add(lastVert);
                output.Add(firstVert);
            }

            // If we're not oriented correctly we would need another degenerate.
            // Stock asserts this cannot happen and then adds one anyway; the
            // assert is compiled out of the shipped tool, so the branch is live
            // and is reproduced.
            if (strip.ClockWise != ((output.Count & 1) == 0))
            {
                output.Add(firstVert);
            }

            for (int i = 0; i < strip.Length; i++)
            {
                output.Add(strip.Indices[i]);
                cache.Add(1, strip.Indices[i]);
            }

            strips.RemoveAt(chosen);
        }

        return [.. output];
    }

    /// <summary>
    /// Picks the strip that would hit the vertex cache hardest, reversing it
    /// when that helps(<c>FindBestCachedStrip</c>).
    /// </summary>
    /// <param name="strips">The remaining strips. The winner is swapped to the front.</param>
    /// <param name="state">The cache as it stands.</param>
    /// <returns>The index of the chosen strip, which is always 0.</returns>
    /// <remarks>
    /// Only strips of the same orientation AND the same length parity as the
    /// FIRST one are considered, because those are the ones that can be
    /// appended without a flip. If none qualifies, the first is taken
    /// unchanged — <c>istriplistbest</c> is initialised to <c>begin()</c>.
    /// </remarks>
    internal static int FindBestCachedStrip(List<StripVerts> strips, VertCache state)
    {
        bool flipStrip = false;
        int bestCacheHits = -1;
        int best = 0;

        int stripLength = strips[0].Length;
        bool clockWise = strips[0].ClockWise;

        for (int index = 0; index < strips.Count; index++)
        {
            bool flip = false;
            StripVerts strip = strips[index];
            int newLength = strip.Length;

            if (strip.ClockWise != clockWise || (stripLength & 1) != (newLength & 1))
            {
                continue;
            }

            VertCache candidate = state.Clone();

            for (int i = 0; i < newLength; i++)
            {
                candidate.Add(2, strip.Indices[i]);
            }

            // even length strip - see if better cache hits reversed
            if ((newLength & 1) == 0)
            {
                VertCache flipped = state.Clone();

                for (int i = newLength - 1; i >= 0; i--)
                {
                    flipped.Add(2, strip.Indices[i]);
                }

                if (flipped.CacheHits > candidate.CacheHits)
                {
                    candidate = flipped;
                    flip = true;
                }
            }

            int hits = candidate.CacheHits - state.CacheHits;

            if (hits > bestCacheHits)
            {
                bestCacheHits = hits;
                best = index;
                flipStrip = flip;
            }
        }

        if (flipStrip)
        {
            strips[best].Indices.Reverse(0, strips[best].Length);
        }

        if (best != 0)
        {
            (strips[0], strips[best]) = (strips[best], strips[0]);
        }

        return 0;
    }

    /// <summary>
    /// One strip: its vertex indices, and whether it starts clockwise.
    /// </summary>
    /// <remarks>
    /// Stock stores the orientation as an extra entry past the end of the
    /// vector, so <c>StripLen</c> is <c>size()-1</c> and <c>FIsStripCW</c>
    /// reads the last element. Named fields here; the arithmetic is the same.
    /// </remarks>
    public sealed class StripVerts
    {
        /// <summary>Creates a strip.</summary>
        /// <param name="indices">Its vertex indices.</param>
        /// <param name="clockWise">Whether the first triangle is clockwise.</param>
        public StripVerts(List<ushort> indices, bool clockWise)
        {
            ArgumentNullException.ThrowIfNull(indices);
            Indices = indices;
            ClockWise = clockWise;
        }

        /// <summary>The strip's vertex indices.</summary>
        public List<ushort> Indices { get; }

        /// <summary>Whether the strip's first triangle is wound clockwise.</summary>
        public bool ClockWise { get; }

        /// <summary>How many indices the strip has (<c>StripLen</c>).</summary>
        public int Length => Indices.Count;
    }

    /// <summary>
    /// The simulated post-transform vertex cache
    /// (<c>CVertCache</c>).
    /// </summary>
    /// <remarks>
    /// A FIFO of <see cref="CacheSize"/> entries, each remembering which STRIP
    /// put it there. A hit is counted only when the vertex is already present
    /// AND was put there by a different strip, which is what makes the count a
    /// measure of sharing between strips rather than within one.
    /// </remarks>
    public sealed class VertCache
    {
        private readonly int[] _cache = new int[CacheSize];
        private readonly int[] _strip = new int[CacheSize];
        private int _pointer;

        /// <summary>Creates an empty cache.</summary>
        public VertCache()
        {
            // memset(m_rgCache, 0xff, ...) over a WORD array: every entry is
            // 0xffff, which no real index can be.
            Array.Fill(_cache, 0xffff);
        }

        /// <summary>How many hits have been counted (<c>NumCacheHits</c>).</summary>
        public int CacheHits { get; private set; }

        /// <summary>Copies the cache, as stock copies the struct by value.</summary>
        /// <returns>An independent copy.</returns>
        public VertCache Clone()
        {
            VertCache copy = new();
            _cache.CopyTo(copy._cache, 0);
            _strip.CopyTo(copy._strip, 0);
            copy._pointer = _pointer;
            copy.CacheHits = CacheHits;
            return copy;
        }

        /// <summary>Offers a vertex to the cache (<c>CVertCache::Add</c>).</summary>
        /// <param name="strip">Which strip is adding it.</param>
        /// <param name="vertexIndex">The vertex index.</param>
        /// <returns>True when the cache changed.</returns>
        public bool Add(int strip, int vertexIndex)
        {
            for (int i = 0; i < CacheSize; i++)
            {
                if (vertexIndex != _cache[i])
                {
                    continue;
                }

                if (strip != _strip[i])
                {
                    CacheHits++;
                    _strip[i] = strip;
                    return true;
                }

                return false;
            }

            _cache[_pointer] = vertexIndex;
            _strip[_pointer] = strip;
            _pointer = (_pointer + 1) % CacheSize;
            return true;
        }
    }

    /// <summary>
    /// The stripper's per-mesh state: adjacency and the used flags
    /// (<c>CStripper</c>).
    /// </summary>
    internal sealed class Stripper
    {
        private readonly IReadOnlyList<ushort> _triangles;
        private readonly int _triangleCount;
        private readonly int[,] _neighbourTri;
        private readonly int[,] _neighbourEdge;
        private readonly int[] _used;

        internal Stripper(IReadOnlyList<ushort> triangles)
        {
            _triangles = triangles;
            _triangleCount = triangles.Count / 3;
            _neighbourTri = new int[_triangleCount, 3];
            _neighbourEdge = new int[_triangleCount, 3];
            _used = new int[_triangleCount];

            for (int i = 0; i < _triangleCount; i++)
            {
                _neighbourTri[i, 0] = -1;
                _neighbourTri[i, 1] = -1;
                _neighbourTri[i, 2] = -1;
            }

            // The used flags are reused as a per-edge "already matched" bitmask
            // while adjacency is built, then cleared.
            for (int tri = 0; tri < _triangleCount; tri++)
            {
                for (int vert = 0; vert < 3; vert++)
                {
                    if ((_used[tri] & (1 << vert)) == 0)
                    {
                        InitTriangleInfo(tri, vert);
                    }
                }
            }

            Array.Clear(_used);
        }

        /// <summary>
        /// Builds a set of strips covering the whole mesh
        /// (<c>CStripper::BuildStrips</c>).
        /// </summary>
        /// <param name="maxLength">The longest strip to build.</param>
        /// <param name="lookAhead">Whether to use the SGI greedy lookahead.</param>
        /// <returns>The strips, in the order they were found.</returns>
        internal List<StripVerts> BuildStrips(int maxLength, bool lookAhead)
        {
            List<StripVerts> strips = [];

            int[] stripVerts = new int[MaxStripLength + 1];
            int[] stripTris = new int[MaxStripLength + 1];

            Array.Clear(_used);

            bool startCw = true;

            while (true)
            {
                int bestTri = 0;
                int bestVert = 0;
                float bestRatio = 2.0f;
                int bestNeighbourCount = int.MaxValue;

                for (int tri = 0; tri < _triangleCount; tri++)
                {
                    if (_used[tri] != 0)
                    {
                        continue;
                    }

                    int neighbours = GetNeighborCount(tri);

                    // push all the singletons to the very end
                    if (neighbours == 0)
                    {
                        neighbours = 4;
                    }

                    if (neighbours > bestNeighbourCount)
                    {
                        continue;
                    }

                    for (int vert = 0; vert < 3; vert++)
                    {
                        int length = CreateStrip(
                            tri, vert, maxLength, out int swaps, lookAhead, startCw, stripTris, stripVerts);

                        float ratio = length == 3 ? 1.0f : (float)swaps / length;

                        if (neighbours < bestNeighbourCount
                            || (neighbours == bestNeighbourCount && ratio < bestRatio))
                        {
                            bestNeighbourCount = neighbours;
                            bestTri = tri;
                            bestVert = vert;
                            bestRatio = ratio;
                        }
                    }
                }

                if (bestNeighbourCount == int.MaxValue)
                {
                    break;
                }

                int len = CreateStrip(
                    bestTri, bestVert, maxLength, out _, lookAhead, startCw, stripTris, stripVerts);

                for (int i = 0; i < len; i++)
                {
                    _used[stripTris[i]] = 1;
                }

                List<ushort> indices = new(len);

                for (int i = 0; i < len; i++)
                {
                    indices.Add(_triangles[(stripTris[i] * 3) + stripVerts[i]]);
                }

                strips.Add(new StripVerts(indices, startCw));

                // if strip was odd - swap orientation
                if ((len & 1) != 0)
                {
                    startCw = !startCw;
                }
            }

            return strips;
        }

        /// <summary>
        /// Walks the longest strip it can from one triangle and vertex
        /// (<c>CStripper::CreateStrip</c>).
        /// </summary>
        /// <param name="tri">The starting triangle.</param>
        /// <param name="vert">Which of its vertices to start at.</param>
        /// <param name="maxLength">The longest strip to build.</param>
        /// <param name="swaps">How many orientation swaps the strip needed.</param>
        /// <param name="lookAhead">Whether to use the SGI greedy lookahead.</param>
        /// <param name="startCw">Whether the strip starts clockwise.</param>
        /// <param name="stripTris">Filled with the triangle each index comes from.</param>
        /// <param name="stripVerts">Filled with which vertex of that triangle.</param>
        /// <returns>The strip length, or 0 when the triangle was already used.</returns>
        /// <remarks>
        /// It marks triangles used as it goes and then UNMARKS all but the
        /// first two on the way out, so that the caller can measure several
        /// candidate strips before committing to one.
        /// </remarks>
        internal int CreateStrip(
            int tri,
            int vert,
            int maxLength,
            out int swaps,
            bool lookAhead,
            bool startCw,
            int[] stripTris,
            int[] stripVerts)
        {
            swaps = 0;

            if (_used[tri] != 0)
            {
                return 0;
            }

            _used[tri] = 1;

            stripTris[0] = tri;
            stripTris[1] = tri;
            stripTris[2] = tri;

            if (startCw)
            {
                stripVerts[0] = vert % 3;
                stripVerts[1] = (vert + 1) % 3;
                stripVerts[2] = (vert + 2) % 3;
            }
            else
            {
                stripVerts[0] = (vert + 1) % 3;
                stripVerts[1] = (vert + 0) % 3;
                stripVerts[2] = (vert + 2) % 3;
            }

            startCw = !startCw;

            int edge = (startCw ? vert + 2 : vert + 1) % 3;
            int nextTri = _neighbourTri[tri, edge];
            int nextVert = _neighbourEdge[tri, edge];

            int stripCount;

            for (stripCount = 3; stripCount < maxLength; stripCount++)
            {
                if (nextTri == -1 || _used[nextTri] != 0)
                {
                    break;
                }

                tri = nextTri;
                vert = nextVert;

                startCw = !startCw;

                edge = (startCw ? vert + 2 : vert + 1) % 3;
                nextTri = _neighbourTri[tri, edge];
                nextVert = _neighbourEdge[tri, edge];

                bool swap = false;

                if (nextTri == -1 || _used[nextTri] != 0)
                {
                    // if the next tri is a dead end - try swapping orientation
                    swap = true;
                }
                else if (lookAhead)
                {
                    int edgeSwap = (startCw ? vert + 1 : vert + 2) % 3;
                    int nextTriSwap = _neighbourTri[tri, edgeSwap];
                    int nextVertSwap = _neighbourEdge[tri, edgeSwap];

                    if (nextTriSwap != -1 && _used[nextTriSwap] == 0)
                    {
                        if (GetNeighborCount(nextTriSwap) < GetNeighborCount(nextTri))
                        {
                            swap = true;
                        }
                        else if (GetNeighborCount(nextTriSwap) == GetNeighborCount(nextTri))
                        {
                            // same number of neighbours - check THEIR neighbours
                            edgeSwap = (startCw ? nextVertSwap + 2 : nextVertSwap + 1) % 3;
                            nextTriSwap = _neighbourTri[nextTriSwap, edgeSwap];

                            int edge1 = (startCw ? nextVert + 1 : nextVert + 2) % 3;
                            int nextTri1 = _neighbourTri[nextTri, edge1];

                            if (nextTri1 == -1 || _used[nextTri1] != 0)
                            {
                                // natural winding order leads to a dead end so turn
                                swap = true;
                            }
                            else if (nextTriSwap != -1 && _used[nextTriSwap] == 0
                                && GetNeighborCount(nextTriSwap) < GetNeighborCount(nextTri1))
                            {
                                swap = true;
                            }
                        }
                    }
                }

                if (swap)
                {
                    int edgeSwap = (startCw ? vert + 1 : vert + 2) % 3;
                    nextTri = _neighbourTri[tri, edgeSwap];
                    nextVert = _neighbourEdge[tri, edgeSwap];

                    if (nextTri != -1 && _used[nextTri] == 0)
                    {
                        stripTris[stripCount] = stripTris[stripCount - 2];
                        stripVerts[stripCount] = stripVerts[stripCount - 2];
                        stripCount++;
                        swaps++;
                        startCw = !startCw;
                    }
                }

                stripTris[stripCount] = tri;
                stripVerts[stripCount] = (vert + 2) % 3;

                _used[tri] = 1;
            }

            // Clear the used flags. The loop starts at 2 rather than 0 because
            // slots 0, 1 and 2 all hold the STARTING triangle, so slot 2 is
            // what unmarks it; the strip is left with nothing marked, which is
            // what lets the caller measure every candidate before committing.
            for (int j = 2; j < stripCount; j++)
            {
                _used[stripTris[j]] = 0;
            }

            return stripCount;
        }

        internal int GetNeighborCount(int tri)
        {
            int count = 0;

            for (int vert = 0; vert < 3; vert++)
            {
                int neighbour = _neighbourTri[tri, vert];

                if (neighbour != -1 && _used[neighbour] == 0)
                {
                    count++;
                }
            }

            return count;
        }

        private void InitTriangleInfo(int tri, int vert)
        {
            int vert1 = _triangles[(tri * 3) + ((vert + 1) % 3)];
            int vert2 = _triangles[(tri * 3) + vert];

            for (int other = tri + 1; other < _triangleCount; other++)
            {
                if (_used[other] == 0x7)
                {
                    continue;
                }

                for (int i = 0; i < 3; i++)
                {
                    if (_triangles[(other * 3) + i] != vert1
                        || _triangles[(other * 3) + ((i + 1) % 3)] != vert2)
                    {
                        continue;
                    }

                    _neighbourTri[tri, vert] = other;
                    _neighbourEdge[tri, vert] = i;
                    _used[tri] |= 1 << vert;

                    _neighbourTri[other, i] = tri;
                    _neighbourEdge[other, i] = vert;
                    _used[other] |= 1 << i;
                    return;
                }
            }
        }
    }
}
