//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A room's displacements as the link carries them: every displacement of
/// the room's compile, its start position and its vertices' vectors turned
/// for each quarter turn (the rooms design, 4.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a turn changes.</b> vbsp writes one <c>DispInfo</c> record per
/// displacement, its vertices' vectors and distances into
/// <c>DispVerts</c>, its triangles' tags into <c>DispTris</c>, its luxels'
/// sample positions into <c>DispLightmapSamplePositions</c> (and nothing
/// into <c>DispLightmapAlphas</c>), and, with a cooker, the packed bounding
/// hull of its collision mesh into <c>PhysDisp</c>. Of all that, a
/// placement moves the record's start position (a point: turned, then the
/// cell added) and turns every vertex's vector (a direction). The rest does
/// not change with a turn or a move, and is the room's byte for byte: the
/// distances and alphas are lengths and weights; the triangle tags,
/// neighbour orientations, allowed vertices and sample positions are
/// stated in the displacement's own grid and triangles, which are built
/// from its base face's corners starting at the one nearest the start
/// position, and a turned face has the same corners in the same order; and
/// the collision hull names the displacement's own vertices by index, not
/// by position. The indices are the level's: the vertex, triangle and
/// sample-position runs, the base face and the neighbours are rebased at
/// link (<c>LevelLinker.LinkDisplacements</c>).
/// </para>
/// <para>
/// <b>Per turn.</b> The start positions and vectors are stored for all four
/// quarter turns (the rooms design's pack rule, 1.1: data the link would
/// otherwise turn element by element is turned at pack time), so the link
/// only adds the placement's translation to each start. A quarter turn
/// permutes and negates components, so the turned vectors are exact, and
/// they are the vectors vbsp writes for the flattened level's turned
/// <c>dispinfo</c> (<see cref="VmfPlacement.MoveSolid"/>): each is a
/// normalised sum of the turned normal and offset, and neither the sum nor
/// the length's sum of squares depends on which horizontal component comes
/// first. The section records how many turns it holds, 1 or 4, as every
/// per-turn section of that design does; a pack with one holds turn 0 and
/// the link turns it, to the same bytes (<see cref="Starts"/>,
/// <see cref="Vectors"/>).
/// </para>
/// <para>
/// <b>Required, not only a shortcut.</b> A room is held to the displacement
/// rules of the pack (<see cref="Problem"/>) when <c>ssmap room</c>
/// compiles it, and this section is what says it was. So a room whose lump
/// has displacements and that has none of this bound to its compile (a pack
/// written before displacements were carried) is refused at link, naming
/// the room, as a room with overlays and no overlay data is.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>DISP</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>, big-endian), and a payload
/// that starts with an <c>int32</c> revision (<see cref="Revision"/>).
/// After the revision, big-endian <c>int32</c>s: the displacement count and
/// the vertex count (the room's lumps'), then the turn count, 1 or 4, and
/// per turn every displacement's start position and then every vertex's
/// vector, three little-endian floats each. A section of a revision this
/// build does not read is absent; a section that does not fit the room's
/// lumps is refused as damaged. The tag is one an older build skips, and
/// such a build refuses a room with displacements by its lumps, so the
/// pack's format version is unchanged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lumps: read from a pack or
/// built by the room compile, it remembers the BSP, and the link uses it
/// only for that very BSP (<see cref="IsFor"/>), as the other stored work
/// is used.
/// </para>
/// </remarks>
internal sealed class RoomDisplacements
{
    /// <summary>The tag of a room's displacement section.</summary>
    public const string SectionTag = "DISP";

    /// <summary>The revision this build writes and reads: the link sections' own (<see cref="RoomLinkSections.RevisionFor"/>).</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>
    /// The most displacements a map holds: <c>MAX_MAP_DISPINFO</c>, which the
    /// SDK's vbsp refuses a map past, so a linked level past it would be one
    /// the flattened level's stock compile refuses.
    /// </summary>
    public const int MaxMapDispInfo = Bsp.Write.WriteLimits.MaxMapDispInfo;

