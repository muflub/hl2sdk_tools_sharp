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
/// The level file: the schema read and written, and every refusal naming
/// the line and column of what it refused.
/// </summary>
public sealed class LevelYamlTests
{
    private const string Level = """
        # the sample level
        library: ../rooms.vmf
        rows: 3
        columns: 2
        grid:
          - [end@270, hall@90]   # north row
          - [tee,     ~]
          - [corner@180, cross]  # south row
        """;

    // ---- reading --------------------------------------------------------------

    /// <summary>
    /// Every key reads: the library as written, the counts, and the grid
    /// with its first line as the NORTH row, <c>~</c> as no room, and
    /// <c>@degrees</c> as quarter turns; each cell remembers where it was
    /// written.
    /// </summary>
    [Fact]
    public void ALevelReadsItsGridNorthRowFirst()
    {
        LevelGrid level = LevelYaml.Parse(Level, "sample");

        Assert.Equal("sample", level.Name);
        Assert.Equal("../rooms.vmf", level.Library);
        Assert.Equal((3, 2), (level.Rows, level.Columns));
        Assert.Equal(new LevelCell("corner", 2, 8, 6), level[0, 0]);
        Assert.Equal(new LevelCell("cross", 0, 8, 18), level[1, 0]);
        Assert.Equal(new LevelCell("tee", 0, 7, 6), level[0, 1]);
        Assert.Null(level[1, 1]);
        Assert.Equal(new LevelCell("end", 3, 6, 6), level[0, 2]);
        Assert.Equal(new LevelCell("hall", 1, 6, 15), level[1, 2]);
        Assert.Equal("line 6, column 15: ", level[1, 2]!.Where);
        Assert.Equal(string.Empty, new LevelCell("x", 0).Where);
        Assert.Equal(
            [(0, 0, "corner"), (1, 0, "cross"), (0, 1, "tee"), (0, 2, "end"), (1, 2, "hall")],
            level.Placed.Select(p => (p.X, p.Y, p.Cell.Room)));
    }

    /// <summary>
    /// It is YAML, not a look-alike: rows may be block sequences, cells and
    /// the library may be quoted, keys may come in any order, and a UTF-8
    /// room name reads as itself.
    /// </summary>
    [Fact]
    public void AnyYamlSpellingOfTheSameLevelReadsTheSame()
    {
        const string Block = """
            grid:
              -
                - "end@270"
                - 'hall@90'
              - [tee, null]
              - [corner@180, salle-é]
            columns: 2
            rows: 3
            library: "../rooms.vmf"
            """;

        LevelGrid level = LevelYaml.Parse(Block, "block");
        Assert.Equal("end", level[0, 2]!.Room);
        Assert.Equal(3, level[0, 2]!.Rotation);
        Assert.Equal("hall", level[1, 2]!.Room);
        Assert.Null(level[1, 1]);
        Assert.Equal("salle-é", level[1, 0]!.Room);
        Assert.Equal("../rooms.vmf", level.Library);
    }

    /// <summary>Every core-schema spelling of YAML's null, unquoted, is an empty cell.</summary>
    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    public void EverySpellingOfNullIsAnEmptyCell(string nil)
    {
        LevelGrid level = LevelYaml.Parse($"library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [tee, {nil}]\n", "nil");
        Assert.Equal("tee", level[0, 0]!.Room);
        Assert.Null(level[1, 0]);
    }

    /// <summary>
    /// A quoted <c>'~'</c> is a string in YAML, not null, so it is not an empty cell: it is
    /// read as a room name and refused as one.
    /// </summary>
    [Fact]
    public void AQuotedTildeIsNotAnEmptyCell()
    {
        LevelFileException error = Assert.Throws<LevelFileException>(
            () => LevelYaml.Parse("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [tee, '~']\n", "quoted"));
        Assert.Contains("the room name \"~\"", error.Message, StringComparison.Ordinal);
    }

    // ---- writing ----------------------------------------------------------------

