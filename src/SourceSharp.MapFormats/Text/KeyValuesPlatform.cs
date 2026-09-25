namespace SourceSharp.MapFormats.Text;

/// <summary>
/// Which platform a <c>[$WIN32]</c>-style conditional is evaluated against.
/// </summary>
/// <remarks>
/// The seven names <c>EvaluateConditional</c> recognises are searched for as
/// SUBSTRINGS, in this order: <c>$DECK</c>, <c>$X360</c>, <c>$WIN32</c>,
/// <c>$WINDOWS</c>, <c>$OSX</c>, <c>$LINUX</c>, <c>$POSIX</c>
/// as the reference tokenizer evaluates them. The first that matches decides,
/// so there is no boolean algebra: <c>[$WIN32||$X360]</c> is answered by
/// whichever of the two appears EARLIER IN THAT LIST, which is <c>$X360</c>.
/// An unrecognised condition is false.
/// </remarks>
public enum KeyValuesPlatform
{
    /// <summary>
    /// A PC build. Satisfies <c>$WIN32</c>, <c>$WINDOWS</c>, <c>$POSIX</c> and
    /// <c>$LINUX</c> as the Linux tools are built.
    /// </summary>
    /// <remarks>
    /// <c>$WIN32</c> is deliberately <c>IsPC()</c> rather than "is Windows":
    /// the reference tokenizer's own comment has it stand for "WIN32 really
    /// means IsPC".
    /// </remarks>
    Pc = 0,

    /// <summary>An Xbox 360 build: satisfies <c>$X360</c> only.</summary>
    X360,
}
