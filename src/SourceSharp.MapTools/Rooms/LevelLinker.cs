using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

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
/// leaves, brushes — is carried in verbatim and moved by <see cref="RoomTransform"/>:
/// a quarter turn about +z and a whole-cell translation, so every relocated
/// coordinate is a sum of exactly representable values and no rotation matrix
/// is ever multiplied (invariant I4: the same input byte-yields the same output
/// on one thread or sixty-four). One top tree hangs the room subtrees in world
/// order; its split planes are the grid's cell faces, so a point inside a cell
/// descends to exactly that cell's room root and the room's own tree routes it
/// to the leaf vvis assigned its cluster.
/// </para>
/// <para>
/// The plug brushes <b>stay</b> in the linked map — they are what keeps each
/// room's door-shut vis honest (the trigger-plug probes A/B are the evidence) —
/// and the door graph rewrites the plug clusters' rows. A room's linked row
/// starts as its own vvis row; the only thing that makes two rooms see each
/// other is a <b>door edge</b>: every open cluster whose leaf boxes overlap a
/// joint's plug box (with <see cref="DoorOverlapEpsilon"/>; the link geometry
/// is integer and bevels are ±8, so a face-sharing leaf sits at gap 0 and the
/// next space over is never closer than the wall's thickness) reaches every
/// open cluster facing the joint on the other side, in both directions. Rows
/// are the transitive closure of own-row steps plus door-edge steps, so a
/// corridor that does not itself face an opening still inherits the door's
/// vis. Solid leaves — the plugs' and the shared void leaf's — carry cluster
/// <c>-1</c> and are in no row: that is what the door shut means in the PVS.
/// </para>
/// <para>
/// A room whose compile left anything outside the relocation set — a second
/// model, a water leaf, game lump, clip portal, overlay, displacement — is
/// refused rather than silently dropped: the linked map must be the rooms, not
/// an approximation of them.
/// </para>
/// </remarks>
public static class LevelLinker
{
    /// <summary>
    /// How far past a shared face two leaf boxes must overlap to face a joint:
    /// touching (gap 0) counts, which a non-negative epsilon would reject.
    /// </summary>
    public const float DoorOverlapEpsilon = -0.5f;

