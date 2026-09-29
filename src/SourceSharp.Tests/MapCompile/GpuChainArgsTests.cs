//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>
/// <c>ssmap all</c>'s <c>-gpu_depth</c>: taken as a chain option or in the
/// <c>--vrad</c> section (the section wins), refused outside its bound, and
/// handed to the compile request, which hands it to vrad.
/// </summary>
public sealed class GpuChainArgsTests
{
    [Fact]
    public void AChainDepthIsParsedAndAVradSectionDepthWins()
    {
        Assert.Equal(6, AllCommand.Parse(["m.vmf", "-gpu_depth", "6"]).GpuPipelineDepth);
        Assert.Equal(2, AllCommand.Parse(["m.vmf", "-gpu_depth", "6", "--vrad", "-gpu_depth", "2"]).GpuPipelineDepth);
        Assert.Null(AllCommand.Parse(["m.vmf"]).GpuPipelineDepth);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("deep")]
    public void AChainDepthOutsideItsBoundIsAnError(string value)
    {
        AllArgs parsed = AllCommand.Parse(["m.vmf", "-gpu_depth", value]);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Diagnostics, d => d.Message.Contains("-gpu_depth", StringComparison.Ordinal));
        Assert.Null(parsed.GpuPipelineDepth);
    }

    private static CompileRequest Request() => new()
    {
        Source = MapSource.FromVmf(new InMemoryFileSystem(), VPath.Create("maps/m.vmf")),
        Content = new ContentFileSystem([]),
    };

    [Fact]
    public async Task TheDepthReachesTheCompileRequest()
    {
        AllArgs parsed = AllCommand.Parse(["m.vmf", "-gpu_depth", "7"]);
        CompileRequest request = await AllCommand.WithBackendsAsync(
            Request(), parsed, "/maps", "m", presetName: null);

        Assert.Equal(7, request.GpuPipelineDepth);
        Assert.Null(request.TracerFactory);

        CompileRequest none = await AllCommand.WithBackendsAsync(
            Request(), AllCommand.Parse(["m.vmf"]), "/maps", "m", presetName: null);
        Assert.Equal(0, none.GpuPipelineDepth);
    }
}
