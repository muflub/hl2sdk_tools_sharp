//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Collections.Concurrent;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// Material facts shared by several compiles over the same content: one read
/// of each material for all of them, from any number of threads at once.
/// The facts (<see cref="GetAsync"/>), the material files the patcher reads,
/// and the surface property table: everything a compile reads about the
/// kit's materials that no compile changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A room library compiles hundreds of small maps
/// against one kit of materials. Each compile has its own
/// <see cref="VbspContext"/>, and so its own <see cref="MaterialFactsCache"/>,
/// which is right for the tables a compile fills but meant that every room
/// read and parsed the kit's VMTs and base textures again: a 1024-room
/// library spent seconds of thread time inside the content filesystem
/// reading the same seven files. A <see cref="MaterialFacts"/> is a function
/// of the material name, the content and the material-system options, and
/// nothing a compile does changes it, so compiles that share all three can
/// share the answer.
/// </para>
/// <para>
/// <b>Only a success is shared.</b> The first compile to ask for a material
/// reads it on its own thread with its own token, and the others that ask
/// meanwhile wait for that read. If it fails or is cancelled, the entry is
/// dropped and each waiter asks again, so it reads for itself: a compile gets
/// exactly the answer (or the error) it would have got alone, and one room
/// that is cancelled or hits a read error never hands its failure to
/// another. There are no background reads: every read runs on a caller that
/// awaits it, so disposing the store leaves nothing running.
/// </para>
/// <para>
/// <b>The facts are read-only by contract.</b> A <see cref="MaterialFacts"/>
/// is immutable except for the parsed material it exposes through
/// <see cref="MaterialFacts.Material"/>, which every caller reads and the one
/// that patches it (<c>WorldVertexTransitionFixup.PatchMaterial</c>) clones
/// first. Sharing the object across concurrent compiles relies on that.
/// </para>
/// <para>
/// <b>Lifetime.</b> One store per batch of compiles (one <c>ssmap room</c>
/// run), disposed when the batch ends however it ended. Disposing empties
/// it, so a service that runs batch after batch keeps nothing from one to
/// the next; a store is never static and never keyed by map.
/// </para>
/// </remarks>
public sealed class SharedMaterialFacts : IDisposable
{
    // Every shared answer, by kind and key: "facts:" + normalised material
    // name, "file:" + content path (the patcher's parse), "bytes:" + folded
    // VMT path, and the one surface property table. One
    // dictionary, so Count and Dispose see everything the store holds.
    private readonly ConcurrentDictionary<string, Task<object?>> _entries = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    private const string SurfacePropertiesKey = "surfaceproperties";

