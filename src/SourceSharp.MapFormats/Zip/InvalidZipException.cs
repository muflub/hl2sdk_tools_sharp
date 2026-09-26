//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// A pakfile is malformed, or uses something this port cannot read.
/// </summary>
/// <remarks>
/// The reference implementation's equivalents are an <c>Assert</c> that
/// vanishes in release, a <c>Warning</c> that carries on with bad data, a bare
/// <c>return NULL</c>, and a fatal <c>Error</c> that aborts the process. None
/// of those is available to a library, so every one of them becomes this,
/// carrying the reason.
/// </remarks>
public sealed class InvalidZipException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidZipException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the pak.</param>
    public InvalidZipException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the pak.</param>
    /// <param name="innerException">The failure that led here.</param>
    public InvalidZipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
