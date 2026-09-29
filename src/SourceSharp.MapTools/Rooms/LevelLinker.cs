//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Collections.Immutable;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Validation;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Links a <see cref="LevelLayout"/> of compiled <see cref="RoomObject"/>s into
/// one <see cref="BspData"/> plus one <see cref="VisResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// The link is <b>relocation, not rebuild</b>: each
/// room's compiled BSP — planes, vertices, faces, edges, its own node subtree,
/// leaves, brushes, texture axes, world collision — is carried in and moved by
/// <see cref="RoomTransform"/>: a quarter turn about +z and a whole-cell
/// translation, so every relocated coordinate is a permutation of the room's
/// own plus an integer multiple of the cell, and no rotation matrix is ever
/// multiplied (invariant I4: the same input byte-yields the same output on
/// one thread or sixty-four). One top tree hangs the room subtrees in world
/// order; its split planes are the grid's cell faces, so a point inside a
/// cell descends to exactly that cell's room root and the room's own tree
/// routes it to the leaf vvis assigned its cluster.
/// </para>
/// <para>
/// <b>Doors.</b> Every room is compiled sealed: each socket carries a plug
/// brush, so vbsp's flood fill stops at the doorway and the room's own vvis is
/// its door-shut visibility. At a <em>jointed</em> socket the linker strips
/// the plug (see <c>LevelLinker.Plugs</c>): the solid leaves the plug made are
/// split so the doorway box becomes an empty leaf, the plug brush leaves every
/// leaf's brush list, the world collision and the brush lump (the brushes
/// after it are renumbered), and its faces are drawn as
/// nodraw. A <em>capped</em> socket keeps its plug, so it stays a wall.
/// Stripping at link time rather than compiling each room twice (sealed for
/// vis, open for geometry) keeps one compile per room, at one cost: the
/// doorway's jambs, lintel and sill have no faces, because in the room's
/// compile they faced the solid plug and vbsp emits no face between two
/// solids. The doorway is open to traces, physics and vis; it only draws as
/// a gap at its edges unless something in the socket covers them.
/// </para>
/// <para>
/// <b>Brushes.</b> After relocation the world's touching box brushes are
/// folded into larger boxes (<see cref="LinkBrushFold"/>, on unless
/// <see cref="LevelLinkOptions.FoldBrushes"/> is off): rooms meet cell to
/// cell, so floors, ceilings and back-to-back walls are one box in two
/// brushes, and the brush cap is otherwise the first limit a large level
/// meets. The leaves' brush runs and the ledges' client data follow the
/// new numbering; nothing else changes. A brush entity's brushes are its
/// model's and never fold.
/// </para>
/// <para>
/// <b>Visibility</b> is composed through the doorways, never flooded and
/// never vvis'd (<see cref="LevelDoorVisibility"/>): a room's linked rows
/// start as its own vvis rows, and two rooms see each other only along
/// straight lines through the chain of doorways between them, which a flow
/// over the doorway rectangles works out from each room's door visibility
/// (<see cref="RoomDoorVisibility"/>, stored in the pack). The facing
/// clusters of a joint are the open clusters whose leaf boxes overlap its
/// plug box (with <see cref="DoorOverlapEpsilon"/>; the link geometry is
/// integer and bevels are ±8, so a face-sharing leaf sits at gap 0 and the
/// next space over is never closer than the wall's thickness), and the
/// stripped doorway leaf joins the lowest of its own side's facing clusters.
/// With <see cref="LevelLinkOptions.DoorVisibility"/> off, the rows are what
/// the link wrote before: the transitive closure of own-row steps plus
/// door edges (every facing cluster to every cluster facing it from the
/// other side), in which every cluster of a level sees every other.
/// </para>
/// <para>
/// A room whose compile left anything outside the relocation set — a water
/// leaf, a real area portal, displacements, detail props — is refused
/// rather than silently dropped: the linked map must be the rooms, not an
/// approximation of them.
/// </para>
/// <para>
/// <b>Brush entities</b> are carried as their own models: every placed
/// room's brush models after the world, in link order, each with its tree,
/// faces, brushes and collision record, an origin-relative one in its
/// entity's frame, and one that <c>room_needs</c> or the socket furniture
/// rule drops omitted whole (<see cref="PlanModels"/>, <see cref="RoomModelLayout"/>,
/// <see cref="RoomBrushModels"/>).
/// </para>
/// <para>
/// <b>Static props</b> are carried: every placed room's records, moved
/// with the room, kept or dropped by <c>room_needs</c> and the socket
/// furniture rule, their dictionaries merged, and each prop's leaves
/// listed by walking the linked tree with the hull the pack stores
/// (<see cref="PlanProps"/>, <see cref="WritePropsAsync"/>). They cost
/// the level no entity.
/// </para>
/// <para>
/// <b>Packed files</b> are carried: the level's one pak holds every placed
/// room's, merged by name, the room's default cubemaps renamed to the
/// level's map name (<see cref="LevelPakFiles"/>).
/// </para>
/// <para>
/// <b>Overlays</b> are carried: every placed room's <c>info_overlay</c>
/// records in link order, each moved and turned with its room, its id,
/// texinfo and faces rebased, a named one's accessor renumbered to match
/// (<see cref="LinkOverlays"/>, <see cref="RoomOverlays"/>). Water overlays
/// are refused with water.
/// </para>
/// </remarks>
public static partial class LevelLinker
{
    /// <summary>
    /// How far past a shared face two leaf boxes must overlap to face a joint:
    /// touching (gap 0) counts, which a non-negative epsilon would reject.
    /// </summary>
    public const float DoorOverlapEpsilon = -0.5f;

    /// <summary>
    /// The lumps the relocation carries; anything else non-empty is refused.
    /// </summary>
    /// <remarks>
    /// Several of these are carried only in their empty form, and
    /// <see cref="PlanRoom"/> checks that: <see cref="BspLump.AreaPortals"/>
    /// holds only the reserved portal 0, <see cref="BspLump.PhysDisp"/> counts
    /// no displacement, and every game lump but the static props' is all
    /// zeros (no detail props); the static prop lump is rebuilt for the
    /// level from the rooms' (<see cref="WritePropsAsync"/>).
    /// <see cref="BspLump.PakFile"/> is carried whole: the rooms'
    /// archives are merged (<see cref="LevelPakFiles"/>).
    /// <see cref="BspLump.Cubemaps"/> is every placement's samples at their
    /// linked positions (<see cref="LevelCubemaps"/>).
    /// <see cref="BspLump.ClipPortalVerts"/> is not in the set: its vertices
    /// only exist for area portals, which are refused.
    /// <see cref="BspLump.Overlays"/> and <see cref="BspLump.OverlayFades"/>
    /// are rebuilt for the level from the rooms' (<see cref="LinkOverlays"/>),
    /// when the room's compile left its overlay data with it
    /// (<see cref="RoomOverlaysOf"/>); <see cref="BspLump.WaterOverlays"/>
    /// is not in the set: water overlays are drawn along water, which is
    /// refused.
    /// </remarks>
    private static readonly ImmutableHashSet<BspLump> CarriedLumps =
        ImmutableHashSet.CreateRange([
        BspLump.Entities, BspLump.Planes, BspLump.TexData, BspLump.Vertexes,
        BspLump.Visibility, BspLump.Nodes, BspLump.TexInfo, BspLump.Faces,
        BspLump.Lighting, BspLump.Leafs, BspLump.Edges, BspLump.Models,
        BspLump.LeafFaces, BspLump.LeafBrushes, BspLump.Brushes, BspLump.BrushSides,
        BspLump.TexDataStringData, BspLump.TexDataStringTable, BspLump.LeafMinDistToWater,
        BspLump.FaceIds, BspLump.SurfEdges, BspLump.OriginalFaces,
        BspLump.VertNormals, BspLump.VertNormalIndices,
        BspLump.Primitives, BspLump.PrimVerts, BspLump.PrimIndices,
        BspLump.FaceMacroTextureInfo,
        BspLump.Areas, BspLump.AreaPortals,
        BspLump.Occlusion, BspLump.PakFile, BspLump.MapFlags, BspLump.Cubemaps,
        BspLump.PhysCollide, BspLump.PhysDisp,
        BspLump.Overlays, BspLump.OverlayFades,
        ]);

    /// <summary>Links <paramref name="layout"/>'s rooms into one map.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms, by name.</param>
    /// <param name="context">
    /// The compile context: its parallelism plans the rooms, its
    /// compliance chooses the precision the world collision is rebuilt at,
    /// and its <see cref="VbspContext.MapBase"/> is the linked map's name
    /// (the file name the engine loads it by, without directory or
    /// extension), which the rooms' default cubemaps are renamed to
    /// (<see cref="LevelPakFiles"/>). It may be empty for a level none of
    /// whose rooms packs such a file.
    /// </param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The linked BSP, its visibility, and the plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The layout's own shape is wrong (<see cref="LevelLayout.Validate"/>):
    /// no rooms, a shared cell, a blank name, a grid that is not a positive
    /// finite number.
    /// </exception>
    /// <exception cref="RoomLintException">
    /// A layout rule is broken: a room the library lacks, a socket neither
    /// jointed nor capped, a grid or kit that is not the library's, or a room
    /// a player cannot reach from the others (<see cref="RoomLinter.CheckReachable"/>).
    /// </exception>
    /// <exception cref="LinkException">
    /// A joint is geometrically wrong (no neighbour in its direction, a socket
    /// that does not exist, sides that do not meet head-on, a cap naming no
    /// socket, a joint no open leaf faces), a room's compile carries something
    /// the relocation refuses, two rooms pack one file with different bytes,
    /// or the level outgrows a field of the format.
    /// </exception>
    public static Task<LinkedLevel> LinkAsync(
        LevelLayout layout,
        RoomLibrary library,
        VbspContext context,
        CancellationToken cancellationToken = default) =>
        LinkAsync(layout, library, context, LevelLinkOptions.Default, cancellationToken);

