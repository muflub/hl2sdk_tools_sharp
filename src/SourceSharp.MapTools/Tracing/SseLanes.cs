//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SourceSharp.MapTools.Tracing;

/// <summary>
/// The SSE lane operations <see cref="KdRayTracer"/> uses whose rules are
/// x86's own, so that they give x86's answer on any instruction set.
/// </summary>
/// <remarks>
/// <para>
/// Most of the tracer's arithmetic is written against <see cref="Vector128"/>
/// directly: an add, a compare or an AND is the same IEEE operation on every
/// CPU, and on x86 the JIT emits the SSE instruction. These are the exceptions.
/// <c>maxps</c> and <c>minps</c> return their SECOND operand when either is a
/// NaN or both are zeros, which <see cref="Vector128.Max{T}"/> and
/// <see cref="Vector128.Min{T}"/> do not promise, and the traversal does meet
/// those cases: a zero direction component gives infinite and NaN clip
/// distances. <c>andnps</c> inverts its FIRST operand, the opposite of
/// <see cref="Vector128.AndNot{T}"/>.
/// </para>
/// <para>
/// Each has an SSE form, taken when the CPU has SSE, and a portable form that
/// spells out the x86 rule for other CPUs. The tests check each portable form
/// against its SSE form on x86.
/// </para>
/// </remarks>
internal static class SseLanes
{
    /// <summary><c>movmskps</c>: the four lanes' sign bits, lane 0 lowest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MoveMask(Vector128<float> v) => (int)v.ExtractMostSignificantBits();

    /// <summary><c>maxps</c>: <c>a &gt; b ? a : b</c> in each lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Max(Vector128<float> a, Vector128<float> b) =>
        Sse.IsSupported ? Sse.Max(a, b) : MaxPortable(a, b);

    /// <summary><c>minps</c>: <c>a &lt; b ? a : b</c> in each lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Min(Vector128<float> a, Vector128<float> b) =>
        Sse.IsSupported ? Sse.Min(a, b) : MinPortable(a, b);

    /// <summary><see cref="Max"/> without SSE.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MaxPortable(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);

    /// <summary><see cref="Min"/> without SSE.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MinPortable(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);

    /// <summary><c>andnps</c>: <c>~mask &amp; b</c>, in x86's operand order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> AndNot(Vector128<float> mask, Vector128<float> b) =>
        Vector128.AndNot(b, mask);

    /// <summary><c>_MM_TRANSPOSE4_PS</c>: rows in, columns out. Moves bits only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transpose(
        Vector128<float> r0, Vector128<float> r1, Vector128<float> r2, Vector128<float> r3,
        out Vector128<float> c0, out Vector128<float> c1, out Vector128<float> c2, out Vector128<float> c3)
    {
        if (!Sse.IsSupported)
        {
            TransposePortable(r0, r1, r2, r3, out c0, out c1, out c2, out c3);
            return;
        }

        Vector128<float> t0 = Sse.UnpackLow(r0, r1);
        Vector128<float> t1 = Sse.UnpackLow(r2, r3);
        Vector128<float> t2 = Sse.UnpackHigh(r0, r1);
        Vector128<float> t3 = Sse.UnpackHigh(r2, r3);
        c0 = Sse.MoveLowToHigh(t0, t1);
        c1 = Sse.MoveHighToLow(t1, t0);
        c2 = Sse.MoveLowToHigh(t2, t3);
        c3 = Sse.MoveHighToLow(t3, t2);
    }

    /// <summary><see cref="Transpose"/> without SSE.</summary>
    public static void TransposePortable(
        Vector128<float> r0, Vector128<float> r1, Vector128<float> r2, Vector128<float> r3,
        out Vector128<float> c0, out Vector128<float> c1, out Vector128<float> c2, out Vector128<float> c3)
    {
        c0 = Vector128.Create(r0.GetElement(0), r1.GetElement(0), r2.GetElement(0), r3.GetElement(0));
        c1 = Vector128.Create(r0.GetElement(1), r1.GetElement(1), r2.GetElement(1), r3.GetElement(1));
        c2 = Vector128.Create(r0.GetElement(2), r1.GetElement(2), r2.GetElement(2), r3.GetElement(2));
        c3 = Vector128.Create(r0.GetElement(3), r1.GetElement(3), r2.GetElement(3), r3.GetElement(3));
    }
}
