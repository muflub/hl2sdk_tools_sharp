//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>lights.rad</c> texlight file: read with the rules of the reference
/// reader <c>ReadLightFile</c>, including the colour conversion of the
/// reference <c>LightForString</c>.
/// </summary>
/// <remarks>
/// <para>
/// A line-oriented format, NOT KeyValues and NOT the reference tokenizer.
/// It has no comment syntax at all -- nothing strips <c>//</c>,
/// <c>#</c> or <c>;</c> -- so a line beginning <c>// a note</c> is parsed as a
/// texlight for a material named <c>//</c>.
/// </para>
/// <para>
/// The reference lightmapper reads up to three of these in order: the global
/// <c>lights.rad</c>, a <c>-lights</c> file from the command line, and
/// <c>&lt;mapname&gt;.rad</c>. Later files OVERRIDE earlier ones, in place --
/// see <see cref="Merge"/>.
/// </para>
/// </remarks>
public sealed class RadLightFile
{
    /// <summary>
    /// The format's <c>MAX_TEXLIGHTS</c> ceiling.
    /// </summary>
    /// <remarks>
    /// The reference check is <c>num_texlights == MAX_TEXLIGHTS</c>
    /// and it runs BEFORE the duplicate lookup, so at 128
    /// entries even a line that would merely override an existing texlight
    /// aborts the compile.
    /// </remarks>
    public const int MaxTexLights = 128;

    /// <summary>
    /// The buffer the reference reader's line read uses.
    /// </summary>
    /// <remarks>
    /// A line longer than this is silently SPLIT into several lines,
    /// each parsed on its own.
    /// </remarks>
    public const int LineBufferSize = 1024;

    /// <summary>The texlights, in the order they were defined.</summary>
    public IList<TexLight> TexLights { get; } = [];

    /// <summary>
    /// The materials named by <c>noshadow</c> lines, with any extension
    /// stripped.
    /// </summary>
    /// <remarks>
    /// The name is truncated at its FIRST <c>.</c>, so
    /// <c>noshadow glass/window01.vmt</c> records
    /// <c>glass/window01</c> -- and <c>noshadow a.b/c</c> records just
    /// <c>a</c>. It is later used as a SUBSTRING match by the reference
    /// lightmapper's shadow pass.
    /// </remarks>
    public IList<string> NonShadowCastingMaterials { get; } = [];

    /// <summary>The models named by <c>forcetextureshadow</c> lines.</summary>
    /// <remarks>Named by the reference reader's directive pass.</remarks>
    public IList<string> ForcedTextureShadowModels { get; } = [];

    /// <summary>
    /// The lines that were neither a directive nor a usable texlight.
    /// </summary>
    /// <remarks>
    /// The reference prints "ignoring bad texlight '%s' in %s" for a
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
    /// The intensity the reference lightmapper uses for a named material, or null.
    /// </summary>
    /// <param name="materialName">The material name.</param>
    /// <returns>The intensity, or null when the material has no texlight.</returns>
    /// <remarks>
    /// The reference <c>LightForTexture</c> compares
    /// with <c>Q_strcasecmp</c> -- CASE-INSENSITIVELY -- even though the
    /// duplicate check during parsing uses <c>strcmp</c> and is case-SENSITIVE.
    /// So <c>WOOD</c> and <c>wood</c> become two separate table
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
    /// Merges a later file over this one, as the reference lightmapper's second
    /// and third <c>ReadLightFile</c> calls do.
    /// </summary>
    /// <param name="later">The file read afterwards.</param>
    /// <returns>What the merge found worth reporting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="later"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// A name already present is OVERWRITTEN IN PLACE
    /// at its existing index, so the table does not grow and
    /// the later definition wins. The comparison is <c>strcmp</c>, case
    /// SENSITIVE -- which does not match the case-insensitive
    /// lookup, and that mismatch is the bug behind duplicate texlights that
    /// never warn.
    /// </para>
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
                // strcmp, not stricmp -- the reference merge is case sensitive.
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

            // The reference tests the prefixes BEFORE whitespace is
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

            // The reference skips " \t" only. A '\r' left behind by
            // the line read on a CRLF file is NOT skipped here.
            while (scan < line.Length && (line[scan] == ' ' || line[scan] == '\t'))
            {
                scan++;
            }

