using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Vpk;

/// <summary>
/// Where one part of a file's bytes live.
/// </summary>
/// <param name="ArchiveIndex">
/// Which <c>_NNN.vpk</c> holds them, or
/// <see cref="VpkArchive.EmbeddedArchiveIndex"/> when they are in the directory
/// file itself.
/// </param>
/// <param name="Offset">Where they start, from the beginning of that file's data section.</param>
/// <param name="Length">How many bytes.</param>
public readonly record struct VpkFilePart(int ArchiveIndex, long Offset, long Length);

/// <summary>
/// One file in a VPK, as the directory describes it.
/// </summary>
/// <param name="Path">The file's path, in the archive's own spelling.</param>
/// <param name="Crc">
/// The CRC-32 the archive records for the file's bytes. Read back so a caller
/// CAN verify, and deliberately not verified on every read: a compile reads
/// tens of thousands of these and the cache verifies its own blobs by hash
/// anyway.
/// </param>
/// <param name="Preload">
/// The first bytes of the file, stored in the directory itself so that a header
/// can be read without touching an archive part. Concatenated before
/// <paramref name="Parts"/>, never instead of them.
/// </param>
/// <param name="Parts">Where the rest of the bytes live.</param>
/// <remarks>
/// The format allows a file to be split across several parts, and every
/// shipped VPK uses exactly one. Both are handled, because handling the general
/// case is a loop and handling only the common case is a reader that dies on
/// somebody's mod.
/// </remarks>
public sealed record VpkEntry(
    VPath Path,
    uint Crc,
    ReadOnlyMemory<byte> Preload,
    IReadOnlyList<VpkFilePart> Parts)
{
    /// <summary>The file's length in bytes: preload plus every part.</summary>
    public long Length
    {
        get
        {
            long total = Preload.Length;

            foreach (VpkFilePart part in Parts)
            {
                total += part.Length;
            }

            return total;
        }
    }
}
