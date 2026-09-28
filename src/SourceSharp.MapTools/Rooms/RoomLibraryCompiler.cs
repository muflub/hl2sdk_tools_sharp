//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapTools.Rooms;

/// <summary>How one room of a library compile ended.</summary>
public sealed class RoomCompileOutcome
{
    internal RoomCompileOutcome(int index, LibraryRoom room, RoomObject? compiled, Exception? error, RoomNav? nav = null)
    {
        Index = index;
        Room = room;
        Compiled = compiled;
        Error = error;
        Nav = nav;
    }

    /// <summary>
    /// The room's navigation at turn 0, or null when it failed or the
    /// library builds none (<see cref="RoomLibraryCompileSettings.Nav"/>).
    /// </summary>
    public RoomNav? Nav { get; }

    /// <summary>The room's place in the library, from zero.</summary>
    public int Index { get; }

    /// <summary>The room as the library gave it.</summary>
    public LibraryRoom Room { get; }

    /// <summary>The compiled room, or null when it failed.</summary>
    public RoomObject? Compiled { get; }

    /// <summary>
    /// Why the room failed, or null when it compiled: a
    /// <see cref="RoomLintException"/> (the room is not linkable), a
    /// <see cref="MapCompileException"/> (it does not compile as a map), a
    /// <see cref="LinkException"/>, or an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> from reading its content.
    /// </summary>
    public Exception? Error { get; }
}

/// <summary>What a library compile needs besides its rooms.</summary>
/// <param name="options">The vbsp switches every room compiles with.</param>
/// <param name="content">The game content every room reads from.</param>
public sealed class RoomLibraryCompileSettings(VbspOptions options, IContentFileSystem content)
{
    /// <summary>The vbsp switches every room compiles with.</summary>
    public VbspOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The game content every room reads from.</summary>
    public IContentFileSystem Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>
    /// The collision cooker every room shares, or null for rooms without
    /// collision. The caller owns it and disposes it after the compile.
    /// </summary>
    /// <remarks>
    /// Shared because both cookers are safe to share: the managed one keeps
    /// its scratch per thread and no other state, so rooms cooking at once
    /// get the bytes each would get alone; the native one runs every call on
    /// its one cooker thread, whoever asks.
    /// </remarks>
    public ICollisionCooker? CollisionCooker { get; init; }

    /// <summary>
    /// The library's navigation settings (<see cref="NavSettings.FromLibrary"/>),
    /// or null to build none. With settings, each room's
    /// <see cref="RoomPois"/> are taken out of its VMF before it compiles and
    /// its navigation is built from its compile, on the room's own task;
    /// without, the points are still taken out (they are never entities of
    /// the map) and are dropped.
    /// </summary>
    public NavSettings? Nav { get; init; }

    /// <summary>
    /// How much of the machine the whole library may use: <c>-threads</c>.
    /// </summary>
    /// <remarks>
    /// A ceiling for the run, not per room: see <see cref="RoomLibraryCompiler"/>.
    /// </remarks>
    public CompileParallelism Parallelism { get; init; } = CompileParallelism.Default;

    /// <summary>
    /// For the facts: runs as a room starts, before its compile, with its
    /// index. Lets a fact hold one room back until another has finished, so
    /// "delivered in library order" is tested on the schedule that could
    /// break it rather than on whatever the machine does. Null in every real
    /// compile.
    /// </summary>
    internal Func<int, CancellationToken, ValueTask>? BeforeRoomProbe { get; init; }

    /// <summary>For the facts: runs as a room's compile ends, before its outcome is delivered. Null in every real compile.</summary>
    internal Action<int>? RoomCompiledProbe { get; init; }

    /// <summary>For the facts: sees the run's shared material store, to check it is emptied at the end. Null in every real compile.</summary>
    internal Action<SharedMaterialFacts>? MaterialsProbe { get; init; }

    /// <summary>For the facts: sees the thread pool the run made for itself, to check it is gone at the end. Null in every real compile.</summary>
    internal Action<CompilePool>? PoolProbe { get; init; }
}