            string body = line[scan..];

            // sscanf matches "noshadow " case-SENSITIVELY and
            // requires whitespace after it.
            if (TryReadDirective(body, "noshadow", out string? shadowName))
            {
                int dot = shadowName.IndexOf('.', StringComparison.Ordinal);
                file.NonShadowCastingMaterials.Add(dot >= 0 ? shadowName[..dot] : shadowName);
                continue;
            }

            // The reference reads forcetextureshadow the same way.
            if (TryReadDirective(body, "forcetextureshadow", out string? modelName))
            {
                file.ForcedTextureShadowModels.Add(modelName);
                continue;
            }

            // One %s for the material name.
            string name = ReadWord(body);
            if (name.Length == 0)
            {
                // A line of four characters or fewer is dropped in
                // SILENCE; anything longer gets a message.
                if (body.Length > 4)
                {
                    file.IgnoredLines.Add(body);
                }

                continue;
            }

            if (file.TexLights.Count == MaxTexLights)
            {
                // The limit check fires BEFORE the duplicate
                // lookup, so an override at the limit aborts too.
                throw new RadLightFileException(
                    $"Too many texlights, max = {MaxTexLights}");
            }

            // The reference calls LightForString(scan + strlen(name) + 1).
            // The "+1" skips exactly one separator byte, and the RETURN VALUE
            // IS IGNORED: a light that failed to parse is stored anyway.
            int valueStart = body.IndexOf(name, StringComparison.Ordinal) + name.Length;
            string values = valueStart < body.Length ? body[(valueStart + 1)..] : string.Empty;

            (Vec3 intensity, bool parsed) = ParseIntensity(values, options);

            file.TexLights.Add(new TexLight(name, intensity, options.SourceFile, parsed));
        }

        return file;
    }

    /// <summary>
    /// The reference <c>LightForString</c> colour conversion.
    /// </summary>
    /// <param name="text">The part of the line after the material name.</param>
    /// <param name="options">The HDR mode and the global light scale.</param>
    /// <returns>
    /// The intensity, and whether the reference would have returned true.
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

        // The reference scans up to EIGHT fields, all as double.
        double[] fields = ScanDoubles(text, 8);
        int count = fields.Length;

        double r = count > 0 ? fields[0] : 0;
        double g = count > 1 ? fields[1] : 0;
        double b = count > 2 ? fields[2] : 0;
        double scaler = count > 3 ? fields[3] : 0;

        // Exactly eight fields means two 4-tuples, LDR then HDR.
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

        // Any negative component makes the whole light black.
        if (r < 0.0 || g < 0.0 || b < 0.0 || scaler < 0.0)
        {
            return (Vec3.Zero, false);
        }

        // Gamma to linear, exponent 2.2, and computed BEFORE the
        // switch so it is set even on the error path.
        float x = (float)(Math.Pow(r / 255.0, 2.2) * 255);

        switch (count)
        {
            case 1:
                // Greyscale.
                return (Scale(new Vec3(x, x, x), options.LightScale), true);

            case 3:
            case 4:
            {
                // Green and blue get the same gamma conversion.
                float y = (float)(Math.Pow(g / 255.0, 2.2) * 255);
                float z = (float)(Math.Pow(b / 255.0, 2.2) * 255);
                Vec3 intensity = new(x, y, z);

                if (count == 4)
                {
                    // The fourth field is a brightness
                    // multiplier NORMALISED BY 255, not a plain factor.
                    intensity *= (float)(scaler / 255.0);
                }

                return (Scale(intensity, options.LightScale), true);
            }

            default:
                // 0, 2, 5, 6 and 7 fields all land here. Note
                // that intensity[0] was already written before the
                // switch and the caller stores the result regardless.
                return (Scale(new Vec3(x, 0, 0), options.LightScale), false);
        }
    }

    private static Vec3 Scale(Vec3 intensity, float lightScale) =>
        // The global -scale, applied last to every
        // texlight.
        intensity * lightScale;

    private static IEnumerable<string> SplitLines(string text)
    {
        // The reference line reader reads byte by
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
