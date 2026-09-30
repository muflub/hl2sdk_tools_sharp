//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A library's rooms compiled side by side: the same bytes as one at a time,
/// delivered in library order whatever order they finish in, one failure
/// kept to its room, and nothing left behind however the run ends.
/// </summary>
/// <remarks>
/// The schedules that could break the order or the cleanup are forced with
/// the compiler's probes (hold one room until another has finished, cancel
/// as a room starts), never left to timing.
/// </remarks>
public sealed class RoomLibraryCompilerTests
{
    private static readonly RoomDefinition[] Definitions =
    [
        RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY),
        RoomHarness.WalkableRoom("End", RoomFacing.PositiveX),
        RoomHarness.WalkableRoom("hall", RoomFacing.PositiveX, RoomFacing.NegativeX),
        RoomHarness.WalkableRoom("tee", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY),
        RoomHarness.WalkableRoom("corner", RoomFacing.PositiveX, RoomFacing.PositiveY),
    ];

    // ---- the bytes ------------------------------------------------------------

    /// <summary>
    /// Every room compiled side by side on a shared pool, at two threads and
    /// at four, with the managed cooker, writes the container a serial
    /// compile of that room alone writes: its own context, a private
    /// material cache, one thread.
    /// </summary>
    [Fact]
    public async Task AParallelCompileWritesTheSameBytesAsASerialOne()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        List<byte[]> serial = [];
        foreach (LibraryRoom room in rooms)
        {
            VbspContext alone = new(VbspOptions.Default, content)
            {
                MapBase = room.Definition.Name.ToLowerInvariant(),
                CollisionCooker = cooker,
                Parallelism = CompileParallelism.Serial,
            };
            serial.Add(await SaveAsync(await RoomCompiler.CompileAsync(room.Document, room.Definition, alone)));
        }

        foreach (int degree in new[] { 1, 2, 4 })
        {
            List<RoomCompileOutcome> outcomes = await CompileAsync(
                rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
                {
                    CollisionCooker = cooker,
                    Parallelism = new CompileParallelism { MaxDegree = degree },
                });

            Assert.Equal(serial.Count, outcomes.Count);
            for (int i = 0; i < serial.Count; i++)
            {
                byte[] parallel = await SaveAsync(outcomes[i].Compiled!);
                Assert.True(serial[i].AsSpan().SequenceEqual(parallel), $"room {i} at {degree} thread(s)");
            }
        }
    }

    /// <summary>
    /// The link work each room's compile does ahead is on the room it is for,
    /// and the pack written from the rooms (containers and link sections) is
    /// the same bytes at one thread and at four, and run after run.
    /// </summary>
    [Fact]
    public async Task ThePackWithLinkDataIsTheSameAtAnyThreadCount()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);

        List<byte[]> packs = [];
        foreach (int degree in new[] { 1, 4, 1, 4 })
        {
            List<RoomCompileOutcome> outcomes = await CompileAsync(
                rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
                {
                    CollisionCooker = cooker,
                    Parallelism = new CompileParallelism { MaxDegree = degree },
                });

            List<RoomPackItem> items = [];
            foreach (RoomCompileOutcome outcome in outcomes)
            {
                RoomObject room = outcome.Compiled!;
                Assert.NotNull(room.Link);
                Assert.True(room.Link.IsFor(room));
                items.Add(await RoomPackItem.CreateAsync(room));
                Assert.Contains(items[^1].Extra, s => s.Tag == RoomLinkSections.SharedTag);
            }

            using MemoryStream pack = new();
            await RoomPack.SaveAsync(items, pack);
            packs.Add(pack.ToArray());
        }

        Assert.All(packs, p => Assert.Equal(packs[0], p));
    }

    /// <summary>
    /// On a scheduler the host lends instead of a pool, the rooms run on it
    /// (and so do the managed cooker's cooks), and write the same bytes.
    /// </summary>
    [Fact]
    public async Task OnALentSchedulerTheRoomsRunThereAndWriteTheSameBytes()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        List<byte[]> onPool = [.. await BytesAsync(await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            CollisionCooker = cooker,
            Parallelism = new CompileParallelism { MaxDegree = 2 },
        }))];

        CountingScheduler scheduler = new();
        bool madePool = false;
        List<RoomCompileOutcome> lent = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            CollisionCooker = cooker,
            Parallelism = new CompileParallelism { MaxDegree = 2, Scheduler = scheduler },
            PoolProbe = _ => madePool = true,
        });

        Assert.False(madePool);
        Assert.True(scheduler.Queued > 0);
        Assert.Equal(onPool, await BytesAsync(lent));
    }

    /// <summary>
    /// A pool the host lends is used, not made, and not disposed: it is still
    /// the host's, and still runs work, after the library is done.
    /// </summary>
    [Fact]
    public async Task ALentPoolIsUsedAndLeftToItsOwner()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        using CompilePool pool = new(2);
        bool madePool = false;

        List<RoomCompileOutcome> outcomes = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 2, Pool = pool },
            PoolProbe = _ => madePool = true,
        });

        Assert.False(madePool);
        Assert.All(outcomes, o => Assert.NotNull(o.Compiled));
        Assert.True(pool.LiveThreadCount > 0);
        Assert.Equal(7, await Task.Factory.StartNew(() => 7, CancellationToken.None, TaskCreationOptions.None, pool.Scheduler));
    }

    // ---- the order -------------------------------------------------------------

    /// <summary>
    /// The second room is made to finish before the first has started its
    /// compile; the first is still delivered first, and the second only once
    /// it has been.
    /// </summary>
    [Fact]
    public async Task OutcomesComeInLibraryOrderWhenALaterRoomFinishesFirst()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        TaskCompletionSource secondDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> finished = [];

        List<RoomCompileOutcome> delivered = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 2 },
            BeforeRoomProbe = async (index, token) =>
            {
                if (index == 0)
                {
                    await secondDone.Task.WaitAsync(token);
                }
            },
            RoomCompiledProbe = index =>
            {
                lock (finished)
                {
                    finished.Add(index);
                }

                if (index == 1)
                {
                    secondDone.SetResult();
                }
            },
        });

        Assert.Equal(1, finished[0]);
        Assert.Equal(Enumerable.Range(0, rooms.Count), delivered.Select(o => o.Index));
        Assert.Equal(Definitions.Select(d => d.Name), delivered.Select(o => o.Room.Definition.Name));
    }

    /// <summary>
    /// A room that is not linkable is an outcome with its error, delivered in
    /// its place; the rooms after it still compile.
    /// </summary>
    [Fact]
    public async Task AFailingRoomIsDeliveredInItsPlaceAndTheOthersCompile()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync(breakRoom: 1);

        List<RoomCompileOutcome> delivered = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 4 },
        });

        Assert.Equal(Enumerable.Range(0, rooms.Count), delivered.Select(o => o.Index));
        Assert.Null(delivered[1].Compiled);
        RoomLintException lint = Assert.IsType<RoomLintException>(delivered[1].Error);
        Assert.StartsWith("rule 4", lint.Message, StringComparison.Ordinal);
        Assert.All(delivered.Where(o => o.Index != 1), o => Assert.NotNull(o.Compiled));
        Assert.All(delivered.Where(o => o.Index != 1), o => Assert.Null(o.Error));
    }

    /// <summary>
    /// A room with no brushes is that room's failure, named, and the rooms
    /// around it still compile. Before the refusal its vbsp threw an
    /// <see cref="ArgumentOutOfRangeException"/>, which is not a room
    /// failure, so it ended the whole library's run.
    /// </summary>
    [Fact]
    public async Task ARoomWithNoBrushesIsRefusedByNameAndTheOthersCompile()
    {
        VmfDocument library = RoomHarness.LibraryVmf(Definitions[0], Definitions[1]);
        RoomHarness.AddEmptyRoom(library, "void", 2);
        (_, CountingContent content) = await LibraryAsync();
        IReadOnlyList<LibraryRoom> rooms = RoomLibraryVmf.Split(library);

        List<RoomCompileOutcome> delivered = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 4 },
        });

        Assert.Equal([0, 1, 2], delivered.Select(o => o.Index));
        Assert.Equal("void", delivered[2].Room.Definition.Name);
        Assert.Null(delivered[2].Compiled);
        RoomLintException lint = Assert.IsType<RoomLintException>(delivered[2].Error);
        Assert.Equal(
            "rule 2 (ShellSealedExceptAtSockets): room void has no world brushes; a room is a shell of world brushes"
            + " around its cell, and a compile of none has no world to build.",
            lint.Message);
        Assert.All(delivered.Take(2), o => Assert.NotNull(o.Compiled));
    }

    /// <summary>The exceptions that are one room's failure, and one that is the run's.</summary>
    [Fact]
    public void TheRoomFailuresAreTheOnesTheCommandReportedPerRoom()
    {
        Assert.True(RoomLibraryCompiler.IsRoomFailure(new RoomLintException("x")));
        Assert.True(RoomLibraryCompiler.IsRoomFailure(new LinkException("x")));
        Assert.True(RoomLibraryCompiler.IsRoomFailure(new IOException("x")));
        Assert.True(RoomLibraryCompiler.IsRoomFailure(new UnauthorizedAccessException("x")));
        Assert.True(RoomLibraryCompiler.IsRoomFailure(new SourceSharp.MapTools.Diagnostics.MapCompileException("x")));
        Assert.False(RoomLibraryCompiler.IsRoomFailure(new OperationCanceledException()));
        Assert.False(RoomLibraryCompiler.IsRoomFailure(new InvalidOperationException()));
    }

    /// <summary>A library of no rooms delivers nothing and is done.</summary>
    [Fact]
    public async Task NoRoomsDeliverNothing()
    {
        (_, CountingContent content) = await LibraryAsync();
        Assert.Empty(await CompileAsync([], new RoomLibraryCompileSettings(VbspOptions.Default, content)));
    }

    // ---- the ends --------------------------------------------------------------

    /// <summary>
    /// Cancelled as the third room starts, the run ends cancelled; no room
    /// after it is delivered; and everything it made is gone: its pool's
    /// threads are joined, its material store is empty, and the next run in
    /// the same process compiles as if nothing happened.
    /// </summary>
    [Fact]
    public async Task ACancelledRunStopsEveryRoomAndReleasesEverything()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        using CancellationTokenSource cancel = new();
        CompilePool? pool = null;
        SharedMaterialFacts? materials = null;
        List<RoomCompileOutcome> delivered = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoomLibraryCompiler.CompileAsync(
            rooms,
            new RoomLibraryCompileSettings(VbspOptions.Default, content)
            {
                Parallelism = new CompileParallelism { MaxDegree = 2 },
                PoolProbe = p => pool = p,
                MaterialsProbe = m => materials = m,
                BeforeRoomProbe = async (index, token) =>
                {
                    if (index == 2)
                    {
                        await cancel.CancelAsync();
                        token.ThrowIfCancellationRequested();
                    }
                },
            },
            (outcome, _) =>
            {
                delivered.Add(outcome);
                return ValueTask.CompletedTask;
            },
            cancel.Token));

        Assert.True(delivered.Count <= 2);
        Assert.Equal(Enumerable.Range(0, delivered.Count), delivered.Select(o => o.Index));
        Assert.Equal(0, pool!.LiveThreadCount);
        Assert.Equal(0, materials!.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => materials.GetAsync(RoomHarness.Plain).AsTask());

        List<RoomCompileOutcome> next = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 2 },
        });
        Assert.All(next, o => Assert.NotNull(o.Compiled));
    }

    /// <summary>
    /// A callback that throws ends the run with its exception, is not called
    /// again for the rooms after, and the run still releases its pool and its
    /// material store. The later rooms are held until the first has been
    /// delivered, so they are finished and waiting when the callback throws.
    /// </summary>
    [Fact]
    public async Task ACallbackThatThrowsEndsTheRunAndReleasesEverything()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        CompilePool? pool = null;
        SharedMaterialFacts? materials = null;
        int calls = 0;
        TaskCompletionSource secondDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RoomLibraryCompiler.CompileAsync(
            rooms,
            new RoomLibraryCompileSettings(VbspOptions.Default, content)
            {
                Parallelism = new CompileParallelism { MaxDegree = 2 },
                PoolProbe = p => pool = p,
                MaterialsProbe = m => materials = m,
                BeforeRoomProbe = async (index, token) =>
                {
                    if (index == 0)
                    {
                        await secondDone.Task.WaitAsync(token);
                    }
                },
                RoomCompiledProbe = index =>
                {
                    if (index == 1)
                    {
                        secondDone.SetResult();
                    }
                },
            },
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("the host's log is full");
            }));

        Assert.Equal("the host's log is full", thrown.Message);
        Assert.Equal(1, calls);
        Assert.Equal(0, pool!.LiveThreadCount);
        Assert.Equal(0, materials!.Count);
    }

    /// <summary>
    /// The kit's materials are read once for the whole library, not once per
    /// room: each VMT is read exactly once, the surface properties once, and
    /// no material file is read more often for five rooms than for one. The
    /// run's material store holds nothing once it returns.
    /// </summary>
    /// <remarks>
    /// Two reads remain per compile and are pinned here so a change to them
    /// is seen: a base texture is read once per material that names it (the
    /// facts reader's own rule, the same for one room as for many), and the
    /// detail-prop dictionary (<c>detail.vbsp</c>) is still read by every
    /// room, since it is not a material fact.
    /// </remarks>
    [Fact]
    public async Task TheKitsMaterialsAreReadOnceAcrossTheRooms()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        (_, CountingContent single) = await LibraryAsync();
        SharedMaterialFacts? materials = null;

        List<RoomCompileOutcome> outcomes = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Parallelism = new CompileParallelism { MaxDegree = 4 },
            MaterialsProbe = m => materials = m,
        });
        _ = await CompileAsync([rooms[0]], new RoomLibraryCompileSettings(VbspOptions.Default, single));

        Assert.All(outcomes, o => Assert.NotNull(o.Compiled));
        Assert.Equal(1, content.ReadsOf($"materials/{RoomHarness.Plain}.vmt"));
        Assert.Equal(1, content.ReadsOf($"materials/{RoomHarness.Trigger}.vmt"));
        Assert.Equal(1, content.ReadsOf("scripts/surfaceproperties_manifest.txt"));
        Assert.Equal(rooms.Count, content.ReadsOf("detail.vbsp"));
        Assert.All(
            content.Reads.Where(r => r.Key.StartsWith("materials/", StringComparison.Ordinal)),
            read => Assert.True(read.Value == single.ReadsOf(read.Key), $"{read.Key}: {read.Value} reads for {rooms.Count} rooms, {single.ReadsOf(read.Key)} for one"));
        Assert.Equal(0, materials!.Count);
    }

    /// <summary>The arguments are checked.</summary>
    [Fact]
    public async Task TheArgumentsAreChecked()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        RoomLibraryCompileSettings settings = new(VbspOptions.Default, content);
        static ValueTask Ignore(RoomCompileOutcome o, CancellationToken t) => ValueTask.CompletedTask;

        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryCompiler.CompileAsync(null!, settings, Ignore));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryCompiler.CompileAsync(rooms, null!, Ignore));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RoomLibraryCompiler.CompileAsync(rooms, settings, null!));
        Assert.Throws<ArgumentNullException>(() => new RoomLibraryCompileSettings(null!, content));
        Assert.Throws<ArgumentNullException>(() => new RoomLibraryCompileSettings(VbspOptions.Default, null!));
    }

    // ---- helpers -----------------------------------------------------------------

    /// <summary>
    /// The library's rooms, split as <c>ssmap room</c> splits them, over
    /// counting content holding the harness materials; with one room's plug
    /// made plain, so that room is not linkable, when asked.
    /// </summary>
    private static async Task<(IReadOnlyList<LibraryRoom> Rooms, CountingContent Content)> LibraryAsync(int breakRoom = -1)
    {
        VmfDocument library = RoomHarness.LibraryVmf(Definitions);
        if (breakRoom >= 0)
        {
            float low = breakRoom * (RoomHarness.Cell + RoomHarness.LibraryGap);
            float east = low + RoomHarness.Cell;
            foreach (VmfChunk solid in library.GetChunk("world")!.Chunks)
            {
                List<VmfChunk> sides = [.. solid.Chunks];
                if (sides.Any(s => s.GetValue("material") == RoomHarness.Trigger)
                    && sides.Any(s => s.GetValue("plane")!.Contains($"({east}", StringComparison.Ordinal)))
                {
                    foreach (VmfKey key in sides.SelectMany(s => s.Keys).Where(k => k.Name == "material"))
                    {
                        key.Value = RoomHarness.Plain;
                    }
                }
            }
        }

        InMemoryFileSystem files = new();
        files.AddText($"materials/{RoomHarness.Plain}.vmt", "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{RoomHarness.Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        return (RoomLibraryVmf.Split(library), new CountingContent(new ContentFileSystem([mount])));
    }

    /// <summary>
    /// With navigation settings, every outcome carries its room's navigation,
    /// built on the room's own task: the same sections at one thread and at
    /// four as a build of each compiled room alone; without, none.
    /// </summary>
    [Fact]
    public async Task EachRoomsNavigationIsBuiltBesideItsCompileAtAnyThreadCount()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        List<List<byte[]>> runs = [];
        foreach (int degree in new[] { 1, 4 })
        {
            List<RoomCompileOutcome> outcomes = await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
            {
                Nav = SourceSharp.MapTools.Nav.NavSettings.Default,
                Parallelism = new CompileParallelism { MaxDegree = degree },
            });
            runs.Add([.. outcomes.Select(o => SourceSharp.MapTools.Nav.RoomNavSection.Write(o.Compiled!.Nav!.Base, SourceSharp.MapFormats.Nav.NavCompression.None))]);
            for (int i = 0; i < outcomes.Count; i++)
            {
                SourceSharp.MapTools.Nav.RoomNav alone = SourceSharp.MapTools.Nav.RoomNavBuilder.Build(
                    rooms[i].Definition, outcomes[i].Compiled!.Bsp, [], RoomRole.None, SourceSharp.MapTools.Nav.NavSettings.Default);
                Assert.Equal(SourceSharp.MapTools.Nav.RoomNavSection.Write(alone, SourceSharp.MapFormats.Nav.NavCompression.None), runs[^1][i]);
            }
        }

        Assert.Equal(runs[0], runs[1]);
        Assert.All(await CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)), o => Assert.Null(o.Compiled!.Nav));
    }

    /// <summary>A voxel that does not fit the library's cell is refused once, before any room compiles.</summary>
    [Fact]
    public async Task AVoxelThatDoesNotFitTheCellIsRefusedBeforeAnyRoom()
    {
        (IReadOnlyList<LibraryRoom> rooms, CountingContent content) = await LibraryAsync();
        int started = 0;
        await Assert.ThrowsAsync<RoomLibraryException>(() => CompileAsync(rooms, new RoomLibraryCompileSettings(VbspOptions.Default, content)
        {
            Nav = SourceSharp.MapTools.Nav.NavSettings.Default with { VoxelSize = 100 },
            BeforeRoomProbe = (_, _) =>
            {
                Interlocked.Increment(ref started);
                return ValueTask.CompletedTask;
            },
        }));
        Assert.Equal(0, started);
    }

    private static async Task<List<RoomCompileOutcome>> CompileAsync(
        IReadOnlyList<LibraryRoom> rooms, RoomLibraryCompileSettings settings)
    {
        List<RoomCompileOutcome> delivered = [];
        await RoomLibraryCompiler.CompileAsync(
            rooms,
            settings,
            (outcome, _) =>
            {
                delivered.Add(outcome);
                return ValueTask.CompletedTask;
            });
        return delivered;
    }

    private static async Task<IReadOnlyList<byte[]>> BytesAsync(IEnumerable<RoomCompileOutcome> outcomes)
    {
        List<byte[]> bytes = [];
        foreach (RoomCompileOutcome outcome in outcomes)
        {
            bytes.Add(await SaveAsync(outcome.Compiled!));
        }

        return bytes;
    }

    private static async Task<byte[]> SaveAsync(RoomObject room)
    {
        using MemoryStream stream = new();
        await RoomObjectStore.SaveAsync(room, stream);
        return stream.ToArray();
    }

    /// <summary>The default scheduler, counting what is queued on it: proof the rooms went where the host said.</summary>
    private sealed class CountingScheduler : TaskScheduler
    {
        private int _queued;

        public int Queued => Volatile.Read(ref _queued);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queued);
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
