//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Options;

/// <summary>
/// The resolved BSP-output format a compile writes with: what the presets,
/// the <c>gameinfo.txt</c> <c>Tools</c> key, and the command line agreed on.
/// </summary>
/// <remarks>
/// <para>
/// Every field defaults to what this port's writer does today, so
/// <see cref="Default"/> is the no-preset path and a run that never touches
/// a format flag must write the same bytes it wrote before this record
/// existed (a corpus-identity fact pins that).
/// </para>
/// <para>
/// The fields are the extended format vocabulary, not this port's writer
/// vocabulary.
/// The one adapter that turns them into the writer's format record is
/// <c>Compile.BspFormatWriter.ToWriteFormat</c> — translation only. The
/// write-path call sites (the <c>MapCompiler</c> chain write and
/// <c>VbspCommand.WriteBspAsync</c>) are wired to it by the format-plumbing
/// lane: a null from the adapter means the legacy <c>BspFile.SaveAsync</c> call.
/// </para>
/// </remarks>
public sealed record FormatOptions
{
    /// <summary>
    /// Builds the resolved format.
    /// </summary>
    /// <param name="bspVersion">GAMELUMP_MAPVERS version.</param>
    /// <param name="worldLightVersion">GAMELUMP_LIGHTGROUPS version.</param>
    /// <param name="staticPropsToken">
    /// The written static-props token, or null for the writer default.
    /// </param>
    /// <param name="matsysCompat">The matsys-compat lump set.</param>
    /// <param name="simpleLadders">Simple ladders.</param>
    /// <param name="noDisp4VirtualMesh">The disp 4 virtual mesh suppression.</param>
    /// <param name="noIneligibleVertexLitProps">
    /// Skipping props ineligible for vertex lighting.
    /// </param>
    /// <param name="csgoClipContents">The csgo clip contents.</param>
    /// <param name="dispInfoLimit">The disp-info cap, or null for the writer default.</param>
    /// <param name="l4d2LumpDirLayout">The L4D2 lump-directory layout.</param>
    /// <param name="presetName">
    /// Content provenance: the last preset flag or auto-detected bucket named,
    /// or null when nothing ever applied a preset.
    /// </param>
    /// <param name="detectedSteamAppId">
    /// Content provenance: the mounted <c>SteamAppId</c> that auto-detect saw,
    /// or 0 when auto-detect never ran or the gameinfo named none.
    /// </param>
    public FormatOptions(
        int bspVersion = DefaultBspVersion,
        int worldLightVersion = 0,
        string? staticPropsToken = null,
        bool matsysCompat = false,
        bool simpleLadders = false,
        bool noDisp4VirtualMesh = false,
        bool noIneligibleVertexLitProps = false,
        bool csgoClipContents = false,
        int? dispInfoLimit = null,
        bool l4d2LumpDirLayout = false,
        string? presetName = null,
        int detectedSteamAppId = 0)
    {
        BspVersion = bspVersion;
        WorldLightVersion = worldLightVersion;
        StaticPropsToken = staticPropsToken;
        MatsysCompat = matsysCompat;
        SimpleLadders = simpleLadders;
        NoDisp4VirtualMesh = noDisp4VirtualMesh;
        NoIneligibleVertexLitProps = noIneligibleVertexLitProps;
        CsgoClipContents = csgoClipContents;
        DispInfoLimit = dispInfoLimit;
        L4d2LumpDirLayout = l4d2LumpDirLayout;
        PresetName = presetName;
        DetectedSteamAppId = detectedSteamAppId;
    }

    /// <summary>
    /// Today's writer's pinned GAMELUMP_MAPVERS version (the Source-2013-era
    /// 20), so an untouched <see cref="FormatOptions"/> asks for exactly what
    /// the writer already writes.
    /// </summary>
    public const int DefaultBspVersion = 20;

    /// <summary>
    /// The static-props version today's writer pins in GAMELUMP_STATIC_PROPS
    /// (10), so a null <see cref="StaticPropsToken"/> and this version are the
    /// same observable thing.
    /// </summary>
    public const int DefaultStaticPropsVersion = 10;

