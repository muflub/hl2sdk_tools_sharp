//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.IO.Compression;

namespace SourceSharp.MapFormats.Nav;

/// <summary>How a navigation payload is stored: the codec byte of a <c>.nav3d</c> file and of a room's nav section.</summary>
public enum NavCodec : byte
{
    /// <summary>Stored as is.</summary>
    None = 0,

    /// <summary>Raw Deflate (RFC 1951, no zlib or gzip wrapper).</summary>
    Deflate = 1,

    /// <summary>Brotli (RFC 7932).</summary>
    Brotli = 2,
}

/// <summary>A codec and its effort: what a writer compresses with.</summary>
/// <param name="Codec">The codec.</param>
/// <param name="Level">
/// The effort: Deflate 0 to 9 (zlib's levels), Brotli 0 to 11 (its
/// quality). Ignored for <see cref="NavCodec.None"/>. A reader needs only
/// the codec; the level decides the size and the writer's time.
/// </param>
public readonly record struct NavCompression(NavCodec Codec, int Level)
{
    /// <summary>No compression.</summary>
    public static NavCompression None => new(NavCodec.None, 0);

    /// <summary>
    /// The largest window Brotli is given, as a power of two. Fixed rather
    /// than left to the library's default so the bytes do not change if
    /// that default ever does, and small enough (4 MiB) that a reader's
    /// window stays modest.
    /// </summary>
    public const int BrotliWindowLog2 = 22;

    /// <summary>Parses <c>none</c>, <c>deflate[:level]</c> or <c>brotli[:level]</c>.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="compression">The compression it names.</param>
    /// <returns>Whether the spelling is one of those, with a level in range.</returns>
    public static bool TryParse(string? text, out NavCompression compression)
    {
        compression = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split(':');
        if (parts.Length > 2)
        {
            return false;
        }

        (NavCodec codec, int max, int standard) = parts[0].ToUpperInvariant() switch
        {
            "NONE" => (NavCodec.None, 0, 0),
            "DEFLATE" => (NavCodec.Deflate, 9, 6),
            "BROTLI" => (NavCodec.Brotli, 11, 9),
            _ => ((NavCodec)255, -1, -1),
        };
        if (max < 0)
        {
            return false;
        }

        int level = standard;
        if (parts.Length == 2
            && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out level)
                || level > max || codec == NavCodec.None))
        {
            return false;
        }

        compression = new NavCompression(codec, level);
        return true;
    }

    /// <summary>Compresses bytes.</summary>
    /// <param name="raw">The bytes.</param>
    /// <returns>The stored bytes: <paramref name="raw"/> itself (copied) for <see cref="NavCodec.None"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The codec is unknown or the level out of range.</exception>
    /// <remarks>
    /// Deterministic: the same bytes, codec and level give the same output on
    /// every run and at any thread count, because both codecs are
    /// single-threaded and seeded by nothing but their input. .NET carries its
    /// own zlib-ng and Brotli builds, so the output is expected to be the
    /// same on every operating system too; the facts pin exact bytes so a
    /// platform that differs is caught.
    /// </remarks>
    public byte[] Compress(ReadOnlySpan<byte> raw)
    {
        switch (Codec)
        {
            case NavCodec.None:
                return raw.ToArray();
            case NavCodec.Deflate:
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(Level, 9);
                    ArgumentOutOfRangeException.ThrowIfNegative(Level);
                    using MemoryStream output = new();
                    using (DeflateStream deflate = new(output, new ZLibCompressionOptions
                    {
                        CompressionLevel = Level,
                        CompressionStrategy = ZLibCompressionStrategy.Default,
                    }, leaveOpen: true))
                    {
                        deflate.Write(raw);
                    }

                    return output.ToArray();
                }

            case NavCodec.Brotli:
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(Level, 11);
                    ArgumentOutOfRangeException.ThrowIfNegative(Level);
                    byte[] output = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
                    if (!BrotliEncoder.TryCompress(raw, output, out int written, Level, BrotliWindowLog2))
                    {
                        throw new InvalidOperationException("Brotli could not compress into its own worst-case bound.");
                    }

                    return output.AsSpan(0, written).ToArray();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(Codec), Codec, "no such codec.");
        }
    }

    /// <summary>Decompresses stored bytes whose raw length is known.</summary>
    /// <param name="codec">The codec they were stored with.</param>
    /// <param name="stored">The stored bytes.</param>
    /// <param name="rawLength">How long the raw bytes are.</param>
    /// <returns>The raw bytes.</returns>
    /// <exception cref="InvalidDataException">The codec is unknown, or the stored bytes do not decode to exactly <paramref name="rawLength"/> bytes.</exception>
    public static byte[] Decompress(NavCodec codec, ReadOnlySpan<byte> stored, int rawLength)
    {
        if (rawLength < 0)
        {
            throw new InvalidDataException($"a raw length of {rawLength} bytes.");
        }

        switch (codec)
        {
            case NavCodec.None:
                if (stored.Length != rawLength)
                {
                    throw new InvalidDataException($"{stored.Length} stored bytes for a raw length of {rawLength}.");
                }

                return stored.ToArray();
            case NavCodec.Deflate:
                {
                    byte[] raw = new byte[rawLength];
                    unsafe
                    {
                        fixed (byte* p = stored)
                        {
                            using UnmanagedMemoryStream input = new(p, stored.Length);
                            using DeflateStream inflate = new(input, CompressionMode.Decompress);
                            int filled = 0;
                            try
                            {
                                while (filled < rawLength)
                                {
                                    int read = inflate.Read(raw, filled, rawLength - filled);
                                    if (read == 0)
                                    {
                                        break;
                                    }

                                    filled += read;
                                }

                                Span<byte> probe = stackalloc byte[1];
                                if (filled != rawLength || inflate.Read(probe) != 0)
                                {
                                    throw new InvalidDataException($"the Deflate stream does not decode to {rawLength} bytes.");
                                }
                            }
                            catch (InvalidDataException)
                            {
                                throw;
                            }
                            catch (IOException exception)
                            {
                                throw new InvalidDataException($"the Deflate stream is corrupt: {exception.Message}");
                            }
                        }
                    }

                    return raw;
                }

            case NavCodec.Brotli:
                {
                    // Exactly the claimed length: a stream that decodes to
                    // more stops with "destination too small" rather than
                    // "done", and one that ends early writes less, so either
                    // is refused without a spare buffer or a copy.
                    byte[] raw = new byte[rawLength];
                    using BrotliDecoder decoder = new();
                    System.Buffers.OperationStatus status = decoder.Decompress(stored, raw, out int consumed, out int written);
                    if (status != System.Buffers.OperationStatus.Done || written != rawLength || consumed != stored.Length)
                    {
                        throw new InvalidDataException($"the Brotli stream does not decode to {rawLength} bytes.");
                    }

                    return raw;
                }

            default:
                throw new InvalidDataException($"codec {(byte)codec} is not one this build reads (0 none, 1 Deflate, 2 Brotli).");
        }
    }
}
