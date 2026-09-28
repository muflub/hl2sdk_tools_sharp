//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Nav;

namespace SourceSharp.MapTools.Rooms;

/// <summary>How one room of a library build ended: packed (compiled or reused), or failed.</summary>
public sealed class RoomBuildOutcome
{
    internal RoomBuildOutcome(
        int index, LibraryRoom room, RoomPackItem? item, bool reused, int clusterCount, IReadOnlyList<string> nameWarnings, Exception? error)
    {
        Index = index;
        Room = room;
        Item = item;
        Reused = reused;
        ClusterCount = clusterCount;
        NameWarnings = nameWarnings;
        Error = error;
    }

    /// <summary>The room's place in the library, from zero.</summary>
    public int Index { get; }

    /// <summary>The room as the library gave it.</summary>
    public LibraryRoom Room { get; }

    /// <summary>The room's pack item, or null when it failed.</summary>
    public RoomPackItem? Item { get; }

    /// <summary>Whether the item came from the cache rather than a compile.</summary>
    public bool Reused { get; }

    /// <summary>The room's vis cluster count; zero when it failed.</summary>
    public int ClusterCount { get; }

    /// <summary>What the naming rule warned of when the room compiled (replayed on a reuse).</summary>
    public IReadOnlyList<string> NameWarnings { get; }

    /// <summary>Why the room failed, or null: as <see cref="RoomCompileOutcome.Error"/>.</summary>
    public Exception? Error { get; }
}

/// <summary>
/// Builds a library's pack items: every room the cache holds unchanged is
/// reused, every other room is compiled (<see cref="RoomLibraryCompiler"/>),
/// and each outcome is handed back in library order.
/// </summary>
/// <remarks>
/// <para>
/// What <c>ssmap room</c> runs, with or without <c>-incremental</c>, and
/// what a host calls to do the same. Without a cache it is
/// <see cref="RoomLibraryCompiler.CompileAsync"/> with each compiled room
/// turned into its pack item as it is delivered, as the verb always did.
/// </para>
/// <para>
/// <b>Lookups first, then the compile.</b> Every room is looked up before
/// any compiles, so the rooms that do compile are known at the start and run
/// side by side on the library's pool exactly as a full compile's would, just
/// fewer of them. The hits are held until their turn in library order, then
/// delivered between the compiled rooms, so a host that prints a line per
/// room prints the same lines in the same order as a clean run, whichever
/// rooms were reused. Holding the hits costs no more than the pack the host
/// is assembling from them.
/// </para>
/// <para>
/// <b>What is cached.</b> Each compiled room's item is handed to the cache
/// (<see cref="RoomCompileCache.Add"/>) as it is delivered; nothing reaches
/// the store until the host calls <see cref="RoomCompileCache.CommitAsync"/>
/// after writing the pack. A room that failed is never cached: it compiles
/// again next run and reports its failure again.
/// </para>
/// <para>
/// A cancellation or a failure of the run (not of a room) leaves as
/// <see cref="RoomLibraryCompiler.CompileAsync"/> does: rooms not yet
/// delivered never will be, and the exception comes out of
/// <see cref="BuildAsync"/>.
/// </para>
/// </remarks>
public static class RoomLibraryBuild
{
    /// <summary>Builds every room of a library, reusing what the cache holds.</summary>
    /// <param name="rooms">The library's rooms, in library order.</param>
    /// <param name="settings">The switches, content, cooker and parallelism the changed rooms compile with.</param>
    /// <param name="packOptions">How each room's navigation is stored in its pack item.</param>
    /// <param name="cache">The run's cache, or null to compile every room.</param>
    /// <param name="roomFinished">Called once per room, in library order, one call at a time.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>A task that completes once every room has been delivered.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="cache"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token fired.</exception>
    public static async Task BuildAsync(
        IReadOnlyList<LibraryRoom> rooms,
        RoomLibraryCompileSettings settings,
        RoomNavPackOptions packOptions,
        RoomCompileCache? cache,
        Func<RoomBuildOutcome, CancellationToken, ValueTask> roomFinished,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packOptions);
        ArgumentNullException.ThrowIfNull(roomFinished);
        cancellationToken.ThrowIfCancellationRequested();

        RoomCacheHit?[] hits = new RoomCacheHit?[rooms.Count];
        List<int> compile = [];
        for (int i = 0; i < rooms.Count; i++)
        {
            hits[i] = cache is null ? null : await cache.TryGetAsync(rooms[i], cancellationToken).ConfigureAwait(false);
            if (hits[i] is null)
            {
                compile.Add(i);
            }
        }

        // The next library index to deliver: every hit before a compiled
        // room goes out before it.
        int next = 0;
        async ValueTask DeliverHitsBeforeAsync(int end, CancellationToken token)
        {
            for (; next < end; next++)
            {
                RoomCacheHit hit = hits[next]!;
                hits[next] = null;
                await roomFinished(
                    new RoomBuildOutcome(next, rooms[next], hit.Item, reused: true, hit.ClusterCount, hit.NameWarnings, null), token)
                    .ConfigureAwait(false);
            }
        }

        if (compile.Count > 0)
        {
            RoomLibraryCompileSettings compileSettings = cache is null ? settings : settings.WithContent(cache.Content);
            await RoomLibraryCompiler.CompileAsync(
                [.. compile.Select(i => rooms[i])],
                compileSettings,
                async (outcome, token) =>
                {
                    int index = compile[outcome.Index];
                    await DeliverHitsBeforeAsync(index, token).ConfigureAwait(false);
                    RoomBuildOutcome built;
                    if (outcome.Compiled is { } compiled)
                    {
                        RoomPackItem item = await RoomPackItem.CreateAsync(compiled, packOptions, token).ConfigureAwait(false);
                        cache?.Add(outcome.Room, item, compiled.ClusterCount, compiled.NameWarnings);
                        built = new RoomBuildOutcome(
                            index, outcome.Room, item, reused: false, compiled.ClusterCount, compiled.NameWarnings, null);
                    }
                    else
                    {
                        built = new RoomBuildOutcome(index, outcome.Room, null, reused: false, 0, [], outcome.Error);
                    }

                    next = index + 1;
                    await roomFinished(built, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await DeliverHitsBeforeAsync(rooms.Count, cancellationToken).ConfigureAwait(false);
    }
}
