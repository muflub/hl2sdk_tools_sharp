//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// SplitMix64: a seeded random sequence written out here, so a seed means the
/// same sequence on every runtime, CPU and operating system.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Random"/> is not used on purpose: its algorithm is the
/// runtime's to change, and a seeded level that came out differently after
/// a .NET update would break every level file and test that names a seed.
/// SplitMix64 is a published, fixed algorithm of three multiply-xorshift
/// steps on 64-bit integers, so it involves no floating point and no platform
/// library at all.
/// </para>
/// <para>
/// <see cref="Next(int)"/> draws without modulo bias: a raw value from the
/// top of the range that would favour the low results is drawn again, so
/// every result below the bound is exactly as likely as every other.
/// </para>
/// </remarks>
public sealed class SplitMix64
{
    private ulong _state;

    /// <summary>A sequence from its seed.</summary>
    /// <param name="seed">Any 64-bit value; equal seeds give equal sequences.</param>
    public SplitMix64(ulong seed) => _state = seed;

    /// <summary>The next 64 bits of the sequence.</summary>
    /// <returns>The value.</returns>
    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A uniform integer in <c>[0, bound)</c>.</summary>
    /// <param name="bound">One past the largest result; at least 1.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is less than 1.</exception>
    public int Next(int bound)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, 1);
        ulong b = (ulong)bound;

        // 2^64 mod b: the raw values at the top that would over-weight the
        // low results. Zero when b divides 2^64 (a power of two), and then
        // every raw value is fair.
        ulong excess = ((ulong.MaxValue % b) + 1) % b;
        if (excess == 0)
        {
            return (int)(NextUInt64() % b);
        }

        ulong limit = 0UL - excess;
        ulong raw;
        do
        {
            raw = NextUInt64();
        }
        while (raw >= limit);

        return (int)(raw % b);
    }

    /// <summary>Shuffles a list in place (Fisher–Yates), drawing from this sequence.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The list.</param>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
