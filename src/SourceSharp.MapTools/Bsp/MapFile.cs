using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One loaded VMF: <c>CMapFile</c>.
/// </summary>
/// <remarks>
/// <para>
/// The main map is one of these and so is every <c>func_instance</c>, which is
/// the whole reason stock made <c>CMapFile</c> a class in the first place: an
/// instance is loaded into its own plane table, brush array and entity array
/// and then merged into the main map's (<c>CMapFile::MergeInstance</c>,
///). Nothing here is static. Stock kept
/// <c>m_InstancePath</c>, <c>m_InstanceCount</c> and <c>c_areaportals</c> as
/// class statics — shared across every map in the
/// process — and those three live on <see cref="VbspContext"/> instead, which
/// is what makes two compiles in one process possible.
/// </para>
/// <para>
/// <b>Indices are the model.</b> Sides are addressed by their position in
/// <see cref="BrushSides"/>, brushes by theirs in <see cref="Brushes"/>, and
/// entities by theirs in <see cref="Entities"/>, because stock's pointer
/// arithmetic over fixed arrays makes those positions load-bearing: a brush's
/// sides are a RANGE, <see cref="SideBrushTextures"/> is parallel to
/// <see cref="BrushSides"/>, and brushes are re-ordered in place by
/// <see cref="MoveBrushesToWorld"/> so that each entity's brushes stay
/// contiguous. Nothing is ever removed from any of the three.
/// </para>
/// </remarks>
public sealed class MapFile
{
    /// <summary>
    /// The format's brush ceiling: <c>MAX_MAP_BRUSHES</c>,
    /// </summary>
    public const int MaxMapBrushes = 8192;

    /// <summary>
    /// The format's side ceiling: <c>MAX_MAP_BRUSHSIDES</c>,
    /// </summary>
    public const int MaxMapBrushSides = 65536;

    /// <summary>
    /// The format's entity ceiling: <c>MAX_MAP_ENTITIES</c>,
    /// </summary>
    public const int MaxMapEntities = 8192;

    /// <summary>
    /// The clip epsilon <c>MakeBrushWindings</c> chops with:
    /// <c>BRUSH_CLIP_EPSILON</c>.
    /// </summary>
    /// <remarks>
    /// <c>0.01f</c>, and stock's own comment says it "should probably be the
    /// same as clip epsilon, but it is 0.1f and I currently don't know how that
    /// number was come to". So the two epsilons differ by a factor of ten on
    /// purpose-by-accident, and both are kept.
    /// </remarks>
    public const float BrushClipEpsilon = 0.01f;

    /// <summary>
    /// What <c>ClearBounds</c> puts in a minimum: 99999,
    /// </summary>
    /// <remarks>
    /// Not <see cref="float.MaxValue"/>, and the difference is observable:
    /// <c>LoadMapFile</c> skips a brush from the map bounds when its
    /// <c>mins[0] &gt; MAX_COORD_INTEGER</c>, and
    /// <c>MakeBrushWindings</c> reports "no visible sides on brush" on the same
    /// test — so this value is compared against, not just overwritten.
    /// </remarks>
    public static Vec3 ClearedMins => new(99999f, 99999f, 99999f);

    /// <summary>
    /// What <c>ClearBounds</c> puts in a maximum: -99999.
    /// </summary>
    public static Vec3 ClearedMaxs => new(-99999f, -99999f, -99999f);

    private readonly List<MapBrushSide> _brushSides = [];
    private readonly List<BrushTexture> _sideBrushTextures = [];

    /// <summary>Creates an empty map over a winding arena.</summary>
    /// <param name="windings">
    /// Where side windings are allocated. Shared with the map this one is
    /// merged into, because a merged side keeps the winding it was given.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="windings"/> is null.</exception>
    public MapFile(WindingArena windings)
    {
        ArgumentNullException.ThrowIfNull(windings);
        Windings = windings;
    }

    /// <summary>The arena this map's side windings live in.</summary>
    public WindingArena Windings { get; }

