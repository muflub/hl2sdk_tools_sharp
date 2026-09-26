//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;

namespace SourceSharp.MapTools.Phys;

/// <summary>Which instruction set an ELF image was built for.</summary>
public enum ElfArchitecture
{
    /// <summary>Not an ELF image, or one this reader does not understand.</summary>
    Unknown,

    /// <summary>32-bit x86. Cannot be loaded by a 64-bit host process.</summary>
    X86,

    /// <summary>64-bit x86.</summary>
    X64,
}

/// <summary>
/// The two things about a shared library that decide whether it can be used
/// and which build it is.
/// </summary>
/// <param name="Architecture">The instruction set.</param>
/// <param name="BuildId">
/// The GNU build id as lower-case hex, or null when the image carries none.
/// </param>
/// <remarks>
/// The build id matters more here than it looks. Spike 0b measured that two
/// builds of <c>vphysics.so</c> cook the same cube to different bytes -- 15 of
/// 440, in the mass centre and one ULP of the inertia tensor -- so a collision
/// result is only reproducible if the library that produced it can be named
/// exactly. A path is not enough: the same path holds different bytes after a
/// game update.
/// </remarks>
public readonly record struct ElfIdentity(ElfArchitecture Architecture, string? BuildId)
{
    /// <summary>Nothing could be read.</summary>
    public static ElfIdentity Unknown => new(ElfArchitecture.Unknown, null);

    /// <summary>Whether a 64-bit host process could load this image at all.</summary>
    public bool IsLoadableHere => Architecture == ElfArchitecture.X64;
}

/// <summary>
/// Reads an ELF image's architecture and GNU build id, without loading it.
/// </summary>
/// <remarks>
/// Deliberately a READER over bytes rather than anything that calls
/// <c>dlopen</c>. Discovery has to describe libraries it will not use --
/// including 32-bit ones, which a 64-bit host cannot load at all -- and
/// answering "what is this?" by trying to load it would crash on exactly the
/// files that most need reporting.
/// </remarks>
public static class ElfIdentityReader
{
    private const int NtGnuBuildId = 3;
    private const uint PtNote = 4;

    /// <summary>
    /// Reads the identity of the ELF image in <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">A seekable stream over the whole file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// What could be read. A file that is not an ELF image, or is a form this
    /// reader does not handle, yields <see cref="ElfIdentity.Unknown"/> rather
    /// than throwing: discovery walks directories full of files that are not
    /// libraries, and that is not an error.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static async ValueTask<ElfIdentity> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek || stream.Length < 64)
        {
            return ElfIdentity.Unknown;
        }

        byte[] header = new byte[64];
        stream.Seek(0, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
        {
            return ElfIdentity.Unknown;
        }

        // EI_CLASS: 1 is 32-bit, 2 is 64-bit. EI_DATA: 1 is little-endian.
        // Source ships only little-endian images, and a big-endian one would
        // need every field below byte-swapped, so it is reported as unknown
        // rather than mis-read.
        byte elfClass = header[4];
        if (header[5] != 1)
        {
            return ElfIdentity.Unknown;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
        ElfArchitecture architecture = (elfClass, machine) switch
        {
            (2, 62) => ElfArchitecture.X64,   // EM_X86_64
            (1, 3) => ElfArchitecture.X86,    // EM_386
            _ => ElfArchitecture.Unknown,
        };

        // Only the 64-bit program-header layout is walked for the build id.
        // A 32-bit image is reported with its architecture and no build id,
        // which is all a caller needs: it will be rejected for being 32-bit
        // long before anyone wants its identity.
        if (elfClass != 2)
        {
            return new ElfIdentity(architecture, null);
        }

        long programHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(54));
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(56));

        if (programHeaderOffset <= 0 || entrySize < 56 || entryCount <= 0
            || programHeaderOffset + ((long)entrySize * entryCount) > stream.Length)
        {
            return new ElfIdentity(architecture, null);
        }

        byte[] entry = new byte[entrySize];
        for (int i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            stream.Seek(programHeaderOffset + ((long)entrySize * i), SeekOrigin.Begin);
            await stream.ReadExactlyAsync(entry, cancellationToken).ConfigureAwait(false);

            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != PtNote)
            {
                continue;
            }

            long noteOffset = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8));
            long noteLength = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(32));

            if (noteOffset < 0 || noteLength <= 0 || noteOffset + noteLength > stream.Length)
            {
                continue;
            }

            string? buildId = await ReadBuildIdAsync(stream, noteOffset, (int)noteLength, cancellationToken)
                .ConfigureAwait(false);

            if (buildId is not null)
            {
                return new ElfIdentity(architecture, buildId);
            }
        }

        return new ElfIdentity(architecture, null);
    }

    private static async ValueTask<string?> ReadBuildIdAsync(
        Stream stream,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        byte[] notes = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(notes, cancellationToken).ConfigureAwait(false);

        int position = 0;
        while (position + 12 <= length)
        {
            int nameSize = BinaryPrimitives.ReadInt32LittleEndian(notes.AsSpan(position));
            int descSize = BinaryPrimitives.ReadInt32LittleEndian(notes.AsSpan(position + 4));
            int type = BinaryPrimitives.ReadInt32LittleEndian(notes.AsSpan(position + 8));

            if (nameSize < 0 || descSize < 0)
            {
                return null;
            }

            // Both the name and the descriptor are padded out to a four-byte
            // boundary, and the padding is not counted in the sizes.
            int namePadded = (nameSize + 3) & ~3;
            int descPadded = (descSize + 3) & ~3;
            int descStart = position + 12 + namePadded;

            if (descStart + descPadded > length)
            {
                return null;
            }

            bool isGnu = nameSize >= 4
                && notes[position + 12] == (byte)'G'
                && notes[position + 13] == (byte)'N'
                && notes[position + 14] == (byte)'U';

            if (isGnu && type == NtGnuBuildId && descSize > 0)
            {
                return Convert.ToHexString(notes.AsSpan(descStart, descSize))
                    .ToLower(CultureInfo.InvariantCulture);
            }

            position = descStart + descPadded;
        }

        return null;
    }
}
