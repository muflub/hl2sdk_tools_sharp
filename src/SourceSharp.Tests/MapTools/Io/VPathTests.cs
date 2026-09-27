//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// <see cref="VPath"/>'s normalisation: separators, <c>.</c> and <c>..</c>,
/// the root rule, and that a path already normal is not rebuilt.
/// </summary>
public sealed class VPathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("materials/brick/wall.vmt", "materials/brick/wall.vmt")]
    [InlineData("materials\\brick\\wall.vmt", "materials/brick/wall.vmt")]
    [InlineData("/materials//brick/./wall.vmt/", "materials/brick/wall.vmt")]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("a/b/../../c", "c")]
    [InlineData("a/..", "")]
    [InlineData("./a/./", "a")]
    [InlineData("a/b\\..\\..\\c\\d/..", "c")]
    [InlineData("..a/b..", "..a/b..")]
    [InlineData("a/.../b", "a/.../b")]
    public void PathsAreNormalised(string input, string expected)
    {
        Assert.True(VPath.TryCreate(input, out VPath result));
        Assert.Equal(expected, result.Value);
        Assert.Equal(expected, VPath.Create(input).Value);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/../..")]
    [InlineData("a/b/../../../c")]
    public void WalkingAboveTheRootIsRefused(string input)
    {
        Assert.False(VPath.TryCreate(input, out VPath result));
        Assert.True(result.IsEmpty);
        ArgumentException e = Assert.Throws<ArgumentException>(() => VPath.Create(input));
        Assert.Contains("walks above its own root", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullAndNulAreRefused()
    {
        Assert.False(VPath.TryCreate(null, out _));
        Assert.False(VPath.TryCreate("a\0b", out _));
    }

    [Fact]
    public void ALongPathIsNormalisedThroughThePooledBuffer()
    {
        // Longer than the stack buffer, so the scratch is rented; the result
        // must be the same as for a short path, '..' included.
        string segment = new('s', 120);
        string input = $"{segment}\\{segment}/./x/../{segment}//{segment}/";
        string expected = $"{segment}/{segment}/{segment}/{segment}";
        Assert.True(input.Length > 256);
        Assert.Equal(expected, VPath.Create(input).Value);

        // And a refused one returns its rented scratch on the way out: a
        // second, valid long path afterwards still normalises correctly.
        Assert.False(VPath.TryCreate(string.Concat(Enumerable.Repeat("../", 100)), out _));
        Assert.Equal(expected, VPath.Create(input).Value);
    }

    [Fact]
    public void AnAlreadyNormalPathKeepsItsStringAndAllocatesNothing()
    {
        string input = "materials/concrete/floor01.vmt";
        Assert.Same(input, VPath.Create(input).Value);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = VPath.TryCreate(input, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ANonNormalPathAllocatesOnlyTheResult()
    {
        // The earlier shape built a list and a string per segment, then
        // joined them.
        string input = "materials\\concrete\\floor01.vmt";
        _ = VPath.Create(input);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = VPath.Create(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 1, (2 * input.Length) + 32);
    }
}
