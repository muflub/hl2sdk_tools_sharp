//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Zip;

namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// A map's checksum as the engine computes it when it loads a map: the
/// CRC-32 of every lump's bytes, in lump order, with the entity lump left out.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> A sidecar made for one compile of a map, such as
/// the level map overlay (<c>.map2d</c>), records this number, and the game
/// refuses the sidecar when the map it loaded has another. The engine
/// already has the number for every map it loads (it is what a server and
/// its clients compare to agree they run the same map), so the check costs
/// the game nothing, and a mod that computes it itself needs only the file.
/// </para>
/// <para>
/// <b>Why the entity lump is left out.</b> That is the engine's rule, and a
/// useful one here: an edit that touches only entities (a key changed with
/// an entity editor) keeps the geometry the sidecar describes, and keeps
/// the checksum with it. Every other lump counts, the pakfile and the game
/// lump included, in the order of the header's directory, each as the bytes
/// its directory entry names (a lump the directory records as empty adds
/// nothing).
/// </para>
/// <para>
/// The CRC-32 is <see cref="Crc32"/>'s: the reflected polynomial
/// <c>0xEDB88320</c> with an initial value and final XOR of <c>0xFFFFFFFF</c>.
/// The directory is read in the standard layout (offset, length, version,
/// four-character code per entry); the Left 4 Dead 2 layout, which swaps the
/// first two fields, is not a layout this port writes.
/// </para>
/// </remarks>
public static class BspMapChecksum
{
    /// <summary>The index of the entity lump, the one lump the checksum leaves out.</summary>
    public const int EntityLump = 0;

    // "VBSP", version, then 64 entries of 16 bytes, then the map revision.
    private const int DirectoryStart = 8;
    private const int EntryBytes = 16;

    /// <summary>The checksum of a map file.</summary>
    /// <param name="file">The whole <c>.bsp</c>, as it is on disk.</param>
    /// <returns>The checksum.</returns>
    /// <exception cref="InvalidBspException">
    /// The bytes are not a BSP (no <c>VBSP</c> magic, a header cut short), or a
    /// lump's directory entry points outside the file.
    /// </exception>
    public static uint Compute(ReadOnlySpan<byte> file)
    {
        if (file.Length < DirectoryStart + (BspData.HeaderLumps * EntryBytes) + 4 || !file[..4].SequenceEqual("VBSP"u8))
        {
            throw new InvalidBspException("not a BSP: the header is missing or cut short");
        }

        uint crc = 0;
        for (int lump = 0; lump < BspData.HeaderLumps; lump++)
        {
            if (lump == EntityLump)
            {
                continue;
            }

            ReadOnlySpan<byte> entry = file.Slice(DirectoryStart + (lump * EntryBytes), EntryBytes);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(entry);
            int length = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            if (length == 0)
            {
                continue;
            }

            if (offset < 0 || length < 0 || (long)offset + length > file.Length)
            {
                throw new InvalidBspException($"lump {lump} runs outside the file (offset {offset}, length {length})");
            }

            crc = Crc32.Append(crc, file.Slice(offset, length));
        }

        return crc;
    }
}
