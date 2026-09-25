namespace SourceSharp.MapGen;

/// <summary>
/// The entity lump of a compiled map, read straight off disk.
///
/// <para>
/// WHY THIS EXISTS RATHER THAN <c>devapi mapents</c>. The gate reads a running
/// game's idea of the map, which is the right answer to "what is the server
/// looking at" and needs a game up to ask. But the question that let a crash
/// ship — "does the map the gate will load actually contain one of these" — is
/// answerable from the .bsp alone, before anything is launched, and it wants
/// answering at the moment the map is COMPILED rather than the next time
/// somebody happens to run the gate.
/// </para>
///
/// <para>
/// The format is the whole reason this is only sixty lines. A .bsp opens with
/// <c>VBSP</c>, a version, and 64 lump descriptors of four ints each; lump 0 is
/// the entity lump and it is the map's entities as PLAIN TEXT, in the same
/// brace-and-quoted-pair syntax a .vmf uses for its keyvalues. So reading it is
/// reading a string, and the only real decision is what to do with a lump that
/// is compressed — which this build's maps never are, and which is refused
/// loudly rather than parsed into a confident zero.
/// </para>
/// </summary>
public static class BspEntities
{
    /// <summary>The four bytes every Source .bsp starts with.</summary>
    private const int VbspIdent = ('P' << 24) | ('S' << 16) | ('B' << 8) | 'V';

    /// <summary>Lump 0. The entity lump has been lump 0 since Quake.</summary>
    private const int EntityLump = 0;

    private const int HeaderLumps = 64;

    /// <summary>
    /// The entity lump's text, exactly as the compiler wrote it.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is not a VBSP, or its entity lump is compressed. Both are
    /// refused rather than returning an empty string: "this map declares
    /// nothing" and "this reader did not understand the file" must not arrive
    /// as the same answer, because the first is a failure the coverage rule
    /// reports and the second is a failure OF the coverage rule.
    /// </exception>
    public static string ReadLumpText(string bspPath)
    {
        byte[] bytes = File.ReadAllBytes(bspPath);

        if (bytes.Length < 8 + (HeaderLumps * 16))
            throw new InvalidDataException($"{bspPath} is too short to be a .bsp ({bytes.Length} bytes)");

        int ident = BitConverter.ToInt32(bytes, 0);

        if (ident != VbspIdent)
            throw new InvalidDataException($"{bspPath} does not start with VBSP");

        int at = 8 + (EntityLump * 16);

        int offset = BitConverter.ToInt32(bytes, at);
        int length = BitConverter.ToInt32(bytes, at + 4);
        int fourCc = BitConverter.ToInt32(bytes, at + 12);

        // A non-zero fourCC is the uncompressed size of an LZMA lump. Nothing
        // in this tree compresses lumps, and a compressed one read as text
        // would parse to no entities at all — a silent pass.
        if (fourCc != 0)
            throw new InvalidDataException($"{bspPath}'s entity lump is compressed; this reader handles only plain lumps");

        if (offset < 0 || length < 0 || offset + length > bytes.Length)
            throw new InvalidDataException($"{bspPath}'s entity lump is out of bounds (offset {offset}, length {length})");

        // The lump is ASCII with a trailing NUL. Latin-1 rather than UTF-8
        // because a mapper's stray high byte must not throw here.
        return System.Text.Encoding.Latin1.GetString(bytes, offset, length).TrimEnd('\0');
    }

    /// <summary>
    /// The entities a compiled map declares, in the shape the pass scores.
    /// </summary>
    public static List<DeclaredEntity> Read(string bspPath)
        => Parse(ReadLumpText(bspPath));

    /// <summary>
    /// The lump text as entities. Separate from <see cref="Read"/> so a test can
    /// hand it a string and never touch a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SCANNER over the whole lump, not a loop over its lines, and the
    /// difference is not stylistic. A line-based reader assumes every keyvalue
    /// fits on one line — which is true of almost all of them and false of the
    /// one that matters: <c>point_worldtext</c>'s <c>message</c> holds real
    /// newlines, because that is how the entity draws more than one line.
    /// The VMF tokenizer turns the <c>\n</c> escape into a literal LF
    /// (<c>TokenReader::GetString</c>) and <c>CPointWorldText::KeyValue</c>
    /// copies the value verbatim, so vbsp writes a quoted string with an LF
    /// inside it and the .bsp is correct.
    /// </para>
    /// <para>
    /// Split on '\n' and that value becomes an opening quote with no closing
    /// quote on one line and a stray word on the next. The old reader dropped
    /// the key without a word and answered "" for it, so the staleness rule
    /// reported the COMPILED MAP as behind the generator when the map was right
    /// and the reader was wrong. It cost a whole investigation: ss_sandbox.bsp
    /// was rebuilt correctly and still came back two entities "stale", both of
    /// them the multi-line worldtexts.
    /// </para>
    /// <para>
    /// Scanning also makes braces inside a value harmless, which line-splitting
    /// only got away with by never seeing a value it could not close.
    /// </para>
    /// <para>
    /// There is no escape syntax to honour: the entity lump is what vbsp wrote,
    /// and a quote runs to the next quote. That is the same rule the engine's
    /// own entity parser uses.
    /// </para>
    /// </remarks>
    public static List<DeclaredEntity> Parse(string lump)
    {
        var entities = new List<DeclaredEntity>();

        Dictionary<string, string>? current = null;

        // The key of a pair whose value has not been read yet. Null between
        // pairs, so the two quoted tokens of "key" "value" alternate.
        string? pendingKey = null;

        for (int i = 0; i < lump.Length; i++)
        {
            char c = lump[i];

            if (c == '{')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                pendingKey = null;
                continue;
            }

            if (c == '}')
            {
                if (current is not null
                    && current.TryGetValue("classname", out string? className)
                    && className.Length > 0)
                {
                    current.TryGetValue("targetname", out string? targetName);
                    entities.Add(new DeclaredEntity(className, targetName ?? "", current));
                }

                current = null;
                pendingKey = null;
                continue;
            }

            if (c != '"')
                continue;

            int end = lump.IndexOf('"', i + 1);

            // An unterminated quote is the end of anything readable. Stopping
            // beats resynchronising: past this point every key and value would
            // be paired one slot out, and a confidently wrong entity list is
            // worse than a short one.
            if (end < 0)
                break;

            string token = lump.Substring(i + 1, end - i - 1);
            i = end;

            if (current is null)
                continue;

            if (pendingKey is null)
            {
                pendingKey = token;
                continue;
            }

            if (pendingKey.Length > 0)
                current[pendingKey] = token;

            pendingKey = null;
        }

        return entities;
    }
}
