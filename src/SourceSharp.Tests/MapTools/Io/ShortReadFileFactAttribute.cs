//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using Xunit;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// A fact that needs a file whose size on disk is larger than what reading it
/// returns, SKIPPED (not passed) on a host without one.
/// </summary>
/// <remarks>
/// <para>
/// A file that shrinks between being measured and being read is a race, and a
/// race cannot be arranged reliably in a test. Linux's sysfs files are that
/// shape permanently: <c>stat</c> reports a page (4096 bytes) and a read
/// returns the few bytes the attribute holds. So the "the file ended early"
/// branch of a whole-file read can be driven for real there.
/// </para>
/// <para>
/// Other hosts, and Linux hosts without the file, skip with the reason rather
/// than passing having examined nothing.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ShortReadFileFactAttribute : FactAttribute
{
    /// <summary>The directory the short file is found under.</summary>
    public const string Root = "/sys/kernel/mm";

    /// <summary>The short file, relative to <see cref="Root"/>.</summary>
    public const string Relative = "transparent_hugepage/enabled";

    public ShortReadFileFactAttribute()
    {
        string path = Path.Combine(Root, Relative);
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
        {
            Skip = $"needs {path}, a file whose stat size exceeds its contents (Linux sysfs)";
            return;
        }

        try
        {
            // ReadAllText reads to the end; ReadAllBytes would trust the size.
            if (new FileInfo(path).Length <= File.ReadAllText(path).Length)
            {
                Skip = $"{path} reads back as long as its stat size here";
            }
        }
        catch (IOException exception)
        {
            Skip = $"{path} cannot be read: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            Skip = $"{path} cannot be read: {exception.Message}";
        }
    }
}
