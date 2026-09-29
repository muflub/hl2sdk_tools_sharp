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
/// <b>The schema</b>: these four keys required, the transition keys below
/// optional, and no other allowed:
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
/// <b>Transitions</b> (<see cref="LevelTransitions"/>, the rooms design's
/// section 11), each optional: <c>up: none</c> and <c>down: none</c>,
/// <c>up_map: &lt;map&gt;</c> and <c>down_map: &lt;map&gt;</c>,
/// <c>spawn: [column, row]</c> and <c>spawn_count: K</c>. They are written
/// between <c>columns</c> and <c>grid</c>, in that order, and only when the
/// level has them.
/// </para>
/// <para>
/// <b>Why <c>~</c> for an empty cell</b>: the owner's choice, YAML's own
/// null. Any spelling of it the core schema allows reads the same (<c>~</c>,
/// <c>null</c>, <c>Null</c>, <c>NULL</c>, unquoted); a quoted <c>'~'</c> is a
/// string, as YAML says. The other marks that read well are not YAML values
/// inside a row's brackets: a bare <c>-</c> is a sequence indicator and a
/// bare <c>*</c> an alias, and YAML refuses the row; <c>@</c> is reserved.
/// A file that writes any of them (bare or quoted) gets an error naming
/// <c>~</c>.
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

    /// <summary>The key switching the up role off: <c>up: none</c> (<see cref="LevelTransitions"/>).</summary>
    public const string UpKey = "up";

    /// <summary>The key switching the down role off: <c>down: none</c>.</summary>
    public const string DownKey = "down";

    /// <summary>The key naming the map above.</summary>
    public const string UpMapKey = "up_map";

    /// <summary>The key naming the map below.</summary>
    public const string DownMapKey = "down_map";

    /// <summary>The key naming the spawn cell of a level with <c>up: none</c>, as <c>[column, row]</c>.</summary>
    public const string SpawnKey = "spawn";

    /// <summary>The key giving the fewest spawn points the level's spawn room must have.</summary>
    public const string SpawnCountKey = "spawn_count";

    /// <summary>The only value <see cref="UpKey"/> and <see cref="DownKey"/> take.</summary>
    public const string None = "none";

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
        LevelTransitions? transitions = null;
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
                case UpKey or DownKey or UpMapKey or DownMapKey or SpawnKey or SpawnCountKey:
                    transitions = Transition(transitions ?? new LevelTransitions(), key, value);
                    break;
                default:
                    throw At(keyNode, $"unknown key \"{key}\"; a level has {LibraryKey}, {RowsKey}, {ColumnsKey} and {GridKey},"
                        + $" and may have {UpKey}, {DownKey}, {UpMapKey}, {DownMapKey}, {SpawnKey} and {SpawnCountKey}.");
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

        if (transitions?.SpawnCell is (int spawnColumn, int spawnRow) && (spawnColumn >= columnCount || spawnRow >= rowCount))
        {
            YamlNode spawnNode = root.Children.First(c => c.Key is YamlScalarNode { Value: SpawnKey }).Value;
            throw At(spawnNode, string.Create(CultureInfo.InvariantCulture,
                $"{SpawnKey} names cell ({spawnColumn}, {spawnRow}), which is off the {rowCount}x{columnCount} grid."));
        }

        return new LevelGrid(name, library.Value!, rowCount, columnCount, cells) { Transitions = transitions };
    }

    /// <summary>One transition key read into the level's transitions (<see cref="LevelTransitions"/>).</summary>
    private static LevelTransitions Transition(LevelTransitions transitions, string key, YamlNode value)
    {
        switch (key)
        {
            case UpKey or DownKey:
                YamlScalarNode off = Scalar(value, key);
                if (off.Value != None)
                {
                    throw At(off, $"{key} is \"{off.Value}\"; the only value it takes is {None}, which switches the role off.");
                }

                return key == UpKey ? transitions with { NoUp = true } : transitions with { NoDown = true };
            case UpMapKey or DownMapKey:
                YamlScalarNode map = Scalar(value, key);
                if (LevelTransitions.MapNameProblem(map.Value) is { } problem)
                {
                    throw At(map, $"{key} \"{map.Value}\" {problem}.");
                }

                return key == UpMapKey ? transitions with { UpMap = map.Value } : transitions with { DownMap = map.Value };
            case SpawnKey:
                if (value is not YamlSequenceNode { Children.Count: 2 } pair
                    || pair.Children[0] is not YamlScalarNode column || pair.Children[1] is not YamlScalarNode row
                    || !int.TryParse(column.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(row.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int y))
                {
                    throw At(value, $"{SpawnKey} is a cell, [column, row], each a whole number from 0 (column from the west, row from the south).");
                }

                return transitions with { SpawnCell = (x, y) };
            default:
                return transitions with { SpawnCount = Count(Scalar(value, key), key) };
        }
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
        if (grid.Transitions is { } transitions)
        {
            WriteTransitions(text, transitions);
        }

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

    /// <summary>
    /// The transition keys, between the grid's size and the grid, in a fixed
    /// order: the roles switched off, the maps, then the spawn settings.
    /// Nothing is written for a level without transitions, so every level
    /// file written before them is written as it was.
    /// </summary>
    private static void WriteTransitions(StringBuilder text, LevelTransitions transitions)
    {
        if (transitions.NoUp)
        {
            text.Append(UpKey).Append(": ").Append(None).Append('\n');
        }

        if (transitions.NoDown)
        {
            text.Append(DownKey).Append(": ").Append(None).Append('\n');
        }

        if (transitions.UpMap is { } up)
        {
            text.Append(UpMapKey).Append(": ").Append(Quote(up)).Append('\n');
        }

        if (transitions.DownMap is { } down)
        {
            text.Append(DownMapKey).Append(": ").Append(Quote(down)).Append('\n');
        }

        if (transitions.SpawnCell is (int x, int y))
        {
            text.Append(SpawnKey).Append(": ").Append(string.Create(CultureInfo.InvariantCulture, $"[{x}, {y}]")).Append('\n');
        }

        if (transitions.SpawnCount is int count)
        {
            text.Append(SpawnCountKey).Append(": ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>
    /// Whether a cell is YAML's null: a plain (unquoted) <c>~</c>, <c>null</c>, <c>Null</c> or
    /// <c>NULL</c>, the core schema's spellings of it. A quoted <c>'~'</c> is a string in YAML,
    /// not null, and is read as a (bad) room name like any other string.
    /// </summary>
    private static bool IsNull(YamlScalarNode scalar) =>
        scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
        && scalar.Value is "~" or "null" or "Null" or "NULL";

    private static LevelCell? Cell(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            throw At(node, "a cell is a room name, name@rotation, or ~ for no room.");
        }

        string token = scalar.Value ?? string.Empty;
        if (IsNull(scalar))
        {
            return null;
        }

        if (token is "-" or "*" or "@")
        {
            throw At(scalar, $"{token} is not a cell; an empty cell is YAML's null, written {Empty}.");
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
    /// A hint for the likeliest mistake: a <c>-</c> or <c>*</c> written for an
    /// empty cell inside a row's brackets, which YAML refuses.
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
            if (cell.Trim() is "-" or "*")
            {
                return $" (an empty cell is written {Empty}, YAML's null, not {cell.Trim()})";
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
