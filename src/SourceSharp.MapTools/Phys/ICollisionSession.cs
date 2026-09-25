using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// The collision cooker's operations, callable only on the cooker thread,
/// inside <see cref="ICollisionCooker.RunAsync{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// One member per <c>IPhysicsCollision</c> method vbsp calls
/// (<c>vphysics_interface.h:179</c>), in HL units and HL axes: the
/// <c>(x,-z,y)</c> metre transform is done INSIDE vphysics
/// (<c>vphysics/convert.h:36-48</c>), so nothing here converts.
/// </para>
/// <para>
/// Synchronous on purpose. The closed library is not thread-safe at any level
/// (spike 0b: two threads corrupt its heap, and <c>ThreadContextCreate</c> is
/// <c>return this;</c>), so every call runs on one dedicated thread and a
/// whole cook -- dozens of calls whose handles only mean something to each
/// other -- is one work item on it. The implementation throws if it is used
/// from any other thread, because a handle smuggled out of the callback is
/// exactly the bug that would corrupt the heap.
/// </para>
/// </remarks>
public interface ICollisionSession
{
    /// <summary><c>ConvexFromVerts</c> (slot 2): the hull of a point cloud.</summary>
    /// <param name="points">The points.</param>
    /// <returns>The convex, or null when the points span no volume.</returns>
    ConvexHandle ConvexFromVerts(ReadOnlySpan<Vec3> points);

    /// <summary><c>ConvexFromPlanes</c> (slot 3): the intersection of half-spaces.</summary>
    /// <param name="planes">Outward-facing planes.</param>
    /// <param name="mergeDistance">Vertices closer than this are merged.</param>
    /// <returns>The convex, or null when the planes bound nothing.</returns>
    ConvexHandle ConvexFromPlanes(ReadOnlySpan<CollisionPlane> planes, float mergeDistance);

    /// <summary><c>ConvexVolume</c> (slot 4), cubic inches.</summary>
    /// <param name="convex">The convex.</param>
    /// <returns>Its volume.</returns>
    float ConvexVolume(ConvexHandle convex);

    /// <summary><c>ConvexSurfaceArea</c> (slot 5), square inches.</summary>
    /// <param name="convex">The convex.</param>
    /// <returns>Its surface area.</returns>
    float ConvexSurfaceArea(ConvexHandle convex);

    /// <summary><c>SetConvexGameData</c> (slot 6): the brush number stock stores.</summary>
    /// <param name="convex">The convex.</param>
    /// <param name="gameData">The value.</param>
    void SetConvexGameData(ConvexHandle convex, uint gameData);

    /// <summary><c>ConvexFree</c> (slot 7).</summary>
    /// <param name="convex">The convex to free.</param>
    void ConvexFree(ConvexHandle convex);

    /// <summary><c>BBoxToConvex</c> (slot 8).</summary>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <returns>The convex.</returns>
    ConvexHandle BBoxToConvex(Vec3 mins, Vec3 maxs);

    /// <summary><c>PolysoupCreate</c> (slot 11).</summary>
    /// <returns>An empty soup.</returns>
    PolysoupHandle PolysoupCreate();

    /// <summary><c>PolysoupDestroy</c> (slot 12).</summary>
    /// <param name="soup">The soup.</param>
    void PolysoupDestroy(PolysoupHandle soup);

    /// <summary><c>PolysoupAddTriangle</c> (slot 13).</summary>
    /// <param name="soup">The soup.</param>
    /// <param name="a">First corner.</param>
    /// <param name="b">Second corner.</param>
    /// <param name="c">Third corner.</param>
    /// <param name="materialIndex7Bits">The per-triangle material, 0..127.</param>
    void PolysoupAddTriangle(PolysoupHandle soup, Vec3 a, Vec3 b, Vec3 c, int materialIndex7Bits);

    /// <summary><c>ConvertPolysoupToCollide</c> (slot 14).</summary>
    /// <param name="soup">The soup; it is NOT consumed.</param>
    /// <param name="useMopp">MOPP trees; compiled out of vphysics, stock passes false.</param>
    /// <returns>The collide, or null.</returns>
    CollideHandle ConvertPolysoupToCollide(PolysoupHandle soup, bool useMopp);

    /// <summary><c>ConvertConvexToCollide</c> (slot 15). Consumes the convexes.</summary>
    /// <param name="convexes">The pieces.</param>
    /// <returns>The collide, or null.</returns>
    CollideHandle ConvertConvexToCollide(ReadOnlySpan<ConvexHandle> convexes);

    /// <summary><c>ConvertConvexToCollideParams</c> (slot 16). Consumes the convexes.</summary>
    /// <param name="convexes">The pieces.</param>
    /// <param name="parameters">How to build it.</param>
    /// <returns>The collide, or null.</returns>
    CollideHandle ConvertConvexToCollideParams(ReadOnlySpan<ConvexHandle> convexes, ConvertConvexParams parameters);

    /// <summary><c>DestroyCollide</c> (slot 17).</summary>
    /// <param name="collide">The collide.</param>
    void DestroyCollide(CollideHandle collide);

