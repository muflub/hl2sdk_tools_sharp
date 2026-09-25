using System.Buffers.Binary;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Zip;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// One mutation per comparison kind, each of which must be found -- and must
/// be the ONLY thing found.
/// </summary>
/// <remarks>
/// <para>
/// The comparison rules require a mutation proof per instrument, and this
/// project's standing lesson is "the check that cannot fail". A diff is a check
/// whose failure mode is silence: it reports nothing, and nothing is exactly
/// what it reports when it is working. So each kind is handed a map perturbed
/// in one place and required to name that place, while every other lump stays
/// clean.
/// </para>
/// <para>
/// Every corruption happens on a copy in memory. The tracked
/// <c>dm_lockdown.bsp</c> is opened read-only and never written.
/// </para>
/// </remarks>
public sealed class BspDiffMutationTests : IClassFixture<LockdownDiffFixture>
{
    private readonly LockdownDiffFixture _map;

    /// <summary>Takes the loaded golden map.</summary>
    /// <param name="map">The fixture.</param>
    public BspDiffMutationTests(LockdownDiffFixture map) => _map = map;

    /// <summary>
    /// Requires that <paramref name="only"/> is the single lump that differs.
    /// </summary>
    private static void AssertOnlyLumpDiffers(DiffReport report, BspLump only)
    {
        foreach (LumpDiff lump in report.Lumps)
        {
            if (lump.LumpIndex == (int)only)
            {
                Assert.False(lump.Identical, $"{lump.Name} was supposed to catch the mutation");
                continue;
            }

            Assert.True(lump.Identical, lump.ToText());
        }
    }

    // ---- EXACT: one entity keyvalue ------------------------------------.

