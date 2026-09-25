using System.Text;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.MaterialPatch;

/// <summary>
/// <c>utils/vbsp/materialpatch.cpp</c> and the pak and KeyValues behaviours
/// it rests on, each fact derived from the C++ at the cited line.
/// </summary>
public class MaterialPatcherTests
{
    private const string Specular =
        "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"Metal/metalwall058a\"\n\t\"$envmap\" \"env_cubemap\"\n" +
        "\t\"$envmaptint\" \"[ .5 .5 .5]\"\n}\n";

    // The shape of nature/water_canals_cheap001: $envmap at the top, an
    // empty-bodied fallback section, and a Proxies section of empty sections.
    private const string WaterLike =
        "\"Water\"\n{\n\t\"Water_DX60\"\n\t{\n\t\t\"$fallbackmaterial\" \"x_dx70\"\n\t}\n" +
        "\t\"$envmap\" \"env_cubemap\"\n\t\"$bottommaterial\" \"dev/dev_waterbeneath2\"\n" +
        "\t\"Proxies\"\n\t{\n\t\t\"AnimatedTexture\"\n\t\t{\n\t\t\t\"animatedtexturevar\" \"$normalmap\"\n\t\t}\n" +
        "\t\t\"WaterLOD\"\n\t\t{\n\t\t}\n\t}\n}\n";

    private const string Patch =
        "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$envmaptint\" \"[.25 .25 .25]\"\n\t}\n}\n";

    [Fact]
    public void PakNamesAreLowerCased()
    {
        // zip_utils.cpp:1000-1002, AddBufferToZip: "Lower case only".
        MapPakFile pak = new();
        pak.Add("Materials/Maps/X/Cubemapdefault.VTF", [1], textMode: false);

        Assert.Equal("materials/maps/x/cubemapdefault.vtf", Assert.Single(pak.Entries).Name);
    }

    [Fact]
    public void PakLookupIgnoresCase()
    {
        // zip_utils.cpp:1227-1230, FileExistsInZip lowers the query too.
        MapPakFile pak = new();
        pak.Add("materials/a.vtf", [1], textMode: false);

        Assert.True(pak.Contains("MATERIALS/A.VTF"));
    }

    [Fact]
    public void PakTextModeStoresCrLf()
    {
        // zip_utils.cpp:958-985, CopyTextData.
        MapPakFile pak = new();
        pak.Add("a.vmt", "x\ny\n"u8, textMode: true);

        Assert.Equal("x\r\ny\r\n"u8.ToArray(), pak.Entries[0].Data);
    }

    [Fact]
    public void PakTextReadDropsTheCrBeforeEachLf()
    {
        // zip_utils.cpp:931-952, ReadTextData.
        MapPakFile pak = new();
        pak.Add("a.vmt", "x\ny\n"u8, textMode: true);

        Assert.Equal("x\ny\n"u8.ToArray(), pak.Read("a.vmt", textMode: true));
    }

    [Fact]
    public void PakAddOfAnExistingNameReplacesItInPlace()
    {
        // zip_utils.cpp:988, "Adds a new lump, or overwrites existing one".
        MapPakFile pak = new();
        pak.Add("a", [1], textMode: false);
        pak.Add("b", [2], textMode: false);
        pak.Add("A", [3], textMode: false);

        Assert.Equal(["a", "b"], pak.Entries.Select(e => e.Name));
        Assert.Equal([3], pak.Entries[0].Data);
    }

    [Fact]
    public void SaveDropsAnEmptySectionBelowTheRoot()
    {
        // KeyValues.cpp:856-862: SaveKeyToFile recurses only on m_pSub; an
        // empty TYPE_NONE key matches no case of the switch.
        KeyValuesNode root = new("patch");
        root.Children.Add(new KeyValuesNode("empty"));

        Assert.Equal("\"patch\"\n{\n}\n", StockKeyValues.Save(root));
    }

    [Fact]
    public void SaveWritesASectionOfEmptySectionsAsEmptyBraces()
    {
        KeyValuesNode root = new("patch");
        KeyValuesNode proxies = StockKeyValues.FindOrCreate(root, "Proxies");
        StockKeyValues.FindOrCreate(proxies, "WaterLOD");

        Assert.Equal("\"patch\"\n{\n\t\"Proxies\"\n\t{\n\t}\n}\n", StockKeyValues.Save(root));
    }

    [Fact]
    public void SaveDropsAnEmptyString()
    {
        // KeyValues.cpp:871, bAllowEmptyString defaults to false.
        KeyValuesNode root = new("r");
        StockKeyValues.SetString(root, "k", string.Empty);

        Assert.Equal("\"r\"\n{\n}\n", StockKeyValues.Save(root));
    }

