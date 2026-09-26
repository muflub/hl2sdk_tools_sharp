//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// Facts for VMT <c>patch</c> resolution, from the TWO sides of the
/// reference implementation: what a map compile does and what the game
/// does.
/// </summary>
public class VmtPatchTests
{
    private static VmtDocument Vmt(string text) =>
        VmtDocument.ParseAsync(text, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// A loader over an in-memory set of materials, standing in for the
    /// caller's file system.
    /// </summary>
    private static Func<string, CancellationToken, Task<VmtDocument?>> Loader(
        Dictionary<string, string> files) =>
        (path, _) => Task.FromResult(
            files.TryGetValue(path, out string? text) ? Vmt(text) : null);

    private static Task<VmtDocument> ResolveAsync(
        string patch,
        Dictionary<string, string> files,
        VmtPatchDialect dialect) =>
        VmtPatchResolver.ResolveAsync(Vmt(patch), Loader(files), dialect, CancellationToken.None);

    [Fact]
    public void RootNamedPatchIsRecognisedWithoutRegardToCase()
    {
        // And the reference implementation both use a
        // case-insensitive compare.
        Assert.True(Vmt("\"Patch\"\n{\n}\n").IsPatch);
    }

    [Fact]
    public void ARegularMaterialIsNotAPatch()
    {
        Assert.False(Vmt("\"LightmappedGeneric\"\n{\n}\n").IsPatch);
    }

    [Fact]
    public void IncludePathIsUsedVerbatimWithNoPrefixAndNoExtension()
    {
        // And the reference implementation both hand the
        // value straight to LoadFromFile. Nothing prepends "materials/" and
        // nothing appends ".vmt", which is why a real patch spells the whole
        // path out.
        Assert.Equal(
            "materials/nature/blendrocks.vmt",
            Vmt("\"patch\"\n{\n\t\"include\" \"materials/nature/blendrocks.vmt\"\n}\n").IncludePath);
    }

    [Fact]
    public async Task ResolvedRootTakesTheIncludedMaterialsShaderName()
    {
        // Assigns the base wholesale, name included -- the
        // string "patch" never survives resolution. vbsp's does the same
        // through `keyValues = *includeKeyValues`.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$x\" \"1\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Equal("LightmappedGeneric", resolved.ShaderName);
    }

    [Fact]
    public async Task InsertAddsAKeyTheBaseDidNotHave()
    {
        // With bCheckForExistence false: SET, unconditional.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$waterdepth\" \"128\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Equal("128", resolved.Root.GetString("$waterdepth"));
    }

    [Fact]
    public async Task InsertAlsoOVERWRITESAKeyTheBaseAlreadyHad()
    {
        // The part the name gets wrong: "insert" is not "add if absent".
        // The reference implementation's gate is `!bCheckForExistence ||...`, so with
        // the flag false every key is written.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$basetexture\" \"new\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"old\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Equal("new", resolved.Root.GetString("$basetexture"));
    }

    [Fact]
    public async Task ReplaceOverwritesAKeyThatAlreadyExists()
    {
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n\t\"replace\"\n\t{\n\t\t\"$envmap\" \"env_cubemap\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$envmap\" \"old\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Equal("env_cubemap", resolved.Root.GetString("$envmap"));
    }

    [Fact]
    public async Task ReplaceSkipsAKeyTheBaseDoesNotHave()
    {
        // With bCheckForExistence true -- silently skipped,
        // not added.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n\t\"replace\"\n\t{\n\t\t\"$envmap\" \"env_cubemap\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Null(resolved.Root.GetString("$envmap"));
    }

    [Fact]
    public async Task CompilerLosesTheReplaceBlockWhenAnInsertBlockIsAlsoPresent()
    {
        // THE BUG, and it changes what a compiled map contains.
        // Applies the insert, then does
        // `keyValues = *includeKeyValues` -- replacing the whole object --
        // so the FindKey("replace") searches the BASE MATERIAL rather
        // than the patch, and the patch's replace block is never applied.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n" +
            "\t\"include\" \"materials/a.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"$waterdepth\" \"128\"\n\t}\n" +
            "\t\"replace\"\n\t{\n\t\t\"$basetexture\" \"replaced\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"original\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Equal("128", resolved.Root.GetString("$waterdepth"));
        Assert.Equal("original", resolved.Root.GetString("$basetexture"));
    }

    [Fact]
    public async Task EngineAppliesBothBlocksWhenBothArePresent()
    {
        // The other side of the same input. The reference implementation caches both
        // section pointers BEFORE applying either, so nothing is lost, and
        // applies insert then replace.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n" +
            "\t\"include\" \"materials/a.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"$waterdepth\" \"128\"\n\t}\n" +
            "\t\"replace\"\n\t{\n\t\t\"$basetexture\" \"replaced\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"original\"\n}\n",
            },
            VmtPatchDialect.Engine);

