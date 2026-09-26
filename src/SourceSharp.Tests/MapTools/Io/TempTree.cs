//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Io;

/// <summary>
/// A throwaway host directory for the handful of facts that must exercise a
/// REAL disk.
/// </summary>
/// <remarks>
/// <para>
/// Almost nothing should need this: a compile that never touches a disk is the
/// normal test here, and <see cref="InMemoryFileSystem"/> is what it runs on.
/// What genuinely cannot be tested in memory is
/// <see cref="PhysicalFileSystem"/> itself — whether the rename is atomic,
/// whether a killed write leaves the old <c>.bsp</c>, whether a memory-mapped
/// read gives back the same bytes as a copied one.
/// </para>
/// <para>
/// <c>System.IO</c> is used freely here on purpose. The rule that only
/// <see cref="PhysicalFileSystem"/> may touch it is about the LIBRARY, which is
/// where a stray <c>File.ReadAllBytes</c> would slip past the dependency
/// recorder; a fixture in the test assembly cannot make a compile read an
/// unrecorded file.
/// </para>
/// </remarks>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "ssmap-fs-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    /// <summary>The absolute host path of the directory.</summary>
    public string Root { get; }

    /// <summary>A file system rooted at this directory.</summary>
    /// <returns>The file system.</returns>
    public PhysicalFileSystem FileSystem() => new(Root);

    /// <summary>A file system rooted here that never memory-maps.</summary>
    /// <returns>The file system.</returns>
    public PhysicalFileSystem CopyingFileSystem() => new(Root, long.MaxValue);

    /// <summary>A file system rooted here that always memory-maps.</summary>
    /// <returns>The file system.</returns>
    public PhysicalFileSystem MappingFileSystem() => new(Root, 1);

    /// <summary>Writes a host file under this directory.</summary>
    /// <param name="relative">A relative path, using forward slashes.</param>
    /// <param name="contents">The bytes to write.</param>
    public void Write(string relative, byte[] contents)
    {
        string host = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, contents);
    }

    /// <summary>Reads a host file under this directory.</summary>
    /// <param name="relative">A relative path, using forward slashes.</param>
    /// <returns>Its bytes.</returns>
    public byte[] Read(string relative) =>
        File.ReadAllBytes(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The host entries directly under this directory.</summary>
    /// <returns>The file names, sorted.</returns>
    public string[] ListRoot()
    {
        string[] names = [.. Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName)!];
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}
