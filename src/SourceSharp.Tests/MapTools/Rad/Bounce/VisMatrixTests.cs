//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapTools.Rad.Light;

using Xunit;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.Tests.MapTools.Rad.Bounce;

/// <summary><c>MakeScales</c>.</summary>
public sealed class MakeScalesTests
{
    /// <summary>A total at or under pi is divided by pi: <c>1.0f/M_PI</c>, a double narrowed.</summary>
    [Fact]
    public void ATotalUnderPiIsDividedByPi()
    {
        Transfer[] t = [new(4, 1.0f), new(9, 2.0f)];
        VisMatrix.MakeScales(t);
        float scale = (float)(1.0f / Math.PI);
        Assert.Equal(new Transfer(4, 1.0f * scale), t[0]);
        Assert.Equal(new Transfer(9, 2.0f * scale), t[1]);
    }

    /// <summary>A total above pi is divided by itself, so the list sums to 1.</summary>
    [Fact]
    public void ATotalOverPiIsDividedByItself()
    {
        Transfer[] t = [new(1, 3.0f), new(2, 5.0f)];
        VisMatrix.MakeScales(t);
        Assert.Equal(3.0f * (1.0f / 8.0f), t[0].Weight);
        Assert.Equal(5.0f * (1.0f / 8.0f), t[1].Weight);
    }

    /// <summary>The source patches are kept, in order.</summary>
    [Fact]
    public void ThePatchesAreKeptInOrder()
    {
        Transfer[] t = [new(7, 1f), new(3, 1f), new(5, 1f)];
        VisMatrix.MakeScales(t);
        Assert.Equal([7, 3, 5], t.Select(x => x.Patch));
    }

    /// <summary>An empty list is left alone.</summary>
    [Fact]
    public void AnEmptyListIsANoOp() => VisMatrix.MakeScales([]);
}

/// <summary>The transfer stream's ray.</summary>
public sealed class TransferRayTests
{
    /// <summary>The reach is the segment's length, stock's <c>ray_length</c>.</summary>
    [Fact]
    public void TheReachIsTheLength()
    {
        Ray r = VisMatrix.TransferRay(new(1, 2, 3), new(4, 6, 3), estimate: false);
        Assert.Equal(5f, r.MaxDistance);
    }

    /// <summary>Correct divides: the direction is exactly delta / length.</summary>
    [Fact]
    public void CorrectDividesTheDirection()
    {
        Ray r = VisMatrix.TransferRay(new(1, 2, 3), new(4, 6, 3), estimate: false);
        Assert.Equal((3f / 5f, 4f / 5f, 0f), (r.DirectionX, r.DirectionY, r.DirectionZ));
    }

    /// <summary>The ray starts where it was asked to.</summary>
    [Fact]
    public void TheOriginIsTheStart()
    {
        Ray r = VisMatrix.TransferRay(new(1, 2, 3), new(4, 6, 3), estimate: true);
        Assert.Equal((1f, 2f, 3f), (r.OriginX, r.OriginY, r.OriginZ));
    }

    /// <summary>
    /// Stock multiplies by <c>ReciprocalSaturateSIMD</c>, an estimate within
    /// one Newton step of exact.
    /// </summary>
    [Fact]
    public void TheEstimateIsCloseToTheDivide()
    {
        Vec3 end = new(123.4f, -56.7f, 89.1f);
        Ray e = VisMatrix.TransferRay(Vec3.Zero, end, estimate: true);
        Ray x = VisMatrix.TransferRay(Vec3.Zero, end, estimate: false);
        Assert.Equal(x.DirectionX, e.DirectionX, 1e-6f);
        Assert.Equal(x.MaxDistance, e.MaxDistance);
    }

