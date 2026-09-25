using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// The base face of one displacement, as the lump builder needs it.
/// </summary>
/// <param name="FaceIndex">
/// Its index in LUMP_FACES, which becomes <c>ddispinfo_t::m_iMapFace</c>.
/// </param>
/// <param name="Winding">
/// The face's four points, in winding order and NOT yet rotated to the start
/// corner.
/// </param>
/// <param name="Contents">The <c>CONTENTS_*</c> bits of the brush it came from.</param>
/// <param name="LightmapVecU">
/// <c>texinfo_t::lightmapVecsLuxelsPerWorldUnits[0]</c>'s three spatial
/// components.
/// </param>
/// <param name="LightmapVecV">The same for <c>[1]</c>.</param>
/// <param name="TextureVecs">
/// <c>texinfo_t::textureVecsTexelsPerWorldUnits</c>, two rows of four.
/// </param>
public readonly record struct DisplacementFace(
    int FaceIndex,
    Vec3[] Winding,
    int Contents,
    Vec3 LightmapVecU,
    Vec3 LightmapVecV,
    float[] TextureVecs);

/// <summary>
/// What a compile gets back for one displacement.
/// </summary>
/// <param name="Info">Its LUMP_DISPINFO entry.</param>
/// <param name="Core">
/// The tessellated displacement, which vbsp's physics pass and vrad both want.
/// </param>
/// <param name="LightmapSizeU">
/// The base face's <c>m_LightmapTextureSizeInLuxels[0]</c>, which the caller
/// must write back onto the face.
/// </param>
/// <param name="LightmapSizeV">Its <c>[1]</c>.</param>
/// <param name="NeedsSwappedTexInfo">
/// Whether stock's <c>CalcLuxelCoords</c> asked for the lightmap axes to be
/// swapped. See <see cref="CoreDispSurface.CalcLuxelCoords"/>.
/// <b>Stock output never shows it</b>: the swap repoints only the MAP face's
/// texinfo after the <c>dface_t</c> was emitted
/// And <c>CompactTexinfos</c> removes the
/// unreferenced copy. The driver asks <see cref="DispVbspHooks.FaceTexInfos"/>
/// which texinfo the face carries: stock's (unchanged) or, under Correct
/// (<see cref="StockQuirk.DispLightmapSwapDropped"/>), the swapped copy.
/// Measured on <c>p3f_swap</c> (<c>DispStockCoverageTests</c>).
/// </param>
public sealed record DisplacementResult(
    DispInfo Info,
    CoreDispInfo Core,
    int LightmapSizeU,
    int LightmapSizeV,
    bool NeedsSwappedTexInfo);

/// <summary>
/// Every displacement lump a vbsp compile writes:
/// <c>EmitInitialDispInfos</c> and <c>EmitDispLMAlphaAndNeighbors</c>,
/// </summary>
/// <remarks>
/// <para>
/// Stock splits this across two moments of the compile because it has to: the
/// vertex and triangle runs are laid out before the BSP is built
/// And the neighbour, allowed-vertex and lightmap-sample
/// data cannot be computed until faces exist
/// The split is real but the DATA does not
/// actually straddle it — the first half needs only the VMF, and the second
/// needs the faces. So this is one call taking both, and the caller is
/// responsible for having faces.
/// </para>
/// <para>
/// WHAT IS NOT HERE, and must be done by the caller because it is not this
/// lane's: writing <c>m_LightmapTextureSizeInLuxels</c> back onto the face
/// (<see cref="DisplacementResult.LightmapSizeU"/>/<c>V</c>, which stock DOES
/// write). The swapped texinfo
/// (<see cref="DisplacementResult.NeedsSwappedTexInfo"/>) is reported, and in
/// stock never reaches the BSP.
/// </para>
/// </remarks>
public static class DisplacementLumpBuilder
{
    /// <summary>
    /// The contents bits that stop <c>DispMapToCoreDispInfo</c> forcing a
    /// displacement solid.
    /// </summary>
    /// <remarks>
    /// <c>ALL_VISIBLE_CONTENTS | CONTENTS_PLAYERCLIP | CONTENTS_MONSTERCLIP</c>,
    /// A displacement whose material gave it none of
    /// these — a nodraw one, say — is made <c>CONTENTS_SOLID</c> so that it
    /// still blocks movement.
    /// </remarks>
    public const int VisibleOrClipContents =
        AllVisibleContents
        | (int)BrushContents.PlayerClip
        | (int)BrushContents.MonsterClip;

