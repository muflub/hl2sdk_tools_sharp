//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rooms;

/// <summary>The per-turn parts of a room's link data, each its own pack section.</summary>
[Flags]
internal enum RoomLinkParts
{
    /// <summary>No per-turn part.</summary>
    None = 0,

    /// <summary>The turned geometry (<c>GEO</c><i>r</i>).</summary>
    Geometry = 1,

    /// <summary>The turned world collision (<c>COL</c><i>r</i>).</summary>
    Collision = 2,

    /// <summary>The turned entities (<c>ENT</c><i>r</i>).</summary>
    Entities = 4,
}

/// <summary>
/// How a link section's payload is stored: the byte every link section
/// starts with, before the payload's decoded length (<c>int64</c>, big-endian).
/// </summary>
internal enum RoomLinkCodec : byte
{
    /// <summary>The payload as it is.</summary>
    None = 0,

    /// <summary>The payload in raw Deflate (RFC 1951) at zlib level 9.</summary>
    Deflate = 1,

    /// <summary>The payload in Brotli (RFC 7932) at quality 11, window 22.</summary>
    Brotli = 2,
}

/// <summary>
/// The room pack sections that carry a room's <see cref="RoomLinkData"/>:
/// how they are written, read, and checked against the room they sit with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tags.</b> After a room's <see cref="RoomPack.RoomSection"/> come
/// <see cref="SharedTag"/> (<c>LNKA</c>), the part that depends on the room
/// alone, then per quarter turn <i>r</i> the three parts that depend on the
/// room and its turn: <c>GEO</c><i>r</i> (the turned geometry,
/// <see cref="RoomLinkGeometry"/>), <c>COL</c><i>r</i> (the world collision
/// read out and turned, <see cref="RoomLinkCollision"/>; only for a room with
/// a collision lump) and <c>ENT</c><i>r</i> (the entities parsed and turned,
/// <see cref="RoomLinkEntities"/>), turn by turn, so one turn's sections are
/// adjacent and a link placing a room at one turn reads them in one go. Every
/// section is optional and read on its own: a part whose section is missing
/// (a pack written before them, a room the link refuses, a pack that left a
/// part out) is computed at link, per placement, as it always was, to the
/// same bytes; a reader that does not know a tag skips it. That is why the
/// pack stays at version 1.
/// </para>
/// <para>
/// <b>Why one section per part and turn, four copies of the geometry.</b> A
/// quarter turn is exact and cheap, so the turned geometry could be derived
/// at link from the room's own lumps, and larger packs are the accepted
/// price only where they buy link time. Measured on the 1024-room stress
/// library (a busy 4-core box, medians of interleaved runs, 19×19 and 16×16
/// levels): the census in <c>LNKA</c> is 89 bytes a room and removes the
/// checks and the plug census (5 to 8 ms of work a level on one thread), and
/// with it stored for every room the planning runs without a thread pool
/// (10 to 20 ms of pool start-up and join a level); the numbers for each
/// per-turn part, and the choice they led to, are with
/// <see cref="StoredParts"/>. Separate sections let the choice be made per
/// part, and let a later per-turn part (lighting baked per turn) or a
/// room-alone one (door-to-door visibility) take a tag of its own.
/// </para>
/// <para>
/// <b>Codec.</b> Every link section starts with a <see cref="RoomLinkCodec"/>
/// byte and the payload's decoded length (<c>int64</c>), so any section can
/// be stored compressed without a format change; a codec this build does
/// not read, or a payload that decodes to another length than recorded, is
/// refused naming the room and section. The default is <see cref="RoomLinkCodec.None"/>,
/// by measurement: on the stress library Deflate shrinks the per-turn
/// sections about six times and Brotli about seven, but decompressing the
/// 261 rooms a 19×19 level places costs 5 to 11 ms where copying the raw
/// bytes out of a pack in the page cache costs under 1.5 ms, which is
/// more than the link saves by storing them. Compression stays
/// deterministic (a fixed zlib level, and a fixed Brotli quality and window,
/// set by number rather than through a level enum whose mapping the runtime
/// may change; the runtime's own zlib-ng and Brotli on every OS, which facts
/// pin), so a compressed pack is still a function of its rooms.
/// </para>
/// <para>
/// <b>Layout.</b> After the codec byte and the decoded length, the payload
/// (decoded) starts with an <c>int32</c> revision (<see cref="Revision"/>);
/// a reader skips a section of a revision it does not know, as it skips an
/// unknown tag. Counts, lengths and scalars are big-endian <c>int32</c>, as
/// in the rest of the pack. Arrays of the BSP's own structs
/// (<see cref="Vec3"/>, <see cref="DPlane"/>, <see cref="TexInfo"/>) and of
/// boxes (six floats, mins then maxs) are stored as in a BSP lump,
/// little-endian IEEE floats, after an <c>int32</c> count: the layout the
/// data already has in the room's lumps, which loads with one copy. Strings
/// are an <c>int32</c> byte length and UTF-8. Collision convexes are IVP
/// compact ledge bytes, as in the room's collision lump but turned.
/// </para>
/// <list type="table">
/// <listheader><term>Section</term><description>Payload, after the revision</description></listheader>
/// <item><term><c>LNKA</c></term><description>
/// socket count; per socket in the definition's order, four <c>int32</c>
/// arrays (count, then values): the facing clusters, the plug brushes, the
/// leaves to carve, the plug's drawn faces (<see cref="SocketCensus"/>).
/// </description></item>
/// <item><term><c>GEO</c><i>r</i></term><description>
/// vertices, plane pairs, one byte per pair for its swap, texinfos, node
/// boxes, leaf boxes, vertex normals, primitive vertices, occluder boxes, the
/// world model's box (no count), the plug boxes.
/// </description></item>
/// <item><term><c>COL</c><i>r</i></term><description>
/// the material names (count, strings), a virtual-terrain byte, the solid
/// count, and per solid its contents, its ledge count, and its ledges' bytes
/// back to back (length, bytes).
/// </description></item>
/// <item><term><c>ENT</c><i>r</i></term><description>
/// the entity count, and per entity a worldspawn byte, its keys (count, then
/// per key the key string, a kind byte, and for kind 0 the value string, for
/// kind 1 the turned origin as three floats), and for a worldspawn a byte
/// and, when it is 1, the turned extent box.
/// </description></item>
/// </list>
/// <para>
/// <b>Checked on read.</b> A section is the room's own only if its shape fits
/// the room it sits with: every array as long as the room's lump, every
/// census index inside the room, every ledge's length its own header's. A
/// section that does not fit is a damaged pack and is refused, naming the
/// room and the section, rather than linked into a map that would be wrong.
/// </para>
/// </remarks>
internal static class RoomLinkSections
{
    /// <summary>The tag of the section that depends on the room alone.</summary>
    public const string SharedTag = "LNKA";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>The largest payload a compressed section may claim: a lying length fails before it allocates.</summary>
    private const int MaxPayloadBytes = 1 << 30;

