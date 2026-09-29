//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// The compile lets go of the bounce transfers on its last pass only: a
/// single range builds them once, and <c>-both</c> still hands the LDR
/// pass's to the HDR pass rather than building (and caching) them twice.
/// </summary>
public sealed class VradTransferReleaseTests
{
    [Theory]
    [InlineData(VradLightingRange.Ldr, 1)]
    [InlineData(VradLightingRange.Hdr, 1)]
    [InlineData(VradLightingRange.Both, 1)]
    public async Task TheTransfersAreBuiltOncePerCompile(VradLightingRange range, int builds)
    {
        (BspData bsp, IContentFileSystem content) = await VradScratchPoolTests.RoomAsync();
        CountingTransferCache cache = new();

        _ = await Vrad.LightAsync(bsp, new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1, Range = range },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            TransferCache = cache,
        });

        Assert.Equal(builds, cache.Lookups);
        Assert.Equal(builds, cache.Stores);
    }

    /// <summary>A cache that never hits and counts what it was asked.</summary>
    private sealed class CountingTransferCache : ITransferCache
    {
        private int _lookups;
        private int _stores;

        public int Lookups => _lookups;

        public int Stores => _stores;

        public ValueTask<TransferSet?> TryGetAsync(string key, int patchCount, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookups);
            return ValueTask.FromResult<TransferSet?>(null);
        }

        public ValueTask StoreAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stores);
            return ValueTask.CompletedTask;
        }
    }
}
