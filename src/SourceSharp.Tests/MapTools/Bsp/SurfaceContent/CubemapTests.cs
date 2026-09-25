using System.Text;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary><c>utils/vbsp/cubemap.cpp</c>, fact by fact.</summary>
public class CubemapTests
{
    // The first 88 bytes of stock's cubemapdefault.vtf for sky_day01_01
    // (BGR888, flags 0x434c): extracted from l1_sealed_room.bsp's pak.
    private const string StockLdrHeaderHex =
        "5654460007000000040000005800000020002000" + "4c430000" + "01000000" + "00000000" +
        "0000803f0000803f0000803f00000000" + "0000803f" + "03000000" + "06" + "ffffffff" + "0000" + "0100" +
        "000000" + "01000000" + "0000000000000000" + "30000000" + "58000000";

    [Fact]
    public void ASampleOriginIsTruncatedTowardZero()
    {
        // cubemap.cpp:91-93, (int)origin.
        Assert.Equal((-128, 0, 128), CubemapFixups.SampleOrigin(new Vec3(-128.75f, 0.5f, 128.25f)));
    }

    [Fact]
    public void APatchedMaterialNameSeparatesTheOriginWithAnUnderscore()
    {
        // cubemap.cpp:510-512.
        Assert.Equal("maps/m/metal/wall_1_-2_3", CubemapFixups.PatchedName("Metal\\Wall", "m", (1, -2, 3), true));
    }

    [Fact]
    public void ACubemapTextureNameHasNoSeparator()
    {
        Assert.Equal("maps/m/c1_-2_3", CubemapFixups.PatchedName("c", "m", (1, -2, 3), false));
    }

    [Fact]
    public void ATooLongPatchedMaterialNameIsFatal()
    {
        // cubemap.cpp:514-521: nLen >= TEXTURE_NAME_LENGTH - 1.
        string material = new('a', 127 - "maps/m/_1_2_3".Length);

        Assert.Throws<MapCompileException>(() => CubemapFixups.PatchedName(material, "m", (1, 2, 3), true));
    }

    [Fact]
    public void AShorterPatchedMaterialNameIsAllowed()
    {
        string material = new('a', 126 - "maps/m/_1_2_3".Length);

        Assert.Equal(126, CubemapFixups.PatchedName(material, "m", (1, 2, 3), true).Length);
    }

    [Fact]
    public void ASideListTakesEachTokensLeadingInteger()
    {
        // strtok(" ") then sscanf("%d"), cubemap.cpp:489-501.
        Assert.Equal([12, 7, -3], CubemapFixups.ParseSideList("12  x 7abc -3"));
    }

    [Fact]
    public void AnHdrNameReplacesFromTheFirstVtf()
    {
        // cubemap.cpp:272-277.
        Assert.Equal("materials/maps/m/c1_2_3.hdr.vtf", DefaultCubemapBuilder.HdrName("materials/maps/m/c1_2_3.vtf", true));
        Assert.Equal("a.VTF", DefaultCubemapBuilder.HdrName("a.VTF", false));
    }

    [Fact]
    public void TheCubemapLumpIsSixteenBytesASampleWithTheSizeWrapped()
    {
        byte[] lump = CubemapSampleLump.ToBytes([new CubemapSample(new Vec3(-1.5f, 2.9f, 3f), 257, "")]);

        Assert.Equal(16, lump.Length);
        Assert.Equal(-1, BitConverter.ToInt32(lump, 0));
        Assert.Equal(2, BitConverter.ToInt32(lump, 4));
        Assert.Equal(1, lump[12]);
        Assert.Equal([0, 0, 0], lump[13..16]);
    }

    [Fact]
    public void AnLdrRoundTripThroughRgbaClearsBothAlphaFlagsOfAnOpaqueFormat()
    {
        // vtf.cpp:1958-1974: RGBA8888 sets EIGHTBITALPHA, BGR888 clears both.
        uint flags = DefaultCubemapBuilder.AfterConversion(0x3000u, ImageFormat.Rgba8888);
        flags = DefaultCubemapBuilder.AfterConversion(flags, ImageFormat.Bgr888);

        Assert.Equal(0u, flags);
    }

