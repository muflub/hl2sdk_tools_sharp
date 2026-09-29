//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapGen.Content;

using Xunit;

namespace SourceSharp.Tests.MapGen.Content;

/// <summary>
/// The stand-in meshes, and so the synthetic content built from them, are the
/// same bits on every OS and CPU.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic models are compile input for the pinned static-prop and
/// sandbox digests, so a platform-dependent vertex is a platform-dependent
/// golden. The cylinder used to take its ring from <see cref="MathF.Sin"/> and
/// <see cref="MathF.Cos"/>, which forward to the C library. On macOS arm64
/// that library returns <c>-0.38268346</c> (<c>0xBEC3EF16</c>) for the sine of
/// the oil drum's ninth ring angle, <c>2*pi*9/16</c>; the correctly rounded
/// value, which glibc, the Windows UCRT and Intel macOS all return, is
/// <c>-0.38268343</c> (<c>0xBEC3EF15</c>). One bit of one vertex moved the
/// drum's <c>.mdl</c>, <c>.vvd</c> and <c>.phy</c>, and with them every
/// static-prop lighting digest of the scene the drum stands in, which is why
/// that fact once carried an arm64 delta.
/// </para>
/// <para>
/// The ring now comes from <see cref="DetMathF"/>. On x86 hosts the bytes did
/// not change (the libraries there were already right for every angle the
/// content uses); on arm64 they became x86's.
/// </para>
/// </remarks>
public sealed class PrimitivesTests
{
    /// <summary>
    /// SHA-256 over every synthetic file, path then bytes, in ordinal path
    /// order. One value for every CPU and OS: CI runs it on AMD, Intel and
    /// arm64, and a runner that disagrees has found platform arithmetic in the
    /// generator.
    /// </summary>
    /// <remarks>
    /// Any intended change to the synthetic content moves it; recapture it
    /// from the failure message and check the other runners agree.
    /// </remarks>
    private const string ContentDigest = "CAE5A7195F7F8422B05A5E0B82210BA8884E62BAE3F5FEEF76D918FCF62971AA";

    [Fact]
    public void TheOilDrumsNinthRingVertexHasTheCorrectlyRoundedSine()
    {
        // The drum's ring: radius 14, 16 sides. Band vertex 2i is ring i.
        MeshSpec drum = Primitives.Cylinder(0, 14, 0, 46, 16);
        MeshVertex v = drum.Vertices[2 * 9];

        Assert.Equal(0xBEC3EF15u, BitConverter.SingleToUInt32Bits(v.Normal.Y));
        Assert.Equal(0xBF6C835Eu, BitConverter.SingleToUInt32Bits(v.Normal.X));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    public void EveryRingVertexIsTheCorrectlyRoundedCosineAndSine(int sides)
    {
        MeshSpec c = Primitives.Cylinder(0, 5, 0, 10, sides);

        // The band: two vertices a ring angle, sides + 1 angles.
        for (int i = 0; i <= sides; i++)
        {
            float a = 2 * MathF.PI * i / sides;
            (float sin, float cos) = (DetMathF.Sin(a), DetMathF.Cos(a));
            foreach (MeshVertex v in new[] { c.Vertices[2 * i], c.Vertices[(2 * i) + 1] })
            {
                Assert.Equal(BitConverter.SingleToUInt32Bits(cos), BitConverter.SingleToUInt32Bits(v.Normal.X));
                Assert.Equal(BitConverter.SingleToUInt32Bits(sin), BitConverter.SingleToUInt32Bits(v.Normal.Y));
                Assert.Equal(BitConverter.SingleToUInt32Bits(cos * 5), BitConverter.SingleToUInt32Bits(v.Position.X));
                Assert.Equal(BitConverter.SingleToUInt32Bits(sin * 5), BitConverter.SingleToUInt32Bits(v.Position.Y));
            }
        }

        // The caps: a centre and one vertex an angle, top then bottom.
        int at = 2 * (sides + 1);
        for (int cap = 0; cap < 2; cap++, at += sides + 1)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = 2 * MathF.PI * i / sides;
                MeshVertex v = c.Vertices[at + 1 + i];
                Assert.Equal(BitConverter.SingleToUInt32Bits(DetMathF.Cos(a) * 5), BitConverter.SingleToUInt32Bits(v.Position.X));
                Assert.Equal(BitConverter.SingleToUInt32Bits(DetMathF.Sin(a) * 5), BitConverter.SingleToUInt32Bits(v.Position.Y));
                Assert.Equal(BitConverter.SingleToUInt32Bits(0.5f + (DetMathF.Cos(a) / 2)), BitConverter.SingleToUInt32Bits(v.U));
                Assert.Equal(BitConverter.SingleToUInt32Bits(0.5f + (DetMathF.Sin(a) / 2)), BitConverter.SingleToUInt32Bits(v.V));
            }
        }
    }

    [Fact]
    public void TheSyntheticContentIsPinnedOnEveryCpu()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] bytes) in SyntheticContent.Build().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        Assert.Equal(ContentDigest, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
