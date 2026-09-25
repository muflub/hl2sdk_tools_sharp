using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Overlays;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary> and its loader half in the reference implementation.</summary>
public class OverlayTests
{
    [Fact]
    public void ANamedOverlayBecomesAnAccessorCarryingItsId()
    {
        OverlaySet set = new();
        set.AddFromEntity(Entity("targetname", "a"));
        MapEntity second = Entity("targetname", "b");

        int accessor = set.AddFromEntity(second);

        Assert.Equal(1, accessor);
        Assert.Equal("info_overlay_accessor", second.ValueForKey("classname"));
        Assert.Equal("1", second.ValueForKey("OverlayID"));
    }

    [Fact]
    public void AnUnnamedOverlayEntityIsCleared()
    {
        //: iAccessorID < 0 -> epairs = NULL.
        MapEntity entity = Entity();

        Assert.Equal(-1, new OverlaySet().AddFromEntity(entity));
        Assert.Empty(entity.Pairs);
    }

    [Fact]
    public void APositiveFadeIsSquaredAndANegativeOneIsNot()
    {
        OverlaySet set = new();
        set.AddFromEntity(Entity("fademindist", "3", "fademaxdist", "-1"));

        Assert.Equal(9f, set.Overlays[0].FadeDistMinSq);
        Assert.Equal(-1f, set.Overlays[0].FadeDistMaxSq);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    public void ARenderOrderOutsideZeroToThreeIsFatal(string order)
    {
        Assert.Throws<MapCompileException>(() => new OverlaySet().AddFromEntity(Entity("RenderOrder", order)));
    }

    [Fact]
    public void AMaterialNameOf256CharactersIsFatal()
    {
        //: strlen >= OVERLAY_MAP_STRLEN.
        Assert.Throws<MapCompileException>(() => new OverlaySet().AddFromEntity(Entity("material", new string('m', 256))));
    }

    [Fact]
    public void TheSideListIsEveryLeadingInteger()
    {
        OverlaySet set = new();
        set.AddFromEntity(Entity("sides", "3 19 x 4"));

        Assert.Equal([3, 19, 4], set.Overlays[0].SideList);
    }

    [Fact]
    public void AWaterOverlayIdIsOffsetPastTheOverlayLimit()
    {
        //: (MAX_MAP_OVERLAYS + 1) + count - 1.
        OverlaySet set = new();
        set.AddWaterOverlay(Data());
        MapOverlay second = set.AddWaterOverlay(Data());

        Assert.Equal(514, second.Id);
    }

    [Fact]
    public void AWaterOverlayVectorNeedsBrackets()
    {
        //, sscanf("[%f %f %f]").
        MapOverlay overlay = new OverlaySet().AddWaterOverlay(Data("BasisOrigin", "1 2 3", "BasisNormal", "[0 0 1]"));

        Assert.Equal(Vec3.Zero, overlay.Origin);
        Assert.Equal(new Vec3(0f, 0f, 1f), overlay.Basis[2]);
    }

    [Fact]
    public void AWaterOverlaysLastSidesKeyWins()
    {
        // purge before refilling.
        MapOverlay overlay = new OverlaySet().AddWaterOverlay(Data("sides", "1 2", "sides", "7"));

        Assert.Equal([7], overlay.SideList);
    }

    [Fact]
    public void AWaterOverlayKeyIsMatchedIgnoringCase()
    {
        MapOverlay overlay = new OverlaySet().AddWaterOverlay(Data("startv", "0.5"));

        Assert.Equal(0.5f, overlay.V.Start);
    }

    [Fact]
    public async Task AWaterOverlayMaterialGoesThroughTheReplacementTable()
    {
        KeyValuesDocument cfg = await KeyValuesDocument.ParseAsync("\"materialsub\"\n{\n\t\"AllMaps\"\n\t{\n\t\t\"a/b\" \"c/d\"\n\t}\n}\n");
        OverlaySet set = new() { Replacements = new MaterialReplacements(cfg, "m") };

        Assert.Equal("c/d", set.AddWaterOverlay(Data("material", "a/b")).MaterialName);
    }

    [Fact]
    public async Task UpdatingSideListsAddsEachIdOnceToEveryNamedSide()
    {
        //, Find == -1 guard.
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        int floor = TestMapCatalog.SideId(SurfaceUnit.Room(SurfaceUnit.Plain), 0, 0);
        OverlaySet set = new();
        set.AddFromEntity(Entity("sides", $"{floor} {floor}"));

        set.UpdateSideLists(loaded.Map, 0, 0);

        Assert.Equal([0], loaded.Map.BrushSides[loaded.Map.SideIdToIndex(floor)].OverlayIds);
    }

    [Fact]
    public async Task AFaceIsAddedOnceToEachOfItsSidesOverlays()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        int floor = TestMapCatalog.SideId(SurfaceUnit.Room(SurfaceUnit.Plain), 0, 0);
        OverlaySet set = new();
        set.AddFromEntity(Entity("sides", $"{floor}"));
        set.AddWaterOverlay(Data("sides", $"{floor}"));
        set.UpdateSideLists(loaded.Map, 0, 0);
        MapBrushSide side = loaded.Map.BrushSides[loaded.Map.SideIdToIndex(floor)];

        set.AddFace(5, side);
        set.AddFace(5, side);
        set.AddFace(9, side);

        Assert.Equal([5, 9], set.Overlays[0].FaceList);
        Assert.Equal([5, 9], set.WaterOverlays[0].FaceList);
    }

