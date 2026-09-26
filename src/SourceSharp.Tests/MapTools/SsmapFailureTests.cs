//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Diagnostics;

using Xunit;

namespace SourceSharp.Tests.MapTools;

/// <summary>
/// <c>ssmap</c>'s top-level handler: every exception becomes a message and an
/// exit code, never an unhandled-exception crash.
/// </summary>
public class SsmapFailureTests
{
    [Fact]
    public async Task AMapCompileExceptionIsItsMessageAndExitOne()
    {
        StringWriter error = new();

        int exit = await Program.ReportFailureAsync(
            new MapCompileException("MAX_MAP_PLANES"), "vbsp", error);

        Assert.Equal(Program.ExitFailure, exit);
        Assert.Equal("ssmap: error in vbsp: MAX_MAP_PLANES" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task AMapCompileExceptionWithACodePrintsTheCodeBeforeTheMessage()
    {
        using StringWriter error = new();

        int exit = await Program.ReportFailureAsync(
            new MapCompileException("VBSP0101", "too many planes"), "vbsp", error);

        Assert.Equal(Program.ExitFailure, exit);
        Assert.Equal("ssmap: error in vbsp: VBSP0101: too many planes" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void TheCodeIsKeptOnTheException()
    {
        MapCompileException withCause = new("VBSP0102", "bad", new InvalidOperationException());

        Assert.Equal("VBSP0102", withCause.Code);
        Assert.IsType<InvalidOperationException>(withCause.InnerException);
    }

    [Fact]
    public void AMessageOnlyExceptionHasNoCode()
    {
        Assert.Null(new MapCompileException("MAX_MAP_PLANES").Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCodeIsRefused(string code)
    {
        Assert.Throws<ArgumentException>(() => new MapCompileException(code, "message"));
    }

    [Fact]
    public async Task OutOfMemoryIsSaidPlainlyAndExitsOne()
    {
        StringWriter error = new();

        int exit = await Program.ReportFailureAsync(new OutOfMemoryException(), "vrad", error);

        Assert.Equal(Program.ExitFailure, exit);
        Assert.Equal("ssmap: out of memory in vrad" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task AnythingElseIsAnInternalErrorWithItsTypeAndStackAndExitsSeventy()
    {
        StringWriter error = new();
        Exception thrown = Thrown(new InvalidOperationException("bad state"));

        int exit = await Program.ReportFailureAsync(thrown, "vvis", error);

        string[] lines = error.ToString().Split(Environment.NewLine);
        Assert.Equal(Program.ExitSoftware, exit);
        Assert.Equal("ssmap: internal error in vvis: System.InvalidOperationException: bad state", lines[0]);
        Assert.Contains(nameof(Thrown), lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoStageTheMessageNamesNone()
    {
        StringWriter error = new();

        await Program.ReportFailureAsync(new OutOfMemoryException(), null, error);

        Assert.Equal("ssmap: out of memory" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void ExitSoftwareIsSysexitsExSoftware()
    {
        //: #define EX_SOFTWARE 70 /* internal software error */
        Assert.Equal(70, Program.ExitSoftware);
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
