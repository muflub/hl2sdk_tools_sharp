//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Compile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Rad;

using Xunit;

using static SourceSharp.Tests.MapTools.Compile.MapCompilerTests;

namespace SourceSharp.Tests.MapTools.Compile;

/// <summary>
/// The public entry points a host awaits for a whole tool run hand the
/// result back on a fresh thread-pool stack, not on the worker the run
/// finished on, so the finished run's frames -- and every state machine they
/// keep alive -- are gone by the time the host resumes.
/// </summary>
/// <remarks>
/// <para>
/// Without the hand-off, the host resumes inline, more than a hundred frames
/// deep inside the leaf-ambient stage's batch driver, and the runtime has not
/// yet cleared any of those async methods' state: the compile's scratch is
/// reachable until the host yields. A service looping on
/// <c>await CompileAsync</c> starts its next compile on top of the last one.
/// </para>
/// <para>
/// Every fact runs its body through <see cref="HostStack.AsServiceAsync"/>,
/// off xUnit's synchronization context, as a service has none: that is the
/// case where the continuation runs inline, and xUnit's context would
/// otherwise post it elsewhere and hide the stack the host really gets.
/// </para>
/// </remarks>
public sealed class HostHandoffTests
{
    [Fact]
    public Task ACompileHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());

            CompileResult result = await MapCompiler.CompileAsync(Request(files, content), null);

            HostStack.AssertOffCompileStack();
            Assert.True(result.Succeeded);
        });
    }

    [Fact]
    public Task ACompilesScratchIsCollectableBeforeTheHostYields()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            // The tracer factory is handed the compile's shadow casters and makes
            // the compile's tracer; it keeps both only weakly. Both are the
            // compile's to drop, and neither is in the result.
            WeakTracerFactory factory = new();
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());

            CompileResult result = await MapCompiler.CompileAsync(
                Request(files, content) with { TracerFactory = factory }, null);

            // No await between here and the checks: this is still the
            // continuation the compile handed back.
            Assert.True(result.Succeeded);
            Assert.True(HostStack.Collected(factory.Casters!), "the compile's shadow casters are still reachable from its stack");
            Assert.True(HostStack.Collected(factory.Tracer!), "the compile's tracer is still reachable from its stack");
        });
    }

    [Fact]
    public Task AFailedCompileHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            WeakTracerFactory factory = new();
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
            InvalidDataException planted = new("planted failure");

            Exception? thrown = null;
            try
            {
                _ = await MapCompiler.CompileAsync(
                    Request(files, content) with { TracerFactory = factory },
                    new ActAt(p => p.Stage == Vrad.FacelightsStage && p.Done == 1, () => throw planted));
            }
            catch (InvalidDataException ex)
            {
                thrown = ex;
                HostStack.AssertOffCompileStack();
                Assert.True(HostStack.Collected(factory.Casters!), "the failed compile's shadow casters are still reachable from its stack");
            }

            Assert.Same(planted, thrown);
        });
    }

    [Fact]
    public Task EachStageHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
            VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
            MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));

            VbspResult vbsp = await Vbsp.CompileAsync(map, context);
            HostStack.AssertOffCompileStack();

            BspData bsp = vbsp.Bsp!;
            PortalFile prt = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
            CompileParallelism degree = new() { MaxDegree = 2 };
            _ = await Vvis.ComputeAsync(bsp, PortalSet.FromPortalFile(prt), new VisContext { Parallelism = degree });
            HostStack.AssertOffCompileStack();

            VradContext rad = new()
            {
                Options = VradOptions.Default with { Bounces = 0 },
                MapName = "room",
                Content = content,
                Parallelism = degree,
            };
            _ = await Vrad.LightAsync(bsp, rad);
            HostStack.AssertOffCompileStack();
        });
    }

    [Fact]
    public Task TheSplitStagesHandTheHostBackOffTheirOwnStacks()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
            VbspContext context = new(VbspOptions.Default, content) { MapBase = "room" };
            MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/room.vmf"));
            VbspResult vbsp = await SurfaceContentVbsp.CompileAsync(map, context);
            HostStack.AssertOffCompileStack();

            BspData bsp = vbsp.Bsp!;
            PortalFile prt = await PortalFile.ParseAsync(vbsp.Portals!.ToBytes(PortalLineEnding.CrLf));
            CompileParallelism degree = new() { MaxDegree = 2 };
            VisContext vis = new() { Parallelism = degree };
            VisFlow flow = await Vvis.FlowAsync(PortalSet.FromPortalFile(prt), default, vis);
            HostStack.AssertOffCompileStack();
            _ = await Vvis.FinishAsync(bsp, flow, vis);
            HostStack.AssertOffCompileStack();

            VradContext rad = new()
            {
                Options = VradOptions.Default with { Bounces = 0 },
                MapName = "room",
                Content = content,
                Parallelism = degree,
            };
            VradPreparation prepared = await Vrad.PrepareAsync(bsp, rad);
            HostStack.AssertOffCompileStack();
            _ = await Vrad.LightAsync(bsp, prepared, rad);
            HostStack.AssertOffCompileStack();
        });
    }

    [Fact]
    public Task AnEntityUpdateHandsTheHostBackOffItsOwnStack()
    {
        return HostStack.AsServiceAsync(async () =>
        {
            (InMemoryFileSystem files, IContentFileSystem content) = await DiskAsync(Room());
            VbspContext full = new(VbspOptions.Default, content) { MapBase = "room" };
            MapFile map = await new MapFileReader(full, files).LoadAsync(VPath.Create("maps/room.vmf"));
            BspData bsp = (await Vbsp.CompileAsync(map, full)).Bsp!;

            VbspContext onlyEnts = new(VbspOptions.Default with { OnlyEnts = true }, content) { MapBase = "room" };
            MapFile ents = await new MapFileReader(onlyEnts, files).LoadAsync(VPath.Create("maps/room.vmf"));
            _ = await Vbsp.UpdateAsync(bsp, ents, onlyEnts);
            HostStack.AssertOffCompileStack();
            _ = await SurfaceContentVbsp.UpdateAsync(bsp, ents, onlyEnts);
            HostStack.AssertOffCompileStack();
        });
    }

    /// <summary>
    /// Offers the compile a KD tracer over its own casters and keeps only
    /// weak references to what the compile owns: the casters it was handed
    /// and the tracer it made.
    /// </summary>
    private sealed class WeakTracerFactory : IGpuTracerFactory
    {
        public WeakReference? Casters { get; private set; }

        public WeakReference? Tracer { get; private set; }

        public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IRayTracer tracer = casters.BuildTracer();
            Casters = new WeakReference(casters);
            Tracer = new WeakReference(tracer);
            return ValueTask.FromResult(new GpuTracerOffer(tracer, null));
        }
    }
}

