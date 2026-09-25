using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// <see cref="VbspOptions.Cooker"/> and the <c>-cooker native|vphysics|managed|none</c> and <c>-vphysics</c> flags (Phase 8a).
/// </summary>
/// <remarks>
/// The default is native on purpose: the integrator decides whether the managed cooker becomes the
/// default from its gate numbers, so changing it must break <see cref="DefaultIsNative"/>.
/// </remarks>
public class CookerOptionTests
{
    private const string Map = "maps/testmap.bsp";

    [Fact]
    public void DefaultIsManaged()
    {
        // User ruling Q19 (2026-09-22): managed by default, native on request.
        Assert.Equal(CollisionCookerKind.Managed, VbspOptions.Default.Cooker);
    }

    [Fact]
    public void OmittingTheFlagKeepsTheManagedDefault()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([Map]);

        Assert.Equal(CollisionCookerKind.Managed, result.Options.Cooker);
    }

    [Fact]
    public void ManagedSelectsTheManagedCooker()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-cooker", "managed", Map]);

        Assert.Equal(CollisionCookerKind.Managed, result.Options.Cooker);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ExplicitNativeIsAcceptedAfterManaged()
    {
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-cooker", "managed", "-cooker", "native", Map]);

        Assert.Equal(CollisionCookerKind.Native, result.Options.Cooker);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ValueIsCaseInsensitive()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-COOKER", "Managed", Map]);

        Assert.Equal(CollisionCookerKind.Managed, result.Options.Cooker);
    }

    [Fact]
    public void AnUnrecognisedValueIsMalformed()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-cooker", "manged", Map]);

        Assert.Contains(
            result.Diagnostics,
            d => d.Code == StockArgsCodes.MalformedValue && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnUnrecognisedValueLeavesTheDefault()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-cooker", "manged", Map]);

        Assert.Equal(VbspOptions.Default.Cooker, result.Options.Cooker);
    }

    [Fact]
    public void TheCookerIsIndependentOfCompliance()
    {
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-compliance", "stock", "-cooker", "managed", Map]);

        Assert.Equal(CompliancePolicy.Stock, result.Options.Compliance.Policy);
        Assert.Equal(CollisionCookerKind.Managed, result.Options.Cooker);
    }

    [Fact]
    public void VPhysicsIsAnAliasOfNative()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-cooker", "managed", "-cooker", "vphysics", Map]);

        Assert.Equal(CollisionCookerKind.Native, result.Options.Cooker);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void NoneSelectsNoCooker()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-COOKER", "None", Map]);

        Assert.Equal(CollisionCookerKind.None, result.Options.Cooker);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ACookerWithNoValueIsAnError()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp([Map, "-cooker"]);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void TheLibrarySelectorIsKept()
    {
        StockArgsResult<VbspOptions> result = StockArgs.ParseVbsp(["-vphysics", "team-fortress-2", Map]);

        Assert.Equal("team-fortress-2", result.Options.VPhysicsLibrary);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void TheLibrarySelectorDefaultsToNull()
    {
        Assert.Null(VbspOptions.Default.VPhysicsLibrary);
    }

    [Fact]
    public void TheOptionsLeaveTheMapPathAlone()
    {
        StockArgsResult<VbspOptions> result =
            StockArgs.ParseVbsp(["-cooker", "none", "-vphysics", "team-fortress-2", "-v", Map]);

        Assert.False(result.HasErrors);
        Assert.True(result.Options.Verbose);
    }
}
