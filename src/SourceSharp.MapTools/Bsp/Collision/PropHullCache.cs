//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Bsp.Collision;

/// <summary>
/// A host-owned cache of cooked static-prop hulls that outlives one compile:
/// bounded by bytes, least recently used out first, safe for any number of
/// concurrent compiles, and holding nothing but immutable cooked bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Every compile cooks one convex hull per distinct
/// <c>prop_static</c> model, and a map with a few hundred models spends
/// seconds of CPU doing it (about 2.4 CPU-s of a warm <c>sdk_ctf_2fort</c>
/// vbsp, some 40% of the stage). A service compiling many maps of one game,
/// or the same map again after an edit, cooks the same models over and over.
/// <see cref="StaticPropHullCache"/> already cooks each model once per
/// compile; this is the level above it, shared between compiles. Pass it as
/// <see cref="Compile.CompileRequest.PropHullCache"/> (or
/// <see cref="VbspContext.PropHullCache"/> when driving vbsp directly).
/// Without one, every compile cooks exactly as it always has.
/// </para>
/// <para>
/// <b>The key is the cook's whole input, never the model's name.</b> A hull
/// is a pure function of three things: the model's collision vertices (every
/// mesh's positions, in order, with the mesh boundaries), the cooker that
/// turns them into bytes, and the build of this library. The key is a
/// SHA-256 over all three: the vertices' raw bits; the cooker's
/// <see cref="ICollisionCooker.CookerIdentity"/> (the native cooker's names
/// its library's BuildID and MD5, the managed one names its arithmetic and
/// every compliance quirk it was built to reproduce) folded with
/// <see cref="ToolIdentity.Current"/>; and the compile's
/// <see cref="ComplianceOptions"/>, folded whole even though only the
/// cooker's share of it reaches the bytes today, so a quirk that starts to
/// reach the hull cook later cannot be served a hull cooked without it. A
/// name is deliberately not in it: two games' <c>models/crate.mdl</c> that
/// differ get two entries, and one model reached under two names gets one.
/// Because the key is the content, a hit is the same bytes as a fresh cook
/// by construction, which is what keeps the output byte-identical with and
/// without the cache.
/// </para>
/// <para>
/// <b>What it stores.</b> The cooked collide bytes, or the fact that the
/// meshes gave no convex (a "bad geometry" model the compile drops), as a
/// private copy. Every hit hands out a fresh copy, so nothing a compile does
/// with its array can reach another compile. A model that failed to load is
/// not a cook and is never stored: whether it loads belongs to the compile's
/// content, which the next compile may mount differently.
/// </para>
/// <para>
/// <b>Bound.</b> <see cref="MaxBytes"/> caps the sum of every entry's
/// cooked bytes plus <see cref="EntryOverheadBytes"/> each. Inserting past
/// it evicts the least recently used entries (a hit counts as a use) until
/// the new one fits; one entry larger than the whole bound is not stored at
/// all. Nothing else grows: the cache holds no per-compile, per-map or
/// per-name state, and its counters are plain integers.
/// </para>
/// <para>
/// <b>Failure and cancellation.</b> An entry is inserted only after its
/// cook has returned and the cooking compile's token has been checked once
/// more, so a cook that throws, or whose compile was cancelled while it ran,
/// leaves nothing behind. Concurrent compiles missing on the same key each
/// cook (the results are equal) and the first insert wins; there is
/// deliberately no shared in-flight cook, because that would let one
/// compile's cancellation fail another compile's hull.
/// </para>
/// <para>
/// <b>Lifetime.</b> The host creates it, shares it, and disposes it.
/// <see cref="Clear"/> empties it at any time, for example when the host
/// swaps game content or wants the memory back. Disposing it empties it too
/// and turns it into a pass-through: a compile still holding it keeps
/// working and simply cooks, so a service can shut its cache down without
/// failing compiles that are in flight.
/// </para>
/// </remarks>
public sealed class PropHullCache : IDisposable
{
    /// <summary>The default bound: 64 MiB, room for tens of thousands of typical 1-4 KB hulls.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// What each entry is charged besides its cooked bytes: its key, its list
    /// node and its record, rounded up, so that a flood of tiny or empty hulls
    /// is bounded too and not only large ones.
    /// </summary>
    public const int EntryOverheadBytes = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<PropHullKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = new();
    private long _bytes;
    private long _hits;
    private long _misses;
    private long _stored;
    private long _evictions;
    private long _rejected;
    private bool _disposed;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="maxBytes">The bound, in bytes; see <see cref="MaxBytes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is negative.</exception>
    public PropHullCache(long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        MaxBytes = maxBytes;
    }

    /// <summary>
    /// The most the entries may hold together: their cooked bytes plus
    /// <see cref="EntryOverheadBytes"/> each. Zero stores nothing.
    /// </summary>
    public long MaxBytes { get; }