    /// <summary>
    /// Whether <see cref="AddBrushBevels"/> normalises the way stock does.
    /// </summary>
    /// <remarks>
    /// Taken from the arena rather than carried separately, because the two
    /// normalise quirks are two halves of one answer: the PLANES lump matches
    /// stock only when <see cref="StockQuirk.BaseWindingNormalise"/> and
    /// <see cref="StockQuirk.EdgeBevelNormalise"/> agree, and a map whose
    /// windings were built one way and whose bevels were built the other
    /// matches neither tool.
    /// </remarks>
    private bool StockEdgeBevels => Windings.Compliance.Emulates(StockQuirk.EdgeBevelNormalise);

    /// <summary>The plane table: <c>mapplanes</c> and <c>nummapplanes</c>.</summary>
    public PlaneTable Planes { get; } = new();

    /// <summary>The entities, in file order: <c>entities</c>.</summary>
    public List<MapEntity> Entities { get; } = [];

    /// <summary>The brushes: <c>mapbrushes</c>.</summary>
    /// <remarks>
    /// Sized to <see cref="BrushCount"/> and no further. Stock's array holds
    /// slots past <c>nummapbrushes</c> that a discarded brush left behind; here
    /// a discarded brush is simply never added, which is the same thing seen
    /// from the committed side.
    /// </remarks>
    public List<MapBrush> Brushes { get; } = [];

    /// <summary>How many brushes are committed: <c>nummapbrushes</c>.</summary>
    public int BrushCount => Brushes.Count;

    /// <summary>The brush sides: <c>brushsides</c>.</summary>
    public IReadOnlyList<MapBrushSide> BrushSides => _brushSides;

    /// <summary>How many sides are committed: <c>nummapbrushsides</c>.</summary>
    public int BrushSideCount => _brushSides.Count;

    /// <summary>
    /// Each side's texture placement, parallel to <see cref="BrushSides"/>:
    /// <c>side_brushtextures</c>.
    /// </summary>
    /// <remarks>
    /// Kept so an origin brush found later in the same entity can rebuild every
    /// texinfo from the original placement. Parallel by
    /// construction: <see cref="AddBrushSide"/> is the only thing that appends
    /// to either.
    /// </remarks>
    public IReadOnlyList<BrushTexture> SideBrushTextures => _sideBrushTextures;

    /// <summary>The map's bounding box minimum: <c>map_mins</c>.</summary>
    public Vec3 Mins { get; set; }

    /// <summary>The map's bounding box maximum: <c>map_maxs</c>.</summary>
    public Vec3 Maxs { get; set; }

    /// <summary>How many box bevels were added: <c>c_boxbevels</c>.</summary>
    public int BoxBevels { get; private set; }

    /// <summary>How many edge bevels were added: <c>c_edgebevels</c>.</summary>
    public int EdgeBevels { get; private set; }

    /// <summary>How many clip brushes were found: <c>c_clipbrushes</c>.</summary>
    public int ClipBrushes { get; set; }

    /// <summary>
    /// The texinfo of the first clip brush side seen: <c>g_ClipTexinfo</c>.
    /// </summary>
    /// <remarks>
    /// -1 until a world clip brush is loaded. The clip brush's own sides are
    /// then set to <see cref="TexInfoTable.TexInfoNode"/>, so this is the only
    /// surviving record of what texinfo a clip surface had.
    /// </remarks>
    public int ClipTexInfo { get; set; } = -1;

    /// <summary>
    /// The output pairs the <c>connections</c> chunk produced, newest first:
    /// <c>m_ConnectionPairs</c>.
    /// </summary>
    /// <remarks>
    /// Stock builds a singly-linked stack, so walking it visits the pairs in
    /// reverse of the order they were parsed. Instance merging walks it twice
    /// And the second walk rewrites the
    /// values, so the order is observable and this list is in stock's order,
    /// newest first.
    /// </remarks>
    public List<MapKeyValue> ConnectionPairs { get; } = [];

    /// <summary>
    /// The entity numbers of the map's <c>func_viscluster</c> entities.
    /// </summary>
    /// <remarks>
    /// <c>AddVisCluster</c> keeps these for the portal
    /// file rather than emitting them to the BSP. The visibility lane consumes
    /// the list; the loader's job is to notice them and not blank them.
    /// </remarks>
    public List<int> VisClusterEntities { get; } = [];

