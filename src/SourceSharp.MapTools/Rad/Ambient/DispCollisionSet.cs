//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Disp;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// <c>s_DispTested[iThread]</c> as per-work-item state:
/// which displacements the current ray has already been tested against.
/// </summary>
/// <remarks>
/// Stock's <c>DispTested_t</c> is an int per displacement plus a running
/// counter; <c>StartRayTest</c> bumps the counter so that one ray walking
/// several leaves tests each displacement once.
/// Here one instance belongs to one work item (a leaf, a detail prop), never
/// to a thread, so the stage stays a pure function of its item.
/// </remarks>
public sealed class DispTestedScratch
{
    private readonly int[] _tested;
    private int _enum;

    /// <summary>Makes scratch for a map's displacements.</summary>
    /// <param name="displacements">How many there are.</param>
    public DispTestedScratch(int displacements) => _tested = new int[Math.Max(displacements, 0)];

    /// <summary><c>StartRayTest</c>: begin a new ray.</summary>
    public void StartRayTest()
    {
        if (++_enum == int.MaxValue)
        {
            Array.Clear(_tested);
            _enum = 1;
        }
    }

    /// <summary>Marks a displacement tested; false when it already was this ray.</summary>
    /// <param name="disp">The displacement.</param>
    /// <returns>Whether it still needs testing.</returns>
    public bool TryMark(int disp)
    {
        if (_tested[disp] == _enum)
        {
            return false;
        }

        _tested[disp] = _enum;
        return true;
    }
}

/// <summary>
/// vrad's displacement manager as leaf ambient sees it
/// (<c>CVRadDispMgr::UnserializeDisps</c>, and the
/// two <c>ClipRayToDispInLeaf</c> overloads,).
/// </summary>
/// <remarks>
/// <para>
/// Each displacement is rebuilt from the lump the way
/// <c>DispBuilderInit</c> does -- base points from
/// the face's surfedges, start corner, luxel coordinates from the texinfo's
/// lightmap vectors, field vectors from <c>LUMP_DISP_VERTS</c> -- and becomes a
/// <see cref="DispCollisionTree"/>. Each tree's bloated box is then inserted
/// into every leaf the box query returns, as <c>CBSPTreeData::Insert</c> does.
/// </para>
/// <para>
/// LEAF LIST ORDER: <c>AddHandleToLeaf</c> links each new element BEFORE the
/// Leaf's first, and the trees are inserted in
/// displacement order, so a leaf enumerates its displacements in DESCENDING
/// index order. The distance comparison is strict, so on an exact tie the
/// first enumerated keeps the hit; the order is reproduced for that reason.
/// </para>
/// </remarks>
public sealed class DispCollisionSet
{
    private readonly DispCollisionTree[] _trees;
    private readonly int[] _leafStart;
    private readonly int[] _leafDisps;

    // The bloated bounds of _leafDisps[i]'s tree, as six rows (min x, y, z,
    // max x, y, z) of _leafDisps.Length + BoxBatch floats each: eight entries
    // of a leaf's list load as one vector, and the padding keeps the last
    // load inside the row.
    private readonly float[] _listBoxes;
    private readonly int _boxRow;

    /// <summary>How many list entries one batched box test covers.</summary>
    private const int BoxBatch = 8;

    private DispCollisionSet(DispCollisionTree[] trees, int[] leafStart, int[] leafDisps)
    {
        _trees = trees;
        _leafStart = leafStart;
        _leafDisps = leafDisps;
        _boxRow = leafDisps.Length + BoxBatch;
        _listBoxes = new float[_boxRow * 6];
        for (int i = 0; i < leafDisps.Length; i++)
        {
            DispCollisionTree t = trees[leafDisps[i]];
            _listBoxes[i] = t.Mins.X;
            _listBoxes[_boxRow + i] = t.Mins.Y;
            _listBoxes[(2 * _boxRow) + i] = t.Mins.Z;
            _listBoxes[(3 * _boxRow) + i] = t.Maxs.X;
            _listBoxes[(4 * _boxRow) + i] = t.Maxs.Y;
            _listBoxes[(5 * _boxRow) + i] = t.Maxs.Z;
        }
    }

    /// <summary>How many displacements.</summary>
    public int Count => _trees.Length;

    /// <summary>A set with no displacements, for a map with none.</summary>
    /// <param name="leafCount">The map's leaf count.</param>
    /// <returns>The set.</returns>
    public static DispCollisionSet Empty(int leafCount) =>
        new([], new int[leafCount + 1], []);

    /// <summary>Makes scratch sized for this set.</summary>
    /// <returns>New scratch, for one work item.</returns>
    public DispTestedScratch CreateScratch() => new(_trees.Length);

