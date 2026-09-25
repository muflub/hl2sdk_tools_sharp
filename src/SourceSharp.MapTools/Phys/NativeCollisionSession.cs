using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// <see cref="ICollisionSession"/> over the real library's vtables.
/// </summary>
/// <remarks>
/// Every call goes through <see cref="Check"/>, which refuses any thread but
/// the cooker's. The calling convention is SysV x64: <c>this</c> first,
/// <c>const Vector&amp;</c> as a pointer, <c>bool</c> as one byte, and the
/// 12-byte <c>Vector</c> return of <c>CollideGetExtent</c> in xmm0/xmm1 rather
/// than through a hidden pointer, because <c>Vector</c> is trivially copyable
/// in this tree (its copy constructor is commented out at
/// <c>mathlib/vector.h:187</c>).
/// </remarks>
internal sealed unsafe class NativeCollisionSession : ICollisionSession, ISurfacePropertySession
{
    private readonly VPhysicsModule _module;
    private readonly int _thread;
    private readonly Dictionary<nint, VirtualMeshEvent> _meshEvents = [];

    private readonly FloatEnv _library;

    /// <summary>Creates the session.</summary>
    /// <param name="module">The loaded library.</param>
    /// <param name="cookerThread">The only thread allowed to call.</param>
    /// <param name="library">The floating-point environment the library's load left (FTZ/DAZ).</param>
    public NativeCollisionSession(VPhysicsModule module, int cookerThread, FloatEnv library)
    {
        _module = module;
        _thread = cookerThread;
        _library = library;
    }

    /// <summary>Checks the thread and enters the library's floating-point environment for one call.</summary>
    /// <returns>The scope; disposing it restores the caller's environment.</returns>
    internal NativeScope Enter()
    {
        Check();
        return new NativeScope(_library);
    }

    private nint C => _module.Collision;

    public ISurfacePropertySession SurfaceProps => this;

    public ConvexHandle ConvexFromVerts(ReadOnlySpan<Vec3> points)
    {
        using NativeScope scope = Enter();
        nint[] pointers = new nint[Math.Max(1, points.Length)];
        fixed (Vec3* p = points)
        fixed (nint* pp = pointers)
        {
            for (int i = 0; i < points.Length; i++)
            {
                pp[i] = (nint)(p + i);
            }

            var f = (delegate* unmanaged<nint, nint*, int, nint>)VPhysicsModule.Slot(C, CollisionSlot.ConvexFromVerts);
            return new ConvexHandle(f(C, pp, points.Length));
        }
    }

    public ConvexHandle ConvexFromPlanes(ReadOnlySpan<CollisionPlane> planes, float mergeDistance)
    {
        using NativeScope scope = Enter();
        fixed (CollisionPlane* p = planes)
        {
            var f = (delegate* unmanaged<nint, float*, int, float, nint>)VPhysicsModule.Slot(C, CollisionSlot.ConvexFromPlanes);
            return new ConvexHandle(f(C, (float*)p, planes.Length, mergeDistance));
        }
    }

