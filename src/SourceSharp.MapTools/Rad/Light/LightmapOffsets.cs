//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The lightmap layout <c>PrecompLightmapOffsets</c> decides: each face's
/// styles and byte offset into <c>LUMP_LIGHTING</c>, and the lump's size.
/// </summary>
/// <param name="Styles">
/// <c>dface_t::styles</c>, four per face, face-major -- the bytes the FACES lump
/// carries.
/// </param>
/// <param name="LightOffsets"><c>dface_t::lightofs</c>, -1 for an unlit face.</param>
/// <param name="LightDataSize">The size <c>pdlightdata</c> is set to.</param>
public sealed record LightmapLayout(byte[] Styles, int[] LightOffsets, int LightDataSize);

/// <summary>
/// <c>PrecompLightmapOffsets</c>.
/// </summary>
/// <remarks>
/// <para>
/// Faces are laid out in face order. Each lit face reserves FOUR bytes per style
/// first -- the average colour of each style, stored "in reverse order of the
/// way the lightstyles appear" (stock's comment) and written later by 4f --
/// and then its luxels: <c>4 * styles * luxels</c> bytes, times four more on a
/// bumped face. <c>lightofs</c> points PAST the averages, at the first luxel.
/// </para>
/// <para>
/// A face whose texinfo is <c>TEX_SPECIAL</c> is skipped and keeps whatever
/// <c>lightofs</c> it had; <c>BuildFacelights</c> set every face to -1 and
/// 255 first, so that is -1. Under
/// <c>-dlightmap</c> every non-special face's second style becomes 0 before
/// counting -- including faces that had no style at all, whose first slot is
/// still 255, so they stay unlit.
/// </para>
/// </remarks>
public static class LightmapOffsets
{
    /// <summary>Lays out the lightmaps.</summary>
    /// <param name="geometry">The map.</param>
    /// <param name="faceLights">Each face's facelight, or null for a face that was never lit.</param>
    /// <param name="separateDirectLightmap"><c>dlight_map</c>.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The facelight array is not one per face.</exception>
    public static LightmapLayout Compute(
        LightGeometry geometry,
        IReadOnlyList<FaceLight?> faceLights,
        bool separateDirectLightmap)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(faceLights);

        int faceCount = geometry.Faces.Length;
        if (faceLights.Count != faceCount)
        {
            throw new ArgumentException(
                $"{faceLights.Count} facelights for {faceCount} faces", nameof(faceLights));
        }

        byte[] styles = new byte[faceCount * LightConstants.MaxLightmaps];
        int[] offsets = new int[faceCount];
        const int texSpecial = (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight);

        for (int f = 0; f < faceCount; f++)
        {
            offsets[f] = -1;
            FaceLight? fl = faceLights[f];
            for (int k = 0; k < LightConstants.MaxLightmaps; k++)
            {
                styles[(f * LightConstants.MaxLightmaps) + k] = fl?.Styles[k] ?? 255;
            }
        }

        int lightDataSize = 0;
        for (int f = 0; f < faceCount; f++)
        {
            ref readonly DFace face = ref geometry.Faces[f];
            int flags = geometry.TexInfos[face.TexInfo].Flags;
            if ((flags & texSpecial) != 0)
            {
                continue;
            }

            Span<byte> faceStyles = styles.AsSpan(f * LightConstants.MaxLightmaps, LightConstants.MaxLightmaps);

            if (separateDirectLightmap)
            {
                faceStyles[1] = 0;
            }

            int lightstyles;
            for (lightstyles = 0; lightstyles < LightConstants.MaxLightmaps; lightstyles++)
            {
                if (faceStyles[lightstyles] == 255)
                {
                    break;
                }
            }

            if (lightstyles == 0)
            {
                continue;
            }

            lightDataSize += lightstyles * 4;
            offsets[f] = lightDataSize;

            int luxels = (face.LightmapTextureSizeInLuxels[0] + 1) * (face.LightmapTextureSizeInLuxels[1] + 1);
            bool bumped = (flags & (int)SurfaceFlags.BumpLight) != 0;
            lightDataSize += bumped
                ? luxels * 4 * lightstyles * BumpBasis.LightmapCount
                : luxels * 4 * lightstyles;
        }

        return new LightmapLayout(styles, offsets, lightDataSize);
    }
}
