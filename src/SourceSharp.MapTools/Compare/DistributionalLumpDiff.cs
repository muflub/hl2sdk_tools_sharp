//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Compare;

/// <summary>
/// The lumps that can only be described: visibility as a bit-difference count,
/// lighting as an error histogram, and the leaf-ambient lumps as a property.
/// </summary>
internal static class DistributionalLumpDiff
{
    /// <summary>
    /// <c>SURF_BUMPLIGHT</c>: the surface carries a
    /// lightmap per bump basis vector as well as the flat one.
    /// </summary>
    private const int SurfBumpLight = 0x0800;

    /// <summary>
    /// <c>NUM_BUMP_VECTS</c> plus the flat lightmap:
    /// the multiplier applies to a bumped face.
    /// </summary>
    private const int BumpLightmapCount = 4;

    /// <summary><c>MAXLIGHTMAPS</c>.</summary>
    private const int MaxLightStyles = 4;

    /// <summary>
    /// LUMP_VISIBILITY, decompressed row by row and compared bit by bit.
    /// </summary>
    internal static void Visibility(BspData a, BspData b, LumpDiffBuilder into, DiffOptions options)
    {
        VisibilityLump? va;
        VisibilityLump? vb;
        try
        {
            va = VisibilityLump.Read(a[BspLump.Visibility]);
            vb = VisibilityLump.Read(b[BspLump.Visibility]);
        }
        catch (InvalidBspException error)
        {
            into.Note = $"a visibility lump did not decode ({error.Message}); compared as raw bytes";
            ExactLumpDiff.Bytes(a[BspLump.Visibility], b[BspLump.Visibility], "VISIBILITY", into);
            return;
        }

        if (va is null || vb is null)
        {
            // An unvised map is a normal state mid-compile, not corruption --
            // but one map vised and the other not is a difference.
            if (va is not null || vb is not null)
            {
                into.Add("visibility", va is null ? "absent" : "present", vb is null ? "absent" : "present");
            }

            into.Note = "one or both maps have not been vised";
            return;
        }

        if (va.NumClusters != vb.NumClusters)
        {
            into.ComparedAsProperty = true;
            into.Note =
                "the cluster counts differ, so the rows are not the same width and cannot be "
                + "compared bit for bit; compared as a property";
            into.Add(
                "cluster count",
                va.NumClusters.ToString(CultureInfo.InvariantCulture),
                vb.NumClusters.ToString(CultureInfo.InvariantCulture));
            into.Visibility = new VisibilityDifference(va.NumClusters, vb.NumClusters, null, null);
            return;
        }

        into.Note =
            "compared as a per-cluster bit difference; B being a strict superset of A is a "
            + "legitimate vvis result (a cluster that sees too much renders too much and is "
            + "never wrong), so the direction is reported and not judged unless a caller asks";

        PvsDifference pvs = CompareColumn(a, b, va, vb, VisibilityLump.Pvs, "pvs", options);
        PvsDifference pas = CompareColumn(a, b, va, vb, VisibilityLump.Pas, "pas", options);
        into.Visibility = new VisibilityDifference(va.NumClusters, vb.NumClusters, pvs, pas);

        into.AddCount((int)Math.Min(int.MaxValue, pvs.DifferingBits + pas.DifferingBits));

        into.Judge("VISIBILITY.pvs.differingBits", pvs.DifferingBits, options.VisibilityDifferingBits);
        into.Judge("VISIBILITY.pas.differingBits", pas.DifferingBits, options.VisibilityDifferingBits);
        into.JudgeProperty("VISIBILITY.pvs.bSupersetOfA", pvs.BContainsA, options.RequireVisibilitySupersetOfA);
        into.JudgeProperty("VISIBILITY.pas.bSupersetOfA", pas.BContainsA, options.RequireVisibilitySupersetOfA);
    }

