using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
///: subtraction, the list splices, the bite rules and
/// <c>ChopBrushes</c>.
/// </summary>
public class BrushCsgTests
{
    [Fact]
    public async Task BrushesDisjointSaysTrueForTwoSeparatedBoxes()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(16, 16, 16));
        BspBrush b = CsgFixture.Box(build, new Vec3(64, 0, 0), new Vec3(80, 16, 16));

        Assert.True(BrushCsg.BrushesDisjoint(a, b));
    }

    /// <summary>
    /// The box test is <c>&gt;=</c> and <c>&lt;=</c>, so two boxes sharing a
    /// face are disjoint. That is what keeps stacked blocks from biting each
    /// other.
    /// </summary>
    [Fact]
    public async Task BrushesDisjointSaysTrueForTwoBoxesThatMerelyTouch()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(16, 16, 16));
        BspBrush b = CsgFixture.Box(build, new Vec3(16, 0, 0), new Vec3(32, 16, 16));

        Assert.True(BrushCsg.BrushesDisjoint(a, b));
    }

    [Fact]
    public async Task BrushesDisjointSaysFalseForOverlappingBoxes()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(16, 16, 16));
        BspBrush b = CsgFixture.Box(build, new Vec3(8, 8, 8), new Vec3(24, 24, 24));

        Assert.False(BrushCsg.BrushesDisjoint(a, b));
    }

    /// <summary>
    /// The second half of the test looks for a plane of one brush that is the
    /// exact mirror of a plane of the other, which is why two boxes whose
    /// BOUNDS overlap can still be disjoint.
    /// </summary>
    [Fact]
    public async Task BrushesDisjointFindsMirroredPlanesWhenTheBoundsOverlap()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = build.AllocBrush(2);
        a.AddSide(new BspBrushSide { PlaneNumber = build.Planes.Find(new Vec3(0, 0, 1), 10f) });
        a.AddSide(new BspBrushSide { PlaneNumber = build.Planes.Find(new Vec3(0, 0, -1), 0f) });
        a.Mins = new Vec3(-10, -10, -10);
        a.Maxs = new Vec3(10, 10, 10);

        BspBrush b = build.AllocBrush(1);
        b.AddSide(new BspBrushSide
        {
            PlaneNumber = build.Planes.Find(new Vec3(0, 0, 1), 10f) ^ 1,
        });
        b.Mins = new Vec3(-10, -10, -10);
        b.Maxs = new Vec3(10, 10, 10);

        Assert.True(BrushCsg.BrushesDisjoint(a, b));
    }

    /// <summary>
    /// <c>SubtractBrush</c> returns its own first argument when the two did not
    /// really intersect, and every caller has to recognise that by reference.
    /// </summary>
    [Fact]
    public async Task SubtractBrushReturnsTheInputItselfWhenNothingIsTakenAway()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(16, 16, 16));
        BspBrush b = CsgFixture.Box(build, new Vec3(64, 64, 64), new Vec3(80, 80, 80));

        Assert.Same(a, BrushCsg.SubtractBrush(build, a, b));
    }

    [Fact]
    public async Task SubtractBrushReturnsNothingWhenTheBiterSwallowsTheBitten()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(16, 16, 16));
        BspBrush b = CsgFixture.Box(build, new Vec3(-64, -64, -64), new Vec3(64, 64, 64));

        Assert.Null(BrushCsg.SubtractBrush(build, a, b));
    }

    /// <summary>
    /// A cube with a corner bitten out becomes three fragments, one per plane
    /// of the biter that actually cuts it.
    /// </summary>
    [Fact]
    public async Task SubtractBrushFragmentsACornerBiteIntoThree()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(64, 64, 64));
        BspBrush b = CsgFixture.Box(build, new Vec3(32, 32, 32), new Vec3(128, 128, 128));

        BspBrush? result = BrushCsg.SubtractBrush(build, a, b);

        Assert.Equal(3, BrushCsg.CountBrushList(result));
    }

    [Fact]
    public async Task IntersectBrushIsNullForDisjointBrushesAndABrushForOverlap()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush a = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(64, 64, 64));
        BspBrush far = CsgFixture.Box(build, new Vec3(512, 0, 0), new Vec3(576, 64, 64));
        BspBrush near = CsgFixture.Box(build, new Vec3(32, 32, 32), new Vec3(128, 128, 128));

        Assert.Null(BrushCsg.IntersectBrush(build, a, far));

        BspBrush? overlap = BrushCsg.IntersectBrush(build, a, near);
        Assert.NotNull(overlap);
        Assert.Null(overlap!.Next);
        Assert.Equal(32f * 32f * 32f, BrushGeometry.BrushVolume(build, overlap), 1f);
    }

    [Fact]
    public async Task AddBrushListToTailKeepsTheAppendedListsOrderAndReturnsItsLast()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush head = CsgFixture.Box(build, new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        BspBrush first = CsgFixture.Box(build, new Vec3(2, 0, 0), new Vec3(3, 1, 1));
        BspBrush second = CsgFixture.Box(build, new Vec3(4, 0, 0), new Vec3(5, 1, 1));
        first.Next = second;

        BspBrush tail = BrushCsg.AddBrushListToTail(first, head);

        Assert.Same(second, tail);
        Assert.Same(first, head.Next);
        Assert.Same(second, first.Next);
        Assert.Null(second.Next);
    }

    /// <summary>
    /// <b><c>CullList</c> reverses.</b> Every element is pushed onto the head
    /// of a new list, and <c>ChopBrushes</c> then rescans from the top — so the
    /// order brushes are compared in flips on every bite.
    /// </summary>
    [Fact]
    public async Task CullListComesBackReversedWithTheSkippedBrushGone()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (1, 1, 1)),
                (UnitMap.Plain, (8, 0, 0), (9, 1, 1)),
                (UnitMap.Plain, (16, 0, 0), (17, 1, 1))));

        BspBrush? list = CsgFixture.AllBrushes(build);
        List<BspBrush> before = CsgFixture.ToList(list);

        BspBrush? culled = BrushCsg.CullList(build, list, before[1]);

        Assert.Equal([before[2], before[0]], CsgFixture.ToList(culled));
    }

    [Fact]
    public async Task MakeBspBrushListComesOutInReverseMapOrder()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (1, 1, 1)),
                (UnitMap.Plain, (8, 0, 0), (9, 1, 1)),
                (UnitMap.Plain, (16, 0, 0), (17, 1, 1))));

        BspBrush? list = CsgFixture.AllBrushes(build);

        Assert.Equal([3, 2, 1], CsgFixture.OriginalIds(list));
    }

    [Fact]
    public async Task MakeBspBrushListSkipsABrushEntirelyOutsideTheBlock()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (2048, 0, 0), (2112, 64, 64))));

        BlockGridBounds(0, 0, out Vec3 mins, out Vec3 maxs);

        BspBrush? list = BrushCsg.MakeBspBrushList(
            build, 0, build.Map.BrushCount, mins, maxs, DetailScreen.FullDetail);

        Assert.Equal([1], CsgFixture.OriginalIds(list));
    }

    /// <summary>
    /// A side lying in a block boundary is marked <c>TEXINFO_NODE</c> and made
    /// invisible, so the seam between blocks never becomes geometry.
    /// </summary>
    [Fact]
    public async Task ClipBrushToBoxMarksTheSidesItCutOnAsNodeSides()
    {
        // Inside the block in Y and Z, across its far edge in X, so exactly one
        // boundary plane is involved.
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (512, 64, -64), (1536, 192, 64))));

        BlockGridBounds(0, 0, out Vec3 mins, out Vec3 maxs);

        BspBrush? list = BrushCsg.MakeBspBrushList(
            build, 0, build.Map.BrushCount, mins, maxs, DetailScreen.FullDetail);

        Assert.NotNull(list);
        Assert.Equal(1024f, list!.Maxs.X, 0.001f);

        int nodeSides = 0;
        for (int i = 0; i < list.SideCount; i++)
        {
            if (list.Sides[i].TexInfo == BspBrushSide.TexInfoNode)
            {
                nodeSides++;
                Assert.False(list.Sides[i].Visible);
            }
        }

        Assert.Equal(1, nodeSides);
    }

    [Fact]
    public async Task MakeBspBrushListWithNoDetailDropsDetailBrushes()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (128, 0, 0), (192, 64, 64))));

        map.Brushes[1].Contents |= (int)BrushContents.Detail;

        BspBrush? structural = BrushCsg.MakeBspBrushList(
            build, 0, map.BrushCount, CsgFixture.WorldMins, CsgFixture.WorldMaxs,
            DetailScreen.NoDetail);
        BspBrush? detail = BrushCsg.MakeBspBrushList(
            build, 0, map.BrushCount, CsgFixture.WorldMins, CsgFixture.WorldMaxs,
            DetailScreen.OnlyDetail);

        Assert.Equal([1], CsgFixture.OriginalIds(structural));
        Assert.Equal([2], CsgFixture.OriginalIds(detail));
    }

    /// <summary>
    /// <c>MakeBrushWindings</c> sets <c>visible</c> on every side that got a
    /// winding, so a freshly loaded map has nothing
    /// invisible. That is what makes the reference implementation's "hints are always
    /// visible" look like a no-op, and the fact below is why it is not.
    /// </summary>
    [Fact]
    public async Task EverySideOfAFreshlyLoadedBrushIsAlreadyVisible()
    {
        (_, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Hint, (0, 0, 0), (64, 64, 64))));

        Assert.All(map.BrushSides, side => Assert.True(side.Visible));
    }

    /// <summary>
    /// <b> only ever matters on the SECOND world pass.</b>
    /// <c>MarkVisibleSides</c> (Phase 3c) clears <c>visible</c> on every map
    /// side that did not become a face, and <c>ProcessWorldModel</c> then
    /// rebuilds the whole world from those same map brushes.
    /// A hint side cleared by that pass is raised again
    /// here, and a plain side is not — which is how a hint brush keeps shaping
    /// the tree after the optimiser has decided it renders nothing.
    /// </summary>
    [Fact]
    public async Task AHintSideIsForcedBackToVisibleWhereAPlainSideIsNot()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Hint, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (128, 0, 0), (192, 64, 64))));

        foreach (MapBrushSide side in map.BrushSides)
        {
            side.Visible = false;
        }

        List<BspBrush> brushes = CsgFixture.ToList(CsgFixture.AllBrushes(build));
        BspBrush plain = brushes[0];
        BspBrush hint = brushes[1];

        for (int i = 0; i < hint.SideCount; i++)
        {
            Assert.True(hint.Sides[i].Visible);
        }

        for (int i = 0; i < plain.SideCount; i++)
        {
            Assert.False(plain.Sides[i].Visible);
        }
    }

    [Theory]
    // b1 contents, b2 contents, may b1 bite b2
    [InlineData(BrushContents.Solid, BrushContents.Solid, true)]
    [InlineData(BrushContents.Solid, BrushContents.Water, true)]
    [InlineData(BrushContents.Solid | BrushContents.Detail, BrushContents.Solid, false)]
    [InlineData(
        BrushContents.Solid | BrushContents.Detail,
        BrushContents.Solid | BrushContents.Detail,
        true)]
    [InlineData(BrushContents.Water, BrushContents.Solid, false)]
    [InlineData(BrushContents.Grate, BrushContents.Window, true)]
    [InlineData(BrushContents.Grate, BrushContents.Water, false)]
    [InlineData(BrushContents.AreaPortal, BrushContents.Water, true)]
    [InlineData(BrushContents.AreaPortal, BrushContents.Slime, true)]
    [InlineData(BrushContents.AreaPortal, BrushContents.Solid, false)]
    public async Task BrushGreaterOrEqualFollowsTheFourRulesInOrder(
        BrushContents biter,
        BrushContents bitten,
        bool expected)
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (32, 32, 32), (96, 96, 96))));

        map.Brushes[0].Contents = (int)biter;
        map.Brushes[1].Contents = (int)bitten;

        BspBrush? list = CsgFixture.AllBrushes(build);
        List<BspBrush> brushes = CsgFixture.ToList(list);

        // The list is reversed, so brushes[1] is map brush 0.
        Assert.Equal(expected, BrushCsg.BrushGreaterOrEqual(brushes[1], brushes[0]));
    }

    /// <summary>
    /// An areaportal biting water is rule ONE, so it wins even though an
    /// areaportal is neither solid nor transparent — which rules two to four
    /// would all decline.
    /// </summary>
    [Fact]
    public async Task AnAreaportalBitesWaterEvenThoughNoLaterRuleWouldLetIt()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (32, 32, 32), (96, 96, 96))));

        map.Brushes[0].Contents = (int)BrushContents.AreaPortal;
        map.Brushes[1].Contents = (int)BrushContents.Water;

        List<BspBrush> brushes = CsgFixture.ToList(CsgFixture.AllBrushes(build));
        BspBrush areaportal = brushes[1];
        BspBrush water = brushes[0];

        Assert.True(BrushCsg.BrushGreaterOrEqual(areaportal, water));
        Assert.False(BrushCsg.BrushGreaterOrEqual(water, areaportal));
    }

    [Fact]
    public async Task ChopBrushesLeavesTwoDisjointBrushesAlone()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (128, 0, 0), (192, 64, 64))));

        BspBrush? chopped = BrushCsg.ChopBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(2, BrushCsg.CountBrushList(chopped));
    }

    /// <summary>
    /// A brush entirely inside another is swallowed: the result is the biter
    /// alone.
    /// </summary>
    [Fact]
    public async Task ChopBrushesSwallowsAContainedBrush()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (-64, -64, -64), (64, 64, 64)),
                (UnitMap.Plain, (-8, -8, -8), (8, 8, 8))));

        BspBrush? chopped = BrushCsg.ChopBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(1, BrushCsg.CountBrushList(chopped));
        Assert.Equal([1], CsgFixture.OriginalIds(chopped));
    }

    /// <summary>
    /// Two solid boxes overlapping on a face: one bite produces one fragment,
    /// which is under the fragmentation limit, so it is taken.
    /// </summary>
    [Fact]
    public async Task ChopBrushesTakesAOneFragmentBite()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (32, 0, 0), (96, 64, 64))));

        BspBrush? chopped = BrushCsg.ChopBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(2, BrushCsg.CountBrushList(chopped));

        float volume = 0f;
        for (BspBrush? b = chopped; b is not null; b = b.Next)
        {
            volume += BrushGeometry.BrushVolume(build, b);
        }

        // The union, not the sum of the two boxes: 96x64x64.
        Assert.Equal(96f * 64f * 64f, volume, 10f);
    }

    /// <summary>
    /// <b>The fragmentation rule.</b> Two solid boxes crossing each other would
    /// need more than one fragment either way, so NEITHER bite is taken and the
    /// two are left overlapping.
    /// </summary>
    [Fact]
    public async Task ChopBrushesRefusesABiteThatWouldFragmentBothWays()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (-64, -8, -8), (64, 8, 8)),
                (UnitMap.Plain, (-8, -64, -8), (8, 64, 8))));

        BspBrush? chopped = BrushCsg.ChopBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(2, BrushCsg.CountBrushList(chopped));

        float volume = 0f;
        for (BspBrush? b = chopped; b is not null; b = b.Next)
        {
            volume += BrushGeometry.BrushVolume(build, b);
        }

        // Both bars survive whole, so the overlap is counted twice.
        Assert.Equal((128f * 16f * 16f) + (128f * 16f * 16f), volume, 10f);
    }

    /// <summary>
    /// The same crossing pair, both marked detail: the rule's escape hatch lets
    /// them fragment.
    /// </summary>
    [Fact]
    public async Task ChopBrushesAllowsTheSameBiteWhenBothBrushesAreDetail()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (-64, -8, -8), (64, 8, 8)),
                (UnitMap.Plain, (-8, -64, -8), (8, 64, 8))));

        map.Brushes[0].Contents |= (int)BrushContents.Detail;
        map.Brushes[1].Contents |= (int)BrushContents.Detail;

        BspBrush? chopped = BrushCsg.ChopBrushes(build, CsgFixture.AllBrushes(build));

        Assert.True(BrushCsg.CountBrushList(chopped) > 2);

        float volume = 0f;
        for (BspBrush? b = chopped; b is not null; b = b.Next)
        {
            volume += BrushGeometry.BrushVolume(build, b);
        }

        // The union this time: two bars less one shared 16x16x16 core.
        Assert.Equal((2f * 128f * 16f * 16f) - (16f * 16f * 16f), volume, 10f);
    }

    [Fact]
    public async Task ChopBrushesOfNothingIsNothing()
    {
        (BspBuildContext build, _) = await CsgFixture.LoadAsync(
            CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));

        Assert.Null(BrushCsg.ChopBrushes(build, null));
    }

    private static void BlockGridBounds(int x, int y, out Vec3 mins, out Vec3 maxs) =>
        SourceSharp.MapTools.Bsp.Tree.BlockGrid.BlockBounds(x, y, out mins, out maxs);
}
