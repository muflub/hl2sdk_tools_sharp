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

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A room's areas and area portals as the link carries them: the room's
/// own <c>Areas</c>, <c>AreaPortals</c> and <c>ClipPortalVerts</c> lumps,
/// checked, the clip vertices turned for each quarter turn, and how many
/// portal numbers the room's compile gave out (the rooms design, 4.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>What vbsp writes.</b> A <c>func_areaportal</c> (or
/// <c>func_areaportalwindow</c>) becomes a world brush of
/// <c>CONTENTS_AREAPORTAL</c> and keeps its entity, which gains a
/// <c>portalnumber</c> key: 1, 2, ... in the order the loader meets the
/// portal entities. The area flood numbers the regions the portals bound
/// from 1 (area 0 is reserved), every leaf and node records its area, and
/// each portal is listed twice in the <c>AreaPortals</c> lump, once from
/// each side's area: its <c>portalnumber</c> as the key, the other side's
/// area, the plane it lies in (oriented into the listing area) and a run of
/// <c>ClipPortalVerts</c>, the outline of the doorway the portal fills. The
/// <c>Areas</c> lump gives each area its run of listings, in area order.
/// </para>
/// <para>
/// <b>What a turn changes.</b> The areas, the listings and their keys do
/// not change with a placement; the plane is the room's, which the link
/// turns and moves with every other plane (<c>PlaneRef</c>); only the clip
/// vertices are points the link would turn one by one, so they are stored
/// turned for all four quarter turns (the pack's rule, 1.1), and the link
/// only adds the placement's translation, the float additions every moved
/// point takes (<see cref="RoomTransform.Translate"/>). The section records
/// how many turns it holds, 1 or 4, as every per-turn section does; a pack
/// with one holds turn 0 and the link turns it, to the same bytes
/// (<see cref="ClipVerts"/>).
/// </para>
/// <para>
/// <b>Required, not only a shortcut.</b> A room is held to the area portal
/// rules of the pack (<see cref="Problem"/>: no portal in a socket's plug
/// box, no <c>room_socket</c> on a portal; and no <c>room_needs</c> on one,
/// which the name analysis refuses) when <c>ssmap room</c> compiles it,
/// and this section is what says it was. So a room whose lumps have area
/// portals and that has none of this bound to its compile (a pack written
/// before area portals were carried) is refused at link, naming the room,
/// as a room with overlays and no overlay data is.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>APRT</c>) follows the
/// link sections' framing (<see cref="RoomLinkSections"/>): a codec byte,
/// the payload's decoded length (<c>int64</c>, big-endian), and a payload
/// that starts with an <c>int32</c> revision (<see cref="Revision"/>).
/// After the revision, big-endian <c>int32</c>s: the room's area count (its
/// <c>Areas</c> lump, area 0 included), its area portal listing count (the
/// <c>AreaPortals</c> lump, the reserved listing 0 included), its clip
/// vertex count, the portal numbers its compile gave out, then the turn
/// count, 1 or 4, and per turn every clip vertex as three little-endian
/// floats. A section of a revision this build does not read is absent; a
/// section that does not fit the room's lumps is refused as damaged. The tag
/// is one an older build skips, and such a build refuses a room with area
/// portals by its lumps, so the pack's format version is unchanged.
/// </para>
/// <para>
/// <b>Binding.</b> It describes one compile's lumps: read from a pack or
/// built by the room compile, it remembers the BSP, and the link uses it
/// only for that very BSP (<see cref="IsFor"/>), as the other stored work
/// is used.
/// </para>
/// </remarks>
internal sealed class RoomAreaPortals
{
    /// <summary>The tag of a room's area portal section.</summary>
    public const string SectionTag = "APRT";

    /// <summary>The revision this build writes and reads: the link sections' own (<see cref="RoomLinkSections.RevisionFor"/>).</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>The key vbsp gives an area portal's entity: its portal's number, which the portal's listings carry as their key.</summary>
    public const string PortalNumberKey = "portalnumber";

    private readonly BspData? _bsp;
    private readonly Vec3[][] _clipVerts;

    private RoomAreaPortals(int areaCount, int listingCount, int portalNumbers, Vec3[][] clipVerts, BspData? bsp)
    {
        AreaCount = areaCount;
        ListingCount = listingCount;
        PortalNumbers = portalNumbers;
        _clipVerts = clipVerts;
        _bsp = bsp;
    }

    /// <summary>How many entries the room's <c>Areas</c> lump holds, the reserved area 0 included.</summary>
    public int AreaCount { get; }

    /// <summary>How many entries the room's <c>AreaPortals</c> lump holds, the reserved listing 0 included.</summary>
    public int ListingCount { get; }

