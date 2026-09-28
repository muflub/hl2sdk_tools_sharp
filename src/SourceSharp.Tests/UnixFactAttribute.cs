//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// A fact about Linux and macOS path rules, SKIPPED (not passed) on Windows.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="WindowsFactAttribute"/>: a single-slash
/// root and a colon that is only a character are the host's rules, and on
/// Windows neither holds.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Linux and macOS path rules do not apply on Windows";
        }
    }
}
