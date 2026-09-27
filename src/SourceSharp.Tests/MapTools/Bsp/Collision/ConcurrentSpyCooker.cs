//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.Tests.MapTools.Bsp.Collision;

/// <summary>
/// A cooker around a concurrent one (the managed cooker) that records every
/// <see cref="IConcurrentConvexSession.BuildConvexes"/> call the emitter makes
/// and can run a hook before each item, and otherwise changes nothing.
/// </summary>
internal sealed class ConcurrentSpyCooker(ICollisionCooker inner) : ICollisionCooker
{
    /// <summary>Every BuildConvexes call: how many items, at what degree.</summary>
    public ConcurrentQueue<(int Count, int MaxDegree)> Calls { get; } = new();

    /// <summary>Runs before each item's build, on the item's thread.</summary>
    public Action<int>? BeforeItem { get; init; }

    public string CookerIdentity => inner.CookerIdentity;

    public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default) =>
        inner.RunAsync(s => work(new Session(s, this)), cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Session(ICollisionSession s, ConcurrentSpyCooker spy) : ICollisionSession, IConcurrentConvexSession
    {
        public ConvexHandle[] BuildConvexes(int count, Func<ICollisionSession, int, ConvexHandle> build, int maxDegree, CancellationToken cancellationToken)
        {
            spy.Calls.Enqueue((count, maxDegree));
            return ((IConcurrentConvexSession)s).BuildConvexes(
                count,
                (w, i) =>
                {
                    spy.BeforeItem?.Invoke(i);
                    return build(w, i);
                },
                maxDegree,
                cancellationToken);
        }

        public ISurfacePropertySession SurfaceProps => s.SurfaceProps;

        public ConvexHandle ConvexFromVerts(ReadOnlySpan<Vec3> points) => s.ConvexFromVerts(points);

        public ConvexHandle ConvexFromPlanes(ReadOnlySpan<CollisionPlane> planes, float mergeDistance) => s.ConvexFromPlanes(planes, mergeDistance);

        public float ConvexVolume(ConvexHandle convex) => s.ConvexVolume(convex);

        public float ConvexSurfaceArea(ConvexHandle convex) => s.ConvexSurfaceArea(convex);

        public void SetConvexGameData(ConvexHandle convex, uint gameData) => s.SetConvexGameData(convex, gameData);

        public void ConvexFree(ConvexHandle convex) => s.ConvexFree(convex);

        public ConvexHandle BBoxToConvex(Vec3 mins, Vec3 maxs) => s.BBoxToConvex(mins, maxs);

        public PolysoupHandle PolysoupCreate() => s.PolysoupCreate();

        public void PolysoupDestroy(PolysoupHandle soup) => s.PolysoupDestroy(soup);

        public void PolysoupAddTriangle(PolysoupHandle soup, Vec3 a, Vec3 b, Vec3 c, int materialIndex7Bits) => s.PolysoupAddTriangle(soup, a, b, c, materialIndex7Bits);

        public CollideHandle ConvertPolysoupToCollide(PolysoupHandle soup, bool useMopp) => s.ConvertPolysoupToCollide(soup, useMopp);

        public CollideHandle ConvertConvexToCollide(ReadOnlySpan<ConvexHandle> convexes) => s.ConvertConvexToCollide(convexes);

        public CollideHandle ConvertConvexToCollideParams(ReadOnlySpan<ConvexHandle> convexes, ConvertConvexParams parameters) => s.ConvertConvexToCollideParams(convexes, parameters);

        public void DestroyCollide(CollideHandle collide) => s.DestroyCollide(collide);

        public int CollideSize(CollideHandle collide) => s.CollideSize(collide);

        public byte[] CollideWrite(CollideHandle collide) => s.CollideWrite(collide);

        public CollideHandle UnserializeCollide(ReadOnlySpan<byte> blob, int index) => s.UnserializeCollide(blob, index);

        public float CollideVolume(CollideHandle collide) => s.CollideVolume(collide);

        public float CollideSurfaceArea(CollideHandle collide) => s.CollideSurfaceArea(collide);

        public Vec3 CollideGetExtent(CollideHandle collide, Vec3 origin, Vec3 angles, Vec3 direction) => s.CollideGetExtent(collide, origin, angles, direction);

        public (Vec3 Mins, Vec3 Maxs) CollideGetAABB(CollideHandle collide, Vec3 origin, Vec3 angles) => s.CollideGetAABB(collide, origin, angles);

        public Vec3 CollideGetMassCenter(CollideHandle collide) => s.CollideGetMassCenter(collide);

        public Vec3 CollideGetOrthographicAreas(CollideHandle collide) => s.CollideGetOrthographicAreas(collide);

        public int CollideIndex(CollideHandle collide) => s.CollideIndex(collide);

        public CollideHandle BBoxToCollide(Vec3 mins, Vec3 maxs) => s.BBoxToCollide(mins, maxs);

        public CollisionTrace TraceBox(Vec3 start, Vec3 end, Vec3 mins, Vec3 maxs, CollideHandle collide, Vec3 origin, Vec3 angles) =>
            s.TraceBox(start, end, mins, maxs, collide, origin, angles);

        public CollisionTrace TraceCollide(Vec3 start, Vec3 end, CollideHandle sweep, Vec3 sweepAngles, CollideHandle collide, Vec3 origin, Vec3 angles) =>
            s.TraceCollide(start, end, sweep, sweepAngles, collide, origin, angles);

        public LoadedVCollide VCollideLoad(ReadOnlySpan<byte> buffer, int solidCount) => s.VCollideLoad(buffer, solidCount);

        public void VCollideUnload(LoadedVCollide collide) => s.VCollideUnload(collide);

        public Vec3[] CreateDebugMesh(CollideHandle collide) => s.CreateDebugMesh(collide);

        public void WithQueryModel(CollideHandle collide, Action<ICollisionQueryModel> visit) => s.WithQueryModel(collide, visit);

        public CollideHandle CreateVirtualMesh(VirtualMeshSource mesh, bool buildOuterHull) => s.CreateVirtualMesh(mesh, buildOuterHull);

        public bool SupportsVirtualMesh() => s.SupportsVirtualMesh();
    }
}
