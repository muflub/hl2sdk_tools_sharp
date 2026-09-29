//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What a room library's rooms are lit with at pack time: the vrad switches
/// every room's bake runs with, and the library's sun.
/// </summary>
/// <remarks>
/// <para>
/// The rooms design (section 9, option C) lights each room once, alone, at
/// pack time, where the game's files are, so that the link needs nothing but
/// the pack (D1). The bake is a normal vrad run of the room as it was
/// compiled, sealed by its plugs, with the library's sun added (D3: one sun,
/// library-wide, never turned). A level linked from the pack carries the
/// rooms' lighting; a level linked from a pack built without these settings
/// is unlit, byte for byte as before the bake existed.
/// </para>
/// <para>
/// A level linked from lit rooms is held to the full compile of its
/// flattened map (<c>ssmap link --flatten</c>, then vbsp, vvis and vrad)
/// with the same switches, so the flattened compile must be run with them
/// too: the pack records them (<see cref="Describe"/>) in its id.
/// </para>
/// </remarks>
public sealed class RoomLightingSettings
{
    /// <summary>Lights the rooms with the given vrad switches.</summary>
    /// <param name="options">
    /// The vrad switches every room is lit with. <c>-luxeldensity</c> below
    /// one is refused, because it rewrites texture axes and face extents,
    /// which a bake may not change (the link carries the room's geometry
    /// from its compile, not from its bake).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options change the room's geometry.</exception>
    public RoomLightingSettings(VradOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LuxelDensity < 1.0f)
        {
            throw new ArgumentException(
                "-luxeldensity below 1 rewrites texture axes and face extents; a room's bake keeps its compile's geometry.",
                nameof(options));
        }