    /// <summary>
    /// <c>ALL_VISIBLE_CONTENTS</c>.
    /// </summary>
    /// <remarks>
    /// Spelled <c>LAST_VISIBLE_CONTENTS | (LAST_VISIBLE_CONTENTS - 1)</c>, so
    /// it is every bit up to and including <c>CONTENTS_OPAQUE</c> — the low
    /// byte, and NOT an enumeration of the visible contents by name.
    /// <c>CONTENTS_MOVEABLE</c> is 0x4000 and is therefore NOT in it, which is
    /// the trap in reading the macro as a list.
    /// </remarks>
    public const int AllVisibleContents =
        (int)BrushContents.Opaque | ((int)BrushContents.Opaque - 1);

    /// <summary>
    /// Builds every displacement lump for a map.
    /// </summary>
    /// <param name="displacements">
    /// The map's displacements in VMF order, which is LUMP_DISPINFO order.
    /// </param>
    /// <param name="faces">
    /// The base face of each, at the same index.
    /// </param>
    /// <param name="options">
    /// The compile's options; only <see cref="VbspOptions.Compliance"/> is
    /// read.
    /// </param>
    /// <param name="lumps">The vertex, triangle and sample buffers to fill.</param>
    /// <param name="diagnostics">Where warnings go, or null.</param>
    /// <param name="worldBounds">
    /// Receives, when given, each displacement's <see cref="ComputeDispInfoBounds"/>
    /// box in order, taken from the core this build makes instead of from a
    /// second one.
    /// </param>
    /// <returns>One result per displacement, in the same order.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The two lists are different lengths, or a face does not have four
    /// winding points.
    /// </exception>
    public static IReadOnlyList<DisplacementResult> Build(
        IReadOnlyList<MapDisplacement> displacements,
        IReadOnlyList<DisplacementFace> faces,
        VbspOptions options,
        DisplacementLumps lumps,
        ICollection<CompileDiagnostic>? diagnostics = null,
        List<DispBox>? worldBounds = null)
    {
        ArgumentNullException.ThrowIfNull(displacements);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lumps);

        if (displacements.Count != faces.Count)
        {
            throw new ArgumentException(
                $"{displacements.Count} displacements but {faces.Count} base faces.",
                nameof(faces));
        }

        bool stockNormalise = options.Compliance.Emulates(StockQuirk.DispVertNormalise);
        bool stockNormalMean = options.Compliance.Emulates(StockQuirk.DispVertexNormalMeanUnnormalised);

        DispInfo[] infos = new DispInfo[displacements.Count];
        CoreDispInfo[] cores = new CoreDispInfo[displacements.Count];
        bool[] swapped = new bool[displacements.Count];

        // EmitInitialDispInfos: the vertex and triangle runs, laid out in
        // order, before anything else is known.
        for (int i = 0; i < displacements.Count; i++)
        {
            MapDisplacement disp = displacements[i];
            DisplacementFace face = faces[i];

            if (face.Winding is not { Length: 4 })
            {
                throw new ArgumentException(
                    $"displacement {i}'s base face has "
                    + $"{face.Winding?.Length ?? 0} winding points, not four.",
                    nameof(faces));
            }

            infos[i] = new DispInfo
            {
                DispVertStart = lumps.Verts.Count,
                DispTriStart = lumps.Tris.Count,
                Power = disp.Power,

                // The high bit says "these are FLAGS", and vbsp always sets it.
                // With the minTess line commented out
                // beside it.
                MinTess = unchecked((int)0x80000000) | disp.Flags,
                SmoothingAngle = disp.SmoothingAngle,

                // From the BRUSH, not from the material and not from the
                // forced-solid value DispMapToCoreDispInfo computes below.
                Contents = face.Contents,
                StartPosition = disp.StartPosition,
                MapFace = (ushort)face.FaceIndex,
                LightmapAlphaStart = 0,
            };

            for (int v = 0; v < disp.VertCount; v++)
            {
                (Vec3 vector, float distance) = disp.CombinedField(v, stockNormalise);

                lumps.Verts.Add(new DispVert
                {
                    Vector = vector,
                    Dist = distance,
                    Alpha = disp.AlphaValues[v],
                });
            }

            for (int t = 0; t < disp.TriangleCount; t++)
            {
                lumps.Tris.Add(new DispTri { Tags = disp.TriangleTags[t] });
            }
        }