    /// <summary>The codec byte and the <c>int64</c> decoded length every link section starts with.</summary>
    private const int HeaderBytes = 1 + 8;

    /// <summary>The zlib level Deflate sections are written at (0 to 9): fixed, so the bytes are.</summary>
    private const int DeflateLevel = 9;

    /// <summary>The Brotli quality compressed sections are written at: fixed, so the bytes are.</summary>
    private const int BrotliQuality = 11;

    /// <summary>The Brotli window compressed sections are written with: fixed, so the bytes are.</summary>
    private const int BrotliWindow = 22;

    /// <summary>The per-turn parts a pack stores by default: the geometry and the collision, not the entities.</summary>
    /// <remarks>
    /// <para>
    /// Chosen by measured link time against pack size, on the 1024-room
    /// stress library with the pack in the page cache (one thread; per
    /// level, what computing the part for every placement costs against what
    /// reading and decoding its sections costs, index included):
    /// </para>
    /// <list type="table">
    /// <listheader><term>Part</term><description>Computed at link / read instead (16×16, 19×19); pack added</description></listheader>
    /// <item><term>geometry</term><description>6.4 / 2.6 ms, 8.1 / 1.5 ms; +13.0 MB (+77%). Stored.</description></item>
    /// <item><term>collision</term><description>4.1 / 1.5 ms, 5.6 to 6.7 / 1.4 ms; +18.1 MB (+107%). Stored.</description></item>
    /// <item><term>entities</term><description>
    /// 1.5 / 0.9 to 2.5 ms, 2.2 / 1.3 to 1.8 ms; +1.4 MB (+8%). Not stored:
    /// decoding a handful of strings costs what parsing them does, and the
    /// difference is inside the run-to-run noise.
    /// </description></item>
    /// </list>
    /// <para>
    /// Every part is written by <see cref="Write"/> when asked for and read
    /// when present, so the choice is a writer default, not a format rule.
    /// </para>
    /// </remarks>
    public const RoomLinkParts StoredParts = RoomLinkParts.Geometry | RoomLinkParts.Collision;

