//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The level's visibility composed through its doorways
/// (<see cref="LevelDoorVisibility"/>), on hand-made rooms whose sight lines
/// are known: the per-room doorway data (<see cref="RoomDoorVisibility"/>)
/// of a room whose two doorways cannot see each other, what a neighbour sees
/// of it, a room it hides, a U-turn of rooms, the same level at every
/// rotation and thread count, and the flow's fallback and cancellation.
/// </summary>
/// <remarks>
/// <para>
/// The wall room is the harness's 256 cell with doors on its west and east
/// faces (96 wide, 96 high, centred) and a full-height wall across its
/// middle, x from 112 to 144, from the south wall to y = 200. What is left
/// is three regions: the west part, the east part, and a corridor along the
/// north wall (y from 200 to 240) that joins them. Every line from the west
/// doorway to the east one stays below y = 176 and so meets the wall: the
/// two doorways do not see each other, and a line in through the west
/// doorway reaches the corridor and the west part, never the east part.
/// vvis finds exactly that: three clusters, the west one seeing the
/// corridor but not the east one.
/// </para>
/// </remarks>
public sealed class LevelDoorVisibilityTests
{
    private const string West = "NegativeX";
    private const string East = "PositiveX";

    /// <summary>A point in the wall room's west part, its east part and its corridor, room-local.</summary>
    private static readonly Vec3 WestPoint = new(60, 100, 128);
    private static readonly Vec3 EastPoint = new(200, 100, 128);
    private static readonly Vec3 CorridorPoint = new(128, 220, 128);

