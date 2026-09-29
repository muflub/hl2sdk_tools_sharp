//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Phys.Managed;

using SourceSharp.RoomContracts;

namespace SourceSharp.MapTools.Rooms;

/// <summary>A run of consecutive indices into one lump: <c>[First, First + Count)</c>.</summary>
/// <param name="First">The first index.</param>
/// <param name="Count">How many; 0 for an empty run, whose first index means nothing.</param>
internal readonly record struct RoomRange(int First, int Count)
{
    /// <summary>One past the last index.</summary>
    public int End => First + Count;

    /// <summary>Whether an index is in the run.</summary>
    public bool Contains(int index) => Count > 0 && index >= First && index < First + Count;
}

/// <summary>
/// One brush model of a room other than the world: the brush entity that
/// names it, how it moves with a turned placement, the conditions that keep
/// it in a level, and the runs of the room's lumps it owns.
/// </summary>
/// <param name="Model">Its index in the room's model lump, 1 or more.</param>
/// <param name="Id">The brush entity's Hammer id, for messages; -1 when it had none.</param>
/// <param name="ClassName">The brush entity's class.</param>
/// <param name="OriginRelative">
/// Whether its geometry is in the entity's own frame (the entity has a
/// non-zero <c>origin</c>, from an origin brush or its key, and vbsp rebuilt
/// its brushes relative to it): then its vertices, planes, texture axes,
/// bounds and collision turn with the placement and take no translation, the
/// entity's moved <c>origin</c> supplying it. A world-coordinate model is
/// moved as the world is.
/// </param>
/// <param name="Needs">Its entity's <c>room_needs</c> conditions in the room's authored frame; empty when it has none.</param>
/// <param name="Socket">The index of the socket its entity's <c>room_socket</c> names, or -1 when it is not socket furniture.</param>
/// <param name="Priority">Its entity's <c>socket_priority</c>, 0 when it has none.</param>
/// <param name="Nodes">The nodes of its tree.</param>
/// <param name="Leaves">The leaves of its tree.</param>
/// <param name="Faces">Its drawn faces, the model's own face range.</param>
/// <param name="LeafFaces">The leaf-face entries its leaves list.</param>
/// <param name="Brushes">Its entity's brushes, and with them their sides.</param>
/// <param name="Edges">The edges its drawn and original faces run through, which no other model's faces use.</param>
/// <param name="OrigFaces">The original faces its drawn faces were cut from.</param>
/// <param name="VertNormalIndices">Its faces' runs of vertex-normal indices, which the engine walks in face order.</param>
/// <param name="KeyData">
/// The key data of its record in the room's collision lump (its
/// <c>solid</c> blocks: mass, material, volume, none of which a turn
/// changes), or null when it has no record.
/// </param>
/// <param name="OuterHulls">
/// Per solid of that record, whether its surface was built with an outer
/// convex hull (vbsp builds one for a model of more than one convex), so the
/// link rebuilds it the same way.
/// </param>
internal sealed record RoomBrushModel(
    int Model,
    int Id,
    string ClassName,
    bool OriginRelative,
    ImmutableArray<RoomNeed> Needs,
    int Socket,
    int Priority,
    RoomRange Nodes,
    RoomRange Leaves,
    RoomRange Faces,
    RoomRange LeafFaces,
    RoomRange Brushes,
    RoomRange Edges,
    RoomRange OrigFaces,
    RoomRange VertNormalIndices,
    byte[]? KeyData,
    ImmutableArray<bool> OuterHulls);

/// <summary>One solid of a brush model's collision at one quarter turn.</summary>
/// <param name="DragAreas">The surface's orthographic areas, in map axes, turned (x and y swap on an odd turn).</param>
/// <param name="Ledges">Its convexes, taken out of the room's surface and turned, the room's brush indices as their client data.</param>
internal sealed record RoomModelSolid(Vec3 DragAreas, RoomLinkSolid Ledges);

/// <summary>A brush model at one quarter turn: its bounds and its collision, turned and not yet moved to a cell.</summary>
/// <param name="Bounds">The model's bounds, turned.</param>
/// <param name="Solids">Its collision's solids, in the record's order; empty when it has no record.</param>
internal sealed record RoomBrushModelTurn(Box Bounds, IReadOnlyList<RoomModelSolid> Solids);

