namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// The only two compression methods Source accepts in a pakfile: a port of
/// <c>IZip::eCompressionType</c> (<c>src/public/zip_utils.h:21-27</c>).
/// </summary>
/// <remarks>
/// <para>
/// This enumeration is the reason this assembly hand-writes a zip instead of
/// using <see cref="System.IO.Compression"/>. The engine's reader rejects
/// anything but these two -- <c>zip_utils.cpp:700-705</c> warns on a bad
/// method from a buffer and <c>:860-871</c> rejects the whole file from disk --
/// and <c>System.IO.Compression</c> writes DEFLATE with data descriptors,
/// which is method 8 with bit 3 of the flags set. A pak it produced could not
/// be read by the game at all.
/// </para>
/// <para>
/// DEFLATE is not "also supported, just unused". There is no code path in the
/// tree that accepts method 8.
/// </para>
/// </remarks>
public enum ZipCompressionMethod
{
    /// <summary>
    /// <c>eCompressionType_None</c>, method 0: the bytes are stored verbatim.
    /// The only method the map compilers ever WRITE.
    /// </summary>
    Store = 0,

    /// <summary>
    /// <c>eCompressionType_LZMA</c>, method 14. Read-only in this port; see
    /// <see cref="ZipArchiveReader"/>.
    /// </summary>
    Lzma = 14,
}
