using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// The brush and sky half of vrad's caster load: <c>trace.cpp:478-652</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two entry points because stock has two, thirty-seven lines apart and with
/// the static prop and displacement managers wedged between them:
/// <see cref="AddBrushEntities"/> is <c>ExtractBrushEntityShadowCasters</c> at
/// <c>vrad.cpp:2240</c> and <see cref="AddWorld"/> is
/// <c>AddBrushesForRayTrace</c> at <c>vrad.cpp:2277</c>. Merging them would be
/// the obvious tidy-up and it would be wrong: a caster triangle has no identity
/// beyond its index in <see cref="ShadowCasterBuilder"/>'s list, so the order
/// the two runs append in is the only thing that makes a recorded comparison
/// against stock possible at all.
/// </para>
/// <para>
/// Everything here is a static function over lumps, with the winding arena
/// created per call. Stock's equivalents read six file-scope arrays and write
/// one file-scope <c>g_RtEnv</c>; this project's rule against mutable statics
/// is what turns those into parameters, and the mechanical consequence is that
/// the whole file can be exercised on a synthetic map with eight brush sides in
/// it, which is how the filters below are tested.
/// </para>
/// </remarks>
public static class BrushShadowCasters
{
    /// <summary>
    /// <c>MASK_OPAQUE</c>, <c>public/bspflags.h:114</c>: everything that blocks
    /// lighting.
    /// </summary>
    /// <remarks>
    /// Narrower than <c>MASK_SOLID</c>, and deliberately so in stock: a
    /// <c>CONTENTS_WINDOW</c> or <c>CONTENTS_GRATE</c> brush stops a player and
    /// does not stop a photon. <c>CONTENTS_MOVEABLE</c> being in the list is the
    /// surprise -- a <c>func_movelinear</c>'s brushes bake their shadow into the
    /// lightmap wherever the compiler found them.
    /// </remarks>
    private const BrushContents MaskOpaque =
        BrushContents.Solid | BrushContents.Moveable | BrushContents.Opaque;

    /// <summary>
    /// Adds every brush of every entity that carries
    /// <c>vrad_brush_cast_shadows</c>, under that entity's own transform.
    /// </summary>
    /// <param name="bsp">The map, for its brush, plane, texinfo, node and leaf lumps.</param>
    /// <param name="entities">The parsed entity lump, in file order.</param>
    /// <param name="builder">Where the triangles go.</param>
    /// <param name="compliance">
    /// The compile's compliance, or null for
    /// <see cref="ComplianceOptions.Correct"/>. Reaches
    /// <see cref="StockQuirk.BaseWindingNormalise"/> through the arena this
    /// method builds.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidBspException">
    /// A brush, side, plane, node or leaf names an index the map does not have.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>ExtractBrushEntityShadowCasters</c>, <c>trace.cpp:578</c>. The key is
    /// read with <c>IntForKey</c>, so any non-zero integer turns it on and a
    /// missing key reads as zero; no classname is required and nothing checks
    /// that the entity is a brush entity at all. An entity whose <c>model</c>
    /// key does not name a submodel simply contributes nothing, because
    /// <c>AddBrushes</c> takes a null model (<c>trace.cpp:563</c>).
    /// </para>
    /// <para>
    /// <see cref="ShadowCasterSource.BrushEntity"/> is begun once here rather
    /// than per entity: the run is contiguous in stock and the builder's
    /// bookkeeping is per source, not per entity.
    /// </para>
    /// </remarks>
    public static void AddBrushEntities(
        BspData bsp,
        IReadOnlyList<BspEntity> entities,
        ShadowCasterBuilder builder,
        ComplianceOptions? compliance = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(builder);

        BrushLumps lumps = new(bsp);
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);

        // StockQuirk.BaseWindingNormalise reaches vrad here: every brush face
        // below starts as a BaseWindingForPlane and is chopped with an epsilon
        // of exactly zero, so the normalise decides which slivers survive.
        WindingArena arena = new()
        {
            Compliance = compliance ?? ComplianceOptions.Correct,
        };

        builder.BeginSource(ShadowCasterSource.BrushEntity);

