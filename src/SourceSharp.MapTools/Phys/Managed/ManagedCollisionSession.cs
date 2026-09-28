//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// <see cref="ICollisionSession"/> with no native library: every <c>IPhysicsCollision</c> call the
/// vbsp path makes, answered by the managed IVP builders.
/// </summary>
/// <remarks>
/// <para>
/// One session per <see cref="ManagedCollisionCooker.RunAsync{T}(Func{ICollisionSession, T}, TaskScheduler, CancellationToken)"/> call, on whatever thread the
/// call runs; handles are keys into this session's own tables and mean nothing outside it, which
/// is the contract <see cref="ICollisionCooker.RunAsync{T}"/> already states. Nothing is shared
/// between sessions except the surface-property table, which is locked.
/// </para>
/// <para>
/// Mirrors the reference <c>CPhysicsCollision</c> wrapper over the IVP
/// builders. The traces (<c>TraceBox</c> as a ray, a zero-length <c>TraceCollide</c>) are exact
/// geometric tests rather than a port of <c>CPhysicsTrace</c>; see <see cref="ManagedTrace"/>.
/// </para>
/// </remarks>
internal sealed class ManagedCollisionSession : ICollisionSession, IConcurrentConvexSession
{
    private readonly IIvpBuild _build;
    private readonly ISurfacePropertySession _surfaceProps;
    private readonly Dictionary<nint, IvpCompactLedge> _convexes = [];
    private readonly Dictionary<nint, ManagedCollide> _collides = [];
    private readonly Dictionary<nint, Polysoup> _soups = [];
    private readonly Dictionary<nint, List<CollideHandle>> _vcollides = [];
    private nint _next = 16;

    /// <summary>A session over one thread's builders.</summary>
    /// <param name="build">The IVP builders at the cooker's precision.</param>
    /// <param name="surfaceProps">The cooker's surface-property table.</param>
    public ManagedCollisionSession(IIvpBuild build, ISurfacePropertySession surfaceProps)
    {
        _build = build;
        _surfaceProps = surfaceProps;
    }

    private sealed class Polysoup
    {
        public List<IvpCompactLedge> Ledges { get; } = [];

        public bool IsValid { get; set; }
    }

    /// <inheritdoc/>
    public ISurfacePropertySession SurfaceProps => _surfaceProps;

    /// <summary>
    /// Copy a polysoup ledge's material to its own triangles only, instead of stock's walk past
    /// the ledge (<see cref="Options.StockQuirk.CollisionPolysoupMaterialOverrun"/>).
    /// </summary>
    public bool FixPolysoupMaterialWalk { get; init; }

    /// <summary>
    /// Opens the builders a worker thread cooks with, called ON that thread; null when this
    /// session has no cooker behind it, and <see cref="BuildConvexes"/> then runs on this session.
    /// </summary>
    /// <remarks>
    /// A factory rather than this session's own builders because those carry the opening
    /// thread's scratch (its qhull storage), which is not thread-safe: every worker has to cook
    /// with its own thread's.
    /// </remarks>
    public Func<IIvpBuild>? WorkerBuilds { get; init; }

    /// <summary>Where <see cref="BuildConvexes"/> queues its helpers: the cooker's scheduler when the session was opened.</summary>
    public TaskScheduler WorkerScheduler { get; init; } = TaskScheduler.Default;

    /// <summary>How many convexes this session holds, for the facts that check a failed build adopted nothing.</summary>
    internal int LiveConvexCount => _convexes.Count;

    private nint NextHandle() => _next += 16;

    /// <summary>
    /// The qhull storage of the thread this session was opened on (its builders' context), which
    /// the geometric queries build their leaf hulls with. Sessions are used on the thread that
    /// opened them (the cooker runs each unit of work synchronously there), which is what makes
    /// sharing the thread's storage with the builders safe.
    /// </summary>
    private Qhull.QhullSession Hulls => _build.Context.Hulls;

    private ConvexHandle Add(IvpCompactLedge? ledge)
    {
        if (ledge is null)
        {
            return default;
        }

        nint h = NextHandle();
        _convexes[h] = ledge;
        return new ConvexHandle(h);
    }

    private CollideHandle Add(ManagedCollide? collide)
    {
        if (collide is null)
        {
            return default;
        }

        nint h = NextHandle();
        _collides[h] = collide;
        return new CollideHandle(h);
    }

    private IvpCompactLedge Convex(ConvexHandle h) =>
        _convexes.TryGetValue(h.Value, out IvpCompactLedge? l) ? l : throw new ArgumentException("unknown or freed convex handle", nameof(h));

