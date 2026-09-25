using System.Collections.Immutable;

namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// The order the reference writer emits lumps in, and the lump versions it
/// stamps on them.
/// </summary>
/// <remarks>
/// <para>
/// This sequence matches the reference writer's emission order, and it is
/// load-bearing rather
/// than cosmetic: the gate on this phase is that loading a stock-compiled BSP
/// and writing it back out produces the same bytes. That can only hold if the
/// lumps come out in the same sequence, because each lump's recorded file
/// offset is simply where the writer had got to.
/// </para>
/// <para>
/// The sequence is NOT lump-index order, and it is not sorted by anything. It
/// is the order the reference writer happens to perform its writes in, which
/// is why it is written out longhand here instead of being derived from a rule
/// -- there is no rule to derive it from.
/// </para>
/// <para>
/// Three lumps are written only when they carry data, and stock decides that
/// from a count rather than a pointer in one case and a pointer in the other
/// two. All three come to the same thing: an empty lump is skipped entirely and
/// keeps the zeroed header entry it was memset to, which is different from an
/// empty lump that IS written and so records the offset the writer had reached.
/// That distinction is exactly the kind of thing a byte comparison catches and
/// an eyeball does not.
/// </para>
/// </remarks>
public static class BspWriteOrder
{
    /// <summary>
    /// One step of the write sequence: which lump, at which version, and
    /// whether stock skips it when it is empty.
    /// </summary>
    /// <param name="Lump">The lump to write.</param>
    /// <param name="Version">The version to stamp in the header entry.</param>
    /// <param name="SkipWhenEmpty">
    /// When true, an empty lump is not written at all and its header entry
    /// stays all zeroes. When false, an empty lump is still "written": it
    /// records the current file offset and a length of zero.
    /// </param>
    public readonly record struct Step(BspLump Lump, int Version, bool SkipWhenEmpty);

    // The lump versions the reference format stamps. From the reference
    // layout's second enum: every other lump is written at version 0.
    private const int LightingVersion = 1;
    private const int FacesVersion = 1;
    private const int OcclusionVersion = 2;
    private const int LeafsVersion = 1;
    private const int LeafAmbientVersion = 1;

