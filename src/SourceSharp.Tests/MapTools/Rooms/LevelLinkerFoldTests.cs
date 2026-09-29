//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapGen.Rooms;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The brush fold inside the link: on by default, the unfolded link's map
/// with only the brush lumps (and what names brushes) changed, the same
/// solid to every trace but at the seams it removes, and deterministic.
/// </summary>
public sealed class LevelLinkerFoldTests(Rooms3x3Fixture fixture, ITestOutputHelper output) : IClassFixture<Rooms3x3Fixture>
{
    private static readonly LevelLinkOptions NoFold = new() { FoldBrushes = false };

    /// <summary>The lumps a fold may change: the brushes, their sides, the leaves' brush runs and the ledges' client data.</summary>
    private static readonly BspLump[] FoldLumps =
        [BspLump.Brushes, BspLump.BrushSides, BspLump.LeafBrushes, BspLump.Leafs, BspLump.PhysCollide];

    /// <summary>
    /// The folded map is the unfolded one with exactly
    /// <see cref="LinkBrushFold"/> applied: the brushes and sides the fold
    /// makes of the unfolded ones, every leaf's run renumbered through its
    /// map without repeats, every ledge's client data renumbered, and every
    /// other lump, the planes included (the fold makes none), byte for byte.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task TheFoldedMapIsTheUnfoldedOneWithTheFoldApplied(string name)
    {
        (LinkedLevel folded, LinkedLevel unfolded) = await BothAsync(name);
        BrushFoldResult fold = FoldOf(unfolded.Bsp);
        Assert.True(fold.Removed > 0, $"{name}: nothing folded");
        Assert.Equal(fold.Removed, folded.FoldedBrushes);
        Assert.Equal(0, unfolded.FoldedBrushes);

        Assert.Equal(fold.Brushes, BspStructView.As<DBrush>(folded.Bsp[BspLump.Brushes]).ToArray());
        Assert.Equal(fold.Sides, BspStructView.As<DBrushSide>(folded.Bsp[BspLump.BrushSides]).ToArray());
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (!FoldLumps.Contains((BspLump)i))
            {
                Assert.True(folded.Bsp[i].Data.Span.SequenceEqual(unfolded.Bsp[i].Data.Span), $"{(BspLump)i} differs");
            }
        }

