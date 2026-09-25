using System.Collections.Immutable;

using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// When a shader asks for a lightmap.
/// </summary>
public enum ShaderLightmapRule
{
    /// <summary>It never does. Every shader not in the table.</summary>
    Never = 0,

    /// <summary>Unconditionally, in <c>SHADER_INIT_PARAMS</c>.</summary>
    Always,

    /// <summary>
    /// Only when <c>$basetexture</c> is defined
    /// (<c>LightmappedReflective</c>).
    /// </summary>
    WhenBaseTexture,
}

/// <summary>
/// When a shader asks for BUMPED lightmaps.
/// </summary>
public enum ShaderBumpedLightmapRule
{
    /// <summary>It never does.</summary>
    Never = 0,

    /// <summary>
    /// <c>$bumpmap</c> defined and <c>$nodiffusebumplighting</c> zero —
    /// Shared by
    /// <c>LightmappedGeneric</c>, <c>WorldVertexTransition</c> and
    /// <c>WorldTwoTextureBlend</c>.
    /// </summary>
    BumpMapWithoutNoDiffuse,

    /// <summary>
    /// <c>$normalmap</c> defined.
    /// </summary>
    NormalMap,

    /// <summary>
    /// <c>$basetexture</c> AND <c>$normalmap</c> defined
    /// (: the bumped test is nested
    /// inside the basetexture one).
    /// </summary>
    NormalMapWithBaseTexture,
}

/// <summary>
/// One shader's answers to the two questions <c>utilmatlib</c> asks about
/// lighting.
/// </summary>
/// <param name="ShaderName">The shader, as a VMT's root key spells it.</param>
/// <param name="Lightmap">When it sets <c>MATERIAL_VAR2_LIGHTING_LIGHTMAP</c>.</param>
/// <param name="BumpedLightmap">
/// When it sets <c>MATERIAL_VAR2_LIGHTING_BUMPED_LIGHTMAP</c>.
/// </param>
public readonly record struct MaterialShaderRule(
    string ShaderName,
    ShaderLightmapRule Lightmap,
    ShaderBumpedLightmapRule BumpedLightmap)
{
    /// <summary>
    /// <c>GetMaterialShaderPropertyBool(UTILMATLIB_NEEDS_LIGHTMAP)</c>.
    /// </summary>
    /// <param name="material">The RESOLVED material's root section.</param>
    /// <returns>True when the shader would ask for a lightmap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is null.</exception>
    public bool NeedsLightmap(KeyValuesNode material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return Lightmap switch
        {
            ShaderLightmapRule.Always => true,
            ShaderLightmapRule.WhenBaseTexture => IsDefined(material, MaterialVarNames.BaseTexture),
            _ => false,
        };
    }

    /// <summary>
    /// <c>GetMaterialShaderPropertyBool(UTILMATLIB_NEEDS_BUMPED_LIGHTMAPS)</c>.
    /// </summary>
    /// <param name="material">The RESOLVED material's root section.</param>
    /// <param name="useBumpmapping">
    /// <c>g_pConfig-&gt;UseBumpmapping()</c>. True for a default
    /// <c>MaterialSystem_Config_t</c>, which is what
    /// <c>InitMaterialSystem</c> hands the compilers
    /// So true is what a map compile sees.
    /// </param>
    /// <returns>True when the shader would ask for bumped lightmaps.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is null.</exception>
    public bool NeedsBumpedLightmaps(KeyValuesNode material, bool useBumpmapping = true)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (!useBumpmapping)
        {
            // Every bumped-lightmap site in the stdshaders is guarded by it.
            return false;
        }

        return BumpedLightmap switch
        {
            ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse =>
                IsDefined(material, MaterialVarNames.BumpMap) &&
                material.GetInt(MaterialVarNames.NoDiffuseBumpLighting) == 0,
            ShaderBumpedLightmapRule.NormalMap =>
                IsDefined(material, MaterialVarNames.NormalMap),
            ShaderBumpedLightmapRule.NormalMapWithBaseTexture =>
                IsDefined(material, MaterialVarNames.BaseTexture) &&
                IsDefined(material, MaterialVarNames.NormalMap),
            _ => false,
        };
    }

    private static bool IsDefined(KeyValuesNode material, string name) =>
        !string.IsNullOrEmpty(material.GetString(name));
}