    /// <summary>A zero-length segment reaches nowhere and has a finite direction (<c>FLT_EPSILON</c>).</summary>
    [Fact]
    public void AZeroLengthSegmentIsFinite()
    {
        Ray r = VisMatrix.TransferRay(new(1, 1, 1), new(1, 1, 1), estimate: true);
        Assert.Equal(0f, r.MaxDistance);
        Assert.True(float.IsFinite(r.DirectionX));
    }
}

/// <summary><c>MakeTransfer</c> on the box's patches.</summary>
public sealed class MakeTransferTests
{
    private static (VisMatrix Matrix, RadWorld World) Box(SurfaceFlags ceiling = 0)
    {
        RadWorld world = BounceBox.Build(BounceBox.Map(ceiling: ceiling));
        return (new VisMatrix(world.BounceContext()), world);
    }

    /// <summary>Light is never taken from a sky patch.</summary>
    [Fact]
    public void ASkySourceMakesNoTransfer()
    {
        (VisMatrix m, RadWorld w) = Box(SurfaceFlags.Sky);
        Assert.False(m.MakeTransfer(BounceBox.Leaves(w, 0)[0], BounceBox.Leaves(w, 1)[0], out _));
    }

    /// <summary>A source with no area makes none.</summary>
    [Fact]
    public void AZeroAreaSourceMakesNoTransfer()
    {
        (VisMatrix m, RadWorld w) = Box();
        int source = BounceBox.Leaves(w, 1)[0];
        w.Patches.At(source).Area = 0;
        Assert.False(m.MakeTransfer(BounceBox.Leaves(w, 0)[0], source, out _));
    }

    /// <summary>Two patches in one plane have a zero form factor and make none.</summary>
    [Fact]
    public void CoplanarPatchesMakeNoTransfer()
    {
        (VisMatrix m, RadWorld w) = Box();
        List<int> floor = BounceBox.Leaves(w, 0);
        Assert.False(m.MakeTransfer(floor[0], floor[^1], out _));
    }

    /// <summary>
    /// A far source -- pi * 0.04 * d^2 at or above its area -- uses the
    /// differential form factor: trans = area * FormFactorDiffToDiff(source,
    /// receiver).
    /// </summary>
    [Fact]
    public void AFarSourceUsesTheDifferentialFormFactor()
    {
        (VisMatrix m, RadWorld w) = Box();
        int receiver = BounceBox.Leaves(w, 0)[0];
        int source = BounceBox.Leaves(w, 1)[^1];
        ref Patch r = ref w.Patches.At(receiver);
        ref Patch s = ref w.Patches.At(source);
        Vec3 d = r.Origin - s.Origin;
        Assert.True((float)(Math.PI * 0.04 * Vec3.Dot(d, d)) >= s.Area);

        Assert.True(m.MakeTransfer(receiver, source, out float trans));
        Assert.Equal(s.Area * FormFactors.DiffToDiff(s.Origin, s.Normal, r.Origin, r.Normal, false), trans);
    }

    /// <summary>A near source uses the polygon form factor over its winding.</summary>
    [Fact]
    public void ANearSourceUsesThePolygonFormFactor()
    {
        (VisMatrix m, RadWorld w) = Box();
        (int receiver, int source) = NearPair(w);
        ref Patch r = ref w.Patches.At(receiver);
        ref Patch s = ref w.Patches.At(source);

        Assert.True(m.MakeTransfer(receiver, source, out float trans));
        float poly = FormFactors.PolyToDiff(
            w.Patches.Arena.Points(s.Winding), s.Area, r.Origin, r.Normal, false, ComplianceOptions.Correct);
        Assert.Equal(s.Area * poly, trans);
    }

    /// <summary>A transfer at or below <c>TRANSFER_EPSILON</c> is dropped.</summary>
    [Fact]
    public void ATransferUnderTheEpsilonIsDropped()
    {
        (VisMatrix m, RadWorld w) = Box();
        int source = BounceBox.Leaves(w, 1)[0];
        w.Patches.At(source).Area = 1e-12f;
        Assert.False(m.MakeTransfer(BounceBox.Leaves(w, 0)[0], source, out _));
    }

