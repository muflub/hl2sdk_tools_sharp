//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// The three points where the vbsp driver (lane p3e) hands displacement work
/// to this lane, in compile order: <c>DispGetFaceInfo</c> at load
/// The base-face description once the face is emitted
/// And the lump write at the end of
/// <c>EndBSPFile</c>.
/// </summary>
/// <remarks>
/// <para>
/// The driver's sequence, stock's order:
/// </para>
/// <list type="number">
/// <item>load: per <c>dispinfo</c> chunk, <see cref="VmfDisplacementReader.Read"/>;
/// per brush that has one, <see cref="CheckBrush"/>;</item>
/// <item>model loop: emit each displacement's base face (entity 0 only) and
/// remember its LUMP_FACES index;</item>
/// <item>build a <see cref="DisplacementFace"/> per displacement with
/// <see cref="Face"/> and call <see cref="DisplacementLumpBuilder.Build"/>
/// (it needs no face extents); write <see cref="FaceTexInfos"/>' answer
/// onto the base faces; run <c>UpdateAllFaceLightmapExtents</c>; then
/// <see cref="WriteLumps"/>. Under stock the texinfo step is the identity, so
/// the order is stock's in effect;</item>
/// <item>world bounds: <see cref="DisplacementLumpBuilder.ComputeDispInfoBounds"/>;</item>
/// <item>physics (3h) and detail props (3g) read
/// <see cref="DisplacementResult.Core"/>.</item>
/// </list>
/// </remarks>
public static class DispVbspHooks
{
    /// <summary>The error code for a displacement on a brush entity.</summary>
    public const string OnEntityCode = "VBSP0504";

    /// <summary>The error code for a displacement whose side is not a quad.</summary>
    public const string NotQuadCode = "VBSP0505";

    /// <summary>
    /// The checks <c>DispGetFaceInfo</c> makes before a displacement brush is
    /// Accepted:.
    /// </summary>
    /// <param name="entityNumber">The brush's entity; 0 is worldspawn.</param>
    /// <param name="className">That entity's classname, for the message.</param>
    /// <param name="brushNumber">The brush's number, for the message.</param>
    /// <param name="sideWindingPoints">The displacement side's winding point count.</param>
    /// <exception cref="MapCompileException">
    /// A displacement on anything but worldspawn, or on a side that is not a
    /// quad — both fatal <c>Error()</c>s in stock.
    /// </exception>
    public static void CheckBrush(int entityNumber, string className, int brushNumber, int sideWindingPoints)
    {
        if (entityNumber != 0)
        {
            throw new MapCompileException(
                $"{OnEntityCode}: displacement found on a(n) {className} entity - not supported "
                + $"(entity {entityNumber}, brush {brushNumber})");
        }

        if (sideWindingPoints != 4)
        {
            throw new MapCompileException(
                $"{NotQuadCode}: Trying to create a non-quad displacement! "
                + $"(entity {entityNumber}, brush {brushNumber})");
        }
    }

    /// <summary>
    /// The base face the lump builder needs, from what the driver has.
    /// </summary>
    /// <param name="faceIndex">The emitted face's LUMP_FACES index.</param>
    /// <param name="sideWinding">
    /// The SIDE's winding — <c>face.originalface-&gt;winding</c>
    /// — in float, not the snapped emitted
    /// vertices.
    /// </param>
    /// <param name="brushContents">The brush's contents: <c>face.contents</c>.</param>
    /// <param name="texInfo">The side's texinfo, unswapped.</param>
    /// <returns>The face description.</returns>
    /// <exception cref="ArgumentException">The winding is not four points.</exception>
    public static DisplacementFace Face(int faceIndex, IReadOnlyList<Vec3> sideWinding, int brushContents, TexInfo texInfo)
    {
        ArgumentNullException.ThrowIfNull(sideWinding);
        if (sideWinding.Count != 4)
        {
            throw new ArgumentException($"a displacement face has four points, not {sideWinding.Count}.", nameof(sideWinding));
        }

        float[] textureVecs = new float[8];
        for (int k = 0; k < 8; k++)
        {
            textureVecs[k] = texInfo.TextureVecsTexelsPerWorldUnits[k];
        }

        return new DisplacementFace(
            faceIndex,
            [.. sideWinding],
            brushContents,
            new Vec3(texInfo.LightmapVecsLuxelsPerWorldUnits[0], texInfo.LightmapVecsLuxelsPerWorldUnits[1], texInfo.LightmapVecsLuxelsPerWorldUnits[2]),
            new Vec3(texInfo.LightmapVecsLuxelsPerWorldUnits[4], texInfo.LightmapVecsLuxelsPerWorldUnits[5], texInfo.LightmapVecsLuxelsPerWorldUnits[6]),
            textureVecs);
    }

