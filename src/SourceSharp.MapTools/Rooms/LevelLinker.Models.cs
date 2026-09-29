//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Validation;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// Every placement's socket furniture, props and brush entities
    /// together: per socket the highest <c>socket_priority</c> of its pieces
    /// (<see cref="SocketFurniture"/>), so a door and a frame hung in one
    /// doorway count as one side's furniture.
    /// </summary>
    internal sealed class LevelFurniture
    {
        private readonly ResolvedPlacement[] _resolved;
        private readonly LevelLayout _layout;
        private readonly Dictionary<string, int>[] _bySocket;

        public LevelFurniture(ResolvedPlacement[] resolved, LevelLayout layout)
        {
            _resolved = resolved;
            _layout = layout;
            _bySocket = new Dictionary<string, int>[resolved.Length];
            for (int p = 0; p < resolved.Length; p++)
            {
                RoomObject room = resolved[p].Room;
                Dictionary<string, int> sockets = new(StringComparer.Ordinal);
                foreach ((int socket, int priority) in Pieces(room))
                {
                    string name = room.Definition.Sockets[socket].Name;
                    sockets[name] = sockets.TryGetValue(name, out int held) ? Math.Max(held, priority) : priority;
                }

                _bySocket[p] = sockets;
            }
        }

        /// <summary>The priority of a placement's furniture on a socket, or null when it has none there.</summary>
        public int? Of(int placement, string socket) =>
            _bySocket[placement].TryGetValue(socket, out int priority) ? priority : null;

        /// <summary>Whether a placement keeps its furniture on one of its sockets (by the socket's index).</summary>
        public bool Keeps(int placement, int socket) =>
            SocketFurniture.Keeps(
                _layout, i => _resolved[i].Room.Definition, placement, _resolved[placement].Room.Definition.Sockets[socket].Name, Of);

        /// <summary>A room's furniture pieces: its props' and its brush models' sockets and priorities.</summary>
        private static IEnumerable<(int Socket, int Priority)> Pieces(RoomObject room)
        {
            foreach (RoomProp prop in room.StaticProps?.Props ?? [])
            {
                if (prop.Socket >= 0)
                {
                    yield return (prop.Socket, prop.Priority);
                }
            }

            foreach (RoomBrushModel model in room.BrushModelsOfCompile?.Models ?? [])
            {
                if (model.Socket >= 0)
                {
                    yield return (model.Socket, model.Priority);
                }
            }
        }
    }

    /// <summary>
    /// The level's brush models before the tree exists: per placement, per
    /// brush model of its room, the model's index in the linked map, or -1
    /// when the level omits it.
    /// </summary>
    /// <param name="Linked">Per placement, per brush model (model 1 first), its linked index or -1; null for a room with none.</param>
    /// <param name="Count">How many models the linked map has, the world included.</param>
    internal sealed record LevelModels(int[]?[] Linked, int Count);

    /// <summary>
    /// Plans a level's brush models: every placed room's, kept or omitted by
    /// <c>room_needs</c> and the socket furniture rule, numbered in link
    /// order after the world.
    /// </summary>
    /// <param name="resolved">The placements, in link order.</param>
    /// <param name="layout">The level.</param>
    /// <param name="furniture">The level's socket furniture.</param>
    /// <param name="transitions">The level's transitions, whose omitted volumes are left out; null for a level without them.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="LinkException">The level passes <c>MAX_MAP_MODELS</c>, naming the placement that crossed it.</exception>
    /// <remarks>
    /// <para>
    /// <b>The same rule as the entities'.</b> A brush model is kept when its
    /// entity's <c>room_needs</c> holds for the placement
    /// (<see cref="RoomNeeds.Hold"/>, the naming resolver's (c) rule, which
    /// drops the entity in the same case) and, for socket furniture, when the
    /// level keeps that side's furniture (<see cref="SocketFurniture"/>, which
    /// the flatten applies too). An omitted model's entity is not in the
    /// level either, so nothing names it.
    /// </para>
    /// <para>
    /// <b>Transition volumes.</b> A transition room's volume
    /// (<see cref="LevelTransitionPlan"/>) is omitted too when the level
    /// writes <c>logic_level_transition</c> in its place or folds the hallway
    /// trigger into the changelevel; the resolver drops its entity in the
    /// same cases.
    /// </para>
    /// <para>
    /// <b>Numbering.</b> The world is model 0; then placements in link
    /// order, each room's kept models in its own model order. It depends on
    /// the layout alone, so the entity lump's <c>*N</c> keys and the model
    /// lump agree, and the output is a function of the level.
    /// </para>
    /// </remarks>
    internal static LevelModels PlanModels(
        ResolvedPlacement[] resolved, LevelLayout layout, LevelFurniture furniture, LevelTransitionPlan? transitions = null)
    {
        int cap = BspLimits.Caps.First(c => c.Lump == BspLump.Models).Max;
        HashSet<(int, int)> occupied = [.. layout.Rooms.Select(r => (r.Placement.CellX, r.Placement.CellY))];
        int[]?[] linked = new int[]?[resolved.Length];
        int next = 1;
        for (int p = 0; p < resolved.Length; p++)
        {
            if (resolved[p].Room.BrushModelsOfCompile is not { } models)
            {
                continue;
            }

            RoomPlacement where = resolved[p].Instance.Placement;
            JoinedMask joined = JoinedSides(resolved[p].Room.Definition, resolved[p].Instance);
            int[] map = new int[models.Models.Count];
            int omittedVolume = transitions?.Placements[p] is { } transition && transition.OmitsVolume(transitions.ModEntities)
                ? transition.VolumeId
                : int.MinValue;
            for (int m = 0; m < map.Length; m++)
            {
                RoomBrushModel model = models.Models[m];
                bool keep = RoomNeeds.Hold(model.Needs, where.NormalizedRotation, where.CellX, where.CellY, occupied.Contains, joined)
                    && (model.Socket < 0 || furniture.Keeps(p, model.Socket))
                    && model.Id != omittedVolume;
                map[m] = keep ? next++ : -1;
                if (keep)
                {
                    LoaderLimit(resolved[p].Room.Definition.Name, where.CellX, where.CellY, "models", next, cap, "MAX_MAP_MODELS");
                }
            }

            linked[p] = map;
        }

        return new LevelModels(linked, next);
    }

    /// <summary>
    /// A placed room's transition data (<see cref="RoomTransit"/>), or null
    /// for a room without a role or spawn points; refuses a room whose
    /// compile has a transition volume but that carries no transition data
    /// from its compile (a pack written before transitions), which would
    /// otherwise link its volume as an ordinary brush entity.
    /// </summary>
    private static RoomTransit? TransitOf(RoomObject room)
    {
        if (room.TransitOfCompile is { } transit)
        {
            return transit;
        }

        if (room.BrushModelsOfCompile?.Models.Any(m => m.ClassName == RoomTransit.VolumeClass) == true)
        {
            throw new LinkException(
                $"room {room.Definition.Name} has a {RoomTransit.VolumeClass} but no transition data from its compile"
                + " (a pack written before the link carried transitions, or a room built without ssmap room);"
                + " recompile the library with ssmap room.");
        }

        return null;
    }

    /// <summary>
    /// Refuses a room whose compile has brush models besides the world but
    /// carries no brush model data from its compile: which brushes are an
    /// entity's, and its furniture keys, are not in the lumps, so linking it
    /// would mean guessing its models' brushes.
    /// </summary>
    private static void RefuseUndescribedModels(RoomObject room)
    {
        int models = BspStructView.Count<DModel>(room.Bsp[BspLump.Models]);
        if (models > 1 && room.BrushModelsOfCompile is null)
        {
            throw new LinkException(
                $"room {room.Definition.Name} has {models - 1} brush entity models but no brush entity data from its compile"
                + " (a pack written before the link carried brush entities, or a room built without ssmap room);"
                + " recompile the library with ssmap room.");
        }
    }

    /// <summary>
    /// One placement's view of its room's brush models: which it keeps, the
    /// runs the omitted ones leave out of each lump, which structures are in
    /// an entity's own frame, and the untranslated copies of the vertices
    /// those use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Omitted runs.</b> A model the level omits takes its nodes, leaves,
    /// leaf faces, faces (with their face ids, macro textures and
    /// vertex-normal index runs), brushes and brush sides out of the linked
    /// map; every index past such a run shifts down by the run's length
    /// (<see cref="Kept"/>), a prefix sum over at most a room's handful of
    /// models.
    /// </para>
    /// <para>
    /// <b>Faces, world first.</b> The engine takes model 0's faces as one
    /// range, so the linked face lump is every placement's world faces in
    /// link order, then every kept brush model's faces in link and model
    /// order, as vbsp writes a map's own; each kept model's first linked face
    /// is set when the bases are (<see cref="LinkedFaceStart"/>).
    /// </para>
    /// <para>
    /// <b>Origin-relative models</b> keep their entity's frame: their
    /// vertices, planes, texture axes and boxes take the turn and not the
    /// translation, so they are looked up in tables of their own
    /// (<see cref="LocalVertices"/>, and the plane and texinfo maps the
    /// assembly fills). A plane or vertex vbsp shared between the world and
    /// such a model is linked twice, once per frame.
    /// </para>
    /// </remarks>
    internal sealed class RoomModelLayout
    {
        public required RoomBrushModels Source { get; init; }

        /// <summary>The models at the placement's turn: bounds and collision.</summary>
        public required RoomBrushModelTurn[] Turn { get; init; }

        /// <summary>Per brush model, its linked index or -1 when omitted.</summary>
        public required int[] Linked { get; init; }

        /// <summary>The room's world faces: model 0's, the first run of the face lump.</summary>
        public required int WorldFaces { get; init; }

        /// <summary>The runs of the node lump the omitted models own, ascending.</summary>
        public required RoomRange[] OmittedNodes { get; init; }

        /// <summary>The runs of the leaf lump the omitted models own, ascending.</summary>
        public required RoomRange[] OmittedLeaves { get; init; }

        /// <summary>The runs of the leaf-face lump the omitted models own, ascending.</summary>
        public required RoomRange[] OmittedLeafFaces { get; init; }

        /// <summary>The origin-relative models, kept or not (an omitted one's structures are never reached).</summary>
        public required RoomBrushModel[] LocalModels { get; init; }

        /// <summary>Per room vertex, its index among <see cref="LocalVertices"/>, or -1.</summary>
        public required int[] LocalVertex { get; init; }

        /// <summary>The turned, untranslated copies of the vertices origin-relative edges use, in room vertex order.</summary>
        public required Vec3[] LocalVertices { get; init; }

        /// <summary>Per primitive vertex, whether an origin-relative face's primitive owns it (then it takes no translation).</summary>
        public required bool[] LocalPrimVerts { get; init; }

        /// <summary>Per brush model, its first linked face (kept models only; set with the bases).</summary>
        public int[] LinkedFaceStart { get; set; } = [];

        /// <summary>Per room plane pair used in an entity's frame, the linked even index; -1 elsewhere.</summary>
        public int[] LocalPlanePairs { get; set; } = [];

        /// <summary>Per such pair, whether the shared pair holds it flipped.</summary>
        public bool[] LocalPlanePairFlipped { get; set; } = [];

        /// <summary>Per room texinfo used in an entity's frame, the linked texinfo; -1 elsewhere.</summary>
        public int[] LocalTexInfoMap { get; set; } = [];

        /// <summary>How many of the room's faces past the world's the placement keeps.</summary>
        public int KeptModelFaces => Source.Models.Where((m, i) => Linked[i] >= 0).Sum(m => m.Faces.Count);

        /// <summary>The brush model owning a room node, leaf, face, brush, edge or original face, or null (the world's, or no model's).</summary>
        public RoomBrushModel? Owner(Func<RoomBrushModel, RoomRange> run, int index)
        {
            foreach (RoomBrushModel model in Source.Models)
            {
                if (run(model).Contains(index))
                {
                    return model;
                }
            }

            return null;
        }

        /// <summary>Whether a room node, leaf, face, brush, edge or original face is in an origin-relative model's frame.</summary>
        public bool IsLocal(Func<RoomBrushModel, RoomRange> run, int index)
        {
            foreach (RoomBrushModel model in LocalModels)
            {
                if (run(model).Contains(index))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// An index's place among the ones the placement keeps: -1 inside an
        /// omitted run, else the index less every omitted run before it.
        /// </summary>
        public static int Kept(RoomRange[] omitted, int index)
        {
            int shift = 0;
            foreach (RoomRange run in omitted)
            {
                if (index < run.First)
                {
                    break;
                }

                if (index < run.End)
                {
                    return -1;
                }

                shift += run.Count;
            }

            return index - shift;
        }

        /// <summary>
        /// <see cref="Kept"/>, except that an index inside an omitted run
        /// takes the place the run would have had (for a range start of
        /// length 0, which names no entry).
        /// </summary>
        public static int KeptOrAt(RoomRange[] omitted, int index)
        {
            int shift = 0;
            foreach (RoomRange run in omitted)
            {
                if (index < run.First)
                {
                    break;
                }

                if (index < run.End)
                {
                    return run.First - shift;
                }

                shift += run.Count;
            }

            return index - shift;
        }

        /// <summary>How many indices of a lump of <paramref name="length"/> the placement keeps.</summary>
        public static int KeptCount(RoomRange[] omitted, int length) => length - omitted.Sum(r => r.Count);
    }

    /// <summary>
    /// A placement's view of its room's brush models (<see cref="RoomModelLayout"/>),
    /// or null for a room with only the world.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="rotation">The placement's quarter turns.</param>
    /// <param name="linked">Per brush model, its linked index or -1 (<see cref="PlanModels"/>).</param>
    /// <param name="turnedVertices">The room's vertices, turned for the placement and not moved.</param>
    /// <exception cref="LinkException">The room's brush model faces do not follow the world's, model by model.</exception>
    private static RoomModelLayout? LayoutModels(RoomObject room, int rotation, int[]? linked, Vec3[] turnedVertices)
    {
        RefuseUndescribedModels(room);
        if (room.BrushModelsOfCompile is not { } models || linked is null)
        {
            return null;
        }

        BspData bsp = room.Bsp;
        DModel world = BspStructView.As<DModel>(bsp[BspLump.Models])[0];
        int expected = world.FirstFace + world.NumFaces;
        foreach (RoomBrushModel model in models.Models)
        {
            if (world.FirstFace != 0 || model.Faces.Count > 0 && model.Faces.First != expected)
            {
                throw new LinkException(
                    $"room {room.Definition.Name}'s brush model {model.Model} does not follow the world's faces; brush models are not laid out as vbsp lays them out");
            }

            expected += model.Faces.Count;
        }

        List<RoomBrushModel> omitted = [.. models.Models.Where((m, i) => linked[i] < 0)];
        RoomBrushModel[] local = [.. models.Models.Where(m => m.OriginRelative)];

        // The vertices an origin-relative model's edges use, copied in their
        // own frame: vbsp's vertex table is shared by every model, so a
        // world face and a model face may name one vertex, which the world
        // needs moved and the model needs only turned.
        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(bsp[BspLump.Edges]);
        int[] localVertex = new int[turnedVertices.Length];
        Array.Fill(localVertex, -1);
        SortedSet<int> used = [];
        foreach (RoomBrushModel model in local)
        {
            for (int e = model.Edges.First; e < model.Edges.End; e++)
            {
                used.Add(edges[e].V[0]);
                used.Add(edges[e].V[1]);
            }
        }

        List<Vec3> copies = new(used.Count);
        foreach (int v in used)
        {
            localVertex[v] = copies.Count;
            copies.Add(turnedVertices[v]);
        }

        // Their primitives' vertices likewise take no translation.
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DPrimitive> prims = BspStructView.As<DPrimitive>(bsp[BspLump.Primitives]);
        bool[] localPrimVerts = new bool[BspStructView.Count<Vec3>(bsp[BspLump.PrimVerts])];
        foreach (RoomBrushModel model in local)
        {
            for (int f = model.Faces.First; f < model.Faces.End; f++)
            {
                for (int p = faces[f].FirstPrimId; p < faces[f].FirstPrimId + faces[f].GetNumPrims(); p++)
                {
                    for (int v = prims[p].FirstVert; v < prims[p].FirstVert + prims[p].VertCount; v++)
                    {
                        localPrimVerts[v] = true;
                    }
                }
            }
        }

        return new RoomModelLayout
        {
            Source = models,
            Turn = models.Turn(rotation),
            Linked = linked,
            WorldFaces = world.NumFaces,
            OmittedNodes = [.. omitted.Select(m => m.Nodes).Where(r => r.Count > 0).OrderBy(r => r.First)],
            OmittedLeaves = [.. omitted.Select(m => m.Leaves).Where(r => r.Count > 0).OrderBy(r => r.First)],
            OmittedLeafFaces = [.. omitted.Select(m => m.LeafFaces).Where(r => r.Count > 0).OrderBy(r => r.First)],
            LocalModels = local,
            LocalVertex = localVertex,
            LocalVertices = [.. copies],
            LocalPrimVerts = localPrimVerts,
        };
    }

    /// <summary>
    /// How many vertices a room adds at most for its origin-relative models'
    /// own frame (<see cref="RoomModelLayout.LocalVertices"/>): the capacity
    /// check's share, counting every such model whether a level keeps it.
    /// </summary>
    internal static int LocalVertexCount(RoomObject room)
    {
        if (room.BrushModelsOfCompile is not { } models)
        {
            return 0;
        }

        ReadOnlySpan<DEdge> edges = BspStructView.As<DEdge>(room.Bsp[BspLump.Edges]);
        HashSet<int> used = [];
        foreach (RoomBrushModel model in models.Models.Where(m => m.OriginRelative))
        {
            for (int e = model.Edges.First; e < model.Edges.End; e++)
            {
                used.Add(edges[e].V[0]);
                used.Add(edges[e].V[1]);
            }
        }

        return used.Count;
    }

    /// <summary>
    /// Finds the planes and texinfos a room's origin-relative models use in
    /// the shared tables, in their own frame: turned, never moved.
    /// </summary>
    /// <remarks>
    /// Run right after the room's own entries are interned, so the tables
    /// stay in layout order; a room with no such model adds nothing, and a
    /// level without brush entities has the tables it always had.
    /// </remarks>
    private static void InternLocalTables(RoomPlan plan, LinkPlanes planes, LinkTextures textures, int[] texDatas)
    {
        if (plan.Models is not { LocalModels.Length: > 0 } layout)
        {
            return;
        }

        BspData bsp = plan.Bsp;
        int pairs = plan.Geometry.PlanePairs.Length;
        bool[] pairUsed = new bool[pairs];
        int texInfoCount = plan.Geometry.TexInfos.Length;
        bool[] texInfoUsed = new bool[texInfoCount];
        void Plane(int planeNum) => pairUsed[planeNum >> 1] = true;
        void TexInfo(int texInfo)
        {
            if (texInfo >= 0 && texInfo < texInfoCount)
            {
                texInfoUsed[texInfo] = true;
            }
        }

        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DFace> origFaces = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]);
        ReadOnlySpan<DBrush> brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]);
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        foreach (RoomBrushModel model in layout.LocalModels)
        {
            for (int n = model.Nodes.First; n < model.Nodes.End; n++)
            {
                Plane(nodes[n].PlaneNum);
            }

            for (int f = model.Faces.First; f < model.Faces.End; f++)
            {
                Plane(faces[f].PlaneNum);
                TexInfo(faces[f].TexInfo);
            }

            for (int f = model.OrigFaces.First; f < model.OrigFaces.End; f++)
            {
                Plane(origFaces[f].PlaneNum);
                TexInfo(origFaces[f].TexInfo);
            }

            for (int b = model.Brushes.First; b < model.Brushes.End; b++)
            {
                for (int s = brushes[b].FirstSide; s < brushes[b].FirstSide + brushes[b].NumSides; s++)
                {
                    Plane(sides[s].PlaneNum);
                    TexInfo(sides[s].TexInfo);
                }
            }
        }

        DPlane[] own = TranslatePlanes(plan.Geometry.PlanePairs, plan.Geometry.PlaneSwapped, Vec3.Zero);
        layout.LocalPlanePairs = new int[pairs];
        layout.LocalPlanePairFlipped = new bool[pairs];
        Array.Fill(layout.LocalPlanePairs, -1);
        for (int k = 0; k < pairs; k++)
        {
            if (pairUsed[k])
            {
                (layout.LocalPlanePairs[k], layout.LocalPlanePairFlipped[k]) = planes.Intern(own[2 * k]);
            }
        }

        layout.LocalTexInfoMap = new int[texInfoCount];
        Array.Fill(layout.LocalTexInfoMap, -1);
        for (int t = 0; t < texInfoCount; t++)
        {
            if (texInfoUsed[t])
            {
                TexInfo info = plan.Geometry.TexInfos[t];
                info.TexData = Remap(texDatas, info.TexData);
                layout.LocalTexInfoMap[t] = textures.InternTexInfo(info);
            }
        }
    }
}