    /// <summary>
    /// The token table for <c>-staticpropformat</c>, in stored index order
    /// (<c>10_TF2</c> compared
    /// first, then the digit range 6–14).
    /// </summary>
    public static IReadOnlyList<string> StaticPropsTokens { get; } =
        ["6", "7", "8", "9", "10", "10_TF2", "11", "12", "13", "14"];

    /// <summary>The no-preset default: every field at today's writer behavior.</summary>
    public static FormatOptions Default { get; } = new();

    /// <summary>GAMELUMP_MAPVERS version.</summary>
    public int BspVersion { get; init; }

    /// <summary>GAMELUMP_LIGHTGROUPS version.</summary>
    public int WorldLightVersion { get; init; }

    /// <summary>
    /// The written static-props token; null keeps the
    /// writer's pinned default version.
    /// </summary>
    public string? StaticPropsToken { get; init; }

    /// <summary>The matsys-compat lump set.</summary>
    public bool MatsysCompat { get; init; }

    /// <summary>Simple ladders.</summary>
    public bool SimpleLadders { get; init; }

    /// <summary>Suppress the disp 4 virtual mesh.</summary>
    public bool NoDisp4VirtualMesh { get; init; }

    /// <summary>Skip props ineligible for vertex lighting.</summary>
    public bool NoIneligibleVertexLitProps { get; init; }

    /// <summary>The csgo clip contents.</summary>
    public bool CsgoClipContents { get; init; }

    /// <summary>The disp-info cap; null is the writer default.</summary>
    public int? DispInfoLimit { get; init; }

    /// <summary>The L4D2 lump-directory layout.</summary>
    public bool L4d2LumpDirLayout { get; init; }

    /// <summary>Provenance: the preset last applied, or null.</summary>
    public string? PresetName { get; init; }

    /// <summary>Provenance: the appid auto-detect saw, or 0.</summary>
    public int DetectedSteamAppId { get; init; }

    /// <summary>
    /// Whether every writable field matches <see cref="Default"/>; the
    /// provenance fields do not count, so a run whose preset happened to
    /// change nothing still reports <c>true</c> and takes the legacy write
    /// path unchanged.
    /// </summary>
    public bool IsDefault =>
        BspVersion == DefaultBspVersion
        && WorldLightVersion == 0
        && StaticPropsToken is null
        && !MatsysCompat
        && !SimpleLadders
        && !NoDisp4VirtualMesh
        && !NoIneligibleVertexLitProps
        && !CsgoClipContents
        && DispInfoLimit is null
        && !L4d2LumpDirLayout;
}

