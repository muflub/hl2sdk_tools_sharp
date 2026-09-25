using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Materials;
using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// A closed 256-unit box of six faces with configurable entities: the
/// smallest map every stage of 4c can run on.
/// </summary>
internal static class LightBox
{
    internal const float Size = 256;

    internal static LightTestMap Map(
        SurfaceFlags ceiling = 0,
        string ceilingMaterial = "concrete/floor",
        SurfaceFlags walls = 0)
    {
        LightTestMap map = new();
        int floor = map.AddTexture("concrete/floor");
        int top = map.AddTexture(ceilingMaterial, ceiling);
        int wall = map.AddTexture("brick/wall", walls, sAxis: new Vec3(0, 1, 0), tAxis: new Vec3(0, 0, -1));
        int wallX = map.AddTexture("brick/wallx", walls, sAxis: new Vec3(1, 0, 0), tAxis: new Vec3(0, 0, -1));
        float s = Size;
        map.AddFloor(floor, 0, 0, s, s, 0, up: true);
        map.AddFloor(top, 0, 0, s, s, s, up: false);
        map.AddFace(wall, new Vec3(1, 0, 0), new(0, 0, s), new(0, s, s), new(0, s, 0), new(0, 0, 0));
        map.AddFace(wall, new Vec3(-1, 0, 0), new(s, s, s), new(s, 0, s), new(s, 0, 0), new(s, s, 0));
        map.AddFace(wallX, new Vec3(0, 1, 0), new(s, 0, s), new(0, 0, s), new(0, 0, 0), new(s, 0, 0));
        map.AddFace(wallX, new Vec3(0, -1, 0), new(0, s, s), new(s, s, s), new(s, s, 0), new(0, s, 0));
        return map;
    }

    internal static DirectLightingSettings Settings(bool stock = false, bool hdr = false) =>
        new()
        {
            Hdr = hdr,
            Compliance = stock ? ComplianceOptions.Stock : ComplianceOptions.Correct,
        };

    internal static async Task<TextureLightTable> TexLightsAsync(string text = "", bool hdr = false) =>
        new(await RadLightFile.ParseAsync(text, new RadLightOptions(Hdr: hdr)), "box");

    internal static RadWorld Build(LightTestMap map, DirectLightingSettings? settings = null, TextureLightTable? texLights = null) =>
        RadWorld.Build(map.Build(), settings ?? Settings(), texLights ?? new TextureLightTable(new RadLightFile(), "box"));
}

public sealed class DirectLightBuilderTests
{
    [Fact]
    public void APointLightBecomesOneActiveLight()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        RadWorld world = LightBox.Build(map);

