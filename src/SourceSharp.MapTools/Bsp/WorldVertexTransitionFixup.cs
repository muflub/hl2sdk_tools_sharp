using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One material patch this pass generated, for the caller to write into the
/// BSP's pak lump.
/// </summary>
/// <param name="Name">
/// The patched material's name, without <c>materials/</c> or <c>.vmt</c>.
/// </param>
/// <param name="Material">The patched material.</param>
public sealed record WorldVertexTransitionPatch(string Name, KeyValuesNode Material);

/// <summary>
/// <c>WorldVertexTransitionFixup</c>,
/// <c>utils/vbsp/worldvertextransitionfixup.cpp</c>: gives every
/// NON-displacement brush side that uses a blend shader a single-texture
/// material of its own.
/// </summary>
/// <remarks>
/// <para>
/// A <c>WorldVertexTransition*</c> material blends two textures by vertex
/// alpha, which only a displacement has. A brush side using one would render
/// with undefined blend, so vbsp generates
/// <c>maps/&lt;mapbase&gt;/&lt;material&gt;_wvt_patch</c>: the same material
/// with its shader forced to <c>LightmappedGeneric</c> and every
/// second-texture variable removed. The side is then repointed at a new
/// texinfo naming the patch.
/// </para>
/// <para>
/// <b>Two pieces of stock are deliberately not reproduced.</b> The prefix-sum
/// over entity side ranges at <c>worldvertextransitionfixup.cpp:167-185</c>
/// and the <c>currentEntity</c> walk at <c>:205-209</c> compute a value that
/// is never read afterwards; they are dead. And
/// <c>SideIsNotDispAndHasDispMaterial</c> (<c>:20</c>) is never called at all,
/// and its body is a no-op expression statement where the second half of the
/// test should be.
/// </para>
/// <para>
/// Writing the patched <c>.vmt</c> into the BSP's pak lump is the writer
/// lane's; this returns the patches instead, so nothing here needs a zip.
/// </para>
/// </remarks>
public static class WorldVertexTransitionFixup
{
    /// <summary>
    /// The suffix a patched material's name carries:
    /// <c>worldvertextransitionfixup.cpp:51</c>.
    /// </summary>
    public const string PatchSuffix = "_wvt_patch";

    /// <summary>
    /// The shader a patched material is forced to:
    /// <c>worldvertextransitionfixup.cpp:78</c>.
    /// </summary>
    public const string PatchedShader = "LightmappedGeneric";

    /// <summary>
    /// The substring that identifies a blend shader, matched
    /// case-insensitively: <c>worldvertextransitionfixup.cpp:202</c>.
    /// </summary>
    public const string BlendShaderSubstring = "worldvertextransition";

    /// <summary>
    /// The longest a generated name may be:
    /// <c>TEXTURE_NAME_LENGTH - 1</c>, <c>public/bspfile.h:508</c>.
    /// </summary>
    public const int MaxPatchedNameLength = 127;

    /// <summary>
    /// The variables stripped from a patched material, in stock's order:
    /// <c>worldvertextransitionfixup.cpp:81-87</c>.
    /// </summary>
    public static IReadOnlyList<string> StrippedVariables { get; } =
    [
        "$basetexture2",
        "$bumpmap2",
        "$bumpframe2",
        "$basetexture2noenvmap",
        "$blendmodulatetexture",
        "$maskedblending",
        "$surfaceprop2",
    ];

    /// <summary>
    /// The name of the patch for a material:
    /// <c>GeneratePatchedMaterialName</c>,
    /// <c>utils/vbsp/worldvertextransitionfixup.cpp:47</c>.
    /// </summary>
    /// <param name="materialName">The original material name.</param>
    /// <param name="mapBase">The map's base name.</param>
    /// <returns>The patched name, slash-normalised and lowercased.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="MapCompileException">
    /// The generated name is longer than <see cref="MaxPatchedNameLength"/>.
    /// </exception>
    /// <remarks>
    /// Lowercased at the end, which is what lets the case-insensitive
    /// <see cref="TexDataTable.Find"/> find it again on a later side using the
    /// same material.
    /// </remarks>
    public static string PatchedMaterialName(string materialName, string mapBase)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(mapBase);

        string name = $"maps/{mapBase}/{materialName}{PatchSuffix}";