/// <summary>
/// Which shaders ask for a lightmap, and under what condition.
/// </summary>
/// <remarks>
/// <para>
/// A table and not a shader system, because <c>utilmatlib</c> asks a material
/// exactly two lighting questions and both are answered by flags a shader sets
/// in <c>SHADER_INIT_PARAMS</c> — <c>MATERIAL_VAR2_LIGHTING_LIGHTMAP</c> and
/// <c>MATERIAL_VAR2_LIGHTING_BUMPED_LIGHTMAP</c>
/// Grepping
/// <c>src/materialsystem/stdshaders</c> for those two names gives the complete
/// list, and this is it.
/// </para>
/// <para>
/// The name is matched against the VMT's ROOT KEY, which is not quite what
/// <c>GetMaterialShaderName</c> returns: the real call returns the shader
/// AFTER <c>DEFINE_FALLBACK_SHADER</c> resolution, so a <c>Water</c> material
/// reports <c>Water_DX81</c> or <c>Water_DX90</c> depending on the DX level
/// the empty shader API claims. Both the fallback targets and the names a VMT
/// actually uses are in the table, so either spelling answers the same. The
/// one consumer that looks at the string itself
/// Compares only the first five characters
/// against <c>"water"</c>, which every spelling shares.
/// </para>
/// </remarks>
public static class MaterialShaderTable
{
    /// <summary>
    /// Every shader in <c>stdshaders</c> that sets a lightmap lighting flag in
    /// <c>SHADER_INIT_PARAMS</c>, with its fallback spellings.
    /// </summary>
    public static ImmutableArray<MaterialShaderRule> Rules { get; } =
    [
        //
        new("LightmappedGeneric", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("LightmappedGeneric_DX9", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("LightmappedGeneric_DX8", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("LightmappedGeneric_DX6", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        // Routes the DX9 shader through the
        // LightmappedGeneric helper, so the rule is the same one.
        new("WorldVertexTransition", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("WorldVertexTransition_DX9", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("WorldVertexTransition_DX8", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("WorldVertexTransition_DX6", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        //
        new("WorldTwoTextureBlend", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("WorldTwoTextureBlend_DX8", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.BumpMapWithoutNoDiffuse),
        new("WorldTwoTextureBlend_DX6", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        //
        new("Water", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.NormalMap),
        new("Water_DX90", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.NormalMap),
        new("Water_DX9_HDR", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.NormalMap),
        new("Water_DX81", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.NormalMap),
        new("Water_DX80", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.NormalMap),
        new("Water_DX60", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        // -- the only conditional lightmap.
        new("LightmappedReflective", ShaderLightmapRule.WhenBaseTexture, ShaderBumpedLightmapRule.NormalMapWithBaseTexture),

        //
        new("LightmappedTwoTexture", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        //
        new("LightmappedGeneric_Decal", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        //
        new("WorldVertexAlpha", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
        new("WorldVertexAlpha_DX8", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        // And the dx8/dx6
        // copies at the same place in each.
        new("DecalBaseTimesLightmapAlphaBlendSelfIllum", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
        new("DecalBaseTimesLightmapAlphaBlendSelfIllum_DX9", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
        new("DecalBaseTimesLightmapAlphaBlendSelfIllum_DX8", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
        new("DecalBaseTimesLightmapAlphaBlendSelfIllum_DX6", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),

        // And -- SHADER_NOT_EDITABLE, so
        // no VMT should name them, but vbsp would answer for them if one did.
        new("DebugLuxels", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
        new("Overlay_Fit", ShaderLightmapRule.Always, ShaderBumpedLightmapRule.Never),
    ];

    /// <summary>
    /// The rule for a shader, or an unlit one when nothing matches.
    /// </summary>
    /// <param name="shaderName">The VMT's root key.</param>
    /// <returns>
    /// The matching rule, or a <see cref="ShaderLightmapRule.Never"/> rule
    /// carrying <paramref name="shaderName"/>.
    /// </returns>
    /// <remarks>
    /// Unknown means unlit, and that is the right default rather than a
    /// failure: <c>VertexLitGeneric</c>, <c>UnlitGeneric</c>, <c>Sky</c>,
    /// <c>Modulate</c> and everything else in <c>stdshaders</c> genuinely sets
    /// no lightmap flag, and a mod's own shader that vbsp has never heard of
    /// gets the same answer from the real material system — the flag is simply
    /// not set.
    /// </remarks>
    public static MaterialShaderRule RuleFor(string? shaderName)
    {
        foreach (MaterialShaderRule rule in Rules)
        {
            if (string.Equals(rule.ShaderName, shaderName, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return new MaterialShaderRule(
            shaderName ?? string.Empty,
            ShaderLightmapRule.Never,
            ShaderBumpedLightmapRule.Never);
    }
}
