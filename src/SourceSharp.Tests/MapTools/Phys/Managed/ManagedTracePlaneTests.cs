//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Phys.Managed.Qhull;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The leaf convexes the collide queries test against build their planes (a qhull hull per
/// leaf) only when a ray test asks for them, on the thread's reused qhull storage.
/// </summary>
public class ManagedTracePlaneTests
{
    private static readonly CollisionPlane[] Box =
    [
        new(new Vec3(1, 0, 0), 16), new(new Vec3(-1, 0, 0), 16), new(new Vec3(0, 1, 0), 8),
        new(new Vec3(0, -1, 0), 8), new(new Vec3(0, 0, 1), 4), new(new Vec3(0, 0, -1), 4),
    ];

    /// <summary>The compact surface of a collide cooked by a session's own calls.</summary>
    private static async Task<byte[]> SurfaceAsync(Func<ICollisionSession, CollideHandle> make)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        byte[] vphy = await cooker.RunAsync(s => s.CollideWrite(make(s)));
        return IvpCollideQueries.Surface(vphy).ToArray();
    }

    [Fact]
    public async Task PointQueriesBuildNoLeafHulls()
    {
        byte[] surface = await SurfaceAsync(s => s.ConvertConvexToCollide([s.ConvexFromPlanes(Box, 0f)]));
        var hulls = new QhullSession();
        List<ManagedTrace.Convex> convexes = ManagedTrace.Convexes(surface, null, hulls);
        Assert.NotEmpty(convexes);

        // The points are there for the overlap and bounds queries; no hull has been built.
        Assert.All(convexes, c => Assert.True(c.Points.Length >= 4));
        Assert.All(convexes, c => Assert.False(c.PlanesBuilt));
        Assert.Equal(0, hulls.Pool.Retained.Facets);

        // Asking for the planes builds the hull once, on the storage handed in.
        ManagedTrace.Convex box = convexes[0];
        Assert.False(box.Flat);
        Assert.True(box.PlanesBuilt);
        Assert.True(hulls.Pool.Retained.Facets > 0);
        (double[] N, double D)[] planes = box.Planes;
        Assert.Same(planes, box.Planes);
        Assert.Equal(6, planes.Length);
        Assert.All(planes, p => Assert.Equal(1.0, Math.Abs(p.N[0]) + Math.Abs(p.N[1]) + Math.Abs(p.N[2]), 12));
        Assert.Contains(planes, p => p.N[2] > 0.5 && Math.Abs(p.D - 4) < 1e-4);
    }

    [Fact]
    public async Task APlanesFirstConvexAgreesWithAFlatFirstOne()
    {
        // Either property may be the first asked; both give the same answer.
        byte[] surface = await SurfaceAsync(s => s.ConvertConvexToCollide([s.ConvexFromPlanes(Box, 0f)]));
        ManagedTrace.Convex a = ManagedTrace.Convexes(surface, null, new QhullSession())[0];
        ManagedTrace.Convex b = ManagedTrace.Convexes(surface, null, new QhullSession())[0];
        (double[] N, double D)[] planesFirst = a.Planes;
        bool flatFirst = b.Flat;
        Assert.Equal(flatFirst, a.Flat);
        Assert.Equal(planesFirst.Select(p => (p.N[0], p.N[1], p.N[2], p.D)), b.Planes.Select(p => (p.N[0], p.N[1], p.N[2], p.D)));
    }

    [Fact]
    public async Task AFlatLedgeFallsBackToItsTrianglePlanes()
    {
        // A single polysoup triangle cooks to a two-sided flat ledge: its points have no hull,
        // so the planes are the ledge's own triangles, facing away from each other.
        byte[] surface = await SurfaceAsync(s =>
        {
            PolysoupHandle soup = s.PolysoupCreate();
            s.PolysoupAddTriangle(soup, new Vec3(0, 0, 0), new Vec3(32, 0, 0), new Vec3(0, 32, 0), 0);
            return s.ConvertPolysoupToCollide(soup, false);
        });
        ManagedTrace.Convex flat = Assert.Single(ManagedTrace.Convexes(surface, null, new QhullSession()));
        Assert.True(flat.Flat);
        Assert.Equal(2, flat.Planes.Length);
        double dot = (flat.Planes[0].N[0] * flat.Planes[1].N[0]) + (flat.Planes[0].N[1] * flat.Planes[1].N[1]) + (flat.Planes[0].N[2] * flat.Planes[1].N[2]);
        Assert.Equal(-1.0, dot, 6);
    }
}
