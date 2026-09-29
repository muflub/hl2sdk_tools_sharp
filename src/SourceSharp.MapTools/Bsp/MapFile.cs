//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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

    // SideIdToIndex's answers: each id's FIRST index in _brushSides, for the
    // first _sideIdsIndexed sides. Built lazily and extended as sides are
    // appended, so a lookup is O(1) instead of a scan of every side -- the
    // overlay and cubemap passes ask once per side they name, and on a
    // full-size map the scans were a measurable share of the whole load.
    //
    // Anything that could change an answer throws the table away rather than
    // patching it: Swap (bevel ordering moves sides) and a side's Id being
    // set after it was added (MapBrushSide calls back through
    // _invalidateSideIds). Both are rare after loading, which is when the
    // lookups happen, so a rebuild costs one pass at most.
    private readonly Dictionary<int, int> _sideIds = [];
    private readonly Action _invalidateSideIds;
    private int _sideIdsIndexed;

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
        _invalidateSideIds = InvalidateSideIds;
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
    /// The entities themselves are cleared as they load, the way the
    /// reference compiler clears them: a <c>func_viscluster</c> is a compile
    /// instruction with no runtime meaning, so it gets no model and no entity
    /// lump record. The numbers stay here so the incremental cache's digest
    /// still sees where they were; their volumes are in
    /// <see cref="VisClusters"/>.
    /// </remarks>
    public List<int> VisClusterEntities { get; } = [];

    /// <summary>
    /// The volumes of the map's <c>func_viscluster</c> entities, in load
    /// order, which the portal file stage uses to merge the leaves each one
    /// covers into a single vis cluster.
    /// </summary>
    /// <remarks>
    /// Built while the map loads, for the plane-table reason given on
    /// <see cref="Tree.VisClusterVolumes"/>. Only the map the entities were
    /// loaded into has them: a <c>func_viscluster</c> inside a
    /// <c>func_instance</c> is not carried into the main map, which is what
    /// the reference does in effect too, since it builds the volume from the
    /// main map's brushes at the instance's brush numbers.
    /// </remarks>
    public Tree.VisClusterVolumes VisClusters { get; } = new();

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
        side.IdChanged += _invalidateSideIds;
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

                    // StockQuirk.BoxBevelWindingBounds. Stock places the bevel
                    // at the brush's bounds, which are read off windings that
                    // carry the clipping's rounding; Correct places it at the
                    // extreme corner solved from the brush's own planes.
                    if (!Windings.Compliance.Emulates(StockQuirk.BoxBevelWindingBounds))
                    {
                        dist = BoxBevelDistanceFromPlanes(brush, axis, dir, dist);
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
    /// <remarks>
    /// The FIRST side with the id, as the reference's linear scan finds it:
    /// ids are not required to be unique (an instance merge offsets them, a
    /// hand-edited VMF can repeat them), and the first one wins. Answered from
    /// a table built on first use and extended as sides are appended; see the
    /// field comment for what invalidates it. Not safe to call concurrently
    /// with itself or with any change to the map, like the rest of this type.
    /// </remarks>
    public int SideIdToIndex(int brushSideId)
    {
        for (int i = _sideIdsIndexed; i < _brushSides.Count; i++)
        {
            // TryAdd keeps the earlier index when an id repeats.
            _sideIds.TryAdd(_brushSides[i].Id, i);
        }

        _sideIdsIndexed = _brushSides.Count;
        return _sideIds.TryGetValue(brushSideId, out int index) ? index : -1;
    }

    private void InvalidateSideIds()
    {
        _sideIds.Clear();
        _sideIdsIndexed = 0;
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
        if (a != b && (a < _sideIdsIndexed || b < _sideIdsIndexed))
        {
            // Two indexed positions trade sides: first-index answers may move.
            InvalidateSideIds();
        }

        (_brushSides[a], _brushSides[b]) = (_brushSides[b], _brushSides[a]);
        (_sideBrushTextures[a], _sideBrushTextures[b]) = (_sideBrushTextures[b], _sideBrushTextures[a]);
    }

    /// <summary>
    /// How close a winding vertex has to be to one of its brush's planes, and
    /// to the brush's extreme along an axis, for
    /// <see cref="BoxBevelDistanceFromPlanes"/> to count it: the vertex is on
    /// that plane, or is one of the corners that decide the extreme.
    /// </summary>
    /// <remarks>
    /// Stock's <c>ON_EPSILON</c>, the resolution the rest of the loader's
    /// bevel tests work at, and over ten times the rounding a winding vertex
    /// carries from being clipped out of a base winding 65536 units across
    /// (about 0.007, more where two planes meet at a glancing angle: 0.016
    /// was measured on 2fort). See <see cref="StockQuirk.BoxBevelWindingBounds"/>.
    /// </remarks>
    public const float BoxBevelCornerEpsilon = 0.1f;

    /// <summary>
    /// Where a box bevel goes under <see cref="CompliancePolicy.Correct"/>: the
    /// brush's extreme along an axis, taken from the corners where its planes
    /// meet, solved in double precision, rather than from its winding bounds.
    /// </summary>
    /// <param name="brush">The brush being bevelled.</param>
    /// <param name="axis">The bevel's axis, 0 to 2.</param>
    /// <param name="dir">-1 for the minimum, +1 for the maximum.</param>
    /// <param name="windingDistance">
    /// Stock's distance, <c>maxs[axis]</c> or <c>-mins[axis]</c>, returned
    /// unchanged when no corner can be solved.
    /// </param>
    /// <returns>The bevel plane's distance along <c>dir</c> times the axis.</returns>
    /// <remarks>
    /// <para>
    /// <b>What it replaces.</b> A box bevel is added on an axis where the
    /// brush has no side of its own, at the brush's bounds, and the bounds
    /// come from its windings. Those windings are cut from base windings
    /// 65536 units across in single precision, so a corner is a few
    /// thousandths off where its planes put it, and more where two planes
    /// meet at a glancing angle, as along the thin edge of a wedge. The
    /// plane table then snaps the bevel to an integer distance only when it
    /// is within 0.01 of one. On 2fort a wedge whose thin edge is at
    /// y = -512 had a winding bound of -511.99x under Correct, so its bevel
    /// snapped onto the plane y = -512 that a neighbouring brush's face also
    /// uses, and the tree counted that brush as facing the plane; with
    /// <see cref="StockQuirk.PlaneFromPointsNormalise"/> flipped the bound
    /// was -511.98654, the bevel became a plane of its own, and the node
    /// split elsewhere. Bevels at 640.0117 and 464.0156 did the same with
    /// <see cref="StockQuirk.BaseWindingNormalise"/> flipped (see
    /// <see cref="StockQuirk.BoxBevelWindingBounds"/>).
    /// </para>
    /// <para>
    /// <b>How.</b> Every winding vertex within
    /// <see cref="BoxBevelCornerEpsilon"/> of the winding extreme is a corner
    /// that might decide it. Its planes are the brush's non-bevel sides it
    /// lies within <see cref="BoxBevelCornerEpsilon"/> of; every three of them
    /// with independent normals meet in one point, solved by Cramer's rule in
    /// double from the table's planes, and the solution nearest the vertex
    /// (and within the same epsilon of it) is the corner. The extreme is the
    /// furthest corner along the axis. The answer depends on the planes and
    /// not on the order the windings were clipped in or on how their base
    /// windings were normalised, and it lands on an integer when the planes
    /// meet at one, which is what the table's snap is for. A vertex with no
    /// solvable corner keeps its winding value.
    /// </para>
    /// </remarks>
    private float BoxBevelDistanceFromPlanes(MapBrush brush, int axis, int dir, float windingDistance)
    {
        double best = double.NegativeInfinity;
        List<Plane> incident = [];

        for (int i = 0; i < brush.SideCount; i++)
        {
            MapBrushSide side = _brushSides[brush.FirstSide + i];
            if (side.Bevel || side.Winding.IsNull)
            {
                continue;
            }

            foreach (Vec3 p in Windings.Points(side.Winding))
            {
                if ((dir * p[axis]) < windingDistance - BoxBevelCornerEpsilon)
                {
                    continue;
                }

                incident.Clear();
                for (int j = 0; j < brush.SideCount; j++)
                {
                    MapBrushSide other = _brushSides[brush.FirstSide + j];
                    if (other.Bevel)
                    {
                        continue;
                    }

                    Plane plane = Planes[other.PlaneNumber];
                    if (Math.Abs(Vec3.Dot(p, plane.Normal) - plane.Dist) < BoxBevelCornerEpsilon
                        && !incident.Contains(plane))
                    {
                        incident.Add(plane);
                    }
                }

                double value = dir * (double)p[axis];
                if (NearestCorner(incident, p, out double cx, out double cy, out double cz))
                {
                    value = dir * (axis == 0 ? cx : axis == 1 ? cy : cz);
                }

                best = Math.Max(best, value);
            }
        }

        return double.IsNegativeInfinity(best) ? windingDistance : (float)best;
    }

    /// <summary>
    /// The point where three of <paramref name="planes"/> meet that is nearest
    /// <paramref name="near"/>, and within <see cref="BoxBevelCornerEpsilon"/>
    /// of it, solved in double.
    /// </summary>
    private static bool NearestCorner(
        List<Plane> planes, Vec3 near, out double x, out double y, out double z)
    {
        x = y = z = 0;
        double bestDistance = (double)BoxBevelCornerEpsilon * BoxBevelCornerEpsilon;
        bool found = false;

        for (int a = 0; a < planes.Count; a++)
        {
            for (int b = a + 1; b < planes.Count; b++)
            {
                for (int c = b + 1; c < planes.Count; c++)
                {
                    Plane p1 = planes[a];
                    Plane p2 = planes[b];
                    Plane p3 = planes[c];

                    // n2 x n3, n3 x n1, n1 x n2, in double.
                    double ax = ((double)p2.Normal.Y * p3.Normal.Z) - ((double)p2.Normal.Z * p3.Normal.Y);
                    double ay = ((double)p2.Normal.Z * p3.Normal.X) - ((double)p2.Normal.X * p3.Normal.Z);
                    double az = ((double)p2.Normal.X * p3.Normal.Y) - ((double)p2.Normal.Y * p3.Normal.X);
                    double bx = ((double)p3.Normal.Y * p1.Normal.Z) - ((double)p3.Normal.Z * p1.Normal.Y);
                    double by = ((double)p3.Normal.Z * p1.Normal.X) - ((double)p3.Normal.X * p1.Normal.Z);
                    double bz = ((double)p3.Normal.X * p1.Normal.Y) - ((double)p3.Normal.Y * p1.Normal.X);
                    double cx = ((double)p1.Normal.Y * p2.Normal.Z) - ((double)p1.Normal.Z * p2.Normal.Y);
                    double cy = ((double)p1.Normal.Z * p2.Normal.X) - ((double)p1.Normal.X * p2.Normal.Z);
                    double cz = ((double)p1.Normal.X * p2.Normal.Y) - ((double)p1.Normal.Y * p2.Normal.X);

                    double det = ((double)p1.Normal.X * ax) + ((double)p1.Normal.Y * ay) + ((double)p1.Normal.Z * az);
                    if (det == 0)
                    {
                        continue;
                    }

                    double sx = ((p1.Dist * ax) + (p2.Dist * bx) + (p3.Dist * cx)) / det;
                    double sy = ((p1.Dist * ay) + (p2.Dist * by) + (p3.Dist * cy)) / det;
                    double sz = ((p1.Dist * az) + (p2.Dist * bz) + (p3.Dist * cz)) / det;

                    double dx = sx - near.X;
                    double dy = sy - near.Y;
                    double dz = sz - near.Z;
                    double distance = (dx * dx) + (dy * dy) + (dz * dz);

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        x = sx;
                        y = sy;
                        z = sz;
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// How close to a candidate edge bevel every vertex of an existing side
    /// has to be, under <see cref="CompliancePolicy.Correct"/>, for that side
    /// to count as already lying on the bevel's plane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it guards.</b> <see cref="AddBrushBevels"/> adds a bevel along
    /// an edge unless the brush already has a side on the candidate plane.
    /// Stock decides "already has" with the plane-equality test at 0.01 on
    /// every normal component and 0.01 on the distance, and the distance is
    /// measured at the ORIGIN. A slanted face far from the origin gives two
    /// planes through the same face whose normals differ by a few millionths,
    /// one from the face's three map points and one from a winding edge, and
    /// that angle times the lever arm from the origin to the face is more
    /// than 0.01: on 2fort, a side (-0.20486315, -0.97879064) at 737.871 and
    /// a candidate (-0.20486762, -0.9787897) at 737.894 are 0.023 apart at
    /// the origin and the same plane to a thousandth where the brush is. So
    /// the brush gains a bevel that duplicates its own face, and whether it
    /// does depends on the last bits of both normals: flipping one of the
    /// normalise quirks adds or drops it. The duplicate is not inert. It is a
    /// new plane in the table, and a later brush's face within 0.01 of it is
    /// merged onto it rather than onto the face it duplicates, so the tree
    /// splits on one plane or the other by noise
    /// (<see cref="StockQuirk.EdgeBevelDuplicateAtOrigin"/>).
    /// </para>
    /// <para>
    /// <b>Why 0.1.</b> It is the resolution the same loop already works at:
    /// the test right after it calls a vertex outside the candidate only when
    /// it is more than 0.1 in front of it. A side facing the candidate's way,
    /// with every vertex within 0.1 of the candidate plane, bounds the brush
    /// along that plane to that resolution already, so the bevel adds
    /// nothing. It is also stock's <c>ON_EPSILON</c>, and more than ten times
    /// the rounding a winding vertex carries from being clipped out of a base
    /// winding 65536 units across (about 0.007 at worst).
    /// </para>
    /// </remarks>
    public const float BevelOnSideEpsilon = 0.1f;

    /// <summary>
    /// Whether a side lies on a candidate bevel plane where the side is:
    /// its normal agrees with the candidate's as closely as stock's own
    /// duplicate test asks (0.01 per component) and every vertex of its
    /// winding is within <see cref="BevelOnSideEpsilon"/> of the candidate.
    /// </summary>
    /// <param name="side">The side's plane.</param>
    /// <param name="winding">The side's winding, not null.</param>
    /// <param name="candidate">The candidate bevel plane.</param>
    /// <returns>True when the candidate duplicates the side.</returns>
    /// <remarks>
    /// This is stock's duplicate test with the distance measured at the side
    /// rather than at the origin; the normal half is unchanged. Only
    /// <see cref="CompliancePolicy.Correct"/> asks it, and only in addition to
    /// stock's test, so Correct never adds a bevel stock would not.
    /// </remarks>
    private bool SideLiesOnBevel(Plane side, Winding winding, Plane candidate)
    {
        if (Math.Abs(side.Normal.X - candidate.Normal.X) >= 0.01f
            || Math.Abs(side.Normal.Y - candidate.Normal.Y) >= 0.01f
            || Math.Abs(side.Normal.Z - candidate.Normal.Z) >= 0.01f)
        {
            return false;
        }

        foreach (Vec3 p in Windings.Points(winding))
        {
            if (Math.Abs(Vec3.Dot(p, candidate.Normal) - candidate.Dist) >= BevelOnSideEpsilon)
            {
                return false;
            }
        }

        return true;
    }

    private void AddEdgeBevels(MapBrush brush, Winding w, int pointIndex, Vec3 edge)
    {
        bool stockDuplicateTest = Windings.Compliance.Emulates(StockQuirk.EdgeBevelDuplicateAtOrigin);

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

                    // StockQuirk.EdgeBevelDuplicateAtOrigin. Stock compares
                    // distances at the origin, where a normal a few millionths
                    // off moves a plane through this brush by more than 0.01;
                    // Correct also asks whether the side lies on the candidate
                    // where the side actually is.
                    if (!stockDuplicateTest && SideLiesOnBevel(Planes[other.PlaneNumber], other.Winding, candidate))
                    {
                        break;
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