    [Fact]
    public async Task BasisUIsPackedIntoTheFirstThreeUvZs()
    {
        DOverlay o = await EmitOneAsync(Entity("BasisU", "0.25 0.5 0.75", "uv0", "1 2 9"));

        Assert.Equal(new Vec3(1f, 2f, 0.25f), o.UvPoints[0]);
        Assert.Equal(0.5f, o.UvPoints[1].Z);
        Assert.Equal(0.75f, o.UvPoints[2].Z);
    }

    [Fact]
    public async Task AFlippedBasisSetsTheFourthUvZToOne()
    {
        //: (N x U). V < 0.
        DOverlay o = await EmitOneAsync(Entity("BasisU", "1 0 0", "BasisV", "0 -1 0", "BasisNormal", "0 0 1", "uv3", "4 5 0.25"));

        Assert.Equal(1f, o.UvPoints[3].Z);
    }

    [Fact]
    public async Task AnUnflippedBasisKeepsTheFourthUvZ()
    {
        DOverlay o = await EmitOneAsync(Entity("BasisU", "1 0 0", "BasisV", "0 1 0", "BasisNormal", "0 0 1", "uv3", "4 5 0.25"));

        Assert.Equal(0.25f, o.UvPoints[3].Z);
    }

    [Fact]
    public async Task TheRenderOrderIsTheTopTwoBits()
    {
        DOverlay o = await EmitOneAsync(Entity("RenderOrder", "3"));

        Assert.Equal(0xC000, o.FaceCountAndRenderOrder);
    }

    [Fact]
    public async Task TheTexInfoHasNoFlagsAndTheSentinelOffsets()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(Entity("material", SurfaceUnit.Specular));

        OverlayLumps lumps = await set.EmitAsync(loaded.Context);
        TexInfo texInfo = loaded.Context.TexInfos[lumps.Overlays[0].TexInfo];

