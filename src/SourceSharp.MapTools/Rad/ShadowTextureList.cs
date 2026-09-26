//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Numerics;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// What <c>FindOrLoadIfValid</c> answers: whether the material resolved at all,
/// and which alpha texture it got.
/// </summary>
/// <param name="Found">
/// True when a VMT was there and parsed — <c>bFound</c>
/// </param>
/// <param name="Index">
/// The <see cref="AlphaTexture"/>'s index, or -1 when the material casts no
/// alpha shadow.
/// </param>
/// <remarks>
/// The two are INDEPENDENT, and that is the point of returning both.
/// <c>LoadAllTexturesForModel</c> breaks its search-path loop on
/// <see cref="Found"/>, not on <see cref="Index"/>: a texture
/// whose VMT is found in the first material search path but is opaque stops the
/// search there and never looks in the second path, even if the second holds an
/// alpha-tested material of the same name.
/// </remarks>
public readonly record struct ShadowTextureLookup(bool Found, int Index);

/// <summary>
/// What a prop triangle gets out of the texture-shadow database: its average
/// coverage, and the material entry that lets a ray re-sample it later.
/// </summary>
/// <param name="Coverage">
/// The fraction of light the triangle lets through on average, 0 to 1.
/// </param>
/// <param name="MaterialIndex">
/// The material entry to hand <see cref="ShadowTextureList.SampleMaterial"/>,
/// or -1 when the triangle is fully opaque.
/// </param>
/// <remarks>
/// A coverage of exactly 1 produces -1
/// and therefore no <c>FCACHETRI_TRANSPARENT</c> flag, so the ray tracer treats
/// the triangle as a solid blocker and never calls back into the texture at
/// all — which is the only reason it is affordable to run this over every
/// triangle of every opted-in prop.
/// </remarks>
public readonly record struct TriangleShadowMaterial(float Coverage, int MaterialIndex)
{
    /// <summary>
    /// True when the triangle gets <c>FCACHETRI_TRANSPARENT</c>
    /// </summary>
    public bool IsTransparent => MaterialIndex >= 0;

    /// <summary>
    /// The value stock writes into the triangle's colour x component
    /// </summary>
    /// <remarks>
    /// Zero for an opaque triangle, because <c>color</c> is left at
    /// <c>vec3_origin</c> on that branch — the coverage is computed and then
    /// discarded. Exposed as its own member so a caller cannot accidentally
    /// store a coverage of 1.0 where stock stores 0.
    /// </remarks>
    public float ColorRed => IsTransparent ? Coverage : 0f;
}

/// <summary>
/// The alpha textures that cast shadows, and the per-triangle material entries
/// that address them.
/// </summary>
/// <remarks>
/// <para>
/// <c>CShadowTextureList</c>,
/// which in stock is the single global <c>g_ShadowTextureList</c> at
///An instance here, with no mutable statics: the tables it holds
/// are written during the prop load and read from inside the ray tracer's
/// transparency callback, so making them global makes a concurrent compile
/// impossible to reason about.
/// </para>
/// <para>
/// It serves two callers, and the second is what makes the first worth having.
/// The LOAD path asks <see cref="AddTriangle"/> what a triangle's average
/// coverage is and which material entry it gets
/// The TRACE path — stock's
/// <c>ComputeCoverageFromTexture</c>, installed as the ray
/// tracer's transparency callback — asks
/// <see cref="ComputeCoverageFromTexture"/> what fraction of light gets through
/// a given material entry at given barycentrics, which is a per-ray question
/// and needs the UVs the load path stored.
/// </para>
/// <para>
/// NOTHING IN HERE CHANGES WHICH TRIANGLES EXIST. This lane measured stock's
/// own <c>-dumptrace</c> output on <c>dm_lockdown</c> with and without
/// <c>-textureshadows</c> and the two files are byte-identical (and identical
/// again as a pair under <c>-StaticPropPolys</c>). All <c>-textureshadows</c>
/// does is set <c>FCACHETRI_TRANSPARENT</c> and a material index on triangles
/// that were already there, so a triangle count is not a gate on any of this.
/// </para>
/// </remarks>
public sealed class ShadowTextureList
{
    // CUtlDict's default compare type is k_eDictCompareTypeCaseInsensitive
    // And default-constructs it -- so
    // two search paths spelling the same material differently share one entry.
    private readonly Dictionary<string, int> _indexByMaterial =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<AlphaTexture> _textures = [];
    private readonly List<MaterialEntry> _materialEntries = [];

