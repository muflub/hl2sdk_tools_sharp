//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Outputs as the naming resolver reads them from a key's value on both
/// paths: their shape, the bytes they keep, the delays a fold composes, and
/// the resolved names found inside a longer value.
/// </summary>
public class RoomOutputTests
{
    /// <summary>ESC-separated and comma-separated outputs read and write back to the same bytes.</summary>
    [Theory]
    [InlineData("door\u001bOpen\u001b\u001b0\u001b-1", '\u001b')]
    [InlineData("door,Open,,1.5,1", ',')]
    [InlineData("lamp\u001bSetColor\u001b255,0,0\u001b0.25\u001b2", '\u001b')]
    public void AnOutputReadsAndWritesBack(string value, char separator)
    {
        Assert.True(RoomOutput.TryParse(value, out RoomOutput output));
        Assert.Equal(separator, output.Separator);
        Assert.Equal(value, output.Format());
        Assert.Equal(value.Contains(",1.5", StringComparison.Ordinal) ? 1.5f : value.Contains("0.25", StringComparison.Ordinal) ? 0.25f : 0f, output.DelaySeconds);
    }

    /// <summary>Anything not shaped like an output is an ordinary key.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("255 255 255")]
    [InlineData("a,b,c,d")]
    [InlineData("a,b,c,d,e,f")]
    [InlineData("a,b,c,soon,-1")]
    [InlineData("a,b,c,0,always")]
    [InlineData("a,b,c,NaN,-1")]
    [InlineData("a,b,c,Infinity,-1")]
    public void OtherValuesAreNotOutputs(string? value) => Assert.False(RoomOutput.TryParse(value, out _));

    /// <summary>A composed delay keeps either side's text when the other is zero, and is written shortest otherwise.</summary>
    [Theory]
    [InlineData("0", "1.50", "1.50")]
    [InlineData("0.5", "0", "0.5")]
    [InlineData("0.5", "0.25", "0.75")]
    [InlineData("1", "2", "3")]
    public void DelaysAdd(string caller, string callee, string sum)
    {
        RoomOutput.TryParse($"a,b,,{caller},-1", out RoomOutput first);
        RoomOutput.TryParse($"c,d,,{callee},-1", out RoomOutput second);
        Assert.Equal(sum, RoomOutput.AddDelays(first, second));
    }

    /// <summary>The resolved names inside a value: each token that starts with a resolved prefix at a separator.</summary>
    [Theory]
    [InlineData("OnUser1 c3r5_relay:Trigger::0:-1", "c3r5_relay")]
    [InlineData("c0r0_a,c1r1_b", "c0r0_a|c1r1_b")]
    [InlineData("C2R2_x\u001bmore", "C2R2_x")]
    [InlineData("abc3r5_x", "")]
    [InlineData("cxry_a", "")]
    [InlineData("", "")]
    public void ResolvedNamesAreFoundInsideAValue(string value, string names) =>
        Assert.Equal(names.Length == 0 ? [] : names.Split('|'), LevelReferences.ResolvedTokens(value));
}
