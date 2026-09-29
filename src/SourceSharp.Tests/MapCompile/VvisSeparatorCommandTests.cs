//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap vvis -separator</c>: the flag reaches the flow, the output file is
/// the same bytes on either path, and the command says which path ran.
/// </summary>
public sealed class VvisSeparatorCommandTests
{
    [Theory]
    [InlineData(VisSeparatorPath.Auto, VisSeparatorPath.Vector512, "separator: 512 (auto)")]
    [InlineData(VisSeparatorPath.Auto, VisSeparatorPath.Vector256, "separator: 256 (auto)")]
    [InlineData(VisSeparatorPath.Vector512, VisSeparatorPath.Vector512, "separator: 512")]
    [InlineData(VisSeparatorPath.Vector256, VisSeparatorPath.Vector256, "separator: 256")]
    public void TheLineNamesThePathAndWhetherTheCpuChoseIt(VisSeparatorPath requested, VisSeparatorPath ran, string line) =>
        Assert.Equal(line, VvisCommand.SeparatorLine(requested, ran));

    [Fact]
    public async Task EitherPathWritesTheSameMapAndSaysWhichRan()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ssmap-separator"));
        string bspPath = Path.Combine(root, "grid.bsp");
        string prtPath = Path.Combine(root, "grid.prt");

        using MemoryStream bspBytes = new();
        await BspFile.SaveAsync(VisFixture.Map(100), bspBytes, CancellationToken.None);
        using MemoryStream prtBytes = new();
        await VvisTightenTests.GridFile().WriteAsync(prtBytes, cancellationToken: CancellationToken.None);

        async Task<(int Exit, string Output, byte[] Bsp)> RunCommandAsync(params string[] flags)
        {
            InMemoryFileSystem files = new InMemoryFileSystem()
                .AddFile(bspPath, bspBytes.ToArray())
                .AddFile(prtPath, prtBytes.ToArray());
            using StringWriter output = new();
            int exit = await VvisCommand.RunAsync(files, [.. flags, "-threads", "2", bspPath], output);
            return (exit, output.ToString(), files.GetBytes(VPath.Create(bspPath))!);
        }

        (int narrowExit, string narrowOutput, byte[] narrow) = await RunCommandAsync("-separator", "256");
        (int wideExit, string wideOutput, byte[] wide) = await RunCommandAsync("-separator", "512");
        (int autoExit, string autoOutput, byte[] auto) = await RunCommandAsync();

        Assert.Equal(Program.ExitSuccess, narrowExit);
        Assert.Equal(Program.ExitSuccess, wideExit);
        Assert.Equal(Program.ExitSuccess, autoExit);
        Assert.Equal(narrow, wide);
        Assert.Equal(narrow, auto);
        Assert.Contains("separator: 256\n", narrowOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("separator: 512\n", wideOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains(
            VvisCommand.SeparatorLine(VisSeparatorPath.Auto, Vvis.ResolveSeparators(new VisContext())) + "\n",
            autoOutput.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);

        (int badExit, string badOutput, _) = await RunCommandAsync("-separator", "1024");
        Assert.Equal(Program.ExitUsage, badExit);
        Assert.Contains("-separator", badOutput, StringComparison.Ordinal);
    }
}
