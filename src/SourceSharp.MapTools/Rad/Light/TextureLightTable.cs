using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>LightForTexture</c> (<c>vrad.cpp:300</c>): the emissive colour of a
/// material, with vbsp's cubemap patch names unwound first.
/// </summary>
/// <remarks>
/// <para>
/// The table itself is <see cref="RadLightFile"/>, which Phase 4a already
/// ports; the only thing added here is the NAME REWRITE, and it is the part
/// that would silently do nothing if it were left out.
/// </para>
/// <para>
/// vbsp rewrites the material of every face a cubemap touches to
/// <c>maps/&lt;levelname&gt;/&lt;original&gt;_&lt;x&gt;_&lt;y&gt;_&lt;z&gt;</c>, a
/// per-cubemap patch material baked into the pakfile. Without the rewrite,
/// <b>a texlight stops emitting the moment a cubemap is placed near it</b>:
/// the patched name is not in <c>lights.rad</c> and the lookup returns black.
/// So this is the difference between a map's lights working and not, and it is
/// invisible on any test map without cubemaps -- which is every map in this
/// project's catalogue.
/// </para>
/// <para>
/// The unwind is three <c>strrchr('_')</c> truncations
/// (<c>vrad.cpp:322-333</c>), applied only if all three found a separator. A
/// material whose own name contains underscores is therefore safe: the three
/// stripped are the three vbsp appended.
/// </para>
/// </remarks>
public sealed class TextureLightTable
{
    private readonly RadLightFile _file;
    private readonly string _levelName;

    /// <summary>Binds a texlight table to a map.</summary>
    /// <param name="file">The merged <c>lights.rad</c> content.</param>
    /// <param name="levelName">
    /// <c>level_name</c>: the map's filename without path or extension. The
    /// rewrite is keyed on it, so passing the wrong one silently disables the
    /// rewrite rather than misfiring.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public TextureLightTable(RadLightFile file, string levelName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(levelName);

        _file = file;
        _levelName = levelName;
    }

    /// <summary>A table with no texlights in it, for a map that has none.</summary>
    public static TextureLightTable Empty { get; } = new(new RadLightFile(), string.Empty);

    /// <summary>
    /// The emissive colour of a material.
    /// </summary>
    /// <param name="name">The material name as the texdata string table holds it.</param>
    /// <returns>The colour, or zero when the material is not a texlight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public Vec3 Lookup(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _file.Lookup(Unpatch(name)) ?? Vec3.Zero;
    }

    /// <summary>
    /// Undoes vbsp's cubemap patch renaming, if this name carries it.
    /// </summary>
    /// <param name="name">The material name.</param>
    /// <returns>The original name, or <paramref name="name"/> unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <remarks>
    /// Exposed because it is the only interesting half and it is testable
    /// without a <c>lights.rad</c>.
    /// </remarks>
    public string Unpatch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // vrad.cpp:308. Q_strncmp, case-SENSITIVE, unlike the table lookup it
        // feeds.
        if (!name.StartsWith("maps/", StringComparison.Ordinal))
        {
            return name;
        }

        // :311. Also case-sensitive, and a PREFIX test rather than an equality
        // one -- so a level called "de_dust" matches a material under
        // "maps/de_dust2/...". Reproduced; the three-underscore test below is
        // what actually rejects the mismatch.
        string rest = name[5..];
        if (!rest.StartsWith(_levelName, StringComparison.Ordinal))
        {
            return name;
        }

        string baseName = rest[_levelName.Length..];
        if (baseName.Length == 0 || baseName[0] != '/')
        {
            return name;
        }

        baseName = baseName[1..];

        // :322-333. Three truncations at the LAST underscore each time. Stock
        // tracks whether every one of them found a separator and keeps the
        // original name unless all three did.
        for (int i = 0; i < 3; i++)
        {
            int underscore = baseName.LastIndexOf('_');
            if (underscore < 0)
            {
                return name;
            }

            baseName = baseName[..underscore];
        }

        return baseName;
    }
}
