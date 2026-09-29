//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Final;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>What a <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/> compile reads, and how much machine it uses.</summary>
public sealed record VradContext
{
    /// <summary>What was asked for.</summary>
    public VradOptions Options { get; init; } = VradOptions.Default;

    /// <summary>
    /// The map's name: stock's <c>source</c> after <c>Q_FileBase</c>, i.e. the
    /// file name without directory or extension.
    /// </summary>
    /// <remarks>
    /// It names the level texlight file (<c>&lt;MapName&gt;.rad</c>), is the
    /// prefix <c>LightForTexture</c> strips from patched material names, and
    /// names the macro texture (<c>materials/macro/&lt;MapName&gt;/base.vtf</c>).
    /// </remarks>
    public string MapName { get; init; } = string.Empty;

    /// <summary>
    /// Everything the compile reads besides the map: <c>lights.rad</c>, the
    /// <c>-lights</c> file, <c>&lt;MapName&gt;.rad</c>, static prop models,
    /// macro textures. Null reads nothing, as an empty game would.
    /// </summary>
    /// <remarks>
    /// Stock reads <c>&lt;map&gt;.rad</c> from beside the <c>.bsp</c>, not from
    /// the game's search path. A host that wants that must layer the map's
    /// directory over the game content; <c>ssmap vrad</c> does.
    /// </remarks>
    public IContentFileSystem? Content { get; init; }

    /// <summary>How many workers.</summary>
    public CompileParallelism Parallelism { get; init; } = CompileParallelism.Default;

    /// <summary>Stage boundaries, as <c>(stage, 0, 1)</c> then <c>(stage, 1, 1)</c>.</summary>
    public IProgress<CompileProgress>? Progress { get; init; }

    /// <summary>
    /// The tracer, or null to build the managed KD-tree over the map's shadow
    /// casters (<c>g_RtEnv</c>). A supplied tracer must already hold the
    /// casters.
    /// </summary>
    public IRayTracer? Tracer { get; init; }

    /// <summary>
    /// The host's GPU-tracer factory, or null for the
    /// managed KD tracer alone. Unlike <see cref="Tracer"/> it is asked AFTER
    /// the shadow casters are loaded — the GPU scene needs them — so a host
    /// with <c>-gpu</c> supplies a factory rather than a tracer. An offered
    /// tracer is wrapped with the KD tracer (the line samplers keep CPU); a
    /// declined device costs one <see cref="VradCodes.GpuTracerDeclined"/>
    /// warning and the run proceeds on CPU, never a crash.
    /// </summary>
    public IGpuTracerFactory? GpuTracerFactory { get; init; }

    /// <summary>
    /// How many batches each face-lighting worker may keep traced and not yet
    /// resolved on a tracer that answers asynchronously (<c>-gpu_depth</c>),
    /// 1 to <see cref="Light.RadWorld.MaxFacelightPipelineDepth"/>; 0, the
    /// default, means <see cref="Light.RadWorld.DefaultFacelightPipelineDepth"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A throughput knob and nothing else: the lightmaps are the same bytes
    /// at every depth (<see cref="Light.RadWorld.LightFacesAsync"/> says
    /// why). Deeper keeps more rays queued on the device while the workers
    /// resolve and fill, at a few megabytes of pooled scratch per batch; 1
    /// lets each worker wait out every batch it traces.
    /// </para>
    /// <para>
    /// A tracer that answers inside the call, the CPU tracer, never has a
    /// batch in flight, so the depth changes nothing for it.
    /// </para>
    /// </remarks>
    public int GpuPipelineDepth { get; init; }

    /// <summary>
    /// Where static props' collision triangles come from, for their shadows;
    /// null for <see cref="NullPropCollisionSource"/> (stock's AABB fallback).
    /// </summary>
    public IPropCollisionSource? PropCollision { get; init; }

    /// <summary>The stages other lanes own; see <see cref="VradStages"/>.</summary>
    public VradStages Stages { get; init; } = VradStages.Default;

    /// <summary>
    /// The cross-compile bounce transfer cache, or null. Used only when vrad
    /// builds its own tracer (not with <see cref="Tracer"/>), since the key
    /// needs a digest of the scene the transfer rays are traced against.
    /// </summary>
    public Bounce.ITransferCache? TransferCache { get; init; }

    /// <summary>
    /// Makes the compile's scratch pool, or null for a plain new one. For the
    /// facts only: they keep a reference to the pool to check, after the
    /// compile, that every array went back and the pool ended empty. The
    /// compile owns and disposes whatever this returns, as it does its own.
    /// </summary>
    internal Func<Bounce.CompileScratchPool>? ScratchPoolFactory { get; init; }
}

/// <summary>One pass's counts.</summary>
/// <param name="Hdr">True for the HDR pass.</param>
/// <param name="World">Faces, patches, lights, rays.</param>
/// <param name="Final">The <c>FinalLightFace</c> counts; null when <c>RadWorld_Go</c> was skipped.</param>
/// <param name="LightDataSize">The lighting lump's size.</param>
public sealed record RadPassResult(
    bool Hdr,
    RadWorldStatistics World,
    FinalLightingStatistics? Final,
    int LightDataSize);

/// <summary>What a <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/> compile did.</summary>
/// <param name="Passes">One per range, LDR first.</param>
/// <param name="Diagnostics">Warnings and notes, in the order they arose.</param>
/// <param name="StagesNotYetPorted">
/// The stages the compile needed and this build does not have, by name; each
/// also has a <see cref="VradCodes.StageNotYetPorted"/> diagnostic.
/// </param>
public sealed record RadResult(
    IReadOnlyList<RadPassResult> Passes,
    IReadOnlyList<CompileDiagnostic> Diagnostics,
    IReadOnlyList<string> StagesNotYetPorted)
{
    /// <summary>
    /// What the tracer did over the whole compile, every pass: rays to the GPU
    /// and to the CPU by query kind, the GPU's slabs and busy time, and the
    /// time workers spent parked on batches in flight. Null only for a result
    /// made outside <see cref="Vrad"/>.
    /// </summary>
    /// <remarks>
    /// An init property rather than a positional one, so a host that builds
    /// results itself keeps compiling.
    /// </remarks>
    public Tracing.RayTraceReport? Tracing { get; init; }
}

/// <summary>Diagnostic codes <see cref="Vrad"/> reports.</summary>
public static class VradCodes
{
    /// <summary>A stage the compile needed is not ported yet; the output lacks it.</summary>
    public const string StageNotYetPorted = "VRAD0701";

    /// <summary>A texlight file could not be opened (stock's "Couldn't open texlight file").</summary>
    public const string TexlightFileMissing = "VRAD0702";

    /// <summary>A texlight redefined by a later file (stock's override/redundant/duplicate messages).</summary>
    public const string TexlightOverride = "VRAD0703";

    /// <summary>A warning from a lighting stage, in stock's words.</summary>
    public const string StageWarning = "VRAD0704";

    /// <summary><c>SampleRadial</c> fell off a face's grid (stock's "SampleRadial: Punting").</summary>
    public const string SampleRadialPunting = "VRAD0705";

    /// <summary>A style slot with no light, where stock reads a null pointer.</summary>
    public const string MissingStyleSlot = "VRAD0706";

    /// <summary>
    /// A requested GPU tracer was declined (no device, no package, a pin
    /// matching nothing, or a failed capability self-test); the run continues
    /// on the CPU KD tracer with the reason in the message.
    /// </summary>
    public const string GpuTracerDeclined = "VRAD0707";
}
