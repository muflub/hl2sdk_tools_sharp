namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// The on-disk constants of a Source pakfile, from
/// <c>src/public/zip_uncompressed.h</c> and the writer in
/// <c>src/public/zip_utils.cpp</c>.
/// </summary>
/// <remarks>
/// Gathered in one place because byte-exactness is the gate for Phase 1b and
/// every one of these is a literal a stock pak contains. The structures are
/// inside <c>#pragma pack(1)</c> (<c>zip_uncompressed.h:21,115</c>), so the
/// sizes below are exact with no padding.
/// </remarks>
public static class ZipFormat
{
    /// <summary>
    /// <c>PKID(3,4)</c> -- the local file header signature, bytes
    /// <c>50 4B 03 04</c> (<c>zip_uncompressed.h:15</c>).
    /// </summary>
    public const uint LocalFileHeaderSignature = 0x04034B50u;

    /// <summary>
    /// <c>PKID(1,2)</c> -- the central directory file header signature.
    /// </summary>
    public const uint CentralDirectoryHeaderSignature = 0x02014B50u;

    /// <summary>
    /// <c>PKID(5,6)</c> -- the end-of-central-directory signature.
    /// </summary>
    public const uint EndOfCentralDirectorySignature = 0x06054B50u;

    /// <summary><c>ZIP_LocalFileHeader</c> (<c>zip_uncompressed.h:62-78</c>).</summary>
    public const int LocalFileHeaderSize = 30;

    /// <summary><c>ZIP_FileHeader</c> (<c>zip_uncompressed.h:37-60</c>).</summary>
    public const int CentralDirectoryHeaderSize = 46;

    /// <summary>
    /// <c>ZIP_EndOfCentralDirRecord</c> (<c>zip_uncompressed.h:23-35</c>).
    /// </summary>
    public const int EndOfCentralDirectorySize = 22;

    /// <summary>
    /// <c>XZIP_COMMENT_LENGTH</c> (<c>zip_uncompressed.h:90</c>).
    /// </summary>
    /// <remarks>
    /// EVERY zip this writer produces carries a 32-byte comment. It is not
    /// optional and not conditional: <c>MakeXZipCommentString</c>
    /// (<c>zip_utils.cpp:1303-1316</c>) returns the fixed length, and
    /// <c>CalculateSize</c> notes at <c>:1384-1385</c> that "All processed zip
    /// files will have a comment string". A writer that omits it produces a
    /// perfectly valid zip that is NOT byte-identical to a stock pak.
    /// </remarks>
    public const int CommentLength = 32;

    /// <summary>
    /// The comment a PC pak carries: <c>"XZP1 0"</c> padded to 32 bytes with
    /// NULs.
    /// </summary>
    /// <remarks>
    /// <c>V_snprintf(tempString, 32, "XZP%c %d", m_bCompatibleFormat ? '1' : '2', m_AlignmentSize)</c>
    /// (<c>zip_utils.cpp:1311</c>) over a buffer that was zeroed first
    /// (<c>:1307</c>). On the PC path <c>m_bCompatibleFormat</c> is true
    /// (<c>:515</c>) and <c>m_AlignmentSize</c> is zero (<c>:513</c>), so the
    /// text is exactly <c>"XZP1 0"</c> and the remaining 26 bytes are NUL.
    /// </remarks>
    public const string PcComment = "XZP1 0";

    /// <summary>
    /// <c>versionMadeBy</c> in the central header: 20
    /// (<c>zip_utils.cpp:1603</c>).
    /// </summary>
    /// <remarks>
    /// The comment there says why: "This is the version that the winzip that I
    /// have writes."
    /// </remarks>
    public const ushort VersionMadeBy = 20;

    /// <summary>
    /// <c>versionNeededToExtract</c> for a stored entry: 10
    /// (<c>zip_utils.cpp:1537,1604</c>). An LZMA entry uses 63.
    /// </summary>
    public const ushort VersionNeededToExtractStore = 10;

    /// <summary>
    /// <c>versionNeededToExtract</c> for an LZMA entry: 63
    /// (<c>zip_utils.cpp:1542,1609</c>).
    /// </summary>
    public const ushort VersionNeededToExtractLzma = 63;

    /// <summary>
    /// The alignment a PC pak uses: none.
    /// </summary>
    /// <remarks>
    /// <c>CZipFile</c>'s constructor sets <c>m_AlignmentSize = 0</c> and
    /// <c>m_bForceAlignment = false</c> (<c>zip_utils.cpp:513-514</c>), and
    /// <c>ForceAlignment</c> has no call site in the tree -- only the
    /// declaration at <c>src/utils/common/bsplib.h:206</c> and the pass-through
    /// at <c>bsplib.cpp:789-792</c>. So <c>GetAlignment()</c> returns 0
    /// (<c>zip_utils.cpp:597-605</c>), every <c>extraFieldLength</c> is 0, and
    /// <c>AlignFilePosition(g_hBSPFile, 0)</c> at <c>bsplib.cpp:797</c> is a
    /// no-op because that function returns early unless the alignment is at
    /// least 2 (<c>bsplib.cpp:731</c>).
    /// </remarks>
    public const int PcAlignment = 0;

    /// <summary>
    /// <c>LUMP_PAKFILE</c> (<c>src/public/bspfile.h:330</c>).
    /// </summary>
    /// <remarks>
    /// Written LAST, and that is load-bearing rather than incidental:
    /// <c>WriteBSPFile</c> calls <c>WritePakFileLump()</c> at
    /// <c>src/utils/common/bsplib.cpp:2737</c> immediately before
    /// <c>Lumps_Write()</c>, the repack path forces it last in the offset
    /// comparator (<c>bsplib.cpp:5037</c>, "force LUMP_PAKFILE to be last,
    /// always"), and the engine's loader expects it there
    /// (<c>engine/modelloader.cpp:643</c>).
    /// </remarks>
    public const int PakFileLumpIndex = 40;
}
