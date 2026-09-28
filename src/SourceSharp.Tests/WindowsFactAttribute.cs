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
/// A fact about Windows path rules, SKIPPED (not passed) on any other host.
/// </summary>
/// <remarks>
/// Drive letters and backslashes are the host's rules, applied by
/// <c>System.IO.Path</c> as the running OS has them, so a fact that checks a
/// Windows spelling can only mean something on Windows. CI runs the suite
/// there; elsewhere the fact says why it did not run instead of a green row
/// that examined nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path rules apply only on Windows";
        }
    }
}