    /// <summary>
    /// The entity numbers of the map's <c>info_overlay</c> entities.
    /// </summary>
    /// <remarks>
    /// Stock parses each into <c>g_aMapOverlays</c> and rewrites the entity as
    /// an <c>info_overlay_accessor</c>. Overlays are
    /// a later lane's; recording the entity numbers here means that lane has
    /// the list and the loader drops nothing silently.
    /// </remarks>
    public List<int> OverlayEntities { get; } = [];

    /// <summary>
    /// The <c>overlaydata</c> chunks of every <c>overlaytransition</c> in the
    /// map, in file order: the water overlays.
    /// </summary>
    /// <remarks>
    /// Stock reads each into <c>g_aMapWaterOverlays</c> while the entity is
    /// loaded (<c>LoadOverlayTransitionCallback</c>,
    /// registered for every <c>world</c> and <c>entity</c> chunk at
    ///). The chunks are kept whole because the overlay stage, not
    /// the loader, parses them; before this list the stage needed the VMF
    /// document passed in beside the map.
    /// </remarks>
    public List<SourceSharp.MapFormats.Text.VmfChunk> WaterOverlayData { get; } = [];

    /// <summary>
    /// The brush side ids named by <c>info_no_dynamic_shadow</c> entities:
    /// <c>g_NoDynamicShadowSides</c>.
    /// </summary>
    public List<int> NoDynamicShadowSides { get; } = [];

    /// <summary>
    /// The overlay count when this map started loading:
    /// <c>m_StartMapOverlays</c>.
    /// </summary>
    public int StartMapOverlays { get; set; }

    /// <summary>
    /// The water overlay count when this map started loading:
    /// <c>m_StartMapWaterOverlays</c>.
    /// </summary>
    public int StartMapWaterOverlays { get; set; }

    /// <summary>Appends a side and its placement, keeping the two parallel.</summary>
    /// <param name="side">The side.</param>
    /// <param name="texture">The side's placement.</param>
    /// <returns>The side's index in <see cref="BrushSides"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="side"/> is null.</exception>
    /// <exception cref="MapCompileException">The side array is full.</exception>
    public int AddBrushSide(MapBrushSide side, BrushTexture texture)
    {
        ArgumentNullException.ThrowIfNull(side);

        if (_brushSides.Count == MaxMapBrushSides)
        {
            throw new MapCompileException("MAX_MAP_BRUSHSIDES");
        }

        _brushSides.Add(side);
        _sideBrushTextures.Add(texture);
        return _brushSides.Count - 1;
    }

    /// <summary>Replaces a side's stored placement.</summary>
    /// <param name="index">The side's index.</param>
    /// <param name="texture">The placement.</param>
    public void SetSideBrushTexture(int index, BrushTexture texture) =>
        _sideBrushTextures[index] = texture;

    /// <summary>
    /// <c>AddPointToBounds</c>: strict
    /// <c>&lt;</c> and <c>&gt;</c>, so the FIRST of two equal values stays.
    /// </summary>
    /// <param name="v">The point.</param>
    /// <param name="mins">The running minimum.</param>
    /// <param name="maxs">The running maximum.</param>
    /// <remarks>
    /// Not <c>MathF.Min</c>/<c>Max</c>: IEEE 754-2019 minimum orders -0 below
    /// +0, so <c>MathF.Min(+0, -0)</c> is -0 where stock keeps the +0 it saw
    /// first. The bound's sign of zero reaches LUMP_MODELS through
    /// <c>BeginModel</c>; it made l0_micro_brush's and x0_corner_cut's model
    /// mins differ from stock's in the sign bit alone.
    /// </remarks>
    public static void AddPointToBounds(Vec3 v, ref Vec3 mins, ref Vec3 maxs)
    {
        float minX = mins.X, minY = mins.Y, minZ = mins.Z;
        float maxX = maxs.X, maxY = maxs.Y, maxZ = maxs.Z;

        if (v.X < minX) { minX = v.X; }
        if (v.X > maxX) { maxX = v.X; }
        if (v.Y < minY) { minY = v.Y; }
        if (v.Y > maxY) { maxY = v.Y; }
        if (v.Z < minZ) { minZ = v.Z; }
        if (v.Z > maxZ) { maxZ = v.Z; }

        mins = new Vec3(minX, minY, minZ);
        maxs = new Vec3(maxX, maxY, maxZ);
    }