        for (int i = 0; i < entities.Count; i++)
        {
            BspEntity entity = entities[i];
            if (VmfValue.ParseInt(entity.Get("vrad_brush_cast_shadows")) == 0)
            {
                continue;
            }

            if (!TryBrushmodelForEntity(entity, models, out DModel model))
            {
                continue;
            }

            Vec3 origin = ScanVector(entity.Get("origin"));
            Vec3 angles = ScanVector(entity.Get("angles"));

            // VMatrix::SetupMatrixOrgAngles (vmatrix.cpp:533) is
            // SetupMatrixAnglesInternal plus a translation in column 3, and
            // SetupMatrixAnglesInternal (vmatrix.cpp:510) is element for
            // element the same (YAW * PITCH) * ROLL that AngleMatrix builds --
            // so InstanceTransform.FromAngles IS that matrix, and
            // TransformPoint (VectorTransform) is VMul4x3's
            // Vector3DMultiplyPosition. A QAngle is pitch, yaw, roll in that
            // order, so angles.Y is the yaw.
            InstanceTransform xform = InstanceTransform.FromAngles(angles, origin);
            AddBrushes(in lumps, arena, model, in xform, builder);
        }
    }

    /// <summary>
    /// Adds model 0's brushes and then its sky faces, untransformed.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="useHdrFaces">
    /// Whether this is an HDR run. When it is and the map has an HDR face lump,
    /// the sky faces are read from that lump instead of the LDR one.
    /// </param>
    /// <param name="builder">Where the triangles go.</param>
    /// <param name="compliance">
    /// The compile's compliance, or null for
    /// <see cref="ComplianceOptions.Correct"/>. Reaches
    /// <see cref="StockQuirk.BaseWindingNormalise"/> through the arena this
    /// method builds.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bsp"/> or <paramref name="builder"/> is null.
    /// </exception>
    /// <exception cref="InvalidBspException">
    /// A brush, side, plane, node, leaf, face, edge or vertex index is out of
    /// range, or a sky face has more than
    /// <see cref="WindingArena.MaxPointsOnWinding"/> edges.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>AddBrushesForRayTrace</c>, <c>trace.cpp:595</c>. Model 0 only, so a
    /// <c>func_detail</c>'s brushes are in (vbsp merges them into the world) and
    /// a <c>func_brush</c>'s are not unless it opted in through
    /// <see cref="AddBrushEntities"/>.
    /// </para>
    /// <para>
    /// The <paramref name="useHdrFaces"/> switch is stock's <c>g_pFaces</c>
    /// selection at <c>vrad.cpp:2223-2237</c>, and it has one wrinkle worth
    /// naming: when the run is HDR but the HDR face lump is EMPTY, stock copies
    /// the LDR faces into it and then points at the copy. The two are then
    /// identical, so reading the LDR lump directly -- which is what this does --
    /// gives the same faces without the copy.
    /// </para>
    /// </remarks>
    public static void AddWorld(
        BspData bsp,
        bool useHdrFaces,
        ShadowCasterBuilder builder,
        ComplianceOptions? compliance = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(builder);

        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        if (models.Length == 0)
        {
            // trace.cpp:597, `if ( !nummodels ) return;`. A map with no models
            // has no world and no sky either, so both loops below are skipped
            // rather than only the first.
            return;
        }

        BrushLumps lumps = new(bsp);
        WindingArena arena = new()
        {
            Compliance = compliance ?? ComplianceOptions.Correct,
        };
        DModel world = models[0];

        builder.BeginSource(ShadowCasterSource.WorldBrush);
        InstanceTransform identity = InstanceTransform.Identity;
        AddBrushes(in lumps, arena, world, in identity, builder);

        builder.BeginSource(ShadowCasterSource.Sky);
        AddSkyFaces(bsp, world, useHdrFaces, builder);
    }

    /// <summary>
    /// <c>BrushmodelForEntity</c>, <c>trace.cpp:478</c>.
    /// </summary>
    /// <remarks>
    /// Stock does NOT check for the leading <c>*</c>: it requires a value longer
    /// than one character and then runs <c>atol</c> on the value FROM THE SECOND
    /// CHARACTER ON, whatever the first one was. So <c>"maps/foo.bsp"</c> parses
    /// as <c>atol("aps/foo.bsp")</c> = 0 and is rejected by the range test
    /// rather than by any syntax check, and model 0 -- the world -- is excluded
    /// by the same <c>modelIndex &gt; 0</c>.
    /// </remarks>
    private static bool TryBrushmodelForEntity(
        BspEntity entity,
        ReadOnlySpan<DModel> models,
        out DModel model)
    {
        model = default;

        string name = entity.Get("model") ?? string.Empty;
        if (name.Length <= 1)
        {
            return false;
        }

        int index = VmfValue.ParseInt(name[1..]);
        if (index <= 0 || index >= models.Length)
        {
            return false;
        }

        model = models[index];
        return true;
    }

    /// <summary>
    /// <c>AddBrushes</c>, <c>trace.cpp:563</c>: collect the model's brushes and
    /// add each one.
    /// </summary>
    private static void AddBrushes(
        in BrushLumps lumps,
        WindingArena arena,
        DModel model,
        in InstanceTransform xform,
        ShadowCasterBuilder builder)
    {
        List<int> order = [];
        HashSet<int> seen = [];
        GetBrushes(model.HeadNode, in lumps, order, seen);

        for (int i = 0; i < order.Count; i++)
        {
            AddBrushToRaytraceEnvironment(
                in lumps, arena, lumps.Brush(order[i]), in xform, builder);
        }
    }

    /// <summary>
    /// <c>GetBrushes_r</c>, <c>trace.cpp:536</c>: the brushes of every leaf under
    /// a node, deduplicated, in first-visit order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A brush spans leaves, so the same index arrives many times and stock
    /// rejects the repeats with <c>list.Find(brushIndex) &lt; 0</c> -- a linear
    /// scan of everything added so far, which makes the whole walk quadratic in
    /// the brush count. The hash set beside the list here answers the same
    /// question in constant time; the LIST is what is iterated afterwards,
    /// because first-insertion order is the triangle order and a set has none.
    /// </para>
    /// <para>
    /// Child 0 before child 1, and the leaf's brushes in leafbrush-lump order
    /// within a leaf. Both are load-bearing for the same reason.
    /// </para>
    /// </remarks>
    private static void GetBrushes(int node, in BrushLumps lumps, List<int> order, HashSet<int> seen)
    {
        if (node < 0)
        {
            int leaf = -1 - node;
            if ((uint)leaf >= (uint)lumps.LeafCount)
            {
                throw new InvalidBspException(
                    $"a node's child names leaf {leaf}, and the map has {lumps.LeafCount}");
            }

            int first = lumps.LeafFirstBrush[leaf];
            int count = lumps.LeafNumBrushes[leaf];
            for (int i = 0; i < count; i++)
            {
                int slot = first + i;
                if ((uint)slot >= (uint)lumps.LeafBrushes.Length)
                {
                    throw new InvalidBspException(
                        $"leaf {leaf} names leafbrush {slot}, and the map has "
                        + $"{lumps.LeafBrushes.Length}");
                }

                int brush = lumps.LeafBrushes[slot];
                if (seen.Add(brush))
                {
                    order.Add(brush);
                }
            }

            return;
        }

        if ((uint)node >= (uint)lumps.Nodes.Length)
        {
            throw new InvalidBspException(
                $"a model or node names node {node}, and the map has {lumps.Nodes.Length}");
        }

        DNode branch = lumps.Nodes[node];
        GetBrushes(branch.Children[0], in lumps, order, seen);
        GetBrushes(branch.Children[1], in lumps, order, seen);
    }

    /// <summary>
    /// <c>AddBrushToRaytraceEnvironment</c>, <c>trace.cpp:492</c>: one brush as a
    /// triangle soup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The winding for a side is built BEFORE the sky and displacement tests,
    /// which is why it is built before them here: <c>BaseWindingForPlane</c>
    /// fails loudly on a plane with no major axis, so moving the tests up would
    /// make a map with a degenerate sky-side plane compile here and not in
    /// stock. Stock then <c>continue</c>s without freeing the winding
    /// (<c>trace.cpp:504</c>) -- a real leak of one winding per skipped side,
    /// not reproduced, because the arena's free list is the reason the port does
    /// not allocate per side at all.
    /// </para>
    /// <para>
    /// The clip loop skips <c>bevel</c> sides and clips against every other
    /// side's OPPOSITE plane (<c>planenum ^ 1</c>), with an epsilon of exactly
    /// zero. Zero, not <c>ON_EPSILON</c>: sliver sides therefore survive as
    /// zero-area triangles instead of being clipped away, and the builder keeps
    /// them for the same reason it keeps any other triangle.
    /// </para>
    /// <para>
    /// A brush side may carry <c>texinfo == -1</c> -- 542 of
    /// <c>dm_lockdown</c>'s 15,653 do -- and stock reads <c>texinfo[-1]</c>,
    /// which is the 72 bytes in front of a <c>CUtlVector</c>'s heap allocation.
    /// That is undefined behaviour rather than a rule, so it cannot be ported;
    /// this treats a negative texinfo as carrying no flags, and the world-brush
    /// triangle count on that map is the gate that says the two agree.
    /// </para>
    /// </remarks>
    private static void AddBrushToRaytraceEnvironment(
        in BrushLumps lumps,
        WindingArena arena,
        DBrush brush,
        in InstanceTransform xform,
        ShadowCasterBuilder builder)
    {
        if (((BrushContents)brush.Contents & MaskOpaque) == 0)
        {
            return;
        }

        for (int i = 0; i < brush.NumSides; i++)
        {
            DBrushSide side = lumps.Side(brush, i);
            DPlane plane = lumps.Plane(side.PlaneNum);

            Winding winding = arena.BaseWindingForPlane(plane.Normal, plane.Dist);

            if (lumps.IsSky(side.TexInfo) || side.DispInfo != 0)
            {
                arena.Free(winding);
                continue;
            }

            for (int j = 0; j < brush.NumSides && !winding.IsNull; j++)
            {
                if (i == j)
                {
                    continue;
                }

                DBrushSide other = lumps.Side(brush, j);
                if (other.Bevel != 0)
                {
                    continue;
                }

                DPlane back = lumps.Plane(other.PlaneNum ^ 1);
                winding = arena.ChopInPlace(winding, back.Normal, back.Dist, 0f);
            }

            if (winding.IsNull)
            {
                continue;
            }

            Span<Vec3> points = arena.Points(winding);
            for (int j = 2; j < points.Length; j++)
            {
                builder.AddTriangle(
                    TraceId.Opaque,
                    xform.TransformPoint(points[0]),
                    xform.TransformPoint(points[j - 1]),
                    xform.TransformPoint(points[j]),
                    1f);
            }

            arena.Free(winding);
        }
    }

    /// <summary>
    /// The second loop of <c>AddBrushesForRayTrace</c>, <c>trace.cpp:612-651</c>:
    /// model 0's <c>SURF_SKY</c> faces as <see cref="TraceId.Sky"/> triangles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Faces, not brush sides, and that asymmetry is stock's: the opaque half
    /// above walks brushes and rejects sky sides, and this half walks faces and
    /// keeps only sky ones, so a sky surface is added exactly once and as
    /// geometry that was tessellated by vbsp rather than clipped here.
    /// </para>
    /// <para>
    /// There is no displacement test. A displaced sky face would be added as its
    /// flat base polygon; nothing in this branch produces one, and the omission
    /// is stock's rather than an oversight to fix.
    /// </para>
    /// </remarks>
    private static void AddSkyFaces(
        BspData bsp,
        DModel world,
        bool useHdrFaces,
        ShadowCasterBuilder builder)
    {
        // g_pFaces, vrad.cpp:2223. The HDR lump is used only when the run is HDR
        // AND that lump has faces in it; stock's "copy the LDR faces into it"
        // branch makes the empty case identical to reading the LDR lump.
        BspLumpData faceLump = useHdrFaces && !bsp[BspLump.FacesHdr].IsEmpty
            ? bsp[BspLump.FacesHdr]
            : bsp[BspLump.Faces];

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(faceLump);
        ReadOnlySpan<TexInfo> texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<Vec3> vertexes = BspStructView.As<Vec3>(bsp[BspLump.Vertexes]);
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);

        Span<Vec3> points = stackalloc Vec3[WindingArena.MaxPointsOnWinding];

        for (int i = 0; i < world.NumFaces; i++)
        {
            int index = world.FirstFace + i;
            if ((uint)index >= (uint)faces.Length)
            {
                throw new InvalidBspException(
                    $"model 0 names face {index}, and the face lump has {faces.Length}");
            }

            DFace face = faces[index];
            if ((uint)face.TexInfo >= (uint)texInfo.Length)
            {
                throw new InvalidBspException(
                    $"face {index} names texinfo {face.TexInfo}, and the map has "
                    + $"{texInfo.Length}");
            }

            if (((SurfaceFlags)texInfo[face.TexInfo].Flags & SurfaceFlags.Sky) == 0)
            {
                continue;
            }

            for (int j = 0; j < face.NumEdges; j++)
            {
                if (j >= WindingArena.MaxPointsOnWinding)
                {
                    throw new InvalidBspException(
                        $"face {index} has {face.NumEdges} edges; stock calls this "
                        + "\"***** ERROR! MAX_POINTS_ON_WINDING reached!\" (trace.cpp:624)");
                }

                int slot = face.FirstEdge + j;
                if ((uint)slot >= (uint)surfEdges.Length)
                {
                    throw new InvalidBspException(
                        $"face {index} names surfedge {slot}, and the map has "
                        + $"{surfEdges.Length}");
                }

                // trace.cpp:634. The sign of a surfedge says which END of the
                // edge the face walks into, so a negative one takes v[1] of the
                // edge at the NEGATED index -- reversing the edge rather than
                // indexing backwards.
                int surfEdge = surfEdges[slot];
                int edge = surfEdge < 0 ? -surfEdge : surfEdge;
                if ((uint)edge >= (uint)edges.Length)
                {
                    throw new InvalidBspException(
                        $"surfedge {slot} names edge {edge}, and the map has {edges.Length}");
                }

                ushort vertex = surfEdge < 0 ? edges[edge].V[1] : edges[edge].V[0];
                if (vertex >= vertexes.Length)
                {
                    throw new InvalidBspException(
                        $"edge {edge} names vertex {vertex}, and the map has {vertexes.Length}");
                }

                points[j] = vertexes[vertex];
            }

            for (int j = 2; j < face.NumEdges; j++)
            {
                builder.AddTriangle(
                    TraceId.Sky, points[0], points[j - 1], points[j], 1f);
            }
        }
    }

    /// <summary>
    /// <c>GetVectorForKey</c> / <c>GetAnglesForKey</c>, <c>bsplib.cpp:3193</c>
    /// and <c>:3218</c>: the two are the same function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>sscanf(k, "%lf %lf %lf", ...)</c> onto three pre-zeroed doubles, so a
    /// value with fewer than three numbers keeps zeroes for the rest and a value
    /// that stops being numeric stops the whole scan -- <c>"10 x 30"</c> is
    /// <c>(10, 0, 0)</c>, not <c>(10, 0, 30)</c>. That is why this is not
    /// <see cref="VmfValue.TryParseVector3"/>, whose format is the BRACKETED
    /// <c>[%f %f %f]</c> of the VMF reader and a different thing entirely.
    /// </para>
    /// <para>
    /// A private copy rather than a call: <see cref="BspEntity"/> lives in
    /// MapFormats and has only <c>Get</c>, and the one existing port of this
    /// scan is private to <c>MapEntity</c>, which is the VMF-side entity and not
    /// what vrad reads.
    /// </para>
    /// </remarks>
    private static Vec3 ScanVector(string? value)
    {
        Span<double> parts = [0d, 0d, 0d];

        if (value is not null)
        {
            int position = 0;
            for (int i = 0; i < 3 && TryScanDouble(value, ref position, out parts[i]); i++)
            {
                // The loop's work is the scan itself; a failed field stops it.
            }
        }

        return new Vec3((float)parts[0], (float)parts[1], (float)parts[2]);
    }

    /// <summary>
    /// One <c>%lf</c> field: <c>strtod</c>'s prefix scan, reporting where it
    /// stopped so the next field starts there.
    /// </summary>
    private static bool TryScanDouble(string text, ref int position, out double value)
    {
        value = 0d;

        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        int start = position;

        if (position < text.Length && (text[position] == '+' || text[position] == '-'))
        {
            position++;
        }

        int digits = 0;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
            digits++;
        }

        if (position < text.Length && text[position] == '.')
        {
            position++;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
                digits++;
            }
        }

        if (digits == 0)
        {
            position = start;
            return false;
        }

        int beforeExponent = position;
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            int exponentDigits = 0;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
                exponentDigits++;
            }

            // "1e" is not a double: strtod backs up over the exponent marker
            // and converts the mantissa alone.
            if (exponentDigits == 0)
            {
                position = beforeExponent;
            }
        }

        value = double.Parse(
            text[start..position],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// The lumps <c>trace.cpp</c>'s brush walk reads, as spans rather than the
    /// six file-scope arrays stock reaches for.
    /// </summary>
    /// <remarks>
    /// A <c>ref struct</c> so the spans need no copy, and the per-leaf brush
    /// ranges are flattened into two <c>int</c> arrays because
    /// <c>LUMP_LEAFS</c> has two layouts: version 0 is
    /// <see cref="DLeafVersion0"/> at 56 bytes with the ambient cube inline, and
    /// anything later is <see cref="DLeaf"/> at 32. This is not hypothetical --
    /// <c>dm_lockdown.bsp</c> is BSP 19 with a version 0 leaf lump, so the older
    /// one is the branch a committed map in this tree actually takes.
    /// </remarks>
    private readonly ref struct BrushLumps
    {
        public BrushLumps(BspData bsp)
        {
            Brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
            Sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
            Planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
            TexInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
            Nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
            LeafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]);

            if (bsp[BspLump.Leafs].Version == 0)
            {
                ReadOnlySpan<DLeafVersion0> leaves =
                    BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]);
                LeafFirstBrush = new int[leaves.Length];
                LeafNumBrushes = new int[leaves.Length];
                for (int i = 0; i < leaves.Length; i++)
                {
                    LeafFirstBrush[i] = leaves[i].FirstLeafBrush;
                    LeafNumBrushes[i] = leaves[i].NumLeafBrushes;
                }
            }
            else
            {
                ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
                LeafFirstBrush = new int[leaves.Length];
                LeafNumBrushes = new int[leaves.Length];
                for (int i = 0; i < leaves.Length; i++)
                {
                    LeafFirstBrush[i] = leaves[i].FirstLeafBrush;
                    LeafNumBrushes[i] = leaves[i].NumLeafBrushes;
                }
            }
        }

        /// <summary><c>dbrushes</c>.</summary>
        public ReadOnlySpan<DBrush> Brushes { get; }

        /// <summary><c>dbrushsides</c>.</summary>
        public ReadOnlySpan<DBrushSide> Sides { get; }

        /// <summary><c>dplanes</c>.</summary>
        public ReadOnlySpan<DPlane> Planes { get; }

        /// <summary><c>texinfo</c>.</summary>
        public ReadOnlySpan<TexInfo> TexInfos { get; }

        /// <summary><c>dnodes</c>.</summary>
        public ReadOnlySpan<DNode> Nodes { get; }

        /// <summary><c>dleafbrushes</c>.</summary>
        public ReadOnlySpan<ushort> LeafBrushes { get; }

        /// <summary>Each leaf's <c>firstleafbrush</c>, whichever layout the lump has.</summary>
        public int[] LeafFirstBrush { get; }

        /// <summary>Each leaf's <c>numleafbrushes</c>.</summary>
        public int[] LeafNumBrushes { get; }

        /// <summary>How many leaves the map has.</summary>
        public int LeafCount => LeafFirstBrush.Length;

        /// <summary>One brush, by index.</summary>
        public DBrush Brush(int index) => (uint)index < (uint)Brushes.Length
            ? Brushes[index]
            : throw new InvalidBspException(
                $"a leaf names brush {index}, and the map has {Brushes.Length}");

        /// <summary>One side of a brush, by the side's ordinal within it.</summary>
        public DBrushSide Side(DBrush brush, int ordinal)
        {
            int index = brush.FirstSide + ordinal;
            return (uint)index < (uint)Sides.Length
                ? Sides[index]
                : throw new InvalidBspException(
                    $"a brush names side {index}, and the map has {Sides.Length}");
        }

        /// <summary>One plane, by index.</summary>
        public DPlane Plane(int index) => (uint)index < (uint)Planes.Length
            ? Planes[index]
            : throw new InvalidBspException(
                $"a brush side names plane {index}, and the map has {Planes.Length}");

        /// <summary>Whether a brush side's texinfo carries <c>SURF_SKY</c>.</summary>
        /// <remarks>
        /// A negative index reads as "no flags" -- see the remarks on
        /// <see cref="AddBrushToRaytraceEnvironment"/> for why stock cannot be
        /// followed here.
        /// </remarks>
        public bool IsSky(short texInfo) => (uint)texInfo < (uint)TexInfos.Length
            && ((SurfaceFlags)TexInfos[texInfo].Flags & SurfaceFlags.Sky) != 0;
    }
}
