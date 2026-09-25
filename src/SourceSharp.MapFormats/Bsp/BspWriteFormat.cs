using System;

namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// Which static-prop wire format a written <c>sprp</c> game lump declares.
/// </summary>
/// <remarks>
/// <para>
/// The members carry the numbering of the tools++ prop-format global
/// (<c>DAT_14010c0e0</c> in the recovered <c>-staticpropformat</c> parser:
/// report §1.1, <c>dumps/vbsp.all.c:46397</c>), which is a token index rather
/// than the wire version: <see cref="BspStaticPropsFormatInfo.GameLumpVersion"/>
/// maps it to the number the game-lump directory actually stores. The tokens
/// and their groupings ("7 8 (L4D)", "11 12 (CSGO)") are the tool's own help
/// text; the engine's <c>sprp</c> versioning is what makes 6..14 the domain.
/// </para>
/// <para>
/// Stock SDK 2013 writes one constant, <c>GAMELUMP_STATIC_PROPS_VERSION = 10</c>
/// (<c>gamebspfile.h:37</c>); everything here is the feature that lets other
/// branches' maps be produced, not a stock behaviour.
/// </para>
/// </remarks>
public enum BspStaticPropsFormat
{
    /// <summary>Token <c>6</c>: the HL2 format.</summary>
    V6 = 0,

    /// <summary>Token <c>7</c>: left 4 Dead, first pass.</summary>
    V7,

    /// <summary>Token <c>8</c>: left 4 Dead.</summary>
    V8,

    /// <summary>Token <c>9</c>: left 4 Dead 2.</summary>
    V9,

    /// <summary>Token <c>10_TF2</c>: Team Fortress 2, the ++ default token.</summary>
    V10Tf2,

    /// <summary>Token <c>10</c>: Insurgency. Same wire version as <see cref="V10Tf2"/>.</summary>
    V10,

    /// <summary>Token <c>11</c>: Counter-Strike: Global Offensive, first pass.</summary>
    V11,

    /// <summary>Token <c>12</c>: Counter-Strike: Global Offensive.</summary>
    V12,

    /// <summary>Token <c>13</c>: Dota (Strata).</summary>
    V13,

    /// <summary>Token <c>14</c>: the newest format the ++ table names.</summary>
    V14,
}

/// <summary>
/// The token and wire version behind a <see cref="BspStaticPropsFormat"/>.
/// </summary>
/// <remarks>
/// A companion class rather than enum members because an enum cannot carry
/// methods, and the tool's parser compares the tokens with <c>strcmp</c>: the
/// match here is <see cref="StringComparison.Ordinal"/> for the same reason
/// <c>"10"</c> and <c>"10_TF2"</c> are distinct formats with distinct meanings
/// despite the numeric prefix they share.
/// </remarks>
public static class BspStaticPropsFormatInfo
{
    /// <summary>The token the ++ tool defaults to when none is given.</summary>
    public const string DefaultToken = "10_TF2";

