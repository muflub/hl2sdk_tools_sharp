using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// The vtable slots of <c>IPhysicsCollision</c> this port binds, derived from
/// The reference implementation's declaration order.
/// </summary>
/// <remarks>
/// The interface has no base class and its first member is a virtual
/// destructor, which the Itanium ABI turns into TWO slots (the complete-object
/// and deleting destructors, 0 and 1). Everything after takes one slot each
/// in source order, overloads included. Spike 0b checked the mapping
/// numerically; <c>VPhysicsBindingTests</c> re-checks every bound slot with a
/// known answer, so a shift fails loudly instead of returning a plausible
/// number.
/// </remarks>
internal static class CollisionSlot
{
    public const int ConvexFromVerts = 2;
    public const int ConvexFromPlanes = 3;
    public const int ConvexVolume = 4;
    public const int ConvexSurfaceArea = 5;
    public const int SetConvexGameData = 6;
    public const int ConvexFree = 7;
    public const int BBoxToConvex = 8;
    public const int PolysoupCreate = 11;
    public const int PolysoupDestroy = 12;
    public const int PolysoupAddTriangle = 13;
    public const int ConvertPolysoupToCollide = 14;
    public const int ConvertConvexToCollide = 15;
    public const int ConvertConvexToCollideParams = 16;
    public const int DestroyCollide = 17;
    public const int CollideSize = 18;
    public const int CollideWrite = 19;
    public const int UnserializeCollide = 20;
    public const int CollideVolume = 21;
    public const int CollideSurfaceArea = 22;
    public const int CollideGetExtent = 23;
    public const int CollideGetAABB = 24;
    public const int CollideGetMassCenter = 25;
    public const int CollideGetOrthographicAreas = 27;
    public const int CollideIndex = 29;
    public const int BBoxToCollide = 30;
    public const int TraceBox = 32;
    public const int TraceCollide = 35;
    public const int VCollideLoad = 37;
    public const int VCollideUnload = 38;
    public const int CreateDebugMesh = 41;
    public const int DestroyDebugMesh = 42;
    public const int CreateQueryModel = 43;
    public const int DestroyQueryModel = 44;
    public const int ThreadContextCreate = 45;
    public const int CreateVirtualMesh = 47;
    public const int SupportsVirtualMesh = 48;
}

/// <summary>
/// <c>ICollisionQuery</c>'s slots: a virtual
/// destructor (two slots), then source order.
/// </summary>
internal static class QuerySlot
{
    public const int ConvexCount = 2;
    public const int TriangleCount = 3;
    public const int GetGameData = 4;
    public const int GetTriangleVerts = 5;
    public const int GetTriangleMaterialIndex = 7;
    public const int SetTriangleMaterialIndex = 8;
}

/// <summary>
/// <c>IPhysicsSurfaceProps</c>' slots.
/// </summary>
internal static class SurfacePropsSlot
{
    public const int ParseSurfaceData = 2;
    public const int SurfacePropCount = 3;
    public const int GetSurfaceIndex = 4;
    public const int GetPhysicsProperties = 5;
    public const int GetPropName = 8;
}

/// <summary>
/// The byte offsets inside <c>trace_t</c> (the reference implementation's
/// <c>CBaseTrace</c>) this port reads. The buffer handed to vphysics is
/// larger than the whole struct, so a trace writing its game fields cannot
/// overrun it.
/// </summary>
internal static class TraceLayout
{
    public const int BufferSize = 256;
    public const int StartPos = 0;
    public const int EndPos = 12;
    public const int PlaneNormal = 24;
    public const int Fraction = 44;
    public const int AllSolid = 54;
    public const int StartSolid = 55;
}

/// <summary><c>convertconvexparams_t</c>'s wire layout (16 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct ConvertConvexParamsNative
{
    [FieldOffset(0)] public byte BuildOuterConvexHull;
    [FieldOffset(1)] public byte BuildDragAxisAreas;
    [FieldOffset(2)] public byte BuildOptimizedTraceTables;
    [FieldOffset(4)] public float DragAreaEpsilon;
    [FieldOffset(8)] public nint ForcedOuterHull;
}

/// <summary><c>virtualmeshlist_t</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 32 + (VirtualMeshSource.MaxVirtualTriangles * 3 * 2))]
internal unsafe struct VirtualMeshListNative
{
    [FieldOffset(0)] public Vec3* Verts;
    [FieldOffset(8)] public int IndexCount;
    [FieldOffset(12)] public int TriangleCount;
    [FieldOffset(16)] public int VertexCount;
    [FieldOffset(20)] public int SurfacePropsIndex;
    [FieldOffset(24)] public nint Hull;
    [FieldOffset(32)] public fixed ushort Indices[VirtualMeshSource.MaxVirtualTriangles * 3];
}

