//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The outcome of one chunk-file read step, mirroring the reference
/// reader's <c>ChunkFileResult_t</c>.
/// </summary>
/// <remarks>
/// The values are the reference enumerators' own, in their declared order,
/// because the reference reader compares against them by name everywhere and
/// the parity facts assert the correspondence.
/// </remarks>
public enum ChunkFileResult
{
    /// <summary><c>ChunkFile_Ok</c>: a key or a chunk was read.</summary>
    Ok = 0,

    /// <summary><c>ChunkFile_Fail</c>: a write failed.</summary>
    Fail,

    /// <summary><c>ChunkFile_OpenFail</c>: the file could not be opened.</summary>
    OpenFail,

    /// <summary>
    /// <c>ChunkFile_EndOfChunk</c>: a <c>}</c> closed the current chunk.
    /// </summary>
    EndOfChunk,

    /// <summary><c>ChunkFile_EOF</c>: end of file at depth zero.</summary>
    EndOfFile,

    /// <summary>
    /// <c>ChunkFile_UnexpectedEOF</c>: end of file inside a chunk, or between a
    /// key and its value.
    /// </summary>
    UnexpectedEndOfFile,

    /// <summary>
    /// <c>ChunkFile_UnexpectedSymbol</c>: an operator where a key, a value or a
    /// <c>{</c> was required.
    /// </summary>
    UnexpectedSymbol,

    /// <summary><c>ChunkFile_OutOfMemory</c>: never produced by this writer or reader.</summary>
    OutOfMemory,

    /// <summary>
    /// <c>ChunkFile_StringTooLong</c>: an unterminated string, or one longer
    /// than the destination buffer.
    /// </summary>
    StringTooLong,

    /// <summary>
    /// <c>ChunkFile_NotHandled</c>: a default chunk handler declined a chunk.
    /// </summary>
    NotHandled,
}
