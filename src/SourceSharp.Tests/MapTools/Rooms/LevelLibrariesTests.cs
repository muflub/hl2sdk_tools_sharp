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
/// A level of several libraries as text (the rooms design, 17.2 and 17.3):
/// the <c>libraries</c> and <c>aliases</c> keys, every level-file message by
/// its exact text, each way a cell resolves, the dotted-name guard, the
/// same-file check, and writing a level back.
/// </summary>
public sealed class LevelLibrariesTests
{
    private const string Head = "libraries:\n  base: ../rooms.vmf\n  caves: ../caves.vmf\n";

    /// <summary>The two libraries' rooms: <c>corner</c> in both, the rest in one each.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>> Rooms =
        [["corner", "cross", "tee"], ["corner", "hall", "pit"]];

    private static LevelGrid Parse(string text) => LevelYaml.Parse(text, "multi");

    private static string Level(string cells, string extra = "") =>
        $"{Head}rows: 1\ncolumns: {cells.Split(',').Length}\n{extra}grid:\n  - [{cells}]\n";

    private static string Problem(string text, IReadOnlyList<IReadOnlyList<string>>? rooms = null) =>
        Assert.Throws<LevelFileException>(() => LevelLibraries.Resolve(Parse(text), rooms ?? Rooms)).Message;

    private static string ParseProblem(string text) => Assert.Throws<LevelFileException>(() => Parse(text)).Message;

    /// <summary><c>libraries</c> reads as a mapping in the order written, and the level's library is the first one's path.</summary>
    [Fact]
    public void LibrariesAreReadInOrderWithTheirPlaces()
    {
        LevelGrid level = Parse(Level("base.corner, hall"));
        Assert.Equal(["base", "caves"], level.Libraries!.Select(l => l.Key));
        Assert.Equal(["../rooms.vmf", "../caves.vmf"], level.Libraries!.Select(l => l.Path));
        Assert.Equal((2, 3), (level.Libraries![0].Line, level.Libraries[0].Column));
        Assert.Equal("../rooms.vmf", level.Library);
        Assert.Empty(level.Aliases);
    }

    /// <summary>A <c>library</c> level has no libraries list: it reads exactly as before.</summary>
    [Fact]
    public void ALibraryLevelHasNoLibrariesList()
    {
        LevelGrid level = Parse(RoomHarness.LevelText("../rooms.vmf", "corner, tee@90"));
        Assert.Null(level.Libraries);
        Assert.Equal("../rooms.vmf", level.Library);
    }

    /// <summary>17.3, both: <c>library</c> and <c>libraries</c> together, whichever comes first.</summary>
    [Theory]
    [InlineData("library: a.vmf\nlibraries:\n  base: b.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n", 2)]
    [InlineData("libraries:\n  base: b.vmf\nlibrary: a.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n", 3)]
    public void BothLibraryKeysAreRefused(string text, int line)
    {
        Assert.Equal($"line {line}, column 1: a level names its libraries once: library or libraries, not both.", ParseProblem(text));
    }

    /// <summary>17.3, neither: today's missing-key message with the library entry spelt both ways.</summary>
    [Fact]
    public void NeitherLibraryKeyIsRefused()
    {
        Assert.Equal("line 1, column 1: the level has no library or libraries.", ParseProblem("rows: 1\ncolumns: 1\ngrid:\n  - [x]\n"));
    }

    /// <summary>17.3, unknown key: PR 8's text with <c>libraries</c> and <c>aliases</c> added.</summary>
    [Fact]
    public void AnUnknownKeyNamesTheLibraryKeys()
    {
        Assert.Equal(
            "line 5, column 1: unknown key \"librarys\"; a level has library (or libraries), rows, columns and grid,"
            + " and may have aliases, up, down, up_map, down_map, spawn and spawn_count.",
            ParseProblem(Head + "rows: 1\nlibrarys: x\n"));
    }

