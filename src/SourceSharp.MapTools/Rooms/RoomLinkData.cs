//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Everything the link does to one room that does not depend on the level:
/// what the room compile works out once so that <c>ssmap link</c> only adds
/// what the level decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A room is compiled once and linked into many levels, so any
/// work the link does that is a function of the room alone, or of the room
/// and one of its four quarter turns, is work repeated per level (and per
/// placement) for no reason. The owner's rule for rooms is to do as much as
/// possible at room compile time and keep the link to what the level
/// decides; slower room compiles and bigger packs are the accepted price.
/// </para>
/// <para>
/// <b>What is in it.</b> Two parts, matching the two kinds of work:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Shared"/>, which depends on the room alone: the verdict that
/// the room's compile is one the relocation carries (the checks
/// <c>PlanRoom</c> and the pak check make), and the plug census of every
/// socket (the clusters facing it, the plug brushes, the solid leaves to
/// carve, the plug's drawn faces). The level only picks which sockets are
/// jointed.
/// </description></item>
/// <item><description>
/// One <see cref="RoomLinkRotation"/> per quarter turn: the room's geometry
/// turned (vertices, plane normals and which flip pairs swap, texture axes,
/// node, leaf, occluder, model and plug boxes, vertex normals, primitive
/// vertices), its world collision's convexes read out and turned, and its
/// entities parsed and turned. The level only adds the cell: the plane
/// distances and texture offsets take the translation, the points and boxes
/// are moved by <see cref="RoomTransform.Translate"/>. Each of the three
/// parts is its own pack section and may be absent (the entities are not
/// stored by default, <see cref="RoomLinkSections.StoredParts"/> says why),
/// in which case the link works it out per placement.
/// </description></item>
/// </list>
/// <para>
/// <b>Exactness.</b> A turn is a negation and a permutation, which round
/// nothing, and the link then makes the very additions it made before, in
/// the same order (<see cref="RoomTransform.Translate"/> says why that is
/// exact, <see cref="RoomTransform.TranslateBox"/> why it holds for boxes).
/// So a level links to the same bytes whether this was computed at room
/// compile time, read from a pack, or computed on the fly for a room whose
/// pack has none; the facts check all three.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile: the <see cref="Bsp"/>,
/// <see cref="Vis"/> and <see cref="Definition"/> it was computed from. A
/// room object copied with any of them replaced (<c>room with { Bsp = ... }</c>)
/// carries a <see cref="RoomObject.Link"/> that no longer describes it, and
/// <see cref="IsFor"/> is how the linker notices and computes afresh.
/// </para>
/// </remarks>
internal sealed class RoomLinkData
{
    private readonly RoomLinkRotation?[] _rotations;

    /// <summary>Creates the link data of one compile.</summary>
    /// <param name="definition">The room's definition, as compiled.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="vis">The room's own visibility.</param>
    /// <param name="shared">The rotation-free part.</param>
    /// <param name="rotations">Four slots, by quarter turn; a slot may be null when that turn was not read.</param>
    /// <param name="doors">The room's door visibility, or null when it was not read (the link works it out).</param>
    public RoomLinkData(
        RoomDefinition definition, BspData bsp, VisResult vis, RoomLinkShared shared, RoomLinkRotation?[] rotations, RoomDoorVisibility? doors = null)
    {
        if (rotations.Length != 4)
        {
            throw new ArgumentException("link data holds four rotation slots", nameof(rotations));
        }

        Definition = definition;
        Bsp = bsp;
        Vis = vis;
        Shared = shared;
        _rotations = rotations;
        Doors = doors;
    }

    /// <summary>The definition it was computed from.</summary>
    public RoomDefinition Definition { get; }

    /// <summary>The BSP it was computed from.</summary>
    public BspData Bsp { get; }

    /// <summary>The visibility it was computed from.</summary>
    public VisResult Vis { get; }

    /// <summary>The part that depends on the room alone.</summary>
    public RoomLinkShared Shared { get; }

    /// <summary>
    /// The room's door visibility (<see cref="RoomDoorVisibility"/>), which
    /// depends on the room alone; null when not held, and the link then
    /// works it out from the room's own vvis and census.
    /// </summary>
    public RoomDoorVisibility? Doors { get; }