    /// <summary>Links <paramref name="layout"/>'s rooms into one map, with the entity budget's settings.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms, by name, and the library's settings (<see cref="RoomLibrary.Options"/>).</param>
    /// <param name="context">As for <see cref="LinkAsync(LevelLayout, RoomLibrary, VbspContext, CancellationToken)"/>.</param>
    /// <param name="options">
    /// The entity budget's settings: the reserve, overriding the library's,
    /// and the class table (<see cref="LevelEntityBudget"/>).
    /// </param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The linked BSP, its visibility, the plan, and the entity budget's report.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">As for the overload without options.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The reserve is not from 0 to the edict cap.</exception>
    /// <exception cref="RoomLintException">As for the overload without options.</exception>
    /// <exception cref="LinkException">
    /// As for the overload without options, and a level whose edicts pass
    /// the cap or whose entity list passes what a map may hold.
    /// </exception>
    public static Task<LinkedLevel> LinkAsync(
        LevelLayout layout,
        RoomLibrary library,
        VbspContext context,
        LevelLinkOptions options,
        CancellationToken cancellationToken = default) =>
        // The host resumes on a fresh stack, not on the planning worker the
        // link finished on, whose frames hold the link's scratch until they
        // return (HostHandoff says why).
        HostHandoff.ReturnAsync(LinkCoreAsync(layout, library, context, options, cancellationToken));

