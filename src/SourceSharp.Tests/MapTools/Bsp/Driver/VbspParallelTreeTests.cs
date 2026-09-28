//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Catalog;
using SourceSharp.MapGen.Content;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.Tree;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// A whole vbsp compile with the world tree's subtrees built on several
/// threads writes the serial compile's bytes, and the compile hands the tree
/// build the threads it was given, and gives back any it made.
/// </summary>
/// <remarks>
/// The node-for-node half of the proof is <c>BrushBspTreeParallelTests</c>;
/// this is the written <c>.bsp</c> and <c>.prt</c>, which also covers the
/// second optimize pass, the brush entities and everything that reads the
/// tree afterwards. The reference is a compile at one thread with no pool,
/// which never forks. The parallel compiles lower the fork threshold to one
/// brush so that these small maps fork at every level the depth cap allows,
/// and each checks that something forked.
/// </remarks>
public sealed class VbspParallelTreeTests
{
    /// <summary>The degrees, with the high ones twice: a race shows up as a run that differs.</summary>
    private static readonly int[] DegreeRuns = [1, 2, 3, 8, 8, 32, 32];

    /// <summary>Catalogue maps compiled whole.</summary>
    public static TheoryData<string> CatalogueMaps =>
    [
        "l3_arena_144_pillars",
        "l2_detail_and_hint_in_a_corridor",
        "l2_areaportal_between_pools",
    ];

    // ---- same bytes -----------------------------------------------------------

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheSandboxCompilesToTheSerialBytesAtEveryDegree()
    {
        byte[] vmf = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!);

