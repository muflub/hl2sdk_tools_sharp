using SourceSharp.MapFormats.Text;
using System.Buffers;
using System.Text;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// The surface-property database vbsp indexes materials into: a managed
/// port of <c>CPhysicsSurfaceProps</c> (the 2018 engine drop's
/// <c>vphysics/physics_material.cpp</c>, read as the consumer-side oracle),
/// loaded the way <c>LoadSurfaceProperties</c> (<c>textures.cpp:711</c>) loads it.
/// </summary>
/// <remarks>
/// <para>
/// Managed because the library's own copy is a process-wide singleton that
/// only grows and refuses to parse the same file name twice
/// (<c>AddFileToDatabase</c>, <c>physics_material.cpp:205</c>): two compiles
/// against two games in one process could not both be right through it. The
/// indices are output -- <c>g_SurfaceProperties</c> numbers go into the world
/// collide's per-triangle materials and the <c>materialtable</c> keydata -- so
/// this is asserted equal to the library's <c>IPhysicsSurfaceProps</c> by a
/// fact, property by property.
/// </para>
/// <para>
/// Behaviours reproduced, each pinned by a fact: names and values are
/// LOWER-CASED by the tokenizer (<c>ParseKeyvalue</c>,
/// <c>vcollide_parse.cpp:919</c>) and looked up case-insensitively; a property
/// seen again overrides the first one's data and keeps its index; a property
/// inherits from one of the same name if it exists, otherwise from
/// <c>default</c>, and <c>base</c> re-inherits; and after the FIRST file the
/// reserved <c>$MATERIAL_INDEX_SHADOW</c> property is appended
/// (<c>physics_material.cpp:593</c>), so every later file's properties come
/// after it.
/// </para>
/// </remarks>
public sealed class SurfacePropertyTable
{
    /// <summary><c>MATERIAL_INDEX_SHADOW</c>, <c>vphysics/physics_material.h:31</c>.</summary>
    public const int ShadowMaterialIndex = 0xF000;

    /// <summary>The reserved name for <see cref="ShadowMaterialIndex"/>.</summary>
    public const string ShadowMaterialName = "$MATERIAL_INDEX_SHADOW";

    /// <summary><c>MAX_KEYVALUE</c>, <c>vcollide_parse_private.h</c>.</summary>
    private const int MaxKeyValue = 1024;

    private readonly List<Entry> _props = [];
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialised;
    private int _shadowFallback;

    /// <summary>How many properties: <c>SurfacePropCount</c>.</summary>
    public int Count => _props.Count;

    /// <summary>
    /// Loads <c>scripts/surfaceproperties_manifest.txt</c> and every file it
    /// names, in order: <c>LoadSurfaceProperties</c>, <c>textures.cpp:711</c>.
    /// </summary>
    /// <param name="content">The game content.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The table; empty when there is no manifest, as stock's is.</returns>
    public static async Task<SurfacePropertyTable> LoadAsync(
        IContentFileSystem content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        SurfacePropertyTable table = new();
        string? manifestText = await ReadTextAsync(
            content, SurfacePropertiesManifest.ManifestPath, cancellationToken).ConfigureAwait(false);

        if (manifestText is null)
        {
            return table;
        }

        SurfacePropertiesManifest manifest =
            await SurfacePropertiesManifest.ParseAsync(manifestText, cancellationToken).ConfigureAwait(false);

        foreach (string file in manifest.Files)
        {
            // LoadSurfacePropFile, textures.cpp:688: a missing file is skipped.
            string? text = await ReadTextAsync(content, file, cancellationToken).ConfigureAwait(false);
            if (text is not null)
            {
                table.ParseSurfaceData(file, text);
            }
        }

        return table;
    }

