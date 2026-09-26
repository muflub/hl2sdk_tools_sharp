//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// One lump's bytes, as they sit in the file, with the two header fields that
/// describe them.
/// </summary>
/// <param name="Data">
/// The lump's payload. For a compressed lump this is the compressed bytes as
/// read; see <see cref="UncompressedSize"/>.
/// </param>
/// <param name="Version">
/// The lump's version. Most lumps are 0; the ones that are not are listed in
/// the reference layout's second enum and reproduced by
/// <see cref="BspWriteOrder"/>.
/// </param>
/// <param name="UncompressedSize">
/// Zero when the lump is stored uncompressed, which is what every reference
/// tool writes. A non-zero value means LZMA, and is the size to decompress to. The
/// compilers never emit this, so it is read-only state: a map that arrives
/// compressed is decompressed on load and written back out uncompressed.
/// </param>
public readonly record struct BspLumpData(
    ReadOnlyMemory<byte> Data,
    int Version,
    int UncompressedSize)
{
    /// <summary>An absent lump: no bytes, version 0, uncompressed.</summary>
    public static BspLumpData Empty => new(ReadOnlyMemory<byte>.Empty, 0, 0);

    /// <summary>The lump's length in bytes as the header records it.</summary>
    public int Length => Data.Length;

    /// <summary>Whether this lump holds no bytes at all.</summary>
    public bool IsEmpty => Data.Length == 0;
}

/// <summary>
/// One entry of the game lump: a nested lump identified by a four-character
/// code, carrying its own version.
/// </summary>
/// <param name="Id">
/// The four-character code, as the <see cref="int"/> the file stores. The codes
/// this branch writes are <c>sprp</c> (static props), <c>dprp</c> (detail
/// props), <c>dplt</c> and <c>dplh</c> (detail prop lighting, LDR and HDR).
/// </param>
/// <param name="Flags">
/// <c>GAMELUMPFLAG_COMPRESSED</c> (1) when the payload is LZMA. The compilers
/// never set it.
/// </param>
/// <param name="Version">
/// The nested lump's own version, independent of the outer lump's. The
/// reference build silently skips <c>sprp</c> below 4 and bails out of
/// <c>dprp</c> below 4, so
/// this field decides whether a map's props appear at all.
/// </param>
/// <param name="Data">The nested lump's payload.</param>
public readonly record struct GameLumpEntry(
    int Id,
    ushort Flags,
    ushort Version,
    ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// The <paramref name="code"/> four-character code as the <see cref="int"/>
    /// the file stores, in the byte order the format defines.
    /// </summary>
    /// <param name="code">Exactly four ASCII characters, for example <c>sprp</c>.</param>
    /// <returns>The packed identifier.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not exactly four characters.
    /// </exception>
    public static int MakeId(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 4)
        {
            throw new ArgumentException(
                $"a game lump id is exactly four characters, got \"{code}\"",
                nameof(code));
        }

        // The reference format spells these as C MULTI-CHARACTER CONSTANTS --
        // `GAMELUMP_STATIC_PROPS = 'sprp'` -- and both GCC and MSVC put the
        // leftmost character in the HIGHEST byte. So 'sprp' is 0x73707270, and
        // dm_lockdown.bsp's directory holds exactly that.
        //
        // This was written the other way round first, from a misreading of the
        // format, and the fact that was supposed to catch it asserted the same
        // misreading -- so it passed by comparing the code against itself. The
        // fact now reads the golden map's directory instead, because the FILE
        // is the authority and arithmetic restated in a test is not.
        //
        // Longhand rather than BinaryPrimitives so the order is visible and
        // cannot be flipped by a helper that means well.
        return (code[0] << 24) | (code[1] << 16) | (code[2] << 8) | code[3];
    }

    /// <summary>The four-character code this entry's <see cref="Id"/> spells.</summary>
    /// <returns>The four characters, high byte first.</returns>
    public string IdString() =>
        new([(char)((Id >> 24) & 0xFF), (char)((Id >> 16) & 0xFF), (char)((Id >> 8) & 0xFF), (char)(Id & 0xFF)]);
}

/// <summary>
/// Where one lump sat in the file it was read from.
/// </summary>
/// <param name="Offset">The byte offset the header recorded.</param>
/// <param name="Length">The byte length the header recorded.</param>
/// <param name="Version">The lump version the header recorded.</param>
/// <param name="Written">
/// False for a slot whose header entry was all zeroes, meaning the writer that
/// produced the file never emitted that lump at all. This is a different state
/// from an empty lump that WAS emitted, which records the writer's position and
/// a length of zero.
/// </param>
public readonly record struct BspLumpPlacement(int Offset, int Length, int Version, bool Written);

/// <summary>
/// Which lump order <see cref="BspFile.SaveAsync(BspData, Stream, BspWriteMode, CancellationToken)"/>
/// writes in.
/// </summary>
public enum BspWriteMode
{
    /// <summary>
    /// The order the reference writer uses: what the compilers emit, and
    /// what a freshly compiled map must be written in.
    /// </summary>
    Canonical,

    /// <summary>
    /// The order the file this data was loaded from used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because the canonical order is the order of the current reference
    /// writer, and maps outlive their compilers. A stock version 19 map writes
    /// no leaf-ambient lumps at all and does write lump 49, which the current
    /// reference writer abandoned -- so re-emitting it canonically produces a
    /// valid file that is not the same file.
    /// </para>
    /// <para>
    /// The distinction is not only about old maps: it is what lets an operation
    /// that rewrites one lump of someone else's map leave every other byte
    /// alone.
    /// </para>
    /// </remarks>
    PreserveSourceLayout,
}

