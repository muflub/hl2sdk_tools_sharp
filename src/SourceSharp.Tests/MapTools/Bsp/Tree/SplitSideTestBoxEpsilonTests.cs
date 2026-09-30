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
/// <see cref="StockQuirk.SplitSideTestBoxEpsilon"/>: whether the split
/// heuristic sides a brush that only touches a candidate plane by the
/// rounding of its touching corner.
/// </summary>
/// <remarks>
/// <para>
/// Most facts here use one brush and one plane. The brush is the box
/// (-64, -64, -64)-(64, 64, 64). The plane has the normal (0.8, 0.6, 0) and
/// passes a chosen distance <c>r</c> short of the box's corner edge at
/// (64, 64), so the box's leading corner is <c>r</c> in front of it and its
/// trailing corner 179 units behind. With <c>r</c> a few ten-thousandths or a
/// few thousandths, the brush touches the plane and the only thing that
/// differs is where its corner rounded to, either side of the box test's
/// 0.001. With <c>r</c> = 0.05 it touches it by more than any rounding but
/// less than the 0.1 inside which <see cref="BrushGeometry.SplitBrush"/>
/// would not cut it; with 0.25 it really crosses.
/// </para>
/// <para>
/// The mirrored plane (the same plane number with the low bit flipped) puts
/// the box in front and the corner <c>r</c> behind, which is the other half of
/// the box test, where stock's slanted-plane path is asymmetric.
/// </para>
/// </remarks>
public class SplitSideTestBoxEpsilonTests
{
    /// <summary>
    /// <b>Stock sides the brush by its corner's rounding.</b> Its corner
    /// 0.0004 in front reads as behind; 0.0016 in front, as both. The 2fort
    /// flip was this 0.0016.
    /// </summary>
    [Fact]
    public async Task StockSidesATouchingBrushByItsCornersRounding()
    {
        Assert.Equal(PlaneSideFlags.Back, await SideAsync(ComplianceOptions.Stock, 0.0004f, mirrored: false));
        Assert.Equal(PlaneSideFlags.Both, await SideAsync(ComplianceOptions.Stock, 0.0016f, mirrored: false));
    }

    /// <summary>
    /// <b>Correct does not.</b> Either way the corner rounded, the brush is
    /// behind, which is where <see cref="BrushGeometry.SplitBrush"/> would put
    /// it.
    /// </summary>
    [Fact]
    public async Task CorrectCallsATouchingBrushBehindWhicheverWayItsCornerRounds()
    {
        Assert.Equal(PlaneSideFlags.Back, await SideAsync(ComplianceOptions.Correct, 0.0004f, mirrored: false));
        Assert.Equal(PlaneSideFlags.Back, await SideAsync(ComplianceOptions.Correct, 0.0016f, mirrored: false));
    }

    /// <summary>
    /// The other half of the test. Stock's slanted-plane path calls a box
    /// behind when its trailing corner is under <b>+</b>0.001, so a box in
    /// front of the mirrored plane with its corner a hair behind it is both,
    /// however small the hair. Correct calls it in front.
    /// </summary>
    [Fact]
    public async Task AgainstTheMirroredPlaneStockCallsItBothAndCorrectInFront()
    {
        Assert.Equal(PlaneSideFlags.Both, await SideAsync(ComplianceOptions.Stock, 0.0004f, mirrored: true));
        Assert.Equal(PlaneSideFlags.Both, await SideAsync(ComplianceOptions.Stock, 0.0016f, mirrored: true));
        Assert.Equal(PlaneSideFlags.Front, await SideAsync(ComplianceOptions.Correct, 0.0004f, mirrored: true));
        Assert.Equal(PlaneSideFlags.Front, await SideAsync(ComplianceOptions.Correct, 0.0016f, mirrored: true));
    }

    /// <summary>
    /// The band, not only the noise: a corner 0.05 across is past any
    /// rounding but inside the 0.1 that SplitBrush treats as not crossing.
    /// Stock calls the brush both-sided; Correct calls it on the side
    /// SplitBrush would send it to.
    /// </summary>
    [Fact]
    public async Task ABoxInsideTheBandIsOneSidedOnlyUnderCorrect()
    {
        Assert.Equal(PlaneSideFlags.Both, await SideAsync(ComplianceOptions.Stock, 0.05f, mirrored: false));
        Assert.Equal(PlaneSideFlags.Back, await SideAsync(ComplianceOptions.Correct, 0.05f, mirrored: false));
        Assert.Equal(PlaneSideFlags.Front, await SideAsync(ComplianceOptions.Correct, 0.05f, mirrored: true));
    }

    /// <summary>
    /// A brush a quarter of a unit across the plane really is cut by it, and
    /// both policies say both.
    /// </summary>
    [Fact]
    public async Task BothPoliciesCallABoxAQuarterUnitAcrossBothSided()
    {
        foreach (ComplianceOptions policy in new[] { ComplianceOptions.Stock, ComplianceOptions.Correct })
        {
            Assert.Equal(PlaneSideFlags.Both, await SideAsync(policy, 0.25f, mirrored: false));
            Assert.Equal(PlaneSideFlags.Both, await SideAsync(policy, 0.25f, mirrored: true));
        }
    }

