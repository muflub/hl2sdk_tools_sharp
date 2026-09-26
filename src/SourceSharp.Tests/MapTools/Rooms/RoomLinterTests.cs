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
}