    /// <summary>
    /// A written level is the schema's shape exactly — comments first,
    /// columns padded so the grid reads as a grid, the north row first, LF
    /// line ends — and reads back as the same level.
    /// </summary>
    [Fact]
    public void AWrittenLevelReadsBackIdentically()
    {
        LevelGrid level = LevelYaml.Parse(Level, "sample");
        string text = LevelYaml.Write(level, ["one", "two"]);

        Assert.Equal(
            "# one\n# two\nlibrary: ../rooms.vmf\nrows: 3\ncolumns: 2\ngrid:\n"
            + "  - [end@270,    hall@90]\n"
            + "  - [tee,        ~]\n"
            + "  - [corner@180, cross]\n",
            text);
        AssertSame(level, LevelYaml.Parse(text, "sample"));
        Assert.Equal(text, LevelYaml.Write(LevelYaml.Parse(text, "sample"), ["one", "two"]));
        Assert.StartsWith("library:", LevelYaml.Write(level), StringComparison.Ordinal);
    }

    /// <summary>
    /// A library path that would not read back as itself unquoted — a
    /// space, a quote, a backslash, a leading dash, a YAML indicator — is
    /// written double-quoted and escaped, and still reads back identically.
    /// </summary>
    [Theory]
    [InlineData("../rooms.vmf", "library: ../rooms.vmf")]
    [InlineData("C:/games/my rooms.vmf", "library: \"C:/games/my rooms.vmf\"")]
    [InlineData("say \"hi\".vmf", "library: \"say \\\"hi\\\".vmf\"")]
    [InlineData("-rooms.vmf", "library: \"-rooms.vmf\"")]
    [InlineData("#rooms.vmf", "library: \"#rooms.vmf\"")]
    [InlineData("a\\b\tc.vmf", "library: \"a\\\\b\\tc.vmf\"")]
    [InlineData("bell\u0007.vmf", "library: \"bell\\u0007.vmf\"")]
    [InlineData("trailing ", "library: \"trailing \"")]
    public void ALibraryPathIsQuotedWhenItMustBe(string library, string line)
    {
        LevelGrid level = new("l", library, 1, 1, [new LevelCell("hub", 0)]);
        string text = LevelYaml.Write(level);

        Assert.Contains(line + "\n", text, StringComparison.Ordinal);
        Assert.Equal(library, LevelYaml.Parse(text, "l").Library);
    }

    // ---- refusals -----------------------------------------------------------------

