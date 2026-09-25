using System.Globalization;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// What the parser does with a command line it cannot honour, and what it does
/// on a machine whose culture writes numbers differently.
/// </summary>
/// <remarks>
/// The reference tool answers every one of these with an error message and a
/// hard process exit, which is why the plan needed one process per compile.
/// None of these
/// facts may ever be allowed to pass by throwing.
/// </remarks>
public class StockArgsDiagnosticTests
{
    private const string Map = "maps/testmap.vmf";

    /// <summary>
    /// A culture whose decimal separator is a comma, so that an accidental
    /// <c>float.Parse(s)</c> would read "2.5" as 25 and "2,5" as 2.5.
    /// </summary>
    private static CultureInfo CommaDecimal => new("de-DE");

    private static T InCulture<T>(CultureInfo culture, Func<T> body)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheCommaDecimalCultureReallyWritesNumbersWithACommaHere()
    {
        // The three invariance facts below are only worth anything if this
        // culture behaves as advertised on this machine. Checked, not assumed.
        string written = InCulture(CommaDecimal, () => 2.5f.ToString(CultureInfo.CurrentCulture));

        Assert.Equal("2,5", written);
    }

    [Fact]
    public void AVbspFloatParsesTheSameUnderACommaDecimalCulture()
    {
        VbspOptions invariant = StockArgs.ParseVbsp(["-micro", "2.5", Map]).Options;
        VbspOptions comma = InCulture(CommaDecimal, () => StockArgs.ParseVbsp(["-micro", "2.5", Map]).Options);

        Assert.Equal(2.5f, comma.MicroVolume);
        Assert.Equal(invariant, comma);
    }

    [Fact]
    public void AVradFloatParsesTheSameUnderACommaDecimalCulture()
    {
        VradOptions invariant = StockArgs.ParseVrad(["-chop", "3.5", "maps/t.bsp"]).Options;
        VradOptions comma = InCulture(
            CommaDecimal,
            () => StockArgs.ParseVrad(["-chop", "3.5", "maps/t.bsp"]).Options);

        Assert.Equal(3.5f, comma.MinChop);
        Assert.Equal(invariant, comma);
    }

    [Fact]
    public void AVvisFloatParsesTheSameUnderACommaDecimalCulture()
    {
        VvisOptions comma = InCulture(
            CommaDecimal,
            () => StockArgs.ParseVvis(["-radius_override", "1024.5", "maps/t.bsp"]).Options);

        Assert.Equal(1024.5f, comma.RadiusOverride);
    }

    [Fact]
    public void ACommaDecimalValueIsRejectedEvenUnderACommaDecimalCulture()
    {
        // The other half of invariance: the same command line means the same
        // thing everywhere, so a locale-shaped number is not quietly accepted
        // on the one machine whose locale would take it.
        StockArgsResult<VbspOptions> result =
            InCulture(CommaDecimal, () => StockArgs.ParseVbsp(["-micro", "2,5", Map]));

        Assert.Equal(StockArgsCodes.MalformedValue, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AnUnknownVbspFlagIsADiagnosticAndNotAnException()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-nosuchflag", Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.UnknownOption, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void AnUnknownFlagIsNamedInItsMessage()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-nosuchflag", Map]);

