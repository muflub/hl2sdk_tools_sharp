//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A level file that cannot be read: what is wrong, and where.
/// </summary>
public sealed class LevelFileException : Exception
{
    /// <summary>Names the problem at a place in the file.</summary>
    /// <param name="line">The 1-based line.</param>
    /// <param name="column">The 1-based column.</param>
    /// <param name="problem">What is wrong there.</param>
    public LevelFileException(int line, int column, string problem)
        : base(string.Create(CultureInfo.InvariantCulture, $"line {line}, column {column}: {problem}"))
    {
        Line = line;
        Column = column;
        Problem = problem;
    }

    /// <summary>The 1-based line the problem is on.</summary>
    public int Line { get; }

    /// <summary>The 1-based column the problem starts at.</summary>
    public int Column { get; }

    /// <summary>The problem, without its position.</summary>
    public string Problem { get; }
}

/// <summary>
/// The level file: a YAML map of a grid of rooms, read and written.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema</b>, every key required and no other allowed:
/// </para>
/// <code>
/// # the sample level
/// library: ../rooms.vmf        # the room library VMF, relative to this file
/// rows: 3                      # rows, south to north
/// columns: 3                   # columns, west to east
/// grid:                        # one line per row, NORTH row first, as a map is drawn
///   - [end@270,   hall@90,  corner@270]
///   - [tee@270,   cross,    ~]
///   - [corner@90, corner,   corner@90]
/// </code>
/// <para>
/// A cell is <c>~</c> for no room, or a room's library name, optionally
/// followed by <c>@</c> and its rotation in degrees counter-clockwise seen
/// from above: <c>0</c>, <c>90</c>, <c>180</c> or <c>270</c>. The grid is
/// written the way the level looks from above with north up, so its first
/// line is the northernmost row (<c>y = rows − 1</c>) and its last is row
/// <c>y = 0</c>; each line runs west to east. Joints are never written: two
/// sockets that face each other across a shared wall are joined, and every
/// other socket is capped (<see cref="LevelGrid.ToLayout"/>).
/// </para>
/// <para>
/// <b>Why <c>~</c> for an empty cell</b>: it is YAML's own spelling of
/// "nothing", and it is a plain value inside a flow sequence. A bare
/// <c>-</c>, which might read better, is not: YAML reads a <c>-</c> there as
/// a sequence indicator, and a standard reader refuses the row. A file that
/// tries it gets that refusal with a hint to write <c>~</c>.
/// </para>
/// <para>
/// <b>Reading</b> uses YamlDotNet's representation model, a standard YAML
/// reader, so anything YAML allows in the file is accepted (comments, quoted
/// scalars, block or flow sequences for the rows) and the schema is checked
/// on the nodes it builds. Every refusal is a <see cref="LevelFileException"/>
/// with the line and column of the node at fault, from YamlDotNet's marks.
/// The representation model needs no reflection, which keeps the NativeAOT
/// build trim-clean.
/// </para>
/// <para>
/// <b>Writing</b> is plain text in exactly the shape above, columns padded
/// so the grid reads as a grid, LF line ends and a final newline, so a
/// generated level is deterministic and diffable, and reads back as the
/// same grid.
/// </para>
/// </remarks>
public static class LevelYaml
{
    /// <summary>The key naming the room library.</summary>
    public const string LibraryKey = "library";

    /// <summary>The key giving the row count.</summary>
    public const string RowsKey = "rows";

    /// <summary>The key giving the column count.</summary>
    public const string ColumnsKey = "columns";

    /// <summary>The key holding the grid.</summary>
    public const string GridKey = "grid";

    /// <summary>The cell that holds no room.</summary>
    public const string Empty = "~";

    /// <summary>The largest grid a level file may describe, in cells: a guard, not a format limit.</summary>
    public const int MaxCells = 4096;

    /// <summary>Reads a level file.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="name">The level's name: by convention, the file's base name.</param>
    /// <returns>The grid.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="LevelFileException">The file is not YAML, or not a level.</exception>
    public static LevelGrid Parse(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            int line = (int)exception.Start.Line;
            throw new LevelFileException(
                line, (int)exception.Start.Column, $"not YAML: {Plain(exception)}{DashHint(text, line)}");
        }

