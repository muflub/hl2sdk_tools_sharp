//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The material facts a batch of compiles shares: each material is read
/// once for all of them, only a successful read is shared, and the store
/// holds nothing once disposed.
/// </summary>
public sealed class SharedMaterialFactsTests
{
    private const string Wall = "kit/wall";

    /// <summary>
    /// Many callers at once, under any spelling, get one object from one read
    /// of the VMT: the store's whole purpose.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallersShareOneRead()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        content.BeforeRead = async (_, token) => await release.Task.WaitAsync(token);

        Task<MaterialFacts>[] callers =
            [.. Enumerable.Range(0, 16).Select(i => store.GetAsync(i % 2 == 0 ? Wall : "KIT\\Wall.vmt").AsTask())];
        release.SetResult();
        MaterialFacts[] facts = await Task.WhenAll(callers);

        Assert.All(facts, f => Assert.Same(facts[0], f));
        Assert.True(facts[0].Found);
        Assert.Equal(1, content.ReadsOf($"materials/{Wall}.vmt"));
        Assert.Equal(2, store.Count); // the facts, and the VMT's bytes they were parsed from
    }

    /// <summary>
    /// A read that fails is not handed to the next caller: the entry is
    /// dropped, the next caller reads for itself and gets its own answer, as
    /// it would have without the store.
    /// </summary>
    [Fact]
    public async Task AFailedReadIsNotSharedTheNextCallerReadsAgain()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        int reads = 0;
        content.BeforeRead = (_, _) =>
            Interlocked.Increment(ref reads) == 1 ? throw new IOException("the disk hiccuped") : ValueTask.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => store.GetAsync(Wall).AsTask());
        Assert.Equal(0, store.Count);

        Assert.True((await store.GetAsync(Wall)).Found);
        Assert.Equal(2, content.ReadsOf($"materials/{Wall}.vmt"));
    }

    /// <summary>
    /// A caller waiting on another compile's read is not failed by that
    /// read's failure: it goes round and reads for itself.
    /// </summary>
    [Fact]
    public async Task AWaiterGoesRoundWhenTheReadItWaitedOnFails()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        content.BeforeRead = async (path, _) =>
        {
            if (!path.Value.EndsWith(".vmt", StringComparison.Ordinal) || Interlocked.Increment(ref reads) != 1)
            {
                return;
            }

            firstEntered.SetResult();
            await failFirst.Task;
            throw new IOException("the first reader's disk went away");
        };

        Task<MaterialFacts> first = store.GetAsync(Wall).AsTask();
        await firstEntered.Task;
        Task<MaterialFacts> waiter = store.GetAsync(Wall).AsTask();
        failFirst.SetResult();

        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.True((await waiter).Found);
        Assert.Equal(2, content.ReadsOf($"materials/{Wall}.vmt"));
    }

    /// <summary>
    /// One compile's cancellation stays its own: the reader that is cancelled
    /// ends cancelled, and a caller that was waiting on it reads the material
    /// itself and succeeds.
    /// </summary>
    [Fact]
    public async Task ACancelledReaderDoesNotCancelTheCallersWaitingOnIt()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        content.BeforeRead = async (path, token) =>
        {
            if (path.Value.EndsWith(".vmt", StringComparison.Ordinal) && Interlocked.Increment(ref reads) == 1)
            {
                firstEntered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        using CancellationTokenSource cancel = new();
        Task<MaterialFacts> first = store.GetAsync(Wall, cancel.Token).AsTask();
        await firstEntered.Task;
        Task<MaterialFacts> waiter = store.GetAsync(Wall).AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True((await waiter).Found);
    }

    /// <summary>A waiter whose own token fires stops waiting, cancelled, and the read it waited on carries on.</summary>
    [Fact]
    public async Task AWaiterWhoseOwnTokenFiresStopsWaiting()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        content.BeforeRead = async (path, _) =>
        {
            if (path.Value.EndsWith(".vmt", StringComparison.Ordinal))
            {
                firstEntered.TrySetResult();
                await release.Task;
            }
        };

        Task<MaterialFacts> first = store.GetAsync(Wall).AsTask();
        await firstEntered.Task;
        using CancellationTokenSource cancel = new();
        Task<MaterialFacts> waiter = store.GetAsync(Wall, cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        release.SetResult();
        Assert.True((await first).Found);
        Assert.Equal(1, content.ReadsOf($"materials/{Wall}.vmt"));
    }

    /// <summary>
    /// A material that is not there is an answer too, shared like any other:
    /// not found, from one look.
    /// </summary>
    [Fact]
    public async Task AMissingMaterialIsSharedAsNotFound()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);

        Assert.False((await store.GetAsync("kit/none")).Found);
        Assert.False((await store.GetAsync("KIT/None")).Found);
        Assert.Equal(1, content.ReadsOf("materials/kit/none.vmt"));
    }

    /// <summary>
    /// The store's view of the content answers lookups and listings as the
    /// content does, and hands every caller its own copy of a shared VMT,
    /// which the caller may write to without touching the next caller's.
    /// </summary>
    [Fact]
    public async Task TheStoresViewOfTheContentIsTheContent()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        IContentFileSystem view = store.MaterialFiles;
        VPath vmt = VPath.Create($"materials/{Wall}.vmt");

        Assert.Equal(await content.ResolveAsync(vmt), await view.ResolveAsync(vmt));
        List<VPath> listed = [];
        await foreach (VPath path in view.EnumerateAsync(VPath.Create("materials/kit")))
        {
            listed.Add(path);
        }

        Assert.Equal([vmt], listed);

        using (System.Buffers.IMemoryOwner<byte> first = (await view.ReadAsync(vmt))!)
        {
            first.Memory.Span[0] = (byte)'X';
        }

        using System.Buffers.IMemoryOwner<byte> second = (await view.ReadAsync(vmt))!;
        Assert.Equal((byte)'"', second.Memory.Span[0]);
        Assert.Equal(1, content.ReadsOf(vmt.Value));
        Assert.Null(await view.ReadAsync(VPath.Create("materials/kit/none.vmt")));
    }

    /// <summary>
    /// The patcher's parse of a material file and the surface property table
    /// are shared the same way: one read, one object, for every compile.
    /// </summary>
    [Fact]
    public async Task ThePatchersFilesAndTheSurfacePropertiesAreSharedToo()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        VbspContext a = new(VbspOptions.Default, content, store);
        VbspContext b = new(VbspOptions.Default, content, store);

        Assert.True(await a.Patcher.HasKeyAsync(Wall, "$basetexture"));
        Assert.True(await b.Patcher.HasKeyAsync(Wall, "$basetexture"));
        Assert.Same(await store.GetSurfacePropertiesAsync(CancellationToken.None), await store.GetSurfacePropertiesAsync(CancellationToken.None));

        Assert.Same(store, a.SharedMaterials);
        Assert.Equal(1, content.ReadsOf($"materials/{Wall}.vmt"));
        Assert.Equal(1, content.ReadsOf("scripts/surfaceproperties_manifest.txt"));
    }

    /// <summary>Disposing empties the store and it refuses every later read, so nothing it held outlives the batch.</summary>
    [Fact]
    public async Task DisposingEmptiesTheStoreAndRefusesLaterReads()
    {
        (CountingContent content, _) = await ContentAsync();
        SharedMaterialFacts store = new(content);
        _ = await store.GetAsync(Wall);
        Assert.Equal(2, store.Count);

        store.Dispose();

        Assert.Equal(0, store.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetAsync(Wall).AsTask());
    }

    /// <summary>The store's answer is what a private read gives: the same facts, field for field.</summary>
    [Fact]
    public async Task TheSharedAnswerIsThePrivateOne()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);

        MaterialFacts shared = await store.GetAsync(Wall);
        MaterialFacts alone = await MaterialFactsReader.ReadAsync(MaterialFactsReader.Normalize(Wall), content);

        Assert.Equal(alone.Name, shared.Name);
        Assert.Equal(alone.ShaderName, shared.ShaderName);
        Assert.Equal(alone.CompileFlags, shared.CompileFlags);
        Assert.Equal(alone.Width, shared.Width);
        Assert.Equal(alone.Opacity, shared.Opacity);
    }

    /// <summary>
    /// A context given the store reads its materials through it, keeping its
    /// own memo: two contexts get one object from one read.
    /// </summary>
    [Fact]
    public async Task ContextsSharingTheStoreReadEachMaterialOnce()
    {
        (CountingContent content, _) = await ContentAsync();
        using SharedMaterialFacts store = new(content);
        VbspContext a = new(VbspOptions.Default, content, store);
        VbspContext b = new(VbspOptions.Default, content, store);

        MaterialFacts fromA = await a.Materials.GetAsync(Wall);
        MaterialFacts fromB = await b.Materials.GetAsync(Wall);
        MaterialFacts again = await b.Materials.GetAsync(Wall);

        Assert.Same(fromA, fromB);
        Assert.Same(fromB, again);
        Assert.Equal(1, a.Materials.Count);
        Assert.Equal(1, content.ReadsOf($"materials/{Wall}.vmt"));
    }

    /// <summary>A context refuses a store over other content: those facts would be another compile's answers.</summary>
    [Fact]
    public async Task AContextRefusesAStoreOverOtherContent()
    {
        (CountingContent content, _) = await ContentAsync();
        (CountingContent other, _) = await ContentAsync();
        using SharedMaterialFacts store = new(other);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new VbspContext(VbspOptions.Default, content, store));
        Assert.Equal("sharedMaterials", refused.ParamName);
    }

    private static async Task<(CountingContent Content, InMemoryFileSystem Files)> ContentAsync()
    {
        InMemoryFileSystem files = new();
        files.AddText($"materials/{Wall}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"kit/wall\"\n}\n");
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return (new CountingContent(new ContentFileSystem([mount])), files);
    }
}