    private static PvsDifference CompareColumn(
        BspData a,
        BspData b,
        VisibilityLump va,
        VisibilityLump vb,
        int column,
        string name,
        DiffOptions options)
    {
        ReadOnlySpan<byte> bytesA = a[BspLump.Visibility].Data.Span;
        ReadOnlySpan<byte> bytesB = b[BspLump.Visibility].Data.Span;

        int row = va.RowBytes();
        Span<byte> rowA = row <= 4096 ? stackalloc byte[row] : new byte[row];
        Span<byte> rowB = row <= 4096 ? stackalloc byte[row] : new byte[row];

        long onlyInA = 0;
        long onlyInB = 0;
        int differingClusters = 0;
        List<ClusterBitDifference> worst = [];

        for (int cluster = 0; cluster < va.NumClusters; cluster++)
        {
            int offsetA = va.BitOffset(cluster, column);
            int offsetB = vb.BitOffset(cluster, column);
            if (offsetA < 0 || offsetA > bytesA.Length || offsetB < 0 || offsetB > bytesB.Length)
            {
                // A row that points outside the lump is I1's business, not
                // this instrument's; skipping it silently would be the gap, so
                // it is counted as a whole row of difference.
                onlyInA += row * 8L;
                differingClusters++;
                continue;
            }

            va.DecompressRow(bytesA[offsetA..], rowA);
            vb.DecompressRow(bytesB[offsetB..], rowB);

            int rowOnlyA = 0;
            int rowOnlyB = 0;
            for (int i = 0; i < row; i++)
            {
                byte x = rowA[i];
                byte y = rowB[i];
                if (x == y)
                {
                    continue;
                }

                rowOnlyA += BitOperations.PopCount((uint)(x & ~y));
                rowOnlyB += BitOperations.PopCount((uint)(y & ~x));
            }

            if (rowOnlyA == 0 && rowOnlyB == 0)
            {
                continue;
            }

            onlyInA += rowOnlyA;
            onlyInB += rowOnlyB;
            differingClusters++;
            worst.Add(new ClusterBitDifference(cluster, rowOnlyA, rowOnlyB));
        }

        worst.Sort(static (x, y) => y.Total.CompareTo(x.Total));
        int max = Math.Max(0, options.MaxReportedItems);
        return new PvsDifference(
            name,
            va.NumClusters,
            onlyInA,
            onlyInB,
            differingClusters,
            [.. worst.Take(max)]);
    }

