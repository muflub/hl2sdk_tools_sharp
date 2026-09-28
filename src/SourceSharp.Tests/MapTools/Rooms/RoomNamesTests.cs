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
/// A room name is one path segment on every host, and a single cell of a
/// level's grid: letters and digits of any script, <c>_</c>, <c>-</c> and
/// <c>.</c>, starting with a letter, a digit or <c>_</c>.
/// </summary>
public sealed class RoomNamesTests
{
    [Theory]
    [InlineData("hub", null)]
    [InlineData("salle-é", null)]
    [InlineData("_hall.2", null)]
    [InlineData("9lives", null)]
    [InlineData("", "is empty")]
    [InlineData("a/b", "contains '/'")]
    [InlineData("a\\b", "contains '\\'")]
    [InlineData("..", "starts with '.'")]
    [InlineData(".", "starts with '.'")]
    [InlineData("-", "starts with '-'")]
    [InlineData("~", "starts with '~'")]
    [InlineData("c:x", "contains ':'")]
    [InlineData("a\tb", "contains 'U+0009'")]
    [InlineData("a b", "contains 'U+0020'")]
    [InlineData("tee@90", "contains '@'")]
    [InlineData("a,b", "contains ','")]
    [InlineData("\u0001a", "starts with 'U+0001'")]
    public void ARoomNameIsOnePathSegmentAndOneCell(string name, string? problem)
    {
        string? found = RoomNames.Problem(name);
        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.NotNull(found);
            Assert.StartsWith(problem, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ANullNameIsAnArgumentError() =>
        Assert.Throws<ArgumentNullException>(() => RoomNames.Problem(null!));
}