    /// <summary>The tag of a room's turned geometry for one quarter turn.</summary>
    public static string GeometryTag(int rotation) => rotation switch
    {
        0 => "GEO0",
        1 => "GEO1",
        2 => "GEO2",
        3 => "GEO3",
        _ => throw Turn(rotation),
    };

    /// <summary>The tag of a room's turned collision for one quarter turn.</summary>
    public static string CollisionTag(int rotation) => rotation switch
    {
        0 => "COL0",
        1 => "COL1",
        2 => "COL2",
        3 => "COL3",
        _ => throw Turn(rotation),
    };

    /// <summary>The tag of a room's turned entities for one quarter turn.</summary>
    public static string EntitiesTag(int rotation) => rotation switch
    {
        0 => "ENT0",
        1 => "ENT1",
        2 => "ENT2",
        3 => "ENT3",
        _ => throw Turn(rotation),
    };

    /// <summary>The tags of one quarter turn's sections, in pack order.</summary>
    public static IEnumerable<string> RotationTags(int rotation) =>
        [GeometryTag(rotation), CollisionTag(rotation), EntitiesTag(rotation)];

    /// <summary>The sections for a room's link data, in pack order.</summary>
    /// <param name="data">The link data, every turn's every part present (collision only if the room has some).</param>
    /// <param name="parts">Which per-turn parts to store.</param>
    /// <param name="codec">How to store every section's payload.</param>
    /// <returns><c>LNKA</c>, then per turn its <c>GEO</c>, <c>COL</c> and <c>ENT</c> sections.</returns>
    public static IReadOnlyList<RoomPackSectionData> Write(
        RoomLinkData data, RoomLinkParts parts = StoredParts, RoomLinkCodec codec = RoomLinkCodec.None)
    {
        List<RoomPackSectionData> sections = [new(SharedTag, Encode(WriteShared(data.Shared), codec))];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            RoomLinkRotation turn = data.Rotation(rotation)
                ?? throw new ArgumentException($"link data for {data.Definition.Name} lacks turn {rotation}", nameof(data));
            if ((parts & RoomLinkParts.Geometry) != 0)
            {
                sections.Add(new(GeometryTag(rotation), Encode(WriteGeometry(Held(turn.Geometry, data)), codec)));
            }

            if ((parts & RoomLinkParts.Collision) != 0 && turn.Collision is { } collision)
            {
                sections.Add(new(CollisionTag(rotation), Encode(WriteCollision(collision), codec)));
            }

            if ((parts & RoomLinkParts.Entities) != 0)
            {
                sections.Add(new(EntitiesTag(rotation), Encode(WriteEntities(Held(turn.Entities, data)), codec)));
            }
        }