    private static async Task<LinkedLevel> LinkCoreAsync(
        LevelLayout layout,
        RoomLibrary library,
        VbspContext context,
        LevelLinkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        EntityClassTable classes = options.EntityClasses ?? EntityClassTable.Default;

        // Layout first (rule 5 / rule 2's layout halves, house messages), then
        // the format's limits, then the joint geometry only the linker can
        // check. The limits come before anything that costs in the rooms: a
        // level far past them is refused from the rooms' lump counts alone,
        // not after every room has been planned.
        RoomLinter.CheckLayout(layout, library);
        LevelEntityReport entities = CheckCapacity(layout, library, options, context.MapBase);
        ValidateJoints(layout, library);
        RoomLinter.CheckReachable(layout, name => library.Get(name).Definition);

        ResolvedPlacement[] resolved = [.. layout.Rooms.Select((p, i) => Resolve(p, i, library))];

        // The level's transitions and spawn (the rooms design, section 11):
        // the level rule checked, and what each transition room writes
        // decided, from the rooms' stored transition data. Null for a level
        // without transitions, which links exactly as before them.
        LevelTransitionPlan? transitions = LevelTransitionPlan.Make(
            layout, [.. resolved.Select(p => TransitOf(p.Room))], name => library.Get(name).Definition, options.ModEntities);

        // Whether the level is lit (its rooms' base bakes, the rooms design,
        // section 9), and what its rooms agree on: null for a level of unlit
        // rooms, which links exactly as it did before the bake.
        LevelLight? lit = PlanLighting(resolved);

        // The level's one pak: every placed room's packed files, merged by
        // name (LevelPakFiles). Each room's pak is a zip, and reading it is
        // async, so it is read here rather than inside the planning
        // workers; once per room, in the order the level first places it,
        // since every placement packs the same bytes. A room with stored
        // link data had its pak read when it was compiled, but its files
        // are wanted now too.
        List<(string Room, ZipArchiveReader Pak)> paks = [];
        HashSet<string> readPaks = new(StringComparer.Ordinal);
        foreach (ResolvedPlacement placement in resolved)
        {
            if (readPaks.Add(placement.Room.Definition.Name)
                && await ReadPakAsync(placement.Room, cancellationToken).ConfigureAwait(false) is { } pak)
            {
                paks.Add((placement.Room.Definition.Name, pak));
            }
        }

        // The level's static props: which it keeps and their linked indices
        // depend on the layout alone, and the pak names each prop's lighting
        // files by that index; the leaves wait for the linked tree.
        // Socket furniture is a side's props and brush entities together;
        // which brush models the level keeps, and their linked numbers,
        // depend on the layout alone too, and every placement's plan needs
        // them.
        LevelFurniture furniture = new(resolved, layout);
        LevelProps? props = PlanProps(resolved, layout, furniture);
        LevelModels models = PlanModels(resolved, layout, furniture, transitions);

        // The level's cubemaps: every placement's samples at its position,
        // and the names its room made after them renamed for it, which the
        // pak and the texdata strings take (LevelCubemaps). Null for a level
        // without samples, which links exactly as before them.
        LevelCubemaps? cubemaps = LevelCubemaps.Plan(
            [.. resolved.Select(p => (p.Room, new RoomTransform(p.Instance.Placement, p.Room.Definition.CellSize)))], context.MapBase);
        (byte[]? mergedPak, int packedFiles) = LevelPakFiles.Merge(
            paks,
            context.MapBase,
            props?.Files,
            cubemaps?.ByRoom(),
            lit is not null && props is not null ? BakedPropFiles(resolved, props) : null,
            cancellationToken);

        // Per-room work: validate the compile against the relocation set and
        // move the turned structs to the cell. Each item writes only its own
        // slot (disjoint ranges), and the thread count comes from the
        // context's parallelism — degree 1 and degree 32 produce
        // byte-identical output because the counts and bases are a
        // sequential prefix sum below and no item reads another's result
        // (invariant I4).
        //
        // When every room's checks and plug census were done at room compile
        // time (its stored link data), what is left per placement is a
        // translation pass and at most a quarter turn, microseconds each,
        // and starting (and joining) a thread pool for it cost several times
        // the work (10 to 20 ms a level on the stress library, against 2 to
        // 7 ms of planning on one thread): the link runs it on this thread.
        // Rooms without stored data are checked and censused here, the heavy
        // work the pool is for.
        RoomPlan[] plans = new RoomPlan[resolved.Length];
        if (resolved.All(p => StoredLink(p.Room) is not null))
        {
            for (int index = 0; index < resolved.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plans[index] = PlanRoom(resolved[index], models.Linked[index]);
            }
        }
        else
        {
            using WorkQueue queue = new(context.Parallelism);
            await queue.RunAsync(
                resolved.Length,
                (index, _) =>
                {
                    plans[index] = PlanRoom(resolved[index], models.Linked[index]);
                },
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        AssignBases(plans);

        // A lit level's lightmaps: each stored turn of a room once, every
        // placement of it pointing there.
        List<(RoomLightingPayload Payload, int LdrBase, int HdrBase)>? lightBlocks = lit is null ? null : AssignLightBases(plans);

        // Cluster space: room r's room-local cluster c is clusterBase_r + c;
        // solid leaves (plugs, shared void) stay cluster -1 and get no row.
        int clusterCursor = 0;
        foreach (RoomPlan plan in plans)
        {
            plan.ClusterBase = clusterCursor;
            clusterCursor += plan.ClusterCount;
        }

        int clusterCount = clusterCursor; // at most short.MaxValue: CheckCapacity

        // The level's visibility, composed from the rooms' own and the
        // doorways between them (LevelDoorVisibility), or with the door
        // visibility off, the door graph's closure.
        LevelVisibility visibility = options.DoorVisibility
            ? await LevelDoorVisibility.ComposeAsync(
                DoorRooms(resolved, plans), clusterCount, context.Parallelism, options.DoorFlowStateCap, cancellationToken).ConfigureAwait(false)
            : DoorGraphVisibility(resolved, plans, clusterCount, cancellationToken);
        int rowBytes = visibility.RowBytes;
        byte[] pvs = visibility.Pvs;
        byte[] visibilityLump = BuildVisibilityLump(clusterCount, rowBytes, pvs, visibility.Pas);
        RoomInstance lastRoom = plans[^1].Placement.Instance;
        LimitVisibility(plans[^1].Placement.Room.Definition.Name, lastRoom.Placement.CellX, lastRoom.Placement.CellY, visibilityLump.Length);

        LevelNaming naming = new(
            new LevelNamingOptions(options.ModEntities, library.Options.Folds, layout.Columns, layout.Rows, transitions),
            library.Options.NameKeySet);
        LevelSingletons singletons = new(library.LibraryEntities);
        List<(int Placement, string ClassName)> droppedFurniture = [];
        LevelLightStyles styles = new();
        List<(int Leaf, int Placement, int Cluster)> doorways = [];
        (BspData linked, int foldedBrushes) = Assemble(
            plans, layout, visibilityLump, context, classes, naming, singletons, library.Options.MapVersion, options.FoldBrushes, mergedPak,
            cubemaps, droppedFurniture, styles, doorways, cancellationToken);
        if (props is not null)
        {
            await WritePropsAsync(linked, props, plans, cancellationToken).ConfigureAwait(false);
        }

        if (lit is not null)
        {
            WriteLighting(linked, plans, lit, lightBlocks!, doorways, styles, pvs, rowBytes);
        }

        // The budget checked before planning counted the rooms as compiled.
        // When the naming resolver ran, what the level holds is what it left
        // (dropped by room_needs, folded, merged, or written by the linker),
        // and a duplicate singleton the merge dropped is gone too, so the
        // level is budgeted again from what it holds, and refused if that is
        // over the cap; its report is the one the link returns.
        if (naming.Result is not null || singletons.Dropped.Count > 0 || droppedFurniture.Count > 0)
        {
            entities = BudgetLinked(
                layout, library, naming.Result, [.. singletons.Dropped, .. droppedFurniture], LevelEntityBudget.ReserveFor(options, library.Options), classes);
        }

        VisResult vis = new(
            clusterCount,
            portalCount: 0,
            rowBytes,
            pvs,
            visibility.Pas,
            visDataSize: visibilityLump.Length,
            totalVisibleClusters: visibility.Visible,
            optimizedClusters: 0,
            totalAudibleClusters: visibility.Audible,
            usedRadius: false,
            visRadiusSquared: 0,
            deepestFlow: 0,
            work: VisWorkCounters.Zero,
            trace: null);

        return new LinkedLevel(linked, vis, new LevelPlan(layout, resolved, TopPlanes(layout, layout.CellSize)))
        {
            EntityBudget = entities,
            FoldedBrushes = foldedBrushes,
            PackedFiles = packedFiles,
            CubemapSamples = cubemaps?.SampleCount ?? 0,
            NameWarnings = naming.Result?.Warnings ?? [],
            NameNotes = naming.Result?.Verbose ?? [],
            HasTransitions = transitions is not null,
        };
    }

    /// <summary>
    /// The entity budget of a level the link changed from its rooms'
    /// counts: each placement counted from the entities the naming resolver
    /// left for it (its own kept, and what the linker wrote), or from its
    /// room's counts when the resolver did not run, less the duplicate
    /// singletons the merge dropped; by class, then budgeted as the rooms'
    /// own counts are, with the library's entities.
    /// </summary>
    private static LevelEntityReport BudgetLinked(
        LevelLayout layout,
        RoomLibrary library,
        LevelResolution? resolution,
        IReadOnlyList<(int Placement, string ClassName)> dropped,
        int reserve,
        EntityClassTable classes)
    {
        List<string>[] byPlacement = [.. layout.Rooms.Select(_ => new List<string>())];
        if (resolution is not null)
        {
            foreach (LevelEntity entity in resolution.Entities)
            {
                byPlacement[entity.Placement].Add(entity.ClassName);
            }
        }
        else
        {
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                RoomEntityCounts counts = library.Get(layout.Rooms[i].Placement.Room).CountEntities();
                byPlacement[i].AddRange(counts.Classes.SelectMany(c => Enumerable.Repeat(c.ClassName, c.Count)));
            }
        }

        foreach ((int placement, string className) in dropped)
        {
            byPlacement[placement].Remove(className);
        }

        return LevelEntityBudget.Check(
            layout.Rooms.Select((r, i) => (r.Placement.Room, RoomEntityCounts.FromClasses(byPlacement[i]))),
            reserve,
            classes,
            LibraryCounts(library));
    }

    /// <summary>The library's own entities (<see cref="RoomLibrary.LibraryEntities"/>) counted by class, or null when it has none.</summary>
    private static RoomEntityCounts? LibraryCounts(RoomLibrary library) =>
        library.LibraryEntities.Count == 0
            ? null
            : RoomLibraryEntities.Count(library.LibraryEntities);

    /// <summary>
    /// The prefix sums every index-bearing struct is shifted by, in layout
    /// order.
    /// </summary>
    /// <remarks>
    /// The totals were measured against the format's fields before any room
    /// was planned (<see cref="CheckCapacity"/>, over the same counts in the
    /// same order), so every base here fits the field that carries it. The
    /// leaves, leaf brushes and nodes the plug carve adds are checked where
    /// they are added. The planes, texinfos, texdatas and strings have no
    /// base: they are shared tables, and a room's entries are found by
    /// content when the assembly builds them (<see cref="LinkPlanes"/>,
    /// <see cref="LinkTextures"/>), and checked there.
    /// </remarks>
    private static void AssignBases(RoomPlan[] plans)
    {
        long vertices = 0, edges = 0, surfEdges = 0,
             faces = 0, origFaces = 0, brushes = 0, leafFaces = 0,
             leaves = 1, lighting = 0,
             primVerts = 0, primIndices = 0, prims = 0, vertNormals = 0, vertNormalIndices = 0,
             occluders = 0, occluderPolys = 0, occluderVerts = 0, overlays = 0;

        // The world faces of every placement come first, then every kept
        // brush model's, as a map's own model 0 range is its first faces.
        long modelFaces = plans.Sum(p => (long)p.WorldFaceCount);
        foreach (RoomPlan plan in plans)
        {
            if (plan.Models is not { } models)
            {
                continue;
            }

            models.LinkedFaceStart = new int[models.Linked.Length];
            for (int m = 0; m < models.Linked.Length; m++)
            {
                models.LinkedFaceStart[m] = models.Linked[m] < 0 ? -1 : (int)modelFaces;
                modelFaces += models.Linked[m] < 0 ? 0 : models.Source.Models[m].Faces.Count;
            }
        }

        foreach (RoomPlan plan in plans)
        {
            plan.VertexBase = (int)vertices;
            plan.EdgeBase = (int)edges;
            plan.FaceBase = (int)faces;
            plan.BrushBase = (int)brushes;
            plan.LeafFaceBase = (int)leafFaces;
            plan.LeafBase = (int)leaves;
            plan.LightBase = (int)lighting;
            plan.SurfEdgeBase = (int)surfEdges;
            plan.OrigFaceBase = (int)origFaces;
            plan.PrimBase = (int)prims;
            plan.PrimIndexBase = (int)primIndices;
            plan.PrimVertBase = (int)primVerts;
            plan.VertNormalBase = (int)vertNormals;
            plan.VertexNormalIndexBase = (int)vertNormalIndices;
            plan.OccluderBase = (int)occluders;
            plan.OccluderPolyBase = (int)occluderPolys;
            plan.OccluderVertexBase = (int)occluderVerts;
            plan.OverlayBase = (int)overlays;

            vertices += plan.Vertices.Length + (plan.Models?.LocalVertices.Length ?? 0);
            edges += plan.EdgeCount;
            faces += plan.WorldFaceCount;
            brushes += plan.KeptBrushCount;
            leafFaces += plan.KeptLeafFaceCount;
            leaves += plan.KeptLeafCount;
            lighting += plan.LightingLength;
            surfEdges += plan.SurfEdgeCount;
            origFaces += plan.OrigFaceCount;
            prims += plan.PrimCount;
            primIndices += plan.PrimIndexCount;
            primVerts += plan.PrimVertCount;
            vertNormals += plan.VertNormalCount;
            vertNormalIndices += plan.VertNormalIndexCount;
            occluders += plan.Occlusion?.Occluders.Count ?? 0;
            occluderPolys += plan.Occlusion?.Polys.Count ?? 0;
            occluderVerts += plan.Occlusion?.VertexIndices.Count ?? 0;
            overlays += plan.Overlays?.Count ?? 0;
        }
    }

    /// <summary>
    /// Refuses a level whose running totals outgrow a field of the format,
    /// before any room is planned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The totals are the ones <see cref="AssignBases"/> shifts by, summed in
    /// the same layout order from the same lumps each room's plan reads
    /// (<see cref="LinkCounts.Of"/>), so the refusal names the same room at
    /// the same total as a check made while assigning the bases would. It
    /// runs first because everything after it costs in the rooms: planning
    /// reads and transforms every room's structs, and a level hundreds of
    /// times too big spent minutes and gigabytes on that before being
    /// refused. This pass reads a handful of lump lengths per room.
    /// </para>
    /// <para>
    /// What the plug carve and the nodraw copies add during assembly (planes,
    /// texinfos, leaves, leaf brushes, nodes), and the top tree's nodes past
    /// their floor, are not known until then, and are checked where they are
    /// added. So are the rooms' own planes and texinfos: they are shared by
    /// content, and what a room shares depends on its cell
    /// (<see cref="CheckSharedTables"/>). The texdatas and strings are shared
    /// too, but do not depend on the cell, so they are counted here exactly,
    /// each room's the first time it is placed, with the same
    /// <see cref="LinkTextures"/> the assembly builds them with; a room with
    /// cubemap patches is counted at every placement, since each renames its
    /// patches to its own position (<see cref="PlacementCubemaps"/>), which
    /// is why the level's map name is wanted here. The cubemap samples are
    /// totalled here too, against <c>MAX_MAP_CUBEMAPSAMPLES</c>
    /// (<see cref="LevelCubemaps.Plan"/>).
    /// </para>
    /// <para>
    /// The entity budget is checked here too, after the lump totals
    /// (<see cref="LevelEntityBudget"/>): from each room's stored entity
    /// counts when it has them, else from its entity lump, which the counts
    /// equal.
    /// </para>
    /// </remarks>
    /// <param name="layout">The level, its rooms already known to be in the library.</param>
    /// <param name="library">The rooms, and the library's settings.</param>
    /// <param name="options">The entity budget's settings; null for <see cref="LevelLinkOptions.Default"/>.</param>
    /// <param name="mapBase">The linked map's name, which the rooms' cubemap patches are renamed with; empty when unknown.</param>
    /// <returns>The entity budget's report.</returns>
    /// <exception cref="LinkException">A total passes its field's limit, or the level passes the edict cap or the entity list's.</exception>
    internal static LevelEntityReport CheckCapacity(LevelLayout layout, RoomLibrary library, LevelLinkOptions? options = null, string mapBase = "")
    {
        options ??= LevelLinkOptions.Default;
        int reserve = LevelEntityBudget.ReserveFor(options, library.Options);
        EntityClassTable classes = options.EntityClasses ?? EntityClassTable.Default;
        List<(string, RoomEntityCounts)> placements = new(layout.Rooms.Count);
        if (layout.Rooms.Count > 0)
        {
            LinkTotals totals = new(checkBrushes: !options.FoldBrushes);
            Dictionary<string, RoomEntityCounts> counted = new(StringComparer.Ordinal);
            Dictionary<(string Room, int Socket), SocketCensus> censuses = [];
            LinkTextures textures = new();
            List<(RoomObject, RoomTransform)> placed = new(layout.Rooms.Count);
            foreach (RoomInstance instance in layout.Rooms)
            {
                RoomObject room = library.Get(instance.Placement.Room);
                placed.Add((room, new RoomTransform(instance.Placement, room.Definition.CellSize)));
            }

            // The cubemaps first: a room without its cubemap data, a patch
            // name too long and too many samples are refused here, before
            // anything is counted against them.
            LevelCubemaps? cubemaps = LevelCubemaps.Plan(placed, mapBase);
            for (int p = 0; p < layout.Rooms.Count; p++)
            {
                RoomInstance instance = layout.Rooms[p];
                RoomObject room = placed[p].Item1;
                string name = room.Definition.Name;
                int texDatas = textures.TexDatas.Count;
                int strings = textures.StringTable.Count;
                PlacementCubemaps? patches = cubemaps?.At(p);
                if (!counted.TryGetValue(name, out RoomEntityCounts? counts) || patches is { Strings.Count: > 0 })
                {
                    // The material tables are shared by content and a room's
                    // texdata does not depend on its placement, so only the
                    // first placement of a room can add to them; unless it
                    // has cubemap patches, which each placement renames.
                    textures.InternTexDatas(room.Bsp, textures.InternStrings(room.Bsp, name, patches?.Strings));
                    counts ??= counted[name] = room.CountEntities();
                }

                (int keptBrushes, int keptSides) = KeptBrushTotals(room, instance, censuses);
                LinkCounts compiled = LinkCounts.Of(room.Bsp, room.ClusterCount);
                LinkCounts added = compiled with
                {
                    // An origin-relative brush model's vertices are linked
                    // twice, the second copy in its entity's own frame.
                    Vertices = compiled.Vertices + LocalVertexCount(room),
                    TexDatas = textures.TexDatas.Count - texDatas,
                    StringTable = textures.StringTable.Count - strings,
                    Brushes = keptBrushes,
                    BrushSides = keptSides,
                };
                totals.Add(added, name, instance.Placement.CellX, instance.Placement.CellY);

                placements.Add((name, counts));
            }

            RoomInstance last = layout.Rooms[^1];
            totals.CheckClusters(library.Get(last.Placement.Room).Definition.Name, last.Placement.CellX, last.Placement.CellY);
        }

        return LevelEntityBudget.Check(placements, reserve, classes, LibraryCounts(library));
    }

    /// <summary>
    /// The brushes and brush sides one placement adds to the link: the room's
    /// own, less the plug brushes of the sockets the level joints
    /// (<see cref="KeptBrushes"/>), exactly what the assembly will write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The census that names the plug brushes depends on the room alone, so
    /// it is known before any room is planned: from the room's stored link
    /// data when it has some, else made here per socket, once per room and
    /// socket for the whole check (<paramref name="censuses"/>, which the
    /// caller drops when the check ends). Counting the rooms as compiled
    /// would refuse levels that load: on the stress library a quarter of the
    /// brushes are jointed plugs.
    /// </para>
    /// <para>
    /// A joint naming a socket the room does not have strips nothing here;
    /// the joint check that runs next refuses it by name.
    /// </para>
    /// </remarks>
    private static (int Brushes, int Sides) KeptBrushTotals(
        RoomObject room, RoomInstance instance, Dictionary<(string Room, int Socket), SocketCensus> censuses)
    {
        RoomDefinition definition = room.Definition;
        RoomLinkData? stored = StoredLink(room);
        HashSet<int> stripped = [];
        foreach ((string socketName, _) in instance.Joints)
        {
            int socket = -1;
            for (int s = 0; s < definition.Sockets.Count && socket < 0; s++)
            {
                socket = definition.Sockets[s].Name == socketName ? s : -1;
            }

            if (socket < 0)
            {
                continue;
            }

            SocketCensus census;
            if (stored is not null)
            {
                census = stored.Shared.Sockets[socket];
            }
            else if (!censuses.TryGetValue((definition.Name, socket), out census!))
            {
                census = CensusSocket(room, BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]), definition.Sockets[socket]);
                censuses[(definition.Name, socket)] = census;
            }

            stripped.UnionWith(census.StrippedBrushes);
        }