    /// <summary>17.3, shape: <c>libraries</c> that is not a mapping of scalars.</summary>
    [Theory]
    [InlineData("libraries: rooms.vmf\n", 1, 12)]
    [InlineData("libraries:\n  - rooms.vmf\n", 2, 3)]
    [InlineData("libraries:\n  base: [a, b]\n", 2, 9)]
    [InlineData("libraries:\n  [a]: rooms.vmf\n", 2, 3)]
    public void LibrariesOfAnotherShapeAreRefused(string text, int line, int column)
    {
        Assert.Equal(
            $"line {line}, column {column}: libraries is a mapping of a key to a room library VMF, like base: ../rooms.vmf.",
            ParseProblem(text + "rows: 1\ncolumns: 1\ngrid:\n  - [x]\n"));
    }

    /// <summary>17.3, empty: a mapping of nothing.</summary>
    [Fact]
    public void EmptyLibrariesAreRefused()
    {
        Assert.Equal("line 1, column 12: libraries names no library.", ParseProblem("libraries: {}\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n"));
    }

    /// <summary>17.3, key: a key that does not start with a letter or holds a dot or other mark.</summary>
    [Theory]
    [InlineData("1base")]
    [InlineData("ba.se")]
    [InlineData("_base")]
    [InlineData("ba se")]
    public void ABadKeyIsRefused(string key)
    {
        Assert.Equal(
            $"line 2, column 3: the library key \"{key}\" is not a key; a key starts with a letter and holds only letters, digits, '_' and '-'.",
            ParseProblem($"libraries:\n  \"{key}\": rooms.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n"));
    }

    /// <summary>A key may hold letters of any script, digits, <c>_</c> and <c>-</c> after its first letter.</summary>
    [Theory]
    [InlineData("base")]
    [InlineData("b2_x-y")]
    [InlineData("été")]
    public void AGoodKeyIsAKey(string key) => Assert.Null(LevelLibraries.KeyProblem(key));

    /// <summary>17.3, key case: two keys that differ only in case, at the second.</summary>
    [Fact]
    public void KeysDifferingOnlyInCaseAreRefused()
    {
        Assert.Equal(
            "line 3, column 3: the library keys \"base\" and \"Base\" differ only in case.",
            ParseProblem("libraries:\n  base: a.vmf\n  Base: b.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n"));
    }

    /// <summary>17.3, path: a key naming nothing.</summary>
    [Theory]
    [InlineData("  caves:\n")]
    [InlineData("  caves: \"  \"\n")]
    public void AnEmptyPathIsRefused(string entry)
    {
        Assert.Contains(
            "library caves is empty; it names a room library VMF.",
            ParseProblem("libraries:\n  base: a.vmf\n" + entry + "rows: 1\ncolumns: 1\ngrid:\n  - [x]\n"),
            StringComparison.Ordinal);
    }

    /// <summary>17.3, same file: two keys naming one file once resolved, at the second; different files pass.</summary>
    [Fact]
    public void TwoKeysNamingOneFileAreRefused()
    {
        LevelGrid level = Parse("libraries:\n  base: ../rooms.vmf\n  caves: ./../rooms.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [x]\n");
        LevelFileException refused = Assert.Throws<LevelFileException>(
            () => LevelLibraries.CheckFiles(level, p => p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p));
        Assert.Equal("line 3, column 3: libraries base and caves name the same file, ../rooms.vmf.", refused.Message);
        LevelLibraries.CheckFiles(Parse(Level("x")), p => p);
    }

    /// <summary>17.3, aliases shape: not a mapping, or an entry that is not two scalars.</summary>
    [Theory]
    [InlineData("aliases: C\n")]
    [InlineData("aliases:\n  - C\n")]
    [InlineData("aliases:\n  C: [a]\n")]
    [InlineData("aliases:\n  [C]: a\n")]
    public void AliasesOfAnotherShapeAreRefused(string aliases)
    {
        Assert.Contains(
            "aliases is a mapping of a short name to a room, like C: base.corner.",
            ParseProblem(Level("x", aliases)),
            StringComparison.Ordinal);
    }

