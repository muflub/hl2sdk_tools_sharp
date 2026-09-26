//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Vpk;

/// <summary>
/// A VPK that cannot be read: a bad marker, a version the format does not
/// define, a directory that runs past its own declared size, or an entry whose
/// bytes are not there.
/// </summary>
/// <remarks>
/// A typed exception rather than a bare <see cref="IOException"/> so that the
/// compilers can tell "this archive is broken, and here is which entry" from
/// "the disk went away". The short-read case is the one that matters most: a
/// truncated archive part hands back fewer bytes than the directory promised,
/// and a reader that did not check would pass a short buffer to a VTF or MDL
/// parser and produce a confusing failure somewhere else entirely.
/// </remarks>
public sealed class InvalidVpkException : IOException
{
    /// <summary>Creates the exception.</summary>
    public InvalidVpkException()
        : base("the VPK is not readable")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, and where.</param>
    public InvalidVpkException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, and where.</param>
    /// <param name="innerException">The failure underneath.</param>
    public InvalidVpkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