    /// <summary>The power vbsp gives up the virtual mesh for (the collision then goes into the world's solids).</summary>
    public const int StaticMeshPower = 4;

    private readonly BspData? _bsp;
    private readonly Vec3[][] _starts;
    private readonly Vec3[][] _vectors;

    private RoomDisplacements(Vec3[][] starts, Vec3[][] vectors, BspData? bsp)
    {
        _starts = starts;
        _vectors = vectors;
        _bsp = bsp;
    }

    /// <summary>How many displacements the room's compile wrote.</summary>
    public int Count => _starts[0].Length;

    /// <summary>How many displacement vertices the room's compile wrote.</summary>
    public int VertexCount => _vectors[0].Length;

    /// <summary>How many turns the data is stored for: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _starts.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>Every displacement's start position at one quarter turn, not yet moved: the stored turn, or turn 0 turned.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public Vec3[] Starts(int rotation) => _starts.Length == 4 ? _starts[rotation] : TurnAll(_starts[0], rotation);

    /// <summary>Every displacement vertex's vector at one quarter turn: the stored turn, or turn 0 turned.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public Vec3[] Vectors(int rotation) => _vectors.Length == 4 ? _vectors[rotation] : TurnAll(_vectors[0], rotation);

    /// <summary>
    /// The same displacements stored with only turn 0 (the link turns it):
    /// what a pack written with a rotation count of 1 reads as, for the facts
    /// that hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomDisplacements WithTurnZeroOnly() => new([_starts[0]], [_vectors[0]], _bsp);

    /// <summary>
    /// A compiled room's displacements for the link, or null when its compile
    /// wrote none: every start position and vector turned four ways.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The displacements, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="LinkException">The lumps are not the ones vbsp writes (<see cref="Records"/>).</exception>
    public static RoomDisplacements? Build(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<DispInfo> infos = Records(room, bsp);
        if (infos.Length == 0)
        {
            return null;
        }

        Vec3[] starts = new Vec3[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            starts[i] = infos[i].StartPosition;
        }

        ReadOnlySpan<DispVert> verts = BspStructView.As<DispVert>(bsp[BspLump.DispVerts]);
        Vec3[] vectors = new Vec3[verts.Length];
        for (int v = 0; v < verts.Length; v++)
        {
            vectors[v] = verts[v].Vector;
        }

        Vec3[][] turnedStarts = new Vec3[4][];
        Vec3[][] turnedVectors = new Vec3[4][];
        for (int turn = 0; turn < 4; turn++)
        {
            turnedStarts[turn] = TurnAll(starts, turn);
            turnedVectors[turn] = TurnAll(vectors, turn);
        }

        return new RoomDisplacements(turnedStarts, turnedVectors, bsp);
    }