    /// <summary>
    /// A lighting lump as a per-sample linear error histogram, attributed to
    /// faces.
    /// </summary>
    /// <param name="a">Map A.</param>
    /// <param name="b">Map B.</param>
    /// <param name="lump">LUMP_LIGHTING or LUMP_LIGHTING_HDR.</param>
    /// <param name="faceLump">The face lump whose <c>lightofs</c> addresses it.</param>
    /// <param name="into">The verdict being built.</param>
    /// <param name="options">The caller's thresholds.</param>
    internal static void Lighting(
        BspData a,
        BspData b,
        BspLump lump,
        BspLump faceLump,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        BspLumpData la = a[lump];
        BspLumpData lb = b[lump];

        if (la.IsEmpty && lb.IsEmpty)
        {
            into.Note = "neither map has been lit";
            return;
        }

        if (la.Length != lb.Length)
        {
            into.ComparedAsProperty = true;
            into.Note =
                "the lumps are different LENGTHS, so sample n of one is not sample n of the "
                + "other; compared as a property, NOT element-wise";
            into.Add(
                "lighting sample count",
                BspStructView.Count<ColorRgbExp32>(la).ToString(CultureInfo.InvariantCulture),
                BspStructView.Count<ColorRgbExp32>(lb).ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (!BspStructView.Fits<ColorRgbExp32>(la))
        {
            into.Note =
                "the lump is not a whole number of ColorRGBExp32; compared as raw bytes";
            ExactLumpDiff.Bytes(la, lb, lump.ToString(), into);
            return;
        }

        into.Note = string.Create(
            CultureInfo.InvariantCulture,
            $"per-sample error is the largest per-CHANNEL absolute difference in linear space "
            + $"(c * 2^e / 255); statistics are over EVERY sample, as Phase 0's "
            + $"were; samples are attributed to faces through {faceLump}'s lightofs");

        ReadOnlySpan<ColorRgbExp32> sa = BspStructView.As<ColorRgbExp32>(la);
        ReadOnlySpan<ColorRgbExp32> sb = BspStructView.As<ColorRgbExp32>(lb);

        LightmapStatistics stats = Measure(sa, sb, a, faceLump, into.BytesIdentical, options);
        into.Lightmap = stats;
        into.AddCount((int)Math.Min(int.MaxValue, stats.DifferingSampleCount));

        string prefix = lump.ToString();
        into.Judge($"{prefix}.max", stats.MaxLinear, options.LightmapMaxLinear);
        into.Judge($"{prefix}.mean", stats.MeanLinear, options.LightmapMeanLinear);
        into.Judge($"{prefix}.p99", stats.P99Linear, options.LightmapP99Linear);
        into.Judge($"{prefix}.p99.9", stats.P999Linear, options.LightmapP999Linear);
        into.Judge($"{prefix}.p99.99", stats.P9999Linear, options.LightmapP9999Linear);
        into.Judge(
            $"{prefix}.differingFraction",
            stats.DifferingFraction,
            options.LightmapDifferingFraction);
    }

    private static LightmapStatistics Measure(
        ReadOnlySpan<ColorRgbExp32> a,
        ReadOnlySpan<ColorRgbExp32> b,
        BspData map,
        BspLump faceLump,
        bool bytesIdentical,
        DiffOptions options)
    {
        (int faces, long attributed, ImmutableArray<FaceLightError> worst) =
            bytesIdentical
                ? AttributeIdentical(map, faceLump)
                : Attribute(a, b, map, faceLump, options);

        if (bytesIdentical)
        {
            // The identical case is the common one -- a self-diff, and every
            // lump of a pair of runs that agreed -- and it would otherwise cost
            // an allocation and a sort of a million-element array to learn that
            // every number is zero.
            return new LightmapStatistics(a.Length, 0, 0.0, 0.0, 0.0, 0.0, 0.0, worst, faces, attributed);
        }

        float[] errors = new float[a.Length];
        double sum = 0.0;
        long differing = 0;
        double max = 0.0;

        for (int i = 0; i < a.Length; i++)
        {
            double error = LinearLight.Error(a[i], b[i]);
            errors[i] = (float)error;
            sum += error;
            if (error > 0.0)
            {
                differing++;
            }

            if (error > max)
            {
                max = error;
            }
        }

        Array.Sort(errors);
        return new LightmapStatistics(
            a.Length,
            differing,
            max,
            a.Length == 0 ? 0.0 : sum / a.Length,
            Percentile(errors, 99.0),
            Percentile(errors, 99.9),
            Percentile(errors, 99.99),
            worst,
            faces,
            attributed);
    }

    /// <summary>
    /// The nearest-rank percentile of an ASCENDING array.
    /// </summary>
    /// <remarks>
    /// Nearest rank, <c>ceil(p/100 * N)</c>, clamped -- the definition that
    /// needs no interpolation and therefore always returns a value that is
    /// actually in the data. Phase 0's numbers were read off the same kind of
    /// rank, so the two are comparable.
    /// </remarks>
    private static double Percentile(float[] ascending, double percent)
    {
        if (ascending.Length == 0)
        {
            return 0.0;
        }

        int rank = (int)Math.Ceiling(percent / 100.0 * ascending.Length);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Length - 1)];
    }

    private static (int Faces, long Samples, ImmutableArray<FaceLightError> Worst) AttributeIdentical(
        BspData map,
        BspLump faceLump)
    {
        int faces = 0;
        long samples = 0;
        foreach ((int _, int _, int count) in FaceRanges(map, faceLump))
        {
            faces++;
            samples += count;
        }

        return (faces, samples, []);
    }

    private static (int Faces, long Samples, ImmutableArray<FaceLightError> Worst) Attribute(
        ReadOnlySpan<ColorRgbExp32> a,
        ReadOnlySpan<ColorRgbExp32> b,
        BspData map,
        BspLump faceLump,
        DiffOptions options)
    {
        int faces = 0;
        long samples = 0;
        List<FaceLightError> errors = [];

        foreach ((int index, int start, int count) in FaceRanges(map, faceLump))
        {
            faces++;
            samples += count;

            if (start < 0 || count <= 0 || start + count > a.Length)
            {
                continue;
            }

            double max = 0.0;
            double sum = 0.0;
            int differing = 0;
            for (int i = start; i < start + count; i++)
            {
                double error = LinearLight.Error(a[i], b[i]);
                sum += error;
                if (error > 0.0)
                {
                    differing++;
                }

                if (error > max)
                {
                    max = error;
                }
            }

            if (differing > 0)
            {
                errors.Add(new FaceLightError(index, count, max, sum / count, differing));
            }
        }

        errors.Sort(static (x, y) => y.MaxLinear.CompareTo(x.MaxLinear));
        return (faces, samples, [.. errors.Take(Math.Max(0, options.MaxReportedItems))]);
    }

