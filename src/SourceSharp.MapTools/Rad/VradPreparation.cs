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
public sealed class VradPreparation
{
    private int _taken;

    internal VradPreparation(
        List<(string Name, byte[] Bytes)> radFiles,
        RadLightFile firstTexlights,
        IRayTracer tracer,
        IReadOnlyList<CompileDiagnostic> diagnostics)
    {
        RadFiles = radFiles;
        FirstTexlights = firstTexlights;
        Tracer = tracer;
        Diagnostics = diagnostics;
    }

    /// <summary>The tracer the lighting will use.</summary>
    public IRayTracer Tracer { get; }

    /// <summary>What the load had to say, in the order the whole compile says it.</summary>
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }

    internal List<(string Name, byte[] Bytes)> RadFiles { get; }

    internal RadLightFile FirstTexlights { get; }

    // One lighting per preparation: the first pass's density edit has already
    // been made to the map it was prepared from.
    internal bool TryTake() => Interlocked.Exchange(ref _taken, 1) == 0;
}