    /// <summary>The part for one quarter turn, or null when it is not held.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public RoomLinkRotation? Rotation(int rotation) => _rotations[rotation];

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same objects, not equal ones).</summary>
    public bool IsFor(RoomObject room) =>
        ReferenceEquals(Bsp, room.Bsp) && ReferenceEquals(Vis, room.Vis) && ReferenceEquals(Definition, room.Definition);
}

/// <summary>The link data that depends on the room alone.</summary>
/// <param name="Sockets">The plug census of every socket, in the definition's socket order.</param>
/// <remarks>
/// Its existence is also a verdict: it is only made for a room whose compile
/// passed every check the link makes of one room (the lumps it carries, one
/// world model, no water, no area portal, empty game lumps, no displacement
/// collision, its vis numbering its leaves, an empty pak).
/// </remarks>
internal sealed record RoomLinkShared(IReadOnlyList<SocketCensus> Sockets);

/// <summary>
/// What stripping one socket's plug touches, room-local, as the link's plug
/// census finds it; computed for every socket because which ones a level
/// joints is the level's choice.
/// </summary>
/// <param name="Facing">The open clusters whose leaves face the plug box, sorted and distinct; empty when none does (a joint there is refused).</param>
/// <param name="StrippedBrushes">The plug brushes, ascending.</param>
/// <param name="CarveLeaves">The solid leaves the plug box reaches into, ascending.</param>
/// <param name="StrippedFaces">The plug's drawn faces, ascending.</param>
internal sealed record SocketCensus(int[] Facing, int[] StrippedBrushes, int[] CarveLeaves, int[] StrippedFaces);

/// <summary>The link data for one quarter turn of a room: three parts, each held or not.</summary>
/// <param name="Geometry">The turned geometry, or null when not held.</param>
/// <param name="Collision">
/// The turned world collision, or null when not held; a room without a
/// collision lump never holds it (computing "none" costs nothing).
/// </param>
/// <param name="Entities">The parsed, turned entities, or null when not held.</param>
/// <remarks>
/// A part not held is computed at link, per placement, as the link always
/// did; each part is its own pack section so a pack can carry any of them.
/// </remarks>
internal sealed record RoomLinkRotation(RoomLinkGeometry? Geometry, RoomLinkCollision? Collision, RoomLinkEntities? Entities);

/// <summary>A room's geometry turned by one quarter turn and not yet moved to a cell.</summary>
/// <remarks>
/// Every array is indexed as the room's own lump is. The link finishes each
/// one by adding the placement's translation, and nothing else.
/// </remarks>
internal sealed class RoomLinkGeometry
{
    /// <summary>The quarter turns, 0 to 3.</summary>
    public required int Rotation { get; init; }

    /// <summary>The vertices, turned.</summary>
    public required Vec3[] Vertices { get; init; }

    /// <summary>
    /// One entry per flip pair of the plane lump: the pair's even plane with
    /// its normal turned and its type re-derived from it, and its distance
    /// still the room-local one (the translation's share is added at link).
    /// </summary>
    public required DPlane[] PlanePairs { get; init; }

    /// <summary>Per flip pair, whether the turn brought its negative half to the front (see <c>TransformPlanes</c>).</summary>
    public required bool[] PlaneSwapped { get; init; }

    /// <summary>The texinfos with their texture and lightmap axes turned and their offsets still room-local.</summary>
    public required TexInfo[] TexInfos { get; init; }

    /// <summary>The nodes' bounds, turned.</summary>
    public required Box[] NodeBoxes { get; init; }

    /// <summary>The leaves' bounds, turned.</summary>
    public required Box[] LeafBoxes { get; init; }

    /// <summary>The phong vertex normals, turned; a normal takes no translation, so these are final.</summary>
    public required Vec3[] VertNormals { get; init; }

    /// <summary>The primitive vertices, turned.</summary>
    public required Vec3[] PrimVerts { get; init; }

    /// <summary>The occluders' bounds, turned, in the occlusion lump's order.</summary>
    public required Box[] OccluderBoxes { get; init; }

    /// <summary>The world model's bounds, turned.</summary>
    public required Box ModelBox { get; init; }

    /// <summary>Every socket's plug box, turned, in the definition's socket order.</summary>
    public required Box[] PlugBoxes { get; init; }
}

