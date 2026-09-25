using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>-replacematerials</c>: the substitution table from
/// <c>utils/vbsp/materialsub.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// Substitution happens at exactly two points, both while the VMF is being
/// read — a brush side's <c>material</c> key (<c>map.cpp:2847-2850</c>) and an
/// overlay's (<c>map.cpp:1325-1330</c>) — so everything downstream, including
/// <see cref="TextureReferenceTable"/> and <see cref="TexDataTable"/>, only
/// ever sees the replaced name. Nothing else is substituted: not displacement
/// materials, not entity keyvalues, not the skybox.
/// </para>
/// <para>
/// <b>The lookup order is backwards from what the file looks like.</b> Stock
/// chains the map-specific section as a FALLBACK of the <c>AllMaps</c> section
/// (<c>allMapKeys-&gt;ChainKeyValue(curMapKeys)</c>,
/// <c>materialsub.cpp:61</c>), so <c>AllMaps</c> is searched first and a
/// per-map entry only applies to a name <c>AllMaps</c> does not mention. That
/// is the opposite of the usual "more specific wins", and it is reproduced.
/// </para>
/// <para>
/// <b>And the keys are written with the platform's separator.</b> The lookup
/// runs the name through <c>Q_FixSlashes</c> first
/// (<c>materialsub.cpp:86</c>), which on the Windows tools turns a VMF's
/// <c>tools/toolsnodraw</c> into <c>tools\toolsnodraw</c> — so a
/// <c>materialsub.cfg</c> written for stock has backslashes in its keys. Both
/// spellings are accepted here, because a config written for the Windows tool
/// is the only kind that exists and refusing it would make the switch useless.
/// </para>
/// </remarks>
public sealed class MaterialReplacements
{
    /// <summary>
    /// The section that applies to every map: <c>materialsub.cpp:56</c>.
    /// </summary>
    public const string AllMapsSection = "AllMaps";

    /// <summary>
    /// Where the file lives, relative to the game directory:
    /// <c>materialsub.cpp:39-40</c>.
    /// </summary>
    /// <remarks>
    /// <c>cfg\materialsub.cfg</c>, NOT the <c>materialsub.txt in content\maps</c>
    /// that <c>vbsp.cpp:1259</c>'s usage text advertises. The usage text is
    /// wrong; the path the code opens is this one.
    /// </remarks>
    public const string ConfigPath = "cfg/materialsub.cfg";

    private readonly Dictionary<string, string> _allMaps =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _thisMap =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty table, which replaces nothing.</summary>
    public MaterialReplacements()
    {
    }

    /// <summary>
    /// Reads the table out of a parsed <c>materialsub.cfg</c>:
    /// <c>LoadMaterialReplacementKeys</c>, <c>materialsub.cpp:23</c>.
    /// </summary>
    /// <param name="document">The parsed config.</param>
    /// <param name="mapName">
    /// The map's base name, which names the map-specific section.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="mapName"/> is null.
    /// </exception>
    public MaterialReplacements(KeyValuesDocument document, string mapName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mapName);

        KeyValuesNode? root = document.Root;
        if (root is null)
        {
            return;
        }

        Fill(_allMaps, root.Find(AllMapsSection));
        Fill(_thisMap, root.Find(mapName));
    }

    /// <summary>How many substitutions the table holds, both sections.</summary>
    public int Count => _allMaps.Count + _thisMap.Count;

    /// <summary>
    /// The replacement for a material name, or the name itself:
    /// <c>ReplaceMaterialName</c>, <c>materialsub.cpp:79</c>.
    /// </summary>
    /// <param name="name">The material name as the map spells it.</param>
    /// <returns>The replacement, or <paramref name="name"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public string Replace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Both separators, because Q_FixSlashes makes the stock tool's keys
        // backslashed and a config written for it is the only kind there is.
        string forward = name.Replace('\\', '/');
        string backward = name.Replace('/', '\\');

        // AllMaps FIRST: the map-specific section is its chained fallback.
        if (_allMaps.TryGetValue(forward, out string? replacement) ||
            _allMaps.TryGetValue(backward, out replacement) ||
            _thisMap.TryGetValue(forward, out replacement) ||
            _thisMap.TryGetValue(backward, out replacement))
        {
            return replacement;
        }

        return name;
    }

    private static void Fill(Dictionary<string, string> into, KeyValuesNode? section)
    {
        if (section is null)
        {
            return;
        }

        foreach (KeyValuesNode child in section.Children)
        {
            if (child.Value is { } value)
            {
                into[child.Name] = value;
            }
        }
    }
}