    /// <summary>The scale from an alpha byte to a coverage fraction.</summary>
    /// <remarks>.</remarks>
    public const float AlphaScale = 1f / 255f;

    /// <summary>How many distinct alpha textures have been loaded.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>How many per-triangle material entries have been added.</summary>
    public int MaterialEntryCount => _materialEntries.Count;

    /// <summary>One loaded alpha texture.</summary>
    /// <param name="index">The texture's index, as a lookup returned it.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no texture.</exception>
    public AlphaTexture Texture(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _textures.Count);

        return _textures[index];
    }

    /// <summary>
    /// Which alpha texture a material entry samples.
    /// </summary>
    /// <param name="materialIndex">The material entry's index.</param>
    /// <returns>The alpha texture's index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no material entry.</exception>
    public int MaterialTextureIndex(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, _materialEntries.Count);

        return _materialEntries[materialIndex].TextureIndex;
    }

    /// <summary>
    /// The three texture coordinates a material entry stored.
    /// </summary>
    /// <param name="materialIndex">The material entry's index.</param>
    /// <returns>The triangle's UVs, in the order they were added.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no material entry.</exception>
    public (Vector2 T0, Vector2 T1, Vector2 T2) MaterialTexCoords(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, _materialEntries.Count);

        MaterialEntry entry = _materialEntries[materialIndex];
        return (entry.T0, entry.T1, entry.T2);
    }

    /// <summary>
    /// Adds an alpha texture directly, outside the VMT acceptance rule.
    /// </summary>
    /// <param name="materialName">
    /// The key to cache it under, matched without regard to case.
    /// </param>
    /// <param name="texture">The texture.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="materialName"/> or <paramref name="texture"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">The material is already loaded.</exception>
    /// <remarks>
    /// Not a stock entry point: stock only ever inserts from
    /// <c>FindOrLoadIfValid</c>. It exists so
    /// the coverage and sampling maths can be pinned against a hand-built alpha
    /// plane without a VTF fixture standing between the fact and the arithmetic
    /// it is checking.
    /// </remarks>
    public int AddTexture(string materialName, AlphaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(texture);

        string key = MaterialFactsReader.Normalize(materialName);
        if (_indexByMaterial.ContainsKey(key))
        {
            throw new ArgumentException(
                $"'{materialName}' is already in the shadow texture list", nameof(materialName));
        }

        int index = _textures.Count;
        _textures.Add(texture);
        _indexByMaterial[key] = index;
        return index;
    }

    /// <summary>
    /// Finds a material in the database, loading it if it is one that casts an
    /// alpha shadow.
    /// </summary>
    /// <param name="materialPath">
    /// The material, as <c>LoadAllTexturesForModel</c> spells it:
    /// <c>materials/&lt;search path&gt;&lt;texture&gt;.vmt</c>. Any spelling
    /// <see cref="MaterialFactsReader.Normalize(string)"/> accepts works.
    /// </param>
    /// <param name="content">Where to read the VMT and the VTF from.</param>
    /// <param name="options">The compile's material-system configuration.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether the material was there, and which texture it got.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="materialPath"/> or <paramref name="content"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// THE ACCEPTANCE RULE: a material is
    /// accepted only if it has <c>$translucent</c> OR <c>$alphatest</c>, AND a
    /// <c>$basetexture</c> whose VTF loads. Everything else answers -1.
    /// </para>
    /// <para>
    /// The two opacity keys are tested with <c>KeyValues::FindKey</c>
    /// — PRESENCE, not truth. So <c>$translucent 0</c> is
    /// accepted and casts alpha shadows, which is why this reads
    /// <see cref="MaterialFacts.GetVar"/> for null rather than asking
    /// <see cref="MaterialFacts.Opacity"/>: that property is the material
    /// system's answer, and it folds in <c>$alpha</c> and the truthiness this
    /// call does not want.
    /// </para>
    /// <para>
    /// ONE KNOWN DIVERGENCE, recorded rather than hidden. Stock parses the VMT
    /// as raw KeyValues here and does not resolve <c>patch</c> materials, so a
    /// <c>patch</c> that includes an alpha-tested material is REJECTED by stock
    /// (its root key is <c>patch</c> and neither opacity key is at the root).
    /// This port goes through <see cref="MaterialFactsReader"/>, which resolves
    /// the patch first and therefore accepts it. The patch is resolved in
    /// vbsp's own dialect, which is the dialect the rest of the port uses, and
    /// re-parsing VMTs here to reproduce the miss would be a second material
    /// reader in the tree. Pinned by
    /// <c>ShadowTextureListTests.APatchMaterialIsAcceptedWhereStockWouldRejectIt</c>
    /// so the difference is visible if it ever matters.
    /// </para>
    /// <para>
    /// The cache holds only ACCEPTED materials (inserts inside the
    /// innermost <c>if</c>), so a rejected material is re-read from disk every
    /// time it is asked about. Reproduced, because the read is observable
    /// through <c>RecordingContentFileSystem</c> and a compile's recorded input
    /// set would otherwise differ from stock's.
    /// </para>
    /// </remarks>
    public async Task<ShadowTextureLookup> FindOrLoadIfValidAsync(
        string materialPath,
        IContentFileSystem content,
        MaterialFactsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialPath);
        ArgumentNullException.ThrowIfNull(content);

        string key = MaterialFactsReader.Normalize(materialPath);
        if (_indexByMaterial.TryGetValue(key, out int cached))
        {
            return new ShadowTextureLookup(Found: true, cached);
        }

        MaterialFacts facts = await MaterialFactsReader
            .ReadAsync(key, content, options, cancellationToken)
            .ConfigureAwait(false);

        if (!facts.Found)
        {
            // 695-700 -- LoadFromBuffer failed, so bFound stays false and the
            // caller keeps walking the model's material search paths.
            return new ShadowTextureLookup(Found: false, -1);
        }

        // 702 -- FindKey, so presence. See the remarks.
        bool opacityKey = facts.GetVar(MaterialVarNames.Translucent) is not null
            || facts.GetVar(MaterialVarNames.AlphaTest) is not null;

        if (!opacityKey || facts.BaseTexture is not VPath baseTexture)
        {
            return new ShadowTextureLookup(Found: true, -1);
        }

        // 723 -- $nocull is FindKey too, so "$nocull 0" allows backfaces.
        bool allowBackface = facts.GetVar(NoCull) is not null;

        AlphaTexture? texture = await LoadAlphaTextureAsync(
            content, baseTexture, allowBackface, cancellationToken).ConfigureAwait(false);

        if (texture is null)
        {
            // LoadVTFRGB8888 returned NULL: no file, not a VTF, or a format
            // ConvertImageFormat would not take (656).
            return new ShadowTextureLookup(Found: true, -1);
        }

        int index = _textures.Count;
        _textures.Add(texture);
        _indexByMaterial[key] = index;
        return new ShadowTextureLookup(Found: true, index);
    }

    /// <summary>
    /// Loads every texture of a model that has opted into texture shadows.
    /// </summary>
    /// <param name="model">The model's MDL.</param>
    /// <param name="content">Where to read materials from.</param>
    /// <param name="options">The compile's material-system configuration.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// One entry per studio texture, in the model's own order: the alpha
    /// texture's index, or -1 for a texture that casts no alpha shadow.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="content"/> is null.
    /// </exception>
    /// <remarks>
    /// <c>numtextures</c> outer,
    /// <c>numcdtextures</c> inner, building
    /// <c>materials/&lt;cdtexture&gt;&lt;texturename&gt;.vmt</c> and stopping
    /// at the first search path where the material EXISTS — see
    /// <see cref="ShadowTextureLookup"/> for why that is not the first search
    /// path where it is transparent.
    /// </remarks>
    public async Task<int[]> LoadAllTexturesForModelAsync(
        MdlFile model,
        IContentFileSystem content,
        MaterialFactsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(content);

        IReadOnlyList<string> searchPaths = model.MaterialSearchPaths();
        int[] textureList = new int[model.Textures().Length];

        for (int i = 0; i < textureList.Length; i++)
        {
            int textureIndex = -1;
            string textureName = model.TextureName(i);

            foreach (string searchPath in searchPaths)
            {
                ShadowTextureLookup lookup = await FindOrLoadIfValidAsync(
                    $"{MaterialFactsReader.MaterialsDirectory}/{searchPath}{textureName}"
                        + MaterialFactsReader.MaterialExtension,
                    content,
                    options,
                    cancellationToken).ConfigureAwait(false);

                if (lookup.Found)
                {
                    textureIndex = lookup.Index;
                    break;
                }
            }

            textureList[i] = textureIndex;
        }

        return textureList;
    }

    /// <summary>
    /// The average coverage of a triangle, and the material entry it gets.
    /// </summary>
    /// <param name="shadowTextureIndex">The alpha texture the triangle's material got.</param>
    /// <param name="t0">The first vertex's texture coordinate.</param>
    /// <param name="t1">The second vertex's texture coordinate.</param>
    /// <param name="t2">The third vertex's texture coordinate.</param>
    /// <returns>The coverage, and -1 for an opaque triangle.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The texture index names no texture.</exception>
    /// <remarks>
    /// The load path's whole interaction with this class
    /// As one call so that the rule
    /// "coverage of 1 means no material entry" cannot be got wrong by a caller
    /// and cannot drift between the two callers stock has.
    /// </remarks>
    public TriangleShadowMaterial AddTriangle(
        int shadowTextureIndex,
        Vector2 t0,
        Vector2 t1,
        Vector2 t2)
    {
        float coverage = ComputeCoverageForTriangle(shadowTextureIndex, t0, t1, t2);

        return coverage < 1f
            ? new TriangleShadowMaterial(coverage, AddMaterialEntry(shadowTextureIndex, t0, t1, t2))
            : new TriangleShadowMaterial(coverage, -1);
    }

    /// <summary>
    /// Records a triangle's texture and texture space for the trace-time
    /// callback to sample later.
    /// </summary>
    /// <param name="shadowTextureIndex">The alpha texture's index.</param>
    /// <param name="t0">The first vertex's texture coordinate.</param>
    /// <param name="t1">The second vertex's texture coordinate.</param>
    /// <param name="t2">The third vertex's texture coordinate.</param>
    /// <returns>The new material entry's index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The texture index names no texture.</exception>
    /// <remarks>
    /// Append-only and never de-duplicated:
    /// two triangles with identical UVs get two entries, because the index is
    /// the ray tracer's per-triangle payload and stock has nowhere to put a
    /// shared one.
    /// </remarks>
    public int AddMaterialEntry(int shadowTextureIndex, Vector2 t0, Vector2 t1, Vector2 t2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shadowTextureIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shadowTextureIndex, _textures.Count);

        _materialEntries.Add(new MaterialEntry(shadowTextureIndex, t0, t1, t2));
        return _materialEntries.Count - 1;
    }

    /// <summary>
    /// The average alpha of a triangle's texture footprint, 0 to 1.
    /// </summary>
    /// <param name="shadowTextureIndex">The alpha texture's index.</param>
    /// <param name="t0">The first vertex's texture coordinate.</param>
    /// <param name="t1">The second vertex's texture coordinate.</param>
    /// <param name="t2">The third vertex's texture coordinate.</param>
    /// <returns>The coverage, 0 for fully transparent and 1 for fully opaque.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The texture index names no texture.</exception>
    /// <remarks>
    /// <para>
    /// And stock labels it HACKHACK in its
    /// own comment. TWO DELIBERATE DEFECTS ARE REPRODUCED HERE.
    /// </para>
    /// <para>
    /// FIRST: it averages over the AXIS-ALIGNED BOUNDING BOX of the triangle in
    /// UV space, not over the triangle. Roughly half the texels it averages are
    /// outside the triangle being shaded, so a thin diagonal sliver across a
    /// mostly-transparent texture reports the box's average and not its own.
    /// A "fixed" version that integrated over the triangle would be a better
    /// number and a different compile, and the thing being matched is stock's
    /// lightmaps. Pinned by
    /// <c>ShadowTextureListTests.CoverageAveragesTheBoundingBoxAndNotTheTriangle</c>,
    /// which is built so a true triangle-area average would give a different
    /// answer.
    /// </para>
    /// <para>
    /// SECOND: the UV box is CLAMPED to [0,1], under a
    /// comment reading "UNDONE: Do something about tiling". A triangle whose
    /// UVs run 0..4 across a tiling texture therefore has its footprint
    /// collapsed to the texture's first tile, and one whose UVs are entirely
    /// outside [0,1] — say 2..3 — collapses to the single texel at (1,1). Note
    /// that <see cref="SampleMaterial"/> at trace time does NOT clamp; it
    /// wraps. So the average a triangle is admitted with and the values it is
    /// later sampled at come from different parts of the texture.
    /// </para>
    /// <para>
    /// The box is walked in TEXEL coordinates scaled by <c>width - 1</c>
    /// Inclusive at both ends, and truncated rather than
    /// rounded — which is a third disagreement with <see cref="AlphaTexture.Sample"/>,
    /// which scales by <c>width</c> and rounds.
    /// </para>
    /// </remarks>
    public float ComputeCoverageForTriangle(
        int shadowTextureIndex,
        Vector2 t0,
        Vector2 t1,
        Vector2 t2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shadowTextureIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shadowTextureIndex, _textures.Count);

        // 784-793 -- the AABB, min then max on each axis.
        float uMin = MathF.Min(MathF.Min(t0.X, t1.X), t2.X);
        float uMax = MathF.Max(MathF.Max(t0.X, t1.X), t2.X);
        float vMin = MathF.Min(MathF.Min(t0.Y, t1.Y), t2.Y);
        float vMax = MathF.Max(MathF.Max(t0.Y, t1.Y), t2.Y);

        // 795-798 -- "UNDONE: Do something about tiling".
        uMin = Math.Clamp(uMin, 0f, 1f);
        uMax = Math.Clamp(uMax, 0f, 1f);
        vMin = Math.Clamp(vMin, 0f, 1f);
        vMax = Math.Clamp(vMax, 0f, 1f);

        AlphaTexture texture = _textures[shadowTextureIndex];

        // 803-806 -- float to int is a C truncation, and the scale is
        // width - 1, so uMax of 1.0 lands on the LAST column rather than one
        // past it.
        int u0 = (int)(uMin * (texture.Width - 1));
        int u1 = (int)(uMax * (texture.Width - 1));
        int v0 = (int)(vMin * (texture.Height - 1));
        int v1 = (int)(vMax * (texture.Height - 1));

        int total = 0;
        int count = 0;
        for (int v = v0; v <= v1; v++)
        {
            for (int u = u0; u <= u1; u++)
            {
                total += texture.Texel(u, v);
                count++;
            }
        }

        // The clamp above makes u1 >= u0 and v1 >= v0 for every
        // finite input, so count is at least one; the branch is stock's and is
        // kept because a NaN texture coordinate reaches it.
        return count > 0 ? total / (count * 255f) : 1f;
    }

    /// <summary>
    /// The alpha a ray sees at a point on a triangle, 0 to 255.
    /// </summary>
    /// <param name="materialIndex">The material entry the triangle carries.</param>
    /// <param name="b0">The first barycentric coordinate.</param>
    /// <param name="b1">The second barycentric coordinate.</param>
    /// <param name="b2">The third barycentric coordinate.</param>
    /// <param name="backface">Whether the ray hit the triangle from behind.</param>
    /// <returns>The alpha, 0 for fully blocked and 255 for fully transmitting.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no material entry.</exception>
    /// <remarks>
    /// <para>
    /// The barycentrics interpolate the
    /// three stored UVs, and <see cref="AlphaTexture.Sample"/> does the rest —
    /// including the wrap that makes <see cref="AlphaTexture.ClampU"/> dead.
    /// </para>
    /// <para>
    /// A backfacing hit on a texture without <c>$nocull</c> returns 0, meaning
    /// the triangle blocks everything. See
    /// <see cref="ComputeCoverageFromTexture"/> for why that branch is
    /// unreachable in stock.
    /// </para>
    /// </remarks>
    public int SampleMaterial(int materialIndex, float b0, float b1, float b2, bool backface)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, _materialEntries.Count);

        MaterialEntry entry = _materialEntries[materialIndex];
        AlphaTexture texture = _textures[entry.TextureIndex];

        if (backface && !texture.AllowBackface)
        {
            return 0;
        }

        Vector2 uv = (b0 * entry.T0) + (b1 * entry.T1) + (b2 * entry.T2);
        return texture.Sample(uv.X, uv.Y);
    }

    /// <summary>
    /// The fraction of light that gets through a triangle at a ray's hit point.
    /// </summary>
    /// <param name="b0">The first barycentric coordinate.</param>
    /// <param name="b1">The second barycentric coordinate.</param>
    /// <param name="b2">The third barycentric coordinate.</param>
    /// <param name="materialIndex">
    /// The hit triangle's material entry — stock reads it off the ray tracer
    /// with <c>g_RtEnv.GetTriangleMaterial(hitID)</c>.
    /// </param>
    /// <returns>The transmitted fraction, 0 to 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index names no material entry.</exception>
    /// <remarks>
    /// <para>
    /// <c>ComputeCoverageFromTexture</c>,
    /// the ray tracer's transparency callback. The argument order is stock's,
    /// barycentrics before the id.
    /// </para>
    /// <para>
    /// A DELIBERATE REPRODUCTION OF A STOCK DEFECT: <c>bBackface</c> is
    /// HARDCODED FALSE, under a commented-out two-line body
    /// That would have computed it from the ray direction and
    /// the triangle normal ("UNDONE: Pass ray down to determine backfacing?").
    /// The consequence is that <c>$nocull</c> and
    /// <see cref="AlphaTexture.AllowBackface"/> have NO EFFECT on a compile:
    /// the only caller of <see cref="SampleMaterial"/> never passes true, so
    /// the backface branch is dead in stock and the flag is loaded, stored and
    /// never read. Pinned by
    /// <c>ShadowTextureListTests.TheTraceCallbackNeverReportsABackfaceHit</c>.
    /// </para>
    /// </remarks>
    public float ComputeCoverageFromTexture(float b0, float b1, float b2, int materialIndex) =>
        AlphaScale * SampleMaterial(materialIndex, b0, b1, b2, backface: false);

    /// <summary><c>$nocull</c>, which no other compile stage reads.</summary>
    /// <remarks>
    /// Spelled here rather than added to <see cref="MaterialVarNames"/> because
    /// that type is the set of variables the two COMPILERS name, and this one
    /// is named by exactly one function in vrad
    /// </remarks>
    private const string NoCull = "$nocull";

    private static async Task<AlphaTexture?> LoadAlphaTextureAsync(
        IContentFileSystem content,
        VPath path,
        bool allowBackface,
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
            return AlphaTexture.FromVtf(VtfFile.Parse(owner.Memory), allowBackface);
        }
        catch (InvalidVtfException)
        {
            // 661 -- Unserialize returned false, so LoadVTFRGB8888 returns
            // NULL and the material is silently given no alpha shadow.
            return null;
        }
        catch (NotSupportedException)
        {
            // 677-681 -- ConvertImageFormat returned false. Same answer.
            return null;
        }
    }

    private readonly record struct MaterialEntry(int TextureIndex, Vector2 T0, Vector2 T1, Vector2 T2);
}

