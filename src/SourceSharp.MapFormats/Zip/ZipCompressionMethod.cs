//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Zip;

/// <summary>
/// The only two compression methods the reference pakfile format accepts.
/// </summary>
/// <remarks>
/// <para>
/// This enumeration is the reason this assembly hand-writes a zip instead of
/// using <see cref="System.IO.Compression"/>. The reference reader rejects
/// anything but these two -- it warns on a bad
/// method from a buffer and rejects the whole file from disk --
/// and <c>System.IO.Compression</c> writes DEFLATE with data descriptors,
/// which is method 8 with bit 3 of the flags set. A pak it produced could not
/// be read by the game at all.
/// </para>
/// <para>
/// DEFLATE is not "also supported, just unused". There is no path in
/// the reference implementation that accepts method 8.
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