    [Fact]
    public void SaveEscapesQuotesButNotBackslashes()
    {
        // KeyValues.cpp:785-794.
        KeyValuesNode root = new("r");
        StockKeyValues.SetString(root, "k", "a\"b\\c");

        Assert.Equal("\"r\"\n{\n\t\"k\"\t\t\"a\\\"b\\c\"\n}\n", StockKeyValues.Save(root));
    }

    [Fact]
    public void SetStringReusesTheFirstMatchIgnoringCase()
    {
        KeyValuesNode root = new("r");
        StockKeyValues.SetString(root, "$EnvMap", "a");
        StockKeyValues.SetString(root, "$envmap", "b");

        Assert.Equal("b", Assert.Single(root.Children).Value);
    }

    [Fact]
    public void GetStringOfASectionIsNull()
    {
        // KeyValues.cpp:1453-1454, TYPE_NONE returns the default.
        KeyValuesNode root = new("r");
        StockKeyValues.FindOrCreate(root, "s");

        Assert.Null(StockKeyValues.GetString(root, "s"));
    }

    [Fact]
    public async Task AReplacePatchIsByteForByteWhatStockWrote()
    {
        // Stock's l1_env_cubemap pak holds exactly this for its first sample
        // (materials/maps/l1_env_cubemap/metal/metalwall058a_-128_0_128.vmt).
        MaterialPatcher patcher = await PatcherAsync(("metal/metalwall058a", Specular));

        await patcher.CreatePatchAsync(
            "metal/metalwall058a",
            "maps/l1_env_cubemap/metal/metalwall058a_-128_0_128",
            [new MaterialPatchInfo("$envmap", "maps/l1_env_cubemap/c-128_0_128", "env_cubemap")],
            MaterialPatchType.Replace);

        Assert.Equal(
            "\"patch\"\r\n{\r\n\t\"include\"\t\t\"materials/metal/metalwall058a.vmt\"\r\n\t\"replace\"\r\n\t{\r\n" +
            "\t\t\"$envmap\"\t\t\"maps/l1_env_cubemap/c-128_0_128\"\r\n\t}\r\n}\r\n",
            Encoding.Latin1.GetString(Assert.Single(patcher.Pak.Entries).Data));
    }

    [Fact]
    public async Task AReplacePatchMirrorsEverySectionAndTheWriterDropsTheEmptyOnes()
    {
        // materialpatch.cpp:82-85 creates a same-named key for EVERY true sub
        // key. Stock's water patch in l2_cubemap_on_water_and_patch is exactly
        // this: Proxies survives as { } because it has (empty) children, and
        // Water_DX60 vanishes.
        MaterialPatcher patcher = await PatcherAsync(("nature/w", WaterLike));

        await patcher.CreatePatchAsync(
            "nature/w",
            "maps/m/nature/w_0_0_160",
            [new MaterialPatchInfo("$envmap", "maps/m/c0_0_160", "env_cubemap")],
            MaterialPatchType.Replace);

        Assert.Equal(
            "\"patch\"\r\n{\r\n\t\"include\"\t\t\"materials/nature/w.vmt\"\r\n\t\"replace\"\r\n\t{\r\n" +
            "\t\t\"$envmap\"\t\t\"maps/m/c0_0_160\"\r\n\t\t\"Proxies\"\r\n\t\t{\r\n\t\t}\r\n\t}\r\n}\r\n",
            Encoding.Latin1.GetString(patcher.Pak.Entries[0].Data));
    }