        // EmitDispLMAlphaAndNeighbors, first half: build a CCoreDispInfo per
        // displacement, all of them knowing about each other.
        for (int i = 0; i < displacements.Count; i++)
        {
            cores[i] = new CoreDispInfo(displacements[i].Power)
            {
                ListIndex = i,
                StockVertexNormalMean = stockNormalMean,
            };
        }

        foreach (CoreDispInfo core in cores)
        {
            core.SetListBase(cores);
        }

        for (int i = 0; i < displacements.Count; i++)
        {
            swapped[i] = DispMapToCoreDispInfo(
                displacements[i], faces[i], cores[i], stockNormalise);
        }

        // AddDispsToBounds' boxes(ComputeDispInfoBounds),
        // before the neighbours are found. Stock builds a second CCoreDispInfo
        // per displacement for them, with no face: that only leaves the corner
        // texture coordinates at the unit square, and
        // neither box reads them -- the base quad's points and the displaced
        // vertices are the same in both builds -- so this core's boxes are the
        // second one's (plan 3p: the rebuild was a third of the stage).
        if (worldBounds is not null)
        {
            bool baseQuad = options.Compliance.Emulates(StockQuirk.DispWorldBoundsBaseQuad);
            foreach (CoreDispInfo core in cores)
            {
                worldBounds.Add(baseQuad ? DispNeighbourFinder.GetDispBox(core) : core.RootBounds);
            }
        }

        DispNeighbourFinder.FindNeighbouringDispSurfs(cores, diagnostics);
        DispNeighbourFinder.SetupAllowedVerts(cores);

        for (int i = 0; i < displacements.Count; i++)
        {
            ExportNeighbourData(cores[i], ref infos[i]);
            SnapRemainingVertsToSurface(cores[i], infos[i], lumps);
        }

        // Second half: the lightmap sample positions, which need the luxel
        // coordinates the core displacements now carry.
        for (int i = 0; i < displacements.Count; i++)
        {
            infos[i].LightmapSamplePositionStart = lumps.LightmapSamplePositions.Count;

            LightmapSamplePositions.Append(
                cores[i],
                cores[i].Surface.LuxelU,
                cores[i].Surface.LuxelV,
                lumps.LightmapSamplePositions);
        }

        DisplacementResult[] results = new DisplacementResult[displacements.Count];
        for (int i = 0; i < displacements.Count; i++)
        {
            results[i] = new DisplacementResult(
                infos[i],
                cores[i],
                cores[i].Surface.LuxelU,
                cores[i].Surface.LuxelV,
                swapped[i]);
        }