/// <summary>
/// Compiles every room of a library, several at once, and hands each
/// outcome back in library order: what <c>ssmap room</c> runs, and what a
/// host calls to do the same.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why concurrent.</b> A room is a small map: a few dozen milliseconds of
/// vbsp and vvis, most of it serial (the load, the models, the lint). Inside
/// one room there is almost nothing for a second thread to do, so compiling
/// rooms one after another left <c>-threads</c> without effect: a 1024-room
/// library took as much CPU time as wall time. Rooms share nothing mutable
/// (each has its own <see cref="VbspContext"/>), so they can run side by side.
/// </para>
/// <para>
/// <b>One thread pool for the whole library, and every room on it.</b> The
/// run makes one <see cref="CompilePool"/> of <c>-threads</c> threads (or
/// uses the one the host lends in <see cref="CompileParallelism.Pool"/>) and
/// runs up to that many rooms at once on it (<see cref="CompilePool.ForAsync"/>).
/// Each room's compile gets the same pool as its parallelism, at the full
/// degree, so its vbsp models, its tree forks, its vvis flow and its managed
/// collision cooks are pool jobs too. The alternative, rooms in parallel with
/// each room at degree one, would be simpler to reason about but leaves
/// cores idle on the library's last and largest rooms, and each degree-one
/// room would still start a private one-thread pool for its serial model
/// queue. A shared pool avoids both: the pool has a fixed number of threads,
/// which is the most the library ever runs, whatever the rooms ask for,
/// and it hands its threads out a chunk at a time, round-robin over its jobs,
/// so eight rooms' vvis runs share the cores instead of oversubscribing them.
/// When one big room is left at the end, its jobs get every thread.
/// </para>
/// <para>
/// <b>Library order, whatever the finishing order.</b> Rooms finish when they
/// finish. Their outcomes are held until every room before them has been
/// delivered, then handed to the caller's callback one at a time, never two
/// at once, in the library's order. A host that prints a line per room (as
/// <c>ssmap room</c> does) therefore prints the same lines in the same order
/// at any thread count. An outcome is dropped as soon as it is delivered, so
/// the run holds only the rooms finished ahead of a slower one before them.
/// </para>
/// <para>
/// <b>Failures stay with their room.</b> A room that does not lint, does not
/// compile or cannot read its content is an outcome with an
/// <see cref="RoomCompileOutcome.Error"/>, and the others compile on, as
/// they did when rooms ran one at a time. Anything else (a cancellation, a
/// bug, a callback that throws) stops the run: no further room starts, the
/// ones running finish, those before it in the library are still delivered,
/// and the exception comes out of <see cref="CompileAsync"/>. Rooms compile
/// on the caller's token, not the loop's, so one room's crash does not cancel
/// the rooms before it; a serial run would have reported them.
/// </para>
/// <para>
/// <b>Material facts are read once.</b> The run makes one
/// <see cref="SharedMaterialFacts"/> over the content, and every room's
/// context reads through it: the kit's VMTs and base textures are read once
/// for the library, not once per room. It lives exactly as long as the run
/// and is emptied at the end.
/// </para>
/// <para>
/// <b>Nothing outlives the call.</b> Whether the run completes, fails or is
/// cancelled, it has, by the time it returns: waited for every room it
/// started, emptied the material store, and disposed its own pool, joining
/// the pool's threads. The cooker and the content are the caller's, and are
/// left as they were.
/// </para>
/// </remarks>
public static class RoomLibraryCompiler
{
    /// <summary>Compiles every room of a library.</summary>
    /// <param name="rooms">The library's rooms, in library order (<see cref="RoomLibraryVmf.Split"/>).</param>
    /// <param name="settings">The switches, content, cooker and parallelism.</param>
    /// <param name="roomFinished">
    /// Called once per room, in library order, one call at a time, with the
    /// room's outcome. It runs on one of the compile's threads, so it should
    /// return promptly.
    /// </param>
    /// <param name="cancellationToken">Cancels every room.</param>
    /// <returns>A task that completes once every room has been delivered.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException">The token fired; rooms not yet delivered never will be.</exception>
    public static async Task CompileAsync(
        IReadOnlyList<LibraryRoom> rooms,
        RoomLibraryCompileSettings settings,
        Func<RoomCompileOutcome, CancellationToken, ValueTask> roomFinished,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roomFinished);
        cancellationToken.ThrowIfCancellationRequested();

        CompileParallelism parallelism = settings.Parallelism;
        if (settings.Nav is { } nav && rooms.Count > 0)
        {
            // The library's grid and voxel either fit or every room would fail
            // the same way: refused once, before any room compiles.
            _ = nav.CellVoxels(rooms[0].Definition.CellSize);
        }

