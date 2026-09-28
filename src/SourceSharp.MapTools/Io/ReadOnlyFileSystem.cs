//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// A decorator that refuses every write.
/// </summary>
/// <remarks>
/// <para>
/// What a content mount is wrapped in. An installed game's <c>hl2</c> directory
/// is an input to a compile and nothing else, and the way to say so is to make
/// the attempt fail loudly at the seam rather than to trust that no reader ever
/// calls a writing method. It costs one indirection and removes a class of bug
/// where a tool writes a cubemap or a temp file into the user's game install.
/// </para>
/// <para>
/// Refusal is <see cref="UnauthorizedAccessException"/>, which is what the BCL
/// throws for "you may not write here" and what a caller that already handles
/// real read-only media is already catching.
/// </para>
/// </remarks>
public sealed class ReadOnlyFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    /// <summary>Wraps a file system so that only reads reach it.</summary>
    /// <param name="inner">The file system to protect.</param>
    public ReadOnlyFileSystem(IFileSystem inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default) =>
        _inner.OpenReadAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAllAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileRange> ReadRangeAsync(
        VPath path,
        long offset,
        int length,
        CancellationToken cancellationToken = default) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default) =>
        throw Refuse(path, "open for writing");

    /// <inheritdoc />
    public ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default) =>
        throw Refuse(path, "replace");

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
        _inner.ExistsAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetInfoAsync(path, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        _inner.EnumerateAsync(directory, searchPattern, recursive, cancellationToken);

    /// <inheritdoc />
    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
        throw Refuse(path, "delete");

    private static UnauthorizedAccessException Refuse(VPath path, string what) =>
        new($"this file system is read-only; refusing to {what} {path}");
}
