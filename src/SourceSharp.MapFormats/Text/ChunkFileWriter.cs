using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The writing half of <c>CChunkFile</c>
/// (<c>src/public/chunkfile.cpp:188-258, 782-984</c>), byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// Three rules, and every VMF Hammer has ever written obeys them:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>WriteLine</c> emits the current indent, then the text, then
/// <c>"\r\n"</c> -- a literal CRLF written through <c>fwrite</c>
/// (<c>chunkfile.cpp:977</c>), NOT a <c>'\n'</c> through a text-mode handle, so
/// it is CRLF on Linux too.
/// </description></item>
/// <item><description>
/// The indent is <c>m_nCurrentDepth</c> TAB characters
/// (<c>BuildIndentString</c>, <c>chunkfile.cpp:213-224</c>).
/// </description></item>
/// <item><description>
/// <c>BeginChunk</c> writes the name and the brace as ONE line containing an
/// embedded CRLF -- <c>"%s\r\n%s{"</c>, <c>chunkfile.cpp:194</c> -- and
/// increments the depth AFTERWARDS, so the name and its opening brace sit at
/// the OUTER indent. <c>EndChunk</c> decrements FIRST
/// (<c>chunkfile.cpp:249-255</c>), so the closing brace matches.
/// </description></item>
/// </list>
/// <para>
/// In-memory, not a stream, because the C++ writes through a <c>FILE *</c> and
/// every byte is decided by the depth counter rather than by anything
/// asynchronous. <see cref="VmfDocument.WriteAsync"/> is the seam that puts the
/// finished bytes on a stream.
/// </para>
/// </remarks>
public sealed class ChunkFileWriter
{
    private readonly StringBuilder _output = new();

    /// <summary>
    /// The nesting depth, <c>m_nCurrentDepth</c>. Also the number of tabs each
    /// line is indented by.
    /// </summary>
    public int CurrentDepth { get; private set; }

    /// <summary>
    /// Opens a chunk: the name on its own line, then <c>{</c>, both at the
    /// current indent. A port of <c>BeginChunk</c>
    /// (<c>chunkfile.cpp:188-207</c>).
    /// </summary>
    /// <param name="name">The chunk name, written unquoted as Hammer writes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public void BeginChunk(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // The C++ builds "%s\r\n%s{" and hands it to WriteLine, which prepends
        // the indent once more -- hence the indent appearing twice here.
        WriteLine(name + "\r\n" + Indent() + "{");
        CurrentDepth++;
    }

    /// <summary>
    /// Closes a chunk. A port of <c>EndChunk</c>
    /// (<c>chunkfile.cpp:247-258</c>).
    /// </summary>
    /// <remarks>
    /// The depth is clamped at zero exactly as the C++ clamps it
    /// (<c>if (m_nCurrentDepth &gt; 0)</c>, <c>chunkfile.cpp:249</c>), so an
    /// unbalanced <c>EndChunk</c> writes a brace at column zero rather than
    /// throwing.
    /// </remarks>
    public void EndChunk()
    {
        if (CurrentDepth > 0)
        {
            CurrentDepth--;
        }

        WriteLine("}");
    }

    /// <summary>
    /// Writes a <c>"key" "value"</c> line. A port of <c>WriteKeyValue</c>
    /// (<c>chunkfile.cpp:782-792</c>).
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <param name="value">The value.</param>
    /// <remarks>
    /// Both halves are quoted unconditionally, and NOTHING is escaped: the
    /// format string is a plain <c>"\"%s\" \"%s\""</c>
    /// (<c>chunkfile.cpp:787</c>). A value containing a double quote therefore
    /// produces a file the reader cannot parse back, in stock and here alike.
    /// Escaping it would be the friendlier choice and the wrong one -- stock
    /// vbsp would then read the backslash as data.
    /// </remarks>
    public void WriteKeyValue(string key, string value)
    {
        if (key is null || value is null)
        {
            // chunkfile.cpp:784 -- a null key or value writes nothing and
            // reports success.
            return;
        }

        WriteLine($"\"{key}\" \"{value}\"");
    }

    /// <summary>
    /// Writes an integer key, <c>"%d"</c>
    /// (<c>chunkfile.cpp:820-830</c>).
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <param name="value">The value.</param>
    public void WriteKeyValueInt(string key, int value) =>
        WriteKeyValue(key, VmfValue.FormatInt(value));

    /// <summary>
    /// Writes a boolean key as <c>"0"</c> or <c>"1"</c>
    /// (<c>chunkfile.cpp:801-811</c>).
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <param name="value">The value.</param>
    public void WriteKeyValueBool(string key, bool value) =>
        WriteKeyValue(key, value ? "1" : "0");

    /// <summary>
    /// Writes a float key with <c>"%g"</c>
    /// (<c>chunkfile.cpp:839-849</c>).
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <param name="value">The value.</param>
    public void WriteKeyValueFloat(string key, float value) =>
        WriteKeyValue(key, VmfValue.FormatFloat(value));

    /// <summary>
    /// Writes one line: the indent, the text, and CRLF. A port of
    /// <c>WriteLine</c> (<c>chunkfile.cpp:948-984</c>).
    /// </summary>
    /// <param name="line">The text, which may itself contain CRLFs.</param>
    public void WriteLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (CurrentDepth > 0)
        {
            _output.Append(Indent());
        }

        _output.Append(line).Append("\r\n");
    }

    /// <summary>The file written so far.</summary>
    /// <returns>The bytes, Latin-1 encoded.</returns>
    /// <remarks>
    /// Latin-1 for the same reason <see cref="ChunkTokenReader"/> decodes it:
    /// one character out, one byte on disk, so a VMF that came in with a
    /// high-bit byte in a texture name goes back out unchanged.
    /// </remarks>
    public byte[] ToBytes() => Encoding.Latin1.GetBytes(_output.ToString());

    /// <inheritdoc />
    public override string ToString() => _output.ToString();

    private string Indent() => new('\t', CurrentDepth);
}