/// <summary>A room's world collision, read out of its collision lump, with every convex turned.</summary>
/// <param name="Materials">The collision material table's names, slot 1 first.</param>
/// <param name="VirtualTerrain">Whether the keydata announced virtual terrain.</param>
/// <param name="Solids">Each static solid, in solid order: its contents class and its leaf convexes (IVP compact ledges), turned.</param>
/// <remarks>
/// The ledges keep their room-local brush index as client data and their
/// room-local material slots: which brushes a level strips and where the
/// link's material table puts a name are the level's.
/// </remarks>
internal sealed record RoomLinkCollision(IReadOnlyList<string> Materials, bool VirtualTerrain, IReadOnlyList<RoomLinkSolid> Solids);

/// <summary>One static solid of a room's world collision.</summary>
/// <param name="Contents">The contents class the solid was collected for.</param>
/// <param name="Ledges">
/// Its leaf ledges, left first, each turned, back to back: one array rather
/// than one per ledge, because a pack loads it with one copy and the link
/// copies each ledge out of it anyway (it moves them).
/// </param>
/// <param name="Starts">Where each ledge starts in <paramref name="Ledges"/>; a ledge's own header gives its length.</param>
internal sealed record RoomLinkSolid(int Contents, byte[] Ledges, int[] Starts)
{
    /// <summary>Joins turned ledges into one solid.</summary>
    public static RoomLinkSolid Of(int contents, IReadOnlyList<byte[]> ledges)
    {
        int[] starts = new int[ledges.Count];
        byte[] bytes = new byte[ledges.Sum(l => l.Length)];
        int at = 0;
        for (int l = 0; l < ledges.Count; l++)
        {
            starts[l] = at;
            ledges[l].CopyTo(bytes, at);
            at += ledges[l].Length;
        }

        return new RoomLinkSolid(contents, bytes, starts);
    }

    /// <summary>One ledge's bytes.</summary>
    public ReadOnlySpan<byte> Ledge(int index)
    {
        int end = index + 1 < Starts.Length ? Starts[index + 1] : Ledges.Length;
        return Ledges.AsSpan(Starts[index], end - Starts[index]);
    }
}

/// <summary>A room's entity lump, parsed, with every entity turned for one quarter turn.</summary>
/// <param name="Items">The entities in lump order.</param>
internal sealed record RoomLinkEntities(IReadOnlyList<RoomLinkEntity> Items);

/// <summary>One entity of a room, turned; the link moves its origin and nothing else.</summary>
/// <param name="IsWorld">Whether it is a worldspawn, which the link merges rather than moves.</param>
/// <param name="Pairs">
/// Its keys in order. A key whose value is a position (the <c>origin</c>)
/// carries the turned point in <see cref="RoomLinkPair.Origin"/> and no
/// value; every other key carries its final value (yaws already turned).
/// A worldspawn's pairs are its own, as written.
/// </param>
/// <param name="Extent">A worldspawn's <c>world_mins</c>/<c>world_maxs</c> box, turned, when it has both.</param>
/// <param name="Error">
/// Why the entity cannot be moved (a key that should hold numbers does not),
/// or null. Held rather than thrown so the link reports it at the same point
/// in its walk over the entities as it always has; a room with any error is
/// never stored, only computed on the fly.
/// </param>
internal sealed record RoomLinkEntity(bool IsWorld, IReadOnlyList<RoomLinkPair> Pairs, Box? Extent, string? Error);

/// <summary>One key of a turned entity.</summary>
/// <param name="Key">The key as written.</param>
/// <param name="Value">The final value, or null for a position key.</param>
/// <param name="Origin">The turned position, for a position key.</param>
/// <param name="Component">
/// -1 for a key whose value is the whole position (<c>origin</c>, written as
/// three numbers); 0, 1 or 2 for a key that holds one component of it, with
/// two decimals: an <c>info_ladder</c>'s <c>mins.x</c> ... <c>maxs.z</c>,
/// whose <see cref="Origin"/> is the turned bound corner the component is
/// read from once the link has moved it to the cell.
/// </param>
internal readonly record struct RoomLinkPair(string Key, string? Value, Vec3 Origin, int Component = -1);