        if (name.Length >= MaxPatchedNameLength)
        {
            throw new MapCompileException(
                $"Generated worldvertextransition patch name : {name} too long! (max = 128)");
        }

#pragma warning disable CA1308 // Q_strlower, worldvertextransitionfixup.cpp:58.
        return name.Replace('\\', '/').ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>
    /// Whether a shader name is a blend shader:
    /// <c>Q_stristr(pShaderName, "worldvertextransition")</c>,
    /// <c>worldvertextransitionfixup.cpp:202</c>.
    /// </summary>
    /// <param name="shaderName">The shader name, or null.</param>
    /// <returns>True when it contains the substring, in any case.</returns>
    /// <remarks>
    /// A SUBSTRING test, so it catches <c>WorldVertexTransition</c>,
    /// <c>WorldVertexTransition_DX8</c> and
    /// <c>LightmappedGeneric_WorldVertexTransition</c> alike.
    /// </remarks>
    public static bool IsBlendShader(string? shaderName) =>
        shaderName is not null &&
        shaderName.Contains(BlendShaderSubstring, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the patched material from the original:
    /// <c>CreateWorldVertexTransitionPatchedMaterial</c>,
    /// <c>utils/vbsp/worldvertextransitionfixup.cpp:71</c>.
    /// </summary>
    /// <param name="original">The original material.</param>
    /// <returns>The patched material, which is a copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is null.</exception>
    /// <remarks>
    /// <c>$envmap</c> is removed only when <c>$basetexturenoenvmap</c> is a
    /// non-zero integer. Stock reads that variable through a <c>FindKey</c>
    /// that can return null and calls <c>GetInt</c> on it unguarded
    /// (<c>:89-92</c>); an absent variable reads as zero, so the key is KEPT,
    /// and that is the behaviour here.
    /// </remarks>
    public static KeyValuesNode PatchMaterial(KeyValuesNode original)
    {
        ArgumentNullException.ThrowIfNull(original);

        KeyValuesNode patched = original.Clone();
        patched.Name = PatchedShader;

        foreach (string variable in StrippedVariables)
        {
            Remove(patched, variable);
        }

        if (patched.GetInt("$basetexturenoenvmap") != 0)
        {
            Remove(patched, "$envmap");
        }

        return patched;
    }

    /// <summary>
    /// Repoints every non-displacement blend-shader side at a patched
    /// material: <c>WorldVertexTransitionFixup</c>,
    /// <c>utils/vbsp/worldvertextransitionfixup.cpp:164</c>.
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="map">The map to fix up.</param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>
    /// The patched materials that were generated, for the pak writer.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="map"/> is null.
    /// </exception>
    public static async Task<IReadOnlyList<WorldVertexTransitionPatch>> RunAsync(
        VbspContext context,
        MapFile map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);

        List<WorldVertexTransitionPatch> patches = [];

        for (int i = 0; i < map.BrushSideCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MapBrushSide side = map.BrushSides[i];

            // Displacements legitimately use a blend shader.
            if (side.Displacement is not null || side.TexInfo < 0)
            {
                continue;
            }

            string materialName = context.TexDatas.NameOf(context.TexInfos[side.TexInfo].TexData);
            MaterialFacts facts = await context.Materials
                .GetAsync(materialName, cancellationToken)
                .ConfigureAwait(false);

            if (!IsBlendShader(facts.ShaderName))
            {
                continue;
            }

            side.TexInfo = await PatchTexInfoAsync(
                    context, side.TexInfo, patches, cancellationToken)
                .ConfigureAwait(false);
        }

        return patches;
    }

    /// <summary>
    /// The patched texinfo for one original:
    /// <c>CreateBrushVersionOfWorldVertexTransitionMaterial</c>,
    /// <c>utils/vbsp/worldvertextransitionfixup.cpp:98</c>.
    /// </summary>
    /// <param name="context">The compile.</param>
    /// <param name="originalTexInfo">The texinfo to patch.</param>
    /// <param name="patches">Receives a patch when one is generated.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>The patched texinfo's index.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="patches"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Idempotent by name: a texinfo whose material already contains
    /// <c>_wvt_patch</c> is returned unchanged (<c>:109</c>), and so is
    /// <see cref="TexInfoTable.TexInfoNode"/>.
    /// </para>
    /// <para>
    /// The texinfo lookup is SKIPPED when the texdata had to be created,
    /// because no existing texinfo can reference a brand-new texdata
    /// (<c>:134-142</c>) — and the new texinfo is appended directly rather than
    /// through <c>FindOrCreateTexInfo</c>, which is how this pass adds texinfos
    /// under <c>-onlyents</c> where that function would have errored.
    /// </para>
    /// </remarks>
    public static async ValueTask<int> PatchTexInfoAsync(
        VbspContext context,
        int originalTexInfo,
        ICollection<WorldVertexTransitionPatch> patches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(patches);

        if (originalTexInfo == TexInfoTable.TexInfoNode)
        {
            return originalTexInfo;
        }

        TexInfo texInfo = context.TexInfos[originalTexInfo];
        string originalName = context.TexDatas.NameOf(texInfo.TexData);

        if (originalName.Contains(PatchSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return originalTexInfo;
        }

        string patchedName = PatchedMaterialName(originalName, context.MapBase);

        int texDataId = context.TexDatas.Find(patchedName);
        bool hadTexData = texDataId != -1;

        if (!hadTexData)
        {
            MaterialFacts facts = await context.Materials
                .GetAsync(originalName, cancellationToken)
                .ConfigureAwait(false);

            if (facts.Material() is { } material)
            {
                patches.Add(new WorldVertexTransitionPatch(patchedName, PatchMaterial(material)));
            }

            texDataId = context.TexDatas.AddClone(texInfo.TexData, patchedName);
        }

        TexInfo patched = texInfo;
        patched.TexData = texDataId;

        if (hadTexData)
        {
            int existing = context.TexInfos.Find(patched);
            if (existing != -1)
            {
                return existing;
            }
        }

        return context.TexInfos.Add(patched);
    }

    private static void Remove(KeyValuesNode node, string name)
    {
        KeyValuesNode? child = node.Find(name);
        if (child is not null)
        {
            node.Children.Remove(child);
        }
    }
}
