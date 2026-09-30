//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Zip;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Zip;

/// <summary>
/// The CRC-32's eight-at-a-time loop is the bit-by-bit definition, at every
/// length and alignment; continuing a CRC-32 over more bytes is the CRC-32
/// of all of them.
/// </summary>
public sealed class Crc32AppendTests
{
    /// <summary><c>Append(Compute(a), b)</c> is <c>Compute(a + b)</c>, and appending to 0 is computing.</summary>
    [Fact]
    public void AppendingIsComputingTheWhole()
    {
        byte[] a = "123456789"u8.ToArray();
        byte[] b = "the quick brown fox"u8.ToArray();
        Assert.Equal(0xCBF43926u, Crc32.Compute(a));
        Assert.Equal(Crc32.Compute([.. a, .. b]), Crc32.Append(Crc32.Compute(a), b));
        Assert.Equal(Crc32.Compute(a), Crc32.Append(0, a));
        Assert.Equal(0u, Crc32.Compute([]));
        Assert.Equal(Crc32.Compute(a), Crc32.Append(Crc32.Compute(a), []));
    }

    /// <summary>
    /// Every length from 0 to 70 (the eight-byte loop, the byte loop after
    /// it, and both) at three offsets gives the bit-by-bit CRC of the
    /// reflected polynomial.
    /// </summary>
    [Fact]
    public void TheSlicedLoopIsTheBitwiseDefinition()
    {
        byte[] data = new byte[80];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 151) + 7);
        }

        for (int offset = 0; offset < 3; offset++)
        {
            for (int length = 0; length <= 70; length++)
            {
                ReadOnlySpan<byte> span = data.AsSpan(offset, length);
                Assert.Equal(Bitwise(span), Crc32.Compute(span));
                Assert.Equal(Bitwise(span), Crc32.Append(Crc32.Compute(span[..(length / 3)]), span[(length / 3)..]));
            }
        }

        static uint Bitwise(ReadOnlySpan<byte> bytes)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
