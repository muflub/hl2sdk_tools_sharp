using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A KeyValues text file: the parser and serialiser of the grammar the
/// reference implementation uses, without its object model.
/// </summary>
/// <remarks>
/// <para>
/// This is the format a VMT, a <c>detail.vbsp</c>, a surfaceproperties
/// manifest and a <c>gameinfo.txt</c> are all written in. It is NOT the same
/// grammar as a VMF (<see cref="VmfDocument"/>) and NOT the same grammar as
/// the reference map reader's own tokenizer: the three differ on comment
/// syntax, on escapes, on include directives and on whether braces exist at
/// all. Mixing them up is the classic way to write a parser that works on
/// the test file and fails on real content.
/// </para>
/// <para>
/// A file may hold SEVERAL root sections. The reference tokenizer
/// (<c>LoadFromBuffer</c>) loops until the buffer runs out, chaining them as
/// peers.
/// Its <c>SaveToFile</c> writes only <c>this</c> and its subtree, so stock
/// cannot round-trip such a file; this
/// document holds the list and can.
/// </para>
/// </remarks>
public sealed class KeyValuesDocument
{
    /// <summary>The root sections, in file order.</summary>
    public IList<KeyValuesNode> Roots { get; } = [];

    /// <summary>
    /// The first root section, or null when the file was empty.
    /// </summary>
    public KeyValuesNode? Root => Roots.Count > 0 ? Roots[0] : null;

    /// <summary>
    /// The <c>#base</c> filenames the file asked for, in the order they
    /// appeared.
    /// </summary>
    /// <remarks>
    /// Recorded rather than resolved, because resolving means opening files and
    /// this assembly's readers never touch a path. A caller with an
    /// <c>IFileSystem</c> loads each one and calls
    /// <see cref="MergeBase"/>. The reference implementation resolves them
    /// relative to the DIRECTORY OF THE FILE BEING PARSED
    /// (<c>ParseIncludedKeys</c>), which is the
    /// opposite of the reference map reader's <c>$include</c>.
    /// </remarks>
    public IList<string> BaseFiles { get; } = [];

    /// <summary>
    /// The <c>#include</c> filenames the file asked for, in order.
    /// </summary>
    public IList<string> IncludeFiles { get; } = [];

    /// <summary>Parses a KeyValues file from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async Task<KeyValuesDocument> ReadAsync(
        Stream stream,
        KeyValuesParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await ParseAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a KeyValues file already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed document.</returns>
    /// <remarks>
    /// A UTF-16LE byte-order mark is transcoded before parsing, as the
    /// reference tokenizer does. A UTF-8 one is NOT handled there and
    /// is not handled here: its three bytes become part of the first token,
    /// which is why a VMT saved as "UTF-8 with BOM" names a shader nothing
    /// recognises.
    /// </remarks>
    public static ValueTask<KeyValuesDocument> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        KeyValuesParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReadOnlySpan<byte> span = bytes.Span;

        string text = span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE
            ? Encoding.Unicode.GetString(span[2..])
            : Encoding.Latin1.GetString(span);

