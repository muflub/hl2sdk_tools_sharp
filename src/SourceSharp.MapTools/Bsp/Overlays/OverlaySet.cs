using System.Globalization;
using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Overlays;

/// <summary>
/// A compile's overlays: <c>g_aMapOverlays</c> and <c>g_aMapWaterOverlays</c>
/// From the map to LUMP_OVERLAYS,
/// LUMP_OVERLAY_FADES and LUMP_WATEROVERLAYS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock call order.</b> During the load of each map, every
/// <c>info_overlay</c> goes through <see cref="AddFromEntity"/> as its entity
/// is read and every <c>overlaytransition</c>'s
/// <c>overlaydata</c> through <see cref="AddWaterOverlay"/>
/// When that map's load finishes the side lists
/// are resolved against ITS sides (<see cref="UpdateSideLists"/>,
///). While faces are written, <c>EmitFace</c> calls
/// <see cref="AddFace"/> for every face. In
/// <c>EndBSPFile</c>, after the displacement pass and before the collision
/// lumps, <see cref="EmitAsync"/> converts both lists.
/// </para>
/// <para>
/// The 3a loader records <c>info_overlay</c> entities in
/// <see cref="MapFile.OverlayEntities"/> without converting them and keeps
/// the <c>overlaytransition</c> chunks in <see cref="MapFile.WaterOverlayData"/>;
/// <see cref="Load(MapFile, MaterialReplacements?, ComplianceOptions?)"/> does
/// both from the map, for a map with no instances. Overlays inside instances
/// (<c>Overlay_Translate</c>) are not handled here.
/// </para>
/// </remarks>
public sealed class OverlaySet
{
    /// <summary><c>g_aMapOverlays</c>.</summary>
    public List<MapOverlay> Overlays { get; } = [];

    /// <summary><c>g_aMapWaterOverlays</c>.</summary>
    public List<MapOverlay> WaterOverlays { get; } = [];

    /// <summary>
    /// The material replacement table, or null: applied to a WATER overlay's
    /// material, and to an <c>info_overlay</c>'s
    /// only under Correct(reads the key raw).
    /// </summary>
    public MaterialReplacements? Replacements { get; set; }

    /// <summary>Which of the loader's defects to reproduce; the compile's compliance.</summary>
    public ComplianceOptions Compliance { get; set; } = ComplianceOptions.Correct;