    /// <summary>
    /// How many portal numbers the room's compile gave out: its area portal
    /// entities, whose <c>portalnumber</c>s are 1 to this. The link numbers
    /// a placement's portals after every earlier placement's.
    /// </summary>
    public int PortalNumbers { get; }

    /// <summary>How many clip vertices the room's compile wrote.</summary>
    public int ClipVertCount => _clipVerts[0].Length;

    /// <summary>How many turns the clip vertices are stored for: 4 when built, 1 or 4 when read.</summary>
    public int TurnCount => _clipVerts.Length;

    /// <summary>Whether this describes exactly <paramref name="room"/>'s compile (the same BSP object).</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The clip vertices at one quarter turn: the stored turn, or turn 0 turned when only it is stored.</summary>
    /// <param name="rotation">The quarter turns, 0 to 3.</param>
    public Vec3[] ClipVerts(int rotation)
    {
        if (_clipVerts.Length == 4)
        {
            return _clipVerts[rotation];
        }

        Vec3[] turned = new Vec3[ClipVertCount];
        for (int i = 0; i < turned.Length; i++)
        {
            turned[i] = RoomTransform.Rotate(_clipVerts[0][i], rotation);
        }

        return turned;
    }

    /// <summary>
    /// The same data stored with only turn 0 (the link turns it): what a
    /// pack written with a rotation count of 1 reads as, for the facts that
    /// hold the two storages to the same linked bytes.
    /// </summary>
    internal RoomAreaPortals WithTurnZeroOnly() => new(AreaCount, ListingCount, PortalNumbers, [_clipVerts[0]], _bsp);

    /// <summary>
    /// Whether a compiled room has anything of area portals: more than the
    /// one area and the reserved listing a map without portals has, or an
    /// entity carrying a portal number (a portal whose two sides the room's
    /// flood found to be one area has no listing, but its number still
    /// counts).
    /// </summary>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <param name="portalNumbers">The portal numbers its entities carry (<see cref="PortalNumbersOf"/>).</param>
    public static bool Has(BspData bsp, int portalNumbers) =>
        BspStructView.Count<DArea>(bsp[BspLump.Areas]) > 2
        || BspStructView.Count<DAreaPortal>(bsp[BspLump.AreaPortals]) > 1
        || portalNumbers > 0;

    /// <summary>
    /// A compiled room's area portals for the link, or null when its compile
    /// has none (<see cref="Has"/>): the lumps checked, every clip vertex
    /// turned four ways.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The data, bound to <paramref name="bsp"/>; or null.</returns>
    /// <exception cref="LinkException">The lumps are not the ones vbsp writes (<see cref="Lumps"/>, <see cref="PortalNumbersOf"/>).</exception>
    public static RoomAreaPortals? Build(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bsp);
        int numbers = PortalNumbersOf(room, EntityLump.Parse(bsp[BspLump.Entities]));
        if (!Has(bsp, numbers))
        {
            return null;
        }

        RoomAreaLumps lumps = Lumps(room, bsp);
        Vec3[][] turns = new Vec3[4][];
        for (int turn = 0; turn < 4; turn++)
        {
            turns[turn] = new Vec3[lumps.ClipVerts.Length];
            for (int v = 0; v < lumps.ClipVerts.Length; v++)
            {
                turns[turn][v] = RoomTransform.Rotate(lumps.ClipVerts[v], turn);
            }
        }

