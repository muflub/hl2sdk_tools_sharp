namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// The on-disk constants of a Source pakfile, as the format defines them and
/// as the reference pakfile writer emits them.
/// </summary>
/// <remarks>
/// Gathered in one place because byte-exactness is the gate for Phase 1b and
/// every one of these is a literal a stock pak contains. The reference layout
/// declares the structures under <c>#pragma pack(1)</c>, so the sizes below
/// are exact with no padding.
/// </remarks>
public static class ZipFormat
{
    /// <summary>
    /// <c>PKID(3,4)</c> -- the local file header signature, bytes
    /// <c>50 4B 03 04</c>.
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

    /// <summary><c>ZIP_LocalFileHeader</c>.</summary>
    public const int LocalFileHeaderSize = 30;

    /// <summary><c>ZIP_FileHeader</c>.</summary>
    public const int CentralDirectoryHeaderSize = 46;

    /// <summary>
    /// <c>ZIP_EndOfCentralDirRecord</c>.
    /// </summary>
    public const int EndOfCentralDirectorySize = 22;

    /// <summary>
    /// <c>XZIP_COMMENT_LENGTH</c>.
    /// </summary>
    /// <remarks>
    /// EVERY zip this writer produces carries a 32-byte comment. It is not
    /// optional and not conditional: the reference writer's
    /// <c>MakeXZipCommentString</c> returns the fixed length, and its
    /// <c>CalculateSize</c> notes that "All processed zip files will have a
    /// comment string". A writer that omits it produces a perfectly valid zip
    /// that is NOT byte-identical to a stock pak.
    /// </remarks>
    public const int CommentLength = 32;

    /// <summary>
    /// The comment a PC pak carries: <c>"XZP1 0"</c> padded to 32 bytes with
    /// NULs.
    /// </summary>
    /// <remarks>
    /// The reference writer formats <c>"XZP%c %d"</c> from a compatibility
    /// flag and an alignment size into a 32-byte buffer that was zeroed first.
    /// On the PC path the compatibility flag is true and the alignment size is
    /// zero, so the text is exactly <c>"XZP1 0"</c> and the remaining 26 bytes
    /// are NUL.
    /// </remarks>
    public const string PcComment = "XZP1 0";

    /// <summary>
    /// <c>versionMadeBy</c> in the central header: 20.
    /// </summary>
    /// <remarks>
    /// The comment in the reference writer says why: "This is the version that
    /// the winzip that I have writes."
    /// </remarks>
    public const ushort VersionMadeBy = 20;

    /// <summary>
    /// <c>versionNeededToExtract</c> for a stored entry: 10. An LZMA entry
    /// uses 63.
    /// </summary>
    public const ushort VersionNeededToExtractStore = 10;

    /// <summary>
    /// <c>versionNeededToExtract</c> for an LZMA entry: 63.
    /// </summary>
    public const ushort VersionNeededToExtractLzma = 63;

    /// <summary>
    /// The alignment a PC pak uses: none.
    /// </summary>
    /// <remarks>
    /// The reference writer's <c>CZipFile</c> constructor sets the alignment
    /// size to 0 and clears the force-alignment flag, and
    /// <c>ForceAlignment</c> has no call site anywhere -- only the declaration
    /// and a pass-through wrapper. So <c>GetAlignment()</c> returns 0, every
    /// <c>extraFieldLength</c> is 0, and the align call on the BSP file handle
    /// is a no-op because that function returns early unless the alignment is
    /// at least 2.
    /// </remarks>
    public const int PcAlignment = 0;

    /// <summary>
    /// <c>LUMP_PAKFILE</c>.
    /// </summary>
    /// <remarks>
    /// Written LAST, and that is load-bearing rather than incidental: the
    /// reference writer writes the pakfile lump immediately before writing the
    /// lump table, the repack path forces it last in the offset comparator
    /// ("force LUMP_PAKFILE to be last, always"), and the loader expects it
    /// there.
    /// </remarks>
    public const int PakFileLumpIndex = 40;
}
