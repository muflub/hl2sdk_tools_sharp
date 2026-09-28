//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.MaterialPatch;

/// <summary>Which section a generated patch VMT carries: <c>MaterialPatchType_t</c>.</summary>
public enum MaterialPatchType
{
    /// <summary><c>PATCH_INSERT</c>: every key is written into <c>"insert"</c>.</summary>
    Insert,

    /// <summary>
    /// <c>PATCH_REPLACE</c>: the original is walked and a key is written into
    /// <c>"replace"</c> only where the original has it
    /// </summary>
    Replace,
}

/// <summary>One key a patch sets: <c>MaterialPatchInfo_t</c>.</summary>
/// <param name="Key">The material variable.</param>
/// <param name="Value">What it is set to.</param>
/// <param name="RequiredOriginalValue">
/// When not null, the key is patched only where the original value equals this,
/// Ignoring case.
/// </param>
public readonly record struct MaterialPatchInfo(string Key, string Value, string? RequiredOriginalValue = null);

/// <summary>
/// Vbsp's material patcher, as one object
/// per compile: the patched-to-original name table and the pak the patches
/// are written into.
/// </summary>
/// <remarks>
/// <para>
/// Stock keeps the table in two statics (<c>s_SymbolTable</c>,
/// <c>s_MapPatchedMatToOriginalMat</c>, <c>,32</c>). They
/// are shared by every stage that asks "what material is this patch of" —
/// the cubemap fixups that create patches, the detail-prop emitter that reads
/// <c>%detailtype</c> through <c>FindOriginalMaterial</c>, the water depth
/// patches, the WorldVertexTransition fixup — so one instance must be shared
/// by all of them for the compile.
/// </para>
/// <para>
/// THE TWO FILE SOURCES ARE DIFFERENT ON PURPOSE. The "does this material have
/// key X" family reads the ORIGINAL through the game file system only
/// (<c>kv-&gt;LoadFromFile(g_pFileSystem,...)</c>) and
/// never expands a patch; the "load for rewriting" family reads the BSP's own
/// pak first (<c>LoadKeyValuesFromPackOrFile</c>). A patch
/// VMT therefore reads as having none of the keys it inserts — which is why
/// stock never cubemap-patches a material that is itself a <c>patch</c>
/// (measured: <c>l2_cubemap_on_water_and_patch</c>'s <c>p3g/patchedmetal</c>
/// walls get no <c>_x_y_z</c> patch). That is a defect
/// (<see cref="StockQuirk.CubemapIgnoresPatchMaterials"/>): under
/// <see cref="CompliancePolicy.Correct"/> the original is expanded first.
/// </para>
/// <para>
/// <c>#include</c>/<c>#base</c> inside a VMT are not followed; the document's
/// first root is the material, as <c>LoadFromBuffer</c> makes it.
/// </para>
/// </remarks>
public sealed class MaterialPatcher
{
    private readonly IContentFileSystem _content;

    // Patched name -> original name, case-insensitively: s_SymbolTable is
    // constructed case-insensitive, so the RB tree's
    // symbol compare is too. First registration wins, as a CUtlRBTree Find
    // of a key inserted twice returns the earlier node.
    private readonly Dictionary<string, string> _originals = new(StringComparer.OrdinalIgnoreCase);

    // Every game file this patcher has parsed, by content path: the parsed
    // tree, or null for a file that is absent or will not parse. The cubemap
    // pass asks the same handful of questions ("has it $envmap?", "what is
    // its $bottommaterial?", and the REPLACE walk) about the same materials
    // once per material and again per material-and-sample pair, and each
    // question used to read and parse the VMT afresh -- on a full-size map
    // that was thousands of reads of a few hundred files, and most of the
    // pass's time and allocation.
    //
    // Sound because the game file system is read-only for the length of a
    // compile (the patcher writes only to its pak, which this cache never
    // holds), and because nothing a cached tree is handed to may change it:
    // the read-only questions get the shared tree, and every public path
    // gets a deep copy. The cache belongs to this patcher and so to one
    // compile, and goes when it does.
    private readonly Dictionary<string, KeyValuesNode?> _parsedFiles = new(StringComparer.Ordinal);

