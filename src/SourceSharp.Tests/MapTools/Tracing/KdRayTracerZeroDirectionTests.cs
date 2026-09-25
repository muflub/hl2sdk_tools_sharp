using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// A ray with an exactly zero direction component, and
/// <see cref="StockQuirk.KdZeroDirectionReachCut"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Trace4Rays</c> takes the direction's reciprocal with
/// <c>ReciprocalSaturateSIMD</c> (<c>ssemath.h:2288-2291</c>), which ORs
/// <c>Four_Epsilons</c> = <c>FLT_EPSILON</c> (<c>sseconst.cpp:27</c>) into a
/// zero component. The scene-box clip (<c>raytrace.cpp:360-368</c>) then puts
/// the max face of a +0 axis at <c>(max - origin) * 8.4e6</c>, so a ray one ulp
/// inside x = 100 is limited to 64 units.
/// </para>
/// <para>
/// The expected answers were produced by stock's own <c>raytrace.cpp</c>,
/// compiled unchanged (p5a's oracle, <c>oracle_kd real</c>, on the scene and
/// rays below): the +0 ray misses (id -1, distance 1e23), the -0 ray and the
/// control hit triangle id 7 at distance 1,000. Before lane p5-trace the port
/// substituted 1e-10, and the +0 ray hit.
/// </para>
/// </remarks>
public sealed class KdRayTracerZeroDirectionTests
{
    /// <summary>One ulp below 100: the ray's x, just inside the box's max face.</summary>
    private static readonly float JustInside = MathF.BitDecrement(100f);

    /// <summary>A wall at y = 0 spanning x and z over [0, 100], id 7.</summary>
    private static KdRayTracer Wall(ComplianceOptions compliance) =>
        KdRayTracer.Build(
        [
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 0, 100), 0),
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(100, 0, 100), new Vec3(0, 0, 100), 0),
        ],
        compliance);

    /// <summary>
    /// Four copies of one ray, so that no packet-mate keeps the packet alive:
    /// stock tests a leaf's triangles on every lane once any lane is active.
    /// </summary>
    private static HitId Closest(KdRayTracer tracer, float x, float dx)
    {
        Ray ray = new(x, -1000f, 50f, dx, 1f, 0f, 2000f);
        HitId[] hits = new HitId[4];
        tracer.TraceClosest([ray, ray, ray, ray], hits, RayTraceOptions.StockExact);
        return hits[0];
    }

    /// <summary>Stock's saturation constant is <c>FLT_EPSILON</c>, 2^-23.</summary>
    [Fact]
    public void StocksSubstituteIsFltEpsilon() =>
        Assert.Equal(0x34000000, BitConverter.SingleToInt32Bits(KdRayTracer.StockZeroSubstitute));

    /// <summary>
    /// Under stock, a +0 ray one ulp inside the box's max face is cut at 64
    /// units and misses the wall at 1,000, as stock's <c>Trace4Rays</c> does.
    /// Red before the fix: the 1e-10 substitute reached 76,000 units and hit.
    /// </summary>
    [Fact]
    public void StockCutsAPositiveZeroRayShortOfTheWall() =>
        Assert.Equal(HitId.Miss, Closest(Wall(ComplianceOptions.Stock), JustInside, 0f).Surface);

    /// <summary>
    /// Under stock the SAME ray with a -0 component hits: the cut is at the
    /// min face instead, which is behind the origin.
    /// </summary>
    [Fact]
    public void StockLetsANegativeZeroRayReachTheWall()
    {
        HitId hit = Closest(Wall(ComplianceOptions.Stock), JustInside, -0f);
        Assert.Equal(7, hit.Surface);
        Assert.Equal(0.5f, hit.Fraction);
    }

    /// <summary>A ray well inside the face hits under either policy: the control.</summary>
    [Fact]
    public void AZeroComponentRayWellInsideTheBoxHitsUnderStock()
    {
        HitId hit = Closest(Wall(ComplianceOptions.Stock), 50f, 0f);
        Assert.Equal(7, hit.Surface);
        Assert.Equal(0.5f, hit.Fraction);
    }

    /// <summary>Under correct, the +0 ray one ulp inside x = 100 hits the wall.</summary>
    [Fact]
    public void CorrectLetsAPositiveZeroRayReachTheWall()
    {
        HitId hit = Closest(Wall(ComplianceOptions.Correct), JustInside, 0f);
        Assert.Equal(7, hit.Surface);
        Assert.Equal(0.5f, hit.Fraction);
    }

    /// <summary>
    /// Under correct, a +0 ray one ulp inside a face at x = 1 hits a wall
    /// 1,000 units away. Red under the 1e-10 substitute this port used before:
    /// 6e-8 * 1e10 is a 596-unit reach.
    /// </summary>
    [Fact]
    public void CorrectDoesNotCutARayOneUlpInsideAFaceNearTheOrigin()
    {
        KdRayTracer tracer = KdRayTracer.Build(
        [
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 0, 1), 0),
            new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(1, 0, 1), new Vec3(0, 0, 1), 0),
        ],
        ComplianceOptions.Correct);
        Ray ray = new(MathF.BitDecrement(1f), -1000f, 0.5f, 0f, 1f, 0f, 2000f);
        ulong[] bits = new ulong[1];
        tracer.TraceVisibility([ray, ray, ray, ray], bits, RayTraceOptions.StockExact);
        Assert.Equal(0b1111UL, bits[0]);
    }

    /// <summary>
    /// The two policies are two tracer identities, because a result from one
    /// is not a result from the other.
    /// </summary>
    [Fact]
    public void ThePoliciesHaveDifferentIdentities() =>
        Assert.NotEqual(
            Wall(ComplianceOptions.Stock).TracerIdentity,
            Wall(ComplianceOptions.Correct).TracerIdentity);

    /// <summary>
    /// The unparameterised build is the library default, correct.
    /// </summary>
    [Fact]
    public void TheDefaultBuildIsCorrect() =>
        Assert.Equal(
            Wall(ComplianceOptions.Correct).TracerIdentity,
            KdRayTracer.Build(
            [
                new TracedTriangle(7, new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 0, 100), 0),
            ]).TracerIdentity);
}
