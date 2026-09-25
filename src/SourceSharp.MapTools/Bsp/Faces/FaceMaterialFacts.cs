using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// The two questions the face stage asks the material system.
/// </summary>
/// <remarks>
/// Both are asked mid-recursion, so both must answer synchronously. The
/// implementation reads its VMTs before the stage starts; this interface is
/// what the stage sees.
/// </remarks>
public interface IFaceMaterialResolver
{
    /// <summary>
    /// The texinfo to give the underside of a water surface
    /// (<c>AssignBottomWaterMaterialToFace</c>).
    /// </summary>
    /// <param name="texInfo">The top surface's texinfo.</param>
    /// <param name="bottomTexInfo">The underside's texinfo, when there is one.</param>
    /// <param name="warn">
    /// Whether stock would print its "doesn't have a $bottommaterial" warning,
    /// which it suppresses for nodraw and toolsskip.
    /// </param>
    /// <returns>False when the material has no <c>$bottommaterial</c>, which discards the face.</returns>
    bool TryGetBottomTexInfo(int texInfo, out int bottomTexInfo, out bool warn);

    /// <summary>
    /// A material's <c>$subdivsize</c>
    /// (<c>SubdivideFaceBySubdivSize</c>).
    /// </summary>
    /// <param name="texInfo">The face's texinfo.</param>
    /// <returns>The size, or zero when the material has none or was not found.</returns>
    float SubdivSize(int texInfo);
}

/// <summary>
/// <see cref="IFaceMaterialResolver"/> over a compile's own texture tables and
/// a pre-read material cache.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PrepareAsync"/> reads every material named by the texdata table
/// once, up front, so that the stage itself does no IO. That is exactly what
/// stock gets for free: its <c>FindOriginalMaterial</c> hits the material
/// system's cache, which the map load already populated.
/// </para>
/// <para>
/// <b>The texinfo it creates is created lazily and that matters.</b> Stock
/// calls <c>FindOrCreateTexData</c> and <c>FindOrCreateTexInfo</c> from inside
/// <c>FaceFromPortal</c>, so a bottom material's texinfo lands in the table at
/// the point the first face needing it is built. Precomputing the whole
/// mapping would put the same entries in a different order, and the order IS
/// the lump.
/// </para>
/// </remarks>
public sealed class FaceMaterialFacts : IFaceMaterialResolver
{
    private readonly VbspContext _compile;
    private readonly Dictionary<string, MaterialFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _bottomNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _bottomTexInfo = [];

    private FaceMaterialFacts(VbspContext compile) => _compile = compile;

    /// <summary>
    /// Reads every material the texdata table names, then returns a resolver
    /// over them.
    /// </summary>
    /// <param name="compile">The compile whose tables are read and extended.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compile"/> is null.</exception>
    /// <remarks>
    /// Call it where stock's face stage would first ask: after the post-load
    /// fixups, so the cubemap patches are in the
    /// texdata table and the pak. The driver does
    /// (<see cref="Driver.Vbsp"/>).
    /// </remarks>
    public static async Task<FaceMaterialFacts> PrepareAsync(
        VbspContext compile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compile);