        Assert.Contains("-nosuchflag", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVvisFlagIsADiagnosticAndNotAnException()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis(["-nosuchflag", "maps/t.bsp"]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AnUnknownVradFlagIsADiagnosticAndNotAnException()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-nosuchflag", "maps/t.bsp"]);

        Assert.Equal(StockArgsCodes.UnknownOption, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AnUnknownFlagLeavesEveryOptionAtItsDefault()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-nosuchflag", Map]);

        Assert.Equal(VbspOptions.Default, result.Options);
    }

    [Fact]
    public void ParsingContinuesPastAnUnknownFlag()
    {
        // Stock abandons the rest of the line the moment it hits an unknown
        // flag, so the rest is never read.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-nosuchflag", "-onlyents", Map]);

        Assert.True(result.Options.OnlyEnts);
    }

    [Fact]
    public void ThreeBadFlagsProduceThreeDiagnostics()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-one", "-two", "-three", Map]);

        Assert.Equal(3, result.Diagnostics.Count);
    }

    [Fact]
    public void AMalformedNumericArgumentNamesItsFlag()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-micro", "banana", Map]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, diagnostic.Code);
        Assert.Contains("-micro", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedNumericArgumentQuotesWhatItWasGiven()
    {
        // The reference tool's numeric conversion would silently return 0 here,
        // which is a quiet request
        // for a microvolume of zero.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-micro", "banana", Map]);

        Assert.Contains("banana", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedNumericArgumentLeavesTheOptionAtItsDefault()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-micro", "banana", Map]);

        Assert.Equal(VbspOptions.Default.MicroVolume, result.Options.MicroVolume);
    }

    [Fact]
    public void AMalformedIntegerNamesItsFlag()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-bounce", "lots", "maps/t.bsp"]);

        CompileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(StockArgsCodes.MalformedValue, diagnostic.Code);
        Assert.Contains("-bounce", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFractionalValueForAnIntegerFlagIsMalformed()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-bounce", "2.5", "maps/t.bsp"]);

        Assert.Equal(StockArgsCodes.MalformedValue, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void AValueFlagAtTheEndOfTheLineIsAMissingValueError()
    {
        // Stock reads past the end of the argument array here
        // rather than checking.
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([Map, "-micro"]);

        Assert.Contains(result.Diagnostics, d => d.Code == StockArgsCodes.MissingValue);
    }

    [Fact]
    public void AMissingValueDoesNotThrow()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([Map, "-embed"]);

        Assert.True(result.HasErrors);
        Assert.Equal(Map, result.MapPath);
    }

    [Fact]
    public void NoMapPathIsAnError()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-onlyents"]);

        Assert.Equal(StockArgsCodes.MissingMapPath, Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.MapPath);
    }

    [Fact]
    public void AnEmptyCommandLineIsAnErrorAndNotAnException()
    {
        StockArgsResult<VvisOptions> result = StockArgs.ParseVvis([]);

        Assert.Equal(StockArgsCodes.MissingMapPath, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(VvisOptions.Default, result.Options);
    }

    [Fact]
    public void TwoMapPathsAreAnErrorAndTheFirstIsKept()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["first.vmf", "second.vmf"]);

        Assert.Equal(StockArgsCodes.TooManyMapPaths, Assert.Single(result.Diagnostics).Code);
        Assert.Equal("first.vmf", result.MapPath);
    }

    [Fact]
    public void HasErrorsIsFalseWhenTheOnlyDiagnosticIsAWarning()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-low", Map]);

        Assert.NotEmpty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void HasErrorsIsFalseOnACleanLine()
    {
        Assert.False(StockArgs.ParseVbsp([Map]).HasErrors);
    }

    [Fact]
    public void ANullArgumentListIsACallerBugAndThrows()
    {
        // The one thing that is not a diagnostic: a bad map produces
        // diagnostics, a bug throws.
        Assert.Throws<ArgumentNullException>(() => StockArgs.ParseVbsp(null!));
    }

    [Fact]
    public void ANullArgumentListThrowsFromEveryTool()
    {
        Assert.Throws<ArgumentNullException>(() => StockArgs.ParseVvis(null!));
        Assert.Throws<ArgumentNullException>(() => StockArgs.ParseVrad(null!));
    }

    [Fact]
    public void EveryDiagnosticCodeIsDistinct()
    {
        // Codes are a stable host-facing contract; two meanings behind one
        // code would make a host's filter wrong forever.
        string[] codes =
        [
            StockArgsCodes.UnknownOption,
            StockArgsCodes.MissingValue,
            StockArgsCodes.MalformedValue,
            StockArgsCodes.ValueOutOfRange,
            StockArgsCodes.MissingMapPath,
            StockArgsCodes.TooManyMapPaths,
            StockArgsCodes.DroppedOption,
            StockArgsCodes.ConflictingOptions,
        ];

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDiagnosticNamesTheToolThatProducedIt()
    {
        StockArgsResult<VradOptions> result = StockArgs.ParseVrad(["-nosuchflag", "maps/t.bsp"]);

        Assert.StartsWith("vrad:", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }
}