    public float ConvexVolume(ConvexHandle convex)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, float>)VPhysicsModule.Slot(C, CollisionSlot.ConvexVolume);
        return f(C, convex.Value);
    }

    public float ConvexSurfaceArea(ConvexHandle convex)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, float>)VPhysicsModule.Slot(C, CollisionSlot.ConvexSurfaceArea);
        return f(C, convex.Value);
    }

    public void SetConvexGameData(ConvexHandle convex, uint gameData)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, uint, void>)VPhysicsModule.Slot(C, CollisionSlot.SetConvexGameData);
        f(C, convex.Value, gameData);
    }

    public void ConvexFree(ConvexHandle convex)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, void>)VPhysicsModule.Slot(C, CollisionSlot.ConvexFree);
        f(C, convex.Value);
    }

    public ConvexHandle BBoxToConvex(Vec3 mins, Vec3 maxs)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, Vec3*, Vec3*, nint>)VPhysicsModule.Slot(C, CollisionSlot.BBoxToConvex);
        return new ConvexHandle(f(C, &mins, &maxs));
    }

    public PolysoupHandle PolysoupCreate()
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint>)VPhysicsModule.Slot(C, CollisionSlot.PolysoupCreate);
        return new PolysoupHandle(f(C));
    }

    public void PolysoupDestroy(PolysoupHandle soup)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, void>)VPhysicsModule.Slot(C, CollisionSlot.PolysoupDestroy);
        f(C, soup.Value);
    }

    public void PolysoupAddTriangle(PolysoupHandle soup, Vec3 a, Vec3 b, Vec3 c, int materialIndex7Bits)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, Vec3*, Vec3*, Vec3*, int, void>)VPhysicsModule.Slot(C, CollisionSlot.PolysoupAddTriangle);
        f(C, soup.Value, &a, &b, &c, materialIndex7Bits);
    }

    public CollideHandle ConvertPolysoupToCollide(PolysoupHandle soup, bool useMopp)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, byte, nint>)VPhysicsModule.Slot(C, CollisionSlot.ConvertPolysoupToCollide);
        return new CollideHandle(f(C, soup.Value, useMopp ? (byte)1 : (byte)0));
    }

    public CollideHandle ConvertConvexToCollide(ReadOnlySpan<ConvexHandle> convexes)
    {
        using NativeScope scope = Enter();
        fixed (ConvexHandle* p = convexes)
        {
            var f = (delegate* unmanaged<nint, nint*, int, nint>)VPhysicsModule.Slot(C, CollisionSlot.ConvertConvexToCollide);
            return new CollideHandle(f(C, (nint*)p, convexes.Length));
        }
    }

    public CollideHandle ConvertConvexToCollideParams(ReadOnlySpan<ConvexHandle> convexes, ConvertConvexParams parameters)
    {
        using NativeScope scope = Enter();
        ConvertConvexParamsNative native = new()
        {
            BuildOuterConvexHull = parameters.BuildOuterConvexHull ? (byte)1 : (byte)0,
            BuildDragAxisAreas = parameters.BuildDragAxisAreas ? (byte)1 : (byte)0,
            BuildOptimizedTraceTables = parameters.BuildOptimizedTraceTables ? (byte)1 : (byte)0,
            DragAreaEpsilon = parameters.DragAreaEpsilon,
            ForcedOuterHull = 0,
        };

        fixed (ConvexHandle* p = convexes)
        {
            var f = (delegate* unmanaged<nint, nint*, int, ConvertConvexParamsNative*, nint>)VPhysicsModule.Slot(C, CollisionSlot.ConvertConvexToCollideParams);
            return new CollideHandle(f(C, (nint*)p, convexes.Length, &native));
        }
    }

    public void DestroyCollide(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, void>)VPhysicsModule.Slot(C, CollisionSlot.DestroyCollide);
        f(C, collide.Value);

        if (_meshEvents.Remove(collide.Value, out VirtualMeshEvent? handler))
        {
            handler.Dispose();
        }
    }

    public int CollideSize(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, int>)VPhysicsModule.Slot(C, CollisionSlot.CollideSize);
        return f(C, collide.Value);
    }

    public byte[] CollideWrite(CollideHandle collide)
    {
        int size = CollideSize(collide);
        byte[] bytes = new byte[size];
        using NativeScope scope = Enter();
        fixed (byte* p = bytes)
        {
            var f = (delegate* unmanaged<nint, byte*, nint, byte, int>)VPhysicsModule.Slot(C, CollisionSlot.CollideWrite);
            int written = f(C, p, collide.Value, 0);
            if (written != size)
            {
                throw new InvalidOperationException(
                    $"CollideWrite wrote {written} bytes but CollideSize promised {size}.");
            }
        }

        return bytes;
    }

    public CollideHandle UnserializeCollide(ReadOnlySpan<byte> blob, int index)
    {
        using NativeScope scope = Enter();
        fixed (byte* p = blob)
        {
            var f = (delegate* unmanaged<nint, byte*, int, int, nint>)VPhysicsModule.Slot(C, CollisionSlot.UnserializeCollide);
            return new CollideHandle(f(C, p, blob.Length, index));
        }
    }

    public float CollideVolume(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, float>)VPhysicsModule.Slot(C, CollisionSlot.CollideVolume);
        return f(C, collide.Value);
    }

    public float CollideSurfaceArea(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, float>)VPhysicsModule.Slot(C, CollisionSlot.CollideSurfaceArea);
        return f(C, collide.Value);
    }

    public Vec3 CollideGetExtent(CollideHandle collide, Vec3 origin, Vec3 angles, Vec3 direction)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, Vec3*, Vec3*, Vec3*, Vec3>)VPhysicsModule.Slot(C, CollisionSlot.CollideGetExtent);
        return f(C, collide.Value, &origin, &angles, &direction);
    }

    public (Vec3 Mins, Vec3 Maxs) CollideGetAABB(CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        using NativeScope scope = Enter();
        Vec3 mins = default, maxs = default;
        var f = (delegate* unmanaged<nint, Vec3*, Vec3*, nint, Vec3*, Vec3*, void>)VPhysicsModule.Slot(C, CollisionSlot.CollideGetAABB);
        f(C, &mins, &maxs, collide.Value, &origin, &angles);
        return (mins, maxs);
    }

    public Vec3 CollideGetMassCenter(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        Vec3 center = default;
        var f = (delegate* unmanaged<nint, nint, Vec3*, void>)VPhysicsModule.Slot(C, CollisionSlot.CollideGetMassCenter);
        f(C, collide.Value, &center);
        return center;
    }

    public Vec3 CollideGetOrthographicAreas(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, Vec3>)VPhysicsModule.Slot(C, CollisionSlot.CollideGetOrthographicAreas);
        return f(C, collide.Value);
    }

    public int CollideIndex(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, int>)VPhysicsModule.Slot(C, CollisionSlot.CollideIndex);
        return f(C, collide.Value);
    }

    public CollideHandle BBoxToCollide(Vec3 mins, Vec3 maxs)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, Vec3*, Vec3*, nint>)VPhysicsModule.Slot(C, CollisionSlot.BBoxToCollide);
        return new CollideHandle(f(C, &mins, &maxs));
    }

    public CollisionTrace TraceBox(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs, CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        using NativeScope scope = Enter();
        byte* trace = stackalloc byte[TraceLayout.BufferSize];
        new Span<byte>(trace, TraceLayout.BufferSize).Clear();
        var f = (delegate* unmanaged<nint, Vec3*, Vec3*, Vec3*, Vec3*, nint, Vec3*, Vec3*, byte*, void>)VPhysicsModule.Slot(C, CollisionSlot.TraceBox);
        f(C, &start, &end, &mins, &maxs, collide.Value, &origin, &angles, trace);
        return ReadTrace(trace);
    }

    public CollisionTrace TraceCollide(Vec3 start, Vec3 end, CollideHandle sweep, Vec3 sweepAngles, CollideHandle collide, Vec3 origin, Vec3 angles)
    {
        using NativeScope scope = Enter();
        byte* trace = stackalloc byte[TraceLayout.BufferSize];
        new Span<byte>(trace, TraceLayout.BufferSize).Clear();
        var f = (delegate* unmanaged<nint, Vec3*, Vec3*, nint, Vec3*, nint, Vec3*, Vec3*, byte*, void>)VPhysicsModule.Slot(C, CollisionSlot.TraceCollide);
        f(C, &start, &end, sweep.Value, &sweepAngles, collide.Value, &origin, &angles, trace);
        return ReadTrace(trace);
    }

    public LoadedVCollide VCollideLoad(ReadOnlySpan<byte> buffer, int solidCount)
    {
        using NativeScope scope = Enter();

        // vcollide_t: {ushort solidCount:15, isPacked:1; ushort descSize; CPhysCollide **solids; char *pKeyValues}.
        byte* vc = (byte*)NativeMemory.AllocZeroed(VCollideSize);
        fixed (byte* p = buffer)
        {
            var f = (delegate* unmanaged<nint, byte*, int, byte*, int, byte, void>)VPhysicsModule.Slot(C, CollisionSlot.VCollideLoad);
            f(C, vc, solidCount, p, buffer.Length, 0);
        }

        int count = *(ushort*)vc & 0x7FFF;
        nint* solids = *(nint**)(vc + 8);
        CollideHandle[] handles = new CollideHandle[count];
        for (int i = 0; i < count; i++)
        {
            handles[i] = new CollideHandle(solids[i]);
        }

        // VCollideLoad copies exactly the bytes after the last solid (physics_collide.cpp:1650).
        int position = 0;
        for (int i = 0; i < solidCount && position + 4 <= buffer.Length; i++)
        {
            position += 4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer[position..]);
        }

        byte* keys = *(byte**)(vc + 16);
        byte[] keyData = keys is null || position > buffer.Length
            ? []
            : new ReadOnlySpan<byte>(keys, buffer.Length - position).ToArray();

        return new LoadedVCollide((nint)vc, handles, keyData);
    }

    public void VCollideUnload(LoadedVCollide collide)
    {
        ArgumentNullException.ThrowIfNull(collide);
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint, void>)VPhysicsModule.Slot(C, CollisionSlot.VCollideUnload);
        f(C, collide.Native);
        NativeMemory.Free((void*)collide.Native);
    }

    private const int VCollideSize = 24;

    public Vec3[] CreateDebugMesh(CollideHandle collide)
    {
        using NativeScope scope = Enter();
        Vec3* verts = null;
        var create = (delegate* unmanaged<nint, nint, Vec3**, int>)VPhysicsModule.Slot(C, CollisionSlot.CreateDebugMesh);
        int count = create(C, collide.Value, &verts);
        Vec3[] result = new ReadOnlySpan<Vec3>(verts, count).ToArray();
        var destroy = (delegate* unmanaged<nint, int, Vec3*, void>)VPhysicsModule.Slot(C, CollisionSlot.DestroyDebugMesh);
        destroy(C, count, verts);
        return result;
    }

    public void WithQueryModel(CollideHandle collide, Action<ICollisionQueryModel> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        nint query;
        using (NativeScope scope = Enter())
        {
            var create = (delegate* unmanaged<nint, nint, nint>)VPhysicsModule.Slot(C, CollisionSlot.CreateQueryModel);
            query = create(C, collide.Value);
        }

        // The visitor is managed code: it runs under the caller's IEEE
        // environment, and each query call enters the library's for itself.
        try
        {
            visit(new NativeQueryModel(this, query));
        }
        finally
        {
            using NativeScope scope = Enter();
            var destroy = (delegate* unmanaged<nint, nint, void>)VPhysicsModule.Slot(C, CollisionSlot.DestroyQueryModel);
            destroy(C, query);
        }
    }

    public CollideHandle CreateVirtualMesh(VirtualMeshSource mesh, bool buildOuterHull)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        using NativeScope scope = Enter();
        VirtualMeshEvent handler = new(mesh);
        VirtualMeshParamsNative parameters = new()
        {
            MeshEventHandler = handler.NativeObject,
            UserData = handler.UserData,
            BuildOuterHull = buildOuterHull ? (byte)1 : (byte)0,
        };

        var f = (delegate* unmanaged<nint, VirtualMeshParamsNative*, nint>)VPhysicsModule.Slot(C, CollisionSlot.CreateVirtualMesh);
        nint collide = f(C, &parameters);
        if (collide == 0)
        {
            handler.Dispose();
        }
        else
        {
            _meshEvents[collide] = handler;
        }

        return new CollideHandle(collide);
    }

    public bool SupportsVirtualMesh()
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, byte>)VPhysicsModule.Slot(C, CollisionSlot.SupportsVirtualMesh);
        return f(C) != 0;
    }

    /// <summary><c>ThreadContextCreate</c> (slot 45), kept for the fact that it is <c>return this;</c>.</summary>
    /// <returns>The pointer it returns.</returns>
    public nint ThreadContextCreate()
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, nint>)VPhysicsModule.Slot(C, CollisionSlot.ThreadContextCreate);
        return f(C);
    }

    /// <summary>The raw collision interface pointer, for the same fact.</summary>
    public nint CollisionInterface => C;

    /// <summary>Releases every virtual-mesh handler still alive.</summary>
    public void ReleaseMeshEvents()
    {
        foreach (VirtualMeshEvent handler in _meshEvents.Values)
        {
            handler.Dispose();
        }

        _meshEvents.Clear();
    }

    // ---- IPhysicsSurfaceProps ------------------------------------------------

    private nint S => _module.SurfaceProps;

    public int ParseSurfaceData(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);
        using NativeScope scope = Enter();
        byte[] name = Encoding.UTF8.GetBytes(fileName + "\0");
        byte[] body = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* n = name)
        fixed (byte* b = body)
        {
            var f = (delegate* unmanaged<nint, byte*, byte*, int>)VPhysicsModule.Slot(S, SurfacePropsSlot.ParseSurfaceData);
            return f(S, n, b);
        }
    }

    public int Count
    {
        get
        {
            using NativeScope scope = Enter();
            var f = (delegate* unmanaged<nint, int>)VPhysicsModule.Slot(S, SurfacePropsSlot.SurfacePropCount);
            return f(S);
        }
    }

    public int GetSurfaceIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using NativeScope scope = Enter();
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            var f = (delegate* unmanaged<nint, byte*, int>)VPhysicsModule.Slot(S, SurfacePropsSlot.GetSurfaceIndex);
            return f(S, p);
        }
    }

    public SurfacePhysics GetPhysicsProperties(int index)
    {
        using NativeScope scope = Enter();
        float density = 0, thickness = 0, friction = 0, elasticity = 0;
        var f = (delegate* unmanaged<nint, int, float*, float*, float*, float*, void>)VPhysicsModule.Slot(S, SurfacePropsSlot.GetPhysicsProperties);
        f(S, index, &density, &thickness, &friction, &elasticity);
        return new SurfacePhysics(density, thickness, friction, elasticity);
    }

    public string? GetPropName(int index)
    {
        using NativeScope scope = Enter();
        var f = (delegate* unmanaged<nint, int, byte*>)VPhysicsModule.Slot(S, SurfacePropsSlot.GetPropName);
        byte* name = f(S, index);
        return name is null ? null : Marshal.PtrToStringUTF8((nint)name);
    }

    // ---- helpers ---------------------------------------------------------------

    internal void Check()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException(
                "a collision session was used off the cooker thread. vphysics is not thread-safe "
                + "(spike 0b: two threads corrupt its heap); keep every call inside RunAsync's callback.");
        }
    }

    private static CollisionTrace ReadTrace(byte* trace) => new(
        *(Vec3*)(trace + TraceLayout.StartPos),
        *(Vec3*)(trace + TraceLayout.EndPos),
        *(Vec3*)(trace + TraceLayout.PlaneNormal),
        *(float*)(trace + TraceLayout.Fraction),
        trace[TraceLayout.AllSolid] != 0,
        trace[TraceLayout.StartSolid] != 0);

    private sealed class NativeQueryModel : ICollisionQueryModel
    {
        private readonly NativeCollisionSession _session;
        private readonly nint _q;

        public NativeQueryModel(NativeCollisionSession session, nint query)
        {
            _session = session;
            _q = query;
        }

        public int ConvexCount
        {
            get
            {
                _session.Check();
                var f = (delegate* unmanaged<nint, int>)VPhysicsModule.Slot(_q, QuerySlot.ConvexCount);
                return f(_q);
            }
        }

        public int TriangleCount(int convexIndex)
        {
            using NativeScope scope = _session.Enter();
            var f = (delegate* unmanaged<nint, int, int>)VPhysicsModule.Slot(_q, QuerySlot.TriangleCount);
            return f(_q, convexIndex);
        }

        public uint GetGameData(int convexIndex)
        {
            using NativeScope scope = _session.Enter();
            var f = (delegate* unmanaged<nint, int, uint>)VPhysicsModule.Slot(_q, QuerySlot.GetGameData);
            return f(_q, convexIndex);
        }

        public (Vec3 A, Vec3 B, Vec3 C) GetTriangleVerts(int convexIndex, int triangleIndex)
        {
            using NativeScope scope = _session.Enter();
            Vec3* verts = stackalloc Vec3[3];
            var f = (delegate* unmanaged<nint, int, int, Vec3*, void>)VPhysicsModule.Slot(_q, QuerySlot.GetTriangleVerts);
            f(_q, convexIndex, triangleIndex, verts);
            return (verts[0], verts[1], verts[2]);
        }

        public int GetTriangleMaterialIndex(int convexIndex, int triangleIndex)
        {
            using NativeScope scope = _session.Enter();
            var f = (delegate* unmanaged<nint, int, int, int>)VPhysicsModule.Slot(_q, QuerySlot.GetTriangleMaterialIndex);
            return f(_q, convexIndex, triangleIndex);
        }

        public void SetTriangleMaterialIndex(int convexIndex, int triangleIndex, int index7Bits)
        {
            using NativeScope scope = _session.Enter();
            var f = (delegate* unmanaged<nint, int, int, int, void>)VPhysicsModule.Slot(_q, QuerySlot.SetTriangleMaterialIndex);
            f(_q, convexIndex, triangleIndex, index7Bits);
        }
    }
}