    private static (int Receiver, int Source) NearPair(RadWorld w)
    {
        foreach (int r in BounceBox.Leaves(w, 0))
        {
            foreach (int s in BounceBox.Leaves(w, 2))
            {
                Vec3 d = w.Patches.At(r).Origin - w.Patches.At(s).Origin;
                if ((float)(Math.PI * 0.04 * Vec3.Dot(d, d)) < w.Patches.At(s).Area)
                {
                    return (r, s);
                }
            }
        }

        throw new InvalidOperationException("the box has no near floor/wall pair");
    }
}

/// <summary><c>BuildVisMatrix</c> end to end on the box.</summary>
public sealed class VisMatrixBuildTests
{
    private static async Task<(VisMatrix Matrix, TransferSet Transfers, RadWorld World)> BuildAsync(
        LightTestMap map, DirectLightingSettings? settings = null, Action<RadWorld>? mutate = null)
    {
        RadWorld world = BounceBox.Build(map, settings);
        mutate?.Invoke(world);
        VisMatrix matrix = new(world.BounceContext());
        using WorkQueue queue = new(BounceBox.One);
        TransferSet set = await matrix.BuildAsync(map.Tracer(), queue, CancellationToken.None);
        return (matrix, set, world);
    }

    /// <summary>
    /// Cutting the receivers into many small chunks, each its own segment of
    /// the set, gives every patch the same list as one chunk does.
    /// </summary>
    [Fact]
    public async Task TheChunkSizeDoesNotChangeAnyList()
    {
        LightTestMap map = BounceBox.Map();
        (VisMatrix one, TransferSet whole, RadWorld w) = await BuildAsync(map);
        RadWorld world = BounceBox.Build(map);
        VisMatrix small = new(world.BounceContext()) { ChunkRays = 64 };
        using WorkQueue queue = new(new CompileParallelism { MaxDegree = 4 });
        TransferSet split = await small.BuildAsync(map.Tracer(), queue, CancellationToken.None);

        Assert.Equal(1, one.Statistics.Chunks);
        Assert.True(small.Statistics.Chunks > 1);
        Assert.Equal(whole.Total, split.Total);
        Assert.Equal(whole.Max, split.Max);
        Assert.Equal(whole.Arena.ToArray(), split.Arena.ToArray());
        for (int p = 0; p < w.Patches.Count; p++)
        {
            Assert.Equal(whole.For(p).ToArray(), split.For(p).ToArray());
        }
    }

    /// <summary>"don't check patches on the same face".</summary>
    [Fact]
    public async Task NoPatchTakesLightFromItsOwnFace()
    {
        (_, TransferSet t, RadWorld w) = await BuildAsync(BounceBox.Map());
        for (int p = 0; p < w.Patches.Count; p++)
        {
            int face = w.Patches.At(p).FaceNumber;
            foreach (Transfer x in t.For(p))
            {
                Assert.NotEqual(face, w.Patches.At(x.Patch).FaceNumber);
            }
        }
    }

    /// <summary>
    /// The same-face skip holds even for a receiver lifted off its face's
    /// plane -- the case of a displacement patch, whose origin is on the
    /// displaced surface -- where the plane tests alone would let the face's
    /// own lifted patches through.
    /// </summary>
    [Fact]
    public void ALiftedReceiverStillSkipsItsOwnFace()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        foreach (int p in BounceBox.Leaves(w, 0))
        {
            w.Patches.At(p).Origin += new Vec3(0, 0, 10);
        }