        FaceMaterialFacts resolver = Create(compile);
        await resolver.LoadAsync(cancellationToken).ConfigureAwait(false);
        return resolver;
    }

    /// <summary>
    /// A resolver with nothing read yet, for a caller that must hand it to the
    /// face stage before the materials can be read; <see cref="LoadAsync"/>
    /// fills it.
    /// </summary>
    internal static FaceMaterialFacts Create(VbspContext compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        return new FaceMaterialFacts(compile);
    }

    /// <summary>
    /// Reads what the face stage will ask about every texdata the table names
    /// now, and about each one's <c>$bottommaterial</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different readers, as in stock:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>$subdivsize</c> and a created texdata's size come from
    /// <c>FindOriginalMaterial</c>: the material a patch was made FROM
    /// </description></item>
    /// <item><description>
    /// <c>$bottommaterial</c> comes from <c>GetValueFromPatchedMaterial</c>:
    /// the PATCHED VMT, pak first, with its patch chain expanded
    /// A water the
    /// cubemap fixup patched names a patched bottom
    /// Which exists only in the pak, and which
    /// a read from disk cannot see: it found no facts, dropped the bottom face
    /// and its texinfo, and warned VBSP0320 for it (p3g integration item 2).
    /// </description></item>
    /// </list>
    /// </remarks>
    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        MaterialPatch.MaterialPatcher patcher = _compile.Patcher;

        for (int i = 0; i < _compile.TexDatas.Count; i++)
        {
            string name = _compile.TexDatas.NameOf(i);

            if (_facts.ContainsKey(name))
            {
                continue;
            }

            _facts[name] = await _compile.Materials
                .GetAsync(patcher.OriginalNameFor(name), cancellationToken)
                .ConfigureAwait(false);
            _bottomNames[name] = await patcher
                .GetValueFromPatchedMaterialAsync(name, BottomMaterialVar, cancellationToken)
                .ConfigureAwait(false);
        }

        // AssignBottomWaterMaterialToFace creates the bottom
        // material's texdata during MakeFaces -- often one the map never
        // references, so it is not among the above. Read those now, while the
        // compile is still in its async load phase, so that the face stage can
        // create the entry at the moment stock does without doing any I/O.
        List<string> bottoms = [];
        foreach (string? bottom in _bottomNames.Values)
        {
            if (!string.IsNullOrEmpty(bottom) && !_facts.ContainsKey(bottom))
            {
                bottoms.Add(bottom);
            }
        }

        foreach (string bottom in bottoms)
        {
            if (!_facts.ContainsKey(bottom))
            {
                _facts[bottom] = await _compile.Materials
                    .GetAsync(patcher.OriginalNameFor(bottom), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetBottomTexInfo(int texInfo, out int bottomTexInfo, out bool warn)
    {
        warn = false;

        if (_bottomTexInfo.TryGetValue(texInfo, out bottomTexInfo))
        {
            return true;
        }

        TexInfo source = _compile.TexInfos[texInfo];
        string materialName = _compile.TexDatas.NameOf(source.TexData);

        string? bottomName = _bottomNames.TryGetValue(materialName, out string? read) ? read : null;

        if (string.IsNullOrEmpty(bottomName))
        {
            // Stock stays quiet about nodraw and toolsskip, which are normal
            // things to build a water brush out of.
            warn = !materialName.Contains("nodraw", StringComparison.OrdinalIgnoreCase)
                && !materialName.Contains("toolsskip", StringComparison.OrdinalIgnoreCase);

            bottomTexInfo = 0;
            return false;
        }

        // Stock copies flags and BOTH vector arrays and replaces only texdata;
        // it leaves the rest of the struct -- which in C is uninitialised stack
        // -- alone. Every field of TexInfo is one of the three it copies, so
        // there is nothing uninitialised here to reproduce.
        TexInfo bottom = new()
        {
            Flags = source.Flags,
            TextureVecsTexelsPerWorldUnits = source.TextureVecsTexelsPerWorldUnits,
            LightmapVecsLuxelsPerWorldUnits = source.LightmapVecsLuxelsPerWorldUnits,
            TexData = FindOrCreateTexData(bottomName!),
        };

        bottomTexInfo = _compile.TexInfos.FindOrCreate(bottom);
        _bottomTexInfo[texInfo] = bottomTexInfo;
        return true;
    }

    /// <inheritdoc/>
    public float SubdivSize(int texInfo)
    {
        TexInfo source = _compile.TexInfos[texInfo];
        string materialName = _compile.TexDatas.NameOf(source.TexData);

        if (!_facts.TryGetValue(materialName, out MaterialFacts? facts) || !facts.Found)
        {
            return 0f;
        }

        return facts.SubdivSize;
    }

    private const string BottomMaterialVar = "$bottommaterial";

    private int FindOrCreateTexData(string name)
    {
        int existing = _compile.TexDatas.Find(name);

        if (existing >= 0)
        {
            return existing;
        }

        // The bottom material was not one the map referenced, so it has no
        // texdata yet: create it now, from the facts PrepareAsync read for
        // exactly this(FindOrCreateTexData).
        if (_facts.TryGetValue(name, out MaterialFacts? facts))
        {
            return _compile.TexDatas.FindOrCreateLoaded(name, facts, _compile.Diagnostics);
        }

        // Only reachable for a material whose facts were never read -- a
        // resolver whose LoadAsync did not run.
        throw new InvalidOperationException(
            $"$bottommaterial '{name}' has no texdata: it is not referenced by the map, so "
            + "FaceMaterialFacts.PrepareAsync never read it. Add it to the texdata table before "
            + "the face stage runs.");
    }
}
