using SourceSharp.MapCompile;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// <c>-listcompliance</c> on the three stock parsers, and the
/// <c>ssmap compliance</c> command.
/// </summary>
public class ListComplianceTests
{
    [Fact]
    public void VbspAcceptsListComplianceWithoutAMap()
    {
        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp(["-listcompliance"]);

        Assert.True(parsed.ListCompliance);
        Assert.False(parsed.HasErrors);
    }

    [Fact]
    public void VvisAcceptsListComplianceWithoutAMap()
    {
        StockArgsResult<VvisOptions> parsed = StockArgs.ParseVvis(["-listcompliance"]);

        Assert.True(parsed.ListCompliance);
        Assert.False(parsed.HasErrors);
    }

    [Fact]
    public void VradAcceptsListComplianceWithoutAMap()
    {
        StockArgsResult<VradOptions> parsed = StockArgs.ParseVrad(["-listcompliance"]);

        Assert.True(parsed.ListCompliance);
        Assert.False(parsed.HasErrors);
    }

    [Fact]
    public void WithoutTheFlagNothingIsListedAndTheMapIsStillRequired()
    {
        StockArgsResult<VbspOptions> parsed = StockArgs.ParseVbsp([]);

        Assert.False(parsed.ListCompliance);
        Assert.True(parsed.HasErrors);
    }

    [Fact]
    public void AMapNamedBesideTheFlagIsStillRecorded()
    {
        StockArgsResult<VradOptions> parsed = StockArgs.ParseVrad(["-listcompliance", "maps/a.bsp"]);

        Assert.True(parsed.ListCompliance);
        Assert.Equal("maps/a.bsp", parsed.MapPath);
    }

    [Fact]
    public void TheFlagIsCaseInsensitiveLikeEveryOtherFlag()
    {
        Assert.True(StockArgs.ParseVvis(["-ListCompliance"]).ListCompliance);
    }

    [Fact]
    public async Task SsmapComplianceListsEveryQuirk()
    {
        StringWriter output = new();

        int exit = await Program.RunAsync(["compliance"], output);

        Assert.Equal(Program.ExitSuccess, exit);
        foreach (StockQuirk quirk in Enum.GetValues<StockQuirk>())
        {
            Assert.Contains("  " + quirk + " [", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SsmapComplianceForOneToolPrintsThatToolsListing()
    {
        StringWriter output = new();

        int exit = await Program.RunAsync(["compliance", "vrad"], output);

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Equal(ComplianceCatalogue.Format(CompileTools.Vrad), output.ToString());
    }

    [Fact]
    public async Task SsmapComplianceRefusesAnUnknownTool()
    {
        StringWriter output = new();

        int exit = await Program.RunAsync(["compliance", "vbsp", "vtex"], output);

        Assert.Equal(Program.ExitUsage, exit);
        Assert.Contains("unknown tool \"vtex\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SsmapVvisListComplianceCompilesNothingAndPrintsTheVvisListing()
    {
        StringWriter output = new();

        int exit = await Program.RunAsync(["vvis", "-listcompliance"], output);

        Assert.Equal(Program.ExitSuccess, exit);
        Assert.Equal(ComplianceCatalogue.Format(CompileTools.Vvis), output.ToString());
    }
}
