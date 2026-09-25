namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The two arguments <c>RecursiveSaveToFile</c> takes that change the bytes.
/// </summary>
/// <param name="EscapeSequences">
/// <c>m_bHasEscapeSequences</c>, which decides whether a backslash is doubled
/// on the way out (<c>src/tier1/KeyValues.cpp:790-794</c>). A double quote is
/// escaped either way (<c>:785-789</c>). False by default, matching
/// <c>Init</c> at <c>:462</c>.
/// </param>
/// <param name="AllowEmptyString">
/// <c>bAllowEmptyString</c>. FALSE by default (<c>KeyValues.cpp:511,740,820</c>),
/// and the consequence is data loss: a key whose value is the empty string is
/// not written at all (<c>:871</c>).
/// </param>
/// <param name="SortKeys">
/// <c>sortKeys</c>. False by default; when true, children are sorted by name
/// before writing (<c>KeyValues.cpp:831-845</c>). vbsp's patch writer takes the
/// default, so a patch VMT's keys are in INSERTION order.
/// </param>
public sealed record KeyValuesWriteOptions(
    bool EscapeSequences = false,
    bool AllowEmptyString = false,
    bool SortKeys = false)
{
    /// <summary>
    /// The arguments <c>CreateMaterialPatch</c> passes
    /// (<c>src/utils/vbsp/materialpatch.cpp:141</c>, taking every default at
    /// <c>src/public/tier1/KeyValues.h:214</c>).
    /// </summary>
    public static KeyValuesWriteOptions Default { get; } = new();
}
