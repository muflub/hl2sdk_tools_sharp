//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// What <see cref="Vrad.PrepareAsync"/> loaded before any lighting: the
/// texlight files, the first pass's texlights and the tracer.
/// </summary>
/// <remarks>
/// <para>
/// When the context brought no tracer, the preparation owns the one vrad
/// built, and that can hold native resources: under a host's
/// <see cref="IGpuTracerFactory"/> it holds a GPU device. Ownership passes
/// to <see cref="Vrad.LightAsync(MapFormats.Bsp.BspData, VradPreparation, VradContext, CancellationToken)"/>,
/// which releases the tracer when the lighting ends, however it ends. A
/// preparation that is never lit must be disposed instead; disposing one
/// that was lit, or disposing twice, does nothing. A tracer the host passed
/// in <see cref="VradContext.Tracer"/> stays the host's, and neither the
/// preparation nor the lighting releases it.
/// </para>
/// <para>
/// A long-lived host runs many compiles in one process, so a preparation
/// it abandons (a chain whose vvis failed after vrad's load had finished)
/// would otherwise keep a device open for the life of the process.
/// </para>
/// </remarks>
public sealed class VradPreparation : IDisposable
{
    private const int Fresh = 0;
    private const int Taken = 1;
    private const int Released = 2;

    private int _state;

    internal VradPreparation(
        List<(string Name, byte[] Bytes)> radFiles,
        RadLightFile firstTexlights,
        IRayTracer tracer,
        bool ownsTracer,
        string? tracerDigest,
        IReadOnlyList<CompileDiagnostic> diagnostics)
    {
        TracerDigest = tracerDigest;
        RadFiles = radFiles;
        FirstTexlights = firstTexlights;
        Tracer = tracer;
        OwnsTracer = ownsTracer;
        Diagnostics = diagnostics;
    }

    /// <summary>The tracer the lighting will use.</summary>
    public IRayTracer Tracer { get; }

    /// <summary>What the load had to say, in the order the whole compile says it.</summary>
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }

    internal List<(string Name, byte[] Bytes)> RadFiles { get; }

    internal RadLightFile FirstTexlights { get; }

    // What the tracer traces against, for the transfer cache key: set only
    // when vrad built the tracer and the context carries a transfer cache.
    internal string? TracerDigest { get; }

    // Whether vrad built the tracer: then whoever holds the preparation, and
    // after it the lighting that takes it, must release the tracer.
    internal bool OwnsTracer { get; }

    /// <summary>
    /// Releases the tracer vrad built for this preparation, unless a
    /// lighting has taken the preparation (the lighting releases it then).
    /// Safe to call more than once, and after the lighting.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, Released, Fresh) == Fresh && OwnsTracer)
        {
            Vrad.ReleaseTracer(Tracer);
        }
    }

    // One lighting per preparation: the first pass's density edit has already
    // been made to the map it was prepared from. A disposed preparation has
    // released its tracer, so it cannot be lit either.
    internal void Take()
    {
        int was = Interlocked.CompareExchange(ref _state, Taken, Fresh);
        ObjectDisposedException.ThrowIf(was == Released, this);
        if (was == Taken)
        {
            throw new InvalidOperationException("a VradPreparation lights one map once");
        }
    }
}