        Options = options;
    }

    /// <summary>The vrad switches every room is lit with.</summary>
    public VradOptions Options { get; }

    /// <summary>
    /// The library's sun (its <c>light_environment</c>, as the library wrote
    /// it in the gaps between cells), or null for a library without one.
    /// Each room's bake adds it after the worldspawn, at the level's origin,
    /// exactly as the link and the flatten write it.
    /// </summary>
    public VmfChunk? Sun { get; init; }

    /// <summary>The library's sun among its entities (<see cref="RoomLibrary.LibraryEntities"/>): the first <c>light_environment</c>, or null.</summary>
    /// <param name="libraryEntities">The library-wide entities.</param>
    /// <returns>The sun, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="libraryEntities"/> is null.</exception>
    public static VmfChunk? SunOf(IReadOnlyList<VmfChunk> libraryEntities)
    {
        ArgumentNullException.ThrowIfNull(libraryEntities);
        return libraryEntities.FirstOrDefault(e =>
            string.Equals(e.GetValue("classname"), "light_environment", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A stable text of everything that shapes a bake, for the pack's id and
    /// the incremental cache's keys: the switches as vrad would echo them and
    /// the sun's keys.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        StringBuilder text = new("light:");
        text.Append(RoomOptionsDigest.Of(Options));
        if (Sun is not null)
        {
            text.Append("|sun:");
            foreach (VmfKey key in Sun.Keys)
            {
                text.Append(key.Name).Append('=').Append(key.Value).Append(';');
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// A room's base lighting (the rooms design, 9.1 part 1): what one vrad run
/// of the room, sealed by its plugs, gives each face, leaf, light and prop,
/// stored once, or per quarter turn when the sun or sky reaches the room.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is baked.</b> The room's compile is lit as a map is, with the
/// library's sun added (<see cref="RoomLightingSettings"/>). For each lit
/// range (LDR, HDR, or both, as the library compile asks) the bake keeps:
/// every face's four styles and lightmap offset, its luxels (bump pages
/// included), the room's world lights, the leaf ambient samples, and the
/// static props' vertex colours when vrad lit them. Beside those, for the
/// room as a whole: the vertex normals vrad writes, the leaves pass one of
/// the sky test flags (those holding a sky face), the map flags, and the
/// sun's two world lights.
/// </para>
/// <para>
/// <b>Once or per turn (1.1, D16).</b> A room no sun or sky reaches is lit
/// once: point, spot and surface lights turn with the room, so its light is
/// the same in its own frame at every turn, and a turn moves none of the
/// bytes (lightmaps are stored per luxel in face order, vertex colours in
/// vertex order). A room with a sky face (<c>SURF_SKY</c> or
/// <c>SURF_SKY2D</c>) in a library with a sun is lit four times, once per
/// quarter turn, because the sun is fixed in the world: each bake runs in
/// the room's frame with every world-fixed direction turned into it
/// (<see cref="Rad.Light.BakeFrame"/>), which keeps the lightmap layout (the
/// luxel axes turn with the room) so turn <i>r</i>'s data drops into the
/// same faces. The link takes payload <c>rotation mod count</c>. Every
/// payload is in the room's own frame; what has a direction in it (the
/// ambient cubes and sample positions, the world lights) is turned at link,
/// exactly, since a quarter turn permutes and negates.
/// </para>
/// <para>
/// <b>Linear values (9.3).</b> Luxels, ambient cube faces and vertex colours
/// are stored as half floats of linear radiance, not as the encoded bytes
/// a map holds, so that the door terms of the capture and response (PR 10)
/// can be summed onto them before the one encode at link. Each is the exact
/// value of the <c>ColorRGBExp32</c> vrad wrote (an 8-bit mantissa times a
/// power of two, which a half holds exactly from 2^-24 to 65,280), so the
/// link's encode (<see cref="StockLightColor.Encode"/>) gives back vrad's
/// bytes: a capped room links to its own bake, bit for bit.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>LITE</c>) has the link
/// sections' framing (<see cref="RoomLinkSections"/>: codec byte, decoded
/// length, revision; codec none), then, as 1.1 lays out any section that
/// may hold turns, the rotation count (1 or 4) and that many payloads in
/// turn order, each behind its byte length; then the room-wide part. A
/// payload is a flag byte (1 LDR, 2 HDR) and one block per range: the face
/// count, styles (four bytes a face), offsets (<c>int32</c> a face, -1 for
/// none), the luxels (count, then three halves each), the room's own world
/// lights (count, then the lump's structs, the sun's two left out), the
/// ambient index (one per leaf), the samples (count, then 18 halves and the
/// four position bytes each), and the lit props (count; per prop its index,
/// the model checksum, its strip groups' LODs and vertex counts, then three
/// halves a vertex). The room-wide part is the face and leaf counts, the
/// vertex normals and their indices, whether a sun lit the room and the
/// pass-one sky leaves (leaf, flag), the map flags, and per range the sun's
/// world lights at turn 0.
/// </para>
/// <para>
/// <b>Bound to its compile.</b> Like every stored part of a room, it holds
/// only while it describes the compile it was baked from (<see cref="IsFor"/>);
/// a pack read back binds it to the container's BSP after checking its
/// counts against it.
/// </para>
/// </remarks>
internal sealed class RoomLighting
{
    /// <summary>The tag of a room's lighting section.</summary>
    public const string SectionTag = "LITE";

    /// <summary>The revision this build writes and reads.</summary>
    public const int Revision = 1;

    /// <summary>Luxels per face style and bump page are four bytes in the lump.</summary>
    internal const int LuxelBytes = 4;

    private readonly BspData? _bsp;

    internal RoomLighting(
        int faceCount,
        int leafCount,
        Vec3[] vertNormals,
        ushort[] vertNormalIndices,
        bool sunlit,
        IReadOnlyList<(int Leaf, LeafFlags Flags)> skyLeaves,
        uint mapFlags,
        RoomLightingPayload[] payloads,
        DWorldLight[]? skyLdr,
        DWorldLight[]? skyHdr,
        BspData? bsp)
    {
        FaceCount = faceCount;
        LeafCount = leafCount;
        VertNormals = vertNormals;
        VertNormalIndices = vertNormalIndices;
        HasSun = sunlit;
        SkyLeaves = skyLeaves;
        MapFlags = mapFlags;
        Payloads = payloads;
        SkyLdr = skyLdr;
        SkyHdr = skyHdr;
        _bsp = bsp;
    }

    /// <summary>The room's face count, which every payload's per-face arrays match.</summary>
    public int FaceCount { get; }

    /// <summary>The room's leaf count, which every payload's ambient index matches.</summary>
    public int LeafCount { get; }

    /// <summary>The vertex normals vrad wrote for the room, in its frame.</summary>
    public Vec3[] VertNormals { get; }

    /// <summary>The vertex-normal indices vrad wrote, one per face vertex, in face order.</summary>
    public ushort[] VertNormalIndices { get; }

    /// <summary>
    /// Whether the bake had a sun (the library has a <c>light_environment</c>):
    /// only then does vrad flag sky leaves, so only then does the link.
    /// </summary>
    public bool HasSun { get; }

    /// <summary>The leaves holding a sky face and the flag pass one gives each (<see cref="LeafFlags.Sky"/> or <see cref="LeafFlags.Sky2D"/>).</summary>
    public IReadOnlyList<(int Leaf, LeafFlags Flags)> SkyLeaves { get; }

    /// <summary>The map flags vrad wrote (the baked-prop-lighting bits).</summary>
    public uint MapFlags { get; }

    /// <summary>The payloads, 1 or 4, in turn order.</summary>
    public RoomLightingPayload[] Payloads { get; }

    /// <summary>The sun's world lights of the LDR range, at turn 0 (the world's frame), or null.</summary>
    public DWorldLight[]? SkyLdr { get; }

    /// <summary>The sun's world lights of the HDR range, at turn 0, or null.</summary>
    public DWorldLight[]? SkyHdr { get; }

    /// <summary>How many payloads the room stores: 4 when sun or sky light reaches it, else 1.</summary>
    public int RotationCount => Payloads.Length;

    /// <summary>The payload a placement at <paramref name="rotation"/> takes: <c>rotation mod count</c>.</summary>
    /// <param name="rotation">The placement's quarter turns, 0 to 3.</param>
    public RoomLightingPayload For(int rotation) => Payloads[rotation % Payloads.Length];

    /// <summary>Whether this is the lighting of <paramref name="room"/>'s own compile.</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same lighting bound to another BSP of the same room (a pack's read-back container).</summary>
    internal RoomLighting BoundTo(BspData bsp) =>
        new(FaceCount, LeafCount, VertNormals, VertNormalIndices, HasSun, SkyLeaves, MapFlags, Payloads, SkyLdr, SkyHdr, bsp);

    /// <summary>
    /// Whether sun or sky light can reach a compiled room: a face whose
    /// texinfo is sky (<c>SURF_SKY</c> or <c>SURF_SKY2D</c>). Pass one of the
    /// sky test flags exactly the leaves holding such a face, so the two
    /// halves of 1.1's rule are one test.
    /// </summary>
    /// <param name="bsp">The room's compile.</param>
    /// <returns>True when the room has a sky face.</returns>
    public static bool HasSkyFace(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        foreach (DFace face in BspStructView.As<DFace>(bsp[BspLump.Faces]))
        {
            if (face.TexInfo >= 0 && face.TexInfo < texInfos.Length
                && (texInfos[face.TexInfo].Flags & (int)(SurfaceFlags.Sky | SurfaceFlags.Sky2D)) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pass one of vrad's sky test on a room: each leaf holding a sky face,
    /// flagged <see cref="LeafFlags.Sky2D"/> when that face is 2D sky and
    /// <see cref="LeafFlags.Sky"/> otherwise, as vrad's pass one decides it.
    /// </summary>
    /// <param name="bsp">The room's compile.</param>
    /// <returns>The sky leaves, in leaf order.</returns>
    public static IReadOnlyList<(int Leaf, LeafFlags Flags)> PassOne(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<ushort> leafFaces = BspStructView.As<ushort>(bsp[BspLump.LeafFaces]);
        ReadOnlySpan<DLeaf> leaves = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]);
        List<(int, LeafFlags)> sky = [];
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            for (int i = 0; i < leaves[leaf].NumLeafFaces; i++)
            {
                int flags = texInfos[faces[leafFaces[leaves[leaf].FirstLeafFace + i]].TexInfo].Flags;
                if ((flags & (int)SurfaceFlags.Sky) == 0)
                {
                    continue;
                }

                // As vrad's pass one: a face with both bits counts as 2D.
                sky.Add((leaf, (flags & (int)SurfaceFlags.Sky2D) != 0 ? LeafFlags.Sky2D : LeafFlags.Sky));
                break;
            }
        }

        return sky;
    }

    /// <summary>
    /// Bakes a compiled room: one vrad run of its compile with the library's
    /// sun, or four, one per quarter turn, when sun or sky light reaches it.
    /// </summary>
    /// <param name="room">The compiled room; its BSP is not changed.</param>
    /// <param name="settings">The vrad switches and the sun.</param>
    /// <param name="content">The game content vrad reads (materials, models, texlight files).</param>
    /// <param name="parallelism">How much of the machine each run may use.</param>
    /// <param name="cancellationToken">Cancels the bake.</param>
    /// <returns>The room's lighting, bound to its compile.</returns>
    /// <exception cref="Diagnostics.MapCompileException">vrad refuses the room as it would refuse a map.</exception>
    /// <remarks>
    /// <para>
    /// The runs are one after another, each on the room's whole parallelism,
    /// so the bake is the same bytes at any thread count, as vrad is. Each
    /// run lights a copy of the room's BSP (its lumps are shared until vrad
    /// replaces one, and vrad replaces rather than writes into them), so the
    /// room's own compile is left as it was.
    /// </para>
    /// </remarks>
    public static async Task<RoomLighting> BakeAsync(
        RoomObject room,
        RoomLightingSettings settings,
        IContentFileSystem content,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parallelism);

        BspData source = room.Bsp;
        string name = room.Definition.Name;
        int faceCount = BspStructView.Count<DFace>(source[BspLump.Faces]);
        int leafCount = BspStructView.Count<DLeaf>(source[BspLump.Leafs]);
        bool sun = settings.Sun is not null;
        int turns = sun && HasSkyFace(source) ? 4 : 1;

        RoomLightingPayload[] payloads = new RoomLightingPayload[turns];
        Vec3[] normals = [];
        ushort[] normalIndices = [];
        uint mapFlags = 0;
        DWorldLight[]? skyLdr = null, skyHdr = null;
        for (int turn = 0; turn < turns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BspData lit = Copy(source);
            if (settings.Sun is { } library)
            {
                AddSun(lit, library);
            }

            List<(bool Hdr, StaticPropLightingResult Result)> props = [];
            VradContext context = new()
            {
                Options = settings.Options,
#pragma warning disable CA1308 // vbsp names a map's files in lower case; the bake reads the room's as the room compile named them
                MapName = name.ToLowerInvariant(),
#pragma warning restore CA1308
                Content = content,
                Parallelism = parallelism,
                FrameTurns = turn,
                StaticPropLightingObserver = (hdr, result) =>
                {
                    lock (props)
                    {
                        props.Add((hdr, result));
                    }
                },
            };

            _ = await Vrad.LightAsync(lit, context, cancellationToken).ConfigureAwait(false);
            RequireSameGeometry(name, source, lit);

            (RoomLightRange? ldr, DWorldLight[]? sunLdr) = ExtractRange(lit, hdr: false, faceCount, leafCount, props);
            (RoomLightRange? hdrRange, DWorldLight[]? sunHdr) = ExtractRange(lit, hdr: true, faceCount, leafCount, props);
            payloads[turn] = new RoomLightingPayload(ldr, hdrRange);
            if (turn == 0)
            {
                normals = BspStructView.As<Vec3>(lit[BspLump.VertNormals]).ToArray();
                normalIndices = BspStructView.As<ushort>(lit[BspLump.VertNormalIndices]).ToArray();
                mapFlags = lit[BspLump.MapFlags].Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(lit[BspLump.MapFlags].Data.Span) : 0u;
                skyLdr = sunLdr;
                skyHdr = sunHdr;
            }
        }

        IReadOnlyList<(int, LeafFlags)> skyLeaves = sun ? PassOne(source) : [];
        return new RoomLighting(faceCount, leafCount, normals, normalIndices, sun, skyLeaves, mapFlags, payloads, skyLdr, skyHdr, source);
    }

    /// <summary>A copy of a map whose lumps a vrad run may replace without touching the original's.</summary>
    internal static BspData Copy(BspData source)
    {
        BspData copy = new() { FileVersion = source.FileVersion, MapRevision = source.MapRevision };
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            copy[i] = source[i];
        }

        copy.GameLumps.AddRange(source.GameLumps);
        return copy;
    }

    /// <summary>
    /// Adds the library's sun to a room's entity lump right after the
    /// worldspawn, as the link and the flatten place it
    /// (<see cref="RoomLibraryEntities.ToLinked"/>: at the level's origin,
    /// never turned), so vrad makes its lights in the order a full compile
    /// of a level makes them.
    /// </summary>
    internal static void AddSun(BspData bsp, VmfChunk sun)
    {
        List<BspEntity> entities = EntityLump.Parse(bsp[BspLump.Entities]);
        entities.Insert(Math.Min(1, entities.Count), RoomLibraryEntities.ToLinked(sun));
        bsp[BspLump.Entities] = EntityLump.Write(entities);
    }

    /// <summary>
    /// Refuses a bake that changed anything of the room's geometry: the link
    /// carries the geometry from the compile and only the lighting from the
    /// bake, so the two must describe the same faces.
    /// </summary>
    private static void RequireSameGeometry(string room, BspData compiled, BspData lit)
    {
        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.TexInfo, BspLump.Planes, BspLump.Vertexes, BspLump.Leafs])
        {
            if (lump != BspLump.Leafs && !compiled[lump].Data.Span.SequenceEqual(lit[lump].Data.Span))
            {
                throw new InvalidOperationException($"room {room}'s bake changed its {lump} lump, which only its compile may write");
            }
        }

        ReadOnlySpan<DFace> before = BspStructView.As<DFace>(compiled[BspLump.Faces]);
        ReadOnlySpan<DFace> after = BspStructView.As<DFace>(lit[BspLump.Faces]);
        if (before.Length != after.Length)
        {
            throw new InvalidOperationException($"room {room}'s bake has {after.Length} faces; its compile has {before.Length}");
        }

        for (int f = 0; f < before.Length; f++)
        {
            DFace a = before[f];
            DFace b = after[f];
            a.Styles = default;
            b.Styles = default;
            a.LightOfs = 0;
            b.LightOfs = 0;
            if (!MemoryMarshal.AsBytes(new ReadOnlySpan<DFace>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<DFace>(in b))))
            {
                throw new InvalidOperationException($"room {room}'s bake changed face {f} beyond its styles and lightmap offset");
            }
        }
    }

    /// <summary>One range's data out of a lit map, and the sun's world lights of that range; nulls when the run did not light the range.</summary>
    private static (RoomLightRange? Range, DWorldLight[]? Sun) ExtractRange(
        BspData lit, bool hdr, int faceCount, int leafCount, List<(bool Hdr, StaticPropLightingResult Result)> props)
    {
        BspLumpData faces = lit[hdr ? BspLump.FacesHdr : BspLump.Faces];
        BspLumpData lighting = lit[hdr ? BspLump.LightingHdr : BspLump.Lighting];
        if (hdr && faces.IsEmpty)
        {
            return (null, null);
        }

        // An LDR pass writes the LDR lighting lump; a map lit only in HDR
        // keeps its compile's empty one, and has no LDR range.
        if (!hdr && lighting.IsEmpty && !lit[BspLump.FacesHdr].IsEmpty)
        {
            return (null, null);
        }

        ReadOnlySpan<DFace> faceStructs = BspStructView.As<DFace>(faces);
        byte[] styles = new byte[faceCount * 4];
        int[] offsets = new int[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 4; k++)
            {
                styles[(f * 4) + k] = faceStructs[f].Styles[k];
            }

            offsets[f] = faceStructs[f].LightOfs;
        }

        Half[] luxels = DecodeColors(MemoryMarshal.Cast<byte, ColorRgbExp32>(lighting.Data.Span));

        List<DWorldLight> own = [];
        List<DWorldLight> sun = [];
        foreach (DWorldLight light in BspStructView.As<DWorldLight>(lit[hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights]))
        {
            (light.Type is (int)EmitType.SkyLight or (int)EmitType.SkyAmbient ? sun : own).Add(light);
        }

        DLeafAmbientIndex[] index = BspStructView.As<DLeafAmbientIndex>(lit[hdr ? BspLump.LeafAmbientIndexHdr : BspLump.LeafAmbientIndex]).ToArray();
        if (index.Length != 0 && index.Length != leafCount)
        {
            throw new InvalidOperationException($"the bake wrote {index.Length} leaf ambient entries for {leafCount} leaves");
        }

        ReadOnlySpan<DLeafAmbientLighting> samples = BspStructView.As<DLeafAmbientLighting>(lit[hdr ? BspLump.LeafAmbientLightingHdr : BspLump.LeafAmbientLighting]);
        Half[] cubes = new Half[samples.Length * AmbientHalves];
        byte[] positions = new byte[samples.Length * 4];
        for (int s = 0; s < samples.Length; s++)
        {
            for (int side = 0; side < 6; side++)
            {
                Vec3 c = samples[s].Cube.Color[side].ToLinear();
                int at = (s * AmbientHalves) + (side * 3);
                cubes[at] = (Half)c.X;
                cubes[at + 1] = (Half)c.Y;
                cubes[at + 2] = (Half)c.Z;
            }

            positions[s * 4] = samples[s].X;
            positions[(s * 4) + 1] = samples[s].Y;
            positions[(s * 4) + 2] = samples[s].Z;
            positions[(s * 4) + 3] = samples[s].Pad;
        }

        List<RoomPropColors> propColors = [];
        foreach ((bool passHdr, StaticPropLightingResult result) in props)
        {
            if (passHdr != hdr)
            {
                continue;
            }

            foreach (StaticPropVhvFile file in result.Files)
            {
                if (file.Colors is not { } colors)
                {
                    continue;
                }

                List<Vec3> all = [];
                foreach ((_, Vec3[] c) in colors.Meshes)
                {
                    all.AddRange(c);
                }

                // The colours as vrad's encode leaves them: the exact value
                // of the ColorRGBExp32 each becomes, so the link's encode of
                // the half gives the same byte (EncodeVhv reads the encode).
                Half[] halves = new Half[all.Count * 3];
                for (int v = 0; v < all.Count; v++)
                {
                    Vec3 e = StockLightColor.Encode(all[v]).ToLinear();
                    halves[v * 3] = (Half)e.X;
                    halves[(v * 3) + 1] = (Half)e.Y;
                    halves[(v * 3) + 2] = (Half)e.Z;
                }

                propColors.Add(new RoomPropColors(
                    file.PropIndex, colors.Checksum, [.. colors.Meshes.Select(m => m.Lod)], [.. colors.Meshes.Select(m => m.Colors.Length)], halves));
            }
        }

        propColors.Sort((a, b) => a.Prop.CompareTo(b.Prop));
        RoomLightRange range = new(styles, offsets, luxels, [.. own], index, cubes, positions, [.. propColors]);
        return (range, sun.Count == 0 ? null : [.. sun]);
    }

    /// <summary>Halves per ambient sample: six faces of three channels.</summary>
    internal const int AmbientHalves = 18;

    /// <summary>
    /// Luxels as the half floats of their exact linear values (three a luxel).
    /// </summary>
    internal static Half[] DecodeColors(ReadOnlySpan<ColorRgbExp32> colors)
    {
        Half[] halves = new Half[colors.Length * 3];
        for (int i = 0; i < colors.Length; i++)
        {
            Vec3 c = colors[i].ToLinear();
            halves[i * 3] = (Half)c.X;
            halves[(i * 3) + 1] = (Half)c.Y;
            halves[(i * 3) + 2] = (Half)c.Z;
        }

        return halves;
    }

    /// <summary>
    /// The one encode at link (9.3): linear half floats, three a colour, back
    /// to <c>ColorRGBExp32</c> as vrad encodes (<see cref="StockLightColor.Encode"/>).
    /// </summary>
    internal static void EncodeColors(ReadOnlySpan<Half> halves, Span<ColorRgbExp32> colors)
    {
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = StockLightColor.Encode(new Vec3((float)halves[i * 3], (float)halves[(i * 3) + 1], (float)halves[(i * 3) + 2]));
        }
    }

    // ---- the section -------------------------------------------------------

    /// <summary>The room's lighting as its pack section.</summary>
    public RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(Payloads.Length);
        foreach (RoomLightingPayload payload in Payloads)
        {
            RoomLinkSections.Writer p = new();
            p.Byte((byte)((payload.Ldr is null ? 0 : 1) | (payload.Hdr is null ? 0 : 2)));
            foreach (RoomLightRange? range in (ReadOnlySpan<RoomLightRange?>)[payload.Ldr, payload.Hdr])
            {
                if (range is not null)
                {
                    WriteRange(p, range);
                }
            }

            byte[] bytes = p.ToArray();
            w.Int(bytes.Length);
            w.Raw(bytes);
        }

        w.Int(FaceCount);
        w.Int(LeafCount);
        w.Structs<Vec3>(VertNormals);
        w.Structs<ushort>(VertNormalIndices);
        w.Byte(HasSun ? (byte)1 : (byte)0);
        w.Int(SkyLeaves.Count);
        foreach ((int leaf, LeafFlags flags) in SkyLeaves)
        {
            w.Int(leaf);
            w.Byte((byte)flags);
        }

        w.Int(unchecked((int)MapFlags));
        foreach (DWorldLight[]? sky in (ReadOnlySpan<DWorldLight[]?>)[SkyLdr, SkyHdr])
        {
            w.Structs<DWorldLight>(sky ?? []);
        }

        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    private static void WriteRange(RoomLinkSections.Writer w, RoomLightRange range)
    {
        w.Int(range.LightOffsets.Length);
        w.Raw(range.Styles);
        w.Structs<int>(range.LightOffsets, counted: false);
        w.Int(range.Luxels.Length / 3);
        w.Structs<Half>(range.Luxels, counted: false);
        w.Structs<DWorldLight>(range.Lights);
        w.Structs<DLeafAmbientIndex>(range.AmbientIndex);
        w.Int(range.AmbientPositions.Length / 4);
        w.Structs<Half>(range.AmbientCubes, counted: false);
        w.Raw(range.AmbientPositions);
        w.Int(range.Props.Length);
        foreach (RoomPropColors prop in range.Props)
        {
            w.Int(prop.Prop);
            w.Int(prop.Checksum);
            w.Ints(prop.Lods);
            w.Ints(prop.Counts);
            w.Structs<Half>(prop.Colors, counted: false);
        }
    }

    /// <summary>
    /// A room's lighting read back from its pack section and bound to the
    /// room's compile, or null when the room has none (a pack built without
    /// lighting, or a section of a revision this build does not read).
    /// </summary>
    /// <exception cref="LinkException">The section is damaged or does not fit the room's compile.</exception>
    public static RoomLighting? Read(ArraySegment<byte>? section, RoomDefinition definition, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bsp);
        string room = definition.Name;
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        int faceCount = BspStructView.Count<DFace>(bsp[BspLump.Faces]);
        int leafCount = BspStructView.Count<DLeaf>(bsp[BspLump.Leafs]);
        int turns = r.Int();
        if (turns is not (1 or 4))
        {
            throw r.Mismatch($"a rotation count of {turns}; a section stores 1 or 4");
        }

        RoomLightingPayload[] payloads = new RoomLightingPayload[turns];
        for (int t = 0; t < turns; t++)
        {
            // The length lets a later reader skip the turns it does not
            // place; this one reads them all, and holds each to its length.
            int length = r.Count("payload bytes");
            int before = r.Position;
            int flags = r.Small(3, "a range flag of");
            RoomLightRange? ldr = (flags & 1) != 0 ? ReadRange(r, faceCount, leafCount) : null;
            RoomLightRange? hdr = (flags & 2) != 0 ? ReadRange(r, faceCount, leafCount) : null;
            if (r.Position - before != length)
            {
                throw r.Mismatch($"a payload of {r.Position - before} bytes that records {length}");
            }

            payloads[t] = new RoomLightingPayload(ldr, hdr);
        }

        Expect(r, "faces", faceCount);
        Expect(r, "leaves", leafCount);
        Vec3[] normals = r.Structs<Vec3>("vertex normals", r.Count("vertex normals"), counted: false);
        ushort[] normalIndices = r.Structs<ushort>("vertex normal indices", r.Count("vertex normal indices"), counted: false);
        foreach (ushort index in normalIndices)
        {
            if (index >= normals.Length)
            {
                throw r.Mismatch($"a vertex normal index of {index}; it holds {normals.Length} normals");
            }
        }

        bool sun = r.Flag();
        int skyCount = r.Count("sky leaves");
        List<(int, LeafFlags)> skyLeaves = new(skyCount);
        for (int i = 0; i < skyCount; i++)
        {
            int leaf = r.Int();
            int flag = r.Small((int)LeafFlags.Sky2D, "a sky leaf flag of");
            if ((uint)leaf >= (uint)leafCount || flag is not ((int)LeafFlags.Sky or (int)LeafFlags.Sky2D))
            {
                throw r.Mismatch($"sky leaf {leaf} flagged {flag}; the room has {leafCount} leaves");
            }

            skyLeaves.Add((leaf, (LeafFlags)flag));
        }

        uint mapFlags = unchecked((uint)r.Int());
        DWorldLight[] skyLdr = r.Structs<DWorldLight>("sun lights", r.Count("sun lights"), counted: false);
        DWorldLight[] skyHdr = r.Structs<DWorldLight>("sun lights", r.Count("sun lights"), counted: false);
        r.End();
        return new RoomLighting(
            faceCount, leafCount, normals, normalIndices, sun, skyLeaves, mapFlags, payloads,
            skyLdr.Length == 0 ? null : skyLdr, skyHdr.Length == 0 ? null : skyHdr, bsp);
    }

    private static void Expect(RoomLinkSections.Reader r, string what, int expected)
    {
        int count = r.Int();
        if (count != expected)
        {
            throw r.Mismatch($"{count} {what}; the room has {expected}");
        }
    }

    private static RoomLightRange ReadRange(RoomLinkSections.Reader r, int faceCount, int leafCount)
    {
        Expect(r, "faces", faceCount);
        byte[] styles = r.Structs<byte>("styles", faceCount * 4, counted: false);
        int[] offsets = r.Structs<int>("lightmap offsets", faceCount, counted: false);
        int luxelCount = r.Count("luxels");
        Half[] luxels = r.Structs<Half>("luxels", luxelCount * 3, counted: false);
        foreach (int offset in offsets)
        {
            if (offset != -1 && (offset < 0 || offset % LuxelBytes != 0 || offset / LuxelBytes > luxelCount))
            {
                throw r.Mismatch($"a lightmap offset of {offset}; it holds {luxelCount} luxels");
            }
        }

        DWorldLight[] lights = r.Structs<DWorldLight>("world lights", r.Count("world lights"), counted: false);
        int indexCount = r.Int();
        if (indexCount != 0 && indexCount != leafCount)
        {
            throw r.Mismatch($"{indexCount} leaf ambient entries; the room has {leafCount} leaves");
        }

        DLeafAmbientIndex[] index = r.Structs<DLeafAmbientIndex>("leaf ambient entries", indexCount, counted: false);
        int samples = r.Count("ambient samples");
        Half[] cubes = r.Structs<Half>("ambient cubes", samples * AmbientHalves, counted: false);
        byte[] positions = r.Structs<byte>("ambient positions", samples * 4, counted: false);
        foreach (DLeafAmbientIndex entry in index)
        {
            if (entry.FirstAmbientSample + entry.AmbientSampleCount > samples)
            {
                throw r.Mismatch($"a leaf's samples {entry.FirstAmbientSample} to {entry.FirstAmbientSample + entry.AmbientSampleCount}; it holds {samples}");
            }
        }

        int propCount = r.Count("lit props");
        RoomPropColors[] props = new RoomPropColors[propCount];
        for (int i = 0; i < propCount; i++)
        {
            int prop = r.Int();
            int checksum = r.Int();
            int[] lods = r.Ints("strip group LODs", 8);
            int[] counts = r.Ints("strip group vertex counts", int.MaxValue);
            if (counts.Length != lods.Length)
            {
                throw r.Mismatch($"{lods.Length} strip group LODs for {counts.Length} vertex counts");
            }

            long vertices = counts.Sum(c => (long)c);
            if (vertices > int.MaxValue / 3)
            {
                throw r.Mismatch($"a prop of {vertices} vertices");
            }

            props[i] = new RoomPropColors(prop, checksum, lods, counts, r.Structs<Half>("vertex colours", (int)vertices * 3, counted: false));
        }

        return new RoomLightRange(styles, offsets, luxels, lights, index, cubes, positions, props);
    }
}

