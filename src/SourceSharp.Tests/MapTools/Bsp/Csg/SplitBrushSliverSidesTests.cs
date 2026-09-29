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

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <see cref="StockQuirk.SplitBrushSliverSides"/>: whether
/// <see cref="BrushGeometry.SplitBrush"/> hands one half a sliver of a side
/// that only touches the splitting plane, by the sign of that side's rounding.
/// </summary>
/// <remarks>
/// <para>
/// Every fact splits the same brush: the 128-unit box around the origin, cut
/// along its diagonal <c>x + y = 0</c>. The diagonal runs through two of the
/// box's vertical edges, (64, -64) and (-64, 64). The four vertical faces each
/// meet the plane only along one of those edges and are otherwise wholly on
/// one side of it, so with the edges exactly on the plane each goes whole to
/// its own half, and each half is a prism with five sides: two vertical faces,
/// two triangles cut from the top and bottom, and the new side on the plane.
/// </para>
/// <para>
/// The facts move the edge at (64, -64) along x, which moves it across the
/// plane without moving the plane. By 0.004, the edge carries the size of
/// rounding a real clip leaves (a 2fort corner meant to be (452, 1911, 256)
/// came out 0.0024 off in x); by 0.1 it is inside the band SplitBrush already
/// treats as not crossing; by 0.35 it is a quarter of a unit across, a real
/// piece of face.
/// </para>
/// <para>
/// Moving the edge in front of the plane leaves the face <c>y = -64</c> with
/// one edge a hair in front and the rest far behind. Moving it behind does
/// the same to the face <c>x = 64</c> on the other side. Those are the two
/// faces the zero-epsilon clip divides by noise.
/// </para>
/// </remarks>
public class SplitBrushSliverSidesTests
{
    /// <summary>
    /// <b>Stock hands out a sliver by the residual's sign.</b> The edge a few
    /// thousandths in front gives the FRONT half a sliver of the face
    /// <c>y = -64</c>: six sides, the sixth a strip no wider than the nudge.
    /// The same edge a few thousandths behind gives the BACK half a sliver of
    /// the face <c>x = 64</c> instead. Which half has an extra side is decided
    /// by rounding; that is the 2fort mechanism in miniature.
    /// </summary>
    [Fact]
    public async Task StockPutsTheSliverOnWhicheverHalfTheEdgeRoundedInto()
    {
        Split across = await SplitAsync(ComplianceOptions.Stock, Nudge.NoiseAcross);
        Assert.Equal(6, across.Front.SideCount);
        Assert.Equal(5, across.Back.SideCount);
        AssertSliver(across.Build, across.Front, across.FaceYMinus, across.Diagonal);

        Split short_ = await SplitAsync(ComplianceOptions.Stock, Nudge.NoiseShort);
        Assert.Equal(5, short_.Front.SideCount);
        Assert.Equal(6, short_.Back.SideCount);
        AssertSliver(short_.Build, short_.Back, short_.FaceXPlus, short_.Diagonal);
    }

    /// <summary>
    /// <b>Correct does not.</b> Both halves have five sides whichever way the
    /// edge rounded: a side reaching less than
    /// <see cref="BrushGeometry.SliverSideEpsilon"/> across goes whole to the
    /// half it is on.
    /// </summary>
    [Fact]
    public async Task CorrectGivesNeitherHalfASliverWhicheverWayTheEdgeRounds()
    {
        foreach (Nudge nudge in new[] { Nudge.None, Nudge.NoiseAcross, Nudge.NoiseShort })
        {
            Split split = await SplitAsync(ComplianceOptions.Correct, nudge);
            Assert.Equal(5, split.Front.SideCount);
            Assert.Equal(5, split.Back.SideCount);
            Assert.False(HasPlane(split.Front, split.FaceYMinus));
            Assert.False(HasPlane(split.Back, split.FaceXPlus));
        }
    }

    /// <summary>
    /// The band, not only the noise: an edge 0.07 across is far past any
    /// rounding but inside the 0.1 within which SplitBrush does not cut a
    /// whole brush either. Stock cuts the side and gives the front half a
    /// 0.07 strip of it; Correct does not.
    /// </summary>
    [Fact]
    public async Task AnEdgeInsideTheBandIsCutOnlyUnderStock()
    {
        Split stock = await SplitAsync(ComplianceOptions.Stock, Nudge.InsideTheBand);
        Assert.Equal(6, stock.Front.SideCount);
        Assert.True(HasPlane(stock.Front, stock.FaceYMinus));

        Split correct = await SplitAsync(ComplianceOptions.Correct, Nudge.InsideTheBand);
        Assert.Equal(5, correct.Front.SideCount);
        Assert.False(HasPlane(correct.Front, correct.FaceYMinus));
    }

