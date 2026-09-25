namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The kinds of token <see cref="ChunkTokenReader"/> returns, one for each
/// value of <c>trtoken_t</c> in the reference tokenizer's declaration.
/// </summary>
/// <remarks>
/// <para>
/// The underlying values are the reference enumerators' own values, negatives
/// included, so a fact can assert the correspondence without a translation
/// table standing between the two.
/// </para>
/// <para>
/// The set is closed: the reference's <c>CChunkFile::ReadNext</c> switches
/// over it exhaustively and relies on which cases are absent -- see
/// <see cref="ChunkFileReader"/> for the fall-through that depends on it -- so
/// adding a member is a breaking change to the VMF reader rather than an
/// additive one.
/// </para>
/// </remarks>
public enum ChunkTokenType
{
    /// <summary>
    /// <c>TOKENSTRINGTOOLONG</c> (-4): an unterminated string, or one that did
    /// not fit the destination buffer.
    /// </summary>
    StringTooLong = -4,

    /// <summary><c>TOKENERROR</c> (-3): a malformed number.</summary>
    Error = -3,

    /// <summary>
    /// <c>TOKENNONE</c> (-2): no token. The reference tokenizer never returns
    /// it and neither does this one; it exists so the enumerations correspond.
    /// </summary>
    None = -2,

    /// <summary><c>TOKENEOF</c> (-1): end of input.</summary>
    EndOfFile = -1,

    /// <summary>
    /// <c>OPERATOR</c> (0): one of the single characters the reference
    /// tokenizer lists.
    /// </summary>
    Operator = 0,

    /// <summary><c>INTEGER</c> (1): digits with an optional leading minus.</summary>
    Integer = 1,

    /// <summary><c>STRING</c> (2): the contents of a quoted string.</summary>
    String = 2,

    /// <summary><c>IDENT</c> (3): letters, digits and underscores.</summary>
    Identifier = 3,
}
