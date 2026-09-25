using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// <c>FindMiptex</c>: which
/// <c>SURF_*</c> and <c>CONTENTS_*</c> bits a material produces.
/// </summary>
/// <remarks>
/// These are the flags that reach the BSP unchanged —
/// copies them to the brush side and
/// copies the side's straight into
/// <c>texinfo_t::flags</c>, and vbsp sets a <c>SURF_</c> bit nowhere else.
/// </remarks>
public class MaterialSurfaceTests
{
    [Fact]
    public async Task ASkyMaterialIsSkyAndUnlit()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileSky 1 }");

        Assert.Equal(SurfaceFlags.Sky | SurfaceFlags.NoLight, surface.Flags);
    }

    [Fact]
    public async Task ATwoDimensionalSkyAlsoCarriesTheSkyBit()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compile2DSky 1 }");

        Assert.Equal(
            SurfaceFlags.Sky | SurfaceFlags.Sky2D | SurfaceFlags.NoLight,
            surface.Flags);
    }

    [Fact]
    public async Task TheCompileChainIsExclusiveSoASkyThatIsAlsoNodrawIsOnlyASky()
    {
        // is one if/else-if chain. A material setting both
        // gets ONLY the first branch, and reordering the tests into something
        // tidier would change this answer.
        MaterialSurface surface = await Classify("UnlitGeneric { %compileSky 1 %compileNoDraw 1 }");

        Assert.Equal(SurfaceFlags.Sky | SurfaceFlags.NoLight, surface.Flags);
    }

    [Fact]
    public async Task AHintMaterialIsNodrawUnlitAndAHint()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileHint 1 }");

        Assert.Equal(
            SurfaceFlags.NoDraw | SurfaceFlags.NoLight | SurfaceFlags.Hint,
            surface.Flags);
    }

    [Fact]
    public async Task ASkipMaterialIsNodrawUnlitAndSkipped()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileSkip 1 }");

        Assert.Equal(
            SurfaceFlags.NoDraw | SurfaceFlags.NoLight | SurfaceFlags.Skip,
            surface.Flags);
    }

    [Fact]
    public async Task AnOriginMaterialIsOriginAndDetailContents()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileOrigin 1 }");

        Assert.Equal(BrushContents.Origin | BrushContents.Detail, surface.Contents);
    }

    [Fact]
    public async Task AClipMaterialClipsBothPlayersAndNpcs()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileClip 1 }");

        Assert.Equal(BrushContents.PlayerClip | BrushContents.MonsterClip, surface.Contents);
    }

    [Fact]
    public async Task PlayerClipIsSpeltWithoutTheWordCompile()
    {
        // %playerClip, not %compilePlayerClip. A table built on a "%compile"
        // prefix drops it and every player-clip brush in every map becomes an
        // ordinary solid.
        MaterialSurface surface = await Classify("UnlitGeneric { %playerClip 1 }");

        Assert.Equal(BrushContents.PlayerClip, surface.Contents);
    }

    [Fact]
    public async Task NpcClipClipsOnlyNpcs()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileNpcClip 1 }");

        Assert.Equal(BrushContents.MonsterClip, surface.Contents);
    }

    [Fact]
    public async Task ANoChopMaterialGetsNoChopAndNothingElse()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileNoChop 1 }");

        Assert.Equal(SurfaceFlags.NoChop, surface.Flags);
    }

    [Fact]
    public async Task ATriggerIsUnlitAndATrigger()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileTrigger 1 }");

        Assert.Equal(SurfaceFlags.NoLight | SurfaceFlags.Trigger, surface.Flags);
    }

    [Fact]
    public async Task NodrawTriggersAddsNodrawToATrigger()
    {
        MaterialSurface surface = await Classify(
            "UnlitGeneric { %compileTrigger 1 }",
            new MaterialCompileOptions { NodrawTriggers = true });

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.NoDraw));
    }

    [Fact]
    public async Task NoLightOnWaterIsIgnoredByTheExclusiveChain()
    {
        //: %compileNoLight only takes that branch when
        // %compileWater is absent, so water falls through to the rendered
        // block and gets its warp and decal flags.
        MaterialSurface surface = await Classify(
            "Water { %compileNoLight 1 %compileWater 1 $normalmap a/n }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.Warp));
    }

    [Fact]
    public async Task WaterContentsReplaceSolidAndDetail()
    {
        MaterialSurface surface = await Classify("Water { %compileWater 1 %compileDetail 1 }");

        Assert.Equal(BrushContents.Water, surface.Contents);
    }

    [Fact]
    public async Task SlimeGetsNoDecals()
    {
        MaterialSurface surface = await Classify("UnlitGeneric { %compileSlime 1 }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.NoDecals));
    }

    [Fact]
    public async Task PassBulletsMakesAMaterialAGrate()
    {
        MaterialSurface surface = await Classify("LightmappedGeneric { %compilePassBullets 1 }");

        Assert.True(surface.Contents.HasFlag(BrushContents.Grate));
    }

    [Fact]
    public async Task NonsolidAssignsContentsRatherThanOringThem()
    {
        // is `=`, not `|=`, so the ladder bit set above it is
        // discarded.
        MaterialSurface surface = await Classify(
            "LightmappedGeneric { %compileLadder 1 %compileNonsolid 1 }");

        Assert.Equal(BrushContents.Opaque, surface.Contents);
    }

    [Fact]
    public async Task BlockLosBeatsNonsolidWhenAMaterialSetsBoth()
    {
        // Both are assignments and BlockLOS is second.
        MaterialSurface surface = await Classify(
            "LightmappedGeneric { %compileNonsolid 1 %compileBlockLOS 1 }");

        Assert.Equal(BrushContents.BlockLos, surface.Contents);
    }

    [Fact]
    public async Task ANonsolidMaterialIsNeverAWindow()
    {
        MaterialSurface surface = await Classify(
            "LightmappedGeneric { %compileNonsolid 1 $translucent 1 }");

        Assert.False(surface.Contents.HasFlag(BrushContents.Window));
    }

    [Fact]
    public async Task ATranslucentSolidMaterialBecomesAWindow()
    {
        MaterialSurface surface = await Classify("LightmappedGeneric { $translucent 1 }");

        Assert.True(surface.Contents.HasFlag(BrushContents.Window));
    }

    [Fact]
    public async Task ATranslucentMaterialSortsAsTranslucent()
    {
        MaterialSurface surface = await Classify("LightmappedGeneric { $translucent 1 }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.Trans));
    }

    [Fact]
    public async Task AnAlphaTestedMaterialIsAWindowButNotTranslucent()
    {
        //: opacity != OPAQUE makes the window, but only
        // TRANSLUCENT sets SURF_TRANS.
        MaterialSurface surface = await Classify("LightmappedGeneric { $alphatest 1 }");

        Assert.False(surface.Flags.HasFlag(SurfaceFlags.Trans));
    }

    [Fact]
    public async Task AGrateIsNotAlsoAWindow()
    {
        MaterialSurface surface = await Classify(
            "LightmappedGeneric { %compilePassBullets 1 $alphatest 1 }");

        Assert.False(surface.Contents.HasFlag(BrushContents.Window));
    }

    [Fact]
    public async Task ALightmappedMaterialKeepsItsLighting()
    {
        MaterialSurface surface = await Classify("LightmappedGeneric { $basetexture a/b }");

        Assert.False(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task AnUnlitShaderGetsNoLight()
    {
        MaterialSurface surface = await Classify("VertexLitGeneric { $basetexture a/b }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task LightIfMissingLeavesAnUnlitShaderLit()
    {
        MaterialSurface surface = await Classify(
            "VertexLitGeneric { $basetexture a/b }",
            new MaterialCompileOptions { LightIfMissing = true });

        Assert.False(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task ABumpMappedLightmappedMaterialGetsBumpLight()
    {
        MaterialSurface surface = await Classify("LightmappedGeneric { $bumpmap a/n }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.BumpLight));
    }

    [Fact]
    public async Task BumpAllGivesBumpLightToAMaterialWithNoBumpMap()
    {
        MaterialSurface surface = await Classify(
            "LightmappedGeneric { $basetexture a/b }",
            new MaterialCompileOptions { BumpAll = true });

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.BumpLight));
    }

    [Fact]
    public async Task AnUnlitSurfaceLosesItsBumpLightAtTheEnd()
    {
        //, the last thing FindMiptex does.
        MaterialSurface surface = await Classify(
            "VertexLitGeneric { $bumpmap a/n }",
            new MaterialCompileOptions { BumpAll = true });

        Assert.False(surface.Flags.HasFlag(SurfaceFlags.BumpLight));
    }

    [Fact]
    public async Task AWaterShaderIsForcedUnlitByItsName()
    {
        // compares the first five characters of the
        // SHADER NAME, not a material variable.
        MaterialSurface surface = await Classify("Water { $normalmap a/n }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task KeepLightProtectsAWaterShaderFromThatRule()
    {
        MaterialSurface surface = await Classify("Water { $normalmap a/n %compileKeepLight 1 }");

        Assert.False(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task KeepLightDoesNotProtectAnUnlitGenericMaterial()
    {
        // The precedence: && binds tighter than ||, so
        // bKeepLighting guards only the water half of the test. Reading it as
        // (!keep && (water || unlit)) is the natural misreading and would
        // light every %compileKeepLight UnlitGeneric surface in every map.
        MaterialSurface surface = await Classify("UnlitGeneric { %compileKeepLight 1 }");

        Assert.True(surface.Flags.HasFlag(SurfaceFlags.NoLight));
    }

    [Fact]
    public async Task AMaterialThatIsNotFoundContributesNothing()
    {
        await using ContentFileSystem content = await new MaterialContent().MountAsync();
        MaterialFacts facts = await MaterialFactsReader.ReadAsync("gone", content);

        Assert.Equal(default, MaterialSurfaceClassifier.Classify(facts));
    }

    private static async Task<MaterialSurface> Classify(
        string vmt,
        MaterialCompileOptions? options = null)
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", vmt)
            .MountAsync();

        MaterialFacts facts = await MaterialFactsReader.ReadAsync("a/b", content);

        return MaterialSurfaceClassifier.Classify(facts, options);
    }
}