    /// <summary>
    /// The axial path of the box test has the same cliff, symmetric this
    /// time: a box whose face is 0.0016 past an axial plane is both-sided
    /// under Stock and behind under Correct, and 0.0004 past it is behind
    /// under both.
    /// </summary>
    [Fact]
    public async Task TheAxialPathUsesTheSameBand()
    {
        Assert.Equal(PlaneSideFlags.Back, await AxialSideAsync(ComplianceOptions.Stock, 0.0004f));
        Assert.Equal(PlaneSideFlags.Both, await AxialSideAsync(ComplianceOptions.Stock, 0.0016f));
        Assert.Equal(PlaneSideFlags.Both, await AxialSideAsync(ComplianceOptions.Stock, 0.05f));
        Assert.Equal(PlaneSideFlags.Back, await AxialSideAsync(ComplianceOptions.Correct, 0.0004f));
        Assert.Equal(PlaneSideFlags.Back, await AxialSideAsync(ComplianceOptions.Correct, 0.0016f));
        Assert.Equal(PlaneSideFlags.Back, await AxialSideAsync(ComplianceOptions.Correct, 0.05f));
        Assert.Equal(PlaneSideFlags.Both, await AxialSideAsync(ComplianceOptions.Correct, 0.25f));
    }

    /// <summary>
    /// A box inside the band on BOTH sides, thinner than 0.2 and lying on
    /// the plane, is behind, as SplitBrush would have it. Stock's axial box
    /// test answers 0 for it, which the node split then drops; the banded
    /// test never answers 0.
    /// </summary>
    [Fact]
    public void ABoxInsideTheBandOnBothSidesIsBehindAndNeverDropped()
    {
        Plane axial = new(new Vec3(1, 0, 0), 64f);
        Vec3 mins = new(63.95f, -8, -8);
        Vec3 maxs = new(64.05f, 8, 8);

        Assert.Equal(PlaneSideFlags.Both, BrushBspTree.BoxOnPlaneSide(mins, maxs, axial, PlaneType.X));
        Assert.Equal(PlaneSideFlags.Back, BrushBspTree.BoxOnPlaneSideBeyondBand(mins, maxs, axial, PlaneType.X));

        Vec3 flatMins = new(63.9995f, -8, -8);
        Vec3 flatMaxs = new(64.0005f, 8, 8);
        Assert.Equal(0, BrushBspTree.BoxOnPlaneSide(flatMins, flatMaxs, axial, PlaneType.X));
        Assert.Equal(PlaneSideFlags.Back, BrushBspTree.BoxOnPlaneSideBeyondBand(flatMins, flatMaxs, axial, PlaneType.X));

        Plane slanted = new(new Vec3(0.6f, 0.8f, 0f), 0f);
        Assert.Equal(PlaneSideFlags.Back, BrushBspTree.BoxOnPlaneSideBeyondBand(
            new Vec3(-0.01f, -0.01f, -8), new Vec3(0.01f, 0.01f, 8), slanted, PlaneType.AnyY));
    }

    /// <summary>
    /// <b>Under Stock the corner's rounding picks the splitter.</b> A node
    /// holds the box and a wedge that owns the candidate plane. With the
    /// box's corner 0.0004 in front, the box is behind, the candidate costs
    /// nothing and wins; 0.0016 in front, the box is both-sided, goes on to
    /// the vertex test, is charged 1000 as an epsilon brush, and another
    /// plane wins.
    /// </summary>
    [Fact]
    public async Task UnderStockTheCornersRoundingChangesTheSplitter()
    {
        (int near, int candidate) = await WinnerAsync(ComplianceOptions.Stock, 0.0004f);
        (int far, _) = await WinnerAsync(ComplianceOptions.Stock, 0.0016f);

        Assert.Equal(candidate, near);
        Assert.NotEqual(candidate, far);
    }

    /// <summary>
    /// <b>This quirk alone takes that out.</b> Stock in every respect but
    /// this one, including the epsilon-brush test that charged the 1000: the
    /// box is behind either way, never reaches that test, and the candidate
    /// wins both nodes. So the flip above is this quirk's, not
    /// <see cref="StockQuirk.SplitEpsilonBrushOnPlane"/>'s.
    /// </summary>
    [Fact]
    public async Task WithOnlyThisQuirkCorrectedTheSplitterDoesNotDependOnIt()
    {
        ComplianceOptions stockButThis = ComplianceOptions.Stock.Flipping(StockQuirk.SplitSideTestBoxEpsilon);

        (int near, int candidate) = await WinnerAsync(stockButThis, 0.0004f);
        (int far, _) = await WinnerAsync(stockButThis, 0.0016f);

        Assert.Equal(candidate, near);
        Assert.Equal(candidate, far);
    }