        await AssertEveryDegreeMatchesSerialAsync("ss_sandbox", vmf);
    }

    [Theory]
    [MemberData(nameof(CatalogueMaps))]
    public async Task ACatalogueMapCompilesToTheSerialBytesAtEveryDegree(string name)
    {
        await AssertEveryDegreeMatchesSerialAsync(name, TestMapCatalog.Named(name).WriteVmfBytes());
    }

    /// <summary>
    /// With no pool and no scheduler lent, the compile makes its own threads
    /// for the tree build, forks on them, writes the same bytes, and has
    /// disposed of them by the time it returns.
    /// </summary>
    [Fact]
    public async Task ACompileWithNoLentThreadsForksOnItsOwnPoolAndDisposesIt()
    {
        byte[] vmf = TestMapCatalog.Named("l3_arena_144_pillars").WriteVmfBytes();
        (byte[] serial, _, _) = await CompileAsync("pillars", vmf, new CompileParallelism { MaxDegree = 1 }, minBrushes: null);

        (byte[] own, int forks, VbspCompilation compilation) =
            await CompileAsync("pillars", vmf, new CompileParallelism { MaxDegree = 4 }, minBrushes: 1);

        Assert.True(forks > 0, "nothing forked");
        Assert.Equal(serial, own);
        Assert.Null(compilation.OwnPool);
    }

    // ---- which threads --------------------------------------------------------

    [Fact]
    public async Task AtOneThreadTheTreeBuildHasNoSchedulerAndMakesNoPool()
    {
        VbspCompilation compilation = await PrepareAsync(new CompileParallelism { MaxDegree = 1 });
        using CancellationTokenSource cancel = new();

        BspTreeParallelism tree = compilation.TreeParallelism(cancel.Token);

        Assert.Null(tree.Scheduler);
        Assert.Equal(0, tree.MaxForkDepth);
        Assert.Equal(cancel.Token, tree.CancellationToken);
        Assert.Null(compilation.OwnPool);
    }

    [Theory]
    [InlineData(8, 4, 4)]
    [InlineData(2, 8, 3)]
    public async Task ALentPoolIsUsedAtTheSmallerOfItsDegreeAndTheCompiles(int maxDegree, int poolDegree, int depth)
    {
        using CompilePool pool = new(poolDegree);
        VbspCompilation compilation = await PrepareAsync(new CompileParallelism { MaxDegree = maxDegree, Pool = pool });

        BspTreeParallelism tree = compilation.TreeParallelism(CancellationToken.None);

        Assert.Same(pool.Scheduler, tree.Scheduler);
        Assert.Equal(depth, tree.MaxForkDepth);
        Assert.Null(compilation.OwnPool);
    }

    [Fact]
    public async Task ALentSchedulerIsUsedWhenThereIsNoPool()
    {
        using CompilePool lender = new(2);
        VbspCompilation compilation = await PrepareAsync(
            new CompileParallelism { MaxDegree = 8, Scheduler = lender.Scheduler });

        BspTreeParallelism tree = compilation.TreeParallelism(CancellationToken.None);

        Assert.Same(lender.Scheduler, tree.Scheduler);
        Assert.Equal(BspTreeParallelism.ForkDepthFor(8), tree.MaxForkDepth);
        Assert.Null(compilation.OwnPool);
    }

    [Fact]
    public async Task WithNothingLentTheCompileMakesOnePoolOfItsDegree()
    {
        VbspCompilation compilation = await PrepareAsync(new CompileParallelism { MaxDegree = 6 }, minBrushes: 77);

        BspTreeParallelism first = compilation.TreeParallelism(CancellationToken.None);
        CompilePool pool = compilation.OwnPool!;
        BspTreeParallelism second = compilation.TreeParallelism(CancellationToken.None);

        try
        {
            Assert.Equal(6, pool.Degree);
            Assert.Same(pool.Scheduler, first.Scheduler);
            Assert.Same(pool, compilation.OwnPool);
            Assert.Same(first.Scheduler, second.Scheduler);
            Assert.Equal(77, first.MinBrushes);
        }
        finally
        {
            pool.Dispose();
        }
    }

    // ---- given back however it ends ------------------------------------------------

    [Fact]
    public async Task AFailedCompileDisposesItsOwnPool()
    {
        VbspCompilation? compilation = null;
        bool hadPool = false;
        compilation = await PrepareAsync(
            new CompileParallelism { MaxDegree = 4 },
            extension: new Failing(_ =>
            {
                hadPool = compilation!.OwnPool is not null;
                throw new InvalidDataException("extension failed");
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => compilation.RunAsync(CancellationToken.None));

        Assert.True(hadPool, "the compile never made a pool, so there was nothing to dispose");
        Assert.Null(compilation.OwnPool);
    }

    /// <summary>
    /// Cancelled as the world tree starts: the tree build sees the token at its
    /// first node and the compile unwinds, pool and all.
    /// </summary>
    [Fact]
    public async Task ACompileCancelledAsTheWorldTreeStartsStopsThereAndDisposesItsOwnPool()
    {
        using CancellationTokenSource cancel = new();
        StageCanceller progress = new("vbsp.world.tree", cancel);
        VbspCompilation compilation = await PrepareAsync(new CompileParallelism { MaxDegree = 4 }, progress: progress);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.RunAsync(cancel.Token));

        Assert.True(progress.Fired, "the world tree stage was never reached");
        Assert.DoesNotContain("vbsp.world.portals", progress.Seen);
        Assert.Null(compilation.OwnPool);
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task AssertEveryDegreeMatchesSerialAsync(string name, byte[] vmf)
    {
        (byte[] serial, _, _) = await CompileAsync(name, vmf, new CompileParallelism { MaxDegree = 1 }, minBrushes: null);

        foreach (int degree in DegreeRuns)
        {
            using CompilePool pool = new(degree);
            (byte[] parallel, int forks, _) = await CompileAsync(
                name, vmf, new CompileParallelism { MaxDegree = degree, Pool = pool }, minBrushes: 1);

            if (degree > 1)
            {
                Assert.True(forks > 0, $"{name} at degree {degree}: nothing forked");
            }

            if (!serial.AsSpan().SequenceEqual(parallel))
            {
                Assert.Fail(
                    $"{name} at degree {degree}: {Convert.ToHexString(SHA256.HashData(parallel))}, "
                    + $"serial {Convert.ToHexString(SHA256.HashData(serial))}");
            }
        }
    }

    private static async Task<(byte[] Bytes, int Forks, VbspCompilation Compilation)> CompileAsync(
        string name, byte[] vmf, CompileParallelism parallelism, int? minBrushes)
    {
        (VbspContext context, MapFile map) = await LoadAsync(name, vmf, parallelism, progress: null);
        if (minBrushes is int min)
        {
            context.TreeForkMinBrushes = min;
        }

        VbspCompilation compilation = new(map, context, Vbsp.DefaultExtensions(context));
        VbspResult result = await compilation.RunAsync(CancellationToken.None);

        using MemoryStream stream = new();
        await BspFile.SaveAsync(result.Bsp!, stream, BspWriteMode.Canonical, CancellationToken.None);
        byte[] portals = result.Portals?.ToBytes(PortalLineEnding.Lf) ?? [];

        return ([.. stream.ToArray(), .. portals], compilation.Build!.ForkedSubtrees, compilation);
    }

    private static async Task<VbspCompilation> PrepareAsync(
        CompileParallelism parallelism,
        int? minBrushes = null,
        IVbspExtension? extension = null,
        IProgress<CompileProgress>? progress = null)
    {
        (VbspContext context, MapFile map) = await LoadAsync(
            "pillars", TestMapCatalog.Named("l3_arena_144_pillars").WriteVmfBytes(), parallelism, progress);
        if (minBrushes is int min)
        {
            context.TreeForkMinBrushes = min;
        }

        return new VbspCompilation(map, context, extension is null ? [] : [extension]);
    }

    private static async Task<(VbspContext Context, MapFile Map)> LoadAsync(
        string name, byte[] vmf, CompileParallelism parallelism, IProgress<CompileProgress>? progress)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in SyntheticContent.Build())
        {
            disk.AddFile(path, bytes);
        }

        disk.AddFile($"maps/{name}.vmf", vmf);
        ContentFileSystem content = new([await DirectoryContentMount.MountAsync(disk, VPath.Empty)]);
        VbspContext context = new(SourceSharp.MapTools.Options.VbspOptions.Default, content)
        {
            MapBase = name,
            Parallelism = parallelism,
            Progress = progress,
        };

        MapFile map = await new MapFileReader(context, disk).LoadAsync(VPath.Create($"maps/{name}.vmf"));
        return (context, map);
    }

    /// <summary>Fails the compile once every tree has been built.</summary>
    private sealed class Failing(Action<CancellationToken> fail) : IVbspExtension
    {
        public ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken)
        {
            if (point == VbspExtensionPoint.DefaultCubemaps)
            {
                fail(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Cancels the compile when a named stage is reported, and records every stage.</summary>
    private sealed class StageCanceller(string stage, CancellationTokenSource cancel) : IProgress<CompileProgress>
    {
        private readonly List<string> _seen = [];

        public bool Fired { get; private set; }

        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public void Report(CompileProgress value)
        {
            lock (_seen)
            {
                _seen.Add(value.Stage);
            }

            if (!Fired && value.Stage == stage)
            {
                Fired = true;
                cancel.Cancel();
            }
        }
    }
}
