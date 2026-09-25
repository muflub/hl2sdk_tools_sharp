using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One material's compile-time classification: <c>textureref_t</c>,
/// </summary>
/// <param name="Name">The material name, as the map spelled it.</param>
/// <param name="Flags">The <c>SURF_*</c> bits the material contributes.</param>
/// <param name="Contents">The <c>CONTENTS_*</c> bits it contributes.</param>
/// <param name="LightmapWorldUnitsPerLuxel">
/// Always zero. <c>FindMiptex</c> initialises it and
/// nothing in the compiler ever assigns it again — the side's own
/// <c>lightmapscale</c> key is the only source of a non-zero value, and that
/// writes the side's <see cref="BrushTexture"/> rather than this. It is carried
/// because copies it, and copying a zero is the behaviour.
/// </param>
public readonly record struct TextureReference(
    string Name,
    int Flags,
    int Contents,
    float LightmapWorldUnitsPerLuxel);

/// <summary>
/// <c>textureref</c> and <c>nummiptex</c>
/// With <c>FindMiptex</c>.
/// </summary>
/// <remarks>
/// <para>
/// The per-material half of the texture work: given a material name, what flags
/// and contents does every side using it start from. The classification itself
/// is <see cref="MaterialSurfaceClassifier"/>, which is already gated against
/// stock; this type is the TABLE around it, and the table has behaviour of its
/// own that the classifier does not.
/// </para>
/// <para>
/// <b>The missing-material quirk.</b> When a material does not resolve,
/// <c>FindMiptex</c> returns 0 without advancing <c>nummiptex</c>
/// — having already written the name into the slot
/// it was about to commit. So the caller reads slot 0, which is some OTHER
/// material's flags and contents whenever the map has already loaded one, and
/// the half-written slot is silently overwritten by the next call. Both halves
/// of that are reproduced: <see cref="this[int]"/> does not range-check against
/// <see cref="Count"/>, and the scratch slot really is written.
/// </para>
/// </remarks>
public sealed class TextureReferenceTable
{
    /// <summary>
    /// The format's ceiling: <c>MAX_MAP_TEXTURES</c>,
    /// </summary>
    public const int MaxMapTextures = 1024;

    // Holds Count committed entries plus, always, one uncommitted scratch slot
    // at index Count -- the slot FindMiptex fills in before it knows whether it
    // will keep it.
    private readonly List<TextureReference> _slots = [default];

    /// <summary>How many materials are committed: stock's <c>nummiptex</c>.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether any material so far was water or slime: <c>g_bHasWater</c>,
    /// </summary>
    /// <remarks>
    /// Set by <c>%compileWater</c> AND by
 /// <c>%compileSlime</c> — slime sets the water flag, which is
    /// not a typo in the C++ and is not one here.
    /// </remarks>
    public bool HasWater { get; private set; }

    /// <summary>
    /// The entry at an index, committed or not.
    /// </summary>
    /// <param name="index">The miptex number.</param>
    /// <returns>The entry.</returns>
    /// <remarks>
    /// No check against <see cref="Count"/>, deliberately: a caller handed
    /// index 0 for a material that did not resolve must read slot 0 whatever is
    /// in it, which is what stock's raw array access does.
    /// </remarks>
    public TextureReference this[int index] => _slots[index];

    /// <summary>
    /// Classifies a material, memoised by name: <c>FindMiptex</c>,
    /// </summary>
    /// <param name="name">The material name, as the map spells it.</param>
    /// <param name="materials">Where the material's facts come from.</param>
    /// <param name="options">The compile's material switches.</param>
    /// <param name="diagnostics">Where a missing material is reported, or null.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>
    /// The index into this table — which is 0, and not a new entry, when the
    /// material did not resolve.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="materials"/> is null.
    /// </exception>
    /// <exception cref="MapCompileException">The table is full.</exception>
    /// <remarks>
    /// The existing-entry scan is a case-SENSITIVE <c>strcmp</c>
    /// So <c>TOOLS/TOOLSNODRAW</c> and
    /// <c>tools/toolsnodraw</c> get two entries here while
    /// <see cref="TexDataTable.Find"/>'s case-insensitive scan gives them one
    /// texdata. The two disagreeing is stock.
    /// </remarks>
    public async ValueTask<int> FindMiptexAsync(
        string name,
        MaterialFactsCache materials,
        MaterialCompileOptions? options = null,
        ICollection<CompileDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(materials);

        for (int i = 0; i < Count; i++)
        {
            if (string.Equals(_slots[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        if (Count == MaxMapTextures)
        {
            throw new MapCompileException(
                $"Too many unique textures, max {MaxMapTextures}");
        }

        // The slot is written BEFORE the material is
        // resolved, and stays written whether or not it is committed.
        _slots[Count] = new TextureReference(name, 0, 0, 0f);

        MaterialFacts facts = await materials.GetAsync(name, cancellationToken)
            .ConfigureAwait(false);

        if (!facts.Found)
        {
            // Nummiptex is NOT advanced, and the caller is
            // sent to slot 0.
            diagnostics?.Add(new CompileDiagnostic(
                TextureDiagnostics.MaterialNotFound,
                DiagnosticSeverity.Warning,
                $"Material not found!: {name}"));
            return 0;
        }

        MaterialSurface surface = MaterialSurfaceClassifier.Classify(facts, options);

        int index = Count;
        _slots[index] = new TextureReference(name, (int)surface.Flags, (int)surface.Contents, 0f);
        Count++;
        _slots.Add(default);

        // g_bHasWater is set inside the "rendered normally" branch only
 //So a material that is BOTH %compileWater
        // and, say, %compileSky never reaches it -- the sky branch wins and the
        // flag stays clear. Testing the resulting CONTENTS rather than the
        // compile vars is what reproduces that: CONTENTS_WATER and
        // CONTENTS_SLIME are only reachable from those two lines.
        if ((surface.Contents & (BrushContents.Water | BrushContents.Slime)) != 0)
        {
            HasWater = true;
        }

        return index;
    }
}