    /// <summary>
    /// <b>A real piece of face is still cut.</b> An edge a quarter of a unit
    /// in front of the plane puts a quarter-unit strip of the face
    /// <c>y = -64</c> in the front half, and both policies give it to that
    /// half as a side.
    /// </summary>
    [Fact]
    public async Task BothPoliciesCutASideThatCrossesByAQuarterUnit()
    {
        foreach (ComplianceOptions policy in new[] { ComplianceOptions.Stock, ComplianceOptions.Correct })
        {
            Split split = await SplitAsync(policy, Nudge.QuarterUnit);
            Assert.Equal(6, split.Front.SideCount);
            Assert.Equal(5, split.Back.SideCount);
            Assert.True(HasPlane(split.Front, split.FaceYMinus));
        }
    }

    /// <summary>
    /// <b>Under Stock the sliver is not inert.</b> The split heuristic asks a
    /// brush whether it owns a candidate plane before anything else, and one
    /// that does is FACING it: free to split on, and counted in the plane's
    /// favour. The front half does not reach the plane <c>y = -64</c> (its
    /// box ends on it), yet with the sliver it answers "facing".
    /// </summary>
    [Fact]
    public async Task UnderStockTheSliverMakesAHalfFaceAPlaneItDoesNotReach()
    {
        Split split = await SplitAsync(ComplianceOptions.Stock, Nudge.NoiseAcross);
        int epsilon = 0;
        int side = BrushBspTree.TestBrushToPlaneNumber(
            split.Build, split.Front, split.FaceYMinus, out _, out _, ref epsilon);

        Assert.NotEqual(0, side & PlaneSideFlags.Facing);
    }

    /// <summary>
    /// <b>Under Correct it answers the same whichever way the edge
    /// rounded.</b> Neither half faces the plane of a face it only touches
    /// along an edge.
    /// </summary>
    [Fact]
    public async Task UnderCorrectNeitherHalfFacesAPlaneItDoesNotReach()
    {
        foreach (Nudge nudge in new[] { Nudge.NoiseAcross, Nudge.NoiseShort })
        {
            Split split = await SplitAsync(ComplianceOptions.Correct, nudge);
            int epsilon = 0;
            int front = BrushBspTree.TestBrushToPlaneNumber(
                split.Build, split.Front, split.FaceYMinus, out _, out _, ref epsilon);
            int back = BrushBspTree.TestBrushToPlaneNumber(
                split.Build, split.Back, split.FaceXPlus, out _, out _, ref epsilon);

            Assert.Equal(0, front & PlaneSideFlags.Facing);
            Assert.Equal(0, back & PlaneSideFlags.Facing);
        }
    }

    /// <summary>
    /// The band is SplitBrush's own whole-brush threshold and the split
    /// heuristic's, and clears the rounding noise by a wide margin. A fact
    /// rather than a comment, so the numbers cannot drift apart unnoticed.
    /// </summary>
    [Fact]
    public void TheSliverBandIsTheSplitBandAndClearsTheNoise()
    {
        Assert.Equal(0.1f, BrushGeometry.SliverSideEpsilon);
        Assert.Equal(BrushBspTree.SplitOnPlaneEpsilon, BrushGeometry.SliverSideEpsilon);

        // Half an ulp at the base winding's 65536, over three coordinates of
        // a unit normal: the worst a single rounding of each coordinate can
        // move a vertex's distance from a plane.
        float noise = MathF.Sqrt(3f) * 0.5f * (MathF.BitIncrement(65536f) - 65536f);
        Assert.True(BrushGeometry.SliverSideEpsilon > 10f * noise);
    }

    /// <summary>How far, and which way, the edge at (64, -64) is moved in x.</summary>
    private enum Nudge
    {
        /// <summary>Left on the plane.</summary>
        None,

        /// <summary>0.004 in front: rounding-sized.</summary>
        NoiseAcross,

        /// <summary>0.004 behind: rounding-sized, the other sign.</summary>
        NoiseShort,

