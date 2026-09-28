//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Asks the CPU to start loading a bit vector's words into cache before the
/// flow reads them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The portal flow's candidate test intersects the frame's
/// might-see words with the candidate's own vector (its flood, or its
/// finished <c>portalvis</c>), and on 2fort the load of the candidate's
/// first word was the single hottest instruction of the whole flow: the
/// vectors of thirteen thousand portals are tens of megabytes, a candidate is
/// any of them, and so its words are rarely in cache when the test reaches
/// them. A hint issued when the frame is entered for every candidate it will
/// test gives each load a head start of the work done on the candidates
/// before it.
/// </para>
/// <para>
/// <b>Why it cannot change an answer.</b> A prefetch is a hint: it reads no
/// value into the program, it cannot fault (an address that is no longer
/// mapped, or never was, is silently ignored), and the loads that follow see
/// exactly the memory they would have seen without it. That is also why the
/// address is taken with <c>Unsafe.AsPointer</c> and no pin:
/// if a collection moved the array between taking the address and issuing
/// the hint, the hint would warm the wrong line -- a missed optimisation,
/// never a wrong read.
/// </para>
/// <para>
/// <b>Why x86 only.</b> .NET exposes a prefetch instruction only through
/// <see cref="Sse"/>; arm64 has none in its intrinsics, so there this is a
/// no-op the JIT removes, and the flow runs exactly as it did. The branch on
/// <see cref="Sse.IsSupported"/> is a JIT-time constant, so neither CPU pays
/// for the other's side.
/// </para>
/// </remarks>
internal static class VisPrefetch
{
    /// <summary>The cache line size every CPU this runs on uses.</summary>
    internal const int LineBytes = 64;

    /// <summary>
    /// The most lines hinted for one vector. The hardware's own stream
    /// prefetcher takes over once a sequential walk has missed a line or two,
    /// so hinting a long extent in full would only spend issue slots.
    /// </summary>
    internal const int MaxLines = 4;

    /// <summary>Hints the first lines of some words into every level of cache.</summary>
    /// <param name="words">The words the flow is about to read.</param>
    /// <returns>How many lines were hinted: zero where the CPU has no prefetch instruction.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe int Lines(ReadOnlySpan<ulong> words)
    {
        if (!Sse.IsSupported || words.IsEmpty)
        {
            return 0;
        }

        byte* first = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(words));
        int bytes = words.Length * sizeof(ulong);
        int lines = 0;
        for (int offset = 0; offset < bytes && lines < MaxLines; offset += LineBytes)
        {
            Sse.Prefetch0(first + offset);
            lines++;
        }

        return lines;
    }
}