    /// <summary>Rebuilds every displacement in a map.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <exception cref="InvalidBspException">A displacement has no valid base face.</exception>
    public static DispCollisionSet Build(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        int leafCount = AmbientScene.ReadLeaves(bsp).Length;

        ReadOnlySpan<DispInfo> dispInfo = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        if (dispInfo.Length == 0)
        {
            return Empty(leafCount);
        }

        ReadOnlySpan<DispVert> dispVerts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        ReadOnlySpan<DispTri> dispTris = BspStructView.As<DispTri>(bsp[BspLump.DispTris]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);

        CoreDispInfo?[] builders = new CoreDispInfo?[dispInfo.Length];
        int[] faceOf = new int[dispInfo.Length];
        Array.Fill(faceOf, -1);

        // Every ValidDispFace, in face order.
        for (int f = 0; f < faces.Length; f++)
        {
            ref readonly DFace face = ref faces[f];
            if (face.DispInfo == -1 || face.NumEdges != 4)
            {
                continue;
            }

            if (face.DispInfo < 0 || face.DispInfo >= dispInfo.Length)
            {
                throw new InvalidBspException(
                    $"face {f} names displacement {face.DispInfo} of a LUMP_DISPINFO holding {dispInfo.Length}.");
            }

            ref readonly DispInfo info = ref dispInfo[face.DispInfo];
            builders[face.DispInfo] = BuilderInit(in face, in info, texInfo, vertexes, edges, surfEdges, dispVerts, dispTris);
            faceOf[face.DispInfo] = f;
        }

        DispCollisionTree[] trees = new DispCollisionTree[dispInfo.Length];
        for (int d = 0; d < dispInfo.Length; d++)
        {
            CoreDispInfo builder = builders[d]
                ?? throw new InvalidBspException(
                    $"displacement {d} has no four-edged face naming it (ValidDispFace).");
            builder.Create();
            trees[d] = new DispCollisionTree(builder, faceOf[d]);
        }

        // InsertDispIntoTree, in displacement order;
        // AddHandleToLeaf prepends, so each leaf's list is reversed below.
        List<int>[] perLeaf = new List<int>[leafCount];
        List<int> leaves = [];
        for (int d = 0; d < trees.Length; d++)
        {
            leaves.Clear();
            ToolBspTree.EnumerateLeavesInBox(nodes, planes, trees[d].Mins, trees[d].Maxs, leaves);
            foreach (int leaf in leaves)
            {
                (perLeaf[leaf] ??= []).Add(d);
            }
        }

        int[] leafStart = new int[leafCount + 1];
        List<int> flat = [];
        for (int leaf = 0; leaf < leafCount; leaf++)
        {
            leafStart[leaf] = flat.Count;
            if (perLeaf[leaf] is { } list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    flat.Add(list[i]);
                }
            }
        }