    /// <summary>
    /// Builds every side's winding and the brush's bounds:
    /// <c>MakeBrushWindings</c>.
    /// </summary>
    /// <param name="brush">The brush.</param>
    /// <param name="diagnostics">Where out-of-range bounds are reported, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Each side starts as the huge base winding for its plane and is chopped
    /// by the OPPOSITE of every other non-bevel side's plane
    /// (<c>mapplanes[planenum ^ 1]</c>) — which is only a
    /// single array lookup because the plane table stores pairs.
    /// </para>
    /// <para>
    /// The chop takes <see cref="BrushClipEpsilon"/> rather than zero, and
    /// stock's comment says why: "adding an epsilon here, due to precision
    /// issues creating complex displacement surfaces". The commented-out
    /// zero-epsilon line is still present in the reference chain.
    /// </para>
    /// <para>
    /// A side that chops away to nothing keeps <see cref="Winding.Null"/> and
    /// stays NOT visible, and its points contribute no bounds — so a brush all
    /// of whose sides vanish keeps the cleared bounds, which is exactly the
    /// "no visible sides on brush" message the loop below reports.
    /// </para>
    /// </remarks>
    public void MakeBrushWindings(MapBrush brush, ICollection<CompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(brush);

        Vec3 mins = ClearedMins;
        Vec3 maxs = ClearedMaxs;

        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = _brushSides[brush.FirstSide + i];
            Plane plane = Planes[side.PlaneNumber];
            Winding w = Windings.BaseWindingForPlane(plane.Normal, plane.Dist);

            for (int j = 0; j < brush.SideCount && !w.IsNull; j++)
            {
                if (i == j)
                {
                    continue;
                }

                MapBrushSide other = _brushSides[brush.FirstSide + j];
                if (other.Bevel)
                {
                    continue;
                }

                Plane back = Planes[other.PlaneNumber ^ 1];
                w = Windings.ChopInPlace(w, back.Normal, back.Dist, BrushClipEpsilon);
            }

            side.Winding = w;

            if (w.IsNull)
            {
                continue;
            }

            side.Visible = true;

            foreach (Vec3 point in Windings.Points(w))
            {
                AddPointToBounds(point, ref mins, ref maxs);
            }
        }

        brush.Mins = mins;
        brush.Maxs = maxs;