    /// <summary><c>CollideSize</c> (slot 18): the serialised size.</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>Bytes <see cref="CollideWrite"/> will produce.</returns>
    int CollideSize(CollideHandle collide);

    /// <summary><c>CollideWrite</c> (slot 19) with <c>bSwap = false</c>.</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>The blob, from its <c>VPHY</c> header; the size prefix is the caller's.</returns>
    byte[] CollideWrite(CollideHandle collide);

    /// <summary><c>UnserializeCollide</c> (slot 20): a blob back into a collide.</summary>
    /// <param name="blob">What <see cref="CollideWrite"/> produced.</param>
    /// <param name="index">The collide index to record.</param>
    /// <returns>The collide.</returns>
    CollideHandle UnserializeCollide(ReadOnlySpan<byte> blob, int index);

    /// <summary><c>CollideVolume</c> (slot 21).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>Its volume, cubic inches.</returns>
    float CollideVolume(CollideHandle collide);

    /// <summary><c>CollideSurfaceArea</c> (slot 22).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>Its area, square inches.</returns>
    float CollideSurfaceArea(CollideHandle collide);

    /// <summary><c>CollideGetExtent</c> (slot 23): the support point along a direction.</summary>
    /// <param name="collide">The collide.</param>
    /// <param name="origin">Where it is placed.</param>
    /// <param name="angles">How it is oriented (pitch, yaw, roll).</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The furthest point.</returns>
    Vec3 CollideGetExtent(CollideHandle collide, Vec3 origin, Vec3 angles, Vec3 direction);

    /// <summary><c>CollideGetAABB</c> (slot 24).</summary>
    /// <param name="collide">The collide.</param>
    /// <param name="origin">Where it is placed.</param>
    /// <param name="angles">How it is oriented.</param>
    /// <returns>The world box.</returns>
    (Vec3 Mins, Vec3 Maxs) CollideGetAABB(CollideHandle collide, Vec3 origin, Vec3 angles);

    /// <summary><c>CollideGetMassCenter</c> (slot 25).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>The mass centre.</returns>
    Vec3 CollideGetMassCenter(CollideHandle collide);

    /// <summary><c>CollideGetOrthographicAreas</c> (slot 27).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>The drag areas per axis.</returns>
    Vec3 CollideGetOrthographicAreas(CollideHandle collide);

    /// <summary><c>CollideIndex</c> (slot 29).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>The index it was unserialised with.</returns>
    int CollideIndex(CollideHandle collide);

    /// <summary><c>BBoxToCollide</c> (slot 30).</summary>
    /// <param name="mins">The box minimum.</param>
    /// <param name="maxs">The box maximum.</param>
    /// <returns>The collide.</returns>
    CollideHandle BBoxToCollide(Vec3 mins, Vec3 maxs);

    /// <summary>The first <c>TraceBox</c> overload (slot 32).</summary>
    /// <param name="start">Sweep start.</param>
    /// <param name="end">Sweep end.</param>
    /// <param name="mins">Box minimum, relative.</param>
    /// <param name="maxs">Box maximum, relative.</param>
    /// <param name="collide">What is traced against.</param>
    /// <param name="origin">Where it is placed.</param>
    /// <param name="angles">How it is oriented.</param>
    /// <returns>The trace.</returns>
    CollisionTrace TraceBox(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs, CollideHandle collide, Vec3 origin, Vec3 angles);

    /// <summary><c>TraceCollide</c> (slot 35): sweep one collide against another.</summary>
    /// <param name="start">Sweep start.</param>
    /// <param name="end">Sweep end.</param>
    /// <param name="sweep">The swept collide.</param>
    /// <param name="sweepAngles">Its orientation.</param>
    /// <param name="collide">What it is swept against.</param>
    /// <param name="origin">Where that is placed.</param>
    /// <param name="angles">How that is oriented.</param>
    /// <returns>The trace.</returns>
    CollisionTrace TraceCollide(Vec3 start, Vec3 end, CollideHandle sweep, Vec3 sweepAngles, CollideHandle collide, Vec3 origin, Vec3 angles);

    /// <summary>
    /// <c>VCollideLoad</c> (slot 37): what the engine does with each
    /// PHYSCOLLIDE record and each <c>.phy</c> -- a run of
    /// <c>{int size, blob}</c> solids followed by keydata.
    /// </summary>
    /// <param name="buffer">The solids and keydata, without any file header.</param>
    /// <param name="solidCount">How many solids the buffer holds.</param>
    /// <returns>The loaded set; release it with <see cref="VCollideUnload"/>.</returns>
    LoadedVCollide VCollideLoad(ReadOnlySpan<byte> buffer, int solidCount);

    /// <summary><c>VCollideUnload</c> (slot 38): frees every solid of a loaded set.</summary>
    /// <param name="collide">What <see cref="VCollideLoad"/> returned.</param>
    void VCollideUnload(LoadedVCollide collide);

