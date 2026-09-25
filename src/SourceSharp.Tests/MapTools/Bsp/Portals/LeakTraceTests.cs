using System.Collections.Immutable;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Portals;

/// <summary>The leak line (<c>leakfile.cpp</c>).</summary>
public class LeakTraceTests
{
    [Fact]
    public void ASealedMapHasNoLeakToTrace()
    {
        PortalFixture f = PortalFixture.SealedRoom();
        f.Add("light", new Vec3(0f, 0f, 8f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);

        Assert.Null(LeakTrace.Trace(f.Tree, f.Arena, f.Entities));
    }

    [Fact]
    public void ALeakedMapIsMarkedOnTheTree()
    {
        PortalFixture f = Leaked();

        LeakTrace.Trace(f.Tree, f.Arena, f.Entities);

        Assert.True(f.Tree.Leaked);
    }

    [Fact]
    public void TheLeakPathIsOnePointPerHopPlusTheEntity()
    {
        PortalFixture f = Leaked();

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        // The outside leaf is numbered 2, so the walk takes one step and then
        // writes the occupant's origin.
        Assert.Equal(2, report.Path.Length);
    }

    /// <summary>
    /// <b>Stock side of <see cref="StockQuirk.LeakFileUnnudgedOrigin"/>.</b>
    /// </summary>
    /// <remarks>
    /// <c>FloodEntities</c> raises every origin by a unit before finding the
    /// leaf (<c>portals.cpp:765</c>), but <c>LeakFile</c> re-reads the
    /// <c>origin</c> key (<c>leakfile.cpp:82</c>), so the <c>.lin</c> ends at
    /// the point the mapper typed and not at the point the flood stood on.
    /// </remarks>
    [Fact]
    public void TheLastPointIsTheEntitysUnnudgedOriginUnderStock()
    {
        PortalFixture f = Leaked(ComplianceOptions.Stock);

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        Assert.Equal((48f, 0f, 0f), report.Path[^1]);
    }

    /// <summary>
    /// <b>Correct side, and it differs by the one unit in z the flood
    /// used.</b>
    /// </summary>
    [Fact]
    public void TheLastPointIsTheNudgedOriginTheFloodUsedUnderCorrect()
    {
        PortalFixture f = Leaked(ComplianceOptions.Correct);

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        Assert.Equal((48f, 0f, 1f), report.Path[^1]);
    }

    /// <summary>
    /// The default is Correct, so a compile that expresses no opinion draws the
    /// line from the end the flood actually used.
    /// </summary>
    [Fact]
    public void TheDefaultLeakFileUsesTheFloodsOwnOrigin()
    {
        PortalFixture f = Leaked();

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        Assert.Equal((48f, 0f, 1f), report.Path[^1]);
    }

    [Fact]
    public void TheFirstPointIsOnTheBoundaryOfTheLeafThatGotOut()
    {
        PortalFixture f = Leaked();

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;
        (float X, float Y, float Z) first = report.Path[0];

        // The hop is through one of the head node's box faces, all of which
        // are 72 units out on one axis.
        Assert.True(
            MathF.Abs(MathF.Abs(first.X) - 72f) < 0.01f
            || MathF.Abs(MathF.Abs(first.Y) - 72f) < 0.01f
            || MathF.Abs(MathF.Abs(first.Z) - 72f) < 0.01f,
            $"({first.X} {first.Y} {first.Z}) is not on the padded box");
    }

    [Fact]
    public void TheReportNamesTheClassnameOfTheEntityThatGotOut()
    {
        PortalFixture f = Leaked();

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        Assert.Equal("light", report.ClassName);
    }

    [Fact]
    public void TheReportCarriesTheEntitysIndexInTheMapsEntityList()
    {
        PortalFixture f = Leaked();

        LeakReport report = LeakTrace.Trace(f.Tree, f.Arena, f.Entities)!;

        Assert.Equal(1, report.EntityId);
    }

    [Fact]
    public void TheLineFileIsThreeCoordinatesAtSixDecimalPlacesPerLine()
    {
        LeakReport report = new(0, "light", [(136f, 136f, 288f), (0f, 0f, 768f)]);

        string text = Encoding.Latin1.GetString(LeakTrace.Write(report, PortalLineEnding.Lf));

        Assert.Equal("136.000000 136.000000 288.000000\n0.000000 0.000000 768.000000\n", text);
    }

    [Fact]
    public void TheLineFileCanBeWrittenWithWindowsLineEndings()
    {
        LeakReport report = new(0, "light", [(1f, 2f, 3f)]);

        string text = Encoding.Latin1.GetString(LeakTrace.Write(report, PortalLineEnding.CrLf));

        Assert.Equal("1.000000 2.000000 3.000000\r\n", text);
    }

    [Fact]
    public void AFractionalCoordinateKeepsTheFloatsOwnSixDigitsAndNotTheShortestDecimal()
    {
        // The value goes through C's varargs to a double, so 0.1f prints the
        // digits of 0.100000001490116119384765625.
        LeakReport report = new(0, "light", [(0.1f, -0.25f, 1234.5678f)]);

        string text = Encoding.Latin1.GetString(LeakTrace.Write(report, PortalLineEnding.Lf));

        Assert.Equal("0.100000 -0.250000 1234.567749\n", text);
    }

    [Fact]
    public void TheDiagnosticNamesTheEntityAndWhereItWas()
    {
        LeakReport report = new(4, "info_player_start", [(1f, 2f, 3f), (10f, 20f, 30f)]);

        CompileDiagnostic diagnostic = LeakTrace.Diagnose(report);

        Assert.Equal(LeakTrace.MapLeaked, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Entity info_player_start (10.00 20.00 30.00) leaked!", diagnostic.Message);
        Assert.Equal(4, diagnostic.Location.EntityId);
    }

    private static PortalFixture Leaked(ComplianceOptions? compliance = null)
    {
        PortalFixture f = PortalFixture.SealedRoom(compliance);
        f.BeyondXHigh.Contents = 0;
        f.Add("light", new Vec3(48f, 0f, 0f));
        f.Portalise();
        EntityFlood.FloodEntities(f.Tree, f.Planes, f.Entities);
        return f;
    }
}
