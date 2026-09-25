using System.Buffers.Binary;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>The public face of the managed cooker: the <c>ICollisionCooker</c> contract.</summary>
public class ManagedCollisionCookerTests
{
    private static readonly CollisionPlane[] Cube =
    [
        new(new Vec3(1, 0, 0), 16), new(new Vec3(-1, 0, 0), 16), new(new Vec3(0, 1, 0), 16),
        new(new Vec3(0, -1, 0), 16), new(new Vec3(0, 0, 1), 16), new(new Vec3(0, 0, -1), 16),
    ];

    [Fact]
    public async Task CorrectComplianceCooksInDoublePrecision()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        Assert.True(cooker.IsDoublePrecision);
    }

    [Fact]
    public async Task StockComplianceCooksInSinglePrecision()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        Assert.False(cooker.IsDoublePrecision);
    }

    [Fact]
    public async Task TheIdentityNamesTheArithmetic()
    {
        await using ManagedCollisionCooker stock = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        await using ManagedCollisionCooker correct = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        Assert.Contains("sdk2013-float", stock.CookerIdentity, StringComparison.Ordinal);
        Assert.Contains("tf2-double", correct.CookerIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACubeCooksToTheSpikesFourHundredFortyBytes()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Stock);
        CookedCollide cooked = (await cooker.CookFromPlanesAsync(Cube))!.Value;
        Assert.Equal(440, cooked.Bytes.Length);
        Assert.Equal(412, BinaryPrimitives.ReadInt32LittleEndian(cooked.Bytes.Span[8..]));
        Assert.Equal(cooker.CookerIdentity, cooked.CookerIdentity);
    }

    [Fact]
    public async Task ACancelledCookThrowsBeforeCooking()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cooker.CookFromPlanesAsync(Cube, 0f, cts.Token));
    }

    [Fact]
    public async Task PlanesThatBoundNothingCookToNull()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        CollisionPlane[] slab = [new(new Vec3(1, 0, 0), 16), new(new Vec3(-1, 0, 0), 16)];
        Assert.Null(await cooker.CookFromPlanesAsync(slab));
    }

    [Fact]
    public async Task PointsCookThroughTheRebuildFromPlanes()
    {
        // ConvexFromVerts = pointsoup + RebuildConvexFromPlanes(0.01): an interior point is gone.
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        var points = new List<Vec3>();
        for (int b = 0; b < 8; b++)
        {
            points.Add(new Vec3((b & 1) != 0 ? 16 : -16, (b & 2) != 0 ? 16 : -16, (b & 4) != 0 ? 16 : -16));
        }

        points.Add(new Vec3(0, 0, 0));
        CookedCollide cooked = (await cooker.CookFromVertsAsync(points.ToArray()))!.Value;
        Assert.Equal(440, cooked.Bytes.Length);
    }

    [Fact]
    public async Task CorrectComplianceWritesFiniteInertiaWhereTf2WritesNaN()
    {
        // StockQuirk.CollisionInertiaZeroLengthEdge wired through Create: a dm_lockdown brush whose
        // TF2-arithmetic cook has NaN rotation inertia cooks finite through the correct cooker.
        CookerFixture.Convex brush = FirstNaNInertiaBrush();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        byte[] blob = cooker.CookPlanes(Planes(brush), brush.Merge)!;
        for (int k = 0; k < 3; k++)
        {
            Assert.True(float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(28 + 12 + (4 * k)))));
        }
    }

    private static async Task<(float Material0, float Material1)> FirstPointX(ComplianceOptions compliance)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(compliance);
        return await cooker.RunAsync(s =>
        {
            float X(int material)
            {
                PolysoupHandle soup = s.PolysoupCreate();
                s.PolysoupAddTriangle(soup, new Vec3(100, 0, 0), new Vec3(164, 0, 0), new Vec3(100, 64, 0), material);
                byte[] blob = s.CollideWrite(s.ConvertPolysoupToCollide(soup, false));
                ReadOnlySpan<byte> surface = IvpCollideQueries.Surface(blob);
                return IvpCollideQueries.LedgeAt(surface, IvpCollideQueries.LeafOffsets(surface)[0]).Point(0).X;
            }

            return (X(0), X(1));
        });
    }

    [Fact]
    public async Task StockPolysoupsOverrunIntoTheirPointsOnMaterialZero()
    {
        // StockQuirk.CollisionPolysoupMaterialOverrun: both vphysics builds clear the exponent of
        // the first point's x when the triangle's material is 0.
        (float zero, float one) = await FirstPointX(ComplianceOptions.Stock);
        Assert.NotEqual(one, zero);
    }

    [Fact]
    public async Task CorrectPolysoupsKeepTheirPointsOnMaterialZero()
    {
        (float zero, float one) = await FirstPointX(ComplianceOptions.Correct);
        Assert.Equal(one, zero);
    }

    private static CookerFixture.Convex FirstNaNInertiaBrush()
    {
        IvpCookContext tf2 = CookerFixture.Context(CookMode.Tf2);
        foreach (CookerFixture.Job job in CookerFixture.Jobs("brushes"))
        {
            if (job.Convexes.Length != 1 || job.Outer || job.Convexes[0].Kind != 'P')
            {
                continue;
            }

            byte[]? blob = CookerFixture.Cook(job, CookMode.Tf2, tf2);
            if (blob is not null && float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(28 + 12))))
            {
                return job.Convexes[0];
            }
        }

        throw new InvalidOperationException("no NaN-inertia brush in the goldens");
    }

    private static (float, float, float, float)[] Planes(CookerFixture.Convex c)
    {
        var planes = new (float, float, float, float)[c.Data.Length / 4];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = (c.Data[4 * i], c.Data[(4 * i) + 1], c.Data[(4 * i) + 2], c.Data[(4 * i) + 3]);
        }

        return planes;
    }
}