    /// <summary>The lumps the relocation carries; anything else non-empty is refused.</summary>
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
        BspLump.Areas, BspLump.AreaPortals, BspLump.ClipPortalVerts,
        BspLump.Occlusion, BspLump.PakFile, BspLump.MapFlags,
        ]);

    /// <summary>Links <paramref name="layout"/>'s rooms into one map.</summary>
    /// <param name="layout">The level.</param>
    /// <param name="library">The rooms, by name.</param>
    /// <param name="context">The compile context the rooms came from.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The linked BSP, its visibility, and the plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="RoomLintException">
    /// A layout rule is broken: a room the library lacks, a socket neither
    /// jointed nor capped.
    /// </exception>
    /// <exception cref="LinkException">
    /// A joint is geometrically wrong (no neighbour in its direction, a socket
    /// that does not exist, sides that do not meet head-on, a cap naming no
    /// socket), or a room's compile carries something the relocation refuses.
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

        // Bases: a sequential prefix sum over the layout order.
        long vertices = 0, edges = 0, surfEdges = 0, texInfos = 0, texDatas = 0, planes = 0,
             faces = 0, origFaces = 0, brushes = 0, brushSides = 0, leafFaces = 0, leafBrushes = 0,
             leaves = 1, lighting = 0, texDataBytes = 0, stringDataTable = 0, stringDataBytes = 0,
             primVerts = 0, primIndices = 0, prims = 0, vertNormals = 0, vertNormalIndices = 0,
             areas = 0, areaPortals = 0, clipPortalVerts = 0, occluderPolys = 0, occluderVerts = 0;
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
            plan.LeafBrushBase = (int)leafBrushes;
            plan.LeafBase = (int)leaves;
            plan.LightBase = (int)lighting;
            plan.StringTableBase = (int)stringDataTable;
            plan.StringDataBase = (int)stringDataBytes;
            plan.SurfEdgeBase = (int)surfEdges;
            plan.OrigFaceBase = (int)origFaces;
            plan.PrimBase = (int)prims;
            plan.PrimIndexBase = (int)primIndices;
            plan.PrimVertBase = (int)primVerts;
            plan.VertNormalBase = (int)vertNormals;
            plan.VertexNormalIndexBase = (int)vertNormalIndices;
            plan.AreaBase = (int)areas;
            plan.AreaPortalBase = (int)areaPortals;
            plan.ClipPortalVertBase = (int)clipPortalVerts;
            plan.OccluderPolyBase = (int)occluderPolys;
            plan.OccluderVertexBase = (int)occluderVerts;
            vertices += plan.Vertices.Length / SizeOf<Vec3>();
            edges += plan.EdgeCount;
            texInfos += plan.TexInfoCount;
            texDatas += plan.TexDataCount;
            planes += (2 + plan.TransformedPlanes.Length) + 2 * plan.TopPlaneCount;
            faces += plan.FaceCount;
            brushes += plan.BrushCount;
            brushSides += plan.BrushSideCount;
            leafFaces += plan.LeafFaceCount;
            leafBrushes += plan.LeafBrushCount;
            leaves += plan.LeafCount;
            lighting += plan.LightingLength;
            texDataBytes += plan.TexDataCount;
            stringDataTable += plan.StringTableCount;
            stringDataBytes += plan.StringDataLength;
            surfEdges += plan.SurfEdgeCount;
            origFaces += plan.OrigFaceCount;
            prims += plan.PrimCount;
            primIndices += plan.PrimIndexCount;
            primVerts += plan.PrimVertCount;
            vertNormals += plan.VertNormalCount;
            vertNormalIndices += plan.VertNormalIndexCount;
            areas += plan.AreaCount;
            areaPortals += plan.AreaPortalCount;
            clipPortalVerts += plan.ClipPortalVertCount;
            occluderPolys += plan.Occlusion?.Polys.Count ?? 0;
            occluderVerts += plan.Occlusion?.VertexIndices.Count ?? 0;
            if (leaves > 65535 || faces > 65535 || brushes > 65535
                || leafFaces > 65535 || leafBrushes > 65535)
            {
                throw new LinkException(
                    $"room {plan.Placement.Instance.Placement.Room} pushes the link past the ushort leaf-face/leaf-brush ranges");
            }

            if (vertices > 65535)
            {
                throw new LinkException(
                    $"room {plan.Placement.Instance.Placement.Room} pushes the link past the ushort edge-vertex range");
            }
        }

        // The plane lump is [null pair][every room's transformed planes][top
        // cell-face planes in pairs]; room planes start at 2.
        int planeCursor = 2;
        foreach (RoomPlan plan in plans)
        {
            plan.PlaneBase = planeCursor;
            planeCursor += plan.TransformedPlanes.Length;
        }

        int topPlaneBase = planeCursor;

        // Cluster space: room r's room-local cluster c is clusterBase_r + c;
        // solid leaves (plugs, shared void) stay cluster -1 and get no row.
        int clusterCursor = 0;
        foreach (RoomPlan plan in plans)
        {
            plan.ClusterBase = clusterCursor;
            clusterCursor += plan.ClusterCount;
        }

        int clusterCount = clusterCursor;
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

        CloseRows(rows, clusterCount);

        byte[] pvs = new byte[clusterCount * rowBytes];
        int totalVisible = 0;
        for (int c = 0; c < clusterCount; c++)
        {
            rows[c].CopyTo(pvs, c * rowBytes);
            totalVisible += PopCount(rows[c]);
        }

        byte[] visibilityLump = BuildVisibilityLump(clusterCount, rowBytes, pvs, pvs);

        BspData linked = Assemble(resolved, plans, layout, topPlaneBase, visibilityLump, clusterCount);

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

    /// <summary>The joint graph's door edges: per joint, the clusters facing each side.</summary>
    /// <remarks>
    /// A joint's two plug boxes meet inside the wall; the clusters joined are
    /// the open leaves whose boxes overlap each side's plug box within
    /// <see cref="DoorOverlapEpsilon"/> (touching counts: the integer link
    /// geometry shares the face at gap 0). Deterministic: layout order,
    /// cluster order.
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
                Box plugA = RoomLinter.SealBox(a.Room.Definition, aSocket, a.Room.Definition.CellSize);
                Box plugB = RoomLinter.SealBox(planB.Placement.Room.Definition, bSocket, planB.Placement.Room.Definition.CellSize);
                // Room-local comparisons: the seal boxes are room-local, and
                // so are the leaf boxes as compiled — the transform is identity
                // per room here because both sides' plugs live in their own
                // rooms' coordinates and the joint's geometry is validated in
                // world space by ValidateJoints.
                yield return (plans[i], planB, Facing(plans[i], plugA), Facing(planB, plugB));
            }
        }
    }

    /// <summary>The open clusters whose leaf boxes overlap a plug box.</summary>
    internal static int[] Facing(RoomPlan plan, Box plug)
    {
        List<int> clusters = [];
        foreach (DLeaf leaf in plan.Leafs)
        {
            if ((leaf.Contents & (int)BrushContents.Solid) != 0 || leaf.Cluster < 0)
            {
                continue;
            }

            Box box = new(
                new Vec3(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]),
                new Vec3(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]));
            if (box.Overlaps(plug, DoorOverlapEpsilon))
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
    /// The repo's own reader convention (<c>BspTreeView</c>: a point on the
    /// plane's negative side takes <c>Children[1]</c>), and the room subtrees
    /// are carried verbatim from the repo's vbsp, so the same walk routes them.
    /// </remarks>
    internal static int PointInLeafCluster(BspData bsp, Vec3 point)
    {
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        return PointInLeafCluster(nodes, planes, leafs, point);
    }

    internal static int PointInLeafCluster(
        ReadOnlySpan<DNode> nodes, ReadOnlySpan<DPlane> planes, ReadOnlySpan<DLeaf> leafs, Vec3 point)
    {
        int index = 0; // model 0's head node: the top tree's root
        while (index >= 0)
        {
            DNode node = nodes[index];
            DPlane plane = planes[node.PlaneNum];
            index = Vec3.Dot(point, plane.Normal) < plane.Dist
                ? node.Children[1]
                : node.Children[0];
        }

        return leafs[~index].Cluster;
    }

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
    /// side, which the repo's reader takes with a non-negative dot) holds the
    /// higher coordinates, the back the lower. A single occupied cell splits at
    /// its +x cell face: the cell interior falls back into that cell's room
    /// root, the outside falls to the shared solid leaf. An empty region is
    /// solid. A point inside a cell therefore descends to exactly that cell's
    /// room root, and the room's own tree — carried verbatim from vbsp — does
    /// the rest.
    /// </para>
    /// <para>
    /// Every node bounds its region, planes are recorded in node order so the
    /// caller appends them to the plane lump as pairs (the format demands
    /// pairs: <c>(x &amp; ~1)</c> and <c>(x &amp; ~1) + 1</c> are each other's
    /// flip), and child node indices name nodes already built (pre-order: a
    /// parent's children have larger indices than it, and the root is node 0,
    /// which is what makes <c>model0.HeadNode = 0</c>).
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

    private static void CloseRows(byte[][] rows, int clusterCount)
    {
        // Warshall over uint words: rows[i] |= rows[k] wherever i sees k. The
        // sweep order is the cluster numbering — never a schedule — so the
        // closure is the same byte on one thread or thirty-two (I4).
        int words = ((clusterCount - 1) >> 5) + 1;
        uint[][] bits = new uint[clusterCount][];
        for (int i = 0; i < clusterCount; i++)
        {
            bits[i] = ToWords(rows[i], words);
        }

        for (int k = 0; k < clusterCount; k++)
        {
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
            ((instance.Placement.Rotation % 4) + 4) % 4,
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


    // ---- per-room planning ---------------------------------------------

    /// <summary>
    /// Validates one room's compile against the relocation set and reads the
    /// structs the assembly patches. Pure: reads only this room, writes only
    /// the returned plan, so any number of rooms plan concurrently.
    /// </summary>
    private static RoomPlan PlanRoom(ResolvedPlacement placement)
    {
        RoomObject room = placement.Room;
        BspData bsp = room.Bsp;
        RoomTransform transform = new(placement.Instance.Placement, room.Definition.CellSize);

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            BspLumpData lump = bsp[i];
            if (lump.Length == 0)
            {
                continue;
            }

            if (!CarriedLumps.Contains((BspLump)i))
            {
                throw new LinkException(
                    $"room {room.Definition.Name} carries lump {((BspLump)i)} ({i}), which the relocation does not understand");
            }
        }

        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        if (models.Length != 1)
        {
            throw new LinkException(
                $"room {room.Definition.Name} has {models.Length} models; a linkable room is one world model");
        }

        if (models[0].HeadNode != 0)
        {
            throw new LinkException(
                $"room {room.Definition.Name}'s world model starts at node {models[0].HeadNode}, not 0");
        }

        foreach (DLeaf leaf in BspStructView.As<DLeaf>(bsp[BspLump.Leafs]))
        {
            if (leaf.LeafWaterDataId != -1)
            {
                throw new LinkException(
                    $"room {room.Definition.Name} has a water leaf, which the relocation refuses");
            }
        }

        VisResult own = room.Vis;
        if (own.Pvs(0).Length != own.RowBytes)
        {
            throw new LinkException($"room {room.Definition.Name}'s vis rows are inconsistent");
        }

        byte[][] ownRows = new byte[own.ClusterCount][];
        for (int c = 0; c < own.ClusterCount; c++)
        {
            ownRows[c] = own.Pvs(c).ToArray();
        }

        // Vertices and planes are relocated here (they need no cross-room
        // bases); everything index-bearing is patched at assembly.
        Vec3[] vertexSource = [.. BspStructView.As<Vec3>(bsp[BspLump.Vertexes])];
        Vec3[] vertices = new Vec3[vertexSource.Length];
        for (int v = 0; v < vertexSource.Length; v++)
        {
            vertices[v] = transform.Apply(vertexSource[v]);
        }

        DPlane[] roomPlanes = TransformPlanes(
            [.. BspStructView.As<DPlane>(bsp[BspLump.Planes])], transform);

        // The room's model bounds, transformed corner-wise: integer
        // coordinates under a quarter turn and whole-cell translation, so this
        // is exact (I14).
        Vec3[] corners = TakeCorners(transform, models[0].Mins, models[0].Maxs);
        Vec3 mins = corners[0];
        Vec3 maxs = corners[0];
        foreach (Vec3 c in corners)
        {
            mins = new Vec3(Math.Min(mins.X, c.X), Math.Min(mins.Y, c.Y), Math.Min(mins.Z, c.Z));
            maxs = new Vec3(Math.Max(maxs.X, c.X), Math.Max(maxs.Y, c.Y), Math.Max(maxs.Z, c.Z));
        }

        return new RoomPlan
        {
            Placement = placement,
            Bsp = bsp,
            OwnRows = ownRows,
            ClusterCount = own.ClusterCount,
            Vertices = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(),
            TransformedPlanes = roomPlanes,
            Leafs = [.. BspStructView.As<DLeaf>(bsp[BspLump.Leafs])],
            LeafCount = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).Length,
            EdgeCount = BspStructView.As<DEdge>(bsp[BspLump.Edges]).Length,
            TexInfoCount = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).Length,
            TexDataCount = BspStructView.As<DTexData>(bsp[BspLump.TexData]).Length,
            FaceCount = BspStructView.As<DFace>(bsp[BspLump.Faces]).Length,
            BrushCount = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).Length,
            BrushSideCount = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).Length,
            LeafFaceCount = BspStructView.As<ushort>(bsp[BspLump.LeafFaces]).Length,
            LeafBrushCount = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).Length,
            NodeCount = BspStructView.As<DNode>(bsp[BspLump.Nodes]).Length,
            ModelMins = mins,
            ModelMaxs = maxs,
            SurfEdgeCount = BspStructView.As<int>(bsp[BspLump.SurfEdges]).Length,
            OrigFaceCount = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]).Length,
            VertNormalCount = BspStructView.As<Vec3>(bsp[BspLump.VertNormals]).Length,
            VertNormalIndexCount = bsp[BspLump.VertNormalIndices].Length / sizeof(ushort),
            PrimCount = BspStructView.As<DPrimitive>(bsp[BspLump.Primitives]).Length,
            PrimIndexCount = BspStructView.As<ushort>(bsp[BspLump.PrimIndices]).Length,
            PrimVertCount = BspStructView.As<Vec3>(bsp[BspLump.PrimVerts]).Length,
            AreaCount = BspStructView.As<DArea>(bsp[BspLump.Areas]).Length,
            AreaPortalCount = BspStructView.As<DAreaPortal>(bsp[BspLump.AreaPortals]).Length,
            ClipPortalVertCount = BspStructView.As<Vec3>(bsp[BspLump.ClipPortalVerts]).Length,
            Occlusion = bsp[BspLump.Occlusion].Length == 0
                ? null
                : OcclusionLump.Read(bsp[BspLump.Occlusion]),
            FacesVersion = bsp[BspLump.Faces].Version,
            LeafsVersion = bsp[BspLump.Leafs].Version,
            LightingLength = bsp[BspLump.Lighting].Length,
            StringTableCount = BspStructView.As<ushort>(bsp[BspLump.TexDataStringTable]).Length,
            StringDataLength = bsp[BspLump.TexDataStringData].Length,
            TopPlaneCount = 0,
        };
    }

    /// <summary>The room's planes through the transform, flip pairs kept as pairs.</summary>
    /// <remarks>
    /// <c>n' = R·n, d' = d + n·t</c>: the permutation picks components and the
    /// dot is an integer times an integer, so the relocated plane is exactly
    /// representable — the same input byte-yields on any thread (I14). The
    /// plane's flip entry is rebuilt as the exact negation of the transformed
    /// non-flip, which is what the pair contract in the reference implementation asks
    /// for and what survives a vbsp that stored either side non-canonical.
    /// </remarks>
    private static DPlane[] TransformPlanes(DPlane[] planes, RoomTransform transform)
    {
        if (planes.Length % 2 != 0)
        {
            throw new LinkException("the planes lump holds an odd number of planes, so a flip pair is missing");
        }

        Vec3 translation = transform.Apply(Vec3.Zero);
        for (int i = 0; i < planes.Length; i += 2)
        {
            DPlane plane = planes[i];
            Vec3 normal = ApplyNormal(plane.Normal, ((transform.Placement.Rotation % 4) + 4) % 4);
            float dist = plane.Dist + Vec3.Dot(plane.Normal, translation);
            planes[i] = new DPlane { Normal = normal, Dist = dist, Type = plane.Type };
            planes[i + 1] = new DPlane { Normal = -normal, Dist = -dist, Type = planes[i + 1].Type };
        }

        return planes;
    }

    /// <summary>Quarter-turn of a normal about +z: 0 identity, 1 (-y,x,z), 2 (-x,-y,z), 3 (y,-x,z).</summary>
    private static Vec3 ApplyNormal(Vec3 n, int rotation) => rotation switch
    {
        0 => n,
        1 => new Vec3(-n.Y, n.X, n.Z),
        2 => new Vec3(-n.X, -n.Y, n.Z),
        _ => new Vec3(n.Y, -n.X, n.Z),
    };

    // ---- assembly ------------------------------------------------------

    private static BspData Assemble(
        ResolvedPlacement[] resolved,
        RoomPlan[] plans,
        LevelLayout layout,
        int topPlaneBase,
        byte[] visibilityLump,
        int clusterCount)
    {
        float cell = layout.CellSize;
        BspData linked = new();

        // Planes: [0],[1] the null pair; every room's transformed planes in
        // layout order; the top tree's cell-face planes as flip pairs.
        List<DPlane> planes = [new(), new()];
        foreach (RoomPlan plan in plans)
        {
            planes.AddRange(plan.TransformedPlanes);
        }

        List<Plane> topPlanes = [];
        List<DNode> top = BuildTopNodes(layout, cell, topPlanes, topPlaneBase);
        foreach (Plane p in topPlanes)
        {
            planes.Add(new DPlane { Normal = p.Normal, Dist = p.Dist, Type = (int)p.Type });
            planes.Add(new DPlane { Normal = -p.Normal, Dist = -p.Dist, Type = (int)p.Type });
        }

        // Nodes: the top tree first (so model 0's head node is 0), then every
        // room's subtree with its children rebased.
        List<DNode> nodes = [];
        nodes.AddRange(top);
        foreach (RoomPlan plan in plans)
        {
            plan.NodeBase = nodes.Count;
            foreach (DNode n in BspStructView.As<DNode>(plan.Bsp[BspLump.Nodes]).ToArray())
            {
                DNode shifted = n;
                shifted.PlaneNum += plan.PlaneBase;
                IntArray2 children = default;
                for (int side = 0; side < 2; side++)
                {
                    int child = n.Children[side];
                    children[side] = child >= 0
                        ? child + plan.NodeBase
                        : -(plan.LeafBase + ~child + 1);
                }

                shifted.Children = children;
                shifted.FirstFace += (ushort)plan.FaceBase;
                if (shifted.Area >= 0)
                {
                    shifted.Area += (short)plan.AreaBase;
                }
                nodes.Add(shifted);
            }
        }

        // Top nodes reference room roots (their positive children) and the
        // shared solid leaf (leaf 0, negative child -1); room bases are only
        // known now, so the second pass fills them.
        FillTopChildren(nodes, top, plans, layout, cell);

        // Leafs: the shared solid at index 0 — the void outside every room,
        // bounded by the grid — then every room's leaves with clusters, leaf
        // faces and leaf brushes rebased.
        (int minx, int miny, int maxx, int maxy) = Extent(layout);
        List<DLeaf> leafs = [new DLeaf
        {
            Contents = (int)BrushContents.Solid,
            Cluster = -1,
            AreaFlags = 0,
            Mins = Short3(new Vec3(minx * cell, miny * cell, 0)),
            Maxs = Short3(new Vec3((maxx + 1) * cell, (maxy + 1) * cell, cell)),
            LeafWaterDataId = -1,
        }];

        foreach (RoomPlan plan in plans)
        {
            foreach (DLeaf leaf in plan.Leafs)
            {
                DLeaf shifted = leaf;
                if (shifted.Cluster >= 0)
                {
                    shifted.Cluster += (short)plan.ClusterBase;
                }

                shifted.FirstLeafFace += (ushort)plan.LeafFaceBase;
                shifted.FirstLeafBrush += (ushort)plan.LeafBrushBase;
                int area = leaf.GetArea();
                shifted.SetAreaFlags(area + plan.AreaBase, leaf.GetFlags());
                leafs.Add(shifted);
            }
        }

        List<ushort> leafFaces = [];
        List<ushort> leafBrushes = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (ushort face in BspStructView.As<ushort>(plan.Bsp[BspLump.LeafFaces]).ToArray())
            {
                leafFaces.Add((ushort)(face + plan.FaceBase));
            }

            foreach (ushort brush in BspStructView.As<ushort>(plan.Bsp[BspLump.LeafBrushes]).ToArray())
            {
                leafBrushes.Add((ushort)(brush + plan.BrushBase));
            }
        }

        List<DFace> faces = [];
        List<byte> lighting = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DFace face in BspStructView.As<DFace>(plan.Bsp[BspLump.Faces]).ToArray())
            {
                DFace shifted = face;
                shifted.PlaneNum += (ushort)plan.PlaneBase;
                shifted.TexInfo += (short)plan.TexInfoBase;
                shifted.FirstEdge = face.FirstEdge >= 0
                    ? face.FirstEdge + plan.EdgeBase
                    : -(Math.Abs(face.FirstEdge) + plan.EdgeBase);
                shifted.LightOfs = face.LightOfs == 0 ? 0 : face.LightOfs + plan.LightBase;
                shifted.OrigFace = face.OrigFace >= 0
                    ? face.OrigFace + plan.OrigFaceBase
                    : face.OrigFace;
                shifted.FirstPrimId += (ushort)plan.PrimBase;
                faces.Add(shifted);
            }

            lighting.AddRange(plan.Bsp[BspLump.Lighting].Data.Span.ToArray());
        }

        List<DEdge> edges = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DEdge edge in BspStructView.As<DEdge>(plan.Bsp[BspLump.Edges]).ToArray())
            {
                DEdge shifted = edge;
                shifted.V[0] += (ushort)plan.VertexBase;
                shifted.V[1] += (ushort)plan.VertexBase;
                edges.Add(shifted);
            }
        }

        List<DBrush> brushes = [];
        List<DBrushSide> brushSides = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DBrushSide side in BspStructView.As<DBrushSide>(plan.Bsp[BspLump.BrushSides]).ToArray())
            {
                DBrushSide shifted = side;
                shifted.PlaneNum += (ushort)plan.PlaneBase;
                shifted.TexInfo += (short)plan.TexInfoBase;
                brushSides.Add(shifted);
            }

            foreach (DBrush brush in BspStructView.As<DBrush>(plan.Bsp[BspLump.Brushes]).ToArray())
            {
                DBrush shifted = brush;
                shifted.FirstSide += plan.BrushSideBase;
                brushes.Add(shifted);
            }
        }

        List<TexInfo> texInfos = [];
        List<DTexData> texDatas = [];
        List<ushort> stringTable = [];
        List<byte> stringData = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (TexInfo info in BspStructView.As<TexInfo>(plan.Bsp[BspLump.TexInfo]).ToArray())
            {
                TexInfo shifted = info;
                shifted.TexData += plan.TexDataBase;
                texInfos.Add(shifted);
            }

            foreach (DTexData data in BspStructView.As<DTexData>(plan.Bsp[BspLump.TexData]).ToArray())
            {
                DTexData shifted = data;
                shifted.NameStringTableId += plan.StringTableBase;
                texDatas.Add(shifted);
            }

            foreach (ushort entry in BspStructView.As<ushort>(plan.Bsp[BspLump.TexDataStringTable]).ToArray())
            {
                // The table is concatenated verbatim, but its entries are byte
                // offsets into the room's OWN string-data lump; the data lump is
                // concatenated too, so every entry shifts by the preceding rooms.
                stringTable.Add((ushort)(entry + plan.StringDataBase));
            }

            stringData.AddRange(plan.Bsp[BspLump.TexDataStringData].Data.Span.ToArray());
        }

        // Surfedges: the signed edge indices the faces' runs point into.
        List<int> surfEdges = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (int se in BspStructView.As<int>(plan.Bsp[BspLump.SurfEdges]).ToArray())
            {
                surfEdges.Add(se >= 0 ? se + plan.EdgeBase : -(Math.Abs(se) + plan.EdgeBase));
            }
        }

        // Face ids and macro textures: ids are opaque, macro names index the
        // shared string table (0xFFFF is "none" and stays 0xFFFF).
        List<DFaceId> faceIds = [];
        List<FaceMacroTextureInfo> macroTextures = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DFaceId id in BspStructView.As<DFaceId>(plan.Bsp[BspLump.FaceIds]).ToArray())
            {
                faceIds.Add(id);
            }

            foreach (FaceMacroTextureInfo macro in
                BspStructView.As<FaceMacroTextureInfo>(plan.Bsp[BspLump.FaceMacroTextureInfo]).ToArray())
            {
                FaceMacroTextureInfo shifted = macro;
                if (macro.MacroTextureNameId != 0xFFFF)
                {
                    shifted.MacroTextureNameId += (ushort)plan.StringTableBase;
                }

                macroTextures.Add(shifted);
            }
        }

        // Original faces: the face's own plane is the SAME planes-lump entry
        // as the drawn face (vbsp emits them together), so its plane and
        // texinfo shift by the room's bases; its surfedge run lives in this
        // room's surfedge range, so FirstEdge shifts sign-preserving by the
        // SURFEDGE base, not the edge base.
        List<DFace> origFaces = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DFace face in BspStructView.As<DFace>(plan.Bsp[BspLump.OriginalFaces]).ToArray())
            {
                DFace shifted = face;
                shifted.PlaneNum += (ushort)plan.PlaneBase;
                shifted.TexInfo += (short)plan.TexInfoBase;
                shifted.FirstEdge = face.FirstEdge >= 0
                    ? face.FirstEdge + plan.SurfEdgeBase
                    : -(Math.Abs(face.FirstEdge) + plan.SurfEdgeBase);
                shifted.OrigFace = -1;
                origFaces.Add(shifted);
            }
        }

        // Vert normals: phong normals rotate with the room but never
        // translate, so ApplyNormal and not Apply.
        List<Vec3> vertNormals = [];
        List<ushort> vertNormalIndices = [];
        foreach (RoomPlan plan in plans)
        {
            int rotation = ((plan.Placement.Instance.Placement.Rotation % 4) + 4) % 4;
            foreach (Vec3 normal in BspStructView.As<Vec3>(plan.Bsp[BspLump.VertNormals]).ToArray())
            {
                vertNormals.Add(ApplyNormal(normal, rotation));
            }

            foreach (ushort index in BspStructView.As<ushort>(plan.Bsp[BspLump.VertNormalIndices]).ToArray())
            {
                vertNormalIndices.Add((ushort)(index + plan.VertNormalBase));
            }
        }

        // Primitives: indices are opaque within the lump, vertices are world
        // geometry and go through the transform.
        List<DPrimitive> prims = [];
        List<ushort> primIndices = [];
        List<Vec3> primVerts = [];
        foreach (RoomPlan plan in plans)
        {
            RoomTransform roomTransform =
                new(plan.Placement.Instance.Placement, plan.Placement.Room.Definition.CellSize);
            foreach (DPrimitive prim in BspStructView.As<DPrimitive>(plan.Bsp[BspLump.Primitives]).ToArray())
            {
                DPrimitive shifted = prim;
                shifted.FirstIndex += (ushort)plan.PrimIndexBase;
                shifted.FirstVert += (ushort)plan.PrimVertBase;
                prims.Add(shifted);
            }

            foreach (ushort index in BspStructView.As<ushort>(plan.Bsp[BspLump.PrimIndices]).ToArray())
            {
                primIndices.Add((ushort)(index + plan.PrimVertBase));
            }

            foreach (Vec3 vertex in BspStructView.As<Vec3>(plan.Bsp[BspLump.PrimVerts]).ToArray())
            {
                primVerts.Add(roomTransform.Apply(vertex));
            }
        }

        // Areas and areaportals: every room carries a reserved error entry at
        // index 0, so concatenation duplicates it harmlessly — nothing the
        // engine walks references area 0 or portal 0.
        List<DArea> areas = [];
        List<DAreaPortal> areaPortals = [];
        foreach (RoomPlan plan in plans)
        {
            foreach (DArea area in BspStructView.As<DArea>(plan.Bsp[BspLump.Areas]).ToArray())
            {
                DArea shifted = area;
                shifted.FirstAreaPortal += plan.AreaPortalBase;
                areas.Add(shifted);
            }

            foreach (DAreaPortal portal in BspStructView.As<DAreaPortal>(plan.Bsp[BspLump.AreaPortals]).ToArray())
            {
                DAreaPortal shifted = portal;
                shifted.OtherArea += (ushort)plan.AreaBase;
                shifted.FirstClipPortalVert += (ushort)plan.ClipPortalVertBase;
                shifted.PlaneNum += plan.PlaneBase;
                areaPortals.Add(shifted);
            }
        }

        List<Vec3> clipPortalVerts = [];
        foreach (RoomPlan plan in plans)
        {
            RoomTransform roomTransform =
                new(plan.Placement.Instance.Placement, plan.Placement.Room.Definition.CellSize);
            foreach (Vec3 vertex in BspStructView.As<Vec3>(plan.Bsp[BspLump.ClipPortalVerts]).ToArray())
            {
                clipPortalVerts.Add(roomTransform.Apply(vertex));
            }
        }

        // Occlusion: rebased polygons and vertex indices, corner-wise boxes
        // (non-axis-aligned after a quarter turn, so no min/max shortcut).
        OcclusionLump occlusion = new();
        foreach (RoomPlan plan in plans)
        {
            if (plan.Occlusion is not { } roomOcclusion)
            {
                continue;
            }

            RoomTransform roomTransform =
                new(plan.Placement.Instance.Placement, plan.Placement.Room.Definition.CellSize);
            foreach (DOccluderData occluder in roomOcclusion.Occluders)
            {
                DOccluderData shifted = occluder;
                shifted.FirstPoly += plan.OccluderPolyBase;
                Vec3[] boxCorners = TakeCorners(roomTransform, occluder.Mins, occluder.Maxs);
                Vec3 boxMins = boxCorners[0];
                Vec3 boxMaxs = boxCorners[0];
                foreach (Vec3 corner in boxCorners)
                {
                    boxMins = new Vec3(
                        Math.Min(boxMins.X, corner.X), Math.Min(boxMins.Y, corner.Y), Math.Min(boxMins.Z, corner.Z));
                    boxMaxs = new Vec3(
                        Math.Max(boxMaxs.X, corner.X), Math.Max(boxMaxs.Y, corner.Y), Math.Max(boxMaxs.Z, corner.Z));
                }

                shifted.Mins = boxMins;
                shifted.Maxs = boxMaxs;
                if (shifted.Area >= 0)
                {
                    shifted.Area += plan.AreaBase;
                }

                occlusion.Occluders.Add(shifted);
            }

            foreach (DOccluderPolyData poly in roomOcclusion.Polys)
            {
                DOccluderPolyData shifted = poly;
                shifted.FirstVertexIndex += plan.OccluderVertexBase;
                shifted.PlaneNum += plan.PlaneBase;
                occlusion.Polys.Add(shifted);
            }

            foreach (int vertexIndex in roomOcclusion.VertexIndices)
            {
                occlusion.VertexIndices.Add(vertexIndex + plan.VertexBase);
            }
        }

        // LeafMinDistToWater: vvis rewrote it as one ushort per leaf
        // (Vvis.cs ToBytes(ushort[])); the shared solid leaf gets 65535,
        // which is VisWater.NoWater's ushort spelling.
        List<byte> leafMinDist = [];
        bool anyWaterLump = false;
        foreach (RoomPlan plan in plans)
        {
            anyWaterLump |= plan.Bsp[BspLump.LeafMinDistToWater].Length > 0;
        }

        if (anyWaterLump)
        {
            leafMinDist.AddRange([0xFF, 0xFF]);
            foreach (RoomPlan plan in plans)
            {
                BspLumpData dists = plan.Bsp[BspLump.LeafMinDistToWater];
                if (dists.Length != plan.LeafCount * sizeof(ushort))
                {
                    throw new LinkException(
                        $"room {plan.Placement.Room.Definition.Name}'s LeafMinDistToWater holds {dists.Length} bytes for {plan.LeafCount} leaves");
                }

                leafMinDist.AddRange(dists.Data.Span.ToArray());
            }
        }

        // Entities: concatenated verbatim. Documented debt — entity relocation
        // (info_player_start and friends back through RoomTransform) is the
        // follow-up; nothing the room tests check reads this lump.
        List<byte> entities = [];
        foreach (RoomPlan plan in plans)
        {
            entities.AddRange(plan.Bsp[BspLump.Entities].Data.Span.ToArray());
        }

        // Models: one merged world model over the grid, hanging on the top root.
        DModel[] models =
        [
            new DModel
            {
                Mins = new Vec3(minx * cell, miny * cell, 0),
                Maxs = new Vec3((maxx + 1) * cell, (maxy + 1) * cell, cell),
                Origin = new Vec3((minx * cell + (maxx + 1) * cell) / 2f, (miny * cell + (maxy + 1) * cell) / 2f, cell / 2f),
                HeadNode = 0,
                FirstFace = 0,
                NumFaces = faces.Count,
            },
        ];

        RoomPlan first = plans[0];
        linked.FileVersion = first.Bsp.FileVersion;
        foreach (RoomPlan plan in plans)
        {
            if (plan.FacesVersion != first.FacesVersion || plan.LeafsVersion != first.LeafsVersion)
            {
                throw new LinkException(
                    $"room {plan.Placement.Room.Definition.Name}'s Faces/Leafs lump versions "
                    + $"({plan.FacesVersion}/{plan.LeafsVersion}) disagree with "
                    + $"{first.Placement.Room.Definition.Name}'s ({first.FacesVersion}/{first.LeafsVersion})");
            }
        }


        linked.SetLump(BspLump.Entities, entities.ToArray());
        linked.SetLump(BspLump.Planes, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(planes)).ToArray());
        linked.SetLump(BspLump.TexData, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(texDatas)).ToArray());
        linked.SetLump(BspLump.Vertexes, plans.SelectMany(p => p.Vertices).ToArray());
        linked.SetLump(BspLump.Visibility, visibilityLump);
        linked.SetLump(BspLump.Nodes, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(nodes)).ToArray());
        linked.SetLump(BspLump.TexInfo, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(texInfos)).ToArray());
        linked.SetLump(BspLump.Faces, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(faces)).ToArray(), first.FacesVersion);
        linked.SetLump(BspLump.Lighting, lighting.ToArray());
        linked.SetLump(BspLump.Leafs, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(leafs)).ToArray(), first.LeafsVersion);
        linked.SetLump(BspLump.Edges, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(edges)).ToArray());
        linked.SetLump(BspLump.Models, MemoryMarshal.AsBytes(models.AsSpan()).ToArray());
        linked.SetLump(BspLump.LeafFaces, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(leafFaces)).ToArray());
        linked.SetLump(BspLump.LeafBrushes, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(leafBrushes)).ToArray());
        linked.SetLump(BspLump.Brushes, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(brushes)).ToArray());
        linked.SetLump(BspLump.BrushSides, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(brushSides)).ToArray());
        linked.SetLump(BspLump.TexDataStringData, stringData.ToArray());
        linked.SetLump(BspLump.TexDataStringTable, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(stringTable)).ToArray());
        linked.SetLump(BspLump.SurfEdges, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(surfEdges)).ToArray());
        linked.SetLump(BspLump.FaceIds, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(faceIds)).ToArray());
        linked.SetLump(BspLump.FaceMacroTextureInfo, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(macroTextures)).ToArray());
        linked.SetLump(BspLump.OriginalFaces, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(origFaces)).ToArray());
        linked.SetLump(BspLump.VertNormals, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertNormals)).ToArray());
        linked.SetLump(BspLump.VertNormalIndices, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertNormalIndices)).ToArray());
        linked.SetLump(BspLump.Primitives, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(prims)).ToArray());
        linked.SetLump(BspLump.PrimIndices, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(primIndices)).ToArray());
        linked.SetLump(BspLump.PrimVerts, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(primVerts)).ToArray());
        linked.SetLump(BspLump.Areas, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(areas)).ToArray());
        linked.SetLump(BspLump.AreaPortals, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(areaPortals)).ToArray());
        linked.SetLump(BspLump.ClipPortalVerts, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(clipPortalVerts)).ToArray());
        linked[BspLump.Occlusion] = occlusion.Write();
        linked.SetLump(BspLump.PakFile, first.Bsp[BspLump.PakFile].Data.ToArray());
        linked.SetLump(BspLump.MapFlags, first.Bsp[BspLump.MapFlags].Data.ToArray());
        foreach (GameLumpEntry entry in first.Bsp.GameLumps)
        {
            linked.GameLumps.Add(entry);
        }

        if (anyWaterLump)
        {
            linked.SetLump(BspLump.LeafMinDistToWater, leafMinDist.ToArray());
        }

        return linked;
    }

    /// <summary>Fills the top tree's children now that the room bases exist.</summary>
    private static void FillTopChildren(
        List<DNode> nodes, List<DNode> top, RoomPlan[] plans, LevelLayout layout, float cell)
    {
        Dictionary<(int, int), RoomPlan> byCell = [];
        foreach (RoomPlan plan in plans)
        {
            byCell[(plan.Placement.Instance.Placement.CellX, plan.Placement.Instance.Placement.CellY)] = plan;
        }

        (int minx, int miny, int maxx, int maxy) = Extent(layout);
        _ = (minx, miny, maxx, maxy);
        // The nodes list already holds the top tree built by BuildTopNodes in
        // layout order; its positive children are region-node references and
        // its room-bearing single-cell nodes carry a marker child that names
        // no node yet. Rebuild the children with the same recursion the node
        // list itself used — see BuildRegion — so both walks agree.
        int index = 0;
        Dictionary<(int, int), int> occupants = [];
        foreach (RoomInstance room in layout.Rooms)
        {
            occupants[(room.Placement.CellX, room.Placement.CellY)] = 0;
        }

        List<(int, int, int, int)> regions = [];
        CollectRegions(occupants, (minx, miny, maxx, maxy), regions);
        if (regions.Count != top.Count)
        {
            throw new LinkException(
                "the top-tree region walk disagrees with the top node list");
        }
        foreach (ref DNode node in CollectionsMarshal.AsSpan(nodes).Slice(0, top.Count))
        {
            (int rminx, int rminy, int rmaxx, int rmaxy) = regions[index];
            DNode filled = node;
            IntArray2 children = node.Children;
            for (int side = 0; side < 2; side++)
            {
                int child = node.Children[side];
                if (child >= 0)
                {
                    continue; // a node index: already right (pre-order guarantees it)
                }

                // The markers are stored in the child encoding itself (negative
                // = leaf side), so compare the raw child, not ~child.
                if (child == MarkerRoomLeaf)
                {
                    RoomPlan room = byCell[(rminx, rminy)];
                    children[side] = room.NodeBase;
                }
                else if (child == MarkerSolidLeaf)
                {
                    children[side] = -(0 + 1); // the shared solid leaf
                }
            }

            filled.Children = children;
            node = filled;
            index++;
        }
    }

    /// <summary>The child marker for a room-bearing cell (resolved at assembly).</summary>
    private const int MarkerRoomLeaf = -1000;

    /// <summary>The child marker for a solid region (resolved at assembly).</summary>
    private const int MarkerSolidLeaf = -1001;

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

        bool hasOccupant = false;
        for (int x = rect.minx; x <= rect.maxx; x++)
        {
            for (int y = rect.miny; y <= rect.maxy; y++)
            {
                hasOccupant |= occupants.ContainsKey((x, y));
            }
        }

        if (!hasOccupant || (rect.minx == rect.maxx && rect.miny == rect.maxy))
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
        List<Plane> planes, int topPlaneBase)
    {
        int index = nodes.Count;
        nodes.Add(default);

        bool hasOccupant = false;
        for (int x = rect.minx; x <= rect.maxx; x++)
        {
            for (int y = rect.miny; y <= rect.maxy; y++)
            {
                hasOccupant |= occupants.ContainsKey((x, y));
            }
        }

        Vec3 mins = new(rect.minx * cellSize, rect.miny * cellSize, 0);
        Vec3 maxs = new((rect.maxx + 1) * cellSize, (rect.maxy + 1) * cellSize, cellSize);

        if (!hasOccupant)
        {
            // A solid region: split at an arbitrary cell face and send both
            // sides to the shared solid leaf.
            planes.Add(new Plane(new Vec3(1, 0, 0), (rect.maxx + 1) * cellSize));
            IntArray2 solidChildren = default;
            solidChildren[0] = MarkerSolidLeaf;
            solidChildren[1] = MarkerSolidLeaf;
            nodes[index] = new DNode
            {
                PlaneNum = topPlaneNum(topPlaneBase, planes.Count - 1),
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
                PlaneNum = topPlaneNum(topPlaneBase, planes.Count - 1),
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
        int planeNumber = topPlaneNum(topPlaneBase, planes.Count - 1);
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

    private static int topPlaneNum(int topPlaneBase, int index) => topPlaneBase + (2 * index);


    /// <summary>A room's link-time facts: its bytes, counts, and assigned bases.</summary>
    internal sealed class RoomPlan
    {
        public required ResolvedPlacement Placement { get; init; }
        public required BspData Bsp { get; init; }
        public required byte[][] OwnRows { get; init; }
        public required int ClusterCount { get; init; }
        public required byte[] Vertices { get; init; }
        public required DPlane[] TransformedPlanes { get; init; }
        public required DLeaf[] Leafs { get; init; }
        public required int LeafCount { get; init; }
        public required int EdgeCount { get; init; }
        public required int TexInfoCount { get; init; }
        public required int TexDataCount { get; init; }
        public required int FaceCount { get; init; }
        public required int BrushCount { get; init; }
        public required int BrushSideCount { get; init; }
        public required int LeafFaceCount { get; init; }
        public required int LeafBrushCount { get; init; }
        public required int NodeCount { get; init; }
        public required int LightingLength { get; init; }
        public required int StringTableCount { get; init; }
        public required int StringDataLength { get; init; }
        public required int TopPlaneCount { get; init; }
        public required Vec3 ModelMins { get; init; }
        public required Vec3 ModelMaxs { get; init; }
        public required int SurfEdgeCount { get; init; }
        public required int OrigFaceCount { get; init; }
        public required int VertNormalCount { get; init; }
        public required int VertNormalIndexCount { get; init; }
        public required int PrimCount { get; init; }
        public required int PrimIndexCount { get; init; }
        public required int PrimVertCount { get; init; }
        public required int AreaCount { get; init; }
        public required int AreaPortalCount { get; init; }
        public required int ClipPortalVertCount { get; init; }
        public required OcclusionLump? Occlusion { get; init; }
        public required int FacesVersion { get; init; }
        public required int LeafsVersion { get; init; }

        public int VertexBase;
        public int EdgeBase;
        public int TexInfoBase;
        public int TexDataBase;
        public int PlaneBase;
        public int FaceBase;
        public int BrushBase;
        public int BrushSideBase;
        public int LeafFaceBase;
        public int LeafBrushBase;
        public int NodeBase;
        public int LeafBase;
        public int LightBase;
        public int StringTableBase;
        public int StringDataBase;
        public int ClusterBase;
        public int SurfEdgeBase;
        public int OrigFaceBase;
        public int PrimBase;
        public int PrimIndexBase;
        public int PrimVertBase;
        public int VertNormalBase;
        public int AreaBase;
        public int AreaPortalBase;
        public int ClipPortalVertBase;
        public int OccluderPolyBase;
        public int OccluderVertexBase;
        public int VertexNormalIndexBase;
    }

    private static int SizeOf<T>() where T : unmanaged => Marshal.SizeOf<T>();

    private static ShortArray3 Short3(Vec3 v)
    {
        ShortArray3 s = default;
        s[0] = (short)Math.Clamp(MathF.Round(v.X), short.MinValue, short.MaxValue);
        s[1] = (short)Math.Clamp(MathF.Round(v.Y), short.MinValue, short.MaxValue);
        s[2] = (short)Math.Clamp(MathF.Round(v.Z), short.MinValue, short.MaxValue);
        return s;
    }

    private static void Dedupe(List<int> values)
    {
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
