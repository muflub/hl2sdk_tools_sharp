using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>FindOriginalMaterial</c> with its memo: one read per distinct material
/// for the whole compile.
/// </summary>
/// <remarks>
/// <para>
/// Stock's material system caches inside <c>materialsystem.dll</c>, so
/// <c>FindMiptex</c>, <c>FindOrCreateTexData</c> and
/// <c>GetShaderNameForTexInfo</c> each ask for the same material by name and
/// each get the same answer without re-reading the VMT. This is that cache,
/// made explicit because the plan's rule is that no stage may reach a global.
/// </para>
/// <para>
/// Keyed case-insensitively through <see cref="MaterialFactsReader.Normalize"/>,
/// which is what makes <c>TOOLS/TOOLSNODRAW</c> and <c>tools/toolsnodraw</c>
/// one read. That is NOT the same as saying the two names are one texdata:
/// <see cref="TextureReferenceTable"/>'s dedup is case-SENSITIVE and
/// <see cref="TexDataTable"/>'s is not, and both are reproduced separately.
/// </para>
/// <para>
/// Not thread-safe, and deliberately so: the map load is single-threaded in
/// stock and its ordering is the output, so a cache that invited concurrent use
/// would invite a reordering.
/// </para>
/// </remarks>
public sealed class MaterialFactsCache
{
    private readonly IContentFileSystem _content;
    private readonly MaterialFactsOptions _options;
    private readonly Dictionary<string, MaterialFacts> _facts = [];

    /// <summary>Creates a cache over a content filesystem.</summary>
    /// <param name="content">Where materials are read from.</param>
    /// <param name="options">The material-system configuration, or null for the default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public MaterialFactsCache(IContentFileSystem content, MaterialFactsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _options = options ?? MaterialFactsOptions.Default;
    }

    /// <summary>How many distinct materials have been read.</summary>
    public int Count => _facts.Count;

    /// <summary>Reads a material's facts, or returns the memoised answer.</summary>
    /// <param name="materialName">The material, as the map spells it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The facts. A material that does not resolve comes back with
    /// <see cref="MaterialFacts.Found"/> false, which is
    /// <c>FindOriginalMaterial</c>'s <c>MATERIAL_NOT_FOUND</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialName"/> is null.</exception>
    public async ValueTask<MaterialFacts> GetAsync(
        string materialName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialName);

        string key = MaterialFactsReader.Normalize(materialName);

        if (_facts.TryGetValue(key, out MaterialFacts? cached))
        {
            return cached;
        }

        MaterialFacts facts = await MaterialFactsReader
            .ReadAsync(key, _content, _options, cancellationToken)
            .ConfigureAwait(false);

        _facts[key] = facts;
        return facts;
    }
}
