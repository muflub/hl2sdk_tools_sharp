//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Numerics;
using System.Runtime.CompilerServices;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// Arithmetic whose NaN has x86's bits on every CPU.
/// </summary>
/// <remarks>
/// <para>
/// When an operation on ordinary numbers has no answer (<c>0/0</c>), x86 SSE
/// returns its "default NaN" with the sign bit SET (<c>0xFFC00000</c> as a
/// float); ARM returns one with the sign bit CLEAR (<c>0x7FC00000</c>). Both are
/// NaN, but they are different bytes, and stock's cooker writes such a NaN
/// into <c>rotation_inertia</c> for a ledge with a zero-length edge (see
/// <see cref="Options.StockQuirk.CollisionInertiaZeroLengthEdge"/>). On arm64
/// the collision blob then differs from stock's in one sign bit.
/// </para>
/// <para>
/// So where the cooker can make that NaN, it takes x86's instead. A NaN that
/// was already an operand is left alone: both instruction sets pass an operand
/// NaN through unchanged.
/// </para>
/// </remarks>
internal static class X86Nan
{
    /// <summary><c>a / b</c>, with x86's default NaN when the divide makes one.</summary>
    /// <typeparam name="T">float or double.</typeparam>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Divide<T>(T a, T b)
        where T : IBinaryFloatingPointIeee754<T>
    {
        T q = a / b;
        return T.IsNaN(q) && !T.IsNaN(a) && !T.IsNaN(b) ? Default<T>() : q;
    }

    /// <summary>x86's default NaN: sign set, quiet bit set, payload zero.</summary>
    /// <typeparam name="T">float or double.</typeparam>
    /// <returns>The NaN.</returns>
    public static T Default<T>()
        where T : IBinaryFloatingPointIeee754<T>
    {
        if (typeof(T) == typeof(float))
        {
            return (T)(object)BitConverter.UInt32BitsToSingle(0xFFC00000u);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)BitConverter.UInt64BitsToDouble(0xFFF8000000000000ul);
        }

        throw new NotSupportedException($"no x86 default NaN for {typeof(T)}");
    }
}
