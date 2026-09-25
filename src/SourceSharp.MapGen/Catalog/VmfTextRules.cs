namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The rule every generated map file has to obey, checked on the text before it
/// leaves the generator.
///
/// <para>
/// THE TRAP THIS EXISTS FOR. The only VMF in this tree, `maps/ss_sandbox.vmf`,
/// has a raw newline inside a quoted value — a `point_worldtext` whose `message`
/// runs to two lines. It parses, and it parses in STOCK too, but only by
/// accident: stock's tokenizer checks for 0x0d, a carriage return,
/// inside a quoted string and rejects the value as too long.
/// A bare 0x0a is not checked.
/// </para>
///
/// <para>
/// Stock's VMF writer emits a literal "\r\n" unconditionally
/// at the end of every line, and its keyvalue writer is a plain
/// `"\"%s\" \"%s\""` with no escaping. So the moment such a file is
/// written by a faithful writer — or simply converted to CRLF — that newline
/// becomes CR+LF INSIDE the quotes and stock's own tokenizer rejects the file
/// stock's own writer produced. `CorpusVmfTests` pins both halves of that.
/// </para>
///
/// <para>
/// THE FIX IS HERE, IN THE GENERATOR, and not on write. Escaping the value as
/// it is written would make every managed-written value differ from stock's for
/// every map, to work around a shape no generated map needs to contain. So the
/// catalogue simply does not produce it, and this says so out loud instead of
/// hoping.
/// </para>
/// </summary>
public static class VmfTextRules
{
    /// <summary>
    /// Checks a generated map file and throws if any quoted value contains a
    /// raw CR or LF.
    ///
    /// <para>
    /// Written as a scanner over the finished text rather than as a check on
    /// each keyvalue as it is set, because the text is what stock's tokenizer
    /// reads: a value assembled from pieces, or a material name that came from
    /// somewhere else, is still caught here.
    /// </para>
    /// </summary>
    /// <param name="vmf">The generated map file's text.</param>
    /// <param name="name">The entry's name, for the message.</param>
    /// <exception cref="InvalidOperationException">A quoted value spans a line.</exception>
    public static void RequireNoNewlineInsideAQuotedValue(string vmf, string name)
    {
        ArgumentNullException.ThrowIfNull(vmf);

        int offence = FindNewlineInsideAQuotedValue(vmf);

        if (offence < 0)
            return;

        // The line number, because a character offset into a 400 KB file is not
        // something anybody can act on.
        int line = 1;

        for (int i = 0; i < offence; i++)
        {
            if (vmf[i] == '\n')
                line++;
        }

        throw new InvalidOperationException(
            $"catalogue entry '{name}' emitted a raw newline inside a quoted value at line {line}. "
            + "The reference tokenizer rejects that shape once the file is CRLF, "
            + "so the value must not contain one.");
    }

    /// <summary>
    /// The offset of the first CR or LF that sits inside quotes, or -1.
    ///
    /// <para>
    /// A VMF has no escape character in its quoting — stock's keyvalue writer
    /// writes the value raw — so quotes simply alternate and there is no
    /// backslash case to handle. Following stock rather than being clever about
    /// it is the point.
    /// </para>
    /// </summary>
    /// <param name="vmf">Text to scan.</param>
    public static int FindNewlineInsideAQuotedValue(string vmf)
    {
        ArgumentNullException.ThrowIfNull(vmf);

        bool insideQuotes = false;

        for (int i = 0; i < vmf.Length; i++)
        {
            char c = vmf[i];

            if (c == '"')
            {
                insideQuotes = !insideQuotes;
                continue;
            }

            if (insideQuotes && (c == '\n' || c == '\r'))
                return i;
        }

        return -1;
    }
}