/// <summary>
/// A room's brush entities as the link carries them: per brush model, what
/// it is, what keeps it in a level, which runs of the room's lumps it owns,
/// and its bounds and collision at each quarter turn. What <c>ssmap room</c>
/// works out once per room so that a level links its brush entities as their
/// own models (the rooms design, 4.1) without reading anything but the pack.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the compile leaves, and what it does not.</b> vbsp gives every
/// brush entity a model (<c>"model" "*N"</c>, in entity order) with its own
/// tree, faces, brushes and collision record, laid out in the room's lumps
/// right after the world's and after each other, model by model. The
/// compiled lumps say where each tree starts and which faces it has, but not
/// which brushes are its entity's (a brush vbsp chopped away is in no leaf,
/// yet it is the entity's), nor which of the room's plane and texinfo entries
/// are in the entity's own frame. This records, per model, every run it owns
/// (read from the compile, and the brushes from the map the compile loaded),
/// whether it is origin-relative, its entity's <c>room_needs</c>,
/// <c>room_socket</c> and <c>socket_priority</c>, and its collision's convexes.
/// </para>
/// <para>
/// <b>Why the runs.</b> A brush entity dropped by <c>room_needs</c> or by the
/// socket furniture rule is omitted from the level whole (the rooms design,
/// 5.8 (c)): its model, nodes, leaves, leaf faces, drawn faces (with their
/// face ids, macro textures and vertex-normal index runs), brushes and
/// brush sides, and its collision record, and everything after it in each
/// lump shifts down (<see cref="LevelLinker"/>'s prefix-sum maps). Its
/// vertices, edges, surfedges, original faces, primitives and lightmap bytes
/// are kept: vertices may be shared with other models, and the rest is
/// addressed only through the dropped faces, so it is unreachable.
/// </para>
/// <para>
/// <b>Per turn, stored four times.</b> The bounds and the collision convexes
/// are turned at pack time for all four quarter turns (the rooms design's
/// pack rule, 1.1), so the link only adds a world-coordinate model's
/// translation; a section may hold only turn 0 (rotation count 1), which the
/// link turns to the same bytes (<see cref="Turn"/>). The rest of the model's
/// geometry (vertices, planes, texture axes, node and leaf bounds) is the
/// room's, already stored turned in its <c>GEO</c><i>r</i> sections.
/// </para>
/// <para>
/// <b>Refused at pack time.</b> A <c>room_socket</c> naming no socket of the
/// room and a <c>socket_priority</c> that is not a whole number, as for a
/// static prop. (The angles rule, <see cref="BrushEntityDirections"/>, is
/// checked on the VMF before the compile.)
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>BMOD</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>), and a payload that starts
/// with an <c>int32</c> revision (<see cref="Revision"/>). After it,
/// big-endian <c>int32</c>s except where said: the model count (the room's
/// models less the world); per model its model index, Hammer id, class
/// (string), a flags byte (1 origin-relative), its condition count and per
/// condition a direction byte and a flags byte (1 joined, 2 negated), its
/// socket (-1 for none) and priority, eight runs (first, count) in the order
/// of the record (nodes to vertex-normal indices), its key data (a length, -1 for none, and the bytes) and
/// per solid an outer-hull byte (a count first); then the turn count, 1 or
/// 4, and per turn per model its bounds (six little-endian floats) and per
/// solid its drag areas (three floats), its convex count and its convexes'
/// bytes (a length, then the bytes back to back).
/// A section of a revision this build does not read is absent (and a room
/// with brush models but no section is refused at link, naming the room);
/// a section that does not fit the room's compile is refused as damaged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile: read from a pack or built by
/// the room compile, it remembers the BSP, and the link uses it only for
/// that very BSP (<see cref="IsFor"/>).
/// </para>
/// </remarks>
internal sealed class RoomBrushModels
{
    /// <summary>The tag of a room's brush model section.</summary>
    public const string SectionTag = "BMOD";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>The key-data length that means "no collision record".</summary>
    private const int NoRecord = -1;

    private readonly BspData? _bsp;
    private readonly RoomBrushModelTurn[][] _turns;

    private RoomBrushModels(IReadOnlyList<RoomBrushModel> models, RoomBrushModelTurn[][] turns, BspData? bsp)
    {
        Models = models;
        _turns = turns;
        _bsp = bsp;
    }

