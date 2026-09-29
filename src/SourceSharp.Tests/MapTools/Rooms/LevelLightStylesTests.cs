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
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Switchable light styles over a linked level (the rooms design, section 8,
/// finding 8 and 15.3 fact 8): each room's compile numbers its named lights
/// from 32, and the link numbers the level's as vbsp numbers the flattened
/// map's, one style per distinct name, the faces and world lights of a lit
/// level following their lights.
/// </summary>
public sealed class LevelLightStylesTests
{
    private static BspEntity Entity(params (string Key, string Value)[] pairs)
    {
        BspEntity entity = new();
        entity.Pairs.AddRange(pairs.Select(p => new BspKeyValue(p.Key, p.Value)));
        return entity;
    }

    /// <summary>
    /// Two placements whose rooms each gave their one named light style 32
    /// take 32 and 33, in lump order; each placement's room style maps to its
    /// own; a name two placements share (a global name) shares one style;
    /// unnamed lights, <c>light_dynamic</c> and other classes are left alone,
    /// and a named light without a <c>style</c> key gets one.
    /// </summary>
    [Fact]
    public void EachDistinctNameTakesOneStyleInLumpOrder()
    {
        BspEntity first = Entity(("classname", "light"), ("targetname", "c0r0_lamp"), ("style", "32"));
        BspEntity second = Entity(("classname", "light_spot"), ("targetname", "c1r0_lamp"), ("style", "32"));
        BspEntity shared = Entity(("classname", "light"), ("targetname", "hall"), ("style", "33"));
        BspEntity sharedAgain = Entity(("classname", "light"), ("targetname", "hall"), ("style", "32"));
        BspEntity unnamed = Entity(("classname", "light"), ("style", "5"));
        BspEntity dynamic = Entity(("classname", "light_dynamic"), ("targetname", "torch"), ("style", "0"));
        BspEntity other = Entity(("classname", "info_target"), ("targetname", "c2r0_lamp"));
        BspEntity bare = Entity(("classname", "LIGHT"), ("targetname", "bare"));

        LevelLightStyles styles = new();
        styles.Renumber([(first, 0), (second, 1), (shared, 0), (sharedAgain, 2), (unnamed, 0), (dynamic, 0), (other, 2), (bare, -1)]);

        Assert.Equal("32", first.Get("style"));
        Assert.Equal("33", second.Get("style"));
        Assert.Equal("34", shared.Get("style"));
        Assert.Equal("34", sharedAgain.Get("style"));
        Assert.Equal("5", unnamed.Get("style"));
        Assert.Equal("0", dynamic.Get("style"));
        Assert.Null(other.Get("style"));
        Assert.Equal("35", bare.Get("style"));
        Assert.Equal(4, styles.Count);

        Assert.Equal(32, styles.Remap(0, 32));
        Assert.Equal(34, styles.Remap(0, 33));
        Assert.Equal(33, styles.Remap(1, 32));
        Assert.Equal(34, styles.Remap(2, 32));

        // Styles no named light took pass through: the normal style, the
        // presets, a style the placement never named, and "none".
        Assert.Equal(0, styles.Remap(1, 0));
        Assert.Equal(5, styles.Remap(0, 5));
        Assert.Equal(40, styles.Remap(1, 40));
        Assert.Equal(255, styles.Remap(0, 255));
    }

    /// <summary>More switched light names than a map holds are refused, naming the one past the limit.</summary>
    [Fact]
    public void MoreThanThirtyTwoNamesAreRefused()
    {
        List<(BspEntity, int)> lights = [.. Enumerable.Range(0, 33).Select(i =>
            (Entity(("classname", "light"), ("targetname", string.Create(CultureInfo.InvariantCulture, $"lamp{i}")), ("style", "32")), i))];
        LinkException refused = Assert.Throws<LinkException>(() => new LevelLightStyles().Renumber(lights));
        Assert.Equal("the level names more than 32 switched lights (lamp32 is one too many); a map holds at most 32.", refused.Message);
    }

    /// <summary>
    /// 15.3 fact 8, the names half, red first: two rooms each with a named
    /// switchable light, linked side by side (unlit), carry two distinct
    /// styles, the ones vbsp gives the flattened level. Before the link
    /// renumbered them, both kept their compiles' 32.
    /// </summary>
    [Fact]
    public async Task TwoRoomsWithANamedLightEachLinkTwoStyles()
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(library, light: false);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        BspData linked = (await RoomLightHarness.LinkAsync(rooms, level)).Bsp;
        Assert.Equal(
            [("c0r0_lamp", "32"), ("c1r0_lamp", "33")],
            Named(linked));

        VbspContextFlat flat = await VbspContextFlat.CompileAsync(library, level);
        Assert.Equal(Named(flat.Bsp), Named(linked));
    }

    /// <summary>
    /// 15.3 fact 8, the lightmaps half: in a lit level the faces the second
    /// room's lamp lights carry its level style, not its room's, the world
    /// light carries it too, and each style's luxels stand at the same
    /// points as the flattened full compile's.
    /// </summary>
    [Fact]
    public async Task TheLitFacesAndWorldLightsFollowTheirLightsStyle()
    {
        VmfDocument library = Library();
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level("hub, other");
        BspData linked = (await RoomLightHarness.LinkAsync(rooms, level)).Bsp;
        BspData flat = await RoomLightHarness.CompileFlatLitAsync(library, level);

        HashSet<int> faceStyles = [.. BspStructView.As<DFace>(linked[BspLump.Faces]).ToArray().SelectMany(f => new[] { f.Styles[0], f.Styles[1], f.Styles[2], f.Styles[3] }).Select(s => (int)s)];
        Assert.Contains(32, faceStyles);
        Assert.Contains(33, faceStyles);
        Assert.Equal(
            [32, 33],
            BspStructView.As<DWorldLight>(linked[BspLump.WorldLights]).ToArray().Select(l => l.Style).Where(s => s >= 32).Order());

        var ours = LitCompare.Lattice(linked);
        var theirs = LitCompare.Lattice(flat);
        foreach (int style in (int[])[32, 33])
        {
            HashSet<(long, long, long)> a = [.. ours.Keys.Where(k => k.Item7 == style).Select(k => (k.Item1, k.Item2, k.Item3))];
            HashSet<(long, long, long)> b = [.. theirs.Keys.Where(k => k.Item7 == style).Select(k => (k.Item1, k.Item2, k.Item3))];
            Assert.NotEmpty(a);
            Assert.True(a.Overlaps(b), $"style {style}");
            Assert.Empty(a.Where(p => p.Item1 < RoomHarness.Cell * 100 != (style == 32)));
        }
    }

    /// <summary>The prop harness's rooms, each with a lamp named room-locally.</summary>
    private static VmfDocument Library() => RoomLightHarness.Library(
        true,
        [],
        (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150), "cxry_lamp")),
        (1, RoomLightHarness.Light(801, new Vec3(100, 60, 120), "cxry_lamp")));

    /// <summary>The named lights of a map and their styles, in lump order.</summary>
    private static List<(string, string)> Named(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities])
            .Where(e => e.ClassName.StartsWith("light", StringComparison.Ordinal) && e.Get("targetname") is not null)
            .Select(e => (e.Get("targetname")!, e.Get("style")!))];

    /// <summary>The flattened level compiled by vbsp alone.</summary>
    private sealed record VbspContextFlat(BspData Bsp)
    {
        public static async Task<VbspContextFlat> CompileAsync(VmfDocument library, LevelGrid level)
        {
            SourceSharp.MapTools.Bsp.Driver.VbspResult whole = await RoomHarness.CompileAsync(
                LevelFlattener.Flatten(level, library), await RoomLightHarness.ContextAsync("flat"));
            return new VbspContextFlat(whole.Bsp!);
        }
    }
}