        // Leaves: the same but for the brush runs, each the unfolded run
        // through the map, first occurrences kept.
        DLeaf[] foldedLeafs = BspStructView.As<DLeaf>(folded.Bsp[BspLump.Leafs]).ToArray();
        DLeaf[] unfoldedLeafs = BspStructView.As<DLeaf>(unfolded.Bsp[BspLump.Leafs]).ToArray();
        ushort[] foldedRuns = BspStructView.As<ushort>(folded.Bsp[BspLump.LeafBrushes]).ToArray();
        ushort[] unfoldedRuns = BspStructView.As<ushort>(unfolded.Bsp[BspLump.LeafBrushes]).ToArray();
        Assert.Equal(unfoldedLeafs.Length, foldedLeafs.Length);
        for (int l = 0; l < foldedLeafs.Length; l++)
        {
            DLeaf a = foldedLeafs[l], b = unfoldedLeafs[l];
            int[] expected = [.. unfoldedRuns.Skip(b.FirstLeafBrush).Take(b.NumLeafBrushes).Select(x => fold.Map[x]).Distinct()];
            Assert.Equal(expected, foldedRuns.Skip(a.FirstLeafBrush).Take(a.NumLeafBrushes).Select(x => (int)x));
            a.FirstLeafBrush = b.FirstLeafBrush;
            a.NumLeafBrushes = b.NumLeafBrushes;
            Assert.True(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DLeaf>(in a))
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DLeaf>(in b))),
                $"leaf {l} differs beyond its brush run");
        }

        // Ledges: the same geometry, the client data through the map.
        Assert.True(folded.Bsp[BspLump.PhysCollide].Length > 0, $"{name} has no collision to check");
        Assert.Equal(
            LinkedBrushProbe.CollisionWithNumbering(unfolded.Bsp, fold.Map),
            folded.Bsp[BspLump.PhysCollide].Data.ToArray());
    }

    /// <summary>
    /// Every merged brush is an axial box of six sides with no bevel and no
    /// displacement, whose volume is exactly its constituents' (the union
    /// is the box, nothing added), of their contents, each side on the
    /// surface every constituent had there; every ledge names a brush of its
    /// own contents class.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("seed_9")]
    public async Task AMergedBrushIsExactlyTheUnionOfItsConstituents(string name)
    {
        (LinkedLevel folded, LinkedLevel unfolded) = await BothAsync(name);
        BrushFoldResult fold = FoldOf(unfolded.Bsp);
        DBrush[] before = BspStructView.As<DBrush>(unfolded.Bsp[BspLump.Brushes]).ToArray();
        WorldBrushTrace surfaces = new(folded.Bsp);
        int merged = 0;
        for (int b = 0; b < fold.Brushes.Length; b++)
        {
            int[] group = fold.Groups[b];
            if (group.Length == 1)
            {
                Assert.Equal(LinkedBrushProbe.Geometry(unfolded.Bsp, group[0]), LinkedBrushProbe.Geometry(folded.Bsp, b));
                continue;
            }

            merged++;
            DBrush brush = fold.Brushes[b];
            Assert.Equal(6, brush.NumSides);
            Assert.All(fold.Sides.Skip(brush.FirstSide).Take(6), s => Assert.Equal((0, 0), ((int)s.Bevel, (int)s.DispInfo)));
            Assert.All(group, g => Assert.Equal(before[g].Contents, brush.Contents));

            Box box = LinkedBrushProbe.BrushBox(folded.Bsp, b);
            double volume = group.Sum(g => Volume(LinkedBrushProbe.BrushBox(unfolded.Bsp, g)));
            Assert.Equal(Volume(box), volume);
            foreach (int g in group)
            {
                Box part = LinkedBrushProbe.BrushBox(unfolded.Bsp, g);
                Assert.True(part.ContainsWithin(box, 0), $"brush {g} is not inside the box it joined");
            }
        }

        Assert.True(merged > 0, "no merged brush to check");
        foreach ((int contents, SourceSharp.MapTools.Phys.Managed.IvpCompactLedge ledge) in LinkedBrushProbe.Ledges(folded.Bsp))
        {
            Assert.NotEqual(0, fold.Brushes[ledge.ClientData].Contents & contents);
            Assert.True(
                LedgeInside(LinkedBrushProbe.LedgeBox(ledge), LinkedBrushProbe.BrushBox(folded.Bsp, ledge.ClientData)),
                $"a ledge is not inside brush {ledge.ClientData}");
        }
    }

    /// <summary>
    /// The folded link is the same bytes on one thread or eight, with rooms
    /// planned on the fly (so the planning runs on the pool) or from stored
    /// link data, and on a second run.
    /// </summary>
    [Fact]
    public async Task TheFoldIsTheSameOnAnyThreadCountAndRunAfterRun()
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(true, RoomHarness.Hub());
        RoomObject hub = compiled.Get("hub");
        RoomLibrary onTheFly = RoomHarness.Library(hub with { Link = null });
        RoomLibrary stored = RoomHarness.Library(hub with { Link = await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None) });
        LevelLayout layout = RandomGrid(onTheFly, new Random(11), 4, 3);

        List<BspData> maps = [];
        foreach ((RoomLibrary library, int degree) in (IEnumerable<(RoomLibrary, int)>)[(onTheFly, 1), (onTheFly, 8), (stored, 1), (onTheFly, 8)])
        {
            LinkedLevel link = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(degree: degree));
            Assert.True(link.FoldedBrushes > 0);
            maps.Add(link.Bsp);
        }

        foreach (BspData other in maps.Skip(1))
        {
            for (int i = 0; i < BspData.HeaderLumps; i++)
            {
                Assert.True(maps[0][i].Data.Span.SequenceEqual(other[i].Data.Span), $"{(BspLump)i} differs");
            }
        }
    }

    /// <summary>
    /// Traces meet the same solid in the folded map as in the unfolded one,
    /// on random levels of turned hubs and on the sample's levels (details,
    /// grates, player clip), for point and box sweeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sweep that starts outside every brush gets the same answer in
    /// both: the same fraction to the bit, the same contents and the same
    /// surface, or both miss. The one allowance is the removed seam: if the
    /// unfolded sweep stopped on a plane that lies strictly inside a merged
    /// box (the face between two constituents, which only the 1/32-unit
    /// trace epsilon could let a sweep grazing the union stop on), the
    /// folded sweep must go at least as far. That case is counted; on these
    /// rays it does not occur.
    /// </para>
    /// <para>
    /// A sweep that starts inside a brush starts inside one in both, of the
    /// same contents. What it reports past that differs at a seam by
    /// construction: unfolded, a sweep from one constituent into the next
    /// leaves the first and is not all solid; folded it never leaves the box.
    /// So all-solid may go from false to true, and only when the sweep's end
    /// is inside the merged box that holds its start; and fractions of
    /// start-solid sweeps are not compared.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task TracesMeetTheSameSolidButAtTheRemovedSeams(int seed)
    {
        Random random = new(seed);
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        LevelLayout layout = RandomGrid(library, random, 2 + random.Next(3), 2 + random.Next(3));
        LinkedLevel folded = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());
        LinkedLevel unfolded = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(), NoFold);
        output.WriteLine($"hubs {layout.Rooms.Count}: {AssertTracesAgree(folded.Bsp, unfolded.Bsp, random, 3000)}");

        string[] cases = ["rooms3x3", "rooms3x3_turn1", "seed_9"];
        (LinkedLevel sampleFolded, LinkedLevel sampleUnfolded) = await BothAsync(cases[seed % cases.Length]);
        output.WriteLine($"{cases[seed % cases.Length]}: {AssertTracesAgree(sampleFolded.Bsp, sampleUnfolded.Bsp, random, 3000)}");
    }

    /// <summary>
    /// The removed seam made on purpose: a player box resting a hair into
    /// one room's floor, swept into the next room's floor. Unfolded it starts
    /// solid in the first floor brush and leaves it at the seam (so it is not
    /// all solid); folded, the two floors are one box it never leaves.
    /// </summary>
    [Fact]
    public async Task ABoxSweptAcrossAFoldedFloorSeamIsAllSolid()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        LevelLayout layout = RoomHarness.AutoLayout("pair", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0));
        LinkedLevel folded = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());
        LinkedLevel unfolded = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync(), NoFold);

        // The floors' top is z = 16 (the kit's wall depth); the box's bottom
        // sits 1/64 unit into it, inside the trace epsilon, and the sweep
        // crosses the cell boundary x = 256 in the middle of the rooms.
        float floor = RoomHarness.Kit.Depth;
        Vec3 extents = new(16, 16, 36);
        Vec3 start = new(200, 128, floor + extents.Z - (1f / 64));
        Vec3 end = new(312, 128, start.Z);
        WorldHit before = new WorldBrushTrace(unfolded.Bsp).Trace(start, end, extents);
        WorldHit after = new WorldBrushTrace(folded.Bsp).Trace(start, end, extents);
        Assert.True(before.StartSolid && after.StartSolid, "the box is not resting in the floor");
        Assert.False(before.AllSolid);
        Assert.True(after.AllSolid);
    }

    /// <summary>
    /// A grid of hubs whose kept brushes pass <c>MAX_MAP_BRUSHES</c> is
    /// refused before planning without the fold and links with it; a grid
    /// whose rooms alternate between two shell materials, so that no two
    /// rooms' pieces may merge, is refused after the fold, with the folded
    /// total.
    /// </summary>
    [Fact]
    public async Task TheBrushCapIsHeldAfterTheFold()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomDefinition other = RoomHarness.Hub() with { Name = "hubb" };
        library.Add(await RoomCompiler.CompileAsync(
            RoomHarness.BuildRoomModel(other, shell: "unit/othershell"), other, await RoomHarness.ContextAsync()));
        const int Size = 22; // 484 hubs: 10,648 brushes compiled, 8,800 kept
        int cap = BspLimits.Caps.First(c => c.Lump == BspLump.Brushes).Max;

        LevelLayout plain = Grid(library, Size, (_, _) => "hub");
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(plain, library, await RoomHarness.ContextAsync(), NoFold));
        Assert.Contains("brushes; the engine loads at most 8192 (MAX_MAP_BRUSHES)", refused.Message, StringComparison.Ordinal);

        LinkedLevel link = await LevelLinker.LinkAsync(plain, library, await RoomHarness.ContextAsync());
        Assert.InRange(BspStructView.Count<DBrush>(link.Bsp[BspLump.Brushes]), 1, cap);
        ValidationReport report = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));

        LevelLayout checkered = Grid(library, Size, (x, y) => (x + y) % 2 == 0 ? "hub" : "hubb");
        LinkException stillOver = await Assert.ThrowsAsync<LinkException>(
            async () => await LevelLinker.LinkAsync(checkered, library, await RoomHarness.ContextAsync()));
        Assert.Matches(@"^room hubb? at cell \(21, 21\) pushes the link to \d+ brushes; the engine loads at most 8192 \(MAX_MAP_BRUSHES\)\.$", stillOver.Message);
    }

    /// <summary>
    /// With the fold on, the capacity check leaves the brush and brush side
    /// caps to the assembly; with it off it holds them before planning.
    /// </summary>
    [Fact]
    public void TheUpFrontTotalsSkipTheBrushCapsOnlyWhenFolding()
    {
        LevelLinker.LinkTotals folding = new(checkBrushes: false);
        folding.Add(new LevelLinker.LinkCounts { Brushes = 9000, BrushSides = 70000 }, "a", 0, 0);

        LevelLinker.LinkTotals plain = new();
        Assert.Throws<LinkException>(() => plain.Add(new LevelLinker.LinkCounts { Brushes = 9000 }, "a", 0, 0));
        LevelLinker.LinkTotals sides = new();
        Assert.Throws<LinkException>(() => sides.Add(new LevelLinker.LinkCounts { BrushSides = 70000 }, "a", 0, 0));
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<(LinkedLevel Folded, LinkedLevel Unfolded)> BothAsync(string name)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        return (
            await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name)),
            await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name), NoFold));
    }

    /// <summary>The fold of an unfolded linked map, every brush foldable, as the link runs it.</summary>
    private static BrushFoldResult FoldOf(BspData unfolded)
    {
        DBrush[] brushes = BspStructView.As<DBrush>(unfolded[BspLump.Brushes]).ToArray();
        return LinkBrushFold.Fold(
            brushes,
            BspStructView.As<DBrushSide>(unfolded[BspLump.BrushSides]).ToArray(),
            BspStructView.As<DPlane>(unfolded[BspLump.Planes]).ToArray(),
            BspStructView.As<TexInfo>(unfolded[BspLump.TexInfo]).ToArray(),
            [.. Enumerable.Repeat(true, brushes.Length)]);
    }

    /// <summary>A grid of hubs, each turned at random.</summary>
    private static LevelLayout RandomGrid(RoomLibrary library, Random random, int columns, int rows)
    {
        List<(string, int, int, int)> cells = [];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                cells.Add(("hub", x, y, random.Next(4)));
            }
        }

        return RoomHarness.AutoLayout("random", library, [.. cells]);
    }

    private static LevelLayout Grid(RoomLibrary library, int size, Func<int, int, string> room)
    {
        LevelCell?[] cells = new LevelCell?[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                cells[(y * size) + x] = new LevelCell(room(x, y), 0);
            }
        }

        return new LevelGrid("grid", "rooms.vmf", size, size, cells)
            .ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
    }

    private static double Volume(Box box) =>
        (double)(box.Maxs.X - box.Mins.X) * (box.Maxs.Y - box.Mins.Y) * (box.Maxs.Z - box.Mins.Z);

    private static bool LedgeInside(Box ledge, Box brush) =>
        ledge.Mins.X >= brush.Mins.X - 0.5f && ledge.Mins.Y >= brush.Mins.Y - 0.5f && ledge.Mins.Z >= brush.Mins.Z - 0.5f
        && ledge.Maxs.X <= brush.Maxs.X + 0.5f && ledge.Maxs.Y <= brush.Maxs.Y + 0.5f && ledge.Maxs.Z <= brush.Maxs.Z + 0.5f;

    /// <summary>The trace agreement the fold promises (see <see cref="TracesMeetTheSameSolidButAtTheRemovedSeams"/>).</summary>
    /// <returns>What was met: hits, start-solid sweeps, all-solid only once folded, seam stops.</returns>
    private static string AssertTracesAgree(BspData folded, BspData unfolded, Random random, int rays)
    {
        BrushFoldResult fold = FoldOf(unfolded);
        List<Box> mergedBoxes = [];
        for (int b = 0; b < fold.Brushes.Length; b++)
        {
            if (fold.Groups[b].Length > 1)
            {
                mergedBoxes.Add(LinkedBrushProbe.BrushBox(folded, b));
            }
        }

        Assert.NotEmpty(mergedBoxes);
        WorldBrushTrace after = new(folded);
        WorldBrushTrace before = new(unfolded);
        DModel world = BspStructView.As<DModel>(folded[BspLump.Models])[0];
        Vec3[] hulls = [Vec3.Zero, new(16, 16, 36), new(4, 4, 4)];
        int hits = 0, startSolid = 0, seams = 0, allSolid = 0;
        for (int r = 0; r < rays; r++)
        {
            Vec3 start = Within(random, world.Mins, world.Maxs);
            Vec3 end = random.Next(2) == 0
                ? Within(random, world.Mins, world.Maxs)
                : start + new Vec3(random.NextSingle() * 128 - 64, random.NextSingle() * 128 - 64, random.NextSingle() * 128 - 64);
            Vec3 extents = hulls[r % hulls.Length];
            WorldHit a = after.Trace(start, end, extents);
            WorldHit b = before.Trace(start, end, extents);
            string why = $"ray {r} {start} -> {end} extents {extents}: folded {a}, unfolded {b}";
            Assert.True(a.StartSolid == b.StartSolid, why);
            if (b.StartSolid)
            {
                startSolid++;
                Assert.True(a.StartContents == b.StartContents, why);
                if (a.AllSolid != b.AllSolid)
                {
                    Assert.True(a.AllSolid && !b.AllSolid, why);
                    allSolid++;
                    Assert.True(
                        mergedBoxes.Any(m => Inside(start, m, extents) && Inside(end, m, extents)),
                        "all solid only folded, but the sweep does not start and end in one merged box: " + why);
                }

                continue;
            }

            if (a != b)
            {
                Assert.True(IsSeamStop(b, start, end, extents, mergedBoxes), "outside a removed seam: " + why);
                Assert.True(a.Fraction >= b.Fraction, why);
                seams++;
                continue;
            }

            hits += a.Fraction < 1f ? 1 : 0;
        }

        Assert.True(hits > rays / 20, $"only {hits} of {rays} rays hit anything");
        Assert.True(startSolid > 0, "no ray started solid");
        Assert.True(seams <= rays / 100, $"{seams} of {rays} rays stopped on a removed seam");
        return $"{rays} rays, {hits} hits, {startSolid} start solid ({allSolid} all solid only folded), {seams} seam stops";
    }

    /// <summary>
    /// Whether an unfolded sweep stopped on a removed seam: its stop plane
    /// (an axial plane, at coordinate c on its axis) lies strictly inside a
    /// merged box on that axis, so it is a face between two constituents,
    /// and the sweep's position at the stop is in that box grown by the
    /// sweep's extents and twice the trace epsilon.
    /// </summary>
    private static bool IsSeamStop(WorldHit hit, Vec3 start, Vec3 end, Vec3 extents, List<Box> merged)
    {
        if (hit.Plane is not { } plane || hit.Fraction >= 1f)
        {
            return false;
        }

        float[] n = [plane.Normal.X, plane.Normal.Y, plane.Normal.Z];
        int axis = Array.FindIndex(n, v => v != 0);
        if (n.Count(v => v != 0) != 1)
        {
            return false;
        }

        float c = n[axis] * plane.Dist;
        Vec3 at = start + ((end - start) * hit.Fraction);
        float[] p = [at.X, at.Y, at.Z];
        float[] e = [extents.X, extents.Y, extents.Z];
        float slack = 2 * WorldBrushTrace.DistEpsilon;
        foreach (Box box in merged)
        {
            float[] lo = [box.Mins.X, box.Mins.Y, box.Mins.Z];
            float[] hi = [box.Maxs.X, box.Maxs.Y, box.Maxs.Z];
            bool near = true;
            for (int k = 0; k < 3 && near; k++)
            {
                near = p[k] >= lo[k] - e[k] - slack && p[k] <= hi[k] + e[k] + slack;
            }

            if (near && c > lo[axis] && c < hi[axis])
            {
                return true;
            }
        }

        return false;
    }

    private static bool Inside(Vec3 p, Box box, Vec3 extents) =>
        p.X > box.Mins.X - extents.X && p.X < box.Maxs.X + extents.X
        && p.Y > box.Mins.Y - extents.Y && p.Y < box.Maxs.Y + extents.Y
        && p.Z > box.Mins.Z - extents.Z && p.Z < box.Maxs.Z + extents.Z;

    private static Vec3 Within(Random random, Vec3 min, Vec3 max) =>
        new(
            min.X + ((max.X - min.X) * random.NextSingle()),
            min.Y + ((max.Y - min.Y) * random.NextSingle()),
            min.Z + ((max.Z - min.Z) * random.NextSingle()));
}
