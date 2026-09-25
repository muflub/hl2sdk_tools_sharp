using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// A managed stand-in for the cooker, for the unit tier: it understands AXIAL
/// boxes only (every convex it is given must be six axis-aligned planes, or
/// eight-plus points whose bounds it takes), records every call, and "cooks"
/// to a deterministic text blob describing what it was given.
/// </summary>
/// <remarks>
/// It exists so the emitter's own logic -- which brushes, which planes and
/// shrinks, materials, masses, water, framing -- is tested without the
/// closed library. It is NOT an oracle for the library: the native tier
/// checks the real one.
/// </remarks>
internal sealed class FakeCollisionCooker : ICollisionCooker
{
    public FakeCollisionCooker(bool supportsVirtualMesh = true) => Session = new FakeCollisionSession(supportsVirtualMesh);

    public FakeCollisionSession Session { get; }

    public string CookerIdentity => "fake";

    public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(work(Session));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeCollisionSession : ICollisionSession
{
    private readonly bool _supportsVirtualMesh;
    private readonly Dictionary<nint, FakeConvex> _convexes = [];
    private readonly Dictionary<nint, FakeCollide> _collides = [];
    private readonly Dictionary<nint, List<(Vec3 A, Vec3 B, Vec3 C, int Material)>> _soups = [];
    private nint _next = 1;

    public FakeCollisionSession(bool supportsVirtualMesh) => _supportsVirtualMesh = supportsVirtualMesh;

    /// <summary>Every ConvexFromPlanes call's planes, in order.</summary>
    public List<CollisionPlane[]> PlaneCalls { get; } = [];

    /// <summary>Every ConvertConvexToCollideParams call's parameters.</summary>
    public List<ConvertConvexParams> ParamsCalls { get; } = [];

    /// <summary>Every mesh CreateVirtualMesh was handed.</summary>
    public List<VirtualMeshSource> VirtualMeshes { get; } = [];

    /// <summary>How many collides are alive (a leak check).</summary>
    public int LiveCollides => _collides.Count;

    public ISurfacePropertySession SurfaceProps => throw new NotSupportedException();

    public ConvexHandle ConvexFromVerts(ReadOnlySpan<Vec3> points)
    {
        if (points.Length < 4)
        {
            return default;
        }

        Vec3 mins = points[0], maxs = points[0];
        foreach (Vec3 p in points)
        {
            mins = new(MathF.Min(mins.X, p.X), MathF.Min(mins.Y, p.Y), MathF.Min(mins.Z, p.Z));
            maxs = new(MathF.Max(maxs.X, p.X), MathF.Max(maxs.Y, p.Y), MathF.Max(maxs.Z, p.Z));
        }

        return Add(new FakeConvex(mins, maxs, "verts"));
    }

    public ConvexHandle ConvexFromPlanes(ReadOnlySpan<CollisionPlane> planes, float mergeDistance)
    {
        PlaneCalls.Add(planes.ToArray());
        float[] lo = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
        float[] hi = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        foreach (CollisionPlane p in planes)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (p.Normal[axis] == 1f)
                {
                    hi[axis] = MathF.Min(hi[axis], p.Dist);
                }
                else if (p.Normal[axis] == -1f)
                {
                    lo[axis] = MathF.Max(lo[axis], -p.Dist);
                }
            }
        }

        if (lo.Concat(hi).Any(float.IsInfinity) || lo[0] >= hi[0] || lo[1] >= hi[1] || lo[2] >= hi[2])
        {
            return default;
        }

