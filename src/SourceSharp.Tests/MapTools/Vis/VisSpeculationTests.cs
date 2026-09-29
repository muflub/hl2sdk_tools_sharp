//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Vis;

/// <summary>
/// Phase 5's parallel <c>-tighten</c>: speculative reads of unfinished
/// neighbours (<see cref="VisSpeculativeReads"/>), their judgement and repair
/// (<see cref="VisRepairTree"/>), and splitting one portal's walk
/// (<see cref="VisFrameLedger"/>) -- each piece on its own, then the two
/// end-to-end claims on the windowed grid of <see cref="VvisTightenTests"/>:
/// a speculative run repaired after its neighbour finishes, and a split run,
/// each end with exactly the vector the one-thread walk (stock's
/// read order) produces.
/// </summary>
public class VisSpeculationTests
{
    private const int Words = 8;

    private static readonly WorkerContext Worker = new(0, 1 << 20, CancellationToken.None);

    private static ulong[] Vector(params int[] bits)
    {
        ulong[] v = new ulong[Words];
        foreach (int b in bits)
        {
            BitVectorOps.SetBit(v, b);
        }

        return v;
    }

    private static VisSpeculativeReads NewReads() => new(Words, () => new ulong[Words]);

    private static VisRepairTree NewTree(int levels = 3) => new(_ => { }, levels);

    // ---- the record vectors' pool -----------------------------------------

    [Fact]
    public void ARecordVectorGivenBackDirtyIsRentedOutClean()
    {
        // Vectors are cleared when rented rather than when given back (the
        // giving back happens under the schedule's gate), so a vector a run
        // filled comes out of the pool empty all the same.
        VisTightening ranking = new Grid().Ranking;
        ulong[] first = ranking.RentVector();
        Assert.All(first, w => Assert.Equal(0UL, w));
        Array.Fill(first, ulong.MaxValue);
        ranking.ReturnVector(first);

        ulong[] again = ranking.RentVector();
        Assert.Same(first, again);
        Assert.All(again, w => Assert.Equal(0UL, w));
        Assert.Equal(1, ranking.VectorsPeak);
    }

    [Fact]
    public void MissedAnythingAgreesWithTheWordByWordTestOnEveryLengthAndBit()
    {
        // The four-word form against the definition: every length up to 21
        // words (so the four-word loop ends on every remainder), a single
        // shared bit at every position, and vectors that overlap nowhere.
        for (int length = 0; length <= 21; length++)
        {
            ulong[] none = new ulong[length];
            ulong[] all = new ulong[length];
            Array.Fill(all, ulong.MaxValue);
            Assert.False(VisRepairTree.MissedAnything(none, all));
            Assert.False(VisRepairTree.MissedAnything(all, none));

            for (int bit = 0; bit < length * 64; bit += 7)
            {
                ulong[] missed = new ulong[length];
                ulong[] final = new ulong[length];
                BitVectorOps.SetBit(missed, bit);
                BitVectorOps.SetBit(final, bit);
                Assert.True(VisRepairTree.MissedAnything(missed, final));

                // The neighbouring bit instead: disjoint, so nothing missed.
                BitVectorOps.ClearBit(final, bit);
                BitVectorOps.SetBit(final, bit ^ 1);
                Assert.False(VisRepairTree.MissedAnything(missed, final));
            }
        }
    }

    // ---- VisSpeculativeReads ------------------------------------------------

    [Fact]
    public void ASpeculativeReadWritesPrevAndSeen()
    {
        //: might = prevmight & test.
        VisSpeculativeReads reads = NewReads();
        ulong[] might = new ulong[Words];

        reads.AndSpeculative(0, 5, Vector(1, 2, 3, 300), Vector(2, 3, 4, 300), Vector(), might);

        Assert.Equal(Vector(2, 3, 300), might);
    }

    [Theory]
    [InlineData(new int[0], true)]
    [InlineData(new[] { 2 }, true)]
    [InlineData(new[] { 2, 3 }, false)]
    public void ASpeculativeReadReportsNewBitsAsTheExactReadDoes(int[] visible, bool expected)
    {
        //: `more` is set by a bit of might not yet in vis.
        VisSpeculativeReads reads = NewReads();
        ulong[] prev = Vector(1, 2, 3);
        ulong[] seen = Vector(2, 3, 4);
        ulong[] vis = Vector(visible);
        ulong[] might = new ulong[Words];

        bool more = reads.AndSpeculative(0, 5, prev, seen, vis, might);

        Assert.Equal(expected, more);
        Assert.Equal(BitVectorOps.AndWithNewBits(prev, seen, vis, new ulong[Words]), more);
    }

    [Fact]
    public void ASpeculativeReadRecordsWhatItCouldHaveMissed()
    {
        // The exact read would have kept prev & final; this one kept
        // prev & seen, so what it may have lost is prev & ~seen.
        VisSpeculativeReads reads = NewReads();
        reads.AndSpeculative(7, 5, Vector(1, 2, 3), Vector(2), Vector(), new ulong[Words]);

        List<(int Node, int Portal, ulong[] Missed)> taken = [];
        reads.MoveTo(taken);

        (int node, int portal, ulong[] missed) = Assert.Single(taken);
        Assert.Equal(7, node);
        Assert.Equal(5, portal);
        Assert.Equal(Vector(1, 3), missed);
    }

