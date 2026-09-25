using System.Buffers.Binary;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Validation;

/// <content>
/// The rules that are about what a lump SAYS rather than where it points: the
/// solid leaf, lightmap extents, cubemaps, the physics lump's framing and the
/// displacements.
/// </content>
public static partial class BspValidator
{
    private static void CheckContent(
        BspData bsp,
        Counts counts,
        Findings findings,
        CancellationToken cancellationToken)
    {
        CheckSolidLeaf(bsp, findings);
        CheckSurfaceExtents(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        // -- with -requirecubemaps this is
        // Sys_Error( "Map \"%s\" does not have cubemaps!" ); without it every
        // reflective surface in the map silently falls back to
        // engine/defaultcubemap.
        if (counts.Cubemaps == 0)
        {
            findings.Add(
                BspRuleCodes.NoCubemaps,
                "the map has no cubemap samples; the engine falls back to a default cubemap for "
                + "every reflective surface, and stops outright under -requirecubemaps");
        }

        CheckPhysics(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckDisplacements(bsp, counts, findings);
        cancellationToken.ThrowIfCancellationRequested();

        CheckSubLumps(bsp, findings);
    }

    /// <summary>
    /// <see cref="BspRuleCodes.Leaf0Solid"/>.
    /// </summary>
    /// <remarks>
    /// -- the same
    /// <c>Sys_Error( "Map leaf 0 is not CONTENTS_SOLID")</c> at the end of both
    /// leaf loaders, because the collision code uses leaf 0 AS the solid leaf
    /// (<c>pBSPData-&gt;solidleaf = 0</c> on the next line). <c>contents</c> is
    /// the first field of both <c>dleaf_t</c> and <c>dleaf_version_0_t</c>, so
    /// it reads the same at either stride.
    /// </remarks>
    private static void CheckSolidLeaf(BspData bsp, Findings findings)
    {
        BspLumpData leafs = bsp[BspLump.Leafs];
        if (leafs.Length < sizeof(int) || leafs.Version is not (0 or 1))
        {
            return;
        }

        int contents = BinaryPrimitives.ReadInt32LittleEndian(leafs.Data.Span);
        if (contents != BspLimits.ContentsSolid)
        {
            findings.Add(
                BspRuleCodes.Leaf0Solid,
                $"leaf 0's contents are 0x{contents:X8}, not CONTENTS_SOLID "
                + $"(0x{BspLimits.ContentsSolid:X8}); the collision loader uses leaf 0 as the "
                + "solid leaf and stops");
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.SurfaceExtents"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>if (!(tex-&gt;flags &amp;
    /// SURF_NOLIGHT) &amp;&amp; MSurf_LightmapExtents(...)[i] &gt;
    /// MSurf_MaxLightmapSizeWithBorder( surfID ) ) Sys_Error ("Bad surface
    /// extents on texture %s")</c>. The limit is picked per face by
    /// 128 for a displacement,
    /// 35 for anything else.
    /// </para>
    /// <para>
    /// The engine compares the extents it RECOMPUTES from the face's vertices
    /// against that limit, not the ones the file stores -- but it stores what it
    /// computed, so for a map written by a working compiler
    /// the two are the same number and the stored one is checkable without a
    /// vertex walk. A face whose stored extents and real extents disagree is a
    /// separate defect, and one the diff instrument is the right tool for.
    /// </para>
    /// </remarks>
    private static void CheckSurfaceExtents(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<TexInfo> texInfo = View<TexInfo>(bsp, BspLump.TexInfo);

        int bad = 0;
        string first = string.Empty;

        foreach ((BspLump lump, int count) in
            new[] { (BspLump.Faces, counts.Faces), (BspLump.FacesHdr, counts.FacesHdr) })
        {
            if (count == 0)
            {
                continue;
            }

            ReadOnlySpan<DFace> faces = View<DFace>(bsp, lump);
            for (int i = 0; i < faces.Length; i++)
            {
                DFace face = faces[i];
                if (face.TexInfo < 0 || face.TexInfo >= texInfo.Length)
                {
                    // Already reported as BSP0014; there is no flags field to
                    // read, so there is nothing to say about its extents.
                    continue;
                }

                if ((texInfo[face.TexInfo].Flags & BspLimits.SurfNoLight) != 0)
                {
                    continue;
                }

                int limit = face.DispInfo != -1
                    ? BspLimits.MaxDispLightmapDim
                    : BspLimits.MaxBrushLightmapDim;

                for (int axis = 0; axis < 2; axis++)
                {
                    int extent = face.LightmapTextureSizeInLuxels[axis];
                    if (extent > limit)
                    {
                        bad++;
                        if (bad == 1)
                        {
                            first =
                                $"{lump} face {i} has a lightmap extent of {extent} luxels on "
                                + $"axis {axis}, above the {limit} the engine allows for "
                                + (face.DispInfo != -1 ? "a displacement" : "a brush face");
                        }
                    }
                }
            }
        }

        if (bad > 0)
        {
            findings.AddRepeated(BspRuleCodes.SurfaceExtents, first, bad);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.PhysFraming"/> and
    /// <see cref="BspRuleCodes.PhysModelIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks LUMP_PHYSCOLLIDE as a flat
    /// run of <c>dphysmodel_t</c> headers, each followed by <c>dataSize</c>
    /// bytes of solids and <c>keydataSize</c> bytes of text, ending at a record
    /// whose <c>dataSize</c> is not positive -- the comment
    /// says that terminator is <c>modelIndex -1, dataSize -1</c>. Its only
    /// defence against a corrupt lump is <c>if ( (int)(ptr - basePtr) &gt;
    /// lh.LumpSize() ) break;</c>, which stops the walk after it has already
    /// read past the end.
    /// </para>
    /// <para>
    /// Inside <c>dataSize</c> each solid is <c>int size</c> then that many
    /// Bytes. The four-byte
    /// padding that code adds belongs to the X360 swap pass and is folded into
    /// <c>dataSize</c> there; a PC map frames tight, which is what
    /// <c>dm_lockdown.bsp</c> does, so this rule demands the solids account for
    /// <c>dataSize</c> exactly and allows no slack.
    /// </para>
    /// <para>
    /// Then subscripts <c>map_cmodels[ physModel.modelIndex ]</c>
    /// with nothing bounding it.
    /// </para>
    /// </remarks>
    private static void CheckPhysics(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<byte> lump = bsp[BspLump.PhysCollide].Data.Span;
        if (lump.IsEmpty)
        {
            return;
        }

        const int HeaderSize = 16;
        int offset = 0;
        int record = 0;
        int badModel = 0;
        string firstModel = string.Empty;

        while (true)
        {
            if (offset + HeaderSize > lump.Length)
            {
                findings.Add(
                    BspRuleCodes.PhysFraming,
                    $"the PhysCollide lump runs out after {offset} of {lump.Length} bytes with "
                    + "no terminating record; the engine's walk reads past the lump");
                return;
            }

            int modelIndex = BinaryPrimitives.ReadInt32LittleEndian(lump[offset..]);
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(lump[(offset + 4)..]);
            int keydataSize = BinaryPrimitives.ReadInt32LittleEndian(lump[(offset + 8)..]);
            int solidCount = BinaryPrimitives.ReadInt32LittleEndian(lump[(offset + 12)..]);
            offset += HeaderSize;

            if (dataSize <= 0)
            {
                if (offset != lump.Length)
                {
                    findings.Add(
                        BspRuleCodes.PhysFraming,
                        $"the PhysCollide lump's terminator is at byte {offset - HeaderSize} but "
                        + $"the lump is {lump.Length} bytes, leaving {lump.Length - offset} "
                        + "unaccounted for");
                }

                break;
            }

            if (modelIndex < 0 || modelIndex >= counts.Models)
            {
                badModel++;
                if (badModel == 1)
                {
                    firstModel =
                        $"physics record {record} is for model {modelIndex}, outside the "
                        + $"{counts.Models}-entry Models lump";
                }
            }

            if (keydataSize < 0
                || solidCount < 0
                || (long)offset + dataSize + keydataSize > lump.Length)
            {
                findings.Add(
                    BspRuleCodes.PhysFraming,
                    $"physics record {record} declares {dataSize} bytes of solids and "
                    + $"{keydataSize} of key data from byte {offset}, past the end of the "
                    + $"{lump.Length}-byte lump");
                return;
            }

            if (!SolidsFrameExactly(lump.Slice(offset, dataSize), solidCount, out string reason))
            {
                findings.Add(
                    BspRuleCodes.PhysFraming,
                    $"physics record {record}'s solids do not frame: {reason}");
                return;
            }

            offset += dataSize + keydataSize;
            record++;
        }

        if (badModel > 0)
        {
            findings.AddRepeated(BspRuleCodes.PhysModelIndex, firstModel, badModel);
        }
    }

    /// <summary>
    /// Whether <paramref name="solidCount"/> length-prefixed solids account for
    /// exactly <paramref name="data"/>.
    /// </summary>
    private static bool SolidsFrameExactly(
        ReadOnlySpan<byte> data,
        int solidCount,
        out string reason)
    {
        int offset = 0;
        for (int i = 0; i < solidCount; i++)
        {
            if (offset + sizeof(int) > data.Length)
            {
                reason = $"solid {i}'s size field starts at byte {offset} of {data.Length}";
                return false;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            if (size < 0 || (long)offset + sizeof(int) + size > data.Length)
            {
                reason = $"solid {i} declares {size} bytes at byte {offset} of {data.Length}";
                return false;
            }

            offset += sizeof(int) + size;
        }

        if (offset != data.Length)
        {
            reason = $"{solidCount} solids account for {offset} of {data.Length} bytes";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// <see cref="BspRuleCodes.DispPower"/> and
    /// <see cref="BspRuleCodes.DispRuns"/>.
    /// </summary>
    /// <remarks>
    /// Reads
    /// <c>NUM_DISP_POWER_VERTS( dispInfo.power )</c> vertices into a
    /// <c>CDispVert tempVerts[MAX_DISPVERTS]</c> declared on the stack, where
    /// <c>MAX_DISPVERTS</c> is fixed at <c>MAX_MAP_DISP_POWER</c> of 4
    /// A power above 4 overruns that
    /// buffer, and the running <c>iCurVert</c> / <c>iCurTri</c> offsets read
    /// past the vertex and triangle lumps.
    /// </remarks>
    private static void CheckDisplacements(BspData bsp, Counts counts, Findings findings)
    {
        ReadOnlySpan<DispInfo> disps = View<DispInfo>(bsp, BspLump.DispInfo);
        if (disps.IsEmpty)
        {
            return;
        }

        int badPower = 0;
        string firstPower = string.Empty;
        int badRun = 0;
        string firstRun = string.Empty;

        for (int i = 0; i < disps.Length; i++)
        {
            DispInfo disp = disps[i];

            if (disp.Power is < 2 or > BspLimits.MaxDispPower)
            {
                badPower++;
                if (badPower == 1)
                {
                    firstPower =
                        $"displacement {i} has power {disp.Power}; vbsp writes 2..4 and the "
                        + $"engine's fixed buffers are sized for MAX_MAP_DISP_POWER "
                        + $"({BspLimits.MaxDispPower})";
                }

                continue;
            }

            if (disp.DispVertStart < 0
                || (long)disp.DispVertStart + disp.NumVerts() > counts.DispVerts)
            {
                badRun++;
                if (badRun == 1)
                {
                    firstRun =
                        $"displacement {i}'s vertex run is {disp.DispVertStart}.."
                        + $"{(long)disp.DispVertStart + disp.NumVerts()}, past the "
                        + $"{counts.DispVerts}-entry DispVerts lump";
                }
            }

            if (disp.DispTriStart < 0
                || (long)disp.DispTriStart + disp.NumTris() > counts.DispTris)
            {
                badRun++;
                if (badRun == 1)
                {
                    firstRun =
                        $"displacement {i}'s triangle run is {disp.DispTriStart}.."
                        + $"{(long)disp.DispTriStart + disp.NumTris()}, past the "
                        + $"{counts.DispTris}-entry DispTris lump";
                }
            }
        }

        if (badPower > 0)
        {
            findings.AddRepeated(BspRuleCodes.DispPower, firstPower, badPower);
        }

        if (badRun > 0)
        {
            findings.AddRepeated(BspRuleCodes.DispRuns, firstRun, badRun);
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.SubLumpFraming"/>,
    /// <see cref="BspRuleCodes.StaticPropDictIndex"/> and
    /// <see cref="BspRuleCodes.StaticPropLeafRun"/>.
    /// </summary>
    /// <remarks>
    /// The lumps that are a header plus counted runs rather than an array --
    /// visibility, occlusion, <c>sprp</c>, <c>dprp</c> -- cannot be checked by a
    /// modulus. The engine reads them through a <c>CUtlBuffer</c> whose
    /// <c>Get</c> past the end is an overflow with no message
    /// So "the counts frame inside
    /// the bytes" is the whole rule. Each reader in this tree raises
    /// <see cref="InvalidBspException"/> for exactly that, which is what is
    /// caught here and turned into a finding.
    /// </remarks>
    private static void CheckSubLumps(BspData bsp, Findings findings)
    {
        BspLumpData visibility = bsp[BspLump.Visibility];
        if (!visibility.IsEmpty)
        {
            try
            {
                _ = VisibilityLump.Read(visibility);
            }
            catch (InvalidBspException ex)
            {
                findings.Add(BspRuleCodes.SubLumpFraming, $"the Visibility lump: {ex.Message}");
            }
        }

        BspLumpData occlusion = bsp[BspLump.Occlusion];
        if (!occlusion.IsEmpty && occlusion.Version is 0 or 1 or 2)
        {
            try
            {
                _ = OcclusionLump.Read(occlusion);
            }
            catch (InvalidBspException ex)
            {
                findings.Add(BspRuleCodes.SubLumpFraming, $"the Occlusion lump: {ex.Message}");
            }
        }

        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            string code = entry.IdString();

            if (code == GameLumpId.StaticProps
                && entry.Version >= BspLimits.MinStaticPropVersion
                && StaticPropLump.SupportedVersions.Contains(entry.Version))
            {
                CheckStaticProps(entry, findings);
            }

            if (code == GameLumpId.DetailProps
                && entry.Version == GameLumpVersions.DetailProps)
            {
                try
                {
                    _ = DetailPropLump.Read(entry);
                }
                catch (InvalidBspException ex)
                {
                    findings.Add(
                        BspRuleCodes.SubLumpFraming,
                        $"the dprp game lump: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// <see cref="BspRuleCodes.StaticPropDictIndex"/> and
    /// <see cref="BspRuleCodes.StaticPropLeafRun"/>.
    /// </summary>
    /// <remarks>
    /// Subscripts
    /// <c>m_StaticPropDict[ lump.m_PropType ]</c> straight from the file, and
    /// Walks <c>m_StaticPropLeaves</c>
    /// <c>prop.FirstLeaf()</c> for <c>prop.LeafCount()</c> entries. Neither is
    /// bounded anywhere in that file.
    /// </remarks>
    private static void CheckStaticProps(GameLumpEntry entry, Findings findings)
    {
        StaticPropLump props;
        try
        {
            props = StaticPropLump.Read(entry);
        }
        catch (InvalidBspException ex)
        {
            findings.Add(BspRuleCodes.SubLumpFraming, $"the sprp game lump: {ex.Message}");
            return;
        }

        int badType = 0;
        string firstType = string.Empty;
        int badLeaf = 0;
        string firstLeaf = string.Empty;

        for (int i = 0; i < props.Props.Count; i++)
        {
            StaticProp prop = props.Props[i];

            if (prop.PropType >= props.ModelNames.Count)
            {
                badType++;
                if (badType == 1)
                {
                    firstType =
                        $"static prop {i} has type {prop.PropType}, outside the "
                        + $"{props.ModelNames.Count}-entry model dictionary";
                }
            }

            if (prop.FirstLeaf + prop.LeafCount > props.LeafEntries.Count)
            {
                badLeaf++;
                if (badLeaf == 1)
                {
                    firstLeaf =
                        $"static prop {i}'s leaf run is {prop.FirstLeaf}.."
                        + $"{prop.FirstLeaf + prop.LeafCount}, past the "
                        + $"{props.LeafEntries.Count}-entry static prop leaf list";
                }
            }
        }

        if (badType > 0)
        {
            findings.AddRepeated(BspRuleCodes.StaticPropDictIndex, firstType, badType);
        }

        if (badLeaf > 0)
        {
            findings.AddRepeated(BspRuleCodes.StaticPropLeafRun, firstLeaf, badLeaf);
        }
    }
}
