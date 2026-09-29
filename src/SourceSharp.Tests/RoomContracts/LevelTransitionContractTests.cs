//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.RegularExpressions;

using SourceSharp.RoomContracts;

using Xunit;

namespace SourceSharp.Tests.RoomContracts;

/// <summary>
/// <c>logic_level_transition</c>'s contract (the rooms design, 7.6): its
/// FGD held to its constants and to the design's text, the direction and
/// point-of-interest spellings.
/// </summary>
public sealed class LevelTransitionContractTests
{
    /// <summary>The FGD declares exactly the class's keys, inputs and outputs, and names the class.</summary>
    [Fact]
    public void TheFgdAndTheConstantsAgree()
    {
        HashSet<string> keys = [], inputs = [], outputs = [];
        foreach (Match match in Regex.Matches(LevelTransition.Fgd, @"^\s*(?:(?<io>input|output)\s+)?(?<name>\w+)\((?<type>\w+)\)", RegexOptions.Multiline))
        {
            (match.Groups["io"].Value switch
            {
                "input" => inputs,
                "output" => outputs,
                _ => keys,
            }).Add(match.Groups["name"].Value);
        }

        string[] expected = [LevelTransition.DirectionKey, LevelTransition.MapKey, LevelTransition.StartDisabledKey];
        Assert.Equal(expected.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [LevelTransition.DisableInput, LevelTransition.EnableInput, LevelTransition.TransitionInput],
            inputs.Order(StringComparer.Ordinal));
        Assert.Equal([LevelTransition.OnTransitionOutput], outputs);
        Assert.Contains($"= {LevelTransition.ClassName} :", LevelTransition.Fgd, StringComparison.Ordinal);
        Assert.Contains($"\"{LevelTransition.Spell(TransitionDirection.Up)}\" : \"Up\"", LevelTransition.Fgd, StringComparison.Ordinal);
        Assert.Contains($"\"{LevelTransition.Spell(TransitionDirection.Down)}\" : \"Down\"", LevelTransition.Fgd, StringComparison.Ordinal);
    }

    /// <summary>The design's FGD text for the class is the contract's, line for line.</summary>
    [RepoSourceFact("docs/rooms-full-features.md")]
    public void TheDesignsFgdIsTheContracts()
    {
        string doc = File.ReadAllText(RepoSourceFactAttribute.Find("docs/rooms-full-features.md")!);
        int start = doc.IndexOf("= logic_level_transition :", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = doc.IndexOf("```", start, StringComparison.Ordinal);
        string[] fgd = [.. LevelTransition.Fgd.Split('\n').Select(l => l.Trim())];
        foreach (string line in doc[start..end].Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line.StartsWith("= logic_level_transition", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Contains(line, fgd);
        }
    }

    /// <summary>The directions spell as the key writes them, parse ignoring case, and have their opposites.</summary>
    [Fact]
    public void DirectionsSpellParseAndTurnAround()
    {
        Assert.Equal(("up", "down"), (LevelTransition.Spell(TransitionDirection.Up), LevelTransition.Spell(TransitionDirection.Down)));
        Assert.True(LevelTransition.TryParse("UP", out TransitionDirection up));
        Assert.Equal(TransitionDirection.Up, up);
        Assert.True(LevelTransition.TryParse("down", out TransitionDirection down));
        Assert.Equal(TransitionDirection.Down, down);
        Assert.False(LevelTransition.TryParse("sideways", out _));
        Assert.False(LevelTransition.TryParse(null, out _));
        Assert.Equal(TransitionDirection.Down, LevelTransition.Opposite(TransitionDirection.Up));
        Assert.Equal(TransitionDirection.Up, LevelTransition.Opposite(TransitionDirection.Down));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelTransition.Spell((TransitionDirection)5));
    }

    /// <summary>The point-of-interest types the contract defines spell as <c>poi_type</c> writes them.</summary>
    [Fact]
    public void PoiTypesSpellAsTheKeyWritesThem()
    {
        Assert.Equal(("arrival", "spawn"), (LevelTransition.Spell(PoiType.Arrival), LevelTransition.Spell(PoiType.Spawn)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelTransition.Spell((PoiType)9));
        Assert.False(LevelTransition.Networked);
        Assert.Equal("transition", RoomLinkerNames.Transition);
    }
}
