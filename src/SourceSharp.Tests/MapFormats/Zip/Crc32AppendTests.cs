//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Zip;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Zip;

/// <summary>Continuing a CRC-32 over more bytes is the CRC-32 of all of them.</summary>
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
}
