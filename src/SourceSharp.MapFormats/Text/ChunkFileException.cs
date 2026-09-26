//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A chunk file (a <c>.vmf</c>, a <c>.vmm</c>) is malformed.
/// </summary>
/// <remarks>
/// Carries the reader's result code and the line, because those are what a
/// user sees from stock: the reference reader's error rendering,
/// <c>CChunkFile::GetErrorText</c>, prints
/// <c>"File %s, line %d: "</c> and then the code's message. Reporting only
/// "parse error" would make a mapper's existing debugging routine stop
/// working.
/// </remarks>
public sealed class ChunkFileException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ChunkFileException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong.</param>
    public ChunkFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The failure that led here.</param>
    public ChunkFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception from a read result.</summary>
    /// <param name="result">The result code the reader produced.</param>
    /// <param name="line">The line the reader had reached, counting from 1.</param>
    /// <param name="errorToken">
    /// The offending token for
    /// <see cref="ChunkFileResult.UnexpectedSymbol"/>; empty otherwise.
    /// </param>
    public ChunkFileException(ChunkFileResult result, int line, string errorToken)
        : base(Describe(result, line, errorToken))
    {
        Result = result;
        Line = line;
        ErrorToken = errorToken ?? string.Empty;
    }

    /// <summary>The result code the reader produced.</summary>
    public ChunkFileResult Result { get; }

    /// <summary>The line the reader had reached, counting from 1.</summary>
    public int Line { get; }

    /// <summary>The offending token, when there was one.</summary>
    public string ErrorToken { get; } = string.Empty;

    private static string Describe(ChunkFileResult result, int line, string? errorToken)
    {
        // The three reader messages, verbatim.
        string detail = result switch
        {
            ChunkFileResult.UnexpectedEndOfFile => "unexpected end of file",
            ChunkFileResult.UnexpectedSymbol => $"unexpected symbol '{errorToken}'",
            ChunkFileResult.StringTooLong => "unterminated string or string too long",
            _ => $"error {(int)result}",
        };

        return $"line {line}: {detail}";
    }
}
