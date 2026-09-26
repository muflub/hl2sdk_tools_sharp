//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// What a measured number was judged to be, against the limit the caller set
/// for it -- if the caller set one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unset"/> is the default, and it is a VISIBLE state rather than a
/// quiet pass. The comparison rules freeze each threshold from the first
/// measurement in the phase that owns the lump, so at this point in the port
/// almost every threshold is unset by construction; a report that rendered an
/// unset threshold as "within limits" would claim an agreement nobody has
/// measured.
/// </para>
/// </remarks>
public enum ThresholdVerdict
{
    /// <summary>
    /// The caller set no limit for this number. The number is reported and
    /// nothing is claimed about it.
    /// </summary>
    Unset = 0,

    /// <summary>The measurement is at or below the limit.</summary>
    Within,

    /// <summary>The measurement is above the limit.</summary>
    Exceeded,
}

/// <summary>
/// One measured number, the limit it was judged against, and the verdict.
/// </summary>
/// <param name="Name">
/// What was measured, for example <c>LIGHTING.p99</c>. Stable across runs so a
/// caller can look one up.
/// </param>
/// <param name="Measured">
/// The measurement. A boolean property is carried as 1 for true and 0 for
/// false, and its limit as 1 for "must hold".
/// </param>
/// <param name="Limit">
/// The largest value the caller accepts, or null when the caller set none.
/// </param>
/// <param name="Verdict">The judgement, which is <see cref="ThresholdVerdict.Unset"/> when
/// <paramref name="Limit"/> is null.</param>
public readonly record struct ThresholdJudgement(
    string Name,
    double Measured,
    double? Limit,
    ThresholdVerdict Verdict)
{
    /// <summary>
    /// Judges <paramref name="measured"/> against <paramref name="limit"/>.
    /// </summary>
    /// <param name="name">What was measured.</param>
    /// <param name="measured">The measurement.</param>
    /// <param name="limit">The limit, or null for "judge nothing".</param>
    /// <returns>The judgement.</returns>
    /// <remarks>
    /// The comparison is <c>&lt;=</c>: a threshold frozen at a measured maximum
    /// must accept that maximum, or the very run that set it would fail.
    /// </remarks>
    public static ThresholdJudgement Judge(string name, double measured, double? limit) =>
        new(
            name,
            measured,
            limit,
            limit is null
                ? ThresholdVerdict.Unset
                : measured <= limit.Value ? ThresholdVerdict.Within : ThresholdVerdict.Exceeded);

    /// <summary>
    /// Judges a boolean property that the caller may require to hold.
    /// </summary>
    /// <param name="name">What was measured.</param>
    /// <param name="holds">Whether the property holds.</param>
    /// <param name="required">True to require it, false to require it NOT to hold, null to judge nothing.</param>
    /// <returns>The judgement, carrying 1 for true and 0 for false.</returns>
    public static ThresholdJudgement JudgeProperty(string name, bool holds, bool? required) =>
        new(
            name,
            holds ? 1.0 : 0.0,
            required is null ? null : required.Value ? 1.0 : 0.0,
            required is null
                ? ThresholdVerdict.Unset
                : holds == required.Value ? ThresholdVerdict.Within : ThresholdVerdict.Exceeded);

    /// <summary>This judgement as one line of the report.</summary>
    /// <returns>The line, in the invariant culture.</returns>
    public string ToText() => Verdict switch
    {
        ThresholdVerdict.Unset => string.Create(
            CultureInfo.InvariantCulture,
            $"{Name} = {Measured:G9}  (threshold unset)"),
        ThresholdVerdict.Within => string.Create(
            CultureInfo.InvariantCulture,
            $"{Name} = {Measured:G9}  (within {Limit:G9})"),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"{Name} = {Measured:G9}  EXCEEDS {Limit:G9}"),
    };
}
