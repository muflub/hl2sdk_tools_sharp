//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// Reads a texture's VTF header -- and only its header -- out of game content.
/// </summary>
/// <remarks>
/// <para>
/// vbsp opens every texture a map's materials name, and all it wants from
/// each is in the first few hundred bytes: the width and height a material
/// reports, the reflectivity copied into the TEXDATA lump, and, for the
/// default cubemap, the skybox faces' size, flags, format and frame count.
/// Reading each file whole to get those was 70 to 90 MB of reads per compile
/// of a stock TF2 map, part of it memory-mapped, for a few hundred bytes of
/// answer per file. This reads <see cref="VtfFile.HeaderReadLength"/> bytes
/// with one ranged read and parses them against the file's real length, so
/// the answer and every rejection are what a whole-file
/// <see cref="VtfFile.Parse"/> gives.
/// </para>
/// <para>
/// A reader that needs pixels -- vrad's alpha shadows and macro textures --
/// still reads the whole file: the mip it decodes is at the END of the file,
/// so there is nothing a prefix could save it.
/// </para>
/// </remarks>
public static class VtfHeaderReader
{
    /// <summary>Reads and checks a VTF's header.</summary>
    /// <param name="content">Where the texture lives.</param>
    /// <param name="path">The texture's content path, <c>.vtf</c> included.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The header, or null when nothing has the file or it is not a VTF this
    /// port can read. The two are not told apart here because no caller
    /// treats them differently: the material system substitutes the same
    /// fallback for a missing preview image as for a bad one, and the
    /// default-cubemap builder asks <see cref="IContentFileSystem.ResolveAsync"/>
    /// separately when it needs to say which it was.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static async ValueTask<VtfHeader?> TryReadAsync(
        IContentFileSystem content,
        VPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using FileRange? range = await content
            .ReadRangeAsync(path, 0, VtfFile.HeaderReadLength, cancellationToken)
            .ConfigureAwait(false);

        if (range is null)
        {
            return null;
        }

        return TryParse(range.Memory.Span, range.FileLength);
    }

    /// <summary>
    /// Parses the bytes a range read returned, judging them against the file
    /// they came from.
    /// </summary>
    /// <param name="prefix">The range's bytes, from offset zero.</param>
    /// <param name="fileLength">The whole file's length, as the read reported it.</param>
    /// <returns>The header, or null when it is not a VTF this port can read.</returns>
    /// <remarks>
    /// A read can come back holding less than the file's length says it
    /// should: the file was cut short between the length and the read, or a
    /// store is serving a truncated copy. A whole read in that position hands
    /// the parser the bytes it got, and the parser judges the file by them.
    /// The same is done here -- the delivered bytes ARE the file as far as
    /// this read knows -- so a truncation is rejected or accepted exactly as a
    /// whole read of the same truncated bytes would be, instead of turning
    /// into an argument error blaming the caller.
    /// </remarks>
    internal static VtfHeader? TryParse(ReadOnlySpan<byte> prefix, long fileLength)
    {
        long judgedLength = prefix.Length < Math.Min(fileLength, VtfFile.HeaderReadLength)
            ? prefix.Length
            : fileLength;

        try
        {
            return VtfFile.ParseHeader(prefix, judgedLength);
        }
        catch (InvalidVtfException)
        {
            return null;
        }
    }
}
