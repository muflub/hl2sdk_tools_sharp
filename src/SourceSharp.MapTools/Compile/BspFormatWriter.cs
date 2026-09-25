using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Compile;

/// <summary>
/// The single adapter where a resolved <see cref="FormatOptions"/> becomes the
/// writer's <see cref="BspWriteFormat"/> — translation only.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not write.</b> The two host save call sites
/// (<c>Compile.MapCompiler</c>'s chain write and <c>ssmap vbsp</c>'s
/// <c>VbspCommand.WriteBspAsync</c>) ask <see cref="ToWriteFormat"/> and pass
/// the result to the format-aware <c>BspFile.SaveAsync</c> overload when it is
/// non-null — the seam T1/T2 left open is closed (the T3 feature lane); this class
/// remains the one place the mapping lives, with its facts.
/// </para>
/// <para>
/// <see cref="FormatOptions.IsDefault"/> maps to <see langword="null"/> —
/// "use the legacy overload" — so the no-preset bytes are literally the old
/// call (the corpus-identity fact pins that; T1 pinned the same default path
/// to the goldens). Anything else constructs the writer's record from the
/// four fields observable in the written bytes (header version, world-light
/// version, the static-props game-lump version, the L4D2 lump-dir layout).
/// The remaining <see cref="FormatOptions"/> fields
/// (<c>matsyscompat</c>, <c>simpleladders</c>, <c>nodisp4virtualmesh</c>,
/// <c>noineligiblevertexlitprops</c>, <c>csgoclipcontents</c>,
/// <c>dispinfolimit</c>) are compile-BEHAVIOUR flags: they change what the
/// compiler generates, not the container, so they never reach the writer;
/// the T3/T4 feature lanes consume them off the resolved record.
/// </para>
/// <para>
/// The L4D2 layout flag is inert unless the header version is 21 (T1's ctor
/// refuses that combination; the reference write path only re-layouts when writing 21),
/// so the adapter asks for the layout only with version 21.
/// </para>
/// </remarks>
public static class BspFormatWriter
{
    /// <summary>
    /// Whether this port can currently write a map in <paramref name="format"/>.
    /// </summary>
    /// <param name="format">The resolved format.</param>
    /// <returns>True for any format the merged writer expresses.</returns>
    public static bool IsWriteSupported(FormatOptions format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.BspVersion is 19 or 20 or 21
            && format.WorldLightVersion is 0 or 1
            && (format.StaticPropsToken is null
                || BspStaticPropsFormatInfo.TryParse(format.StaticPropsToken, out _));
    }

    /// <summary>
    /// The writer's format record for a resolved format.
    /// </summary>
    /// <param name="format">The resolved format.</param>
    /// <returns>
    /// The format record to hand the writer, or <see langword="null"/> for the
    /// default format — null means "use the legacy save overload", which keeps
    /// the no-preset path literally the old call.
    /// </returns>
    /// <exception cref="FormatException">
    /// The static-props token is not in the table (hosts check
    /// <see cref="IsWriteSupported"/> first; this is the backstop).
    /// </exception>
    public static BspWriteFormat? ToWriteFormat(FormatOptions format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.IsDefault)
        {
            return null;
        }

        BspStaticPropsFormat? props =
            format.StaticPropsToken is null
                ? null
                : BspStaticPropsFormatInfo.Parse(format.StaticPropsToken);

        bool layout = format.L4d2LumpDirLayout && format.BspVersion == 21;
        return new BspWriteFormat(
            format.BspVersion,
            format.WorldLightVersion,
            props,
            layout);
    }
}
