//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Parallel;

/// <summary>
/// Hands a finished compile back to its host from a fresh thread-pool stack
/// rather than from the stack the compile finished on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A compile ends on whichever worker completed its
/// last task, and every <c>await</c> above that point, from the stage's own
/// batch driver up to the public entry point, resumes <em>inline</em> on
/// that worker: completing a task runs its continuations synchronously. So
/// without this hand-off, the host's code after
/// <c>await MapCompiler.CompileAsync(...)</c> runs on a thread-pool worker
/// more than a hundred frames deep, inside the leaf-ambient stage's batch
/// driver. Each of those frames is an async method's <c>MoveNext</c> that
/// has not returned yet, and the runtime clears an async method's state
/// (every hoisted local: the scene, the workers, the tracer, the BSP's
/// scratch) only once its <c>MoveNext</c> returns. Until the host itself
/// yields, the finished compile's whole working set stays reachable: about
/// 135 MB on the sandbox map and 400 MB on a 2fort-sized map, most of it
/// large-object heap. A host that loops <c>await CompileAsync</c> starts the
/// next compile on top of those frames, so two compiles' scratch are live at
/// once and the stack grows with each one.
/// </para>
/// <para>
/// <b>What it does.</b> After the work completes, successfully or not, the
/// continuation is forced onto the thread pool
/// (<see cref="ConfigureAwaitOptions.ForceYielding"/>). The worker the
/// compile ended on then unwinds, every frame returns and every state machine
/// on it is cleared, and the host resumes on a new work item a few frames
/// deep, holding only the result. A failure takes the same road, so a
/// host's <c>catch</c> is not left sitting on the failed compile's frames
/// either. It costs one thread-pool work item per call, nothing against a
/// compile, and it changes no output: it only moves where the caller
/// resumes.
/// </para>
/// <para>
/// <b>Where it goes.</b> On the public entry points a host awaits for a
/// whole tool run (the chain, each stage's public run, the room compiler,
/// the library build and the linker), not on internal helpers: inside a
/// compile, resuming inline is what keeps a stage's hand-off cheap, and the
/// frames are the compile's own until it returns.
/// </para>
/// </remarks>
internal static class HostHandoff
{
    /// <summary>
    /// Awaits <paramref name="work"/> and completes on a fresh thread-pool
    /// stack, with the same result or the same exception.
    /// </summary>
    /// <typeparam name="T">The work's result.</typeparam>
    /// <param name="work">The compile's task.</param>
    /// <returns>A task that completes with <paramref name="work"/>'s outcome, off the stack it completed on.</returns>
    public static async Task<T> ReturnAsync<T>(Task<T> work)
    {
        try
        {
            return await work.ConfigureAwait(false);
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
    }

    /// <summary>
    /// Awaits <paramref name="work"/> and completes on a fresh thread-pool
    /// stack, with the same outcome.
    /// </summary>
    /// <param name="work">The compile's task.</param>
    /// <returns>A task that completes with <paramref name="work"/>'s outcome, off the stack it completed on.</returns>
    public static async Task ReturnAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
    }
}