    /// <summary>
    /// <c>ParseSurfaceData</c>, <c>physics_material.cpp:402</c>.
    /// </summary>
    /// <param name="fileName">The file's name; a name parsed before is ignored.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The count after parsing, or 0 when the file was a repeat.</returns>
    public int ParseSurfaceData(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        if (!_files.Add(fileName))
        {
            return 0;
        }

        byte[] bytes = Encoding.Latin1.GetBytes(text);
        int? cursor = 0;

        do
        {
            cursor = ParseKeyvalue(bytes, cursor, out string key, out string value);
            if (value == "{")
            {
                Entry prop = new(key);
                int baseMaterial = GetSurfaceIndex(key);
                if (baseMaterial < 0)
                {
                    baseMaterial = GetSurfaceIndex("default");
                }

                CopyPhysicsProperties(prop, baseMaterial);

                do
                {
                    cursor = ParseKeyvalue(bytes, cursor, out key, out value);
                    if (key == "}")
                    {
                        // "already in the database, don't add again, override values instead"
                        int propIndex = GetSurfaceIndex(prop.Name);
                        if (propIndex >= 0)
                        {
                            Entry existing = Internal(propIndex)!;
                            existing.Physics = prop.Physics;
                            break;
                        }

                        Add(prop);
                        break;
                    }

                    ApplyKey(prop, key, value);
                }
                while (cursor is not null);
            }
        }
        while (cursor is not null);

        if (!_initialised)
        {
            _initialised = true;
            Entry shadow = new(ShadowMaterialName);
            CopyPhysicsProperties(shadow, GetSurfaceIndex("default"));
            shadow.Physics = shadow.Physics with { Elasticity = 1e-3f, Friction = 0.8f };
            _shadowFallback = _props.Count;
            Add(shadow);
        }

        return _props.Count;
    }

