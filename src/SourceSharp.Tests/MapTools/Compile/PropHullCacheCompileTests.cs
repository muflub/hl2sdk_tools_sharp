//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

using Xunit;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// <see cref="CompileRequest.PropHullCache"/> through the whole chain: a
/// room furnished with the synthetic content's prop models, compiled with the
/// managed cooker, with and without a hull cache shared between compiles.
/// </summary>
public sealed class PropHullCacheCompileTests
{
    private static readonly string[] Models =
    [
        "models/props_junk/wood_crate001a.mdl",
        "models/props_c17/oildrum001.mdl",
        "models/props_c17/furniturechair001a.mdl",
        "models/props_junk/propanecanister001a.mdl",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Content = new(SyntheticContent.Build);

    // The chain's room with props in its open half, every model twice.
    private static VmfDocument FurnishedRoom()
    {
        VmfDocument room = MapCompilerTests.Room();
        for (int i = 0; i < Models.Length * 2; i++)
        {
            float x = 40 + (24 * (i % 4));
            float y = i < 4 ? 40 : 200;
            VmfChunk prop = MapCompilerTests.Entity(
                room, "prop_static", string.Create(CultureInfo.InvariantCulture, $"{x} {y} 0"));
            prop.AddKey("model", Models[i % Models.Length]);
            prop.AddKey("angles", string.Create(CultureInfo.InvariantCulture, $"0 {15 * i} 0"));
            prop.AddKey("solid", "6");
        }

        return room;
    }

    private static async Task<(InMemoryFileSystem Files, IContentFileSystem Content)> DiskAsync()
    {
        (InMemoryFileSystem files, _) = await MapCompilerTests.DiskAsync(FurnishedRoom());
        foreach ((string path, byte[] bytes) in Content.Value)
        {
            files.AddFile(path, bytes);
        }

        // Mounted after the models are on the disk, so the mount sees them.
        return (files, new ContentFileSystem([await DirectoryContentMount.MountAsync(files, VPath.Empty)]));
    }

    private static async Task<byte[]> CompileAsync(
        ICollisionCooker cooker, PropHullCache? hulls, CancellationToken cancellationToken = default)
    {
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync();
        CompileRequest request = MapCompilerTests.Request(files, content) with
        {
            CollisionCooker = cooker,
            PropHullCache = hulls,
        };
        await MapCompiler.CompileAsync(request, null, cancellationToken);
        return await MapCompilerTests.ReadAsync(files, $"{MapCompilerTests.MapDirectory}/room.bsp");
    }

    [Fact]
    public async Task CompilesSharingTheCacheWriteTheBytesOfCompilesWithout()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        byte[] plain = await CompileAsync(cooker, null);

        using PropHullCache hulls = new();

        // Three at once (cold, racing to fill it), then one after (all hits).
        byte[][] concurrent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => CompileAsync(cooker, hulls))));
        PropHullCacheStatistics filled = hulls.Statistics;
        byte[] warm = await CompileAsync(cooker, hulls);
        PropHullCacheStatistics after = hulls.Statistics;

        foreach (byte[] bsp in concurrent)
        {
            Assert.Equal(plain, bsp);
        }

        Assert.Equal(plain, warm);
        Assert.Equal(Models.Length, filled.Count);
        Assert.Equal(filled.Hits + Models.Length, after.Hits);
        Assert.Equal(filled.Misses, after.Misses);
    }

    [Fact]
    public async Task ACompileWithoutACookerNeverConsultsTheCache()
    {
        // No cooker: the managed approximation answers the prop questions,
        // and nothing is cooked to keep.
        using PropHullCache hulls = new();
        (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync();

        await MapCompiler.CompileAsync(MapCompilerTests.Request(files, content) with { PropHullCache = hulls }, null);

        Assert.Equal(default, hulls.Statistics);
    }

    [Fact]
    public async Task VbspDrivenDirectlyTakesTheCacheFromItsContext()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using PropHullCache hulls = new();

        async Task<byte[]> VbspAsync(PropHullCache? cache)
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync();
            using CompilePool pool = new(2);
            VbspContext context = new(VbspOptions.Default, content)
            {
                CollisionCooker = cooker.On(pool.Scheduler),
                PropHullCache = cache,
                Parallelism = new CompileParallelism { MaxDegree = 2, Pool = pool },
            };
            MapFile map = await MapSource.FromVmf(files, VPath.Create($"{MapCompilerTests.MapDirectory}/room.vmf"))
                .LoadAsync(context, CancellationToken.None);
            VbspResult result = await Vbsp.CompileAsync(map, context);
            return await MapCompilerTests.BytesAsync(result.Bsp!);
        }

        byte[] plain = await VbspAsync(null);
        byte[] cold = await VbspAsync(hulls);
        byte[] warm = await VbspAsync(hulls);

        Assert.Equal(plain, cold);
        Assert.Equal(plain, warm);
        Assert.Equal(Models.Length, hulls.Statistics.Hits);
    }

    /// <summary>
    /// The two update entry points (<c>-onlyprops</c> through
    /// <see cref="Vbsp.UpdateAsync"/>, and <see cref="SurfaceContentVbsp.UpdateAsync"/>
    /// with its own cooker) take the cache from the context too: the
    /// rewritten file is the bytes an update without it writes, and the
    /// second update cooks nothing.
    /// </summary>
    [Fact]
    public async Task TheUpdatePathsTakeTheCacheFromTheContext()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using PropHullCache hulls = new();

        async Task<byte[]> UpdateAsync(PropHullCache? cache, bool surfaceEntryPoint)
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync();
            VPath vmf = VPath.Create($"{MapCompilerTests.MapDirectory}/room.vmf");
            VbspContext full = new(VbspOptions.Default, content) { CollisionCooker = cooker };
            VbspResult compiled = await Vbsp.CompileAsync(
                await MapSource.FromVmf(files, vmf).LoadAsync(full, CancellationToken.None), full);

            VbspContext update = new(VbspOptions.Default with { OnlyProps = true }, content)
            {
                CollisionCooker = cooker,
                PropHullCache = cache,
            };
            MapFile map = await MapSource.FromVmf(files, vmf).LoadAsync(update, CancellationToken.None);
            BspData updated = surfaceEntryPoint
                ? await SurfaceContentVbsp.UpdateAsync(compiled.Bsp!, map, update, cooker)
                : await Vbsp.UpdateAsync(compiled.Bsp!, map, update);
            return await MapCompilerTests.BytesAsync(updated);
        }

        foreach (bool surfaceEntryPoint in new[] { false, true })
        {
            hulls.Clear();
            byte[] plain = await UpdateAsync(null, surfaceEntryPoint);
            byte[] cold = await UpdateAsync(hulls, surfaceEntryPoint);
            PropHullCacheStatistics filled = hulls.Statistics;
            byte[] warm = await UpdateAsync(hulls, surfaceEntryPoint);

            Assert.Equal(plain, cold);
            Assert.Equal(plain, warm);
            Assert.Equal(Models.Length, filled.Count);
            Assert.Equal(filled.Misses, hulls.Statistics.Misses);
            Assert.Equal(filled.Hits + Models.Length, hulls.Statistics.Hits);
        }
    }

    [Fact]
    public async Task ACancelledCompileLeavesTheCacheFitForTheNext()
    {
        using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        using PropHullCache hulls = new();
        byte[] plain = await CompileAsync(cooker, null);

        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompileAsync(cooker, hulls, cancel.Token));

        Assert.Equal(0, hulls.Statistics.Count);
        Assert.Equal(plain, await CompileAsync(cooker, hulls));
    }
}