    [Fact]
    public void ReadsOfOnePortalChargedToOneNodeShareARecord()
    {
        VisSpeculativeReads reads = NewReads();
        reads.AndSpeculative(1, 5, Vector(1), Vector(), Vector(), new ulong[Words]);
        reads.AndSpeculative(1, 5, Vector(9), Vector(), Vector(), new ulong[Words]);

        List<(int Node, int Portal, ulong[] Missed)> taken = [];
        reads.MoveTo(taken);

        (_, _, ulong[] missed) = Assert.Single(taken);
        Assert.Equal(Vector(1, 9), missed);
    }

    [Fact]
    public void ReadsChargedToDifferentNodesKeepSeparateRecords()
    {
        VisSpeculativeReads reads = NewReads();
        reads.AndSpeculative(1, 5, Vector(1), Vector(), Vector(), new ulong[Words]);
        reads.AndSpeculative(2, 5, Vector(1), Vector(), Vector(), new ulong[Words]);

        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public void HandingTheRecordsOverEmptiesTheWorkersRecord()
    {
        VisSpeculativeReads reads = NewReads();
        reads.AndSpeculative(1, 5, Vector(1), Vector(), Vector(), new ulong[Words]);

        reads.MoveTo([]);

        Assert.Equal(0, reads.Count);
    }

    // ---- VisRepairTree ------------------------------------------------------

    [Fact]
    public void AChildIsFoundAgainByItsCandidate()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        int b = tree.Child(VisRepairTree.Root, index: 2, count: 16);

        Assert.NotEqual(a, b);
        Assert.Equal(a, tree.Child(VisRepairTree.Root, index: 1, count: 16));
    }

    [Fact]
    public void ANodeNeverWalkedIsNotComplete()
    {
        // Complete means "walked with exact masks"; a node a walk has only
        // just reached has not been walked.
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);

