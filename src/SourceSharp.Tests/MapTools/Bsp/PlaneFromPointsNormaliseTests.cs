//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <see cref="StockQuirk.PlaneFromPointsNormalise"/>: how a brush side's three
/// points become its plane's normal.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of fact. The first three read the committed stock planes in
/// <c>Fixtures/stock-2fort-planes-from-points.txt</c> (stock's own 2fort BSP)
/// and establish what stock does: its normals are what the estimate's Newton
/// step produces, and some of them are what no exact formula produces. Those
/// need no stock tools and no game content, so they run everywhere. The rest
/// load a small in-memory map under each policy and show the loader follows
/// the switch.
/// </para>
/// <para>
/// <b>Why this was worth a quirk.</b> On 2fort, ssmap's tree first parts from
/// stock's at nodes where two candidate planes score the same except for the
/// epsilon-brush penalty, and that penalty fires when a vertex lying on the
/// plane has any positive residual against it, 6e-5 being typical. Which sign
/// the residual has is decided by the normal's last bits, which this switch
/// decides for every slanted brush side.
/// </para>
/// </remarks>
public sealed class PlaneFromPointsNormaliseTests
{
    /// <summary>The vendor-delta key for <see cref="StockModeReproducesStocksPlanes"/>.</summary>
    internal const string ParityKey = "stock-2fort.planes-from-points";

    private const string FixtureName = "stock-2fort-planes-from-points.txt";

    /// <summary>
    /// rsqrtss's architectural error bound: the estimate is within
    /// <c>1.5 * 2^-12</c> of the true reciprocal square root, relatively.
    /// </summary>
    private const double EstimateRelativeError = 1.5 / 4096.0;

    /// <summary>
    /// Every committed stock plane is the Newton step from SOME estimate the
    /// instruction is allowed to return. This is what makes the Stock side's
    /// algorithm the right one, independent of which CPU runs the fact.
    /// </summary>
    [Fact]
    public void StocksSlantedPlanesAreTheEstimatesNewtonStep()
    {
        IReadOnlyList<Row> rows = Rows();
        Assert.Equal(122, rows.Count);

        foreach (Row row in rows)
        {
            Vec3 cross = Cross(row);
            float sqrlen = ((cross.X * cross.X) + (cross.Y * cross.Y) + (cross.Z * cross.Z)) + 1.0e-10f;
            double exact = 1.0 / Math.Sqrt(sqrlen);
            int low = BitConverter.SingleToInt32Bits((float)(exact * (1 - EstimateRelativeError)));
            int high = BitConverter.SingleToInt32Bits((float)(exact * (1 + EstimateRelativeError)));

            bool reached = false;
            for (int bits = low; bits <= high && !reached; bits++)
            {
                float xr = NewtonStep(BitConverter.Int32BitsToSingle(bits), sqrlen);
                reached = SameBits(new Vec3(cross.X * xr, cross.Y * xr, cross.Z * xr), row.Normal);
            }

            Assert.True(reached, $"stock plane {row.Index} is no estimate's Newton step");
        }
    }

    /// <summary>
    /// And the exact alternatives cannot be what stock does: the divide misses
    /// 50 of the 122, and 24 are the answer of no exact formula tried --
    /// divide, divide in double, multiply by the reciprocal, and multiply by
    /// the reciprocal of the length plus <c>FLT_EPSILON</c> (the reference's
    /// non-SSE spelling).
    /// </summary>
    [Fact]
    public void SomeOfStocksPlanesAreNoExactFormulasAnswer()
    {
        int divideMatches = 0;
        int noExactFormula = 0;

        foreach (Row row in Rows())
        {
            Vec3 cross = Cross(row);
            Vec3 divided = cross.Normalise().Normalised;

            double x = cross.X;
            double y = cross.Y;
            double z = cross.Z;
            double length = Math.Sqrt((x * x) + (y * y) + (z * z));
            Vec3 doubled = new((float)(x / length), (float)(y / length), (float)(z / length));

            float reciprocal = 1f / cross.Length();
            Vec3 multiplied = new(cross.X * reciprocal, cross.Y * reciprocal, cross.Z * reciprocal);

            float guarded = 1f / (cross.Length() + 1.1920929e-07f);
            Vec3 epsilon = new(cross.X * guarded, cross.Y * guarded, cross.Z * guarded);

            if (SameBits(divided, row.Normal))
            {
                divideMatches++;
            }

            if (!SameBits(divided, row.Normal) && !SameBits(doubled, row.Normal)
                && !SameBits(multiplied, row.Normal) && !SameBits(epsilon, row.Normal))
            {
                noExactFormula++;
            }
        }

        Assert.Equal(72, divideMatches);
        Assert.Equal(24, noExactFormula);
    }