        VisMatrix m = new(w.BounceContext());
        int receiver = BounceBox.Leaves(w, 0)[0];
        Assert.DoesNotContain(m.Candidates(receiver), s => w.Patches.At(s).FaceNumber == 0);
    }

    /// <summary>
    /// The enumeration reads its sources from a dense copy of the patches;
    /// its candidates must be exactly the ones a walk over the patches
    /// themselves finds. The patches are altered first so the copy's every
    /// field decides something: areas spread over four orders of magnitude
    /// (so a near parent is split into its children for some receivers and
    /// not others), and two faces' root chains joined (so the walk follows a
    /// NextParent link).
    /// </summary>
    [Fact]
    public void TheCandidatesAreTheOnesAWalkOverThePatchesFinds()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        PatchSet patches = w.Patches;
        Random r = new(31);
        for (int p = 0; p < patches.Count; p++)
        {
            patches.At(p).Area *= (float)Math.Pow(10, (r.NextDouble() * 4) - 2);
        }

        int ceiling = patches.FaceParents[1];
        int wall = patches.FaceParents[2];
        Assert.Equal(Patch.Invalid, patches.At(ceiling).NextParent);
        patches.At(ceiling).NextParent = wall;

        VisMatrix m = new(w.BounceContext());
        int split = 0;
        for (int receiver = 0; receiver < patches.Count; receiver++)
        {
            if (patches.At(receiver).Child1 != Patch.Invalid || patches.At(receiver).ClusterNumber < 0)
            {
                continue;
            }

            List<int> expected = [];
            split += ReferenceCandidates(patches, receiver, expected);
            int[] actual = m.Candidates(receiver);
            Array.Sort(actual);
            expected.Sort();
            Assert.Equal(expected, actual);
        }

        Assert.True(split > 0, "no parent was split into its children");
    }

    // BuildVisRow's walk over the Patch structs, for the one-leaf box: every
    // face but the receiver's own, root chains, children when near.
    private static int ReferenceCandidates(PatchSet patches, int receiver, List<int> into)
    {
        ref Patch me = ref patches.At(receiver);
        int split = 0;
        for (int face = 0; face < patches.FaceParents.Length; face++)
        {
            int head = patches.FaceParents[face];
            if (face == me.FaceNumber || head == Patch.Invalid)
            {
                continue;
            }

            ref Patch first = ref patches.At(head);
            if (!(Vec3.Dot(me.Origin, first.Normal) > first.CachedPlaneDist + VisMatrix.PlaneTestEpsilon))
            {
                continue;
            }

            for (int p = head; p != Patch.Invalid; p = patches.At(p).NextParent)
            {
                split += Test(patches, receiver, p, into);
            }
        }

        return split;
    }

    private static int Test(PatchSet patches, int receiver, int source, List<int> into)
    {
        ref Patch me = ref patches.At(receiver);
        ref Patch s = ref patches.At(source);
        if (s.Child1 != Patch.Invalid)
        {
            Vec3 tmp = me.Origin - s.Origin;
            if (Vec3.Dot(tmp, tmp) * 0.0625 < s.Area)
            {
                return 1 + Test(patches, receiver, s.Child1, into) + Test(patches, receiver, s.Child2, into);
            }
        }

        if (Vec3.Dot(s.Origin, me.PlaneNormal) > me.CachedPlaneDist + VisMatrix.PlaneTestEpsilon)
        {
            into.Add(source);
        }

        return 0;
    }

    /// <summary>A receiver's candidates include the opposite face's patches.</summary>
    [Fact]
    public void TheCeilingIsACandidateOfTheFloor()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        VisMatrix m = new(w.BounceContext());
        Assert.Contains(m.Candidates(BounceBox.Leaves(w, 0)[0]), s => w.Patches.At(s).FaceNumber == 1);
    }

    /// <summary>The floor takes light from the ceiling.</summary>
    [Fact]
    public async Task TheFloorReceivesFromTheCeiling()
    {
        (_, TransferSet t, RadWorld w) = await BuildAsync(BounceBox.Map());
        int floor = BounceBox.Leaves(w, 0)[0];
        Assert.Contains(t.For(floor).ToArray(), x => w.Patches.At(x.Patch).FaceNumber == 1);
    }

    /// <summary>A cluster whose PVS row does not include itself tests nothing.</summary>
    [Fact]
    public async Task APvsThatExcludesTheClusterMakesNoTransfers()
    {
        (VisMatrix m, TransferSet t, _) = await BuildAsync(BounceBox.Map(seesItself: false));
        Assert.Equal(0, t.Total);
        Assert.Equal(0, m.Statistics.Rays);
    }

    /// <summary>A blocked ray makes no transfer.</summary>
    [Fact]
    public async Task AnOccluderRemovesTransfers()
    {
        (_, TransferSet open, _) = await BuildAsync(BounceBox.Map());
        LightTestMap map = BounceBox.Map();
        map.AddOccluder(new(8, 8, 128), new(248, 8, 128), new(248, 248, 128), new(8, 248, 128));
        (VisMatrix m, TransferSet shut, _) = await BuildAsync(map);

        Assert.True(shut.Total < open.Total);
        Assert.True(m.Statistics.Blocked > 0);
    }

    /// <summary>Only leaf patches in a cluster receive; a parent has no list.</summary>
    [Fact]
    public async Task OnlyClusterChildrenHaveTransfers()
    {
        (VisMatrix m, TransferSet t, RadWorld w) = await BuildAsync(BounceBox.Map());
        HashSet<int> receivers = [.. m.ReceiverOrder()];
        for (int p = 0; p < w.Patches.Count; p++)
        {
            if (!receivers.Contains(p))
            {
                Assert.Equal(0, t.CountFor(p));
            }
        }
    }

    /// <summary>The total and the maximum are the per-patch counts' sum and max.</summary>
    [Fact]
    public async Task TotalAndMaxAreTheCounts()
    {
        (_, TransferSet t, RadWorld w) = await BuildAsync(BounceBox.Map());
        long sum = 0;
        int max = 0;
        for (int p = 0; p < w.Patches.Count; p++)
        {
            sum += t.CountFor(p);
            max = Math.Max(max, t.CountFor(p));
        }

        Assert.Equal(sum, t.Total);
        Assert.Equal(max, t.Max);
    }

    /// <summary>Every receiver's list is scaled: it sums to at most 1.</summary>
    [Fact]
    public async Task EveryListSumsToAtMostOne()
    {
        (_, TransferSet t, RadWorld w) = await BuildAsync(BounceBox.Map());
        for (int p = 0; p < w.Patches.Count; p++)
        {
            float sum = 0;
            foreach (Transfer x in t.For(p))
            {
                sum += x.Weight;
            }

            Assert.True(sum <= 1.0001f, $"patch {p}: {sum}");
        }
    }

    /// <summary>
    /// <see cref="StockQuirk.VisPlaneTestPhongNormal"/>, stock side: tilting
    /// the floor's shading normals changes which candidates pass.
    /// </summary>
    [Fact]
    public async Task StockPlaneTestFollowsTheShadingNormal()
    {
        DirectLightingSettings stock = LightBox.Settings(stock: true);
        (VisMatrix flat, _, _) = await BuildAsync(BounceBox.Map(), stock);
        (VisMatrix tilted, _, _) = await BuildAsync(BounceBox.Map(), stock, TiltFloor);
        Assert.NotEqual(flat.Statistics.Rays, tilted.Statistics.Rays);
    }

    /// <summary>
    /// <see cref="StockQuirk.VisPlaneTestPhongNormal"/>, correct side: the
    /// plane test takes the plane's normal, so tilting the shading normals
    /// leaves the candidates unchanged.
    /// </summary>
    [Fact]
    public async Task CorrectPlaneTestIgnoresTheShadingNormal()
    {
        (VisMatrix flat, _, _) = await BuildAsync(BounceBox.Map());
        (VisMatrix tilted, _, _) = await BuildAsync(BounceBox.Map(), null, TiltFloor);
        Assert.Equal(flat.Statistics.Rays, tilted.Statistics.Rays);
    }

    /// <summary>The cluster table lists a cluster's leaves.</summary>
    [Fact]
    public void TheClusterTableListsTheLeaf()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        ClusterTables tables = ClusterTables.Build(w.Geometry, w.Patches, 1);
        Assert.Equal([0], tables.Leaves(0).ToArray());
    }

    /// <summary>A map with no displacements has no displacement faces in any cluster.</summary>
    [Fact]
    public void NoDisplacementsMeansNoDispFaces()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        Assert.True(ClusterTables.Build(w.Geometry, w.Patches, 1).DispFaces(0).IsEmpty);
    }

    /// <summary>
    /// Receivers go cluster by cluster in each cluster's child-list order.
    /// </summary>
    [Fact]
    public void ReceiversFollowTheClusterChildList()
    {
        RadWorld w = BounceBox.Build(BounceBox.Map());
        List<int> expected = [];
        for (int p = w.Patches.ClusterChildren[0]; p != Patch.Invalid; p = w.Patches.At(p).NextClusterChild)
        {
            expected.Add(p);
        }

        Assert.Equal(expected, new VisMatrix(w.BounceContext()).ReceiverOrder());
    }

    private static void TiltFloor(RadWorld w)
    {
        Vec3 tilt = new Vec3(-0.8f, 0f, 0.6f);
        foreach (int p in BounceBox.Leaves(w, 0))
        {
            w.Patches.At(p).Normal = tilt;
        }
    }
}

