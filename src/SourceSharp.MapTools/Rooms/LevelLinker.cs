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

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
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
/// leaf's brush list and the world collision, and its faces are drawn as
/// nodraw. A <em>capped</em> socket keeps its plug, so it stays a wall.
/// Stripping at link time rather than compiling each room twice (sealed for
/// vis, open for geometry) keeps one compile per room, at one cost: the
/// doorway's jambs, lintel and sill have no faces, because in the room's
/// compile they faced the solid plug and vbsp emits no face between two
/// solids. The doorway is open to traces, physics and vis; it only draws as
/// a gap at its edges unless something in the socket covers them.
/// </para>
/// <para>
/// <b>Visibility</b> is composed from the door graph, never flooded. A room's
/// linked row starts as its own vvis row; the only thing that makes two rooms
/// see each other is a <b>door edge</b>: every open cluster whose leaf boxes
/// overlap a joint's plug box (with <see cref="DoorOverlapEpsilon"/>; the link
/// geometry is integer and bevels are ±8, so a face-sharing leaf sits at gap
/// 0 and the next space over is never closer than the wall's thickness)
/// reaches every open cluster facing the joint on the other side, in both
/// directions. Rows are the transitive closure of own-row steps plus door-edge
/// steps. The stripped doorway leaf joins the lowest of its own side's facing
/// clusters, which after the closure sees everything that side sees.
/// </para>
/// <para>
/// A room whose compile left anything outside the relocation set — a second
/// model, a water leaf, a real area portal, displacements, static or detail
/// props, packed files — is refused rather than silently dropped: the linked
/// map must be the rooms, not an approximation of them.
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
    /// no displacement, <see cref="BspLump.PakFile"/> holds no file, and every
    /// game lump is all zeros (no static or detail props).
    /// <see cref="BspLump.ClipPortalVerts"/> is not in the set: its vertices
    /// only exist for area portals, which are refused.
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
        BspLump.Occlusion, BspLump.PakFile, BspLump.MapFlags,
        BspLump.PhysCollide, BspLump.PhysDisp,
        ]);

    /// <summary>Links <paramref name="layout"/>'s rooms into one map.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms, by name.</param>
    /// <param name="context">
    /// The compile context: its parallelism plans the rooms, and its
    /// compliance chooses the precision the world collision is rebuilt at.
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
    /// jointed nor capped, a grid or kit that is not the library's.
    /// </exception>
    /// <exception cref="LinkException">
    /// A joint is geometrically wrong (no neighbour in its direction, a socket
    /// that does not exist, sides that do not meet head-on, a cap naming no
    /// socket, a joint no open leaf faces), a room's compile carries something
    /// the relocation refuses, or the level outgrows a field of the format.
    /// </exception>
    public static async Task<LinkedLevel> LinkAsync(
        LevelLayout layout,
        RoomLibrary library,
        VbspContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(context);

        // Layout first (rule 5 / rule 2's layout halves, house messages), then
        // the joint geometry only the linker can check.
        RoomLinter.CheckLayout(layout, library);
        ValidateJoints(layout, library);

        ResolvedPlacement[] resolved = [.. layout.Rooms.Select((p, i) => Resolve(p, i, library))];

        // The pak is a zip: whether it holds a file is a parse, and the parse
        // is async, so it runs here rather than inside the planning workers.
        foreach (ResolvedPlacement placement in resolved)
        {
            await RefusePackedFilesAsync(placement.Room, cancellationToken).ConfigureAwait(false);
        }

        // Per-room work: validate the compile against the relocation set and
        // parse + transform the structs. Each item writes only its own slot
        // (disjoint ranges), and the thread count comes from the context's
        // parallelism — degree 1 and degree 32 produce byte-identical output
        // because the counts and bases are a sequential prefix sum below and
        // no item reads another's result (invariant I4).
        RoomPlan[] plans = new RoomPlan[resolved.Length];
        using (WorkQueue queue = new(context.Parallelism))
        {
            await queue.RunAsync(
                resolved.Length,
                (index, _) =>
                {
                    plans[index] = PlanRoom(resolved[index]);
                },
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        AssignBases(plans);

        // Cluster space: room r's room-local cluster c is clusterBase_r + c;
        // solid leaves (plugs, shared void) stay cluster -1 and get no row.
        int clusterCursor = 0;
        foreach (RoomPlan plan in plans)
        {
            plan.ClusterBase = clusterCursor;
            clusterCursor += plan.ClusterCount;
        }

        int clusterCount = clusterCursor;
        Limit(plans[^1], "clusters", clusterCount, short.MaxValue);
        int rowBytes = (clusterCount + 7) >> 3;

        // Rows: own rows shifted into the global numbering, door edges from
        // the joint graph (both directions), then the transitive closure.
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

        byte[] visibilityLump = BuildVisibilityLump(clusterCount, rowBytes, pvs, pvs);

        BspData linked = Assemble(plans, layout, visibilityLump, context, cancellationToken);

        VisResult vis = new(
            clusterCount,
            portalCount: 0,
            rowBytes,
            pvs,
            (byte[])pvs.Clone(),
            visDataSize: visibilityLump.Length,
            totalVisibleClusters: totalVisible,
            optimizedClusters: 0,
            totalAudibleClusters: totalVisible,
            usedRadius: false,
            visRadiusSquared: 0,
            deepestFlow: 0,
            work: VisWorkCounters.Zero,
            trace: null);

        return new LinkedLevel(linked, vis, new LevelPlan(layout, resolved, TopPlanes(layout, layout.CellSize)));
    }

    /// <summary>
    /// The prefix sums every index-bearing struct is shifted by, in layout
    /// order, with a refusal the moment a sum outgrows the field that will
    /// carry it.
    /// </summary>
    /// <remarks>
    /// Each limit is the narrowest field that holds an index into (or a count
    /// of) that lump: a face's plane number and a brush side's are
    /// <c>ushort</c>, a face's and a brush side's texinfo is <c>short</c>, a
    /// leaf's cluster is <c>short</c>, an edge's vertices, a leaf's face and
    /// brush runs, a node's first face, a face's first primitive, a
    /// primitive's first index and vertex, a vertex-normal index and a macro
    /// texture's name id are <c>ushort</c>. A sum past its field would wrap
    /// silently in the cast that writes it and point into some other room.
    /// The planes, texinfos, leaves and leaf brushes the plug carve and the
    /// nodraw copies add are checked where they are added.
    /// </remarks>
    private static void AssignBases(RoomPlan[] plans)
    {
        long vertices = 0, edges = 0, surfEdges = 0, texInfos = 0, texDatas = 0, planes = 2,
             faces = 0, origFaces = 0, brushes = 0, brushSides = 0, leafFaces = 0,
             leaves = 1, lighting = 0, stringTable = 0, stringData = 0,
             primVerts = 0, primIndices = 0, prims = 0, vertNormals = 0, vertNormalIndices = 0,
             occluderPolys = 0, occluderVerts = 0;
        foreach (RoomPlan plan in plans)
        {
            plan.VertexBase = (int)vertices;
            plan.EdgeBase = (int)edges;
            plan.TexInfoBase = (int)texInfos;
            plan.TexDataBase = (int)texDatas;
            plan.PlaneBase = (int)planes;
            plan.FaceBase = (int)faces;
            plan.BrushBase = (int)brushes;
            plan.BrushSideBase = (int)brushSides;
            plan.LeafFaceBase = (int)leafFaces;
            plan.LeafBase = (int)leaves;
            plan.LightBase = (int)lighting;
            plan.StringTableBase = (int)stringTable;
            plan.StringDataBase = (int)stringData;
            plan.SurfEdgeBase = (int)surfEdges;
            plan.OrigFaceBase = (int)origFaces;
            plan.PrimBase = (int)prims;
            plan.PrimIndexBase = (int)primIndices;
            plan.PrimVertBase = (int)primVerts;
            plan.VertNormalBase = (int)vertNormals;
            plan.VertexNormalIndexBase = (int)vertNormalIndices;
            plan.OccluderPolyBase = (int)occluderPolys;
            plan.OccluderVertexBase = (int)occluderVerts;

            vertices += plan.Vertices.Length;
            edges += plan.EdgeCount;
            texInfos += plan.TexInfos.Length;
            texDatas += plan.TexDataCount;
            planes += plan.TransformedPlanes.Length;
            faces += plan.FaceCount;
            brushes += plan.BrushCount;
            brushSides += plan.BrushSideCount;
            leafFaces += plan.LeafFaceCount;
            leaves += plan.Leafs.Length;
            lighting += plan.LightingLength;
            stringTable += plan.StringTableCount;
            stringData += plan.StringDataLength;
            surfEdges += plan.SurfEdgeCount;
            origFaces += plan.OrigFaceCount;
            prims += plan.PrimCount;
            primIndices += plan.PrimIndexCount;
            primVerts += plan.PrimVertCount;
            vertNormals += plan.VertNormalCount;
            vertNormalIndices += plan.VertNormalIndexCount;
            occluderPolys += plan.Occlusion?.Polys.Count ?? 0;
            occluderVerts += plan.Occlusion?.VertexIndices.Count ?? 0;

            Limit(plan, "vertices", vertices, ushort.MaxValue + 1);
            Limit(plan, "planes", planes, ushort.MaxValue + 1);
            Limit(plan, "texinfos", texInfos, short.MaxValue + 1);
            Limit(plan, "faces", faces, ushort.MaxValue + 1);
            Limit(plan, "brushes", brushes, ushort.MaxValue + 1);
            Limit(plan, "leaf faces", leafFaces, ushort.MaxValue + 1);
            Limit(plan, "leaves", leaves, ushort.MaxValue + 1);
            Limit(plan, "texdata string table entries", stringTable, ushort.MaxValue);
            Limit(plan, "primitives", prims, ushort.MaxValue + 1);
            Limit(plan, "primitive indices", primIndices, ushort.MaxValue + 1);
            Limit(plan, "primitive vertices", primVerts, ushort.MaxValue + 1);
            Limit(plan, "vertex normals", vertNormals, ushort.MaxValue + 1);
        }
    }

    /// <summary>Refuses a count past what its field can carry.</summary>
    /// <param name="plan">The room whose addition crossed it, for the message.</param>
    /// <param name="what">The count's name.</param>
    /// <param name="count">The running total.</param>
    /// <param name="max">One past the largest total the field holds.</param>
    /// <exception cref="LinkException">The total reaches <paramref name="max"/>.</exception>
    internal static void Limit(RoomPlan plan, string what, long count, long max)
    {
        if (count > max)
        {
            throw new LinkException(
                $"room {plan.Placement.Room.Definition.Name} at cell ({plan.Placement.Instance.Placement.CellX},"
                + $" {plan.Placement.Instance.Placement.CellY}) pushes the link to {count} {what};"
                + $" the format carries at most {max}.");
        }
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
        for (int i = 0; i < resolved.Length; i++)
        {
            ResolvedPlacement a = resolved[i];
            foreach ((string socket, string neighborSocket) in a.Instance.Joints)
            {
                RoomSocket aSocket = Socket(a.Room, socket);
                (RoomPlan planB, RoomSocket bSocket, int _) = Neighbor(a, aSocket, neighborSocket, resolved, plans);
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

    /// <summary>The grid's cell-face split planes, in the order the top tree emits them.</summary>
    internal static List<Plane> TopPlanes(LevelLayout layout, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(layout);
        List<Plane> planes = [];
        BuildTopNodes(layout, cellSize, planes, topPlaneBase: 0);
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
    /// Every node bounds its region, planes are recorded in node order so the
    /// caller appends them to the plane lump as pairs (the format demands
    /// pairs: <c>(x &amp; ~1)</c> and <c>(x &amp; ~1) + 1</c> are each other's
    /// flip, positive normal first), and child node indices name nodes already
    /// built (pre-order: a parent's children have larger indices than it, and
    /// the root is node 0, which is what makes <c>model0.HeadNode = 0</c>).
    /// </para>
    /// </remarks>
    internal static List<DNode> BuildTopNodes(LevelLayout layout, float cellSize, List<Plane> planes, int topPlaneBase)
    {
        Dictionary<(int, int), int> occupants = [];
        int order = 0;
        foreach (RoomInstance room in layout.Rooms)
        {
            occupants[(room.Placement.CellX, room.Placement.CellY)] = order++;
        }

        (int minx, int miny, int maxx, int maxy) = Extent(layout);
        List<DNode> nodes = [];
        BuildRegion(nodes, occupants, (minx, miny, maxx, maxy), cellSize, planes, topPlaneBase);
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
    /// Warshall over uint words: rows[i] |= rows[k] wherever i sees k. The
    /// sweep order is the cluster numbering — never a schedule — so the
    /// closure is the same byte on one thread or thirty-two (I4). It is cubic
    /// in the cluster count, which for a large level is the link's longest
    /// loop, so it observes the token once per pivot: a cancelled link stops
    /// within one pass over the rows instead of finishing the closure first.
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

        for (int k = 0; k < clusterCount; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < clusterCount; i++)
            {
                if ((bits[i][k >> 5] & (1u << (k & 31))) == 0)
                {
                    continue;
                }

                for (int w = 0; w < words; w++)
                {
                    bits[i][w] |= bits[k][w];
                }
            }
        }

        for (int i = 0; i < clusterCount; i++)
        {
            rows[i] = ToBytes(bits[i], rows[i].Length);
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

                RoomInstance? neighbour = null;
                foreach (RoomInstance other in layout.Rooms)
                {
                    if (other.Placement.CellX == nx && other.Placement.CellY == ny)
                    {
                        neighbour = other;
                        break;
                    }
                }

                if (neighbour is null)
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

    private static (RoomPlan Plan, RoomSocket Socket, int Index) Neighbor(
        ResolvedPlacement a, RoomSocket mine, string neighborSocket, ResolvedPlacement[] resolved, RoomPlan[] plans)
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

        foreach (ResolvedPlacement other in resolved)
        {
            if (other.Instance.Placement.CellX != nx || other.Instance.Placement.CellY != ny)
            {
                continue;
            }

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
        List<Plane> planes,
        int topPlaneBase)
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
                PlaneNum = TopPlaneNum(topPlaneBase, planes.Count - 1),
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
                PlaneNum = TopPlaneNum(topPlaneBase, planes.Count - 1),
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
        int planeNumber = TopPlaneNum(topPlaneBase, planes.Count - 1);
        int front = BuildRegion(nodes, occupants, frontRect, cellSize, planes, topPlaneBase);
        int back = BuildRegion(nodes, occupants, backRect, cellSize, planes, topPlaneBase);
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

    private static int TopPlaneNum(int topPlaneBase, int index) => topPlaneBase + (2 * index);

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

/// <summary>What <see cref="LevelLinker.LinkAsync"/> produced: one map, one vis, one plan.</summary>
/// <param name="Bsp">The linked BSP, visibility lump included.</param>
/// <param name="Vis">The door-graph visibility the linked BSP's rows compress to.</param>
/// <param name="Plan">What the linker knew: resolved placements and the cell-face planes.</param>
public sealed record LinkedLevel(BspData Bsp, VisResult Vis, LevelPlan Plan);