    /// <summary>The room's brush models, model 1 first, in model order.</summary>
    public IReadOnlyList<RoomBrushModel> Models { get; }

    /// <summary>How many turns are stored: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _turns.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The models at one quarter turn: the stored turn, or turn 0 turned when only it is stored.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomBrushModelTurn[] Turn(int rotation)
    {
        if (_turns.Length == 4)
        {
            return _turns[rotation];
        }

        return [.. _turns[0].Select(model => TurnModel(model, rotation))];
    }

    /// <summary>The same models stored with only turn 0 (the link turns it), for the facts that hold the two storages to one linked map.</summary>
    internal RoomBrushModels WithTurnZeroOnly() => new(Models, [_turns[0]], _bsp);

    /// <summary>The models bound to another BSP: what the pack reader does once it has loaded the room they sit with.</summary>
    internal RoomBrushModels For(BspData bsp) => new(Models, _turns, bsp);

    /// <summary>
    /// A room's brush models for the link, or null when its compile has none
    /// but the world: each model's runs read from the compile, its brushes
    /// from the map the compile loaded, its entity's keys checked, and its
    /// bounds and collision turned four ways.
    /// </summary>
    /// <param name="definition">The room.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="map">The map the compile loaded, as vbsp left it (its entities' brush runs and <c>model</c> keys).</param>
    /// <returns>The models, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="RoomLintException">A furniture key names no socket of the room, or no number.</exception>
    /// <exception cref="MapCompileException">
    /// The compile is not laid out as vbsp lays models out (a model whose
    /// entity is missing, a tree or run that is not its own); a bug, never an
    /// author's mistake.
    /// </exception>
    public static RoomBrushModels? Build(RoomDefinition definition, BspData bsp, MapFile map)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(map);
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        if (models.Length <= 1)
        {
            return null;
        }