    /// <summary>
    /// The Stock side reproduces stock's planes, normal and distance, bit for
    /// bit on the vendor stock ran on; on another vendor, against that
    /// vendor's captured delta (see <see cref="VendorGolden"/>).
    /// </summary>
    [StockPlaneParityFact]
    public void StockModeReproducesStocksPlanes()
    {
        IReadOnlyList<Row> rows = Rows();
        List<string> stock = [];
        List<string> actual = [];

        foreach (Row row in rows)
        {
            stock.Add(Line(row.Normal, row.Dist));

            PlaneTable table = new();
            Plane ours = table[table.FromPoints(row.P0, row.P1, row.P2, snapAxialPlanes: false, estimateNormalise: true)];
            actual.Add(Line(ours.Normal, ours.Dist));
        }

        Assert.Equal(VendorGolden.Expected(ParityKey, stock, actual), actual);
    }

    /// <summary>
    /// The loader under Stock stores the estimate's normal for a slanted side.
    /// Before the switch existed this failed on every x86 CPU: the loader
    /// divided under both policies.
    /// </summary>
    [Fact]
    public async Task StockLoadTakesTheEstimatedNormal()
    {
        Plane plane = await SlantedSidePlaneAsync(ComplianceOptions.Stock);

        // The Z component is a signed zero either way and is not compared:
        // it is the cross product's sign of zero, not the normalise's.
        Vec3 expected = new Vec3(3072f, 3072f, 0f).NormaliseLikeStock().Normalised;
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X), BitConverter.SingleToUInt32Bits(plane.Normal.X));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y), BitConverter.SingleToUInt32Bits(plane.Normal.Y));
    }

    /// <summary>The loader under Correct divides: the same side's normal is the exact one.</summary>
    [Fact]
    public async Task CorrectLoadTakesTheExactNormal()
    {
        Plane plane = await SlantedSidePlaneAsync(ComplianceOptions.Correct);

        Assert.Equal(0x3F3504F4u, BitConverter.SingleToUInt32Bits(plane.Normal.X));
        Assert.Equal(0x3F3504F4u, BitConverter.SingleToUInt32Bits(plane.Normal.Y));
    }

    /// <summary>
    /// Flipping only this quirk moves the loaded plane table: over a spread of
    /// slants at least one side's plane changes. The spread keeps the fact
    /// independent of which slants a particular CPU's estimate happens to
    /// round the same way as the divide.
    /// </summary>
    [Fact]
    public async Task TheSlantedPlanesMoveWhenOnlyThisQuirkIsCorrected()
    {
        IReadOnlyList<string> stock = await PlaneBitsAsync(ComplianceOptions.Stock);
        IReadOnlyList<string> corrected =
            await PlaneBitsAsync(ComplianceOptions.Stock.Flipping(StockQuirk.PlaneFromPointsNormalise));

        Assert.NotEqual(stock, corrected);
    }

    /// <summary>
    /// A vertical triangular prism whose apex <paramref name="apex"/> makes
    /// two slanted sides. With the apex at (16, 48) the side from (64, 0)
    /// has the cross product (3072, 3072, 0), the one 2fort's plane 38 has.
    /// </summary>
    private static VmfChunk Prism(int id, (float X, float Y) apex)
    {
        VmfChunk solid = new(MapFileLoader.SolidChunk);
        solid.AddKey("id", id.ToString(CultureInfo.InvariantCulture));

        (float X, float Y) a = (0f, 0f);
        (float X, float Y) b = (64f, 0f);
        (float X, float Y) c = apex;
        const float z0 = 0f;
        const float z1 = 64f;

        // Wound as UnitMap.Box winds its faces, so every normal faces out:
        // the top clockwise seen from above, the bottom counter-clockwise, and
        // each vertical side through edge P->Q (counter-clockwise order) as
        // (Q, z1), (Q, z0), (P, z0).
        UnitMap.Add(solid, UnitMap.Plain, (c.X, c.Y, z1), (b.X, b.Y, z1), (a.X, a.Y, z1));
        UnitMap.Add(solid, UnitMap.Plain, (a.X, a.Y, z0), (b.X, b.Y, z0), (c.X, c.Y, z0));
        UnitMap.Add(solid, UnitMap.Plain, (b.X, b.Y, z1), (b.X, b.Y, z0), (a.X, a.Y, z0));
        UnitMap.Add(solid, UnitMap.Plain, (c.X, c.Y, z1), (c.X, c.Y, z0), (b.X, b.Y, z0));
        UnitMap.Add(solid, UnitMap.Plain, (a.X, a.Y, z1), (a.X, a.Y, z0), (c.X, c.Y, z0));
        return solid;
    }

    private static async Task<MapFile> LoadAsync(ComplianceOptions compliance, params (float X, float Y)[] apexes)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        for (int i = 0; i < apexes.Length; i++)
        {
            world.Children.Add(Prism(i + 2, apexes[i]));
        }

        document.Chunks.Add(world);

        VbspContext context = await UnitMap.ContextAsync(VbspOptions.Default with { Compliance = compliance });
        return await MapFileLoader.LoadAsync(context, document);
    }

    private static async Task<Plane> SlantedSidePlaneAsync(ComplianceOptions compliance)
    {
        MapFile map = await LoadAsync(compliance, (16f, 48f));
        MapBrush brush = Assert.Single(map.Brushes);

        // The side through (64, 0) and (16, 48), found by its orientation:
        // the bevel pass moves axial sides to the front of the brush, so its
        // position in the brush is not the VMF's.
        List<Plane> slanted = [];
        for (int i = 0; i < brush.SideCount; i++)
        {
            Plane plane = map.Planes[map.BrushSides[brush.FirstSide + i].PlaneNumber];
            if (plane.Normal.X > 0.7f && plane.Normal.Y > 0.7f)
            {
                slanted.Add(plane);
            }
        }

        return Assert.Single(slanted);
    }

    private static async Task<IReadOnlyList<string>> PlaneBitsAsync(ComplianceOptions compliance)
    {
        MapFile map = await LoadAsync(
            compliance,
            (16f, 48f), (8f, 40f), (40f, 24f), (20f, 52f), (5f, 33f), (27f, 61f), (11f, 13f));

        List<string> planes = [];
        for (int i = 0; i < map.Planes.Count; i++)
        {
            planes.Add(Line(map.Planes[i].Normal, map.Planes[i].Dist));
        }

        return planes;
    }

    private static float NewtonStep(float xr, float sqrlen)
    {
        // The reference's refinement, operation for operation, as
        // Vec3.NormaliseLikeStock spells it.
        float xt = xr * xr;
        xt *= sqrlen;
        xt = 3f - xt;
        xt *= 0.5f;
        return xr * xt;
    }

    private static Vec3 Cross(Row row) => Vec3.Cross(row.P0 - row.P1, row.P2 - row.P1);

    private static bool SameBits(Vec3 a, Vec3 b) =>
        BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
        && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
        && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);

    private static string Line(Vec3 normal, float dist) =>
        $"{VendorGolden.Bits(normal.X)} {VendorGolden.Bits(normal.Y)} {VendorGolden.Bits(normal.Z)} {VendorGolden.Bits(dist)}";

    /// <summary>Where the committed fixture sits in this worktree.</summary>
    internal static string FixturePath()
    {
        string root = RepoTree.FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\"; the stock planes "
                + "fixture is read from the tree this binary was built from");

        string path = Path.Combine(root, "src", "SourceSharp.Tests", "MapTools", "Bsp", "Fixtures", FixtureName);
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"{path} is missing. It is a COMMITTED file, not build output.");
    }

    private static IReadOnlyList<Row> Rows()
    {
        List<Row> rows = [];
        foreach (string line in File.ReadLines(FixturePath()))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float P(int i) => float.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            float H(int i) => VendorGolden.FromBits(f[i]);

            rows.Add(new Row(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                new Vec3(P(1), P(2), P(3)),
                new Vec3(P(4), P(5), P(6)),
                new Vec3(P(7), P(8), P(9)),
                new Vec3(H(10), H(11), H(12)),
                H(13)));
        }

        return rows;
    }

    private sealed record Row(int Index, Vec3 P0, Vec3 P1, Vec3 P2, Vec3 Normal, float Dist);
}

