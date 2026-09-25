using System.Buffers.Binary;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>The managed session's contract: handles, ownership, round trips and refusals.</summary>
public class ManagedCollisionSessionTests
{
    private static readonly CollisionPlane[] Cube =
    [
        new(new Vec3(1, 0, 0), 16), new(new Vec3(-1, 0, 0), 16), new(new Vec3(0, 1, 0), 16),
        new(new Vec3(0, -1, 0), 16), new(new Vec3(0, 0, 1), 16), new(new Vec3(0, 0, -1), 16),
    ];

    private static async Task<T> Run<T>(Func<ICollisionSession, T> work)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        return await cooker.RunAsync(work);
    }

    [Fact]
    public async Task ConvertingAConvexFreesItsHandle()
    {
        // The reference contract: converting a convex frees its ledges.
        await Assert.ThrowsAsync<ArgumentException>(() => Run(s =>
        {
            ConvexHandle c = s.ConvexFromPlanes(Cube, 0f);
            s.ConvertConvexToCollide([c]);
            return s.ConvexVolume(c);
        }));
    }

    [Fact]
    public async Task ACubesVolumeIsItsSideCubed()
    {
        float volume = await Run(s => s.ConvexVolume(s.ConvexFromPlanes(Cube, 0f)));
        Assert.Equal(32f * 32f * 32f, volume, 0.5f);
    }

    [Fact]
    public async Task AWrittenCollideUnserialisesToTheSameBytes()
    {
        (byte[] first, byte[] second) = await Run(s =>
        {
            byte[] a = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]));
            byte[] b = s.CollideWrite(s.UnserializeCollide(a, 0));
            return (a, b);
        });
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task UnserialisingSetsTheCollideIndex()
    {
        int index = await Run(s =>
        {
            byte[] a = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]));
            return s.CollideIndex(s.UnserializeCollide(a, 7));
        });
        Assert.Equal(7, index);
    }

    [Fact]
    public async Task GameDataSurvivesTheCompile()
    {
        uint data = await Run(s =>
        {
            ConvexHandle c = s.ConvexFromPlanes(Cube, 0f);
            s.SetConvexGameData(c, 1234);
            uint read = 0;
            s.WithQueryModel(s.ConvertConvexToCollide([c]), q => read = q.GetGameData(0));
            return read;
        });
        Assert.Equal(1234u, data);
    }

    [Fact]
    public async Task AMaterialSetThroughTheQueryModelIsWritten()
    {
        int material = await Run(s =>
        {
            CollideHandle collide = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            s.WithQueryModel(collide, q => q.SetTriangleMaterialIndex(0, 3, 42));
            CollideHandle back = s.UnserializeCollide(s.CollideWrite(collide), 0);
            int read = 0;
            s.WithQueryModel(back, q => read = q.GetTriangleMaterialIndex(0, 3));
            return read;
        });
        Assert.Equal(42, material);
    }

    [Fact]
    public async Task DragAreasOfABoxAreOne()
    {
        Vec3 areas = await Run(s =>
            s.CollideGetOrthographicAreas(s.ConvertConvexToCollideParams(
                [s.ConvexFromPlanes(Cube, 0f)], ConvertConvexParams.Defaults with { BuildDragAxisAreas = true, DragAreaEpsilon = 4f })));
        Assert.Equal(new Vec3(1, 1, 1), areas);
    }

    [Fact]
    public async Task DragAreasAreWrittenIntoTheHeader()
    {
        // A wedge: half of the x-y projection is empty.
        CollisionPlane[] wedge = [.. Cube, new(new Vec3(0.70710677f, 0.70710677f, 0), 0)];
        byte[] blob = await Run(s => s.CollideWrite(s.ConvertConvexToCollideParams(
            [s.ConvexFromPlanes(wedge, 0f)], ConvertConvexParams.Defaults with { BuildDragAxisAreas = true, DragAreaEpsilon = 4f })));
        float z = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(20));
        Assert.InRange(z, 0.4f, 0.6f);
    }

    [Fact]
    public async Task ABoxTraceIsRefusedRatherThanApproximated()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(s => s.TraceBox(
            new Vec3(-100, 0, 0), new Vec3(100, 0, 0), new Vec3(-1, -1, -1), new Vec3(1, 1, 1),
            s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]), default, default)));
    }

    [Fact]
    public async Task ARayThroughACubeHitsItsFace()
    {
        CollisionTrace trace = await Run(s => s.TraceBox(
            new Vec3(-100, 0, 0), new Vec3(100, 0, 0), default, default,
            s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]), default, default));
        Assert.Equal(84f / 200f, trace.Fraction, 1e-4f);
    }

    [Fact]
    public async Task ASweptCollideIsRefused()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            CollideHandle d = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            return s.TraceCollide(default, new Vec3(1, 0, 0), c, default, d, default, default);
        }));
    }

    [Fact]
    public async Task OverlappingCollidesStartSolid()
    {
        bool solid = await Run(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            CollideHandle d = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            return s.TraceCollide(default, default, c, default, d, new Vec3(20, 0, 0), default).StartSolid;
        });
        Assert.True(solid);
    }

    [Fact]
    public async Task SeparateCollidesDoNotStartSolid()
    {
        bool solid = await Run(s =>
        {
            CollideHandle c = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            CollideHandle d = s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]);
            return s.TraceCollide(default, default, c, default, d, new Vec3(40, 0, 0), default).StartSolid;
        });
        Assert.False(solid);
    }

    [Fact]
    public async Task VirtualMeshesAreSupported()
    {
        Assert.True(await Run(s => s.SupportsVirtualMesh()));
    }

    [Fact]
    public async Task AVirtualMeshWithoutAHullWritesNothing()
    {
        Vec3[] v = [new(0, 0, 0), new(64, 0, 0), new(0, 64, 0), new(64, 64, 0)];
        byte[] blob = await Run(s => s.CollideWrite(s.CreateVirtualMesh(new VirtualMeshSource(v, [0, 1, 2, 1, 3, 2]), false)));
        Assert.Empty(blob);
    }

    [Fact]
    public async Task APolysoupWithOnlyDegenerateTrianglesIsNull()
    {
        bool isNull = await Run(s =>
        {
            PolysoupHandle soup = s.PolysoupCreate();
            s.PolysoupAddTriangle(soup, new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 1, 1), 1);
            return s.ConvertPolysoupToCollide(soup, false).IsNull;
        });
        Assert.True(isNull);
    }

    [Fact]
    public async Task ACollideLoadsBackThroughVCollideLoad()
    {
        int solids = await Run(s =>
        {
            byte[] blob = s.CollideWrite(s.ConvertConvexToCollide([s.ConvexFromPlanes(Cube, 0f)]));
            byte[] buffer = [.. BitConverter.GetBytes(blob.Length), .. blob, .. "solid {}\0"u8.ToArray()];
            LoadedVCollide loaded = s.VCollideLoad(buffer, 1);
            return loaded.Solids.Count(h => !h.IsNull);
        });
        Assert.Equal(1, solids);
    }

    [Fact]
    public async Task ConcurrentRunsGiveTheSameBytes()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        byte[][] blobs = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => cooker.RunAsync(s =>
            s.CollideWrite(s.ConvertConvexToCollideParams(
                [s.ConvexFromPlanes(Cube, 0f), s.ConvexFromPlanes([.. Cube.Select(p => p with { Dist = p.Dist + (p.Normal.X * 40) })], 0f)],
                ConvertConvexParams.Defaults with { BuildOuterConvexHull = true, BuildDragAxisAreas = true, DragAreaEpsilon = 16f })))));
        Assert.All(blobs, b => Assert.Equal(blobs[0], b));
    }
}