/// <summary>
/// One overlay step of the format resolution: a preset's fields, or the
/// hand-typed <c>-bspformat</c>/<c>-lightformat</c>/<c>-staticpropformat</c>
/// flags. A null field means "not said", so merging is last-write-wins per
/// field, which is how the reference behaves — later stores overwrite only the
/// fields later code names.
/// </summary>
/// <param name="BspVersion">The GAMELUMP_MAPVERS version, or null.</param>
/// <param name="WorldLightVersion">The GAMELUMP_LIGHTGROUPS version, or null.</param>
/// <param name="StaticPropsToken">The static-props token, or null.</param>
/// <param name="MatsysCompat">The matsys-compat lump set, or null.</param>
/// <param name="SimpleLadders">Simple ladders, or null.</param>
/// <param name="NoDisp4VirtualMesh">The disp-4-virtual-mesh suppression, or null.</param>
/// <param name="NoIneligibleVertexLitProps">The vertex-lit-eligibility skip, or null.</param>
/// <param name="CsgoClipContents">The csgo clip contents, or null.</param>
/// <param name="DispInfoLimit">The disp-info cap, or null.</param>
/// <param name="L4d2LumpDirLayout">The L4D2 lump-dir layout, or null.</param>
public sealed record FormatOverrides(
    int? BspVersion = null,
    int? WorldLightVersion = null,
    string? StaticPropsToken = null,
    bool? MatsysCompat = null,
    bool? SimpleLadders = null,
    bool? NoDisp4VirtualMesh = null,
    bool? NoIneligibleVertexLitProps = null,
    bool? CsgoClipContents = null,
    int? DispInfoLimit = null,
    bool? L4d2LumpDirLayout = null)
{
    /// <summary>The empty overlay: nothing said.</summary>
    public static FormatOverrides None { get; } = new();

    /// <summary>Whether no field is said.</summary>
    public bool IsEmpty =>
        BspVersion is null
        && WorldLightVersion is null
        && StaticPropsToken is null
        && MatsysCompat is null
        && SimpleLadders is null
        && NoDisp4VirtualMesh is null
        && NoIneligibleVertexLitProps is null
        && CsgoClipContents is null
        && DispInfoLimit is null
        && L4d2LumpDirLayout is null;

    /// <summary>
    /// Applies this overlay over <paramref name="base"/>; null fields leave
    /// <paramref name="base"/>'s values standing.
    /// </summary>
    /// <param name="base">The options so far.</param>
    /// <returns>The options with this overlay's non-null fields written in.</returns>
    public FormatOverrides ApplyOver(FormatOverrides @base)
    {
        ArgumentNullException.ThrowIfNull(@base);
        return new FormatOverrides(
            BspVersion ?? @base.BspVersion,
            WorldLightVersion ?? @base.WorldLightVersion,
            StaticPropsToken ?? @base.StaticPropsToken,
            MatsysCompat ?? @base.MatsysCompat,
            SimpleLadders ?? @base.SimpleLadders,
            NoDisp4VirtualMesh ?? @base.NoDisp4VirtualMesh,
            NoIneligibleVertexLitProps ?? @base.NoIneligibleVertexLitProps,
            CsgoClipContents ?? @base.CsgoClipContents,
            DispInfoLimit ?? @base.DispInfoLimit,
            L4d2LumpDirLayout ?? @base.L4d2LumpDirLayout);
    }
}
/// <summary>
/// The format-resolution pipeline: defaults, then the appid's auto-detected
/// preset, then the gameinfo <c>Tools</c> key, then the real command line,
/// each step overwriting only the fields it names (last-write-wins), plus
/// the diagnostics each step produced.
/// </summary>
/// <remarks>
/// <para>
/// Order, pinned by facts: defaults, then the appid's auto-detected preset,
/// then the mounted gameinfo's <c>Tools → vbsp</c> key (tokenised and parsed
/// as if typed), then the real command line. The
/// appid auto-apply stores its preset during registration, ahead of the real
/// argv walk; the Tools tokens are then prepended and
/// walked, and finally the typed argv — so the Tools line WINS
/// over the auto-detected preset (which is what lets a Tools line select its
/// own preset), and the typed flags win over both. A Tools line may itself
/// select a preset (its own <c>-csgo</c> token) or name fields; a preset is
/// an overlay, so a Tools preset over an auto preset merges field-wise
/// over already-set values.
/// </para>
/// </remarks>
public static class FormatResolution
{
    /// <summary>The auto-detect message, formatted with the preset name.</summary>
    public const string AutoDetectMessage = "Auto-detected that this game requires {0} BSP format";

    /// <summary>The Tools splice log line.</summary>
    public const string ToolsSpliceMessage = "Adding arguments from gameinfo: {0}";

    /// <summary>The unknown-token message for <c>-staticpropformat</c>.</summary>
    public const string UnrecognizedPropFormatMessage = "Unrecognized prop format {0}";

    /// <summary>Code of the auto-detect info diagnostic.</summary>
    public const string AutoDetectCode = "ARGS0009";

    /// <summary>Code of the Tools-splice info diagnostic.</summary>
    public const string ToolsSpliceCode = "ARGS0010";

    /// <summary>Code of the unknown <c>-staticpropformat</c> token warning.</summary>
    public const string UnrecognizedPropFormatCode = "ARGS0011";

    /// <summary>Code of a problem surfaced from the Tools key.</summary>
    public const string ToolsProblemCode = "ARGS0012";

    /// <summary>
    /// Code of the warning for the flat <c>Tools "…"</c> form that the
    /// reference reads as a section and ignores — <see cref="GameInfo.HasFlatToolsValue"/>.
    /// </summary>
    public const string FlatToolsCode = "ARGS0013";

