using System.Text;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// LUMP_TEXDATA_STRING_DATA and LUMP_TEXDATA_STRING_TABLE:
/// <c>g_TexDataStringData</c> and <c>g_TexDataStringTable</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two lumps, one structure. The DATA lump is every material name concatenated
/// with its terminating nul; the TABLE lump is the byte offset of each. A
/// texdata stores the TABLE index, not the offset — so the two lumps together
/// are an indirection, and both are compared byte for byte against stock.
/// </para>
/// <para>
/// <b>Insertion order is output</b>, for the same reason the plane table's is:
/// <c>dtexdata_t.nameStringTableID</c> is an index into the TABLE lump.
/// </para>
/// <para>
/// The dedup (<c>TexDataStringTable_AddOrFindString</c>,
///) is a linear <c>stricmp</c> scan — case-INSENSITIVE
/// matching, but the string is STORED verbatim. So the first spelling of a name
/// wins and every later spelling collapses onto it, casing and all. The comment
/// on the reference table ("Make this use an RBTree!") notwithstanding, a
/// faster lookup here would have to return the same index for the same input
/// sequence, which is why the index this returns is computed from a structure
/// that preserves order rather than from a hash of the name.
/// </para>
/// </remarks>
public sealed class TexDataStringTable
{
    // Latin1, one char to one byte, because these lumps are counted in BYTES
    // and stock is manipulating a char array with strlen. Material names out of
    // a VMF are ASCII in practice; Latin1 round-trips anything that is not
    // without inventing a multi-byte encoding stock never had.
    //
    // A PROPERTY and not a static readonly field: Encoding is a mutable type,
    // so a static field holding one is static state the library rule forbids
    // -- and the rule is right, because Encoding exposes settable properties.
    private static Encoding NameEncoding => Encoding.Latin1;

    private readonly List<byte> _data = [];
    private readonly List<int> _offsets = [];

    // The same strings the bytes encode, so a lookup does not decode the whole
    // DATA lump on every comparison. Written only alongside _data, and a fact
    // asserts the two agree rather than the invariant being assumed.
    private readonly List<string> _names = [];

    /// <summary>How many strings the table holds.</summary>
    public int Count => _offsets.Count;

    /// <summary>
    /// The byte offsets, in table order: the TEXDATA_STRING_TABLE lump.
    /// </summary>
    public IReadOnlyList<int> Offsets => _offsets;

    /// <summary>
    /// The concatenated, nul-terminated names: the TEXDATA_STRING_DATA lump.
    /// </summary>
    /// <returns>A copy of the lump's bytes.</returns>
    public byte[] ToData() => [.. _data];

    /// <summary>
    /// Adds a name or finds the existing one:
    /// <c>TexDataStringTable_AddOrFindString</c>.
    /// </summary>
    /// <param name="name">The material name.</param>
    /// <returns>The index into the TABLE lump.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <remarks>
    /// The stored bytes are <c>name</c> plus one nul —
    /// <c>AddMultipleToTail(len+1, pString)</c> copies the terminator too — so
    /// the DATA lump's length is the sum of <c>strlen + 1</c> over the distinct
    /// names, which is what stock's "Reduced N texdatas (X bytes to Y)" line
    /// counts.
    /// </remarks>
    public int AddOrFind(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        for (int i = 0; i < _names.Count; i++)
        {
            if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        int offset = _data.Count;
        _data.AddRange(NameEncoding.GetBytes(name));
        _data.Add(0);
        _offsets.Add(offset);
        _names.Add(name);
        return _offsets.Count - 1;
    }

    /// <summary>
    /// Decodes one name back out of the DATA lump's bytes.
    /// </summary>
    /// <param name="stringId">The TABLE lump index.</param>
    /// <returns>The name as the stored bytes spell it.</returns>
    /// <remarks>
    /// <see cref="GetString"/> answers from the parallel string list, which is
    /// the same value by construction. This one reads the bytes, so a fact can
    /// hold the two against each other instead of trusting the construction.
    /// </remarks>
    public string DecodeString(int stringId)
    {
        int start = _offsets[stringId];
        int end = _data.IndexOf((byte)0, start);
        return NameEncoding.GetString([.. _data.GetRange(start, end - start)]);
    }

    /// <summary>
    /// The name at a table index: <c>TexDataStringTable_GetString</c>.
    /// </summary>
    /// <param name="stringId">The TABLE lump index.</param>
    /// <returns>The name, without its terminator.</returns>
    public string GetString(int stringId) => _names[stringId];
}
