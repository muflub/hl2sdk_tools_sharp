//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A level flattened into one VMF: every placed room copied out of the
/// library into its cell and turned, the joined plugs left out and the
/// capped ones kept, deterministic, and refused on the link's own rules.
/// </summary>
public sealed class LevelFlattenerTests
{
    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    private static RoomDefinition End => RoomHarness.WalkableRoom("end", RoomFacing.PositiveX);

    /// <summary>
    /// An end room turned to face a hub: one joint, whose two plugs are left
    /// out, and every other plug kept; each room in its own cell; the end's
    /// player start moved and turned with it; the library's worldspawn keys
    /// and version carried, and no <c>info_room</c>.
    /// </summary>
    [Fact]
    public void AFlattenedLevelIsEveryRoomInItsCellWithTheJoinedPlugsOut()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub, End);
        library.GetChunk("world")!.AddKey("skyname", "sky_day01_01");
        VmfChunk version = new("versioninfo");
        version.AddKey("formatversion", "100");
        library.Chunks.Insert(0, version);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, end@180"), "pair");

        VmfDocument flat = LevelFlattener.Flatten(level, library);

        List<VmfChunk> solids = [.. flat.GetChunk("world")!.GetChunks("solid")];
        List<Box> plugs = [.. solids.Where(IsPlug).Select(VmfPlacement.Bounds)];
        Assert.Equal(3, plugs.Count); // the hub's four and the end's one, less the joined pair
        Assert.DoesNotContain(plugs, p => p.Mins.X == 240 && p.Maxs.X == 256);
        Assert.DoesNotContain(plugs, p => p.Mins.X == 256 && p.Maxs.X == 272);
        Assert.All(solids.Select(VmfPlacement.Bounds), b => Assert.True(b.ContainsWithin(new Box(Vec3.Zero, new Vec3(512, 256, 256)), 0)));

        List<VmfChunk> entities = [.. flat.GetChunks("entity")];
        Assert.DoesNotContain(entities, e => e.GetValue("classname") == RoomLibraryVmf.RoomEntity);
        Assert.Equal(["128 128 129", "384 128 129"], entities.Select(e => e.GetValue("origin")));
        Assert.Equal("sky_day01_01", flat.GetChunk("world")!.GetValue("skyname"));
        Assert.Equal("100", flat.GetChunk("versioninfo")!.GetValue("formatversion"));
    }

    /// <summary>
    /// Every <c>id</c> is renumbered from 1 in document order, so a room
    /// placed twice does not repeat its brushes' ids; and the same level
    /// and library give the same bytes.
    /// </summary>
    [Fact]
    public void AFlattenedLevelIsNumberedAfreshAndDeterministic()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", "hub, hub", "hub, hub"), "square");

        VmfDocument flat = LevelFlattener.Flatten(level, library);

        List<int> ids = [];
        void Collect(VmfChunk chunk)
        {
            if (chunk.GetValue("id") is { } id)
            {
                ids.Add(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (VmfChunk child in chunk.Chunks)
            {
                Collect(child);
            }
        }

        foreach (VmfChunk chunk in flat.Chunks)
        {
            Collect(chunk);
        }

        Assert.Equal(Enumerable.Range(1, ids.Count), ids);
        Assert.Null(flat.GetChunk("versioninfo"));
        Assert.Equal(flat.ToBytes(), LevelFlattener.Flatten(level, library).ToBytes());

        // Four hubs in a square: four shared walls, eight plugs out of sixteen.
        Assert.Equal(8, flat.GetChunk("world")!.GetChunks("solid").Count(IsPlug));
    }

    /// <summary>The flatten refuses what the link refuses: an unknown room, no room, an island; and a library that does not split.</summary>
    [Fact]
    public void AFlattenIsRefusedOnTheLinksRules()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);

        LinkException unknown = Assert.Throws<LinkException>(
            () => LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("x", "hub, attic"), "l"), library));
        Assert.Equal("line 5, column 11: the level places room \"attic\", which is not in the room library.", unknown.Message);

        Assert.Throws<ArgumentException>(
            () => LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("x", "@"), "l"), library));

        RoomLintException island = Assert.Throws<RoomLintException>(
            () => LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("x", "hub, @, hub"), "l"), library));
        Assert.Contains("rule 6 (EveryRoomReachable)", island.Message, StringComparison.Ordinal);

        Assert.Throws<RoomLibraryException>(
            () => LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("x", "hub"), "l"), new VmfDocument()));
        Assert.Throws<ArgumentNullException>(() => LevelFlattener.Flatten(null!, library));
        Assert.Throws<ArgumentNullException>(
            () => LevelFlattener.Flatten(LevelYaml.Parse(RoomHarness.LevelText("x", "hub"), "l"), null!));
    }

    private static bool IsPlug(VmfChunk solid) =>
        solid.GetChunks("side").Any(s => s.GetValue("material") == RoomHarness.Trigger);
}
