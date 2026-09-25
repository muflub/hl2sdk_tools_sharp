using System.Security.Cryptography;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// A known answer per bound vtable slot, so a shifted slot fails loudly
/// instead of answering a plausible number (plan §7; memory
/// <c>a-one-sided-slot-answers-a-plausible-zero</c>).
/// </summary>
/// <remarks>
/// The shapes are chosen so a neighbouring slot cannot produce the answer:
/// volume and area differ for the same cube (slots 4 and 5), and the
/// non-cubic box has a different extent per axis so an AABB returned in the
/// wrong order is wrong.
/// </remarks>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class VPhysicsBindingTests
{
    private static readonly Vec3 Zero = Vec3.Zero;

    private readonly VPhysicsCookerFixture _fixture;

    public VPhysicsBindingTests(VPhysicsCookerFixture fixture) => _fixture = fixture;

    private ICollisionCooker Cooker => _fixture.Cooker;

    /// <summary>A box as six outward planes, the shape <c>ivp.cpp:529</c> hands the cooker.</summary>
    internal static CollisionPlane[] BoxPlanes(Vec3 mins, Vec3 maxs) =>
    [
        new(new Vec3(1, 0, 0), maxs.X),
        new(new Vec3(-1, 0, 0), -mins.X),
        new(new Vec3(0, 1, 0), maxs.Y),
        new(new Vec3(0, -1, 0), -mins.Y),
        new(new Vec3(0, 0, 1), maxs.Z),
        new(new Vec3(0, 0, -1), -mins.Z),
    ];

    internal static Vec3[] BoxCorners(Vec3 mins, Vec3 maxs)
    {
        Vec3[] corners = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3(
                (i & 1) != 0 ? maxs.X : mins.X,
                (i & 2) != 0 ? maxs.Y : mins.Y,
                (i & 4) != 0 ? maxs.Z : mins.Z);
        }

        return corners;
    }

    private static readonly Vec3 CubeMins = new(-16, -16, -16);
    private static readonly Vec3 CubeMaxs = new(16, 16, 16);
    private static readonly Vec3 BoxMins = new(-4, -12, -32);
    private static readonly Vec3 BoxMaxs = new(4, 12, 32);

    private Task<T> WithCube<T>(Func<ICollisionSession, CollideHandle, T> work) =>
        Cooker.RunAsync(s =>
        {
            CollideHandle collide = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f)]);
            try
            {
                return work(s, collide);
            }
            finally
            {
                s.DestroyCollide(collide);
            }
        });

    private static readonly float Denormal = BitConverter.Int32BitsToSingle(0x00000100);

    [VPhysicsNativeFact]
    public void LoadingTheLibrarySetsFlushToZeroForItsCalls()
    {
        // crtfastmath's constructor, run by dlopen on the loading thread; the
        // cooker keeps that environment for native calls (FloatEnvironment).
        Assert.True(_fixture.Cooker.LibraryFlushesDenormals);
    }

    [VPhysicsNativeFact]
    public void TheCookerThreadIsIeeeBetweenNativeCalls()
    {
        Assert.False(_fixture.Cooker.CookerThreadFlushesDenormals);
    }

    [VPhysicsNativeFact]
    public async Task ManagedCallbackCodeRunsIeee()
    {
        Assert.False(await Cooker.RunAsync(_ => VPhysicsCollisionCooker.FlushesDenormals(Denormal)));
    }

    [Fact]
    public void TheFtzProbeMeasuresTheThreadNotTheJit()
    {
        // The Release-only failure this pins: the probe's denormal was
        // materialised as a compile-time float constant, and a thread in
        // FTZ/DAZ at JIT time flushed it to +0.0 in the shared native code.
        // The first-ever call therefore happens with FTZ+DAZ forced on, so the
        // JIT compiles the probe in the very environment the shipped cooker
        // thread compiles it in; if the answer afterwards still tracks MXCSR,
        // no compilation context can pin it.
        (bool underFtz, bool afterRestore) =
            new DenormalProbe().RunUnderForcedFtz();
        Assert.True(underFtz);
        // The call that used to lie: same compiled code, thread restored to
        // IEEE -- an honest probe must answer false here even though the JIT
        // saw FTZ/DAZ when it compiled it.
        Assert.False(afterRestore);
    }

    [VPhysicsNativeFact]
    public async Task AManagedThreadIsLeftIeee()
    {
        // MXCSR is per thread: the library's load must not leak off the cooker thread.
        await Cooker.RunAsync(_ => 0);

        Assert.False(VPhysicsCollisionCooker.FlushesDenormals(Denormal));
    }

    [VPhysicsNativeFact]
    public void TheCookerIdentityNamesThePinnedBuild()
    {
        Assert.Contains("md5=" + PinnedVPhysics.PinnedMd5, Cooker.CookerIdentity, StringComparison.Ordinal);
    }

    [VPhysicsNativeFact]
    public async Task ConvexFromPlanesThenConvexVolumeIsTheCubesVolume()
    {
        float volume = await Cooker.RunAsync(s =>
        {
            ConvexHandle c = s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f);
            float v = s.ConvexVolume(c);
            s.ConvexFree(c);
            return v;
        });

        Assert.Equal(32768f, volume);
    }

    [VPhysicsNativeFact]
    public async Task ConvexSurfaceAreaIsTheCubesArea()
    {
        float area = await Cooker.RunAsync(s =>
        {
            ConvexHandle c = s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f);
            float a = s.ConvexSurfaceArea(c);
            s.ConvexFree(c);
            return a;
        });

        Assert.Equal(6144f, area);
    }

    [VPhysicsNativeFact]
    public async Task ConvexFromVertsIsTheHullOfTheCorners()
    {
        float volume = await Cooker.RunAsync(s =>
        {
            ConvexHandle c = s.ConvexFromVerts(BoxCorners(BoxMins, BoxMaxs));
            float v = s.ConvexVolume(c);
            s.ConvexFree(c);
            return v;
        });

        Assert.Equal(8f * 24f * 64f, volume, 0.5f);
    }

    [VPhysicsNativeFact]
    public async Task BBoxToConvexHasTheBoxsVolume()
    {
        float volume = await Cooker.RunAsync(s =>
        {
            ConvexHandle c = s.BBoxToConvex(BoxMins, BoxMaxs);
            float v = s.ConvexVolume(c);
            s.ConvexFree(c);
            return v;
        });

        Assert.Equal(12288f, volume, 0.5f);
    }

    [VPhysicsNativeFact]
    public async Task SetConvexGameDataIsReadBackByTheQueryModel()
    {
        uint gameData = await Cooker.RunAsync(s =>
        {
            ConvexHandle c = s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f);
            s.SetConvexGameData(c, 1234);
            CollideHandle collide = s.ConvertConvexToCollide([c]);
            uint read = 0;
            s.WithQueryModel(collide, q => read = q.GetGameData(0));
            s.DestroyCollide(collide);
            return read;
        });

        Assert.Equal(1234u, gameData);
    }

    [VPhysicsNativeFact]
    public async Task CollideVolumeAndAreaSurviveConversion()
    {
        (float volume, float area) = await WithCube((s, c) => (s.CollideVolume(c), s.CollideSurfaceArea(c)));

        Assert.Equal(32768f, volume);
        Assert.Equal(6144f, area);
    }

    [VPhysicsNativeFact]
    public async Task CollideGetAABBReturnsEachAxisOfAnAsymmetricBox()
    {
        (Vec3 mins, Vec3 maxs) = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(BoxMins, BoxMaxs), 0f)]);
            var box = s.CollideGetAABB(c, Zero, Zero);
            s.DestroyCollide(c);
            return box;
        });

        Assert.Equal(BoxMins, mins);
        Assert.Equal(BoxMaxs, maxs);
    }

    [VPhysicsNativeFact]
    public async Task CollideGetAABBAppliesTheOrigin()
    {
        (Vec3 mins, Vec3 maxs) = await WithCube((s, c) => s.CollideGetAABB(c, new Vec3(100, 200, 300), Zero));

        Assert.Equal(new Vec3(84, 184, 284), mins);
        Assert.Equal(new Vec3(116, 216, 316), maxs);
    }

    [VPhysicsNativeFact]
    public async Task CollideGetExtentReturnsTheSupportPointByValue()
    {
        Vec3 plusX = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(BoxMins, BoxMaxs), 0f)]);
            Vec3 e = s.CollideGetExtent(c, Zero, Zero, new Vec3(0, 0, 1));
            s.DestroyCollide(c);
            return e;
        });

        // Along +Z the support point's z is the box's top, 32; x and y are a corner.
        Assert.Equal(32f, plusX.Z, 0.001f);
        Assert.Equal(4f, MathF.Abs(plusX.X), 0.001f);
        Assert.Equal(12f, MathF.Abs(plusX.Y), 0.001f);
    }

    [VPhysicsNativeFact]
    public async Task CollideGetMassCenterIsTheBoxCentre()
    {
        Vec3 center = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(new Vec3(0, 0, 0), new Vec3(32, 64, 128)), 0f)]);
            Vec3 m = s.CollideGetMassCenter(c);
            s.DestroyCollide(c);
            return m;
        });

        Assert.Equal(16f, center.X, 0.01f);
        Assert.Equal(32f, center.Y, 0.01f);
        Assert.Equal(64f, center.Z, 0.01f);
    }

    [VPhysicsNativeFact]
    public async Task OrthographicAreasAreOneWithoutDragAxisAreas()
    {
        Vec3 areas = await WithCube((s, c) => s.CollideGetOrthographicAreas(c));

        Assert.Equal(new Vec3(1, 1, 1), areas);
    }

    [VPhysicsNativeFact]
    public async Task ConvertConvexToCollideParamsBuildsDragAxisAreas()
    {
        // A cube cut in half along a vertical diagonal: seen down Z it covers
        // half its bounding square, seen down X all of it. The drag areas are
        // coverage fractions (a cube's are exactly (1,1,1), the no-drag default
        // too), so only this wedge can tell the parameter block was read.
        Vec3 areas = await Cooker.RunAsync(s =>
        {
            CollisionPlane[] planes =
            [
                .. BoxPlanes(CubeMins, CubeMaxs),
                new(new Vec3(0.70710677f, 0.70710677f, 0f), 0f),
            ];
            ConvexHandle convex = s.ConvexFromPlanes(planes, 0f);
            CollideHandle c = s.ConvertConvexToCollideParams(
                [convex], ConvertConvexParams.Defaults with { BuildDragAxisAreas = true, DragAreaEpsilon = 1f });
            Vec3 a = s.CollideGetOrthographicAreas(c);
            s.DestroyCollide(c);
            return a;
        });

        Assert.Equal(0.5f, areas.Z / areas.X, 0.1f);
    }

    [VPhysicsNativeFact]
    public async Task ConvertConvexToCollideParamsWithoutDragAreasLeavesTheDefault()
    {
        Vec3 areas = await Cooker.RunAsync(s =>
        {
            CollisionPlane[] planes =
            [
                .. BoxPlanes(CubeMins, CubeMaxs),
                new(new Vec3(0.70710677f, 0.70710677f, 0f), 0f),
            ];
            CollideHandle c = s.ConvertConvexToCollideParams([s.ConvexFromPlanes(planes, 0f)], ConvertConvexParams.Defaults);
            Vec3 a = s.CollideGetOrthographicAreas(c);
            s.DestroyCollide(c);
            return a;
        });

        Assert.Equal(new Vec3(1, 1, 1), areas);
    }

    [VPhysicsNativeFact]
    public async Task CollideWriteWritesCollideSizeBytesFromTheVphyHeader()
    {
        (int size, byte[] bytes) = await WithCube((s, c) => (s.CollideSize(c), s.CollideWrite(c)));

        Assert.Equal(440, size);
        Assert.Equal(size, bytes.Length);
        Assert.Equal("VPHY"u8.ToArray(), bytes[..4]);
    }

    [VPhysicsNativeFact]
    public async Task UnserializeCollideRoundTripsVolumeAndRecordsTheIndex()
    {
        (float volume, int index) = await WithCube((s, c) =>
        {
            byte[] blob = s.CollideWrite(c);
            CollideHandle back = s.UnserializeCollide(blob, 5);
            var answer = (s.CollideVolume(back), s.CollideIndex(back));
            s.DestroyCollide(back);
            return answer;
        });

        Assert.Equal(32768f, volume);
        Assert.Equal(5, index);
    }

    private static byte[] VCollideBuffer(params byte[][] parts)
    {
        // {int size, blob} per solid, then the keydata: a PHYSCOLLIDE record's body.
        List<byte> buffer = [];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            buffer.AddRange(BitConverter.GetBytes(parts[i].Length));
            buffer.AddRange(parts[i]);
        }

        buffer.AddRange(parts[^1]);
        return [.. buffer];
    }

    [VPhysicsNativeFact]
    public async Task VCollideLoadReadsEverySolid()
    {
        (int count, float first, float second) = await Cooker.RunAsync(s =>
        {
            byte[] cube = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f)]));
            byte[] box = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(BoxMins, BoxMaxs), 0f)]));
            LoadedVCollide loaded = s.VCollideLoad(VCollideBuffer(cube, box, "solid {}\0"u8.ToArray()), 2);
            var answer = (loaded.Solids.Count, s.CollideVolume(loaded.Solids[0]), s.CollideVolume(loaded.Solids[1]));
            s.VCollideUnload(loaded);
            return answer;
        });

        Assert.Equal(2, count);
        Assert.Equal(32768f, first);
        Assert.Equal(12288f, second);
    }

    [VPhysicsNativeFact]
    public async Task VCollideLoadCopiesTheKeydataAfterTheLastSolid()
    {
        byte[] keys = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f)]);
            byte[] cube = s.CollideWrite(c);
            s.DestroyCollide(c);
            LoadedVCollide loaded = s.VCollideLoad(VCollideBuffer(cube, "solid {\n}\n\0"u8.ToArray()), 1);
            s.VCollideUnload(loaded);
            return loaded.KeyData;
        });

        Assert.Equal("solid {\n}\n\0"u8.ToArray(), keys);
    }

    [VPhysicsNativeFact]
    public async Task VCollideLoadNumbersTheSolids()
    {
        // CPhysCollide::UnserializeFromBuffer( ..., i, ... ): the index is the solid's position.
        int index = await Cooker.RunAsync(s =>
        {
            byte[] cube = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(CubeMins, CubeMaxs), 0f)]));
            LoadedVCollide loaded = s.VCollideLoad(VCollideBuffer(cube, cube, [0]), 2);
            int i = s.CollideIndex(loaded.Solids[1]);
            s.VCollideUnload(loaded);
            return i;
        });

        Assert.Equal(1, index);
    }

    [VPhysicsNativeFact]
    public async Task BBoxToCollideHasTheBoxsBounds()
    {
        (Vec3 mins, Vec3 maxs) = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.BBoxToCollide(BoxMins, BoxMaxs);
            var box = s.CollideGetAABB(c, Zero, Zero);
            s.DestroyCollide(c);
            return box;
        });

        Assert.Equal(BoxMins, mins);
        Assert.Equal(BoxMaxs, maxs);
    }

    [VPhysicsNativeFact]
    public async Task TraceBoxAlongXStopsAtTheCubesFace()
    {
        CollisionTrace trace = await WithCube((s, c) =>
            s.TraceBox(new Vec3(-100, 0, 0), new Vec3(100, 0, 0), Zero, Zero, c, Zero, Zero));

        Assert.Equal(84f / 200f, trace.Fraction, 0.001f);
        // A trace backs off by DIST_EPSILON (1/32), so the end sits just outside.
        Assert.Equal(-16f, trace.EndPosition.X, 0.05f);
        Assert.Equal(new Vec3(-1, 0, 0), trace.PlaneNormal);
        Assert.False(trace.StartSolid);
    }

    [VPhysicsNativeFact]
    public async Task TraceBoxThatMissesGoesAllTheWay()
    {
        CollisionTrace trace = await WithCube((s, c) =>
            s.TraceBox(new Vec3(-100, 50, 0), new Vec3(100, 50, 0), Zero, Zero, c, Zero, Zero));

        Assert.Equal(1f, trace.Fraction);
    }

    [VPhysicsNativeFact]
    public async Task TraceCollideStartingInsideIsStartSolid()
    {
        CollisionTrace trace = await WithCube((s, c) =>
        {
            CollideHandle small = s.BBoxToCollide(new Vec3(-2, -2, -2), new Vec3(2, 2, 2));
            CollisionTrace t = s.TraceCollide(Zero, Zero, small, Zero, c, Zero, Zero);
            s.DestroyCollide(small);
            return t;
        });

        Assert.True(trace.StartSolid);
    }

    [VPhysicsNativeFact]
    public async Task TraceCollideDisjointIsNotStartSolid()
    {
        CollisionTrace trace = await WithCube((s, c) =>
        {
            CollideHandle small = s.BBoxToCollide(new Vec3(-2, -2, -2), new Vec3(2, 2, 2));
            CollisionTrace t = s.TraceCollide(new Vec3(100, 0, 0), new Vec3(100, 0, 0), small, Zero, c, Zero, Zero);
            s.DestroyCollide(small);
            return t;
        });

        Assert.False(trace.StartSolid);
    }

    [VPhysicsNativeFact]
    public async Task CreateDebugMeshIsTwelveTrianglesOnTheCube()
    {
        Vec3[] verts = await WithCube((s, c) => s.CreateDebugMesh(c));

        Assert.Equal(36, verts.Length);
        Assert.All(verts, v => Assert.Equal(16f, MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z))), 0.01f));
    }

    [VPhysicsNativeFact]
    public async Task QueryModelCountsOneConvexOfTwelveTriangles()
    {
        (int convexes, int triangles) = await WithCube((s, c) =>
        {
            (int, int) counts = default;
            s.WithQueryModel(c, q => counts = (q.ConvexCount, q.TriangleCount(0)));
            return counts;
        });

        Assert.Equal(1, convexes);
        Assert.Equal(12, triangles);
    }

    [VPhysicsNativeFact]
    public async Task QueryModelTriangleVertsLieOnTheCube()
    {
        (Vec3 a, Vec3 b, Vec3 c) = await WithCube((s, collide) =>
        {
            (Vec3, Vec3, Vec3) tri = default;
            s.WithQueryModel(collide, q => tri = q.GetTriangleVerts(0, 0));
            return tri;
        });

        foreach (Vec3 v in new[] { a, b, c })
        {
            Assert.Equal(16f, MathF.Abs(v.X), 0.01f);
            Assert.Equal(16f, MathF.Abs(v.Y), 0.01f);
            Assert.Equal(16f, MathF.Abs(v.Z), 0.01f);
        }
    }

    [VPhysicsNativeFact]
    public async Task QueryModelTriangleMaterialRoundTrips()
    {
        (int before, int after) = await WithCube((s, c) =>
        {
            (int, int) m = default;
            s.WithQueryModel(c, q =>
            {
                int b = q.GetTriangleMaterialIndex(0, 3);
                q.SetTriangleMaterialIndex(0, 3, 17);
                m = (b, q.GetTriangleMaterialIndex(0, 3));
            });
            return m;
        });

        Assert.Equal(0, before);
        Assert.Equal(17, after);
    }

    [VPhysicsNativeFact]
    public async Task PolysoupOfTheCubesTrianglesHasTheCubesBounds()
    {
        (Vec3 mins, Vec3 maxs) = await Cooker.RunAsync(s =>
        {
            CollideHandle cube = s.ConvertConvexToCollide([s.ConvexFromPlanes(BoxPlanes(BoxMins, BoxMaxs), 0f)]);
            Vec3[] tris = s.CreateDebugMesh(cube);
            s.DestroyCollide(cube);

            PolysoupHandle soup = s.PolysoupCreate();
            for (int i = 0; i < tris.Length; i += 3)
            {
                s.PolysoupAddTriangle(soup, tris[i], tris[i + 1], tris[i + 2], 1);
            }

            CollideHandle c = s.ConvertPolysoupToCollide(soup, false);
            s.PolysoupDestroy(soup);
            var box = s.CollideGetAABB(c, Zero, Zero);
            s.DestroyCollide(c);
            return box;
        });

        Assert.Equal(BoxMins, mins);
        Assert.Equal(BoxMaxs, maxs);
    }

    [VPhysicsNativeFact]
    public async Task SupportsVirtualMeshIsTrue()
    {
        Assert.True(await Cooker.RunAsync(s => s.SupportsVirtualMesh()));
    }

    private static VirtualMeshSource Tent(float apex) => new(
        [new Vec3(0, 0, 10), new Vec3(64, 0, 10), new Vec3(64, 32, 10), new Vec3(0, 32, 10), new Vec3(32, 16, apex)],
        [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4]);

    // vbsp only ever sizes and writes a virtual mesh (disp_ivp.cpp:316-336).
    // Querying one (CollideGetAABB) crashes the pinned library outside a
    // physics environment, measured: so these facts stay on the calls vbsp makes.

    [VPhysicsNativeFact]
    public async Task CreateVirtualMeshWithAHullSerialisesTheHull()
    {
        int size = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.CreateVirtualMesh(Tent(30), buildOuterHull: true);
            int n = s.CollideSize(c);
            s.DestroyCollide(c);
            return n;
        });

        Assert.True(size > 0, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [VPhysicsNativeFact]
    public async Task CreateVirtualMeshWithoutAHullSerialisesNothing()
    {
        // CPhysCollideVirtualMesh::GetSerializationSize: no hull, zero bytes.
        int size = await Cooker.RunAsync(s =>
        {
            CollideHandle c = s.CreateVirtualMesh(Tent(30), buildOuterHull: false);
            int n = s.CollideSize(c);
            s.DestroyCollide(c);
            return n;
        });

        Assert.Equal(0, size);
    }

    [VPhysicsNativeFact]
    public async Task CreateVirtualMeshReadsTheVerticesThroughTheManagedEvent()
    {
        // The hull is built from what the managed GetVirtualMesh callback
        // served, so moving one vertex must move the bytes -- and the same
        // mesh twice must not.
        (byte[] a, byte[] b, byte[] c) = await Cooker.RunAsync(s =>
        {
            byte[] Write(VirtualMeshSource mesh)
            {
                CollideHandle h = s.CreateVirtualMesh(mesh, buildOuterHull: true);
                byte[] bytes = s.CollideWrite(h);
                s.DestroyCollide(h);
                return bytes;
            }

            return (Write(Tent(30)), Write(Tent(30)), Write(Tent(90)));
        });

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
    [VPhysicsNativeFact]
    public async Task ThreadContextCreateIsReturnThis()
    {
        // physics_collide.cpp:1698: there is no per-thread cooker to build on.
        (nint context, nint self) = await Cooker.RunAsync(s =>
        {
            // Internal members of the native session, reached by reflection:
            // this fact is about the library, not about the public surface.
            Type type = s.GetType();
            nint context = (nint)type.GetMethod("ThreadContextCreate")!.Invoke(s, null)!;
            nint self = (nint)type.GetProperty("CollisionInterface")!.GetValue(s)!;
            return (context, self);
        });

        Assert.Equal(self, context);
    }

    [VPhysicsNativeFact]
    public async Task ASessionUsedOffTheCookerThreadThrows()
    {
        ICollisionSession leaked = await Cooker.RunAsync(s => s);

        Assert.Throws<InvalidOperationException>(() => leaked.SupportsVirtualMesh());
    }

    [VPhysicsNativeFact]
    public async Task ASecondCookerInTheProcessIsRefused()
    {
        SourceSharp.MapTools.Io.PhysicalFileSystem host = SourceSharp.MapTools.Io.PhysicalFileSystem.AtHostRoot();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VPhysicsCollisionCooker.CreateAsync(host, host.ToVirtualPath(PinnedVPhysics.LibraryPath)));
    }

    [VPhysicsNativeFact]
    public async Task APreCancelledTokenDoesNoWork()
    {
        bool ran = false;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Cooker.RunAsync(_ => ran = true, cts.Token));
        Assert.False(ran);
    }

    [VPhysicsNativeFact]
    public async Task TheSameShapeCookedTwiceIsByteIdentical()
    {
        byte[] first = await WithCube((s, c) => s.CollideWrite(c));
        byte[] second = await WithCube((s, c) => s.CollideWrite(c));

        Assert.Equal(first, second);
    }

    [VPhysicsNativeFact]
    public async Task AShapeCookedAfterADifferentShapeIsByteIdentical()
    {
        byte[] first = await WithCube((s, c) => s.CollideWrite(c));
        await Cooker.RunAsync(s =>
        {
            CollideHandle other = s.ConvertConvexToCollide([s.ConvexFromVerts(BoxCorners(new Vec3(-7, -3, -90), new Vec3(40, 11, 2)))]);
            byte[] ignored = s.CollideWrite(other);
            s.DestroyCollide(other);
            return ignored.Length;
        });
        byte[] again = await WithCube((s, c) => s.CollideWrite(c));

        Assert.Equal(first, again);
    }

    [VPhysicsNativeFact]
    public async Task TheCubeBlobMatchesSpike0bsDigest()
    {
        // Spike 0b, s0-phys-findings.md §7: the 32-unit cube through the pinned
        // library, sha256 1cbd3458...; three processes, three identical files.
        byte[] blob = await WithCube((s, c) => s.CollideWrite(c));

        Assert.Equal(
            "1cbd34585bd45c262bbbc52bc38c7b637db402cc487242aa6a0f947b56a4231f",
            Convert.ToHexStringLower(SHA256.HashData(blob)));
    }
}