    /// <summary>
    /// The CLI token that selects <paramref name="format"/>.
    /// </summary>
    /// <param name="format">The format to name.</param>
    /// <returns>One of the ten tokens in the ++ help string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="format"/> is not one of the defined members.
    /// </exception>
    public static string Token(BspStaticPropsFormat format) => format switch
    {
        BspStaticPropsFormat.V6 => "6",
        BspStaticPropsFormat.V7 => "7",
        BspStaticPropsFormat.V8 => "8",
        BspStaticPropsFormat.V9 => "9",
        BspStaticPropsFormat.V10Tf2 => "10_TF2",
        BspStaticPropsFormat.V10 => "10",
        BspStaticPropsFormat.V11 => "11",
        BspStaticPropsFormat.V12 => "12",
        BspStaticPropsFormat.V13 => "13",
        BspStaticPropsFormat.V14 => "14",
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "not a defined static-prop format"),
    };

    /// <summary>
    /// The version the game-lump directory stores for <paramref name="format"/>:
    /// the number the token is named after, so 6..14.
    /// </summary>
    /// <param name="format">The format to convert.</param>
    /// <returns>The <c>sprp</c> entry version to stamp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="format"/> is not one of the defined members.
    /// </exception>
    /// <remarks>
    /// The tokens are named by the wire version they select, which is what
    /// makes <c>"10_TF2"</c> and <c>"10"</c> two tokens with the SAME version
    /// (10 -- stock's <c>GAMELUMP_STATIC_PROPS_VERSION</c>) while differing in
    /// the payload fields the tool's serializer emits for them
    /// (<c>dumps/vbsp.all.c:38504</c> switches on the token index for the
    /// field layout, not for the stamped number). Reading the numbering
    /// <i>as</i> the version (+6 on the token index) is the trap: it puts
    /// <c>"10"</c> at 11 and <c>"14"</c> at 15, outside the 6..14 domain the
    /// report itself names.
    /// </remarks>
    public static int GameLumpVersion(BspStaticPropsFormat format) => format switch
    {
        BspStaticPropsFormat.V6 => 6,
        BspStaticPropsFormat.V7 => 7,
        BspStaticPropsFormat.V8 => 8,
        BspStaticPropsFormat.V9 => 9,
        BspStaticPropsFormat.V10Tf2 => 10,
        BspStaticPropsFormat.V10 => 10,
        BspStaticPropsFormat.V11 => 11,
        BspStaticPropsFormat.V12 => 12,
        BspStaticPropsFormat.V13 => 13,
        BspStaticPropsFormat.V14 => 14,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "not a defined static-prop format"),
    };

    /// <summary>
    /// Resolves a <c>-staticpropformat</c> token.
    /// </summary>
    /// <param name="token">The token, matched exactly as ++'s <c>strcmp</c> would.</param>
    /// <returns>The format the token names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
    /// <exception cref="FormatException">
    /// The token is not one of the ten. The message repeats ++'s
    /// <c>"Unrecognized prop format %s"</c> so a user who knows the tool
    /// recognises the complaint.
    /// </exception>
    public static BspStaticPropsFormat Parse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (TryParse(token, out BspStaticPropsFormat format))
        {
            return format;
        }

        throw new FormatException($"Unrecognized prop format {token}");
    }

    /// <summary>
    /// Resolves a token without throwing, for command-line validation.
    /// </summary>
    /// <param name="token">The token to look up.</param>
    /// <param name="format">The resolved format; <c>default</c> when false.</param>
    /// <returns>Whether <paramref name="token"/> names a format.</returns>
    public static bool TryParse(string? token, out BspStaticPropsFormat format)
    {
        switch (token)
        {
            case "6": format = BspStaticPropsFormat.V6; return true;
            case "7": format = BspStaticPropsFormat.V7; return true;
            case "8": format = BspStaticPropsFormat.V8; return true;
            case "9": format = BspStaticPropsFormat.V9; return true;
            case "10_TF2": format = BspStaticPropsFormat.V10Tf2; return true;
            case "10": format = BspStaticPropsFormat.V10; return true;
            case "11": format = BspStaticPropsFormat.V11; return true;
            case "12": format = BspStaticPropsFormat.V12; return true;
            case "13": format = BspStaticPropsFormat.V13; return true;
            case "14": format = BspStaticPropsFormat.V14; return true;
            default:
                format = default;
                return false;
        }
    }
}

