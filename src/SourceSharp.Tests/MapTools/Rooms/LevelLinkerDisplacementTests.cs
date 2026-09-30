//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.RoomDisplacementHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Displacements through the link (section 4.5 of the rooms design): every
/// placed room's displacements are carried, their starts moved and their
/// vectors turned with the room at every quarter turn, their runs, base
/// faces and neighbours rebased, their collision the room's, and the
/// level's surfaces agree with the flattened level's vbsp compile.
/// </summary>
public sealed class LevelLinkerDisplacementTests(ITestOutputHelper output)
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A level of a hub with two neighbouring patches beside another room
    /// with one, at every quarter turn: the linked map holds the three in
    /// link order, each record's runs, face and neighbours its own, and it
    /// agrees with the flattened level's compile on everything that does not
    /// depend on where a compile put a displacement (power, flags,
    /// neighbours, allowed vertices, distances, alphas, tags, sample
    /// positions, material and lightmap size, collision hull size), bit for
    /// bit; the start positions bit for bit; the surfaces' vertices within a
    /// thousandth of a unit and their normals within a hundred-thousandth;
    /// and <c>ssmap check</c> finds no error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelHoldsItsRoomsDisplacementsAtEveryRotation(int rotation)
    {
        VmfDocument library = Library(Patches);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        DispInfo[] infos = Infos(linked.Bsp);
        Assert.Equal(3, infos.Length);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.Equal(Infos(flat).Select(d => d.StartPosition), infos.Select(d => d.StartPosition));

        float vertexGap = VertexGap(linked.Bsp, flat);
        float normalGap = NormalGap(linked.Bsp, flat);
        output.WriteLine($"turn {rotation}: vertex gap {vertexGap:G9}, normal gap {normalGap:G9}");
        Assert.True(vertexGap < 1e-3f, $"vertices {vertexGap} apart");
        Assert.True(normalGap < 1e-5f, $"normals {normalGap} apart");

        // Every record names its own face, which names it back.
        DFace[] faces = BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray();
        for (int i = 0; i < infos.Length; i++)
        {
            Assert.Equal(i, faces[infos[i].MapFace].DispInfo);
        }

        // The collision hulls are each room's own (a placement's hull is its
        // room's bytes, which name vertices by index); the flattened compile
        // cooks them anew and cuts the same convex hull into its own
        // triangles where the move rounds differently, so Observed compares
        // their corners.
        IReadOnlyList<byte[]?> hulls = RoomDisplacements.CollisionBlobs(linked.Bsp);
        Assert.Equal(
            [.. RoomDisplacements.CollisionBlobs(rooms.Get("hub").Bsp), .. RoomDisplacements.CollisionBlobs(rooms.Get("other").Bsp)],
            hulls);

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The level's world collision announces the virtual terrain its
    /// displacements' hulls stand for, as a map vbsp compiles with
    /// displacements does, and a level whose rooms were compiled without a
    /// cooker carries its displacements with no collision at all, as such a
    /// compile writes none.
    /// </summary>
    [Fact]
    public async Task TheCollisionIsTheRoomsOwn()
    {
        VmfDocument library = Library(Patches);
        LevelGrid level = RoomPropHarness.Level("hub@90, other@270");
        LinkedLevel cooked = await RoomPropHarness.LinkAsync(await CompileAsync(library), level);
        PhysCollideModel world = PhysCollideLump.Read(cooked.Bsp[BspLump.PhysCollide].Data.Span)[0];
        Assert.Contains("virtualterrain", world.KeyText, StringComparison.Ordinal);
        Assert.Contains("virtualterrain", PhysCollideLump.Read((await CompileFlatAsync(library, level))[BspLump.PhysCollide].Data.Span)[0].KeyText, StringComparison.Ordinal);

        LinkedLevel bare = await RoomPropHarness.LinkAsync(await CompileAsync(library, cook: false), level);
        Assert.Equal(3, Infos(bare.Bsp).Length);
        Assert.True(bare.Bsp[BspLump.PhysDisp].IsEmpty);
        Assert.True(bare.Bsp[BspLump.PhysCollide].IsEmpty);
        Assert.Equal(Observed(await CompileFlatAsync(library, level, cook: false)), Observed(bare.Bsp));
    }

    /// <summary>
    /// A room placed twice and three rooms in a row, at mixed turns: every
    /// placement's displacements follow the one before, each placement's
    /// neighbours rebased onto its own displacements, and the level agrees
    /// with the flattened compile as in the level of two.
    /// </summary>
    [Fact]
    public async Task EveryPlacementBringsItsOwnDisplacements()
    {
        VmfDocument library = Library(Patches);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other@90");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        DispInfo[] infos = Infos(linked.Bsp);
        Assert.Equal(9, infos.Length);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.Equal(Infos(flat).Select(d => d.StartPosition), infos.Select(d => d.StartPosition));
        Assert.True(VertexGap(linked.Bsp, flat) < 1e-3f);

        // The hub's two patches share an edge: each placement's pair names
        // each other, never another placement's.
        foreach (int first in infos.Select((d, i) => (d, i)).Where(x => x.d.Power == 2 && x.i + 1 < infos.Length && infos[x.i + 1].Power == 2).Select(x => x.i))
        {
            HashSet<int> named = [.. Neighbours(infos[first])];
            Assert.Equal([first + 1], named);
            Assert.Equal([first], [.. Neighbours(infos[first + 1])]);
        }

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));

        static IEnumerable<int> Neighbours(DispInfo d)
        {
            for (int e = 0; e < 4; e++)
            {
                for (int s = 0; s < 2; s++)
                {
                    if (d.EdgeNeighbors[e].SubNeighbors[s].IsValid())
                    {
                        yield return d.EdgeNeighbors[e].SubNeighbors[s].Neighbor;
                    }
                }

                for (int k = 0; k < d.CornerNeighbors[e].NumNeighbors; k++)
                {
                    yield return d.CornerNeighbors[e].Neighbors[k];
                }
            }
        }
    }

    // ---- refusals ----------------------------------------------------------------------------

    /// <summary>
    /// A displacement the link cannot carry as the flattened level compiles
    /// it is refused with the rooms design's text (15.4 for the socket) by
    /// the split, so by the pack and the flatten, and by a room compile given
    /// the room's VMF: an edge reaching into a doorway, a surface out of the
    /// cell, power 4, a displacement on a plug's side (the patch here is a
    /// second brush on the plug's box, whose top is the displacement). A patch whose edge only touches the doorway's inner face
    /// is carried.
    /// </summary>
    [Theory]
    [InlineData("doorway", "room hub: the displacement on brush side 48000 has an edge on socket \"east\"'s plug box; displacements may not meet at a joint.")]
    [InlineData("below", "room hub: the displacement on brush side 48000 reaches 4.50 units outside the cell; displacements stay in their cell.")]
    [InlineData("power", "room hub: the displacement on brush side 48000 is power 4; the link carries displacement collision only as the virtual mesh vbsp builds for powers 2 and 3.")]
    [InlineData("plug", "room hub: the displacement on brush side 48000 is on socket \"east\"'s plug, which a joint removes.")]
    public async Task ADisplacementTheLinkCannotCarryIsRefused(string fault, string message)
    {
        Box plug = RoomLinter.SealBox(RoomPropHarness.Hub, RoomPropHarness.Hub.Sockets.Single(s => s.Name == "east"), RoomHarness.Cell);
        VmfChunk patch = fault switch
        {
            "doorway" => Patch(PatchBrush, new Box(new Vec3(176, 96, 16), new Vec3(250, 160, 24)), offsets: false),
            "below" => Downward(Patch(PatchBrush, new Box(new Vec3(64, 64, 0), new Vec3(128, 128, 4)))),
            "plug" => Patch(PatchBrush, plug, offsets: false),
            _ => Patch(PatchBrush, HubWest, power: 4),
        };

        // A patch is a brush of its own; the plug's displacement is on the
        // plug itself (the hub's plug brush, whose top side takes it and
        // whose sides take the patch's id), since a second brush filling the
        // plug box is a second plug.
        void Add(VmfDocument document)
        {
            VmfChunk world = document.GetChunk(MapFileLoader.WorldChunk)!;
            if (fault != "plug")
            {
                world.Children.Add(patch);
                return;
            }

            VmfChunk solid = world.GetChunks(MapFileLoader.SolidChunk).Single(b => RoomLibraryVmf.Same(VmfPlacement.Bounds(b), plug));
            foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
            {
                side.Keys.Single(k => k.Name == "id").Value = "48000";
            }

            solid.Chunks.First().Children.Add(VmfPlacement.Clone(patch.Chunks.First().GetChunk("dispinfo")!));
        }

        VmfDocument library = Library([]);
        Add(library);
        Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message);
        Assert.Equal(message, Assert.Throws<RoomLibraryException>(() => LevelFlattener.Flatten(RoomPropHarness.Level("hub"), library)).Message);

        LibraryRoom hub = RoomLibraryVmf.SplitLibrary(Library([])).Rooms[0];
        VmfDocument room = hub.Document;
        Add(room);
        VbspContext context = await ContextAsync(null, "hub");
        RoomLintException compile = await Assert.ThrowsAsync<RoomLintException>(
            () => SourceSharp.MapTools.Rooms.RoomCompiler.CompileAsync(room, hub.Definition, context));
        Assert.Equal(message, compile.Message);

        static VmfChunk Downward(VmfChunk solid)
        {
            foreach (VmfKey row in solid.Chunks.First().GetChunk("dispinfo")!.GetChunk("normals")!.Keys)
            {
                row.Value = row.Value.Replace("0 0 1", "0 0 -1", StringComparison.Ordinal);
            }

            return solid;
        }
    }

    /// <summary>
    /// A patch that runs up to a doorway's inner face (where a floor meets
    /// the doorway, two wall depths from any neighbour's) is carried, and a
    /// level jointing that doorway links it as the flattened level compiles
    /// it, with no neighbour across the joint.
    /// </summary>
    [Fact]
    public async Task APatchUpToTheDoorwayIsCarried()
    {
        VmfDocument library = Library([
            (0, Patch(PatchBrush, new Box(new Vec3(176, 96, 16), new Vec3(240, 160, 24)), offsets: false)),
            (1, Patch(PatchBrush + 1, new Box(new Vec3(16, 96, 16), new Vec3(80, 160, 24)), offsets: false)),
        ]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);
        Assert.Equal(2, Infos(linked.Bsp).Length);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.All(Infos(linked.Bsp), d => Assert.All(Enumerable.Range(0, 4), e => Assert.False(d.EdgeNeighbors[e].SubNeighbors[0].IsValid())));
    }

    /// <summary>
    /// A room whose lumps have displacements but that carries no
    /// displacement data from its compile (a pack written before
    /// displacements were carried) is refused by name at link, and so is one
    /// carrying displacement collision without displacements; the lumps
    /// themselves are no longer refused (the old refusal by lump is gone).
    /// </summary>
    [Fact]
    public async Task ARoomWithDisplacementsButNoDisplacementDataIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(Library(Patches));
        RoomObject bare = rooms.Get("hub") with { Displacements = null };
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf(bare), RoomPropHarness.Level("hub")));
        Assert.Equal(
            "room hub has 2 displacements but no displacement data from its compile (a pack written before the link carried displacements,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);
        Assert.DoesNotContain("carries lump", refused.Message, StringComparison.Ordinal);

        RoomObject other = RoomHarness.WithLumps(rooms.Get("other"), bsp =>
        {
            bsp.SetLump(BspLump.DispInfo, Array.Empty<byte>());
            bsp.SetLump(BspLump.DispVerts, Array.Empty<byte>());
            bsp.SetLump(BspLump.DispTris, Array.Empty<byte>());
        });
        refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf(other), RoomPropHarness.Level("other")));
        Assert.Equal("room other carries displacement collision for 1 displacements but has none.", refused.Message);
    }

    /// <summary>A level past the displacements a map holds is refused naming the room and cell that crossed it.</summary>
    [Fact]
    public void ALevelPastTheDisplacementCapIsRefused()
    {
        LevelLinker.LinkTotals totals = new();
        totals.Add(new LevelLinker.LinkCounts { Displacements = 2000 }, "a", 0, 0);
        LinkException refused = Assert.Throws<LinkException>(() => totals.Add(new LevelLinker.LinkCounts { Displacements = 49 }, "b", 1, 0));
        Assert.Equal("room b at cell (1, 0) pushes the link to 2049 displacements; a map holds at most 2048 (MAX_MAP_DISPINFO).", refused.Message);

        LevelLinker.LinkTotals full = new();
        full.Add(new LevelLinker.LinkCounts { Displacements = 2048 }, "a", 0, 0);
        Assert.Equal(2, LevelLinker.LinkCounts.Of(CompileHubLumps(), 0).Displacements);

        static BspData CompileHubLumps()
        {
            BspData bsp = new();
            bsp.SetLump(BspLump.DispInfo, new byte[2 * 176]);
            return bsp;
        }
    }

    // ---- the pack, determinism, the budget -----------------------------------------------------

    /// <summary>
    /// Rooms with displacements through a pack: the pack stores a room's
    /// displacements (four turns), and none for a room without, the rooms it
    /// loads carry them, and the level links to the same bytes as from the
    /// rooms in memory; a pack holding only turn 0 (the rotation count of 1,
    /// the link turning them) links to the same bytes too, so storing once or
    /// four times is a choice of speed alone.
    /// </summary>
    [Fact]
    public async Task DisplacementsRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(Library([Patches[0], Patches[1]]));
        LevelGrid level = RoomPropHarness.Level("hub@90, other@180", "other, hub@270");
        byte[] expected = await BytesAsync(await RoomPropHarness.LinkAsync(rooms, level));

        using MemoryStream pack = new();
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms.Rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        await RoomPack.SaveAsync(items, pack);
        pack.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(pack);
        Assert.NotNull(index.Find("hub")!.Find(RoomDisplacements.SectionTag));
        Assert.Null(index.Find("other")!.Find(RoomDisplacements.SectionTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        Assert.Equal(4, loaded[0].DisplacementsOfCompile!.TurnCount);
        Assert.Null(loaded[1].DisplacementsOfCompile);
        Assert.Equal(expected, await BytesAsync(await RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf(
            [.. rooms.Rooms.Select(r => r.Displacements is { } d ? r with { Displacements = d.WithTurnZeroOnly() } : r)]);
        Assert.Equal(1, once.Get("hub").DisplacementsOfCompile!.TurnCount);
        Assert.Equal(expected, await BytesAsync(await RoomPropHarness.LinkAsync(once, level)));
    }

    /// <summary>
    /// A pack of rooms with displacements is the same bytes whether its rooms
    /// compiled on one thread or on four, run after run (the rooms design,
    /// 15.5), and so is a level linked from them at one thread and at many.
    /// </summary>
    [Fact]
    public async Task PacksAndLevelsWithDisplacementsAreTheSameBytesAtAnyThreadCount()
    {
        async Task<byte[]> PackAsync(int degree)
        {
            RoomLibrary rooms = await CompileAsync(Library(Patches), degree);
            List<RoomPackItem> items = [];
            foreach (string name in new[] { "hub", "other" })
            {
                items.Add(await RoomPackItem.CreateAsync(rooms.Find(name)!));
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            return pack.ToArray();
        }

        byte[] serial = await PackAsync(1);
        Assert.Equal(serial, await PackAsync(4));
        Assert.Equal(serial, await PackAsync(1));

        RoomLibrary library = await CompileAsync(Library(Patches));
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other");
        byte[] linked = await BytesAsync(await RoomPropHarness.LinkAsync(library, level, 1));
        Assert.Equal(linked, await BytesAsync(await RoomPropHarness.LinkAsync(library, level, 8)));
        Assert.Equal(linked, await BytesAsync(await RoomPropHarness.LinkAsync(library, level, 1)));
    }

    /// <summary>
    /// Displacements cost the level no entity (the rooms design, 15.6): a
    /// level with them has the entity lump and budget of the same level
    /// without them.
    /// </summary>
    [Fact]
    public async Task DisplacementsCostNoEntity()
    {
        LevelGrid level = RoomPropHarness.Level("hub@90, other");
        LinkedLevel bare = await RoomPropHarness.LinkAsync(await CompileAsync(Library([])), level);
        LinkedLevel patched = await RoomPropHarness.LinkAsync(await CompileAsync(Library(Patches)), level);
        Assert.Equal(3, Infos(patched.Bsp).Length);
        Assert.Equal(bare.Bsp[BspLump.Entities].Data.ToArray(), patched.Bsp[BspLump.Entities].Data.ToArray());
        Assert.Equal(bare.EntityBudget!.Edicts, patched.EntityBudget!.Edicts);
        Assert.Equal(bare.EntityBudget.Listed, patched.EntityBudget.Listed);
    }
}