/// <summary><c>virtualmeshtrianglelist_t</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 4 + (VirtualMeshSource.MaxVirtualTriangles * 3 * 2))]
internal unsafe struct VirtualMeshTriangleListNative
{
    [FieldOffset(0)] public int TriangleCount;
    [FieldOffset(4)] public fixed ushort TriangleIndices[VirtualMeshSource.MaxVirtualTriangles * 3];
}

/// <summary><c>virtualmeshparams_t</c>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct VirtualMeshParamsNative
{
    [FieldOffset(0)] public nint MeshEventHandler;
    [FieldOffset(8)] public nint UserData;
    [FieldOffset(16)] public byte BuildOuterHull;
}

/// <summary>
/// A C++ <c>IVirtualMeshEvent</c> object built in native memory, whose three
/// virtual methods are managed callbacks: <c>CDispMeshEvent</c>.
/// </summary>
/// <remarks>
/// vphysics keeps the handler pointer in the collide it returns and calls it
/// again when the mesh is queried, so the object, its vertex copy and the
/// GC handle all live until the collide is destroyed or the cooker is
/// disposed. The interface has no virtual destructor, so the vtable is
/// exactly the three methods in declaration order.
/// </remarks>
internal sealed unsafe class VirtualMeshEvent : IDisposable
{
    private readonly GCHandle _self;
    private nint _object;
    private Vec3* _verts;

    public VirtualMeshEvent(VirtualMeshSource mesh)
    {
        Mesh = mesh;

        _verts = (Vec3*)NativeMemory.Alloc((nuint)Math.Max(1, mesh.Vertices.Length), (nuint)sizeof(Vec3));
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            _verts[i] = mesh.Vertices[i];
        }

        // [vtable pointer][GetVirtualMesh][GetWorldspaceBounds][GetTrianglesInSphere]
        nint* block = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        block[0] = (nint)(block + 1);
        block[1] = (nint)(delegate* unmanaged<nint, nint, VirtualMeshListNative*, void>)&GetVirtualMesh;
        block[2] = (nint)(delegate* unmanaged<nint, nint, Vec3*, Vec3*, void>)&GetWorldspaceBounds;
        block[3] = (nint)(delegate* unmanaged<nint, nint, Vec3*, float, VirtualMeshTriangleListNative*, void>)&GetTrianglesInSphere;
        _object = (nint)block;