/// <summary>What a host sees of the stack it resumed on after a tool run.</summary>
internal static class HostStack
{
    // A continuation queued to the pool resumes about a dozen frames deep
    // (dispatch loop, the hand-off's state machine, the task's continuation
    // plumbing, this frame). Inline on a finished compile it is over a
    // hundred. The bound sits well clear of both.
    private const int MaxFrames = 40;

    // The one MapTools type allowed below the host: the hand-off itself,
    // whose state machine is what resumes the host.
    private const string HandoffType = "SourceSharp.MapTools.Parallel.HostHandoff";

    /// <summary>
    /// Runs <paramref name="body"/> the way a service would, on the thread
    /// pool with no synchronization context, so each await in it resumes
    /// where the awaited task completed.
    /// </summary>
    /// <param name="body">The fact's body.</param>
    /// <returns>The body's task.</returns>
    public static Task AsServiceAsync(Func<Task> body) => Task.Run(body);

    /// <summary>
    /// Asserts the caller runs on a shallow stack with no frame of the tool
    /// run under it except the hand-off.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AssertOffCompileStack()
    {
        StackTrace trace = new();
        Assembly tools = typeof(MapCompiler).Assembly;
        List<string> compileFrames = [];
        foreach (StackFrame frame in trace.GetFrames())
        {
            Type? type = frame.GetMethod()?.DeclaringType;
            Type? outer = type;
            while (outer?.DeclaringType is { } parent)
            {
                outer = parent;
            }

            if (type?.Assembly == tools && outer?.FullName != HandoffType)
            {
                compileFrames.Add(type.FullName + "." + frame.GetMethod()!.Name);
            }
        }

        Assert.True(
            compileFrames.Count == 0 && trace.FrameCount < MaxFrames,
            $"the host resumed {trace.FrameCount} frames deep, on {compileFrames.Count} of the tool's frames:\n"
            + string.Join("\n", compileFrames.Take(20)));
    }

    /// <summary>
    /// Whether the object <paramref name="reference"/> points at can be
    /// collected, without yielding the calling continuation.
    /// </summary>
    /// <remarks>
    /// The worker the run ended on is unwinding while the host resumes on
    /// another, so the first collection can still see its last frames; a few
    /// tries with a short sleep let it finish. Sleeping blocks, it does not
    /// yield: on a host resumed inline, the run's frames stay under this one
    /// however long it waits.
    /// </remarks>
    public static bool Collected(WeakReference reference)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!reference.IsAlive)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }
}
