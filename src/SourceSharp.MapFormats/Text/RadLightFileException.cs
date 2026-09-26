//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>lights.rad</c> file cannot be used.
/// </summary>
/// <remarks>
/// There is exactly one condition that aborts the reference lightmapper while
/// reading one:
/// more than <see cref="RadLightFile.MaxTexLights"/> texlights. Everything
/// else -- a missing
/// file, a malformed value, a duplicate -- is a <c>Warning</c> or a <c>Msg</c>
/// and the compile carries on.
/// </remarks>
public sealed class RadLightFileException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public RadLightFileException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public RadLightFileException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What is wrong with the file.</param>
    /// <param name="innerException">The failure that led here.</param>
    public RadLightFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