    /// <summary>A patcher over a compile's content, writing into its pak.</summary>
    /// <param name="content">Where original materials are read from.</param>
    /// <param name="pak">The BSP's pak.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <param name="compliance">
    /// Which of vbsp's patch-reading defects to reproduce; the compile's
    /// <see cref="VbspOptions.Compliance"/>. Null is <see cref="ComplianceOptions.Correct"/>.
    /// </param>
    public MaterialPatcher(IContentFileSystem content, MapPakFile pak, ComplianceOptions? compliance = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(pak);

        _content = content;
        Pak = pak;
        Compliance = compliance ?? ComplianceOptions.Correct;
    }

    /// <summary>The compliance the patcher reads materials under.</summary>
    public ComplianceOptions Compliance { get; }

    /// <summary>The pak every patch is written into.</summary>
    public MapPakFile Pak { get; }

    /// <summary>How many patched names have been registered.</summary>
    public int TranslationCount => _originals.Count;

    /// <summary><c>AddNewTranslation</c>.</summary>
    /// <param name="originalMaterialName">The material patched.</param>
    /// <param name="newMaterialName">The patch's name.</param>
    public void AddTranslation(string originalMaterialName, string newMaterialName)
    {
        ArgumentNullException.ThrowIfNull(originalMaterialName);
        ArgumentNullException.ThrowIfNull(newMaterialName);

        _originals.TryAdd(newMaterialName, originalMaterialName);
    }

    /// <summary>
    /// <c>GetOriginalMaterialNameForPatchedMaterial</c>
    /// Follows the chain of patches back to
    /// the first name that is not itself a patch.
    /// </summary>
    /// <param name="patchedMaterialName">Any material name.</param>
    /// <returns>
    /// The end of the chain, or the argument itself when it is not a patch.
    /// </returns>
    /// <remarks>
    /// The loop has no bound in stock and a cycle would spin forever; a chain
    /// longer than the table is a cycle, and this stops there.
    /// </remarks>
    public string OriginalNameFor(string patchedMaterialName)
    {
        ArgumentNullException.ThrowIfNull(patchedMaterialName);

        string current = patchedMaterialName;
        for (int steps = 0; steps <= _originals.Count; steps++)
        {
            if (!_originals.TryGetValue(current, out string? original))
            {
                break;
            }

            current = original;
        }

        return current;
    }

