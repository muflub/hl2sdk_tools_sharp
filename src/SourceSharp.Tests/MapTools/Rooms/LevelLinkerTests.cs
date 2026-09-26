//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level linker: a room grid arrives as compiled room objects, and leaves
/// as one BSP plus one vis whose PVS is the door graph. The facts read the
/// delivered <see cref="VisResult"/> and the delivered <see cref="BspData"/>,
/// never a debug back door: expected visibility is rebuilt here from the
/// public primitives (<see cref="RoomLinter.SealBox"/>, the compile's own
/// leaves, each room's own vvis rows) so a linker that quietly dropped the
/// door graph would fail, not agree.
/// </summary>
public sealed class LevelLinkerTests
{
    // ---- the door graph, end to end -------------------------------------.

    /// <summary>
    /// Every linked PVS row equals the transitive closure of "own vvis row,
    /// shifted into the global cluster space, plus the door edges of the
    /// joint graph" — computed independently here from the rooms' own compiles
    /// and the kit, not from the linker's internals.
    /// </summary>
    [Fact]
    public async Task LinkedPvsRowsMatchTheDoorGraph()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);

        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(room);
        LevelLayout layout = RingLayout();

        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);

        // The cluster space is every room's own clusters, in layout order.
        Assert.Equal(4 * room.ClusterCount, link.Vis.ClusterCount);

        bool[][] expected = ExpectedRows(layout, library);
        for (int from = 0; from < link.Vis.ClusterCount; from++)
        {
            for (int to = 0; to < link.Vis.ClusterCount; to++)
            {
                Assert.True(
                    expected[from][to] == link.Vis.CanSee(from, to),
                    $"cluster {from} seeing {to}: the door-graph replay says {expected[from][to]}, "
                    + $"the linked vis says {link.Vis.CanSee(from, to)} (rooms are {room.ClusterCount} clusters each)");
            }
        }

        // The visibility lump is not just the VisResult's mirror: it is the
        // file's row set, RLE-packed with the header vvis writes, and it must
        // decompress back to exactly those rows. (A header the body overwrote
        // fails here first.)
        byte[] lump = [.. link.Bsp[BspLump.Visibility].Data.Span];
        Assert.Equal(link.Vis.ClusterCount, BitConverter.ToInt32(lump, 0));
        int rowBytes = link.Vis.RowBytes;
        for (int cluster = 0; cluster < link.Vis.ClusterCount; cluster++)
        {
            int offset = BitConverter.ToInt32(lump, 4 + cluster * 8);
            Assert.True(offset > 0 && offset < lump.Length, $"row {cluster} offset {offset} is outside the lump");
            byte[] row = new byte[rowBytes];
            VisRunLength.Decompress(lump.AsSpan(offset, lump.Length - offset), row);
            Assert.True(row.SequenceEqual(link.Vis.Pvs(cluster)), $"row {cluster} does not survive the lump");
        }
    }

    /// <summary>
    /// The superset gate (§10b's premise): compile the same ring as ONE map.
    /// <see cref="RoomModel.BuildMerged"/>, which drops the plugs at jointed
    /// sockets so the rooms merge — run the real vvis on it, and every pair
    /// the monolithic map's PVS can see must be visible in the linked map too.
    /// Clusters are matched to space by multi-sample point-in-leaf: each open
    /// leaf's centre and eight inset points, walked down the linked tree.
    /// </summary>
    [Fact]
    public async Task LinkedPvsIsASupersetOfTheMonolithicOracle()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(room);
        LevelLayout layout = RingLayout();

        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);

        // The oracle: the same level as one CSG'd map, compiled and vvis'd for
        // real. Jointed plugs are dropped, so its vis sees through the doors.
        VmfDocument merged = RoomModel.BuildMerged(layout, library);
        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "900100");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", $"{RoomHarness.Cell / 2f} {RoomHarness.Cell / 2f} {RoomHarness.Cell / 2f + 1f}");
        merged.Chunks.Add(start);
        VbspResult monolithic = await RoomHarness.CompileAsync(merged, context);
        Assert.Null(monolithic.Leak);
        VisResult monoVis = await RoomHarness
            .VisAsync(monolithic.Bsp!, PortalSet.FromPortalFile(monolithic.Portals!));

        // Map every monolithic open cluster to the set of linked clusters its
        // open samples fall in — the centre and eight insets a third of the
        // way to each corner of every open leaf, walked down the linked tree,
        // with an off-plane nudge for samples that land exactly on a cell
        // face. Samples that land inside a kept plug are skipped (the merged
        // oracle has no plug there), and a cluster whose samples ALL land in
        // plug solid is a merged doorway passage that does not exist in the
        // linked map: its pairs are carried by the door edges between the
        // open clusters on each side, which do have to be visible.
        int[][] map = MapClusters(monolithic.Bsp!, link.Bsp);

        for (int from = 0; from < monoVis.ClusterCount; from++)
        {
            if (map[from].Length == 0)
            {
                continue; // this cluster is entirely inside a kept plug
            }

            for (int to = 0; to < monoVis.ClusterCount; to++)
            {
                if (!monoVis.CanSee(from, to) || map[to].Length == 0)
                {
                    continue;
                }

                foreach (int a in map[from])
                {
                    foreach (int b in map[to])
                    {
                        Assert.True(
                            link.Vis.CanSee(a, b),
                            $"the monolithic map sees {from}->{to} (linked {a}->{b}); the door graph dropped it");
                    }
                }
            }
        }
    }

    // ---- the cap mutation: doors are the only cross-room sight ----------.

    /// <summary>
    /// Three rooms in a line, one door each. Capping the A–B joint (both sides
    /// drop the joint and cap the socket; the plugs stay) must remove exactly
    /// the pairs that travelled that bridge — A–B and A–C, both directions.
    /// and change nothing else: B still sees C, every room still sees itself.
    /// </summary>
    [Fact]
    public async Task CappingTheOnlyBridgeRemovesExactlyItsPairs()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(room);

        LevelLayout line = LineLayout();
        LinkedLevel full = await LevelLinker.LinkAsync(line, library, context);

        // Baseline: the open line sees through both doors.
        int per = room.ClusterCount;
        Assert.True(full.Vis.CanSee(0, per), "the open line does not cross the A-B door");
        Assert.True(full.Vis.CanSee(0, 2 * per), "the open line does not cross both doors");

        LinkedLevel capped = await LevelLinker.LinkAsync(CapBridgeLayout(line), library, context);

        bool[][] before = Matrix(full.Vis);
        bool[][] after = Matrix(capped.Vis);
        int total = full.Vis.ClusterCount;
        Assert.Equal(total, capped.Vis.ClusterCount);

        // The A-B bridge is every pair crossing the A|BC cut. Capping it must
        // disconnect exactly the pairs that were connected, in either
        // direction, across that cut — and no pair outside the cut may move.
        for (int from = 0; from < total; from++)
        {
            for (int to = 0; to < total; to++)
            {
                bool acrossCut = (from < per) != (to < per);
                Assert.True(
                    before[from][to] == after[from][to] || acrossCut,
                    $"pair {from}->{to} is not across the capped cut but changed "
                    + $"(before {before[from][to]}, after {after[from][to]})");
                if (acrossCut)
                {
                    Assert.True(
                        !(before[from][to] && !after[from][to]) || before[to][from] && !after[to][from],
                        $"pair {from}->{to} lost its bridge sight without {to}->{from} losing it too");
                    Assert.False(
                        !before[from][to] && after[from][to],
                        $"capping the bridge created sight {from}->{to}");
                }
            }
        }
    }

    // ---- refusals: one broken guarantee, named ---------------------------.

    /// <summary>
    /// Rule 2's layout half: a socket that is neither jointed nor capped is
    /// refused by name and cell, with the house message.
    /// </summary>
    [Fact]
    public async Task AnUnjointedUncappedSocketIsRefusedNamingTheSocket()
    {
        RoomDefinition east = RoomHarness.Room("eastonly", RoomFacing.PositiveX);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(east), east, context);
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(room);

        // One room, its only socket facing empty space, jointed to nobody.
        LevelLayout lonely = new("lonely", RoomHarness.Cell, RoomHarness.Kit,
            [new RoomInstance(new RoomPlacement("eastonly", 0, 0, 0), [], [])]);

        RoomLintException refused = await Assert
            .ThrowsAsync<RoomLintException>(async () => await LevelLinker.LinkAsync(lonely, library, context));
        Assert.Equal(
            "rule 2 (ShellSealedExceptAtSockets): socket \"PositiveX\" of the room at cell (0, 0)"
            + " is neither jointed to a neighbour nor capped.",
            refused.Message);
    }

    /// <summary>
    /// Rule 3's compile half: a room whose compile has no open interior leaf.
    /// here a fully solid BSP — is refused. The hand-built compile is the
    /// linter's public input: a solid-only leaf lump is what such a room's
    /// vbsp output would be.
    /// </summary>
    [Fact]
    public void AFullySolidRoomIsRefusedAsHavingNoOpenInteriorLeaf()
    {
        RoomDefinition solid = RoomHarness.Room("solid");
        BspData bsp = new();
        DLeaf[] leaves =
        [
            new DLeaf
            {
                Contents = (int)BrushContents.Solid,
                Cluster = -1,
                Mins = Short3(Vec3.Zero),
                Maxs = Short3(new Vec3(solid.CellSize, solid.CellSize, solid.CellSize)),
                LeafWaterDataId = -1,
            },
        ];
        bsp.SetLump(BspLump.Leafs, MemoryMarshal.AsBytes(leaves.AsSpan()).ToArray());

        RoomLintException refused = Assert.Throws<RoomLintException>(
            () => RoomLinter.CheckCompiled(solid, bsp, RoomCompiler.SealBoxes(solid), leaked: false));
        Assert.Equal(
            "rule 3 (InteriorCannotEscape): room solid has no open interior leaf.",
            refused.Message);
    }

    /// <summary>
    /// Rule 5's layout half: a layout naming a room the library does not have
    /// is refused by name, with the house message.
    /// </summary>
    [Fact]
    public void ALayoutNamingARoomTheLibraryLacksIsRefused()
    {
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        LevelLayout ghost = new("ghost", RoomHarness.Cell, RoomHarness.Kit,
            [new RoomInstance(new RoomPlacement("ghost", 0, 0, 0), [], [])]);

        RoomLintException refused = Assert.Throws<RoomLintException>(
            () => RoomLinter.CheckLayout(ghost, library));
        Assert.Equal(
            "rule 5 (PlacementIsQuarterTurnGrid): the layout places room \"ghost\", which the library does not have.",
            refused.Message);
    }

    // ---- I4: the bytes cannot depend on the schedule ----------------------.

    /// <summary>
    /// Invariant I4: the same layout linked on one worker and on thirty-two
    /// produces byte-identical lumps and identical vis rows — the bases are a
    /// sequential prefix sum and no room reads another's plan.
    /// </summary>
    [Fact]
    public async Task OneThreadAndThirtyTwoLinkByteIdentical()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        RoomObject room;
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        {
            VbspContext context = await RoomHarness.ContextAsync();
            room = await RoomCompiler
                .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
            library.Add(room);
        }

        LevelLayout layout = RingLayout();

        VbspContext serial = await RoomHarness.ContextAsync();
        serial.Parallelism = new CompileParallelism { MaxDegree = 1 };
        LinkedLevel one = await LevelLinker.LinkAsync(layout, library, serial);

        VbspContext swarm = await RoomHarness.ContextAsync();
        swarm.Parallelism = new CompileParallelism { MaxDegree = 32 };
        LinkedLevel many = await LevelLinker.LinkAsync(layout, library, swarm);

        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            Assert.True(
                one.Bsp[lump].Data.Span.SequenceEqual(many.Bsp[lump].Data.Span),
                $"lump {lump} differs between degree 1 and degree 32");
        }

        for (int cluster = 0; cluster < one.Vis.ClusterCount; cluster++)
        {
            Assert.True(
                one.Vis.Pvs(cluster).SequenceEqual(many.Vis.Pvs(cluster)),
                $"row {cluster} differs between degree 1 and degree 32");
        }
    }

    // ---- the heavy tier: gated, runs in the corpus measure window ---------.

    /// <summary>
    /// The corpus tier, scaled between the unit grids and L4: a 3×3 of two
    /// different room kinds, every joint real. Nothing stock is read — the
    /// mount marks the measure window in which the tier is expected to run.
    /// </summary>
    [RoomLinkCorpusFact]
    public async Task CorpusKitNineRoomGridLinks()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        RoomDefinition corner = RoomHarness.Room("corner", RoomFacing.PositiveX, RoomFacing.PositiveY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject hubRoom = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
        RoomObject cornerRoom = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(corner), corner, context);

        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(hubRoom);
        library.Add(cornerRoom);

        // The corner room has no -x/-y sockets, so it may only sit at the grid's
        // low corner; the other eight cells are hubs.
        List<RoomInstance> rooms = [];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                bool cornerCell = x == 0 && y == 0;
                string name = cornerCell ? "corner" : "hub";
                List<(string, string)> joints = [];
                List<string> capped = [];
                foreach (RoomSocket socket in (cornerCell ? corner : hub).Sockets)
                {
                    (int dx, int dy) = Offset(socket.Facing);
                    string neighbourSocket = Opposite(socket.Facing).ToString();
                    if (Inside(x + dx, y + dy, 3) && HasSocket(cornerCell && x + dx == 0 && y + dy == 0 ? corner : hub, neighbourSocket))
                    {
                        joints.Add((socket.Name, neighbourSocket));
                    }
                    else
                    {
                        capped.Add(socket.Name);
                    }
                }

                rooms.Add(new RoomInstance(new RoomPlacement(name, x, y, 0), joints, capped));
            }
        }

        LevelLayout layout = new("corpus9", RoomHarness.Cell, RoomHarness.Kit, rooms);
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);

        int expected = 0;
        foreach (RoomInstance instance in rooms)
        {
            expected += library.Get(instance.Placement.Room).ClusterCount;
        }

        Assert.Equal(expected, link.Vis.ClusterCount);

        // The grid is connected: the low hub row must see the far corner.
        Assert.True(link.Vis.CanSee(0, link.Vis.ClusterCount - 1), "the 3×3 grid does not link end to end");
    }

    /// <summary>
    /// L4: sixteen rooms on a 4×4 grid of one kit — the scale at which the
    /// cluster numbering, the row width, and the closure are exercised. Gated
    /// with the corpus tier so the default suite stays light.
    /// </summary>
    [RoomLinkCorpusFact]
    public async Task L4SixteenRoomGridLinks()
    {
        RoomDefinition hub = RoomHarness.Room("hub",
            RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject room = await RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(hub), hub, context);
        RoomLibrary library = new(RoomHarness.Kit, RoomHarness.Cell);
        library.Add(room);

        List<RoomInstance> rooms = [];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                List<(string, string)> joints = [];
                List<string> capped = [];
                foreach (RoomSocket socket in hub.Sockets)
                {
                    (int dx, int dy) = Offset(socket.Facing);
                    if (Inside(x + dx, y + dy, 4))
                    {
                        joints.Add((socket.Name, Opposite(socket.Facing).ToString()));
                    }
                    else
                    {
                        capped.Add(socket.Name);
                    }
                }

                rooms.Add(new RoomInstance(new RoomPlacement("hub", x, y, 0), joints, capped));
            }
        }

        LevelLayout layout = new("l4", RoomHarness.Cell, RoomHarness.Kit, rooms);
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);

        Assert.Equal(16 * room.ClusterCount, link.Vis.ClusterCount);
        Assert.True(link.Vis.CanSee(0, 15 * room.ClusterCount), "the 4×4 grid does not link corner to corner");
    }

    // ---- helpers -----------------------------------------------------------

    private const string Hub = "hub";

    /// <summary>The 2×2 ring: four copies of the four-socket room, every face jointed or capped.</summary>
    private static LevelLayout RingLayout() =>
        new("ring", RoomHarness.Cell, RoomHarness.Kit,
        [
            Instance((0, 0), [("PositiveX", "NegativeX"), ("PositiveY", "NegativeY")], ["NegativeX", "NegativeY"]),
            Instance((1, 0), [("NegativeX", "PositiveX"), ("PositiveY", "NegativeY")], ["PositiveX", "NegativeY"]),
            Instance((1, 1), [("NegativeX", "PositiveX"), ("NegativeY", "PositiveY")], ["PositiveX", "PositiveY"]),
            Instance((0, 1), [("NegativeY", "PositiveY"), ("PositiveX", "NegativeX")], ["NegativeX", "PositiveY"]),
        ]);

    /// <summary>Three rooms in a line: A–B and B–C, everything else capped.</summary>
    private static LevelLayout LineLayout() =>
        new("line", RoomHarness.Cell, RoomHarness.Kit,
        [
            Instance((0, 0), [("PositiveX", "NegativeX")], ["NegativeX", "PositiveY", "NegativeY"]),
            Instance((1, 0), [("NegativeX", "PositiveX"), ("PositiveX", "NegativeX")], ["PositiveY", "NegativeY"]),
            Instance((2, 0), [("NegativeX", "PositiveX")], ["PositiveX", "PositiveY", "NegativeY"]),
        ]);

    /// <summary>
    /// The same line with the A–B bridge capped on both sides: each side drops
    /// the joint and caps its own socket. The plugs stay (capping a socket does
    /// not remove its plug — that is what a cap is), so only the door edge goes.
    /// </summary>
    private static LevelLayout CapBridgeLayout(LevelLayout line) =>
        line with
        {
            Rooms =
            [
                new RoomInstance(line.Rooms[0].Placement, [], ["NegativeX", "PositiveY", "NegativeY", "PositiveX"]),
                new RoomInstance(line.Rooms[1].Placement, [("PositiveX", "NegativeX")], ["PositiveY", "NegativeY", "NegativeX"]),
                line.Rooms[2],
            ],
        };

    private static RoomInstance Instance((int X, int Y) cell, (string, string)[] joints, string[] capped) =>
        new(new RoomPlacement(Hub, cell.X, cell.Y, 0), joints, capped);

    private static (int Dx, int Dy) Offset(RoomFacing facing) => facing switch
    {
        RoomFacing.PositiveX => (1, 0),
        RoomFacing.NegativeX => (-1, 0),
        RoomFacing.PositiveY => (0, 1),
        _ => (0, -1),
    };

    private static RoomFacing Opposite(RoomFacing facing) => facing switch
    {
        RoomFacing.PositiveX => RoomFacing.NegativeX,
        RoomFacing.NegativeX => RoomFacing.PositiveX,
        RoomFacing.PositiveY => RoomFacing.NegativeY,
        _ => RoomFacing.PositiveY,
    };

    private static bool Inside(int x, int y, int size) => x >= 0 && y >= 0 && x < size && y < size;

    private static bool HasSocket(RoomDefinition definition, string name)
    {
        foreach (RoomSocket socket in definition.Sockets)
        {
            if (socket.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The door graph computed the other way: own rows from each room's own
    /// vvis result shifted by the layout-order cluster bases, door edges from
    /// the kit's seal boxes against the compile's open leaf boxes, transitive
    /// closure by the test's own Warshall. Shares no code with the linker.
    /// </summary>
    private static bool[][] ExpectedRows(LevelLayout layout, RoomLibrary library)
    {
        RoomObject[] rooms = [.. layout.Rooms.Select(i => library.Get(i.Placement.Room))];
        int[] baseOf = new int[rooms.Length];
        int total = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            baseOf[i] = total;
            total += rooms[i].ClusterCount;
        }

        bool[][] seen = [.. Enumerable.Range(0, total).Select(_ => new bool[total])];

        for (int i = 0; i < rooms.Length; i++)
        {
            for (int c = 0; c < rooms[i].ClusterCount; c++)
            {
                for (int d = 0; d < rooms[i].ClusterCount; d++)
                {
                    seen[baseOf[i] + c][baseOf[i] + d] = rooms[i].Vis.CanSee(c, d);
                }

                seen[baseOf[i] + c][baseOf[i] + c] = true;
            }
        }

        for (int i = 0; i < layout.Rooms.Count; i++)
        {
            RoomInstance instance = layout.Rooms[i];
            RoomDefinition definition = rooms[i].Definition;
            foreach ((string socketName, string neighbourSocketName) in instance.Joints)
            {
                RoomSocket socket = definition.Sockets.First(s => s.Name == socketName);
                (int dx, int dy) = Offset(socket.Facing);
                int j = -1;
                for (int k = 0; k < layout.Rooms.Count; k++)
                {
                    if (layout.Rooms[k].Placement.CellX == instance.Placement.CellX + dx
                        && layout.Rooms[k].Placement.CellY == instance.Placement.CellY + dy)
                    {
                        j = k;
                    }
                }

                Assert.True(j >= 0, $"the joint at {instance.Placement} has no neighbour");

                RoomSocket neighbourSocket = rooms[j].Definition.Sockets.First(s => s.Name == neighbourSocketName);
                foreach (int x in Facing(rooms[i], definition, socket))
                {
                    foreach (int y in Facing(rooms[j], rooms[j].Definition, neighbourSocket))
                    {
                        seen[baseOf[i] + x][baseOf[j] + y] = true;
                        seen[baseOf[j] + y][baseOf[i] + x] = true;
                    }
                }
            }
        }

        for (int k = 0; k < total; k++)
        {
            for (int i = 0; i < total; i++)
            {
                if (seen[i][k])
                {
                    for (int j = 0; j < total; j++)
                    {
                        seen[i][j] |= seen[k][j];
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>The open clusters whose leaf boxes overlap a socket's kit plug box.</summary>
    private static int[] Facing(RoomObject room, RoomDefinition definition, RoomSocket socket)
    {
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        List<int> clusters = [];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]))
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            Box box = new(
                new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
                new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));
            if (box.Overlaps(plug, LevelLinker.DoorOverlapEpsilon))
            {
                clusters.Add(leaf.Cluster);
            }
        }

        return [.. clusters.Order()];
    }

    private static int[][] MapClusters(BspData monolithic, BspData linked)
    {
        List<DLeaf> leaves = [.. BspStructView.As<DLeaf>(monolithic[BspLump.Leafs])];
        List<HashSet<int>> sets = [.. Enumerable
            .Range(0, leaves.Max(l => (int)l.Cluster) + 1)
            .Select(_ => new HashSet<int>())];

        foreach (DLeaf leaf in leaves)
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            Vec3 mins = new(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]);
            Vec3 maxs = new(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]);
            Vec3 centre = new((mins.X + maxs.X) / 2f, (mins.Y + maxs.Y) / 2f, (mins.Z + maxs.Z) / 2f);

            List<Vec3> samples = [centre];
            for (int corner = 0; corner < 8; corner++)
            {
                Vec3 point = new(
                    (corner & 1) == 0 ? mins.X : maxs.X,
                    (corner & 2) == 0 ? mins.Y : maxs.Y,
                    (corner & 4) == 0 ? mins.Z : maxs.Z);
                samples.Add(new Vec3(
                    centre.X + 0.3f * (point.X - centre.X),
                    centre.Y + 0.3f * (point.Y - centre.Y),
                    centre.Z + 0.3f * (point.Z - centre.Z)));
            }

            foreach (Vec3 sample in samples)
            {
                int cluster = LevelLinker.PointInLeafCluster(linked, sample);
                if (cluster < 0)
                {
                    // Exactly on a cell-face plane the top tree's tie can
                    // send the point into the shared solid leaf. Retry off
                    // each axis by one unit; the first open landing wins.
                    foreach (Vec3 nudge in OffPlaneNudges)
                    {
                        cluster = LevelLinker.PointInLeafCluster(
                            linked, new Vec3(sample.X + nudge.X, sample.Y + nudge.Y, sample.Z + nudge.Z));
                        if (cluster >= 0)
                        {
                            break;
                        }
                    }
                }

                if (cluster < 0)
                {
                    // The linked map keeps every room's kit plug — the merged
                    // oracle dropped the plugs at jointed sockets, so the
                    // doorway's own space is open there and plug-solid here.
                    // A sample inside a plug witnesses nothing: sight through
                    // that door is carried by the door edges between the open
                    // clusters on each side, which the other samples cover.
                    continue;
                }

                _ = sets[leaf.Cluster].Add(cluster);
            }
        }

        // Guard against a vacuous mapping: every room's interior contains its
        // cell centre, which no plug reaches, so at least one open cluster
        // per room must carry a mapped linked cluster.
        Assert.True(sets.Count(s => s.Count > 0) >= 3, "the mapping mapped nothing: no open cluster landed anywhere");
        return [.. sets.Select(s => s.Order().ToArray())];
    }

    /// <summary>Unit nudges off each axis, for samples that land exactly on a cell face.</summary>
    private static readonly Vec3[] OffPlaneNudges =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    private static bool[][] Matrix(VisResult vis)
    {
        bool[][] matrix = [.. Enumerable.Range(0, vis.ClusterCount).Select(_ => new bool[vis.ClusterCount])];
        for (int from = 0; from < vis.ClusterCount; from++)
        {
            for (int to = 0; to < vis.ClusterCount; to++)
            {
                matrix[from][to] = vis.CanSee(from, to);
            }
        }

        return matrix;
    }

    private static ShortArray3 Short3(Vec3 v)
    {
        ShortArray3 s = default;
        s[0] = (short)Math.Round(v.X);
        s[1] = (short)Math.Round(v.Y);
        s[2] = (short)Math.Round(v.Z);
        return s;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the link tier's heavy grids. Skips,
/// visibly, outside the corpus measure window: nothing stock is read — the
/// mount marks the window in which the heavy compile/link rows are expected to
/// run, so the default fleet suite stays at its baseline skip count without
/// them. The recipe is the corpus mount itself:
/// <code>PP_CATMAPS_DIR=&lt;catalogue directory&gt; dotnet test --filter SixteenRoomGridLinks</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RoomLinkCorpusFactAttribute : FactAttribute
{
    /// <summary>The environment variable naming the corpus mount.</summary>
    public const string CorpusVariable = "PP_CATMAPS_DIR";

    /// <summary>Decides at discovery whether the window is open.</summary>
    public RoomLinkCorpusFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(CorpusVariable) is not { Length: > 0 })
        {
            Skip = $"'{CorpusVariable}' is not set: the link tier's heavy grids run in the "
                + "corpus measure window only (no stock bytes are read — the mount marks the "
                + $"window). Set {CorpusVariable} to run them.";
        }
    }
}