    /// <summary>
    /// Each lit face's sample range, as (first sample, sample count).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lays the lump out per face as: the average
    /// colours first, one per light style, THEN <c>lightofs</c>, then
    /// <c>(w+1)*(h+1)</c> luxels per style, times four when the surface carries
    /// <c>SURF_BUMPLIGHT</c>. So a face's own bytes start
    /// <c>4 * styles</c> before its <c>lightofs</c>, and the average colours
    /// are that face's data and are counted with it.
    /// </para>
    /// <para>
    /// A face with <c>lightofs == -1</c> owns nothing: vrad skips
    /// <c>TEX_SPECIAL</c> surfaces and faces with no styles entirely.
    /// </para>
    /// </remarks>
    private static List<(int Face, int Start, int Count)> FaceRanges(BspData map, BspLump faceLump)
    {
        List<(int Face, int Start, int Count)> ranges = [];

        BspLumpData faces = map[faceLump];
        BspLumpData texInfoLump = map[BspLump.TexInfo];
        if (!BspStructView.Fits<DFace>(faces) || !BspStructView.Fits<TexInfo>(texInfoLump))
        {
            return ranges;
        }

        ReadOnlySpan<DFace> faceSpan = BspStructView.As<DFace>(faces);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(texInfoLump);

        for (int i = 0; i < faceSpan.Length; i++)
        {
            DFace face = faceSpan[i];
            if (face.LightOfs < 0)
            {
                continue;
            }

            int styles = 0;
            for (int s = 0; s < MaxLightStyles; s++)
            {
                if (face.Styles[s] == 255)
                {
                    break;
                }

                styles++;
            }

            if (styles == 0)
            {
                continue;
            }

            bool bumped = face.TexInfo >= 0
                && face.TexInfo < texInfos.Length
                && (texInfos[face.TexInfo].Flags & SurfBumpLight) != 0;

            int luxels = (face.LightmapTextureSizeInLuxels[0] + 1)
                * (face.LightmapTextureSizeInLuxels[1] + 1);
            int count = styles + (luxels * styles * (bumped ? BumpLightmapCount : 1));
            int start = (face.LightOfs / 4) - styles;
            ranges.Add((i, start, count));
        }

        return ranges;
    }

