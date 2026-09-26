//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A <c>patch</c> material could not be resolved.
/// </summary>
/// <remarks>
/// The reference implementations only WARN and carry on with whatever they
/// have. The compile then continues
/// with a material that is missing its base's parameters, which shows up much
/// later as a surface with the wrong compile flags. A library reports it
/// instead of leaving the caller to notice.
/// </remarks>
public sealed class VmtPatchException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public VmtPatchException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What could not be resolved.</param>
    public VmtPatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What could not be resolved.</param>
    /// <param name="innerException">The failure that led here.</param>
    public VmtPatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