/// <summary>
/// <see cref="ReferenceRsqrtFactAttribute"/> for one key: runs on the vendor
/// stock ran on, on a vendor with a captured delta for
/// <see cref="PlaneFromPointsNormaliseTests.ParityKey"/>, and on a capture
/// run; skips, with the reason, anywhere else.
/// </summary>
/// <remarks>
/// The general attribute runs wherever a vendor has ANY capture directory,
/// which on a vendor that has captures for other facts but not this one
/// would turn a missing capture into a failure rather than a skip.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StockPlaneParityFactAttribute : FactAttribute
{
    /// <summary>Decides at discovery whether this CPU has expected values.</summary>
    public StockPlaneParityFactAttribute()
    {
        string? vendor = ReferenceRsqrt.CpuVendor();
        bool captured = vendor is not null
            && VendorGolden.Directory(vendor) is { } dir
            && File.Exists(Path.Combine(dir, PlaneFromPointsNormaliseTests.ParityKey + ".txt"));

        Skip = vendor == ReferenceRsqrt.ReferenceVendor || captured || (vendor is not null && VendorGolden.Capturing)
            ? null
            : $"stock's planes were measured on {ReferenceRsqrt.ReferenceVendor}; this CPU is "
                + $"{vendor ?? "neither x86 nor arm64"}, whose estimate differs in the low bits, and "
                + $"it has no captured '{PlaneFromPointsNormaliseTests.ParityKey}' delta";
    }
}
