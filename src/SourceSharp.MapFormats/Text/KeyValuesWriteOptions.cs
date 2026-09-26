//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The two arguments <c>RecursiveSaveToFile</c> takes that change the bytes.
/// </summary>
/// <param name="EscapeSequences">
/// <c>m_bHasEscapeSequences</c>, which decides whether a backslash is doubled
/// on the way out. A double quote is
/// escaped either way. False by default, matching
/// the reference's <c>Init</c>.
/// </param>
/// <param name="AllowEmptyString">
/// <c>bAllowEmptyString</c>. FALSE by default in the reference,
/// and the consequence is data loss: a key whose value is the empty string is
/// not written at all.
/// </param>
/// <param name="SortKeys">
/// <c>sortKeys</c>. False by default; when true, children are sorted by name
/// before writing. The reference's patch writer takes the
/// default, so a patch VMT's keys are in INSERTION order.
/// </param>
public sealed record KeyValuesWriteOptions(
    bool EscapeSequences = false,
    bool AllowEmptyString = false,
    bool SortKeys = false)
{
    /// <summary>
    /// The arguments the reference's <c>CreateMaterialPatch</c> passes,
    /// taking every default.
    /// </summary>
    public static KeyValuesWriteOptions Default { get; } = new();
}