/// <summary>
/// The output format <see cref="BspFile.SaveAsync(BspData, Stream, BspWriteMode, BspWriteFormat, CancellationToken)"/>
/// stamps: header version, world-light lump version, static-prop format, and
/// the L4D2 lump-directory re-layout.
/// </summary>
/// <remarks>
/// <para>
/// Everything the ++ presets set that decides the SHAPE of the written file,
/// as one immutable value (report §1.1: <c>-bspformat</c>, <c>-lightformat</c>,
/// <c>-staticpropformat</c>, and the L4D2 lump layout the <c>-l4d2</c> preset
/// tail-arms). The choices each field can take are the engine's contract, not
/// preferences, so the constructor rejects anything outside them rather than
/// letting a bad value reach a file.
/// </para>
/// <para>
/// The version fields are only honoured by a canonical write: an explicit
/// format is an instruction about the output, and PreserveSourceLayout exists
/// precisely to reproduce the input instead -- there the header and lump
/// versions come from the source file, and the L4D2 layout flag comes from
/// <see cref="BspData.SourceLumpsUseL4d2Layout"/>.
/// </para>
/// <para>
/// <see cref="Version"/> is the header version to stamp: 19 (HL2), 20 (the
/// stock default, which is what ++ writes when <c>-bspformat</c> is absent),
/// or 21 (ASW / L4D2 / Portal 2 / CS:GO).
/// </para>
/// <para>
/// <see cref="WorldLightVersion"/> is the version stamped on both the
/// world-light lumps (slot 15 and slot 54): 0 for HL2/TF2, 1 for
/// ASW/L4D2/Portal 2/CS:GO. Stock bsplib passes no version for these two lumps
/// and so stamps 0; this field is the ++ <c>-lightformat</c> global.
/// </para>
/// <para>
/// <see cref="StaticPropsFormat"/> is the version to stamp on the <c>sprp</c>
/// game-lump entry, or null to leave each game lump's version exactly as
/// loaded -- which is what today's writer does, and what a fresh compile
/// wants: the prop stage already picked the version matching the payload it
/// built.
/// </para>
/// <para>
/// <see cref="L4d2LumpDirLayout"/> decides whether the 64 header lump entries
/// go out in the L4D2 re-layout: the same sixteen bytes per entry with the
/// fields shifted one dword right, so <c>(version, fileofs, filelen,
/// uncompressedSize)</c> instead of <c>(fileofs, filelen, version,
/// uncompressedSize)</c>. The ++ reader detects the layout by <c>version ==
/// 21</c> and the first lump's first dword being zero, so the flag is only
/// constructible with version 21 -- at any other version the file would read
/// back as offsets nobody can find.
/// </para>
/// </remarks>
public sealed record BspWriteFormat
{
    /// <summary>
    /// Builds a format object, enforcing the domains the record's remarks name.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="Version"/> is not 19, 20 or 21, or
    /// <paramref name="WorldLightVersion"/> is not 0 or 1.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="L4d2LumpDirLayout"/> was asked for on a version that
    /// cannot be detected as re-laid out.
    /// </exception>
    public BspWriteFormat(
        int Version,
        int WorldLightVersion,
        BspStaticPropsFormat? StaticPropsFormat,
        bool L4d2LumpDirLayout)
    {
        // Readonly auto-properties: the constructor is the only way in, so a
        // `with` expression cannot rewrite Version or the layout flag past the
        // checks below (`with` on read-only properties is a compile error).
        this.Version = Version;
        this.WorldLightVersion = WorldLightVersion;
        this.StaticPropsFormat = StaticPropsFormat;
        this.L4d2LumpDirLayout = L4d2LumpDirLayout;

        if (Version is not (BspData.MinVersion or BspData.Version or BspData.MaxVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Version), Version,
                $"the header version must be {BspData.MinVersion}, {BspData.Version} "
                + $"or {BspData.MaxVersion}");
        }

        if (WorldLightVersion is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(WorldLightVersion), WorldLightVersion,
                "the world-light lump is written at version 0 or 1");
        }

        if (L4d2LumpDirLayout && Version != BspData.MaxVersion)
        {
            throw new ArgumentException(
                $"the L4D2 lump directory is only recognised by the reader at version "
                + $"{BspData.MaxVersion}; at version {Version} the re-layout would read back "
                + "as offsets nobody can find",
                nameof(L4d2LumpDirLayout));
        }
    }

    /// <summary>The header version this format stamps.</summary>
    public int Version { get; }

    /// <summary>The version this format stamps on both world-light lumps.</summary>
    public int WorldLightVersion { get; }

    /// <summary>The static-prop format to stamp on <c>sprp</c>, or null to keep each entry's own.</summary>
    public BspStaticPropsFormat? StaticPropsFormat { get; }

    /// <summary>Whether canonical writes emit the L4D2 lump-directory re-layout.</summary>
    public bool L4d2LumpDirLayout { get; }

    /// <summary>
    /// The format that reproduces today's output: version 20 as stock and the
    /// ++ default, world-light lumps at 0 as stock's versionless
    /// <c>AddLump</c> stamps them, game-lump versions left alone, and the
    /// standard lump directory.
    /// </summary>
    /// <remarks>
    /// An expression-bodied property, not a stored singleton, so the class
    /// holds no static state for the library rules to police; records compare
    /// by value, so two callers asking for <see cref="Default"/> agree.
    /// </remarks>
    public static BspWriteFormat Default => new(
        BspData.Version, WorldLightVersion: 0, StaticPropsFormat: null, L4d2LumpDirLayout: false);
}
