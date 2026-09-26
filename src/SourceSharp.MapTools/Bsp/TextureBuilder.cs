//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// <c>TexinfoForBrushTexture</c>: turns a
/// brush side's texture placement into a TEXINFO entry and returns its index.
/// </summary>
/// <remarks>
/// <para>
/// This is the function the TEXINFO and TEXDATA lumps are made of. Every number
/// it writes is compared against stock byte for byte, so the arithmetic below
/// is the reference build's and not an equivalent rearrangement: the divisions are per
/// component in the order written, the offset column is a dot product over the
/// first THREE components of a four-component row, and the two shift scales are
/// separate values because the legacy path leaves them at their initial value
/// while the v220 path overwrites both.
/// </para>
/// <para>
/// <b>The pre-220 path is not ported, and neither is the plane parameter it
/// needed.</b> <c>g_nMapFileVersion</c> is assigned exactly once in the whole
/// compiler —, unconditionally <c>400</c>, with the comment
/// "Dummy this up for the texture handling. This can be removed when old .MAP
/// file support is removed" — and does the same for
/// The vrad entry point. So are
/// unreachable, and with them <c>TextureAxisFromPlane</c>
/// The <c>rotate</c> key, and the hard-wired
/// lightmap scale of 16. <see cref="MapFileVersion"/> pins the constant so a
/// fact can assert it rather than this comment being the argument.
/// </para>
/// </remarks>
public static class TextureBuilder
{
    /// <summary>
    /// The one value <c>g_nMapFileVersion</c> ever holds:.
    /// </summary>
    public const int MapFileVersion = 400;

    /// <summary>
    /// The shift scale the legacy path would have used:
    /// <c>1.0f / 16.0f</c>.
    /// </summary>
    /// <remarks>
    /// Never reaches the lump — the v220 branch assigns over it — and named
    /// only so a reader can see that the initial value is not the one used.
    /// </remarks>
    public const float LegacyShiftScale = 1.0f / 16.0f;

    /// <summary>
    /// Builds a side's TEXINFO entry and returns its index.
    /// </summary>
    /// <param name="brushTexture">The side's texture placement.</param>
    /// <param name="origin">
    /// The entity origin to bake into the shifts, or <see cref="Vec3.Zero"/>.
    /// </param>
    /// <param name="texInfos">The texinfo table to dedup into.</param>
    /// <param name="texDatas">The texdata table to name into.</param>
    /// <param name="materials">Where material facts come from.</param>
    /// <param name="diagnostics">Where a missing material is reported, or null.</param>
    /// <param name="cancellationToken">Cancels the material read.</param>
    /// <returns>
    /// The texinfo index, and the placement with stock's zero-scale fix-up
    /// applied.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="texInfos"/>, <paramref name="texDatas"/> or
    /// <paramref name="materials"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A side whose material name is empty returns texinfo 0 without touching
    /// either table — index 0, not -1, so it aliases
    /// whatever the first texinfo turned out to be.
    /// </para>
    /// <para>
    /// The fixed-up placement comes back because stock writes it through the
    /// pointer it was given and the caller then
    /// stores that same struct into <c>side_brushtextures</c>
    ///So the replaced zero is what an
    /// origin brush later rebuilds the texinfo from. An <c>async</c> method
    /// cannot take a <c>ref</c>, hence a return value; a caller that discards
    /// it is choosing stock's <c>MergeBrushSides</c> behaviour, where the
    /// placement was a local copy anyway.
    /// </para>
    /// <para>
    /// <c>lightmapWorldUnitsPerLuxel</c> is divided by with NO zero guard, as
    /// Has none. A side with no
    /// <c>lightmapscale</c> key therefore produces infinities in the lightmap
    /// rows, and because <see cref="TexInfoTable.Find"/> compares bit patterns
    /// those entries still dedup with each other. Stock does that on a map
    /// Hammer did not write, so it is reproduced rather than papered over.
    /// </para>
    /// </remarks>
    public static async ValueTask<(int TexInfo, BrushTexture Fixed)> TexinfoForBrushTextureAsync(
        BrushTexture brushTexture,
        Vec3 origin,
        TexInfoTable texInfos,
        TexDataTable texDatas,
        MaterialFactsCache materials,
        ICollection<CompileDiagnostic>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(texDatas);
        ArgumentNullException.ThrowIfNull(materials);

        BrushTexture bt = brushTexture;

        // -- an empty name is texinfo 0, and neither table is
        // touched. bt comes back unchanged, including any zero scale.
        if (string.IsNullOrEmpty(bt.Name))
        {
            return (0, bt);
        }

        // An EXACT zero test, no epsilon.
        if (bt.TextureWorldUnitsPerTexelU == 0f)
        {
            bt.TextureWorldUnitsPerTexelU = 1f;
        }

        if (bt.TextureWorldUnitsPerTexelV == 0f)
        {
            bt.TextureWorldUnitsPerTexelV = 1f;
        }

        TexInfo tx = default;

        // The v220+ path. Per component, in this order.
        for (int k = 0; k < 3; k++)
        {
            tx.TextureVecsTexelsPerWorldUnits[k] = bt.UAxis[k] / bt.TextureWorldUnitsPerTexelU;
            tx.TextureVecsTexelsPerWorldUnits[4 + k] = bt.VAxis[k] / bt.TextureWorldUnitsPerTexelV;
            tx.LightmapVecsLuxelsPerWorldUnits[k] = bt.UAxis[k] / bt.LightmapWorldUnitsPerLuxel;
            tx.LightmapVecsLuxelsPerWorldUnits[4 + k] = bt.VAxis[k] / bt.LightmapWorldUnitsPerLuxel;
        }

        float shiftScaleU = bt.TextureWorldUnitsPerTexelU / bt.LightmapWorldUnitsPerLuxel;
        float shiftScaleV = bt.TextureWorldUnitsPerTexelV / bt.LightmapWorldUnitsPerLuxel;

        // DOT_PRODUCT reads components
        // 0..2 of the row, so the column being written is not one of its inputs.
        tx.TextureVecsTexelsPerWorldUnits[3] =
            bt.ShiftU + Dot3(tx.TextureVecsTexelsPerWorldUnits, 0, origin);
        tx.TextureVecsTexelsPerWorldUnits[7] =
            bt.ShiftV + Dot3(tx.TextureVecsTexelsPerWorldUnits, 4, origin);
        tx.LightmapVecsLuxelsPerWorldUnits[3] =
            (shiftScaleU * bt.ShiftU) + Dot3(tx.LightmapVecsLuxelsPerWorldUnits, 0, origin);
        tx.LightmapVecsLuxelsPerWorldUnits[7] =
            (shiftScaleV * bt.ShiftV) + Dot3(tx.LightmapVecsLuxelsPerWorldUnits, 4, origin);

        tx.Flags = bt.Flags;
        tx.TexData = await texDatas
            .FindOrCreateAsync(bt.Name, materials, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        return (texInfos.FindOrCreate(tx), bt);
    }

    private static float Dot3(FloatArray8 row, int offset, Vec3 v) =>
        (row[offset] * v.X) + (row[offset + 1] * v.Y) + (row[offset + 2] * v.Z);
}
