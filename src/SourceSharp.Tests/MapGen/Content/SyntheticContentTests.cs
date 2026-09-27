//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using System.Text;

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapGen.Content;

/// <summary>
/// <see cref="SyntheticContent"/>: each stand-in takes the branch it is there
/// for when the compile's own loaders read it.
/// </summary>
public sealed class SyntheticContentTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Files = new(SyntheticContent.Build);

    private static Task<ContentFileSystem> MountAsync() => StudioModelWriterTests.MountAsync(Files.Value);

    private static async Task<MaterialSurface> ClassifyAsync(ContentFileSystem content, string name) =>
        MaterialSurfaceClassifier.Classify(await MaterialFactsReader.ReadAsync(name, content));

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task EveryBrushMaterialOfTheSandboxIsFound()
    {
        VmfDocument map = await VmfDocument.ParseAsync(
            await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!));
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void Walk(VmfChunk chunk)
        {
            if (chunk.GetValue("material") is { } material && chunk.Name == "side")
            {
                names.Add(material);
            }

            foreach (VmfChunk child in chunk.Chunks)
            {
                Walk(child);
            }
        }

        foreach (VmfChunk chunk in map.Chunks)
        {
            Walk(chunk);
        }

        await using ContentFileSystem content = await MountAsync();
        Assert.NotEmpty(names);
        foreach (string name in names)
        {
            MaterialFacts facts = await MaterialFactsReader.ReadAsync(name, content);
            Assert.True(facts.Found, name);
        }
    }

    [Theory]
    [InlineData("TOOLS/TOOLSSKYBOX", SurfaceFlags.Sky)]
    [InlineData("TOOLS/TOOLSTRIGGER", SurfaceFlags.Trigger)]
    [InlineData("TOOLS/TOOLSNODRAW", SurfaceFlags.NoDraw)]
    [InlineData("wood/woodwall014a", SurfaceFlags.BumpLight)]
    [InlineData("glass/glasswindowbreak070a", SurfaceFlags.Trans)]
    public async Task ToolAndSurfaceMaterialsTakeTheirBranch(string name, SurfaceFlags expected)
    {
        await using ContentFileSystem content = await MountAsync();

        Assert.True((await ClassifyAsync(content, name)).Flags.HasFlag(expected));
    }

    [Theory]
    [InlineData("nature/water_canals_cheap001", BrushContents.Water)]
    [InlineData("glass/glasswindowbreak070a", BrushContents.Window)]
    public async Task WaterAndGlassSetTheirContents(string name, BrushContents expected)
    {
        await using ContentFileSystem content = await MountAsync();

        Assert.True((await ClassifyAsync(content, name)).Contents.HasFlag(expected));
    }

    [Fact]
    public async Task TexturesGiveSizeAndReflectivityAndAVarOverridesIt()
    {
        await using ContentFileSystem content = await MountAsync();
        MaterialFacts wood = await MaterialFactsReader.ReadAsync("wood/woodwall014a", content);
        MaterialFacts concrete = await MaterialFactsReader.ReadAsync("concrete/concretefloor001a", content);

        Assert.Equal(512, wood.Width);
        Assert.False(wood.ReflectivityFromVar);
        Assert.InRange(wood.Reflectivity.X, 0.01f, 0.99f);
        Assert.True(concrete.ReflectivityFromVar);
        Assert.Equal(0.25f, concrete.Reflectivity.X, 5);
    }

    [Fact]
    public async Task TheCarpetNamesTheDetailTypeDetailVbspDefines()
    {
        await using ContentFileSystem content = await MountAsync();
        MaterialFacts carpet = await MaterialFactsReader.ReadAsync("props/carpetfloor007a", content);
        KeyValuesDocument detail = await KeyValuesDocument.ParseAsync(Encoding.UTF8.GetString(Files.Value["detail.vbsp"]), null);

        Assert.Equal(SyntheticContent.DetailType, carpet.DetailType);
        Assert.True(DetailDictionary.Parse(detail.Root!).Find(SyntheticContent.DetailType) >= 0);
    }

    [Fact]
    public async Task TheSkyboxBuildsTheDefaultCubemapWithoutAWarning()
    {
        await using ContentFileSystem content = await MountAsync();
        List<CompileDiagnostic> diagnostics = [];

        byte[]? cubemap = await DefaultCubemapBuilder.BuildAsync(
            "sky_day01_01", false, new MaterialFactsCache(content), content, diagnostics);

        Assert.NotNull(cubemap);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task LightsRadMakesTheComputerPanelATexlight()
    {
        RadLightFile rad = await RadLightFile.ParseAsync(Files.Value["lights.rad"]);

        Assert.NotNull(rad.Lookup("halflife/lab1_cmpm5"));
        Assert.Contains("models/props_junk/wood_crate001a.mdl", rad.ForcedTextureShadowModels);
    }

    [Fact]
    public async Task EverySurfacePropTheContentNamesIsInTheTable()
    {
        await using ContentFileSystem content = await MountAsync();
        SurfacePropertyTable table = await SurfacePropertyTable.LoadAsync(content);

        foreach (string prop in new[] { "wood", "carpet", "tile", "plaster", "metal", "concrete", "glass", "water",
                     "computer", "wood_crate", "popcan", "metal_barrel", "wood_furniture" })
        {
            Assert.True(table.GetSurfaceIndex(prop) >= 0, prop);
        }
    }

    [Theory]
    [InlineData("models/props_junk/wood_crate001a.mdl", StudioModelRejection.None)]
    [InlineData("models/props_junk/PopCan01a.mdl", StudioModelRejection.None)]
    [InlineData("models/props_c17/oildrum001.mdl", StudioModelRejection.None)]
    [InlineData("models/props_c17/FurnitureChair001a.mdl", StudioModelRejection.None)]
    [InlineData("models/props_junk/propanecanister001a.mdl", StudioModelRejection.None)]
    [InlineData("models/props_junk/metal_paintcan001a.mdl", StudioModelRejection.None)]
    [InlineData("models/props_c17/oildrum001_explosive.mdl", StudioModelRejection.NotStaticProp)]
    [InlineData("models/props_doors/door01_dynamic.mdl", StudioModelRejection.DynamicOnly)]
    public async Task EachModelTakesItsStaticPropBranch(string model, StudioModelRejection expected)
    {
        await using ContentFileSystem content = await MountAsync();

        StudioModelLoad load = await StudioModelCheck.LoadAsync(content, model, "prop_static", [], ComplianceOptions.Correct);

        Assert.Equal(expected, load.Rejection);
    }

    [Fact]
    public void TheContentIsTheSameBytesEveryTime()
    {
        IReadOnlyDictionary<string, byte[]> again = SyntheticContent.Build();

        Assert.Equal(Files.Value.Keys, again.Keys);
        Assert.All(Files.Value, kv => Assert.Equal(kv.Value, again[kv.Key]));
    }

    [Fact]
    public async Task TheStaticPropVariantAddsATwinForEachModelEntity()
    {
        VmfDocument map = new();
        VmfChunk world = new("world");
        world.AddKey("id", "1");
        map.Chunks.Add(world);
        map.Chunks.Add(Entity("7", "prop_physics", "models/a.mdl", "1 2 3", "0 90 0"));
        map.Chunks.Add(Entity("9", "prop_dynamic", "models/b.mdl", "4 5 6", null));
        map.Chunks.Add(Entity("10", "info_target", "models/c.mdl", "0 0 0", null));
        map.Chunks.Add(Entity("11", "prop_physics", "sprites/glow.spr", "0 0 0", null));

        (VmfDocument variant, int added) = await SyntheticContent.WithStaticPropsAsync(map);
        List<VmfChunk> statics = [.. variant.GetChunks("entity").Where(e => e.GetValue("classname") == "prop_static")];

        Assert.Equal(2, added);
        Assert.Equal(["models/a.mdl", "models/b.mdl"], statics.Select(e => e.GetValue("model")));
        Assert.Equal("1 2 3", statics[0].GetValue("origin"));
        Assert.Equal("0 90 0", statics[0].GetValue("angles"));
        Assert.Equal("0 0 0", statics[1].GetValue("angles"));
        Assert.Equal(["12", "13"], statics.Select(e => e.GetValue("id")));
        Assert.Equal(5, map.Chunks.Count);
    }

    private static VmfChunk Entity(string id, string classname, string model, string origin, string? angles)
    {
        VmfChunk e = new("entity");
        e.AddKey("id", id);
        e.AddKey("classname", classname);
        e.AddKey("model", model);
        e.AddKey("origin", origin);
        if (angles is not null)
        {
            e.AddKey("angles", angles);
        }

        return e;
    }
}
