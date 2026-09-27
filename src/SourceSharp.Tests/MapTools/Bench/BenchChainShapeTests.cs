//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapCompile;
using SourceSharp.MapTools.Compile;
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
    public async Task TheManagedCookerCooksOnThePoolUntilReleased()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using CompilePool pool = new(2);

        using (AllCommand.CookOnPool(cooker, pool))
        {
            Assert.Same(pool.Scheduler, cooker.Scheduler);
        }

        Assert.Same(TaskScheduler.Default, cooker.Scheduler);
    }

    [Fact]
    public void NoCookerNeedsNoLease()
    {
        using CompilePool pool = new(1);
        using IDisposable lease = AllCommand.CookOnPool(null, pool);

        Assert.NotNull(lease);
    }
}
