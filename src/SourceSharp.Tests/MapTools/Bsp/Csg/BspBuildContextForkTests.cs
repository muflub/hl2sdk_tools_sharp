//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <see cref="BspBuildContext.Fork"/> and <see cref="BspBuildContext.Join"/>:
/// a subtree built beside its parent must come home numbered, ordered and
/// stored exactly as though the parent had built it itself.
/// </summary>
/// <remarks>
/// Each "the serial build would have" number below is worked out from the
/// order a serial build allocates in: everything up to the fork, then the
/// front subtree, then the back. A join that numbered the fork's nodes or
/// brushes from where the parent stood at the FORK (rather than after the
/// front), or that left them numbered from zero, or that put the fork's
/// diagnostics where they fell in wall-clock time, fails these.
/// </remarks>
public sealed class BspBuildContextForkTests
{
    // ---- what a fork shares and what it owns ------------------------------

    [Fact]
    public async Task AForkReadsTheSameMapButWritesToItsOwnArenaDiagnosticsAndCounters()
    {
        (BspBuildContext build, MapFile map) = await OneBox();
        build.AllocNode();
        build.AllocBrush(1);
        build.Nodes = 3;
        build.Diagnostics.Add(Diagnostic("before"));

        BspBuildContext fork = build.Fork();

        Assert.True(fork.IsFork);
        Assert.False(build.IsFork);
        Assert.Same(build.Compile, fork.Compile);
        Assert.Same(map, fork.Map);
        Assert.Same(build.Planes, fork.Planes);
        Assert.NotSame(build.Windings, fork.Windings);
        Assert.Equal(0, fork.Windings.ActiveWindings);
        Assert.NotSame(build.Diagnostics, fork.Diagnostics);
        Assert.Empty(fork.Diagnostics);
        Assert.Equal(0, fork.AllocatedNodes);
        Assert.Equal(0, fork.AllocatedBrushes);
        Assert.Equal(0, fork.ActiveBrushes);
        Assert.Equal(0, fork.Nodes);
        Assert.Equal(build.BrushStart, fork.BrushStart);
        Assert.Equal(build.BrushEnd, fork.BrushEnd);
        Assert.Null(fork.TreeParallelism);

        fork.Diagnostics.Add(Diagnostic("fork"));
        Assert.Single(build.Diagnostics);
    }

    [Fact]
    public async Task AForksArenaRunsUnderTheCompliancesOfTheArenaItStandsIn()
    {
        (BspBuildContext build, _) = await OneBox(VbspOptions.Default with { Compliance = ComplianceOptions.Stock });

        BspBuildContext fork = build.Fork();

        Assert.Same(build.Windings.Compliance, fork.Windings.Compliance);
        Assert.Equal(CompliancePolicy.Stock, fork.Windings.Compliance.Policy);
    }