/// <summary>
/// The models named by <c>forcetextureshadow</c> lines, and the opt-in gate
/// that decides whether a model's textures are loaded at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>g_ForcedTextureShadowsModels</c> and the
/// three functions around it, as an instance rather than a
/// global.
/// </para>
/// <para>
/// The lines themselves are parsed by
/// <see cref="MapFormats.Text.RadLightFile"/>, which already ports
/// <c>ReadLightFile</c>'s <c>noshadow</c> and <c>forcetextureshadow</c>
/// Branches and hands back the raw
/// names. It is THIS type that cleans them, matching stock, where
/// <c>ForceTextureShadowsOnModel</c> — not the parser — calls
/// <c>CleanModelName</c>.
/// </para>
/// </remarks>
public sealed class ForcedTextureShadowModels
{
    // CUtlSymbolTable's caseInsensitive argument defaults to FALSE
    // And default-constructs it, so
    // this really is an ordinal match. See CleanModelName.
    private readonly HashSet<string> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>STUDIOHDR_FLAGS_CAST_TEXTURE_SHADOWS</c>.
    /// </summary>
    /// <remarks>
    /// The bit a modeller sets with <c>$casttextureshadows</c> in a QC. It is
    /// the ONLY way a model opts in without the map author editing a
    /// <c>lights.rad</c>.
    /// </remarks>
    public const int CastTextureShadowsFlag = 0x00040000;

