//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The loader's ordering and contents rules, on maps built in code.
/// </summary>
public class MapFileLoaderTests
{
    [Fact]
    public async Task ABoxBrushLoadsWithSixSidesAndSixPlanes()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(1, map.BrushCount);
        Assert.Equal(6, map.Brushes[0].SideCount);
        Assert.Equal(12, map.Planes.Count);
    }

    /// <summary>
    /// A box's six sides are already the six axial planes, so
    /// <c>AddBrushBevels</c> adds none and returns early at.
    /// </summary>
    [Fact]
    public async Task APureAxialBoxNeedsNoBevels()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(0, map.BoxBevels);
        Assert.Equal(0, map.EdgeBevels);
    }

    /// <summary>
    /// The brush's windings give its bounds, so a box that loaded correctly has
    /// exactly the bounds the VMF asked for. A wrong plane normal direction
    /// would clip every side away and leave the 99999 sentinel instead.
    /// </summary>
    [Fact]
    public async Task ABoxBrushHasTheBoundsItsPlanesDescribe()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Plain, (-32, -16, 0), (32, 16, 8));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(new Vec3(-32f, -16f, 0f), map.Brushes[0].Mins);
        Assert.Equal(new Vec3(32f, 16f, 8f), map.Brushes[0].Maxs);
    }

    /// <summary>
    ///: a side with no visible contents and no clip
    /// contents is made solid.
    /// </summary>
    [Fact]
    public async Task ANoDrawSideBecomesSolid()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.NoDraw, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal((int)BrushContents.Solid, map.BrushSides[0].Contents);
    }

    /// <summary>
    /// <c>ALL_VISIBLE_CONTENTS</c> is the CONTIGUOUS low byte, not an OR of
    /// the flags that happen to have names.
    /// </summary>
    /// <remarks>
    /// spells it
    /// <c>LAST_VISIBLE_CONTENTS | (LAST_VISIBLE_CONTENTS-1)</c>.
    /// <c>0x80 | 0x7F</c> — so every bit below <c>CONTENTS_OPAQUE</c> is in it
    /// whether or not it is named, and the mask stays right when a new bit is
    /// added under that ceiling. Writing it as an OR of names dropped
    /// <c>CONTENTS_BLOCKLOS</c> and came to <c>0xBF</c>, which is what
    /// <see cref="ABlockLosSideIsNotMadeSolid"/> then observes.
    /// </remarks>
    [Fact]
    public void AllVisibleContentsIsTheContiguousLowByteAndNotAnOrOfNames()
    {
        const int LastVisibleContents = 0x80;

        Assert.Equal(
            LastVisibleContents | (LastVisibleContents - 1),
            MapFileLoader.AllVisibleContents);
        Assert.Equal(0xFF, MapFileLoader.AllVisibleContents);
    }

    /// <summary>
    /// <c>CONTENTS_BLOCKLOS</c> is inside <c>ALL_VISIBLE_CONTENTS</c>, so the
    /// solid default does NOT fire for it.
    /// </summary>
    /// <remarks>
    /// The behavioural half of the mask. Observed on the catalogue as
    /// <c>l1_tool_textures</c> brush 9: stock <c>0x8000040</c>, this port
    /// <c>0x8000041</c> before the fix.
    /// </remarks>
    [Fact]
    public async Task ABlockLosSideIsNotMadeSolid()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.BlockLos, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal((int)BrushContents.BlockLos, map.BrushSides[0].Contents);
    }

    /// <summary>
    ///: a hint or skip side has its contents cleared
    /// to NOTHING, after the solid default has already been applied. The order
    /// of those two is the behaviour.
    /// </summary>
    [Fact]
    public async Task AHintSideEndsWithNoContentsAtAll()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Hint, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(0, map.BrushSides[0].Contents);
        Assert.Equal((int)SurfaceFlags.Hint, map.BrushSides[0].Surface & (int)SurfaceFlags.Hint);
    }

    /// <summary>
    ///: the <c>material</c> key ASSIGNS the side's
    /// contents, so a <c>contents</c> key that came before it is overwritten
    /// and one that comes after is OR'd on top.
    /// </summary>
    [Fact]
    public async Task TheMaterialKeyOverwritesAnEarlierContentsKey()
    {
        VbspContext context = await UnitMap.ContextAsync();

        MapEntity entity = new();
        MapBrush brush = new();
        MapBrushSide side = new();
        BrushTexture td = new() { Name = string.Empty };
        Vec3[] points = new Vec3[3];

        td = await MapFileLoader.ApplySideKeyAsync(
            context, side, td, "contents", "16777216", points);
        Assert.Equal(16777216, side.Contents);

        td = await MapFileLoader.ApplySideKeyAsync(
            context, side, td, "material", UnitMap.NoDraw, points);

        Assert.NotEqual(16777216, side.Contents);
        _ = entity;
        _ = brush;
        _ = td;
    }

    /// <summary>
    ///: the <c>flags</c> key ORs into the placement's
    /// flags but ASSIGNS the side's surface from the result.
    /// </summary>
    [Fact]
    public async Task TheFlagsKeyOrsIntoThePlacementAndAssignsTheSide()
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapBrushSide side = new();
        BrushTexture td = new() { Name = string.Empty };
        Vec3[] points = new Vec3[3];

        td = await MapFileLoader.ApplySideKeyAsync(
            context, side, td, "material", UnitMap.Hint, points);
        int fromMaterial = td.Flags;

        td = await MapFileLoader.ApplySideKeyAsync(context, side, td, "flags", "1", points);

        Assert.Equal(fromMaterial | 1, td.Flags);
        Assert.Equal(td.Flags, side.Surface);
    }

    /// <summary>
    ///: the base is the FIRST side's contents and the
    /// other sides contribute only the four transparent bits.
    /// </summary>
    [Fact]
    public async Task BrushContentsTakesTheFirstSidesContentsAsItsBase()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapBrush brush = map.Brushes[0];

        map.BrushSides[brush.FirstSide].Contents = (int)BrushContents.Solid;
        map.BrushSides[brush.FirstSide + 1].Contents = (int)BrushContents.Grate;

        int contents = MapFileLoader.BrushContentsOf(map, brush);

        Assert.Equal(0, contents & (int)BrushContents.Solid);
        Assert.NotEqual(0, contents & (int)BrushContents.Grate);
        Assert.NotEqual(0, contents & (int)BrushContents.Translucent);
    }

    /// <summary>
    /// <c>IsAreaPortal</c> is a hand-written prefix compare,
    /// so it is case-SENSITIVE and it also refuses a
    /// classname shorter than the prefix.
    /// </summary>
    [Theory]
    [InlineData("func_areaportal", true)]
    [InlineData("func_areaportalwindow", true)]
    [InlineData("Func_AreaPortal", false)]
    [InlineData("func_area", false)]
    [InlineData("func_detail", false)]
    public void IsAreaPortalIsACaseSensitivePrefixTest(string className, bool expected) =>
        Assert.Equal(expected, MapFileLoader.IsAreaPortal(className));

    /// <summary>
    /// An origin brush sets the entity's origin from its centre and is then
    /// discarded. The origin is formatted with
    /// <c>%i</c> on a float, which truncates toward zero.
    /// </summary>
    [Fact]
    public async Task AnOriginBrushSetsTheEntityOriginAndIsNotKept()
    {
        VbspContext context = await UnitMap.ContextAsync();

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("classname", "func_door");
        entity.Children.Add(UnitMap.Box(UnitMap.Origin, (0, 0, 0), (16, 16, 16), 2));
        document.Chunks.Add(entity);

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(0, map.BrushCount);
        Assert.Equal("8 8 8", map.Entities[1].ValueForKey("origin"));
    }

    /// <summary>
    ///: a world clip brush has every side's texinfo
    /// replaced with <c>TEXINFO_NODE</c>, and the first one is remembered
    /// because it is about to be erased.
    /// </summary>
    [Fact]
    public async Task AWorldClipBrushHasItsSidesSetToTheNodeTexinfo()
    {
        VbspContext context = await UnitMap.ContextAsync();
        VmfDocument document = UnitMap.BoxMap(UnitMap.Clip, (0, 0, 0), (64, 64, 64));

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(1, map.ClipBrushes);
        Assert.True(map.ClipTexInfo >= 0);
        Assert.All(
            Enumerable.Range(0, map.Brushes[0].SideCount),
            i => Assert.Equal(
                TexInfoTable.TexInfoNode,
                map.BrushSides[map.Brushes[0].FirstSide + i].TexInfo));
    }

    /// <summary>
    /// <c>func_detail</c>'s brushes move into worldspawn and the entity is
    /// blanked, but its SLOT survives — entity numbers are referenced
    /// elsewhere.
    /// </summary>
    [Fact]
    public async Task FuncDetailBrushesMoveIntoWorldspawnAndTheEntitySlotSurvives()
    {
        VbspContext context = await UnitMap.ContextAsync();

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        world.Children.Add(UnitMap.Box(UnitMap.Plain, (0, 0, 0), (64, 64, 64), 1));
        document.Chunks.Add(world);

        VmfChunk detail = new(MapFileLoader.EntityChunk);
        detail.AddKey("classname", "func_detail");
        detail.Children.Add(UnitMap.Box(UnitMap.Plain, (128, 0, 0), (192, 64, 64), 2));
        document.Chunks.Add(detail);

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal(2, map.Entities.Count);
        Assert.Equal(2, map.Entities[0].BrushCount);
        Assert.Equal(0, map.Entities[1].BrushCount);
        Assert.Empty(map.Entities[1].Pairs);
    }

    /// <summary>
    /// <c>classname func_detail</c> sets the base contents the SUBSEQUENT
    /// solids are loaded with, so it only works when the key precedes them.
    /// which is why the loader walks the chunk's children in file order.
    /// </summary>
    [Fact]
    public async Task TheDetailBaseContentsReachTheSidesThatFollowTheClassname()
    {
        VbspContext context = await UnitMap.ContextAsync(
            VbspOptions.Default with { FullDetail = false });

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        VmfChunk detail = new(MapFileLoader.EntityChunk);
        detail.AddKey("classname", "func_detail");
        detail.Children.Add(UnitMap.Box(UnitMap.Plain, (0, 0, 0), (64, 64, 64), 1));
        document.Chunks.Add(detail);

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.NotEqual(
            0,
            map.BrushSides[0].Contents & (int)BrushContents.Detail);
    }

    /// <summary>
    ///: a <c>func_ladder</c> gains six bounds keys, is
    /// moved into the world, and becomes an <c>info_ladder</c>.
    /// </summary>
    [Fact]
    public async Task AFuncLadderBecomesAnInfoLadderWithBoundsKeys()
    {
        VbspContext context = await UnitMap.ContextAsync();

        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        VmfChunk ladder = new(MapFileLoader.EntityChunk);
        ladder.AddKey("classname", "func_ladder");
        ladder.Children.Add(UnitMap.Box(UnitMap.Plain, (0, 0, 0), (16, 16, 128), 1));
        document.Chunks.Add(ladder);

        MapFile map = await MapFileLoader.LoadAsync(context, document);

        Assert.Equal("info_ladder", map.Entities[1].ValueForKey("classname"));

        // "%2.2f": two decimals, and a minimum field width of two that never
        // has any effect. The X value is asserted numerically because the
        // clipper can leave a bound at -0.0f, which formats as "-0.00" -- a
        // sign of zero is not something this fact is about.
        Assert.Equal(0f, map.Entities[1].FloatForKey("mins.x"));
        Assert.Equal("128.00", map.Entities[1].ValueForKey("maxs.z"));
        Assert.Equal("16.00", map.Entities[1].ValueForKey("maxs.y"));
        Assert.Equal(1, map.Entities[0].BrushCount);
    }

    /// <summary>
    /// The <c>id</c> key is stored under the name <c>hammerid</c>
    /// and does NOT also land under <c>id</c>.
    /// </summary>
    [Fact]
    public void TheEntityIdKeyIsRenamedToHammerid()
    {
        MapEntity entity = new();
        int baseContents = 0;

        MapFileLoader.ApplyEntityKey(entity, "id", "17", ref baseContents);

        Assert.Equal("17", entity.ValueForKey("hammerid"));
        Assert.False(entity.HasKey("id"));
    }

    [Fact]
    public void TheClassnameKeyIsStoredAsWellAsRead()
    {
        MapEntity entity = new();
        int baseContents = 0;

        MapFileLoader.ApplyEntityKey(entity, "classname", "func_detail", ref baseContents);

        Assert.Equal("func_detail", entity.ValueForKey("classname"));
        Assert.Equal((int)BrushContents.Detail, baseContents);
    }
}
