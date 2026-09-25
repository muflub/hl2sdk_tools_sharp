using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// vrad's displacement shadow casters: every triangle of every opaque
/// displacement in the map.
/// </summary>
/// <remarks>
/// <para>
/// <c>StaticDispMgr->AddPolysForRayTrace</c>, -- one
/// line between the sky faces and the static props, and the shortest of the
/// five load paths by a wide margin. There is no filtering to speak of: no
/// per-displacement identity (every triangle carries the bare
/// <see cref="TraceId.Opaque"/>), no alpha, no material index, no
/// <c>FCACHETRI_TRANSPARENT</c>, and coverage fixed at one
/// </para>
/// <para>
/// The work is therefore all in getting the geometry right, and that is
/// <see cref="DisplacementSurface"/>'s job. This class only decides which
/// displacements exist, in which order, and which of them are opaque.
/// </para>
/// <para>
/// SURPRISE WORTH RECORDING: stock builds a <c>CVRADDispColl</c> for every
/// displacement whether or not it ever casts a shadow, complete with an AABB
/// tree, per-vertex normals and luxel coordinates
/// And only then throws the non-opaque ones
/// away one line into <c>AddPolysForRayTrace</c>. That is not wasted work in
/// stock, because the same trees serve the lighting and patch passes; it is
/// worth knowing here so that a future pass which does need those trees does
/// not conclude this class dropped them.
/// </para>
/// </remarks>
public static class DisplacementShadowCasters
{
    /// <summary>
    /// Adds a map's displacement casters to a builder.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="builder">The caster list being filled.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bsp"/> or <paramref name="builder"/> is null.
    /// </exception>
    /// <exception cref="InvalidBspException">
    /// A displacement names no usable base face, or its lump runs are out of
    /// range.
    /// </exception>
    public static void Add(BspData bsp, ShadowCasterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(builder);

        Add(Build(bsp), builder);
    }

    /// <summary>
    /// Adds already-tessellated displacements to a builder.
    /// </summary>
    /// <param name="surfaces">The displacements, in LUMP_DISPINFO order.</param>
    /// <param name="builder">The caster list being filled.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="surfaces"/> or <paramref name="builder"/> is null.
    /// </exception>
    /// <remarks>
    /// Split out from the <see cref="BspData"/> overload so that the emission
    /// rule can be driven with a displacement a real map does not contain --
    /// notably a non-opaque one, which <c>dm_lockdown</c> has none of and which
    /// no fact over a real map can therefore reach.
    /// </remarks>
    public static void Add(IReadOnlyList<DisplacementSurface> surfaces, ShadowCasterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(builder);

        builder.BeginSource(ShadowCasterSource.Displacement);

        for (int i = 0; i < surfaces.Count; i++)
        {
            DisplacementSurface surface = surfaces[i];

            // Per displacement, before the triangle
            // loop: all of it or none of it.
            if (!surface.IsOpaque)
            {
                continue;
            }

            ReadOnlySpan<Vec3> vertices = surface.Vertices;
            ReadOnlySpan<int> indices = surface.TriangleIndices;

            for (int t = 0; t < indices.Length; t += 3)
            {
                builder.AddTriangle(
                    TraceId.Opaque,
                    vertices[indices[t]],
                    vertices[indices[t + 1]],
                    vertices[indices[t + 2]],
                    1.0f);
            }
        }
    }

    /// <summary>
    /// Tessellates every displacement in a map, in LUMP_DISPINFO order.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>
    /// One surface per LUMP_DISPINFO entry, opaque or not, at its own index.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <exception cref="InvalidBspException">
    /// A displacement names no usable base face, or its lump runs are out of
    /// range.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The face-to-displacement direction is stock's:
    /// <c>UnserializeDisps</c> walks the FACES and initialises
    /// <c>builderDisps[pFace->dispinfo]</c>,
    /// rather than walking the displacements and reading
    /// <c>ddispinfo_t::m_iMapFace</c>. The two agree on a well-formed map, but
    /// only the face direction survives a map where they disagree, and it is
    /// also the only direction that can apply <c>ValidDispFace</c>'s
    /// <c>numedges == 4</c> rule at all.
    /// </para>
    /// <para>
    /// LUMP_FACES and not LUMP_FACES_HDR: <c>g_pFaces</c> is switched to the
    /// HDR faces only when vrad is run with <c>-hdr</c> and the map actually
    /// carries a distinct HDR face lump. Displacement geometry is identical in
    /// both, since the lumps differ only in lightmap offsets and styles.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DisplacementSurface> Build(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        ReadOnlySpan<DispInfo> dispInfo = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        if (dispInfo.Length == 0)
        {
            return [];
        }

        ReadOnlySpan<DispVert> dispVerts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);

        int[] faceForDisp = new int[dispInfo.Length];
        Array.Fill(faceForDisp, -1);

        for (int f = 0; f < faces.Length; f++)
        {
            // ValidDispFace. The numedges test is not a formality:
            // the base surface is read as exactly four winding points below,
            // and a displacement face that had been split would silently give
            // four points of a larger polygon.
            DFace face = faces[f];
            if (face.DispInfo == -1 || face.NumEdges != 4)
            {
                continue;
            }

            if (face.DispInfo < 0 || face.DispInfo >= dispInfo.Length)
            {
                throw new InvalidBspException(
                    $"face {f} names displacement {face.DispInfo} of a LUMP_DISPINFO holding "
                    + $"{dispInfo.Length}.");
            }

            faceForDisp[face.DispInfo] = f;
        }

        DisplacementSurface[] surfaces = new DisplacementSurface[dispInfo.Length];
        Span<Vec3> points = stackalloc Vec3[4];

        for (int d = 0; d < dispInfo.Length; d++)
        {
            int faceIndex = faceForDisp[d];
            if (faceIndex < 0)
            {
                // Stock does not survive this either, it just fails later and
                // less legibly: the CCoreDispInfo keeps its zero point count,
                // Create returns false and the return
                // value is dropped, so AABBTree_CopyDispData
                // then reads a null vertex array.
                throw new InvalidBspException(
                    $"displacement {d} has no face with four edges naming it, so it has no base "
                    + "surface to be built on (ValidDispFace).");
            }

            DFace face = faces[faceIndex];
            for (int k = 0; k < 4; k++)
            {
                int se = surfEdges[face.FirstEdge + k];
                points[k] = se < 0 ? vertexes[edges[-se].V[1]] : vertexes[edges[se].V[0]];
            }

            surfaces[d] = DisplacementSurface.Create(
                d, faceIndex, dispInfo[d], points, dispVerts);
        }

        return surfaces;
    }
}
