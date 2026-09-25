using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Write;

/// <summary>
/// Something outside the face and brush lumps that holds texinfo indices and
/// must be counted and renumbered by <c>CompactTexinfos</c>: overlays and water
/// overlays (Phase 3g), world lights (vrad's, none in vbsp).
/// </summary>
internal interface ITexInfoReferences
{
    /// <summary>Adds one to <paramref name="refCounts"/> for every texinfo held.</summary>
    /// <param name="refCounts">One slot per texinfo.</param>
    void CountReferences(Span<int> refCounts);

    /// <summary>Renumbers every texinfo held through <paramref name="outputIndex"/>.</summary>
    /// <param name="outputIndex">The new index of each old one.</param>
    void Remap(ReadOnlySpan<int> outputIndex);
}

/// <summary>
/// <c>CompactTexinfos</c>: drop every texinfo and
/// texdata nothing references, fold all sky texinfos into one (and all 2D sky
/// into another), and rebuild the texdata string table from what is left.
/// </summary>
/// <remarks>
/// <para>
/// <b>A brush side that no face uses is not simply kept.</b> Before counting
/// it, <c>FindMatchingBrushSideTexinfo</c> retargets it to the FIRST
/// face-referenced texinfo with the same flags and the same surface property,
/// whatever its texture, so that collision-only sides do not keep texinfos
/// alive. Only when there is none is the side's own texinfo counted.
/// </para>
/// <para>
/// The sky fold keeps the first sky texinfo's copy and points every later sky
/// at it; the string table is rebuilt by re-adding the surviving names in
/// texdata order, which renumbers them.
/// </para>
/// </remarks>
internal static class TexInfoCompactor
{
    /// <summary>Counts before and after, for stock's two "Reduced" lines.</summary>
    internal readonly record struct Result(
        int TexInfoBefore, int TexInfoAfter, int TexDataBefore, int TexDataAfter,
        int StringBytesBefore, int StringBytesAfter);

    /// <summary>Runs the compaction over the emitted lumps.</summary>
    /// <param name="state">The emitted lumps; faces, brush sides and water data are remapped in place.</param>
    /// <param name="texInfos">The compile's texinfo table.</param>
    /// <param name="texDatas">The compile's texdata table.</param>
    /// <param name="surfaceProperties"><c>g_SurfaceProperties</c>, one per texdata (<see cref="TexDataTable.SurfaceProperties"/>).</param>
    /// <param name="others">Other holders of texinfo indices, counted after the brush sides.</param>
    /// <returns>The counts.</returns>
    internal static Result Compact(
        BspWriteState state,
        TexInfoTable texInfos,
        TexDataTable texDatas,
        IReadOnlyList<int> surfaceProperties,
        IReadOnlyList<ITexInfoReferences> others)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(texDatas);
        ArgumentNullException.ThrowIfNull(surfaceProperties);
        ArgumentNullException.ThrowIfNull(others);

        int texInfoCount = texInfos.Count;
        int[] texInfoRef = new int[texInfoCount];
        int[] texInfoOut = new int[texInfoCount];
        int[] texDataRef = new int[texDatas.Count];
        int[] texDataOut = new int[texDatas.Count];

        // get texinfos referenced by faces
        foreach (DFace f in state.DrawFaces)
        {
            texInfoRef[f.TexInfo]++;
        }

        // get texinfos referenced by brush sides
        for (int i = 0; i < state.BrushSides.Count; i++)
        {
            DBrushSide side = state.BrushSides[i];

            // not referenced by any visible geometry
            if (texInfoRef[side.TexInfo] == 0)
            {
                side.TexInfo = (short)FindMatchingBrushSideTexinfo(side.TexInfo, texInfoRef, texInfos, surfaceProperties);
                state.BrushSides[i] = side;

                // didn't find anything suitable, go ahead and reference it
                if (texInfoRef[side.TexInfo] == 0)
                {
                    texInfoRef[side.TexInfo]++;
                }
            }
        }

        foreach (ITexInfoReferences other in others)
        {
            other.CountReferences(texInfoRef);
        }

        foreach (DLeafWaterData w in state.LeafWaterData)
        {
            if (w.SurfaceTexInfoId >= 0)
            {
                texInfoRef[w.SurfaceTexInfoId]++;
            }
        }