        (_, int brushes, int sides) = KeptBrushes(BspStructView.As<DBrush>(room.Bsp[BspLump.Brushes]), stripped);
        return (brushes, sides);
    }

    /// <summary>
    /// What one room adds to each total the format limits: the lengths of the
    /// lumps its plan appends, its clusters, and what it adds to the shared
    /// material tables.
    /// </summary>
    /// <remarks>
    /// The planes and texinfos are not here: they are shared by content
    /// (<see cref="LinkPlanes"/>, <see cref="LinkTextures"/>), and a room's
    /// share of them depends on its placement (the translation is in every
    /// plane distance and texture offset), so what a room adds is only known
    /// once it is moved, and the assembly checks them there. The texdatas
    /// and string-table entries are shared too, but a texdata does not
    /// depend on the placement, so <see cref="CheckCapacity"/> works out
    /// exactly what each room adds before any room is planned.
    /// </remarks>
    internal readonly record struct LinkCounts
    {
        public int Vertices { get; init; }

        public int Faces { get; init; }

        /// <summary>
        /// The brushes: <see cref="Of"/> counts the room's as compiled, and
        /// <see cref="CheckCapacity"/> replaces that with what the placement
        /// keeps once its jointed plugs are dropped (<see cref="KeptBrushTotals"/>).
        /// </summary>
        public int Brushes { get; init; }

        /// <summary>The sides of <see cref="Brushes"/>, counted the same way.</summary>
        public int BrushSides { get; init; }

        /// <summary>The texdatas no earlier room brought; not set by <see cref="Of"/>.</summary>
        public int TexDatas { get; init; }

        public int LeafFaces { get; init; }

        public int Leaves { get; init; }

        /// <summary>The string-table entries no earlier room brought; not set by <see cref="Of"/>.</summary>
        public int StringTable { get; init; }

        public int Primitives { get; init; }

        public int PrimitiveIndices { get; init; }

        public int PrimitiveVertices { get; init; }

        public int VertexNormals { get; init; }

        public int Nodes { get; init; }

        public int Clusters { get; init; }

        /// <summary>The room's overlays (<c>info_overlay</c> records), which the link appends as they are.</summary>
        public int Overlays { get; init; }

        /// <summary>
        /// A compiled room's counts of the lumps it appends, read as
        /// <see cref="PlanRoom"/> reads them; the shared tables' counts are
        /// the caller's to add.
        /// </summary>
        public static LinkCounts Of(BspData bsp, int clusters) => new()
        {
            Vertices = BspStructView.Count<Vec3>(bsp[BspLump.Vertexes]),
            Faces = BspStructView.Count<DFace>(bsp[BspLump.Faces]),
            Brushes = BspStructView.Count<DBrush>(bsp[BspLump.Brushes]),
            BrushSides = BspStructView.Count<DBrushSide>(bsp[BspLump.BrushSides]),
            LeafFaces = BspStructView.Count<ushort>(bsp[BspLump.LeafFaces]),
            Leaves = BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]),
            Primitives = BspStructView.Count<DPrimitive>(bsp[BspLump.Primitives]),
            PrimitiveIndices = BspStructView.Count<ushort>(bsp[BspLump.PrimIndices]),
            PrimitiveVertices = BspStructView.Count<Vec3>(bsp[BspLump.PrimVerts]),
            VertexNormals = BspStructView.Count<Vec3>(bsp[BspLump.VertNormals]),
            Nodes = BspStructView.Count<DNode>(bsp[BspLump.Nodes]),
            Clusters = clusters,
            Overlays = BspStructView.Count<DOverlay>(bsp[BspLump.Overlays]),
        };
    }

    /// <summary>
    /// The level's running totals, room by room in layout order, each refused
    /// the moment it passes the narrowest field that carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most limits are the narrowest field that holds an index into (or a
    /// count of) that lump: a leaf's cluster is <c>short</c>, an edge's vertices, a leaf's face and
    /// brush runs, a node's first face, a face's first primitive, a
    /// primitive's first index and vertex, a vertex-normal index and a macro
    /// texture's name id are <c>ushort</c>. A sum past its field would wrap
    /// silently in the cast that writes it and point into some other room.
    /// </para>
    /// <para>
    /// Three totals the engine's loader caps below their field's width
    /// (<see cref="BspLimits.Caps"/>, what <c>ssmap check</c> reports):
    /// texdatas, brushes and brush sides; and one it caps that no narrower
    /// field carries, the nodes. Every room brings its own brushes, so a
    /// level of a few hundred rooms passes <c>MAX_MAP_BRUSHES</c> (8192) long
    /// before any field fills, and the engine would refuse to load the map.
    /// The brushes counted are the ones the link writes: a jointed socket's
    /// plug brushes are dropped from the brush lump, so they are not counted
    /// (<see cref="KeptBrushTotals"/>).
    /// The texdatas are shared by content, so what counts toward
    /// <c>MAX_MAP_TEXDATA</c> (2048) is the level's distinct materials, which
    /// the caller works out (<see cref="LinkCounts.TexDatas"/>).
    /// </para>
    /// <para>
    /// The planes (a <c>ushort</c> in every face and brush side) and the
    /// texinfos (<c>MAX_MAP_TEXINFO</c>, 12,288) are not totalled here: they
    /// are shared by content, and how much a room shares depends on where it
    /// stands, so the assembly checks them exactly as it builds them
    /// (<see cref="CheckSharedTables"/>).
    /// </para>
    /// <para>
    /// The nodes are the fifth: a node's children are <c>int</c>, so no field
    /// fills, but the loader refuses more than <c>MAX_MAP_NODES</c> (65,536),
    /// and every room brings its whole tree. The linked tree is the top tree
    /// over the grid, then every room's nodes, then the chains the plug carve
    /// adds. The top tree is not built until assembly, but its size has a
    /// floor that needs no building: it is a full binary tree (a region that
    /// is not a single cell and not empty splits into two) whose leaves
    /// include one single-cell node per room, so it has at least
    /// 2 x rooms - 1 nodes. The total starts at -1 and each room adds its own
    /// nodes and 2. That keeps the check a lower bound, so it never refuses a
    /// level that would load; the exact total, top tree and carve chains
    /// included, is checked again once assembly has built them.
    /// </para>
    /// <para>
    /// The leaves start at 1 (the shared solid leaf), as the bases do.
    /// </para>
    /// <para>
    /// The overlays are a sixth kind of cap: no field narrower than their
    /// ids holds them, but vbsp refuses a map with more than
    /// <c>MAX_MAP_OVERLAYS</c> (512), so the flattened level would not
    /// compile (<see cref="OverlayLimit"/>).
    /// </para>
    /// </remarks>
    internal sealed class LinkTotals(bool checkBrushes = true)
    {
        private static int Cap(BspLump lump) => BspLimits.Caps.First(c => c.Lump == lump).Max;

        /// <summary>
        /// Whether the brush and brush side caps are held here. With the
        /// brush fold on they are not: the fold merges brushes during
        /// assembly, so the kept totals here are only an upper bound, and a
        /// level whose rooms bring more than the cap may well fold under it.
        /// The assembly holds the folded totals to the caps instead.
        /// </summary>
        private readonly bool _checkBrushes = checkBrushes;

        private readonly int _texDataCap = Cap(BspLump.TexData);
        private readonly int _brushCap = Cap(BspLump.Brushes);
        private readonly int _brushSideCap = Cap(BspLump.BrushSides);
        private readonly int _nodeCap = Cap(BspLump.Nodes);

        private long _vertices, _texDatas, _faces, _brushes, _brushSides, _leafFaces, _leaves = 1,
            _stringTable, _primitives, _primitiveIndices, _primitiveVertices, _vertexNormals, _clusters, _nodes = -1, _overlays;

        /// <summary>Adds one room, refusing the first total it pushes past its limit.</summary>
        public void Add(LinkCounts counts, string room, int cellX, int cellY)
        {
            _vertices += counts.Vertices;
            _texDatas += counts.TexDatas;
            _faces += counts.Faces;
            _brushes += counts.Brushes;
            _brushSides += counts.BrushSides;
            _leafFaces += counts.LeafFaces;
            _leaves += counts.Leaves;
            _stringTable += counts.StringTable;
            _primitives += counts.Primitives;
            _primitiveIndices += counts.PrimitiveIndices;
            _primitiveVertices += counts.PrimitiveVertices;
            _vertexNormals += counts.VertexNormals;
            _nodes += counts.Nodes + 2;
            _clusters += counts.Clusters;
            _overlays += counts.Overlays;

            Limit(room, cellX, cellY, "vertices", _vertices, ushort.MaxValue + 1);
            LoaderLimit(room, cellX, cellY, "texdatas", _texDatas, _texDataCap, "MAX_MAP_TEXDATA");
            Limit(room, cellX, cellY, "faces", _faces, ushort.MaxValue + 1);
            if (_checkBrushes)
            {
                LoaderLimit(room, cellX, cellY, "brushes", _brushes, _brushCap, "MAX_MAP_BRUSHES");
                LoaderLimit(room, cellX, cellY, "brush sides", _brushSides, _brushSideCap, "MAX_MAP_BRUSHSIDES");
            }
            Limit(room, cellX, cellY, "leaf faces", _leafFaces, ushort.MaxValue + 1);
            Limit(room, cellX, cellY, "leaves", _leaves, ushort.MaxValue + 1);
            Limit(room, cellX, cellY, "texdata string table entries", _stringTable, ushort.MaxValue);
            Limit(room, cellX, cellY, "primitives", _primitives, ushort.MaxValue + 1);
            Limit(room, cellX, cellY, "primitive indices", _primitiveIndices, ushort.MaxValue + 1);
            Limit(room, cellX, cellY, "primitive vertices", _primitiveVertices, ushort.MaxValue + 1);
            Limit(room, cellX, cellY, "vertex normals", _vertexNormals, ushort.MaxValue + 1);
            LoaderLimit(room, cellX, cellY, "nodes", _nodes, _nodeCap, "MAX_MAP_NODES");
            OverlayLimit(room, cellX, cellY, _overlays);
        }

        /// <summary>
        /// Refuses a cluster total past a leaf's <c>short</c> cluster field;
        /// checked once, after the last room, which the refusal names.
        /// </summary>
        public void CheckClusters(string room, int cellX, int cellY) =>
            Limit(room, cellX, cellY, "clusters", _clusters, short.MaxValue);
    }

    /// <summary>Refuses a count past what its field can carry.</summary>
    /// <param name="plan">The room whose addition crossed it, for the message.</param>
    /// <param name="what">The count's name.</param>
    /// <param name="count">The running total.</param>
    /// <param name="max">One past the largest total the field holds.</param>
    /// <exception cref="LinkException">The total reaches <paramref name="max"/>.</exception>
    internal static void Limit(RoomPlan plan, string what, long count, long max) =>
        Limit(
            plan.Placement.Room.Definition.Name,
            plan.Placement.Instance.Placement.CellX,
            plan.Placement.Instance.Placement.CellY,
            what,
            count,
            max);

    /// <summary>
    /// Refuses a count past what the engine's loader accepts
    /// (<see cref="BspLimits.Caps"/>), naming the room and cell that crossed
    /// it and the loader's constant.
    /// </summary>
    internal static void LoaderLimit(string room, int cellX, int cellY, string what, long count, long max, string constant)
    {
        if (count > max)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {count} {what};"
                + $" the engine loads at most {max} ({constant}).");
        }
    }

    /// <summary>
    /// Refuses a visibility lump past <c>MAX_MAP_VISIBILITY</c>, which the
    /// loader caps on its bytes; names the last room, since every room's rows
    /// are in it.
    /// </summary>
    /// <remarks>
    /// A linked level's rows are the closure of its whole door graph, and
    /// every room of a level is reachable, so the rows are close to full and
    /// the lump grows with the square of the clusters: 8.9 MB at 6,000.
    /// Run-length compression only shortens runs of zeros.
    /// </remarks>
    internal static void LimitVisibility(string room, int cellX, int cellY, int bytes) =>
        LoaderLimit(room, cellX, cellY, "visibility bytes", bytes, BspLimits.MaxMapVisibilityBytes, "MAX_MAP_VISIBILITY");

    /// <summary>Refuses a count past what its field can carry, naming the room and cell that crossed it.</summary>
    private static void Limit(string room, int cellX, int cellY, string what, long count, long max)
    {
        if (count > max)
        {
            throw new LinkException(
                $"room {room} at cell ({cellX}, {cellY}) pushes the link to {count} {what};"
                + $" the format carries at most {max}.");
        }
    }

    /// <summary>
    /// The visibility the link wrote before door visibility, and still
    /// writes with <see cref="LevelLinkOptions.DoorVisibility"/> off: each
    /// room's own rows, every cluster facing a joint joined to every cluster
    /// facing it from the other side, and the transitive closure. Every room
    /// of a level is reachable, so every cluster sees every other; the PAS
    /// is the PVS, which is already closed.
    /// </summary>
    private static LevelVisibility DoorGraphVisibility(
        ResolvedPlacement[] resolved, RoomPlan[] plans, int clusterCount, CancellationToken cancellationToken)
    {
        int rowBytes = (clusterCount + 7) >> 3;
        byte[][] rows = new byte[clusterCount][];
        foreach (RoomPlan plan in plans)
        {
            for (int c = 0; c < plan.ClusterCount; c++)
            {
                rows[plan.ClusterBase + c] = ShiftRow(plan.OwnRows[c], plan.ClusterBase, rowBytes);
                OrBit(rows[plan.ClusterBase + c], plan.ClusterBase + c);
            }
        }

        foreach ((RoomPlan a, RoomPlan b, int[] ca, int[] cb) in DoorEdges(resolved, plans))
        {
            foreach (int x in ca)
            {
                foreach (int y in cb)
                {
                    OrBit(rows[a.ClusterBase + x], b.ClusterBase + y);
                    OrBit(rows[b.ClusterBase + y], a.ClusterBase + x);
                }
            }
        }

        CloseRows(rows, clusterCount, cancellationToken);

        byte[] pvs = new byte[clusterCount * rowBytes];
        int totalVisible = 0;
        for (int c = 0; c < clusterCount; c++)
        {
            rows[c].CopyTo(pvs, c * rowBytes);
            totalVisible += PopCount(rows[c]);
        }

        return new LevelVisibility(pvs, (byte[])pvs.Clone(), rowBytes, totalVisible, totalVisible);
    }

    /// <summary>
    /// The placed rooms as the door visibility reads them: cluster bases,
    /// door visibility, own rows, placement, and every jointed socket with
    /// its neighbour, the doorway on the shared cell face and the plug box
    /// the link carves, in world coordinates.
    /// </summary>
    /// <remarks>
    /// The doorway is the plug box's face on the cell face, where the two
    /// rooms' plugs meet (<see cref="ValidateJoints"/> checked they meet
    /// head-on); the two sides' faces are joined, so a kit whose two plugs
    /// ever differed would still pass every line either lets through.
    /// </remarks>
    internal static LevelDoorRoom[] DoorRooms(ResolvedPlacement[] resolved, RoomPlan[] plans)
    {
        Dictionary<(int X, int Y), ResolvedPlacement> byCell = new(resolved.Length);
        foreach (ResolvedPlacement placement in resolved)
        {
            byCell[(placement.Instance.Placement.CellX, placement.Instance.Placement.CellY)] = placement;
        }

        LevelDoorRoom[] rooms = new LevelDoorRoom[plans.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            RoomPlan plan = plans[i];
            ResolvedPlacement a = resolved[i];
            List<LevelDoor> joints = [];
            foreach ((string socketName, string neighborSocket) in a.Instance.Joints)
            {
                RoomSocket mine = Socket(a.Room, socketName);
                (RoomPlan other, RoomSocket theirs, int j) = Neighbor(a, mine, neighborSocket, byCell, plans);
                int s = SocketIndex(a.Room.Definition, mine.Name);
                int t = SocketIndex(other.Placement.Room.Definition, theirs.Name);
                Box plug = plan.Transform.TranslateBox(plan.Geometry.PlugBoxes[s]);
                Box face = CellFace(plug, plan.Transform.WorldNormal(mine.Facing));
                Box otherFace = CellFace(
                    other.Transform.TranslateBox(other.Geometry.PlugBoxes[t]), other.Transform.WorldNormal(theirs.Facing));
                Box opening = RoomDoorVisibility.Union(face, otherFace);
                (int axis, _) = plan.Transform.WorldNormal(mine.Facing);
                opening = axis == 0
                    ? new Box(new Vec3(face.Mins.X, opening.Mins.Y, opening.Mins.Z), new Vec3(face.Mins.X, opening.Maxs.Y, opening.Maxs.Z))
                    : new Box(new Vec3(opening.Mins.X, face.Mins.Y, opening.Mins.Z), new Vec3(opening.Maxs.X, face.Mins.Y, opening.Maxs.Z));
                joints.Add(new LevelDoor(s, j, t, opening, plug, plan.JointFacing[socketName]));
            }

            rooms[i] = new LevelDoorRoom
            {
                ClusterBase = plan.ClusterBase,
                Doors = plan.DoorVisibility,
                OwnRows = plan.OwnRows,
                Transform = plan.Transform,
                Joints = [.. joints],
            };
        }

        return rooms;
    }

    /// <summary>A world plug box's face on its cell face: the box flattened to its outer side along the socket's world normal.</summary>
    private static Box CellFace(Box plug, (int Axis, int Sign) normal)
    {
        (int axis, int sign) = normal;
        if (axis == 0)
        {
            float x = sign > 0 ? plug.Maxs.X : plug.Mins.X;
            return new Box(new Vec3(x, plug.Mins.Y, plug.Mins.Z), new Vec3(x, plug.Maxs.Y, plug.Maxs.Z));
        }

        float y = sign > 0 ? plug.Maxs.Y : plug.Mins.Y;
        return new Box(new Vec3(plug.Mins.X, y, plug.Mins.Z), new Vec3(plug.Maxs.X, y, plug.Maxs.Z));
    }

    /// <summary>The joint graph's door edges: per joint, the clusters facing each side.</summary>
    /// <remarks>
    /// A joint's two plug boxes meet inside the wall; the clusters joined are
    /// the open leaves whose boxes overlap each side's plug box within
    /// <see cref="DoorOverlapEpsilon"/> (touching counts: the integer link
    /// geometry shares the face at gap 0). The comparison is room-local on
    /// both sides — each room's plug box against that room's own compiled
    /// leaves — and <see cref="ValidateJoints"/> already proved the two plugs
    /// meet in world space. Deterministic: layout order, cluster order.
    /// </remarks>
    internal static IEnumerable<(RoomPlan A, RoomPlan B, int[] FacingA, int[] FacingB)> DoorEdges(
        ResolvedPlacement[] resolved, RoomPlan[] plans)
    {
        Dictionary<(int X, int Y), ResolvedPlacement> byCell = new(resolved.Length);
        foreach (ResolvedPlacement placement in resolved)
        {
            byCell[(placement.Instance.Placement.CellX, placement.Instance.Placement.CellY)] = placement;
        }

        for (int i = 0; i < resolved.Length; i++)
        {
            ResolvedPlacement a = resolved[i];
            foreach ((string socket, string neighborSocket) in a.Instance.Joints)
            {
                RoomSocket aSocket = Socket(a.Room, socket);
                (RoomPlan planB, RoomSocket bSocket, int _) = Neighbor(a, aSocket, neighborSocket, byCell, plans);
                yield return (plans[i], planB, plans[i].JointFacing[aSocket.Name], planB.JointFacing[bSocket.Name]);
            }
        }
    }

    /// <summary>The open clusters whose leaf boxes overlap a plug box.</summary>
    internal static int[] Facing(RoomPlan plan, Box plug) => Facing(plan.Leafs, plug);

    /// <summary>The open clusters whose leaf boxes overlap a plug box.</summary>
    /// <param name="leafs">The room's own leaves, room-local.</param>
    /// <param name="plug">The plug box, room-local.</param>
    /// <returns>The clusters, sorted and distinct.</returns>
    internal static int[] Facing(ReadOnlySpan<DLeaf> leafs, Box plug)
    {
        List<int> clusters = [];
        foreach (DLeaf leaf in leafs)
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            if (BoxOf(leaf).Overlaps(plug, DoorOverlapEpsilon))
            {
                clusters.Add(leaf.Cluster);
            }
        }

        clusters.Sort();
        Dedupe(clusters);
        return [.. clusters];
    }

    /// <summary>Walks the finalized linked map to the leaf holding a point, as the engine does.</summary>
    /// <remarks>
    /// The engine's walk: for an axial plane (<see cref="DPlane.Type"/> 0..2)
    /// it reads the one coordinate and subtracts the distance, assuming the
    /// normal is the positive axis; otherwise the full dot. That shortcut is
    /// why the relocation keeps every node on a positive axial plane (see
    /// <c>TransformPlanes</c>): a node left on a -x plane would be walked as if
    /// it were +x. A point on the plane's negative side takes
    /// <c>Children[1]</c>.
    /// </remarks>
    internal static int PointInLeaf(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        int index = 0; // model 0's head node: the top tree's root
        while (index >= 0)
        {
            DNode node = nodes[index];
            DPlane plane = planes[node.PlaneNum];
            float d = plane.Type switch
            {
                0 => point.X - plane.Dist,
                1 => point.Y - plane.Dist,
                2 => point.Z - plane.Dist,
                _ => Vec3.Dot(point, plane.Normal) - plane.Dist,
            };
            index = d < 0 ? node.Children[1] : node.Children[0];
        }

        return ~index;
    }

    /// <summary>The cluster of the leaf holding a point (<see cref="PointInLeaf"/>).</summary>
    internal static int PointInLeafCluster(BspData bsp, Vec3 point) =>
        BspStructView.As<DLeaf>(bsp[BspLump.Leafs])[PointInLeaf(bsp, point)].Cluster;

    /// <summary>The 8 transformed corners of a room-local box.</summary>
    internal static Vec3[] TakeCorners(RoomTransform transform, Vec3 mins, Vec3 maxs)
    {
        Vec3[] corners = new Vec3[8];
        for (int b = 0; b < 8; b++)
        {
            corners[b] = transform.Apply(new Vec3(
                (b & 1) == 0 ? mins.X : maxs.X,
                (b & 2) == 0 ? mins.Y : maxs.Y,
                (b & 4) == 0 ? mins.Z : maxs.Z));
        }

        return corners;
    }

    /// <summary>A room-local box through the transform: the corners' bounds.</summary>
    /// <remarks>
    /// A quarter turn maps an axis-aligned box onto an axis-aligned box, so
    /// the bounds of the eight moved corners are exactly the moved box —
    /// integer in, integer out.
    /// </remarks>
    internal static Box MoveBox(RoomTransform transform, Vec3 mins, Vec3 maxs)
    {
        Vec3[] corners = TakeCorners(transform, mins, maxs);
        Vec3 lo = corners[0];
        Vec3 hi = corners[0];
        foreach (Vec3 c in corners)
        {
            lo = new Vec3(Math.Min(lo.X, c.X), Math.Min(lo.Y, c.Y), Math.Min(lo.Z, c.Z));
            hi = new Vec3(Math.Max(hi.X, c.X), Math.Max(hi.Y, c.Y), Math.Max(hi.Z, c.Z));
        }

        return new Box(lo, hi);
    }

    /// <summary>
    /// A room-local box turned by a quarter-turn count and not moved: the
    /// bounds of its eight turned corners, folded in the order
    /// <see cref="MoveBox"/> folds them.
    /// </summary>
    /// <remarks>
    /// What the room compile stores for every box the link moves (node and
    /// leaf bounds, occluders, the world model, the plugs, the worldspawn
    /// extent); the link then only adds the cell
    /// (<see cref="RoomTransform.TranslateBox"/>).
    /// </remarks>
    internal static Box RotateBox(Vec3 mins, Vec3 maxs, int rotation)
    {
        Vec3 lo = default, hi = default;
        for (int b = 0; b < 8; b++)
        {
            Vec3 c = RoomTransform.Rotate(
                new Vec3(
                    (b & 1) == 0 ? mins.X : maxs.X,
                    (b & 2) == 0 ? mins.Y : maxs.Y,
                    (b & 4) == 0 ? mins.Z : maxs.Z),
                rotation);
            if (b == 0)
            {
                lo = hi = c;
                continue;
            }

            lo = new Vec3(Math.Min(lo.X, c.X), Math.Min(lo.Y, c.Y), Math.Min(lo.Z, c.Z));
            hi = new Vec3(Math.Max(hi.X, c.X), Math.Max(hi.Y, c.Y), Math.Max(hi.Z, c.Z));
        }

        return new Box(lo, hi);
    }

    /// <summary>The grid's cell-face split planes, in the order the top tree emits them.</summary>
    internal static List<Plane> TopPlanes(LevelLayout layout, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(layout);
        List<Plane> planes = [];
        BuildTopNodes(layout, cellSize, planes);
        return planes;
    }

    /// <summary>
    /// Builds the top tree: a split tree over the occupied rectangle whose
    /// planes are the grid's cell faces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The region shrinks recursively: a multi-cell region splits at the
    /// longest axis's median cell face; the front side (the positive-normal
    /// side, which the reader takes with a non-negative distance) holds the
    /// higher coordinates, the back the lower. A single occupied cell splits at
    /// its +x cell face: the cell interior falls back into that cell's room
    /// root, the outside falls to the shared solid leaf. An empty region is
    /// solid. A point inside a cell therefore descends to exactly that cell's
    /// room root, and the room's own tree — carried from vbsp — does the rest.
    /// </para>
    /// <para>
    /// Every node bounds its region, planes are recorded in node order and
    /// each node names its plane as pair <c>i</c> of that list (plane number
    /// <c>2i</c>), which the caller replaces with the plane's pair in the
    /// shared table (<see cref="LinkPlanes"/>; the format demands pairs:
    /// <c>(x &amp; ~1)</c> and <c>(x &amp; ~1) + 1</c> are each other's flip,
    /// positive normal first), and child node indices name nodes already
    /// built (pre-order: a parent's children have larger indices than it, and
    /// the root is node 0, which is what makes <c>model0.HeadNode = 0</c>).
    /// </para>
    /// </remarks>
    internal static List<DNode> BuildTopNodes(LevelLayout layout, float cellSize, List<Plane> planes)
    {
        Dictionary<(int, int), int> occupants = [];
        int order = 0;
        foreach (RoomInstance room in layout.Rooms)
        {
            occupants[(room.Placement.CellX, room.Placement.CellY)] = order++;
        }

        (int minx, int miny, int maxx, int maxy) = Extent(layout);
        List<DNode> nodes = [];
        BuildRegion(nodes, occupants, (minx, miny, maxx, maxy), cellSize, planes);
        return nodes;
    }

    /// <summary>The room-local rows of one room, shifted into the linked cluster space.</summary>
    internal static byte[] ShiftRow(ReadOnlySpan<byte> own, int clusterBase, int rowBytes)
    {
        byte[] row = new byte[rowBytes];
        for (int c = 0; c < own.Length * 8; c++)
        {
            if ((own[c >> 3] & (1 << (c & 7))) != 0)
            {
                OrBit(row, clusterBase + c);
            }
        }

        return row;
    }

    internal static void OrBit(byte[] row, int bit) => row[bit >> 3] |= (byte)(1 << (bit & 7));

    internal static int PopCount(ReadOnlySpan<byte> row)
    {
        int count = 0;
        foreach (byte b in row)
        {
            count += System.Numerics.BitOperations.PopCount(b);
        }

        return count;
    }

    /// <summary>The transitive closure of the rows, in place.</summary>
    /// <remarks>
    /// <para>
    /// Row <c>i</c> becomes the union of the rows of every cluster <c>i</c>
    /// reaches, itself included: exactly Warshall's pivot-by-pivot closure
    /// (<c>rows[i] |= rows[k]</c> wherever <c>i</c> sees <c>k</c>), which is
    /// what the linker first computed and what a fact still compares against.
    /// Warshall is cubic in the clusters, though: ~2 s at 6,000 clusters and
    /// minutes at the 32,767 the format allows, and it was the link's longest
    /// loop. So the closure is built from the graph's strongly connected
    /// components instead (Tarjan's, iterative so a long chain of clusters
    /// cannot overflow the stack). Every cluster of a component reaches the
    /// same clusters, so a component's row is the union of its members' own
    /// rows and of the rows of the components it has an edge into, and Tarjan
    /// completes a component only after every component it reaches. The cost
    /// is the edges plus one row-width union per edge between components; a
    /// linked level, whose rooms all join up, is one component.
    /// </para>
    /// <para>
    /// The walk visits clusters, and each cluster's edges, in cluster order,
    /// and the result is a function of the graph alone, so it is the same
    /// byte on one thread or thirty-two (I4). It observes the token once per
    /// cluster it starts from and once per component it completes.
    /// </para>
    /// </remarks>
    internal static void CloseRows(byte[][] rows, int clusterCount, CancellationToken cancellationToken)
    {
        if (clusterCount == 0)
        {
            return;
        }

        int words = ((clusterCount - 1) >> 5) + 1;
        uint[][] bits = new uint[clusterCount][];
        for (int i = 0; i < clusterCount; i++)
        {
            bits[i] = ToWords(rows[i], words);
        }

        int[] order = new int[clusterCount];   // Tarjan's visit number, -1 until visited
        int[] low = new int[clusterCount];
        int[] component = new int[clusterCount];
        bool[] onStack = new bool[clusterCount];
        int[] stack = new int[clusterCount];
        int[] frameNode = new int[clusterCount];
        int[] frameWord = new int[clusterCount];
        uint[] frameMask = new uint[clusterCount];
        List<uint[]> reach = [];
        List<int> lastUnion = [];               // per component: the last component that unioned it in
        Array.Fill(order, -1);
        int visited = 0, stackTop = 0;

        for (int root = 0; root < clusterCount; root++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (order[root] >= 0)
            {
                continue;
            }

            int depth = 0;
            Enter(root);
            while (depth > 0)
            {
                int v = frameNode[depth - 1];
                while (frameMask[depth - 1] == 0 && frameWord[depth - 1] + 1 < words)
                {
                    frameWord[depth - 1]++;
                    frameMask[depth - 1] = bits[v][frameWord[depth - 1]];
                }

                if (frameMask[depth - 1] != 0)
                {
                    uint mask = frameMask[depth - 1];
                    int w = (frameWord[depth - 1] << 5) + System.Numerics.BitOperations.TrailingZeroCount(mask);
                    frameMask[depth - 1] = mask & (mask - 1);
                    if (w >= clusterCount)
                    {
                        continue; // a bit past the clusters is carried by the unions, not walked
                    }

                    if (order[w] < 0)
                    {
                        Enter(w);
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], order[w]);
                    }

                    continue;
                }

                // Every edge of v is walked: close its component if it roots one.
                if (low[v] == order[v])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int id = reach.Count;
                    uint[] union = new uint[words];
                    int first = stackTop;
                    do
                    {
                        first--;
                        component[stack[first]] = id;
                        onStack[stack[first]] = false;
                    }
                    while (stack[first] != v);

                    reach.Add(union);
                    lastUnion.Add(-1);
                    for (int m = first; m < stackTop; m++)
                    {
                        uint[] own = bits[stack[m]];
                        for (int x = 0; x < words; x++)
                        {
                            union[x] |= own[x];
                        }
                    }

                    // The components this one has an edge into, each once;
                    // all of them are complete, so their unions are final.
                    for (int m = first; m < stackTop; m++)
                    {
                        uint[] own = bits[stack[m]];
                        for (int x = 0; x < words; x++)
                        {
                            for (uint e = own[x]; e != 0; e &= e - 1)
                            {
                                int w = (x << 5) + System.Numerics.BitOperations.TrailingZeroCount(e);
                                if (w >= clusterCount || component[w] == id || lastUnion[component[w]] == id)
                                {
                                    continue;
                                }

                                lastUnion[component[w]] = id;
                                uint[] theirs = reach[component[w]];
                                for (int y = 0; y < words; y++)
                                {
                                    union[y] |= theirs[y];
                                }
                            }
                        }
                    }

                    stackTop = first;
                }

                depth--;
                if (depth > 0)
                {
                    int parent = frameNode[depth - 1];
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }

            void Enter(int node)
            {
                order[node] = low[node] = visited++;
                stack[stackTop++] = node;
                onStack[node] = true;
                frameNode[depth] = node;
                frameWord[depth] = 0;
                frameMask[depth] = bits[node][0];
                depth++;
            }
        }

        for (int i = 0; i < clusterCount; i++)
        {
            rows[i] = ToBytes(reach[component[i]], rows[i].Length);
        }
    }

    private static uint[] ToWords(byte[] row, int words)
    {
        uint[] w = new uint[words];
        for (int b = 0; b < row.Length; b++)
        {
            w[b >> 2] |= (uint)row[b] << (8 * (b & 3));
        }

        return w;
    }

    private static byte[] ToBytes(uint[] words, int length)
    {
        byte[] row = new byte[length];
        for (int b = 0; b < length; b++)
        {
            row[b] = (byte)(words[b >> 2] >> (8 * (b & 3)));
        }

        return row;
    }

    /// <summary>Assembles LUMP_VISIBILITY the way vvis does: header, PVS rows, PAS rows.</summary>
    private static byte[] BuildVisibilityLump(int clusters, int rowBytes, byte[] pvs, byte[] pas)
    {
        int headerBytes = sizeof(int) + (clusters * 2 * sizeof(int));
        byte[] scratch = new byte[VisRunLength.MaxCompressedLength(rowBytes)];
        List<byte> body = new(clusters * rowBytes);
        int[] pvsOffset = new int[clusters];
        int[] pasOffset = new int[clusters];

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            pvsOffset[cluster] = headerBytes + body.Count;
            int written = VisRunLength.Compress(pvs.AsSpan(cluster * rowBytes, rowBytes), scratch);
            body.AddRange(scratch.AsSpan(0, written));
        }

        for (int cluster = 0; cluster < clusters; cluster++)
        {
            pasOffset[cluster] = headerBytes + body.Count;
            int written = VisRunLength.Compress(pas.AsSpan(cluster * rowBytes, rowBytes), scratch);
            body.AddRange(scratch.AsSpan(0, written));
        }

        byte[] lump = new byte[headerBytes + body.Count];
        BinaryPrimitives.WriteInt32LittleEndian(lump, clusters);
        for (int cluster = 0; cluster < clusters; cluster++)
        {
            int at = sizeof(int) + (cluster * 2 * sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(at, 4), pvsOffset[cluster]);
            BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(at + 4, 4), pasOffset[cluster]);
        }

        body.CopyTo(lump, headerBytes);
        return lump;
    }

    // ---- validation ----------------------------------------------------

    /// <summary>Refuses joints the rooms cannot physically satisfy.</summary>
    /// <remarks>
    /// Each joint is checked from its owner: the socket exists; the neighbour
    /// cell holds a room; that room has the named socket; and the two sockets
    /// meet head-on — the owner's socket's world normal is the exact negation
    /// of the neighbour's. Caps are checked to name real sockets (the linter
    /// checked coverage; an unknown cap name would otherwise pass unnoticed).
    /// A joint whose neighbour caps the facing socket is caught here too: the
    /// cap keeps the socket's plug and the owner's joint would open onto it.
    /// </remarks>
    internal static void ValidateJoints(LevelLayout layout, RoomLibrary library)
    {
        // Each joint's neighbour is found by its cell. A scan of every room
        // per joint made this pass quadratic in the rooms, and it runs before
        // the level is measured against the format's limits, so a level far
        // too big to link spent minutes here before being refused. The layout
        // was validated first, so no two rooms share a cell and the lookup
        // finds exactly the room the scan did.
        Dictionary<(int X, int Y), RoomInstance> byCell = new(layout.Rooms.Count);
        foreach (RoomInstance room in layout.Rooms)
        {
            byCell[(room.Placement.CellX, room.Placement.CellY)] = room;
        }

        foreach (RoomInstance instance in layout.Rooms)
        {
            RoomObject room = library.Get(instance.Placement.Room);
            foreach (string capped in instance.Capped)
            {
                _ = Socket(room, capped);
            }

            foreach ((string socket, string neighborSocket) in instance.Joints)
            {
                RoomSocket mine = Socket(room, socket);
                RoomTransform transform = new(instance.Placement, layout.CellSize);
                (int axis, int sign) = transform.WorldNormal(mine.Facing);
                int nx = instance.Placement.CellX;
                int ny = instance.Placement.CellY;
                if (axis == 0)
                {
                    nx += sign;
                }
                else
                {
                    ny += sign;
                }

                if (!byCell.TryGetValue((nx, ny), out RoomInstance? neighbour))
                {
                    throw new LinkException(
                        $"the joint at cell ({instance.Placement.CellX}, {instance.Placement.CellY})"
                        + $" socket \"{socket}\" has no neighbour room at cell ({nx}, {ny}).");
                }

                RoomObject neighbourRoom = library.Get(neighbour.Placement.Room);
                RoomSocket theirs = Socket(neighbourRoom, neighborSocket, neighbour);
                if (neighbour.Capped.Contains(neighborSocket))
                {
                    throw new LinkException(
                        $"the joint at cell ({instance.Placement.CellX}, {instance.Placement.CellY})"
                        + $" socket \"{socket}\" meets cell ({nx}, {ny}), which caps socket \"{neighborSocket}\".");
                }

                RoomTransform theirTransform = new(neighbour.Placement, layout.CellSize);
                (int theirAxis, int theirSign) = theirTransform.WorldNormal(theirs.Facing);
                if (theirAxis != axis || theirSign != -sign)
                {
                    throw new LinkException(
                        $"the joint at cell ({instance.Placement.CellX}, {instance.Placement.CellY})"
                        + $" socket \"{socket}\" meets cell ({nx}, {ny}) socket \"{neighborSocket}\""
                        + " from the wrong side: the two sockets do not face each other on the shared wall.");
                }
            }
        }
    }

    private static RoomSocket Socket(RoomObject room, string name, RoomInstance? instance = null)
    {
        foreach (RoomSocket socket in room.Definition.Sockets)
        {
            if (socket.Name == name)
            {
                return socket;
            }
        }

        throw new LinkException(
            $"room {room.Definition.Name}"
            + (instance is null ? string.Empty : $" at cell ({instance.Placement.CellX}, {instance.Placement.CellY})")
            + $" has no socket \"{name}\".");
    }

    private static ResolvedPlacement Resolve(RoomInstance instance, int index, RoomLibrary library)
    {
        RoomObject room = library.Get(instance.Placement.Room);
        RoomTransform transform = new(instance.Placement, library.CellSize);
        return new ResolvedPlacement(
            instance,
            room,
            index,
            transform.Apply(Vec3.Zero),
            instance.Placement.NormalizedRotation,
            room.SealClusters);
    }

    /// <summary>The room across a joint, found by its cell (<paramref name="byCell"/>: every placement by cell).</summary>
    private static (RoomPlan Plan, RoomSocket Socket, int Index) Neighbor(
        ResolvedPlacement a,
        RoomSocket mine,
        string neighborSocket,
        Dictionary<(int X, int Y), ResolvedPlacement> byCell,
        RoomPlan[] plans)
    {
        RoomTransform transform = new(a.Instance.Placement, a.Room.Definition.CellSize);
        (int axis, int sign) = transform.WorldNormal(mine.Facing);
        int nx = a.Instance.Placement.CellX;
        int ny = a.Instance.Placement.CellY;
        if (axis == 0)
        {
            nx += sign;
        }
        else
        {
            ny += sign;
        }

        if (byCell.TryGetValue((nx, ny), out ResolvedPlacement? other))
        {
            return (plans[other.Index], Socket(other.Room, neighborSocket, other.Instance), other.Index);
        }

        throw new LinkException($"no room at cell ({nx}, {ny})"); // ValidateJoints refused this already
    }

    // ---- the top tree --------------------------------------------------

    /// <summary>The child marker for a room-bearing cell (resolved at assembly).</summary>
    private const int MarkerRoomLeaf = -1000;

    /// <summary>The child marker for a solid region (resolved at assembly).</summary>
    private const int MarkerSolidLeaf = -1001;

    /// <summary>Fills the top tree's children now that the room bases exist.</summary>
    private static void FillTopChildren(List<DNode> nodes, int topCount, RoomPlan[] plans, LevelLayout layout)
    {
        Dictionary<(int, int), RoomPlan> byCell = [];
        foreach (RoomPlan plan in plans)
        {
            byCell[(plan.Placement.Instance.Placement.CellX, plan.Placement.Instance.Placement.CellY)] = plan;
        }

        // The nodes list already holds the top tree built by BuildTopNodes;
        // its room-bearing single-cell nodes carry a marker child that names
        // no node yet. Rebuild the regions with the same recursion the node
        // list itself used — see BuildRegion — so both walks agree.
        Dictionary<(int, int), int> occupants = [];
        foreach (RoomInstance room in layout.Rooms)
        {
            occupants[(room.Placement.CellX, room.Placement.CellY)] = 0;
        }

        List<(int, int, int, int)> regions = [];
        CollectRegions(occupants, Extent(layout), regions);
        if (regions.Count != topCount)
        {
            throw new LinkException("the top-tree region walk disagrees with the top node list");
        }

        int index = 0;
        foreach (ref DNode node in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nodes)[..topCount])
        {
            (int rminx, int rminy, _, _) = regions[index];
            IntArray2 children = node.Children;
            for (int side = 0; side < 2; side++)
            {
                int child = node.Children[side];
                if (child == MarkerRoomLeaf)
                {
                    children[side] = byCell[(rminx, rminy)].NodeBase;
                }
                else if (child == MarkerSolidLeaf)
                {
                    children[side] = -(0 + 1); // the shared solid leaf
                }
            }

            node.Children = children;
            index++;
        }
    }

    private static void CollectRegions(
        Dictionary<(int, int), int> occupants,
        (int minx, int miny, int maxx, int maxy) rect,
        List<(int, int, int, int)> regions)
    {
        int width = rect.maxx - rect.minx + 1;
        int height = rect.maxy - rect.miny + 1;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        regions.Add(rect);

        // Mirror BuildRegion exactly, including its stop conditions: an empty
        // region is one solid node with no children, and a 1×1 occupied cell is
        // one room node with no children, so the walk stops at both.
        if (!HasOccupant(occupants, rect) || (rect.minx == rect.maxx && rect.miny == rect.maxy))
        {
            return;
        }

        if (width >= height)
        {
            int split = rect.minx + width / 2;
            CollectRegions(occupants, (split, rect.miny, rect.maxx, rect.maxy), regions);
            CollectRegions(occupants, (rect.minx, rect.miny, split - 1, rect.maxy), regions);
        }
        else
        {
            int split = rect.miny + height / 2;
            CollectRegions(occupants, (rect.minx, split, rect.maxx, rect.maxy), regions);
            CollectRegions(occupants, (rect.minx, rect.miny, rect.maxx, split - 1), regions);
        }
    }

    private static bool HasOccupant(Dictionary<(int, int), int> occupants, (int minx, int miny, int maxx, int maxy) rect)
    {
        for (int x = rect.minx; x <= rect.maxx; x++)
        {
            for (int y = rect.miny; y <= rect.maxy; y++)
            {
                if (occupants.ContainsKey((x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (int minx, int miny, int maxx, int maxy) Extent(LevelLayout layout)
    {
        int minx = int.MaxValue, miny = int.MaxValue, maxx = int.MinValue, maxy = int.MinValue;
        foreach (RoomInstance room in layout.Rooms)
        {
            minx = Math.Min(minx, room.Placement.CellX);
            miny = Math.Min(miny, room.Placement.CellY);
            maxx = Math.Max(maxx, room.Placement.CellX);
            maxy = Math.Max(maxy, room.Placement.CellY);
        }

        return (minx, miny, maxx, maxy);
    }

    private static int BuildRegion(
        List<DNode> nodes,
        Dictionary<(int, int), int> occupants,
        (int minx, int miny, int maxx, int maxy) rect,
        float cellSize,
        List<Plane> planes)
    {
        int index = nodes.Count;
        nodes.Add(default);

        Vec3 mins = new(rect.minx * cellSize, rect.miny * cellSize, 0);
        Vec3 maxs = new((rect.maxx + 1) * cellSize, (rect.maxy + 1) * cellSize, cellSize);

        if (!HasOccupant(occupants, rect))
        {
            // A solid region: split at an arbitrary cell face and send both
            // sides to the shared solid leaf.
            planes.Add(new Plane(new Vec3(1, 0, 0), (rect.maxx + 1) * cellSize));
            IntArray2 solidChildren = default;
            solidChildren[0] = MarkerSolidLeaf;
            solidChildren[1] = MarkerSolidLeaf;
            nodes[index] = new DNode
            {
                PlaneNum = TopPlaneNum(planes.Count - 1),
                Children = solidChildren,
                Mins = Short3(mins),
                Maxs = Short3(maxs),
                Area = -1,
            };

            return index;
        }

        if (rect.minx == rect.maxx && rect.miny == rect.maxy)
        {
            // The cell interior falls back into the room's root; the outside
            // of the cell face falls to the shared solid.
            planes.Add(new Plane(new Vec3(1, 0, 0), (rect.maxx + 1) * cellSize));
            IntArray2 roomChildren = default;
            roomChildren[0] = MarkerSolidLeaf; // front of +x face: outside the grid
            roomChildren[1] = MarkerRoomLeaf;  // back: this cell's room root
            nodes[index] = new DNode
            {
                PlaneNum = TopPlaneNum(planes.Count - 1),
                Children = roomChildren,
                Mins = Short3(mins),
                Maxs = Short3(maxs),
                Area = -1,
            };

            return index;
        }

        int width = rect.maxx - rect.minx + 1;
        int height = rect.maxy - rect.miny + 1;
        Plane split;
        (int, int, int, int) frontRect, backRect;
        if (width >= height)
        {
            int at = rect.minx + width / 2;
            split = new Plane(new Vec3(1, 0, 0), at * cellSize);
            frontRect = (at, rect.miny, rect.maxx, rect.maxy);
            backRect = (rect.minx, rect.miny, at - 1, rect.maxy);
        }
        else
        {
            int at = rect.miny + height / 2;
            split = new Plane(new Vec3(0, 1, 0), at * cellSize);
            frontRect = (rect.minx, at, rect.maxx, rect.maxy);
            backRect = (rect.minx, rect.miny, rect.maxx, at - 1);
        }

        planes.Add(split);
        int planeNumber = TopPlaneNum(planes.Count - 1);
        int front = BuildRegion(nodes, occupants, frontRect, cellSize, planes);
        int back = BuildRegion(nodes, occupants, backRect, cellSize, planes);
        IntArray2 children = default;
        children[0] = front;
        children[1] = back;
        nodes[index] = new DNode
        {
            PlaneNum = planeNumber,
            Children = children,
            Mins = Short3(mins),
            Maxs = Short3(maxs),
            Area = -1,
        };

        return index;
    }

    /// <summary>
    /// The provisional plane number of the top tree's <paramref name="index"/>th
    /// plane: the even half of pair <paramref name="index"/> counted from 0,
    /// which the assembly replaces with the plane's shared pair.
    /// </summary>
    private static int TopPlaneNum(int index) => 2 * index;

    // ---- small helpers -------------------------------------------------

    internal static Box BoxOf(DLeaf leaf) =>
        new(
            new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
            new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));

    internal static ShortArray3 Short3(Vec3 v)
    {
        ShortArray3 s = default;
        s[0] = (short)Math.Clamp(MathF.Round(v.X), short.MinValue, short.MaxValue);
        s[1] = (short)Math.Clamp(MathF.Round(v.Y), short.MinValue, short.MaxValue);
        s[2] = (short)Math.Clamp(MathF.Round(v.Z), short.MinValue, short.MaxValue);
        return s;
    }

    private static void Dedupe(List<int> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        int write = 0;
        for (int read = 1; read < values.Count; read++)
        {
            if (values[read] != values[write])
            {
                values[++write] = values[read];
            }
        }

        values.RemoveRange(write + 1, values.Count - write - 1);
    }
}

/// <summary>What <see cref="LevelLinker.LinkAsync(LevelLayout, RoomLibrary, VbspContext, LevelLinkOptions, CancellationToken)"/> produced: one map, one vis, one plan, and the entity budget.</summary>
/// <param name="Bsp">The linked BSP, visibility lump included.</param>
/// <param name="Vis">The door-graph visibility the linked BSP's rows compress to.</param>
/// <param name="Plan">What the linker knew: resolved placements and the cell-face planes.</param>
public sealed record LinkedLevel(BspData Bsp, VisResult Vis, LevelPlan Plan)
{
    /// <summary>
    /// The level's entity budget as the link found it: its edicts and
    /// entities, the budget, and any warning (<see cref="LevelEntityBudget"/>);
    /// null only for a level made some other way than by the link.
    /// </summary>
    public LevelEntityReport? EntityBudget { get; init; }

    /// <summary>
    /// How many brushes the brush fold removed by merging touching boxes
    /// (<see cref="LevelLinkOptions.FoldBrushes"/>); 0 when it did not run.
    /// </summary>
    public int FoldedBrushes { get; init; }

    /// <summary>
    /// How many files the linked map's pak holds: every placed room's packed
    /// files, each name once (<see cref="LevelPakFiles"/>); 0 when no room
    /// packs one.
    /// </summary>
    public int PackedFiles { get; init; }

    /// <summary>
    /// How many <c>env_cubemap</c> samples the linked map carries: every
    /// placed room's, at their linked positions (<see cref="LevelCubemaps"/>);
    /// 0 when no room has one.
    /// </summary>
    public int CubemapSamples { get; init; }

    /// <summary>
    /// What resolving the rooms' names warned of, each a whole sentence: a
    /// reference to an empty cell or off the grid, whose output was removed
    /// or key cleared; a global name defined by several placements of a room.
    /// </summary>
    public IReadOnlyList<string> NameWarnings { get; init; } = [];

    /// <summary>
    /// What only verbose output reports: references to entities that
    /// <c>room_needs</c> dropped on purpose, removed like the warnings' but
    /// expected.
    /// </summary>
    public IReadOnlyList<string> NameNotes { get; init; } = [];

    /// <summary>
    /// Whether the level has transitions and a spawn (the rooms design,
    /// section 11: a transition key in its file, or a placed role room), so
    /// its room starts were stripped and its transitions written. With the
    /// mod's classes, the arrival and spawn points are the navigation
    /// sidecar's, so a host that writes none should say so.
    /// </summary>
    public bool HasTransitions { get; init; }
}