    /// <summary>
    /// A leaf-ambient lump, compared on its RECORD COUNT when the two disagree
    /// and element-wise only when they do not.
    /// </summary>
    /// <param name="a">Map A.</param>
    /// <param name="b">Map B.</param>
    /// <param name="lump">LUMP_LEAF_AMBIENT_LIGHTING or its HDR twin.</param>
    /// <param name="into">The verdict being built.</param>
    /// <param name="options">The caller's thresholds.</param>
    /// <remarks>
    /// <para>
    /// This is the rule the noise-floor spike forced. Five stock
    /// vrad runs of the SAME map produced 9788 / 9788 / 9790 / 9790 / 9789
    /// leaf-ambient records, and 2fort's lump was 936712 / 936740 / 936684
    /// bytes over three runs. Where the counts differ, record n of one lump is
    /// not record n of the other, and an element-wise diff is misaligned
    /// garbage -- the Phase 0 agent retracted its own first-pass numbers on
    /// exactly that ground.
    /// </para>
    /// <para>
    /// So when the counts differ this comparison reports the counts and SAYS it
    /// did so, by way of <see cref="LumpDiff.ComparedAsProperty"/>. It does not
    /// align, truncate or pad.
    /// </para>
    /// </remarks>
    internal static void LeafAmbient(
        BspData a,
        BspData b,
        BspLump lump,
        LumpDiffBuilder into,
        DiffOptions options)
    {
        BspLumpData la = a[lump];
        BspLumpData lb = b[lump];

        if (la.IsEmpty && lb.IsEmpty)
        {
            into.Note = "neither map carries this lump";
            return;
        }

        int ca = BspStructView.Count<DLeafAmbientLighting>(la);
        int cb = BspStructView.Count<DLeafAmbientLighting>(lb);

        if (ca != cb)
        {
            into.ComparedAsProperty = true;
            into.Note =
                "the record COUNTS differ, so record n of one lump is not record n of the other; "
                + "compared as a property, NOT element-wise. Stock vrad itself produces "
                + "9788/9788/9790/9790/9789 records for one map, "
                + "so this is a measured property of the reference tool";
            into.Add(
                "leaf ambient record count",
                ca.ToString(CultureInfo.InvariantCulture),
                cb.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (!BspStructView.Fits<DLeafAmbientLighting>(la) || !BspStructView.Fits<DLeafAmbientLighting>(lb))
        {
            into.ComparedAsProperty = true;
            into.Note =
                "a lump is not a whole number of dleafambientlighting_t; compared as raw bytes";
            ExactLumpDiff.Bytes(la, lb, lump.ToString(), into);
            return;
        }

        into.Note =
            "the record counts agree, so the cubes are compared element-wise in linear space; "
            + "each record contributes its six cube faces as samples";

        ReadOnlySpan<DLeafAmbientLighting> ra = BspStructView.As<DLeafAmbientLighting>(la);
        ReadOnlySpan<DLeafAmbientLighting> rb = BspStructView.As<DLeafAmbientLighting>(lb);

        long differingSamples = 0;
        double max = 0.0;
        double sum = 0.0;
        int total = ra.Length * 6;
        float[] errors = new float[total];
        int at = 0;

        for (int i = 0; i < ra.Length; i++)
        {
            DLeafAmbientLighting x = ra[i];
            DLeafAmbientLighting y = rb[i];

            if (x.X != y.X || x.Y != y.Y || x.Z != y.Z)
            {
                if (!into.Saturated)
                {
                    into.Add(
                        $"leaf ambient sample {i.ToString(CultureInfo.InvariantCulture)} position",
                        $"({x.X},{x.Y},{x.Z})",
                        $"({y.X},{y.Y},{y.Z})");
                }
                else
                {
                    into.AddCount(1);
                }
            }

            for (int face = 0; face < 6; face++)
            {
                double error = LinearLight.Error(x.Cube.Color[face], y.Cube.Color[face]);
                errors[at++] = (float)error;
                sum += error;
                if (error > 0.0)
                {
                    differingSamples++;
                }

                if (error > max)
                {
                    max = error;
                }
            }
        }

        Array.Sort(errors);
        LightmapStatistics stats = new(
            total,
            differingSamples,
            max,
            total == 0 ? 0.0 : sum / total,
            Percentile(errors, 99.0),
            Percentile(errors, 99.9),
            Percentile(errors, 99.99),
            [],
            0,
            0);

        into.Lightmap = stats;
        into.AddCount((int)Math.Min(int.MaxValue, differingSamples));

        string prefix = lump.ToString();
        into.Judge($"{prefix}.max", stats.MaxLinear, options.LightmapMaxLinear);
        into.Judge($"{prefix}.mean", stats.MeanLinear, options.LightmapMeanLinear);
        into.Judge($"{prefix}.p99", stats.P99Linear, options.LightmapP99Linear);
        into.Judge($"{prefix}.p99.9", stats.P999Linear, options.LightmapP999Linear);
        into.Judge($"{prefix}.p99.99", stats.P9999Linear, options.LightmapP9999Linear);
        into.Judge(
            $"{prefix}.differingFraction",
            stats.DifferingFraction,
            options.LightmapDifferingFraction);
    }

    /// <summary>
    /// A leaf-ambient INDEX lump: its record count is the same property, and
    /// the engine requires it non-empty when the lump it indexes exists.
    /// </summary>
    internal static void LeafAmbientIndex(BspData a, BspData b, BspLump lump, LumpDiffBuilder into)
    {
        BspLumpData la = a[lump];
        BspLumpData lb = b[lump];
        if (la.IsEmpty && lb.IsEmpty)
        {
            into.Note = "neither map carries this lump";
            return;
        }

        int ca = BspStructView.Count<DLeafAmbientIndex>(la);
        int cb = BspStructView.Count<DLeafAmbientIndex>(lb);
        into.ComparedAsProperty = true;
        into.Note =
            "compared on the record count: the index is one record per LEAF and its contents are "
            + "offsets into a lump whose own length is not stable between stock runs";

        if (ca != cb)
        {
            into.Add(
                "leaf ambient index count",
                ca.ToString(CultureInfo.InvariantCulture),
                cb.ToString(CultureInfo.InvariantCulture));
        }
    }
}
