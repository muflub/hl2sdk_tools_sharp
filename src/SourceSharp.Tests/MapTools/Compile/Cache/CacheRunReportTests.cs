//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile.Cache;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="CacheRunReport.Render"/>'s store clause: the store's figures
/// when there are some, "unusable" when the store could not be used, and
/// nothing when there are no figures.
/// </summary>
public sealed class CacheRunReportTests
{
    [Fact]
    public void NoCountersIsTheCacheOff() => Assert.Equal("cache: off", CacheRunReport.Render(null));

    [Fact]
    public void TheStoreClauseCarriesTheStoresFigures()
    {
        string line = CacheRunReport.Render(new CacheRunCounters(), new CacheStats(3, 7, 2048, 2048, 2, 1));

        Assert.EndsWith("; store: 3 row(s), 7 blob(s), 2.0 KB, 2 generation(s), 1 tool(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileLargerThanItsBlobsShowsBoth()
    {
        string line = CacheRunReport.Render(new CacheRunCounters(), new CacheStats(1, 1, 1024, 3L << 20, 1, 1));

        Assert.Contains("1.0 KB on disk 3.0 MB", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusableStoreSaysSoInsteadOfFigures()
    {
        string line = CacheRunReport.Render(new CacheRunCounters(), new CacheStats(0, 0, 0, 0, 0, 0), storeUnusable: true);

        Assert.EndsWith("; store: unusable, the run was cold", line, StringComparison.Ordinal);
        Assert.DoesNotContain("row(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFiguresNoClause() =>
        Assert.DoesNotContain("; store:", CacheRunReport.Render(new CacheRunCounters()), StringComparison.Ordinal);
}