        using SharedMaterialFacts materials = new(settings.Content);
        settings.MaterialsProbe?.Invoke(materials);
        CompilePool? owned = null;
        try
        {
            CompilePool? pool = parallelism.Pool;
            if (pool is null && parallelism.Scheduler is null)
            {
                pool = owned = new CompilePool(Math.Max(1, parallelism.MaxDegree));
                settings.PoolProbe?.Invoke(owned);
            }

            TaskScheduler? scheduler = pool?.Scheduler ?? parallelism.Scheduler;
            CompileParallelism roomParallelism = pool is null ? parallelism : parallelism with { Pool = pool };

            // The managed cooker's cooks run where the rooms do, on the same
            // budget, through a per-run view; the cooker itself is untouched.
            ICollisionCooker? cooker = settings.CollisionCooker is ManagedCollisionCooker managed && scheduler is not null
                ? managed.On(scheduler)
                : settings.CollisionCooker;

            int degree = Math.Max(1, Math.Min(parallelism.MaxDegree, pool?.Degree ?? int.MaxValue));
            Delivery delivery = new(rooms.Count, roomFinished);
            try
            {
                // The loop's own token is not passed on: it also fires when
                // another room's body throws, and a room before that one in
                // the library should still finish and be delivered, as it
                // would have been one room at a time. The loop still stops
                // starting rooms when it fires.
                async ValueTask CompileOneAsync(int index, CancellationToken loopToken)
                {
                    if (settings.BeforeRoomProbe is { } before)
                    {
                        await before(index, cancellationToken).ConfigureAwait(false);
                    }

                    RoomCompileOutcome outcome = await CompileRoomAsync(
                        index, rooms[index], settings, materials, cooker, roomParallelism, cancellationToken).ConfigureAwait(false);
                    settings.RoomCompiledProbe?.Invoke(index);
                    await delivery.FinishAsync(outcome, cancellationToken).ConfigureAwait(false);
                }

                if (pool is not null)
                {
                    await pool.ForAsync(rooms.Count, degree, CompileOneAsync, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ParallelOptions options = new()
                    {
                        MaxDegreeOfParallelism = degree,
                        TaskScheduler = scheduler,
                        CancellationToken = cancellationToken,
                    };
                    await System.Threading.Tasks.Parallel.ForAsync(0, rooms.Count, options, CompileOneAsync).ConfigureAwait(false);
                }
            }
            finally
            {
                delivery.Dispose();
            }
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Whether an exception is one room's failure, reported with that room,
    /// rather than the run's: the same set <c>ssmap room</c> reported per
    /// room when it compiled them one at a time.
    /// </summary>
    internal static bool IsRoomFailure(Exception exception) =>
        exception is MapCompileException or RoomLintException or LinkException
            or IOException or UnauthorizedAccessException;

    private static async Task<RoomCompileOutcome> CompileRoomAsync(
        int index,
        LibraryRoom room,
        RoomLibraryCompileSettings settings,
        SharedMaterialFacts materials,
        ICollisionCooker? cooker,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        // A context per room: a context carries the tables one compile fills
        // (texinfos, planes, the loading map), which two rooms must not
        // share. The content, the material facts and the cooker are
        // read-only or thread-safe, and are.
        VbspContext context = new(settings.Options, settings.Content, materials)
        {
            // mapbase: the room's name, lowercased
#pragma warning disable CA1308 // strlwr
            MapBase = room.Definition.Name.ToLowerInvariant(),
#pragma warning restore CA1308
            CollisionCooker = cooker,
            Parallelism = parallelism,
        };

        try
        {
            (VmfDocument document, IReadOnlyList<AuthoredPoi> pois) = RoomPois.Extract(room.Document);
            RoomObject compiled = await RoomCompiler
                .CompileAsync(document, room.Definition, context, cancellationToken).ConfigureAwait(false);
            RoomNav? nav = settings.Nav is { } navSettings
                ? RoomNavBuilder.Build(room.Definition, compiled.Bsp, pois, room.Role, navSettings, cancellationToken)
                : null;
            return new RoomCompileOutcome(index, room, compiled, null, nav);
        }
        catch (Exception exception) when (IsRoomFailure(exception))
        {
            return new RoomCompileOutcome(index, room, null, exception);
        }
    }

    /// <summary>
    /// Holds finished rooms until every room before them has been handed
    /// over, and hands them over one at a time.
    /// </summary>
    /// <remarks>
    /// Each room stores its outcome and then tries to deliver, under a gate
    /// only one room holds at a time. Whoever holds it delivers every room
    /// that is ready from the next undelivered one on. A room that stores
    /// its outcome just after the holder looked is not lost: it then takes
    /// the gate itself and finds its own outcome ready.
    /// </remarks>
    private sealed class Delivery(int count, Func<RoomCompileOutcome, CancellationToken, ValueTask> deliver) : IDisposable
    {
        private readonly RoomCompileOutcome?[] _finished = new RoomCompileOutcome?[count];
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _next;

        // Set when the callback throws: the run is ending with that
        // exception, and nothing after the room it failed on is handed over,
        // as nothing after it would have been one room at a time.
        private bool _broken;

        public async ValueTask FinishAsync(RoomCompileOutcome outcome, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _finished[outcome.Index], outcome);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (!_broken && _next < _finished.Length && Volatile.Read(ref _finished[_next]) is { } ready)
                {
                    // Dropped before it is delivered: the run keeps no room
                    // it has already handed over.
                    _finished[_next] = null;
                    _next++;
                    _broken = true;
                    await deliver(ready, cancellationToken).ConfigureAwait(false);
                    _broken = false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            Array.Clear(_finished);
            _gate.Dispose();
        }
    }
}
