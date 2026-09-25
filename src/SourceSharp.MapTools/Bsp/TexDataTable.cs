using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// LUMP_TEXDATA: <c>dtexdata</c> and <c>numtexdata</c>,
/// <c>utils/common/bsplib.h:104-105</c>, with the four functions in
/// <c>utils/vbsp/textures.cpp</c> that build it.
/// </summary>
/// <remarks>
/// <para>
/// One entry per distinct material the map references, carrying the
/// reflectivity and dimensions read out of that material. Append-only, and its
/// indices are stored in <see cref="TexInfoTable"/>, so the order is output.
/// </para>
/// <para>
/// Stock keeps <c>g_SurfaceProperties</c> as a separate array indexed by the
/// same number (<c>vbsp.h:397</c>) rather than a field of <c>dtexdata_t</c>,
/// because <c>dtexdata_t</c> is wire format and the surface property is not.
/// <see cref="SurfaceProperties"/> is that array, and is parallel by
/// construction.
/// </para>
/// </remarks>
public sealed class TexDataTable
{
    /// <summary>
    /// The format's ceiling: <c>MAX_MAP_TEXDATA</c>,
    /// <c>public/bspfile.h:64</c>.
    /// </summary>
    public const int MaxMapTexData = 2048;

    private readonly List<DTexData> _texData = [];
    private readonly List<int> _surfaceProperties = [];

    /// <summary>Creates a table over a string table.</summary>
    /// <param name="strings">The name table entries point into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="strings"/> is null.</exception>
    public TexDataTable(TexDataStringTable strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        Strings = strings;
    }

    /// <summary>The string table this one's names live in.</summary>
    public TexDataStringTable Strings { get; }

    /// <summary>How many entries: stock's <c>numtexdata</c>.</summary>
    public int Count => _texData.Count;

    /// <summary>The entries, in insertion order: the TEXDATA lump.</summary>
    public IReadOnlyList<DTexData> TexData => _texData;

    /// <summary>
    /// The surface property index per entry, or -1:
    /// <c>g_SurfaceProperties</c>.
    /// </summary>
    /// <remarks>
    /// -1 is stock's "the material has no <c>$surfaceprop</c>, or there is no
    /// physics surface-prop table loaded" (<c>textures.cpp:347</c> and
    /// <c>:352</c>). An entry created for a material that did not resolve is
    /// never written at all by stock (<c>textures.cpp:493-499</c> returns
    /// early), leaving whatever was in the array — zero on a fresh array. That
    /// zero is reproduced rather than -1.
    /// </remarks>
    public IReadOnlyList<int> SurfaceProperties => _surfaceProperties;

    /// <summary>
    /// The surface-property database <see cref="SurfaceProperties"/> indexes
    /// into: stock's <c>physprops</c>, filled by <c>LoadSurfaceProperties</c>
    /// (<c>textures.cpp:711</c>) before the map loads (<c>vbsp.cpp:1310</c>).
    /// </summary>
    /// <remarks>
    /// Null resolves every material to -1, stock's value when no physics
    /// library loaded. <see cref="MapFileLoader.LoadAsync"/> loads it from the
    /// compile's content when it is still null, so every entry the map creates
    /// is resolved at the moment stock resolves it.
    /// </remarks>
    public SurfacePropertyTable? PropertyTable { get; set; }

    /// <summary>The entry at an index.</summary>
    /// <param name="index">The texdata number.</param>
    /// <returns>The entry.</returns>
    public DTexData this[int index] => _texData[index];

    /// <summary>The material name at an index.</summary>
    /// <param name="index">The texdata number.</param>
    /// <returns>The name as the string table stores it.</returns>
    public string NameOf(int index) => Strings.GetString(_texData[index].NameStringTableId);

