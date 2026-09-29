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

using SourceSharp.MapTools.Rooms;
using SourceSharp.RoomContracts;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomBrushHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A room's brush models as the room compile describes them for the link
/// (<see cref="RoomBrushModels"/>): the runs of the lumps each owns, what it
/// is, its conditions and furniture keys, its collision turned four ways,
/// and the <c>BMOD</c> pack section.
/// </summary>
public sealed class RoomBrushModelsTests
{
    /// <summary>The hub with a world-coordinate door (room furniture on its east socket, needing a room north) and an origin-relative hinged door.</summary>
    internal static async Task<RoomObject> HubWithBrushesAsync() =>
        (await CompileAsync(RoomPropHarness.Library(
            (0, Door(700, new Vec3(100, 100, 16), new Vec3(132, 132, 64), ("targetname", "door"), (RoomNeeds.Key, "north,!joined_east"))),
            (0, Rotating(701, new Vec3(40, 40, 16), new Vec3(56, 90, 100), new Vec3(44, 44, 20),
                (RoomStaticProps.SocketKey, "west"), (RoomStaticProps.PriorityKey, "3")))))).Get("hub");

    /// <summary>
    /// Each brush model is described from the compile: its class and Hammer
    /// id, whether it is origin-relative, its conditions and furniture keys,
    /// and runs that are exactly its own (its tree from its head node, its
    /// face range, its entity's brush, the edges and original faces only its
    /// faces use, its vertex-normal indices counted in face order), with its
    /// collision record's key data and convexes.
    /// </summary>
    [Fact]
    public async Task EachBrushModelIsDescribedFromTheCompile()
    {
        RoomObject hub = await HubWithBrushesAsync();
        RoomBrushModels models = hub.BrushModelsOfCompile!;
        Assert.Equal(4, models.TurnCount);
        Assert.Equal(2, models.Models.Count);

        RoomBrushModel door = models.Models[0];
        Assert.Equal((1, 700, "func_door", false), (door.Model, door.Id, door.ClassName, door.OriginRelative));
        Assert.Equal(new[] { new RoomNeed(RoomDirection.North, false, false), new RoomNeed(RoomDirection.East, true, true) }, door.Needs);
        Assert.Equal((-1, 0), (door.Socket, door.Priority));

        RoomBrushModel hinged = models.Models[1];
        Assert.Equal((2, 701, "func_door_rotating", true), (hinged.Model, hinged.Id, hinged.ClassName, hinged.OriginRelative));
        Assert.Empty(hinged.Needs);
        Assert.Equal((hub.Definition.Sockets.ToList().FindIndex(s => s.Name == "west"), 3), (hinged.Socket, hinged.Priority));

        BspData bsp = hub.Bsp;
        DModel[] lumpModels = BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray();
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        int[] surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]).ToArray();
        foreach (RoomBrushModel model in models.Models)
        {
            DModel lump = lumpModels[model.Model];
            Assert.Equal(new RoomRange(lump.FirstFace, lump.NumFaces), model.Faces);
            Assert.True(model.Nodes.Contains(lump.HeadNode));
            Assert.Equal(1, model.Brushes.Count);
            Assert.True(model.Leaves.Count > 0);
            Assert.True(model.LeafFaces.Count > 0);
            Assert.Equal(faces[model.Faces.First..model.Faces.End].Sum(f => f.NumEdges), model.VertNormalIndices.Count);
            Assert.Equal(faces[..model.Faces.First].Sum(f => f.NumEdges), model.VertNormalIndices.First);
            Assert.Equal(model.Faces.Count, model.OrigFaces.Count);
            for (int f = 0; f < faces.Length; f++)
            {
                for (int e = 0; e < faces[f].NumEdges; e++)
                {
                    Assert.Equal(model.Faces.Contains(f), model.Edges.Contains(Math.Abs(surfEdges[faces[f].FirstEdge + e])));
                }
            }

            Assert.NotNull(model.KeyData);
            Assert.Equal(new[] { false }, model.OuterHulls);
            RoomBrushModelTurn turn0 = models.Turn(0)[model.Model - 1];
            Assert.Equal(new Box(lump.Mins, lump.Maxs), turn0.Bounds);
            Assert.Single(Assert.Single(turn0.Solids).Ledges.Starts);
        }

        Assert.True(models.IsFor(hub));
        Assert.False(models.IsFor(RoomHarness.WithLumps(hub, _ => { })));
    }

    /// <summary>A room whose compile has only the world has no brush models to describe.</summary>
    [Fact]
    public async Task ARoomWithOnlyTheWorldHasNone()
    {
        RoomObject hub = (await CompileAsync(RoomPropHarness.Library())).Get("hub");
        Assert.Null(hub.BrushModels);
        Assert.Null(hub.BrushModelsOfCompile);
    }

    /// <summary>
    /// A turn of a model: its bounds turned as every box of the link, its
    /// convexes turned in IVP's axes, its drag areas with x and y swapped on
    /// an odd turn; turn 0 is the model itself; and turn 0 alone, turned by
    /// the link, is every stored turn exactly (the rooms design, 15.9).
    /// </summary>
    [Fact]
    public async Task TurnZeroTurnedIsEveryStoredTurn()
    {
        RoomBrushModels models = (await HubWithBrushesAsync()).BrushModelsOfCompile!;
        RoomBrushModels once = models.WithTurnZeroOnly();
        Assert.Equal(1, once.TurnCount);
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal(Describe(models.Turn(turn)), Describe(once.Turn(turn)));
        }

        RoomBrushModelTurn model = new(
            new Box(new Vec3(1, 2, 3), new Vec3(4, 6, 8)),
            [new RoomModelSolid(new Vec3(0.25f, 0.5f, 1f), models.Turn(0)[0].Solids[0].Ledges)]);
        Assert.Same(model, RoomBrushModels.TurnModel(model, 0));
        RoomBrushModelTurn quarter = RoomBrushModels.TurnModel(model, 1);
        Assert.Equal(LevelLinker.RotateBox(model.Bounds.Mins, model.Bounds.Maxs, 1), quarter.Bounds);
        Assert.Equal(new Vec3(0.5f, 0.25f, 1f), quarter.Solids[0].DragAreas);
        Assert.Equal(new Vec3(0.25f, 0.5f, 1f), RoomBrushModels.TurnModel(model, 2).Solids[0].DragAreas);
        Assert.NotEqual(model.Solids[0].Ledges.Ledges, quarter.Solids[0].Ledges.Ledges);
        Assert.Equal(model.Solids[0].Ledges.Ledges, RoomBrushModels.TurnModel(RoomBrushModels.TurnModel(quarter, 2), 1).Solids[0].Ledges.Ledges);
    }

    /// <summary>
    /// The models go through their pack section and back unchanged, with
    /// each codec, bound to the BSP they are read with; a section of a
    /// revision this build does not read is absent.
    /// </summary>
    [Fact]
    public async Task TheSectionRoundTripsWithEachCodec()
    {
        RoomObject hub = await HubWithBrushesAsync();
        RoomBrushModels models = hub.BrushModelsOfCompile!;
        foreach (RoomLinkCodec codec in new[] { RoomLinkCodec.None, RoomLinkCodec.Deflate, RoomLinkCodec.Brotli })
        {
            RoomPackSectionData section = models.ToSection(codec);
            Assert.Equal(RoomBrushModels.SectionTag, section.Tag);
            RoomBrushModels read = RoomBrushModels.Read(section.Bytes.ToArray(), hub.Definition, hub.Bsp)!;
            Assert.True(read.IsFor(hub));
            Assert.Equal(models.Models.Select(Describe), read.Models.Select(Describe));
            Assert.Equal(4, read.TurnCount);
            for (int turn = 0; turn < 4; turn++)
            {
                Assert.Equal(Describe(models.Turn(turn)), Describe(read.Turn(turn)));
            }

            Assert.Equal(section.Bytes.ToArray(), read.ToSection(codec).Bytes.ToArray());
        }

        Assert.Equal(1, RoomBrushModels.Read(models.WithTurnZeroOnly().ToSection().Bytes.ToArray(), hub.Definition, hub.Bsp)!.TurnCount);
        Assert.Null(RoomBrushModels.Read(null, hub.Definition, hub.Bsp));
        RoomLinkSections.Writer future = new();
        future.Int(RoomBrushModels.Revision + 1);
        Assert.Null(RoomBrushModels.Read(RoomLinkSections.Encode(future.ToArray(), RoomLinkCodec.None), hub.Definition, hub.Bsp));
    }

    /// <summary>
    /// A section that does not fit the room it sits with is refused as
    /// damaged, naming the room and the section: another model count, a
    /// model out of order, a run outside its lump, a socket the room does
    /// not have, a negative key data length, solids for a model with no
    /// record, a turn count other than 1 or 4, bytes after its end.
    /// </summary>
    [Fact]
    public async Task ASectionThatDoesNotFitItsRoomIsRefused()
    {
        RoomObject hub = await HubWithBrushesAsync();
        int nodes = BspStructView.Count<DNode>(hub.Bsp[BspLump.Nodes]);
        byte[] Payload(Action<RoomLinkSections.Writer> write)
        {
            RoomLinkSections.Writer w = new();
            w.Int(RoomBrushModels.Revision);
            write(w);
            return RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None);
        }

        // One model, with the runs given, a record with no solids, then turns.
        void Model(RoomLinkSections.Writer w, int index = 1, int socket = -1, int firstNode = 0, int keyLength = -1, int solids = 0)
        {
            w.Int(index);
            w.Int(700);
            w.String("func_door");
            w.Byte(0);
            w.Int(0);
            w.Int(socket);
            w.Int(0);
            w.Int(firstNode);
            w.Int(1);
            for (int run = 1; run < 8; run++)
            {
                w.Int(0);
                w.Int(0);
            }

            w.Int(keyLength);
            w.Int(solids);
            for (int s = 0; s < solids; s++)
            {
                w.Byte(0);
            }
        }

        string Refused(Action<RoomLinkSections.Writer> write) =>
            Assert.Throws<LinkException>(() => RoomBrushModels.Read(Payload(write), hub.Definition, hub.Bsp)).Message;

        const string At = "room pack entry \"hub\": its \"BMOD\" section holds ";
        Assert.Equal(At + "5 brush models; the room has 2.", Refused(w => w.Int(5)));
        Assert.Equal(At + "model 2 in place of model 1.", Refused(w => { w.Int(2); Model(w, index: 2); }));
        Assert.Equal(At + $"a run of nodes {nodes} + 1; the room has {nodes}.", Refused(w => { w.Int(2); Model(w, firstNode: nodes); }));
        Assert.Equal(At + "socket 4 for a brush model; the room has 4.", Refused(w => { w.Int(2); Model(w, socket: 4); }));
        Assert.Equal(At + "key data of -2 bytes.", Refused(w => { w.Int(2); Model(w, keyLength: -2); }));
        Assert.Equal(At + "1 solids for a model with no collision record.", Refused(w => { w.Int(2); Model(w, solids: 1); }));
        Assert.Equal(At + "3 turns of brush models; a section holds 1 or 4.", Refused(w => { w.Int(2); Model(w); Model(w, index: 2); w.Int(3); }));
        Assert.Equal(At + "1 bytes after its end.", Refused(w =>
        {
            w.Int(2);
            Model(w);
            Model(w, index: 2);
            w.Int(1);
            w.Box(default);
            w.Box(default);
            w.Byte(0);
        }));

        BspData bare = new();
        Assert.Equal(At + "1 brush models; the room has 0.", Assert.Throws<LinkException>(() => RoomBrushModels.Read(Payload(w => w.Int(1)), hub.Definition, bare)).Message);
    }

    /// <summary>
    /// A brush entity's furniture keys are held to the static prop's rules
    /// when the room is compiled: a <c>room_socket</c> naming no socket of
    /// the room, a <c>socket_priority</c> that is not a whole number.
    /// </summary>
    [Theory]
    [InlineData("up", null, "room hub: func_door 700 has room_socket \"up\", which is not a socket of the room.")]
    [InlineData("east", "first", "room hub: func_door 700 has socket_priority \"first\", which is not a whole number.")]
    public async Task ABrushEntitysFurnitureKeysAreCheckedAtPackTime(string socket, string? priority, string message)
    {
        List<(string, string)> keys = [(RoomStaticProps.SocketKey, socket)];
        if (priority is not null)
        {
            keys.Add((RoomStaticProps.PriorityKey, priority));
        }

        VmfDocument library = RoomPropHarness.Library((0, Door(700, new Vec3(100, 100, 16), new Vec3(132, 132, 64), [.. keys])));
        RoomLintException refused = await Assert.ThrowsAsync<RoomLintException>(() => CompileAsync(library));
        Assert.Equal(message, refused.Message);
    }

    /// <summary>A compiled brush entity is origin-relative exactly when its <c>origin</c> is not zero, as the map loader decides.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("0 0 0", false)]
    [InlineData("-0 0 0.0", false)]
    [InlineData("44 44 20", true)]
    [InlineData("0 0 -1", true)]
    [InlineData("x y z", false)]
    public void ABrushEntityIsOriginRelativeWhenItsOriginIsNotZero(string? origin, bool expected)
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", "func_door"));
        if (origin is not null)
        {
            entity.Pairs.Add(new BspKeyValue("origin", origin));
        }

        Assert.Equal(expected, RoomBrushModels.IsOriginRelative(entity));
    }

    private static string Describe(RoomBrushModel model) =>
        string.Join(
            "|",
            model.Model,
            model.Id,
            model.ClassName,
            model.OriginRelative,
            string.Join(",", model.Needs),
            model.Socket,
            model.Priority,
            model.Nodes,
            model.Leaves,
            model.Faces,
            model.LeafFaces,
            model.Brushes,
            model.Edges,
            model.OrigFaces,
            model.VertNormalIndices,
            Convert.ToHexString(model.KeyData ?? []),
            string.Join(",", model.OuterHulls));

    private static List<string> Describe(RoomBrushModelTurn[] turn) =>
        [.. turn.Select(m => $"{m.Bounds} " + string.Join(
            ";",
            m.Solids.Select(s => $"{s.DragAreas} {Convert.ToHexString(s.Ledges.Ledges)} {string.Join(",", s.Ledges.Starts)}")))];
}