        // reference all used texdatas
        for (int i = 0; i < texInfoCount; i++)
        {
            if (texInfoRef[i] > 0)
            {
                texDataRef[texInfos[i].TexData]++;
            }
        }

        // ComapctTexinfoArray
        List<TexInfo> compacted = [];
        int firstSky = -1;
        int first2DSky = -1;
        for (int i = 0; i < texInfoCount; i++)
        {
            if (texInfoRef[i] == 0)
            {
                texInfoOut[i] = -1;
                continue;
            }

            TexInfo old = texInfos[i];

            // only add one sky texinfo + one 2D sky texinfo
            if ((old.Flags & (int)SurfaceFlags.Sky2D) != 0)
            {
                if (first2DSky < 0)
                {
                    first2DSky = compacted.Count;
                    compacted.Add(old);
                }

                texInfoOut[i] = first2DSky;
                continue;
            }

            if ((old.Flags & (int)SurfaceFlags.Sky) != 0)
            {
                if (firstSky < 0)
                {
                    firstSky = compacted.Count;
                    compacted.Add(old);
                }

                texInfoOut[i] = firstSky;
                continue;
            }

            texInfoOut[i] = compacted.Count;
            compacted.Add(old);
        }

        // CompactTexdataArray
        TexDataStringTable strings = new();
        List<DTexData> texData = [];
        for (int i = 0; i < texDatas.Count; i++)
        {
            // unreferenced, note in map and skip
            if (texDataRef[i] == 0)
            {
                texDataOut[i] = -1;
                continue;
            }

            texDataOut[i] = texData.Count;

            // get old string and re-add to table
            DTexData entry = texDatas[i];
            entry.NameStringTableId = strings.AddOrFind(texDatas.NameOf(i));
            texData.Add(entry);
        }

        for (int i = 0; i < compacted.Count; i++)
        {
            TexInfo t = compacted[i];
            t.TexData = texDataOut[t.TexData];
            compacted[i] = t;
        }

        // remap texinfos on faces
        for (int i = 0; i < state.DrawFaces.Count; i++)
        {
            DFace f = state.DrawFaces[i];
            f.TexInfo = (short)texInfoOut[f.TexInfo];
            state.DrawFaces[i] = f;
        }

        // remap texinfos on brushsides
        for (int i = 0; i < state.BrushSides.Count; i++)
        {
            DBrushSide s = state.BrushSides[i];
            s.TexInfo = (short)texInfoOut[s.TexInfo];
            state.BrushSides[i] = s;
        }

        foreach (ITexInfoReferences other in others)
        {
            other.Remap(texInfoOut);
        }

        // remap leaf water data
        for (int i = 0; i < state.LeafWaterData.Count; i++)
        {
            DLeafWaterData w = state.LeafWaterData[i];
            if (w.SurfaceTexInfoId >= 0)
            {
                w.SurfaceTexInfoId = (short)texInfoOut[w.SurfaceTexInfoId];
                state.LeafWaterData[i] = w;
            }
        }

        int stringBytesBefore = texDatas.Strings.ToData().Length;

        state.CompactedTexInfo = compacted;
        state.CompactedTexData = texData;
        state.CompactedStrings = strings;

        return new Result(
            texInfoCount, compacted.Count, texDatas.Count, texData.Count,
            stringBytesBefore, strings.ToData().Length);
    }

    /// <summary>
    /// <c>FindMatchingBrushSideTexinfo</c>.
    /// </summary>
    private static int FindMatchingBrushSideTexinfo(
        int sideTexInfo, int[] refCounts, TexInfoTable texInfos, IReadOnlyList<int> surfaceProperties)
    {
        // find one with the same flags & surfaceprops (even if the texture name is different)
        int sideTexFlags = texInfos[sideTexInfo].Flags;
        int sideTexData = texInfos[sideTexInfo].TexData;
        int sideSurfaceProp = surfaceProperties[sideTexData];

        for (int j = 0; j < texInfos.Count; j++)
        {
            if (refCounts[j] > 0
                && texInfos[j].Flags == sideTexFlags
                && surfaceProperties[texInfos[j].TexData] == sideSurfaceProp)
            {
                // found one
                return j;
            }
        }

        // can't find a better match
        return sideTexInfo;
    }
}