    /// <summary><c>GetSurfaceIndex</c>, <c>physics_material.cpp:219</c>.</summary>
    /// <param name="name">The property name, any case.</param>
    /// <returns>Its index, or -1.</returns>
    public int GetSurfaceIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length > 0 && name[0] == '$'
            && string.Equals(name, ShadowMaterialName, StringComparison.OrdinalIgnoreCase))
        {
            return ShadowMaterialIndex;
        }

        return _byName.TryGetValue(name, out int index) ? index : -1;
    }

    /// <summary><c>GetPropName</c>, <c>physics_material.cpp:244</c>.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The name as stored (lower case), or null.</returns>
    public string? GetPropName(int index) => Internal(index)?.Name;

    /// <summary>
    /// <c>GetPhysicsProperties</c>, <c>physics_material.cpp:276</c>: an
    /// unknown index answers with <c>default</c>'s values.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns>The four numbers, or zeros when not even <c>default</c> exists.</returns>
    public SurfacePhysics GetPhysicsProperties(int index)
    {
        Entry? entry = Internal(index) ?? Internal(GetSurfaceIndex("default"));
        return entry?.Physics ?? default;
    }

    /// <summary>
    /// <c>GetSurfaceProperties</c>, <c>textures.cpp:344</c>: a material's
    /// <c>$surfaceprop</c> to an index, <c>default</c> when it names nothing,
    /// -1 when the material has none.
    /// </summary>
    /// <param name="surfaceProp">The material's <c>$surfaceprop</c>, or null.</param>
    /// <returns>The index <c>g_SurfaceProperties</c> stores.</returns>
    public int ResolveMaterial(string? surfaceProp)
    {
        if (surfaceProp is null)
        {
            return -1;
        }

        int index = GetSurfaceIndex(surfaceProp);
        return index >= 0 ? index : GetSurfaceIndex("default");
    }

    /// <summary>
    /// <c>GetSurfaceProperties2</c>, <c>textures.cpp:368</c>: as
    /// <see cref="ResolveMaterial"/> for <c>$surfaceprop2</c>, except that a
    /// material without one answers -1 ("No surface property 2") only when a
    /// table is loaded -- with no table stock returns its initial -1 as well.
    /// </summary>
    /// <param name="surfaceProp2">The material's <c>$surfaceprop2</c>, or null.</param>
    /// <returns>The index, or -1.</returns>
    public int ResolveMaterial2(string? surfaceProp2) => ResolveMaterial(surfaceProp2);

    private void Add(Entry entry)
    {
        _byName.TryAdd(entry.Name, _props.Count);
        _props.Add(entry);
    }

    private Entry? Internal(int index)
    {
        // GetInternalSurface, physics_material.cpp:258: reserved indices fall back.
        if (index == ShadowMaterialIndex)
        {
            index = _shadowFallback;
        }

        return index < 0 || index > _props.Count - 1 ? null : _props[index];
    }

    private void CopyPhysicsProperties(Entry target, int baseIndex)
    {
        Entry? source = Internal(baseIndex);
        if (source is not null)
        {
            target.Physics = source.Physics;
        }
    }

    private void ApplyKey(Entry prop, string key, string value)
    {
        switch (key)
        {
            case "base":
                CopyPhysicsProperties(prop, GetSurfaceIndex(value));
                break;
            case "thickness":
                prop.Physics = prop.Physics with { Thickness = (float)CText.Atof(value) };
                break;
            case "density":
                prop.Physics = prop.Physics with { Density = (float)CText.Atof(value) };
                break;
            case "elasticity":
                prop.Physics = prop.Physics with { Elasticity = (float)CText.Atof(value) };
                break;
            case "friction":
                prop.Physics = prop.Physics with { Friction = (float)CText.Atof(value) };
                break;
            default:
                // Audio, game and sound keys do not reach the compile.
                break;
        }
    }

    /// <summary>
    /// <c>ParseKeyvalue</c> (<c>vcollide_parse.cpp:919</c>) over
    /// <c>ParseFile</c> (<c>public/filesystem_helpers.cpp:29</c>).
    /// </summary>
    internal static int? ParseKeyvalue(byte[] text, int? cursor, out string key, out string value)
    {
        value = string.Empty;
        cursor = ParseFile(text, cursor, out key);

        if (key == "}")
        {
            return cursor;
        }

        key = key.ToLowerInvariant();
        cursor = ParseFile(text, cursor, out value);
        value = value.ToLowerInvariant();
        return cursor;
    }

    /// <summary>
    /// <c>ParseFileInternal</c>: one token, or null at the end. The break set
    /// is <c>{}()':</c> (colons included, <c>com_ignorecolons</c> is false),
    /// and a byte at or below a space counts as whitespace in the SIGNED
    /// <c>char</c> comparison, which makes every byte from 0x80 up whitespace
    /// too.
    /// </summary>
    internal static int? ParseFile(byte[] text, int? cursor, out string token)
    {
        token = string.Empty;
        if (cursor is not int p)
        {
            return null;
        }

        StringBuilder builder = new();

    skipwhite:
        while (true)
        {
            int c = At(text, p);
            if (c > ' ')
            {
                break;
            }

            if (c == 0)
            {
                return null;
            }

            p++;
        }

        if (At(text, p) == '/' && At(text, p + 1) == '/')
        {
            while (At(text, p) != 0 && At(text, p) != '\n')
            {
                p++;
            }

            goto skipwhite;
        }

        if (At(text, p) == '/' && At(text, p + 1) == '*')
        {
            p += 2;
            while (At(text, p) != 0)
            {
                if (At(text, p) == '*' && At(text, p + 1) == '/')
                {
                    p += 2;
                    break;
                }

                p++;
            }

            goto skipwhite;
        }

        int ch = At(text, p);

        if (ch == '"')
        {
            p++;
            while (true)
            {
                ch = At(text, p++);
                if (ch == '"' || ch == 0)
                {
                    token = builder.ToString();
                    return ch == 0 ? p : p; // stock returns past the terminator either way
                }

                builder.Append((char)(byte)ch);
                if (builder.Length == MaxKeyValue - 1)
                {
                    token = builder.ToString();
                    return p;
                }
            }
        }

        if (IsBreak(ch))
        {
            token = ((char)ch).ToString();
            return p + 1;
        }

        do
        {
            builder.Append((char)(byte)ch);
            p++;
            if (builder.Length == MaxKeyValue - 1)
            {
                token = builder.ToString();
                return p;
            }

            ch = At(text, p);
            if (IsBreak(ch))
            {
                break;
            }
        }
        while (ch > 32);

        token = builder.ToString();
        return p;
    }

    private static bool IsBreak(int c) => c is '{' or '}' or '(' or ')' or '\'' or ':';

    /// <summary>The byte at an index as a SIGNED char, NUL past the end.</summary>
    private static int At(byte[] text, int index) => index < text.Length ? (sbyte)text[index] : 0;

    private static async Task<string?> ReadTextAsync(
        IContentFileSystem content, string path, CancellationToken cancellationToken)
    {
        using IMemoryOwner<byte>? owner =
            await content.ReadAsync(VPath.Create(path), cancellationToken).ConfigureAwait(false);
        return owner is null ? null : Encoding.Latin1.GetString(owner.Memory.Span);
    }

    private sealed class Entry
    {
        public Entry(string name) => Name = name;

        public string Name { get; }

        public SurfacePhysics Physics { get; set; }
    }
}
