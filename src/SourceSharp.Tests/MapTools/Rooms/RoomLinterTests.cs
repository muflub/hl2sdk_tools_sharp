//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;
using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The seal census: the linter's verdict that every socket's plug is really
/// there, really solid, and really the door hardware — a solid leaf of the
/// compile overlapping the kit's plug box and carrying a trigger-brushed side.
/// </summary>
/// <remarks>
/// <para>
/// Geometry alone was the first draft's rule, and it passed the shell for a
/// plug the compile had thrown away (the plug leaves of a sealed compile are
/// solid and cluster -1 — <c>BuildVisLeafList</c>,
/// never numbers a solid leaf, so no cluster number can witness a plug). The
/// trigger surface flag is the witness: <c>%compileTrigger</c> sets
/// <c>SURF_TRIGGER</c>, and the plug's
/// <c>CONTENTS_SOLID</c> is the settled plug semantics.
/// </para>
/// <para>
/// Guarantee 1 (brushes inside own cells) is a MODEL check: vbsp's own void
/// shell — the solid leaves the block grid carves around the map
/// (<c>BlockGrid.cs:181</c>) — hangs solid geometry outside the cell of every
/// map that compiles, so a compiled-leaf version of the rule refuses every
/// legal room.
/// </para>
/// </remarks>
public class RoomLinterTests
{
    [Fact]
    public async Task AGoodRoomPassesTheSealCensus()
    {
        // The good path, which the pre-census code refused: a sealed two-socket
        // room always has vbsp's void leaves solid outside the cell — the reference
        // shows them at (-8,...) and past 1032 — and the old compiled-leaf rule
        // 1 threw on exactly those. The census must pass it, and name both
        // sockets, in socket order, as sealed.
        RoomDefinition room = RoomHarness.Room("good", RoomFacing.PositiveX, RoomFacing.NegativeY);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomObject obj = await RoomCompiler.CompileAsync(RoomHarness.BuildRoomModel(room), room, context, default);

        Assert.Equal([0, 1], obj.Lint.SealClusters);
        Assert.NotEmpty(obj.Lint.InteriorClusters);
        Assert.Equal(
            [RoomRule.BrushesInsideOwnCells, RoomRule.ShellSealedExceptAtSockets, RoomRule.InteriorCannotEscape, RoomRule.SocketsFromFixedKit],
            obj.Lint.Rules);
    }

    [Fact]
    public async Task APlugThatIsNotATriggerIsRefusedAsUnsealed()
    {
        // Same geometry, plain material: the compile seals (the shell-with.
        // plain-plug probe proves the flood never escapes) and every seal box
        // holds a solid leaf — but no side carries SURF_TRIGGER, so the census
        // finds no plug and refuses the room before any link.
        RoomDefinition room = RoomHarness.Room("plainplug", RoomFacing.PositiveX);
        VbspContext context = await RoomHarness.ContextAsync();
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(() => RoomCompiler
            .CompileAsync(RoomHarness.BuildRoomModel(room, plug: RoomHarness.Plain), room, context, default));

        Assert.Equal(
            "rule 4 (SocketsFromFixedKit): room plainplug: 1 sockets, but only 0"
            + " of them were sealed by a trigger plug — a plug is missing or is not trigger-brushed.",
            error.Message);
    }

    [Fact]
    public async Task APlugMissingAtOneSocketOfTwoIsRefusedNamingTheCount()
    {
        // One trigger plug, one plain plug: the room stays sealed (both boxes
        // are solid) and the census passes socket 0, so only the per-box
        // existence form catches the missing one — and the count it reports is
        // the number of sealed boxes, not the number of solid leaves.
        RoomDefinition room = RoomHarness.Room("mixed", RoomFacing.PositiveX, RoomFacing.NegativeY);
        VmfDocument document = RoomHarness.BuildRoomModel(room);

        // The plugs are the world solids whose every side is the trigger
        // material, in socket order (the model emits them last, in that order);
        // demote the second one to plain.
        List<VmfChunk> plugs = [];
        foreach (VmfChunk solid in document.Chunks[0].Chunks)
        {
            bool allTrigger = true;
            bool anyTrigger = false;
            foreach (VmfChunk side in solid.Chunks)
            {
                if (string.Equals(side.GetValue("material"), RoomHarness.Trigger, StringComparison.Ordinal))
                {
                    anyTrigger = true;
                }
                else
                {
                    allTrigger = false;
                }
            }

            if (anyTrigger && allTrigger)
            {
                plugs.Add(solid);
            }
        }

        Assert.Equal(2, plugs.Count);
        foreach (VmfChunk side in plugs[1].Chunks)
        {
            foreach (VmfKey key in side.Keys)
            {
                if (string.Equals(key.Name, "material", StringComparison.Ordinal))
                {
                    key.Value = RoomHarness.Plain;
                }
            }
        }

        VbspContext context = await RoomHarness.ContextAsync();
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(document, room, context, default));