    /// <summary>17.3, alias name: a dot, a mark, or a bad first character.</summary>
    [Theory]
    [InlineData("C.x")]
    [InlineData("-C")]
    [InlineData("C@1")]
    public void ABadAliasNameIsRefused(string alias)
    {
        Assert.Contains(
            $"the alias \"{alias}\" is not a name; an alias starts with a letter, a digit or '_' and holds only letters, digits, '_' and '-'.",
            ParseProblem(Level("x", $"aliases:\n  \"{alias}\": base.corner\n")),
            StringComparison.Ordinal);
    }

    /// <summary>An alias may start with a digit or <c>_</c>.</summary>
    [Theory]
    [InlineData("1C")]
    [InlineData("_C")]
    [InlineData("C-2")]
    public void AGoodAliasIsAName(string alias) => Assert.Null(LevelLibraries.AliasProblem(alias));

    /// <summary>17.3, alias turn: an alias's value with a rotation.</summary>
    [Fact]
    public void AnAliasWithATurnIsRefused()
    {
        Assert.Contains(
            "the alias \"C\" names \"base.corner@90\"; an alias names a room, and the cell gives the turn, like C@90.",
            ParseProblem(Level("x", "aliases:\n  C: base.corner@90\n")),
            StringComparison.Ordinal);
    }

    /// <summary>An alias's value is held to the room-name rule.</summary>
    [Fact]
    public void AnAliasNamingNoRoomNameIsRefused()
    {
        Assert.Contains("the room name \"a b\" contains 'U+0020'", ParseProblem(Level("x", "aliases:\n  C: \"a b\"\n")), StringComparison.Ordinal);
    }

    /// <summary>17.3, alias shadows: an alias spelt as a room of a listed library, named qualified.</summary>
    [Fact]
    public void AnAliasMayNotHideARoom()
    {
        Assert.Equal(
            "line 7, column 3: the alias \"hall\" is also the name of room caves.hall; an alias may not hide a room.",
            Problem(Level("x", "aliases:\n  hall: base.cross\n")));
    }

    /// <summary>In a <c>library</c> level an alias that hides a room is refused too, naming the room bare.</summary>
    [Fact]
    public void ALibraryLevelsAliasMayNotHideARoom()
    {
        LevelGrid level = Parse("library: rooms.vmf\naliases:\n  tee: cross\nrows: 1\ncolumns: 1\ngrid:\n  - [tee]\n");
        Assert.Equal(
            "line 3, column 3: the alias \"tee\" is also the name of room tee; an alias may not hide a room.",
            Assert.Throws<LevelFileException>(() => LevelLibraries.Resolve(level, [Rooms[0]])).Message);
    }

    /// <summary>An alias may equal a library key: a key only matters before a dot.</summary>
    [Fact]
    public void AnAliasMayBeSpeltAsAKey()
    {
        LevelGrid level = LevelLibraries.Resolve(Parse(Level("base, base.tee", "aliases:\n  base: caves.pit\n")), Rooms);
        Assert.Equal(["caves.pit", "base.tee"], level.Placed.Select(p => p.Cell.Room));
    }

    /// <summary>17.3, dotted name: a room of one library whose name starts with another's key and a dot, at that key, used or not.</summary>
    [Fact]
    public void ARoomReadingAsAnotherLibrarysIsRefused()
    {
        Assert.Equal(
            "line 3, column 3: room \"caves.x\" of library base reads as room \"x\" of library caves; rename the library key caves.",
            Problem(Level("hall"), [["corner", "caves.x"], ["hall"]]));
    }

    /// <summary>A dotted room name whose prefix is not a key, or is its own library's key, is no ambiguity.</summary>
    [Fact]
    public void ADottedRoomNameOfNoOtherKeyResolvesBare()
    {
        LevelGrid level = LevelLibraries.Resolve(Parse(Level("v2.hall, base.base.x")), [["v2.hall", "base.x"], ["pit"]]);
        Assert.Equal(["base.v2.hall", "base.base.x"], level.Placed.Select(p => p.Cell.Room));
    }

