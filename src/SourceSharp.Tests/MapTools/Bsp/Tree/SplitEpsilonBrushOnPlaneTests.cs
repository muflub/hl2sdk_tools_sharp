//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Options;

using SourceSharp.Tests.MapTools.Bsp.Csg;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Tree;

/// <summary>
/// <see cref="StockQuirk.SplitEpsilonBrushOnPlane"/>: whether the split
/// heuristic's "epsilon brush" test reads a vertex that lies on the
/// candidate plane by the sign of its rounding residual.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here builds the same brush: the back half of a 128-unit box cut
/// along <c>x + y = 0</c>, a triangular prism whose corners are all integer.
/// The candidate plane has the normal (0.8, 0.6, 0) and passes through the
/// prism's corner edge at (64, -64). No side of the prism lies on it, the
/// prism's bounding box straddles it (the box corner (64, 64) is 76.8 units
/// in front), and every other vertex is at least 25 units behind. So the only
/// thing that can make the brush "only just" cross is the corner edge, and the
/// facts move that edge by a few ulps, or by a real fraction of a unit, and
/// read <c>epsilonbrush</c>.
/// </para>
/// <para>
/// The mirrored plane (the same plane number with the low bit flipped) puts
/// the prism in front and the edge on the back side, so the same brush covers
/// both halves of the test: <c>d_front</c> in (0, 1) and <c>d_back</c> in
/// (-1, 0).
/// </para>
/// </remarks>
public class SplitEpsilonBrushOnPlaneTests
{
    /// <summary>
    /// <b>Stock flips on the residual's sign.</b> The corner edge a few ulps in
    /// front makes the brush an epsilon brush, worth -1000 in the split score;
    /// a few ulps behind, it is not. That is the whole of the 2fort
    /// sensitivity: which way a vertex's last bit rounds decides the penalty.
    /// </summary>
    [Fact]
    public async Task StockCountsAnEdgeAFewUlpsInFrontAndNotOneAFewUlpsBehind()
    {
        Assert.Equal(1, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheFar, mirrored: false));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheNear, mirrored: false));
    }

    /// <summary>
    /// The same flip on the back half of the test, <c>d_back</c> in (-1, 0):
    /// against the mirrored plane the edge a few ulps behind counts and a few
    /// ulps in front does not.
    /// </summary>
    [Fact]
    public async Task StockCountsAnEdgeAFewUlpsBehindTheMirroredPlaneAndNotOneInFront()
    {
        Assert.Equal(1, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheFar, mirrored: true));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheNear, mirrored: true));
    }

    /// <summary>
    /// <b>Correct does not flip.</b> A vertex within
    /// <see cref="BrushBspTree.SplitOnPlaneEpsilon"/> of the plane is on it,
    /// so the brush is not an epsilon brush whichever way its residual
    /// rounded, on either half of the test.
    /// </summary>
    [Fact]
    public async Task CorrectCountsNeitherSideOfTheResidualOnEitherHalfOfTheTest()
    {
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheFar, mirrored: false));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheNear, mirrored: false));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheFar, mirrored: true));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheNear, mirrored: true));
    }

    /// <summary>
    /// <b>Correct still penalises a real sliver.</b> An edge a quarter of a
    /// unit across the plane is past the on-plane band, would be cut off by
    /// <see cref="BrushGeometry.SplitBrush"/> as a real fragment, and is the
    /// thing the -1000 exists to discourage; both policies count it.
    /// </summary>
    [Fact]
    public async Task BothPoliciesCountAnEdgeAQuarterOfAUnitAcross()
    {
        foreach (ComplianceOptions policy in new[] { ComplianceOptions.Stock, ComplianceOptions.Correct })
        {
            Assert.Equal(1, await EpsilonBrushesAsync(policy, Nudge.QuarterUnit, mirrored: false));
            Assert.Equal(1, await EpsilonBrushesAsync(policy, Nudge.QuarterUnit, mirrored: true));
        }
    }

    /// <summary>
    /// The band, not only the residual: an edge 0.05 across is far past any
    /// rounding noise but inside the 0.1 that <see cref="BrushGeometry.SplitBrush"/>
    /// itself treats as not crossing, so splitting on this plane would move
    /// the whole brush to one side and make no sliver. Stock still charges
    /// the -1000; Correct does not.
    /// </summary>
    [Fact]
    public async Task AnEdgeInsideTheBandCountsOnlyUnderStock()
    {
        Assert.Equal(1, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.InsideTheBand, mirrored: false));
        Assert.Equal(1, await EpsilonBrushesAsync(ComplianceOptions.Stock, Nudge.InsideTheBand, mirrored: true));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.InsideTheBand, mirrored: false));
        Assert.Equal(0, await EpsilonBrushesAsync(ComplianceOptions.Correct, Nudge.InsideTheBand, mirrored: true));
    }

    /// <summary>
    /// <b>Under Stock the residual's sign picks the splitter.</b> Two nodes
    /// that differ only in which way the corner edge's last bits rounded are
    /// split on different planes: a few ulps behind, the candidate wins; a
    /// few ulps in front, it is charged 1000 and another plane wins. This is
    /// the 2fort mechanism in miniature, where a normal one ulp different
    /// moved the edge's residual across zero.
    /// </summary>
    [Fact]
    public async Task UnderStockTheResidualsSignChangesTheSplitter()
    {
        (int near, int candidate) = await WinnerAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheNear);
        (int far, _) = await WinnerAsync(ComplianceOptions.Stock, Nudge.UlpsOnTheFar);

        Assert.Equal(candidate, near);
        Assert.NotEqual(candidate, far);
    }

    /// <summary>
    /// <b>Under Correct the splitter is stable.</b> The same two nodes split
    /// on the same plane, the candidate, whichever way the edge rounded.
    /// </summary>
    [Fact]
    public async Task UnderCorrectTheSplitterDoesNotDependOnTheResidualsSign()
    {
        (int near, int candidate) = await WinnerAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheNear);
        (int far, _) = await WinnerAsync(ComplianceOptions.Correct, Nudge.UlpsOnTheFar);

        Assert.Equal(candidate, near);
        Assert.Equal(candidate, far);
    }

    /// <summary>
    /// The band sits where <see cref="BrushGeometry.SplitBrush"/> draws its own
    /// line, and well clear of both the rounding noise and the 1-unit limit
    /// of the test it narrows. A fact rather than a comment, so the three
    /// numbers cannot drift apart unnoticed.
    /// </summary>
    [Fact]
    public void TheBandIsSplitBrushsOwnThresholdAndClearsTheNoise()
    {
        Assert.Equal(0.1f, BrushBspTree.SplitOnPlaneEpsilon);

        // Half an ulp at the base winding's 65536, over three coordinates of
        // a unit normal: the worst a single rounding of each coordinate can
        // move a vertex's distance from a plane.
        float noise = MathF.Sqrt(3f) * 0.5f * (MathF.BitIncrement(65536f) - 65536f);
        Assert.True(BrushBspTree.SplitOnPlaneEpsilon > 10f * noise);
        Assert.True(BrushBspTree.SplitOnPlaneEpsilon < 1f / 5f);
    }

    /// <summary>How far, and which way, the prism's corner edge is moved.</summary>
    private enum Nudge
    {
        /// <summary>A few ulps across the plane: in front, or behind the mirror.</summary>
        UlpsOnTheFar,

        /// <summary>A few ulps on the prism's own side of the plane.</summary>
        UlpsOnTheNear,

        /// <summary>0.05 across: past the noise, inside the band.</summary>
        InsideTheBand,

        /// <summary>0.25 across: a real sliver.</summary>
        QuarterUnit,
    }

    /// <summary>
    /// Builds the prism under <paramref name="compliance"/>, moves its corner
    /// edge, and runs <see cref="BrushBspTree.TestBrushToPlaneNumber"/> once.
    /// </summary>
    private static async Task<int> EpsilonBrushesAsync(
        ComplianceOptions compliance, Nudge nudge, bool mirrored)
    {
        BspBuildContext build = await LoadAsync(compliance);
        (BspBrush prism, int candidate) = Prism(build, nudge, mirrored);

        int epsilon = 0;
        int side = BrushBspTree.TestBrushToPlaneNumber(
            build, prism, candidate, out _, out _, ref epsilon);

        // The box straddles the plane in every case, so the vertex test ran.
        Assert.Equal(PlaneSideFlags.Both, side);
        return epsilon;
    }

    /// <summary>
    /// Scores a node holding the prism and a wedge that owns the candidate
    /// plane, and returns the plane <see cref="BrushBspTree.SelectSplitSide"/>
    /// picks, with the candidate's number to compare it against.
    /// </summary>
    /// <remarks>
    /// The wedge is the part of the box (0, -128, -64)-(128, 0, 64) in front of
    /// the candidate plane, so it touches the prism only along the corner
    /// edge. The node's volume is the two brushes' joint bounds, so the
    /// planes of the outer faces cannot divide it and are never scored. What
    /// is left is the candidate (facing the wedge, crossing nothing) and
    /// planes that cut several faces, so the candidate wins unless it is
    /// charged for an epsilon brush.
    /// </remarks>
    private static async Task<(int Winner, int Candidate)> WinnerAsync(
        ComplianceOptions compliance, Nudge nudge)
    {
        BspBuildContext build = await LoadAsync(compliance);
        (BspBrush prism, int candidate) = Prism(build, nudge, mirrored: false);

        BspBrush box = CsgFixture.Box(build, new Vec3(0, -128, -64), new Vec3(128, 0, 64));
        BrushGeometry.SplitBrush(build, box, candidate, out BspBrush? wedge, out _);
        Assert.NotNull(wedge);
        for (int i = 0; i < wedge!.SideCount; i++)
        {
            wedge.Sides[i].Visible = true;
            wedge.Sides[i].TexInfo = 0;
        }

        prism.Next = wedge;

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-64, -128, -64), new Vec3(128, 64, 64));

        Assert.True(BrushBspTree.SelectSplitSide(build, prism, node, out BspBrushSide best));
        return (best.PlaneNumber & ~1, candidate & ~1);
    }

    private static async Task<BspBuildContext> LoadAsync(ComplianceOptions compliance)
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))),
            new() { Compliance = compliance });
        return build;
    }

    /// <summary>
    /// The prism, with its corner edge at (64, -64) moved as
    /// <paramref name="nudge"/> says, and the candidate plane (mirrored or
    /// not) that the edge lies on.
    /// </summary>
    private static (BspBrush Prism, int Candidate) Prism(
        BspBuildContext build, Nudge nudge, bool mirrored)
    {
        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int diagonal = build.Planes.Find(new Vec3(1, 1, 0).Normalise().Normalised, 0f);
        BrushGeometry.SplitBrush(build, box, diagonal, out _, out BspBrush? prism);
        Assert.NotNull(prism);

        int candidate = build.Planes.Find(new Vec3(0.8f, 0.6f, 0f), 12.8f);
        if (mirrored)
        {
            candidate ^= 1;
        }

        Plane plane = build.Planes[candidate];

        // The split's corners carry the clip's rounding; put them back on the
        // integers they are meant to be, so the only residual left is the
        // one this fact chooses.
        for (int i = 0; i < prism!.SideCount; i++)
        {
            prism.Sides[i].Visible = true;
            prism.Sides[i].TexInfo = 0;

            Span<Vec3> points = build.Windings.Points(prism.Sides[i].Winding);
            for (int j = 0; j < points.Length; j++)
            {
                points[j] = new Vec3(
                    MathF.Round(points[j].X), MathF.Round(points[j].Y), MathF.Round(points[j].Z));
            }
        }

        // The edge's residual on the prism's own side of the plane is
        // negative against the plane and positive against its mirror; "far"
        // is the other sign. Walk x an ulp at a time until the residual has
        // the sign asked for, so the fact states the sign it tests rather
        // than trusting how one CPU rounds 0.8 * 64.
        // Moving x up moves the edge in front of the unmirrored plane, so
        // `direction` is the way x has to go for the residual to take `sign`.
        float sign = (nudge == Nudge.UlpsOnTheNear ? -1f : 1f) * (mirrored ? -1f : 1f);
        float direction = sign * (mirrored ? -1f : 1f);
        float x = nudge switch
        {
            Nudge.InsideTheBand => 64f + (direction * 0.05f / 0.8f),
            Nudge.QuarterUnit => 64f + (direction * 0.25f / 0.8f),
            _ => 64f,
        };

        if (nudge is Nudge.UlpsOnTheFar or Nudge.UlpsOnTheNear)
        {
            for (int step = 0; Residual(plane, x) * sign <= 0f; step++)
            {
                Assert.True(step < 8, "the residual should change sign within a few ulps");
                x = direction > 0f ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
            }

            Assert.True(MathF.Abs(Residual(plane, x)) < 1e-4f);
        }

        for (int i = 0; i < prism.SideCount; i++)
        {
            Span<Vec3> points = build.Windings.Points(prism.Sides[i].Winding);
            for (int j = 0; j < points.Length; j++)
            {
                if (points[j].X == 64f && points[j].Y == -64f)
                {
                    points[j] = new Vec3(x, -64f, points[j].Z);
                }
            }
        }

        BrushGeometry.BoundBrush(build, prism);
        return (prism, candidate);
    }

    private static float Residual(Plane plane, float x) =>
        Vec3.Dot(new Vec3(x, -64f, 0f), plane.Normal) - plane.Dist;
}
