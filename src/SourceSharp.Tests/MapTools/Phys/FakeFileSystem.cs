//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The little of <see cref="IFileSystem"/> discovery actually uses.
/// </summary>
/// <remarks>
/// Written here rather than reaching for a real filesystem so these facts
/// run on any machine, with no game installed and no disk touched.
/// </remarks>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = [];

    public void Add(string path, byte[] contents) => _files[VPath.Create(path).Value] = contents;

    public ValueTask<Stream> OpenReadAsync(VPath path, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(path.Value, out byte[]? bytes)
            ? ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException(path.Value);

    public ValueTask<FileInfoSnapshot?> GetInfoAsync(
        VPath path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_files.TryGetValue(path.Value, out byte[]? bytes)
            ? new FileInfoSnapshot(bytes.Length, DateTimeOffset.UnixEpoch)
            : (FileInfoSnapshot?)null);

    public async IAsyncEnumerable<VPath> EnumerateAsync(
        VPath directory,
        string searchPattern = "*",
        bool recursive = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        string prefix = directory.IsEmpty ? string.Empty : directory.Value + "/";
        foreach (string path in _files.Keys)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)
                && (searchPattern == "*" || VPath.Create(path).FileName == searchPattern))
            {
                yield return VPath.Create(path);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<bool> ExistsAsync(VPath path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_files.ContainsKey(path.Value));

    public ValueTask<IMemoryOwner<byte>> ReadAllAsync(
        VPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("discovery does not read whole files");

    public ValueTask<Stream> OpenWriteAsync(VPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("discovery never writes");

    public ValueTask ReplaceAsync(
        VPath path,
        Func<Stream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("discovery never writes");

    public ValueTask DeleteAsync(VPath path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("discovery never deletes");
    }