        /// <summary>0.1 in x, 0.07 in front: inside the band.</summary>
        InsideTheBand,

        /// <summary>0.35 in x, a quarter of a unit in front.</summary>
        QuarterUnit,
    }

    private sealed record Split(
        BspBuildContext Build,
        BspBrush Front,
        BspBrush Back,
        int Diagonal,
        int FaceYMinus,
        int FaceXPlus);

    /// <summary>
    /// Builds the box under <paramref name="compliance"/>, moves its edge at
    /// (64, -64), and splits it on the diagonal.
    /// </summary>
    private static async Task<Split> SplitAsync(ComplianceOptions compliance, Nudge nudge)
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))),
            new() { Compliance = compliance });

        BspBrush box = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));
        int diagonal = build.Planes.Find(new Vec3(1, 1, 0).Normalise().Normalised, 0f);
        int faceYMinus = PlaneOf(build, box, new Vec3(0, -1, 0));
        int faceXPlus = PlaneOf(build, box, new Vec3(1, 0, 0));

        float dx = nudge switch
        {
            Nudge.NoiseAcross => 0.004f,
            Nudge.NoiseShort => -0.004f,
            Nudge.InsideTheBand => 0.1f,
            Nudge.QuarterUnit => 0.35f,
            _ => 0f,
        };

        for (int i = 0; i < box.SideCount; i++)
        {
            Span<Vec3> points = build.Windings.Points(box.Sides[i].Winding);
            for (int j = 0; j < points.Length; j++)
            {
                // The box's corners are integers after its windings are
                // built; snap them, so the only residual is the one chosen.
                Vec3 p = new(MathF.Round(points[j].X), MathF.Round(points[j].Y), MathF.Round(points[j].Z));
                if (p.X == 64f && p.Y == -64f)
                {
                    p = new Vec3(64f + dx, -64f, p.Z);
                }

                points[j] = p;
            }
        }

        BrushGeometry.BoundBrush(build, box);

        // SplitBrush works translated to the centre of the brush's bounds.
        // Keep that centre at the origin, so the translation is by exactly
        // zero and adds no rounding of its own: otherwise the OTHER edge on
        // the plane, at (-64, 64), picks up a residual from the translation
        // and its faces are divided by noise too, which is this quirk but not
        // the case a fact should be about. The bounds are used for nothing
        // else before the halves are bounded afresh.
        box.Mins = new Vec3(-box.Maxs.X, box.Mins.Y, box.Mins.Z);

        // The residual has the sign and size the fact is about.
        Plane plane = build.Planes[diagonal];
        float residual = Vec3.Dot(new Vec3(64f + dx, -64f, 0f), plane.Normal) - plane.Dist;
        Assert.Equal(MathF.Sign(dx), MathF.Sign(residual));

        BrushGeometry.SplitBrush(build, box, diagonal, out BspBrush? front, out BspBrush? back);
        Assert.NotNull(front);
        Assert.NotNull(back);
        return new Split(build, front!, back!, diagonal, faceYMinus, faceXPlus);
    }

    private static int PlaneOf(BspBuildContext build, BspBrush brush, Vec3 normal)
    {
        for (int i = 0; i < brush.SideCount; i++)
        {
            int num = brush.Sides[i].PlaneNumber;
            if (build.Planes[num].Normal == normal)
            {
                return num;
            }
        }

        throw new InvalidOperationException($"the box has no side with normal {normal}");
    }

    private static bool HasPlane(BspBrush brush, int planeNumber)
    {
        for (int i = 0; i < brush.SideCount; i++)
        {
            if (brush.Sides[i].PlaneNumber == planeNumber)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The brush has a side on <paramref name="planeNumber"/>, and that side
    /// is a sliver: every point of it within 0.01 of the splitting plane.
    /// </summary>
    private static void AssertSliver(BspBuildContext build, BspBrush brush, int planeNumber, int splitter)
    {
        Plane plane = build.Planes[splitter];
        for (int i = 0; i < brush.SideCount; i++)
        {
            if (brush.Sides[i].PlaneNumber != planeNumber)
            {
                continue;
            }

            foreach (Vec3 p in build.Windings.Points(brush.Sides[i].Winding))
            {
                Assert.True(MathF.Abs(Vec3.Dot(p, plane.Normal) - plane.Dist) < 0.01f);
            }

            return;
        }

        Assert.Fail("the brush has no side on the plane");
    }
}
