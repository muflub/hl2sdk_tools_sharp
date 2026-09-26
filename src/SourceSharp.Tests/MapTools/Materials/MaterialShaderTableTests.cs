//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// The two lighting questions <c>utilmatlib</c> asks a shader.
/// </summary>
/// <remarks>
/// Every expectation here is read off a <c>SET_FLAGS2</c> site in
/// <c>src/materialsystem/stdshaders</c>; the file and line is in the comment
/// on each.
/// </remarks>
public class MaterialShaderTableTests
{
    [Fact]
    public async Task LightmappedGenericNeedsALightmap()
    {
        MaterialFacts facts = await Read("LightmappedGeneric { $basetexture a/b }");

        Assert.True(facts.NeedsLightmap);
    }

    [Fact]
    public async Task UnlitGenericNeedsNoLightmap()
    {
        MaterialFacts facts = await Read("UnlitGeneric { $basetexture a/b }");

        Assert.False(facts.NeedsLightmap);
    }

    [Fact]
    public async Task VertexLitGenericNeedsNoLightmap()
    {
        MaterialFacts facts = await Read("VertexLitGeneric { $basetexture a/b }");

        Assert.False(facts.NeedsLightmap);
    }

    [Fact]
    public async Task AShaderNobodyHasHeardOfNeedsNoLightmap()
    {
        // Unknown is unlit, and that is the material system's answer too: the
        // flag is simply never set.
        MaterialFacts facts = await Read("SomeModsOwnShader { }");

        Assert.False(facts.NeedsLightmap);
    }

    [Fact]
    public async Task TheShaderNameIsMatchedWithoutRegardToCase()
    {
        MaterialFacts facts = await Read("lightmappedgeneric { }");

        Assert.True(facts.NeedsLightmap);
    }

    [Fact]
    public async Task ABumpMappedLightmappedGenericNeedsBumpedLightmaps()
    {
        //
        MaterialFacts facts = await Read("LightmappedGeneric { $bumpmap a/b_normal }");

        Assert.True(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task ALightmappedGenericWithoutABumpMapDoesNot()
    {
        MaterialFacts facts = await Read("LightmappedGeneric { $basetexture a/b }");

        Assert.False(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task NoDiffuseBumpLightingSuppressesBumpedLightmaps()
    {
        MaterialFacts facts = await Read(
            "LightmappedGeneric { $bumpmap a/b_normal $nodiffusebumplighting 1 }");

        Assert.False(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task WorldVertexTransitionSharesLightmappedGenericsBumpRule()
    {
        // routes the DX9 shader through the same
        // helper, so the two cannot disagree.
        MaterialFacts facts = await Read("WorldVertexTransition { $bumpmap a/b_normal }");

        Assert.True(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task WaterTakesItsBumpFromNormalMapAndNotBumpMap()
    {
        // tests $normalmap. A material system that looked
        // for $bumpmap would give every water surface flat lightmaps.
        MaterialFacts facts = await Read("Water { $normalmap a/water_normal }");

        Assert.True(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task WaterWithABumpMapInsteadOfANormalMapIsNotBumped()
    {
        MaterialFacts facts = await Read("Water { $bumpmap a/water_normal }");

        Assert.False(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task LightmappedReflectiveNeedsNoLightmapWithoutABaseTexture()
    {
        // -- the only conditional lightmap in
        // stdshaders.
        MaterialFacts facts = await Read("LightmappedReflective { $normalmap a/n }");

        Assert.False(facts.NeedsLightmap);
    }

    [Fact]
    public async Task LightmappedReflectiveNeedsOneWithABaseTexture()
    {
        MaterialFacts facts = await Read("LightmappedReflective { $basetexture a/b }");

        Assert.True(facts.NeedsLightmap);
    }

    [Fact]
    public async Task LightmappedReflectivesBumpIsNestedInsideItsBaseTextureTest()
    {
        MaterialFacts facts = await Read("LightmappedReflective { $normalmap a/n }");

        Assert.False(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public async Task LightmappedTwoTextureNeverNeedsBumpedLightmaps()
    {
        MaterialFacts facts = await Read("LightmappedTwoTexture { $bumpmap a/n }");

        Assert.False(facts.NeedsBumpedLightmaps);
    }

    [Fact]
    public void TurningBumpmappingOffSilencesEveryBumpedLightmapRule()
    {
        // Every SET_FLAGS2(BUMPED_LIGHTMAP) site in stdshaders is guarded by
        // g_pConfig->UseBumpmapping.
        KeyValuesNode material = new("LightmappedGeneric");
        material.SetString("$bumpmap", "a/n");

        MaterialShaderRule rule = MaterialShaderTable.RuleFor("LightmappedGeneric");

        Assert.False(rule.NeedsBumpedLightmaps(material, useBumpmapping: false));
    }

    [Fact]
    public void AFallbackSpellingAnswersTheSameAsItsShader()
    {
        // GetMaterialShaderName reports the shader a DEFINE_FALLBACK_SHADER
        // chain ended at, so both spellings have to be in the table.
        Assert.Equal(
            MaterialShaderTable.RuleFor("Water").BumpedLightmap,
            MaterialShaderTable.RuleFor("Water_DX81").BumpedLightmap);
    }

    [Fact]
    public void AnUnknownShaderKeepsItsNameInTheRule()
    {
        Assert.Equal("Whatever", MaterialShaderTable.RuleFor("Whatever").ShaderName);
    }

    private static async Task<MaterialFacts> Read(string vmt)
    {
        await using ContentFileSystem content = await new MaterialContent()
            .AddMaterial("a/b", vmt)
            .MountAsync();

        return await MaterialFactsReader.ReadAsync("a/b", content);
    }
}