    [Fact]
    public async Task ChangingOneEntityKeyvalueIsReportedByTheExactKind()
    {
        (BspData a, BspData b) = DiffMaps.RewrittenEntities(_map.Bsp);
        MutateOneKeyvalue(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff entities = report.For(BspLump.Entities);
        Assert.Equal(DiffKind.Exact, entities.Kind);
        Assert.Equal(1, entities.DifferenceCount);
        Assert.Contains("entity 1", entities.Differences[0].Subject, StringComparison.Ordinal);
        Assert.Contains("pair 0", entities.Differences[0].Subject, StringComparison.Ordinal);
        Assert.Contains("ss_diff_mutation", entities.Differences[0].InB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingOneEntityKeyvalueLeavesEveryOtherLumpClean()
    {
        (BspData a, BspData b) = DiffMaps.RewrittenEntities(_map.Bsp);
        MutateOneKeyvalue(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        AssertOnlyLumpDiffers(report, BspLump.Entities);
    }

    [Fact]
    public async Task RewritingTheEntityLumpWithoutChangingItIsNotADifference()
    {
        // The control for the two facts above: without it, "one difference"
        // could be the writer's doing rather than the mutation's.
        (BspData a, BspData b) = DiffMaps.RewrittenEntities(_map.Bsp);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.True(report.Identical, report.ToText(false));
    }

    private static void MutateOneKeyvalue(BspData bsp)
    {
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        BspKeyValue pair = entities[1].Pairs[0];
        entities[1].Pairs[0] = pair with { Value = "ss_diff_mutation" };
        bsp[BspLump.Entities] = EntityLump.Write(entities);
    }

    // ---- CANONICAL SET: one plane --------------------------------------.

    [Fact]
    public async Task MovingOnePlaneIsReportedByTheCanonicalKindAsOneInAndOneInB()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        int moved = MoveOneUnreferencedPlane(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff planes = report.For(BspLump.Planes);
        Assert.Equal(DiffKind.CanonicalSet, planes.Kind);

        SetDifference set = Assert.IsType<SetDifference>(planes.Set);
        Assert.Equal(1, set.OnlyInACount);
        Assert.Equal(1, set.OnlyInBCount);
        Assert.NotEqual(set.OnlyInA[0], set.OnlyInB[0]);
        Assert.True(moved >= 0, "an unreferenced plane was found to move");
    }

    [Fact]
    public async Task MovingOnePlaneLeavesEveryOtherLumpClean()
    {
        // The plane chosen is one no FACE references, which is what makes this
        // sharp: the canonical face key is built from the plane's VALUE, so a
        // plane a face stands on would legitimately change that face's key too.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        MoveOneUnreferencedPlane(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        AssertOnlyLumpDiffers(report, BspLump.Planes);
    }

    private static int MoveOneUnreferencedPlane(BspData bsp)
    {
        HashSet<int> referenced = [];
        foreach (BspLump faceLump in new[] { BspLump.Faces, BspLump.OriginalFaces, BspLump.FacesHdr })
        {
            BspLumpData lump = bsp[faceLump];
            if (BspStructView.Fits<DFace>(lump))
            {
                foreach (DFace face in BspStructView.As<DFace>(lump))
                {
                    referenced.Add(face.PlaneNum);
                }
            }
        }

        Span<DPlane> planes = DiffMaps.MutableLump<DPlane>(bsp, BspLump.Planes);
        for (int i = 0; i < planes.Length; i++)
        {
            if (referenced.Contains(i))
            {
                continue;
            }

            planes[i].Dist += 1.0f;
            return i;
        }

        return -1;
    }

    // ---- DISTRIBUTIONAL: one PVS bit -----------------------------------.

    [Fact]
    public async Task SettingOnePvsBitIsReportedAsExactlyOneBit()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        Assert.True(SetOnePvsBit(b), "a literal byte with a clear bit was found in an unshared row");

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        VisibilityDifference vis = Assert.IsType<VisibilityDifference>(
            report.For(BspLump.Visibility).Visibility);
        PvsDifference pvs = Assert.IsType<PvsDifference>(vis.Pvs);

        Assert.Equal(1, pvs.DifferingBits);
        Assert.Equal(1, pvs.DifferingClusters);
        Assert.Equal(0, pvs.OnlyInABits);
        Assert.Equal(1, pvs.OnlyInBBits);
    }

    [Fact]
    public async Task SettingOnePvsBitMakesBASupersetOfAAndNotTheOtherWayRound()
    {
        // The direction is the whole reason this is reported rather than
        // judged: a cluster that sees MORE than it needs to is a legitimate
        // vvis result, and a cluster that sees less is geometry disappearing.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        SetOnePvsBit(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);
        PvsDifference pvs = Assert.IsType<PvsDifference>(
            Assert.IsType<VisibilityDifference>(report.For(BspLump.Visibility).Visibility).Pvs);

        Assert.True(pvs.BContainsA);
        Assert.False(pvs.AContainsB);
    }

    [Fact]
    public async Task SettingOnePvsBitLeavesEveryOtherLumpClean()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        SetOnePvsBit(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        AssertOnlyLumpDiffers(report, BspLump.Visibility);
    }

    /// <summary>
    /// Sets one clear bit of one literal byte of one cluster's PVS row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is run-length coded, so not every byte is a literal: a zero byte
    /// is a marker and the byte AFTER it is a repeat count
    /// (<c>DecompressVis</c>). Walking the coding is the only way to know which
    /// bytes map one-for-one onto the decompressed row.
    /// </para>
    /// <para>
    /// The row also has to be one no other cluster shares, because bsplib
    /// points several clusters at one offset when their rows are equal, and a
    /// byte in a shared row would flip a bit in every one of them -- which
    /// would make "exactly one bit" a statement about dm_lockdown's row sharing
    /// rather than about the comparer.
    /// </para>
    /// </remarks>
    private static bool SetOnePvsBit(BspData bsp)
    {
        BspLumpData lump = bsp[BspLump.Visibility];
        VisibilityLump vis = Assert.IsType<VisibilityLump>(VisibilityLump.Read(lump));
        ReadOnlySpan<byte> bytes = lump.Data.Span;

        Dictionary<int, int> uses = [];
        List<int> starts = [];
        for (int cluster = 0; cluster < vis.NumClusters; cluster++)
        {
            for (int column = VisibilityLump.Pvs; column <= VisibilityLump.Pas; column++)
            {
                int offset = vis.BitOffset(cluster, column);
                uses[offset] = uses.GetValueOrDefault(offset) + 1;
                starts.Add(offset);
            }
        }

        starts.Sort();

        int row = vis.RowBytes();
        for (int cluster = 0; cluster < vis.NumClusters; cluster++)
        {
            int offset = vis.BitOffset(cluster, VisibilityLump.Pvs);
            if (uses[offset] != 1)
            {
                continue;
            }

            int end = NextStartAfter(starts, offset, bytes.Length);
            int at = FirstLiteralWithAClearBit(bytes, offset, end, row);
            if (at < 0)
            {
                continue;
            }

            byte[] writable = DiffMaps.MutableBytes(bsp, BspLump.Visibility);
            byte value = writable[at];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((value & (1 << bit)) == 0)
                {
                    writable[at] = (byte)(value | (1 << bit));
                    return true;
                }
            }
        }

        return false;
    }

    private static int NextStartAfter(List<int> sortedStarts, int offset, int fallback)
    {
        foreach (int start in sortedStarts)
        {
            if (start > offset)
            {
                return start;
            }
        }

        return fallback;
    }

    private static int FirstLiteralWithAClearBit(ReadOnlySpan<byte> bytes, int start, int end, int rowBytes)
    {
        int decoded = 0;
        int at = start;
        while (decoded < rowBytes && at < end)
        {
            byte b = bytes[at];
            if (b != 0)
            {
                if (b != 0xFF)
                {
                    return at;
                }

                decoded++;
                at++;
                continue;
            }

            if (at + 1 >= end)
            {
                break;
            }

            decoded += bytes[at + 1];
            at += 2;
        }

        return -1;
    }

    // ---- DISTRIBUTIONAL: one lightmap sample ---------------------------.

    [Fact]
    public async Task ChangingOneLightmapSampleIsReportedAsOneDifferingSample()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        (int sample, double expected) = BumpOneLightmapSample(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(
            report.For(BspLump.Lighting).Lightmap);

        Assert.Equal(1, stats.DifferingSampleCount);
        Assert.Equal(expected, stats.MaxLinear, 12);
        Assert.Equal(1.0 / stats.SampleCount, stats.DifferingFraction, 15);
        Assert.True(sample >= 0);
    }

    [Fact]
    public async Task ChangingOneLightmapSampleNamesTheFaceThatOwnsIt()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        BumpOneLightmapSample(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);
        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(
            report.For(BspLump.Lighting).Lightmap);

        FaceLightError face = Assert.Single(stats.WorstFaces);
        Assert.Equal(1, face.DifferingSampleCount);
    }

    [Fact]
    public async Task ChangingOneLightmapSampleLeavesEveryOtherLumpClean()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        BumpOneLightmapSample(b);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        AssertOnlyLumpDiffers(report, BspLump.Lighting);
    }