        Assert.Equal(0, texInfo.Flags);
        Assert.Equal(-99999f, texInfo.TextureVecsTexelsPerWorldUnits[3]);
        Assert.Equal(-99999f, texInfo.LightmapVecsLuxelsPerWorldUnits[7]);
        Assert.Equal(0f, texInfo.TextureVecsTexelsPerWorldUnits[0]);
        Assert.Equal(SurfaceUnit.Specular, loaded.Context.TexDatas.NameOf(texInfo.TexData));
    }

    [Fact]
    public async Task TwoOverlaysOfOneMaterialShareATexInfo()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(Entity("material", SurfaceUnit.Specular));
        set.AddWaterOverlay(Data("material", SurfaceUnit.Specular));

        OverlayLumps lumps = await set.EmitAsync(loaded.Context);

        Assert.Equal(lumps.Overlays[0].TexInfo, lumps.WaterOverlays[0].TexInfo);
    }

    [Fact]
    public async Task UnderStockSixtyFourFacesIsTooManyForAnOverlay()
    {
        //: nFaceCount >= OVERLAY_BSP_FACE_COUNT.
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain), ComplianceOptions.Stock);
        OverlaySet set = new();
        set.AddFromEntity(Entity());
        set.Overlays[0].FaceList.AddRange(Enumerable.Range(0, 64));

        await Assert.ThrowsAsync<MapCompileException>(() => set.EmitAsync(loaded.Context));
    }

    [Fact]
    public async Task SixtyFourFacesFitWhenTheQuirkIsCorrected()
    {
        // StockQuirk.OverlayFaceLimitOffByOne, flipped alone: the array holds 64.
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            SurfaceUnit.Room(SurfaceUnit.Plain), ComplianceOptions.Stock.Flipping(StockQuirk.OverlayFaceLimitOffByOne));
        OverlaySet set = new();
        set.AddFromEntity(Entity());
        set.Overlays[0].FaceList.AddRange(Enumerable.Range(0, 64));

        OverlayLumps lumps = await set.EmitAsync(loaded.Context);

        Assert.Equal(64, lumps.Overlays[0].GetFaceCount());
    }

    [Fact]
    public async Task SixtyFiveFacesAreTooManyEvenWhenCorrected()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(Entity());
        set.Overlays[0].FaceList.AddRange(Enumerable.Range(0, 65));

        await Assert.ThrowsAsync<MapCompileException>(() => set.EmitAsync(loaded.Context));
    }

    [Fact]
    public async Task UnderStockAnOverlayMaterialIsNotReplaced()
    {
        // reads the key raw.
        OverlaySet set = new() { Replacements = await ReplacementsAsync(), Compliance = ComplianceOptions.Stock };

        set.AddFromEntity(Entity("material", "a/b"));

        Assert.Equal("a/b", set.Overlays[0].MaterialName);
    }

    [Fact]
    public async Task AnOverlayMaterialIsReplacedWhenTheQuirkIsCorrected()
    {
        // StockQuirk.OverlayMaterialNotReplaced, flipped alone.
        OverlaySet set = new()
        {
            Replacements = await ReplacementsAsync(),
            Compliance = ComplianceOptions.Stock.Flipping(StockQuirk.OverlayMaterialNotReplaced),
        };

        set.AddFromEntity(Entity("material", "a/b"));

        Assert.Equal("c/d", set.Overlays[0].MaterialName);
    }

    private static async Task<MaterialReplacements> ReplacementsAsync() =>
        new(await KeyValuesDocument.ParseAsync("\"materialsub\"\n{\n\t\"AllMaps\"\n\t{\n\t\t\"a/b\" \"c/d\"\n\t}\n}\n"), "m");

    [Fact]
    public async Task SixtyThreeFacesFit()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(Entity());
        set.Overlays[0].FaceList.AddRange(Enumerable.Range(0, 63));

        OverlayLumps lumps = await set.EmitAsync(loaded.Context);

        Assert.Equal(63, lumps.Overlays[0].GetFaceCount());
        Assert.Equal(62, lumps.Overlays[0].Faces[62]);
    }

    [Fact]
    public async Task TheFadeLumpIsParallelToTheOverlays()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(Entity("fademindist", "2", "fademaxdist", "4"));

        OverlayLumps lumps = await set.EmitAsync(loaded.Context);

        Assert.Equal(8, lumps.FadeBytes().Length);
        Assert.Equal((4f, 16f), (lumps.Fades[0].FadeDistMinSq, lumps.Fades[0].FadeDistMaxSq));
    }

    [Fact]
    public void LoadReadsOverlayTransitionBlocksFromEntitiesInDocumentOrder()
    {
        VmfDocument document = new();
        VmfChunk entity = new("entity");
        VmfChunk transition = new("overlaytransition");
        transition.Children.Add(Data("material", "first"));
        transition.Children.Add(Data("material", "second"));
        entity.Children.Add(transition);
        document.Chunks.Add(entity);

        OverlaySet set = OverlaySet.Load(new MapFile(new SourceSharp.MapTools.Geometry.WindingArena()), document);

        Assert.Equal(["first", "second"], set.WaterOverlays.Select(o => o.MaterialName));
    }

    private static async Task<DOverlay> EmitOneAsync(MapEntity entity)
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));
        OverlaySet set = new();
        set.AddFromEntity(entity);
        return (await set.EmitAsync(loaded.Context)).Overlays[0];
    }

    private static MapEntity Entity(params string[] keys)
    {
        MapEntity entity = new();
        entity.SetKeyValue("classname", "info_overlay");
        entity.SetKeyValue("material", SurfaceUnit.Plain);
        for (int i = 0; i < keys.Length; i += 2)
        {
            entity.SetKeyValue(keys[i], keys[i + 1]);
        }

        return entity;
    }

    private static VmfChunk Data(params string[] keys)
    {
        VmfChunk chunk = new("overlaydata");
        chunk.AddKey("material", SurfaceUnit.Plain);
        for (int i = 0; i < keys.Length; i += 2)
        {
            chunk.AddKey(keys[i], keys[i + 1]);
        }

        return chunk;
    }
}