    /// <summary>
    /// The texinfo each displacement's BASE FACE must carry in LUMP_FACES:
    /// The swap bookkeeping of the reference implementation, under the
    /// compile's compliance.
    /// </summary>
    /// <param name="results"><see cref="DisplacementLumpBuilder.Build"/>'s results.</param>
    /// <param name="faceTexInfos">Each displacement face's current texinfo, same order.</param>
    /// <param name="texInfos">The texinfo table; Correct appends the swapped copies.</param>
    /// <param name="compliance">The compile's compliance.</param>
    /// <returns>The texinfo index to write onto each base face.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// Stock (<see cref="StockQuirk.DispLightmapSwapDropped"/> emulated):
    /// every face keeps its texinfo and the table is untouched — stock's copy
    /// reaches only the map face and <c>CompactTexinfos</c> deletes it, so
    /// this is byte-identical to stock's LUMP_FACES and LUMP_TEXINFO.
    /// </para>
    /// <para>
    /// Correct: swapped faces are repointed at their copy
    /// (<see cref="DisplacementLumpBuilder.AssignSwappedTexInfos"/>). The
    /// driver must apply the indices to the <c>dface_t</c>s BEFORE it computes
    /// lightmap extents (<c>UpdateAllFaceLightmapExtents</c>), so the face's
    /// <c>m_LightmapTextureMinsInLuxels</c> are the swapped vectors' — the
    /// sizes are then overwritten by <see cref="WriteLumps"/> as in stock. The
    /// copy is referenced by a face, so <c>CompactTexinfos</c> keeps it.
    /// </para>
    /// </remarks>
    public static int[] FaceTexInfos(
        IReadOnlyList<DisplacementResult> results,
        IReadOnlyList<int> faceTexInfos,
        IList<TexInfo> texInfos,
        ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(faceTexInfos);
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(compliance);

        if (compliance.Emulates(StockQuirk.DispLightmapSwapDropped))
        {
            return [.. faceTexInfos];
        }

        return DisplacementLumpBuilder.AssignSwappedTexInfos(results, faceTexInfos, texInfos);
    }

    /// <summary>
    /// Writes LUMP_DISPINFO, LUMP_DISP_VERTS, LUMP_DISP_TRIS and
    /// LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS, and each base face's
    /// <c>m_LightmapTextureSizeInLuxels</c>.
    /// </summary>
    /// <param name="bsp">The map being written; its LUMP_FACES must already hold the base faces.</param>
    /// <param name="results">What <see cref="DisplacementLumpBuilder.Build"/> returned.</param>
    /// <param name="lumps">The buffers it filled.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A result names a face LUMP_FACES does not have.</exception>
    /// <remarks>
    /// Does NOT touch any face's texinfo: that is <see cref="FaceTexInfos"/>'
    /// answer, which the driver writes. The lump
    /// structs are written whole, padding included, as zeros — stock's padding
    /// is uninitialised heap, which no gate compares.
    /// </remarks>
    public static void WriteLumps(BspData bsp, IReadOnlyList<DisplacementResult> results, DisplacementLumps lumps)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(lumps);

        DispInfo[] infos = new DispInfo[results.Count];
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i] = results[i].Info;
        }

        bsp[BspLump.DispInfo] = BspStructView.ToLump<DispInfo>(infos, 0);
        bsp[BspLump.DispVerts] = BspStructView.ToLump<DispVert>(lumps.Verts.ToArray(), 0);
        bsp[BspLump.DispTris] = BspStructView.ToLump<DispTri>(lumps.Tris.ToArray(), 0);
        bsp.SetLump(BspLump.DispLightmapSamplePositions, lumps.LightmapSamplePositions.ToArray());

        byte[] faceBytes = bsp[BspLump.Faces].Data.ToArray();
        Span<DFace> faces = BspStructView.AsWritable<DFace>(faceBytes);
        foreach (DisplacementResult r in results)
        {
            if (r.Info.MapFace >= faces.Length)
            {
                throw new ArgumentException(
                    $"displacement names face {r.Info.MapFace}, but LUMP_FACES has {faces.Length}.",
                    nameof(results));
            }

            ref DFace face = ref faces[r.Info.MapFace];
            face.LightmapTextureSizeInLuxels[0] = r.LightmapSizeU;
            face.LightmapTextureSizeInLuxels[1] = r.LightmapSizeV;
        }

        bsp.SetLump(BspLump.Faces, faceBytes, bsp[BspLump.Faces].Version);
    }
}
