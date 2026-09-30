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

using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A jointed socket's plug brushes leave the linked brush lump, and every
/// reference to a kept brush is renumbered: the leaves' brush lists, the
/// collision ledges' client data and the brush sides' runs. Nothing a trace
/// or the physics reads may change.
/// </summary>
public sealed class LevelLinkerBrushStripTests(Rooms3x3Fixture fixture) : IClassFixture<Rooms3x3Fixture>
{
    private static readonly Vec3 PlayerHalfExtents = new(16, 16, 36);

    /// <summary>
    /// These facts are about which brushes are written and how they are
    /// numbered before any fold; the fold has its own (<see cref="LevelLinkerFoldTests"/>).
    /// </summary>
    private static readonly LevelLinkOptions NoFold = new() { FoldBrushes = false };

    /// <summary>
    /// No empty brush is left: the linked brush lump holds exactly the kept
    /// brushes of every placement, with exactly their sides.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task NoStrippedPlugBrushRemains(string name)
    {
        (LinkedLevel link, _) = await LinkAsync(name);
        DBrush[] brushes = BspStructView.As<DBrush>(link.Bsp[BspLump.Brushes]).ToArray();
        Assert.DoesNotContain(brushes, b => b.Contents == 0);

        int expectedBrushes = 0, expectedSides = 0, strippedTotal = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            RoomObject room = fixture.Library.Get(instance.Placement.Room);
            HashSet<int> stripped = LinkedBrushProbe.Stripped(room, instance);
            DBrush[] roomBrushes = BspStructView.As<DBrush>(room.Bsp[BspLump.Brushes]).ToArray();
            strippedTotal += stripped.Count;
            for (int b = 0; b < roomBrushes.Length; b++)
            {
                if (!stripped.Contains(b))
                {
                    expectedBrushes++;
                    expectedSides += roomBrushes[b].NumSides;
                }
            }
        }

        Assert.True(strippedTotal > 0, $"{name} strips no plug, so this fact pins nothing");
        Assert.Equal(expectedBrushes, brushes.Length);
        Assert.Equal(expectedSides, BspStructView.Count<DBrushSide>(link.Bsp[BspLump.BrushSides]));

