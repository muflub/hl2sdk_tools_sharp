//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomBrushHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Brush entities through the link (section 4.1 of the rooms design): each
/// placed room's brush entities linked as their own models, world-coordinate
/// and origin-relative, with their trees, faces, brushes and collision
/// records moved with the placement at every quarter turn, their
/// <c>model</c> keys renumbered, and the level agreeing with the flattened
/// level's vbsp compile.
/// </summary>
public sealed class LevelLinkerBrushEntityTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The hub's brush entities: a door, a hinged door, a trigger, a wall
    /// brush, and a veil whose <c>origin</c> is written at the room's own
    /// origin (as Hammer writes one on many brush entities).
    /// </summary>
    private static (int, VmfChunk)[] HubBrushes =>
    [
        (0, Door(700, new Vec3(100, 100, 16), new Vec3(132, 132, 64), ("targetname", "hub_door"))),
        (0, Rotating(701, new Vec3(40, 40, 16), new Vec3(56, 90, 100), new Vec3(44, 44, 20), ("targetname", "hub_hinged"))),
        (0, Trigger(702, new Vec3(150, 40, 16), new Vec3(200, 90, 100), ("targetname", "hub_trigger"))),
        (0, Brush("func_brush", 703, new Vec3(60, 160, 16), new Vec3(80, 200, 48), keys: ("targetname", "hub_wall"))),
        (0, Brush("func_illusionary", 704, new Vec3(160, 160, 16), new Vec3(200, 200, 48), keys: [("targetname", "hub_veil"), ("origin", "0 0 0")])),
    ];

    /// <summary>
    /// A level of a hub with four brush entities of several classes and
    /// another room with one, both turned: the linked map holds every one
    /// as its own model, and agrees with the flattened level's compile in
    /// what a game observes of each (class, keys, world bounds, faces per
    /// material), in traces against each model at its origin, and in each
    /// model's collision convexes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task LinkAndFlattenAgreeOnBrushEntitiesAtEveryRotation(int rotation)
    {
        VmfDocument library = RoomPropHarness.Library(
        [
            .. HubBrushes,
            (1, Rotating(710, new Vec3(150, 150, 16), new Vec3(200, 166, 90), new Vec3(196, 156, 20), ("targetname", "other_hinged"))),
        ]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{(rotation + 90) % 360}");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(7, BspStructView.Count<DModel>(linked.Bsp[BspLump.Models]));
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        foreach (string name in new[] { "hub_door", "hub_hinged", "hub_trigger", "hub_wall", "hub_veil", "other_hinged" })
        {
            Placed ours = Named(linked.Bsp, "targetname", name);
            Placed theirs = Named(flat, "targetname", name);
            Assert.Equal(Traces(flat, theirs), Traces(linked.Bsp, ours));
            List<Box> a = Convexes(flat, theirs);
            List<Box> b = Convexes(linked.Bsp, ours);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.True(LinkedBrushProbe.Near(a[i], b[i], 0.05f), $"{name}: convex {i} {a[i]} against {b[i]}");
            }
        }
    }

    // ---- models and their entities ------------------------------------------------------------

    /// <summary>
    /// The linked map's models: the world first, over every placement's
    /// world faces and nothing else, then every placement's brush models in
    /// link order, each with its room's faces and a tree of its own (reached
    /// from its head node alone, no cluster, no leaf the world's tree
    /// reaches); each brush entity's <c>model</c> names its own; an
    /// origin-relative model keeps its entity's frame (its bounds turned,
    /// never moved, its entity's origin moved), a world-coordinate one is
    /// moved; and no brush entity carries the furniture keys, which only the
    /// link reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ModelsAreNumberedInLinkOrderAndTheirEntitiesNameThem(int rotation)
    {
        VmfDocument library = RoomPropHarness.Library(
        [
            .. HubBrushes[..2],
            (1, Trigger(710, new Vec3(40, 40, 16), new Vec3(90, 90, 64), ("targetname", "other_trigger"), (RoomStaticProps.PriorityKey, "2"))),
        ]);
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level($"hub@{rotation}, other"));
        BspData bsp = linked.Bsp;

        DModel[] models = BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray();
        Assert.Equal(4, models.Length);
        Assert.Equal(["hub_door", "hub_hinged", "other_trigger"], BrushEntities(bsp).OrderBy(p => p.Model).Select(p => p.Entity.Get("targetname")!));
        Assert.Equal([1, 2, 3], BrushEntities(bsp).Select(p => p.Model).Order());

        RoomObject hub = rooms.Get("hub");
        RoomObject other = rooms.Get("other");
        int hubWorld = BspStructView.As<DModel>(hub.Bsp[BspLump.Models])[0].NumFaces;
        int otherWorld = BspStructView.As<DModel>(other.Bsp[BspLump.Models])[0].NumFaces;
        Assert.Equal((0, hubWorld + otherWorld), (models[0].FirstFace, models[0].NumFaces));
        int face = hubWorld + otherWorld;
        foreach ((RoomObject room, int model, int linkedModel) in new[] { (hub, 1, 1), (hub, 2, 2), (other, 1, 3) })
        {
            DModel own = BspStructView.As<DModel>(room.Bsp[BspLump.Models])[model];
            Assert.Equal((face, own.NumFaces), (models[linkedModel].FirstFace, models[linkedModel].NumFaces));
            face += own.NumFaces;
        }

        Assert.Equal(BspStructView.Count<DFace>(bsp[BspLump.Faces]), face);

        // Every tree its own: the world's reaches no brush model's node or
        // leaf, and a brush model's leaves have no cluster.
        HashSet<int> world = Reached(bsp, models[0].HeadNode);
        for (int m = 1; m < models.Length; m++)
        {
            HashSet<int> own = Reached(bsp, models[m].HeadNode);
            Assert.Empty(own.Intersect(world));
            DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
            Assert.All(own.Where(r => r < 0), r => Assert.Equal(-1, leafs[~r].Cluster));
        }

        RoomTransform transform = new(new RoomPlacement("hub", 0, 0, rotation / 90), RoomHarness.Cell);
        Placed hinged = Named(bsp, "targetname", "hub_hinged");
        Assert.Equal(transform.Apply(new Vec3(44, 44, 20)), hinged.Origin);
        DModel ownHinged = BspStructView.As<DModel>(hub.Bsp[BspLump.Models])[2];
        Box turned = LevelLinker.RotateBox(ownHinged.Mins, ownHinged.Maxs, rotation / 90);
        Assert.Equal((turned.Mins, turned.Maxs), (models[hinged.Model].Mins, models[hinged.Model].Maxs));
        Placed door = Named(bsp, "targetname", "hub_door");
        Box moved = LevelLinker.MoveBox(transform, new Vec3(100, 100, 16), new Vec3(132, 132, 64));
        Assert.Equal((moved.Mins, moved.Maxs), (models[door.Model].Mins, models[door.Model].Maxs));
        Assert.All(BrushEntities(bsp), p => Assert.Null(p.Entity.Get(RoomStaticProps.PriorityKey)));
        Assert.Equal("0 " + ((90 + rotation) % 360).ToString(CultureInfo.InvariantCulture) + " 0", door.Entity.Get("movedir"));
    }

    /// <summary>
    /// The texinfo split of 4.1, and the planes' with it: a world pillar and
    /// a brush entity in the room's own frame (its <c>origin</c> written at
    /// the room's origin) that touch share a plane and a texinfo in the
    /// room's compile; linked into a turned, moved placement, the world's
    /// faces take the placement's translation and the entity's do not (its
    /// moved origin places it), so each is linked twice, once per frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task AnEntitysFrameLinksTheWorldsSharedPlanesAndTexinfosAgain(int rotation)
    {
        VmfChunk pillar = RoomModel.Slab(RoomHarness.Plain, new Vec3(60, 100, 16), new Vec3(80, 140, 64), 9500);
        VmfChunk veil = Brush("func_illusionary", 704, new Vec3(80, 100, 16), new Vec3(100, 140, 64), keys: [("targetname", "veil"), ("origin", "0 0 0")]);
        VmfDocument library = RoomPropHarness.Library((0, veil));
        library.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(pillar);
        RoomLibrary rooms = await CompileAsync(library);
        RoomObject hub = rooms.Get("hub");

        // In the room's compile the two share a plane and a texinfo.
        BspData own = hub.Bsp;
        DModel[] ownModels = BspStructView.As<DModel>(own[BspLump.Models]).ToArray();
        DFace[] ownFaces = BspStructView.As<DFace>(own[BspLump.Faces]).ToArray();
        DPlane[] ownPlanes = BspStructView.As<DPlane>(own[BspLump.Planes]).ToArray();
        int VeilFace(DFace[] faces, DPlane[] planes, DModel model, Vec3 normal) =>
            Enumerable.Range(model.FirstFace, model.NumFaces).Single(f => planes[faces[f].PlaneNum].Normal == normal);
        DFace ownVeil = ownFaces[VeilFace(ownFaces, ownPlanes, ownModels[1], new Vec3(-1, 0, 0))];
        DFace ownPillar = ownFaces.Where((f, i) => i < ownModels[0].NumFaces).First(f => (f.PlaneNum >> 1) == (ownVeil.PlaneNum >> 1) && f.PlaneNum != ownVeil.PlaneNum);
        Assert.Equal(ownPillar.TexInfo, ownVeil.TexInfo);

        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level($"other, hub@{rotation}", "other, other"));
        BspData bsp = linked.Bsp;
        DModel[] models = BspStructView.As<DModel>(bsp[BspLump.Models]).ToArray();
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        RoomTransform transform = new(new RoomPlacement("hub", 1, 1, rotation / 90), RoomHarness.Cell);
        Vec3 normal = LevelLinker.ApplyNormal(new Vec3(-1, 0, 0), rotation / 90);
        Placed placed = Named(bsp, "targetname", "veil");
        Assert.Equal(transform.Apply(Vec3.Zero), placed.Origin);

        DFace linkedVeil = faces[VeilFace(faces, planes, models[placed.Model], normal)];
        DPlane veilPlane = planes[linkedVeil.PlaneNum];
        Assert.Equal(-80f, veilPlane.Dist);

        // The pillar's face on the same plane, moved with the world.
        Vec3 onPillar = transform.Apply(new Vec3(80, 120, 40));
        DFace linkedPillar = faces[..models[0].NumFaces].First(f =>
            planes[f.PlaneNum].Normal == -normal && MathF.Abs(Vec3.Dot(planes[f.PlaneNum].Normal, onPillar) - planes[f.PlaneNum].Dist) < 0.01f);
        Assert.NotEqual(linkedPillar.PlaneNum >> 1, linkedVeil.PlaneNum >> 1);
        Assert.NotEqual(linkedPillar.TexInfo, linkedVeil.TexInfo);

        // The entity's texinfo is the room's turned and not moved: every
        // vertex keeps its texture coordinate in the entity's frame.
        TexInfo local = infos[linkedVeil.TexInfo];
        TexInfo room = LevelLinker.RotateTexInfos([BspStructView.As<TexInfo>(own[BspLump.TexInfo])[ownVeil.TexInfo]], rotation / 90)[0];
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(room.TextureVecsTexelsPerWorldUnits[i] + 0f, local.TextureVecsTexelsPerWorldUnits[i] + 0f);
        }
    }

    // ---- doors in doorways ----------------------------------------------------------------------

    /// <summary>
    /// A door hung in a doorway (a <c>func_door</c> filling the socket's plug
    /// box, socket furniture on it) and a trigger just inside another
    /// doorway: at a joint the world's plug is stripped and the doorway
    /// carved open, while the door and the trigger stay their own models,
    /// whole, with their brushes; the door blocks a trace through the
    /// doorway, the world does not. The census never takes a brush entity
    /// for the plug.
    /// </summary>
    [Fact]
    public async Task ADoorInADoorwayStaysItsOwnModelWhenTheJointOpensTheWall()
    {
        RoomDefinition definition = RoomPropHarness.Hub;
        Box east = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "east"), RoomHarness.Cell);
        Box north = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "north"), RoomHarness.Cell);
        VmfDocument library = RoomPropHarness.Library(
            (0, Door(700, east.Mins, east.Maxs, ("targetname", "hub_door"), (RoomStaticProps.SocketKey, "east"))),
            (0, Trigger(701, north.Mins + new Vec3(4, 0, 4), north.Maxs - new Vec3(4, 0, 4), ("targetname", "hub_trigger"))));
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("other", "hub");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"));
        BspData bsp = linked.Bsp;

        // The world is open through the east doorway: the plug is stripped.
        Vec3 inside = RoomHarness.PlugCentre(definition, new RoomPlacement("hub", 0, 0, 0), "east");
        Assert.Equal(0, RoomHarness.LeafAt(bsp, inside).Contents & (int)SourceSharp.MapTools.Materials.BrushContents.Solid);
        Assert.Equal(1f, new WorldBrushTrace(bsp, ModelBrushes(bsp, 0)).Trace(inside - new Vec3(40, 0, 0), inside + new Vec3(40, 0, 0), Vec3.Zero).Fraction);

        // The door is its own model, whole, and blocks the doorway.
        Placed door = Named(bsp, "targetname", "hub_door");
        Assert.Single(ModelBrushes(bsp, door.Model));
        Assert.True(Traces(bsp, door).All(h => !h.StartsWith("1.000", StringComparison.Ordinal)));
        Assert.True(new WorldBrushTrace(bsp, ModelBrushes(bsp, door.Model)).Trace(inside - new Vec3(40, 0, 0), inside + new Vec3(40, 0, 0), Vec3.Zero).Fraction < 1f);
        Assert.Null(door.Entity.Get(RoomStaticProps.SocketKey));
        Placed trigger = Named(bsp, "targetname", "hub_trigger");
        Assert.Single(ModelBrushes(bsp, trigger.Model));

        // With the north socket jointed, the trigger inside that doorway
        // stays, whole: its brush, and its faces drawn as they were (the
        // plug's faces, not the trigger's, turn nodraw).
        LinkedLevel both = await RoomPropHarness.LinkAsync(rooms, level);
        Placed kept = Named(both.Bsp, "targetname", "hub_trigger");
        Assert.Single(ModelBrushes(both.Bsp, kept.Model));
        DModel triggerModel = BspStructView.As<DModel>(both.Bsp[BspLump.Models])[kept.Model];
        DFace[] drawn = BspStructView.As<DFace>(both.Bsp[BspLump.Faces]).ToArray();
        DFace[] original = BspStructView.As<DFace>(both.Bsp[BspLump.OriginalFaces]).ToArray();
        TexInfo[] infos = BspStructView.As<TexInfo>(both.Bsp[BspLump.TexInfo]).ToArray();
        for (int f = triggerModel.FirstFace; f < triggerModel.FirstFace + triggerModel.NumFaces; f++)
        {
            Assert.Equal(0, infos[drawn[f].TexInfo].Flags & (int)SourceSharp.MapTools.Materials.SurfaceFlags.NoDraw);
            Assert.Equal(0, infos[original[drawn[f].OrigFace].TexInfo].Flags & (int)SourceSharp.MapTools.Materials.SurfaceFlags.NoDraw);
        }

        Assert.Null(BrushEntities(both.Bsp).FirstOrDefault(p => p.Entity.Get("targetname") == "hub_door"));
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(both.Bsp));
    }

    // ---- socket furniture ----------------------------------------------------------------------

    /// <summary>
    /// Socket furniture among brush entities (open point O5, extended from
    /// static props): of two rooms' doors hung in the doorway they share,
    /// the earlier room's is kept and the other's model and entity are
    /// omitted; a higher <c>socket_priority</c> wins instead, and a side's
    /// priority is the highest of its pieces, props and brush entities
    /// together; at a cap the door is omitted. The flattened level keeps the
    /// same pieces.
    /// </summary>
    [Fact]
    public async Task SocketFurnitureDoorsAreKeptOnOneSideOfAJointAndOmittedAtACap()
    {
        RoomDefinition definition = RoomPropHarness.Hub;
        Box east = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "east"), RoomHarness.Cell);
        Box west = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "west"), RoomHarness.Cell);
        VmfChunk hubDoor = Door(700, east.Mins, east.Maxs, ("targetname", "hub_door"), (RoomStaticProps.SocketKey, "east"));
        VmfChunk otherDoor = Door(710, west.Mins, west.Maxs, ("targetname", "other_door"), (RoomStaticProps.SocketKey, "west"));
        LevelGrid level = RoomPropHarness.Level("hub, other");

        VmfDocument library = RoomPropHarness.Library((0, hubDoor), (1, otherDoor));
        LinkedLevel linked = await RoomPropHarness.LinkAsync(await CompileAsync(library), level);
        Assert.Equal(["hub_door"], BrushEntities(linked.Bsp).Select(p => p.Entity.Get("targetname")!));
        Assert.Equal(2, BspStructView.Count<DModel>(linked.Bsp[BspLump.Models]));
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));

        // The other side's higher priority takes the doorway.
        otherDoor.AddKey(RoomStaticProps.PriorityKey, "5");
        library = RoomPropHarness.Library((0, hubDoor), (1, otherDoor));
        linked = await RoomPropHarness.LinkAsync(await CompileAsync(library), level);
        Assert.Equal(["other_door"], BrushEntities(linked.Bsp).Select(p => p.Entity.Get("targetname")!));
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(linked.Bsp));

        // A frame prop on the hub's side with a higher priority still: the
        // hub's side keeps both its pieces, the frame and the door.
        VmfChunk frame = RoomPropHarness.Prop(720, RoomPropHarness.BarModel, new Vec3(236, 128, 100), "0 90 0",
            (RoomStaticProps.SocketKey, "east"), (RoomStaticProps.PriorityKey, "9"));
        library = RoomPropHarness.Library((0, hubDoor), (0, frame), (1, otherDoor));
        linked = await RoomPropHarness.LinkAsync(await CompileAsync(library), level);
        Assert.Equal(["hub_door"], BrushEntities(linked.Bsp).Select(p => p.Entity.Get("targetname")!));
        Assert.Single(RoomPropHarness.Props(linked.Bsp).Props);
        BspData flat = await CompileFlatAsync(library, level);
        Assert.Equal(Observed(flat), Observed(linked.Bsp));
        Assert.Equal(RoomPropHarness.Observed(flat), RoomPropHarness.Observed(linked.Bsp));

        // Alone, every socket is capped: the door is omitted.
        linked = await RoomPropHarness.LinkAsync(await CompileAsync(RoomPropHarness.Library((0, hubDoor))), RoomPropHarness.Level("hub"));
        Assert.Empty(BrushEntities(linked.Bsp));
        Assert.Equal(1, BspStructView.Count<DModel>(linked.Bsp[BspLump.Models]));
    }

    // ---- (c) model omission ----------------------------------------------------------------------

    /// <summary>
    /// <c>room_needs</c> on a brush entity (the (c) mechanism, 5.8): where
    /// its condition fails for the placement, the entity and its whole model
    /// are omitted (its model, nodes, leaves, leaf faces, faces with their
    /// face ids and vertex-normal index runs, brushes and brush sides, and
    /// its collision record), and the models after it are renumbered, so
    /// the level's tree, faces and brushes are exactly those of the same
    /// level of rooms that never had it; where it holds the model is
    /// linked. The flattened level agrees, at every rotation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task RoomNeedsOmitsABrushEntitysModelAtEveryRotation(int rotation)
    {
        (int, VmfChunk) needy = (0, Door(700, new Vec3(100, 100, 16), new Vec3(132, 132, 64), ("targetname", "hub_door"), (RoomNeeds.Key, "east")));
        (int, VmfChunk)[] rest =
        [
            (0, Rotating(701, new Vec3(40, 40, 16), new Vec3(56, 90, 100), new Vec3(44, 44, 20), ("targetname", "hub_hinged"))),
            (1, Trigger(710, new Vec3(40, 40, 16), new Vec3(90, 90, 64), ("targetname", "other_trigger"))),
        ];
        VmfDocument library = RoomPropHarness.Library([needy, .. rest]);
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other");
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData bsp = linked.Bsp;

        // The hub's authored east is world east only unturned, and only then
        // does a room stand there.
        bool kept = rotation == 0;
        Assert.Equal(kept, BrushEntities(bsp).Any(p => p.Entity.Get("targetname") == "hub_door"));
        Assert.Equal(kept ? 4 : 3, BspStructView.Count<DModel>(bsp[BspLump.Models]));
        Assert.Equal(["hub_hinged", "other_trigger"], BrushEntities(bsp).Where(p => p.Entity.Get("targetname") != "hub_door").OrderBy(p => p.Model).Select(p => p.Entity.Get("targetname")!));
        Assert.Equal(Enumerable.Range(1, kept ? 3 : 2), BrushEntities(bsp).Select(p => p.Model).Order());
        Assert.Equal(Observed(await CompileFlatAsync(library, level)), Observed(bsp));
        Assert.Equal(kept ? 3 : 2, PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span).Count(r => r.ModelIndex > 0));
        ValidationReport report = await BspValidator.CheckAsync(bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));

        if (!kept)
        {
            // Omission is absence: every lump the model owned has what the
            // level of rooms without the door has, entry for entry in count.
            LinkedLevel without = await RoomPropHarness.LinkAsync(await CompileAsync(RoomPropHarness.Library(rest)), level);
            foreach (BspLump lump in new[]
            {
                BspLump.Models, BspLump.Nodes, BspLump.Leafs, BspLump.LeafFaces, BspLump.LeafBrushes, BspLump.Faces,
                BspLump.FaceIds, BspLump.VertNormalIndices, BspLump.Brushes, BspLump.BrushSides, BspLump.Visibility, BspLump.PhysCollide,
            })
            {
                Assert.True(without.Bsp[lump].Length == bsp[lump].Length, $"{lump}: {bsp[lump].Length} bytes, {without.Bsp[lump].Length} without the door");
            }

            Assert.Equal(without.Bsp[BspLump.Visibility].Data.ToArray(), bsp[BspLump.Visibility].Data.ToArray());
            Assert.Equal(without.Bsp[BspLump.Models].Data.ToArray(), bsp[BspLump.Models].Data.ToArray());
        }
    }

    // ---- limits and refusals -------------------------------------------------------------------

    /// <summary>
    /// A level whose brush models pass <c>MAX_MAP_MODELS</c> (1024, the
    /// world included) is refused before any room is planned, naming the
    /// placement that crossed the cap.
    /// </summary>
    [Fact]
    public async Task ALevelPastMaxMapModelsIsRefused()
    {
        List<(int, VmfChunk)> crates = [];
        for (int i = 0; i < 40; i++)
        {
            Vec3 at = new(40 + (20 * (i % 8)), 40 + (20 * (i / 8)), 16);
            crates.Add((0, Brush("func_brush", 700 + i, at, at + new Vec3(8, 8, 8))));
        }

        RoomLibrary rooms = await CompileAsync(RoomPropHarness.Library([.. crates]));
        string row = string.Join(", ", Enumerable.Repeat("hub", 6));
        LevelGrid level = RoomPropHarness.Level(row, row, row, row, row);
        LevelLayout layout = RoomPropHarness.Layout(rooms, level);
        RoomPlacement crossing = layout.Rooms[1023 / 40].Placement;
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomPropHarness.LinkAsync(rooms, level));
        Assert.Equal(
            $"room hub at cell ({crossing.CellX}, {crossing.CellY}) pushes the link to 1025 models; the engine loads at most 1024 (MAX_MAP_MODELS).",
            refused.Message);

        // One row fewer fits.
        LinkedLevel fits = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level(row, row, row, row));
        Assert.Equal(1 + (24 * 40), BspStructView.Count<DModel>(fits.Bsp[BspLump.Models]));
    }

    /// <summary>
    /// A room whose compile has brush models besides the world but carries
    /// no brush model data from it (a pack written before the link carried
    /// brush entities) is refused by name; with the data, it links.
    /// </summary>
    [Fact]
    public async Task ARoomWithBrushModelsButNoDataFromItsCompileIsRefused()
    {
        RoomLibrary rooms = await CompileAsync(RoomPropHarness.Library(HubBrushes));
        RoomObject hub = rooms.Get("hub");
        RoomObject bare = hub with { BrushModels = null, Link = null };
        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf(bare, rooms.Get("other")), RoomPropHarness.Level("hub, other")));
        Assert.Equal(
            "room hub has 5 brush entity models but no brush entity data from its compile (a pack written before the link carried brush entities,"
            + " or a room built without ssmap room); recompile the library with ssmap room.",
            refused.Message);

        // Stored link data does not stand in for it.
        RoomObject stale = hub with { BrushModels = hub.BrushModels!.For(new BspData()) };
        Assert.Null(stale.BrushModelsOfCompile);
        await Assert.ThrowsAsync<LinkException>(
            () => RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf(stale, rooms.Get("other")), RoomPropHarness.Level("hub, other")));
    }

    // ---- the pack, determinism, the budget, the loader --------------------------------------------

    /// <summary>
    /// Rooms with brush entities through a pack: the pack stores each room's
    /// brush models (four turns), the rooms it loads carry them, and the
    /// level links to the same bytes as from the rooms in memory; a pack
    /// holding only turn 0 (the rotation count of 1, the link turning it)
    /// links to the same bytes too (the rooms design, 15.9).
    /// </summary>
    [Fact]
    public async Task BrushEntitiesRoundTripThroughAPack()
    {
        RoomLibrary rooms = await CompileAsync(RoomPropHarness.Library(
            [.. HubBrushes, (1, Rotating(710, new Vec3(150, 150, 16), new Vec3(200, 166, 90), new Vec3(196, 156, 20)))]));
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
        Assert.NotNull(index.Find("hub")!.Find(RoomBrushModels.SectionTag));
        Assert.NotNull(index.Find("hub")!.Find(RoomLinkSections.SharedTag));
        IReadOnlyList<RoomObject> loaded = await RoomPack.LoadRoomsAsync(
            pack, index, [new RoomPackRequest("hub", [1, 3]), new RoomPackRequest("other", [0, 2])]);
        Assert.All(loaded, room => Assert.Equal(4, room.BrushModelsOfCompile!.TurnCount));
        Assert.Equal(expected, await BytesAsync(await RoomPropHarness.LinkAsync(RoomPropHarness.RoomsOf([.. loaded]), level)));

        RoomLibrary once = RoomPropHarness.RoomsOf([.. rooms.Rooms.Select(r => r with { BrushModels = r.BrushModels!.WithTurnZeroOnly() })]);
        Assert.Equal(expected, await BytesAsync(await RoomPropHarness.LinkAsync(once, level)));
    }

    /// <summary>A level with brush entities links to the same bytes at one thread and at many, run after run.</summary>
    [Fact]
    public async Task ALevelWithBrushEntitiesIsTheSameBytesAtAnyThreadCount()
    {
        RoomLibrary rooms = await CompileAsync(RoomPropHarness.Library(HubBrushes));
        LevelGrid level = RoomPropHarness.Level("hub@90, other, hub@180", "other@270, hub, other");
        byte[] serial = await BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 1));
        Assert.Equal(serial, await BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 8)));
        Assert.Equal(serial, await BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 1)));
        Assert.Equal(serial, await BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 8)));
    }

    /// <summary>
    /// Brush entities cost the level one entity (and, by the default class
    /// table, one edict) each, less the ones <c>room_needs</c> and the
    /// socket furniture rule omit (the rooms design, 15.6): the budget the
    /// link reports counts exactly the entities its lump holds.
    /// </summary>
    [Fact]
    public async Task BrushEntitiesCostOneEntityEachLessTheOmittedOnes()
    {
        RoomDefinition definition = RoomPropHarness.Hub;
        Box east = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "east"), RoomHarness.Cell);
        Box west = RoomLinter.SealBox(definition, definition.Sockets.First(s => s.Name == "west"), RoomHarness.Cell);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        LinkedLevel bare = await RoomPropHarness.LinkAsync(await CompileAsync(RoomPropHarness.Library()), level);
        LinkedLevel furnished = await RoomPropHarness.LinkAsync(
            await CompileAsync(RoomPropHarness.Library(
                (0, Door(700, east.Mins, east.Maxs, (RoomStaticProps.SocketKey, "east"))),
                (0, Trigger(701, new Vec3(40, 40, 16), new Vec3(90, 90, 64), (RoomNeeds.Key, "west"))),
                (0, Brush("func_brush", 702, new Vec3(60, 160, 16), new Vec3(80, 200, 48))),
                (1, Door(710, west.Mins, west.Maxs, (RoomStaticProps.SocketKey, "west"))))),
            level);

        // Kept: the hub's door, its wall brush; omitted: the other's door
        // (the joint keeps the hub's side) and the trigger (nothing west).
        Assert.Equal(2, BrushEntities(furnished.Bsp).Count);
        Assert.Equal(bare.EntityBudget!.Listed + 2, furnished.EntityBudget!.Listed);
        Assert.Equal(bare.EntityBudget.Edicts + 2, furnished.EntityBudget.Edicts);
        Assert.Equal(EntityLump.Parse(furnished.Bsp[BspLump.Entities]).Count, furnished.EntityBudget.Listed);
    }

    /// <summary>A linked level with brush entities passes the loader checks <c>ssmap check</c> makes.</summary>
    [Fact]
    public async Task ALinkedLevelWithBrushEntitiesPassesTheLoaderChecks()
    {
        RoomLibrary rooms = await CompileAsync(RoomPropHarness.Library(
            [.. HubBrushes, (1, Door(710, new Vec3(100, 100, 16), new Vec3(132, 132, 64), (RoomNeeds.Key, "north")))]));
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub@90, other", "other, hub@180"));
        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>Rooms compiled without a cooker link their brush entities with no collision records, as they had none.</summary>
    [Fact]
    public async Task RoomsWithoutACookerLinkBrushEntitiesWithoutCollision()
    {
        VmfDocument library = RoomPropHarness.Library(HubBrushes);
        RoomLibrary rooms = await RoomPropHarness.CompileAsync(library);
        Assert.All(rooms.Get("hub").BrushModelsOfCompile!.Models, m => Assert.Null(m.KeyData));
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub@90, other"));
        Assert.Equal(0, linked.Bsp[BspLump.PhysCollide].Length);
        Assert.Equal(6, BspStructView.Count<DModel>(linked.Bsp[BspLump.Models]));
    }

    /// <summary>
    /// A room's collision lump with brush model records: the link's world
    /// merge reads the world's record and leaves the models' to the models;
    /// a lump whose first record is not the world's, or with a record for a
    /// model the room does not have, or two for one model, is refused as
    /// before.
    /// </summary>
    [Fact]
    public async Task AWorldCollisionWithBrushModelRecordsReadsTheWorlds()
    {
        RoomObject hub = (await CompileAsync(RoomPropHarness.Library(HubBrushes[..2]))).Get("hub");
        IReadOnlyList<PhysCollideModel> records = PhysCollideLump.Read(hub.Bsp[BspLump.PhysCollide].Data.Span);
        Assert.Equal([0, 1, 2], records.Select(r => r.ModelIndex));
        Assert.Equal(records[0].Solids.Count, LevelLinker.ReadRoomCollide(hub.Bsp, "hub").Solids.Count);

        foreach (PhysCollideModel[] bad in new[]
        {
            new[] { records[1], records[0] },
            new[] { records[0], records[1] with { ModelIndex = 3 } },
            new[] { records[0], records[1], records[1] },
        })
        {
            RoomObject edited = RoomHarness.WithLumps(hub, b => b.SetLump(BspLump.PhysCollide, PhysCollideLump.Write(bad)));
            LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.ReadRoomCollide(edited.Bsp, "hub"));
            Assert.Equal($"room hub's world collision has {bad.Length} records; a linkable room has one, for model 0", refused.Message);
        }
    }

    private static HashSet<int> Reached(BspData bsp, int head)
    {
        DNode[] nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        HashSet<int> reached = [];
        Stack<int> pending = new([head]);
        while (pending.Count > 0)
        {
            int at = pending.Pop();
            if (!reached.Add(at) || at < 0)
            {
                continue;
            }

            pending.Push(nodes[at].Children[0]);
            pending.Push(nodes[at].Children[1]);
        }

        return reached;
    }

    private static async Task<byte[]> BytesAsync(LinkedLevel linked)
    {
        using MemoryStream bytes = new();
        await BspFile.SaveAsync(linked.Bsp, bytes, BspWriteMode.Canonical);
        return bytes.ToArray();
    }
}