        Assert.Equal(
            "rule 4 (SocketsFromFixedKit): room mixed: 2 sockets, but only 1"
            + " of them were sealed by a trigger plug — a plug is missing or is not trigger-brushed.",
            error.Message);
    }

    /// <summary>
    /// A door plug on a wall the room has no socket on is a trigger brush
    /// that is not a declared plug: the model check names it and the kit it
    /// should have been, before the compile is paid for, where the census
    /// alone would have passed the room with an extra plug in a doorway.
    /// </summary>
    [Fact]
    public async Task ATriggerBrushThatIsNotADeclaredPlugIsRefusedByTheModel()
    {
        RoomDefinition declared = RoomHarness.Room("extra", RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(RoomHarness.Room("extra", RoomFacing.PositiveX, RoomFacing.NegativeX));

        VbspContext context = await RoomHarness.ContextAsync();
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(document, declared, context, default));

        Assert.Equal(
            "rule 4 (SocketsFromFixedKit): room extra has a door-plug (trigger) brush at (0 80 80)-(16 176 176)"
            + " that is not the kit's plug on any of its socket walls: a plug fills the 96 by 96 opening centred"
            + " on its wall, 16 deep.",
            error.Message);
    }

    /// <summary>
    /// A room with no brushes at all is refused by the model check, before
    /// the compile, with the room named. Before the check its vbsp threw an
    /// <see cref="ArgumentOutOfRangeException"/> from the world bounds.
    /// </summary>
    [Fact]
    public async Task ARoomWithNoBrushesIsRefusedBeforeTheCompile()
    {
        RoomDefinition room = RoomHarness.Room("void");
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        VbspContext context = await RoomHarness.ContextAsync();
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(document, room, context, default));

        Assert.Equal(
            "rule 2 (ShellSealedExceptAtSockets): room void has no world brushes; a room is a shell of world brushes"
            + " around its cell, and a compile of none has no world to build.",
            error.Message);
    }

    /// <summary>
    /// A room whose only brushes belong to a brush entity is refused the same
    /// way: vbsp would compile it with the brush entity as model 0, which is
    /// no shell and not a room the link can place.
    /// </summary>
    [Fact]
    public async Task ARoomWhoseOnlyBrushesAreABrushEntitysIsRefused()
    {
        RoomDefinition room = RoomHarness.Room("props");
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);
        VmfChunk brush = new(MapFileLoader.EntityChunk);
        brush.AddKey("id", "2");
        brush.AddKey("classname", "func_brush");
        brush.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(64, 64, 16), new Vec3(96, 96, 48), 3));
        document.Chunks.Add(brush);

        MapFile map = await MapFileLoader.LoadAsync(await RoomHarness.ContextAsync(), document, CancellationToken.None);
        MapFileReader.TakeBounds(map);

        RoomLintException error = Assert.Throws<RoomLintException>(() => RoomLinter.CheckModel(room, map));
        Assert.StartsWith("rule 2 (ShellSealedExceptAtSockets): room props has no world brushes;", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only solid world brushes can be plugs: a player clip with one
    /// trigger-surfaced side (a world brush with a trigger side that is not
    /// solid) and a trigger brush of a brush entity are not checked against
    /// the kit.
    /// </summary>
    [Fact]
    public async Task OnlySolidWorldTriggerBrushesAreHeldToTheKit()
    {
        RoomDefinition room = RoomHarness.Room("volumes", RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(room);
        VmfChunk clip = RoomModel.Slab(RoomHarness.PlayerClip, new Vec3(64, 64, 16), new Vec3(96, 96, 48), 4243);
        clip.Chunks.Last().Keys.Single(k => k.Name == "material").Value = RoomHarness.Trigger;
        document.Chunks[0].Children.Add(clip);
        VmfChunk trigger = new(MapFileLoader.EntityChunk);
        trigger.AddKey("id", "4244");
        trigger.AddKey("classname", "func_brush");
        trigger.Children.Add(RoomModel.Slab(RoomHarness.Trigger, new Vec3(160, 160, 16), new Vec3(192, 192, 48), 4245));
        document.Chunks.Add(trigger);

        MapFile map = await MapFileLoader.LoadAsync(await RoomHarness.ContextAsync(), document, CancellationToken.None);
        MapFileReader.TakeBounds(map);
        Assert.True(map.Brushes.Count > map.Entities[0].BrushCount, "the func_brush's brush is not an entity brush");

        RoomLinter.CheckModel(room, map);

        // The same solid trigger brush in the world is held to the kit.
        document.Chunks[0].Children.Add(RoomModel.Slab(RoomHarness.Trigger, new Vec3(160, 160, 16), new Vec3(192, 192, 48), 4246));
        map = await MapFileLoader.LoadAsync(await RoomHarness.ContextAsync(), document, CancellationToken.None);
        MapFileReader.TakeBounds(map);
        RoomLintException world = Assert.Throws<RoomLintException>(() => RoomLinter.CheckModel(room, map));
        Assert.Contains("(160 160 16)-(192 192 48)", world.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrushCrossingACellFaceOutsideTheKitIsRefusedByTheModel()
    {
        // Guarantee 1, model side: an eight-unit collar around the +x socket
        // sticking outward past the cell face is wider than the kit's hardware
        // may be, and CheckModel must refuse it before the compile is paid for.
        RoomDefinition room = RoomHarness.Room("cross", RoomFacing.PositiveX);
        VmfDocument document = RoomHarness.BuildRoomModel(room);
        document.Chunks[0].Children.Add(RoomModel.Slab(
            RoomHarness.Plain,
            new Vec3(240f, 80f, 80f),
            new Vec3(264f, 176f, 176f),
            4242));

        VbspContext context = await RoomHarness.ContextAsync();
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(document, room, context, default));

        Assert.Equal(
            "rule 1 (BrushesInsideOwnCells): a brush of room cross crosses a cell face outside the socket kit:"
            + " mins (240 80 80) maxs (264 176 176) against the cell 0..256.",
            error.Message);
    }

    /// <summary>
    /// An origin-relative brush entity (a hinged door, whose origin brush
    /// makes the loader rebuild its brushes in the entity's own frame) is
    /// held to its cell where it stands, not where its own frame puts it: a
    /// door inside the cell whose frame-local box reaches below zero is
    /// accepted, and one standing across the cell face is refused, its
    /// world bounds named. Before the fix the rule read the frame-local box
    /// and refused the first.
    /// </summary>
    [Fact]
    public async Task AnOriginRelativeBrushEntityIsHeldToItsCellWhereItStands()
    {
        RoomDefinition room = RoomHarness.Room("hinged", RoomFacing.PositiveX);
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: RoomBrushHarness.Files());

        VmfDocument inside = RoomHarness.BuildRoomModel(room);
        inside.Chunks.Add(RoomBrushHarness.Rotating(4300, new Vec3(200, 40, 16), new Vec3(240, 48, 100), new Vec3(236, 44, 20)));
        RoomObject compiled = await RoomCompiler.CompileAsync(inside, room, context, default);
        Assert.True(compiled.BrushModelsOfCompile!.Models[0].OriginRelative);

        VmfDocument across = RoomHarness.BuildRoomModel(room);
        across.Chunks.Add(RoomBrushHarness.Rotating(4310, new Vec3(40, 250, 16), new Vec3(60, 270, 100), new Vec3(44, 254, 20)));
        VbspContext second = await RoomHarness.ContextAsync(extraFiles: RoomBrushHarness.Files());
        RoomLintException error = await Assert.ThrowsAsync<RoomLintException>(
            () => RoomCompiler.CompileAsync(across, room, second, default));
        Assert.Equal(
            "rule 1 (BrushesInsideOwnCells): a brush of room hinged crosses a cell face outside the socket kit:"
            + " mins (40 250 16) maxs (60 270 100) against the cell 0..256.",
            error.Message);
    }
}
