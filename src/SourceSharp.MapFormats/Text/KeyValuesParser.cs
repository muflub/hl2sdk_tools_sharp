//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The KeyValues text parser: <c>ReadToken</c>,
/// <c>LoadFromBuffer</c> and <c>RecursiveLoadFromBuffer</c>
/// as the reference tokenizer defines them.
/// </summary>
/// <remarks>
/// Internal because <see cref="KeyValuesDocument"/> is the surface; the parser
/// has no state worth exposing and the reference tokenizer has none either --
/// it is three free functions over a buffer plus a process-wide mutex it needs
/// only because its token buffer is a file-scope static.
/// This one carries the buffer as a field, so two documents parse concurrently
/// with no lock at all.
/// </remarks>
internal sealed class KeyValuesParser(string text, KeyValuesParseOptions options)
{
    private readonly string _text = text;
    private readonly KeyValuesParseOptions _options = options;
    private int _position;

    /// <summary>Parses the whole buffer.</summary>
    public KeyValuesDocument Parse(CancellationToken cancellationToken)
    {
        KeyValuesDocument document = new();
        int sinceCheck = 0;

        while (true)
        {
            if (++sinceCheck >= 1024)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            string? name = ReadToken(out bool wasQuoted, out _);

            // A null or empty token ends the loop.
            if (string.IsNullOrEmpty(name))
            {
                break;
            }

            // #include and #base, case-insensitive,
            // AT THE TOP LEVEL ONLY. The section parser has no such check,
            // so a #base inside a block is an ordinary key name.
            if (!wasQuoted && string.Equals(name, "#include", StringComparison.OrdinalIgnoreCase))
            {
                string? file = ReadToken(out _, out _);
                if (!string.IsNullOrEmpty(file))
                {
                    document.IncludeFiles.Add(file);
                }

                continue;
            }

            if (!wasQuoted && string.Equals(name, "#base", StringComparison.OrdinalIgnoreCase))
            {
                string? file = ReadToken(out _, out _);
                if (!string.IsNullOrEmpty(file))
                {
                    document.BaseFiles.Add(file);
                }

                continue;
            }

            KeyValuesNode root = new(name) { IsBlock = true };

            // A conditional may sit between the
            // root's name and its brace.
            string? next = ReadToken(out bool nextQuoted, out bool wasConditional);
            bool accepted = true;

            if (wasConditional && next is not null)
            {
                accepted = !_options.EvaluateConditionals || EvaluateConditional(next);
                next = ReadToken(out nextQuoted, out _);
            }

            // A QUOTED "{" is not an opening brace.
            if (next is null || next.Length == 0 || next[0] != '{' || nextQuoted)
            {
                // The reference tokenizer reports "missing {" and carries on
                // from wherever the buffer now is rather than breaking.
                // Reproduced: a file
                // with a stray token at the top level still yields the
                // sections that follow it.
                continue;
            }

            ParseSection(root, cancellationToken);

            if (accepted)
            {
                document.Roots.Add(root);
            }

            // A rejected section is simply dropped.
        }

        return document;
    }