    /// <summary>
    /// Finds an entry by name, or -1: <c>FindTexData</c>,
    /// <c>textures.cpp:452</c>.
    /// </summary>
    /// <param name="name">The material name.</param>
    /// <returns>The index, or -1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <remarks>
    /// Case-INSENSITIVE (<c>Q_stricmp</c>), unlike
    /// <see cref="TextureReferenceTable.FindMiptexAsync"/>'s case-sensitive
    /// scan. The two disagreeing is stock's behaviour and not an oversight to
    /// harmonise: two spellings of one material get two
    /// <c>textureref</c> entries and one texdata.
    /// </remarks>
    public int Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        for (int i = 0; i < _texData.Count; i++)
        {
            if (string.Equals(NameOf(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds an entry by name or creates one from the material:
    /// <c>FindOrCreateTexData</c>, <c>textures.cpp:471</c>.
    /// </summary>
    /// <param name="name">The material name, as the map spells it.</param>
    /// <param name="materials">Where the material's facts come from.</param>
    /// <param name="diagnostics">Where a missing material is reported, or null.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>The entry's index.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="materials"/> is null.
    /// </exception>
    /// <exception cref="MapCompileException">The table is full.</exception>
    /// <remarks>
    /// <para>
    /// The name goes into the string table with the casing the caller supplied
    /// — stock's <c>_alloca</c> copy at <c>textures.cpp:473</c> does no
    /// lowercasing, unlike <c>FindAliasedTexData</c>'s, so what lands in
    /// TEXDATA_STRING_DATA is the VMF's own spelling.
    /// </para>
    /// <para>
    /// A material that does not resolve still gets an entry, and a VALID index
    /// is returned (<c>textures.cpp:497</c>) — with reflectivity and dimensions
    /// left at zero and no surface property written. That is the difference
    /// from <see cref="FindAliasedAsync(string, DTexData, MaterialFactsCache, ICollection{CompileDiagnostic}?, CancellationToken)"/>, which returns -1 in the same situation.
    /// </para>
    /// </remarks>
    public async ValueTask<int> FindOrCreateAsync(
        string name,
        MaterialFactsCache materials,
        ICollection<CompileDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(materials);

        int existing = Find(name);
        if (existing >= 0)
        {
            return existing;
        }

        int index = Append(name);

        MaterialFacts facts = await materials.GetAsync(name, cancellationToken)
            .ConfigureAwait(false);

        if (!facts.Found)
        {
            diagnostics?.Add(new CompileDiagnostic(
                TextureDiagnostics.MaterialNotFound,
                DiagnosticSeverity.Warning,
                $"material not found: \"{name}\""));
            return index;
        }

        Fill(index, facts);
        return index;
    }

    /// <summary>
    /// Creates an entry named for one material but described by another:
    /// <c>FindAliasedTexData</c>, <c>textures.cpp:402</c>.
    /// </summary>
    /// <param name="name">The new name. Lowercased before anything else.</param>
    /// <param name="source">The entry whose material describes it.</param>
    /// <param name="materials">Where the material's facts come from.</param>
    /// <param name="diagnostics">Where a missing material is reported, or null.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>The entry's index, or -1 when the source material is missing.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="materials"/> is null.
    /// </exception>
    /// <exception cref="MapCompileException">The table is full.</exception>
    /// <remarks>
    /// <para>
    /// Three quirks are stock's and are kept. The name is lowercased in place
    /// (<c>strlwr</c>, <c>textures.cpp:406</c>) BEFORE the dedup scan, and that
    /// scan is a case-SENSITIVE <c>strcmp</c> (<c>:413</c>) — so it can only
    /// ever match an entry that was already stored lowercase.
    /// </para>
    /// <para>
    /// And the -1 return happens AFTER the entry has been appended and the name
    /// added to the string table (<c>:424-435</c>), so a failure still grows
    /// both lumps by one. Returning -1 without the append would produce a
    /// different TEXDATA lump than stock's.
    /// </para>
    /// </remarks>
    public async ValueTask<int> FindAliasedAsync(
        string name,
        DTexData source,
        MaterialFactsCache materials,
        ICollection<CompileDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(materials);

        return await FindAliasedAsync(
            name, Strings.GetString(source.NameStringTableId), materials, diagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="FindAliasedAsync(string, DTexData, MaterialFactsCache, ICollection{CompileDiagnostic}?, CancellationToken)"/>
    /// with the describing material already resolved to what
    /// <c>FindOriginalMaterial</c> reads (<c>textures.cpp:431</c>): for a
    /// patched source, the material the patch was made from
    /// (<see cref="MaterialPatch.MaterialPatcher.OriginalNameFor"/>).
    /// </summary>
    internal async ValueTask<int> FindAliasedAsync(
        string name,
        string sourceMaterial,
        MaterialFactsCache materials,
        ICollection<CompileDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1308 // strlwr is what textures.cpp:406 calls; this is a byte-for-byte port of it.
        string lowered = name.ToLowerInvariant();
#pragma warning restore CA1308

        for (int i = 0; i < _texData.Count; i++)
        {
            if (string.Equals(NameOf(i), lowered, StringComparison.Ordinal))
            {
                return i;
            }
        }

        int index = Append(lowered);

        MaterialFacts facts = await materials.GetAsync(sourceMaterial, cancellationToken)
            .ConfigureAwait(false);

        if (!facts.Found)
        {
            diagnostics?.Add(new CompileDiagnostic(
                TextureDiagnostics.MaterialNotFound,
                DiagnosticSeverity.Warning,
                $"material not found: \"{lowered}\""));
            return -1;
        }

        Fill(index, facts);
        return index;
    }

    /// <summary>
    /// Copies an entry under a new name: <c>AddCloneTexData</c>,
    /// <c>textures.cpp:518</c>.
    /// </summary>
    /// <param name="existingIndex">The entry to copy.</param>
    /// <param name="cloneName">The copy's name.</param>
    /// <returns>The copy's index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cloneName"/> is null.</exception>
    /// <exception cref="MapCompileException">The table is full.</exception>
    /// <remarks>
    /// Everything but the name is inherited: reflectivity, dimensions and the
    /// surface property. Stock has no bounds check here at all — it walks off
    /// the end of <c>dtexdata</c> rather than erroring — and that is the one
    /// thing not reproduced, because overrunning the array is not behaviour a
    /// port can be faithful to.
    /// </remarks>
    public int AddClone(int existingIndex, string cloneName) => AddCloneCore(existingIndex, cloneName);

    /// <summary>
    /// <see cref="FindOrCreateAsync"/> for a caller that has already read the
    /// material: the same append and fill, with no I/O, so a synchronous stage
    /// (the face stage's <c>FindOrCreateTexData</c>, <c>faces.cpp:1287</c>)
    /// can create the entry at exactly the point stock does.
    /// </summary>
    /// <param name="name">The material name.</param>
    /// <param name="facts">Its facts, read earlier.</param>
    /// <param name="diagnostics">Where a missing material is reported.</param>
    /// <returns>The entry's index.</returns>
    internal int FindOrCreateLoaded(string name, MaterialFacts facts, ICollection<CompileDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(facts);

        int existing = Find(name);
        if (existing >= 0)
        {
            return existing;
        }

        int index = Append(name);

        if (!facts.Found)
        {
            diagnostics?.Add(new CompileDiagnostic(
                TextureDiagnostics.MaterialNotFound,
                DiagnosticSeverity.Warning,
                $"material not found: \"{name}\""));
            return index;
        }

        Fill(index, facts);
        return index;
    }

    private int AddCloneCore(int existingIndex, string cloneName)
    {
        ArgumentNullException.ThrowIfNull(cloneName);

        DTexData clone = _texData[existingIndex];
        int surfaceProperty = _surfaceProperties[existingIndex];

        int index = Append(cloneName);

        int nameId = _texData[index].NameStringTableId;
        clone.NameStringTableId = nameId;
        _texData[index] = clone;
        _surfaceProperties[index] = surfaceProperty;

        return index;
    }

    private int Append(string name)
    {
        if (_texData.Count >= MaxMapTexData)
        {
            throw new MapCompileException(
                $"Too many unique texture mappings, max = {MaxMapTexData}");
        }

        _texData.Add(new DTexData { NameStringTableId = Strings.AddOrFind(name) });
        _surfaceProperties.Add(0);
        return _texData.Count - 1;
    }

    private void Fill(int index, MaterialFacts facts)
    {
        DTexData entry = _texData[index];
        entry.Width = facts.Width;
        entry.Height = facts.Height;
        entry.ViewWidth = facts.Width;
        entry.ViewHeight = facts.Height;
        entry.Reflectivity = facts.Reflectivity;
        _texData[index] = entry;

        // GetSurfaceProperties (textures.cpp:344): $surfaceprop against the
        // table LoadSurfaceProperties built before the map was loaded
        // (vbsp.cpp:1310); -1 without one, as stock's physprops == NULL.
        _surfaceProperties[index] = PropertyTable?.ResolveMaterial(facts.SurfaceProp) ?? -1;
    }
}

/// <summary>Diagnostic codes the texture tables raise.</summary>
public static class TextureDiagnostics
{
    /// <summary>
    /// A material named by the map did not resolve:
    /// <c>WARNING: material not found</c>, <c>textures.cpp:496</c>.
    /// </summary>
    public const string MaterialNotFound = "VBSP0301";

    /// <summary>
    /// A side's <c>lightmapscale</c> was zero: <c>map.cpp:2884</c>.
    /// </summary>
    public const string LuxelSizeZero = "VBSP0302";
}
