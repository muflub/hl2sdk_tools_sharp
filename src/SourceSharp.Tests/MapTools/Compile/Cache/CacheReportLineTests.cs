using SourceSharp.MapCompile;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile.Cache;

/// <summary>
/// <see cref="AllCommand.CacheReport"/>: the chain's closing cache line
/// never claims nothing was reused when the store was in use.
/// </summary>
public sealed class CacheReportLineTests
{
    [Fact]
    public void AnIncrementalRunPointsAtTheCacheLine()
    {
        string line = AllCommand.CacheReport(noCache: false, TimeSpan.FromSeconds(8.4), incremental: true);

        Assert.DoesNotContain("nothing", line, StringComparison.Ordinal);
        Assert.Contains("cache: line", line, StringComparison.Ordinal);
        Assert.Contains("total 8.4 s", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithoutIncrementalSaysHowToTurnItOn()
    {
        string line = AllCommand.CacheReport(noCache: false, TimeSpan.FromSeconds(1), incremental: false);

        Assert.Contains("-incremental", line, StringComparison.Ordinal);
        Assert.Contains("nothing reused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCacheSaysItWasOff()
    {
        Assert.Contains("off (-nocache)", AllCommand.CacheReport(noCache: true, TimeSpan.FromSeconds(1)), StringComparison.Ordinal);
    }
}
