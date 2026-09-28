//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Frozen;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// Reads <see cref="MaterialFacts"/> out of mounted game content.
/// </summary>
/// <remarks>
/// <para>
/// The port of <c>FindMaterial</c> plus everything the compilers ask the
/// handle it returns. There is no material system here and there does not need
/// to be one: a VMT is KeyValues, a patch is resolved by
/// <see cref="VmtPatchResolver"/> in the COMPILER's dialect, the reflectivity
/// and the dimensions live in the base texture's VTF header, and the two
/// lighting questions are a table (<see cref="MaterialShaderTable"/>).
/// </para>
/// <para>
/// All IO goes through <see cref="IContentFileSystem"/>, which is where
/// <see cref="RecordingContentFileSystem"/> sits: every material resolved and
/// every MISS is then part of the compile's recorded input set. Nothing here
/// bypasses it — including the misses, which matter most, since a material
/// that is absent today and present tomorrow changes the BSP.
/// </para>
/// <para>
/// Load then compute: <see cref="ReadManyAsync"/> does every read up front and
/// hands back a dictionary, so the stages that consume these facts iterate
/// over values and never touch the disk inside a loop.
/// </para>
/// </remarks>
public static class MaterialFactsReader
{
    /// <summary>The content directory every material path is rooted in.</summary>
    public const string MaterialsDirectory = "materials";

    /// <summary>A material file's extension.</summary>
    public const string MaterialExtension = ".vmt";

    /// <summary>A texture file's extension.</summary>
    public const string TextureExtension = ".vtf";

    /// <summary>
    /// The material name as the material system would key it.
    /// </summary>
    /// <param name="materialName">The name as a map or a lump spells it.</param>
    /// <returns>The normalised name, without directory prefix or extension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialName"/> is null.</exception>
    /// <remarks>
    /// Backslashes to forward slashes and lower case, because Hammer writes
    /// <c>Metal\Metalwall048a</c> into a VMF and the file on disk is
    /// <c>materials/metal/metalwall048a.vmt</c>. A leading <c>materials/</c>
    /// and a trailing <c>.vmt</c> are both stripped, so a caller that already
    /// has a content path gets the same answer as one that has a texture name.
    /// </remarks>
    public static string Normalize(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);

        string name = materialName.Replace('\\', '/').Trim().ToLowerInvariant();

        if (name.EndsWith(MaterialExtension, StringComparison.Ordinal))
        {
            name = name[..^MaterialExtension.Length];
        }

        if (name.StartsWith(MaterialsDirectory + "/", StringComparison.Ordinal))
        {
            name = name[(MaterialsDirectory.Length + 1)..];
        }