        return results;
    }

    /// <summary>
    /// Fills one <see cref="CoreDispInfo"/> from its VMF data and its base
    /// face: <c>DispMapToCoreDispInfo</c>.
    /// </summary>
    /// <param name="disp">The VMF displacement.</param>
    /// <param name="face">Its base face.</param>
    /// <param name="core">The displacement to fill.</param>
    /// <param name="stockNormalise">
    /// Reproduce stock's normalise estimate for the field vectors and the
    /// generated normals.
    /// </param>
    /// <param name="withFace">
    /// False reproduces stock's <c>pFace == NULL</c> callers
    /// (<c>ComputeDispInfoBounds</c>, and detail-prop
    /// placement): the corner texture
    /// coordinates stay at stock's unit-square defaults
    /// <c>{(0,0),(0,1),(1,0),(1,1)}</c> instead of being
    /// projected. Everything else, the luxel layout included, is the same.
    /// </param>
    /// <returns>
    /// Whether the face's texinfo needs its lightmap axes swapped. Stock acts
    /// on it only when a face was given.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="disp"/> or <paramref name="core"/> is null.
    /// </exception>
    /// <remarks>
    /// The ORDER here is load bearing twice over. The contents must be forced
    /// solid before the surface takes a copy; and the points must be set,
    /// the start index found and the points rotated before
    /// <see cref="CoreDispSurface.CalcLuxelCoords"/> measures the quad's edges,
    /// because the luxel layout is relative to the start corner.
    /// </remarks>
    public static bool DispMapToCoreDispInfo(
        MapDisplacement disp,
        DisplacementFace face,
        CoreDispInfo core,
        bool stockNormalise,
        bool withFace = true)
    {
        ArgumentNullException.ThrowIfNull(disp);
        ArgumentNullException.ThrowIfNull(core);

        core.StockNormalise = stockNormalise;

        // The displacement's OWN contents, which is the brush's plus a forced
        // solid when the brush contributes nothing visible or clipping. Note
        // this is NOT what goes in the lump -- writes the
        // unforced brush contents there.
        int contents = face.Contents;
        if ((contents & VisibleOrClipContents) == 0)
        {
            contents |= (int)BrushContents.Solid;
        }

        disp.Contents = contents;
        core.Surface.Contents = contents;

        // Texture coordinates at the four corners. Stock defaults them to the
        // unit square when no face is given, which no vbsp path does.
        DispUv[] texCoords = withFace
            ? CalcTextureCoordsAtPoints(face.TextureVecs, face.Winding)
            : [new(0, 0), new(0, 1), new(1, 0), new(1, 1)];

        for (int i = 0; i < 4; i++)
        {
            core.Surface.SetPoint(i, face.Winding[i]);
            core.Surface.SetTexCoord(i, texCoords[i]);
        }

        core.Surface.PointStart = disp.StartPosition;
        core.Surface.FindSurfPointStartIndex();
        core.Surface.AdjustSurfPointData();

        // World units per luxel, TRUNCATED to an int. A lightmapscale of 16
        // gives a vector of length 1/16 and therefore 16; a scale that is not a
        // power of two truncates, so 1/12.5 gives 12 and the displacement's
        // lightmap is very slightly finer than the face's.
        int luxelsPerWorldUnit = (int)(1.0f / face.LightmapVecU.Length());

        bool swap = core.Surface.CalcLuxelCoords(
            luxelsPerWorldUnit, adjust: false, face.LightmapVecU, face.LightmapVecV);

        int size = disp.VertCount;
        Vec3[] vectors = new Vec3[size];
        float[] distances = new float[size];

        for (int i = 0; i < size; i++)
        {
            (vectors[i], distances[i]) = disp.CombinedField(i, stockNormalise);
        }

        core.InitDispInfo(
            unchecked((int)0x80000000) | disp.Flags,
            disp.AlphaValues,
            vectors,
            distances);

        for (int t = 0; t < disp.TriangleCount; t++)
        {
            core.SetTriTagValue(t, disp.TriangleTags[t]);
        }

        core.Create();

        return swap;
    }

    /// <summary>
    /// <c>CalcTextureCoordsAtPoints</c>.
    /// </summary>
    /// <param name="textureVecs">Two rows of four: three axis components and an offset.</param>
    /// <param name="points">The points to map.</param>
    /// <returns>One coordinate per point.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="textureVecs"/> is not eight floats.
    /// </exception>
    /// <remarks>
    /// The dot product accumulates left to right into a zeroed float and THEN
    /// adds the offset, which is one rounding more than a fused expression
    /// would do and is reproduced as written. The <c>subtractOffset</c>
    /// parameter is zero at the only call site that matters
    /// And is dropped.
    /// </remarks>
    public static DispUv[] CalcTextureCoordsAtPoints(
        float[] textureVecs, IReadOnlyList<Vec3> points)
    {
        ArgumentNullException.ThrowIfNull(textureVecs);
        ArgumentNullException.ThrowIfNull(points);

        if (textureVecs.Length != 8)
        {
            throw new ArgumentException(
                $"textureVecsTexelsPerWorldUnits is two rows of four, not {textureVecs.Length} "
                + "floats.",
                nameof(textureVecs));
        }

        DispUv[] coords = new DispUv[points.Count];
        Span<float> value = stackalloc float[2];

        for (int i = 0; i < points.Count; i++)
        {
            for (int c = 0; c < 2; c++)
            {
                float sum = 0;
                sum += points[i].X * textureVecs[(c * 4) + 0];
                sum += points[i].Y * textureVecs[(c * 4) + 1];
                sum += points[i].Z * textureVecs[(c * 4) + 2];
                sum += textureVecs[(c * 4) + 3];
                value[c] = sum;
            }

            coords[i] = new DispUv(value[0], value[1]);
        }

        return coords;
    }

    /// <summary>
    /// Copies the found neighbours and allowed vertices into the lump entry:
    /// <c>ExportCoreDispNeighborData</c> and
    /// <c>ExportCoreDispAllowedVertList</c>, and
    /// </summary>
    /// <param name="core">The tessellated displacement.</param>
    /// <param name="info">The lump entry to fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public static void ExportNeighbourData(CoreDispInfo core, ref DispInfo info)
    {
        ArgumentNullException.ThrowIfNull(core);

        for (int i = 0; i < 4; i++)
        {
            info.EdgeNeighbors[i] = core.EdgeNeighbor(i);
            info.CornerNeighbors[i] = core.CornerNeighbors(i);
        }

        for (int i = 0; i < CoreDispInfo.AllowedVertsDWords; i++)
        {
            info.AllowedVerts[i] = core.AllowedVerts[i];
        }
    }

    /// <summary>
    /// Flattens the vertices the tessellation dropped onto the surface it
    /// kept: <c>SnapRemainingVertsToSurface</c>.
    /// </summary>
    /// <param name="core">The tessellated displacement.</param>
    /// <param name="info">Its lump entry, for the vertex run's start.</param>
    /// <param name="lumps">The lump buffers, whose vertices are edited in place.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="core"/> or <paramref name="lumps"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// WHY: a vertex switched off by <c>SetupAllowedVerts</c> is not drawn, but
    /// it is still IN the lump and the engine's ray tests and overlay
    /// projection still read it. Left where it was, it sticks out of the
    /// surface that is actually rendered. So it is moved onto the plane of
    /// whichever surviving triangle covers its grid position.
    /// </para>
    /// <para>
    /// THE CHEESY PART, and stock's own word for it
    /// The new position cannot be written
    /// directly, because a <c>CDispVert</c> stores a direction and a distance
    /// relative to the flat surface rather than a position. So the OFFSET is
    /// folded into the direction and the distance is set to 1 — which leaves
    /// <c>m_vVector</c> no longer a unit vector at all. Any reader that assumes
    /// it is one is wrong about exactly these vertices.
    /// </para>
    /// <para>
    /// The 2D coordinates the enclosing triangle is found in are the vertex's
    /// own grid indices, <c>(x, y)</c> as floats. Not luxel coordinates and not
    /// world positions: the barycentric weights come out of the GRID and are
    /// then applied to world vertices, which works because the tessellation is
    /// a triangulation of the grid.
    /// </para>
    /// </remarks>
    public static void SnapRemainingVertsToSurface(
        CoreDispInfo core, DispInfo info, DisplacementLumpVertexView lumps)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(lumps);

        List<ushort> indices = DispTesselator.Tesselate(core);

        bool[] touched = new bool[core.Size];
        foreach (ushort index in indices)
        {
            touched[index] = true;
        }

        int width = core.PostSpacing;

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (touched[index])
                {
                    continue;
                }

                if (!FindEnclosingTri(new DispUv(x, y), width, indices, out int start,
                        out Barycentric bary))
                {
                    // Stock asserts here and carries on with the vertex left
                    // where it was. Its own comment says this should not happen
                    // unless produced a triangulation that
                    // misses part of the displacement.
                    continue;
                }

                Vec3 a = core.Vert(indices[start + 0]);
                Vec3 b = core.Vert(indices[start + 1]);
                Vec3 c = core.Vert(indices[start + 2]);

                Vec3 newPos = (a * bary.A) + (b * bary.B) + (c * bary.C);
                Vec3 offset = newPos - core.Vert(index);

                int lumpIndex = info.DispVertStart + index;
                DispVert vert = lumps.Verts[lumpIndex];

                lumps.Verts[lumpIndex] = vert with
                {
                    Vector = (vert.Vector * vert.Dist) + offset,
                    Dist = 1.0f,
                };

                core.SetVert(index, newPos);
            }
        }
    }

    /// <summary>
    /// <c>FindEnclosingTri</c>: the first triangle of
    /// the tessellation whose grid coordinates contain a point.
    /// </summary>
    private static bool FindEnclosingTri(
        DispUv vert,
        int width,
        List<ushort> indices,
        out int start,
        out Barycentric barycentric)
    {
        for (int i = 0; i < indices.Count; i += 3)
        {
            Barycentric bary = LightmapSamplePositions.BarycentricCoords2D(
                GridCoord(indices[i + 0], width),
                GridCoord(indices[i + 1], width),
                GridCoord(indices[i + 2], width),
                vert);

            if (bary.A is >= 0.0f and <= 1.0f &&
                bary.B is >= 0.0f and <= 1.0f &&
                bary.C is >= 0.0f and <= 1.0f)
            {
                start = i;
                barycentric = bary;
                return true;
            }
        }

        start = -1;
        barycentric = default;
        return false;
    }

    /// <summary>
    /// One displacement's box for the world bounds:
    /// <c>ComputeDispInfoBounds</c>, as read by
    /// <c>AddDispsToBounds</c>.
    /// </summary>
    /// <param name="disp">The VMF displacement.</param>
    /// <param name="face">Its base face; only the winding and lightmap vectors are read.</param>
    /// <param name="compliance">The compile's compliance.</param>
    /// <returns>The box.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Stock returns <c>GetDispBox</c> (
    ///): the FLAT base quad puffed by 0.1, so the
    /// displacement's height never reaches <c>world_mins</c>/<c>world_maxs</c>.
    /// Correct returns the box of the displaced vertices
    /// (<see cref="CoreDispInfo.RootBounds"/>). See
    /// <see cref="StockQuirk.DispWorldBoundsBaseQuad"/>.
    /// </remarks>
    public static DispBox ComputeDispInfoBounds(
        MapDisplacement disp, DisplacementFace face, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(disp);
        ArgumentNullException.ThrowIfNull(compliance);

        CoreDispInfo core = new(disp.Power);
        DispMapToCoreDispInfo(
            disp, face, core, compliance.Emulates(StockQuirk.DispVertNormalise), withFace: false);

        return compliance.Emulates(StockQuirk.DispWorldBoundsBaseQuad)
            ? DispNeighbourFinder.GetDispBox(core)
            : core.RootBounds;
    }

    /// <summary>
    /// The texinfo a swapped displacement face is repointed at:
    /// </summary>
    /// <param name="original">The face's texinfo.</param>
    /// <returns>
    /// A copy whose lightmap row 0 is the original's row 1, and whose row 1 is
    /// the original's row 0 NEGATED — all four floats, the offset included.
    /// </returns>
    public static TexInfo SwapLightmapAxes(TexInfo original)
    {
        TexInfo swapped = original;

        for (int c = 0; c < 4; c++)
        {
            swapped.LightmapVecsLuxelsPerWorldUnits[c] = original.LightmapVecsLuxelsPerWorldUnits[4 + c];
            swapped.LightmapVecsLuxelsPerWorldUnits[4 + c] = original.LightmapVecsLuxelsPerWorldUnits[c] * -1.0f;
        }

        return swapped;
    }

    /// <summary>
    /// Appends the swapped texinfos and says which texinfo each displacement
    /// face must use: the <c>pSwappedTexInfos</c> bookkeeping of
    /// <c>EmitDispLMAlphaAndNeighbors</c>, and
    /// </summary>
    /// <param name="results">
    /// <see cref="Build"/>'s results; each one's
    /// <see cref="DispInfo.MapFace"/> is its face.
    /// </param>
    /// <param name="faceTexInfos">Each displacement face's CURRENT texinfo index, in the same order.</param>
    /// <param name="texInfos">The texinfo table, appended to.</param>
    /// <returns>The texinfo each displacement's face must now carry.</returns>
    /// <remarks>
    /// <para>
    /// ORDER: stock visits the faces in LUMP_FACES order, not displacement
    /// order, so the appended entries are numbered by face index. One swapped
    /// copy per ORIGINAL texinfo, shared by every swapped face that used it;
    /// unswapped faces keep theirs, and the original stays in the table for
    /// the non-displacement faces that also use it — the d2_prison_08 fix
    /// stock's comment describes.
    /// </para>
    /// <para>
    /// <b>Stock applies these to the map face only</b>, never to the
    /// <c>dface_t</c>, so in a stock BSP the copies are all compacted away (see
    /// <see cref="DisplacementResult.NeedsSwappedTexInfo"/>). Use this only to
    /// reproduce stock's in-memory texinfo table (e.g. for a pass that reads
    /// <c>mapdispinfo_t::face.texinfo</c>, as does for
    /// the surface property — whose texdata the copy shares), not to repoint
    /// LUMP_FACES.
    /// </para>
    /// </remarks>
    public static int[] AssignSwappedTexInfos(
        IReadOnlyList<DisplacementResult> results,
        IReadOnlyList<int> faceTexInfos,
        IList<TexInfo> texInfos)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(faceTexInfos);
        ArgumentNullException.ThrowIfNull(texInfos);

        if (results.Count != faceTexInfos.Count)
        {
            throw new ArgumentException(
                $"{results.Count} results but {faceTexInfos.Count} face texinfos.",
                nameof(faceTexInfos));
        }

        int[] assigned = new int[results.Count];
        int[] order = new int[results.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
            assigned[i] = faceTexInfos[i];
        }

        // Stable, so two displacements on one face (impossible in stock) keep
        // their relative order rather than failing.
        int[] sorted = [.. order.OrderBy(i => results[i].Info.MapFace)];

        Dictionary<int, int> swappedOf = [];

        foreach (int i in sorted)
        {
            if (!results[i].NeedsSwappedTexInfo)
            {
                continue;
            }

            int original = faceTexInfos[i];
            if (!swappedOf.TryGetValue(original, out int copy))
            {
                texInfos.Add(SwapLightmapAxes(texInfos[original]));
                copy = texInfos.Count - 1;
                swappedOf[original] = copy;
            }

            assigned[i] = copy;
        }

        return assigned;
    }

    private static DispUv GridCoord(int index, int width) =>
        new(index % width, index / width);
}