        DirectLight light = Assert.Single(world.Lights.Active);
        Assert.Equal(EmitType.Point, light.Type);
        Assert.Equal(new Vec3(128, 128, 128), light.Origin);
    }

    [Fact]
    public void LightIntensityIsLinearisedAndScaledByTheFourthField()
    {
        //: pow(255/255, 2.2) * 255 = 255, times 200/255.
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 200")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.Equal(200f, light.Intensity.X, 3);
    }

    [Fact]
    public void LightDynamicIsNotACompileLight()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light_dynamic"), ("origin", "1 1 1"), ("_light", "255 0 0")));
        Assert.Empty(LightBox.Build(map).Lights.Active);
    }

    [Fact]
    public void AnUnknownLightClassIsReportedAndSkipped()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light_glow"), ("origin", "1 1 1")));
        RadWorld world = LightBox.Build(map);
        Assert.Empty(world.Lights.Active);
        Assert.Contains("light_glow", world.Lights.UnsupportedClassNames);
    }

    [Fact]
    public void TheActiveListIsInReverseCreationOrder()
    {
        // AllocDLight PREPENDS, so the worldlights lump
        // lists the last entity first.
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "10 10 10"), ("_light", "255 255 255")));
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "20 20 20"), ("_light", "255 255 255")));
        RadWorld world = LightBox.Build(map);
        Assert.Equal(new Vec3(20, 20, 20), world.Lights.Active[0].Origin);
        Assert.Equal(new Vec3(10, 10, 10), world.Lights.Active[1].Origin);
    }

    [Fact]
    public void ASpotlightsConesBecomeCosines()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "128 128 200"), ("angles", "-90 0 0"),
            ("_light", "255 255 255 200"), ("_inner_cone", "30"), ("_cone", "45")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.Equal(EmitType.Spotlight, light.Type);
        Assert.Equal((float)Math.Cos(30.0f / 180 * Math.PI), light.StopDot);
        Assert.Equal((float)Math.Cos(45.0f / 180 * Math.PI), light.StopDot2);
    }

    [Fact]
    public void ASpotlightWithNoInnerConeGetsTen()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light_spot"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.Equal((float)Math.Cos(10.0f / 180 * Math.PI), light.StopDot);
        Assert.Equal(light.StopDot, light.StopDot2);
    }

    [Fact]
    public void AFullyOpenSpotlightIsAPointLightWithZeroCones()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "1 1 1"), ("_light", "255 255 255"),
            ("_inner_cone", "180"), ("_cone", "180")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.Equal(EmitType.Point, light.Type);
        Assert.Equal((0f, 0f), (light.StopDot, light.StopDot2));
    }

    [Fact]
    public void AConeWiderThanNinetyIsClampedAndWarned()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "1 1 1"), ("_light", "255 255 255"),
            ("_inner_cone", "100"), ("_cone", "120")));
        RadWorld world = LightBox.Build(map);
        DirectLight light = Assert.Single(world.Lights.Active);
        Assert.Equal((float)Math.Cos(90.0f / 180 * Math.PI), light.StopDot);
        Assert.Equal(2, world.Warnings.Count(w => w.Contains("larger than 90", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATargetAimsTheLight()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "info_target"), ("targetname", "t"), ("origin", "100 50 0")));
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "100 50 100"), ("target", "t"), ("_light", "255 255 255")));
        DirectLight light = Assert.Single(LightBox.Build(map).Lights.Active);
        Assert.Equal(new Vec3(0, 0, -1), light.Normal);
    }

    [Fact]
    public void AMissingTargetWarnsAndLeavesTheNormalZero()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_spot"), ("origin", "1 2 3"), ("target", "nobody"), ("_light", "255 255 255")));
        RadWorld world = LightBox.Build(map);
        Assert.Equal(Vec3.Zero, Assert.Single(world.Lights.Active).Normal);
        Assert.Contains(world.Warnings, w => w.Contains("missing target", StringComparison.Ordinal));
    }

    [Fact]
    public void HdrPrefersLightHdrWhenItParses()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255 100"), ("_lightHDR", "255 255 255 50")));
        RadWorld world = LightBox.Build(map, LightBox.Settings(hdr: true));
        Assert.Equal(50f, Assert.Single(world.Lights.Active).Intensity.X, 3);
    }

    [Fact]
    public void HdrFallsBackToLightWhenLightHdrIsNegative()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255 100"), ("_lightHDR", "-1 -1 -1 1")));
        RadWorld world = LightBox.Build(map, LightBox.Settings(hdr: true));
        Assert.Equal(100f, Assert.Single(world.Lights.Active).Intensity.X, 3);
    }

    [Fact]
    public void LightScaleHdrAppliesOnlyToTheHdrPass()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255 100"), ("_lightscaleHDR", "3")));
        Assert.Equal(100f, Assert.Single(LightBox.Build(map).Lights.Active).Intensity.X, 3);
        Assert.Equal(300f, Assert.Single(LightBox.Build(map, LightBox.Settings(hdr: true)).Lights.Active).Intensity.X, 3);
    }

    [Fact]
    public void ALightEnvironmentMakesASunAndAnAmbientAmbientFirst()
    {
        //: sun prepended, then ambient -> ambient heads the list.
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "1 1 1"), ("_light", "255 255 255 200"), ("pitch", "-45")));
        RadWorld world = LightBox.Build(map);
        Assert.Equal(2, world.Lights.Active.Count);
        Assert.Equal(EmitType.SkyAmbient, world.Lights.Active[0].Type);
        Assert.Equal(EmitType.SkyLight, world.Lights.Active[1].Type);
    }

    [Fact]
    public void AMissingAmbientIsHalfTheSun()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light_environment"), ("origin", "1 1 1"), ("_light", "255 255 255 200")));
        RadWorld world = LightBox.Build(map);
        Assert.Equal(world.Lights.SkyLight!.Intensity * 0.5f, world.Lights.Ambient!.Intensity);
    }

    [Fact]
    public void ASecondLightEnvironmentIsCountedButDropped()
    {
        //:1490-1491: allocated (numdlights counts it) but never listed.
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light_environment"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        map.Entities.Add(LightTestMap.Entity(("classname", "light_environment"), ("origin", "2 2 2"), ("_light", "255 0 0")));
        RadWorld world = LightBox.Build(map);
        Assert.Equal(2, world.Lights.Active.Count);
        Assert.Equal(3, world.Statistics.DirectLights);
        Assert.Equal(1, world.Lights.OrphanedSkyLights);
    }

    [Fact]
    public void SunSpreadAngleIsStoredAsASine()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(
            ("classname", "light_environment"), ("origin", "1 1 1"), ("_light", "255 255 255"), ("SunSpreadAngle", "5")));
        Assert.Equal((float)Math.Sin(Math.PI / 180.0 * 5), LightBox.Build(map).Lights.SunAngularExtent);
    }

    [Fact]
    public async Task AnEmissiveTextureMakesOneSurfaceLightPerLeafPatch()
    {
        LightTestMap map = LightBox.Map(ceilingMaterial: "lights/white");
        RadWorld world = LightBox.Build(map, texLights: await LightBox.TexLightsAsync("lights/white 255 255 255 200"));
        Assert.All(world.Lights.Active, l => Assert.Equal(EmitType.Surface, l.Type));
        Assert.Equal(world.Lights.SurfaceLights, world.Lights.Active.Count);
        Assert.True(world.Lights.Active.Count > 0);
    }

    [Fact]
    public async Task ASurfaceLightsIntensityCarriesAreaTextureScaleAndDirectScale()
    {
        //: baselight * lightscale * area * s0 * s1 /
        // basearea, then * 100*100. No vis -> bounces 0 -> one patch per face.
        LightTestMap map = LightBox.Map(ceilingMaterial: "lights/white");
        RadWorld world = LightBox.Build(map, texLights: await LightBox.TexLightsAsync("lights/white 255 255 255 255"));
        DirectLight light = Assert.Single(world.Lights.Active);
        float expected = 255f * 1f * (LightBox.Size * LightBox.Size) * 0.25f * 0.25f / (64f * 64f) * 10000f;
        Assert.Equal(expected, light.Intensity.X, 0);
    }

    [Fact]
    public async Task AnEmissiveTextureSetsSurfLightOnItsTexinfo()
    {
        // -- which is what later lets PreventSubdivision keep
        // a NOLIGHT emitter subdividable.
        LightTestMap map = LightBox.Map(ceilingMaterial: "lights/white");
        RadWorld world = LightBox.Build(map, texLights: await LightBox.TexLightsAsync("lights/white 255 255 255"));
        Assert.NotEqual(0, world.Geometry.TexInfos[1].Flags & (int)SurfaceFlags.Light);
    }
}

