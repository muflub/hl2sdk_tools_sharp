//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// The entities that belong to a whole room library rather than to one of
/// its rooms, and the room pack's library section that keeps them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entities.</b> A few classes set something for the whole map,
/// not for the room they stand in: the sun (<c>light_environment</c>), fog,
/// tone mapping, dynamic shadow direction and post-processing
/// (<c>env_fog_controller</c>, <c>env_tonemap_controller</c>,
/// <c>shadow_control</c>, <c>postprocess_controller</c>). Every room of a
/// library shares one of each (decision D3 of the rooms design: one sun for
/// the library), so an author puts it outside every cell, in the gaps
/// between rooms. The split used to ignore every point entity in the gaps,
/// which is right for the editor's clutter there (a note, a camera, a light
/// to see by) and silently wrong for these: the library's sun vanished
/// without a word. <see cref="RoomLibraryVmf.SplitLibrary"/> collects them
/// instead, and <c>ssmap room</c> writes them into the pack.
/// </para>
/// <para>
/// <b>Only from the gaps.</b> The same classes inside a cell stay with that
/// room, as before; whether a room may carry its own copy, and what the link
/// then emits, is the library-singletons rule, which builds on this section.
/// Every other class in the gaps is still ignored.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>) is written only when the
/// library has such an entity, so a library without one writes the same
/// pack as before this section existed. Its payload follows the pack's
/// convention for new sections: one codec byte (0, none: the only one this
/// build writes or reads, since a handful of entities gains nothing from
/// compression), the payload's uncompressed length as a big-endian
/// <c>int64</c>, then the entities as VMF chunk text, in library order and
/// in library coordinates, exactly as the library wrote them. Text rather
/// than a binary form because the flatten consumes VMF chunks and the link
/// consumes key-value pairs, and both read this text; library coordinates
/// because a gap has no room to be local to.
/// </para>
/// </remarks>
public static class RoomLibraryEntities
{
    /// <summary>The tag of the pack's library section that holds the library-wide entities.</summary>
    public const string SectionTag = "LENT";

    /// <summary>The codec byte for an uncompressed payload, the only one this build writes and reads.</summary>
    public const byte CodecNone = 0;

    // codec byte + int64 uncompressed length
    private const int HeaderBytes = 1 + 8;

    /// <summary>Whether a class is one the whole library shares, and so is collected from the gaps.</summary>
    /// <param name="classname">The entity's class, or null.</param>
    /// <returns>True for the sun, fog, tone map, shadow and post-process controllers, matched exactly as vbsp matches classes.</returns>
    public static bool IsLibraryWide(string? classname) => classname switch
    {
        "light_environment" or "env_fog_controller" or "env_tonemap_controller" or "shadow_control" or "postprocess_controller" => true,
        _ => false,
    };

    /// <summary>The pack section that holds a library's library-wide entities.</summary>
    /// <param name="entities">The entities, in library order (<see cref="RoomLibrarySplit.LibraryEntities"/>).</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    public static RoomPackSectionData ToSection(IReadOnlyList<VmfChunk> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        VmfDocument document = new();
        foreach (VmfChunk entity in entities)
        {
            document.Chunks.Add(VmfPlacement.Clone(entity));
        }

        byte[] text = document.ToBytes();
        byte[] bytes = new byte[HeaderBytes + text.Length];
        bytes[0] = CodecNone;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), text.Length);
        text.CopyTo(bytes, HeaderBytes);
        return new RoomPackSectionData(SectionTag, bytes);
    }

    /// <summary>Reads the library-wide entities back from their section's bytes.</summary>
    /// <param name="section">The section's bytes, as the pack holds them.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    /// <returns>The entities, in library order.</returns>
    /// <exception cref="LinkException">A codec this build does not read, a length that disagrees with the bytes, or text that is not VMF chunks.</exception>
    public static async ValueTask<IReadOnlyList<VmfChunk>> ReadAsync(ReadOnlyMemory<byte> section, CancellationToken cancellationToken = default)
    {
        if (section.Length < HeaderBytes)
        {
            throw new LinkException($"the room pack's {SectionTag} section is {section.Length} bytes, shorter than its {HeaderBytes}-byte header.");
        }

        byte codec = section.Span[0];
        if (codec != CodecNone)
        {
            throw new LinkException($"the room pack's {SectionTag} section has codec {codec}; this build reads codec {CodecNone} (none).");
        }

        long length = BinaryPrimitives.ReadInt64BigEndian(section.Span[1..]);
        if (length != section.Length - HeaderBytes)
        {
            throw new LinkException(
                $"the room pack's {SectionTag} section says {length} bytes of entities but holds {section.Length - HeaderBytes}.");
        }

        VmfDocument document;
        try
        {
            document = await VmfDocument.ParseAsync(section[HeaderBytes..], cancellationToken).ConfigureAwait(false);
        }
        catch (ChunkFileException exception)
        {
            throw new LinkException($"the room pack's {SectionTag} section is not VMF text: {exception.Message}");
        }

        return [.. document.Chunks];
    }
}