    /// <summary>What the cache has seen since it was made (or last cleared); a consistent snapshot.</summary>
    public PropHullCacheStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new PropHullCacheStatistics(_entries.Count, _bytes, _hits, _misses, _stored, _evictions, _rejected);
            }
        }
    }

    /// <summary>Drops every entry and zeroes the counters. Safe while compiles are using the cache.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
            _hits = _misses = _stored = _evictions = _rejected = 0;
        }
    }

    /// <summary>
    /// Empties the cache and stops it storing. A compile still holding it
    /// keeps working: its lookups miss and its cooks are not kept.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// The cooked hull under a key, as a copy the caller owns; a hull whose
    /// meshes gave no convex comes back as a hit with a null blob.
    /// </summary>
    /// <param name="key">The cook's key.</param>
    /// <param name="blob">The bytes, or null for "no convex".</param>
    /// <returns>True on a hit.</returns>
    internal bool TryGet(PropHullKey key, out byte[]? blob)
    {
        lock (_gate)
        {
            if (!_disposed && _entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                _hits++;
                blob = node.Value.Blob?.ToArray();
                return true;
            }

            _misses++;
            blob = null;
            return false;
        }
    }

    /// <summary>
    /// Offers one finished cook. Kept as a private copy unless the key is
    /// already there (a concurrent compile got there first; its bytes are the
    /// same), the entry alone is larger than the bound, or the cache is
    /// disposed. Evicts least recently used entries to make room.
    /// </summary>
    /// <param name="key">The cook's key.</param>
    /// <param name="blob">The cooked bytes, or null for "no convex".</param>
    internal void Add(PropHullKey key, byte[]? blob)
    {
        long size = EntryOverheadBytes + (blob?.LongLength ?? 0);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return;
            }

            if (size > MaxBytes)
            {
                _rejected++;
                return;
            }

            while (_bytes + size > MaxBytes && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Size;
                _evictions++;
            }

            LinkedListNode<Entry> node = _recency.AddFirst(new Entry(key, blob?.ToArray(), size));
            _entries.Add(key, node);
            _bytes += size;
            _stored++;
        }
    }

    /// <summary>Whether a key has an entry, without counting a lookup or touching its recency (for the facts).</summary>
    /// <param name="key">The key.</param>
    /// <returns>True when it is held.</returns>
    internal bool Contains(PropHullKey key)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    private sealed record Entry(PropHullKey Key, byte[]? Blob, long Size);
}

/// <summary>A snapshot of a <see cref="PropHullCache"/>'s state.</summary>
/// <param name="Count">How many hulls it holds.</param>
/// <param name="Bytes">What they are charged against the bound (cooked bytes plus the per-entry overhead).</param>
/// <param name="Hits">Lookups answered from the cache.</param>
/// <param name="Misses">Lookups that had to cook.</param>
/// <param name="Stored">Cooks kept.</param>
/// <param name="Evictions">Entries dropped to make room.</param>
/// <param name="Rejected">Cooks not kept because one alone was larger than the bound.</param>
public readonly record struct PropHullCacheStatistics(
    int Count, long Bytes, long Hits, long Misses, long Stored, long Evictions, long Rejected);

/// <summary>
/// The key of one hull cook: a SHA-256 over the meshes' vertices and the
/// cook's context (see <see cref="PropHullCache"/>).
/// </summary>
/// <param name="A">Bytes 0-7 of the digest.</param>
/// <param name="B">Bytes 8-15.</param>
/// <param name="C">Bytes 16-23.</param>
/// <param name="D">Bytes 24-31.</param>
internal readonly record struct PropHullKey(ulong A, ulong B, ulong C, ulong D)
{
    /// <summary>A version for the key layout itself: bump it when the fold below changes meaning.</summary>
    private const string Layout = "prop-hull/1";

    /// <summary>
    /// The part of the key every cook of one compile shares: the layout, the
    /// build, the cooker and the compliance. Computed once per compile.
    /// </summary>
    /// <param name="cookerIdentity">The cooker's <see cref="ICollisionCooker.CookerIdentity"/>.</param>
    /// <param name="compliance">The compile's compliance.</param>
    /// <returns>The context, as bytes ready to fold.</returns>
    public static byte[] Context(string cookerIdentity, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(cookerIdentity);
        ArgumentNullException.ThrowIfNull(compliance);

        // Each part is length-prefixed by the fold below, so no separator
        // inside an identity string can make two contexts collide.
        string[] parts = [Layout, ToolIdentity.Of(cookerIdentity), OptionsDigest.Of(compliance)];
        using MemoryStream stream = new();
        foreach (string part in parts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(part);
            WriteInt(stream, bytes.Length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    /// <summary>The key of one model's cook.</summary>
    /// <param name="context">The compile's <see cref="Context"/>.</param>
    /// <param name="meshes">The meshes, as the cook is given them.</param>
    /// <returns>The key.</returns>
    public static PropHullKey Of(byte[] context, IReadOnlyList<Vec3[]> meshes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(meshes);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(number, context.Length);
        hash.AppendData(number);
        hash.AppendData(context);
        BinaryPrimitives.WriteInt32LittleEndian(number, meshes.Count);
        hash.AppendData(number);
        foreach (Vec3[] mesh in meshes)
        {
            // A null mesh is not something the loader produces; it is folded
            // as -1 so it can never alias an empty one.
            BinaryPrimitives.WriteInt32LittleEndian(number, mesh?.Length ?? -1);
            hash.AppendData(number);
            if (mesh is not null)
            {
                // The raw bits: -0 and 0, and every NaN payload, stay apart,
                // as they may in the cook. The process's byte order is the
                // same for every compile that shares the cache.
                hash.AppendData(MemoryMarshal.AsBytes(mesh.AsSpan()));
            }
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new PropHullKey(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
