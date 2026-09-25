namespace SourceSharp.MapTools.Vis;

/// <summary>
/// The run-length coder LUMP_VISIBILITY stores its rows in:
/// <c>CompressVis</c> and <c>DecompressVis</c>
/// (<c>src/utils/common/bsplib.cpp:1441</c> and <c>:1477</c>).
/// </summary>
/// <remarks>
/// <para>
/// The code is tiny and asymmetric: a non-zero byte is a literal, and a zero
/// byte is followed by a repeat count of one to 255. So a zero byte always
/// costs TWO bytes, which is why the worst case is twice the row and not the
/// row plus a little.
/// </para>
/// <para>
/// Only the decoder exists in <c>SourceSharp.MapFormats</c>
/// (<c>VisibilityLump.DecompressRow</c>), because reading a map needs nothing
/// else. The encoder is here, with vvis, because vvis is what produces a
/// visibility lump. The two decoders are held against each other by a fact
/// rather than assumed to agree.
/// </para>
/// </remarks>
public static class VisRunLength
{
    /// <summary>The largest repeat one run can encode.</summary>
    /// <remarks>
    /// <c>bsplib.cpp:1458</c> breaks the run at <c>rep == 255</c>, so a longer
    /// stretch of zeroes becomes several runs.
    /// </remarks>
    public const int MaxRepeat = 255;

    /// <summary>
    /// The largest output <see cref="Compress"/> can produce for a row.
    /// </summary>
    /// <param name="rowBytes">The uncompressed row length.</param>
    /// <returns>Twice <paramref name="rowBytes"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rowBytes"/> is negative.
    /// </exception>
    /// <remarks>
    /// Reached by a row of alternating zero and non-zero bytes: every zero
    /// costs its own byte plus a repeat count of one. Stock has no such bound
    /// and writes straight into the vismap, which is why it needs the
    /// "Vismap expansion overflow" check (<c>vvis.cpp:270</c>) at all.
    /// </remarks>
    public static int MaxCompressedLength(int rowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowBytes);
        return rowBytes * 2;
    }

    /// <summary>Compresses one uncompressed visibility row.</summary>
    /// <param name="row">
    /// The row, exactly <c>(numclusters + 7) &gt;&gt; 3</c> bytes. Stock reads
    /// that many from a larger buffer; here the caller passes the slice, so the
    /// length IS the row.
    /// </param>
    /// <param name="destination">
    /// Where the coded bytes go; must be at least
    /// <see cref="MaxCompressedLength"/> of the row.
    /// </param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small for the worst case.
    /// </exception>
    public static int Compress(ReadOnlySpan<byte> row, Span<byte> destination)
    {
        if (destination.Length < MaxCompressedLength(row.Length))
        {
            throw new ArgumentException(
                $"compressing a {row.Length}-byte row needs {MaxCompressedLength(row.Length)} bytes "
                + $"of room in the worst case and was given {destination.Length}",
                nameof(destination));
        }

        int written = 0;

        // bsplib.cpp:1451-1465, including the j-- that hands the byte the inner
        // loop stopped on back to the outer loop. Written with the same index
        // arithmetic rather than restructured, because the boundary case --
        // stopping at rep == 255 WITHOUT consuming that byte -- is exactly what
        // a tidier loop tends to get wrong.
        for (int j = 0; j < row.Length; j++)
        {
            destination[written++] = row[j];
            if (row[j] != 0)
            {
                continue;
            }

            int rep = 1;
            for (j++; j < row.Length; j++)
            {
                if (row[j] != 0 || rep == MaxRepeat)
                {
                    break;
                }

                rep++;
            }

            destination[written++] = (byte)rep;
            j--;
        }

        return written;
    }

    /// <summary>Decompresses one row, as stock's <c>DecompressVis</c> does.</summary>
    /// <param name="compressed">
    /// The lump bytes at and after the row's offset. Reading stops when the row
    /// is full, so passing the rest of the lump is correct and is what the
    /// engine does.
    /// </param>
    /// <param name="row">
    /// The destination, exactly <c>(numclusters + 7) &gt;&gt; 3</c> bytes. Its
    /// length IS the row length the decoder stops at.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// A zero byte is followed by a repeat count of zero, which stock treats as
    /// a fatal error (<c>bsplib.cpp:1497</c>), or the input ends mid-run.
    /// </exception>
    /// <remarks>
    /// Stock's overrun case -- a run that would write past the row -- is
    /// clamped with a warning rather than an error (<c>bsplib.cpp:1500-1504</c>),
    /// and is clamped here too.
    /// </remarks>
    public static void Decompress(ReadOnlySpan<byte> compressed, Span<byte> row)
    {
        int outIndex = 0;
        int inIndex = 0;

        while (outIndex < row.Length)
        {
            if (inIndex >= compressed.Length)
            {
                throw new InvalidDataException(
                    $"a visibility row ran out of input after {outIndex} of {row.Length} bytes");
            }

            byte b = compressed[inIndex];
            if (b != 0)
            {
                row[outIndex++] = b;
                inIndex++;
                continue;
            }

            if (inIndex + 1 >= compressed.Length)
            {
                throw new InvalidDataException("a visibility row ended on a zero with no repeat count");
            }

            int c = compressed[inIndex + 1];
            if (c == 0)
            {
                throw new InvalidDataException("DecompressVis: 0 repeat");
            }

            inIndex += 2;
            if (outIndex + c > row.Length)
            {
                c = row.Length - outIndex;
            }

            while (c > 0)
            {
                row[outIndex++] = 0;
                c--;
            }
        }
    }
}