    /// <summary><c>CreateDebugMesh</c>/<c>DestroyDebugMesh</c> (slots 41/42).</summary>
    /// <param name="collide">The collide.</param>
    /// <returns>Three vertices per triangle.</returns>
    Vec3[] CreateDebugMesh(CollideHandle collide);

    /// <summary>
    /// <c>CreateQueryModel</c> (slot 43), <paramref name="visit"/>, then
    /// <c>DestroyQueryModel</c> (slot 44).
    /// </summary>
    /// <param name="collide">The collide to query.</param>
    /// <param name="visit">What to do with the query.</param>
    void WithQueryModel(CollideHandle collide, Action<ICollisionQueryModel> visit);

    /// <summary><c>CreateVirtualMesh</c> (slot 47).</summary>
    /// <param name="mesh">The mesh the event handler serves. It must outlive the collide.</param>
    /// <param name="buildOuterHull">Build the bounding hull (stock: true).</param>
    /// <returns>The collide.</returns>
    CollideHandle CreateVirtualMesh(VirtualMeshSource mesh, bool buildOuterHull);

    /// <summary><c>SupportsVirtualMesh</c> (slot 48).</summary>
    /// <returns>Whether virtual terrain is available.</returns>
    bool SupportsVirtualMesh();

    /// <summary>The surface-property database of the same library.</summary>
    /// <remarks>
    /// A process-wide singleton inside the library (<c>g_SurfaceDatabase</c>,
    /// <c>vphysics/physics_material.cpp:171</c>) that only ever grows and never
    /// parses the same file name twice, which is why the compile uses the
    /// managed <see cref="SurfacePropertyTable"/> and this is kept for the
    /// equality assertion.
    /// </remarks>
    ISurfacePropertySession SurfaceProps { get; }
}

/// <summary>
/// <c>ICollisionQuery</c>, <c>vphysics_interface.h:305</c>: triangle-level
/// access to a collide's convexes.
/// </summary>
public interface ICollisionQueryModel
{
    /// <summary>How many convexes (slot 2).</summary>
    int ConvexCount { get; }

    /// <summary>Triangles in one convex (slot 3).</summary>
    /// <param name="convexIndex">The convex.</param>
    /// <returns>Its triangle count.</returns>
    int TriangleCount(int convexIndex);

    /// <summary>The game data stored on a convex (slot 4).</summary>
    /// <param name="convexIndex">The convex.</param>
    /// <returns>What <see cref="ICollisionSession.SetConvexGameData"/> stored.</returns>
    uint GetGameData(int convexIndex);

    /// <summary>One triangle's corners (slot 5).</summary>
    /// <param name="convexIndex">The convex.</param>
    /// <param name="triangleIndex">The triangle.</param>
    /// <returns>Its three vertices.</returns>
    (Vec3 A, Vec3 B, Vec3 C) GetTriangleVerts(int convexIndex, int triangleIndex);

    /// <summary>A triangle's material (slot 7).</summary>
    /// <param name="convexIndex">The convex.</param>
    /// <param name="triangleIndex">The triangle.</param>
    /// <returns>Its 7-bit material index.</returns>
    int GetTriangleMaterialIndex(int convexIndex, int triangleIndex);

    /// <summary>Sets a triangle's material (slot 8).</summary>
    /// <param name="convexIndex">The convex.</param>
    /// <param name="triangleIndex">The triangle.</param>
    /// <param name="index7Bits">The material, 0..127.</param>
    void SetTriangleMaterialIndex(int convexIndex, int triangleIndex, int index7Bits);
}

/// <summary>
/// <c>IPhysicsSurfaceProps</c>, <c>vphysics_interface.h:969</c>, the subset vbsp uses.
/// </summary>
public interface ISurfacePropertySession
{
    /// <summary><c>ParseSurfaceData</c> (slot 2).</summary>
    /// <param name="fileName">The file's name; a name seen before is ignored.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The property count after parsing, or 0 for a repeated file.</returns>
    int ParseSurfaceData(string fileName, string text);

    /// <summary><c>SurfacePropCount</c> (slot 3).</summary>
    int Count { get; }

    /// <summary><c>GetSurfaceIndex</c> (slot 4).</summary>
    /// <param name="name">The property name.</param>
    /// <returns>Its index, or -1.</returns>
    int GetSurfaceIndex(string name);

    /// <summary><c>GetPhysicsProperties</c> (slot 5).</summary>
    /// <param name="index">The property index.</param>
    /// <returns>Density, thickness, friction and elasticity.</returns>
    SurfacePhysics GetPhysicsProperties(int index);

    /// <summary><c>GetPropName</c> (slot 8).</summary>
    /// <param name="index">The property index.</param>
    /// <returns>Its name, or null.</returns>
    string? GetPropName(int index);
}

/// <summary>The four physics numbers <c>GetPhysicsProperties</c> returns.</summary>
/// <param name="Density">kg per cubic metre.</param>
/// <param name="Thickness">Shell thickness in inches; 0 means solid.</param>
/// <param name="Friction">Friction coefficient.</param>
/// <param name="Elasticity">Elasticity.</param>
public readonly record struct SurfacePhysics(float Density, float Thickness, float Friction, float Elasticity);
