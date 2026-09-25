using System.Collections.Immutable;

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// The CRC-32 a pakfile entry stores: the checksum the reference
/// implementation computes.
/// </summary>
/// <remarks>
/// <para>
/// The bog-standard one, and the point of saying so is that it is worth
/// checking rather than assuming. <c>CRC32_INIT_VALUE</c> and
/// <c>CRC32_XOR_VALUE</c> are both <c>0xFFFFFFFF</c> and the reference table is
/// the canonical reflected table for polynomial <c>0xEDB88320</c> -- its ninth
/// entry is <c>0x0EDB8832</c>. So this agrees with zlib, with
/// <c>System.IO.Hashing.Crc32</c>, and with every other zip tool.
/// </para>
/// <para>
/// Written out rather than taken from <c>System.IO.Hashing</c> because this
/// assembly takes no package reference, and the table is 256 entries generated
/// once per call site from the polynomial.
/// </para>
/// <para>
/// The table is a <c>static readonly</c> array of a primitive, which is not a
/// mutable static: it is initialised once and never assigned again.
/// </para>
/// </remarks>
public static class Crc32
{
    // An ImmutableArray, not a uint[], and not because a lookup table is
    // dangerous. The no-mutable-statics gate flags a `static readonly` ARRAY
    // because its ELEMENTS stay writable however readonly the field is, and
    // that rule is worth more than the convenience: it cannot distinguish this
    // table from genuinely shared state, and the version that could be argued
    // with case by case would eventually let the real thing through.
    // ImmutableArray is provably non-writable, so the rule stays strict and
    // this stays a constant. The indexer is a struct field read and inlines
    // away.
    private static readonly ImmutableArray<uint> Table = [.. BuildTable()];

    /// <summary>
    /// The polynomial, in its reflected form; the reference table is its
    /// expansion.
    /// </summary>
    public const uint Polynomial = 0xEDB88320u;

    /// <summary>
    /// <c>CRC32_INIT_VALUE</c>.
    /// </summary>
    public const uint InitialValue = 0xFFFFFFFFu;

    /// <summary>
    /// <c>CRC32_XOR_VALUE</c>.
    /// </summary>
    public const uint FinalXorValue = 0xFFFFFFFFu;

    /// <summary>Computes the CRC-32 of a block of bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = InitialValue;

        foreach (byte b in data)
        {
            // The cast is safe by the mask, not by inspection: & 0xFF bounds
            // the index to 0..255 before it is narrowed.
            crc = Table[(int)((crc ^ b) & 0xFF)] ^ (crc >> 8);
        }

        return crc ^ FinalXorValue;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