    /// <summary>17.3, qualified: a key's library lacks the room.</summary>
    [Fact]
    public void AQualifiedRoomTheLibraryLacksIsRefused()
    {
        Assert.Equal("line 7, column 16: library caves (../caves.vmf) has no room \"tee\".", Problem(Level("base.tee, caves.tee")));
    }

    /// <summary>17.3, bare, none: no library has it, the keys listed.</summary>
    [Fact]
    public void ABareRoomNoLibraryHasIsRefused()
    {
        Assert.Equal(
            "line 7, column 6: no library of the level has a room \"attic\"; its libraries are base and caves.",
            Problem(Level("attic")));
    }

    /// <summary>17.3, bare, several: two libraries, then three, every qualified spelling offered.</summary>
    [Fact]
    public void ABareRoomSeveralLibrariesHaveIsRefused()
    {
        Assert.Equal(
            "line 7, column 6: the room \"corner\" is in libraries base and caves; write base.corner or caves.corner, or name one with an alias.",
            Problem(Level("corner")));
        string three = "libraries:\n  a: a.vmf\n  b: b.vmf\n  c: c.vmf\nrows: 1\ncolumns: 1\ngrid:\n  - [corner]\n";
        Assert.Equal(
            "line 8, column 6: the room \"corner\" is in libraries a, b and c; write a.corner, b.corner or c.corner, or name one with an alias.",
            Problem(three, [["corner"], ["corner"], ["corner"]]));
    }

    /// <summary>
    /// Each resolution: qualified, an alias to a qualified name and to a
    /// bare one, a bare name one library has; each keeps its turn and where
    /// it was written, and an empty cell stays empty.
    /// </summary>
    [Fact]
    public void EveryCellResolvesToAQualifiedName()
    {
        LevelGrid level = LevelLibraries.Resolve(
            Parse(Level("caves.corner@90, C@180, P, hall, ~", "aliases:\n  C: base.corner\n  P: pit\n")), Rooms);
        Assert.Equal(
            [("caves.corner", 1), ("base.corner", 2), ("caves.pit", 0), ("caves.hall", 0)],
            level.Placed.Select(p => (p.Cell.Room, p.Cell.Rotation)));
        Assert.Equal(10, level.Placed.First().Cell.Line);
        Assert.Null(level[4, 0]);
        Assert.Equal(2, level.Aliases.Count);
    }

    /// <summary>An alias's own value resolves by the same rules, at the alias: a missing room, an ambiguous one.</summary>
    [Fact]
    public void AnAliasValueIsResolvedAtTheAlias()
    {
        Assert.Equal("line 7, column 3: library base (../rooms.vmf) has no room \"pit\".", Problem(Level("x", "aliases:\n  C: base.pit\n")));
        Assert.StartsWith("line 7, column 3: the room \"corner\" is in libraries base and caves", Problem(Level("x", "aliases:\n  C: corner\n")), StringComparison.Ordinal);
    }

    /// <summary>An alias naming another alias is a bare room name, not a chain.</summary>
    [Fact]
    public void AnAliasNeverNamesAnAlias()
    {
        Assert.Equal(
            "line 8, column 3: no library of the level has a room \"C\"; its libraries are base and caves.",
            Problem(Level("x", "aliases:\n  C: base.corner\n  D: C\n")));
    }

    /// <summary>A <c>library</c> level: its aliases replaced by what they name, every other cell as written, unchecked.</summary>
    [Fact]
    public void ALibraryLevelsAliasesAreReplaced()
    {
        LevelGrid level = Parse("library: rooms.vmf\naliases:\n  X: cross\nrows: 1\ncolumns: 2\ngrid:\n  - [X@90, attic]\n");
        LevelGrid resolved = LevelLibraries.Resolve(level, [Rooms[0]]);
        Assert.Equal([("cross", 1), ("attic", 0)], resolved.Placed.Select(p => (p.Cell.Room, p.Cell.Rotation)));
        Assert.Null(resolved.Libraries);
    }

