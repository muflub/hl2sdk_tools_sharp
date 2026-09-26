//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// A whole chunk file -- a <c>.vmf</c>, a <c>.vmm</c> manifest, a
/// <c>.vmm_prefs</c> -- as a list of top-level chunks.
/// </summary>
/// <remarks>
/// <para>
/// A VMF has no single root: <c>versioninfo</c>, <c>visgroups</c>,
/// <c>viewsettings</c>, <c>world</c> and every <c>entity</c> are siblings at
/// depth zero, which is why the reference load loop calls <c>ReadChunk</c> in
/// a <c>while</c> until it returns EOF.
/// </para>
/// <para>
/// Reading and writing both go through a <see cref="Stream"/> and are async and
/// cancellable: a 900 KB map is a long parse, and a compile that is cancelled
/// half way through loading should stop loading.
/// </para>
/// </remarks>
public sealed class VmfDocument
{
    /// <summary>
    /// How often the parser checks for cancellation, counted in terms read.
    /// </summary>
    /// <remarks>
    /// Per term would put an interlocked read in the innermost loop of the
    /// parse for no benefit; a whole document is the wrong granularity because
    /// it is the thing being cancelled. A few thousand terms is well under a
    /// millisecond either way.
    /// </remarks>
    private const int CancellationCheckInterval = 4096;

    /// <summary>The top-level chunks, in file order.</summary>
    public IList<VmfChunk> Chunks { get; } = [];

    /// <summary>The first top-level chunk with this name, or null.</summary>
    /// <param name="name">The chunk name.</param>
    /// <returns>The chunk, or null.</returns>
    public VmfChunk? GetChunk(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Chunks.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every top-level chunk with this name, in file order.</summary>
    /// <param name="name">The chunk name.</param>
    /// <returns>The matching chunks.</returns>
    public IEnumerable<VmfChunk> GetChunks(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Chunks.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses a chunk file from a stream.</summary>
    /// <param name="stream">The bytes of the file, read to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file is malformed.</exception>
    public static async Task<VmfDocument> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await ParseAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a chunk file already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ChunkFileException">The file is malformed.</exception>
    public static ValueTask<VmfDocument> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        ParseAsync(Encoding.Latin1.GetString(bytes.Span), cancellationToken);

    /// <summary>Parses a chunk file from decoded text.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ChunkFileException">The file is malformed.</exception>
    public static ValueTask<VmfDocument> ParseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Synchronous work over memory, wrapped so the public shape is the
        // assembly's one shape and the caller can still cancel it. There is no
        // IO to await, so offering a Task here would only cost a thread.
        try
        {
            return ValueTask.FromResult(Parse(text, cancellationToken));
        }
        catch (OperationCanceledException exception)
        {
            return ValueTask.FromCanceled<VmfDocument>(
                exception.CancellationToken.IsCancellationRequested
                    ? exception.CancellationToken
                    : cancellationToken);
        }
    }

    /// <summary>Writes the document in Hammer's exact framing.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when everything is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public async Task WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] bytes = ToBytes();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The document's bytes, exactly as <see cref="WriteAsync"/> would write
    /// them.
    /// </summary>
    /// <returns>The encoded file.</returns>
    public byte[] ToBytes()
    {
        ChunkFileWriter writer = new();

        foreach (VmfChunk chunk in Chunks)
        {
            WriteChunk(writer, chunk);
        }

        return writer.ToBytes();
    }

    private static void WriteChunk(ChunkFileWriter writer, VmfChunk chunk)
    {
        writer.BeginChunk(chunk.Name);

        foreach (VmfNode node in chunk.Children)
        {
            switch (node)
            {
                case VmfKey key:
                    writer.WriteKeyValue(key.Name, key.Value);
                    break;

                case VmfChunk child:
                    WriteChunk(writer, child);
                    break;

                default:
                    throw new ChunkFileException(
                        $"unknown node type {node.GetType().Name}");
            }
        }

        writer.EndChunk();
    }

    private static VmfDocument Parse(string text, CancellationToken cancellationToken)
    {
        ChunkTokenReader tokens = new(text);
        ChunkFileReader reader = new(tokens);
        VmfDocument document = new();

        // The stack of chunks we are inside. Empty means depth zero.
        Stack<VmfChunk> open = new();
        int sinceCheck = 0;

        while (true)
        {
            if (++sinceCheck >= CancellationCheckInterval)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            ChunkFileResult result =
                reader.ReadNext(out string name, out string value, out ChunkTermType termType);

            switch (result)
            {
                case ChunkFileResult.Ok when termType == ChunkTermType.Chunk:
                {
                    VmfChunk chunk = new(name);
                    if (open.Count > 0)
                    {
                        open.Peek().Children.Add(chunk);
                    }
                    else
                    {
                        document.Chunks.Add(chunk);
                    }

                    open.Push(chunk);
                    break;
                }

                case ChunkFileResult.Ok:
                {
                    if (open.Count == 0)
                    {
                        // A key at depth zero. The reference loop passes no key
                        // handler at the top level, so
                        // the pair is read and dropped; this port refuses it,
                        // because silently discarding data from a file it was
                        // asked to round-trip is worse than a diagnostic.
                        throw new ChunkFileException(
                            ChunkFileResult.UnexpectedSymbol, tokens.Line, name);
                    }

                    open.Peek().Children.Add(new VmfKey(name, value));
                    break;
                }

                case ChunkFileResult.EndOfChunk:
                {
                    if (open.Count == 0)
                    {
                        // A '}' with nothing open. ReadNext has already taken
                        // the depth negative, which is how
                        // stock then turns a clean EOF into UnexpectedEOF.
                        throw new ChunkFileException(
                            ChunkFileResult.UnexpectedSymbol, tokens.Line, "}");
                    }

                    open.Pop();
                    break;
                }

                case ChunkFileResult.EndOfFile:
                    return document;

                default:
                    throw new ChunkFileException(result, tokens.Line, reader.ErrorToken);
            }
        }
    }
}