public sealed class WorldLightExportTests
{
    [Fact]
    public void IntensityIsDividedBy255()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "128 128 128"), ("_light", "255 255 255 255")));
        DWorldLight wl = Assert.Single(LightBox.Build(map).WorldLights);
        Assert.Equal(255f * (float)(1.0 / 255.0), wl.Intensity.X);
    }

    [Fact]
    public void EachRecordIsEightyEightBytes()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "2 2 2"), ("_light", "255 255 255")));
        Assert.Equal(176, LightBox.Build(map).WorldLightBytes().Length);
    }

    [Fact]
    public void FlagsTexinfoAndOwnerAreZero()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        DWorldLight wl = Assert.Single(LightBox.Build(map).WorldLights);
        Assert.Equal((0, 0, 0), (wl.Flags, wl.TexInfo, wl.Owner));
    }

    [Fact]
    public void TheClusterIsTheLightsOwn()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255")));
        Assert.Equal(0, Assert.Single(LightBox.Build(map).WorldLights).Cluster);
    }
}

public sealed class EntityKeyTests
{
    [Fact]
    public void TheLastDuplicateKeyWins()
    {
        // ParseEntities prepends epairs, so ValueForKey's
        // first match is the file's LAST.
        BspEntity e = LightTestMap.Entity(("style", "1"), ("style", "2"));
        Assert.Equal("2", EntityKeys.ValueForKey(e, "style"));
    }

    [Fact]
    public void AMissingKeyIsEmptyOrNullWithDefault()
    {
        BspEntity e = LightTestMap.Entity();
        Assert.Equal(string.Empty, EntityKeys.ValueForKey(e, "x"));
        Assert.Null(EntityKeys.ValueForKeyWithDefault(e, "x"));
        Assert.Equal(4f, EntityKeys.FloatForKeyWithDefault(e, "x", 4f));
    }

    [Fact]
    public void AVectorStopsAtTheFirstUnparsableField()
    {
        BspEntity e = LightTestMap.Entity(("origin", "1 2 x 4"));
        Assert.Equal(new Vec3(1, 2, 0), EntityKeys.GetVectorForKey(e, "origin"));
    }

    [Fact]
    public void AStyleIsReadAsAFloatAndTruncated()
    {
        LightTestMap map = LightBox.Map();
        map.Entities.Add(LightTestMap.Entity(("classname", "light"), ("origin", "1 1 1"), ("_light", "255 255 255"), ("style", "2.9")));
        Assert.Equal(2, Assert.Single(LightBox.Build(map).Lights.Active).Style);
    }
}

public sealed class TextureLightTableTests
{
    [Fact]
    public async Task LookupIsCaseInsensitive()
    {
        TextureLightTable t = await LightBox.TexLightsAsync("Lights/White 255 255 255");
        Assert.NotEqual(Vec3.Zero, t.Lookup("lights/white"));
    }

    [Fact]
    public async Task AnUnlistedMaterialIsDark() => Assert.Equal(Vec3.Zero, (await LightBox.TexLightsAsync()).Lookup("x"));

    [Fact]
    public void ACubemapPatchedNameIsTracedBackToItsOriginal()
    {
        //: maps/<level>/<name>_x_y_z -> <name>.
        TextureLightTable t = new(new RadLightFile(), "box");
        Assert.Equal("lights/white", t.Unpatch("maps/box/lights/white_1_2_3"));
    }

    [Fact]
    public void AnotherLevelsPatchedNameIsLeftAlone()
    {
        TextureLightTable t = new(new RadLightFile(), "box");
        Assert.Equal("maps/other/lights/white_1_2_3", t.Unpatch("maps/other/lights/white_1_2_3"));
    }

    [Fact]
    public void ANameWithTooFewUnderscoresIsLeftAlone()
    {
        TextureLightTable t = new(new RadLightFile(), "box");
        Assert.Equal("maps/box/white_1_2", t.Unpatch("maps/box/white_1_2"));
    }
}
