//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapCompile;

/// <summary>
/// How a <see cref="VPath"/> the CLI built from a host path is shown to the
/// person who typed that host path.
/// </summary>
/// <remarks>
/// <para>
/// Every verb resolves the paths on its line to full host paths and makes a
/// <see cref="VPath"/> of each, on a disk rooted at the host's filesystem
/// root. A <see cref="VPath"/> carries no root, so its
/// <see cref="VPath.Value"/> is the host path with the root cut off:
/// <c>/tmp/x/rooms/cross.room</c> became <c>tmp/x/rooms/cross.room</c>, which
/// looks like a path relative to wherever the reader happens to be and is
/// not one. Messages print what this class gives back instead: the root
/// restored, in the host's own separators, so the line names the file the
/// user can open.
/// </para>
/// <para>
/// The composition is <see cref="Path.Combine(string, string)"/>, the same one
/// <see cref="PhysicalFileSystem.ToHostPath"/> makes, and the host's own rules
/// are what keep it right. On Windows a full path already carries its drive
/// (<c>D:/game/...</c>), a rooted second operand wins, and so the drive is kept
/// rather than prefixed with the current drive's root; a path without one (a
/// listing under the host root) sits on the root's drive. On Linux <c>C:</c>
/// is an ordinary directory name, not rooted, so <c>/C:/x</c> keeps its slash.
/// It is done on the strings rather than through a file system so it gives the
/// same answer whatever disk is behind the command: the facts run the verbs
/// over an in-memory disk laid out like the host's, and a host may do the same.
/// </para>
/// <para>
/// Public because the CLI gets no <c>InternalsVisibleTo</c>: the facts that
/// check each spelling call this directly.
/// </para>
/// </remarks>
public static class HostPaths
{
    /// <summary>
    /// A path as this host spells it: its root restored and its separators
    /// the host's.
    /// </summary>
    /// <param name="path">A path on a disk rooted at the host's filesystem root.</param>
    /// <returns>The absolute host path.</returns>
    public static string Display(VPath path) =>
        Display(path, Path.GetPathRoot(Path.GetFullPath("/")) ?? "/");

    /// <summary>
    /// A path on a disk rooted at <paramref name="root"/>, as this host spells it.
    /// </summary>
    /// <param name="path">A path relative to <paramref name="root"/>, or carrying its own drive.</param>
    /// <param name="root">The host directory the disk is rooted at.</param>
    /// <returns>The host path.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public static string Display(VPath path, string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar));
    }
}