    [Fact]
    public async Task AReplaceSkipsAKeyWhoseValueIsNotTheRequiredOne()
    {
        // materialpatch.cpp:77.
        MaterialPatcher patcher = await PatcherAsync(
            ("m/a", "\"LightmappedGeneric\"\n{\n\t\"$envmap\" \"some/texture\"\n}\n"));

        await patcher.CreatePatchAsync(
            "m/a", "maps/x/m/a_1_2_3", [new MaterialPatchInfo("$envmap", "c", "env_cubemap")], MaterialPatchType.Replace);

        Assert.DoesNotContain("$envmap", Encoding.Latin1.GetString(patcher.Pak.Entries[0].Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInsertPatchWritesEveryKey()
    {
        // materialpatch.cpp:131-137: the depth patch shape of ivp.cpp:815.
        MaterialPatcher patcher = await PatcherAsync();

        await patcher.CreatePatchAsync(
            "nature/water", "maps/m/nature/water_depth_64",
            [new MaterialPatchInfo("$waterdepth", "64")], MaterialPatchType.Insert);

        Assert.Equal(
            "\"patch\"\r\n{\r\n\t\"include\"\t\t\"materials/nature/water.vmt\"\r\n\t\"insert\"\r\n\t{\r\n" +
            "\t\t\"$waterdepth\"\t\t\"64\"\r\n\t}\r\n}\r\n",
            Encoding.Latin1.GetString(patcher.Pak.Entries[0].Data));
    }

    [Fact]
    public async Task APatchIsWrittenUnderMaterialsWithAVmtExtension()
    {
        MaterialPatcher patcher = await PatcherAsync();

        await patcher.CreatePatchAsync("a", "maps/m/A_1", [new MaterialPatchInfo("k", "v")], MaterialPatchType.Insert);

        Assert.Equal("materials/maps/m/a_1.vmt", patcher.Pak.Entries[0].Name);
    }

    [Fact]
    public async Task CreatingAPatchRegistersItsOriginal()
    {
        // materialpatch.cpp:97, AddNewTranslation.
        MaterialPatcher patcher = await PatcherAsync();

        await patcher.CreatePatchAsync("a", "b", [new MaterialPatchInfo("k", "v")], MaterialPatchType.Insert);

        Assert.Equal("a", patcher.OriginalNameFor("b"));
    }

    [Fact]
    public void TheOriginalOfAPatchOfAPatchIsTheFirstMaterial()
    {
        // materialpatch.cpp:50-59 loops until the name is not a patch.
        MaterialPatcher patcher = new(EmptyContent(), new MapPakFile());
        patcher.AddTranslation("a", "b");
        patcher.AddTranslation("b", "c");

        Assert.Equal("a", patcher.OriginalNameFor("c"));
    }

    [Fact]
    public void TheOriginalLookupIgnoresCase()
    {
        // materialpatch.cpp:18, the symbol table is case-insensitive.
        MaterialPatcher patcher = new(EmptyContent(), new MapPakFile());
        patcher.AddTranslation("a", "Maps/B");

        Assert.Equal("a", patcher.OriginalNameFor("maps/b"));
    }

    [Fact]
    public void ANameThatIsNotAPatchIsItsOwnOriginal()
    {
        MaterialPatcher patcher = new(EmptyContent(), new MapPakFile());

        Assert.Equal("x", patcher.OriginalNameFor("x"));
    }

    [Fact]
    public void ATranslationCycleStops()
    {
        // Stock would spin forever; a cycle is bounded by the table size.
        MaterialPatcher patcher = new(EmptyContent(), new MapPakFile());
        patcher.AddTranslation("a", "b");
        patcher.AddTranslation("b", "a");

        Assert.Contains(patcher.OriginalNameFor("a"), new[] { "a", "b" });
    }

    [Fact]
    public async Task AReplaceOfAMissingOriginalWritesNothingButKeepsTheTranslation()
    {
        // materialpatch.cpp:97 registers first; :121-126 then return early.
        MaterialPatcher patcher = await PatcherAsync();

        bool written = await patcher.CreatePatchAsync(
            "gone", "maps/m/gone_1_2_3", [new MaterialPatchInfo("$envmap", "c", "env_cubemap")], MaterialPatchType.Replace);

        Assert.False(written);
        Assert.Equal(0, patcher.Pak.Count);
        Assert.Equal("gone", patcher.OriginalNameFor("maps/m/gone_1_2_3"));
    }

    [Fact]
    public async Task HasKeyFindsAKeyInANestedSection()
    {
        // materialpatch.cpp:174-178 recurses through true sub keys.
        MaterialPatcher patcher = await PatcherAsync(("nature/w", WaterLike));

        Assert.True(await patcher.HasKeyAsync("nature/w", "$fallbackmaterial"));
    }

    [Fact]
    public async Task UnderStockHasKeyReadsAPatchRawSoItsIncludesKeysAreInvisible()
    {
        // materialpatch.cpp:211: LoadFromFile, no ExpandPatchFile. The insert
        // section is a true sub key, so a key inside IT is found -- but the
        // root-level $envmap the include carries is not.
        MaterialPatcher patcher = await StockPatcherAsync(("metal/specular", Specular), ("p/patched", Patch));

        Assert.False(await patcher.HasKeyAsync("p/patched", "$envmap"));
    }

    [Fact]
    public async Task HasKeySeesThroughAPatchWhenTheQuirkIsCorrected()
    {
        // StockQuirk.CubemapIgnoresPatchMaterials.
        MaterialPatcher patcher = await PatcherAsync(("metal/specular", Specular), ("p/patched", Patch));

        Assert.True(await patcher.HasKeyAsync("p/patched", "$envmap"));
    }

    [Fact]
    public async Task HasKeyValuePairComparesTheValueIgnoringCase()
    {
        MaterialPatcher patcher = await PatcherAsync(
            ("m/a", "\"LightmappedGeneric\"\n{\n\t\"$envmap\" \"ENV_Cubemap\"\n}\n"));

        Assert.True(await patcher.HasKeyValuePairAsync("m/a", "$envmap", "env_cubemap"));
    }

    [Fact]
    public async Task HasKeyReadsTheOriginalOfAPatchedName()
    {
        // materialpatch.cpp:208 maps the name back first.
        MaterialPatcher patcher = await PatcherAsync(("metal/specular", Specular));
        patcher.AddTranslation("metal/specular", "maps/m/metal/specular_1_2_3");

        Assert.True(await patcher.HasKeyAsync("maps/m/metal/specular_1_2_3", "$envmap"));
    }

    [Fact]
    public async Task GetValueReadsTheTopLevelOnly()
    {
        // materialpatch.cpp:260: kv->GetString, no recursion.
        MaterialPatcher patcher = await PatcherAsync(("nature/w", WaterLike));

        Assert.Null(await patcher.GetValueAsync("nature/w", "$fallbackmaterial"));
        Assert.Equal("dev/dev_waterbeneath2", await patcher.GetValueAsync("nature/w", "$bottommaterial"));
    }

    [Fact]
    public async Task ExpandingAnInsertPatchAddsItsKeysToTheInclude()
    {
        MaterialPatcher patcher = await PatcherAsync(("metal/specular", Specular), ("p/patched", Patch));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/patched", expandPatch: true))!;

        Assert.Equal("LightmappedGeneric", expanded.Name);
        Assert.Equal("[.25 .25 .25]", StockKeyValues.GetString(expanded, "$envmaptint"));
    }

    [Fact]
    public async Task ExpandingAReplacePatchSetsOnlyKeysTheIncludeHas()
    {
        // materialpatch.cpp:305, bCheckForExistence.
        MaterialPatcher patcher = await PatcherAsync(
            ("metal/specular", Specular),
            ("p/r", "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n\t\"replace\"\n\t{\n" +
                    "\t\t\"$envmap\" \"new\"\n\t\t\"$absent\" \"1\"\n\t}\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/r", expandPatch: true))!;

        Assert.Equal("new", StockKeyValues.GetString(expanded, "$envmap"));
        Assert.Null(StockKeyValues.GetString(expanded, "$absent"));
    }

    [Fact]
    public async Task UnderStockAPatchWithInsertAndReplaceLosesItsReplace()
    {
        // materialpatch.cpp:352 reassigns keyValues to the include BEFORE the
        // replace lookup at :355, which then searches the include.
        MaterialPatcher patcher = await StockPatcherAsync(
            ("metal/specular", Specular),
            ("p/both", "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$a\" \"1\"\n\t}\n" +
                       "\t\"replace\"\n\t{\n\t\t\"$envmap\" \"new\"\n\t}\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/both", expandPatch: true))!;

        Assert.Equal("env_cubemap", StockKeyValues.GetString(expanded, "$envmap"));
    }

    [Fact]
    public async Task UnderStockAPatchWithNeitherSectionStaysAPatch()
    {
        // materialpatch.cpp:347-359 reassigns only inside the branches.
        MaterialPatcher patcher = await StockPatcherAsync(
            ("metal/specular", Specular),
            ("p/empty", "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/empty", expandPatch: true))!;

        Assert.Equal("patch", expanded.Name);
    }

    [Fact]
    public async Task APatchWhoseIncludeIsMissingStaysAPatch()
    {
        // materialpatch.cpp:341-346.
        MaterialPatcher patcher = await PatcherAsync(
            ("p/orphan", "\"patch\"\n{\n\t\"include\" \"materials/nothing.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$a\" \"1\"\n\t}\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/orphan", expandPatch: true))!;

        Assert.Equal("patch", expanded.Name);
    }

    [Fact]
    public async Task ThePakIsReadBeforeTheFileSystem()
    {
        // materialpatch.cpp:288-293.
        MaterialPatcher patcher = await PatcherAsync(("m/a", "\"FromDisk\"\n{\n}\n"));
        patcher.Pak.Add("materials/m/a.vmt", "\"FromPak\"\n{\n}\n"u8, textMode: true);

        KeyValuesNode loaded = (await patcher.LoadMaterialKeyValuesAsync("m/a", expandPatch: false))!;

        Assert.Equal("FromPak", loaded.Name);
    }

    [Fact]
    public async Task GetValueFromPatchedMaterialReadsThroughAPatchInThePak()
    {
        // materialpatch.cpp:416-440, as faces.cpp:1264 uses it for water.
        MaterialPatcher patcher = await PatcherAsync(("nature/w", WaterLike));
        await patcher.CreatePatchAsync(
            "nature/w", "maps/m/nature/w_1_2_3",
            [new MaterialPatchInfo("$envmap", "maps/m/c1_2_3", "env_cubemap")], MaterialPatchType.Replace);

        Assert.Equal(
            "dev/dev_waterbeneath2",
            await patcher.GetValueFromPatchedMaterialAsync("maps/m/nature/w_1_2_3", "$bottommaterial"));
    }

    [Fact]
    public async Task WritingMaterialKeyValuesUsesTheMaterialsPathInTextMode()
    {
        // materialpatch.cpp:396-410.
        MaterialPatcher patcher = await PatcherAsync();
        KeyValuesNode material = new("LightmappedGeneric");
        StockKeyValues.SetString(material, "$a", "1");

        patcher.WriteMaterialKeyValuesToPak("maps/m/x_wvt_patch", material);

        Assert.Equal("materials/maps/m/x_wvt_patch.vmt", patcher.Pak.Entries[0].Name);
        Assert.Equal("\"LightmappedGeneric\"\r\n{\r\n\t\"$a\"\t\t\"1\"\r\n}\r\n", Encoding.Latin1.GetString(patcher.Pak.Entries[0].Data));
    }

    [Fact]
    public async Task APatchWithInsertAndReplaceKeepsBothWhenTheQuirkIsCorrected()
    {
        // StockQuirk.PatchExpandInsertDropsReplace.
        MaterialPatcher patcher = await PatcherAsync(
            ("metal/specular", Specular),
            ("p/both", "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$a\" \"1\"\n\t}\n" +
                       "\t\"replace\"\n\t{\n\t\t\"$envmap\" \"new\"\n\t}\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/both", expandPatch: true))!;

        Assert.Equal("new", StockKeyValues.GetString(expanded, "$envmap"));
        Assert.Equal("1", StockKeyValues.GetString(expanded, "$a"));
    }

    [Fact]
    public async Task APatchWithNeitherSectionIsItsIncludeWhenTheQuirkIsCorrected()
    {
        // StockQuirk.PatchExpandEmptyPatchNeverResolves.
        MaterialPatcher patcher = await PatcherAsync(
            ("metal/specular", Specular),
            ("p/empty", "\"patch\"\n{\n\t\"include\" \"materials/metal/specular.vmt\"\n}\n"));

        KeyValuesNode expanded = (await patcher.LoadMaterialKeyValuesAsync("p/empty", expandPatch: true))!;

        Assert.Equal("LightmappedGeneric", expanded.Name);
    }

    [Fact]
    public async Task AReplacePatchOfAPatchWalksTheExpandedMaterialWhenCorrected()
    {
        // The corrected CubemapIgnoresPatchMaterials: the include is the patch
        // itself, the replace section what the expanded material has.
        MaterialPatcher patcher = await PatcherAsync(("metal/specular", Specular), ("p/patched", Patch));

        await patcher.CreatePatchAsync(
            "p/patched", "maps/m/p/patched_1_2_3",
            [new MaterialPatchInfo("$envmap", "maps/m/c1_2_3", "env_cubemap")], MaterialPatchType.Replace);

        Assert.Equal(
            "\"patch\"\r\n{\r\n\t\"include\"\t\t\"materials/p/patched.vmt\"\r\n\t\"replace\"\r\n\t{\r\n" +
            "\t\t\"$envmap\"\t\t\"maps/m/c1_2_3\"\r\n\t}\r\n}\r\n",
            Encoding.Latin1.GetString(patcher.Pak.Entries[0].Data));
    }

    internal static async Task<MaterialPatcher> StockPatcherAsync(params (string Name, string Text)[] materials)
        => new(await ContentAsync(materials), new MapPakFile(), ComplianceOptions.Stock);

    internal static async Task<MaterialPatcher> PatcherAsync(params (string Name, string Text)[] materials)
        => new(await ContentAsync(materials), new MapPakFile());

    internal static async Task<IContentFileSystem> ContentAsync(params (string Name, string Text)[] materials)
    {
        InMemoryFileSystem files = new();
        foreach ((string name, string text) in materials)
        {
            files.AddText($"materials/{name}.vmt", text);
        }

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return new ContentFileSystem([mount]);
    }

    private static IContentFileSystem EmptyContent() => new ContentFileSystem([]);
}