    /// <summary>The <c>models/</c> prefix <see cref="CleanModelName"/> strips.</summary>
    public const string ModelDirectory = "models/";

    /// <summary>How many models have been named.</summary>
    public int Count => _models.Count;

    /// <summary>
    /// Normalises a model name the way stock keys its forced-model table.
    /// </summary>
    /// <param name="modelName">The name, as a <c>lights.rad</c> line or a BSP lump spells it.</param>
    /// <returns>The name without a leading <c>models/</c> and without an extension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <c>CleanModelName</c>. Two things
    /// about it are easy to assume and wrong.
    /// </para>
    /// <para>
    /// IT DOES NOT CHANGE CASE. The <c>models/</c> test is
    /// <c>Q_strnicmp</c> and so is case-insensitive, but the name is then
    /// copied verbatim and compared through a case-SENSITIVE
    /// <c>CUtlSymbolTable</c>. So <c>forcetextureshadow Props_C17/OilDrum001</c>
    /// does NOT match the static prop <c>models/props_c17/oildrum001.mdl</c>,
    /// and a map author who capitalises the line gets silence rather than an
    /// error. Reproduced, and pinned by
    /// <c>ShadowTextureListTests.AForcedModelNameIsMatchedCaseSensitively</c>.
    /// </para>
    /// <para>
    /// IT TRUNCATES AT THE FIRST DOT ANYWHERE, not at the extension —
    /// <c>strchr(pOutput, '.')</c>. So a model under a directory
    /// with a dot in its name loses everything from that dot onwards. Also
    /// reproduced.
    /// </para>
    /// <para>
    /// The backslash a BSP may carry is NOT normalised here either: stock does
    /// no <c>Q_FixSlashes</c> on this path, so <c>models\x.mdl</c> and
    /// <c>models/x.mdl</c> are different keys.
    /// </para>
    /// </remarks>
    public static string CleanModelName(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);

