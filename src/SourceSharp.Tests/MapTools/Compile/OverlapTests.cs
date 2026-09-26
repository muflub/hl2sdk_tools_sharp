//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Vis;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// <see cref="CompileRequest.Overlap"/>: the chain that starts vvis's flow at
/// vbsp's portal signal and vrad's load beside vvis's tail must write exactly
/// what the sequential chain writes, and the stage splits it is built from must
/// each equal the whole stage.
/// </summary>
public sealed class OverlapTests
{
    public static TheoryData<string, int> Rooms() => new()
    {
        { "room", 1 }, { "room", 2 }, { "room", 4 },
        { "slanted", 1 }, { "slanted", 2 }, { "slanted", 4 },
    };

    [Theory]
    [MemberData(nameof(Rooms))]
    public async Task OverlapWritesWhatTheSequentialChainWrites(string room, int degree)
    {
        VmfDocument Map() => room == "room" ? Room() : SlantedRoom();
        await AssertSameAsync(Map, request => request with { Parallel = new CompileParallelism { MaxDegree = degree } });
    }

    [Fact]
    public async Task OverlapWithBouncesWritesWhatTheSequentialChainWrites() =>
        await AssertSameAsync(() => Room(), request => request with { Vrad = VradOptions.Default with { Bounces = 2 } });

    [Fact]
    public async Task OverlapOnALeakedMapWritesWhatTheSequentialChainWrites() =>
        await AssertSameAsync(() => Room(sealedRoom: false), request => request);

    [Fact]
    public async Task OverlapUnderLeakTestWritesWhatTheSequentialChainWrites() =>
        await AssertSameAsync(
            () => Room(sealedRoom: false),
            request => request with { Vbsp = VbspOptions.Default with { LeakTest = true } });

    [Theory]
    [InlineData("200")]
    [InlineData("0")]
    public async Task OverlapOnAFoggedMapWritesWhatTheSequentialChainWrites(string farZ) =>
        await AssertSameAsync(() => Fogged(farZ), request => request);

    [Fact]
    public async Task OverlapUnderARadiusOverrideWritesWhatTheSequentialChainWrites() =>
        await AssertSameAsync(
            () => Room(),
            request => request with { Vvis = VvisOptions.Default with { RadiusOverride = 150f } });

    [Fact]
    public async Task OverlapUnderLuxelDensityBelowOneWritesWhatTheSequentialChainWrites() =>
        // The early load is skipped here (it would edit TEXINFO under vvis).
        await AssertSameAsync(() => Room(), request => request with { Vrad = request.Vrad with { LuxelDensity = 0.5f } });

    [Fact]
    public async Task OverlapUnderATraceWritesWhatTheSequentialChainWrites() =>
        // -trace has no split flow; the chain falls back to the whole stage.
        await AssertSameAsync(() => Room(), request => request with { Vvis = VvisOptions.Default with { Trace = (0, 1) } });

    [Fact]
    public async Task ACancelledOverlappedCompileThrowsAndLeavesNoThreads()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        using CompilePool pool = new(2);
        using CancellationTokenSource cancel = new();
        CompileRequest request = Request(files, content) with
        {
            Overlap = true,
            Parallel = new CompileParallelism { MaxDegree = 2, Pool = pool },
        };

        // Cancel as soon as vbsp's portals are handed on: the flow is running.
        CancelAt progress = new(cancel, p => p.Stage == MapCompiler.ChainStage && p.Done == 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapCompiler.CompileAsync(request, progress, cancel.Token));