    /// <summary>
    /// <b>The split itself does not change.</b> A both-sided brush that
    /// reaches less than 0.1 across is handed to SplitBrush, which copies it
    /// whole behind; a brush sided behind is copied behind directly. Either
    /// way the node's back list holds the same brush and its front list
    /// nothing, which is why Correct changes only the score's counts.
    /// </summary>
    [Fact]
    public async Task TheListsTheNodeIsSplitIntoAreTheSameEitherWay()
    {
        BspBuildContext build = await LoadAsync(ComplianceOptions.Stock);
        (BspBrush box, int candidate) = TouchingBox(build, 0.0016f, mirrored: false);

        BspNode node = build.AllocNode();
        node.PlaneNumber = candidate & ~1;

        foreach (int side in new[] { PlaneSideFlags.Both, PlaneSideFlags.Back })
        {
            box.Side = side;
            BrushBspTree.SplitBrushList(build, box, node, out BspBrush? front, out BspBrush? back);

            Assert.Null(front);
            Assert.NotNull(back);
            Assert.Null(back!.Next);
            Assert.Equal(box.Mins, back.Mins);
            Assert.Equal(box.Maxs, back.Maxs);
            Assert.Equal(box.SideCount, back.SideCount);
        }
    }

    /// <summary>
    /// Builds the box and the plane under <paramref name="compliance"/> and
    /// sides the box once with <see cref="BrushBspTree.TestBrushToPlaneNumber"/>.
    /// </summary>
    private static async Task<int> SideAsync(ComplianceOptions compliance, float r, bool mirrored)
    {
        BspBuildContext build = await LoadAsync(compliance);
        (BspBrush box, int candidate) = TouchingBox(build, r, mirrored);

        int epsilon = 0;
        return BrushBspTree.TestBrushToPlaneNumber(build, box, candidate, out _, out _, ref epsilon);
    }

    private static async Task<int> AxialSideAsync(ComplianceOptions compliance, float r)
    {
        BspBuildContext build = await LoadAsync(compliance);
        BspBrush box = CsgFixture.Box(build, new Vec3(-64.5f, -64.5f, -64.5f), new Vec3(64.5f, 64.5f, 64.5f));
        MakeVisible(box);

        // Appended rather than found: the table's 0.01 match would hand back
        // the box's own face plane for any r under 0.01.
        int candidate = build.Planes.Create(new Vec3(1, 0, 0), 64.5f - r);
        Assert.Equal(64.5f - r, build.Planes[candidate].Dist);

        int epsilon = 0;
        return BrushBspTree.TestBrushToPlaneNumber(build, box, candidate, out _, out _, ref epsilon);
    }

    /// <summary>
    /// Scores a node holding the box and a wedge in front of the candidate
    /// that owns it, and returns the plane
    /// <see cref="BrushBspTree.SelectSplitSide"/> picks, with the candidate's
    /// number to compare it against.
    /// </summary>
    /// <remarks>
    /// The wedge is the part of the box (0, 0, -64)-(192, 192, 64) in front of
    /// the candidate. The candidate faces it, crosses nothing and splits the
    /// node one against one, so it scores 5; every other plane that divides
    /// the node's volume cuts several faces of the wedge or the box and
    /// scores less, unless the candidate is charged 1000 for the box.
    /// </remarks>
    private static async Task<(int Winner, int Candidate)> WinnerAsync(ComplianceOptions compliance, float r)
    {
        BspBuildContext build = await LoadAsync(compliance);
        (BspBrush box, int candidate) = TouchingBox(build, r, mirrored: false);

        BspBrush block = CsgFixture.Box(build, new Vec3(0, 0, -64), new Vec3(192, 192, 64));
        BrushGeometry.SplitBrush(build, block, candidate, out BspBrush? wedge, out _);
        Assert.NotNull(wedge);
        MakeVisible(wedge!);

        box.Next = wedge;

        BspNode node = build.AllocNode();
        node.Volume = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(192, 192, 64));

        Assert.True(BrushBspTree.SelectSplitSide(build, box, node, out BspBrushSide best));
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
    /// The box, and the plane (mirrored or not) that its corner edge at
    /// (64, 64) is <paramref name="r"/> in front of.
    /// </summary>
    private static (BspBrush Box, int Candidate) TouchingBox(BspBuildContext build, float r, bool mirrored)
    {
        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        MakeVisible(box);

        Vec3 normal = new(0.8f, 0.6f, 0f);
        float corner = Vec3.Dot(new Vec3(64, 64, 64), normal);
        int candidate = build.Planes.Find(normal, corner - r);
        if (mirrored)
        {
            candidate ^= 1;
        }

        // The residual the fact asks for, to within the rounding of one
        // subtraction at 89.6, and on the side it asks for.
        Plane plane = build.Planes[candidate];
        float leading = Vec3.Dot(new Vec3(64, 64, 64), plane.Normal) - plane.Dist;
        Assert.Equal(mirrored ? -r : r, leading, 1e-5f);

        return (box, candidate);
    }

    private static void MakeVisible(BspBrush brush)
    {
        for (int i = 0; i < brush.SideCount; i++)
        {
            brush.Sides[i].Visible = true;
            brush.Sides[i].TexInfo = 0;
        }
    }
}