/// <summary>The transfer lists, kept in the build's segments.</summary>
public sealed class TransferSetTests
{
    private static TransferSet Two() => new(
        [[new(5, 0.5f), new(6, 0.25f), new(7, 1.0f)], [new(1, 2.0f), new(2, 3.0f)]],
        segmentOf: [0, 0, 1, 0, 1],
        offsets: [0, 1, 0, 0, 1],
        counts: [1, 2, 1, 0, 1],
        max: 2);

    /// <summary>A patch's list is its run in its own segment.</summary>
    [Fact]
    public void AListIsItsRunInItsSegment()
    {
        TransferSet t = Two();
        Assert.Equal([new Transfer(5, 0.5f)], t.For(0).ToArray());
        Assert.Equal([new Transfer(6, 0.25f), new Transfer(7, 1.0f)], t.For(1).ToArray());
        Assert.Equal([new Transfer(1, 2.0f)], t.For(2).ToArray());
        Assert.Equal([new Transfer(2, 3.0f)], t.For(4).ToArray());
    }

    /// <summary>A patch with no transfers reads empty, whatever its segment says.</summary>
    [Fact]
    public void AnEmptyListIsEmpty()
    {
        TransferSet t = Two();
        Assert.True(t.For(3).IsEmpty);
        Assert.Equal(0, t.CountFor(3));
    }

    /// <summary>The totals count every segment.</summary>
    [Fact]
    public void TheTotalsCoverEverySegment()
    {
        TransferSet t = Two();
        Assert.Equal(5, t.Total);
        Assert.Equal(2, t.Max);
        Assert.Equal(5, t.PatchCount);
    }

    /// <summary>The arena is the segments joined in order.</summary>
    [Fact]
    public void TheArenaJoinsTheSegmentsInOrder()
    {
        TransferSet t = Two();
        Assert.Equal([5, 6, 7, 1, 2], t.Arena.ToArray().Select(x => x.Patch));
    }

    /// <summary>A set with no segments is empty.</summary>
    [Fact]
    public void NoSegmentsIsEmpty()
    {
        TransferSet t = new([], [0, 0], [0, 0], [0, 0], 0);
        Assert.Equal(0, t.Total);
        Assert.True(t.Arena.IsEmpty);
        Assert.True(t.For(1).IsEmpty);
    }
}
