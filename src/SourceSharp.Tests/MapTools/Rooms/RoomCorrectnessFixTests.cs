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
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The link and flatten transforms the rooms audit found silently wrong:
/// each fact here failed on the code before its fix, and holds the linked
/// map, the flattened VMF or the split to what a whole-map compile of the
/// same level gives.
/// </summary>
/// <remarks>
/// The rooms are small harness rooms on the walkable kit, built into a
/// library VMF with the feature under test added, so each fact goes through
/// the same split, room compile, link and flatten that <c>ssmap room</c> and
/// <c>ssmap link</c> run, in memory.
/// </remarks>
public sealed class RoomCorrectnessFixTests
{
    /// <summary>The four quarter turns every placement-dependent fact runs at, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    private static RoomDefinition Hub => RoomHarness.WalkableRoom(
        "hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);

    // ---- 1. info_ladder bounds -------------------------------------------------

    /// <summary>
    /// A room with a <c>func_ladder</c>, linked at each rotation: the
    /// <c>info_ladder</c> vbsp makes of it carries its bounds in six keys,
    /// and the linked map's must be the flattened compile's, which vbsp
    /// measures from the moved brushes. Before the fix the linker moved only
    /// <c>origin</c> and <c>angles</c>, so the bounds stayed room-local.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALinkedLaddersBoundsAreTheFlattenedCompiles(int rotation)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Hub);
        VmfChunk ladder = Entity("func_ladder", 700001);
        ladder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(40, 60, 16), new Vec3(56, 72, 120), 70001));
        library.Chunks.Add(ladder);

        (BspData linked, BspData whole) = await LinkAndCompileFlatAsync(library, $"hub@{rotation}, hub");

        List<string> Bounds(BspData bsp) =>
            [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => e.ClassName == "info_ladder")
                .Select(e => string.Join(' ', new[] { "mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z" }.Select(k => e.Get(k))))
                .Order(StringComparer.Ordinal)];

        List<string> expected = Bounds(whole);
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, Bounds(linked));
    }

    /// <summary>
    /// The ladder keys move as one box, re-sorted after the turn; an entity
    /// with only some of them is not a ladder and keeps them as written; a
    /// bound that is not a number is refused naming the room and key.
    /// </summary>
    [Fact]
    public void LadderBoundsMoveAsOneBoxOnlyWhenAllSixArePresent()
    {
        RoomTransform turned = new(new RoomPlacement("r", 1, 0, 1), 256);
        BspEntity ladder = new();
        foreach ((string key, string value) in new[]
        {
            ("classname", "info_ladder"), ("maxs.z", "120.00"), ("maxs.y", "72.00"), ("maxs.x", "56.00"),
            ("mins.z", "16.00"), ("mins.y", "60.00"), ("mins.x", "40.00"),
        })
        {
            ladder.Pairs.Add(new BspKeyValue(key, value));
        }

        BspEntity moved = LevelLinker.MoveEntity(ladder, turned, "r");
        Assert.Equal(
            ["440.00", "40.00", "16.00", "452.00", "56.00", "120.00"],
            new[] { "mins.x", "mins.y", "mins.z", "maxs.x", "maxs.y", "maxs.z" }.Select(k => moved.Get(k)));
        Assert.Equal([.. ladder.Pairs.Select(p => p.Key)], moved.Pairs.Select(p => p.Key));

        BspEntity partial = new();
        partial.Pairs.Add(new BspKeyValue("mins.x", "40"));
        Assert.Equal("40", LevelLinker.MoveEntity(partial, turned, "r").Get("mins.x"));

        ladder.Pairs[1] = new BspKeyValue("maxs.z", "high");
        LinkException refused = Assert.Throws<LinkException>(() => LevelLinker.MoveEntity(ladder, turned, "attic"));
        Assert.Equal("room attic has an entity whose \"maxs.z\" holds \"high\", not a number", refused.Message);
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>A point or brush entity chunk with an id and a class.</summary>
    private static VmfChunk Entity(string classname, int id, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", classname);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>
    /// A library's rooms compiled and linked into the level of the given
    /// rows, and the same level flattened and compiled whole: the two maps
    /// a room feature must agree across.
    /// </summary>
    private static async Task<(BspData Linked, BspData Whole)> LinkAndCompileFlatAsync(VmfDocument library, params string[] rows)
    {
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);
        RoomLibrary compiled = new(rooms[0].Definition.Kit, rooms[0].Definition.CellSize);
        foreach (LibraryRoom room in rooms)
        {
            VbspContext context = await RoomHarness.ContextAsync();
            context.MapBase = room.Definition.Name;
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, context));
        }

        LevelGrid level = LevelYaml.Parse(RoomHarness.LevelText("rooms.vmf", rows), "fixes");
        LevelLayout layout = level.ToLayout(name => compiled.Find(name)?.Definition, compiled.CellSize, compiled.Kit);
        LinkedLevel linked = await LevelLinker.LinkAsync(layout, compiled, await RoomHarness.ContextAsync());

        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await RoomHarness.ContextAsync());
        Assert.NotNull(whole.Bsp);
        return (linked.Bsp, whole.Bsp!);
    }
}