    [Theory]
    [InlineData((int)BrushSidePooling.Off)]
    [InlineData((int)BrushSidePooling.Pooled)]
    [InlineData((int)BrushSidePooling.Checked)]
    public async Task AForkPoolsSideArraysInAPoolOfItsOwnOnlyWhenTheCompileDoes(int mode)
    {
        // An int because the enum is internal and a theory's parameters are public.
        BrushSidePooling pooling = (BrushSidePooling)mode;
        VbspContext context = await UnitMap.ContextAsync();
        context.BrushSidePooling = pooling;
        MapFile map = await MapFileLoader.LoadAsync(context, CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))));
        BspBuildContext build = new(context, map);

        BspBuildContext fork = build.Fork();

        if (pooling == BrushSidePooling.Off)
        {
            Assert.Null(fork.SidePool);
            return;
        }

        Assert.NotNull(fork.SidePool);
        Assert.NotSame(build.SidePool, fork.SidePool);
    }

    [Fact]
    public async Task ABrushAllocatedInAForkIsScopedToItAndNumberedFromZero()
    {
        (BspBuildContext build, _) = await OneBox();
        build.AllocBrush(0);
        BspBrush own = build.AllocBrush(0);

        BspBuildContext fork = build.Fork();
        BspBrush forked = fork.AllocBrush(0);

        Assert.Null(own.IdScope);
        Assert.Equal(1, own.Id);
        Assert.Same(fork, forked.IdScope);
        Assert.Equal(0, forked.Id);
    }

    [Fact]
    public async Task CopyBrushCarriesTheIdScopeWithTheId()
    {
        (BspBuildContext build, _) = await OneBox();
        BspBuildContext fork = build.Fork();
        BspBrush fromParent = build.AllocBrush(0);
        BspBrush fromFork = fork.AllocBrush(0);

        BspBrush copyOfParents = BrushGeometry.CopyBrush(fork, fromParent);
        BspBrush copyOfForks = BrushGeometry.CopyBrush(fork, fromFork);

        Assert.Equal(fromParent.Id, copyOfParents.Id);
        Assert.Null(copyOfParents.IdScope);
        Assert.Equal(fromFork.Id, copyOfForks.Id);
        Assert.Same(fork, copyOfForks.IdScope);
    }

    // ---- TakeWindings ---------------------------------------------------------

    [Fact]
    public async Task TakeWindingsMovesEveryWindingOfTheListAndTheVolumeIntoTheFork()
    {
        (BspBuildContext build, _) = await ThreeBoxes();
        BspBrush list = CsgFixture.AllBrushes(build)!;
        BspBrush volume = BrushGeometry.BrushFromBounds(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));
        List<BspBrush> brushes = [.. CsgFixture.ToList(list), volume];
        List<(Vec3[] Points, int Capacity)> before = Snapshot(build.Windings, brushes);
        int active = build.Windings.ActiveWindings;

        BspBuildContext fork = build.Fork();
        fork.TakeWindings(build, list, volume);

        Assert.Equal(active - before.Count, build.Windings.ActiveWindings);
        Assert.Equal(before.Count, fork.Windings.ActiveWindings);
        Assert.Equal(before, Snapshot(fork.Windings, brushes), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task TakeWindingsOfNothingMovesNothing()
    {
        (BspBuildContext build, _) = await OneBox();
        BspBuildContext fork = build.Fork();
        int active = build.Windings.ActiveWindings;

        fork.TakeWindings(build, null, null);

        Assert.Equal(active, build.Windings.ActiveWindings);
        Assert.Equal(0, fork.Windings.ActiveWindings);
        Assert.Throws<ArgumentNullException>(() => fork.TakeWindings(null!, null, null));
    }

    // ---- Join: numbering --------------------------------------------------------

    /// <summary>
    /// Serially: the root and its two children are nodes 0, 1 and 2; the
    /// front subtree allocates 3 to 6; the back subtree, under node 2,
    /// allocates 7 and 8. Built in a fork, the back's two are the fork's 0 and
    /// 1, and the front's four are allocated on the parent WHILE the fork
    /// runs, so only a join that rebases after the front has finished gets 7
    /// and 8.
    /// </summary>
    [Fact]
    public async Task AJoinNumbersTheForksNodesAfterEverythingTheFrontAllocated()
    {
        (BspBuildContext build, _) = await OneBox();
        BspNode root = build.AllocNode();
        BspNode front = build.AllocNode();
        BspNode back = build.AllocNode();
        Split(root, front, back);

        BspBuildContext fork = build.Fork();
        BspNode backFront = fork.AllocNode();
        BspNode backBack = fork.AllocNode();
        Split(back, backFront, backBack);

        BspNode f0 = build.AllocNode();
        BspNode f1 = build.AllocNode();
        BspNode f2 = build.AllocNode();
        Split(front, f0, f1);
        f1.PlaneNumber = 2;
        f1.Children[0] = f2;
        f1.Children[1] = build.AllocNode();
        f1.Children[1]!.PlaneNumber = BspNode.Leaf;

        build.Join(fork, back);

        Assert.Equal([0, 1, 2], [root.Id, front.Id, back.Id]);
        Assert.Equal([3, 4, 5, 6], [f0.Id, f1.Id, f2.Id, f1.Children[1]!.Id]);
        Assert.Equal([7, 8], [backFront.Id, backBack.Id]);
        Assert.Equal(9, build.AllocatedNodes);
    }

    /// <summary>
    /// A fork's brush ids are its own sequence, but a brush it copied from one
    /// made before the fork keeps that brush's id. Serially: brush 0 before
    /// the fork, the front's 1 and 2, then the back's copy of brush 0 (which
    /// takes id 3 from the sequence and is then given 0) and its own brush 4.
    /// </summary>
    [Fact]
    public async Task AJoinRebasesOnlyTheBrushIdsTheForkItselfAllocated()
    {
        (BspBuildContext build, _) = await OneBox();
        BspBrush before = build.AllocBrush(0);
        BspNode back = build.AllocNode();

        BspBuildContext fork = build.Fork();
        BspBrush copy = BrushGeometry.CopyBrush(fork, before);
        BspBrush own = fork.AllocBrush(0);
        copy.Next = own;
        own.Next = null;
        back.PlaneNumber = BspNode.Leaf;
        back.BrushList = copy;

        build.AllocBrush(0);
        build.AllocBrush(0);

        build.Join(fork, back);

        Assert.Equal(0, copy.Id);
        Assert.Null(copy.IdScope);
        Assert.Equal(4, own.Id);
        Assert.Null(own.IdScope);
        Assert.Equal(5, build.AllocatedBrushes);
    }

    /// <summary>
    /// A fork of a fork joins into its parent fork, and its ids become that
    /// fork's, to be rebased again when that one joins the compile's context.
    /// </summary>
    [Fact]
    public async Task ANestedForkIsRebasedAtEachJoinOnTheWayHome()
    {
        (BspBuildContext build, _) = await OneBox();
        BspNode outer = build.AllocNode();

        BspBuildContext fork = build.Fork();
        BspNode inner = fork.AllocNode();
        BspNode sibling = fork.AllocNode();
        Split(outer, inner, sibling);
        fork.AllocBrush(0);
        fork.AllocBrush(0);

        BspBuildContext nested = fork.Fork();
        BspNode a = nested.AllocNode();
        BspNode b = nested.AllocNode();
        Split(inner, a, b);
        BspBrush deep = nested.AllocBrush(0);
        a.BrushList = deep;

        fork.Join(nested, inner);

        Assert.Equal([0, 1, 2, 3], [inner.Id, sibling.Id, a.Id, b.Id]);
        Assert.Equal(2, deep.Id);
        Assert.Same(fork, deep.IdScope);

        build.AllocNode();
        build.AllocBrush(0);

        build.Join(fork, outer);

        Assert.Equal([0, 2, 3, 4, 5], [outer.Id, inner.Id, sibling.Id, a.Id, b.Id]);
        Assert.Equal(1 + 2, deep.Id);
        Assert.Null(deep.IdScope);
        Assert.Equal(2 + 4, build.AllocatedNodes);
        Assert.Equal(1 + 3, build.AllocatedBrushes);
    }

    // ---- Join: diagnostics, counters, windings, pool ---------------------------

    /// <summary>
    /// The fork says its piece while the front is still being built, so in
    /// wall-clock order it can come first; the serial build said the front's
    /// first, and so does the joined context.
    /// </summary>
    [Fact]
    public async Task AJoinAppendsTheForksDiagnosticsAfterTheFronts()
    {
        (BspBuildContext build, _) = await OneBox();
        build.Diagnostics.Add(Diagnostic("above"));
        BspNode back = build.AllocNode();
        back.PlaneNumber = BspNode.Leaf;

        BspBuildContext fork = build.Fork();
        fork.Diagnostics.Add(Diagnostic("back 1"));
        fork.Diagnostics.Add(Diagnostic("back 2"));
        build.Diagnostics.Add(Diagnostic("front"));

        build.Join(fork, back);

        Assert.Equal(
            ["above", "front", "back 1", "back 2"],
            build.Diagnostics.Select(d => d.Message));
    }

    [Fact]
    public async Task AJoinAddsTheForksCountersToItsOwn()
    {
        (BspBuildContext build, _) = await OneBox();
        BspNode back = build.AllocNode();
        back.PlaneNumber = BspNode.Leaf;
        build.Nodes = 5;
        build.NonVisibleNodes = 1;
        build.PrunedNodes = 2;
        BspBrush parentsBrush = build.AllocBrush(0);

        BspBuildContext fork = build.Fork();
        fork.Nodes = 7;
        fork.NonVisibleNodes = 3;
        fork.PrunedNodes = 4;
        fork.FreeBrush(parentsBrush);
        fork.AllocBrush(0);
        fork.AllocBrush(0);

        BspBuildContext nested = fork.Fork();
        BspNode nestedRoot = fork.AllocNode();
        nestedRoot.PlaneNumber = BspNode.Leaf;
        fork.Join(nested, nestedRoot);

        build.Join(fork, back);

        Assert.Equal(12, build.Nodes);
        Assert.Equal(4, build.NonVisibleNodes);
        Assert.Equal(6, build.PrunedNodes);
        Assert.Equal(1 - 1 + 2, build.ActiveBrushes);
        Assert.Equal(2, build.ForkedSubtrees);
    }

    [Fact]
    public async Task AJoinBringsEveryLiveWindingOfTheSubtreeHome()
    {
        (BspBuildContext build, _) = await ThreeBoxes();
        BspNode back = build.AllocNode();
        back.Volume = BrushGeometry.BrushFromBounds(build, new Vec3(-512, -512, -512), new Vec3(512, 512, 512));

        BspBuildContext fork = build.Fork();
        fork.TakeWindings(build, null, back.Volume);
        BspNode leaf = fork.AllocNode();
        BspNode other = fork.AllocNode();
        Split(back, leaf, other);
        leaf.Volume = BrushGeometry.BrushFromBounds(fork, new Vec3(0, 0, 0), new Vec3(64, 64, 64));
        BrushBspTree.LeafNode(leaf, CsgFixture.AllBrushes(fork));
        List<BspBrush> brushes = [back.Volume, leaf.Volume, .. CsgFixture.ToList(leaf.BrushList)];
        List<(Vec3[] Points, int Capacity)> inFork = Snapshot(fork.Windings, brushes);

        build.Join(fork, back);

        Assert.Equal(inFork, Snapshot(build.Windings, brushes), SnapshotComparer.Instance);
    }

    [Fact]
    public async Task AJoinEmptiesTheForksSidePool()
    {
        (BspBuildContext build, _) = await OneBox();
        BspNode back = build.AllocNode();
        back.PlaneNumber = BspNode.Leaf;
        BspBuildContext fork = build.Fork();
        fork.FreeBrush(fork.AllocBrush(6));
        Assert.Equal(1, fork.SidePool!.PooledArrays);

        build.Join(fork, back);

        Assert.Equal(0, fork.SidePool.PooledArrays);
    }

    [Fact]
    public async Task OnlyAForkCanBeJoined()
    {
        (BspBuildContext build, _) = await OneBox();
        (BspBuildContext other, _) = await OneBox();
        BspNode node = build.AllocNode();

        Assert.Throws<ArgumentException>(() => build.Join(other, node));
        Assert.Throws<ArgumentNullException>(() => build.Join(null!, node));
        Assert.Throws<ArgumentNullException>(() => build.Join(build.Fork(), null!));
    }

    // ---- helpers ------------------------------------------------------------

    private static Task<(BspBuildContext Build, MapFile Map)> OneBox(VbspOptions? options = null) =>
        CsgFixture.LoadAsync(CsgFixture.World((UnitMap.Plain, (0, 0, 0), (64, 64, 64))), options);

    private static Task<(BspBuildContext Build, MapFile Map)> ThreeBoxes() =>
        CsgFixture.LoadAsync(CsgFixture.World(
            (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
            (UnitMap.Plain, (128, 0, 0), (192, 64, 64)),
            (UnitMap.Plain, (256, 0, 0), (320, 64, 32))));

    private static CompileDiagnostic Diagnostic(string message) =>
        new("TEST0001", DiagnosticSeverity.Warning, message);

    private static void Split(BspNode node, BspNode front, BspNode back)
    {
        node.PlaneNumber = 0;
        node.Children[0] = front;
        node.Children[1] = back;
        front.Parent = node;
        back.Parent = node;
        front.PlaneNumber = BspNode.Leaf;
        back.PlaneNumber = BspNode.Leaf;
    }

    private static List<(Vec3[] Points, int Capacity)> Snapshot(WindingArena arena, IEnumerable<BspBrush> brushes)
    {
        List<(Vec3[], int)> windings = [];
        foreach (BspBrush brush in brushes)
        {
            foreach (BspBrushSide side in brush.Sides)
            {
                if (!side.Winding.IsNull)
                {
                    windings.Add((arena.Points(side.Winding).ToArray(), side.Winding.Capacity));
                }
            }
        }

        return windings;
    }

    private sealed class SnapshotComparer : IEqualityComparer<(Vec3[] Points, int Capacity)>
    {
        public static SnapshotComparer Instance { get; } = new();

        public bool Equals((Vec3[] Points, int Capacity) x, (Vec3[] Points, int Capacity) y) =>
            x.Capacity == y.Capacity && x.Points.AsSpan().SequenceEqual(y.Points);

        public int GetHashCode((Vec3[] Points, int Capacity) obj) => obj.Capacity;
    }
}