    /// <summary>
    /// The section parser, mirroring <c>RecursiveLoadFromBuffer</c>.
    /// The opening brace has been
    /// consumed.
    /// </summary>
    private void ParseSection(KeyValuesNode section, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? name = ReadToken(out bool nameQuoted, out _);

            // EOF, or an EMPTY key name, ends the
            // block. A quoted "" therefore terminates it, which is not obvious.
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // An unquoted '}' closes it.
            if (!nameQuoted && name[0] == '}')
            {
                return;
            }

            string? value = ReadToken(out bool valueQuoted, out bool wasConditional);

            // A conditional between the key and its value.
            bool accepted = true;
            if (wasConditional && value is not null)
            {
                accepted = !_options.EvaluateConditionals || EvaluateConditional(value);
                value = ReadToken(out valueQuoted, out _);
            }

            if (value is null)
            {
                // The reference tokenizer reports "got NULL key" here.
                return;
            }

            if (value.Length > 0 && value[0] == '}' && !valueQuoted)
            {
                // A KEY WITH NO VALUE IS A HARD ERROR that ends
                // the enclosing block. There is no such thing as a valueless
                // key in this format, unlike a VMF chunk name.
                return;
            }

            if (value.Length > 0 && value[0] == '{' && !valueQuoted)
            {
                // The key is a section. Note what does NOT happen
                // afterwards: the trailing-conditional look-ahead below
                // is skipped for sections, so a conditional after a block's
                // closing brace is never consulted.
                KeyValuesNode child = new(name) { IsBlock = true };
                ParseSection(child, cancellationToken);

                if (accepted)
                {
                    section.Children.Add(child);
                }

                continue;
            }

            // One token of look-ahead for a trailing conditional,
            // rewound when it turns out not to be one.
            int mark = _position;
            string? trailing = ReadToken(out _, out bool trailingConditional);

            if (trailingConditional && trailing is not null)
            {
                accepted = accepted &&
                    (!_options.EvaluateConditionals || EvaluateConditional(trailing));
            }
            else
            {
                _position = mark;
            }

            if (accepted)
            {
                // Duplicates are ALWAYS created, never merged.
                section.Children.Add(new KeyValuesNode(name) { Value = value });
            }
        }
    }

    /// <summary>
    /// The conditional evaluator, mirroring <c>EvaluateConditional</c>.
    /// </summary>
    private bool EvaluateConditional(string condition)
    {
        int index = 0;
        if (index < condition.Length && condition[index] == '[')
        {
            index++;
        }

        bool negate = index < condition.Length && condition[index] == '!';

        // The ordered substring search. Not a parse: the FIRST of these seven
        // that appears anywhere in the string decides the answer, which is why
        // there is no boolean algebra and why '!' counts only as the very first
        // character after the bracket.
        bool pc = _options.Platform == KeyValuesPlatform.Pc;

        if (Contains(condition, "$DECK"))
        {
            return false ^ negate;
        }

        if (Contains(condition, "$X360"))
        {
            return (_options.Platform == KeyValuesPlatform.X360) ^ negate;
        }

        if (Contains(condition, "$WIN32"))
        {
            // WIN32 really means IsPC.
            return pc ^ negate;
        }

        if (Contains(condition, "$WINDOWS"))
        {
            return pc ^ negate;
        }

        if (Contains(condition, "$OSX"))
        {
            return false ^ negate;
        }

        if (Contains(condition, "$LINUX"))
        {
            return pc ^ negate;
        }

        if (Contains(condition, "$POSIX"))
        {
            return pc ^ negate;
        }

        // Anything unrecognised is false, negation included.
        return false;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tokenizer, mirroring <c>ReadToken</c>.
    /// </summary>
    private string? ReadToken(out bool wasQuoted, out bool wasConditional)
    {
        wasQuoted = false;
        wasConditional = false;

        // Whitespace, then a line comment, repeatedly. The ONLY
        // comment form is '//' to end of line; there is
        // no /* */ anywhere in this grammar.
        while (true)
        {
            SkipWhitespace();
            if (!SkipLineComment())
            {
                break;
            }
        }

        if (_position >= _text.Length)
        {
            return null;
        }

        char c = _text[_position];

        // A quoted token.
        if (c == '"')
        {
            _position++;
            wasQuoted = true;
            return ReadQuoted();
        }

        // Braces are single-character tokens.
        if (c == '{' || c == '}')
        {
            _position++;
            return c.ToString();
        }

        // A bare token, stopping at whitespace or any of " { }.
        StringBuilder token = new();
        bool sawOpenBracket = false;

        while (_position < _text.Length)
        {
            c = _text[_position];

            if (c == '"' || c == '{' || c == '}')
            {
                break;
            }

            // The conditional sniff, and it is this sloppy: ANY
            // bare token with a '[' followed later by a ']' is flagged,
            // "foo[1]" included, and the brackets stay in the text.
            if (c == '[')
            {
                sawOpenBracket = true;
            }

            if (c == ']' && sawOpenBracket)
            {
                wasConditional = true;
            }

            if (char.IsWhiteSpace(c))
            {
                break;
            }

            // The token buffer is 4096, so 4095 characters survive.
            if (token.Length < KeyValuesNode.MaxTokenLength - 1)
            {
                token.Append(c);
            }

            _position++;
        }

        return token.ToString();
    }

    /// <summary>
    /// The quoted-string reader, mirroring
    /// <c>CUtlBuffer::GetDelimitedString</c> for the two conversions KeyValues
    /// uses.
    /// </summary>
    private string ReadQuoted()
    {
        StringBuilder token = new();

        while (_position < _text.Length)
        {
            char c = _text[_position];

            // Only the delimiter ends the string, so an
            // embedded NEWLINE is legal and is kept. An unterminated quote at
            // end of file is not an error either.
            if (c == '"')
            {
                _position++;
                break;
            }

            if (c == '\\' && _options.EscapeSequences)
            {
                _position++;
                if (_position >= _text.Length)
                {
                    break;
                }

                // The standard C escape table, exactly.
                char escaped = _text[_position];
                char? decoded = escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'v' => '\v',
                    'b' => '\b',
                    'r' => '\r',
                    'f' => '\f',
                    'a' => '\a',
                    '\\' => '\\',
                    '?' => '?',
                    '\'' => '\'',
                    '"' => '"',
                    _ => null,
                };

                if (decoded is null)
                {
                    // An unrecognised escape yields
                    // the conversion table's zero with a length of ZERO, so a
                    // NUL is emitted and the offending character is NOT
                    // consumed. Faithfully reproduced, because it is the
                    // difference between "\q" being an empty token and being
                    // the two characters a reasonable parser would give.
                    token.Append('\0');
                    continue;
                }

                token.Append(decoded.Value);
                _position++;
                continue;
            }

            if (c == '' && !_options.EscapeSequences)
            {
                // In the no-escape mode the escape
                // character is 0x7F, whose conversion is always zero, so a
                // literal 0x7F inside a quoted string becomes a NUL.
                token.Append('\0');
                _position++;
                continue;
            }

            if (token.Length < KeyValuesNode.MaxTokenLength - 1)
            {
                token.Append(c);
            }

            _position++;
        }

        return token.ToString();
    }

    private void SkipWhitespace()
    {
        // Whitespace is isspace on an UNSIGNED char, so a high byte
        // is an ordinary token character here. The other reference
        // tokenizer's '<= 32' on a signed char treats it as whitespace;
        // the two grammars disagree.
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private bool SkipLineComment()
    {
        if (_position + 1 >= _text.Length ||
            _text[_position] != '/' || _text[_position + 1] != '/')
        {
            return false;
        }

        // To the next newline or to the end. No
        // character cap, unlike the VMF tokenizer's ignore(1024, '\n').
        while (_position < _text.Length && _text[_position] != '\n')
        {
            _position++;
        }

        return true;
    }
}