    /// <summary>
    /// The warning for the flat form: the file's author typed arguments no
    /// tool will ever see. The reference says nothing at all here (the flags are simply
    /// never read); saying it is this port's deviation, and a deliberate one,
    /// because the shape is a mod author's mistake rather than a choice.
    /// </summary>
    public const string FlatToolsMessage =
        "the gameinfo carries a flat Tools \"…\" entry, which the tools read "
        + "as a section and ignore; write Tools { vbsp \"…\" } to pass arguments.";

    /// <summary>
    /// The full sequence for one run.
    /// </summary>
    /// <param name="cliOverrides">
    /// The format-family flags the real command line said (from
    /// <see cref="StockArgs.ParseVbsp"/>), or null.
    /// </param>
    /// <param name="cliPresetName">
    /// The preset name the real command line's preset flag said, or null.
    /// </param>
    /// <param name="noFormatDetect">Whether <c>-noformatdetect</c> suppressed auto-detect.</param>
    /// <param name="noToolsArgs">Whether <c>-notoolsargs</c> skipped the splice.</param>
    /// <param name="gameInfo">The mounted gameinfo, or null when none was found.</param>
    /// <returns>The resolved options and the diagnostics the steps logged.</returns>
    /// <remarks>
    /// <para>
    /// <b>Tools-parse problems are surfaced, never fatal.</b> The Tools line
    /// is parsed with the same parser as a real command line and can produce
    /// its diagnostics; the file system authored that line, and the reference
    /// would have spliced and ACTED on the flags in it (an unknown flag there reaches
    /// the real parser and kills the process). This port reports such
    /// problems as warnings naming the Tools key and carries on — a broken
    /// Tools line must not fail a compile the rest of which is fine. The
    /// valid flags it did carry are still applied.
    /// </para>
    /// </remarks>
    public static Result Resolve(
        FormatOverrides? cliOverrides,
        string? cliPresetName,
        bool noFormatDetect,
        bool noToolsArgs,
        GameInfo? gameInfo)
    {
        List<CompileDiagnostic> diagnostics = [];
        FormatOverrides merged = FormatOverrides.None;
        string? presetName = null;

        int appid = gameInfo?.SteamAppId ?? 0;

        // 1. The appid's preset, over the defaults, speaking only its own
        // fields (overlay semantics).
        if (!noFormatDetect && appid != 0 && MapFormatPreset.TryFromSteamAppId(appid, out MapFormatPreset? auto))
        {
            merged = auto!.ToOverrides().ApplyOver(merged);
            presetName = auto.Name;
            diagnostics.Add(new CompileDiagnostic(
                AutoDetectCode,
                DiagnosticSeverity.Info,
                string.Format(CultureInfo.InvariantCulture, AutoDetectMessage, auto.Name)));
        }

        if (gameInfo?.HasFlatToolsValue is true)
        {
            diagnostics.Add(new CompileDiagnostic(
                FlatToolsCode, DiagnosticSeverity.Warning, FlatToolsMessage));
        }

        // 2. The Tools splice, applied as if typed — over the auto-preset
        // (see class remarks) and under the CLI.
        string? toolsValue = noToolsArgs ? null : gameInfo?.ToolArguments.GetValueOrDefault("vbsp");
        if (!string.IsNullOrWhiteSpace(toolsValue))
        {
            diagnostics.Add(new CompileDiagnostic(
                ToolsSpliceCode,
                DiagnosticSeverity.Info,
                string.Format(CultureInfo.InvariantCulture, ToolsSpliceMessage, toolsValue)));

            string[] tokens = [.. TokenizeToolsArguments(toolsValue), "tools-args.map"];
            StockArgsResult<VbspOptions> toolsParsed = StockArgs.ParseVbsp(tokens);
            if (toolsParsed.Format is { } tf)
            {
                merged = tf.ApplyOver(merged);
            }

            if (toolsParsed.PresetName is not null)
            {
                presetName = toolsParsed.PresetName;
            }

            foreach (CompileDiagnostic d in toolsParsed.Diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    diagnostics.Add(new CompileDiagnostic(
                        ToolsProblemCode,
                        DiagnosticSeverity.Warning,
                        $"{d.Message} (from the gameinfo Tools key: \"{toolsValue}\")"));
                }
            }
        }

