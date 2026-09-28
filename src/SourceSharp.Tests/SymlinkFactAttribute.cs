//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System;
using System.IO;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// A fact that creates symbolic links, SKIPPED (not passed) on a machine
/// where this process may not create one.
/// </summary>
/// <remarks>
/// Linux and macOS always allow it. Windows needs Developer Mode or the
/// create-symbolic-link privilege, which a CI runner has and a locked-down
/// desktop may not; there the fact reports why it did not run instead of a
/// green row that examined nothing. The probe creates and deletes one link in
/// the temp folder when the attribute is built.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!CanCreateSymlinks())
        {
            Skip = "this process may not create symbolic links (on Windows: enable Developer Mode or run elevated)";
        }
    }

    private static bool CanCreateSymlinks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "symlink-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "target.txt");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