    /// <summary>The wall room: doors west and east, and the wall across its middle.</summary>
    private static async Task<RoomObject> WallRoomAsync(VbspContext context)
    {
        RoomDefinition definition = RoomHarness.Room("wall", RoomFacing.NegativeX, RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(definition);
        VmfChunk world = document.Chunks.First(c => c.Name == MapFileLoader.WorldChunk);
        world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(112, 16, 16), new Vec3(144, 200, 240), 500));
        return await RoomCompiler.CompileAsync(document, definition, context);
    }

    private static int ClusterAt(RoomObject room, Vec3 local) => RoomHarness.LeafAt(room.Bsp, local).Cluster;

    private static int SocketIndex(RoomObject room, string name) =>
        room.Definition.Sockets.Select((s, i) => (s, i)).First(p => p.s.Name == name).i;

    /// <summary>
    /// The doorway-pair data of the wall room, from its own vvis: the west
    /// doorway is seen by the west part and the corridor, the east one by
    /// the east part and the corridor, each doorway faces its own part, and
    /// neither doorway sees the other through the room. Each cluster's box
    /// holds the point it was found by.
    /// </summary>
    [Fact]
    public async Task TheWallRoomsDoorwayDataIsWhatItsGeometrySays()
    {
        RoomObject room = await WallRoomAsync(await RoomHarness.ContextAsync());
        int west = ClusterAt(room, WestPoint), east = ClusterAt(room, EastPoint), corridor = ClusterAt(room, CorridorPoint);
        Assert.Equal(3, room.ClusterCount);
        Assert.Equal(3, new[] { west, east, corridor }.Distinct().Count());

        RoomLinkShared shared = LevelLinker.ComputeShared(room);
        RoomDoorVisibility doors = RoomDoorVisibility.Compute(room, shared);
        int w = SocketIndex(room, West), e = SocketIndex(room, East);
        Assert.Equal([west], shared.Sockets[w].Facing);
        Assert.Equal([east], shared.Sockets[e].Facing);

        Assert.True(doors.SeesDoor(w, west) && doors.SeesDoor(w, corridor) && !doors.SeesDoor(w, east));
        Assert.True(doors.SeesDoor(e, east) && doors.SeesDoor(e, corridor) && !doors.SeesDoor(e, west));
        Assert.False(doors.IsThrough(w, e));
        Assert.False(doors.IsThrough(e, w));
        Assert.False(doors.IsThrough(w, w));

        foreach ((int cluster, Vec3 point) in new[] { (west, WestPoint), (east, EastPoint), (corridor, CorridorPoint) })
        {
            Assert.True(doors.HasBox[cluster]);
            Assert.True(new Box(point, point).ContainsWithin(doors.ClusterBoxes[cluster], 0), $"cluster {cluster}'s box misses {point}");
        }
    }

    /// <summary>
    /// A two-way room joined to a hub through the wall room's west door: the
    /// hub sees the wall room's west part and corridor and not its east
    /// part, which only a line bending round the wall could reach, and the
    /// east part sees nothing of the hub. The door graph's closure (door
    /// visibility off) saw all of it. The level is linked at each quarter
    /// turn as a whole, and every turn gives the same rows, cluster for
    /// cluster: the flows run in the source room's frame, so a turned level
    /// is the same numbers.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AHubSeesOnlyWhatTheWallRoomsDoorwayShows(int turns)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject wall = await WallRoomAsync(context);
        RoomDefinition hubDefinition = RoomHarness.Hub();
        RoomObject hub = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(hubDefinition), hubDefinition, context);
        RoomLibrary library = RoomHarness.Library(hub, wall);

        LevelLayout layout = Turned(library, turns, ("hub", 0, 0), ("wall", 1, 0));
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);
        LinkedLevel graph = await LevelLinker.LinkAsync(layout, library, context, new LevelLinkOptions { DoorVisibility = false });
        DoorGraphFacts.AssertWithinDoorGraph(link, layout, library);
        DoorGraphFacts.AssertDoorGraph(graph, layout, library);

        int wallBase = hub.ClusterCount;
        int west = wallBase + ClusterAt(wall, WestPoint), east = wallBase + ClusterAt(wall, EastPoint), corridor = wallBase + ClusterAt(wall, CorridorPoint);
        for (int h = 0; h < hub.ClusterCount; h++)
        {
            Assert.True(link.Vis.CanSee(h, west) && link.Vis.CanSee(h, corridor), $"hub cluster {h} misses what the doorway shows");
            Assert.False(link.Vis.CanSee(h, east), $"hub cluster {h} sees the wall room's east part round the wall");
            Assert.False(link.Vis.CanSee(east, h));
            Assert.True(graph.Vis.CanSee(h, east));
        }

        // The same rows at every turn: compare with the unturned level's.
        if (turns != 0)
        {
            LinkedLevel straight = await LevelLinker.LinkAsync(Turned(library, 0, ("hub", 0, 0), ("wall", 1, 0)), library, context);
            Assert.Equal(straight.Vis.ClusterCount, link.Vis.ClusterCount);
            for (int c = 0; c < link.Vis.ClusterCount; c++)
            {
                Assert.True(straight.Vis.Pvs(c).SequenceEqual(link.Vis.Pvs(c)), $"row {c} differs at {turns} turn(s)");
                Assert.True(straight.Vis.Pas(c).SequenceEqual(link.Vis.Pas(c)), $"PAS row {c} differs at {turns} turn(s)");
            }
        }
    }

    /// <summary>
    /// A hub, the wall room, and a second hub in a line: no line gets from
    /// one hub through both of the wall room's doorways, so the hubs do not
    /// see each other, while each sees the part of the wall room its side's
    /// doorway shows. The door graph's closure joined them.
    /// </summary>
    [Fact]
    public async Task ARoomWhoseDoorwaysDoNotSeeEachOtherHidesTheRoomBeyond()
    {
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject wall = await WallRoomAsync(context);
        RoomDefinition hubDefinition = RoomHarness.Hub();
        RoomObject hub = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(hubDefinition), hubDefinition, context);
        RoomLibrary library = RoomHarness.Library(hub, wall);
        LevelLayout layout = RoomHarness.AutoLayout("through", library, ("hub", 0, 0, 0), ("wall", 1, 0, 0), ("hub", 2, 0, 0));

        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);
        DoorGraphFacts.AssertWithinDoorGraph(link, layout, library);
        int first = 0, last = hub.ClusterCount + wall.ClusterCount;
        for (int a = 0; a < hub.ClusterCount; a++)
        {
            for (int b = 0; b < hub.ClusterCount; b++)
            {
                Assert.False(link.Vis.CanSee(first + a, last + b), $"the first hub's cluster {a} sees the last hub's {b}");
            }
        }

        int wallBase = hub.ClusterCount;
        Assert.True(link.Vis.CanSee(first, wallBase + ClusterAt(wall, WestPoint)));
        Assert.True(link.Vis.CanSee(last, wallBase + ClusterAt(wall, EastPoint)));
        Assert.False(link.Vis.CanSee(last, wallBase + ClusterAt(wall, WestPoint)));
    }

    /// <summary>
    /// A U-turn and on: an end room, a bend north, a bend west, a bend north
    /// again beside the first room (sharing a wall and no door with it), and
    /// an end room above that. A straight line cannot go east, north and back
    /// west, so the first and last rooms do not see each other; nor do the
    /// first and fourth, whose shared face is wall. The real vvis on the same
    /// level compiled whole agrees, and every sight line of that monolithic
    /// map is kept by the linked one. Linked unturned and turned.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AUTurnHidesTheRoomsBehindAndKeepsEverySightLine(int turns)
    {
        VbspContext context = await RoomHarness.ContextAsync();
        RoomLibrary library = await UTurnLibraryAsync();
        LevelLayout layout = Turned(library, turns, UTurn);
        LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);
        DoorGraphFacts.AssertWithinDoorGraph(link, layout, library);

        (VbspResult monolithic, VisResult monolithicVis) = await MonolithicAsync(layout, library, context);
        LevelProbe linkedProbe = new(link.Bsp);
        LevelProbe monolithicProbe = new(monolithic.Bsp!);
        DoorGraphFacts.AssertKeepsEverySightLine($"u-turn {turns}", link, linkedProbe, monolithicProbe, monolithicVis);

        // The rooms' centres: the first sees neither the fourth nor the
        // last in either map, and sees both bends.
        Vec3 Centre(int room) => RoomTransform(layout, room).Apply(new Vec3(128, 128, 128));
        int LinkedAt(int room) => linkedProbe.Leafs[linkedProbe.Leaf(Centre(room))].Cluster;
        int MonolithicAt(int room) => monolithicProbe.Leafs[monolithicProbe.Leaf(Centre(room))].Cluster;
        foreach (int hidden in new[] { 3, 4 })
        {
            Assert.False(link.Vis.CanSee(LinkedAt(0), LinkedAt(hidden)), $"the first room sees room {hidden}");
            Assert.False(monolithicVis.CanSee(MonolithicAt(0), MonolithicAt(hidden)), $"the monolithic first room sees room {hidden}");
        }

        Assert.True(link.Vis.CanSee(LinkedAt(0), LinkedAt(1)));
        Assert.True(link.Vis.CanSee(LinkedAt(0), LinkedAt(2)));
    }

    /// <summary>The U-turn's rooms: an end room, the bends, and an end room facing south.</summary>
    private static readonly (string Room, int X, int Y)[] UTurn =
        [("end", 0, 0), ("north", 1, 0), ("west", 1, 1), ("up", 0, 1), ("down", 0, 2)];

    private static Task<RoomLibrary> UTurnLibraryAsync() => RoomHarness.LibraryAsync(
        false,
        RoomHarness.Room("end", RoomFacing.PositiveX),
        RoomHarness.Room("north", RoomFacing.NegativeX, RoomFacing.PositiveY),
        RoomHarness.Room("west", RoomFacing.NegativeY, RoomFacing.NegativeX),
        RoomHarness.Room("up", RoomFacing.PositiveX, RoomFacing.PositiveY),
        RoomHarness.Room("down", RoomFacing.NegativeY));

    /// <summary>
    /// The composition is a function of the level alone: the same rows and
    /// the same visibility lump on one thread and on many, run twice.
    /// </summary>
    [Fact]
    public async Task TheVisibilityIsTheSameAtAnyThreadCount()
    {
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject wall = await WallRoomAsync(context);
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        library.Add(wall);
        LevelLayout layout = RoomHarness.AutoLayout(
            "grid", library, ("hub", 0, 0, 0), ("hub", 1, 0, 0), ("wall", 2, 0, 1), ("hub", 0, 1, 0), ("hub", 1, 1, 0), ("hub", 2, 1, 0),
            ("hub", 0, 2, 0), ("hub", 1, 2, 0), ("hub", 2, 2, 0));

        byte[]? expected = null;
        foreach (int degree in new[] { 1, 8, 1, 3 })
        {
            context.Parallelism = new CompileParallelism { MaxDegree = degree };
            LinkedLevel link = await LevelLinker.LinkAsync(layout, library, context);
            byte[] lump = [.. link.Bsp[BspLump.Visibility].Data.Span];
            expected ??= lump;
            Assert.True(expected.AsSpan().SequenceEqual(lump), $"the visibility lump differs at {degree} thread(s)");
        }
    }

    /// <summary>
    /// A flow that would enter more rooms than its cap gives up and keeps
    /// every cluster, rather than cut its walk short and guess: with a cap
    /// of one room the U-turn's first and last rooms see each other again
    /// (they share no face, so only the flows decide them), and every pair
    /// the uncapped flow keeps is still kept.
    /// </summary>
    [Fact]
    public async Task AFlowPastItsCapKeepsEveryCluster()
    {
        VbspContext context = await RoomHarness.ContextAsync();
        RoomLibrary library = await UTurnLibraryAsync();
        LevelLayout layout = Turned(library, 0, UTurn);
        LinkedLevel full = await LevelLinker.LinkAsync(layout, library, context);
        LinkedLevel capped = await LevelLinker.LinkAsync(layout, library, context, new LevelLinkOptions { DoorFlowStateCap = 1 });

        DoorGraphFacts.AssertWithinDoorGraph(capped, layout, library);
        int lastBase = full.Vis.ClusterCount - library.Get("down").ClusterCount;
        Assert.False(full.Vis.CanSee(0, lastBase));
        Assert.True(capped.Vis.CanSee(0, lastBase));
        for (int a = 0; a < full.Vis.ClusterCount; a++)
        {
            for (int b = 0; b < full.Vis.ClusterCount; b++)
            {
                Assert.True(!full.Vis.CanSee(a, b) || capped.Vis.CanSee(a, b), $"the capped flow lost {a} -> {b}");
            }
        }
    }

    /// <summary>
    /// A cancelled token stops the composition: the link throws rather than
    /// write a visibility it did not finish.
    /// </summary>
    [Fact]
    public async Task ACancelledCompositionThrows()
    {
        RoomLibrary library = await RoomHarness.LibraryAsync(false, RoomHarness.Hub());
        RoomObject hub = library.Get("hub");
        RoomLinkShared shared = LevelLinker.ComputeShared(hub);
        RoomDoorVisibility doors = RoomDoorVisibility.Compute(hub, shared);
        byte[][] rows = [.. Enumerable.Range(0, hub.ClusterCount).Select(c => hub.Vis.Pvs(c).ToArray())];
        LevelDoorRoom Room(int x, int index) => new()
        {
            ClusterBase = index * hub.ClusterCount,
            Doors = doors,
            OwnRows = rows,
            Transform = new RoomTransform(new RoomPlacement("hub", x, 0, 0), RoomHarness.Cell),
            Joints = [],
        };

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LevelDoorVisibility.ComposeAsync(
            [Room(0, 0), Room(1, 1)], 2 * hub.ClusterCount, CompileParallelism.Default, LevelDoorVisibility.DefaultStateCap, cancelled.Token));
    }

    /// <summary>
    /// The neighbouring-room test on its own: a segment between two boxes on
    /// either side of a doorway's plane crosses the doorway when the boxes
    /// line up with it, not when both lie above it, in any room's frame;
    /// boxes touching the plane count; a box wholly past the plane is not
    /// decided (kept).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ASegmentBetweenTwoBoxesCrossesTheDoorwayOnlyWhereTheyLineUp(int frameTurns)
    {
        RoomTransform frame = new(new RoomPlacement("any", 3, 2, frameTurns), RoomHarness.Cell);
        Box opening = new(new Vec3(256, 80, 80), new Vec3(256, 176, 176));
        Box low = new(new Vec3(144, 16, 16), new Vec3(240, 200, 240));
        Box farLow = new(new Vec3(272, 16, 16), new Vec3(368, 200, 240));
        Box high = new(new Vec3(144, 16, 200), new Vec3(240, 240, 240));
        Box farHigh = new(new Vec3(272, 16, 200), new Vec3(368, 240, 240));
        Box farSide = new(new Vec3(272, 190, 16), new Vec3(368, 240, 240));
        Box touching = new(new Vec3(240, 100, 100), new Vec3(256, 120, 120));
        Box past = new(new Vec3(300, 16, 16), new Vec3(400, 40, 40));

        // In the frame, and the same either way round.
        bool Through(Box near, Box far, Box door)
        {
            bool forward = LevelDoorVisibility.Through(
                LevelDoorVisibility.ToFrame(frame, near), LevelDoorVisibility.ToFrame(frame, far), LevelDoorVisibility.ToFrame(frame, door));
            bool back = LevelDoorVisibility.Through(
                LevelDoorVisibility.ToFrame(frame, far), LevelDoorVisibility.ToFrame(frame, near), LevelDoorVisibility.ToFrame(frame, door));
            Assert.Equal(forward, back);
            return forward;
        }

        Assert.True(Through(low, farLow, opening));
        Assert.False(Through(high, farHigh, opening));
        Assert.True(Through(low, farHigh, opening));
        Assert.True(Through(touching, farHigh, opening));
        Assert.True(Through(past, farLow, opening));

        // A pocket past the doorway's side: lines from anywhere low reach it
        // through the doorway only by rising, which the low box allows.
        Assert.True(Through(low, farSide, opening));
        Assert.False(Through(high, new Box(new Vec3(272, 190, 190), new Vec3(368, 240, 240)), opening));
    }

    /// <summary>
    /// Neighbouring rooms, composed from hand-made door visibility: each
    /// room has a cluster by its doorway and a pocket under its ceiling
    /// that sees the doorway. The pockets see each other only if a line can
    /// cross the doorway between them, which it cannot; a pocket with no
    /// bounds is kept; and two neighbours with no doorway between them see
    /// nothing of each other, whatever the flows say.
    /// </summary>
    [Fact]
    public async Task NeighbouringRoomsSeeEachOtherOnlyThroughTheirDoorway()
    {
        LevelDoorRoom[] Level(bool pocketBox, bool door)
        {
            LevelDoorRoom Room(int x, int index) => new()
            {
                ClusterBase = index * 2,
                Doors = new RoomDoorVisibility(
                    2,
                    [[0b11UL], [0b11UL]],
                    [false, true, true, false],
                    [new Box(new Vec3(144, 80, 80), new Vec3(240, 176, 176)), new Box(new Vec3(16, 16, 200), new Vec3(240, 240, 240))],
                    [true, pocketBox]),
                OwnRows = [[0b11], [0b11]],
                Transform = new RoomTransform(new RoomPlacement("synthetic", x, 0, 0), RoomHarness.Cell),
                Joints = door
                    ? [x == 0
                        ? new LevelDoor(0, 1, 1, new Box(new Vec3(256, 80, 80), new Vec3(256, 176, 176)), new Box(new Vec3(240, 80, 80), new Vec3(256, 176, 176)), [0])
                        : new LevelDoor(1, 0, 0, new Box(new Vec3(256, 80, 80), new Vec3(256, 176, 176)), new Box(new Vec3(256, 80, 80), new Vec3(272, 176, 176)), [0])]
                    : [],
            };

            return [Room(0, 0), Room(1, 1)];
        }

        LevelVisibility tight = await LevelDoorVisibility.ComposeAsync(Level(pocketBox: true, door: true), 4, CompileParallelism.Default, LevelDoorVisibility.DefaultStateCap, CancellationToken.None);
        Assert.True(Sees(tight, 0, 2) && Sees(tight, 0, 3) && Sees(tight, 1, 2));
        Assert.False(Sees(tight, 1, 3));
        Assert.False(Sees(tight, 3, 1));

        LevelVisibility unbounded = await LevelDoorVisibility.ComposeAsync(Level(pocketBox: false, door: true), 4, CompileParallelism.Default, LevelDoorVisibility.DefaultStateCap, CancellationToken.None);
        Assert.True(Sees(unbounded, 1, 3));

        LevelVisibility walled = await LevelDoorVisibility.ComposeAsync(Level(pocketBox: true, door: false), 4, CompileParallelism.Default, LevelDoorVisibility.DefaultStateCap, CancellationToken.None);
        Assert.False(Sees(walled, 0, 2));
        Assert.True(Sees(walled, 0, 1) && Sees(walled, 2, 3));

        static bool Sees(LevelVisibility v, int from, int to) => (v.Pvs[(from * v.RowBytes) + (to >> 3)] & (1 << (to & 7))) != 0;
    }

    /// <summary>
    /// The bit-matrix transpose the rows are made symmetric with is the
    /// transpose, bit for bit, at sizes around the 64-bit block and on dense
    /// and sparse matrices.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 1)]
    [InlineData(63, 0.3, 2)]
    [InlineData(64, 0.5, 3)]
    [InlineData(65, 0.05, 4)]
    [InlineData(130, 0.9, 5)]
    [InlineData(200, 0.01, 6)]
    public void TheTransposeIsTheTranspose(int count, double density, int seed)
    {
        Random random = new(seed);
        int words = (count + 63) >> 6;
        ulong[][] rows = new ulong[count][];
        for (int r = 0; r < count; r++)
        {
            rows[r] = new ulong[words];
            for (int c = 0; c < count; c++)
            {
                if (random.NextDouble() < density)
                {
                    rows[r][c >> 6] |= 1UL << (c & 63);
                }
            }
        }

        ulong[][] transposed = LevelDoorVisibility.Transpose(rows, count);
        for (int r = 0; r < count; r++)
        {
            for (int c = 0; c < count; c++)
            {
                Assert.Equal((rows[r][c >> 6] >> (c & 63)) & 1, (transposed[c][r >> 6] >> (r & 63)) & 1);
            }

            for (int c = count; c < words * 64; c++)
            {
                Assert.Equal(0UL, (transposed[r][c >> 6] >> (c & 63)) & 1);
            }
        }
    }

    /// <summary>
    /// A level turned <paramref name="turns"/> quarter turns about the grid's
    /// origin as a whole, then moved back onto non-negative cells: each room
    /// moves to its turned cell and takes the turns; joints are derived from
    /// the turned sockets. The rooms keep their order, so their clusters keep
    /// their numbers.
    /// </summary>
    private static LevelLayout Turned(RoomLibrary library, int turns, params (string Room, int X, int Y)[] cells)
    {
        List<(string Room, int X, int Y)> moved = [];
        foreach ((string room, int x, int y) in cells)
        {
            (int tx, int ty) = (x, y);
            for (int t = 0; t < turns; t++)
            {
                (tx, ty) = (-ty, tx);
            }

            moved.Add((room, tx, ty));
        }

        int minX = moved.Min(m => m.X), minY = moved.Min(m => m.Y);
        return RoomHarness.AutoLayout(
            $"turned{turns}", library, [.. moved.Select(m => (m.Room, m.X - minX, m.Y - minY, turns))]);
    }

    private static RoomTransform RoomTransform(LevelLayout layout, int room) =>
        new(layout.Rooms[room].Placement, layout.CellSize);

    /// <summary>The level as one CSG'd map, jointed plugs dropped, compiled and vvis'd for real.</summary>
    private static async Task<(VbspResult Map, VisResult Vis)> MonolithicAsync(LevelLayout layout, RoomLibrary library, VbspContext context)
    {
        VmfDocument merged = RoomModel.BuildMerged(layout, library);
        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "900100");
        start.AddKey("classname", "info_player_start");
        Vec3 origin = new RoomTransform(layout.Rooms[0].Placement, layout.CellSize).Apply(new Vec3(128, 128, 129));
        start.AddKey("origin", RoomModel.Tuple(origin.X, origin.Y, origin.Z));
        merged.Chunks.Add(start);
        VbspResult monolithic = await RoomHarness.CompileAsync(merged, context);
        Assert.Null(monolithic.Leak);
        VisResult vis = await RoomHarness.VisAsync(monolithic.Bsp!, PortalSet.FromPortalFile(monolithic.Portals!));
        return (monolithic, vis);
    }
}