    /// <summary>Every way a file can fail to be a level, with the line and column of the node at fault.</summary>
    [Theory]
    [InlineData("", 1, 1, "the level file is empty")]
    [InlineData("rows: [1\n", 2, 1, "not YAML")]
    [InlineData("library: a\nlibrary: b\n", 2, 1, "not YAML: Duplicate key")]
    [InlineData("rows: 1\n---\nrows: 2\n", 3, 1, "a level file holds one YAML document")]
    [InlineData("- a\n", 1, 1, "a level is a mapping of library, rows, columns and grid")]
    [InlineData("size: 3\n", 1, 1, "unknown key \"size\"")]
    [InlineData("rows: 1\n", 1, 1, "the level has no library, columns, grid")]
    [InlineData("library: \"\"\nrows: 1\ncolumns: 1\ngrid: [[a]]\n", 1, 10, "library is empty")]
    [InlineData("library: [a]\nrows: 1\ncolumns: 1\ngrid: [[a]]\n", 1, 10, "library is a single value, not a sequence")]
    [InlineData("library: l\nrows: {a: 1}\ncolumns: 1\ngrid: [[a]]\n", 2, 7, "rows is a single value, not a mapping")]
    [InlineData("library: l\nrows: x\ncolumns: 1\ngrid: [[a]]\n", 2, 7, "rows is \"x\"; it is a whole number, at least 1")]
    [InlineData("library: l\nrows: 1\ncolumns: 0\ngrid: [[a]]\n", 3, 10, "columns is \"0\"")]
    [InlineData("library: l\nrows: -2\ncolumns: 1\ngrid: [[a]]\n", 2, 7, "rows is \"-2\"")]
    [InlineData("library: l\nrows: 100\ncolumns: 100\ngrid: []\n", 2, 7, "a 100x100 grid has more than 4096 cells")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid: a\n", 4, 7, "grid is a sequence of rows")]
    [InlineData("library: l\nrows: 2\ncolumns: 1\ngrid: [[a]]\n", 4, 7, "grid has 1 row(s); rows says 2")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - a\n", 5, 5, "row 1 of grid is not a sequence of cells")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a]\n", 5, 5, "row 1 of grid has 1 cell(s); columns says 2")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [[a]]\n", 5, 6, "a cell is a room name, name@rotation, or ~ for no room")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [a@45]\n", 5, 6, "the rotation \"45\" of \"a@45\" is not 0, 90, 180 or 270 degrees")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [a@360]\n", 5, 6, "the rotation \"360\"")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [a@-90]\n", 5, 6, "the rotation \"-90\"")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [a@]\n", 5, 6, "the rotation \"\" of \"a@\"")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [a@ninety]\n", 5, 6, "the rotation \"ninety\"")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [@90]\n", 5, 6, "the room name \"\" is empty")]
    [InlineData("library: l\nrows: 1\ncolumns: 1\ngrid:\n  - [my room]\n", 5, 6, "the room name \"my room\" contains 'U+0020'")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a, -]\n", 5, 9, "(an empty cell is written ~, YAML's null, not -)")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a, *]\n", 5, 9, "(an empty cell is written ~, YAML's null, not *)")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a, '*']\n", 5, 9, "* is not a cell; an empty cell is YAML's null, written ~")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a, '-']\n", 5, 9, "- is not a cell; an empty cell is YAML's null, written ~")]
    [InlineData("library: l\nrows: 1\ncolumns: 2\ngrid:\n  - [a, @]\n", 5, 9, "@ is not a cell; an empty cell is YAML's null, written ~")]
    public void ABadLevelIsRefusedWhereItIs(string text, int line, int column, string problem)
    {
        LevelFileException refused = Assert.Throws<LevelFileException>(() => LevelYaml.Parse(text, "l"));
        Assert.Contains(problem, refused.Problem, StringComparison.Ordinal);
        Assert.Equal((line, column), (refused.Line, refused.Column));
        Assert.StartsWith($"line {line}, column {column}: ", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The dash hint is only given when a bracketed row holds a lone dash.</summary>
    [Fact]
    public void TheDashHintNeedsALoneDashInARow()
    {
        LevelFileException other = Assert.Throws<LevelFileException>(() => LevelYaml.Parse("rows: [1\n", "l"));
        Assert.DoesNotContain("an empty cell", other.Problem, StringComparison.Ordinal);
        LevelFileException nested = Assert.Throws<LevelFileException>(() => LevelYaml.Parse("a: b: c\n", "l"));
        Assert.DoesNotContain("an empty cell", nested.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two message helpers' other roads: a line the text does not have
    /// gives no hint, and a reader message without a position prefix is
    /// kept whole.
    /// </summary>
    [Fact]
    public void TheMessageHelpersKeepWhatTheyCannotImprove()
    {
        Assert.Equal(string.Empty, LevelYaml.DashHint("a: [-]", 0));
        Assert.Equal(string.Empty, LevelYaml.DashHint("a: [-]", 2));
        Assert.Equal(" (an empty cell is written ~, YAML's null, not -)", LevelYaml.DashHint("a: [-]", 1));
        Assert.Equal("plain words", LevelYaml.Plain(new YamlDotNet.Core.YamlException("plain words")));
        Assert.Equal("(Line: 1", LevelYaml.Plain(new YamlDotNet.Core.YamlException("(Line: 1")));
    }

    private static void AssertSame(LevelGrid expected, LevelGrid actual)
    {
        Assert.Equal((expected.Library, expected.Rows, expected.Columns), (actual.Library, actual.Rows, actual.Columns));
        Assert.Equal(
            expected.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"),
            actual.Cells.Select(c => c is null ? "~" : $"{c.Room}@{c.Rotation}"));
    }
}