    /// <summary>
    /// The lumps the reference writer emits by hand, in order.
    /// </summary>
    /// <remarks>
    /// <see cref="BspLump.GameLump"/> and <see cref="BspLump.PakFile"/> are
    /// deliberately absent: stock writes them through
    /// <c>AddGameLumps</c> and <c>WritePakFileLump</c>, which have framing and
    /// alignment of their own, so the writer handles them as their own steps
    /// after this list. <see cref="BspLump.Occlusion"/> IS in the list but is
    /// likewise special-cased by the writer, for the reason given on
    /// <see cref="OcclusionPadsToFour"/>.
    /// </remarks>
    /// <remarks>
    /// An <see cref="ImmutableArray{T}"/> rather than a
    /// <see cref="ReadOnlySpan{T}"/>: the writer walks this list across
    /// <c>await</c> points, and a span cannot survive one.
    /// </remarks>
    public static ImmutableArray<Step> Steps { get; } =
    [
        new(BspLump.Planes, 0, false),
        new(BspLump.Leafs, LeafsVersion, false),
        new(BspLump.LeafAmbientLighting, LeafAmbientVersion, false),
        new(BspLump.LeafAmbientIndex, 0, false),
        new(BspLump.LeafAmbientIndexHdr, 0, false),
        new(BspLump.LeafAmbientLightingHdr, LeafAmbientVersion, false),
        new(BspLump.Vertexes, 0, false),
        new(BspLump.Nodes, 0, false),
        new(BspLump.TexInfo, 0, false),
        new(BspLump.TexData, 0, false),
        new(BspLump.DispInfo, 0, false),
        new(BspLump.DispVerts, 0, false),
        new(BspLump.DispTris, 0, false),
        new(BspLump.DispLightmapSamplePositions, 0, false),
        new(BspLump.FaceMacroTextureInfo, 0, false),
        new(BspLump.Primitives, 0, false),
        new(BspLump.PrimVerts, 0, false),
        new(BspLump.PrimIndices, 0, false),
        new(BspLump.Faces, FacesVersion, false),

        // Written only when the HDR face count is non-zero. A map whose HDR
        // faces match its LDR faces has none of these, and the slot stays
        // zeroed.
        new(BspLump.FacesHdr, FacesVersion, true),

        new(BspLump.FaceIds, 0, false),
        new(BspLump.OriginalFaces, 0, false),
        new(BspLump.Brushes, 0, false),
        new(BspLump.BrushSides, 0, false),
        new(BspLump.LeafFaces, 0, false),
        new(BspLump.LeafBrushes, 0, false),
        new(BspLump.SurfEdges, 0, false),
        new(BspLump.Edges, 0, false),
        new(BspLump.Models, 0, false),
        new(BspLump.Areas, 0, false),
        new(BspLump.AreaPortals, 0, false),
        new(BspLump.Lighting, LightingVersion, false),
        new(BspLump.LightingHdr, LightingVersion, false),
        new(BspLump.Visibility, 0, false),
        new(BspLump.Entities, 0, false),
        new(BspLump.WorldLights, 0, false),
        new(BspLump.WorldLightsHdr, 0, false),
        new(BspLump.LeafWaterData, 0, false),

        // Written unconditionally, at version 2, and it is
        // the one lump whose payload stock assembles from three separate arrays
        // with their counts interleaved -- which is why this port keeps the
        // lump's bytes whole rather than modelling the three arrays here.
        new(BspLump.Occlusion, OcclusionVersion, false),

        new(BspLump.MapFlags, 0, false),

        // LUMP_PORTALS, LUMP_CLUSTERS, LUMP_PORTALVERTS and LUMP_CLUSTERPORTALS
        // sit between MapFlags and ClipPortalVerts in the reference writer's
        // sequence, disabled -- vis debugging visualisation, never in a released
        // map. Named here so the next reader does not think they were missed.

        new(BspLump.ClipPortalVerts, 0, false),
        new(BspLump.Cubemaps, 0, false),
        new(BspLump.TexDataStringData, 0, false),
        new(BspLump.TexDataStringTable, 0, false),
        new(BspLump.Overlays, 0, false),
        new(BspLump.WaterOverlays, 0, false),
        new(BspLump.OverlayFades, 0, false),

        // Written only when the physics collide / displaced-collision data is
        // present.
        new(BspLump.PhysCollide, 0, true),
        new(BspLump.PhysDisp, 0, true),

        new(BspLump.VertNormals, 0, false),
        new(BspLump.VertNormalIndices, 0, false),
        new(BspLump.LeafMinDistToWater, 0, false),

        // The game lumps and the pakfile lump follow, then the generic lump
        // pass flushes anything loaded that nothing above claimed. The writer
        // does those three; they are not steps because none of them is a plain
        // "write these bytes and pad to four".
    ];

    /// <summary>
    /// Whether <paramref name="lump"/> is one the ordered sequence writes.
    /// </summary>
    /// <param name="lump">The lump to look for.</param>
    /// <returns>
    /// True when <see cref="Steps"/> contains it. False for
    /// <see cref="BspLump.GameLump"/>, <see cref="BspLump.PakFile"/>, the
    /// unused and deprecated slots, and the three slots with no name -- all of
    /// which stock leaves to <c>Lumps_Write</c> or handles separately.
    /// </returns>
    public static bool IsOrdered(BspLump lump)
    {
        foreach (Step step in Steps)
        {
            if (step.Lump == lump)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// False, recording that stock does NOT pad after the occlusion lump.
    /// </summary>
    /// <remarks>
    /// Every other lump is followed by <c>AlignFilePosition(hFile, 4)</c>.
    /// The reference occlusion pass writes its three counts
    /// and three arrays and then simply returns. It gets away with it because
    /// everything it writes is a multiple of four bytes wide, so the position
    /// is already aligned -- but "already aligned" and "aligned by the writer"
    /// are the same bytes only by luck, and a port that pads here would be
    /// right about the intent and wrong about the file.
    /// </remarks>
    public const bool OcclusionPadsToFour = false;
}
