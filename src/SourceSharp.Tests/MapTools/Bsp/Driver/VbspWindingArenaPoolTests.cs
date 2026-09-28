//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Parallel;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// The forks' <see cref="WindingArenaPool"/> over whole compiles: every
/// arena is back by the time the trees are built, and the pool is released,
/// holding nothing, when the compile ends however it ended.
/// </summary>
public sealed class VbspWindingArenaPoolTests
{
    [Fact]
    public async Task AFinishedCompileReleasesThePool()
    {
        (VbspCompilation compilation, PoolProbe probe) = await PrepareAsync(failWith: null);

        await compilation.RunAsync(CancellationToken.None);

        AssertUsedThenReleased(compilation, probe);
    }

    [Fact]
    public async Task AFailedCompileReleasesThePool()
    {
        (VbspCompilation compilation, PoolProbe probe) =
            await PrepareAsync(failWith: _ => throw new InvalidDataException("extension failed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => compilation.RunAsync(CancellationToken.None));

        AssertUsedThenReleased(compilation, probe);
    }

    [Fact]
    public async Task ACancelledCompileReleasesThePool()
    {
        using CancellationTokenSource cancel = new();
        (VbspCompilation compilation, PoolProbe probe) = await PrepareAsync(
            failWith: token =>
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.RunAsync(cancel.Token));

        AssertUsedThenReleased(compilation, probe);
    }

    private static void AssertUsedThenReleased(VbspCompilation compilation, PoolProbe probe)
    {
        WindingArenaPool pool = compilation.Build!.ForkArenas;

        // Something forked, every fork gave its arena back before the next
        // stage, and the pool was still holding them for reuse until the end.
        Assert.True(probe.CreatedWhenSeen > 0, "nothing forked, so there was nothing to release");
        Assert.Equal(0, probe.RentedWhenSeen);
        Assert.Equal(probe.CreatedWhenSeen, probe.IdleWhenSeen);

        Assert.True(pool.IsReleased);
        Assert.Equal(0, pool.Idle);
        Assert.Equal(0, pool.Rented);
    }

    private static async Task<(VbspCompilation, PoolProbe)> PrepareAsync(Action<CancellationToken>? failWith)
    {
        VbspContext context = await UnitMap.ContextAsync();
        context.Parallelism = new CompileParallelism { MaxDegree = 4 };
        context.TreeForkMinBrushes = 1;
        MapFile map = await MapFileLoader.LoadAsync(context, Room());
        MapFileReader.TakeBounds(map);

        PoolProbe probe = new(failWith);
        VbspCompilation compilation = new(map, context, [probe]);
        probe.Compilation = compilation;
        return (compilation, probe);
    }

    /// <summary>Looks at the pool once every tree is built, and optionally fails or cancels there.</summary>
    private sealed class PoolProbe(Action<CancellationToken>? failWith) : IVbspExtension
    {
        public VbspCompilation? Compilation { get; set; }

        public int CreatedWhenSeen { get; private set; }

        public int RentedWhenSeen { get; private set; } = -1;

        public int IdleWhenSeen { get; private set; } = -1;

        public ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken)
        {
            if (point == VbspExtensionPoint.DefaultCubemaps)
            {
                WindingArenaPool pool = Compilation!.Build!.ForkArenas;
                CreatedWhenSeen = pool.Created;
                RentedWhenSeen = pool.Rented;
                IdleWhenSeen = pool.Idle;
                failWith?.Invoke(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A sealed room with a row of pillars, enough brushes on both sides of a split to fork.</summary>
    private static VmfDocument Room()
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        int id = 10;
        const int Size = 1024;

        void Slab((float, float, float) mins, (float, float, float) maxs) =>
            world.Children.Add(UnitMap.Box(UnitMap.Plain, mins, maxs, id++));

        Slab((-16, -16, -16), (Size + 16, Size + 16, 0));
        Slab((-16, -16, Size), (Size + 16, Size + 16, Size + 16));
        Slab((-16, -16, 0), (0, Size + 16, Size));
        Slab((Size, -16, 0), (Size + 16, Size + 16, Size));
        Slab((0, -16, 0), (Size, 0, Size));
        Slab((0, Size, 0), (Size, Size + 16, Size));
        for (int x = 64; x < Size - 64; x += 128)
        {
            for (int y = 64; y < Size - 64; y += 256)
            {
                Slab((x, y, 0), (x + 32, y + 32, 256));
            }
        }

        VmfChunk player = new(MapFileLoader.EntityChunk);
        player.AddKey("id", (id++).ToString(System.Globalization.CultureInfo.InvariantCulture));
        player.AddKey("classname", "info_player_start");
        player.AddKey("origin", "512 512 512");
        document.Chunks.Add(player);
        return document;
    }
}