    /// <summary>Creates an empty store over a content filesystem.</summary>
    /// <param name="content">Where materials are read from; the compiles sharing the store must read from the same one.</param>
    /// <param name="options">The material-system configuration, or null for the default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public SharedMaterialFacts(IContentFileSystem content, MaterialFactsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        Options = options ?? MaterialFactsOptions.Default;
        _materialFiles = new MaterialFileBytes(this);
    }

    // The content as the store's own reads see it: VMT bytes shared, every
    // other file read through. See MaterialFileBytes.
    private readonly MaterialFileBytes _materialFiles;

    /// <summary>The content as the store's reads see it, for the facts.</summary>
    internal IContentFileSystem MaterialFiles => _materialFiles;

    /// <summary>Where the materials are read from.</summary>
    public IContentFileSystem Content { get; }

    /// <summary>The material-system configuration every read uses.</summary>
    public MaterialFactsOptions Options { get; }

    /// <summary>How many answers the store holds, reads in progress included.</summary>
    public int Count => _entries.Count;

    /// <summary>Reads a material's facts once for every compile sharing the store.</summary>
    /// <param name="materialName">The material, as a map spells it.</param>
    /// <param name="cancellationToken">Cancels this caller's read or wait.</param>
    /// <returns>The facts, the same object for every caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialName"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    public async ValueTask<MaterialFacts> GetAsync(string materialName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        string key = MaterialFactsReader.Normalize(materialName);
        return (MaterialFacts)(await ShareAsync(
            "facts:" + key,
            async token => await MaterialFactsReader.ReadAsync(key, _materialFiles, Options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// A game material file as the material patcher parses it (the first
    /// root, or null for a file that is absent or will not parse), once for
    /// every compile sharing the store.
    /// </summary>
    /// <param name="path">The content path, as the patcher spells it.</param>
    /// <param name="parse">The patcher's parse of the file's bytes.</param>
    /// <param name="cancellationToken">Cancels this caller's read or wait.</param>
    /// <returns>The parsed tree, shared: read-only to every caller.</returns>
    /// <remarks>
    /// The cubemap pass asks every face's material whether it has an
    /// <c>$envmap</c>, which reads the VMT through the patcher rather than
    /// the facts; without this, each room read the kit's VMTs a second time.
    /// </remarks>
    internal async ValueTask<KeyValuesNode?> GetMaterialFileAsync(
        VPath path,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<KeyValuesNode?>> parse,
        CancellationToken cancellationToken)
    {
        return (KeyValuesNode?)await ShareAsync(
            "file:" + path.Value,
            async token =>
            {
                using IMemoryOwner<byte>? owner = await _materialFiles.ReadAsync(path, token).ConfigureAwait(false);
                return owner is null ? null : await parse(owner.Memory, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The game's surface properties (<c>scripts/surfaceproperties_manifest.txt</c>
    /// and the files it lists), loaded once for every compile sharing the store.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's read or wait.</param>
    /// <returns>The table, shared: every compile only looks names up in it.</returns>
    internal async ValueTask<SurfacePropertyTable> GetSurfacePropertiesAsync(CancellationToken cancellationToken) =>
        (SurfacePropertyTable)(await ShareAsync(
            SurfacePropertiesKey,
            async token => await SurfacePropertyTable.LoadAsync(Content, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false))!;

    /// <summary>
    /// The one read-or-wait every answer goes through: the first caller for a
    /// key reads, on its own thread and token; callers meanwhile wait; a
    /// success is kept, anything else is dropped and each waiter goes round.
    /// </summary>
    private async ValueTask<object?> ShareAsync(
        string key, Func<CancellationToken, Task<object?>> read, CancellationToken cancellationToken)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            if (_entries.TryGetValue(key, out Task<object?>? pending))
            {
                try
                {
                    return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!pending.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested)
                {
                    // Another compile's read failed or was cancelled; its
                    // entry is gone, so go round and read for ourselves.
                    continue;
                }
            }

            TaskCompletionSource<object?> fill = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_entries.TryAdd(key, fill.Task))
            {
                continue;
            }

            try
            {
                object? answer = await read(cancellationToken).ConfigureAwait(false);
                fill.SetResult(answer);
                return answer;
            }
            catch (Exception exception)
            {
                // Removed before the waiters are released, so a waiter that
                // goes round finds the slot free rather than this failure.
                _entries.TryRemove(new KeyValuePair<string, Task<object?>>(key, fill.Task));
                if (exception is OperationCanceledException cancelled)
                {
                    fill.SetCanceled(cancelled.CancellationToken);
                }
                else
                {
                    fill.SetException(exception);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// The store's content: a VMT's bytes are read once and shared by every
    /// parse of it (the facts reader's and the patcher's are different
    /// parses of the same file), and anything else is read through.
    /// </summary>
    /// <remarks>
    /// Only VMTs, which are a few hundred bytes of text. A base texture is
    /// read whole to take its header, and can be megabytes; keeping those
    /// bytes for the length of a batch would cost far more than the reads
    /// it saves, and the facts already keep the header's answer.
    /// </remarks>
    private sealed class MaterialFileBytes(SharedMaterialFacts store) : IContentFileSystem
    {
        public ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default) =>
            store.Content.ResolveAsync(path, cancellationToken);

        public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
        {
            if (!path.Value.EndsWith(MaterialFactsReader.MaterialExtension, StringComparison.OrdinalIgnoreCase))
            {
                return await store.Content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }

            // Folded, as the content lookup folds it: two spellings are one file.
            byte[]? bytes = (byte[]?)await store.ShareAsync(
                "bytes:" + ContentIndex.Fold(path),
                async token =>
                {
                    using IMemoryOwner<byte>? owner = await store.Content.ReadAsync(path, token).ConfigureAwait(false);
                    return owner?.Memory.ToArray();
                },
                cancellationToken).ConfigureAwait(false);

            // A copy per caller: the caller disposes it, and may write to it.
            return bytes is null ? null : PooledMemoryOwner.Copy(bytes);
        }

        public IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory, string searchPattern = "*", CancellationToken cancellationToken = default) =>
            store.Content.EnumerateAsync(directory, searchPattern, cancellationToken);
    }

    /// <summary>Empties the store; it refuses every later read.</summary>
    /// <remarks>
    /// Called by the owner of the batch once no compile is using the store
    /// any more (<see cref="Rooms.RoomLibraryCompiler"/> disposes it after
    /// its last room has ended), which is what makes "holds nothing" true
    /// without a lock on the read path: nobody is left to add an entry.
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        _entries.Clear();
    }
}
