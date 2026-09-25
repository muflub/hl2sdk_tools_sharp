using System.Globalization;
using System.Text;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>lights.rad</c> texlight file: a port of <c>ReadLightFile</c>
/// (<c>src/utils/vrad/vrad.cpp:190-292</c>) and the colour conversion in
/// <c>LightForString</c> (<c>src/utils/vrad/lightmap.cpp:1056-1118</c>).
/// </summary>
/// <remarks>
/// <para>
/// A line-oriented format, NOT KeyValues and NOT scriplib. It has no comment
/// syntax at all -- nothing in <c>vrad.cpp:203-289</c> strips <c>//</c>,
/// <c>#</c> or <c>;</c> -- so a line beginning <c>// a note</c> is parsed as a
/// texlight for a material named <c>//</c>.
/// </para>
/// <para>
/// vrad reads up to three of these in order: the global <c>lights.rad</c>, a
/// <c>-lights</c> file from the command line, and <c>&lt;mapname&gt;.rad</c>
/// (<c>vrad.cpp:2185-2187</c>). Later files OVERRIDE earlier ones, in place --
/// see <see cref="Merge"/>.
/// </para>
/// </remarks>
public sealed class RadLightFile
{
    /// <summary>
    /// <c>MAX_TEXLIGHTS</c> (<c>src/utils/vrad/vrad.cpp:180</c>).
    /// </summary>
    /// <remarks>
    /// The check is <c>num_texlights == MAX_TEXLIGHTS</c>
    /// (<c>vrad.cpp:243</c>) and it runs BEFORE the duplicate lookup, so at 128
    /// entries even a line that would merely override an existing texlight
    /// aborts the compile.
    /// </remarks>
    public const int MaxTexLights = 128;

    /// <summary>
    /// The buffer <c>CmdLib_FGets</c> reads a line into
    /// (<c>src/utils/vrad/vrad.cpp:192</c>).
    /// </summary>
    /// <remarks>
    /// A line longer than this is silently SPLIT into several
    /// (<c>src/utils/common/cmdlib.cpp:100-129</c>), each parsed on its own.
    /// </remarks>
    public const int LineBufferSize = 1024;

    /// <summary>The texlights, in the order they were defined.</summary>
    public IList<TexLight> TexLights { get; } = [];

    /// <summary>
    /// The materials named by <c>noshadow</c> lines, with any extension
    /// stripped.
    /// </summary>
    /// <remarks>
    /// <c>vrad.cpp:226-233</c>. The name is truncated at its FIRST <c>.</c>
    /// (<c>:228-230</c>), so <c>noshadow glass/window01.vmt</c> records
    /// <c>glass/window01</c> -- and <c>noshadow a.b/c</c> records just
    /// <c>a</c>. It is later used as a SUBSTRING match
    /// (<c>src/utils/vrad/vradstaticprops.cpp:1906-1909</c>).
    /// </remarks>
    public IList<string> NonShadowCastingMaterials { get; } = [];

    /// <summary>The models named by <c>forcetextureshadow</c> lines.</summary>
    /// <remarks><c>vrad.cpp:234-238</c>.</remarks>
    public IList<string> ForcedTextureShadowModels { get; } = [];

    /// <summary>
    /// The lines that were neither a directive nor a usable texlight.
    /// </summary>
    /// <remarks>
    /// <c>vrad.cpp:248-253</c> prints "ignoring bad texlight '%s' in %s" for a
    /// line longer than four characters and says NOTHING at all for a shorter
    /// one -- so a four-character typo vanishes without a word. Recorded here
    /// so a caller can report both.
    /// </remarks>
    public IList<string> IgnoredLines { get; } = [];

    /// <summary>Reads a texlight file from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async Task<RadLightFile> ReadAsync(
        Stream stream,
        RadLightOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await ParseAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a texlight file already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed file.</returns>
    public static ValueTask<RadLightFile> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        RadLightOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ParseAsync(Encoding.Latin1.GetString(bytes.Span), options, cancellationToken);

    /// <summary>Parses a texlight file from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="RadLightFileException">
    /// The file defines more than <see cref="MaxTexLights"/> texlights.
    /// </exception>
    public static ValueTask<RadLightFile> ParseAsync(
        string text,
        RadLightOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return ValueTask.FromResult(Parse(text, options ?? RadLightOptions.Default, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromCanceled<RadLightFile>(cancellationToken);
        }
    }

    /// <summary>
    /// The intensity vrad uses for a named material, or null.
    /// </summary>
    /// <param name="materialName">The material name.</param>
    /// <returns>The intensity, or null when the material has no texlight.</returns>
    /// <remarks>
    /// <c>LightForTexture</c> (<c>src/utils/vrad/vrad.cpp:343-350</c>) compares
    /// with <c>Q_strcasecmp</c> -- CASE-INSENSITIVELY -- even though the
    /// duplicate check during parsing uses <c>strcmp</c> and is case-SENSITIVE
    /// (<c>:260</c>). So <c>WOOD</c> and <c>wood</c> become two separate table
    /// entries with no warning, and the FIRST in table order wins every lookup.
    /// </remarks>
    public Vec3? Lookup(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);

        foreach (TexLight light in TexLights)
        {
            if (string.Equals(light.Name, materialName, StringComparison.OrdinalIgnoreCase))
            {
                return light.Intensity;
            }
        }

        return null;
    }