    /// <summary>
    /// <c>KeyValues::LoadFromFile(g_pFileSystem, path)</c>: the game file
    /// system only, no pak, no patch expansion.
    /// </summary>
    /// <param name="path">A content path, <c>materials/x.vmt</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The first root, or null when the file is absent or will not parse.</returns>
    public async ValueTask<KeyValuesNode?> LoadFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // The caller owns what it is given and may edit it (ExpandPatchAsync
        // inserts into an included material), so it gets its own copy of the
        // shared parse.
        KeyValuesNode? shared = await LoadSharedAsync(path, cancellationToken).ConfigureAwait(false);
        return shared?.Clone();
    }

    // LoadFromFile's answer, parsed once per path and shared: see
    // _parsedFiles for why that is sound. Callers must treat the tree as
    // read-only.
    private async ValueTask<KeyValuesNode?> LoadSharedAsync(string path, CancellationToken cancellationToken)
    {
        // A path that does not normalise (empty, or escaping with "..") is a
        // file the file system cannot open: LoadFromFile's failure, not a throw.
        if (!VPath.TryCreate(path, out VPath contentPath) || contentPath.IsEmpty)
        {
            return null;
        }

        if (_parsedFiles.TryGetValue(contentPath.Value, out KeyValuesNode? cached))
        {
            return cached;
        }

        KeyValuesNode? parsed;
        using (IMemoryOwner<byte>? owner =
            await _content.ReadAsync(contentPath, cancellationToken).ConfigureAwait(false))
        {
            parsed = owner is null ? null : await ParseAsync(owner.Memory, cancellationToken).ConfigureAwait(false);
        }

        // Only a completed read is remembered: a cancelled or failed one
        // throws past this line and leaves nothing behind.
        _parsedFiles[contentPath.Value] = parsed;
        return parsed;
    }

    /// <summary>
    /// <c>LoadKeyValuesFromPackOrFile</c>:
    /// the pak first, read as text, then the game file system.
    /// </summary>
    /// <param name="path">A content path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The first root, or null.</returns>
    public async ValueTask<KeyValuesNode?> LoadFromPackOrFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        byte[]? packed = Pak.Read(path, textMode: true);
        if (packed is not null)
        {
            return await ParseAsync(packed, cancellationToken).ConfigureAwait(false);
        }

        return await LoadFromFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>CreateMaterialPatch</c>, the multi-key form
    /// </summary>
    /// <param name="originalMaterialName">The material being patched.</param>
    /// <param name="newMaterialName">The patch's name.</param>
    /// <param name="patches">The keys to set.</param>
    /// <param name="patchType">Insert or replace.</param>
    /// <param name="cancellationToken">Cancels the read of the original.</param>
    /// <returns>
    /// True when the patch was written. False only on the REPLACE path when the
    /// original cannot be loaded — stock's <c>Assert(0); return;</c> at
    ///Which in a release build writes NOTHING but has already
    /// registered the translation.
    /// </returns>
    public async ValueTask<bool> CreatePatchAsync(
        string originalMaterialName,
        string newMaterialName,
        IReadOnlyList<MaterialPatchInfo> patches,
        MaterialPatchType patchType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalMaterialName);
        ArgumentNullException.ThrowIfNull(newMaterialName);
        ArgumentNullException.ThrowIfNull(patches);

        AddTranslation(originalMaterialName, newMaterialName);

        KeyValuesNode root = new("patch");
        StockKeyValues.SetString(root, "include", $"materials/{originalMaterialName}.vmt");

        KeyValuesNode section = StockKeyValues.FindOrCreate(
            root, patchType == MaterialPatchType.Insert ? "insert" : "replace");

        if (patchType == MaterialPatchType.Replace)
        {
            KeyValuesNode? original = await LoadOriginalAsync(originalMaterialName, cancellationToken)
                .ConfigureAwait(false);

            if (original is null)
            {
                return false;
            }

            PatchRecursive(original, section, patches);
        }
        else
        {
            foreach (MaterialPatchInfo patch in patches)
            {
                StockKeyValues.SetString(section, patch.Key, patch.Value);
            }
        }

        Pak.Add($"materials/{newMaterialName}.vmt", StockKeyValues.SaveBytes(root), textMode: true);
        return true;
    }

    /// <summary>
    /// <c>DoesMaterialHaveKey</c>: the
    /// original, or any section in it, has the key.
    /// </summary>
    /// <param name="materialName">A material, patched or not.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when the material cannot be loaded.</returns>
    public async ValueTask<bool> HasKeyAsync(
        string materialName,
        string key,
        CancellationToken cancellationToken = default)
    {
        KeyValuesNode? kv = await LoadOriginalAsync(materialName, cancellationToken).ConfigureAwait(false);
        return kv is not null && HasKey(kv, key);
    }

    /// <summary>
    /// <c>DoesMaterialHaveKeyValuePair</c>:
    /// some section has the key with that value, ignoring case.
    /// </summary>
    /// <param name="materialName">A material, patched or not.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>False when the material cannot be loaded.</returns>
    public async ValueTask<bool> HasKeyValuePairAsync(
        string materialName,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        KeyValuesNode? kv = await LoadOriginalAsync(materialName, cancellationToken).ConfigureAwait(false);
        return kv is not null && HasKeyValuePair(kv, key, value);
    }

    /// <summary>
    /// <c>GetValueFromMaterial</c>: a
    /// TOP-LEVEL key of the original, ignoring patches.
    /// </summary>
    /// <param name="materialName">A material, patched or not.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or null.</returns>
    public async ValueTask<string?> GetValueAsync(
        string materialName,
        string key,
        CancellationToken cancellationToken = default)
    {
        KeyValuesNode? kv = await LoadOriginalAsync(materialName, cancellationToken).ConfigureAwait(false);
        return kv is null ? null : StockKeyValues.GetString(kv, key);
    }

    /// <summary>
    /// <c>LoadMaterialKeyValues</c>.
    /// </summary>
    /// <param name="materialName">The material.</param>
    /// <param name="expandPatch">
    /// <c>LOAD_MATERIAL_KEY_VALUES_FLAGS_EXPAND_PATCH</c>: run
    /// <see cref="ExpandPatchAsync"/> on the result.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The material, or null.</returns>
    public async ValueTask<KeyValuesNode?> LoadMaterialKeyValuesAsync(
        string materialName,
        bool expandPatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialName);

        KeyValuesNode? kv = await LoadFromPackOrFileAsync($"materials/{materialName}.vmt", cancellationToken)
            .ConfigureAwait(false);

        if (kv is not null && expandPatch)
        {
            kv = await ExpandPatchAsync(kv, cancellationToken).ConfigureAwait(false);
        }

        return kv;
    }

    /// <summary>
    /// <c>WriteMaterialKeyValuesToPak</c>.
    /// </summary>
    /// <param name="materialName">The material's name.</param>
    /// <param name="material">Its keys.</param>
    public void WriteMaterialKeyValuesToPak(string materialName, KeyValuesNode material)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        ArgumentNullException.ThrowIfNull(material);

        Pak.Add($"materials/{materialName}.vmt", StockKeyValues.SaveBytes(material), textMode: true);
    }

    /// <summary>
    /// <c>GetValueFromPatchedMaterial</c>:
    /// a top-level key after expanding the patch chain, pak first.
    /// </summary>
    /// <param name="materialName">The material, typically a patch in the pak.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The value, or null.</returns>
    public async ValueTask<string?> GetValueFromPatchedMaterialAsync(
        string materialName,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        KeyValuesNode? kv = await LoadMaterialKeyValuesAsync(materialName, expandPatch: true, cancellationToken)
            .ConfigureAwait(false);

        return kv is null ? null : StockKeyValues.GetString(kv, key);
    }

    /// <summary>
    /// <c>ExpandPatchFile</c>, vbsp's OWN
    /// patch dialect.
    /// </summary>
    /// <param name="material">A loaded material, patch or not.</param>
    /// <param name="cancellationToken">Cancels the include reads.</param>
    /// <returns>
    /// The expanded material. Not a patch: returned unchanged.
    /// </returns>
    /// <remarks>
    /// <para>Three behaviours differ from the engine's reader:</para>
    /// <list type="bullet">
    /// <item><description>
    /// After an <c>insert</c> is applied the tree IS the include
    /// (<c>keyValues = *includeKeyValues</c>), so the
    /// <c>replace</c> lookup searches the INCLUDED material,
    /// not the patch: a patch with both sections loses its replace. A defect:
    /// <see cref="StockQuirk.PatchExpandInsertDropsReplace"/>.
    /// </description></item>
    /// <item><description>
    /// A patch with neither section never stops being a patch, so the loop
    /// re-reads the same include ten times and stock warns "Infinite recursion
    /// in patch file?". The result is still the patch. A
    /// defect: <see cref="StockQuirk.PatchExpandEmptyPatchNeverResolves"/>.
    /// </description></item>
    /// <item><description>
    /// Only scalar keys are copied (<c>InsertKeyValues</c> has no
    /// <c>TYPE_NONE</c> case,); a section in insert or replace
    /// is ignored, and replace checks existence at the top level only.
    /// </description></item>
    /// </list>
    /// </remarks>
    public async ValueTask<KeyValuesNode> ExpandPatchAsync(
        KeyValuesNode material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);

        KeyValuesNode current = material;
        for (int count = 0; count < 10 && string.Equals(current.Name, "patch", StringComparison.OrdinalIgnoreCase); count++)
        {
            // GetString("include") with the "" default: never null, and an
            // absent include is a load of "" that fails.
            string include = StockKeyValues.GetString(current, "include") ?? string.Empty;

            KeyValuesNode? included = await LoadFromPackOrFileAsync(include, cancellationToken)
                .ConfigureAwait(false);

            if (included is null)
            {
                return current;
            }

            KeyValuesNode patch = current;
            KeyValuesNode? insert = patch.Find("insert");
            if (insert is not null)
            {
                InsertKeyValues(included, insert, checkForExistence: false);
                current = included;
            }

            // Stock looks the replace section up AFTER the tree became the
            // include, so a patch with both loses its replace.
            KeyValuesNode? replace = Compliance.Emulates(StockQuirk.PatchExpandInsertDropsReplace)
                ? current.Find("replace")
                : patch.Find("replace");
            if (replace is not null)
            {
                InsertKeyValues(included, replace, checkForExistence: true);
                current = included;
            }

            // A patch with neither section never stops being one in stock
            // (the loop re-reads the include ten times); it IS its
            // include, which is what the engine's reader makes of it.
            if (insert is null && replace is null && !Compliance.Emulates(StockQuirk.PatchExpandEmptyPatchNeverResolves))
            {
                current = included;
            }
        }

        return current;
    }

    // The original of a (possibly patched) material, as the "does it have key
    // X" family and the REPLACE walk read it: LoadFromFile, raw. Under
    // Correct a patch is expanded first (StockQuirk.CubemapIgnoresPatchMaterials).
    //
    // Every caller only reads the tree it gets, so this hands out the shared
    // parse rather than a copy. Expanding a patch does not touch the patch
    // itself -- it edits the INCLUDED material, which LoadFromPackOrFileAsync
    // returns as the caller's own copy -- so the shared tree survives that too.
    private async ValueTask<KeyValuesNode?> LoadOriginalAsync(string materialName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materialName);

        KeyValuesNode? kv = await LoadSharedAsync($"materials/{OriginalNameFor(materialName)}.vmt", cancellationToken)
            .ConfigureAwait(false);

        if (kv is not null && !Compliance.Emulates(StockQuirk.CubemapIgnoresPatchMaterials))
        {
            kv = await ExpandPatchAsync(kv, cancellationToken).ConfigureAwait(false);
        }

        return kv;
    }

    private static bool HasKey(KeyValuesNode kv, string key)
    {
        if (StockKeyValues.GetString(kv, key) is not null)
        {
            return true;
        }

        foreach (KeyValuesNode child in kv.Children)
        {
            if (StockKeyValues.IsTrueSubKey(child) && HasKey(child, key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasKeyValuePair(KeyValuesNode kv, string key, string value)
    {
        string? found = StockKeyValues.GetString(kv, key);
        if (found is not null && string.Equals(found, value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (KeyValuesNode child in kv.Children)
        {
            if (StockKeyValues.IsTrueSubKey(child) && HasKeyValuePair(child, key, value))
            {
                return true;
            }
        }

        return false;
    }

    // CreateMaterialPatchRecursive. Every true sub
    // key of the original gets a same-named key in the patch, created whether
    // or not anything is set in it -- the writer then drops the empty ones.
    private static void PatchRecursive(KeyValuesNode original, KeyValuesNode patch, IReadOnlyList<MaterialPatchInfo> infos)
    {
        foreach (MaterialPatchInfo info in infos)
        {
            string? value = StockKeyValues.GetString(original, info.Key);
            if (value is null)
            {
                continue;
            }

            if (info.RequiredOriginalValue is not null &&
                !string.Equals(value, info.RequiredOriginalValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StockKeyValues.SetString(patch, info.Key, info.Value);
        }

        foreach (KeyValuesNode child in original.Children)
        {
            if (StockKeyValues.IsTrueSubKey(child))
            {
                PatchRecursive(child, StockKeyValues.FindOrCreate(patch, child.Name), infos);
            }
        }
    }

    private static void InsertKeyValues(KeyValuesNode destination, KeyValuesNode source, bool checkForExistence)
    {
        foreach (KeyValuesNode key in source.Children)
        {
            if (key.Value is null)
            {
                continue;
            }

            if (checkForExistence && destination.Find(key.Name) is null)
            {
                continue;
            }

            StockKeyValues.SetString(destination, key.Name, key.Value);
        }
    }

    private static async ValueTask<KeyValuesNode?> ParseAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            KeyValuesDocument document = await KeyValuesDocument.ParseAsync(bytes, null, cancellationToken)
                .ConfigureAwait(false);
            return document.Root;
        }
        catch (ChunkFileException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

}