/// <summary>
/// The displacement lump buffers a compile fills.
/// </summary>
/// <remarks>
/// Three growable lists rather than three preallocated arrays.
/// <c>EmitInitialDispInfos</c> counts the totals first and then
/// <c>SetSize</c>s, which it must because it writes through raw pointers into
/// the middle of them; appending gives the same layout because the loop visits
/// the displacements in order either way.
/// </remarks>
public sealed class DisplacementLumps : DisplacementLumpVertexView
{
    /// <summary>LUMP_DISP_TRIS: one tag word per triangle.</summary>
    public List<DispTri> Tris { get; } = [];

    /// <summary>LUMP_DISP_LIGHTMAP_SAMPLE_POSITIONS, a variable-length byte stream.</summary>
    public List<byte> LightmapSamplePositions { get; } = [];
}

/// <summary>
/// The part of <see cref="DisplacementLumps"/> the snap pass edits.
/// </summary>
/// <remarks>
/// Separated so that <see cref="DisplacementLumpBuilder.SnapRemainingVertsToSurface"/>
/// can be driven by a test with a vertex list and nothing else, which is the
/// only way to reach it with a displacement whose tessellation actually drops
/// vertices without also building a whole map.
/// </remarks>
public class DisplacementLumpVertexView
{
    /// <summary>LUMP_DISP_VERTS: one direction, distance and alpha per vertex.</summary>
    public List<DispVert> Verts { get; } = [];
}