        Assert.False(tree.IsComplete(a));
    }

    [Fact]
    public void AWalkedNodeWithNoInexactReadIsComplete()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);

        Assert.True(tree.Validate((_, _) => false));
        Assert.True(tree.IsComplete(a));
    }

    [Fact]
    public void AReadThatMissedAFinalBitMakesItsNodeDirty()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);
        Absorb(tree, a, portal: 5, missed: Vector(3));

        // Portal 5 finished seeing 3: the read of it kept prev & seen without
        // bit 3, which the exact read would have kept.
        bool exact = tree.Validate((portal, missed) =>
            VisRepairTree.MissedAnything(missed, portal == 5 ? Vector(3, 4) : Vector()));

        Assert.False(exact);
        Assert.False(tree.IsComplete(a));
    }

    [Fact]
    public void AReadThatMissedNothingFinalLeavesItsNodeComplete()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);
        Absorb(tree, a, portal: 5, missed: Vector(3));

        bool exact = tree.Validate((_, missed) => VisRepairTree.MissedAnything(missed, Vector(4)));

        Assert.True(exact);
        Assert.True(tree.IsComplete(a));
    }

    [Fact]
    public void ADirtyNodeMakesEveryAncestorIncompleteButNotItsSiblings()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);
        int sibling = tree.Child(VisRepairTree.Root, index: 2, count: 16);
        tree.Walk(sibling);
        int deep = tree.Child(a, index: 11, count: 16);
        tree.Walk(deep);
        Absorb(tree, deep, portal: 5, missed: Vector(3));

        tree.Validate((_, _) => true);

        Assert.False(tree.IsComplete(deep));
        Assert.False(tree.IsComplete(a));
        Assert.True(tree.IsComplete(sibling));
    }

    [Fact]
    public void WalkingADirtyNodeAgainForgetsItsSubtree()
    {
        // Its mask was wrong, so nothing recorded below it is evidence.
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);
        int below = tree.Child(a, index: 11, count: 16);
        tree.Walk(below);
        Absorb(tree, a, portal: 5, missed: Vector(3));
        tree.Validate((_, _) => true);

        tree.Walk(a);

        Assert.NotEqual(below, tree.Child(a, index: 11, count: 16));
    }

    [Fact]
    public void WalkingACleanNodeAgainKeepsItsCompleteChildren()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        tree.Walk(a);
        int done = tree.Child(a, index: 11, count: 16);
        tree.Walk(done);
        int dirty = tree.Child(a, index: 12, count: 16);
        tree.Walk(dirty);
        Absorb(tree, dirty, portal: 5, missed: Vector(3));
        tree.Validate((_, _) => true);

        tree.Walk(a);

        Assert.Equal(done, tree.Child(a, index: 11, count: 16));
        Assert.True(tree.IsComplete(done));
        Assert.False(tree.IsComplete(dirty));
    }

    [Fact]
    public void AbsorbingTwoWorkersReadsOfOneNodeAndPortalUnitesWhatTheyMissed()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        Absorb(tree, a, portal: 5, missed: Vector(3));
        Absorb(tree, a, portal: 5, missed: Vector(4));

        Assert.Equal(1, tree.RecordCount);
        Assert.False(tree.Validate((_, missed) => VisRepairTree.MissedAnything(missed, Vector(4))));
    }

    [Fact]
    public void TheCandidatesARunReadUnfinishedAreListedOnce()
    {
        VisRepairTree tree = NewTree();
        int a = tree.Child(VisRepairTree.Root, index: 1, count: 16);
        int b = tree.Child(VisRepairTree.Root, index: 2, count: 16);
        Absorb(tree, a, portal: 5, missed: Vector(3));
        Absorb(tree, b, portal: 5, missed: Vector(3));
        Absorb(tree, b, portal: 6, missed: Vector(3));

        List<int> pending = [];
        tree.CollectPending(pending);

        Assert.Equal([5, 6], pending);
    }

    [Theory]
    [InlineData(81, false)]
    [InlineData(82, true)]
    public void EnteringACandidateIsChildThenIsCompleteThenWalk(int seed, bool shared)
    {
        // Two trees driven through the same seeded runs -- walks three levels
        // deep, reads charged to random nodes, judgements that dirty some of
        // them, prunes -- one entering each candidate in one step, the other
        // with the three separate calls. They must agree on every child, on
        // every skip, and on what each later run finds complete.
        Random random = new(seed);
        VisRepairTree combined = NewTree();
        VisRepairTree separate = NewTree();
        int skipped = 0;
        int entered = 0;
        for (int run = 0; run < 40; run++)
        {
            List<int> frontier = [VisRepairTree.Root];
            List<int> walked = [];
            for (int level = 0; level < 3; level++)
            {
                List<int> next = [];
                foreach (int node in frontier)
                {
                    int count = 1 + random.Next(6);
                    for (int i = 0; i < count; i++)
                    {
                        if (random.Next(3) == 0)
                        {
                            continue;
                        }

                        bool went = combined.Enter(node, i, count, shared, out int child);
                        int expected = separate.Child(node, i, count, shared);
                        bool complete = separate.IsComplete(expected, shared);
                        if (!complete)
                        {
                            separate.Walk(expected, shared);
                        }

                        Assert.Equal(expected, child);
                        Assert.Equal(!complete, went);
                        if (!went)
                        {
                            skipped++;
                            continue;
                        }

                        entered++;
                        walked.Add(child);
                        if (random.Next(5) == 0)
                        {
                            combined.Prune(child, shared);
                            separate.Prune(child, shared);
                        }
                        else
                        {
                            next.Add(child);
                        }
                    }
                }

                frontier = next;
            }

            // Some reads missed a bit that turns out final, some did not.
            foreach (int node in walked)
            {
                if (random.Next(2) == 0)
                {
                    int portal = random.Next(2);
                    Absorb(combined, node, portal, Vector(portal));
                    Absorb(separate, node, portal, Vector(portal));
                }
            }

            int finalBit = random.Next(2);
            Assert.Equal(
                separate.Validate((_, missed) => VisRepairTree.MissedAnything(missed, Vector(finalBit))),
                combined.Validate((_, missed) => VisRepairTree.MissedAnything(missed, Vector(finalBit))));
        }

        Assert.True(skipped > 20, $"only {skipped} candidates skipped");
        Assert.True(entered > 60, $"only {entered} candidates entered");
    }

    private static void Absorb(VisRepairTree tree, int node, int portal, ulong[] missed)
    {
        VisSpeculativeReads reads = new(Words, () => new ulong[Words]);
        reads.AndSpeculative(node, portal, missed, Vector(), Vector(), new ulong[Words]);
        tree.Absorb(reads);
    }

    // ---- VisFrameLedger ----------------------------------------------------

    [Fact]
    public void TheShallowestFrameWithCandidatesLeftIsSplit()
    {
        VisFrameLedger ledger = new(16) { Root = 1 };
        ledger.Enter(1, cluster: 0, node: 0, end: 3);
        ledger.At(1, 2);
        ledger.Enter(2, cluster: 0, node: 0, end: 10);
        ledger.At(2, 4);
        ledger.Enter(3, cluster: 0, node: 0, end: 10);
        ledger.At(3, 1);

        // Depth 1 is on its last candidate; depth 2 is the shallowest with work.
        Assert.Equal(2, ledger.Shallowest(3));
    }

    [Fact]
    public void AFrameOnItsLastCandidateHasNothingToGive()
    {
        VisFrameLedger ledger = new(16) { Root = 1 };
        ledger.Enter(1, cluster: 0, node: 0, end: 3);
        ledger.At(1, 2);

        Assert.Equal(-1, ledger.Shallowest(1));
    }

    [Fact]
    public void HalvingGivesAwayTheSecondHalfOfWhatIsLeft()
    {
        VisFrameLedger ledger = new(16) { Root = 1 };
        ledger.Enter(1, cluster: 0, node: 0, end: 10);
        ledger.At(1, 3);

        (int from, int to) = ledger.Halve(1);

        // Candidates 4..9 are left after the one being walked: 4..6 stay.
        Assert.Equal((7, 10), (from, to));
        Assert.Equal(7, ledger.End(1));
    }

    [Fact]
    public void HalvingASingleRemainingCandidateGivesItAway()
    {
        VisFrameLedger ledger = new(16) { Root = 1 };
        ledger.Enter(1, cluster: 0, node: 0, end: 5);
        ledger.At(1, 3);

        Assert.Equal((4, 5), ledger.Halve(1));
    }

    // ---- end to end on the grid --------------------------------------------

    /// <summary>A flow set up on a fresh copy of the grid, after the base flow.</summary>
    private sealed class Grid
    {
        internal Grid()
            : this(VvisTightenTests.Grid().Portals)
        {
        }

        internal Grid(PortalSet portals)
        {
            Portals = portals;
            State = new VisPortalState(Portals.Count);
            VisBaseFlow baseFlow = new(Portals, State, useRadius: false, radiusSquared: 0.0);
            VisFloodScratch scratch = new(Portals.Count);
            for (int p = 0; p < Portals.Count; p++)
            {
                baseFlow.Run(p, scratch, Worker);
            }

            Ranking = new VisTightening(State);
            Rank = new int[Portals.Count];
            for (int p = 0; p < Portals.Count; p++)
            {
                Rank[p] = Ranking.RankOf(p);
            }
        }

        internal PortalSet Portals { get; }

        internal VisPortalState State { get; }

        internal VisTightening Ranking { get; }

        internal int[] Rank { get; }

        internal VisPortalFlow NewFlow(VisSpeculativeReads reads)
        {
            VisPortalFlow flow = new(Portals, State, BitVectorPath.Auto);
            flow.UseTightening(Rank, reads);
            return flow;
        }

        /// <summary>Flows the portal at a rank as stock at one thread does, and publishes it.</summary>
        internal void Exact(VisPortalFlow flow, int rank)
        {
            int portal = Ranking.PortalAt(rank);
            flow.Run(portal, rank, tree: null, splitter: null, Worker);
            State.SetStatus(portal, VisPortalStatus.Done);
        }

        internal ulong[] VisOf(int rank) => State.Vis(Ranking.PortalAt(rank)).ToArray();
    }

    /// <summary>
    /// The windowed grid with every window skewed about the vertical: each
    /// wall's window leans 8 units across its width, so no two portal planes
    /// are parallel to an axis and the flow's source windings really get
    /// chopped (the upright grid only ever passes its portals' own windings
    /// down, which leaves a whole kind of split untested).
    /// </summary>
    private static PortalSet SlantedGrid(int Width = 10, int Height = 10)
    {
        const float Cell = 64f;
        const float Lean = 4f;
        List<FilePortal> portals = [];
        for (int j = 0; j < Height; j++)
        {
            for (int i = 0; i + 1 < Width; i++)
            {
                float x = (i + 1) * Cell;
                float from = (j * Cell) + 4f + (((i * 7) + (j * 13)) % 5 * 6f);
                float to = Math.Min(from + 20f + ((i + j) % 3 * 8f), ((j + 1) * Cell) - 4f);
                portals.Add(new FilePortal((j * Width) + i, (j * Width) + i + 1,
                [
                    new Vec3(x - Lean, from, 0f),
                    new Vec3(x + Lean, to, 0f),
                    new Vec3(x + Lean, to, 16f),
                    new Vec3(x - Lean, from, 16f),
                ]));
            }
        }

        for (int j = 0; j + 1 < Height; j++)
        {
            for (int i = 0; i < Width; i++)
            {
                float y = (j + 1) * Cell;
                float from = (i * Cell) + 4f + (((i * 11) + (j * 5)) % 5 * 6f);
                float to = Math.Min(from + 20f + ((i * 2 + j) % 3 * 8f), ((i + 1) * Cell) - 4f);
                portals.Add(new FilePortal((j * Width) + i, ((j + 1) * Width) + i,
                [
                    new Vec3(to, y + Lean, 0f),
                    new Vec3(from, y - Lean, 0f),
                    new Vec3(from, y - Lean, 16f),
                    new Vec3(to, y + Lean, 16f),
                ]));
            }
        }

        return PortalSet.FromPortalFile(VisFixture.Portals(Width * Height, [.. portals]));
    }

    private static ulong[][] OneThreadAnswer() => OneThreadAnswer(new Grid());

    private static ulong[][] OneThreadAnswer(Grid grid)
    {
        VisPortalFlow flow = grid.NewFlow(new VisSpeculativeReads(grid.State.Words, () => new ulong[grid.State.Words]));
        ulong[][] answer = new ulong[grid.Portals.Count][];
        for (int rank = 0; rank < grid.Portals.Count; rank++)
        {
            grid.Exact(flow, rank);
            answer[rank] = grid.VisOf(rank);
        }

        return answer;
    }

    /// <summary>One speculative run and its judgement.</summary>
    private readonly record struct Trial(int Rank, int Neighbour, int Records, bool FirstRunExact, ulong[] Final);

    /// <summary>
    /// For the portal at <paramref name="rank"/>, with every lower rank
    /// flowed exactly: for each lower-ranked neighbour in its flood, puts that
    /// neighbour back to "still being flowed" with only part of its final
    /// vector (<paramref name="keep"/> picks the bits), flows the portal,
    /// restores the neighbour as finished, judges the run, and repairs it if
    /// the judgement says so.
    /// </summary>
    private static List<Trial> Speculate(int rank, Func<int, bool> keep)
    {
        Grid grid = new();
        int words = grid.State.Words;
        VisSpeculativeReads reads = new(words, () => new ulong[words]);
        VisPortalFlow flow = grid.NewFlow(reads);
        for (int r = 0; r < rank; r++)
        {
            grid.Exact(flow, r);
        }

        int portal = grid.Ranking.PortalAt(rank);
        List<Trial> trials = [];
        ulong[] flood = grid.State.Flood(portal).ToArray();
        for (int q = 0; q < grid.Portals.Count; q++)
        {
            if (!BitVectorOps.GetBit(flood, q) || grid.Rank[q] >= rank)
            {
                continue;
            }

            ulong[] final = grid.State.Vis(q).ToArray();
            Span<ulong> growing = grid.State.Vis(q);
            growing.Clear();
            for (int bit = 0; bit < grid.Portals.Count; bit++)
            {
                if (BitVectorOps.GetBit(final, bit) && keep(bit))
                {
                    BitVectorOps.SetBit(growing, bit);
                }
            }

            grid.State.SetStatus(q, VisPortalStatus.Working);
            grid.State.Vis(portal).Clear();
            VisRepairTree tree = NewTree();
            flow.Run(portal, rank, tree, splitter: null, Worker);
            int records = tree.RecordCount;

            final.CopyTo(grid.State.Vis(q));
            grid.State.SetStatus(q, VisPortalStatus.Done);

            bool exact = tree.Validate((other, missed) =>
                VisRepairTree.MissedAnything(missed, grid.State.Vis(other)));
            if (!exact)
            {
                flow.Run(portal, rank, tree, splitter: null, Worker);
                Assert.False(tree.Speculated);
            }

            trials.Add(new Trial(rank, q, records, exact, grid.State.Vis(portal).ToArray()));
        }

        return trials;
    }

    private static IEnumerable<Trial> TopRanks(int count, Func<int, bool> keep)
    {
        int portals = VvisTightenTests.Grid().Portals.Count;
        for (int rank = portals - 1; rank >= portals - count; rank--)
        {
            foreach (Trial trial in Speculate(rank, keep))
            {
                yield return trial;
            }
        }
    }

    [Fact]
    public void ReadingAnUnfinishedNeighbourIsRecordedAndCanBeInexact()
    {
        // The repair facts below would pass vacuously if no speculative run on
        // the grid ever read a neighbour whose missing bits mattered.
        List<Trial> trials = [.. TopRanks(4, _ => false)];

        Assert.Contains(trials, t => t.Records > 0 && !t.FirstRunExact);
    }

    [Fact]
    public void ANeighbourNeverReadLeavesNoRecord()
    {
        // A flood member the walk never tests is not a read: nothing to judge.
        List<Trial> trials = [.. TopRanks(4, _ => false)];

        Assert.Contains(trials, t => t.Records == 0 && t.FirstRunExact);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ARepairedSpeculativeRunEqualsTheOneThreadRun(int kept)
    {
        // kept: 0 = the neighbour had found nothing yet, 1 = every other bit,
        // 2 = all but every third.
        Func<int, bool> keep = kept switch
        {
            0 => _ => false,
            1 => bit => (bit & 1) == 0,
            _ => bit => bit % 3 != 0,
        };
        ulong[][] answer = OneThreadAnswer();
        int repaired = 0;
        foreach (Trial trial in TopRanks(4, keep))
        {
            repaired += trial.FirstRunExact ? 0 : 1;
            Assert.Equal(answer[trial.Rank], trial.Final);
        }

        Assert.True(repaired > 0);
    }

    [Fact]
    public void ASpeculativeRunThatMissedNothingIsAcceptedAsTheOneThreadRun()
    {
        // The other branch of the judgement: accepted without a second walk.
        ulong[][] answer = OneThreadAnswer();
        List<Trial> accepted = [.. TopRanks(4, bit => bit % 3 != 0).Where(t => t.Records > 0 && t.FirstRunExact)];

        Assert.NotEmpty(accepted);
        Assert.All(accepted, t => Assert.Equal(answer[t.Rank], t.Final));
    }

    /// <summary>Hungry until it has taken a quota of frames; keeps them in a shared list.</summary>
    private sealed class CollectingSplitter : IVisFlowSplitter
    {
        private readonly int _words;
        private readonly int _quota;
        private readonly List<VisFrameTask> _into;
        private int _taken;

        internal CollectingSplitter(int words, int quota, List<VisFrameTask> into)
        {
            _words = words;
            _quota = quota;
            _into = into;
            Hunger.Set(quota > 0);
        }

        public VisHunger Hunger { get; } = new();

        public void Split(
            int basePortal,
            int cluster,
            int depth,
            ReadOnlySpan<Vec3> source,
            ReadOnlySpan<Vec3> pass,
            ReadOnlySpan<ulong> mightSee,
            int node,
            int from,
            int to)
        {
            VisFrameTask frame = new(_words);
            frame.Set(0, basePortal, cluster, depth, source, pass, mightSee, node, from, to);
            _into.Add(frame);
            _taken++;
            Hunger.Set(_taken < _quota);
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void ASplitRunAndItsSplitOffFramesTogetherEqualTheOneThreadRun(int quota, bool slanted)
    {
        // A small quota splits only head frames, whose windings are the
        // portal's own; a large one splits frames deep in the walk, whose
        // windings live in the parent frame's slab.
        ulong[][] answer = OneThreadAnswer(slanted ? new Grid(SlantedGrid()) : new Grid());
        Grid grid = slanted ? new Grid(SlantedGrid()) : new Grid();
        int words = grid.State.Words;
        VisPortalFlow flow = grid.NewFlow(new VisSpeculativeReads(words, () => new ulong[words]));
        VisPortalFlow other = grid.NewFlow(new VisSpeculativeReads(words, () => new ulong[words]));
        int splits = 0;
        int resplits = 0;
        for (int rank = 0; rank < answer.Length; rank++)
        {
            List<VisFrameTask> frames = [];
            int portal = grid.Ranking.PortalAt(rank);
            flow.Run(portal, rank, tree: null, new CollectingSplitter(words, quota, frames), Worker);
            int first = frames.Count;

            // Frames split off a frame split off are walked too.
            CollectingSplitter again = new(words, quota, frames);
            for (int i = 0; i < frames.Count; i++)
            {
                other.RunFrame(frames[i], rank, tree: null, again, Worker);
            }

            splits += first;
            resplits += frames.Count - first;
            grid.State.SetStatus(portal, VisPortalStatus.Done);
            Assert.Equal(answer[rank], grid.VisOf(rank));
        }

        Assert.True(splits > 0);
        Assert.True(resplits > 0);
    }

    /// <summary>
    /// Remembers what each depth's open frame was entered with, and checks
    /// every split-off frame against it.
    /// </summary>
    private sealed class CheckingSplitter : IVisFlowSplitter
    {
        private readonly Dictionary<int, (int Cluster, Vec3[] Source, Vec3[] Pass, ulong[] Might)> _open = [];
        private readonly int _words;
        private readonly int _quota;
        private readonly Vec3[] _headSource;
        private int _taken;

        internal CheckingSplitter(int words, int quota, Vec3[] headSource)
        {
            _words = words;
            _quota = quota;
            _headSource = headSource;
            Hunger.Set(quota > 0);
        }

        public VisHunger Hunger { get; } = new();

        internal int Checked { get; private set; }

        internal int Deepest { get; private set; }

        internal int Chopped { get; private set; }

        internal void Entered(
            int depth,
            int cluster,
            ReadOnlySpan<Vec3> source,
            ReadOnlySpan<Vec3> pass,
            ReadOnlySpan<ulong> mightSee) =>
            _open[depth] = (cluster, source.ToArray(), pass.ToArray(), mightSee.ToArray());

        public void Split(
            int basePortal,
            int cluster,
            int depth,
            ReadOnlySpan<Vec3> source,
            ReadOnlySpan<Vec3> pass,
            ReadOnlySpan<ulong> mightSee,
            int node,
            int from,
            int to)
        {
            (int openCluster, Vec3[] openSource, Vec3[] openPass, ulong[] openMight) = _open[depth];
            Assert.Equal(openCluster, cluster);
            Assert.Equal(openSource, source.ToArray());
            Assert.Equal(openPass, pass.ToArray());
            Assert.Equal(openMight, mightSee.ToArray());
            Assert.Equal(_words, mightSee.Length);
            _taken++;
            Hunger.Set(_taken < _quota);
            Checked++;
            Deepest = Math.Max(Deepest, depth);
            Chopped += source.SequenceEqual(_headSource) ? 0 : 1;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASplitOffFrameCarriesExactlyWhatThatFrameWasEnteredWith(bool slanted)
    {
        // The ledger finds a frame's windings and mask again from where they
        // live -- the portal set, the flood, or the parent frame's slab.
        // without copying them on entry. Checked against a copy taken on
        // entry, for every split an always-hungry worker would take.
        Grid grid = slanted ? new Grid(SlantedGrid()) : new Grid();
        int words = grid.State.Words;
        VisPortalFlow flow = grid.NewFlow(new VisSpeculativeReads(words, () => new ulong[words]));
        int checkedFrames = 0;
        int deepest = 0;
        int chopped = 0;
        for (int rank = 0; rank < grid.Portals.Count; rank++)
        {
            int portal = grid.Ranking.PortalAt(rank);
            CheckingSplitter splitter = new(words, 1000, grid.Portals.Winding(portal).ToArray());
            flow.FrameEntered = splitter.Entered;
            flow.Run(portal, rank, tree: null, splitter, Worker);
            grid.State.SetStatus(portal, VisPortalStatus.Done);
            checkedFrames += splitter.Checked;
            deepest = Math.Max(deepest, splitter.Deepest);
            chopped += splitter.Chopped;
        }

        Assert.True(checkedFrames > 0);
        Assert.True(deepest > 2);

        // The slanted grid is there to split frames whose source winding was
        // chopped, and so lives in a slab rather than in the portal set.
        Assert.True(!slanted || chopped > 0);
    }

    [Fact]
    public void SplittingTakesTheHeadFrameFirstAndDeeperFramesLater()
    {
        // The split takes the SHALLOWEST frame with work left, so a walk
        // split again and again hands off its head's candidates first and
        // deeper frames once the head has nothing left to give.
        Grid grid = new();
        int words = grid.State.Words;
        VisPortalFlow flow = grid.NewFlow(new VisSpeculativeReads(words, () => new ulong[words]));
        int deepest = 0;
        int heads = 0;
        for (int rank = 0; rank < grid.Portals.Count; rank++)
        {
            List<VisFrameTask> frames = [];
            int portal = grid.Ranking.PortalAt(rank);
            flow.Run(portal, rank, tree: null, new CollectingSplitter(words, 64, frames), Worker);
            grid.State.SetStatus(portal, VisPortalStatus.Done);

            for (int i = 1; i < frames.Count; i++)
            {
                Assert.True(frames[i].Depth >= frames[i - 1].Depth || frames[i - 1].Depth > 1);
            }

            deepest = Math.Max(deepest, frames.Count == 0 ? 0 : frames.Max(f => f.Depth));
            heads += frames.Count(f => f.Depth == 1);
        }

        Assert.True(heads > 0);
        Assert.True(deepest > 1);
    }

    [Theory]
    [InlineData(2, 256)]
    [InlineData(7, 256)]
    [InlineData(32, 256)]
    [InlineData(32, 1)]
    [InlineData(32, 4096)]
    public async Task EveryPortalVectorIsTheOneThreadOneAtAnyDegreeAndWindow(int degree, int window)
    {
        // Finer than the lump facts: the per-portal vectors, which the cluster
        // merge would forgive a difference in.
        ulong[][] answer = OneThreadAnswer();
        for (int round = 0; round < 3; round++)
        {
            Grid grid = new();
            VisTightening tightening = new(grid.State) { Window = window };
            using WorkQueue queue = new(new CompileParallelism { MaxDegree = degree });
            VisPortalFlow?[] flows = new VisPortalFlow?[queue.Degree];
            await tightening.RunAsync(
                queue,
                flows,
                () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
                progress: null,
                CancellationToken.None);

            for (int rank = 0; rank < answer.Length; rank++)
            {
                Assert.Equal(answer[rank], grid.VisOf(rank));
            }
        }
    }

    [Theory]
    [InlineData(16, 10, 10, 20)]
    [InlineData(32, 10, 10, 20)]
    [InlineData(32, 4, 3, 300)]
    [InlineData(32, 6, 2, 300)]
    public async Task TheSlantedGridIsTheOneThreadAnswerRunAfterRunWithSplitting(
        int degree,
        int width,
        int height,
        int rounds)
    {
        // Twenty whole runs per degree against the one-thread vectors: the
        // per-degree theory above runs three, and a schedule defect that
        // shows one run in ten needs more. Every worker idle at some point
        // means every speculative walk gets split.
        ulong[][] answer = OneThreadAnswer(new Grid(SlantedGrid(width, height)));
        for (int round = 0; round < rounds; round++)
        {
            Grid grid = new(SlantedGrid(width, height));
            VisTightening tightening = new(grid.State) { Window = 4096 };
            using WorkQueue queue = new(new CompileParallelism { MaxDegree = degree });
            await tightening.RunAsync(
                queue,
                new VisPortalFlow?[queue.Degree],
                () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
                progress: null,
                CancellationToken.None);

            for (int rank = 0; rank < answer.Length; rank++)
            {
                Assert.Equal(answer[rank], grid.VisOf(rank));
            }
        }
    }

    /// <summary>
    /// The portal file stock vbsp writes for the catalogue's l1_areaportal:
    /// two corridors joined through an areaportal room, 12 clusters, 14 file
    /// portals. Few portals and many workers is where splitting is heaviest.
    /// </summary>
    private const string AreaportalPrt =
        "PRT1\n" +
        "12\n" +
        "14\n" +
        "4 0 1 (16 32 0 ) (400 32 0 ) (400 32 256 ) (16 32 256 )\n" +
        "4 1 6 (400 0 256 ) (16 0 256 ) (16 0 0 ) (400 0 0 )\n" +
        "4 1 2 (16 32 128 ) (16 32 0 ) (16 0 0 ) (16 0 128 )\n" +
        "4 2 7 (0 0 128 ) (0 0 0 ) (16 0 0 ) (16 0 128 )\n" +
        "4 2 4 (0 0 0 ) (0 0 128 ) (0 32 128 ) (0 32 0 )\n" +
        "4 3 5 (-400 32 0 ) (-16 32 0 ) (-16 32 256 ) (-400 32 256 )\n" +
        "4 4 9 (0 0 0 ) (0 0 128 ) (-16 0 128 ) (-16 0 0 )\n" +
        "4 4 5 (-16 32 128 ) (-16 32 0 ) (-16 0 0 ) (-16 0 128 )\n" +
        "4 5 10 (-16 0 256 ) (-400 0 256 ) (-400 0 0 ) (-16 0 0 )\n" +
        "4 6 8 (16 -32 0 ) (400 -32 0 ) (400 -32 256 ) (16 -32 256 )\n" +
        "4 6 7 (16 0 0 ) (16 -32 0 ) (16 -32 128 ) (16 0 128 )\n" +
        "4 7 9 (0 0 128 ) (0 0 0 ) (0 -32 0 ) (0 -32 128 )\n" +
        "4 9 10 (-16 0 0 ) (-16 -32 0 ) (-16 -32 128 ) (-16 0 128 )\n" +
        "4 10 11 (-400 -32 0 ) (-16 -32 0 ) (-16 -32 256 ) (-400 -32 256 )\n";

    [Fact]
    public async Task TheAreaportalPortalsAreTheOneThreadAnswerRunAfterRunAtThirtyTwo()
    {
        // A real stock portal file, axis-aligned corridors meeting in a room,
        // with far more workers than portals: the heaviest splitting of the
        // lightest walks, thirty runs.
        PortalFile file = await PortalFile.ParseAsync(System.Text.Encoding.ASCII.GetBytes(AreaportalPrt), CancellationToken.None);
        ulong[][] answer = OneThreadAnswer(new Grid(PortalSet.FromPortalFile(file)));
        for (int round = 0; round < 30; round++)
        {
            Grid grid = new(PortalSet.FromPortalFile(file));
            VisTightening tightening = new(grid.State);
            using WorkQueue queue = new(new CompileParallelism { MaxDegree = 32 });
            await tightening.RunAsync(
                queue,
                new VisPortalFlow?[queue.Degree],
                () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
                progress: null,
                CancellationToken.None);

            for (int rank = 0; rank < answer.Length; rank++)
            {
                Assert.Equal(answer[rank], grid.VisOf(rank));
            }
        }
    }

    [Fact]
    public async Task EveryPortalIsPublishedDoneExactlyOnce()
    {
        Grid grid = new();
        VisTightening tightening = new(grid.State) { DoneAt = [] };
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 32 });
        await tightening.RunAsync(
            queue,
            new VisPortalFlow?[queue.Degree],
            () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
            progress: null,
            CancellationToken.None);

        Assert.Equal(
            Enumerable.Range(0, grid.Portals.Count),
            tightening.DoneAt!.Select(d => d.Rank).Order());
    }

    [Fact]
    public async Task SeventyWorkersSpanningTwoParkingWordsGiveTheOneThreadAnswer()
    {
        // More workers than one 64-bit word of parking bits: the schedule's
        // wakes have to reach workers 64 and up, or those wait out their
        // timeouts (slow, not wrong) -- and every portal must still be done
        // exactly once with the one-thread vectors.
        ulong[][] answer = OneThreadAnswer(new Grid(SlantedGrid(6, 4)));
        for (int round = 0; round < 5; round++)
        {
            Grid grid = new(SlantedGrid(6, 4));
            VisTightening tightening = new(grid.State) { DoneAt = [] };
            using WorkQueue queue = new(new CompileParallelism { MaxDegree = 70 });
            Assert.Equal(70, queue.Degree);
            await tightening.RunAsync(
                queue,
                new VisPortalFlow?[queue.Degree],
                () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
                progress: null,
                CancellationToken.None);

            Assert.Equal(
                Enumerable.Range(0, grid.Portals.Count),
                tightening.DoneAt!.Select(d => d.Rank).Order());
            for (int rank = 0; rank < answer.Length; rank++)
            {
                Assert.Equal(answer[rank], grid.VisOf(rank));
            }
        }
    }

    /// <summary>Cancels a token when the flow stage reports its first finished portal.</summary>
    private sealed class CancelOnFirstReport(CancellationTokenSource source) : IProgress<CompileProgress>
    {
        public int Reports;

        public void Report(CompileProgress value)
        {
            Interlocked.Increment(ref Reports);
            source.Cancel();
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    public async Task CancellingMidRunStopsEveryWorkerAndTheQueueRunsTheNextCompile(int degree)
    {
        // Cancelled as the first portal is published: workers are then
        // flowing, parked, or on their way to either. Every one of them has to
        // notice -- a parked one through its wait's timeout -- or the run
        // never completes. The same queue then runs a whole fresh schedule to
        // the one-thread answer, which it cannot do if a worker of the first
        // is still holding a slot.
        //
        // Several rounds, because one round cannot promise the cancel lands
        // mid-run: the worker that publishes the first portal reports it only
        // after leaving the gate, and a thread descheduled there on a loaded
        // machine can find the other workers have finished the whole small
        // grid meanwhile (seen once in about forty runs of the Vis tests).
        // Every round must stop cleanly and leave the queue whole; at least
        // one must have been cut short, which all but a freak schedule are.
        const int Rounds = 6;
        ulong[][] answer = OneThreadAnswer(new Grid(SlantedGrid(8, 6)));
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = degree, CancellationPollInterval = 1 });
        int cutShort = 0;
        for (int round = 0; round < Rounds; round++)
        {
            Grid cancelled = new(SlantedGrid(8, 6));
            VisTightening first = new(cancelled.State) { Window = 4096, DoneAt = [] };
            using CancellationTokenSource source = new();
            CancelOnFirstReport progress = new(source);
            Task run = first.RunAsync(
                queue,
                new VisPortalFlow?[queue.Degree],
                () => new VisPortalFlow(cancelled.Portals, cancelled.State, BitVectorPath.Auto),
                progress,
                source.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.True(progress.Reports > 0);
            if (first.DoneAt!.Count < cancelled.Portals.Count)
            {
                cutShort++;
            }

            Grid grid = new(SlantedGrid(8, 6));
            VisTightening second = new(grid.State);
            await second.RunAsync(
                queue,
                new VisPortalFlow?[queue.Degree],
                () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
                progress: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

            for (int rank = 0; rank < answer.Length; rank++)
            {
                Assert.Equal(answer[rank], grid.VisOf(rank));
            }
        }

        Assert.True(cutShort > 0);
    }

    [Fact]
    public void TheClaimHintSeesFreshPortalsInsideTheWindow()
    {
        Grid grid = new();
        VisTightening tightening = new(grid.State) { Window = 3 };
        int count = grid.Portals.Count;

        Assert.True(tightening.MayClaim(count));
        Assert.Equal(3, tightening.Offered(count));
    }

    [Fact]
    public void TheClaimHintSeesNothingWhenTheWindowIsShut()
    {
        // A window of nothing offers no fresh portal and no run has been
        // found inexact: an idle worker neither takes the gate nor is woken.
        Grid grid = new();
        VisTightening tightening = new(grid.State) { Window = 0 };
        int count = grid.Portals.Count;

        Assert.False(tightening.MayClaim(count));
        Assert.Equal(0, tightening.Offered(count));
    }

    [Fact]
    public void TheClaimHintNeverOffersMorePortalsThanThereAre()
    {
        Grid grid = new();
        VisTightening tightening = new(grid.State) { Window = 1 << 20 };
        int count = grid.Portals.Count;

        Assert.Equal(count, tightening.Offered(count));
    }

    [Fact]
    public async Task OnceEveryPortalIsDoneTheHintSendsEveryWorkerToTheEnd()
    {
        // The end is not an offer, but every idle worker has to take the gate
        // once more to learn of it and return, so it counts as one -- and
        // wakes everybody.
        Grid grid = new();
        VisTightening tightening = new(grid.State);
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 4 });
        await tightening.RunAsync(
            queue,
            new VisPortalFlow?[queue.Degree],
            () => new VisPortalFlow(grid.Portals, grid.State, BitVectorPath.Auto),
            progress: null,
            CancellationToken.None);

        int count = grid.Portals.Count;
        Assert.True(tightening.MayClaim(count));
        Assert.Equal(int.MaxValue, tightening.Offered(count));
    }
}