        Assert.Equal("128", resolved.Root.GetString("$waterdepth"));
        Assert.Equal("replaced", resolved.Root.GetString("$basetexture"));
    }

    [Fact]
    public async Task CompilerDropsANestedBlockInsideInsert()
    {
        // The switch covers string, int, float and
        // pointer and has NO subkey case, so a nested block is silently lost.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"Proxies\"\n\t\t{\n\t\t\t\"$p\" \"1\"\n\t\t}\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.Null(resolved.Root.Find("Proxies"));
    }

    [Fact]
    public async Task EngineKeepsANestedBlockInsideInsert()
    {
        // Recurses into the TYPE_NONE case.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"Proxies\"\n\t\t{\n\t\t\t\"$p\" \"1\"\n\t\t}\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Engine);

        Assert.Equal("1", resolved.Root.Find("Proxies")!.GetString("$p"));
    }

    [Fact]
    public async Task EngineStampsThePatchDummyIntoABlockThatEndsUpEmpty()
    {
        // A recursive call that left a block with no
        // children adds "__vmtpatchdummy" so it is not pruned. vbsp has no
        // equivalent, so a compiled map never contains one.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"Proxies\"\n\t\t{\n\t\t}\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Engine);

        Assert.Equal("1", resolved.Root.Find("Proxies")!.GetString(VmtPatchResolver.PatchDummyKey));
    }

    [Fact]
    public async Task EngineLetsTheDeeperPatchsValueWin()
    {
        // Accumulates the OUTER patch first, and
        // MergeKeyValues (, documented) OVERWRITES
        // so the inner level, merged second, wins.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/outer.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"$x\" \"outer\"\n\t}\n}\n",
            new Dictionary<string, string>
            {
                ["materials/outer.vmt"] =
                    "\"patch\"\n{\n\t\"include\" \"materials/base.vmt\"\n" +
                    "\t\"insert\"\n\t{\n\t\t\"$x\" \"inner\"\n\t}\n}\n",
                ["materials/base.vmt"] = "\"Water\"\n{\n}\n",
            },
            VmtPatchDialect.Engine);

        Assert.Equal("inner", resolved.Root.GetString("$x"));
    }

    [Fact]
    public async Task CompilerDropsTheOuterPatchsKeysAcrossTwoLevels()
    {
        // The real multi-level divergence, and it is not "which value wins":
        // it is that the outer patch's keys DISAPPEAR.
        //
        // vbsp applies each level as it walks, so
        // the outer patch's insert is written onto the INNER PATCH's root --
        // which is then thrown away when the next iteration reassigns
        // `keyValues` from the base. Only the innermost patch's keys reach the
        // material.
        Dictionary<string, string> files = new()
        {
            ["materials/outer.vmt"] =
                "\"patch\"\n{\n\t\"include\" \"materials/base.vmt\"\n" +
                "\t\"insert\"\n\t{\n\t\t\"$b\" \"inner\"\n\t}\n}\n",
            ["materials/base.vmt"] = "\"Water\"\n{\n}\n",
        };

        const string outerPatch =
            "\"patch\"\n{\n\t\"include\" \"materials/outer.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"$a\" \"outer\"\n\t}\n}\n";

        VmtDocument compiled = await ResolveAsync(outerPatch, files, VmtPatchDialect.Compiler);

        Assert.Equal("inner", compiled.Root.GetString("$b"));
        Assert.Null(compiled.Root.GetString("$a"));
    }

    [Fact]
    public async Task EngineKeepsBothLevelsKeysAcrossTwoLevels()
    {
        // The same file through the game side. The reference implementation accumulates
        // every level's sections before applying anything, so both survive.
        Dictionary<string, string> files = new()
        {
            ["materials/outer.vmt"] =
                "\"patch\"\n{\n\t\"include\" \"materials/base.vmt\"\n" +
                "\t\"insert\"\n\t{\n\t\t\"$b\" \"inner\"\n\t}\n}\n",
            ["materials/base.vmt"] = "\"Water\"\n{\n}\n",
        };

        const string outerPatch =
            "\"patch\"\n{\n\t\"include\" \"materials/outer.vmt\"\n" +
            "\t\"insert\"\n\t{\n\t\t\"$a\" \"outer\"\n\t}\n}\n";

        VmtDocument engine = await ResolveAsync(outerPatch, files, VmtPatchDialect.Engine);

        Assert.Equal("inner", engine.Root.GetString("$b"));
        Assert.Equal("outer", engine.Root.GetString("$a"));
    }

    [Fact]
    public async Task CompilerNeverAdvancesPastAPatchThatHasNeitherSection()
    {
        // Has no else: with neither an insert nor a
        // replace, `keyValues` is never reassigned, so the loop spins on the
        // same patch until the counter runs out and warns. A "helpful" port
        // that assigned unconditionally would silently accept a patch stock
        // rejects.
        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/a.vmt\"\n}\n",
            new Dictionary<string, string>
            {
                ["materials/a.vmt"] = "\"Water\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            },
            VmtPatchDialect.Compiler);

        Assert.True(resolved.IsPatch);
    }

    [Fact]
    public async Task NonPatchMaterialComesBackUnchanged()
    {
        VmtDocument resolved = await ResolveAsync(
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"b\"\n}\n",
            [],
            VmtPatchDialect.Engine);

        Assert.Equal("b", resolved.Root.GetString("$basetexture"));
    }

    [Fact]
    public async Task MissingIncludeFileIsReported()
    {
        // And the reference implementation both only Warn and carry on
        // with a half-built material; a library reports it instead.
        await Assert.ThrowsAsync<VmtPatchException>(
            async () => await ResolveAsync(
                "\"patch\"\n{\n\t\"include\" \"materials/missing.vmt\"\n}\n",
                [],
                VmtPatchDialect.Compiler));
    }

    [Fact]
    public async Task PatchWithNoIncludeKeyIsReported()
    {
        // Leaves keyValues unassigned, so the loop
        // spins ten times over the same patch and then warns
        // ("Infinite recursion in patch file?").
        await Assert.ThrowsAsync<VmtPatchException>(
            async () => await ResolveAsync(
                "\"patch\"\n{\n\t\"insert\"\n\t{\n\t\t\"$x\" \"1\"\n\t}\n}\n",
                [],
                VmtPatchDialect.Compiler));
    }

    [Fact]
    public void DepthLimitIsTenInBothImplementations()
    {
        // And the reference implementation -- nCount < 10.
        Assert.Equal(10, VmtPatchResolver.MaxPatchDepth);
    }

    [Fact]
    public async Task ChainLongerThanTheDepthLimitStopsAtTheLimit()
    {
        // Neither implementation FAILS at the limit -- both warn and carry on
        // with whatever they have -- so a chain of eleven patches resolves to
        // the tenth level, not to the base.
        Dictionary<string, string> files = [];
        for (int i = 0; i < 12; i++)
        {
            files[$"materials/p{i}.vmt"] =
                $"\"patch\"\n{{\n\t\"include\" \"materials/p{i + 1}.vmt\"\n}}\n";
        }

        files["materials/p12.vmt"] = "\"Water\"\n{\n}\n";

        VmtDocument resolved = await ResolveAsync(
            "\"patch\"\n{\n\t\"include\" \"materials/p0.vmt\"\n}\n",
            files,
            VmtPatchDialect.Compiler);

        Assert.True(resolved.IsPatch);
    }

    [Fact]
    public void PatchIsSerialisedInVbspsExactPakFraming()
    {
        // CreateMaterialPatch builds a KeyValues
        // tree and writes it with RecursiveSaveToFile at indent level 0 --
        // there is not an fprintf in that file. "include" is written FIRST
        // exactly one section follows.
        KeyValuesNode root = new("patch");
        root.SetString("include", "materials/nature/water.vmt");
        root.FindOrCreate("insert").SetString("$waterdepth", "128");

        Assert.Equal(
            "\"patch\"\n{\n\t\"include\"\t\t\"materials/nature/water.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$waterdepth\"\t\t\"128\"\n\t}\n}\n",
            new VmtDocument(root).ToText());
    }
}