        return ParseAsync(text, options, cancellationToken);
    }

    /// <summary>Parses a KeyValues file from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="options">How to parse; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static ValueTask<KeyValuesDocument> ParseAsync(
        string text,
        KeyValuesParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            KeyValuesParser parser = new(text, options ?? KeyValuesParseOptions.Default);
            return ValueTask.FromResult(parser.Parse(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromCanceled<KeyValuesDocument>(cancellationToken);
        }
    }

    /// <summary>
    /// Merges a <c>#base</c> file's roots into this document's, giving THIS
    /// document's keys priority.
    /// </summary>
    /// <param name="baseDocument">The parsed base file.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="baseDocument"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The rule is the reference <c>RecursiveMergeKeyValues</c>: it never
    /// overwrites, keeping this document's value. For each
    /// child of the base: if a
    /// child of the same name already exists here, recurse into the FIRST
    /// such child; otherwise append a copy at the END.
    /// </para>
    /// <para>
    /// Two consequences fall out of "never overwrite" and are pinned by facts:
    /// with several <c>#base</c> lines the EARLIER one wins, because the later
    /// one finds the key already present; and a scalar here against a section
    /// in the base keeps the scalar AND gains the base's children, producing a
    /// node with both.
    /// </para>
    /// </remarks>
    public void MergeBase(KeyValuesDocument baseDocument)
    {
        ArgumentNullException.ThrowIfNull(baseDocument);

        for (int i = 0; i < Roots.Count && i < baseDocument.Roots.Count; i++)
        {
            Merge(Roots[i], baseDocument.Roots[i]);
        }

        for (int i = Roots.Count; i < baseDocument.Roots.Count; i++)
        {
            Roots.Add(baseDocument.Roots[i].Clone());
        }
    }

    /// <summary>
    /// Appends an <c>#include</c>d file's roots as siblings of this document's.
    /// </summary>
    /// <param name="included">The parsed included file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="included"/> is null.</exception>
    /// <remarks>
    /// The reference <c>AppendIncludedKeys</c> walks to the
    /// end of the peer chain and links the included root there. It is a SIBLING
    /// APPEND and not a merge, which is the whole difference from
    /// <see cref="MergeBase"/>.
    /// </remarks>
    public void AppendInclude(KeyValuesDocument included)
    {
        ArgumentNullException.ThrowIfNull(included);

        foreach (KeyValuesNode root in included.Roots)
        {
            Roots.Add(root.Clone());
        }
    }

    /// <summary>Writes the document as <c>RecursiveSaveToFile</c> does.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="options">How to write; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when everything is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public async Task WriteAsync(
        Stream stream,
        KeyValuesWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] bytes = ToBytes(options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The document's bytes, exactly as <see cref="WriteAsync"/> would write
    /// them.
    /// </summary>
    /// <param name="options">How to write; null for the defaults.</param>
    /// <returns>The encoded file.</returns>
    public byte[] ToBytes(KeyValuesWriteOptions? options = null) =>
        Encoding.Latin1.GetBytes(ToText(options));

    /// <summary>The document as text.</summary>
    /// <param name="options">How to write; null for the defaults.</param>
    /// <returns>The serialised text.</returns>
    /// <remarks>
    /// The framing is exact and every byte of it is what the reference writer
    /// emits: a section is indent, <c>"name"</c>, newline, indent, <c>{</c>,
    /// newline; a scalar is indent, <c>"name"</c>, then the literal
    /// four bytes <c>"\t\t"</c>, then <c>"value"</c>, newline;
    /// the tail is indent, <c>}</c>, newline. One TAB per level, LF and never
    /// CRLF because the reference writer opens the file <c>"wb"</c>, and never
    /// a blank line anywhere.
    /// </remarks>
    public string ToText(KeyValuesWriteOptions? options = null)
    {
        KeyValuesWriteOptions effective = options ?? KeyValuesWriteOptions.Default;
        StringBuilder output = new();

        foreach (KeyValuesNode root in Roots)
        {
            WriteNode(output, root, 0, effective);
        }

        return output.ToString();
    }

    private static void WriteNode(
        StringBuilder output,
        KeyValuesNode node,
        int indentLevel,
        KeyValuesWriteOptions options)
    {
        if (node.IsSection)
        {
            // The section header: name line, then the brace line.
            Indent(output, indentLevel);
            output.Append('"').Append(Convert(node.Name, options)).Append("\"\n");
            Indent(output, indentLevel);
            output.Append("{\n");

            foreach (KeyValuesNode child in node.Children)
            {
                WriteNode(output, child, indentLevel + 1, options);
            }

            // The section tail.
            Indent(output, indentLevel);
            output.Append("}\n");
            return;
        }

        // An empty value is DROPPED unless the caller asks for it, because the
        // reference writer's allow-empty-string flag defaults to false. So
        // `"k" ""` disappears on save; that is a round-trip hazard in
        // the format, not in this port.
        if (node.Value is null || (node.Value.Length == 0 && !options.AllowEmptyString))
        {
            return;
        }

        // The scalar line. The separator is the FOUR bytes
        // quote-tab-tab-quote, written out as one piece.
        Indent(output, indentLevel);
        output.Append('"').Append(Convert(node.Name, options)).Append("\"\t\t\"");
        output.Append(Convert(node.Value, options)).Append("\"\n");
    }

    private static void Indent(StringBuilder output, int level) =>
        output.Append('\t', level);

    /// <summary>
    /// The reference writer's converted-string pass.
    /// </summary>
    private static string Convert(string text, KeyValuesWriteOptions options)
    {
        // A double quote is ALWAYS escaped.
        // A backslash is escaped ONLY when the tree was loaded with
        // escape sequences on, and the reference writer's has-escape-sequences
        // flag defaults to false. So a VMT's backslashes are emitted raw.
        StringBuilder converted = new(text.Length);

        foreach (char c in text)
        {
            if (c == '"')
            {
                converted.Append("\\\"");
            }
            else if (c == '\\' && options.EscapeSequences)
            {
                converted.Append("\\\\");
            }
            else
            {
                converted.Append(c);
            }
        }

        return converted.ToString();
    }

    private static void Merge(KeyValuesNode here, KeyValuesNode baseNode)
    {
        foreach (KeyValuesNode baseChild in baseNode.Children)
        {
            KeyValuesNode? existing = here.Find(baseChild.Name);

            if (existing is null)
            {
                // A new child is appended at the END.
                here.Children.Add(baseChild.Clone());
                continue;
            }

            // Our value is kept, with nothing to do.
            // Only the children are merged, and only into the FIRST match.
            Merge(existing, baseChild);
        }
    }
}