/// <summary>One stored turn of a room's lighting: its LDR and HDR ranges, each null when the bake did not light it.</summary>
/// <param name="Ldr">The LDR range, or null.</param>
/// <param name="Hdr">The HDR range, or null.</param>
internal sealed record RoomLightingPayload(RoomLightRange? Ldr, RoomLightRange? Hdr)
{
    /// <summary>The range of one pass.</summary>
    public RoomLightRange? Range(bool hdr) => hdr ? Hdr : Ldr;
}

/// <summary>What one vrad pass gave a room, in its own frame.</summary>
/// <param name="Styles">Four style bytes a face, in face order (255 unused).</param>
/// <param name="LightOffsets">Each face's lightmap offset in bytes of the encoded lump, or -1.</param>
/// <param name="Luxels">The lighting lump's colours as linear half floats, three a colour, in lump order.</param>
/// <param name="Lights">The room's own world lights in lump order (entity lights, then surface lights), the sun's left out.</param>
/// <param name="AmbientIndex">One entry per leaf: its samples' count and first.</param>
/// <param name="AmbientCubes">Per sample, its cube's six faces as linear half floats (+x, -x, +y, -y, +z, -z).</param>
/// <param name="AmbientPositions">Per sample, its position in its leaf's box (three bytes) and the pad byte.</param>
/// <param name="Props">The props vrad lit, by prop index.</param>
internal sealed record RoomLightRange(
    byte[] Styles,
    int[] LightOffsets,
    Half[] Luxels,
    DWorldLight[] Lights,
    DLeafAmbientIndex[] AmbientIndex,
    Half[] AmbientCubes,
    byte[] AmbientPositions,
    RoomPropColors[] Props);

/// <summary>One static prop's vertex colours as vrad lit them, before its <c>.vhv</c> is encoded.</summary>
/// <param name="Prop">The prop's index in the room's lump.</param>
/// <param name="Checksum">The model's studio checksum, which the file carries.</param>
/// <param name="Lods">Each strip group's LOD.</param>
/// <param name="Counts">Each strip group's vertex count.</param>
/// <param name="Colors">Every vertex's linear colour, three halves each, strip group after strip group.</param>
internal sealed record RoomPropColors(int Prop, int Checksum, int[] Lods, int[] Counts, Half[] Colors);