        for (int i = 0; i < 3; i++)
        {
            if (mins[i] < GeometryEpsilons.MinCoordInteger || maxs[i] > GeometryEpsilons.MaxCoordInteger)
            {
                diagnostics?.Add(new CompileDiagnostic(
                    MapLoadDiagnostics.BrushBoundsOutOfRange,
                    DiagnosticSeverity.Warning,
                    $"Brush {brush.Id}: bounds out of range",
                    new MapLocation(BrushId: brush.Id)));
            }

            if (mins[i] > GeometryEpsilons.MaxCoordInteger || maxs[i] < GeometryEpsilons.MinCoordInteger)
            {
                diagnostics?.Add(new CompileDiagnostic(
                    MapLoadDiagnostics.BrushHasNoVisibleSides,
                    DiagnosticSeverity.Warning,
                    $"Brush {brush.Id}: no visible sides on brush",
                    new MapLocation(BrushId: brush.Id)));
            }
        }
    }

    /// <summary>
    /// Adds the axial and edge bevel planes a brush needs to be expanded
    /// against a bounding box: <c>AddBrushBevels</c>,
    /// </summary>
    /// <param name="brush">The brush, which must be the most recently added.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    /// <exception cref="MapCompileException">The side array is full.</exception>
    /// <remarks>
    /// <para>
    /// <b>Only valid on the brush being loaded.</b> New sides are appended at
    /// <c>original_sides[numsides]</c>, which is the next free slot of the
    /// shared side array — true only while this brush is the last one. Stock
    /// has the same precondition and does not state it; calling this on an
    /// older brush would overwrite a later brush's sides.
    /// </para>
    /// <para>
    /// The first half puts the six axial planes in canonical order
    /// (-X, +X, -Y, +Y, -Z, +Z), SWAPPING existing sides into place — and
    /// swapping <see cref="SideBrushTextures"/> in step, because the two are
    /// parallel arrays and stock swaps both. A brush
    /// with six axial sides returns here.
    /// </para>
    /// <para>
    /// The second half walks every non-axial edge and adds a bevel for each of
    /// the six slanted axials that is outside the hull. Its "already used"
    /// test uses 0.01 for BOTH epsilons rather than the plane table's
    /// (1e-5, 0.01) — stock's comment: "Use a larger tolerance for collision
    /// planes than for rendering planes".
    /// </para>
    /// </remarks>
    public void AddBrushBevels(MapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        int order = 0;

        for (int axis = 0; axis < 3; axis++)
        {
            for (int dir = -1; dir <= 1; dir += 2, order++)
            {
                int i = 0;
                for (; i < brush.SideCount; i++)
                {
                    if (Planes[_brushSides[brush.FirstSide + i].PlaneNumber].Normal[axis] == dir)
                    {
                        break;
                    }
                }

                if (i == brush.SideCount)
                {
                    Vec3 normal = AxisVector(axis, dir);
                    float dist = dir == 1 ? brush.Maxs[axis] : -brush.Mins[axis];

                    MapBrushSide first = _brushSides[brush.FirstSide];
                    MapBrushSide bevel = new()
                    {
                        PlaneNumber = Planes.Find(normal, dist),
                        TexInfo = first.TexInfo,
                        Contents = first.Contents,
                        Bevel = true,
                    };

                    AddBrushSide(bevel, default);
                    brush.SideCount++;
                    BoxBevels++;
                }

                if (i != order)
                {
                    Swap(brush.FirstSide + order, brush.FirstSide + i);
                }
            }
        }

        if (brush.SideCount == 6)
        {
            return;
        }

        for (int i = 6; i < brush.SideCount; i++)
        {
            MapBrushSide side = _brushSides[brush.FirstSide + i];
            Winding w = side.Winding;

            if (w.IsNull)
            {
                continue;
            }

            for (int j = 0; j < w.Count; j++)
            {
                int next = (j + 1) % w.Count;
                Vec3 edge = Windings.Points(w)[j] - Windings.Points(w)[next];

                // StockQuirk.EdgeBevelNormalise. VectorNormalize,
                // so the estimate and not a divide -- and its return is
                // sqrlen*invlen rather than the length, which is what the
                // 0.5 test sees.
                (Vec3 unit, float length) = StockEdgeBevels
                    ? edge.NormaliseLikeStock()
                    : edge.Normalise();

                if (length < 0.5f)
                {
                    continue;
                }

                _ = Plane.TrySnapNormal(unit, out unit);

                int k = 0;
                for (; k < 3; k++)
                {
                    if (unit[k] == -1f || unit[k] == 1f)
                    {
                        break;
                    }
                }

                if (k != 3)
                {
                    // Axial edges are already covered by the box bevels.
                    continue;
                }

                AddEdgeBevels(brush, w, j, unit);
            }
        }
    }

    /// <summary>
    /// Moves an entity's brushes into worldspawn, keeping every entity's
    /// brushes contiguous: <c>MoveBrushesToWorld</c>,
    /// </summary>
    /// <param name="entity">The entity whose brushes move.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    /// <remarks>
    /// <b>Only valid while the entity is the one being loaded.</b> Stock says
    /// so in capitals and the reason is the shift below:
    /// it assumes the moving brushes are at the END of the array, so every
    /// other entity's first brush moves up by the same amount. Brush NUMBERS
    /// are not preserved and stock keeps them deliberately
    /// (is <c>#if 0</c>'d with the comment "let them
    /// keep their original brush numbers").
    /// </remarks>
    public void MoveBrushesToWorld(MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        int newBrushes = entity.BrushCount;
        int worldBrushes = Entities[0].BrushCount;

        List<MapBrush> moved = Brushes.GetRange(entity.FirstBrush, newBrushes);
        Brushes.RemoveRange(entity.FirstBrush, newBrushes);
        Brushes.InsertRange(worldBrushes, moved);

        Entities[0].BrushCount += newBrushes;

        for (int i = 1; i < Entities.Count; i++)
        {
            Entities[i].FirstBrush += newBrushes;
        }

        entity.BrushCount = 0;
    }

    /// <summary>
    /// Moves an entity's brushes into worldspawn when the entity is NOT the
    /// last one loaded: <c>MoveBrushesToWorldGeneral</c>,
    /// </summary>
    /// <param name="entity">The entity whose brushes move.</param>
    /// <param name="displacements">
    /// The map's displacements, whose entity numbers are reassigned to
    /// worldspawn, or null.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    /// <remarks>
    /// The difference from <see cref="MoveBrushesToWorld"/> is the guard on
    /// which entities shift: only those whose first brush is BEFORE the moving
    /// entity's, with a strict comparison so the entity being moved is not
    /// remapped — stock's own comment says "if we use
    /// &lt;=, then we'll remap the passed in ent, which we don't want to".
    /// </remarks>
    public void MoveBrushesToWorldGeneral(
        MapEntity entity,
        IEnumerable<IMapDisplacement>? displacements = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        int entityNumber = Entities.IndexOf(entity);

        if (displacements is not null)
        {
            foreach (IMapDisplacement displacement in displacements)
            {
                if (displacement.EntityNumber == entityNumber)
                {
                    displacement.EntityNumber = 0;
                }
            }
        }

        int newBrushes = entity.BrushCount;
        int worldBrushes = Entities[0].BrushCount;
        int firstBrush = entity.FirstBrush;

        List<MapBrush> moved = Brushes.GetRange(firstBrush, newBrushes);
        Brushes.RemoveRange(firstBrush, newBrushes);
        Brushes.InsertRange(worldBrushes, moved);

        Entities[0].BrushCount += newBrushes;

        for (int i = 1; i < Entities.Count; i++)
        {
            if (Entities[i].FirstBrush < firstBrush)
            {
                Entities[i].FirstBrush += newBrushes;
            }
        }

        entity.BrushCount = 0;
    }

    /// <summary>
    /// Clears <c>CONTENTS_DETAIL</c> from every side of every brush of an
    /// entity: <c>RemoveContentsDetailFromEntity</c>,
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    /// <remarks>
    /// Run on every entity that is not worldspawn once it has finished loading
    /// Detail is a property of world geometry, and
    /// a <c>func_detail</c> that reached here was already folded into the world.
    /// </remarks>
    public void RemoveContentsDetailFromEntity(MapEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        for (int i = 0; i < entity.BrushCount; i++)
        {
            MapBrush brush = Brushes[entity.FirstBrush + i];

            for (int j = 0; j < brush.SideCount; j++)
            {
                MapBrushSide side = _brushSides[brush.FirstSide + j];
                side.Contents &= ~(int)BrushContents.Detail;
            }
        }
    }

    /// <summary>
    /// The index of the side with a given VMF id, or -1:
    /// <c>CMapFile::SideIDToIndex</c>.
    /// </summary>
    /// <param name="brushSideId">The side's VMF <c>id</c>.</param>
    /// <returns>The index into <see cref="BrushSides"/>, or -1.</returns>
    public int SideIdToIndex(int brushSideId)
    {
        for (int i = 0; i < _brushSides.Count; i++)
        {
            if (_brushSides[i].Id == brushSideId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Marks every side as casting dynamic shadows, then clears the ones an
    /// <c>info_no_dynamic_shadow</c> named: <c>MarkNoDynamicShadowSides</c>,
    /// </summary>
    /// <remarks>
    /// Belongs to the pipeline rather than to loading — stock calls it from
    /// — but the list it reads is filled during the load, so
    /// leaving it out would leave that list with no consumer and
    /// <see cref="MapBrushSide.DynamicShadowsEnabled"/> meaning nothing.
    /// </remarks>
    public void MarkNoDynamicShadowSides()
    {
        foreach (MapBrushSide side in _brushSides)
        {
            side.DynamicShadowsEnabled = true;
        }

        foreach (int id in NoDynamicShadowSides)
        {
            foreach (MapBrushSide side in _brushSides)
            {
                if (side.Id == id)
                {
                    side.DynamicShadowsEnabled = false;
                }
            }
        }
    }

    private static Vec3 AxisVector(int axis, float value) => axis switch
    {
        0 => new Vec3(value, 0f, 0f),
        1 => new Vec3(0f, value, 0f),
        _ => new Vec3(0f, 0f, value),
    };

    private void Swap(int a, int b)
    {
        (_brushSides[a], _brushSides[b]) = (_brushSides[b], _brushSides[a]);
        (_sideBrushTextures[a], _sideBrushTextures[b]) = (_sideBrushTextures[b], _sideBrushTextures[a]);
    }

    private void AddEdgeBevels(MapBrush brush, Winding w, int pointIndex, Vec3 edge)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            for (int dir = -1; dir <= 1; dir += 2)
            {
                Vec3 vec2 = AxisVector(axis, dir);

                // StockQuirk.EdgeBevelNormalise. The one that
                // decides the stored plane TYPE: on a normal whose |x| and |z|
                // are mathematically equal, the estimate breaks the tie and an
                // exact divide does not.
                Vec3 cross = Vec3.Cross(edge, vec2);
                (Vec3 normal, float length) = StockEdgeBevels
                    ? cross.NormaliseLikeStock()
                    : cross.Normalise();

                if (length < 0.5f)
                {
                    continue;
                }

                Vec3 point = Windings.Points(w)[pointIndex];
                float dist = Vec3.Dot(point, normal);
                Plane candidate = new(normal, dist);

                int k = 0;
                for (; k < brush.SideCount; k++)
                {
                    MapBrushSide other = _brushSides[brush.FirstSide + k];

                    // A LARGER tolerance than the plane
                    // table's, and the same value for both epsilons.
                    if (Plane.Equal(Planes[other.PlaneNumber], candidate, 0.01f, 0.01f))
                    {
                        break;
                    }

                    if (other.Winding.IsNull)
                    {
                        continue;
                    }

                    Span<Vec3> points = Windings.Points(other.Winding);
                    int l = 0;
                    for (; l < points.Length; l++)
                    {
                        if (Vec3.Dot(points[l], normal) - dist > 0.1f)
                        {
                            break;
                        }
                    }

                    if (l != points.Length)
                    {
                        break;
                    }
                }

                if (k != brush.SideCount)
                {
                    continue;
                }

                MapBrushSide first = _brushSides[brush.FirstSide];
                MapBrushSide bevel = new()
                {
                    PlaneNumber = Planes.Find(normal, dist),
                    TexInfo = first.TexInfo,
                    Contents = first.Contents,
                    Bevel = true,
                };

                AddBrushSide(bevel, default);
                EdgeBevels++;
                brush.SideCount++;
            }
        }
    }
}

/// <summary>Diagnostic codes the map loader raises.</summary>
public static class MapLoadDiagnostics
{
    /// <summary>A brush extends beyond the map's coordinate range.</summary>
    public const string BrushBoundsOutOfRange = "VBSP0101";

    /// <summary>Every side of a brush clipped away to nothing.</summary>
    public const string BrushHasNoVisibleSides = "VBSP0102";

    /// <summary>Two sides of one brush landed on the same plane.</summary>
    public const string DuplicatePlane = "VBSP0103";

    /// <summary>Two sides of one brush landed on opposite halves of one pair.</summary>
    public const string MirroredPlane = "VBSP0104";

    /// <summary>Three points of a side did not define a plane.</summary>
    public const string PlaneWithNoNormal = "VBSP0105";

    /// <summary>A <c>func_instance</c>'s file could not be opened.</summary>
    public const string InstanceNotFound = "VBSP0106";

    /// <summary>
    /// A <c>func_instance</c> asked for a name fixup style this compiler
    /// cannot honour, because it reads no FGD.
    /// </summary>
    public const string InstanceNameFixupUnsupported = "VBSP0107";
}