/// <summary>
/// A whole BSP file in memory: the header fields, all 64 lumps, and the game
/// lump's nested entries.
/// </summary>
/// <remarks>
/// <para>
/// This is the type that replaces the reference compiler's roughly 110 MB of
/// file-scope <c>d*</c> arrays and their bare counters. Every stage of every
/// tool takes one
/// of these explicitly, which is what makes two compiles in one process legal
/// (design rule 1: no statics).
/// </para>
/// <para>
/// Lumps are held as raw bytes here. Typed views over them are a separate
/// concern, added per lump by the code that needs them -- which also means a
/// lump this port has no reader for still round-trips intact rather than being
/// dropped.
/// </para>
/// </remarks>
public sealed class BspData
{
    /// <summary><c>VBSP</c>, the only ident the format accepts.</summary>
    public const int Ident = ('P' << 24) | ('S' << 16) | ('B' << 8) | 'V';

    /// <summary>The oldest version the reference build will load.</summary>
    public const int MinVersion = 19;

    /// <summary>The version this branch writes.</summary>
    public const int Version = 20;

    /// <summary>
    /// The newest version this branch reads and can write. Stock's
    /// <c>BSPVERSION</c> is 20; 21 is the version the branches past TF2
    /// (ASW, L4D2, Portal 2, CS:GO) carry, and the only version at which a
    /// L4D2 re-laid-out lump directory is detectable at all.
    /// </summary>
    public const int MaxVersion = 21;

    /// <summary>The number of lump slots in the header. Fixed by the format.</summary>
    public const int HeaderLumps = 64;

    /// <summary>
    /// The size of the file header in bytes: ident, version, 64 lump entries of
    /// 16 bytes each, and the map revision.
    /// </summary>
    public const int HeaderSize = 4 + 4 + (HeaderLumps * 16) + 4;

    private readonly BspLumpData[] _lumps = new BspLumpData[HeaderLumps];

    /// <summary>
    /// The file version, 19 through <see cref="MaxVersion"/>. Preserved on load
    /// so a round trip does not silently upgrade a version 19 map.
    /// </summary>
    public int FileVersion { get; set; } = Version;

    /// <summary>
    /// The map's revision number, which Hammer increments on every save. The
    /// reference compiler copies it from the VMF's <c>mapversion</c>; nothing
    /// else reads it.
    /// </summary>
    public int MapRevision { get; set; }

    /// <summary>
    /// The game lump's nested entries, in the order they appear in the file.
    /// </summary>
    /// <remarks>
    /// Held apart from <see cref="this[BspLump]"/>'s
    /// <see cref="BspLump.GameLump"/> slot because the nested directory stores
    /// ABSOLUTE file offsets: the entries cannot survive being moved without
    /// their offsets being recomputed, so the writer rebuilds the directory
    /// rather than copying it.
    /// </remarks>
    public List<GameLumpEntry> GameLumps { get; } = [];

    /// <summary>
    /// Where each lump sat in the file this data was read from, or null when it
    /// was not read from a file at all.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="BspFile.LoadAsync"/> and never updated afterwards: it
    /// describes the source file, not the current contents. A stage that
    /// replaces a lump's bytes leaves this alone, and
    /// <see cref="BspWriteMode.PreserveSourceLayout"/> uses it for the ORDER
    /// only -- offsets and lengths are always recomputed from what is actually
    /// written.
    /// </remarks>
    public IReadOnlyList<BspLumpPlacement>? SourceLayout { get; internal set; }

    /// <summary>
    /// Whether the file this data was read from kept its 64 lump entries in
    /// the L4D2 re-layout: the same sixteen bytes per entry with the fields
    /// shifted one dword right, so each entry reads
    /// <c>(version, fileofs, filelen, uncompressedSize)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <see cref="BspFile.LoadAsync"/> when the header said version 21
    /// and the first lump's first dword was zero -- the detection this tool
    /// uses: a standard-layout file's dword there is the planes lump's offset,
    /// 1036, and never zero. It is the only detection that exists; the layout
    /// has no flag of its own.
    /// </para>
    /// <para>
    /// A preserve-mode write honours it, which is what lets a v21 L4D2 map
    /// round-trip byte-exact: reproducing the source's directory means
    /// reproducing its layout. It says nothing about how a canonical write
    /// should lay the directory out -- that is the
    /// <see cref="BspWriteFormat.L4d2LumpDirLayout"/> decision.
    /// </para>
    /// </remarks>
    public bool SourceLumpsUseL4d2Layout { get; internal set; }

    /// <summary>Reads or replaces one lump's data.</summary>
    /// <param name="lump">Which lump.</param>
    /// <returns>The lump's bytes, version and compression state.</returns>
    public BspLumpData this[BspLump lump]
    {
        get => _lumps[(int)lump];
        set => _lumps[(int)lump] = value;
    }

    /// <summary>Reads or replaces one lump's data by its raw slot number.</summary>
    /// <param name="index">A slot number in 0..63.</param>
    /// <returns>The lump's bytes, version and compression state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside 0..63.
    /// </exception>
    public BspLumpData this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, HeaderLumps);
            return _lumps[index];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, HeaderLumps);
            _lumps[index] = value;
        }
    }

    /// <summary>Replaces a lump's bytes, keeping a version of zero.</summary>
    /// <param name="lump">Which lump.</param>
    /// <param name="data">The new payload.</param>
    public void SetLump(BspLump lump, ReadOnlyMemory<byte> data) =>
        this[lump] = new BspLumpData(data, 0, 0);

    /// <summary>Replaces a lump's bytes and version.</summary>
    /// <param name="lump">Which lump.</param>
    /// <param name="data">The new payload.</param>
    /// <param name="version">The lump version to record in the header.</param>
    public void SetLump(BspLump lump, ReadOnlyMemory<byte> data, int version) =>
        this[lump] = new BspLumpData(data, version, 0);
}
