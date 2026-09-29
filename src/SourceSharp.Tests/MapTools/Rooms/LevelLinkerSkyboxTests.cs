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

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;

using static SourceSharp.Tests.MapTools.Rooms.RoomSkyboxHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The 3D skybox (section 4.12 of the rooms design, open point O11): a
/// library's <c>info_room_skybox</c> room, compiled once like a socket-less
/// room, is placed by the link and the flatten below every level's grid,
/// never turned and never joined, as its own area, with its
/// <c>sky_camera</c>; the level's world bounds leave it out.
/// </summary>
public sealed class LevelLinkerSkyboxTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// A level of the hub and the other room, at every quarter turn, with
    /// the library's skybox: the linked map holds the skybox one cell below
    /// the south-west cell, unturned, its space an area of its own after
    /// the rooms' one, and its <c>sky_camera</c> moved there; the flattened
    /// level's compile makes the same partition of the open space (skybox
    /// included), carries the same camera, and the same world bounds, which
    /// leave the skybox out; the skybox's clusters see only one another; and
    /// the map passes the loader checks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALevelCarriesTheSkyboxBelowItsGrid(int rotation)
    {
        VmfDocument library = Library();
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        Assert.Equal(3, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Assert.Equal(RoomAreaPortalHarness.Areas(flat).Length, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Assert.Single(RoomAreaPortalHarness.Listings(linked.Bsp));

        Vec3 inside = new(128 + 100, 128, -128);
        DLeaf skyLeaf = RoomHarness.LeafAt(linked.Bsp, inside);
        Assert.Equal(0, skyLeaf.Contents & (int)BrushContents.Solid);
        Assert.Equal(2, skyLeaf.GetArea());
        Assert.Equal(1, RoomHarness.LeafAt(linked.Bsp, new Vec3(128, 128, 60)).GetArea());
        Dictionary<int, int> names = Partition(linked.Bsp, flat, level);
        Assert.Equal(2, names.Count);

        Assert.Equal(["angles=0 0 0 | scale=16 | origin=128 128 -128 | classname=sky_camera"], OfClass(linked.Bsp, "sky_camera"));
        Assert.Equal(OfClass(flat, "sky_camera"), OfClass(linked.Bsp, "sky_camera"));
        Assert.Equal(World(flat, "world_mins"), World(linked.Bsp, "world_mins"));
        Assert.Equal(World(flat, "world_maxs"), World(linked.Bsp, "world_maxs"));
        Assert.DoesNotContain("-", World(linked.Bsp, "world_mins")!, StringComparison.Ordinal);

        // The skybox's clusters see one another and nothing of the rooms'.
        short cluster = skyLeaf.Cluster;
        short room = RoomHarness.LeafAt(linked.Bsp, new Vec3(128, 128, 60)).Cluster;
        ReadOnlySpan<byte> row = linked.Vis.Pvs(cluster);
        Assert.True((row[cluster >> 3] & (1 << (cluster & 7))) != 0);
        Assert.Equal(0, row[room >> 3] & (1 << (room & 7)));

        ValidationReport report = await BspValidator.CheckAsync(linked.Bsp, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("\n", report.Diagnostics));
    }

    /// <summary>
    /// The linked and the flattened maps put every sample point of the
    /// rooms' cells and of the skybox's in open space alike, and in areas
    /// that name each other one to one.
    /// </summary>
    private static Dictionary<int, int> Partition(BspData linked, BspData flat, LevelGrid level)
    {
        Dictionary<int, int> names = RoomAreaPortalHarness.SamePartition(linked, flat, level);
        foreach (Vec3 point in SkyboxSamples(0, 0))
        {
            DLeaf a = RoomHarness.LeafAt(linked, point);
            DLeaf b = RoomHarness.LeafAt(flat, point);
            bool solidA = (a.Contents & (int)BrushContents.Solid) != 0;
            Assert.True(solidA == ((b.Contents & (int)BrushContents.Solid) != 0), $"at {point} the maps disagree on solid");
            if (!solidA)
            {
                Assert.True(!names.TryGetValue(a.GetArea(), out int seen) || seen == b.GetArea(), $"at {point} the areas disagree");
                names[a.GetArea()] = b.GetArea();
            }
        }

        Assert.Equal(names.Count, names.Values.Distinct().Count());
        return names;
    }

    /// <summary>
    /// The skybox joins the level's other features: with an area portal
    /// room and door portals on, its area follows every room's and the
    /// portals', and the level links as it flattens; its entities are
    /// written after every room's and before the door portals', and
    /// counted once in the level's budget, which counts exactly the lump.
    /// </summary>
    [Fact]
    public async Task TheSkyboxJoinsAreaPortalsAndDoorPortals()
    {
        VmfDocument library = RoomAreaPortalHarness.WithDoorPortals(RoomAreaPortalHarness.Library());
        VmfChunk world = library.GetChunk(SourceSharp.MapTools.Bsp.MapFileLoader.WorldChunk)!;
        Vec3 corner = new(2 * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0);
        QuarterTurn move = QuarterTurn.Translation(corner);
        VmfDocument shell = RoomModel.Build(Definition, RoomHarness.WalkableKit.Depth, RoomLightHarness.Sky);
        foreach (VmfChunk solid in shell.GetChunk(SourceSharp.MapTools.Bsp.MapFileLoader.WorldChunk)!.GetChunks(SourceSharp.MapTools.Bsp.MapFileLoader.SolidChunk))
        {
            world.Children.Add(VmfPlacement.MoveSolid(solid, move));
        }

        library.Chunks.Add(Marker(Name, corner));
        library.Chunks.Add(VmfPlacement.MoveEntity(SkyCamera(), move));
        LevelGrid level = RoomPropHarness.Level("hub, split, hub");
        RoomLibrary rooms = await CompileAsync(library);
        LinkedLevel linked = await RoomPropHarness.LinkAsync(rooms, level);
        BspData flat = await CompileFlatAsync(library, level);

        // Hub, split west, split east, hub, then the skybox.
        Assert.Equal(6, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Assert.Equal(5, RoomHarness.LeafAt(linked.Bsp, new Vec3(128, 128, -128)).GetArea());
        Assert.Equal(RoomAreaPortalHarness.Areas(flat).Length, RoomAreaPortalHarness.Areas(linked.Bsp).Length);
        Dictionary<int, int> names = Partition(linked.Bsp, flat, level);
        Assert.Equal(5, names.Count);
        Assert.Equal(RoomAreaPortalHarness.Portals(flat), RoomAreaPortalHarness.Portals(linked.Bsp, names));
        Assert.Equal(RoomAreaPortalHarness.PortalEntities(flat), RoomAreaPortalHarness.PortalEntities(linked.Bsp));

        List<BspEntity> entities = EntityLump.Parse(linked.Bsp[BspLump.Entities]);
        int camera = entities.FindIndex(e => e.ClassName == "sky_camera");
        Assert.True(camera > entities.FindLastIndex(e => e.ClassName == "info_player_start"));
        Assert.True(camera < entities.FindIndex(e => e.ClassName == "func_areaportal" && e.Get("hammerid") is null));
        Assert.Equal(entities.Count, linked.EntityBudget!.Listed);
    }

    /// <summary>
    /// A level of the library with a skybox links to the same bytes at one
    /// thread and at many; a library without one links the same level as
    /// ever (its areas the first room's, no root below the grid).
    /// </summary>
    [Fact]
    public async Task ALevelWithTheSkyboxIsDeterministic()
    {
        RoomLibrary rooms = await CompileAsync(Library());
        LevelGrid level = RoomPropHarness.Level("hub@90, other", "other@180, hub@270");
        byte[] one = await RoomAreaPortalHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 1));
        Assert.Equal(one, await RoomAreaPortalHarness.BytesAsync(await RoomPropHarness.LinkAsync(rooms, level, 8)));

        rooms.SkyboxRoom = null;
        LinkedLevel plain = await RoomPropHarness.LinkAsync(rooms, level);
        Assert.Equal(rooms.Get("hub").Bsp[BspLump.Areas].Data.ToArray(), plain.Bsp[BspLump.Areas].Data.ToArray());
        Assert.DoesNotContain(EntityLump.Parse(plain.Bsp[BspLump.Entities]), e => e.ClassName == "sky_camera");
    }

    /// <summary>
    /// No level places the skybox room: the link and the flatten refuse one
    /// that does, naming the level and the cell; and the link refuses a
    /// library that names a skybox it does not hold.
    /// </summary>
    [Fact]
    public async Task ALevelThatPlacesTheSkyboxIsRefused()
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, sky");
        const string Message = "level props places the skybox room sky at cell (1, 0); the link places the skybox below the grid itself.";
        Assert.Equal(Message, (await Assert.ThrowsAsync<LinkException>(() => RoomPropHarness.LinkAsync(rooms, level))).Message);
        Assert.Equal(Message, Assert.Throws<LinkException>(() => LevelFlattener.Flatten(level, library)).Message);

        rooms.SkyboxRoom = "nowhere";
        Assert.Equal(
            "the room library has no room \"nowhere\".",
            (await Assert.ThrowsAsync<LinkException>(() => RoomPropHarness.LinkAsync(rooms, RoomPropHarness.Level("hub")))).Message);
    }

    // ---- the split -----------------------------------------------------------------------------

    /// <summary>
    /// The split sets the skybox room apart: not one of the rooms, its
    /// document room-local with its camera and block, its definition the
    /// library's grid and kit with no socket; a library without a marker
    /// has none.
    /// </summary>
    [Fact]
    public void TheSplitSetsTheSkyboxApart()
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(Library());
        Assert.Equal(["hub", "other"], split.Rooms.Select(r => r.Definition.Name));
        LibraryRoom sky = split.Skybox!;
        Assert.Equal(Name, sky.Definition.Name);
        Assert.Empty(sky.Definition.Sockets);
        Assert.Equal(RoomHarness.Cell, sky.Definition.CellSize);
        Assert.Equal(Corner, sky.Corner);
        VmfChunk camera = Assert.Single(sky.Document.GetChunks(SourceSharp.MapTools.Bsp.MapFileLoader.EntityChunk));
        Assert.Equal("128 128 128", camera.GetValue("origin"));
        Assert.Null(RoomLibraryVmf.SplitLibrary(RoomPropHarness.Library()).Skybox);
    }

    /// <summary>
    /// The skybox room's rules, each refused by the split with its message:
    /// one marker at most, a name, a <c>sky_camera</c> exactly once, no
    /// socket, no <c>room_needs</c> or <c>room_socket</c>; a
    /// <c>sky_camera</c> in an ordinary room is still refused.
    /// </summary>
    [Fact]
    public void TheSkyboxsRulesAreHeld()
    {
        VmfDocument two = Library();
        two.Chunks.Add(Marker("sky2", new Vec3(3 * (RoomHarness.Cell + RoomHarness.LibraryGap), 0, 0)));
        Assert.Equal("the library has 2 info_room_skybox entities; a library has one skybox room at most.", Refused(two));

        VmfDocument unnamed = Library();
        VmfChunk marker = unnamed.Chunks.First(c => c.GetValue("classname") == RoomLibraryVmf.SkyboxEntity);
        marker.Children.Remove(marker.Keys.First(k => k.Name == RoomLibraryVmf.NameKey));
        Assert.Equal("the info_room_skybox at (640 0 0) has no \"name\"; the skybox room is named like any room.", Refused(unnamed));

        Assert.Equal("the skybox room \"sky\" has 0 sky_camera entities; the engine draws a skybox from exactly one.", Refused(Library(camera: false)));
        Assert.Equal(
            "the skybox room \"sky\" has 2 sky_camera entities; the engine draws a skybox from exactly one.",
            Refused(Library(true, SkyCamera(700601, new Vec3(64, 200, 64)))));

        VmfDocument plugged = Library();
        RoomDefinition probe = new("probe", RoomHarness.Cell, RoomHarness.WalkableKit, [new RoomSocket(RoomFacing.PositiveX, "east")]);
        Box plug = RoomLinter.SealBox(probe, probe.Sockets[0], RoomHarness.Cell);
        plugged.GetChunk(SourceSharp.MapTools.Bsp.MapFileLoader.WorldChunk)!.Children.Add(
            VmfPlacement.MoveSolid(RoomModel.Slab(RoomHarness.Trigger, plug.Mins, plug.Maxs, 7201), QuarterTurn.Translation(Corner)));
        Assert.Equal("the skybox room \"sky\" has a door plug on its east wall; the skybox is never joined, so it has no sockets.", Refused(plugged));

        Assert.Equal(
            "room sky: entity 700602 (info_target) has room_needs, but the skybox room has no neighbours and no sockets.",
            Refused(Library(true, RoomPropHarness.Entity("info_target", 700602, new Vec3(64, 64, 64), ("room_needs", "east")))));
        Assert.Equal(
            "room sky: entity 700603 (info_target) has room_socket, but the skybox room has no neighbours and no sockets.",
            Refused(Library(true, RoomPropHarness.Entity("info_target", 700603, new Vec3(64, 64, 64), ("room_socket", "east")))));

        VmfDocument inRoom = Library();
        inRoom.Chunks.Add(SkyCamera(700604, new Vec3(100, 100, 100)));
        Assert.Equal("room hub: sky_camera is allowed only in the library's skybox room.", Refused(inRoom));

        static string Refused(VmfDocument library) =>
            Assert.Throws<RoomLibraryException>(() => RoomLibraryVmf.SplitLibrary(library)).Message;
    }

    // ---- the pack's section and the transform --------------------------------------------------

    /// <summary>
    /// The skybox section reads back the name it wrote; a revision this
    /// build does not read is no skybox; a section that is damaged, or names
    /// something that is not a room name, is refused.
    /// </summary>
    [Fact]
    public void TheSkyboxSectionRoundTripsAndRefusesWhatDoesNotFit()
    {
        byte[] section = RoomLibrarySkybox.ToSection("sky").Bytes.ToArray();
        Assert.Equal(RoomLibrarySkybox.SectionTag, RoomLibrarySkybox.ToSection("sky").Tag);
        Assert.Equal("sky", RoomLibrarySkybox.Read(section));

        byte[] revised = [.. section];
        revised[12] = 9;
        Assert.Null(RoomLibrarySkybox.Read(revised));

        Assert.Equal("the room pack's SKYB section is 3 bytes, shorter than its header.", Refused([0, 0, 0]));
        byte[] codec = [.. section];
        codec[0] = 1;
        Assert.Equal("the room pack's SKYB section has codec 1; this build reads codec 0 (none).", Refused(codec));
        Assert.Equal("the room pack's SKYB section says 11 bytes but holds 12.", Refused([.. section, 0]));
        byte[] longer = [.. section, 0];
        longer[8] = 12;
        Assert.Equal("the room pack's SKYB section has 1 bytes after its name.", Refused(longer));
        byte[] shorter = [.. section[..^1]];
        shorter[8] = 10;
        Assert.Equal("the room pack's SKYB section is cut short.", Refused(shorter));
        byte[] bare = [.. section[..13]];
        bare[8] = 4;
        Assert.Equal("the room pack's SKYB section is cut short.", Refused(bare));
        Assert.StartsWith("the room pack's SKYB section names \"a b\", which ", Refused(RoomLibrarySkybox.ToSection("a b").Bytes.ToArray()), StringComparison.Ordinal);

        static string Refused(byte[] bytes) => Assert.Throws<LinkException>(() => RoomLibrarySkybox.Read(bytes)).Message;
    }

    /// <summary>
    /// A placement below the grid moves every height down a whole number of
    /// cells, in the transform, its inverse and its translation half; one on
    /// the grid's level leaves a height's bits alone (a negative zero
    /// included); the skybox stands below the level's south-west cell.
    /// </summary>
    [Fact]
    public void APlacementBelowTheGridMovesItsHeights()
    {
        RoomTransform below = new(new RoomPlacement("sky", 2, 3, 0) { Level = -1 }, 256);
        Assert.Equal(new Vec3(522, 778, -246), below.Apply(new Vec3(10, 10, 10)));
        Assert.Equal(new Vec3(10, 10, 10), below.Unapply(new Vec3(522, 778, -246)));
        Assert.Equal(new Vec3(522, 778, -246), below.Translate(new Vec3(10, 10, 10)));
        RoomTransform onGrid = new(new RoomPlacement("hub", 2, 3, 1), 256);
        Assert.True(float.IsNegative(onGrid.Apply(new Vec3(1, 1, -0f)).Z));
        Assert.True(float.IsNegative(onGrid.Translate(new Vec3(1, 1, -0f)).Z));
        Assert.True(float.IsNegative(onGrid.Unapply(new Vec3(1, 1, -0f)).Z));

        LevelLayout layout = RoomPropHarness.Level("hub, other", "other, hub").ToLayout(
            n => n == "hub" ? RoomPropHarness.Hub : RoomPropHarness.Other, RoomHarness.Cell, RoomHarness.WalkableKit);
        Assert.Equal(new RoomPlacement("sky", 0, 0, 0) { Level = -1 }, LevelLinker.SkyboxPlacement(layout, "sky"));
    }
}