        if (stream.Documents.Count == 0)
        {
            throw new LevelFileException(1, 1, "the level file is empty; it needs library, rows, columns and grid.");
        }

        if (stream.Documents.Count > 1)
        {
            YamlNode second = stream.Documents[1].RootNode;
            throw At(second, "a level file holds one YAML document.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw At(stream.Documents[0].RootNode, "a level is a mapping of library, rows, columns and grid.");
        }

        YamlScalarNode? library = null, rows = null, columns = null;
        YamlNode? grid = null;
        // A key given twice never gets here: the reader refuses a duplicate
        // key as it builds the mapping, and that refusal is the "not YAML"
        // above, at the second key.
        foreach ((YamlNode keyNode, YamlNode value) in root.Children)
        {
            string key = keyNode is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;
            switch (key)
            {
                case LibraryKey:
                    library = Scalar(value, LibraryKey);
                    break;
                case RowsKey:
                    rows = Scalar(value, RowsKey);
                    break;
                case ColumnsKey:
                    columns = Scalar(value, ColumnsKey);
                    break;
                case GridKey:
                    grid = value;
                    break;
                default:
                    throw At(keyNode, $"unknown key \"{key}\"; a level has {LibraryKey}, {RowsKey}, {ColumnsKey} and {GridKey}.");
            }
        }

        List<string> missing = [];
        if (library is null)
        {
            missing.Add(LibraryKey);
        }

        if (rows is null)
        {
            missing.Add(RowsKey);
        }

        if (columns is null)
        {
            missing.Add(ColumnsKey);
        }

        if (grid is null)
        {
            missing.Add(GridKey);
        }

        if (missing.Count > 0)
        {
            throw At(root, $"the level has no {string.Join(", ", missing)}.");
        }

        if (string.IsNullOrWhiteSpace(library!.Value))
        {
            throw At(library, $"{LibraryKey} is empty; it names the room library VMF.");
        }

        int rowCount = Count(rows!, RowsKey);
        int columnCount = Count(columns!, ColumnsKey);
        if ((long)rowCount * columnCount > MaxCells)
        {
            throw At(rows!, $"a {rowCount}x{columnCount} grid has more than {MaxCells} cells.");
        }

        if (grid is not YamlSequenceNode gridRows)
        {
            throw At(grid!, $"{GridKey} is a sequence of rows, one per row, north first.");
        }

        if (gridRows.Children.Count != rowCount)
        {
            throw At(gridRows, $"{GridKey} has {gridRows.Children.Count} row(s); {RowsKey} says {rowCount}.");
        }

        LevelCell?[] cells = new LevelCell?[rowCount * columnCount];
        for (int line = 0; line < rowCount; line++)
        {
            YamlNode rowNode = gridRows.Children[line];
            int y = rowCount - 1 - line;
            if (rowNode is not YamlSequenceNode row)
            {
                throw At(rowNode, $"row {line + 1} of {GridKey} is not a sequence of cells, like [cross, tee@90, ~].");
            }

            if (row.Children.Count != columnCount)
            {
                throw At(row, $"row {line + 1} of {GridKey} has {row.Children.Count} cell(s); {ColumnsKey} says {columnCount}.");
            }

            for (int x = 0; x < columnCount; x++)
            {
                cells[(y * columnCount) + x] = Cell(row.Children[x]);
            }
        }

        return new LevelGrid(name, library.Value!, rowCount, columnCount, cells);
    }