    /// <summary>
    /// A room's displacement records, checked against what vbsp writes:
    /// every displacement's vertex and triangle runs follow the one before
    /// and fill their lumps, its base face is a face of the room that names
    /// it back, its sample positions start inside their lump, and a
    /// collision lump, when there is one, holds one entry per displacement.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The records; empty when the room has none.</returns>
    /// <exception cref="LinkException">A lump out of the shape vbsp writes.</exception>
    /// <remarks>
    /// The link rebases every index a record holds by the placement's base in
    /// its lump, so a record that did not name its own run in its own room
    /// would name another placement's in the level; refusing that here keeps
    /// a damaged room from linking into a map the engine misreads.
    /// </remarks>
    internal static ReadOnlySpan<DispInfo> Records(string room, BspData bsp)
    {
        ReadOnlySpan<DispInfo> infos = BspStructView.As<DispInfo>(bsp[BspLump.DispInfo]);
        int vertCount = BspStructView.Count<DispVert>(bsp[BspLump.DispVerts]);
        int triCount = BspStructView.Count<DispTri>(bsp[BspLump.DispTris]);
        if (infos.Length == 0)
        {
            if (vertCount != 0 || triCount != 0)
            {
                throw new LinkException($"room {room} has {vertCount} displacement vertices and {triCount} triangles but no displacement.");
            }

            return infos;
        }

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        int samples = bsp[BspLump.DispLightmapSamplePositions].Length;
        long verts = 0, tris = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            DispInfo info = infos[i];
            if (info.Power is < PowerInfo.MinMapDispPower or > PowerInfo.MaxMapDispPower)
            {
                throw new LinkException($"room {room}'s displacement {i} has power {info.Power}; vbsp writes 2 to 4.");
            }

            if (info.DispVertStart != verts || info.DispTriStart != tris)
            {
                throw new LinkException(
                    $"room {room}'s displacement {i} starts its vertices at {info.DispVertStart} and its triangles at {info.DispTriStart};"
                    + $" vbsp lays them out in order, at {verts} and {tris}.");
            }

            if (info.MapFace >= faces.Length || faces[info.MapFace].DispInfo != i)
            {
                throw new LinkException($"room {room}'s displacement {i} names face {info.MapFace}, which is not its base face.");
            }

            if (info.LightmapSamplePositionStart < 0 || info.LightmapSamplePositionStart > samples)
            {
                throw new LinkException(
                    $"room {room}'s displacement {i} starts its sample positions at {info.LightmapSamplePositionStart}, past the {samples}-byte lump.");
            }

            verts += info.NumVerts();
            tris += info.NumTris();
        }

        if (verts != vertCount || tris != triCount)
        {
            throw new LinkException(
                $"room {room}'s displacements hold {verts} vertices and {tris} triangles; its lumps hold {vertCount} and {triCount}.");
        }

        int collision = PhysDispLump.ReadSizes(bsp[BspLump.PhysDisp].Data.Span).Count;
        if (bsp[BspLump.PhysDisp].Length != 0 && collision != infos.Length)
        {
            throw new LinkException($"room {room} has {infos.Length} displacements and collision for {collision}.");
        }