    /// <summary>
    /// Everything the loader would have done for one map: its
    /// <c>info_overlay</c> entities in entity order, its water overlays in
    /// document order, then both side lists.
    /// </summary>
    /// <param name="map">The loaded map.</param>
    /// <param name="document">The VMF it was loaded from.</param>
    /// <param name="replacements">The compile's material replacement table, or null.</param>
    /// <param name="compliance">The compile's compliance; null is Correct.</param>
    /// <returns>The set.</returns>
    /// <exception cref="MapCompileException">A fatal stock error.</exception>
    public static OverlaySet Load(MapFile map, VmfDocument document, MaterialReplacements? replacements = null, ComplianceOptions? compliance = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(document);

        List<VmfChunk> water = [];
        foreach (VmfChunk chunk in document.Chunks)
        {
            if (!string.Equals(chunk.Name, "world", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(chunk.Name, "entity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (VmfChunk transition in chunk.Chunks)
            {
                if (!string.Equals(transition.Name, "overlaytransition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (VmfChunk data in transition.Chunks)
                {
                    if (string.Equals(data.Name, "overlaydata", StringComparison.OrdinalIgnoreCase))
                    {
                        water.Add(data);
                    }
                }
            }
        }

        return Load(map, water, replacements, compliance);
    }

    /// <summary>
    /// <see cref="Load(MapFile, VmfDocument, MaterialReplacements?, ComplianceOptions?)"/>
    /// with the water overlays the loader kept on the map
    /// (<see cref="MapFile.WaterOverlayData"/>), so no document is needed.
    /// </summary>
    /// <param name="map">The loaded map.</param>
    /// <param name="replacements">The compile's material replacement table, or null.</param>
    /// <param name="compliance">The compile's compliance; null is Correct.</param>
    /// <returns>The set.</returns>
    /// <exception cref="MapCompileException">A fatal stock error.</exception>
    public static OverlaySet Load(MapFile map, MaterialReplacements? replacements = null, ComplianceOptions? compliance = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Load(map, map.WaterOverlayData, replacements, compliance);
    }

    private static OverlaySet Load(MapFile map, IReadOnlyList<VmfChunk> water, MaterialReplacements? replacements, ComplianceOptions? compliance)
    {
        OverlaySet set = new() { Replacements = replacements, Compliance = compliance ?? ComplianceOptions.Correct };
        int startOverlays = set.Overlays.Count;
        int startWater = set.WaterOverlays.Count;

        foreach (int entityIndex in map.OverlayEntities)
        {
            set.AddFromEntity(map.Entities[entityIndex]);
        }

        foreach (VmfChunk data in water)
        {
            set.AddWaterOverlay(data);
        }

        set.UpdateSideLists(map, startOverlays, startWater);
        return set;
    }

    /// <summary>
    /// <c>Overlay_GetFromEntity</c> plus the
    /// Entity rewrite of the reference implementation.
    /// </summary>
    /// <param name="entity">An <c>info_overlay</c>.</param>
    /// <returns>The accessor id: the overlay's id when it is named, else -1.</returns>
    /// <exception cref="MapCompileException">
    /// An invalid render order or a material name too long.
    /// </exception>
    public int AddFromEntity(MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        MapOverlay overlay = new() { Id = Overlays.Count };
        Overlays.Add(overlay);

        int accessor = entity.ValueForKey("targetname").Length > 0 ? overlay.Id : -1;

        overlay.U = (entity.FloatForKey("StartU"), entity.FloatForKey("EndU"));
        overlay.V = (entity.FloatForKey("StartV"), entity.FloatForKey("EndV"));

        float fadeMin = entity.FloatForKey("fademindist");
        overlay.FadeDistMinSq = fadeMin > 0 ? fadeMin * fadeMin : fadeMin;
        float fadeMax = entity.FloatForKey("fademaxdist");
        overlay.FadeDistMaxSq = fadeMax > 0 ? fadeMax * fadeMax : fadeMax;

        overlay.Origin = entity.GetVectorForKey("BasisOrigin");

        overlay.RenderOrder = entity.IntForKey("RenderOrder");
        if (overlay.RenderOrder < 0 || overlay.RenderOrder >= MapOverlay.RenderOrders)
        {
            throw new MapCompileException(string.Create(
                CultureInfo.InvariantCulture,
                $"{SurfaceContentDiagnostics.OverlayInvalidRenderOrder}: Overlay ({entity.ValueForKey("material")}) at "
                + $"{overlay.Origin.X:F6} {overlay.Origin.Y:F6} {overlay.Origin.Z:F6} has invalid render order ({overlay.RenderOrder})."));
        }

        for (int i = 0; i < 4; i++)
        {
            overlay.UvPoints[i] = entity.GetVectorForKey($"uv{i}");
        }

        overlay.Basis[0] = entity.GetVectorForKey("BasisU");
        overlay.Basis[1] = entity.GetVectorForKey("BasisV");
        overlay.Basis[2] = entity.GetVectorForKey("BasisNormal");

        // Reads the key raw although the water overlays and the
        // brush sides go through -replacematerials: a defect
        // (StockQuirk.OverlayMaterialNotReplaced), fixed under Correct.
        string material = entity.ValueForKey("material");
        if (Replacements is not null && !Compliance.Emulates(StockQuirk.OverlayMaterialNotReplaced))
        {
            material = Replacements.Replace(material);
        }

        overlay.MaterialName = CheckedMaterial(material);

        overlay.SideList.AddRange(CubemapFixups.ParseSideList(entity.ValueForKey("sides")));

        if (accessor < 0)
        {
            entity.Clear();
        }
        else
        {
            entity.SetKeyValue("classname", "info_overlay_accessor");
            entity.SetKeyValue("OverlayID", accessor);
        }

        return accessor;
    }

    /// <summary>
    /// <c>LoadOverlayDataTransitionCallback</c> and its key callback
    /// One water overlay.
    /// </summary>
    /// <param name="overlayData">An <c>overlaydata</c> chunk.</param>
    /// <returns>The overlay.</returns>
    /// <remarks>
    /// <para>
    /// Keys are matched case-insensitively and applied in file order; the
    /// vectors are <c>ReadKeyValueVector3</c>, which wants BRACKETS
    /// (<c>"[%f %f %f]"</c>) and on a failed scan
    /// leaves the vector as it was — zero here, uninitialised in stock. The
    /// render order is always 0 and the fade distances are never read.
    /// </para>
    /// </remarks>
    /// <exception cref="MapCompileException">A material name too long.</exception>
    public MapOverlay AddWaterOverlay(VmfChunk overlayData)
    {
        ArgumentNullException.ThrowIfNull(overlayData);

        MapOverlay overlay = new() { Id = MapOverlay.MaxMapOverlays + 1 + WaterOverlays.Count, RenderOrder = 0 };
        WaterOverlays.Add(overlay);

        foreach (VmfNode node in overlayData.Children)
        {
            if (node is not VmfKey key)
            {
                continue;
            }

            string name = key.Name;
            string value = key.Value;

            if (Is(name, "material"))
            {
                overlay.MaterialName = CheckedMaterial(Replacements?.Replace(value) ?? value);
            }
            else if (Is(name, "StartU"))
            {
                overlay.U = (VmfValue.ParseFloat(value), overlay.U.End);
            }
            else if (Is(name, "EndU"))
            {
                overlay.U = (overlay.U.Start, VmfValue.ParseFloat(value));
            }
            else if (Is(name, "StartV"))
            {
                overlay.V = (VmfValue.ParseFloat(value), overlay.V.End);
            }
            else if (Is(name, "EndV"))
            {
                overlay.V = (overlay.V.Start, VmfValue.ParseFloat(value));
            }
            else if (Is(name, "BasisOrigin"))
            {
                overlay.Origin = Vector3(value, overlay.Origin);
            }
            else if (Is(name, "BasisU"))
            {
                overlay.Basis[0] = Vector3(value, overlay.Basis[0]);
            }
            else if (Is(name, "BasisV"))
            {
                overlay.Basis[1] = Vector3(value, overlay.Basis[1]);
            }
            else if (Is(name, "BasisNormal"))
            {
                overlay.Basis[2] = Vector3(value, overlay.Basis[2]);
            }
            else if (name.Length == 3 && name.StartsWith("uv", StringComparison.OrdinalIgnoreCase) && name[2] is >= '0' and <= '3')
            {
                int i = name[2] - '0';
                overlay.UvPoints[i] = Vector3(value, overlay.UvPoints[i]);
            }
            else if (Is(name, "sides"))
            {
                // The lists are purged and refilled, so the
                // LAST "sides" key wins.
                overlay.SideList.Clear();
                overlay.FaceList.Clear();
                overlay.SideList.AddRange(CubemapFixups.ParseSideList(value));
            }
        }

        return overlay;
    }

    /// <summary>
    /// <c>Overlay_UpdateSideLists</c> and <c>OverlayTransition_UpdateSideLists</c>
    /// Each overlay's id is added, once, to every
    /// side of <paramref name="map"/> it names.
    /// </summary>
    /// <param name="map">The map just loaded: <c>g_LoadingMap</c>.</param>
    /// <param name="startOverlay">The first overlay that map added.</param>
    /// <param name="startWaterOverlay">The first water overlay that map added.</param>
    public void UpdateSideLists(MapFile map, int startOverlay, int startWaterOverlay)
    {
        ArgumentNullException.ThrowIfNull(map);

        for (int i = startOverlay; i < Overlays.Count; i++)
        {
            foreach (int sideId in Overlays[i].SideList)
            {
                MapBrushSide? side = GetSide(map, sideId);
                if (side is not null && !side.OverlayIds.Contains(Overlays[i].Id))
                {
                    side.OverlayIds.Add(Overlays[i].Id);
                }
            }
        }

        for (int i = startWaterOverlay; i < WaterOverlays.Count; i++)
        {
            foreach (int sideId in WaterOverlays[i].SideList)
            {
                MapBrushSide? side = GetSide(map, sideId);
                if (side is not null && !side.WaterOverlayIds.Contains(WaterOverlays[i].Id))
                {
                    side.WaterOverlayIds.Add(WaterOverlays[i].Id);
                }
            }
        }
    }

    /// <summary>
    /// <c>Overlay_AddFaceToLists</c> and <c>OverlayTransition_AddFaceToLists</c>
    /// As <c>EmitFace</c> calls them
    /// For the face it just wrote.
    /// </summary>
    /// <param name="faceIndex">The face's index in LUMP_FACES.</param>
    /// <param name="side">The face's original side, or null.</param>
    public void AddFace(int faceIndex, MapBrushSide? side)
    {
        if (side is null)
        {
            return;
        }

        AddOverlayFace(faceIndex, side);
        AddWaterOverlayFace(faceIndex, side);
    }

    /// <summary><c>Overlay_AddFaceToLists</c> alone.</summary>
    /// <param name="faceIndex">The face's index in LUMP_FACES.</param>
    /// <param name="side">The face's original side.</param>
    public void AddOverlayFace(int faceIndex, MapBrushSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        foreach (int id in side.OverlayIds)
        {
            AddOnce(Overlays[id].FaceList, faceIndex);
        }
    }

    /// <summary><c>OverlayTransition_AddFaceToLists</c> alone.</summary>
    /// <param name="faceIndex">The face's index in LUMP_FACES.</param>
    /// <param name="side">The face's original side.</param>
    public void AddWaterOverlayFace(int faceIndex, MapBrushSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        foreach (int id in side.WaterOverlayIds)
        {
            AddOnce(WaterOverlays[id - (MapOverlay.MaxMapOverlays + 1)].FaceList, faceIndex);
        }
    }

    /// <summary>
    /// <c>Overlay_EmitOverlayFaces</c> and <c>OverlayTransition_EmitOverlayFaces</c>
    /// </summary>
    /// <param name="context">
    /// The compile: each overlay's texdata and texinfo are found or created
    /// in its tables, in overlay order.
    /// </param>
    /// <param name="cancellationToken">Cancels the material reads.</param>
    /// <returns>The three lumps' records.</returns>
    /// <exception cref="MapCompileException">One of stock's fatal limits.</exception>
    public async Task<OverlayLumps> EmitAsync(VbspContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Overlays.Count > MapOverlay.MaxMapOverlays)
        {
            throw new MapCompileException(
                $"{SurfaceContentDiagnostics.TooManyOverlays}: Too Many Overlays!\nMAX_MAP_OVERLAYS = {MapOverlay.MaxMapOverlays}");
        }

        if (WaterOverlays.Count > MapOverlay.MaxMapWaterOverlays)
        {
            throw new MapCompileException(
                $"{SurfaceContentDiagnostics.TooManyOverlays}: Too many water overlays!\nMAX_MAP_WATEROVERLAYS = {MapOverlay.MaxMapWaterOverlays}");
        }

        DOverlay[] overlays = new DOverlay[Overlays.Count];
        DOverlayFade[] fades = new DOverlayFade[Overlays.Count];

        for (int i = 0; i < Overlays.Count; i++)
        {
            MapOverlay map = Overlays[i];
            DOverlay o = default;
            o.Id = map.Id;
            o.U[0] = map.U.Start;
            o.U[1] = map.U.End;
            o.V[0] = map.V.Start;
            o.V[1] = map.V.End;
            FillUv(map, o.UvPoints);
            o.Origin = map.Origin;
            o.BasisNormal = map.Basis[2];
            o.SetRenderOrder((ushort)map.RenderOrder);
            o.TexInfo = checked((short)await TexInfoForAsync(context, map.MaterialName, cancellationToken).ConfigureAwait(false));

            if (TooMany(context, map.FaceList.Count, MapOverlay.MaxFaces))
            {
                throw TooManyFaces("Overlay", map, MapOverlay.MaxFaces);
            }

            o.SetFaceCount((ushort)map.FaceList.Count);
            for (int f = 0; f < map.FaceList.Count; f++)
            {
                o.Faces[f] = map.FaceList[f];
            }

            overlays[i] = o;
            fades[i] = new DOverlayFade { FadeDistMinSq = map.FadeDistMinSq, FadeDistMaxSq = map.FadeDistMaxSq };
        }

        DWaterOverlay[] water = new DWaterOverlay[WaterOverlays.Count];
        for (int i = 0; i < WaterOverlays.Count; i++)
        {
            MapOverlay map = WaterOverlays[i];
            DWaterOverlay o = default;
            o.Id = map.Id;
            o.U[0] = map.U.Start;
            o.U[1] = map.U.End;
            o.V[0] = map.V.Start;
            o.V[1] = map.V.End;
            FillUv(map, o.UvPoints);
            o.Origin = map.Origin;
            o.BasisNormal = map.Basis[2];
            o.TexInfo = checked((short)await TexInfoForAsync(context, map.MaterialName, cancellationToken).ConfigureAwait(false));

            if (TooMany(context, map.FaceList.Count, MapOverlay.MaxWaterFaces))
            {
                // Prints OVERLAY_BSP_FACE_COUNT as the limit it
                // just exceeded WATEROVERLAY_BSP_FACE_COUNT: the message is
                // wrong, the check is right.
                throw TooManyFaces("Water Overlay", map, MapOverlay.MaxFaces);
            }

            // SetRenderOrder then SetFaceCount(370): order in
            // the top two bits, count in the rest.
            o.FaceCountAndRenderOrder = (ushort)((map.RenderOrder << 14) | (map.FaceList.Count & 0x3FFF));
            for (int f = 0; f < map.FaceList.Count; f++)
            {
                o.Faces[f] = map.FaceList[f];
            }

            water[i] = o;
        }

        return new OverlayLumps(overlays, fades, water);
    }

    // The four uv points, BasisU packed into the unused z
    // of the first three, and the V-flip flag into the fourth's z.
    private static void FillUv(MapOverlay map, Span<Vec3> uv)
    {
        for (int i = 0; i < 4; i++)
        {
            uv[i] = map.UvPoints[i];
        }

        uv[0] = new Vec3(uv[0].X, uv[0].Y, map.Basis[0].X);
        uv[1] = new Vec3(uv[1].X, uv[1].Y, map.Basis[0].Y);
        uv[2] = new Vec3(uv[2].X, uv[2].Y, map.Basis[0].Z);

        Vec3 cross = Vec3.Cross(map.Basis[2], map.Basis[0]);
        if (Vec3.Dot(cross, map.Basis[1]) < 0.0f)
        {
            uv[3] = new Vec3(uv[3].X, uv[3].Y, 1.0f);
        }
    }

    // Flags 0, texdata by name, all vectors zero with a
    // -99999 offset -- never through FindMiptex, so no SURF_ flags.
    private static async ValueTask<int> TexInfoForAsync(VbspContext context, string material, CancellationToken cancellationToken)
    {
        TexInfo texInfo = default;
        texInfo.Flags = 0;
        texInfo.TexData = await context.TexDatas
            .FindOrCreateAsync(material, context.Materials, context.Diagnostics, cancellationToken)
            .ConfigureAwait(false);

        for (int v = 0; v < 2; v++)
        {
            texInfo.LightmapVecsLuxelsPerWorldUnits[(v * 4) + 3] = -99999.0f;
            texInfo.TextureVecsTexelsPerWorldUnits[(v * 4) + 3] = -99999.0f;
        }

        return context.TexInfos.FindOrCreate(texInfo);
    }

    // 364: ">=" the array size, so a list that FILLS the
    // 64 (or 256) slots is refused although it fits: a defect
    // (StockQuirk.OverlayFaceLimitOffByOne); Correct refuses only an overflow.
    private static bool TooMany(VbspContext context, int count, int slots) =>
        context.Options.Compliance.Emulates(StockQuirk.OverlayFaceLimitOffByOne) ? count >= slots : count > slots;

    private static MapCompileException TooManyFaces(string what, MapOverlay map, int limit) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"{SurfaceContentDiagnostics.OverlayTooManyFaces}: {what} touching too many faces (touching {map.FaceList.Count}, max {limit})\n"
            + $"Overlay {map.MaterialName} at {map.Origin.X:F1} {map.Origin.Y:F1} {map.Origin.Z:F1}"));

    private static string CheckedMaterial(string material)
    {
        if (material.Length >= MapOverlay.MaterialNameLength)
        {
            throw new MapCompileException(
                $"{SurfaceContentDiagnostics.OverlayMaterialNameTooLong}: Overlay Material Name ({material}) too long! > OVERLAY_MAP_STRLEN ({MapOverlay.MaterialNameLength})");
        }

        return material;
    }

    // GetSide: the first side of the loading map with the id.
    private static MapBrushSide? GetSide(MapFile map, int sideId)
    {
        int index = map.SideIdToIndex(sideId);
        return index < 0 ? null : map.BrushSides[index];
    }

    private static Vec3 Vector3(string value, Vec3 unchanged) =>
        VmfValue.TryParseVector3(value, out Vec3 v) ? v : unchanged;

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static void AddOnce(List<int> list, int value)
    {
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }
}

/// <summary>The records of LUMP_OVERLAYS, LUMP_OVERLAY_FADES and LUMP_WATEROVERLAYS.</summary>
/// <param name="Overlays">One per <c>info_overlay</c>, in order.</param>
/// <param name="Fades">Parallel to <paramref name="Overlays"/>.</param>
/// <param name="WaterOverlays">One per <c>overlaydata</c>, in order.</param>
public sealed record OverlayLumps(DOverlay[] Overlays, DOverlayFade[] Fades, DWaterOverlay[] WaterOverlays)
{
    /// <summary>LUMP_OVERLAYS' bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] OverlayBytes() => MemoryMarshal.AsBytes(Overlays.AsSpan()).ToArray();

    /// <summary>LUMP_OVERLAY_FADES' bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] FadeBytes() => MemoryMarshal.AsBytes(Fades.AsSpan()).ToArray();

    /// <summary>LUMP_WATEROVERLAYS' bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] WaterOverlayBytes() => MemoryMarshal.AsBytes(WaterOverlays.AsSpan()).ToArray();
}