        return name.TrimStart('/');
    }

    /// <summary>
    /// The content path a material name resolves to.
    /// </summary>
    /// <param name="materialName">The name as a map or a lump spells it.</param>
    /// <returns><c>materials/&lt;name&gt;.vmt</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialName"/> is null.</exception>
    public static VPath ContentPath(string materialName) =>
        VPath.Create($"{MaterialsDirectory}/{Normalize(materialName)}{MaterialExtension}");

    /// <summary>
    /// The content path a <c>$basetexture</c> value resolves to.
    /// </summary>
    /// <param name="textureName">The variable's value.</param>
    /// <returns><c>materials/&lt;name&gt;.vtf</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="textureName"/> is null.</exception>
    public static VPath TexturePath(string textureName)
    {
        ArgumentNullException.ThrowIfNull(textureName);

        string name = textureName.Replace('\\', '/').Trim().ToLowerInvariant();

        if (name.EndsWith(TextureExtension, StringComparison.Ordinal))
        {
            name = name[..^TextureExtension.Length];
        }

        if (name.StartsWith(MaterialsDirectory + "/", StringComparison.Ordinal))
        {
            name = name[(MaterialsDirectory.Length + 1)..];
        }

        return VPath.Create($"{MaterialsDirectory}/{name.TrimStart('/')}{TextureExtension}");
    }

    /// <summary>
    /// <c>FindMaterial</c> plus every getter the compilers call on the result.
    /// </summary>
    /// <param name="materialName">The material, as a map spells it.</param>
    /// <param name="content">Where to look.</param>
    /// <param name="options">The compile's material-system configuration.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The facts. A material that does not resolve comes back with
    /// <see cref="MaterialFacts.Found"/> false rather than throwing, matching
    /// <c>FindMaterial</c>'s <c>pFound</c> out-parameter.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="materialName"/> or <paramref name="content"/> is null.
    /// </exception>
    public static async Task<MaterialFacts> ReadAsync(
        string materialName,
        IContentFileSystem content,
        MaterialFactsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(content);

        MaterialFactsOptions settings = options ?? MaterialFactsOptions.Default;
        Dictionary<VPath, VtfHeaderFacts?> textures = [];

        return await ReadOneAsync(materialName, content, settings, textures, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a whole set of materials, sharing the texture reads between them.
    /// </summary>
    /// <param name="materialNames">
    /// The materials. Duplicates and differing casings collapse to one entry.
    /// </param>
    /// <param name="content">Where to look.</param>
    /// <param name="options">The compile's material-system configuration.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The facts, keyed by <see cref="Normalize(string)"/>d name. This is the
    /// load phase: everything the compute phase needs is in it, so no loop
    /// downstream performs IO.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="materialNames"/> or <paramref name="content"/> is null.
    /// </exception>
    public static async Task<FrozenDictionary<string, MaterialFacts>> ReadManyAsync(
        IEnumerable<string> materialNames,
        IContentFileSystem content,
        MaterialFactsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialNames);
        ArgumentNullException.ThrowIfNull(content);

        MaterialFactsOptions settings = options ?? MaterialFactsOptions.Default;

        // One texture read serves every material that names it: a base texture
        // shared by twenty patched materials is read once.
        Dictionary<VPath, VtfHeaderFacts?> textures = [];
        Dictionary<string, MaterialFacts> facts = [];

        foreach (string materialName in materialNames)
        {
            ArgumentNullException.ThrowIfNull(materialName);

            string key = Normalize(materialName);
            if (facts.ContainsKey(key))
            {
                continue;
            }

            facts[key] = await ReadOneAsync(key, content, settings, textures, cancellationToken)
                .ConfigureAwait(false);
        }

        return facts.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static async Task<MaterialFacts> ReadOneAsync(
        string materialName,
        IContentFileSystem content,
        MaterialFactsOptions options,
        Dictionary<VPath, VtfHeaderFacts?> textures,
        CancellationToken cancellationToken)
    {
        string name = Normalize(materialName);

        VmtDocument? material = await LoadMaterialAsync(content, ContentPath(name), cancellationToken)
            .ConfigureAwait(false);

        if (material is null)
        {
            // FindMaterial's !found path: vbsp warns and carries on with a
            // texture that has no flags, no reflectivity and the fallback
            // dimensions.
            return new MaterialFacts(
                name,
                material: null,
                reflectivity: Vec3.Zero,
                reflectivityFromVar: false,
                width: options.FallbackWidth,
                height: options.FallbackHeight,
                hasPreviewImage: false,
                baseTexture: null,
                options.UseBumpmapping);
        }

        VmtDocument resolved;

        try
        {
            resolved = await VmtPatchResolver.ResolveAsync(
                material,
                (path, token) => LoadMaterialAsync(content, VPath.Create(path), token),
                options.PatchDialect,
                cancellationToken).ConfigureAwait(false);
        }
        catch (VmtPatchException)
        {
            // A patch whose include is missing produces the error material,
            // and the error material is what FindMaterial reports as
            // not-found. Not a failed compile: stock warns and carries on.
            return new MaterialFacts(
                name,
                material: null,
                reflectivity: Vec3.Zero,
                reflectivityFromVar: false,
                width: options.FallbackWidth,
                height: options.FallbackHeight,
                hasPreviewImage: false,
                baseTexture: null,
                options.UseBumpmapping);
        }

        string? baseTextureName = resolved.Root.GetString(MaterialVarNames.BaseTexture);
        VPath? baseTexture = string.IsNullOrWhiteSpace(baseTextureName)
            ? null
            : TexturePath(baseTextureName);

        VtfHeaderFacts? texture = baseTexture is null
            ? null
            : await LoadTextureAsync(content, baseTexture.Value, textures, cancellationToken)
                .ConfigureAwait(false);

        bool reflectivityFromVar = MaterialVarValue.TryParseVector(
            resolved.Root.GetString(MaterialVarNames.Reflectivity),
            out Vec3 reflectivity);

        if (!reflectivityFromVar)
        {
            // No $reflectivity, so the material's own
            // value -- which the material system took from the representative
            // texture's VTF header.
            reflectivity = texture?.Reflectivity ?? Vec3.Zero;
        }

        return new MaterialFacts(
            name,
            resolved.Root,
            reflectivity,
            reflectivityFromVar,
            texture?.Width ?? options.FallbackWidth,
            texture?.Height ?? options.FallbackHeight,
            texture is not null,
            baseTexture,
            options.UseBumpmapping);
    }

    private static async Task<VmtDocument?> LoadMaterialAsync(
        IContentFileSystem content,
        VPath path,
        CancellationToken cancellationToken)
    {
        using IMemoryOwner<byte>? owner =
            await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);

        if (owner is null)
        {
            return null;
        }

        try
        {
            return await VmtDocument.ParseAsync(owner.Memory, cancellationToken).ConfigureAwait(false);
        }
        catch (ChunkFileException)
        {
            // A VMT that will not parse is a material the material system
            // could not load either, and FindMaterial answers that with the
            // error material -- not with a failed compile.
            return null;
        }
    }

    private static async Task<VtfHeaderFacts?> LoadTextureAsync(
        IContentFileSystem content,
        VPath path,
        Dictionary<VPath, VtfHeaderFacts?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(path, out VtfHeaderFacts? cached))
        {
            return cached;
        }

        // Only the header: width, height and reflectivity are all in its first
        // bytes, and reading whole textures for them was most of the bytes a
        // compile's material load read. A file that is missing or is not a
        // readable VTF comes back null either way -- the material system's
        // "preview image bad" answer, which substitutes the fallback size
        // rather than erroring, because the check that would have made it an
        // error is compiled out in the reference.
        VtfHeader? header = await VtfHeaderReader.TryReadAsync(content, path, cancellationToken)
            .ConfigureAwait(false);

        VtfHeaderFacts? facts = header is { } h
            ? new VtfHeaderFacts(h.Width, h.Height, h.Reflectivity)
            : null;

        cache[path] = facts;
        return facts;
    }

    private sealed record VtfHeaderFacts(int Width, int Height, Vec3 Reflectivity);
}
