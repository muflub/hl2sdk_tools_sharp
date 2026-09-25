using System.Buffers.Binary;

namespace SourceSharp.MapFormats.Bsp.Structs;

/// <summary>
/// LUMP_VISIBILITY's header, decoded
/// (the reference layout's <c>struct dvis_t</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>dvis_t</c> cannot be a blittable struct: it declares
/// <c>int bitofs[8][2]</c> while the real shape is
/// <c>bitofs[numclusters][2]</c>, so the declared struct is a
/// deliberately undersized stand-in that is always indexed past its end. The
/// eight is not a limit, it is a placeholder.
/// </para>
/// <para>
/// So this is a reader over the lump, not a cast. The offsets it exposes are
/// byte offsets from the START OF THE LUMP into the compressed bit vectors that
/// follow the table.
/// </para>
/// </remarks>
public sealed class VisibilityLump
{
    private readonly ReadOnlyMemory<byte> _data;

    private VisibilityLump(ReadOnlyMemory<byte> data, int numClusters)
    {
        _data = data;
        NumClusters = numClusters;
    }

    /// <summary>The index of the PVS column in the offset table.</summary>
    public const int Pvs = 0;

    /// <summary>The index of the PAS column.</summary>
    public const int Pas = 1;

    /// <summary>How many vis clusters the map has.</summary>
    public int NumClusters { get; }

    /// <summary>Reads the lump's header.</summary>
    /// <param name="lump">The lump's bytes.</param>
    /// <returns>A reader over them, or null when the lump is empty.</returns>
    /// <exception cref="InvalidBspException">
    /// The lump is too short for the cluster count it declares.
    /// </exception>
    public static VisibilityLump? Read(BspLumpData lump)
    {
        if (lump.IsEmpty)
        {
            // vvis has not run. An unvised map is normal at this stage of a
            // compile, not corrupt.
            return null;
        }

        ReadOnlySpan<byte> bytes = lump.Data.Span;
        if (bytes.Length < sizeof(int))
        {
            throw new InvalidBspException(
                $"the visibility lump is {bytes.Length} bytes, too short for a cluster count");
        }

        int numClusters = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int tableEnd = sizeof(int) + (numClusters * 2 * sizeof(int));
        if (numClusters < 0 || tableEnd > bytes.Length)
        {
            throw new InvalidBspException(
                $"the visibility lump declares {numClusters} clusters, whose offset table alone "
                + $"would be {tableEnd} bytes of a {bytes.Length}-byte lump");
        }

        return new VisibilityLump(lump.Data, numClusters);
    }

    /// <summary>The byte offset of one cluster's compressed bit vector.</summary>
    /// <param name="cluster">The cluster index.</param>
    /// <param name="which"><see cref="Pvs"/> or <see cref="Pas"/>.</param>
    /// <returns>An offset from the start of the lump.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either index is out of range.</exception>
    public int BitOffset(int cluster, int which)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, NumClusters);
        ArgumentOutOfRangeException.ThrowIfNegative(which);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(which, Pas);

        int at = sizeof(int) + (((cluster * 2) + which) * sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(_data.Span.Slice(at, sizeof(int)));
    }

    /// <summary>
    /// The number of bytes one uncompressed visibility row occupies.
    /// </summary>
    /// <returns><c>(numClusters + 7) / 8</c>.</returns>
    /// <remarks>
    /// The reference compressor computes it as
    /// <c>(dvis-&gt;numclusters + 7) &gt;&gt; 3</c>; the same number bounds the
    /// decompressed row.
    /// </remarks>
    public int RowBytes() => (NumClusters + 7) >> 3;

    /// <summary>
    /// Decompresses one run-length coded visibility row.
    /// </summary>
    /// <param name="compressed">The lump bytes at and after the row's offset.</param>
    /// <param name="destination">A buffer of at least <see cref="RowBytes"/> bytes.</param>
    /// <returns>How many bytes of <paramref name="destination"/> were filled.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    /// <remarks>
    /// The reference decompressor's rule: a zero byte is followed by a repeat
    /// count, and anything else is a literal. The loop stops at the row length
    /// rather than at the end of the input, so a row that runs long is
    /// truncated exactly as the reference implementation truncates it.
    /// </remarks>
    public int DecompressRow(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        int row = RowBytes();
        if (destination.Length < row)
        {
            throw new ArgumentException(
                $"a visibility row is {row} bytes and the buffer is {destination.Length}",
                nameof(destination));
        }

        destination[..row].Clear();
        int outIndex = 0;
        int inIndex = 0;
        while (outIndex < row && inIndex < compressed.Length)
        {
            byte b = compressed[inIndex++];
            if (b != 0)
            {
                destination[outIndex++] = b;
                continue;
            }

            if (inIndex >= compressed.Length)
            {
                break;
            }

            int repeat = compressed[inIndex++];
            while (repeat > 0 && outIndex < row)
            {
                destination[outIndex++] = 0;
                repeat--;
            }
        }

        return outIndex;
    }
}