        // 3. The typed command line, last word.
        if (cliOverrides is { IsEmpty: false })
        {
            merged = cliOverrides.ApplyOver(merged);
        }

        if (cliPresetName is not null)
        {
            presetName = cliPresetName;
        }

        FormatOptions resolved = new(
            merged.BspVersion ?? FormatOptions.DefaultBspVersion,
            merged.WorldLightVersion ?? 0,
            merged.StaticPropsToken,
            merged.MatsysCompat ?? false,
            merged.SimpleLadders ?? false,
            merged.NoDisp4VirtualMesh ?? false,
            merged.NoIneligibleVertexLitProps ?? false,
            merged.CsgoClipContents ?? false,
            merged.DispInfoLimit,
            merged.L4d2LumpDirLayout ?? false,
            presetName,
            appid);

        return new Result(resolved, diagnostics);
    }

    /// <summary>
    /// The tokenization of the <c>Tools</c> key value, matching the reference:
    /// a plain whitespace
    /// split (strtok-style — no quote handling, consecutive separators
    /// collapse, empty runs drop), so a quoted value's quotes stay inside the
    /// tokens.
    /// </summary>
    /// <param name="toolsValue">The raw key value as written.</param>
    /// <returns>The tokens to splice into the command line.</returns>
    public static IReadOnlyList<string> TokenizeToolsArguments(string toolsValue)
    {
        ArgumentNullException.ThrowIfNull(toolsValue);
        return toolsValue.Split(
            [' ', '\t', '\r', '\n', '\f', '\v'],
            StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Validates a <c>-staticpropformat</c> token against the token table.
    /// </summary>
    /// <param name="token">The token as typed.</param>
    /// <returns>Whether the token is accepted.</returns>
    public static bool IsKnownStaticPropsToken(string token) =>
        token is not null && FormatOptions.StaticPropsTokens.Contains(token, StringComparer.Ordinal);

    /// <summary>
    /// The written static-props version a token asks for: the token's LEADING
    /// NUMBER, per T1's ruling (merged maptools tip 8cd11d3ac,
    /// <c>BspStaticPropsFormatInfo.GameLumpVersion</c>) — <c>"9"</c> writes 9,
    /// <c>"10"</c> (Insurgency) writes 10, <c>"10_TF2"</c> writes 10 with the
    /// TF2 flavour. Tokens are named by the wire version they select; the
    /// token's table INDEX (0..9) is a different numbering and is not the version.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <returns>The version.</returns>
    /// <exception cref="FormatException">The token has no leading digits.</exception>
    public static int StaticPropsVersionOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        int end = 0;
        while (end < token.Length && token[end] is >= '0' and <= '9')
        {
            end++;
        }

        return end > 0
            && int.TryParse(token.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
                ? version
                : throw new FormatException(
                    string.Format(CultureInfo.InvariantCulture, UnrecognizedPropFormatMessage, token));
    }

    /// <summary>
    /// The validated value of <c>-bspformat</c>: the reference parses it with
    /// <c>atoi</c>
    /// and no range check, but this port's writer ctor validates 19/20/21, so
    /// an out-of-table version is a parse error here rather than a late one.
    /// </summary>
    /// <param name="text">The raw argument.</param>
    /// <param name="version">The parsed version.</param>
    /// <returns>Whether the value is accepted.</returns>
    public static bool TryParseBspFormat(string text, out int version) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)
        && version is 19 or 20 or 21;

    /// <summary>
    /// The validated value of <c>-lightformat</c>: the table is 0 and 1.
    /// </summary>
    /// <param name="text">The raw argument.</param>
    /// <param name="version">The parsed version.</param>
    /// <returns>Whether the value is accepted.</returns>
    public static bool TryParseLightFormat(string text, out int version) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)
        && version is 0 or 1;

    /// <summary>The resolved options plus the diagnostics the steps logged.</summary>
    /// <param name="Resolved">The options to compile with.</param>
    /// <param name="Diagnostics">The auto-detect/splice messages.</param>
    public sealed record Result(FormatOptions Resolved, IReadOnlyList<CompileDiagnostic> Diagnostics);
}
