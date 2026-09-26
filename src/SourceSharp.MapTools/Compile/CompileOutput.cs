//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Compile;

/// <summary>
/// What a <see cref="MapCompiler"/> compile writes: nothing at all, or the
/// files the stock tools leave beside a map.
/// </summary>
/// <remarks>
/// <para>
/// The result is in memory either way (<see cref="CompileResult.Bsp"/>).
/// Writing is a separate, optional rendering of it — in-memory first,
/// so <see cref="InMemory"/> is a compile with zero disk
/// writes and the normal way a fact or a service runs one.
/// </para>
/// <para>
/// <see cref="ToDirectory"/> writes what stock's three tools write, with their
/// names and their rules: <c>name.bsp</c>; <c>name.prt</c> for a sealed map
/// And <c>name.lin</c> for a leaked one; the stale
/// <c>.prt</c> and <c>.lin</c> of an earlier compile deleted first
/// And <c>name.log</c>, APPENDED to as stock's
/// <c>SetSpewFunctionLogFile</c> opens it (mode
/// <c>"a"</c>). The <c>.bsp</c> is written with
/// <see cref="IFileSystem.ReplaceAsync"/>, so a killed compile never leaves
/// half of one.
/// </para>
/// </remarks>
public sealed class CompileOutput
{
    private CompileOutput(IFileSystem? files, VPath directory, string? baseName)
    {
        Files = files;
        Directory = directory;
        BaseName = baseName;
    }

    /// <summary>Writes nothing. The compile's products are only in the result.</summary>
    public static CompileOutput InMemory { get; } = new(null, VPath.Empty, null);

    /// <summary>The file system written to, or null for <see cref="InMemory"/>.</summary>
    public IFileSystem? Files { get; }

    /// <summary>The directory the files are written into.</summary>
    public VPath Directory { get; }

    /// <summary>
    /// The files' base name, or null to use the source's
    /// <see cref="MapSource.Name"/>.
    /// </summary>
    public string? BaseName { get; }

    /// <summary>Whether anything is written.</summary>
    public bool WritesFiles => Files is not null;

    /// <summary>Writes stock's files into a directory.</summary>
    /// <param name="files">The file system to write through.</param>
    /// <param name="directory">The directory, which must exist.</param>
    /// <param name="baseName">The base name, or null for the map's own name.</param>
    /// <returns>The output choice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    public static CompileOutput ToDirectory(IFileSystem files, VPath directory, string? baseName = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new CompileOutput(files, directory, baseName);
    }

    internal VPath PathFor(string mapName, string extension)
    {
        string name = (BaseName ?? mapName) + extension;
        return Directory.IsEmpty ? VPath.Create(name) : Directory.Combine(name);
    }
}
