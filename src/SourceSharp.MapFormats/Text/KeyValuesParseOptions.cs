namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The two flags a <c>KeyValues</c> tree carries that change how text parses.
/// </summary>
/// <param name="EscapeSequences">
/// <c>m_bHasEscapeSequences</c>. FALSE by default in the reference
/// <c>KeyValues</c> tree, and no loader in the map pipeline turns it on -- so
/// a backslash in a VMT path is a literal backslash and <c>"\n"</c> in a
/// value is a backslash followed by an <c>n</c>.
/// </param>
/// <param name="EvaluateConditionals">
/// <c>m_bEvaluateConditionals</c>. TRUE by default in the reference tree.
/// When false, every <c>[$WIN32]</c>-style qualifier is ignored and the key
/// it guards is kept.
/// </param>
/// <param name="Platform">
/// Which platform the conditionals are evaluated against. Not a concept the
/// reference format carries -- there the answer comes from <c>IsPC()</c>,
/// <c>IsX360()</c> and friends compiled into the binary -- but a map compiler
/// that cannot be asked "what would the 360 build have read?" cannot diff
/// against a 360 pak.
/// </param>
public sealed record KeyValuesParseOptions(
    bool EscapeSequences = false,
    bool EvaluateConditionals = true,
    KeyValuesPlatform Platform = KeyValuesPlatform.Pc)
{
    /// <summary>The flags a freshly constructed <c>KeyValues</c> has.</summary>
    public static KeyValuesParseOptions Default { get; } = new();
}