        return infos;
    }

    /// <summary>
    /// A room's displacement collision blobs, one per displacement in order
    /// (null for one vbsp built none for), or empty when its compile wrote
    /// no collision lump (a room compiled without a cooker).
    /// </summary>
    /// <param name="bsp">The room's compiled BSP, whose lumps <see cref="Records"/> checked.</param>
    /// <returns>The blobs.</returns>
    internal static IReadOnlyList<byte[]?> CollisionBlobs(BspData bsp)
    {
        ReadOnlySpan<byte> lump = bsp[BspLump.PhysDisp].Data.Span;
        IReadOnlyList<int> sizes = PhysDispLump.ReadSizes(lump);
        byte[]?[] blobs = new byte[]?[sizes.Count];
        int at = 2 + (2 * sizes.Count);
        for (int i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] < 0)
            {
                continue;
            }

            blobs[i] = lump.Slice(at, sizes[i]).ToArray();
            at += sizes[i];
        }

        return blobs;
    }

    /// <summary>The pack section holding these displacements.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Count);
        w.Int(VertexCount);
        w.Int(_starts.Length);
        for (int t = 0; t < _starts.Length; t++)
        {
            w.Structs<Vec3>(_starts[t], counted: false);
            w.Structs<Vec3>(_vectors[t], counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's displacements from its section, bound to <paramref name="bsp"/>;
    /// or null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lumps the section must fit.</param>
    /// <returns>The displacements, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's lumps:
    /// cut short, another displacement or vertex count, a turn count other
    /// than 1 or 4, bytes after its end.
    /// </exception>
    internal static RoomDisplacements? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int lump = BspStructView.Count<DispInfo>(bsp[BspLump.DispInfo]);
        int count = r.Int();
        if (count != lump || count == 0)
        {
            throw r.Mismatch($"{count} displacements; the room has {lump}");
        }

        int vertLump = BspStructView.Count<DispVert>(bsp[BspLump.DispVerts]);
        int verts = r.Int();
        if (verts != vertLump)
        {
            throw r.Mismatch($"{verts} displacement vertices; the room has {vertLump}");
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of displacements; a section holds 1 or 4");
        }

        Vec3[][] starts = new Vec3[turns][];
        Vec3[][] vectors = new Vec3[turns][];
        for (int t = 0; t < turns; t++)
        {
            starts[t] = r.Structs<Vec3>("displacements", count, counted: false);
            vectors[t] = r.Structs<Vec3>("displacement vertices", verts, counted: false);
        }

        r.End();
        return new RoomDisplacements(starts, vectors, bsp);
    }

    /// <summary>
    /// What is wrong with a room's displacements for the link, as the rooms
    /// design's refusals say it, or null: a displacement on a side of a
    /// socket's plug, one of power 4, one whose surface leaves the cell, or
    /// one with an edge on a socket's plug box (15.4).
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="room">The room's VMF, room-local.</param>
    /// <returns>The refusal's text, or null.</returns>
    /// <exception cref="RoomLibraryException">A displacement's brush or keys cannot be read.</exception>
    /// <remarks>
    /// <para>
    /// <b>The surface.</b> Each rule reads the displaced surface, not the
    /// brush: a displacement's vertices stand off its base face by their
    /// normals, distances and offsets, which the brush's box does not show.
    /// The surface is built as vbsp builds it (the base face's corners, the
    /// start corner nearest <c>startposition</c>, <see cref="DisplacementLumpBuilder.DispMapToCoreDispInfo"/>),
    /// the base face cut from the side's plane by the brush's other planes,
    /// in doubles.
    /// </para>
    /// <para>
    /// <b>On a plug.</b> A socket's plug is a wall only where the socket is
    /// capped: a joint strips it (its faces drawn nodraw) and the flatten
    /// leaves it out, so a displacement on one of its sides would stand on a
    /// face the level does not draw in one map and be gone in the other. A
    /// plug is the brush whose box is its socket's plug box, the rule the
    /// flatten leaves joined plugs out by (<see cref="RoomLibraryVmf.Same"/>).
    /// </para>
    /// <para>
    /// <b>Power 4.</b> vbsp gives up the virtual mesh for every displacement
    /// of a map that holds one of power 4 and puts their collision among the
    /// world's solids as triangle soup, which the link does not merge (it
    /// carries the virtual mesh's hulls, <c>PhysDisp</c>); and one such room
    /// would move every displacement of the flattened level to that path.
    /// So a room refuses power 4, and powers 2 and 3 are carried.
    /// </para>
    /// <para>
    /// <b>The cell.</b> A room's geometry stays in its box, the cell in x and
    /// y and the room's height in z (the model lint holds every brush to it);
    /// a displaced surface can stand off its brush, so it is held to the box
    /// by its vertices, within
    /// <see cref="RoomLinter.CellEpsilon"/>, the way a static prop's hull is
    /// (O6), and refused naming how far past it reaches.
    /// </para>
    /// <para>
    /// <b>The plug (O8).</b> vbsp stitches displacements whose edges meet:
    /// it records them as neighbours, smooths their normals across the
    /// shared edge and relights along it. A displacement that reaches into a
    /// doorway meets the neighbour's across the joint in the flattened level,
    /// which the link, carrying each room's compile, cannot reproduce. So a
    /// boundary edge of the surface may touch a socket's plug box on its inner
    /// face (the floor meeting the doorway, 2 wall depths from the
    /// neighbour's), but not reach into it past that face by more than the
    /// tolerance while inside the opening's span; the doorway's floor stays a
    /// brush.
    /// </para>
    /// <para>
    /// The split refuses a library with such a displacement, so the pack and
    /// the flatten both do, and a room compile refuses one too.
    /// </para>
    /// </remarks>
    public static string? Problem(RoomDefinition definition, VmfDocument room)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(room);
        foreach ((VmfChunk solid, VmfChunk side, VmfChunk dispinfo) in DisplacementSides(room))
        {
            string sideId = side.GetValue("id") ?? "?";
            Box brush = VmfPlacement.Bounds(solid);
            foreach (RoomSocket socket in definition.Sockets)
            {
                if (RoomLibraryVmf.Same(brush, RoomLinter.SealBox(definition, socket, definition.CellSize)))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"room {definition.Name}: the displacement on brush side {sideId} is on socket \"{socket.Name}\"'s plug, which a joint removes.");
                }
            }

            MapDisplacement disp;
            try
            {
                disp = VmfDisplacementReader.Read(dispinfo);
            }
            catch (MapCompileException exception)
            {
                throw new RoomLibraryException($"brush {VmfPlacement.IdOf(solid)}: {exception.Message}");
            }

            if (disp.Power >= StaticMeshPower)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"room {definition.Name}: the displacement on brush side {sideId} is power {disp.Power}; the link carries displacement collision only as the virtual mesh vbsp builds for powers 2 and 3.");
            }

            if (Surface(solid, side, disp) is not { } core)
            {
                // A side that is not a four-cornered face: vbsp refuses it
                // with its own message when the room compiles.
                continue;
            }

            // The room's box (17.6): the cell in x and y, the room's height in z.
            float cell = definition.CellSize;
            float top = definition.Height;
            float reach = 0;
            foreach (Vec3 v in core.Verts)
            {
                reach = Math.Max(reach, Math.Max(
                    Math.Max(Math.Max(-v.X, v.X - cell), Math.Max(-v.Y, v.Y - cell)),
                    Math.Max(-v.Z, v.Z - top)));
            }

            if (reach > RoomLinter.CellEpsilon)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"room {definition.Name}: the displacement on brush side {sideId} reaches {reach:0.00} units outside the cell; displacements stay in their cell.");
            }

            foreach (RoomSocket socket in definition.Sockets)
            {
                Box doorway = Doorway(definition, socket);
                if (Boundary(core).Any(edge => SegmentTouches(edge.A, edge.B, doorway)))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"room {definition.Name}: the displacement on brush side {sideId} has an edge on socket \"{socket.Name}\"'s plug box; displacements may not meet at a joint.");
                }
            }
        }

        return null;
    }

    /// <summary>Every displacement side of a room's world and entity brushes, with its brush and chunk, in file order.</summary>
    private static IEnumerable<(VmfChunk Solid, VmfChunk Side, VmfChunk DispInfo)> DisplacementSides(VmfDocument room)
    {
        IEnumerable<VmfChunk> owners = room.GetChunks(MapFileLoader.WorldChunk).Concat(room.GetChunks(MapFileLoader.EntityChunk));
        foreach (VmfChunk owner in owners)
        {
            foreach (VmfChunk solid in owner.GetChunks(MapFileLoader.SolidChunk))
            {
                foreach (VmfChunk side in solid.GetChunks(MapFileLoader.SideChunk))
                {
                    if (side.GetChunk(VmfPlacement.DispInfoChunk) is { } dispinfo)
                    {
                        yield return (solid, side, dispinfo);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The part of a socket's plug box a displacement edge may not enter:
    /// the box widened by the tolerance, less the tolerance behind its inner
    /// face (the face toward the room), so an edge on that face, where a
    /// floor meets the doorway, is clear.
    /// </summary>
    internal static Box Doorway(RoomDefinition definition, RoomSocket socket)
    {
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        float e = RoomLinter.CellEpsilon;
        Vec3 lo = plug.Mins - new Vec3(e, e, e);
        Vec3 hi = plug.Maxs + new Vec3(e, e, e);
        return socket.Facing switch
        {
            RoomFacing.PositiveX => new Box(new Vec3(plug.Mins.X + e, lo.Y, lo.Z), hi),
            RoomFacing.NegativeX => new Box(lo, new Vec3(plug.Maxs.X - e, hi.Y, hi.Z)),
            RoomFacing.PositiveY => new Box(new Vec3(lo.X, plug.Mins.Y + e, lo.Z), hi),
            RoomFacing.NegativeY => new Box(lo, new Vec3(hi.X, plug.Maxs.Y - e, hi.Z)),
            _ => throw new ArgumentOutOfRangeException(nameof(socket), socket.Facing, "Unknown facing."),
        };
    }

    /// <summary>Whether a segment meets a closed box (the slab test, in doubles).</summary>
    internal static bool SegmentTouches(Vec3 a, Vec3 b, Box box)
    {
        double t0 = 0, t1 = 1;
        for (int axis = 0; axis < 3; axis++)
        {
            double p = Component(a, axis), d = Component(b, axis) - p;
            double lo = Component(box.Mins, axis), hi = Component(box.Maxs, axis);
            if (d == 0)
            {
                if (p < lo || p > hi)
                {
                    return false;
                }

                continue;
            }

            double u = (lo - p) / d, v = (hi - p) / d;
            t0 = Math.Max(t0, Math.Min(u, v));
            t1 = Math.Min(t1, Math.Max(u, v));
            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    private static double Component(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>The displaced surface's boundary edges, vertex to vertex along its four sides.</summary>
    private static IEnumerable<(Vec3 A, Vec3 B)> Boundary(CoreDispInfo core)
    {
        int n = core.PostSpacing;
        for (int k = 0; k + 1 < n; k++)
        {
            yield return (core.Vert(k), core.Vert(k + 1));
            yield return (core.Vert(((n - 1) * n) + k), core.Vert(((n - 1) * n) + k + 1));
            yield return (core.Vert(k * n), core.Vert((k + 1) * n));
            yield return (core.Vert((k * n) + n - 1), core.Vert(((k + 1) * n) + n - 1));
        }
    }

    /// <summary>
    /// A displacement's surface as vbsp builds it, from its side's face; or
    /// null when the side is not a face of four corners.
    /// </summary>
    private static CoreDispInfo? Surface(VmfChunk solid, VmfChunk side, MapDisplacement disp)
    {
        if (BaseFace(solid, side) is not { Length: 4 } winding)
        {
            return null;
        }

        // The lightmap axes shape only the luxel layout, which no rule here
        // reads: any pair along the quad's edges will do.
        Vec3 u = winding[1] - winding[0], v = winding[3] - winding[0];
        DisplacementFace face = new(
            0,
            winding,
            (int)BrushContents.Solid,
            u * (1f / (16f * u.Length())),
            v * (1f / (16f * v.Length())),
            new float[8]);
        CoreDispInfo core = new(disp.Power);
        DisplacementLumpBuilder.DispMapToCoreDispInfo(disp, face, core, stockNormalise: false, withFace: false);
        return core;
    }

    /// <summary>
    /// A side's face on its brush: a large square on the side's plane cut by
    /// every other side's plane, in doubles, as a brush's faces are made;
    /// corners closer than a hundredth of a unit merged.
    /// </summary>
    internal static Vec3[]? BaseFace(VmfChunk solid, VmfChunk side)
    {
        List<(double[] N, double D)> planes = [];
        (double[] N, double D)? own = null;
        foreach (VmfChunk s in solid.GetChunks(MapFileLoader.SideChunk))
        {
            (double[] N, double D)? plane = PlaneOf(s, solid);
            if (plane is null)
            {
                return null;
            }

            if (ReferenceEquals(s, side))
            {
                own = plane;
            }
            else
            {
                planes.Add(plane.Value);
            }
        }

        if (own is not { } p)
        {
            return null;
        }

        List<double[]> polygon = Square(p.N, p.D);
        foreach ((double[] n, double d) in planes)
        {
            polygon = Clip(polygon, n, d);
            if (polygon.Count == 0)
            {
                return null;
            }
        }

        List<Vec3> corners = [];
        foreach (double[] c in polygon)
        {
            Vec3 point = new((float)c[0], (float)c[1], (float)c[2]);
            if (corners.Count == 0 || !Near(corners[^1], point))
            {
                corners.Add(point);
            }
        }

        if (corners.Count > 1 && Near(corners[0], corners[^1]))
        {
            corners.RemoveAt(corners.Count - 1);
        }

        return [.. corners];

        static bool Near(Vec3 a, Vec3 b) => (a - b).Length() < 0.01f;
    }

    /// <summary>
    /// A side's plane through its three points, as vbsp makes it: the normal
    /// <c>(p0 − p1) × (p2 − p1)</c>, normalised, points out of the brush.
    /// </summary>
    private static (double[] N, double D)? PlaneOf(VmfChunk side, VmfChunk solid)
    {
        string? text = side.GetValue("plane");
        if (text is null)
        {
            throw new RoomLibraryException($"brush {VmfPlacement.IdOf(solid)} has a side with no plane.");
        }

        string[] parts = text.Split(')', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new RoomLibraryException($"brush {VmfPlacement.IdOf(solid)} has a plane \"{text}\", not three points.");
        }

        double[][] points = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            string[] numbers = parts[i].TrimStart('(').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 3
                || !double.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            {
                throw new RoomLibraryException($"brush {VmfPlacement.IdOf(solid)} has a plane \"{text}\", not three points.");
            }

            points[i] = [x, y, z];
        }

        double[] a = Sub(points[0], points[1]), b = Sub(points[2], points[1]);
        double[] n = [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
        double length = Math.Sqrt((n[0] * n[0]) + (n[1] * n[1]) + (n[2] * n[2]));
        if (length == 0)
        {
            return null;
        }

        n = [n[0] / length, n[1] / length, n[2] / length];
        return (n, Dot(n, points[0]));
    }

    /// <summary>A square on a plane, far larger than any cell, wound so its normal is the plane's.</summary>
    private static List<double[]> Square(double[] n, double d)
    {
        const double Size = 1 << 20;
        double[] up = Math.Abs(n[2]) > 0.7 ? [1, 0, 0] : [0, 0, 1];
        double[] right = Cross(up, n);
        double rl = Math.Sqrt(Dot(right, right));
        right = [right[0] / rl, right[1] / rl, right[2] / rl];
        up = Cross(n, right);
        double[] o = [n[0] * d, n[1] * d, n[2] * d];
        return
        [
            Add(o, Scale(right, -Size), Scale(up, Size)),
            Add(o, Scale(right, Size), Scale(up, Size)),
            Add(o, Scale(right, Size), Scale(up, -Size)),
            Add(o, Scale(right, -Size), Scale(up, -Size)),
        ];
    }

    /// <summary>A polygon clipped to the back of a plane (Sutherland-Hodgman).</summary>
    private static List<double[]> Clip(List<double[]> polygon, double[] n, double d)
    {
        const double Epsilon = 1e-6;
        List<double[]> output = [];
        for (int i = 0; i < polygon.Count; i++)
        {
            double[] p = polygon[i];
            double[] q = polygon[(i + 1) % polygon.Count];
            double sp = Dot(n, p) - d, sq = Dot(n, q) - d;
            if (sp <= Epsilon)
            {
                output.Add(p);
            }

            if ((sp > Epsilon && sq < -Epsilon) || (sp < -Epsilon && sq > Epsilon))
            {
                double t = sp / (sp - sq);
                output.Add([p[0] + (t * (q[0] - p[0])), p[1] + (t * (q[1] - p[1])), p[2] + (t * (q[2] - p[2]))]);
            }
        }

        return output;
    }

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Add(double[] a, double[] b, double[] c) => [a[0] + b[0] + c[0], a[1] + b[1] + c[1], a[2] + b[2] + c[2]];

    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static Vec3[] TurnAll(Vec3[] values, int rotation)
    {
        if (rotation == 0)
        {
            return values;
        }

        Vec3[] turned = new Vec3[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            turned[i] = RoomTransform.Rotate(values[i], rotation);
        }

        return turned;
    }
}
