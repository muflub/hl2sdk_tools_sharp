//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapCompile;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bench;

/// <summary>
/// A bench chain cell runs the compile <c>ssmap all</c> runs: its command line
/// parses with the cache options in the chain's part, <c>-overlap</c> and
/// <c>-threads</c> reach the request, and the managed cooker cooks on the
/// chain's pool.
/// </summary>
public sealed class BenchChainShapeTests
{
    private static readonly string[] Sections =
        ["-overlap", "--vbsp", "-notjunc", "--vvis", "-fast", "--vrad", "-bounce", "2"];

    [Fact]
    public void CacheOptionsGoBeforeTheFirstSection()
    {
        IReadOnlyList<string> args = BenchCommand.StageArgs("m.vmf", "g", 4, Sections, "store");

        Assert.Equal(
            ["m.vmf", "-game", "g", "-threads", "4", "-overlap", "-incremental", "-cache-dir", "store",
             "--vbsp", "-notjunc", "--vvis", "-fast", "--vrad", "-bounce", "2"],
            args);
    }

    [Fact]
    public void WithoutSectionsCacheOptionsGoLast()
    {
        IReadOnlyList<string> args = BenchCommand.StageArgs("m", null, 2, ["-fast"], "store");

        Assert.Equal(["m", "-threads", "2", "-fast", "-incremental", "-cache-dir", "store"], args);
    }

    [Fact]
    public void WithoutAStoreTheOptionsAreUntouched()
    {
        IReadOnlyList<string> args = BenchCommand.StageArgs("m.vmf", null, 1, Sections, null);

        Assert.Equal(["m.vmf", "-threads", "1", .. Sections], args);
    }

    [Fact]
    public void ACachedChainCellWithAVradSectionParses()
    {
        // Appended after the --vrad section, -incremental was vrad's unknown option.
        AllArgs parsed = AllCommand.Parse(BenchCommand.StageArgs("m.vmf", null, 3, Sections, "store"));

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        Assert.True(parsed.Overlap);
        Assert.Equal(3, AllCommand.ChainParallelism(parsed).MaxDegree);
    }

    [Fact]
    public void WithoutThreadsTheChainUsesEveryProcessor()
    {
        AllArgs parsed = AllCommand.Parse(["m.vmf"]);

        Assert.Equal(CompileParallelism.Default.MaxDegree, AllCommand.ChainParallelism(parsed).MaxDegree);
    }

    [Fact]
    public async Task TheManagedCookerCooksOnThePoolThroughAViewAndIsLeftAsItWas()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using CompilePool pool = new(2);

        ICollisionCooker? onPool = AllCommand.CookOnPool(cooker, pool);

        Assert.NotNull(onPool);
        Assert.NotSame(cooker, onPool);
        Assert.True(await onPool.RunAsync(_ => pool.IsPoolThread));
        Assert.False(await cooker.RunAsync(_ => pool.IsPoolThread));
    }

    [Fact]
    public async Task AnyOtherCookerIsUsedAsItIs()
    {
        using CompilePool pool = new(1);
        await using ICollisionCooker other = new NamedCooker();

        Assert.Null(AllCommand.CookOnPool(null, pool));
        Assert.Same(other, AllCommand.CookOnPool(other, pool));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChainCellsRequestCarriesOverlapThePoolAndTheCookerOnIt(bool overlap)
    {
        // The wiring no output comparison can see: the overlapped and the
        // sequential chain write the same bytes, and so do cooks on the pool
        // and beside it. Pinned here on the request the cell hands the compiler.
        AllArgs parsed = AllCommand.Parse(overlap ? ["m.vmf", "-threads", "3", "-overlap"] : ["m.vmf", "-threads", "3"]);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        InMemoryFileSystem disk = new();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using CompilePool pool = new(3);

        CompileRequest request = BenchCommand.ChainRequest(
            parsed, disk, "maps/m.vmf", "maps/m", "m", new ContentFileSystem([]), FormatOptions.Default, pool, cooker);

        Assert.Equal(overlap, request.Overlap);
        Assert.Same(pool, request.Parallel.Pool);
        Assert.Equal(3, request.Parallel.MaxDegree);
        Assert.NotNull(request.CollisionCooker);
        Assert.True(await request.CollisionCooker.RunAsync(_ => pool.IsPoolThread));
        Assert.Equal(cooker.CookerIdentity, request.CollisionCooker.CookerIdentity);
    }

    private sealed class NamedCooker : ICollisionCooker
    {
        public string CookerIdentity => "named";

        public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
