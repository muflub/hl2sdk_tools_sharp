//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// One face's share of the lightmap error.
/// </summary>
/// <param name="Face">The face index in the lump the samples were attributed through.</param>
/// <param name="SampleCount">How many samples that face owns, average colours included.</param>
/// <param name="MaxLinear">The largest per-channel linear error over those samples.</param>
/// <param name="MeanLinear">The mean per-channel linear error over those samples.</param>
/// <param name="DifferingSampleCount">How many of those samples differ at all.</param>
public readonly record struct FaceLightError(
    int Face,
    int SampleCount,
    double MaxLinear,
    double MeanLinear,
    int DifferingSampleCount);

/// <summary>
/// The whole-map lightmap error histogram, in the statistics Phase 0 used.
/// </summary>
/// <remarks>
/// <para>
/// The five numbers and the differing fraction are exactly the ones
/// the noise-floor spike measured for stock-against-stock
/// (2.90 % of samples move, p99 0.000245, p99.9 0.003922, p99.99 0.041176,
/// max 3.396078), so a run of this instrument can be read straight against that
/// measurement. Changing the set would make the comparison impossible.
/// </para>
/// <para>
/// The population is EVERY sample in the lump, not only the ones that differ.
/// That is what makes p99 meaningful when only 2.9 % move: the percentile sits
/// inside the differing tail, and a p99 over the tail alone would be a
/// different and much larger number.
/// </para>
/// </remarks>
public sealed class LightmapStatistics
{
    internal LightmapStatistics(
        long sampleCount,
        long differingSampleCount,
        double maxLinear,
        double meanLinear,
        double p99,
        double p999,
        double p9999,
        ImmutableArray<FaceLightError> worstFaces,
        int facesAttributed,
        long samplesAttributed)
    {
        SampleCount = sampleCount;
        DifferingSampleCount = differingSampleCount;
        MaxLinear = maxLinear;
        MeanLinear = meanLinear;
        P99Linear = p99;
        P999Linear = p999;
        P9999Linear = p9999;
        WorstFaces = worstFaces;
        FacesAttributed = facesAttributed;
        SamplesAttributed = samplesAttributed;
    }

    /// <summary>How many samples the lump holds.</summary>
    public long SampleCount { get; }

    /// <summary>How many of them differ at all.</summary>
    public long DifferingSampleCount { get; }

    /// <summary>
    /// <see cref="DifferingSampleCount"/> over <see cref="SampleCount"/>, in
    /// 0..1, and zero when the lump is empty.
    /// </summary>
    public double DifferingFraction =>
        SampleCount == 0 ? 0.0 : (double)DifferingSampleCount / SampleCount;

    /// <summary>The largest per-channel linear error anywhere in the lump.</summary>
    public double MaxLinear { get; }

    /// <summary>The mean per-channel linear error over every sample.</summary>
    public double MeanLinear { get; }

    /// <summary>The 99th percentile of the per-sample error, by nearest rank.</summary>
    public double P99Linear { get; }

    /// <summary>The 99.9th percentile of the per-sample error, by nearest rank.</summary>
    public double P999Linear { get; }

    /// <summary>The 99.99th percentile of the per-sample error, by nearest rank.</summary>
    public double P9999Linear { get; }

    /// <summary>
    /// The faces carrying the largest errors, worst first, bounded by
    /// <see cref="DiffOptions.MaxReportedItems"/>.
    /// </summary>
    public ImmutableArray<FaceLightError> WorstFaces { get; }

    /// <summary>
    /// How many faces the samples could be attributed to.
    /// </summary>
    /// <remarks>
    /// A face with <c>lightofs == -1</c> owns no samples: vrad skips
    /// <c>TEX_SPECIAL</c> surfaces and faces with no light styles
    /// So this is below the face count on every
    /// real map and that is not a gap.
    /// </remarks>
    public int FacesAttributed { get; }

    /// <summary>
    /// How many samples belong to some face. Below <see cref="SampleCount"/>
    /// means the lump holds samples no face claims.
    /// </summary>
    public long SamplesAttributed { get; }

    /// <summary>Whether the two lumps agree sample for sample.</summary>
    public bool Identical => DifferingSampleCount == 0;

    /// <summary>These statistics as report lines.</summary>
    /// <returns>One line per statistic, in the invariant culture.</returns>
    public string ToText() => string.Create(
        CultureInfo.InvariantCulture,
        $"samples {SampleCount}, differing {DifferingSampleCount} ({DifferingFraction:P4}), "
        + $"max {MaxLinear:G9}, mean {MeanLinear:G9}, p99 {P99Linear:G9}, "
        + $"p99.9 {P999Linear:G9}, p99.99 {P9999Linear:G9}");
}