    [Fact]
    public void ConvertingToDxt5KeepsEightBitAlpha()
    {
        // vtf.cpp:1976-1981: only DXT1 (and ATI) clear the alpha flags.
        Assert.Equal(0x2000u, DefaultCubemapBuilder.AfterConversion(0x2000u, ImageFormat.Dxt5));
    }

    [Fact]
    public void ConvertingToDxt1ClearsBothAlphaFlags()
    {
        Assert.Equal(0u, DefaultCubemapBuilder.AfterConversion(0x3000u, ImageFormat.Dxt1));
    }

    [Fact]
    public void BgraFiveFiveFiveOneKeepsOneBitAlpha()
    {
        // One alpha bit: EIGHTBIT cleared, ONEBIT left as it was.
        Assert.Equal(0x1000u, DefaultCubemapBuilder.AfterConversion(0x3000u, ImageFormat.Bgra5551));
    }

    [Fact]
    public void TheSerialisedHeaderIsStocks()
    {
        byte[] vtf = DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0x434c, 1);

        Assert.Equal(StockLdrHeaderHex, Convert.ToHexString(vtf[..88]).ToLowerInvariant());
    }

    [Fact]
    public void TheLdrFileIsStocksLength()
    {
        // 88 + 7 faces x (32^2 + 16^2 + 8^2 + 4^2 + 2^2 + 1) x 3 bytes = 28753,
        // the size of every cubemapdefault.vtf in the stock catalogue.
        Assert.Equal(28753, DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0x434c, 1).Length);
    }

    [Fact]
    public void TheHdrFileIsStocksLength()
    {
        Assert.Equal(76528, DefaultCubemapBuilder.Serialize(ImageFormat.Rgba16161616F, 0x434c, 1).Length);
    }

    [Fact]
    public void TheImageIsBlack()
    {
        // cubemap.cpp:343-344, memset then continue.
        byte[] vtf = DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0x434c, 1);

        Assert.All(vtf[88..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void FramesMultiplyTheImage()
    {
        int one = DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0, 1).Length - 88;
        int two = DefaultCubemapBuilder.Serialize(ImageFormat.Bgr888, 0, 2).Length - 88;

        Assert.Equal(2 * one, two);
    }

    [Fact]
    public void WriteAddsTheDefaultAndEachNamedCubemapOnce()
    {
        // cubemap.cpp:453-466: FileExistsInPak skips a name already written.
        MapPakFile pak = new();
        DefaultCubemapBuilder.Write([1], false, "M", ["materials/maps/m/c1_2_3.vtf", "materials/maps/m/c1_2_3.vtf"], pak);

        Assert.Equal(["materials/maps/m/cubemapdefault.vtf", "materials/maps/m/c1_2_3.vtf"], pak.Entries.Select(e => e.Name));
    }

    [Fact]
    public void WriteOfTheHdrSetUsesHdrNames()
    {
        MapPakFile pak = new();
        DefaultCubemapBuilder.Write([1], true, "m", ["materials/maps/m/c1_2_3.vtf"], pak);

        Assert.Equal(["materials/maps/m/cubemapdefault.hdr.vtf", "materials/maps/m/c1_2_3.hdr.vtf"], pak.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task AMatchingSkyboxBuildsAFileWithItsFlagsEnvmapAndNoAlphaFlags()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            SurfaceUnit.Room(SurfaceUnit.Plain),
            extra: f => SurfaceUnit.AddSky(f, SurfaceUnit.SkyName, (int)ImageFormat.Bgr888, 0x0304,
                                           face => face == "up" ? 0x1304u : 0x0304u));

        byte[]? vtf = await DefaultCubemapBuilder.BuildAsync(
            SurfaceUnit.SkyName, false, loaded.Context.Materials, loaded.Context.Content, loaded.Context.Diagnostics);

        Assert.NotNull(vtf);
        // The union carries up's ONEBITALPHA; the BGR888 round trip clears it.
        Assert.Equal(0x4304u, BitConverter.ToUInt32(vtf!, 20));
    }

    [Fact]
    public async Task TheHdrFileIsHalfFloatAndKeepsTheSourceAlphaFlags()
    {
        // No ConvertImageFormat on the HDR destination (cubemap.cpp:414-430).
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            SurfaceUnit.Room(SurfaceUnit.Plain),
            extra: f => SurfaceUnit.AddSky(f, SurfaceUnit.SkyName, (int)ImageFormat.Bgr888, 0x2000));

        byte[]? vtf = await DefaultCubemapBuilder.BuildAsync(
            SurfaceUnit.SkyName, true, loaded.Context.Materials, loaded.Context.Content, loaded.Context.Diagnostics);

        Assert.Equal((int)ImageFormat.Rgba16161616F, BitConverter.ToInt32(vtf!, 52));
        Assert.Equal(0x6000u, BitConverter.ToUInt32(vtf!, 20));
    }

    [Fact]
    public async Task AMissingSkyboxWritesNothingAndSaysSo()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Plain));

        byte[]? vtf = await DefaultCubemapBuilder.BuildAsync(
            "nosuchsky", false, loaded.Context.Materials, loaded.Context.Content, loaded.Context.Diagnostics);

        Assert.Null(vtf);
        Assert.Contains(loaded.Context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.DefaultCubemapSkyboxMissing);
    }

    [Fact]
    public async Task SkyboxFacesWithDifferentFlagsWriteNothing()
    {
        // cubemap.cpp:246-252; alpha flags excepted, anything else must agree.
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            SurfaceUnit.Room(SurfaceUnit.Plain),
            extra: f => SurfaceUnit.AddSky(f, SurfaceUnit.SkyName, (int)ImageFormat.Bgr888, 0,
                                           face => face == "dn" ? 0x4u : 0x0u));

        byte[]? vtf = await DefaultCubemapBuilder.BuildAsync(
            SurfaceUnit.SkyName, false, loaded.Context.Materials, loaded.Context.Content, loaded.Context.Diagnostics);

        Assert.Null(vtf);
        Assert.Contains(loaded.Context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.DefaultCubemapSkyboxMismatch);
    }

    [Fact]
    public async Task SkyboxFacesDifferingOnlyInAlphaFlagsAreAccepted()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            SurfaceUnit.Room(SurfaceUnit.Plain),
            extra: f => SurfaceUnit.AddSky(f, SurfaceUnit.SkyName, (int)ImageFormat.Bgr888, 0,
                                           face => face == "dn" ? 0x2000u : 0x0u));

        Assert.NotNull(await DefaultCubemapBuilder.BuildAsync(
            SurfaceUnit.SkyName, false, loaded.Context.Materials, loaded.Context.Content, loaded.Context.Diagnostics));
    }

    [Fact]
    public async Task ASideAnEnvCubemapNamesGetsThatCubemapsPatchedMaterial()
    {
        (SurfaceUnit.Loaded loaded, int wall) = await SpecularRoomWithManualCubemapAsync();
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);

        await fixups.FixupBrushSidesMaterialsAsync();

        Assert.Equal("maps/unit/unit/specular_-128_0_128", MaterialOf(loaded, wall));
    }

    [Fact]
    public async Task ThePatchPointsEnvmapAtTheCubemapTexture()
    {
        (SurfaceUnit.Loaded loaded, _) = await SpecularRoomWithManualCubemapAsync();
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);

        await fixups.FixupBrushSidesMaterialsAsync();

        string patch = Encoding.Latin1.GetString(
            loaded.Patcher.Pak.Read("materials/maps/unit/unit/specular_-128_0_128.vmt", textMode: true)!);
        Assert.Contains("\"$envmap\"\t\t\"maps/unit/c-128_0_128\"", patch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APatchedSideQueuesItsCubemapVtf()
    {
        (SurfaceUnit.Loaded loaded, _) = await SpecularRoomWithManualCubemapAsync();
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);

        await fixups.FixupBrushSidesMaterialsAsync();

        Assert.Equal(["materials/maps/unit/c-128_0_128.vtf"], fixups.DefaultCubemapNames);
    }

    [Fact]
    public async Task ASecondReferenceToTheSamePatchIsAWarningAndLeavesTheSide()
    {
        // cubemap.cpp:609-613, g_IsCubemapTexData.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        int wall = TestMapCatalog.SideId(vmf, 2, 2);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(-128f, 0f, 128f), "sides", $"{wall}");
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(64f, 0f, 128f), "sides", $"{wall}");
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).FixupBrushSidesMaterialsAsync();

        Assert.Equal("maps/unit/unit/specular_-128_0_128", MaterialOf(loaded, wall));
        Assert.Contains(loaded.Context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.CubemapMultipleReferences);
    }

    [Fact]
    public async Task ADeletedSideIsAWarning()
    {
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f), "sides", "99999");
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).FixupBrushSidesMaterialsAsync();

        Assert.Contains(loaded.Context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.CubemapDeletedSide);
    }

    [Fact]
    public async Task ANonSpecularSideIsNotPatched()
    {
        // PatchEnvmapForMaterialAndDependents returns false, :553-554.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Plain);
        int wall = TestMapCatalog.SideId(vmf, 2, 2);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f), "sides", $"{wall}");
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).FixupBrushSidesMaterialsAsync();

        Assert.Equal(SurfaceUnit.Plain, MaterialOf(loaded, wall));
        Assert.Equal(0, loaded.Patcher.Pak.Count);
    }

    [Fact]
    public async Task EverySpecularSideIsAttachedToTheNearestCubemapInFrontOfIt()
    {
        // cubemap.cpp:861-876: the +X wall's inner face (normal -X) is behind
        // nothing; the sample at x = 200 is nearer and in front of it.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        int plusXWall = TestMapCatalog.SideId(vmf, 3, 3);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(-200f, 0f, 128f));
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(200f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal("maps/unit/unit/specular_200_0_128", MaterialOf(loaded, plusXWall));
    }

    [Fact]
    public async Task WithNoCubemapInFrontTheNearestWins()
    {
        // cubemap.cpp:879-896. Both samples are behind the floor's top face.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        int floor = TestMapCatalog.SideId(vmf, 0, 0);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, -300f));
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, -100f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal("maps/unit/unit/specular_0_0_-100", MaterialOf(loaded, floor));
    }

    [Fact]
    public async Task WithNoCubemapsNothingIsAttached()
    {
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(SurfaceUnit.Room(SurfaceUnit.Specular));

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal(0, loaded.Patcher.Pak.Count);
    }

    [Fact]
    public async Task AManuallyReferencedSideIsLeftToItsOwnCubemap()
    {
        // cubemap.cpp:84, bManuallyPickedByAnEnvCubemap.
        (SurfaceUnit.Loaded loaded, int wall) = await SpecularRoomWithManualCubemapAsync(
            extraSample: new Point(-240f, 0f, 128f));
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);

        await fixups.AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal(SurfaceUnit.Specular, MaterialOf(loaded, wall));
    }

    [Fact]
    public async Task UnderStockAPatchMaterialIsNeverTreatedAsSpecular()
    {
        // materialpatch.cpp:211: the raw patch has no $envmap. Measured in
        // stock on l2_cubemap_on_water_and_patch.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.PatchOfSpecular);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf, ComplianceOptions.Stock);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal(0, loaded.Patcher.Pak.Count);
    }

    [Fact]
    public async Task APatchMaterialIsPatchedWhenTheQuirkIsCorrected()
    {
        // StockQuirk.CubemapIgnoresPatchMaterials, flipped alone.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.PatchOfSpecular);
        int floor = TestMapCatalog.SideId(vmf, 0, 0);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(
            vmf, ComplianceOptions.Stock.Flipping(StockQuirk.CubemapIgnoresPatchMaterials));

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal("maps/unit/unit/patchofspecular_0_0_128", MaterialOf(loaded, floor));
    }

    [Fact]
    public async Task UnderStockAddUnreferencedAddsAUsedSampleAgain()
    {
        (SurfaceUnit.Loaded loaded, _) = await SpecularRoomWithManualCubemapAsync(ComplianceOptions.Stock);
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);
        await fixups.FixupBrushSidesMaterialsAsync();

        fixups.AddUnreferencedCubemaps();

        Assert.Equal(2, fixups.DefaultCubemapNames.Count);
    }

    [Fact]
    public async Task AddUnreferencedAddsAUsedSampleOnceWhenTheQuirkIsCorrected()
    {
        // StockQuirk.CubemapUnreferencedNeverMatches, flipped alone.
        (SurfaceUnit.Loaded loaded, _) = await SpecularRoomWithManualCubemapAsync(
            ComplianceOptions.Stock.Flipping(StockQuirk.CubemapUnreferencedNeverMatches));
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);
        await fixups.FixupBrushSidesMaterialsAsync();

        fixups.AddUnreferencedCubemaps();

        Assert.Equal(["materials/maps/unit/c-128_0_128.vtf"], fixups.DefaultCubemapNames);
    }

    [Fact]
    public async Task ADependentMaterialIsPatchedAndTheParentPointsAtThePatch()
    {
        // cubemap.cpp:546-579: the water's $bottommaterial is itself specular,
        // so it gets a patch and the water's patch names it.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Plain);
        vmf.WorldSolids[0].Sides[0].Material = SurfaceUnit.Water;
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        string water = Encoding.Latin1.GetString(
            loaded.Patcher.Pak.Read("materials/maps/unit/unit/water_0_0_128.vmt", textMode: true)!);
        Assert.Contains("\"$bottommaterial\"\t\t\"maps/unit/unit/beneath_0_0_128\"", water, StringComparison.Ordinal);
        Assert.True(loaded.Patcher.Pak.Contains("materials/maps/unit/unit/beneath_0_0_128.vmt"));
    }

    [Fact]
    public async Task AMaterialDependingOnItselfIsAWarning()
    {
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.SelfDependent);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(0f, 0f, 128f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);

        await new CubemapFixups(loaded.Context, loaded.Map, loaded.Patcher).AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Contains(loaded.Context.Diagnostics, d => d.Code == SurfaceContentDiagnostics.MaterialDependsOnItself);
    }

    [Fact]
    public async Task AnUnreferencedSampleIsAddedUnderEitherPolicy()
    {
        // cubemap.cpp:986-993: a sample no side used still gets its VTF.
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Plain);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(1f, 2f, 3f));
        SurfaceUnit.Loaded loaded = await SurfaceUnit.LoadAsync(vmf);
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);

        fixups.AddUnreferencedCubemaps();

        Assert.Equal(["materials/maps/unit/c1_2_3.vtf"], fixups.DefaultCubemapNames);
    }

    [Fact]
    public async Task ASecondSideOfAPatchedMaterialReusesTheTexData()
    {
        // cubemap.cpp:633-678: the texdata already exists, so the texinfo is
        // found rather than added when it matches.
        (SurfaceUnit.Loaded loaded, _) = await SpecularRoomWithManualCubemapAsync();
        CubemapFixups fixups = new(loaded.Context, loaded.Map, loaded.Patcher);
        await fixups.FixupBrushSidesMaterialsAsync();
        int before = loaded.Context.TexDatas.Count;

        await fixups.AttachDefaultCubemapToSpecularSidesAsync();

        Assert.Equal(before, loaded.Context.TexDatas.Count);
    }

    private static async Task<(SurfaceUnit.Loaded Loaded, int Wall)> SpecularRoomWithManualCubemapAsync(ComplianceOptions compliance)
    {
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        int wall = TestMapCatalog.SideId(vmf, 2, 2);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(-128.75f, 0.5f, 128.25f), "sides", $"{wall}", "cubemapsize", "5");
        return (await SurfaceUnit.LoadAsync(vmf, compliance), wall);
    }

    private static async Task<(SurfaceUnit.Loaded Loaded, int Wall)> SpecularRoomWithManualCubemapAsync(Point? extraSample = null)
    {
        VmfMap vmf = SurfaceUnit.Room(SurfaceUnit.Specular);
        int wall = TestMapCatalog.SideId(vmf, 2, 2);
        RoomKit.PointEntity(vmf, "env_cubemap", new Point(-128.75f, 0.5f, 128.25f), "sides", $"{wall}", "cubemapsize", "5");
        if (extraSample is { } p)
        {
            RoomKit.PointEntity(vmf, "env_cubemap", p);
        }

        return (await SurfaceUnit.LoadAsync(vmf), wall);
    }

    internal static string MaterialOf(SurfaceUnit.Loaded loaded, int sideId)
    {
        MapBrushSide side = loaded.Map.BrushSides[loaded.Map.SideIdToIndex(sideId)];
        TexInfo texInfo = loaded.Context.TexInfos[side.TexInfo];
        return loaded.Context.TexDatas.NameOf(texInfo.TexData);
    }
}