    /// <summary>The room lists must match the level's libraries.</summary>
    [Fact]
    public void TheRoomListsMustBeTheLevels()
    {
        Assert.Throws<ArgumentException>(() => LevelLibraries.Resolve(Parse(Level("x")), [Rooms[0]]));
    }

    /// <summary>
    /// Writing: <c>libraries</c> where <c>library</c> goes, the aliases
    /// after <c>columns</c> and before PR 8's keys, and it reads back as
    /// the same level.
    /// </summary>
    [Fact]
    public void ALevelOfSeveralLibrariesIsWrittenAndReadBack()
    {
        LevelGrid level = Parse(Head + "rows: 1\ncolumns: 2\naliases:\n  C: base.corner\nup: none\ngrid:\n  - [C@90, hall]\n");
        string text = LevelYaml.Write(level);
        Assert.Equal(
            "libraries:\n  base: ../rooms.vmf\n  caves: ../caves.vmf\nrows: 1\ncolumns: 2\naliases:\n  C: base.corner\nup: none\ngrid:\n  - [C@90, hall]\n",
            text);
        LevelGrid back = Parse(text);
        Assert.Equal(level.Libraries, back.Libraries);
        Assert.Equal(level.Aliases.Select(a => (a.Name, a.Value)), back.Aliases.Select(a => (a.Name, a.Value)));
        Assert.Equal(level.Placed.Select(p => (p.Cell.Room, p.Cell.Rotation)), back.Placed.Select(p => (p.Cell.Room, p.Cell.Rotation)));
    }

    /// <summary>The shortest spelling: bare when one library has the room, qualified otherwise; a library level unchanged.</summary>
    [Fact]
    public void AResolvedLevelIsSpeltShortest()
    {
        LevelGrid resolved = LevelLibraries.Resolve(Parse(Level("base.corner, caves.hall@90, base.tee")), Rooms);
        LevelGrid shortest = LevelLibraries.Shorten(resolved, Rooms);
        Assert.Equal(["base.corner", "hall", "tee"], shortest.Placed.Select(p => p.Cell.Room));
        Assert.Contains("  - [base.corner, hall@90, tee]\n", LevelYaml.Write(shortest), StringComparison.Ordinal);

        // A bare spelling that would read as a qualified name (a room whose
        // name starts with its own library's key and a dot) stays qualified.
        LevelGrid dotted = LevelLibraries.Resolve(Parse(Level("base.base.x, T", "aliases:\n  T: base.tee\n")), [["base.x", "tee"], ["hall"]]);
        Assert.Equal(["base.base.x", "tee"], LevelLibraries.Shorten(dotted, [["base.x", "tee"], ["hall"]]).Placed.Select(p => p.Cell.Room));
        LevelGrid library = Parse(RoomHarness.LevelText("rooms.vmf", "corner"));
        Assert.Same(library, LevelLibraries.Shorten(library, [Rooms[0]]));
    }

    /// <summary>A qualified name splits at its first dot; a bare one does not split.</summary>
    [Fact]
    public void QualifiedNamesSplitAtTheFirstDot()
    {
        Assert.True(LevelLibraries.TrySplit("base.v2.hall", out string key, out string room));
        Assert.Equal(("base", "v2.hall"), (key, room));
        Assert.False(LevelLibraries.TrySplit("hall", out _, out string bare));
        Assert.Equal("hall", bare);
        Assert.Equal("base.hall", LevelLibraries.Qualified("base", "hall"));
    }

    /// <summary>A level keeps its libraries and aliases through other edits of the grid.</summary>
    [Fact]
    public void TheLibrariesSurviveAGridEdit()
    {
        LevelGrid level = Parse(Level("hall", "aliases:\n  C: base.corner\n"));
        LevelGrid edited = level.WithTransitions(new LevelTransitions { NoUp = true });
        Assert.Same(level.Libraries, edited.Libraries);
        Assert.Same(level.Aliases, edited.Aliases);
        Assert.Same(level.Libraries, edited.WithCells(edited.Cells).Libraries);
    }
}