        _self = GCHandle.Alloc(this);
    }

    public VirtualMeshSource Mesh { get; }

    public nint NativeObject => _object;

    public nint UserData => GCHandle.ToIntPtr(_self);

    public void Dispose()
    {
        if (_object != 0)
        {
            NativeMemory.Free((void*)_object);
            _object = 0;
        }

        if (_verts is not null)
        {
            NativeMemory.Free(_verts);
            _verts = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static VirtualMeshEvent From(nint userData) =>
        (VirtualMeshEvent)GCHandle.FromIntPtr(userData).Target!;

    /// <summary><c>CDispMeshEvent::GetVirtualMesh</c>.</summary>
    [UnmanagedCallersOnly]
    private static void GetVirtualMesh(nint self, nint userData, VirtualMeshListNative* list)
    {
        VirtualMeshEvent handler = From(userData);
        ushort[] indices = handler.Mesh.Indices;

        list->Verts = handler._verts;
        list->IndexCount = indices.Length;
        list->TriangleCount = indices.Length / 3;
        list->VertexCount = handler.Mesh.Vertices.Length;
        list->SurfacePropsIndex = 0; // "doesn't matter here, reset at runtime"
        list->Hull = 0;

        int count = Math.Min(indices.Length, VirtualMeshSource.MaxVirtualTriangles * 3);
        for (int i = 0; i < count; i++)
        {
            list->Indices[i] = indices[i];
        }
    }

    /// <summary><c>CDispMeshEvent::GetWorldspaceBounds</c>.</summary>
    [UnmanagedCallersOnly]
    private static void GetWorldspaceBounds(nint self, nint userData, Vec3* mins, Vec3* maxs)
    {
        VirtualMeshEvent handler = From(userData);

        // ClearBounds: 99999 / -99999.
        float minX = 99999f, minY = 99999f, minZ = 99999f;
        float maxX = -99999f, maxY = -99999f, maxZ = -99999f;

        foreach (Vec3 v in handler.Mesh.Vertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
            if (v.Z > maxZ) maxZ = v.Z;
        }

        *mins = new Vec3(minX, minY, minZ);
        *maxs = new Vec3(maxX, maxY, maxZ);
    }

    /// <summary><c>CDispMeshEvent::GetTrianglesInSphere</c>.</summary>
    [UnmanagedCallersOnly]
    private static void GetTrianglesInSphere(
        nint self, nint userData, Vec3* center, float radius, VirtualMeshTriangleListNative* list)
    {
        VirtualMeshEvent handler = From(userData);
        ushort[] indices = handler.Mesh.Indices;

        list->TriangleCount = indices.Length / 3;
        int count = Math.Min(indices.Length, VirtualMeshSource.MaxVirtualTriangles * 3);
        for (int i = 0; i < count; i++)
        {
            list->TriangleIndices[i] = indices[i];
        }
    }
}

/// <summary>
/// The loaded library: three shared objects and the two interfaces vbsp takes
/// from them.
/// </summary>
internal sealed unsafe class VPhysicsModule
{
    public const string CollisionInterfaceName = "VPhysicsCollision007";
    public const string SurfacePropsInterfaceName = "VPhysicsSurfaceProps001";

    private VPhysicsModule(nint collision, nint surfaceProps, delegate* unmanaged<byte*, int*, nint> createInterface)
    {
        Collision = collision;
        SurfaceProps = surfaceProps;
        CreateInterfaceFunction = createInterface;
    }

    public nint Collision { get; }

    public nint SurfaceProps { get; }

    public delegate* unmanaged<byte*, int*, nint> CreateInterfaceFunction { get; }

    /// <summary>
    /// Loads <c>libtier0.so</c>, <c>libvstdlib.so</c> and <c>vphysics.so</c>
    /// from one directory, in that order, and takes the two interfaces.
    /// </summary>
    /// <param name="hostDirectory">The OS path of the game's <c>bin/linux64</c>.</param>
    /// <returns>The module.</returns>
    /// <exception cref="VPhysicsLoadException">A library or an interface could not be had.</exception>
    public static VPhysicsModule Load(string hostDirectory)
    {
        nint vphysics = 0;
        foreach (string name in new[] { "libtier0.so", "libvstdlib.so", "vphysics.so" })
        {
            string path = hostDirectory.TrimEnd('/') + "/" + name;
            try
            {
                vphysics = NativeLibrary.Load(path);
            }
            catch (DllNotFoundException e)
            {
                throw new VPhysicsLoadException(
                    $"could not load {path}: {e.Message}. vphysics.so and libvstdlib.so name "
                    + "libtier0.so in DT_NEEDED and libtier0.so carries no DT_SONAME, so the loader "
                    + "only finds it through LD_LIBRARY_PATH, which glibc reads once at process start. "
                    + $"Launch the host with LD_LIBRARY_PATH={hostDirectory} (setting it from inside "
                    + "the process does not work; re-executing does).",
                    e);
            }
        }

        if (!NativeLibrary.TryGetExport(vphysics, "CreateInterface", out nint export))
        {
            throw new VPhysicsLoadException($"{hostDirectory}/vphysics.so exports no CreateInterface.");
        }

        delegate* unmanaged<byte*, int*, nint> create = (delegate* unmanaged<byte*, int*, nint>)export;
        nint collision = CreateInterface(create, CollisionInterfaceName);
        nint surfaceProps = CreateInterface(create, SurfacePropsInterfaceName);

        if (collision == 0 || surfaceProps == 0)
        {
            throw new VPhysicsLoadException(
                $"{hostDirectory}/vphysics.so does not provide {CollisionInterfaceName} and "
                + $"{SurfacePropsInterfaceName}; it is not the reference build's library.");
        }

        return new VPhysicsModule(collision, surfaceProps, create);
    }

    /// <summary>Calls the factory by name.</summary>
    /// <param name="create">The factory.</param>
    /// <param name="name">The interface version string.</param>
    /// <returns>The interface, or zero.</returns>
    public static nint CreateInterface(delegate* unmanaged<byte*, int*, nint> create, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        int returnCode = 0;
        fixed (byte* p = bytes)
        {
            return create(p, &returnCode);
        }
    }

    /// <summary>The function in one slot of an object's vtable.</summary>
    /// <param name="self">The object.</param>
    /// <param name="slot">The slot.</param>
    /// <returns>The function pointer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Slot(nint self, int slot) => (*(nint**)self)[slot];
}

/// <summary>The physics library could not be loaded or is not the expected one.</summary>
public sealed class VPhysicsLoadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VPhysicsLoadException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public VPhysicsLoadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The loader's error.</param>
    public VPhysicsLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