        string room = definition.Name;
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]);
        DLeaf[] leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        ReadOnlySpan<DFace> origFaces = BspStructView.As<DFace>(bsp[BspLump.OriginalFaces]);
        ReadOnlySpan<int> surfEdges = BspStructView.As<int>(bsp[BspLump.SurfEdges]);
        int vertNormalIndices = BspStructView.Count<ushort>(bsp[BspLump.VertNormalIndices]);
        int faceEdges = 0;
        foreach (DFace face in faces)
        {
            faceEdges += face.NumEdges;
        }

        if (vertNormalIndices != 0 && vertNormalIndices != faceEdges)
        {
            throw Layout(room, $"has {vertNormalIndices} vertex-normal indices for {faceEdges} face vertices");
        }

        IReadOnlyList<PhysCollideModel> records = bsp[BspLump.PhysCollide].Length == 0
            ? []
            : PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span);

        List<RoomBrushModel> described = new(models.Length - 1);
        List<RoomBrushModelTurn>[] turns = [[], [], [], []];
        for (int k = 1; k < models.Length; k++)
        {
            DModel model = models[k];
            string key = string.Create(CultureInfo.InvariantCulture, $"*{k}");
            BspEntity entity = entities.SingleOrDefault(e => e.Get("model") == key)
                ?? throw Layout(room, $"has no entity naming model {key}");
            MapEntity source = map.Entities.SingleOrDefault(e => e.ValueForKey("model") == key)
                ?? throw Layout(room, $"loaded no entity naming model {key}");

            // The tree: the nodes and leaves its head reaches, which vbsp
            // numbers in one pre-order walk per model, so they are runs.
            (RoomRange nodeRun, RoomRange leafRun) = TreeRuns(room, k, model.HeadNode, nodes);
            RoomRange faceRun = new(model.FirstFace, model.NumFaces);
            RoomRange leafFaceRun = Span(room, k, "leaf faces", leafRun, l => (leafs[l].FirstLeafFace, leafs[l].NumLeafFaces));
            RoomRange origRun = Span(room, k, "original faces", faceRun, f => faces[f].OrigFace >= 0 ? (faces[f].OrigFace, 1) : (0, 0));
            RoomRange edgeRun = EdgeRun(room, k, faceRun, origRun, faces, origFaces, surfEdges);
            RoomRange vniRun = vertNormalIndices == 0 ? default : VertNormalRun(faces, faceRun);

            string className = entity.ClassName ?? string.Empty;
            int id = int.TryParse(entity.Get("hammerid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hammer) ? hammer : -1;
            PhysCollideModel? record = records.FirstOrDefault(r => r.ModelIndex == k);
            List<RoomLinkSolid> solids = [];
            List<Vec3> areas = [];
            foreach (byte[] blob in record?.Solids ?? [])
            {
                List<byte[]> ledges = [.. IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)).Select(l => l.Bytes)];
                solids.Add(RoomLinkSolid.Of(0, ledges));
                areas.Add(new Vec3(
                    BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(12)),
                    BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(16)),
                    BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(20))));
            }

            described.Add(new RoomBrushModel(
                k,
                id,
                className,
                IsOriginRelative(entity),
                Needs(room, id, className, entity),
                Socket(definition, id, className, entity),
                Priority(room, id, className, entity),
                nodeRun,
                leafRun,
                faceRun,
                leafFaceRun,
                new RoomRange(source.FirstBrush, source.BrushCount),
                edgeRun,
                origRun,
                vniRun,
                record?.KeyData,
                [.. solids.Select(s => s.Starts.Length > 1)]));

            RoomBrushModelTurn turn0 = new(new Box(model.Mins, model.Maxs), [.. solids.Select((s, i) => new RoomModelSolid(areas[i], s))]);
            for (int t = 0; t < 4; t++)
            {
                turns[t].Add(TurnModel(turn0, t));
            }
        }

        CheckDisjoint(room, described);
        return new RoomBrushModels(described, [.. turns.Select(t => t.ToArray())], bsp);
    }

    /// <summary>
    /// A model at turn 0 turned by quarter turns: its bounds as every box of
    /// the link turns (<see cref="LevelLinker.RotateBox"/>), its convexes
    /// turned in IVP's axes (<see cref="LevelLinker.RotateLedge"/>), and its
    /// drag areas with x and y swapped on an odd turn. Turn 0 is the model
    /// itself.
    /// </summary>
    internal static RoomBrushModelTurn TurnModel(RoomBrushModelTurn model, int rotation)
    {
        if (rotation == 0)
        {
            return model;
        }

        List<RoomModelSolid> solids = new(model.Solids.Count);
        foreach (RoomModelSolid solid in model.Solids)
        {
            List<byte[]> ledges = new(solid.Ledges.Starts.Length);
            for (int l = 0; l < solid.Ledges.Starts.Length; l++)
            {
                IvpCompactLedge ledge = new(solid.Ledges.Ledge(l).ToArray());
                LevelLinker.RotateLedge(ledge, rotation);
                ledges.Add(ledge.Bytes);
            }

            Vec3 a = solid.DragAreas;
            solids.Add(new RoomModelSolid((rotation & 1) == 0 ? a : new Vec3(a.Y, a.X, a.Z), RoomLinkSolid.Of(solid.Ledges.Contents, ledges)));
        }

        return new RoomBrushModelTurn(LevelLinker.RotateBox(model.Bounds.Mins, model.Bounds.Maxs, rotation), solids);
    }

    /// <summary>The pack section holding these models.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Models.Count);
        foreach (RoomBrushModel model in Models)
        {
            w.Int(model.Model);
            w.Int(model.Id);
            w.String(model.ClassName);
            w.Byte(model.OriginRelative ? (byte)1 : (byte)0);
            w.Int(model.Needs.Length);
            foreach (RoomNeed need in model.Needs)
            {
                w.Byte((byte)need.Direction);
                w.Byte((byte)((need.Joined ? 1 : 0) | (need.Negated ? 2 : 0)));
            }

            w.Int(model.Socket);
            w.Int(model.Priority);
            foreach (RoomRange range in Runs(model))
            {
                w.Int(range.First);
                w.Int(range.Count);
            }

            if (model.KeyData is { } keyData)
            {
                w.Int(keyData.Length);
                w.Raw(keyData);
            }
            else
            {
                w.Int(NoRecord);
            }

            w.Int(model.OuterHulls.Length);
            foreach (bool hull in model.OuterHulls)
            {
                w.Byte(hull ? (byte)1 : (byte)0);
            }
        }

        w.Int(_turns.Length);
        foreach (RoomBrushModelTurn[] turn in _turns)
        {
            foreach (RoomBrushModelTurn model in turn)
            {
                w.Box(model.Bounds);
                foreach (RoomModelSolid solid in model.Solids)
                {
                    w.Structs<Vec3>([solid.DragAreas], counted: false);
                    w.Int(solid.Ledges.Starts.Length);
                    w.Int(solid.Ledges.Ledges.Length);
                    w.Raw(solid.Ledges.Ledges);
                }
            }
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's brush models from its section, bound to <paramref name="bsp"/>;
    /// or null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room, whose sockets a model's socket must be one of.</param>
    /// <param name="bsp">The room's compiled BSP, whose lumps every run must fit.</param>
    /// <returns>The models, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room: cut
    /// short, another model count, a model index or run outside the room's
    /// lumps, a socket the room does not have, a direction or flag out of
    /// range, a solid count other than its record's, a turn count other than
    /// 1 or 4, a convex whose header does not fit, bytes after its end.
    /// </exception>
    internal static RoomBrushModels? Read(ArraySegment<byte>? section, RoomDefinition room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room.Name, SectionTag) is not { } r)
        {
            return null;
        }

        int modelCount = BspStructView.Count<DModel>(bsp[BspLump.Models]);
        int count = r.Int();
        if (count != modelCount - 1)
        {
            throw r.Mismatch($"{count} brush models; the room has {Math.Max(0, modelCount - 1)}");
        }

        int[] limits =
        [
            BspStructView.Count<DNode>(bsp[BspLump.Nodes]),
            BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]),
            BspStructView.Count<DFace>(bsp[BspLump.Faces]),
            BspStructView.Count<ushort>(bsp[BspLump.LeafFaces]),
            BspStructView.Count<DBrush>(bsp[BspLump.Brushes]),
            BspStructView.Count<DEdge>(bsp[BspLump.Edges]),
            BspStructView.Count<DFace>(bsp[BspLump.OriginalFaces]),
            BspStructView.Count<ushort>(bsp[BspLump.VertNormalIndices]),
        ];
        string[] names = ["nodes", "leaves", "faces", "leaf faces", "brushes", "edges", "original faces", "vertex-normal indices"];

        List<RoomBrushModel> models = new(count);
        for (int m = 0; m < count; m++)
        {
            int index = r.Int();
            if (index != m + 1)
            {
                throw r.Mismatch($"model {index} in place of model {m + 1}");
            }

            int id = r.Int();
            string className = r.String();
            bool originRelative = r.Small(1, "a model flags byte of") == 1;
            ImmutableArray<RoomNeed>.Builder needs = ImmutableArray.CreateBuilder<RoomNeed>(r.Count("conditions"));
            for (int n = needs.Capacity; n > 0; n--)
            {
                RoomDirection direction = (RoomDirection)r.Small(7, "a direction");
                int flags = r.Small(3, "condition flags");
                bool joined = (flags & 1) != 0;
                if (joined && !RoomDirections.IsSide(direction))
                {
                    throw r.Mismatch($"a joined condition on the diagonal {RoomDirections.Name(direction)}");
                }

                needs.Add(new RoomNeed(direction, joined, (flags & 2) != 0));
            }

            int socket = r.Int();
            if (socket < -1 || socket >= room.Sockets.Count)
            {
                throw r.Mismatch($"socket {socket} for a brush model; the room has {room.Sockets.Count}");
            }

            int priority = r.Int();
            RoomRange[] runs = new RoomRange[limits.Length];
            for (int k = 0; k < runs.Length; k++)
            {
                int first = r.Int();
                int length = r.Int();
                if (length < 0 || (length > 0 && (first < 0 || (long)first + length > limits[k])))
                {
                    throw r.Mismatch($"a run of {names[k]} {first} + {length}; the room has {limits[k]}");
                }

                runs[k] = new RoomRange(first, length);
            }

            int keyLength = r.Int();
            byte[]? keyData = null;
            if (keyLength != NoRecord)
            {
                if (keyLength < 0)
                {
                    throw r.Mismatch($"key data of {keyLength} bytes");
                }

                keyData = r.Structs<byte>("key data", keyLength, counted: false);
            }

            int solidCount = r.Count("solids");
            bool[] hulls = new bool[solidCount];
            for (int s = 0; s < solidCount; s++)
            {
                hulls[s] = r.Flag();
            }

            if (keyData is null && solidCount != 0)
            {
                throw r.Mismatch($"{solidCount} solids for a model with no collision record");
            }

            models.Add(new RoomBrushModel(
                index, id, className, originRelative, needs.MoveToImmutable(), socket, priority,
                runs[0], runs[1], runs[2], runs[3], runs[4], runs[5], runs[6], runs[7], keyData, [.. hulls]));
        }

        int turnCount = r.Int();
        if (turnCount is not (1 or 4))
        {
            throw r.Mismatch($"{turnCount} turns of brush models; a section holds 1 or 4");
        }

        RoomBrushModelTurn[][] turns = new RoomBrushModelTurn[turnCount][];
        for (int t = 0; t < turnCount; t++)
        {
            turns[t] = new RoomBrushModelTurn[count];
            for (int m = 0; m < count; m++)
            {
                Box bounds = r.Box();
                RoomModelSolid[] solids = new RoomModelSolid[models[m].OuterHulls.Length];
                for (int s = 0; s < solids.Length; s++)
                {
                    Vec3 areas = r.Structs<Vec3>("drag areas", 1, counted: false)[0];
                    int ledges = r.Count("convexes");
                    solids[s] = new RoomModelSolid(areas, r.Ledges(0, ledges));
                }

                turns[t][m] = new RoomBrushModelTurn(bounds, solids);
            }
        }

        r.End();
        return new RoomBrushModels(models, turns, bsp);
    }

    /// <summary>A model's runs in the section's order.</summary>
    private static RoomRange[] Runs(RoomBrushModel model) =>
    [
        model.Nodes, model.Leaves, model.Faces, model.LeafFaces, model.Brushes, model.Edges, model.OrigFaces,
        model.VertNormalIndices,
    ];

    /// <summary>
    /// Whether a compiled brush entity's geometry is in its own frame: the
    /// loader rebuilds an entity's brushes relative to its <c>origin</c>
    /// whenever that is not zero, whether an origin brush wrote it or the
    /// author did.
    /// </summary>
    internal static bool IsOriginRelative(BspEntity entity)
    {
        if (entity.Get("origin") is not { } text)
        {
            return false;
        }

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool any = false;
        foreach (string part in parts.Take(3))
        {
            any |= float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value != 0f;
        }

        return any;
    }

    /// <summary>The nodes and leaves one model's head reaches, as runs; refused unless each is one run.</summary>
    private static (RoomRange Nodes, RoomRange Leaves) TreeRuns(string room, int model, int head, ReadOnlySpan<DNode> nodes)
    {
        HashSet<int> seenNodes = [];
        HashSet<int> seenLeaves = [];
        Stack<int> pending = new([head]);
        while (pending.Count > 0)
        {
            int at = pending.Pop();
            if (at < 0)
            {
                seenLeaves.Add(~at);
                continue;
            }

            if (at >= nodes.Length || !seenNodes.Add(at))
            {
                throw Layout(room, $"model {model}'s tree reaches node {at} twice or outside its {nodes.Length} nodes");
            }

            pending.Push(nodes[at].Children[0]);
            pending.Push(nodes[at].Children[1]);
        }

        return (Run(room, model, "nodes", seenNodes), Run(room, model, "leaves", seenLeaves));
    }

    private static RoomRange Run(string room, int model, string what, HashSet<int> indices)
    {
        if (indices.Count == 0)
        {
            return default;
        }

        int first = indices.Min();
        if (indices.Max() - first + 1 != indices.Count)
        {
            throw Layout(room, $"model {model}'s {what} are not one run");
        }

        return new RoomRange(first, indices.Count);
    }

    /// <summary>The union of per-item runs (each item's first and count), which must itself be one run.</summary>
    private static RoomRange Span(string room, int model, string what, RoomRange items, Func<int, (int First, int Count)> of)
    {
        HashSet<int> indices = [];
        for (int i = items.First; i < items.End; i++)
        {
            (int first, int count) = of(i);
            for (int k = 0; k < count; k++)
            {
                indices.Add(first + k);
            }
        }

        return Run(room, model, what, indices);
    }

    /// <summary>
    /// The edges a model's drawn and original faces run through: one run,
    /// which no face of any other model (or of the world) uses, because vbsp
    /// starts every model's edge sharing afresh.
    /// </summary>
    private static RoomRange EdgeRun(
        string room, int model, RoomRange faces, RoomRange origFaces, ReadOnlySpan<DFace> drawn, ReadOnlySpan<DFace> original, ReadOnlySpan<int> surfEdges)
    {
        HashSet<int> edges = [];
        AddEdges(edges, drawn, faces, surfEdges);
        AddEdges(edges, original, origFaces, surfEdges);
        RoomRange range = Run(room, model, "edges", edges);
        for (int f = 0; f < drawn.Length + original.Length; f++)
        {
            bool isOriginal = f >= drawn.Length;
            int index = isOriginal ? f - drawn.Length : f;
            if ((isOriginal ? origFaces : faces).Contains(index))
            {
                continue;
            }

            DFace face = isOriginal ? original[index] : drawn[index];
            for (int e = 0; e < face.NumEdges; e++)
            {
                if (range.Contains(Math.Abs(surfEdges[face.FirstEdge + e])))
                {
                    throw Layout(room, $"model {model}'s edges are used by a face of another model");
                }
            }
        }

        return range;
    }

    private static void AddEdges(HashSet<int> edges, ReadOnlySpan<DFace> faces, RoomRange run, ReadOnlySpan<int> surfEdges)
    {
        for (int f = run.First; f < run.End; f++)
        {
            for (int e = 0; e < faces[f].NumEdges; e++)
            {
                edges.Add(Math.Abs(surfEdges[faces[f].FirstEdge + e]));
            }
        }
    }

    /// <summary>A model's vertex-normal index run: its faces' vertices, counted in face order from the first face.</summary>
    private static RoomRange VertNormalRun(ReadOnlySpan<DFace> faces, RoomRange run)
    {
        int first = 0;
        for (int f = 0; f < run.First; f++)
        {
            first += faces[f].NumEdges;
        }

        int count = 0;
        for (int f = run.First; f < run.End; f++)
        {
            count += faces[f].NumEdges;
        }

        return new RoomRange(first, count);
    }

    /// <summary>Refuses two models that claim the same entry of a lump: each run belongs to one model.</summary>
    private static void CheckDisjoint(string room, List<RoomBrushModel> models)
    {
        Func<RoomBrushModel, RoomRange>[] runs =
        [
            m => m.Nodes, m => m.Leaves, m => m.Faces, m => m.LeafFaces, m => m.Brushes, m => m.Edges, m => m.OrigFaces, m => m.VertNormalIndices,
        ];
        foreach (Func<RoomBrushModel, RoomRange> run in runs)
        {
            RoomRange[] sorted = [.. models.Select(run).Where(r => r.Count > 0).OrderBy(r => r.First)];
            for (int i = 1; i < sorted.Length; i++)
            {
                if (sorted[i].First < sorted[i - 1].End)
                {
                    throw Layout(room, "has two brush models sharing one run of a lump");
                }
            }
        }
    }

    private static ImmutableArray<RoomNeed> Needs(string room, int id, string className, BspEntity entity)
    {
        if (entity.Get(RoomNeeds.Key) is not { } value)
        {
            return [];
        }

        // The naming rule checked the key on the VMF before the compile
        // (RoomNameAnalysis.CheckVmf), with its own message.
        if (!RoomNeeds.TryParse(value, out List<RoomNeed> parsed, out string? unknown))
        {
            throw new RoomLintException(
                $"room {room}: entity {id} ({className}) room_needs \"{value}\": unknown direction \"{unknown}\";"
                + " use east, west, north, south, a diagonal, or joined_ with a side, optionally negated with !.");
        }

        return [.. parsed];
    }

    private static int Socket(RoomDefinition definition, int id, string className, BspEntity entity)
    {
        if (entity.Get(RoomStaticProps.SocketKey) is not { } name)
        {
            return -1;
        }

        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            if (string.Equals(definition.Sockets[s].Name, name, StringComparison.Ordinal))
            {
                return s;
            }
        }

        throw new RoomLintException(
            $"room {definition.Name}: {className} {id} has {RoomStaticProps.SocketKey} \"{name}\", which is not a socket of the room.");
    }

    private static int Priority(string room, int id, string className, BspEntity entity)
    {
        if (entity.Get(RoomStaticProps.PriorityKey) is not { } text)
        {
            return 0;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority)
            ? priority
            : throw new RoomLintException(
                $"room {room}: {className} {id} has {RoomStaticProps.PriorityKey} \"{text}\", which is not a whole number.");
    }

    private static MapCompileException Layout(string room, string what) =>
        new($"room {room}'s compile {what}; brush models are not laid out as vbsp lays them out.");
}