        return Add(new FakeConvex(new(lo[0], lo[1], lo[2]), new(hi[0], hi[1], hi[2]), "planes"));
    }

    public float ConvexVolume(ConvexHandle convex) => _convexes[convex.Value].Volume;

    public float ConvexSurfaceArea(ConvexHandle convex) => throw new NotSupportedException();

    public void SetConvexGameData(ConvexHandle convex, uint gameData) => _convexes[convex.Value].GameData = gameData;

    public void ConvexFree(ConvexHandle convex) => _convexes.Remove(convex.Value);

    public ConvexHandle BBoxToConvex(Vec3 mins, Vec3 maxs) => Add(new FakeConvex(mins, maxs, "bbox"));

    public PolysoupHandle PolysoupCreate()
    {
        nint h = _next++;
        _soups[h] = [];
        return new PolysoupHandle(h);
    }

    public void PolysoupDestroy(PolysoupHandle soup) => _soups.Remove(soup.Value);

    public void PolysoupAddTriangle(PolysoupHandle soup, Vec3 a, Vec3 b, Vec3 c, int materialIndex7Bits) =>
        _soups[soup.Value].Add((a, b, c, materialIndex7Bits));

    public CollideHandle ConvertPolysoupToCollide(PolysoupHandle soup, bool useMopp)
    {
        StringBuilder text = new("soup");
        foreach (var t in _soups[soup.Value])
        {
            text.Append(' ').Append(t.Material);
        }

        return AddCollide(new FakeCollide([], text.ToString()));
    }

    public CollideHandle ConvertConvexToCollide(ReadOnlySpan<ConvexHandle> convexes) => Convert(convexes, "convex");

    public CollideHandle ConvertConvexToCollideParams(ReadOnlySpan<ConvexHandle> convexes, ConvertConvexParams parameters)
    {
        ParamsCalls.Add(parameters);
        return Convert(convexes, "params");
    }

    public void DestroyCollide(CollideHandle collide) => _collides.Remove(collide.Value);

    public int CollideSize(CollideHandle collide) => Bytes(collide).Length;

    public byte[] CollideWrite(CollideHandle collide) => Bytes(collide);

    public CollideHandle UnserializeCollide(ReadOnlySpan<byte> blob, int index) => throw new NotSupportedException();

    public float CollideVolume(CollideHandle collide) => _collides[collide.Value].Convexes.Sum(c => c.Volume);

    public float CollideSurfaceArea(CollideHandle collide) => throw new NotSupportedException();

    public Vec3 CollideGetExtent(CollideHandle collide, Vec3 origin, Vec3 angles, Vec3 direction)
    {
        // The support point of the union of boxes along the direction.
        Vec3 best = default;
        float bestDot = float.NegativeInfinity;
        foreach (FakeConvex c in _collides[collide.Value].Convexes)
        {
            foreach (Vec3 corner in c.Corners())
            {
                float d = Vec3.Dot(corner, direction);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = corner;
                }
            }
        }

        return best + origin;
    }

    public (Vec3 Mins, Vec3 Maxs) CollideGetAABB(CollideHandle collide, Vec3 origin, Vec3 angles) => throw new NotSupportedException();

    public Vec3 CollideGetMassCenter(CollideHandle collide) => throw new NotSupportedException();

    public Vec3 CollideGetOrthographicAreas(CollideHandle collide) => throw new NotSupportedException();

    public int CollideIndex(CollideHandle collide) => throw new NotSupportedException();

    public CollideHandle BBoxToCollide(Vec3 mins, Vec3 maxs) => throw new NotSupportedException();

    public CollisionTrace TraceBox(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs, CollideHandle collide, Vec3 origin, Vec3 angles) =>
        throw new NotSupportedException();

    public CollisionTrace TraceCollide(Vec3 start, Vec3 end, CollideHandle sweep, Vec3 sweepAngles, CollideHandle collide, Vec3 origin, Vec3 angles) =>
        throw new NotSupportedException();

    public LoadedVCollide VCollideLoad(ReadOnlySpan<byte> buffer, int solidCount) => throw new NotSupportedException();

    public void VCollideUnload(LoadedVCollide collide) => throw new NotSupportedException();

    public Vec3[] CreateDebugMesh(CollideHandle collide) => throw new NotSupportedException();

    public void WithQueryModel(CollideHandle collide, Action<ICollisionQueryModel> visit) =>
        visit(new FakeQuery(_collides[collide.Value]));

    public CollideHandle CreateVirtualMesh(VirtualMeshSource mesh, bool buildOuterHull)
    {
        VirtualMeshes.Add(mesh);
        return AddCollide(new FakeCollide([], "mesh " + string.Join(',', mesh.Indices)));
    }

    public bool SupportsVirtualMesh() => _supportsVirtualMesh;

    private ConvexHandle Add(FakeConvex convex)
    {
        nint h = _next++;
        _convexes[h] = convex;
        return new ConvexHandle(h);
    }

    private CollideHandle AddCollide(FakeCollide collide)
    {
        nint h = _next++;
        _collides[h] = collide;
        return new CollideHandle(h);
    }

    private CollideHandle Convert(ReadOnlySpan<ConvexHandle> convexes, string kind)
    {
        if (convexes.Length == 0)
        {
            return default;
        }

        List<FakeConvex> list = [];
        foreach (ConvexHandle c in convexes)
        {
            if (c.IsNull)
            {
                continue; // vphysics skips a null convex, physics_collide.cpp:1176
            }

            list.Add(_convexes[c.Value]);
            _convexes.Remove(c.Value); // consumed
        }

        return list.Count == 0 ? default : AddCollide(new FakeCollide(list, kind));
    }

    private byte[] Bytes(CollideHandle collide)
    {
        FakeCollide c = _collides[collide.Value];
        StringBuilder text = new(c.Kind);
        foreach (FakeConvex convex in c.Convexes)
        {
            text.Append(' ').Append(convex.GameData).Append(':').Append(convex.Mins).Append(convex.Maxs)
                .Append(" m=").Append(string.Join(',', convex.Materials));
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    internal sealed class FakeConvex
    {
        public FakeConvex(Vec3 mins, Vec3 maxs, string source)
        {
            Mins = mins;
            Maxs = maxs;
            Source = source;
        }

        public Vec3 Mins { get; }

        public Vec3 Maxs { get; }

        public string Source { get; }

        public uint GameData { get; set; }

        public int[] Materials { get; } = new int[12];

        public float Volume => (Maxs.X - Mins.X) * (Maxs.Y - Mins.Y) * (Maxs.Z - Mins.Z);

        public IEnumerable<Vec3> Corners()
        {
            for (int i = 0; i < 8; i++)
            {
                yield return new Vec3(
                    (i & 1) != 0 ? Maxs.X : Mins.X,
                    (i & 2) != 0 ? Maxs.Y : Mins.Y,
                    (i & 4) != 0 ? Maxs.Z : Mins.Z);
            }
        }

        /// <summary>
        /// Two triangles per face, in face order +X -X +Y -Y +Z -Z, wound so
        /// that <c>TriangleNormal</c>'s <c>(p2-p0) x (p1-p0)</c> points OUT --
        /// the "clockwise, normal points out" convention of <c>ivp.cpp:1236</c>.
        /// </summary>
        public (Vec3, Vec3, Vec3) Triangle(int t)
        {
            int face = t / 2;
            int axis = face / 2;
            bool positive = face % 2 == 0;
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            float fixedValue = positive ? Maxs[axis] : Mins[axis];

            Vec3 P(float a, float b)
            {
                float[] c = new float[3];
                c[axis] = fixedValue;
                c[u] = a;
                c[v] = b;
                return new Vec3(c[0], c[1], c[2]);
            }

            Vec3 q0 = P(Mins[u], Mins[v]), q1 = P(Maxs[u], Mins[v]), q2 = P(Maxs[u], Maxs[v]), q3 = P(Mins[u], Maxs[v]);

            // (p2-p0) x (p1-p0) points along +axis for (q0, q3, q2) since u x v = +axis.
            (Vec3, Vec3, Vec3) first = positive ? (q0, q3, q2) : (q0, q2, q3);
            (Vec3, Vec3, Vec3) second = positive ? (q0, q2, q1) : (q0, q1, q2);
            return t % 2 == 0 ? first : second;
        }
    }

    private sealed record FakeCollide(List<FakeConvex> Convexes, string Kind);

    private sealed class FakeQuery(FakeCollide collide) : ICollisionQueryModel
    {
        public int ConvexCount => collide.Convexes.Count;

        public int TriangleCount(int convexIndex) => 12;

        public uint GetGameData(int convexIndex) => collide.Convexes[convexIndex].GameData;

        public (Vec3 A, Vec3 B, Vec3 C) GetTriangleVerts(int convexIndex, int triangleIndex) =>
            collide.Convexes[convexIndex].Triangle(triangleIndex);

        public int GetTriangleMaterialIndex(int convexIndex, int triangleIndex) =>
            collide.Convexes[convexIndex].Materials[triangleIndex];

        public void SetTriangleMaterialIndex(int convexIndex, int triangleIndex, int index7Bits) =>
            collide.Convexes[convexIndex].Materials[triangleIndex] = index7Bits;
    }
}
