//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Rad;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// The whole chain releases the tracer its <see cref="CompileRequest.TracerFactory"/>
/// offered exactly once, sequential or overlapped, lit or failed, including
/// the overlapped chain whose vvis fails after vrad's load has already built
/// the tracer and nobody will light the preparation holding it.
/// </summary>
public sealed class CompileTracerOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompiledMapReleasesTheOfferedTracerOnce(bool overlap)
    {
        CountingGpuTracerFactory factory = new();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());

        CompileResult result = await MapCompiler.CompileAsync(
            Request(files, content) with { Overlap = overlap, TracerFactory = factory }, null);

        Assert.True(result.Succeeded);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChainFailingInVradReleasesTheOfferedTracerOnce(bool overlap)
    {
        CountingGpuTracerFactory factory = new();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InvalidDataException planted = new("planted failure");

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => MapCompiler.CompileAsync(
            Request(files, content) with { Overlap = overlap, TracerFactory = factory },
            new ActAt(p => p.Stage == Vrad.FacelightsStage && p.Done == 1, () => throw planted)));

        Assert.Same(planted, thrown);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task AnOverlappedChainFailingBetweenVvisAndVradReleasesThePreparedTracerOnce()
    {
        // The chain's "vvis done" report comes with the preparation in hand
        // and before vrad takes it.
        CountingGpuTracerFactory factory = new();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InvalidDataException planted = new("planted failure");

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => MapCompiler.CompileAsync(
            Request(files, content) with { Overlap = true, TracerFactory = factory },
            new ActAt(p => p.Stage == MapCompiler.ChainStage && p.Done == 2, () => throw planted)));

        Assert.Same(planted, thrown);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }

    [Fact]
    public async Task AnOverlappedChainWhoseVvisFailsAfterVradsLoadReleasesTheTracerOnce()
    {
        // vvis's tail fails only once the load running beside it has built
        // its tracer: the preparation is complete and will never be lit.
        CountingGpuTracerFactory factory = new();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        InvalidDataException planted = new("planted vvis failure");

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => MapCompiler.CompileAsync(
            Request(files, content) with { Overlap = true, TracerFactory = factory },
            new ActAt(
                p => p.Stage == Vvis.ClusterMergeStage,
                () =>
                {
                    Assert.True(factory.FirstOffer.Wait(TimeSpan.FromMinutes(1)), "vrad's load never offered a tracer");
                    throw planted;
                })));

        Assert.Same(planted, thrown);
        Assert.Equal(1, Assert.Single(factory.Offered).Disposals);
    }
}