        string name = modelName;

        // 905-909 -- Q_strnicmp, so the PREFIX test ignores case even though
        // nothing else here does.
        if (name.StartsWith(ModelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            name = name[ModelDirectory.Length..];
        }

        // 912-917 -- strchr finds the FIRST dot, not the last.
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>
    /// Records a model as forced, as a <c>forcetextureshadow</c> line does.
    /// </summary>
    /// <param name="modelName">The name from the line.</param>
    /// <returns>True when this added it, false when it was already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    /// <remarks><c>ForceTextureShadowsOnModel</c>.</remarks>
    public bool Add(string modelName) => _models.Add(CleanModelName(modelName));

    /// <summary>
    /// Records every model a <c>lights.rad</c> named.
    /// </summary>
    /// <param name="modelNames">
    /// The raw names, for example
    /// <see cref="MapFormats.Text.RadLightFile.ForcedTextureShadowModels"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="modelNames"/> is null.</exception>
    public void AddRange(IEnumerable<string> modelNames)
    {
        ArgumentNullException.ThrowIfNull(modelNames);

        foreach (string modelName in modelNames)
        {
            Add(modelName);
        }
    }

    /// <summary>Whether a model was named by a <c>forcetextureshadow</c> line.</summary>
    /// <param name="modelName">The model, as the BSP's static prop dictionary spells it.</param>
    /// <returns>True when it was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    /// <remarks><c>IsModelTextureShadowsForced</c>.</remarks>
    public bool Contains(string modelName) => _models.Contains(CleanModelName(modelName));

    /// <summary>
    /// The opt-in gate: whether this model's textures are loaded at all.
    /// </summary>
    /// <param name="textureShadowsEnabled"><c>g_bTextureShadows</c>, set by <c>-textureshadows</c>.</param>
    /// <param name="studioHeaderFlags"><c>studiohdr_t::flags</c>.</param>
    /// <param name="modelName">The model, as the static prop dictionary spells it.</param>
    /// <returns>True when <c>LoadAllTexturesForModel</c> should run for it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelName"/> is null.</exception>
    /// <remarks>
    /// The switch AND either the model's
    /// own flag or a <c>forcetextureshadow</c> line. Without
    /// <c>-textureshadows</c> not one VMT of this path is read, which is why a
    /// compile that has never heard of it costs nothing.
    /// </remarks>
    public bool ShouldLoadTextures(bool textureShadowsEnabled, int studioHeaderFlags, string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);

        return textureShadowsEnabled
            && (((studioHeaderFlags & CastTextureShadowsFlag) != 0) || Contains(modelName));
    }
}
