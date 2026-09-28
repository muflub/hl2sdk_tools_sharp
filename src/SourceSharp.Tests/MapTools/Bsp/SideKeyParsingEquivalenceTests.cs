//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using SourceSharp.Tests.MapFormats.Text.Legacy;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// A side's <c>plane</c> and texture-axis readers, rewritten to scan in place
/// instead of splitting and cutting substrings, against the frozen copies in
/// <see cref="LegacyNumbers"/>: same success, same points, same bits -- and
/// on failure, the same points already written.
/// </summary>
public class SideKeyParsingEquivalenceTests
{
    /// <summary>Plane texts the old Split-based reader treated one way or another.</summary>
    public static TheoryData<string> AwkwardPlanes() =>
    [
        "(0 0 0) (1 0 0) (0 1 0)",
        "(-64 -64 64) (-64 64 64) (64 64 64)",
        "(0.5 -0.25 1e2) (1 2 3) (4 5 6)",
        "(0  0   0) (1 0 0) (0 1 0)",
        "( 0 0 0 ) (1 0 0) (0 1 0)",
        "(0\t0 0) (1 0 0) (0 1 0)",
        "(0 0\t0) (1 0 0) (0 1 0)",
        "(0 0) (1 0 0) (0 1 0)",
        "(0 0 0 0) (1 0 0) (0 1 0)",
        "(1 2 3) (4 5 6) (7 8)",
        "(1 2 3) (4 5 6)",
        "(1 2 3) (4 5 6) (7 8 9",
        "1 2 3 4 5 6 7 8 9",
        "",
        "()()()",
        "(a b c) (d e f) (g h i)",
        "(1 2 3)(4 5 6)(7 8 9)",
        "(1 2 3) junk (4 5 6) more (7 8 9) tail",
        "(-0 -0 -0) (0 0 0) (1 1 1)",
        "(0.70710678118654752 0.70710678118654752 0) (1 0 0) (0 1 0)",
        "(123456789012345678 1 1) (1 1 1) (1 1 1)",
        "( 1 2 3) (4 5 6) (7 8 9)",
    ];

    [Theory]
    [MemberData(nameof(AwkwardPlanes))]
    public void PlanePointsParseAsBefore(string value) => AssertSamePlane(value);

    /// <summary>Axis texts the old reader treated one way or another.</summary>
    public static TheoryData<string> AwkwardAxes() =>
    [
        "[1 0 0 0] 0.25",
        "[0 -1 0 -128] 0.25",
        "[0.707107 0.707107 0 32.5] 1",
        "[1 0 0 0]",
        "[1 0 0 0]   ",
        "[1 0 0 0] \t 0.5 \t",
        "[1 0 0 0] x",
        "[1 0 0 0] 0.25 extra",
        "[1 0 0] 0.25",
        "1 0 0 0 0.25",
        "[1 0 0 0 0.25",
        "[1 0 0 0] ] 0.25",
        "[1 0 0 0] 1e-1",
        "[-0 -0 -0 -0] -0",
        "",
        "[1 0 0 0] 0.25",
    ];

    [Theory]
    [MemberData(nameof(AwkwardAxes))]
    public void TextureAxesParseAsBefore(string value) => AssertSameAxis(value);

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public void EverySideOfTheSandboxMapParsesAsBefore() => AssertCorpus("maps/ss_sandbox.vmf");

    [RepoSourceFact("maps/sdk_ctf_2fort.vmf")]
    public void EverySideOfTheFullSizeMapParsesAsBefore() => AssertCorpus("maps/sdk_ctf_2fort.vmf");

    private static void AssertCorpus(string relative)
    {
        string? path = RepoSourceFactAttribute.Find(relative);
        if (path is null)
        {
            return;
        }

        VmfDocument document = VmfDocument.ParseAsync(File.ReadAllBytes(path)).AsTask().GetAwaiter().GetResult();
        int sides = 0;
        foreach (VmfChunk side in Sides(document.Chunks))
        {
            AssertSamePlane(side.GetValue("plane") ?? string.Empty);
            AssertSameAxis(side.GetValue("uaxis") ?? string.Empty);
            AssertSameAxis(side.GetValue("vaxis") ?? string.Empty);
            sides++;
        }

        Assert.True(sides > 0, "the map has sides to compare");
    }

    private static IEnumerable<VmfChunk> Sides(IEnumerable<VmfChunk> chunks)
    {
        foreach (VmfChunk chunk in chunks)
        {
            if (string.Equals(chunk.Name, "side", StringComparison.OrdinalIgnoreCase))
            {
                yield return chunk;
            }

            foreach (VmfChunk side in Sides(chunk.Chunks))
            {
                yield return side;
            }
        }
    }

    private static void AssertSamePlane(string value)
    {
        // Pre-filled alike, so a point the failing reader never reached is
        // compared too.
        Vec3[] expected = [new(9, 9, 9), new(9, 9, 9), new(9, 9, 9)];
        Vec3[] actual = [new(9, 9, 9), new(9, 9, 9), new(9, 9, 9)];

        bool expectedOk = LegacyNumbers.TryParsePlanePoints(value, expected);
        bool actualOk = MapFileLoader.TryParsePlanePoints(value, actual);

        Assert.Equal(expectedOk, actualOk);
        for (int i = 0; i < 3; i++)
        {
            AssertSameBits(expected[i], actual[i]);
        }
    }

    private static void AssertSameAxis(string value)
    {
        bool expectedOk = LegacyNumbers.TryParseAxis(value, out Vec3 expectedAxis, out float expectedShift, out float expectedScale);
        bool actualOk = MapFileLoader.TryParseAxis(value, out Vec3 actualAxis, out float actualShift, out float actualScale);

        Assert.Equal(expectedOk, actualOk);
        AssertSameBits(expectedAxis, actualAxis);
        Assert.Equal(BitConverter.SingleToInt32Bits(expectedShift), BitConverter.SingleToInt32Bits(actualShift));
        Assert.Equal(BitConverter.SingleToInt32Bits(expectedScale), BitConverter.SingleToInt32Bits(actualScale));
    }

    private static void AssertSameBits(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }
}
