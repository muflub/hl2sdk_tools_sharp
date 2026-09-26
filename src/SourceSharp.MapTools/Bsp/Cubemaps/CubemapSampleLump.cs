//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Bsp.Cubemaps;

/// <summary>
/// LUMP_CUBEMAPS: <c>g_CubemapSamples</c> as <c>WriteBSPFile</c> writes it,
/// one <c>dcubemapsample_t</c> per <c>env_cubemap</c> in entity order.
/// </summary>
public static class CubemapSampleLump
{
    /// <summary>
    /// The samples, filled as <c>Cubemap_InsertSample</c> fills them
    /// </summary>
    /// <param name="samples">The context's samples, in entity order.</param>
    /// <returns>The lump's records.</returns>
    /// <remarks>
    /// The origin is TRUNCATED to integers by a C cast; the size is the
    /// <c>cubemapsize</c> key stored into an <c>unsigned char</c>, so it wraps
    /// modulo 256. The three padding bytes are zero, because
    /// <c>g_CubemapSamples</c> is a zero-initialised global.
    /// </remarks>
    public static DCubemapSample[] Build(IReadOnlyList<CubemapSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        DCubemapSample[] records = new DCubemapSample[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            (int x, int y, int z) = CubemapFixups.SampleOrigin(samples[i].Origin);
            records[i].Origin[0] = x;
            records[i].Origin[1] = y;
            records[i].Origin[2] = z;
            records[i].Size = unchecked((byte)samples[i].Size);
        }

        return records;
    }

    /// <summary>The lump's bytes.</summary>
    /// <param name="samples">The context's samples.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ToBytes(IReadOnlyList<CubemapSample> samples) =>
        MemoryMarshal.AsBytes(Build(samples).AsSpan()).ToArray();
}