        return sections;
    }

    /// <summary>
    /// A room's link data from its sections, or null when the room has no
    /// usable <c>LNKA</c> (absent, or a codec or revision this build does not read).
    /// </summary>
    /// <param name="room">The room the sections sit with.</param>
    /// <param name="section">The room's section bytes by tag, or null for a tag not read or absent.</param>
    /// <returns>The link data, bound to <paramref name="room"/>, holding the parts it was given; or null.</returns>
    /// <exception cref="LinkException">A section is truncated or does not fit the room.</exception>
    public static RoomLinkData? Read(RoomObject room, Func<string, ArraySegment<byte>?> section)
    {
        string name = room.Definition.Name;
        if (Open(section(SharedTag), name, SharedTag) is not { } shared)
        {
            return null;
        }

        RoomLinkShared linkShared = ReadShared(shared, room);
        RoomLinkRotation?[] turns = new RoomLinkRotation?[4];
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Reader? geometry = Open(section(GeometryTag(rotation)), name, GeometryTag(rotation));
            Reader? collision = Open(section(CollisionTag(rotation)), name, CollisionTag(rotation));
            Reader? entities = Open(section(EntitiesTag(rotation)), name, EntitiesTag(rotation));
            if (geometry is null && collision is null && entities is null)
            {
                continue;
            }

            turns[rotation] = new RoomLinkRotation(
                geometry is null ? null : ReadGeometry(geometry, room, rotation),
                collision is null ? null : ReadCollision(collision, room),
                entities is null ? null : ReadEntities(entities));
        }

        return new RoomLinkData(room.Definition, room.Bsp, room.Vis, linkShared, turns);
    }

    /// <summary>A section's payload with its codec byte in front.</summary>
    internal static byte[] Encode(byte[] payload, RoomLinkCodec codec)
    {
        byte[] body = codec switch
        {
            RoomLinkCodec.None => payload,
            RoomLinkCodec.Deflate => DeflateBytes(payload),
            RoomLinkCodec.Brotli => BrotliBytes(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "not a link section codec"),
        };

        byte[] section = new byte[HeaderBytes + body.Length];
        section[0] = (byte)codec;
        BinaryPrimitives.WriteInt64BigEndian(section.AsSpan(1), payload.Length);
        body.CopyTo(section, HeaderBytes);
        return section;
    }

    /// <summary>
    /// Deflate (RFC 1951, no zlib wrapper) at a fixed zlib level: set by
    /// number rather than through <see cref="CompressionLevel"/>, whose
    /// mapping to a zlib level is the runtime's to change.
    /// </summary>
    internal static byte[] DeflateBytes(ReadOnlySpan<byte> payload)
    {
        using MemoryStream packed = new();
        using (DeflateStream deflate = new(packed, new ZLibCompressionOptions { CompressionLevel = DeflateLevel }, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return packed.ToArray();
    }

    /// <summary>Brotli (RFC 7932) at a fixed quality and window, through the encoder rather than a level enum.</summary>
    internal static byte[] BrotliBytes(ReadOnlySpan<byte> payload)
    {
        byte[] packed = new byte[BrotliEncoder.GetMaxCompressedLength(payload.Length)];
        if (!BrotliEncoder.TryCompress(payload, packed, out int written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli could not compress a link section into its own bound");
        }

        return packed[..written];
    }

    /// <summary>
    /// A section's payload, decoded, behind its revision: or null when the
    /// section is absent or has a revision this build does not read (the
    /// part is then computed at link, as for an absent section).
    /// </summary>
    /// <exception cref="LinkException">
    /// A codec this build does not read, a payload cut short, or one that
    /// decodes to another length than the section records.
    /// </exception>
    internal static Reader? Open(ArraySegment<byte>? section, string room, string tag)
    {
        if (section is not { } bytes)
        {
            return null;
        }

        if (bytes.Count < HeaderBytes)
        {
            throw new LinkException($"room pack entry \"{room}\": its \"{tag}\" section is truncated.");
        }

        byte codec = bytes[0];
        long length = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1));
        ArraySegment<byte> body = bytes[HeaderBytes..];
        ArraySegment<byte> payload = (RoomLinkCodec)codec switch
        {
            RoomLinkCodec.None => body,
            RoomLinkCodec.Deflate or RoomLinkCodec.Brotli => Decompress((RoomLinkCodec)codec, body, length, room, tag),
            _ => throw new LinkException($"room {room}: section {tag} uses codec {codec}, which this build does not read."),
        };

        if (payload.Count != length)
        {
            throw new LinkException($"room {room}: section {tag} decodes to {payload.Count} bytes, not the {length} it records.");
        }

        Reader reader = new(payload, room, tag);
        return reader.Int() == Revision ? reader : null;
    }

    /// <summary>
    /// A compressed payload, decoded into at most <paramref name="length"/>
    /// bytes (the length the section records, which also bounds what a
    /// lying section can make the reader allocate); what it decodes to is
    /// returned, whatever its length, for the caller to hold against the
    /// record.
    /// </summary>
    private static ArraySegment<byte> Decompress(RoomLinkCodec codec, ArraySegment<byte> packed, long length, string room, string tag)
    {
        if (length is < 0 or > MaxPayloadBytes)
        {
            throw new LinkException($"room {room}: section {tag} records {length} bytes; a section decodes to 0 to {MaxPayloadBytes}.");
        }

        // One byte of room past the record, so a payload that decodes long
        // is seen as long rather than cut to fit.
        byte[] payload = new byte[length + 1];
        int written = 0;
        try
        {
            if (codec == RoomLinkCodec.Brotli)
            {
                using BrotliDecoder decoder = new();
                OperationStatus status = decoder.Decompress(packed, payload, out _, out written);
                if (status is OperationStatus.InvalidData)
                {
                    throw new InvalidDataException();
                }
            }
            else
            {
                using MemoryStream source = new(packed.Array!, packed.Offset, packed.Count, writable: false);
                using DeflateStream inflate = new(source, CompressionMode.Decompress);
                int got;
                while (written < payload.Length && (got = inflate.Read(payload, written, payload.Length - written)) > 0)
                {
                    written += got;
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new LinkException($"room {room}: section {tag} is not valid {codec} data.");
        }

        return new ArraySegment<byte>(payload, 0, written);
    }

    private static T Held<T>(T? part, RoomLinkData data)
        where T : class =>
        part ?? throw new ArgumentException($"link data for {data.Definition.Name} lacks a part", nameof(data));

    private static ArgumentOutOfRangeException Turn(int rotation) =>
        new(nameof(rotation), rotation, "a quarter-turn count is 0 to 3");

    private static byte[] WriteShared(RoomLinkShared shared)
    {
        Writer w = new();
        w.Int(Revision);
        w.Int(shared.Sockets.Count);
        foreach (SocketCensus census in shared.Sockets)
        {
            w.Ints(census.Facing);
            w.Ints(census.StrippedBrushes);
            w.Ints(census.CarveLeaves);
            w.Ints(census.StrippedFaces);
        }

        return w.ToArray();
    }

    private static RoomLinkShared ReadShared(Reader r, RoomObject room)
    {
        BspData bsp = room.Bsp;
        int sockets = r.Int();
        if (sockets != room.Definition.Sockets.Count)
        {
            throw r.Mismatch($"{sockets} sockets, the room has {room.Definition.Sockets.Count}");
        }

        int clusters = room.Vis.ClusterCount;
        int brushes = BspStructView.Count<DBrush>(bsp[BspLump.Brushes]);
        int leaves = BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]);
        int faces = BspStructView.Count<DFace>(bsp[BspLump.Faces]);
        List<SocketCensus> census = new(sockets);
        for (int s = 0; s < sockets; s++)
        {
            census.Add(new SocketCensus(
                r.Ints("facing clusters", clusters),
                r.Ints("plug brushes", brushes),
                r.Ints("carved leaves", leaves),
                r.Ints("plug faces", faces)));
        }

        r.End();
        return new RoomLinkShared(census);
    }

    private static byte[] WriteGeometry(RoomLinkGeometry g)
    {
        Writer w = new();
        w.Int(Revision);
        w.Structs<Vec3>(g.Vertices);
        w.Structs<DPlane>(g.PlanePairs);
        w.Int(g.PlaneSwapped.Length);
        foreach (bool swapped in g.PlaneSwapped)
        {
            w.Byte(swapped ? (byte)1 : (byte)0);
        }

        w.Structs<TexInfo>(g.TexInfos);
        w.Boxes(g.NodeBoxes);
        w.Boxes(g.LeafBoxes);
        w.Structs<Vec3>(g.VertNormals);
        w.Structs<Vec3>(g.PrimVerts);
        w.Boxes(g.OccluderBoxes);
        w.Box(g.ModelBox);
        w.Boxes(g.PlugBoxes);
        return w.ToArray();
    }

    private static RoomLinkGeometry ReadGeometry(Reader r, RoomObject room, int rotation)
    {
        BspData bsp = room.Bsp;
        int occluders = bsp[BspLump.Occlusion].Length == 0 ? 0 : OcclusionLump.Read(bsp[BspLump.Occlusion]).Occluders.Count;
        int planePairs = BspStructView.Count<DPlane>(bsp[BspLump.Planes]) / 2;
        RoomLinkGeometry geometry = new()
        {
            Rotation = rotation,
            Vertices = r.Structs<Vec3>("vertices", BspStructView.Count<Vec3>(bsp[BspLump.Vertexes])),
            PlanePairs = r.Structs<DPlane>("plane pairs", planePairs),
            PlaneSwapped = r.Flags("plane swaps", planePairs),
            TexInfos = r.Structs<TexInfo>("texinfos", BspStructView.Count<TexInfo>(bsp[BspLump.TexInfo])),
            NodeBoxes = r.Boxes("node boxes", BspStructView.Count<DNode>(bsp[BspLump.Nodes])),
            LeafBoxes = r.Boxes("leaf boxes", BspStructView.Count<DLeaf>(bsp[BspLump.Leafs])),
            VertNormals = r.Structs<Vec3>("vertex normals", BspStructView.Count<Vec3>(bsp[BspLump.VertNormals])),
            PrimVerts = r.Structs<Vec3>("primitive vertices", BspStructView.Count<Vec3>(bsp[BspLump.PrimVerts])),
            OccluderBoxes = r.Boxes("occluder boxes", occluders),
            ModelBox = r.Box(),
            PlugBoxes = r.Boxes("plug boxes", room.Definition.Sockets.Count),
        };

        r.End();
        return geometry;
    }

    private static byte[] WriteCollision(RoomLinkCollision collision)
    {
        Writer w = new();
        w.Int(Revision);
        w.Int(collision.Materials.Count);
        foreach (string material in collision.Materials)
        {
            w.String(material);
        }

        w.Byte(collision.VirtualTerrain ? (byte)1 : (byte)0);
        w.Int(collision.Solids.Count);
        foreach (RoomLinkSolid solid in collision.Solids)
        {
            w.Int(solid.Contents);
            w.Int(solid.Starts.Length);
            w.Int(solid.Ledges.Length);
            w.Raw(solid.Ledges);
        }

        return w.ToArray();
    }

    private static RoomLinkCollision ReadCollision(Reader r, RoomObject room)
    {
        if (room.Bsp[BspLump.PhysCollide].Length == 0)
        {
            throw r.Mismatch("world collision the room does not have");
        }

        string[] materials = new string[r.Count("materials")];
        for (int m = 0; m < materials.Length; m++)
        {
            materials[m] = r.String();
        }

        bool virtualTerrain = r.Flag();
        RoomLinkSolid[] solids = new RoomLinkSolid[r.Count("solids")];
        for (int s = 0; s < solids.Length; s++)
        {
            int contents = r.Int();
            int count = r.Count("ledges");
            solids[s] = r.Ledges(contents, count);
        }

        r.End();
        return new RoomLinkCollision(materials, virtualTerrain, solids);
    }

    private static byte[] WriteEntities(RoomLinkEntities entities)
    {
        Writer w = new();
        w.Int(Revision);
        w.Int(entities.Items.Count);
        foreach (RoomLinkEntity entity in entities.Items)
        {
            if (entity.Error is not null)
            {
                throw new ArgumentException("an entity the link cannot move is never stored", nameof(entities));
            }

            w.Byte(entity.IsWorld ? (byte)1 : (byte)0);
            w.Int(entity.Pairs.Count);
            foreach (RoomLinkPair pair in entity.Pairs)
            {
                w.String(pair.Key);
                if (pair.Value is { } value)
                {
                    w.Byte(0);
                    w.String(value);
                }
                else
                {
                    w.Byte(1);
                    w.Structs<Vec3>([pair.Origin], counted: false);
                }
            }

            if (entity.IsWorld)
            {
                w.Byte(entity.Extent is null ? (byte)0 : (byte)1);
                if (entity.Extent is { } extent)
                {
                    w.Box(extent);
                }
            }
        }

        return w.ToArray();
    }

    private static RoomLinkEntities ReadEntities(Reader r)
    {
        RoomLinkEntity[] entities = new RoomLinkEntity[r.Count("entities")];
        for (int e = 0; e < entities.Length; e++)
        {
            bool isWorld = r.Flag();
            RoomLinkPair[] pairs = new RoomLinkPair[r.Count("keys")];
            for (int p = 0; p < pairs.Length; p++)
            {
                string key = r.String();
                pairs[p] = r.Flag()
                    ? new RoomLinkPair(key, null, r.Structs<Vec3>("origin", 1, counted: false)[0])
                    : new RoomLinkPair(key, r.String(), default);
            }

            Box? extent = isWorld && r.Flag() ? r.Box() : null;
            entities[e] = new RoomLinkEntity(isWorld, pairs, extent, null);
        }

        r.End();
        return new RoomLinkEntities(entities);
    }

    /// <summary>Appends big-endian scalars and little-endian struct arrays.</summary>
    private sealed class Writer
    {
        private readonly MemoryStream _bytes = new();

        public void Int(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            _bytes.Write(b);
        }

        public void Byte(byte value) => _bytes.WriteByte(value);

        public void Ints(int[] values)
        {
            Int(values.Length);
            foreach (int value in values)
            {
                Int(value);
            }
        }

        public void Structs<T>(ReadOnlySpan<T> items, bool counted = true)
            where T : unmanaged
        {
            if (counted)
            {
                Int(items.Length);
            }

            _bytes.Write(MemoryMarshal.AsBytes(items));
        }

        public void Box(Box box) => Structs<Vec3>([box.Mins, box.Maxs], counted: false);

        public void Boxes(Box[] boxes) => Structs<Box>(boxes);

        public void Raw(byte[] bytes) => _bytes.Write(bytes);

        public void String(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Int(bytes.Length);
            _bytes.Write(bytes);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>Reads what <see cref="Writer"/> wrote, refusing anything short or out of shape.</summary>
    internal sealed class Reader(ArraySegment<byte> bytes, string room, string tag)
    {
        private readonly UTF8Encoding _strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private int _at;

        public LinkException Mismatch(string what) =>
            new($"room pack entry \"{room}\": its \"{tag}\" section holds {what}.");

        public int Int() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public int Count(string what)
        {
            int count = Int();
            if (count < 0 || count > bytes.Count - _at)
            {
                throw Mismatch($"{count} {what}, more than its bytes can");
            }

            return count;
        }

        public bool Flag() => Take(1)[0] switch
        {
            0 => false,
            1 => true,
            byte other => throw Mismatch($"a flag byte of {other}"),
        };

        public bool[] Flags(string what, int expected)
        {
            int count = Expect(what, expected);
            ReadOnlySpan<byte> flags = Take(count);
            if (flags.IndexOfAnyExcept((byte)0, (byte)1) is int bad and >= 0)
            {
                throw Mismatch($"a flag byte of {flags[bad]}");
            }

            return MemoryMarshal.Cast<byte, bool>(flags).ToArray();
        }

        public int[] Ints(string what, int limit)
        {
            int[] values = new int[Count(what)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = Int();
                if ((uint)values[i] >= (uint)limit)
                {
                    throw Mismatch($"{what} naming {values[i]}; the room has {limit}");
                }
            }

            return values;
        }

        public T[] Structs<T>(string what, int expected, bool counted = true)
            where T : unmanaged
        {
            int count = counted ? Expect(what, expected) : expected;
            int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            if ((long)count * size > bytes.Count - _at)
            {
                throw Truncated();
            }

            return MemoryMarshal.Cast<byte, T>(Take(count * size)).ToArray();
        }

        public Box Box()
        {
            Vec3[] corners = Structs<Vec3>("box", 2, counted: false);
            return new Box(corners[0], corners[1]);
        }

        public Box[] Boxes(string what, int expected) => Structs<Box>(what, expected);

        public string String()
        {
            int length = Count("string bytes");
            try
            {
                return _strict.GetString(Take(length));
            }
            catch (DecoderFallbackException)
            {
                throw Mismatch("a string that is not UTF-8");
            }
        }

        /// <summary>
        /// A solid's IVP compact ledges, back to back: each one's own header
        /// must give a length that fits, and together they must fill the bytes.
        /// </summary>
        public RoomLinkSolid Ledges(int contents, int count)
        {
            byte[] ledges = Take(Count("ledge bytes")).ToArray();
            int[] starts = new int[count];
            int at = 0;
            for (int l = 0; l < count; l++)
            {
                starts[l] = at;
                if (ledges.Length - at < 16)
                {
                    throw Mismatch($"collision convex {l} cut short");
                }

                ReadOnlySpan<byte> header = ledges.AsSpan(at, 16);
                long size = (long)(BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) >> 8) * 16;
                int points = BinaryPrimitives.ReadInt32LittleEndian(header);
                short triangles = BinaryPrimitives.ReadInt16LittleEndian(header[12..]);
                if (size < 16 || size > ledges.Length - at || triangles < 0
                    || points < 16 + (16 * triangles) || points > size || points % 16 != 0)
                {
                    throw Mismatch($"collision convex {l} whose header does not fit its bytes");
                }

                at += (int)size;
            }

            if (at != ledges.Length)
            {
                throw Mismatch($"{ledges.Length - at} bytes after its last collision convex");
            }

            return new RoomLinkSolid(contents, ledges, starts);
        }

        public void End()
        {
            if (_at != bytes.Count)
            {
                throw Mismatch($"{bytes.Count - _at} bytes after its end");
            }
        }

        private int Expect(string what, int expected)
        {
            int count = Int();
            if (count != expected)
            {
                throw Mismatch($"{count} {what}; the room has {expected}");
            }

            return count;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > bytes.Count - _at)
            {
                throw Truncated();
            }

            ReadOnlySpan<byte> span = bytes.AsSpan(_at, count);
            _at += count;
            return span;
        }

        private LinkException Truncated() => new($"room pack entry \"{room}\": its \"{tag}\" section is truncated.");
    }
}