    private ManagedCollide Collide(CollideHandle h) =>
        _collides.TryGetValue(h.Value, out ManagedCollide? c) ? c : throw new ArgumentException("unknown or destroyed collide handle", nameof(h));

    private static (float X, float Y, float Z)[] Points(ReadOnlySpan<Vec3> points)
    {
        var tuples = new (float X, float Y, float Z)[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            tuples[i] = (points[i].X, points[i].Y, points[i].Z);
        }

        return tuples;
    }

    private static InstanceTransform? Placement(Vec3 origin, Vec3 angles) =>
        origin == default && angles == default ? null : InstanceTransform.FromAngles(angles, origin);

    /// <inheritdoc/>
    /// <remarks>
    /// Each participating thread cooks into a worker session of its own, over its own thread's
    /// builders, and each finished ledge is taken out of that session at once; the ledges are
    /// added to this session only when every item has succeeded, in index order. The managed
    /// builders hold nothing between builds that reaches their output (the qhull storage they
    /// reuse is handed out as if fresh), so which thread built a brush, and after what, makes no
    /// difference to its bytes. A worker session holds only managed objects: dropping it, on
    /// success or failure, is all the cleanup it needs.
    /// </remarks>
    public ConvexHandle[] BuildConvexes(
        int count,
        Func<ICollisionSession, int, ConvexHandle> build,
        int maxDegree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        var ledges = new IvpCompactLedge?[Math.Max(count, 0)];
        Func<IIvpBuild>? workerBuilds = WorkerBuilds;
        CallerParallelFor.For(
            ledges.Length,
            workerBuilds is null ? 1 : maxDegree,
            WorkerScheduler,
            () => workerBuilds is null
                ? this
                : new ManagedCollisionSession(workerBuilds(), _surfaceProps) { FixPolysoupMaterialWalk = FixPolysoupMaterialWalk },
            (i, worker) => ledges[i] = worker.TakeConvex(build(worker, i)),
            cancellationToken);

        var handles = new ConvexHandle[ledges.Length];
        for (int i = 0; i < ledges.Length; i++)
        {
            handles[i] = Add(ledges[i]);
        }

        return handles;
    }

    /// <summary>Removes a convex from this session's table and returns its ledge; null for a null handle.</summary>
    private IvpCompactLedge? TakeConvex(ConvexHandle convex) =>
        convex.IsNull ? null
        : _convexes.Remove(convex.Value, out IvpCompactLedge? ledge) ? ledge
        : throw new ArgumentException("the build returned a convex that is not the worker session's", nameof(convex));

    /// <inheritdoc/>
    public ConvexHandle ConvexFromVerts(ReadOnlySpan<Vec3> points) => Add(_build.ConvexFromVerts(Points(points)));

    /// <inheritdoc/>
    public ConvexHandle ConvexFromPlanes(ReadOnlySpan<CollisionPlane> planes, float mergeDistance)
    {
        var tuples = new (float X, float Y, float Z, float Distance)[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            tuples[i] = (planes[i].Normal.X, planes[i].Normal.Y, planes[i].Normal.Z, planes[i].Dist);
        }

        return Add(_build.ConvexFromPlanes(tuples, mergeDistance));
    }

    /// <inheritdoc/>
    public float ConvexVolume(ConvexHandle convex) => IvpCollideQueries.ConvexVolume(Convex(convex));

    /// <inheritdoc/>
    public float ConvexSurfaceArea(ConvexHandle convex) => LedgeArea(Convex(convex));