    /// <summary>Writes a level file: the schema's shape, deterministic, LF line ends.</summary>
    /// <param name="grid">The level.</param>
    /// <param name="comments">Lines to write first, each as a <c>#</c> comment.</param>
    /// <returns>The file text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> is null.</exception>
    public static string Write(LevelGrid grid, IEnumerable<string>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(grid);

        StringBuilder text = new();
        foreach (string comment in comments ?? [])
        {
            text.Append("# ").Append(comment).Append('\n');
        }

        text.Append(LibraryKey).Append(": ").Append(Quote(grid.Library)).Append('\n');
        text.Append(RowsKey).Append(": ").Append(grid.Rows.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(ColumnsKey).Append(": ").Append(grid.Columns.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(GridKey).Append(":\n");

        string[,] tokens = new string[grid.Columns, grid.Rows];
        int[] width = new int[grid.Columns];
        for (int y = 0; y < grid.Rows; y++)
        {
            for (int x = 0; x < grid.Columns; x++)
            {
                LevelCell? cell = grid[x, y];
                string token = cell is null
                    ? Empty
                    : cell.Rotation == 0
                        ? cell.Room
                        : string.Create(CultureInfo.InvariantCulture, $"{cell.Room}@{cell.Rotation * 90}");
                tokens[x, y] = token;
                width[x] = Math.Max(width[x], token.Length);
            }
        }

        for (int y = grid.Rows - 1; y >= 0; y--)
        {
            text.Append("  - [");
            for (int x = 0; x < grid.Columns; x++)
            {
                if (x == grid.Columns - 1)
                {
                    text.Append(tokens[x, y]);
                }
                else
                {
                    text.Append(tokens[x, y]).Append(',').Append(' ', width[x] - tokens[x, y].Length + 1);
                }
            }

            text.Append("]\n");
        }

        return text.ToString();
    }

    private static LevelCell? Cell(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            throw At(node, "a cell is a room name, name@rotation, or ~ for no room.");
        }

        string token = scalar.Value ?? string.Empty;
        if (token == Empty)
        {
            return null;
        }

        int at = token.IndexOf('@', StringComparison.Ordinal);
        string room = at < 0 ? token : token[..at];
        if (RoomNames.Problem(room) is { } problem)
        {
            throw At(scalar, $"the room name \"{room}\" {problem}.");
        }

        int rotation = 0;
        if (at >= 0)
        {
            string degrees = token[(at + 1)..];
            if (!int.TryParse(degrees, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value is not (0 or 90 or 180 or 270))
            {
                throw At(scalar, $"the rotation \"{degrees}\" of \"{token}\" is not 0, 90, 180 or 270 degrees.");
            }

            rotation = value / 90;
        }

        return new LevelCell(room, rotation, (int)scalar.Start.Line, (int)scalar.Start.Column);
    }

    private static YamlScalarNode Scalar(YamlNode node, string key) =>
        node as YamlScalarNode ?? throw At(node, $"{key} is a single value, not a {Kind(node)}.");

    private static int Count(YamlScalarNode node, string key)
    {
        if (!int.TryParse(node.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1)
        {
            throw At(node, $"{key} is \"{node.Value}\"; it is a whole number, at least 1.");
        }

        return value;
    }

    private static string Kind(YamlNode node) => node switch
    {
        YamlSequenceNode => "sequence",
        YamlMappingNode => "mapping",
        _ => "value",
    };

    /// <summary>A plain scalar when the text reads back as itself, else a double-quoted one.</summary>
    private static string Quote(string value)
    {
        bool plain = value.Length > 0 && value[0] is not ('-' or '?' or ':' or '@' or '`' or '\'' or '"' or '&' or '*' or '!' or '|' or '>' or '%' or '#' or '[' or ']' or '{' or '}' or ',' or ' ');
        foreach (char c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '\\' || c == '@' || c == '+'))
            {
                plain = false;
            }
        }

        if (plain && value[^1] != ' ')
        {
            return value;
        }

        StringBuilder quoted = new("\"");
        foreach (char c in value)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(c) => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"),
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// A hint for the likeliest mistake: a <c>-</c> written for an empty cell
    /// inside a row's brackets, which YAML refuses.
    /// </summary>
    internal static string DashHint(string text, int line)
    {
        string[] lines = text.Split('\n');
        if (line < 1 || line > lines.Length)
        {
            return string.Empty;
        }

        string at = lines[line - 1];
        int open = at.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            return string.Empty;
        }

        foreach (string cell in at[(open + 1)..].Split(',', ']'))
        {
            if (cell.Trim() == "-")
            {
                return $" (an empty cell is written {Empty}, not -)";
            }
        }

        return string.Empty;
    }

    private static LevelFileException At(YamlNode node, string problem) =>
        new((int)node.Start.Line, (int)node.Start.Column, problem);

    /// <summary>YamlDotNet's message without the position it prefixes, which the exception carries already.</summary>
    internal static string Plain(YamlException exception)
    {
        string message = exception.Message;
        int close = message.IndexOf("): ", StringComparison.Ordinal);
        return message.StartsWith("(Line:", StringComparison.Ordinal) && close >= 0 ? message[(close + 3)..] : message;
    }
}