        // The side runs tile the side lump in brush order.
        int next = 0;
        foreach (DBrush brush in brushes)
        {
            Assert.Equal(next, brush.FirstSide);
            next += brush.NumSides;
        }
    }

    /// <summary>
    /// Every leaf of every placement lists, in its room's order, the linked
    /// brushes with the geometry of the room brushes its room's leaf listed
    /// (planes moved by the placement, contents, bevels), less the stripped
    /// plugs; the leaves a plug carve made are the doorway (no brush) or
    /// copies of a solid leaf's list.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task EveryLeafBrushIsTheRoomBrushItListed(string name)
    {
        (LinkedLevel link, _) = await LinkAsync(name);
        BspData bsp = link.Bsp;
        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        ushort[] leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
        int leafBase = 1, compared = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            RoomObject room = fixture.Library.Get(instance.Placement.Room);
            HashSet<int> stripped = LinkedBrushProbe.Stripped(room, instance);
            HashSet<int> carved = Carved(room, instance);
            RoomTransform transform = new(instance.Placement, link.Plan.Layout.CellSize);
            (DPlane[] moved, bool[] swapped) = LevelLinker.TransformPlanes(BspStructView.As<DPlane>(room.Bsp[BspLump.Planes]).ToArray(), transform);
            DBrush[] roomBrushes = BspStructView.As<DBrush>(room.Bsp[BspLump.Brushes]).ToArray();
            DBrushSide[] roomSides = BspStructView.As<DBrushSide>(room.Bsp[BspLump.BrushSides]).ToArray();
            DLeaf[] roomLeafs = BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]).ToArray();
            ushort[] roomLeafBrushes = BspStructView.As<ushort>(room.Bsp[BspLump.LeafBrushes]).ToArray();
            for (int l = 0; l < roomLeafs.Length; l++)
            {
                DLeaf linked = leafs[leafBase + l];
                if (carved.Contains(l))
                {
                    Assert.Equal(0, linked.NumLeafBrushes);
                    continue;
                }

                List<string> expected = [];
                for (int i = 0; i < roomLeafs[l].NumLeafBrushes; i++)
                {
                    int b = roomLeafBrushes[roomLeafs[l].FirstLeafBrush + i];
                    if (!stripped.Contains(b))
                    {
                        expected.Add(LinkedBrushProbe.Geometry(roomBrushes[b], roomSides, p => moved[swapped[p >> 1] ? p ^ 1 : p]));
                    }
                }

                List<string> actual = [];
                for (int i = 0; i < linked.NumLeafBrushes; i++)
                {
                    actual.Add(LinkedBrushProbe.Geometry(bsp, leafBrushes[linked.FirstLeafBrush + i]));
                }

                Assert.Equal(expected, actual);
                compared += actual.Count;
            }

            leafBase += roomLeafs.Length;
        }

        Assert.True(compared > 0, "no leaf brush was compared");

        // The carve's solid fragments copy a solid leaf's list: every entry
        // names a kept (non-empty) brush.
        for (int l = leafBase; l < leafs.Length; l++)
        {
            for (int i = 0; i < leafs[l].NumLeafBrushes; i++)
            {
                Assert.NotEqual(0, BspStructView.As<DBrush>(bsp[BspLump.Brushes])[leafBrushes[leafs[l].FirstLeafBrush + i]].Contents);
            }
        }
    }

    /// <summary>
    /// Every collision ledge's client data names the linked brush it was
    /// cooked from: a brush of the ledge's contents class whose box is the
    /// ledge's, and there is one ledge per kept brush the rooms cooked.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task EveryLedgeNamesTheBrushItWasCookedFrom(string name)
    {
        (LinkedLevel link, _) = await LinkAsync(name);
        BspData bsp = link.Bsp;
        DBrush[] brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
        List<(int Contents, IvpCompactLedge Ledge)> ledges = LinkedBrushProbe.Ledges(bsp);

        int expected = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            RoomObject room = fixture.Library.Get(instance.Placement.Room);
            HashSet<int> stripped = LinkedBrushProbe.Stripped(room, instance);
            foreach ((int _, byte[] blob) in LevelLinker.ReadRoomCollide(room.Bsp, room.Definition.Name).Solids)
            {
                expected += IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)).Count(l => !stripped.Contains(l.ClientData));
            }
        }

        Assert.Equal(expected, ledges.Count);
        foreach ((int contents, IvpCompactLedge ledge) in ledges)
        {
            Assert.InRange(ledge.ClientData, 0, brushes.Length - 1);
            DBrush brush = brushes[ledge.ClientData];
            Assert.NotEqual(0, brush.Contents & contents);
            Box brushBox = LinkedBrushProbe.BrushBox(bsp, ledge.ClientData);
            Box ledgeBox = LinkedBrushProbe.LedgeBox(ledge);
            Assert.True(
                LinkedBrushProbe.Near(brushBox, ledgeBox, 0.5f),
                $"ledge box {ledgeBox.Mins}..{ledgeBox.Maxs} is not brush {ledge.ClientData}'s {brushBox.Mins}..{brushBox.Maxs}");
        }
    }

    /// <summary>
    /// Traces cannot tell the map from the one the link wrote while it kept
    /// the plugs as empty brushes (rebuilt from the rooms): the leaf brush
    /// trace the ambient sampler uses gives the same hit in every leaf, and
    /// point and player-hull sweeps across the whole map stop at the same
    /// fraction on the same contents and surface.
    /// </summary>
    [Theory]
    [InlineData("rooms3x3", 1)]
    [InlineData("rooms3x3_turn1", 2)]
    [InlineData("seed_9", 3)]
    public async Task TracesAreTheSameAsWithThePlugsKept(string name, int seed)
    {
        (LinkedLevel link, _) = await LinkAsync(name);
        BspData old = LinkedBrushProbe.WithPlugsKept(link, fixture.Library);
        Assert.True(
            BspStructView.Count<DBrush>(old[BspLump.Brushes]) > BspStructView.Count<DBrush>(link.Bsp[BspLump.Brushes]),
            "the rebuilt map has no plug brush back");

        AmbientScene now = AmbientScene.Create(link.Bsp, LightingMode.Ldr);
        AmbientScene before = AmbientScene.Create(old, LightingMode.Ldr);
        Random random = new(seed);
        int leafHits = 0;
        for (int l = 0; l < now.Leaves.Length; l++)
        {
            DLeaf leaf = now.Leaves[l];
            for (int r = 0; r < 8; r++)
            {
                Vec3 start = Inside(random, leaf, 32);
                Vec3 end = Inside(random, leaf, 32);
                LeafBrushHit a = LeafBrushTrace.Trace(l, start, end, now);
                LeafBrushHit b = LeafBrushTrace.Trace(l, start, end, before);
                Assert.Equal(b, a);
                leafHits += a.Fraction < 1f ? 1 : 0;
            }
        }

        Assert.True(leafHits > 0, "no leaf brush trace hit anything");

        WorldBrushTrace worldNow = new(link.Bsp);
        WorldBrushTrace worldBefore = new(old);
        DModel world = BspStructView.As<DModel>(link.Bsp[BspLump.Models])[0];
        int hits = 0;
        for (int r = 0; r < 2000; r++)
        {
            Vec3 start = Within(random, world.Mins, world.Maxs);
            Vec3 end = Within(random, world.Mins, world.Maxs);
            Vec3 extents = r % 2 == 0 ? Vec3.Zero : PlayerHalfExtents;
            WorldHit a = worldNow.Trace(start, end, extents);
            WorldHit b = worldBefore.Trace(start, end, extents);
            Assert.Equal(b, a);
            hits += a.Fraction < 1f ? 1 : 0;
        }

        Assert.True(hits > 0, "no world trace hit anything");
    }

    /// <summary>The linked maps pass the loader checks <c>ssmap check</c> makes.</summary>
    [Theory]
    [InlineData("rooms3x3")]
    [InlineData("rooms3x3_turn1")]
    [InlineData("seed_9")]
    public async Task TheLinkedMapPassesTheLoaderChecks(string name)
    {
        (LinkedLevel link, _) = await LinkAsync(name);
        ValidationReport report = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// A grid of hubs whose rooms, as compiled, hold more brushes than the
    /// loader's <c>MAX_MAP_BRUSHES</c> links, because its jointed plugs are
    /// not written, and the map it writes loads (passes the loader checks).
    /// </summary>
    [Fact]
    public async Task ALevelOverTheOldBrushCapLinks()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject hub = library.Get("hub");
        int perRoom = BspStructView.Count<DBrush>(hub.Bsp[BspLump.Brushes]);
        int cap = BspLimits.Caps.First(c => c.Lump == BspLump.Brushes).Max;
        int size = 2;
        while (size * size * perRoom <= cap)
        {
            size++;
        }

        LevelLayout layout = LevelLinkerScaleTests.HubGrid(library, size);
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, await RoomHarness.ContextAsync());
        int brushes = BspStructView.Count<DBrush>(link.Bsp[BspLump.Brushes]);
        Assert.True(size * size * perRoom > cap, "the grid's rooms do not pass the cap as compiled");
        Assert.InRange(brushes, 1, cap);
        ValidationReport report = await BspValidator.CheckAsync(link.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The capacity check counts what the link writes: a line of 400 hubs
    /// (8,800 brushes as compiled, 8,002 kept) passes it and a line of 410
    /// (8,202 kept) is refused at its last hub (cell 25 of the line's seventh
    /// row) with the kept total, the same
    /// with the census stored in the room and made on the fly. A joint that
    /// names a socket the room lacks strips nothing here (the joint check
    /// refuses it next).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCapacityCheckCountsTheKeptBrushes(bool stored)
    {
        RoomLibrary compiled = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject hub = compiled.Get("hub");
        hub = hub with { Link = await LevelLinker.TryPrecomputeAsync(hub, CancellationToken.None) };
        Assert.NotNull(hub.Link);
        RoomLibrary library = RoomHarness.Library(stored ? hub : hub with { Link = null });
        Assert.Equal(22, BspStructView.Count<DBrush>(hub.Bsp[BspLump.Brushes]));

        LevelLinker.CheckCapacity(Line(library, 400), library, NoFold);
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(Line(library, 410), library, NoFold));
        Assert.StartsWith("room hub at cell (25, 6) pushes the link to 8202 brushes;", refused.Message, StringComparison.Ordinal);

        // The first hub's joint renamed to a socket it lacks: that hub
        // strips nothing, one brush more than the line above.
        LevelLayout line = Line(library, 409);
        LevelLinker.CheckCapacity(line, library, NoFold); // 8,182
        RoomInstance first = line.Rooms[0];
        LevelLayout bogus = line with { Rooms = [first with { Joints = [("nowhere", first.Joints[0].NeighborSocket)] }, .. line.Rooms.Skip(1)] };
        LevelLinker.CheckCapacity(bogus, library, NoFold); // 8,183: still under
        LevelLayout bogusLonger = Line(library, 410) with { Rooms = [first with { Joints = [("nowhere", first.Joints[0].NeighborSocket)] }, .. Line(library, 410).Rooms.Skip(1)] };
        LinkException refusedBogus = Assert.Throws<LinkException>(() => LevelLinker.CheckCapacity(bogusLonger, library, NoFold));
        Assert.StartsWith("room hub at cell (25, 6) pushes the link to 8203 brushes;", refusedBogus.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The kept-brush numbering: stripped brushes map to -1, the rest are
    /// numbered in order without gaps, and the side count is the kept
    /// brushes' own.
    /// </summary>
    [Fact]
    public void KeptBrushesNumbersTheRestInOrder()
    {
        DBrush[] brushes =
        [
            new() { NumSides = 6 },
            new() { NumSides = 5 },
            new() { NumSides = 7 },
            new() { NumSides = 6 },
        ];
        (int[] map, int kept, int sides) = LevelLinker.KeptBrushes(brushes, new HashSet<int> { 1, 3 });
        Assert.Equal([0, -1, 1, -1], map);
        Assert.Equal(2, kept);
        Assert.Equal(13, sides);

        (map, kept, sides) = LevelLinker.KeptBrushes(brushes, new HashSet<int>());
        Assert.Equal([0, 1, 2, 3], map);
        Assert.Equal(4, kept);
        Assert.Equal(24, sides);
    }

    /// <summary>
    /// A reference to a stripped plug or to a brush the room does not have
    /// is refused by name rather than written as some other brush.
    /// </summary>
    [Fact]
    public void AReferenceToAStrippedOrMissingBrushIsRefused()
    {
        int[] map = [0, -1, 1];
        Assert.Equal(100, LevelLinker.LinkedBrush(map, 100, 0, "r"));
        Assert.Equal(101, LevelLinker.LinkedBrush(map, 100, 2, "r"));

        LinkException stripped = Assert.Throws<LinkException>(() => LevelLinker.LinkedBrush(map, 100, 1, "r"));
        Assert.Equal("room r names brush 1, which is a stripped plug", stripped.Message);
        LinkException past = Assert.Throws<LinkException>(() => LevelLinker.LinkedBrush(map, 100, 3, "r"));
        Assert.Equal("room r names brush 3, which it does not have (3 brushes)", past.Message);
        LinkException negative = Assert.Throws<LinkException>(() => LevelLinker.LinkedBrush(map, 100, -1, "r"));
        Assert.Equal("room r names brush -1, which it does not have (3 brushes)", negative.Message);
    }

    private async Task<(LinkedLevel Link, LevelLayout Layout)> LinkAsync(string name)
    {
        Rooms3x3Case found = Rooms3x3Fixture.Cases.Single(c => c.Name == name);
        LevelGrid level = LevelYaml.Parse(found.Arrangement.LevelYaml(name, Rooms3x3Sample.LibraryFromLevels), name);
        LevelLayout layout = level.ToLayout(n => fixture.Library.Find(n)?.Definition, fixture.Library.CellSize, fixture.Library.Kit);
        return (await LevelLinker.LinkAsync(layout, fixture.Library, fixture.Context(name), NoFold), layout);
    }

    /// <summary>
    /// A line of hubs, each joined to the next, folded into rows of
    /// <see cref="LineRow"/>: the joints are the line's, which is all the
    /// capacity count reads of a placement besides its room, and the rows
    /// keep every placement within the engine's coordinates, which the
    /// capacity check refuses to pass (the rooms design, 17.6).
    /// </summary>
    private static LevelLayout Line(RoomLibrary library, int length)
    {
        LevelCell?[] cells = new LevelCell?[length];
        Array.Fill(cells, new LevelCell("hub", 0));
        LevelLayout line = new LevelGrid("line", "rooms.vmf", 1, length, cells)
            .ToLayout(name => library.Find(name)?.Definition, library.CellSize, library.Kit);
        return line with
        {
            Rooms = [.. line.Rooms.Select((r, i) => r with { Placement = r.Placement with { CellX = i % LineRow, CellY = i / LineRow } })],
        };
    }

    /// <summary>How many hubs a line's row holds: 64 cells of 256 reach 16,384, the engine's limit.</summary>
    private const int LineRow = 64;

    private static HashSet<int> Carved(RoomObject room, RoomInstance instance)
    {
        IReadOnlyList<SocketCensus> sockets = LevelLinker.ComputeShared(room).Sockets;
        HashSet<int> carved = [];
        foreach ((string socket, _) in instance.Joints)
        {
            int index = room.Definition.Sockets.ToList().FindIndex(s => s.Name == socket);
            carved.UnionWith(sockets[index].CarveLeaves);
        }

        return carved;
    }

    private static Vec3 Inside(Random random, DLeaf leaf, float margin) =>
        Within(
            random,
            new Vec3(leaf.Mins[0] - margin, leaf.Mins[1] - margin, leaf.Mins[2] - margin),
            new Vec3(leaf.Maxs[0] + margin, leaf.Maxs[1] + margin, leaf.Maxs[2] + margin));

    private static Vec3 Within(Random random, Vec3 min, Vec3 max) =>
        new(
            min.X + ((max.X - min.X) * random.NextSingle()),
            min.Y + ((max.Y - min.Y) * random.NextSingle()),
            min.Z + ((max.Z - min.Z) * random.NextSingle()));
}