    /// <summary><c>ConvexSurfaceArea</c>: the triangles' HL areas summed.</summary>
    private static float LedgeArea(IvpCompactLedge ledge)
    {
        float area = 0f;
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            (float ax, float ay, float az) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(t, 0));
            (float bx, float by, float bz) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(t, 1));
            (float cx, float cy, float cz) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(t, 2));
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
            area += 0.5f * MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        }

        return area;
    }

    /// <inheritdoc/>
    public void SetConvexGameData(ConvexHandle convex, uint gameData) => Convex(convex).ClientData = (int)gameData;

    /// <inheritdoc/>
    public void ConvexFree(ConvexHandle convex) => _convexes.Remove(convex.Value);

    /// <inheritdoc/>
    public ConvexHandle BBoxToConvex(Vec3 mins, Vec3 maxs) => Add(_build.ConvexFromVertsFast(BoxVerts(mins, maxs)));

    /// <summary><c>InitBoxVerts</c>: bit 0 picks x, bit 1 y, bit 2 z.</summary>
    private static (float X, float Y, float Z)[] BoxVerts(Vec3 mins, Vec3 maxs)
    {
        var verts = new (float X, float Y, float Z)[8];
        for (int i = 0; i < 8; i++)
        {
            verts[i] = ((i & 1) != 0 ? maxs.X : mins.X, (i & 2) != 0 ? maxs.Y : mins.Y, (i & 4) != 0 ? maxs.Z : mins.Z);
        }

        return verts;
    }

    /// <inheritdoc/>
    public PolysoupHandle PolysoupCreate()
    {
        nint h = NextHandle();
        _soups[h] = new Polysoup();
        return new PolysoupHandle(h);
    }

    /// <inheritdoc/>
    public void PolysoupDestroy(PolysoupHandle soup) => _soups.Remove(soup.Value);

    /// <inheritdoc/>
    public void PolysoupAddTriangle(PolysoupHandle soup, Vec3 a, Vec3 b, Vec3 c, int materialIndex7Bits)
    {
        Polysoup s = _soups[soup.Value];
        s.IsValid = true;
        IvpCompactLedge? ledge = _build.ConvexFromVertsFast([(a.X, a.Y, a.Z), (b.X, b.Y, b.Z), (c.X, c.Y, c.Z)]);
        if (ledge is null)
        {
            return; // "Degenerate Triangle"
        }

        SetMaterial(ledge.Bytes, 16, materialIndex7Bits);
        s.Ledges.Add(ledge);
    }

    /// <inheritdoc/>
    public CollideHandle ConvertPolysoupToCollide(PolysoupHandle soup, bool useMopp)
    {
        Polysoup s = _soups[soup.Value];
        if (!s.IsValid || s.Ledges.Count == 0)
        {
            return default;
        }

        byte[]? surface = _build.Compile(s.Ledges, buildRootConvexHull: false);
        if (surface is null)
        {
            return default;
        }

        CopyPolysoupMaterials(surface, FixPolysoupMaterialWalk);
        return Add(ManagedCollide.FromSurface(surface));
    }

    /// <summary>
    /// The material fix-up after a polysoup compile, stock's walk
    /// included: when a ledge's first triangle has material 0 the search loop leaves the triangle
    /// pointer past the ledge's last triangle, and the copy loop then writes past it.
    /// </summary>
    private static void CopyPolysoupMaterials(byte[] surface, bool fixWalk)
    {
        foreach (int at in IvpCollideQueries.LeafOffsets(surface))
        {
            int n = BinaryPrimitives.ReadInt16LittleEndian(surface.AsSpan(at + 12));
            int tri = at + 16;
            int material = GetMaterial(surface, tri);
            if (material == 0)
            {
                for (int j = 0; j < n; j++)
                {
                    if (GetMaterial(surface, tri) != 0)
                    {
                        material = GetMaterial(surface, tri);
                    }

                    tri += 16;
                }
            }

            if (fixWalk)
            {
                tri = at + 16;
            }

            for (int j = 0; j < n; j++)
            {
                if (tri + 4 <= surface.Length)
                {
                    SetMaterial(surface, tri, material);
                }

                tri += 16;
            }
        }
    }

    private static int GetMaterial(byte[] bytes, int triangleAt) =>
        (int)((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(triangleAt)) >> 24) & 0x7f);

    private static void SetMaterial(byte[] bytes, int triangleAt, int material)
    {
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(triangleAt));
        word = (word & ~(0x7fu << 24)) | ((uint)(material & 0x7f) << 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(triangleAt), word);
    }

    /// <inheritdoc/>
    public CollideHandle ConvertConvexToCollide(ReadOnlySpan<ConvexHandle> convexes) =>
        ConvertConvexToCollideParams(convexes, ConvertConvexParams.Defaults);

    /// <inheritdoc/>
    public CollideHandle ConvertConvexToCollideParams(ReadOnlySpan<ConvexHandle> convexes, ConvertConvexParams parameters)
    {
        // "NOTE: THIS FREES THE LEDGES in pConvex!!!"
        var ledges = new List<IvpCompactLedge>(convexes.Length);
        foreach (ConvexHandle h in convexes)
        {
            if (!h.IsNull && _convexes.Remove(h.Value, out IvpCompactLedge? ledge))
            {
                ledges.Add(ledge);
            }
        }

        if (ledges.Count == 0)
        {
            return default;
        }

        byte[]? surface = _build.Compile(ledges, parameters.BuildOuterConvexHull);
        if (surface is null)
        {
            return default;
        }

        var collide = ManagedCollide.FromSurface(surface);
        if (parameters.BuildDragAxisAreas)
        {
            collide.OrthoAreas = ManagedTrace.OrthographicAreas(surface, parameters.DragAreaEpsilon, _build.IsDouble, Hulls);
        }

        return Add(collide);
    }

    /// <inheritdoc/>
    public void DestroyCollide(CollideHandle collide) => _collides.Remove(collide.Value);

    /// <inheritdoc/>
    public int CollideSize(CollideHandle collide) => Collide(collide).SerializedSize;

    /// <inheritdoc/>
    public byte[] CollideWrite(CollideHandle collide) => Collide(collide).Serialize();

    /// <inheritdoc/>
    public CollideHandle UnserializeCollide(ReadOnlySpan<byte> blob, int index) => Add(ManagedCollide.Unserialize(blob, index));

    /// <inheritdoc/>
    public float CollideVolume(CollideHandle collide) => IvpCollideQueries.SurfaceVolume(Collide(collide).RequireSurface());

    /// <inheritdoc/>
    public float CollideSurfaceArea(CollideHandle collide)
    {
        float area = 0f;
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(Collide(collide).RequireSurface()))
        {
            area += LedgeArea(ledge);
        }

        return area;
    }

    /// <inheritdoc/>
    public Vec3 CollideGetExtent(CollideHandle collide, Vec3 origin, Vec3 angles, Vec3 direction)
    {
        byte[] surface = Collide(collide).RequireSurface();
        if (Placement(origin, angles) is not { } t)
        {
            (float x, float y, float z) = IvpCollideQueries.SurfaceExtent(surface, (direction.X, direction.Y, direction.Z));
            return new Vec3(x, y, z);
        }

        Vec3 best = origin;
        float bestDot = float.NegativeInfinity;
        foreach (ManagedTrace.Convex c in ManagedTrace.Convexes(surface, t, Hulls))
        {
            foreach (double[] p in c.Points)
            {
                float dot = (((float)p[0] * direction.X) + ((float)p[1] * direction.Y)) + ((float)p[2] * direction.Z);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = new Vec3((float)p[0], (float)p[1], (float)p[2]);
                }
            }
        }

        return best;
    }

    /// <inheritdoc/>
    public (Vec3 Mins, Vec3 Maxs) CollideGetAABB(CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        byte[] surface = Collide(collide).RequireSurface();
        if (Placement(origin, angles) is not { } t)
        {
            ((float X, float Y, float Z) mn, (float X, float Y, float Z) mx) = IvpCollideQueries.SurfaceAabb(surface);
            return (new Vec3(mn.X, mn.Y, mn.Z), new Vec3(mx.X, mx.Y, mx.Z));
        }

        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
        float x1 = -float.MaxValue, y1 = -float.MaxValue, z1 = -float.MaxValue;
        foreach (ManagedTrace.Convex c in ManagedTrace.Convexes(surface, t, Hulls))
        {
            foreach (double[] p in c.Points)
            {
                x0 = MathF.Min(x0, (float)p[0]);
                y0 = MathF.Min(y0, (float)p[1]);
                z0 = MathF.Min(z0, (float)p[2]);
                x1 = MathF.Max(x1, (float)p[0]);
                y1 = MathF.Max(y1, (float)p[1]);
                z1 = MathF.Max(z1, (float)p[2]);
            }
        }

        return (new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));
    }

    /// <inheritdoc/>
    public Vec3 CollideGetMassCenter(CollideHandle collide)
    {
        byte[] s = Collide(collide).RequireSurface();
        float x = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan(0));
        float y = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan(4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan(8));
        return new Vec3(x * IvpCollideQueries.IvpToHl, z * IvpCollideQueries.IvpToHl, -(y * IvpCollideQueries.IvpToHl));
    }

    /// <inheritdoc/>
    public Vec3 CollideGetOrthographicAreas(CollideHandle collide)
    {
        (float x, float y, float z) = Collide(collide).OrthoAreas;
        return new Vec3(x, y, z);
    }

    /// <inheritdoc/>
    public int CollideIndex(CollideHandle collide) => Collide(collide).Index;

    /// <inheritdoc/>
    public CollideHandle BBoxToCollide(Vec3 mins, Vec3 maxs)
    {
        if (mins == maxs)
        {
            return default;
        }

        ConvexHandle convex = BBoxToConvex(mins, maxs);
        return convex.IsNull ? default : ConvertConvexToCollide([convex]);
    }

    /// <inheritdoc/>
    public CollisionTrace TraceBox(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs, CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        if (mins != default || maxs != default)
        {
            throw new NotSupportedException(
                "the managed cooker traces rays only (TraceBox with zero extents); vbsp's path never sweeps a box");
        }

        return ManagedTrace.Ray(Collide(collide).RequireSurface(), Placement(origin, angles), start, end, Hulls);
    }

    /// <inheritdoc/>
    public CollisionTrace TraceCollide(Vec3 start, Vec3 end, CollideHandle sweep, Vec3 sweepAngles, CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        if (start != end)
        {
            throw new NotSupportedException(
                "the managed cooker answers zero-length TraceCollide (an overlap test) only; vbsp's path never sweeps a collide");
        }

        List<ManagedTrace.Convex> a = ManagedTrace.Convexes(Collide(sweep).RequireSurface(), Placement(start, sweepAngles), Hulls);
        List<ManagedTrace.Convex> b = ManagedTrace.Convexes(Collide(collide).RequireSurface(), Placement(origin, angles), Hulls);
        bool overlap = ManagedTrace.Overlaps(a, b);
        return new CollisionTrace(start, end, default, overlap ? 0f : 1f, overlap, overlap);
    }

    /// <inheritdoc/>
    public LoadedVCollide VCollideLoad(ReadOnlySpan<byte> buffer, int solidCount)
    {
        var solids = new List<CollideHandle>(solidCount);
        int position = 0;
        for (int i = 0; i < solidCount; i++)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(buffer[position..]);
            position += 4;
            solids.Add(UnserializeCollide(buffer.Slice(position, size), i));
            position += size;
        }

        nint h = NextHandle();
        _vcollides[h] = solids;
        return new LoadedVCollide(h, solids, buffer[position..].ToArray());
    }

    /// <inheritdoc/>
    public void VCollideUnload(LoadedVCollide collide)
    {
        ArgumentNullException.ThrowIfNull(collide);
        if (_vcollides.Remove(collide.Native, out List<CollideHandle>? solids))
        {
            foreach (CollideHandle s in solids)
            {
                DestroyCollide(s);
            }
        }
    }

    /// <inheritdoc/>
    public Vec3[] CreateDebugMesh(CollideHandle collide)
    {
        var verts = new List<Vec3>();
        foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(Collide(collide).RequireSurface()))
        {
            for (int t = 0; t < ledge.TriangleCount; t++)
            {
                for (int k = 2; k >= 0; k--)
                {
                    (float x, float y, float z) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(t, k));
                    verts.Add(new Vec3(x, y, z));
                }
            }
        }

        return [.. verts];
    }

    /// <inheritdoc/>
    public void WithQueryModel(CollideHandle collide, Action<ICollisionQueryModel> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        visit(new QueryModel(Collide(collide).RequireSurface()));
    }

    /// <summary><c>CCollisionQuery</c> over the surface bytes, in place.</summary>
    private sealed class QueryModel(byte[] surface) : ICollisionQueryModel
    {
        private readonly List<int> _ledges = IvpCollideQueries.LeafOffsets(surface);

        public int ConvexCount => _ledges.Count;

        public int TriangleCount(int convexIndex) => BinaryPrimitives.ReadInt16LittleEndian(surface.AsSpan(_ledges[convexIndex] + 12));

        public uint GetGameData(int convexIndex) => BinaryPrimitives.ReadUInt32LittleEndian(surface.AsSpan(_ledges[convexIndex] + 4));

        public (Vec3 A, Vec3 B, Vec3 C) GetTriangleVerts(int convexIndex, int triangleIndex)
        {
            IvpCompactLedge ledge = IvpCollideQueries.LedgeAt(surface, _ledges[convexIndex]);

            // Edges 2, 1, 0.
            (float ax, float ay, float az) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(triangleIndex, 2));
            (float bx, float by, float bz) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(triangleIndex, 1));
            (float cx, float cy, float cz) = IvpCollideQueries.HlPoint(ledge, ledge.EdgeStart(triangleIndex, 0));
            return (new Vec3(ax, ay, az), new Vec3(bx, by, bz), new Vec3(cx, cy, cz));
        }

        public int GetTriangleMaterialIndex(int convexIndex, int triangleIndex) =>
            GetMaterial(surface, _ledges[convexIndex] + 16 + (16 * triangleIndex));

        public void SetTriangleMaterialIndex(int convexIndex, int triangleIndex, int index7Bits) =>
            SetMaterial(surface, _ledges[convexIndex] + 16 + (16 * triangleIndex), index7Bits);
    }

    /// <inheritdoc/>
    public CollideHandle CreateVirtualMesh(VirtualMeshSource mesh, bool buildOuterHull)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        byte[]? hull = buildOuterHull ? ManagedVirtualMesh.BuildPackedHull(mesh, _build) : null;
        return Add(ManagedCollide.FromVirtualMesh(hull));
    }

    /// <inheritdoc/>
    public bool SupportsVirtualMesh() => true;
}