    /// <summary>
    /// Adds one to the green mantissa of the first lit face's first luxel.
    /// </summary>
    /// <returns>The sample index, and the linear error the change must produce.</returns>
    private static (int Sample, double Expected) BumpOneLightmapSample(BspData bsp)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        int sample = -1;
        foreach (DFace face in faces)
        {
            if (face.LightOfs >= 0)
            {
                sample = face.LightOfs / 4;
                break;
            }
        }

        Assert.True(sample >= 0, "dm_lockdown.bsp has a lit face");

        Span<ColorRgbExp32> samples = DiffMaps.MutableLump<ColorRgbExp32>(bsp, BspLump.Lighting);
        ColorRgbExp32 original = samples[sample];
        byte green = (byte)(original.G == 255 ? original.G - 1 : original.G + 1);
        samples[sample].G = green;

        double expected = Math.Abs(
            LinearLight.Channel(green, original.Exponent)
            - LinearLight.Channel(original.G, original.Exponent));
        return (sample, expected);
    }

    // ---- EXACT: one pak entry ------------------------------------------.

    [Fact]
    public async Task ARemovedPakEntryIsReportedByName()
    {
        // The pak comparison walks a zip directory rather than a struct array,
        // so it gets a mutation of its own: without one, a comparer that failed
        // to parse the pak would report "identical" for every pair of maps.
        BspData a = WithPak("materials/a.vmt", "materials/b.vmt");
        BspData b = WithPak("materials/a.vmt");

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff pak = report.For(BspLump.PakFile);
        Assert.Equal(1, pak.DifferenceCount);
        Assert.Contains("materials/b.vmt", pak.Differences[0].Subject, StringComparison.Ordinal);
        Assert.Equal("absent", pak.Differences[0].InB);
    }

    [Fact]
    public async Task APakEntryWhoseCONTENTChangedIsReported()
    {
        // Names alone would not catch a replaced file, so the comparison
        // carries size, CRC and compression method.
        BspData a = WithPak("materials/a.vmt");
        BspData b = WithPakContent("materials/a.vmt", "different content entirely");

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff pak = report.For(BspLump.PakFile);
        Assert.Equal(1, pak.DifferenceCount);
        Assert.Contains("materials/a.vmt", pak.Differences[0].Subject, StringComparison.Ordinal);
        Assert.NotEqual(pak.Differences[0].InA, pak.Differences[0].InB);
    }

    [Fact]
    public async Task TwoIdenticalPaksAreNotADifference()
    {
        BspData a = WithPak("materials/a.vmt", "materials/b.vmt");
        BspData b = WithPak("materials/a.vmt", "materials/b.vmt");

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        Assert.True(report.Identical, report.ToText(true));
    }

    private static BspData WithPak(params string[] names)
    {
        ZipArchiveWriter writer = new();
        foreach (string name in names)
        {
            writer.Add(name, "\"UnlitGeneric\"\n{\n}\n"u8.ToArray());
        }

        BspData bsp = new();
        bsp[BspLump.PakFile] = new BspLumpData(writer.ToBytes(), 0, 0);
        return bsp;
    }

    private static BspData WithPakContent(string name, string content)
    {
        ZipArchiveWriter writer = new();
        writer.Add(name, System.Text.Encoding.ASCII.GetBytes(content));

        BspData bsp = new();
        bsp[BspLump.PakFile] = new BspLumpData(writer.ToBytes(), 0, 0);
        return bsp;
    }

    // ---- the mutation the header carries --------------------------------

    [Fact]
    public async Task ChangingTheMapRevisionIsReportedInTheHeader()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        b.MapRevision = a.MapRevision + 1;

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        DiffDifference difference = Assert.Single(report.Header);
        Assert.Equal("map revision", difference.Subject);
        Assert.Equal(b.MapRevision.ToString(CultureInfo.InvariantCulture), difference.InB);
        Assert.False(report.Identical);
    }

    [Fact]
    public async Task AnUncomparedLumpStillReportsAByteDifference()
    {
        // The NotCompared kind must not be a hole. LUMP_PHYSCOLLIDE has no
        // managed reader and the engine validates none of it, but a changed
        // byte is still a changed map.
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        byte[] physics = DiffMaps.MutableBytes(b, BspLump.PhysCollide);
        physics[64] ^= 0x01;

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff phys = report.For(BspLump.PhysCollide);
        Assert.Equal(DiffKind.NotCompared, phys.Kind);
        Assert.False(phys.Identical);
        Assert.False(phys.BytesIdentical);
        Assert.Contains("byte 64", phys.Differences[0].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingALumpVersionIsADifferenceEvenWhenTheBytesAgree()
    {
        BspData a = DiffMaps.Clone(_map.Bsp);
        BspData b = DiffMaps.Clone(_map.Bsp);
        BspLumpData original = b[BspLump.Lighting];
        b[BspLump.Lighting] = new BspLumpData(original.Data, original.Version + 1, 0);

        DiffReport report = await BspDiff.CompareAsync(a, b, CancellationToken.None);

        LumpDiff lighting = report.For(BspLump.Lighting);
        Assert.False(lighting.BytesIdentical);
        Assert.Contains(
            lighting.Differences,
            d => string.Equals(d.Subject, "lump version", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVisibilityMutationHelperReallyChangesOneDecodedBit()
    {
        // A control on the helper, not on the comparer: if the run-length walk
        // were wrong, the helper could be editing a repeat COUNT and changing a
        // whole run, and the "exactly one bit" fact above would be measuring
        // the helper's bug rather than the comparer's precision.
        BspData before = DiffMaps.Clone(_map.Bsp);
        BspData after = DiffMaps.Clone(_map.Bsp);
        Assert.True(SetOnePvsBit(after));

        int differing = 0;
        VisibilityLump va = Assert.IsType<VisibilityLump>(VisibilityLump.Read(before[BspLump.Visibility]));
        VisibilityLump vb = Assert.IsType<VisibilityLump>(VisibilityLump.Read(after[BspLump.Visibility]));
        ReadOnlySpan<byte> bytesA = before[BspLump.Visibility].Data.Span;
        ReadOnlySpan<byte> bytesB = after[BspLump.Visibility].Data.Span;

        byte[] rowA = new byte[va.RowBytes()];
        byte[] rowB = new byte[vb.RowBytes()];
        for (int cluster = 0; cluster < va.NumClusters; cluster++)
        {
            for (int column = VisibilityLump.Pvs; column <= VisibilityLump.Pas; column++)
            {
                va.DecompressRow(bytesA[va.BitOffset(cluster, column)..], rowA);
                vb.DecompressRow(bytesB[vb.BitOffset(cluster, column)..], rowB);
                for (int i = 0; i < rowA.Length; i++)
                {
                    differing += System.Numerics.BitOperations.PopCount((uint)(rowA[i] ^ rowB[i]));
                }
            }
        }

        Assert.Equal(1, differing);
    }

    [Fact]
    public void TheVisibilityLumpDeclaresItsClusterCountWhereTheHelperLooksForIt()
    {
        // Guards the helper's assumption that the offset table starts after a
        // four-byte count, which is how it locates rows at all.
        BspLumpData lump = _map.Bsp[BspLump.Visibility];
        int declared = BinaryPrimitives.ReadInt32LittleEndian(lump.Data.Span);
        VisibilityLump vis = Assert.IsType<VisibilityLump>(VisibilityLump.Read(lump));

        Assert.Equal(declared, vis.NumClusters);
    }
}