    /// <summary>
    /// Merges a later file over this one, as vrad's second and third
    /// <c>ReadLightFile</c> calls do.
    /// </summary>
    /// <param name="later">The file read afterwards.</param>
    /// <returns>What the merge found worth reporting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="later"/> is null.</exception>
    /// <remarks>
    /// <c>vrad.cpp:257-287</c>. A name already present is OVERWRITTEN IN PLACE
    /// at its existing index (<c>:282-284</c>), so the table does not grow and
    /// the later definition wins. The comparison is <c>strcmp</c>, case
    /// SENSITIVE (<c>:260</c>) -- which does not match the case-insensitive
    /// lookup, and that mismatch is the bug behind duplicate texlights that
    /// never warn.
    /// </remarks>
    public IReadOnlyList<RadLightOverride> Merge(RadLightFile later)
    {
        ArgumentNullException.ThrowIfNull(later);

        List<RadLightOverride> overrides = [];

        foreach (TexLight light in later.TexLights)
        {
            int index = -1;
            for (int i = 0; i < TexLights.Count; i++)
            {
                // strcmp, not stricmp: vrad.cpp:260.
                if (string.Equals(TexLights[i].Name, light.Name, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                TexLights.Add(light);
                continue;
            }

            TexLight existing = TexLights[index];

            overrides.Add(new RadLightOverride(
                light.Name,
                existing.Intensity,
                light.Intensity,
                SameFile: string.Equals(existing.SourceFile, light.SourceFile, StringComparison.Ordinal),
                Redundant: existing.Intensity == light.Intensity));

            TexLights[index] = light;
        }

        foreach (string material in later.NonShadowCastingMaterials)
        {
            NonShadowCastingMaterials.Add(material);
        }

        foreach (string model in later.ForcedTextureShadowModels)
        {
            ForcedTextureShadowModels.Add(model);
        }

        return overrides;
    }

    private static RadLightFile Parse(
        string text,
        RadLightOptions options,
        CancellationToken cancellationToken)
    {
        RadLightFile file = new();

        foreach (string rawLine in SplitLines(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string line = rawLine;
            int scan = 0;

            // vrad.cpp:206-222. The prefixes are tested BEFORE whitespace is
            // skipped, so they must start at column 0, and they are two
            // sequential ifs rather than an if/else -- "hdr:ldr:x" strips both.
            if (StartsWithIgnoreCase(line, scan, "hdr:"))
            {
                scan += 4;
                if (!options.Hdr)
                {
                    continue;
                }
            }

            if (StartsWithIgnoreCase(line, scan, "ldr:"))
            {
                scan += 4;
                if (options.Hdr)
                {
                    continue;
                }
            }

            // vrad.cpp:224 -- strspn over " \t" only. A '\r' left behind by
            // CmdLib_FGets on a CRLF file is NOT skipped here.
            while (scan < line.Length && (line[scan] == ' ' || line[scan] == '\t'))
            {
                scan++;
            }

            string body = line[scan..];

            // vrad.cpp:226-233. sscanf matches "noshadow " case-SENSITIVELY and
            // requires whitespace after it.
            if (TryReadDirective(body, "noshadow", out string? shadowName))
            {
                int dot = shadowName.IndexOf('.', StringComparison.Ordinal);
                file.NonShadowCastingMaterials.Add(dot >= 0 ? shadowName[..dot] : shadowName);
                continue;
            }

            // vrad.cpp:234-238.
            if (TryReadDirective(body, "forcetextureshadow", out string? modelName))
            {
                file.ForcedTextureShadowModels.Add(modelName);
                continue;
            }

            // vrad.cpp:246 -- one %s for the material name.
            string name = ReadWord(body);
            if (name.Length == 0)
            {
                // :248-253 -- a line of four characters or fewer is dropped in
                // SILENCE; anything longer gets a message.
                if (body.Length > 4)
                {
                    file.IgnoredLines.Add(body);
                }

                continue;
            }

            if (file.TexLights.Count == MaxTexLights)
            {
                // vrad.cpp:243-244, and note this fires BEFORE the duplicate
                // lookup, so an override at the limit aborts too.
                throw new RadLightFileException(
                    $"Too many texlights, max = {MaxTexLights}");
            }

            // vrad.cpp:255 -- LightForString(scan + strlen(name) + 1). The "+1"
            // skips exactly one separator byte, and the RETURN VALUE IS
            // IGNORED: a light that failed to parse is stored anyway.
            int valueStart = body.IndexOf(name, StringComparison.Ordinal) + name.Length;
            string values = valueStart < body.Length ? body[(valueStart + 1)..] : string.Empty;

            (Vec3 intensity, bool parsed) = ParseIntensity(values, options);

            file.TexLights.Add(new TexLight(name, intensity, options.SourceFile, parsed));
        }

        return file;
    }

    /// <summary>
    /// <c>LightForString</c>
    /// (<c>src/utils/vrad/lightmap.cpp:1056-1118</c>).
    /// </summary>
    /// <param name="text">The part of the line after the material name.</param>
    /// <param name="options">The HDR mode and the global light scale.</param>
    /// <returns>
    /// The intensity, and whether the C++ would have returned true.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> or <paramref name="options"/> is null.
    /// </exception>
    public static (Vec3 Intensity, bool Parsed) ParseIntensity(
        string text,
        RadLightOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        // lightmap.cpp:1066-1067 -- up to EIGHT fields, all scanned as double.
        double[] fields = ScanDoubles(text, 8);
        int count = fields.Length;

        double r = count > 0 ? fields[0] : 0;
        double g = count > 1 ? fields[1] : 0;
        double b = count > 2 ? fields[2] : 0;
        double scaler = count > 3 ? fields[3] : 0;

        // :1069-1079 -- exactly eight fields means two 4-tuples, LDR then HDR.
        // Five, six or seven fall through to the error branch.
        if (count == 8)
        {
            if (options.Hdr)
            {
                r = fields[4];
                g = fields[5];
                b = fields[6];
                scaler = fields[7];
            }

            count = 4;
        }

        // :1082-1086 -- any negative component makes the whole light black.
        if (r < 0.0 || g < 0.0 || b < 0.0 || scaler < 0.0)
        {
            return (Vec3.Zero, false);
        }

        // :1088 -- gamma to linear, exponent 2.2, and computed BEFORE the
        // switch so it is set even on the error path.
        float x = (float)(Math.Pow(r / 255.0, 2.2) * 255);

        switch (count)
        {
            case 1:
                // :1092-1095 -- greyscale.
                return (Scale(new Vec3(x, x, x), options.LightScale), true);

            case 3:
            case 4:
            {
                // :1100-1101.
                float y = (float)(Math.Pow(g / 255.0, 2.2) * 255);
                float z = (float)(Math.Pow(b / 255.0, 2.2) * 255);
                Vec3 intensity = new(x, y, z);

                if (count == 4)
                {
                    // :1104-1108 -- the fourth field is a brightness
                    // multiplier NORMALISED BY 255, not a plain factor.
                    intensity *= (float)(scaler / 255.0);
                }

                return (Scale(intensity, options.LightScale), true);
            }

            default:
                // :1111-1113 -- 0, 2, 5, 6 and 7 fields all land here. Note
                // that intensity[0] was already written at :1088 and the
                // caller stores the result regardless.
                return (Scale(new Vec3(x, 0, 0), options.LightScale), false);
        }
    }

    private static Vec3 Scale(Vec3 intensity, float lightScale) =>
        // lightmap.cpp:1115-1116 -- the global -scale, applied last to every
        // texlight.
        intensity * lightScale;

    private static IEnumerable<string> SplitLines(string text)
    {
        // CmdLib_FGets (src/utils/common/cmdlib.cpp:100-129) reads byte by
        // byte and breaks on '\n', then overwrites it -- so the LF is stripped
        // and a '\r' from a CRLF file is NOT. Reproduced, because a directive's
        // trailing name would otherwise differ between a CRLF and an LF file.
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            yield return text[start..i];
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static bool StartsWithIgnoreCase(string text, int offset, string prefix) =>
        text.Length - offset >= prefix.Length &&
        string.Equals(text.Substring(offset, prefix.Length), prefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadDirective(string body, string keyword, out string argument)
    {
        argument = string.Empty;

        // sscanf(scan, "<keyword> %s", ...) -- the literal is matched
        // case-sensitively and needs at least one whitespace character after
        // it before %s can take a word.
        if (!body.StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        int index = keyword.Length;
        if (index >= body.Length || !char.IsWhiteSpace(body[index]))
        {
            return false;
        }

        argument = ReadWord(body[index..]);
        return argument.Length > 0;
    }

    private static string ReadWord(string text)
    {
        int index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        int start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return text[start..index];
    }

    private static double[] ScanDoubles(string text, int maximum)
    {
        List<double> values = [];
        int index = 0;

        while (values.Count < maximum)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            int start = index;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                index++;
            }

            int digits = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
                digits++;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                {
                    index++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                break;
            }

            values.Add(double.Parse(
                text[start..index], NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return [.. values];
    }
}
