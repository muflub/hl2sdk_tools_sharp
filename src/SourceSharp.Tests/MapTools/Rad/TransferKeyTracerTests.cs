//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;
using SourceSharp.MapTools.Vis;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// The tracer half of vrad's transfer cache key names the tracer by its
/// <see cref="IRayTracer.TracerIdentity"/> (backend, device, driver), whatever
/// the tracer is: a transfer set built on one device must never be replayed
/// on another, where a hit on a plane may fall the other way.
/// </summary>
public sealed class TransferKeyTracerTests
{
    [Fact]
    public async Task TwoDevicesOfOneTracerTypeGiveTwoTransferKeys()
    {
        // The same tracer type twice, as two GPUs (or one GPU before and
        // after a driver update) would be: only the identity differs.
        string onA = await TransferKeyAsync(new CountingGpuTracerFactory("gpu-device-a"));
        string onB = await TransferKeyAsync(new CountingGpuTracerFactory("gpu-device-b"));

        Assert.NotEqual(onA, onB);
    }

    [Fact]
    public async Task OneDeviceGivesOneTransferKey()
    {
        string first = await TransferKeyAsync(new CountingGpuTracerFactory("gpu-device-a"));
        string second = await TransferKeyAsync(new CountingGpuTracerFactory("gpu-device-a"));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TheCpuTracerAndAGpuTracerGiveTwoTransferKeys() =>
        Assert.NotEqual(await TransferKeyAsync(null), await TransferKeyAsync(new CountingGpuTracerFactory("gpu-device-a")));

    [Fact]
    public void TheTracerPartIsTheIdentityAndTheScene()
    {
        Assert.Equal("empty-scene/abc", Vrad.TracerKey(new EmptySceneTracer(), "abc"));
        Assert.Null(Vrad.TracerKey(new EmptySceneTracer(), null));
    }

    // Lights the room with a transfer cache that records the key it is asked
    // for and never hits.
    private static async Task<string> TransferKeyAsync(IGpuTracerFactory? factory)
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
        VbspContext vbspContext = new(VbspOptions.Default, content) { MapBase = "room" };
        MapFile map = await new MapFileReader(vbspContext, files).LoadAsync(VPath.Create("maps/room.vmf"));
        VbspResult vbsp = await Vbsp.CompileAsync(map, vbspContext);
        BspData bsp = vbsp.Bsp!;

        // A map with no vis is lit with no bounce, and so builds no transfers.
        PortalFile read = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
        _ = await Vvis.ComputeAsync(bsp, PortalSet.FromPortalFile(read), new VisContext());

        RecordingTransferCache cache = new();
        _ = await Vrad.LightAsync(bsp, new VradContext
        {
            Options = VradOptions.Default with { Bounces = 1 },
            MapName = "room",
            Content = content,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            GpuTracerFactory = factory,
            TransferCache = cache,
        });

        return Assert.Single(cache.Keys.Distinct());
    }

    private sealed class RecordingTransferCache : ITransferCache
    {
        private readonly List<string> _keys = [];

        public IReadOnlyList<string> Keys
        {
            get
            {
                lock (_keys)
                {
                    return [.. _keys];
                }
            }
        }

        public ValueTask<TransferSet?> TryGetAsync(string key, int patchCount, CancellationToken cancellationToken)
        {
            lock (_keys)
            {
                _keys.Add(key);
            }

            return ValueTask.FromResult<TransferSet?>(null);
        }

        public ValueTask StoreAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken) => default;
    }
}
