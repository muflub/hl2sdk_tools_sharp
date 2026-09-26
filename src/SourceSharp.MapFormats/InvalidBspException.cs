//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats;

/// <summary>
/// A BSP file is malformed in a way that stops it being read at all.
/// </summary>
/// <remarks>
/// This is for files that are not BSPs, or whose lump table does not describe
/// the bytes present -- the cases where there is nothing to hand back. A map
/// that parses but breaks one of the engine's load-time rules is NOT this: that
/// is a validation finding, reported as data so a host can show the user every
/// problem at once rather than the first one.
/// </remarks>
public sealed class InvalidBspException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidBspException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public InvalidBspException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the file.</param>
    /// <param name="innerException">The failure that led here.</param>
    public InvalidBspException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
