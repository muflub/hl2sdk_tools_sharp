//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Compare;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compare;

/// <summary>
/// A map against ITSELF, which must report nothing, by every kind.
/// </summary>
/// <remarks>
/// The first of I2's gates, and the cheap half of the pair: this one says the
/// instrument does not invent differences, and
/// <see cref="BspDiffMutationTests"/> says it can find them. Neither is worth
/// anything without the other.
/// </remarks>
public sealed class BspDiffSelfTests : IClassFixture<LockdownDiffFixture>
{
    private readonly LockdownDiffFixture _map;

    /// <summary>Takes the loaded golden map.</summary>
    /// <param name="map">The fixture.</param>
    public BspDiffSelfTests(LockdownDiffFixture map) => _map = map;

    private Task<DiffReport> SelfAsync() =>
        BspDiff.CompareAsync(_map.Bsp, _map.Bsp, CancellationToken.None);

    [Fact]
    public async Task AMapAgainstItselfIsIdentical()
    {
        DiffReport report = await SelfAsync();

        Assert.True(report.Identical, report.ToText(false));
    }

    [Fact]
    public async Task AMapAgainstItselfReportsZeroDifferences()
    {
        DiffReport report = await SelfAsync();

        Assert.Equal(0, report.DifferenceCount);
    }

    [Fact]
    public async Task AMapAgainstItselfHasIdenticalBytes()
    {
        DiffReport report = await SelfAsync();

        Assert.True(report.BytesIdentical);
    }

    [Fact]
    public async Task TheReportCoversEverySlotInTheHeader()
    {
        // Total over all 64 slots, so a lump nobody compares is visible as a
        // lump nobody compares rather than as an absence.
        DiffReport report = await SelfAsync();

        Assert.Equal(BspData.HeaderLumps, report.Lumps.Length);
        for (int slot = 0; slot < BspData.HeaderLumps; slot++)
        {
            Assert.Equal(slot, report.Lumps[slot].LumpIndex);
        }
    }

    [Theory]
    [InlineData(DiffKind.Exact)]
    [InlineData(DiffKind.CanonicalSet)]
    [InlineData(DiffKind.Distributional)]
    [InlineData(DiffKind.NotCompared)]
    public async Task EveryKindHasMembersAndAllOfThemAreIdentical(DiffKind kind)
    {
        DiffReport report = await SelfAsync();

        LumpDiff[] lumps = [.. report.OfKind(kind)];
        Assert.NotEmpty(lumps);
        Assert.All(lumps, lump => Assert.True(lump.Identical, lump.ToText()));
    }

    [Fact]
    public async Task EveryLumpNotComparedSaysWhy()
    {
        // The gap has to be legible. A NotCompared lump with no note reads
        // exactly like a lump that was compared and agreed.
        DiffReport report = await SelfAsync();

        Assert.All(
            report.OfKind(DiffKind.NotCompared),
            lump => Assert.False(string.IsNullOrWhiteSpace(lump.Note), lump.Name));
    }

    [Fact]
    public async Task ThePakFileIsComparedAsItsFileListAndTheListIsNotEmpty()
    {
        // Proves the zip path actually ran: a comparer that failed to open the
        // pak would also report "identical".
        DiffReport report = await SelfAsync();

        LumpDiff pak = report.For(BspLump.PakFile);
        Assert.Equal(DiffKind.Exact, pak.Kind);
        Assert.Contains("file list", pak.Note ?? string.Empty, StringComparison.Ordinal);
        Assert.True(pak.LengthA > 0, "dm_lockdown.bsp carries an embedded pak");
    }

    [Fact]
    public async Task EveryLightmapSampleBelongsToExactlyOneFace()
    {
        // The strongest single check on the lightofs arithmetic.
        // If the average-colour block before lightofs,
        // the (w+1)(h+1) luxels, the style multiplier or the bump multiplier
        // were wrong, the faces would not tile the lump exactly.
        DiffReport report = await SelfAsync();

        LightmapStatistics stats = Assert.IsType<LightmapStatistics>(
            report.For(BspLump.Lighting).Lightmap);

        Assert.True(stats.FacesAttributed > 0, "dm_lockdown.bsp is lit");
        Assert.Equal(stats.SampleCount, stats.SamplesAttributed);
    }

    [Fact]
    public async Task TheVisibilityLumpIsComparedClusterByCluster()
    {
        DiffReport report = await SelfAsync();

        VisibilityDifference vis = Assert.IsType<VisibilityDifference>(
            report.For(BspLump.Visibility).Visibility);

        Assert.True(vis.ClusterCountA > 0, "dm_lockdown.bsp has been vised");
        Assert.Equal(vis.ClusterCountA, vis.ClusterCountB);
        Assert.Equal(0, Assert.IsType<PvsDifference>(vis.Pvs).DifferingBits);
        Assert.Equal(0, Assert.IsType<PvsDifference>(vis.Pas).DifferingBits);
    }

    [Fact]
    public async Task TheCanonicalKindsReportTheSetTheyCompared()
    {
        // A canonical comparison that produced no set at all would report
        // "identical" for two unrelated maps.
        DiffReport report = await SelfAsync();

        foreach (LumpDiff lump in report.OfKind(DiffKind.CanonicalSet))
        {
            SetDifference set = Assert.IsType<SetDifference>(lump.Set);
            Assert.True(set.Identical, lump.ToText());
        }

        Assert.True(
            Assert.IsType<SetDifference>(report.For(BspLump.Planes).Set).CommonCount > 0,
            "dm_lockdown.bsp has planes");
    }
}
