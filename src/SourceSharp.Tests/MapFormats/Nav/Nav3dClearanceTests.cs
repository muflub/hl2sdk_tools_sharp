//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>
/// Clearance records: the bytes, the checks a reader runs on them, and the
/// corner arithmetic every clearance query is made of.
/// </summary>
public sealed class Nav3dClearanceTests
{
    private static readonly float Inf = float.NegativeInfinity;

    [Fact]
    public void ARecordEncodesItsCountsAndEntriesInOrder()
    {
        byte[] record = Nav3dClearance.Encode(
            [new(Inf, 100), new(8, 50), new(20, Inf)],
            [new(0, new Nav3dCorner(Inf, Inf)), new(0, new Nav3dCorner(4, 30)), new(2, new Nav3dCorner(1, Inf))],
            [1, 3]);
        Assert.Equal(8 + (3 * 8) + (3 * 12) + (2 * 4), record.Length);
        Assert.Equal(record.Length, Nav3dClearance.Length(record));
        Assert.Equal((3, 3, 2), (Nav3dClearance.StaticCount(record), Nav3dClearance.DynamicCount(record), Nav3dClearance.BrushCount(record)));
        Assert.Equal(new Nav3dCorner(8, 50), Nav3dClearance.Corner(record, 1));
        Assert.Equal(new Nav3dDynamicCorner(2, new Nav3dCorner(1, Inf)), Nav3dClearance.Dynamic(record, 2));
        Assert.Equal(3, Nav3dClearance.Brush(record, 1));
        Assert.Null(Nav3dClearance.Problem(record, 3, 4));
        Assert.False(Nav3dClearance.IsBlocked(record));
        Assert.True(Nav3dClearance.IsBlocked(Nav3dClearance.BlockedRecord));
        Assert.Equal(Nav3dClearance.BlockedRecord.ToArray(), Nav3dClearance.Encode([new(Inf, Inf)], [], []));
        Assert.False(Nav3dClearance.IsBlocked(Nav3dClearance.Encode([], [], [])));
    }

    public static TheoryData<string, string> Problems => new()
    {
        { "short", "is cut short" },
        { "long", "runs past its section" },
        { "reserved", "non-zero reserved word" },
        { "nan", "not a number" },
        { "infinite", "infinitely far" },
        { "stair", "not a staircase" },
        { "obstacle", "names obstacle 5 of 3" },
        { "order", "dynamic corners out of order" },
        { "brush", "names brush 9 of 4" },
        { "brushes", "out of order" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void AMalformedRecordSaysWhatIsWrong(string fault, string expected)
    {
        byte[] Raw(ushort statics, ushort dynamics, ushort brushes, params float[] words)
        {
            byte[] bytes = new byte[8 + (words.Length * 4)];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), statics);
            BitConverter.TryWriteBytes(bytes.AsSpan(2), dynamics);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), brushes);
            for (int i = 0; i < words.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(8 + (i * 4)), words[i]);
            }

            return bytes;
        }

        float I(int value) => BitConverter.Int32BitsToSingle(value);
        byte[] record = fault switch
        {
            "short" => [1, 0, 0],
            "long" => Raw(2, 0, 0, 1, 2),
            "reserved" => [.. Raw(0, 0, 0)[..6], 1, 0],
            "nan" => Raw(1, 0, 0, float.NaN, 1),
            "infinite" => Raw(1, 0, 0, float.PositiveInfinity, 1),
            "stair" => Raw(2, 0, 0, 1, 10, 2, 10),
            "obstacle" => Raw(0, 1, 0, I(5), 1, 1),
            "order" => Raw(0, 2, 0, I(1), 1, 1, I(0), 1, 1),
            "brush" => Raw(0, 0, 1, I(9)),
            "brushes" => Raw(0, 0, 2, I(2), I(1)),
            _ => throw new InvalidOperationException(fault),
        };
        Assert.Contains(expected, Nav3dClearance.Problem(record, 3, 4), StringComparison.Ordinal);
    }

    [Fact]
    public void EncodingAMalformedRecordOrTooManyEntriesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Nav3dClearance.Encode([new(2, 10), new(1, 5)], [], []));
        Assert.Throws<ArgumentException>(() => Nav3dClearance.Encode(new Nav3dCorner[70000], [], []));
    }

    /// <summary>
    /// A corner blocks an agent when it is wider than the width threshold and
    /// reaches above the top threshold, by more than the touching tolerance:
    /// both strictly, so an agent exactly at a threshold (plus the tolerance)
    /// fits.
    /// </summary>
    [Fact]
    public void ACornerBlocksAnAgentWiderAndTallerThanItsThresholds()
    {
        const double E = NavBrush.Epsilon;
        Nav3dCorner corner = new(16, 88);
        Assert.False(Nav3dClearance.Blocks(corner, 16, 100, 32));
        Assert.False(Nav3dClearance.Blocks(corner, 16 + (E / 2), 100, 32));
        Assert.True(Nav3dClearance.Blocks(corner, 16 + (2 * E), 100, 32));
        Assert.False(Nav3dClearance.Blocks(corner, 20, 56, 32));
        Assert.False(Nav3dClearance.Blocks(corner, 20, 56 + (E / 2), 32));
        Assert.True(Nav3dClearance.Blocks(corner, 20, 56 + (2 * E), 32));
        Assert.True(Nav3dClearance.Blocks(new(Inf, Inf), 0, 0, 32));
        Assert.False(Nav3dClearance.Blocks(new(Inf, 40), 0, 7.9, 32));
    }

    [Fact]
    public void HeadRoomAndWidthRoomAreTheLeastThresholdOfTheBlockingCorners()
    {
        const double E = NavBrush.Epsilon;
        byte[] record = Nav3dClearance.Encode([new(Inf, 100), new(8, 50), new(20, Inf)], [new(0, new Nav3dCorner(4, 30))], []);
        Assert.Equal(100 + E - 32, Nav3dClearance.HeadRoom(record, 0, 32, []), 12);
        Assert.Equal(50 + E - 32, Nav3dClearance.HeadRoom(record, 10, 32, []), 12);
        Assert.Equal(double.NegativeInfinity, Nav3dClearance.HeadRoom(record, 21, 32, []));
        Assert.Equal(30 + E - 32, Nav3dClearance.HeadRoom(record, 5, 32, [true]), 12);
        Assert.Equal(50 + E - 32, Nav3dClearance.HeadRoom(record, 10, 32, [false]), 12);
        Assert.Equal(double.PositiveInfinity, Nav3dClearance.HeadRoom(Nav3dClearance.Encode([], [], []), 5, 32, []));

        Assert.Equal(20 + E, Nav3dClearance.WidthRoom(record, 0, 32, []), 12);
        Assert.Equal(8 + E, Nav3dClearance.WidthRoom(record, 30, 32, []), 12);
        Assert.Equal(double.NegativeInfinity, Nav3dClearance.WidthRoom(record, 80, 32, []));
        Assert.Equal(4 + E, Nav3dClearance.WidthRoom(record, 0.5, 32, [true]), 12);
        Assert.Equal(double.PositiveInfinity, Nav3dClearance.WidthRoom(Nav3dClearance.Encode([], [], []), 5, 32, []));

        Assert.False(Nav3dClearance.CornersBlock(record, 4, 0, 32, []));
        Assert.True(Nav3dClearance.CornersBlock(record, 4.5, 0, 32, [true]));
        Assert.False(Nav3dClearance.CornersBlock(record, 4.5, 0, 32, [false]));
        Assert.False(Nav3dClearance.CornersBlock(record, 4.5, 0, 32, [false, true]));
    }
}