        return new RoomAreaPortals(lumps.Areas.Length, lumps.Portals.Length, numbers, turns, bsp);
    }

    /// <summary>
    /// The portal numbers a room's entities carry: how many there are, each
    /// checked to be one of 1 to that count, once, on an area portal entity,
    /// as vbsp numbers them.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="entities">The room's compiled entities.</param>
    /// <returns>The count; 0 when no entity carries one.</returns>
    /// <exception cref="LinkException">A number that is not a whole number, a repeat or a gap, or one on an entity that is no area portal.</exception>
    public static int PortalNumbersOf(string room, IEnumerable<BspEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        HashSet<int> seen = [];
        foreach (BspEntity entity in entities)
        {
            if (entity.Get(PortalNumberKey) is not { } text)
            {
                continue;
            }

            if (!MapFileLoader.IsAreaPortal(entity.ClassName ?? string.Empty))
            {
                throw new LinkException(
                    $"room {room} has a {entity.ClassName} with a \"{PortalNumberKey}\"; vbsp numbers only area portal entities.");
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || !seen.Add(number))
            {
                throw new LinkException(
                    $"room {room} has an area portal whose \"{PortalNumberKey}\" is \"{text}\"; vbsp numbers a map's portals 1, 2, ... once each.");
            }
        }

        if (seen.Count > 0 && seen.Max() != seen.Count)
        {
            throw new LinkException(
                $"room {room}'s area portals are numbered up to {seen.Max()} but there are {seen.Count}; vbsp numbers a map's portals 1, 2, ... without a gap.");
        }

        return seen.Count;
    }

    /// <summary>
    /// A room's three area lumps, checked against what vbsp writes: area 0
    /// and listing 0 are the reserved empty entries, every area's run of
    /// listings follows the previous one's from listing 1 to the end, and
    /// every listing names another area of the room, a portal number, a
    /// plane of the room and a run inside the clip vertices.
    /// </summary>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP.</param>
    /// <returns>The lumps.</returns>
    /// <exception cref="LinkException">A lump the link could not read the portals from.</exception>
    public static RoomAreaLumps Lumps(string room, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        DArea[] areas = BspStructView.As<DArea>(bsp[BspLump.Areas]).ToArray();
        DAreaPortal[] portals = BspStructView.As<DAreaPortal>(bsp[BspLump.AreaPortals]).ToArray();
        Vec3[] verts = BspStructView.As<Vec3>(bsp[BspLump.ClipPortalVerts]).ToArray();
        int planes = BspStructView.Count<DPlane>(bsp[BspLump.Planes]);
        if (areas.Length < 2 || portals.Length < 1 || !areas[0].Equals(default(DArea)) || !portals[0].Equals(default(DAreaPortal)))
        {
            throw new LinkException(
                $"room {room} has {areas.Length} areas and {portals.Length} area portal listings; vbsp writes the reserved area 0 and listing 0 empty, and at least one area after them.");
        }

        int next = 1;
        for (int a = 1; a < areas.Length; a++)
        {
            if (areas[a].FirstAreaPortal != next || areas[a].NumAreaPortals < 0)
            {
                throw new LinkException(
                    $"room {room}'s area {a} lists area portals from {areas[a].FirstAreaPortal} ({areas[a].NumAreaPortals} of them); vbsp lists each area's portals after the previous area's, from 1.");
            }

            next += areas[a].NumAreaPortals;
        }

        if (next != portals.Length)
        {
            throw new LinkException(
                $"room {room}'s areas list {next - 1} area portals but the lump holds {portals.Length - 1}; vbsp lists every portal from one area.");
        }

        for (int p = 1; p < portals.Length; p++)
        {
            DAreaPortal portal = portals[p];
            if (portal.OtherArea < 1 || portal.OtherArea >= areas.Length || portal.PortalKey < 1
                || portal.PlaneNum < 0 || portal.PlaneNum >= planes
                || portal.FirstClipPortalVert + portal.ClipPortalVerts > verts.Length)
            {
                throw new LinkException(
                    $"room {room}'s area portal listing {p} names area {portal.OtherArea}, portal {portal.PortalKey}, plane {portal.PlaneNum}"
                    + $" and clip vertices {portal.FirstClipPortalVert} to {portal.FirstClipPortalVert + portal.ClipPortalVerts};"
                    + $" the room has {areas.Length} areas, {planes} planes and {verts.Length} clip vertices.");
            }
        }

        return new RoomAreaLumps(areas, portals, verts);
    }

    /// <summary>The pack section holding this data.</summary>
    /// <param name="codec">How to store the payload; none by default, as every link section.</param>
    /// <returns>The section, tagged <see cref="SectionTag"/>.</returns>
    internal RoomPackSectionData ToSection(RoomLinkCodec codec = RoomLinkCodec.None)
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(AreaCount);
        w.Int(ListingCount);
        w.Int(ClipVertCount);
        w.Int(PortalNumbers);
        w.Int(_clipVerts.Length);
        foreach (Vec3[] turn in _clipVerts)
        {
            w.Structs<Vec3>(turn, counted: false);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), codec));
    }

    /// <summary>
    /// A room's area portals from its section, bound to <paramref name="bsp"/>;
    /// or null when the section is absent or of a revision this build does not read.
    /// </summary>
    /// <param name="section">The section's bytes, or null when the room has none.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <param name="bsp">The room's compiled BSP, whose lumps the section must fit.</param>
    /// <returns>The data, or null.</returns>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload that decodes to another
    /// length than recorded, or a payload that does not fit the room's lumps:
    /// cut short, another area, listing or vertex count, a turn count other
    /// than 1 or 4, bytes after its end.
    /// </exception>
    internal static RoomAreaPortals? Read(ArraySegment<byte>? section, string room, BspData bsp)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int areas = r.Int();
        int listings = r.Int();
        int verts = r.Int();
        int lumpAreas = BspStructView.Count<DArea>(bsp[BspLump.Areas]);
        int lumpListings = BspStructView.Count<DAreaPortal>(bsp[BspLump.AreaPortals]);
        int lumpVerts = BspStructView.Count<Vec3>(bsp[BspLump.ClipPortalVerts]);
        if (areas != lumpAreas || listings != lumpListings || verts != lumpVerts)
        {
            throw r.Mismatch(
                $"{areas} areas, {listings} area portal listings and {verts} clip vertices; the room has {lumpAreas}, {lumpListings} and {lumpVerts}");
        }

        int numbers = r.Int();
        if (numbers < 0 || numbers > ushort.MaxValue)
        {
            throw r.Mismatch($"{numbers} portal numbers");
        }

        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"{turns} turns of clip vertices; a section holds 1 or 4");
        }

        Vec3[][] clipVerts = new Vec3[turns][];
        for (int t = 0; t < turns; t++)
        {
            clipVerts[t] = r.Structs<Vec3>("clip vertices", verts, counted: false);
        }

        r.End();
        return new RoomAreaPortals(areas, listings, numbers, clipVerts, bsp);
    }

    /// <summary>
    /// What is wrong with a room's area portals for the link, as the rooms
    /// design's refusal says it, or null: an area portal entity whose brush
    /// reaches into a socket's plug box, or one that names a socket with
    /// <c>room_socket</c>.
    /// </summary>
    /// <param name="definition">The room: its name, cell and sockets.</param>
    /// <param name="room">The room's VMF, room-local.</param>
    /// <returns>The refusal's text, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why the plug box.</b> A room's areas meet another room's only
    /// through its doorways: at a joint the link joins the areas that face
    /// the doorway from either side (<c>LevelAreas</c>). A portal in the
    /// doorway itself would stand where the link strips the plug and carves
    /// the doorway, half of it in a space the room's compile never saw open;
    /// and at a cap it would seal nothing, the plug being a wall there. So a
    /// portal stays inside its room, and one that divides a room from its
    /// neighbour stands just inside the doorway, clear of the plug box (the
    /// rooms design, 4.11). Touching the plug box's face is not reaching into
    /// it: the test is the one the carve uses, a positive overlap beyond the
    /// cell tolerance.
    /// </para>
    /// <para>
    /// <b>Why no <c>room_socket</c>.</b> Socket furniture is dropped on one
    /// side of a joint, with its brushes; an area portal's brush is moved
    /// into the world by vbsp, not left as a model the link could omit, so
    /// the link could not drop it while the flatten would.
    /// </para>
    /// <para>
    /// The split refuses a library with such a portal, so the pack and the
    /// flatten both do, and a room compile given a VMF refuses one too.
    /// </para>
    /// </remarks>
    public static string? Problem(RoomDefinition definition, VmfDocument room)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(room);
        List<(Box Box, string Socket)>? plugs = null;
        foreach (VmfChunk entity in room.GetChunks(MapFileLoader.EntityChunk))
        {
            string className = entity.GetValue("classname") ?? string.Empty;
            if (!MapFileLoader.IsAreaPortal(className))
            {
                continue;
            }

            if (entity.GetValue(RoomStaticProps.SocketKey) is not null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"room {definition.Name}: {className} {VmfPlacement.IdOf(entity)} has {RoomStaticProps.SocketKey}; an area portal is built into its room's world and cannot be socket furniture.");
            }

            plugs ??= [.. definition.Sockets.Select(s => (RoomLinter.SealBox(definition, s, definition.CellSize), s.Name))];
            foreach (VmfChunk solid in entity.GetChunks(MapFileLoader.SolidChunk))
            {
                Box box = VmfPlacement.Bounds(solid);
                foreach ((Box plug, string socket) in plugs)
                {
                    if (box.Overlaps(plug, RoomLinter.CellEpsilon))
                    {
                        return string.Create(
                            CultureInfo.InvariantCulture,
                            $"room {definition.Name}: {className} {VmfPlacement.IdOf(entity)} lies in socket \"{socket}\"'s plug box.");
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>A room's three area lumps, as <see cref="RoomAreaPortals.Lumps"/> checked them.</summary>
/// <param name="Areas">The <c>Areas</c> lump, area 0 included.</param>
/// <param name="Portals">The <c>AreaPortals</c> lump, listing 0 included.</param>
/// <param name="ClipVerts">The <c>ClipPortalVerts</c> lump, room-local.</param>
internal sealed record RoomAreaLumps(DArea[] Areas, DAreaPortal[] Portals, Vec3[] ClipVerts)
{
    /// <summary>The area whose run of listings holds listing <paramref name="listing"/>.</summary>
    /// <param name="listing">A listing, 1 or more.</param>
    /// <returns>The listing's own area: the side it is listed from.</returns>
    public int SourceOf(int listing)
    {
        for (int a = 1; a < Areas.Length; a++)
        {
            if (listing >= Areas[a].FirstAreaPortal && listing < Areas[a].FirstAreaPortal + Areas[a].NumAreaPortals)
            {
                return a;
            }
        }

        return 0;
    }
}
