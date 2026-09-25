using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The compliance matrix's missing flip facts: the quirks whose stock/correct split
/// was decided in code and exercised nowhere. Each fact calls the public decision API
/// under both policies and requires the two to disagree where the quirk says they
/// must, so the matrix's Evidence column is a demonstration, not a citation.
/// </summary>
/// <remarks>
/// Placed together (not scattered into Rad/, Bsp/, Phys/) because these are the
/// matrix's completeness gaps, cross-domain, and the lane is read-mostly: one new
/// test file touching no sibling's territory. The pre-existing split for a quirk
/// listed in the ledger lives in the citations of <see cref="ComplianceMatrix"/>.
/// Each method names its quirk in the comment the evidence scan attributes by.
/// </remarks>
public class ComplianceMatrixEvidenceTests
{
    private static DWorldLight SurfaceLight(Vec3 origin, Vec3 normal, Vec3 intensity, float radius = 0)
    {
        DWorldLight wl = default;
        wl.Origin = origin;
        wl.Normal = normal;
        wl.Intensity = intensity;
        wl.Radius = radius;
        wl.Type = (int)EmitType.Surface;
        wl.Flags = (int)WorldLightFlags.InAmbientCube;
        return wl;
    }

    private static Vec3[] Cube(DWorldLight light, Vec3 start, ComplianceOptions compliance)
    {
        Vec3[] cube = new Vec3[6];
        AmbientCube.AddEmitSurfaceLights([light], [0], [1.0f], start, cube, compliance);
        return cube;
    }

    [Fact]
    public void TheAmbientBakeOfASurfaceLightDiffersUnderTheReciprocalEstimate()
    {
        // StockQuirk.AmbientCubeReciprocalEstimate (leaf_ambient_lighting.cpp:120):
        // stock's rcpss distance term is a 12-bit estimate, so the same bake lands on
        // different cube sides bit-for-bit. 65000 keeps the estimate's error well
        // above a float's last bits.
        Vec3 origin = new(200f, 90f, 130f);
        float len = MathF.Sqrt(200f * 200f + 90f * 90f + 130f * 130f);
        Vec3 towardSample = new(-origin.X / len, -origin.Y / len, -origin.Z / len);
        DWorldLight light = SurfaceLight(origin, towardSample, new Vec3(1_000_000f, 1_000_000f, 1_000_000f));

        Vec3[] stock = Cube(light, Vec3.Zero, ComplianceOptions.Stock);
        Vec3[] correct = Cube(light, Vec3.Zero, ComplianceOptions.Correct);

        Assert.False(stock.All(c => c == Vec3.Zero), "the fixture must actually bake light");
        Assert.False(stock.Zip(correct).All(pair => pair.First == pair.Second),
            "the estimate and the divide agreed on every side - the quirk changed nothing here");
    }

    [Fact]
    public void StockPolicyNormalisesVectorsTheStockWay()
    {
        // StockQuirk.VradVectorNormalise: vrad's own VectorNormalize is the rsqrtss
        // estimate plus one Newton step, versus a divide. DirectLightingSettings.
        // StockNormalise is the switch, and the obliquest of a fixed spread of
        // directions must show the difference.
        Assert.True(new DirectLightingSettings { Compliance = ComplianceOptions.Stock }.StockNormalise);
        Assert.False(new DirectLightingSettings { Compliance = ComplianceOptions.Correct }.StockNormalise);

        Vec3[] spread =
        [
            new(0.6f, 0.7f, 0.33f), new(1f, 3f, 7f), new(0.125f, 9.5f, 2.25f),
            new(11f, 0.5f, 0.25f), new(3.1f, 4.15f, 5.9f), new(0.7f, 0.1f, 0.9f),
            new(2.5f, 13f, 0.375f), new(9.9f, 9.1f, 0.11f), new(0.3f, 0.3f, 4.7f),
            new(123.5f, 0.001f, 6f),
        ];

        Assert.True(spread.Any(v => BumpBasis.Normalise(v, true) != BumpBasis.Normalise(v, false)),
            "no direction in the spread distinguishes the estimate from the divide");
    }

    [Fact]
    public void AConformingDetailOrientationDiffersUnderTheStockNormalise()
    {
        // StockQuirk.DetailOrientationNormalise (detailobjects.cpp:580-601) builds its
        // basis from a normalised normal; the estimate and the divide propagate into
        // pitch/yaw/roll. One oblique normal in the fixed spread must show it.
        Vec3[] spread =
        [
            new(0.6f, 0.7f, 0.33f), new(0.2f, 0.9f, 0.4f), new(0.75f, 0.15f, 0.6f),
            new(0.4f, 0.4f, 0.85f), new(0.9f, 0.3f, 0.2f), new(0.1f, 0.7f, 0.65f),
            new(0.55f, 0.65f, 0.5f), new(0.05f, 0.5f, 0.86f),
        ];

        Assert.True(spread.Any(static n =>
            DetailPropEmitter.ConformingAngles(n, 123_456_789, ComplianceOptions.Stock)
            != DetailPropEmitter.ConformingAngles(n, 123_456_789, ComplianceOptions.Correct)),
            "no orientation in the spread distinguishes the two normalises");
    }

    [Fact]
    public void StockLeavesTheHdrFaceExtentsStaleAndCorrectUpdatesThem()
    {
        // StockQuirk.LuxelDensityLeavesHdrFacesStale (bsplib.cpp:3317): the update
        // walks dfaces while an HDR pass lights dfaces_hdr.
        Assert.True(LuxelDensity.StaleHdrFaces(ComplianceOptions.Stock));
        Assert.False(LuxelDensity.StaleHdrFaces(ComplianceOptions.Correct));
    }

    [Fact]
    public void StockSupersamplesAgainstUninitialisedGradientMemoryAndCorrectDoesNot()
    {
        // StockQuirk.SupersampleGradientReadsUninitialised (lightmap.cpp:2881): the
        // stackalloc'd sample buffer an edge sample's gradient reads.
        Assert.True(FaceLightJob.SupersampleReadsUninitialised(ComplianceOptions.Stock));
        Assert.False(FaceLightJob.SupersampleReadsUninitialised(ComplianceOptions.Correct));
    }
}