        pool.Dispose();
        Assert.Equal(0, pool.LiveThreadCount);
    }

    [Fact]
    public async Task AChainRunsOnOnePoolOfItsDegree()
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        using CompilePool pool = new(3);
        CompileRequest request = Request(files, content, CompileOutput.InMemory) with
        {
            Overlap = true,
            Parallel = new CompileParallelism { MaxDegree = 3, Pool = pool },
        };

        CompileResult result = await MapCompiler.CompileAsync(request, null);

        Assert.True(result.Succeeded);
        Assert.Equal(3, pool.LiveThreadCount);
    }

    [Fact]
    public async Task SplitVvisMakesWhatWholeVvisMakes()
    {
        (BspData whole, PortalSet wholePortals) = await VbspAsync(Room());
        (BspData split, PortalSet splitPortals) = await VbspAsync(Room());
        VisContext context = new() { Parallelism = new CompileParallelism { MaxDegree = 2 } };

        VisResult a = await Vvis.ComputeAsync(whole, wholePortals, context);
        VisFlow flow = await Vvis.FlowAsync(splitPortals, default, context);
        VisResult b = await Vvis.FinishAsync(split, flow, context);

        Assert.Equal(a.VisDataSize, b.VisDataSize);
        Assert.Equal(await BytesAsync(whole), await BytesAsync(split));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWrongRadiusIsFlowedAgain(bool mapHasFog)
    {
        // The flow's radius disagrees with the map's either way round; the
        // finish must notice and still make whole vvis's bytes.
        VmfDocument Map() => mapHasFog ? Fogged("150") : Room();
        VisRadius wrong = mapHasFog ? default : new VisRadius(true, 150.0 * 150.0);
        (BspData whole, PortalSet wholePortals) = await VbspAsync(Map());
        (BspData split, PortalSet splitPortals) = await VbspAsync(Map());
        VisContext context = VisContext.Default with { Parallelism = CompileParallelism.Serial };

        _ = await Vvis.ComputeAsync(whole, wholePortals, context);
        VisFlow flow = await Vvis.FlowAsync(splitPortals, wrong, context);
        _ = await Vvis.FinishAsync(split, flow, context);

        Assert.Equal(await BytesAsync(whole), await BytesAsync(split));
    }

    [Fact]
    public async Task TheFlowRefusesATrace()
    {
        (_, PortalSet portals) = await VbspAsync(Room());
        VisContext context = new() { Options = VvisOptions.Default with { Trace = (0, 1) } };

        await Assert.ThrowsAsync<ArgumentException>(() => Vvis.FlowAsync(portals, default, context));
    }

    [Theory]
    [InlineData(null, null, false, 0.0)]
    [InlineData("300", null, true, 90000.0)]
    [InlineData("0", null, false, 0.0)]
    [InlineData("-5", null, false, 0.0)]
    [InlineData("junk", null, false, 0.0)]
    [InlineData("300", 40f, true, 1600.0)]
    public void TheRadiusReadsTheFirstFogControllerOrTheOverride(string? farZ, float? radiusOverride, bool use, double squared)
    {
        List<(string, string?)> entities = [("worldspawn", "999"), ("light", null)];
        if (farZ is not null)
        {
            entities.Add(("env_fog_controller", farZ));
            entities.Add(("ENV_FOG_CONTROLLER", "7"));
        }

        VisRadius radius = VisRadius.FromEntities(entities, VvisOptions.Default with { RadiusOverride = radiusOverride });

        Assert.Equal(new VisRadius(use, squared), radius);
    }

    [Fact]
    public async Task SplitVradMakesWhatWholeVradMakes()
    {
        (BspData whole, _) = await VbspAsync(Room());
        (BspData split, _) = await VbspAsync(Room());
        VradContext context = await RadContextAsync();

        RadResult a = await Vrad.LightAsync(whole, context);
        VradPreparation prepared = await Vrad.PrepareAsync(split, context);
        RadResult b = await Vrad.LightAsync(split, prepared, context);

        Assert.Equal(await BytesAsync(whole), await BytesAsync(split));
        Assert.Equal(a.Diagnostics.Select(d => d.Code + d.Message), b.Diagnostics.Select(d => d.Code + d.Message));
    }

    [Fact]
    public async Task APreparationLightsOnce()
    {
        (BspData bsp, _) = await VbspAsync(Room());
        VradContext context = await RadContextAsync();
        VradPreparation prepared = await Vrad.PrepareAsync(bsp, context);
        _ = await Vrad.LightAsync(bsp, prepared, context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Vrad.LightAsync(bsp, prepared, context));
    }

    [Fact]
    public async Task VbspSignalsItsPortalFileOnceAndItIsTheResults()
    {
        List<PortalFile?> seen = [];
        VbspResult result = await VbspWithSignalAsync(Room(), VbspOptions.Default, seen);

        PortalFile? only = Assert.Single(seen);
        Assert.NotNull(only);
        Assert.Same(result.Portals, only);
    }

    [Fact]
    public async Task ALeakedWorldSignalsNoPortalFile()
    {
        List<PortalFile?> seen = [];
        _ = await VbspWithSignalAsync(Room(sealedRoom: false), VbspOptions.Default, seen);

        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public async Task LeakTestStopsBeforeTheSignal()
    {
        List<PortalFile?> seen = [];
        _ = await VbspWithSignalAsync(Room(sealedRoom: false), VbspOptions.Default with { LeakTest = true }, seen);

        Assert.Empty(seen);
    }

    // Runs the chain twice, sequential and overlapped, and compares every
    // file it wrote: bytes for the products, lines for the log less its timings.
    private static async Task AssertSameAsync(Func<VmfDocument> map, Func<CompileRequest, CompileRequest> shape)
    {
        (InMemoryFileSystem a, IContentFileSystem ca) = await DiskAsync(map());
        (InMemoryFileSystem b, IContentFileSystem cb) = await DiskAsync(map());
        CompileResult sequential = await MapCompiler.CompileAsync(shape(Request(a, ca)) with { Overlap = false }, null);
        CompileResult overlapped = await MapCompiler.CompileAsync(shape(Request(b, cb)) with { Overlap = true }, null);

        Assert.Equal(sequential.Succeeded, overlapped.Succeeded);
        List<string> names = await ListAsync(a);
        Assert.Equal(names, await ListAsync(b));
        foreach (string name in names)
        {
            string path = $"{MapDirectory}/{name}";
            if (name.EndsWith(".log", StringComparison.Ordinal))
            {
                Assert.Equal(await LogAsync(a, path), await LogAsync(b, path));
            }
            else
            {
                byte[] left = await ReadAsync(a, path);
                byte[] right = await ReadAsync(b, path);
                Assert.True(left.AsSpan().SequenceEqual(right), name);
            }
        }
    }

    private static async Task<List<string>> LogAsync(InMemoryFileSystem files, string path) =>
        [.. Encoding.UTF8.GetString(await ReadAsync(files, path))
            .Split('\n')
            .Where(line => !line.Contains("seconds elapsed", StringComparison.Ordinal))];

    private static VmfDocument Fogged(string farZ)
    {
        VmfDocument document = Room();
        VmfChunk fog = Entity(document, "env_fog_controller", "64 64 64");
        fog.AddKey("farz", farZ);
        return document;
    }

    private static async Task<(BspData Bsp, PortalSet Portals)> VbspAsync(VmfDocument document)
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(document);
        VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, context);

        // What the chain hands vvis: the .prt's text, read back.
        PortalFile read = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
        return (vbsp.Bsp!, PortalSet.FromPortalFile(read));
    }

    private static async Task<VbspResult> VbspWithSignalAsync(VmfDocument document, VbspOptions options, List<PortalFile?> seen)
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(document);
        VbspContext context = new(options, content)
        {
            MapBase = "room",
            PortalFileReady = portals =>
            {
                lock (seen)
                {
                    seen.Add(portals);
                }
            },
        };
        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
        return await Vbsp.CompileAsync(map, context);
    }

    private static async Task<VradContext> RadContextAsync()
    {
        (_, IContentFileSystem content) = await DiskAsync(Room());
        return new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
        };
    }

    // Synchronous, unlike Progress<T>: the cancel lands before the chain moves on.
    private sealed class CancelAt(CancellationTokenSource cancel, Func<CompileProgress, bool> when) : IProgress<CompileProgress>
    {
        public void Report(CompileProgress value)
        {
            if (when(value))
            {
                cancel.Cancel();
            }
        }
    }
}