        leafStart[leafCount] = flat.Count;
        return new DispCollisionSet(trees, leafStart, [.. flat]);
    }

    /// <summary>The displacements in a leaf, in enumeration order.</summary>
    /// <param name="leaf">The leaf.</param>
    /// <returns>Displacement indices.</returns>
    public ReadOnlySpan<int> InLeaf(int leaf) =>
        _leafDisps.AsSpan(_leafStart[leaf], _leafStart[leaf + 1] - _leafStart[leaf]);

    /// <summary>
    /// <c>ClipRayToDispInLeaf</c>(/) with
    /// <c>CBSPDispRayDistanceEnumerator</c>: the nearest displacement hit in a
    /// leaf, skipping any this ray already tested.
    /// </summary>
    /// <param name="scratch">This ray's tested set; the caller starts the ray.</param>
    /// <param name="start">The ray start.</param>
    /// <param name="delta">The ray delta.</param>
    /// <param name="leaf">The leaf.</param>
    /// <param name="hit">The nearest hit; <see cref="DispRayHit.Distance"/> is
    /// <c>FLT_MAX</c> and <see cref="DispRayHit.Face"/> -1 when there is none.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ClipRayInLeaf(DispTestedScratch scratch, Vec3 start, Vec3 delta, int leaf, out DispRayHit hit)
    {
        // CBSPDispRayDistanceEnumerator starts at m_Distance = FLT_MAX, m_pSurface = 0.
        hit = new DispRayHit(float.MaxValue, -1, 0, 0, Vec3.Zero);
        if ((uint)leaf >= (uint)(_leafStart.Length - 1))
        {
            return;
        }

        if (!Avx.IsSupported)
        {
            foreach (int d in InLeaf(leaf))
            {
                if (!scratch.TryMark(d))
                {
                    continue;
                }

                if (_trees[d].Ray(start, delta, out DispRayHit one) && one.Distance < hit.Distance)
                {
                    hit = one;
                }
            }

            return;
        }

        // The same walk, with each tree's IsBoxIntersectingRay pre-test made
        // for eight list entries at once (BoxMask). A tree already tested this
        // ray is still skipped before anything else, and one whose box the ray
        // misses is skipped where Ray would have returned false -- so the
        // trees reached, their order and the answer are the scalar walk's.
        int first = _leafStart[leaf];
        int count = _leafStart[leaf + 1] - first;
        RayBoxConstants ray = new(start, delta);
        for (int b = 0; b < count; b += BoxBatch)
        {
            int mask = BoxMask(first + b, in ray);
            int n = Math.Min(BoxBatch, count - b);
            for (int j = 0; j < n; j++)
            {
                int d = _leafDisps[first + b + j];
                if (!scratch.TryMark(d) || (mask & (1 << j)) == 0)
                {
                    continue;
                }

                if (_trees[d].RayInsideBox(start, delta, out DispRayHit one) && one.Distance < hit.Distance)
                {
                    hit = one;
                }
            }
        }
    }

    /// <summary>What <see cref="BoxMask"/> needs of a ray, made once per ray.</summary>
    private readonly struct RayBoxConstants
    {
        public readonly Vector256<float> Ox, Oy, Oz, Dx, Dy, Dz, InvX, InvY, InvZ;

        public RayBoxConstants(Vec3 origin, Vec3 delta)
        {
            Ox = Vector256.Create(origin.X);
            Oy = Vector256.Create(origin.Y);
            Oz = Vector256.Create(origin.Z);
            Dx = Vector256.Create(delta.X);
            Dy = Vector256.Create(delta.Y);
            Dz = Vector256.Create(delta.Z);
            InvX = Vector256.Create(DispCollisionTree.SaturatedReciprocal(delta.X));
            InvY = Vector256.Create(DispCollisionTree.SaturatedReciprocal(delta.Y));
            InvZ = Vector256.Create(DispCollisionTree.SaturatedReciprocal(delta.Z));
        }
    }

    /// <summary>
    /// <see cref="DispCollisionTree.IsBoxIntersectingRay"/> with tolerance
    /// <see cref="DispCollisionTree.DistEpsilon"/>, for list entries
    /// <paramref name="at"/> to <paramref name="at"/> + 7, one per lane.
    /// </summary>
    /// <returns>Bit j set when entry <c>at + j</c>'s box is hit.</returns>
    /// <remarks>
    /// Lane for lane the scalar function's operations: the same offsets, the
    /// same six comparisons and rejection, then the slab distances folded axis
    /// by axis, x, y, z, with <c>minps</c>/<c>maxps</c>, which ARE its
    /// <c>a &lt; b ? a : b</c> and <c>a &gt; b ? a : b</c> -- NaN included.
    /// An axis the ray does not cross leaves the fold unchanged, as its
    /// <c>continue</c> does.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BoxMask(int at, in RayBoxConstants ray)
    {
        ref float row = ref MemoryMarshal.GetArrayDataReference(_listBoxes);
        Vector256<float> tol = Vector256.Create(DispCollisionTree.DistEpsilon);
        Vector256<float> reject = Vector256<float>.Zero;
        Vector256<float> firstOut = Vector256.Create(float.MaxValue);
        Vector256<float> lastIn = Vector256.Create(-float.MaxValue);

        Axis(ref row, at, 0, ray.Ox, ray.Dx, ray.InvX, tol, ref reject, ref firstOut, ref lastIn);
        Axis(ref row, at, 1, ray.Oy, ray.Dy, ray.InvY, tol, ref reject, ref firstOut, ref lastIn);
        Axis(ref row, at, 2, ray.Oz, ray.Dz, ray.InvZ, tol, ref reject, ref firstOut, ref lastIn);

        firstOut = Avx.Min(firstOut, Vector256.Create(1.0f));
        lastIn = Avx.Max(lastIn, Vector256<float>.Zero);
        Vector256<float> miss = Avx.Or(reject, Avx.CompareGreaterThan(lastIn, firstOut));
        return ~Avx.MoveMask(miss) & 0xFF;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Axis(
            ref float boxes, int index, int axis, Vector256<float> origin, Vector256<float> delta,
            Vector256<float> inv, Vector256<float> tolerance,
            ref Vector256<float> rejected, ref Vector256<float> first, ref Vector256<float> last)
        {
            Vector256<float> lo = Vector256.LoadUnsafe(ref boxes, (nuint)((axis * _boxRow) + index));
            Vector256<float> hi = Vector256.LoadUnsafe(ref boxes, (nuint)(((axis + 3) * _boxRow) + index));
            Vector256<float> offMin = Avx.Subtract(Avx.Subtract(lo, origin), tolerance);
            Vector256<float> offMax = Avx.Add(Avx.Subtract(hi, origin), tolerance);

            Vector256<float> startOutMins = Avx.CompareGreaterThan(offMin, Vector256<float>.Zero);
            Vector256<float> endOutMins = Avx.CompareLessThan(delta, offMin);
            Vector256<float> startOutMaxs = Avx.CompareLessThan(offMax, Vector256<float>.Zero);
            Vector256<float> endOutMaxs = Avx.CompareGreaterThan(delta, offMax);

            rejected = Avx.Or(
                rejected,
                Avx.Or(Avx.And(startOutMins, endOutMins), Avx.And(startOutMaxs, endOutMaxs)));

            Vector256<float> cross = Avx.Or(
                Avx.Xor(startOutMins, endOutMins), Avx.Xor(startOutMaxs, endOutMaxs));

            Vector256<float> tmin = Avx.Multiply(offMin, inv);
            Vector256<float> tmax = Avx.Multiply(offMax, inv);
            Vector256<float> mint = Avx.Min(tmin, tmax);
            Vector256<float> maxt = Avx.Max(tmin, tmax);
            first = Avx.BlendVariable(first, Avx.Min(first, maxt), cross);
            last = Avx.BlendVariable(last, Avx.Max(last, mint), cross);
        }
    }

    /// <summary>
    /// <c>DispBuilderInit</c>.
    /// </summary>
    /// <remarks>
    /// The surface keeps <see cref="CoreDispSurface.StockNormalise"/> at its
    /// exact default under both policies. The two normalises it governs decide
    /// the base quad's normal (which only the unread tangent spaces take here)
    /// and the lightmap-axis swap flag (discarded below), and this set reads
    /// neither: it keeps the displaced vertices and their triangles. So there
    /// is no compliance to thread here, and nothing that could make the set
    /// depend on the CPU.
    /// </remarks>
    private static CoreDispInfo BuilderInit(
        ref readonly DFace face,
        ref readonly DispInfo info,
        ReadOnlySpan<TexInfo> texInfo,
        ReadOnlySpan<Vec3> vertexes,
        ReadOnlySpan<DEdge> edges,
        ReadOnlySpan<int> surfEdges,
        ReadOnlySpan<DispVert> dispVerts,
        ReadOnlySpan<DispTri> dispTris)
    {
        if (info.Power is < 2 or > 4)
        {
            throw new InvalidBspException(
                $"a displacement has power {info.Power}; the format allows 2 to 4.");
        }

        CoreDispInfo disp = new(info.Power);
        CoreDispSurface surf = disp.Surface;
        surf.Contents = info.Contents;

        for (int i = 0; i < 4; i++)
        {
            int e = surfEdges[face.FirstEdge + i];
            surf.SetPoint(i, e < 0 ? vertexes[edges[-e].V[1]] : vertexes[edges[e].V[0]]);
        }

        surf.PointStart = info.StartPosition;
        surf.FindSurfPointStartIndex();
        surf.AdjustSurfPointData();

        ref readonly TexInfo tex = ref texInfo[face.TexInfo];
        Vec3 vecU = new(
            tex.LightmapVecsLuxelsPerWorldUnits[0],
            tex.LightmapVecsLuxelsPerWorldUnits[1],
            tex.LightmapVecsLuxelsPerWorldUnits[2]);
        Vec3 vecV = new(
            tex.LightmapVecsLuxelsPerWorldUnits[4],
            tex.LightmapVecsLuxelsPerWorldUnits[5],
            tex.LightmapVecsLuxelsPerWorldUnits[6]);

        // static_cast<int>( 1.0f / VectorLength( vecTmp ) ).
        int luxelsPerWorldUnit = (int)(1.0f / vecU.Length());
        surf.CalcLuxelCoords(luxelsPerWorldUnit, false, vecU, vecV);

        int count = info.NumVerts();
        ReadOnlySpan<DispVert> run = dispVerts.Slice(info.DispVertStart, count);
        float[] alphas = new float[count];
        Vec3[] vectors = new Vec3[count];
        float[] dists = new float[count];
        for (int i = 0; i < count; i++)
        {
            vectors[i] = run[i].Vector;
            dists[i] = run[i].Dist;
            alphas[i] = run[i].Alpha;
        }

        disp.InitDispInfo(info.MinTess, alphas, vectors, dists);

        // The tags come from LUMP_DISP_TRIS.
        int tris = disp.TriCount;
        if (info.DispTriStart >= 0 && info.DispTriStart + tris <= dispTris.Length)
        {
            for (int i = 0; i < tris; i++)
            {
                disp.SetTriTagValue(i, dispTris[info.DispTriStart + i].Tags);
            }
        }

        return disp;
    }
}
