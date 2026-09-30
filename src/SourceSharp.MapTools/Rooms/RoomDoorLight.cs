//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.MapTools.Tracing;

using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Which cells of an opening each of a set of receivers sees: a code a
/// receiver (0 none, 1 every cell, 2 some) and, for each receiver that sees
/// some, in order, its cells (<see cref="DoorLightMath.Cell"/>).
/// </summary>
/// <param name="Codes">One code a receiver.</param>
/// <param name="Partial">The cells of each receiver whose code is 2, in receiver order.</param>
internal sealed record DoorSeen(byte[] Codes, UInt128[] Partial)
{
    /// <summary>No receiver sees the opening.</summary>
    public bool None => Codes.All(c => c == 0);

    /// <summary>
    /// Each receiver's cells, walking the codes: the mask of receiver
    /// <c>i</c> is every cell for code 1, the next partial mask for code 2,
    /// and none for code 0.
    /// </summary>
    public UInt128[] Masks()
    {
        UInt128[] masks = new UInt128[Codes.Length];
        int partial = 0;
        for (int i = 0; i < Codes.Length; i++)
        {
            masks[i] = Codes[i] switch
            {
                1 => DoorLightMath.AllCells,
                2 => Partial[partial++],
                _ => UInt128.Zero,
            };
        }

        return masks;
    }

    /// <summary>The codes and partial masks of a list of masks.</summary>
    public static DoorSeen Of(ReadOnlySpan<UInt128> masks)
    {
        byte[] codes = new byte[masks.Length];
        List<UInt128> partial = [];
        for (int i = 0; i < masks.Length; i++)
        {
            if (masks[i] == UInt128.Zero)
            {
                continue;
            }

            if (masks[i] == DoorLightMath.AllCells)
            {
                codes[i] = 1;
            }
            else
            {
                codes[i] = 2;
                partial.Add(masks[i]);
            }
        }

        return new DoorSeen(codes, [.. partial]);
    }
}

/// <summary>One face's receivers for one socket: which cells of the opening each of its sample cells sees.</summary>
/// <param name="Face">The face.</param>
/// <param name="Seen">Per sample cell (<see cref="DoorFaceCells"/>), the opening's cells it sees.</param>
internal sealed record DoorReceiverFace(int Face, DoorSeen Seen);

/// <summary>What one response emitter changed on one face: its bounced light at half the lightmap's resolution.</summary>
/// <param name="Face">The face.</param>
/// <param name="Values">
/// Per page, per half-resolution luxel (<c>ceil(w/2)</c> by <c>ceil(h/2)</c>,
/// each the mean of the luxels it covers), three halves: bounced light per
/// unit of the emitter's intensity.
/// </param>
internal sealed record DoorResponseFace(int Face, Half[] Values);

/// <summary>One leaf ambient sample of a response run, room-local, and its cube per unit of the emitter's intensity.</summary>
/// <param name="Leaf">The sample's leaf.</param>
/// <param name="Position">Where it was taken, room-local.</param>
/// <param name="Cube">Its six faces (+x, -x, +y, -y, +z, -z), three halves each.</param>
internal sealed record DoorResponseSample(int Leaf, Vec3 Position, Half[] Cube);

/// <summary>One static prop's vertex colours in a response run, per unit of the emitter's intensity.</summary>
/// <param name="Prop">The prop's index in the room's lump.</param>
/// <param name="Colours">Every vertex's colour, strip group after strip group as the bake stores them, three halves each.</param>
internal sealed record DoorResponseProp(int Prop, Half[] Colours);

/// <summary>A room's answer to one response emitter at one socket (<see cref="DoorLightMath.Node"/>).</summary>
/// <param name="Faces">The faces the emitter's light bounced onto.</param>
/// <param name="Ambient">The leaf ambient samples it lit.</param>
/// <param name="Props">The props it lit.</param>
internal sealed record DoorResponseEmitter(DoorResponseFace[] Faces, DoorResponseSample[] Ambient, DoorResponseProp[] Props);

/// <summary>
/// One range's door light for a room: what leaves by each socket (per
/// stored turn), which cells of each opening the room's leaf ambient
/// samples see (per stored turn, whose samples they are), and the room's
/// response to light entering by each socket.
/// </summary>
/// <param name="Captures">Per stored turn, per socket, the sources that reach the opening.</param>
/// <param name="Ambient">Per stored turn, per socket, which cells each of that turn's ambient samples sees.</param>
/// <param name="Responses">Per socket, the response emitters, <see cref="DoorLightMath.EmitterCount"/> or none.</param>
internal sealed record DoorLightRange(DoorSource[][][] Captures, DoorSeen[][] Ambient, DoorResponseEmitter[][] Responses);

/// <summary>
/// A room's door light (the rooms design, 9.1 parts 2 and 3, as PR 10
/// built it): what its lights, surfaces and sky send out through each of
/// its openings, and what light entering through each opening does to it,
/// so that the link can add to each room of a level the light its jointed
/// neighbours send through their doors (<see cref="LevelDoorLight"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is recorded.</b> The room is opened at its doorways
/// (<see cref="Open"/>: plug brushes cast no shadow, plug faces reflect
/// nothing, as the black box of 9.1) and:
/// </para>
/// <list type="bullet">
/// <item><b>Receivers</b>, per socket, once: for every sample cell of every
/// lit face (<see cref="DoorFaceCells"/>), which of the opening's cells it
/// sees, traced against the room's own shadow casters.</item>
/// <item><b>Capture</b>, per socket, per stored turn and range: the room
/// lit as the base bake lights it, but open, and from each opening cell's
/// centre (just inside) every light that reaches it and, along 256
/// directions into the room, the lightmap of whatever surface or sky each
/// ray meets (vrad's leaf ambient gather). The lights are kept as they are,
/// with the cells each reaches; what the rays saw is gathered into
/// stand-in point sources, one per 64-unit cube of the room it came from
/// (<see cref="StandInCell"/>), each as bright as the light it sends through
/// the opening.</item>
/// <item><b>Ambient receivers</b>, per socket, per stored turn and range:
/// which cells each of that turn's leaf ambient samples sees.</item>
/// <item><b>Responses</b>, per socket and range, once, for a room whose
/// surfaces reflect or whose props are lit: the room lit by nothing but one
/// point emitter on the <see cref="DoorLightMath.Node"/> grid behind the
/// opening (inverse-square and constant, sixteen runs): the light the
/// emitter's light bounced onto each face (not its direct light, which the
/// link evaluates), the room's leaf ambient samples and its props' colours,
/// per unit of the emitter's intensity.</item>
/// </list>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>DLIT</c>) has the link
/// sections' framing (codec none, revision 1): the sockets' receivers, then
/// a range flag byte and per range the captures and ambient receivers (per
/// stored turn, per socket) and the responses (per socket). An optional
/// section: a pack without it links lit rooms without door light, as PR 9
/// did.
/// </para>
/// </remarks>
internal sealed partial class RoomDoorLight
{
    /// <summary>The tag of a room's door light section.</summary>
    public const string SectionTag = "DLIT";

    /// <summary>
    /// What the door light's arithmetic is, for the pack id and the
    /// incremental cache's key: a change to what is recorded or how raises
    /// it, so a cached room is recorded afresh.
    /// </summary>
    public const int Revision = 1;

    /// <summary>How far inside a room a capture's sample points sit, off the opening's plane.</summary>
    internal const float Inset = 0.5f;

    /// <summary>How far past the opening's plane a receiver's rays aim, into the doorway.</summary>
    internal const float Beyond = 1f;

    /// <summary>
    /// The edge of the cubes a room's surfaces and sky are gathered into
    /// stand-in sources by: the sweep found 32 no better and 128 a little
    /// worse near the joint, and the count, and so the link's work, falls
    /// with the cube.
    /// </summary>
    internal const float StandInCell = 64f;

    /// <summary>
    /// Directions a capture traces from each opening cell: 64 measured
    /// within 2% of 256 on the reflective levels; 256 is what the pack
    /// takes, since the rays are cheap next to the vrad runs.
    /// </summary>
    internal const int CaptureRays = 256;

    /// <summary>
    /// A response value below this fraction of the emitter's brightest is
    /// not stored: the sweep found no difference down to 5%, since bounced
    /// light is smooth; this keeps only the zeros out.
    /// </summary>
    internal const float Prune = 1e-3f;

    private readonly BspData? _bsp;

    internal RoomDoorLight(DoorReceiverFace[][] receivers, DoorLightRange? ldr, DoorLightRange? hdr, BspData? bsp)
    {
        Receivers = receivers;
        Ldr = ldr;
        Hdr = hdr;
        _bsp = bsp;
    }

    /// <summary>Per socket (the definition's order), the faces that see its opening.</summary>
    public DoorReceiverFace[][] Receivers { get; }

    /// <summary>The LDR range, or null.</summary>
    public DoorLightRange? Ldr { get; }

    /// <summary>The HDR range, or null.</summary>
    public DoorLightRange? Hdr { get; }

    /// <summary>One range.</summary>
    public DoorLightRange? Range(bool hdr) => hdr ? Hdr : Ldr;

    /// <summary>Whether this is the door light of <paramref name="room"/>'s own compile.</summary>
    public bool IsFor(RoomObject room) => _bsp is not null && ReferenceEquals(_bsp, room.Bsp);

    /// <summary>The same door light bound to another BSP of the same room (a pack's read-back container).</summary>
    internal RoomDoorLight BoundTo(BspData bsp) => new(Receivers, Ldr, Hdr, bsp);

    // ---- the room opened ---------------------------------------------------------------------------

    /// <summary>
    /// The room with its doorways open onto nothing: every socket's plug
    /// brushes cast no shadow (their contents cleared), and the plug faces,
    /// which the tree still holds, reflect nothing (their texinfo points at a
    /// copy of their texdata with zero reflectivity). Light leaving through
    /// a doorway is lost, as into the black, fully absorbing box of 9.1, and
    /// light entering through one meets no plug. Faces, their order and
    /// every other face's texinfo are the compile's.
    /// </summary>
    internal static BspData Open(RoomObject room, RoomLinkShared census)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(census);
        BspData source = room.Bsp;
        BspData open = RoomLighting.Copy(source);

        byte[] brushes = source[BspLump.Brushes].Data.ToArray();
        Span<DBrush> brushStructs = MemoryMarshal.Cast<byte, DBrush>(brushes.AsSpan());
        SortedSet<int> plugFaces = [];
        foreach (SocketCensus socket in census.Sockets)
        {
            foreach (int b in socket.StrippedBrushes)
            {
                brushStructs[b].Contents = 0;
            }

            plugFaces.UnionWith(socket.StrippedFaces);
        }

        open.SetLump(BspLump.Brushes, brushes, source[BspLump.Brushes].Version);

        List<DTexData> texData = [.. BspStructView.As<DTexData>(source[BspLump.TexData])];
        List<TexInfo> texInfo = [.. BspStructView.As<TexInfo>(source[BspLump.TexInfo])];
        byte[] faces = source[BspLump.Faces].Data.ToArray();
        Span<DFace> faceStructs = MemoryMarshal.Cast<byte, DFace>(faces.AsSpan());
        Dictionary<int, int> dark = [];
        foreach (int f in plugFaces)
        {
            int old = faceStructs[f].TexInfo;
            if (!dark.TryGetValue(old, out int copy))
            {
                DTexData data = texData[texInfo[old].TexData];
                data.Reflectivity = Vec3.Zero;
                texData.Add(data);
                TexInfo info = texInfo[old];
                info.TexData = texData.Count - 1;
                texInfo.Add(info);
                copy = texInfo.Count - 1;
                dark[old] = copy;
            }

            faceStructs[f].TexInfo = (short)copy;
        }

        open.SetLump(BspLump.TexData, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(texData)).ToArray(), source[BspLump.TexData].Version);
        open.SetLump(BspLump.TexInfo, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(texInfo)).ToArray(), source[BspLump.TexInfo].Version);
        open.SetLump(BspLump.Faces, faces, source[BspLump.Faces].Version);
        return open;
    }

    /// <summary>A room's entities with every light taken out (the sun included).</summary>
    internal static List<BspEntity> Unlit(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities]).Where(e => !e.ClassName.StartsWith("light", StringComparison.Ordinal))];

    /// <summary>What one vrad run of an open room gave: the lit copy, its props' lighting, and each face's bounced light per range (LDR, HDR).</summary>
    /// <param name="Lit">The lit copy.</param>
    /// <param name="Props">Each pass's static prop lighting.</param>
    /// <param name="Bounce">Per range, per face, its bounced light: per page and luxel, three floats; null where none was added.</param>
    internal sealed record OpenRun(BspData Lit, List<(bool Hdr, StaticPropLightingResult Result)> Props, float[]?[][] Bounce);

    /// <summary>
    /// One vrad run of an open room with the given entities, the room's own
    /// map left as it was; with the bounced light of every luxel kept apart
    /// per range (<see cref="VradContext.BounceObserver"/>).
    /// </summary>
    internal static async Task<OpenRun> LightAsync(
        BspData open,
        string mapName,
        IReadOnlyList<BspEntity> entities,
        RoomLightingSettings settings,
        IContentFileSystem content,
        CompileParallelism parallelism,
        ITransferCache? transfers,
        bool noTextureLights,
        int turn,
        CancellationToken cancellationToken)
    {
        BspData lit = RoomLighting.Copy(open);
        lit[BspLump.Entities] = EntityLump.Write([.. entities]);
        List<(bool, StaticPropLightingResult)> props = [];
        ReadOnlySpan<DFace> faceStructs = BspStructView.As<DFace>(open[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(open[BspLump.TexInfo]);
        float[]?[][] bounce = [new float[faceStructs.Length][], new float[faceStructs.Length][]];
        int[] pageLuxels = new int[faceStructs.Length];
        int[] pages = new int[faceStructs.Length];
        for (int f = 0; f < faceStructs.Length; f++)
        {
            pageLuxels[f] = PageLuxels(faceStructs[f]);
            pages[f] = Pages(texInfos, faceStructs[f]);
        }

        VradContext context = new()
        {
            Options = settings.Options,
#pragma warning disable CA1308 // the room compile names a room's files in lower case
            MapName = mapName.ToLowerInvariant(),
#pragma warning restore CA1308
            Content = content,
            Parallelism = parallelism,
            FrameTurns = turn,
            TransferCache = transfers,
            NoTextureLights = noTextureLights,
            BounceObserver = (hdr, face, bump, luxel, light) =>
            {
                // Each face is finished on one worker: its array is its own.
                float[] values = bounce[hdr ? 1 : 0][face] ??= new float[pages[face] * pageLuxels[face] * 3];
                int at = ((bump * pageLuxels[face]) + luxel) * 3;
                values[at] = light.X;
                values[at + 1] = light.Y;
                values[at + 2] = light.Z;
            },
            StaticPropLightingObserver = (hdr, result) =>
            {
                lock (props)
                {
                    props.Add((hdr, result));
                }
            },
        };

        _ = await Vrad.LightAsync(lit, context, cancellationToken).ConfigureAwait(false);
        return new OpenRun(lit, props, bounce);
    }

    /// <summary>How many lightmap pages a face's style holds: four on a bumped face, else one.</summary>
    internal static int Pages(ReadOnlySpan<TexInfo> texInfos, DFace face) =>
        (texInfos[face.TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0 ? 4 : 1;

    /// <summary>Luxels in one page of a face.</summary>
    internal static int PageLuxels(DFace face) =>
        (face.LightmapTextureSizeInLuxels[0] + 1) * (face.LightmapTextureSizeInLuxels[1] + 1);

    /// <summary>A face's half-resolution size: <c>ceil(w/2)</c> by <c>ceil(h/2)</c>.</summary>
    internal static (int W, int H) Half(int w, int h) => ((w + 1) / 2, (h + 1) / 2);

    // ---- the bake ----------------------------------------------------------------------------------

    /// <summary>
    /// Records a lit room's door light: its receivers, its captures for every
    /// stored turn of its lighting, and, when its surfaces reflect or its
    /// props are lit, its responses.
    /// </summary>
    /// <param name="room">The compiled room.</param>
    /// <param name="lighting">Its base lighting (<see cref="RoomLighting.BakeAsync"/>): the turns and the ambient samples.</param>
    /// <param name="settings">The switches and the sun it was lit with.</param>
    /// <param name="content">The game content vrad reads.</param>
    /// <param name="parallelism">How much of the machine each run may use.</param>
    /// <param name="cancellationToken">Cancels the bake.</param>
    /// <returns>The door light, bound to the room's compile.</returns>
    /// <remarks>
    /// Every vrad run is one after another on the room's whole parallelism,
    /// and every trace batch is answered bit for bit whatever the thread
    /// count, so the bytes are the same at any thread count. What the runs
    /// share (the bounce's transfers) is held by this call alone and dropped
    /// with it.
    /// </remarks>
    public static async Task<RoomDoorLight> BakeAsync(
        RoomObject room,
        RoomLighting lighting,
        RoomLightingSettings settings,
        IContentFileSystem content,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(lighting);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parallelism);

        RoomDefinition definition = room.Definition;
        RoomLinkShared census = LevelLinker.ComputeShared(room);
        BspData open = Open(room, census);
        DoorFrame[] frames = [.. definition.Sockets.Select(s => DoorFrame.Of(definition, s))];
        ShadowCasterLoadReport casters = await ShadowCasterLoader.LoadAsync(
            open, settings.Options, content, NullPropCollisionSource.Instance, cancellationToken: cancellationToken).ConfigureAwait(false);
        IRayTracer tracer = casters.Set.Count == 0 ? new EmptySceneTracer() : casters.Set.BuildTracer(settings.Options.Compliance);

        DoorFaceCells?[] cells = FaceCellsOf(room.Bsp);
        DoorReceiverFace[][] receivers = new DoorReceiverFace[frames.Length][];
        for (int s = 0; s < frames.Length; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receivers[s] = await ReceiversAsync(tracer, cells, frames[s], cancellationToken).ConfigureAwait(false);
        }

        DLeaf[] leaves = AmbientScene.ReadLeaves(room.Bsp);
        bool ldr = lighting.Payloads[0].Ldr is not null, hdr = lighting.Payloads[0].Hdr is not null;
        int turns = lighting.RotationCount;
        DoorSource[][][][] captures = [new DoorSource[turns][][], new DoorSource[turns][][]];
        DoorSeen[][][] ambient = [new DoorSeen[turns][], new DoorSeen[turns][]];
        for (int turn = 0; turn < turns; turn++)
        {
            List<BspEntity> entities = EntityLump.Parse(room.Bsp[BspLump.Entities]);
            if (settings.Sun is { } sun)
            {
                entities.Insert(Math.Min(1, entities.Count), RoomLibraryEntities.ToLinked(sun));
            }

            OpenRun run = await LightAsync(
                open, definition.Name, entities, settings, content, parallelism, transfers: null, noTextureLights: false, turn, cancellationToken)
                .ConfigureAwait(false);
            for (int r = 0; r < 2; r++)
            {
                if (lighting.Payloads[turn].Range(r == 1) is not { } range)
                {
                    continue;
                }

                captures[r][turn] = new DoorSource[frames.Length][];
                ambient[r][turn] = new DoorSeen[frames.Length];
                (Vec3, Vec3)[] samples = [.. SamplePositions(range, leaves).Select(p => (p.Position, Vec3.Zero))];
                for (int s = 0; s < frames.Length; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    captures[r][turn][s] = Capture(run.Lit, r == 1, frames[s], definition.CellSize);
                    ambient[r][turn][s] = await SeenAsync(tracer, samples, frames[s], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        bool respond = NeedsResponses(room, settings);
        DoorResponseEmitter[][][] responses = [new DoorResponseEmitter[frames.Length][], new DoorResponseEmitter[frames.Length][]];
        MemoryTransferCache transfers = new();
        for (int s = 0; s < frames.Length; s++)
        {
            responses[0][s] = new DoorResponseEmitter[respond && ldr ? DoorLightMath.EmitterCount : 0];
            responses[1][s] = new DoorResponseEmitter[respond && hdr ? DoorLightMath.EmitterCount : 0];
            if (!respond)
            {
                continue;
            }

            for (int e = 0; e < DoorLightMath.EmitterCount; e++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (DoorResponseEmitter? l, DoorResponseEmitter? h) = await RespondAsync(
                    room, open, frames[s], e, settings, content, parallelism, transfers, 0, cancellationToken).ConfigureAwait(false);
                if (ldr)
                {
                    responses[0][s][e] = l ?? throw new InvalidOperationException($"room {definition.Name}'s response run lit no LDR range");
                }

                if (hdr)
                {
                    responses[1][s][e] = h ?? throw new InvalidOperationException($"room {definition.Name}'s response run lit no HDR range");
                }
            }
        }

        return new RoomDoorLight(
            receivers,
            ldr ? new DoorLightRange(captures[0], ambient[0], responses[0]) : null,
            hdr ? new DoorLightRange(captures[1], ambient[1], responses[1]) : null,
            room.Bsp);
    }

    /// <summary>
    /// Whether a room needs responses: it lights static props, or vrad
    /// bounces light (<c>-bounce</c> above zero) and some lit face reflects.
    /// A room of surfaces that reflect nothing (every material without a
    /// texture, as the samples' are) sends none of the light entering it
    /// anywhere, and the link evaluates the direct light itself.
    /// </summary>
    internal static bool NeedsResponses(RoomObject room, RoomLightingSettings settings)
    {
        BspData bsp = room.Bsp;
        if (settings.Options.StaticPropLighting && RoomStaticProps.ReadLump(bsp) is { } props && props.Props.Count > 0)
        {
            return true;
        }

        if (settings.Options.Bounces <= 0)
        {
            return false;
        }

        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        ReadOnlySpan<DTexData> texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]);
        foreach (DFace face in BspStructView.As<DFace>(bsp[BspLump.Faces]))
        {
            TexInfo tex = texInfos[face.TexInfo];
            if ((tex.Flags & (int)(SurfaceFlags.Sky | SurfaceFlags.Sky2D | SurfaceFlags.NoDraw)) == 0
                && tex.TexData >= 0 && tex.TexData < texData.Length && texData[tex.TexData].Reflectivity != Vec3.Zero)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every face's cells (<see cref="DoorLightMath.FaceCells"/>), null for a
    /// face vrad does not light; a displacement's laid on its displaced
    /// surface (<see cref="DoorFaceCells.OnSurface"/>).
    /// </summary>
    /// <remarks>
    /// The pack records which cells of each opening a face's cells see, and
    /// the link evaluates light at the same cells, so both build them here
    /// from the room's own lumps. The surfaces are built as vrad builds them
    /// from the lumps (<see cref="Disp.DispLightingLoader"/>), with the
    /// correct arithmetic whatever the compile's compliance: they place the
    /// door light's receivers, which the pack and the link must agree on,
    /// not the room's own bake.
    /// </remarks>
    internal static DoorFaceCells?[] FaceCellsOf(BspData bsp)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(bsp[BspLump.Faces]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]);
        DoorFaceCells?[] cells = new DoorFaceCells?[faces.Length];
        Disp.CoreDispInfo[]? cores = null;
        Rad.Light.DirectLightingSettings? settings = null;
        for (int f = 0; f < faces.Length; f++)
        {
            cells[f] = DoorLightMath.FaceCells(faces, planes, texInfos, f);
            int disp = faces[f].DispInfo;
            if (cells[f] is { } flat && disp >= 0)
            {
                cores ??= Disp.DispLightingLoader.Load(bsp, ComplianceOptions.Correct);
                settings ??= Rad.Light.DirectLightingSettings.FromVrad(VradOptions.Default, hdr: false);
                if (disp < cores.Length)
                {
                    TexInfo tex = texInfos[faces[f].TexInfo];
                    cells[f] = flat.OnSurface(
                        Rad.Displacement.VradDispSurface.Create(cores[disp], tex, settings, stockNormalise: false), tex);
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// A range's leaf ambient samples, in the range's own order, each with its
    /// leaf and its room-local position (the position bytes are fractions of
    /// the leaf's box, 255 its far side).
    /// </summary>
    internal static (int Leaf, Vec3 Position)[] SamplePositions(RoomLightRange range, DLeaf[] leaves)
    {
        (int, Vec3)[] samples = new (int, Vec3)[range.AmbientPositions.Length / 4];
        for (int l = 0; l < range.AmbientIndex.Length && l < leaves.Length; l++)
        {
            DLeafAmbientIndex entry = range.AmbientIndex[l];
            Vec3 mins = new(leaves[l].Mins[0], leaves[l].Mins[1], leaves[l].Mins[2]);
            Vec3 size = new Vec3(leaves[l].Maxs[0], leaves[l].Maxs[1], leaves[l].Maxs[2]) - mins;
            for (int i = 0; i < entry.AmbientSampleCount; i++)
            {
                int s = entry.FirstAmbientSample + i;
                samples[s] = (l, mins + new Vec3(
                    size.X * range.AmbientPositions[s * 4] / 255f,
                    size.Y * range.AmbientPositions[(s * 4) + 1] / 255f,
                    size.Z * range.AmbientPositions[(s * 4) + 2] / 255f));
            }
        }

        return samples;
    }

    /// <summary>For every lit face with cells in front of the opening, which of the opening's cells each cell sees.</summary>
    private static async Task<DoorReceiverFace[]> ReceiversAsync(
        IRayTracer tracer, DoorFaceCells?[] cells, DoorFrame frame, CancellationToken cancellationToken)
    {
        List<DoorReceiverFace> faces = [];
        foreach (DoorFaceCells? face in cells)
        {
            if (face is null)
            {
                continue;
            }

            (Vec3, Vec3)[] points = new (Vec3, Vec3)[face.Count];
            for (int c = 0; c < points.Length; c++)
            {
                points[c] = (face.Point(c), face.NormalAt(c));
            }

            DoorSeen seen = await SeenAsync(tracer, points, frame, cancellationToken).ConfigureAwait(false);
            if (!seen.None)
            {
                faces.Add(new DoorReceiverFace(face.Face, seen));
            }
        }

        return [.. faces];
    }

    /// <summary>
    /// Which of an opening's cells each point sees: a segment from the point
    /// (nudged half a unit along its normal) to each cell's centre, just past
    /// the opening's plane, traced against the open room's casters. A point
    /// on or beyond the plane, or one whose normal faces away from every
    /// corner of the opening, sees none; a point with no normal (a leaf
    /// ambient sample) faces every way.
    /// </summary>
    internal static async Task<DoorSeen> SeenAsync(IRayTracer tracer, (Vec3 Point, Vec3 Normal)[] points, DoorFrame frame, CancellationToken cancellationToken)
    {
        List<Ray> rays = [];
        List<int> owners = [];
        Vec3[] corners =
        [
            frame.ToRoom(new Vec3(0, -frame.Width / 2, -frame.Height / 2)),
            frame.ToRoom(new Vec3(0, frame.Width / 2, -frame.Height / 2)),
            frame.ToRoom(new Vec3(0, -frame.Width / 2, frame.Height / 2)),
            frame.ToRoom(new Vec3(0, frame.Width / 2, frame.Height / 2)),
        ];
        Vec3[] targets = new Vec3[DoorLightMath.CellCount];
        for (int c = 0; c < targets.Length; c++)
        {
            targets[c] = frame.ToRoom(DoorLightMath.CellCentre(c, frame.Width, frame.Height, Beyond));
        }

        for (int i = 0; i < points.Length; i++)
        {
            (Vec3 point, Vec3 normal) = points[i];
            Vec3 start = point + (normal * 0.5f);
            if (frame.ToLocal(start).X >= 0 || (normal != Vec3.Zero && !corners.Any(c => Vec3.Dot(c - start, normal) > 0)))
            {
                continue;
            }

            foreach (Vec3 target in targets)
            {
                rays.Add(Ray.Segment(start, target, stockReciprocal: false));
            }

            owners.Add(i);
        }

        UInt128[] masks = new UInt128[points.Length];
        if (rays.Count > 0)
        {
            ulong[] bits = new ulong[(rays.Count + 63) / 64];
            await tracer.TraceVisibilityAsync(rays.ToArray(), bits, RayTraceOptions.StockExact, cancellationToken).ConfigureAwait(false);
            for (int o = 0; o < owners.Count; o++)
            {
                UInt128 mask = UInt128.Zero;
                for (int c = 0; c < DoorLightMath.CellCount; c++)
                {
                    int ray = (o * DoorLightMath.CellCount) + c;
                    if ((bits[ray >> 6] & (1UL << (ray & 63))) == 0)
                    {
                        mask |= UInt128.One << c;
                    }
                }

                masks[owners[o]] = mask;
            }
        }

        return DoorSeen.Of(masks);
    }

    // ---- the capture -------------------------------------------------------------------------------

    /// <summary>
    /// The sources of a lit, open room that send light out through one
    /// opening: every light (the sun among them, through the room's own sky)
    /// that reaches a cell of it, with the cells it reaches; then, gathered
    /// by the cube of the room they came from, what the room's surfaces and
    /// sky send through it, each gathering a point source as bright as the
    /// light it sends through the opening. The room is open at every
    /// doorway, and what a ray meets beyond the room's cell (out through
    /// another opening) is the black box of 9.1: it sends nothing.
    /// </summary>
    internal static DoorSource[] Capture(BspData lit, bool hdr, DoorFrame frame, float cell)
    {
        AmbientScene scene = AmbientScene.Create(lit, hdr ? LightingMode.Hdr : LightingMode.Ldr);
        DispTestedScratch scratch = new(scene.Tracer.Displacements.Count);
        float reach = 4f * (frame.Width + frame.Height + 1024f);
        Vec3[] centres = new Vec3[DoorLightMath.CellCount];
        for (int c = 0; c < centres.Length; c++)
        {
            centres[c] = frame.ToRoom(DoorLightMath.CellCentre(c, frame.Width, frame.Height, -Inset));
        }

        List<DoorSource> sources = [];
        foreach (DWorldLight light in scene.WorldLights)
        {
            UInt128 cells = UInt128.Zero;
            for (int c = 0; c < centres.Length; c++)
            {
                if (Reaches(scene, scratch, light, centres[c], frame.Out, reach))
                {
                    cells |= UInt128.One << c;
                }
            }

            if (cells == UInt128.Zero)
            {
                continue;
            }

            EmitType type = (EmitType)light.Type;
            Vec3 origin = type == EmitType.SkyLight ? frame.Centre - (light.Normal * 256f) : light.Origin;
            sources.Add(new DoorSource(
                type, origin, light.Normal, light.Intensity * 255f, light.Style,
                light.ConstantAttn, light.LinearAttn, light.QuadraticAttn, light.StopDot, light.StopDot2, light.Exponent, cells));
        }

        sources.AddRange(StandIns(scene, scratch, frame, centres, reach, cell));
        return [.. sources];
    }

    /// <summary>
    /// Whether a light reaches a point travelling out of the room: a point,
    /// spot or surface light in front of it that the point sees and whose
    /// falloff there is not zero, or the sun when a ray back along its light
    /// meets the sky.
    /// </summary>
    private static bool Reaches(AmbientScene scene, DispTestedScratch scratch, DWorldLight light, Vec3 point, Vec3 @out, float reach)
    {
        switch ((EmitType)light.Type)
        {
            case EmitType.Point:
            case EmitType.Spotlight:
            case EmitType.Surface:
            {
                Vec3 toLight = light.Origin - point;
                if (!(Vec3.Dot(toLight, @out) < 0))
                {
                    return false;
                }

                DoorSource probe = new(
                    (EmitType)light.Type, light.Origin, light.Normal, new Vec3(1, 1, 1), 0,
                    light.ConstantAttn, light.LinearAttn, light.QuadraticAttn, light.StopDot, light.StopDot2, light.Exponent, UInt128.Zero);
                if (DoorLightMath.Falloff(probe, toLight, -@out) <= 0)
                {
                    return false;
                }

                AmbientHit hit = scene.Tracer.Trace(point, toLight, scratch);
                return !hit.IsHit || hit.Fraction >= 0.999f;
            }

            case EmitType.SkyLight:
            {
                if (!(Vec3.Dot(light.Normal, @out) > 0))
                {
                    return false;
                }

                AmbientHit hit = scene.Tracer.Trace(point, light.Normal * -reach, scratch);
                return hit.IsHit && (scene.TexInfo[scene.Faces[hit.Surface].TexInfo].Flags & (int)SurfaceFlags.Sky) != 0;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// What a room's surfaces and sky send out through an opening, as
    /// stand-in point sources: from every cell's centre, rays over the
    /// hemisphere into the room (<see cref="Hemisphere"/>) each read the
    /// style-0 light of what they meet as vrad's leaf ambient does; each
    /// ray's flux through the cell goes to the stand-in of the 64-unit cube
    /// its hit point lies in, placed at the flux-weighted mean of its hits
    /// and made as bright, falling off with the inverse square, as the flux
    /// it sends through the cells it was seen from. Only style 0: vrad
    /// bounces only style 0, and leaf ambient keeps only style 0.
    /// </summary>
    private static List<DoorSource> StandIns(AmbientScene scene, DispTestedScratch scratch, DoorFrame frame, Vec3[] centres, float reach, float cell)
    {
        Vec3? skyAmbient = RayAmbientLighting.FindSkyAmbient(scene);
        Vec3[] directions = Hemisphere(CaptureRays);
        float cellArea = frame.Width * frame.Height / DoorLightMath.CellCount;
        float solid = 2f * MathF.PI / CaptureRays;
        SortedDictionary<(int, int, int), (Vec3 Weighted, float Weight, Vec3 Flux, UInt128 Cells)> gathered = [];
        Span<Vec3> colour = stackalloc Vec3[RayAmbientLighting.MaxLightStyles];
        for (int c = 0; c < centres.Length; c++)
        {
            Vec3 point = centres[c];
            foreach (Vec3 d in directions)
            {
                Vec3 travel = frame.DirectionToRoom(d);
                Vec3 end = point - (travel * reach);
                AmbientHit hit = scene.Tracer.Trace(point, end - point, scratch);
                if (!hit.IsHit || HitPoint(scene, point, -travel, reach, hit) is not { } at || !Inside(at, cell))
                {
                    continue;
                }

                colour.Clear();
                RayAmbientLighting.Accumulate(scene, point, end, 0f, skyAmbient, colour, scratch);
                if (colour[0] == Vec3.Zero)
                {
                    continue;
                }

                // Radiance is the ray's colour over pi; its flux through the
                // cell, radiance times the cosine, the solid angle and the
                // cell's area; in lightmap units, 255 of the colour's.
                Vec3 flux = colour[0] * (255f * d.X * solid * cellArea / MathF.PI);
                Vec3 local = frame.ToLocal(at);
                (int, int, int) key = ((int)MathF.Floor(local.X / StandInCell), (int)MathF.Floor(local.Y / StandInCell), (int)MathF.Floor(local.Z / StandInCell));
                float weight = flux.X + flux.Y + flux.Z;
                gathered.TryGetValue(key, out var g);
                gathered[key] = (g.Weighted + (local * weight), g.Weight + weight, g.Flux + flux, g.Cells | (UInt128.One << c));
            }
        }

        List<DoorSource> standIns = [];
        foreach ((Vec3 weighted, float weight, Vec3 flux, UInt128 cells) in gathered.Values)
        {
            if (!(weight > 0))
            {
                continue;
            }

            Vec3 local = weighted * (1f / weight);
            DoorSource unit = new(EmitType.Point, frame.ToRoom(local), Vec3.Zero, new Vec3(1, 1, 1), 0, 0, 0, 1, 0, 0, 0, cells) { StandIn = true };
            double through = OwnFlux(unit, frame);
            if (!(through > 0))
            {
                continue;
            }

            standIns.Add(unit with { Intensity = flux * (float)(1 / through) });
        }

        return standIns;
    }

    /// <summary>
    /// Where a leaf ambient ray met what it hit: along the ray for a surface
    /// with a lightmap; for the sky, which the ambient tracer reports at the
    /// ray's full length, where the ray crosses the sky face's plane (null
    /// if it does not, ahead of the start).
    /// </summary>
    private static Vec3? HitPoint(AmbientScene scene, Vec3 start, Vec3 direction, float reach, AmbientHit hit)
    {
        if (hit.HasLuxel)
        {
            return start + (direction * (reach * hit.Fraction));
        }

        DPlane plane = scene.Planes[scene.Faces[hit.Surface].PlaneNum];
        float along = Vec3.Dot(plane.Normal, direction);
        if (MathF.Abs(along) < 1e-6f)
        {
            return null;
        }

        float t = (plane.Dist - Vec3.Dot(plane.Normal, start)) / along;
        return t >= 0 ? start + (direction * t) : null;
    }

    /// <summary>
    /// Whether a room-local point is in the room's cell (to a unit): a ray
    /// that leaves by another doorway meets whatever lies beyond, which in a
    /// level is another room the door light does not reach (D3), and in the
    /// open room is nothing that belongs to it.
    /// </summary>
    internal static bool Inside(Vec3 point, float cell) =>
        point.X >= -1 && point.Y >= -1 && point.Z >= -1 && point.X <= cell + 1 && point.Y <= cell + 1 && point.Z <= cell + 1;

    /// <summary>A source's flux through its own room's opening per unit intensity, from the cells it reaches.</summary>
    internal static double OwnFlux(in DoorSource source, DoorFrame frame) =>
        DoorLightMath.SourceFlux(DoorLightMath.ToNeighbour(source, frame), frame.Width, frame.Height, frame.Depth);

    /// <summary>
    /// Directions over the hemisphere around door-local +x (out of the room),
    /// equal in solid angle: a Fibonacci spiral, the same for every point and
    /// every run.
    /// </summary>
    internal static Vec3[] Hemisphere(int count)
    {
        Vec3[] directions = new Vec3[count];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int k = 0; k < count; k++)
        {
            float x = 1f - ((k + 0.5f) / count);
            float r = MathF.Sqrt(MathF.Max(0f, 1f - (x * x)));
            float phi = golden * k;
            directions[k] = new Vec3(x, r * DetMathF.Cos(phi), r * DetMathF.Sin(phi));
        }

        return directions;
    }

    // ---- the responses -----------------------------------------------------------------------------

    /// <summary>
    /// One response emitter: the open room lit by nothing but a point light
    /// at the emitter's node (inverse-square for the first
    /// <see cref="DoorLightMath.NodesPerClass"/>, constant for the rest), its
    /// texture lights dark; per range, what its light bounced onto each face,
    /// the room's leaf ambient samples and its props' colours, per unit of
    /// the emitter's intensity.
    /// </summary>
    /// <remarks>
    /// The runs light the room in its own frame at turn 0, and the link
    /// applies them at every turn (O14: one response set per door). Nothing
    /// in a run is fixed in the world but the directions vrad samples leaf
    /// ambient and props along, which the bake frame turns
    /// (<paramref name="turn"/>, for the fact that holds the four turns to
    /// agree): the bounced light is the same at every turn, and the samples
    /// agree to their sampling noise.
    /// </remarks>
    internal static async Task<(DoorResponseEmitter? Ldr, DoorResponseEmitter? Hdr)> RespondAsync(
        RoomObject room,
        BspData open,
        DoorFrame frame,
        int emitter,
        RoomLightingSettings settings,
        IContentFileSystem content,
        CompileParallelism parallelism,
        ITransferCache? transfers,
        int turn,
        CancellationToken cancellationToken)
    {
        RoomDefinition definition = room.Definition;
        bool flat = emitter >= DoorLightMath.NodesPerClass;
        Vec3 node = frame.ToRoom(DoorLightMath.Node(emitter % DoorLightMath.NodesPerClass, definition.CellSize, definition.Kit.Depth));
        List<BspEntity> entities = Unlit(room.Bsp);
        BspEntity light = new();
        light.Pairs.Add(new BspKeyValue("classname", "light"));
        light.Pairs.Add(new BspKeyValue("origin", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{node.X:R} {node.Y:R} {node.Z:R}")));
        light.Pairs.Add(new BspKeyValue("_light", "255 255 255 255"));
        light.Pairs.Add(new BspKeyValue(flat ? "_constant_attn" : "_quadratic_attn", "1"));
        entities.Insert(Math.Min(1, entities.Count), light);
        OpenRun run = await LightAsync(open, definition.Name, entities, settings, content, parallelism, transfers, noTextureLights: true, turn, cancellationToken)
            .ConfigureAwait(false);

        DoorResponseEmitter? Range(bool hdr)
        {
            BspLumpData worldLights = run.Lit[hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights];
            if (run.Lit[hdr ? BspLump.FacesHdr : BspLump.Faces].IsEmpty || worldLights.IsEmpty)
            {
                return null;
            }

            DWorldLight wl = BspStructView.As<DWorldLight>(worldLights).ToArray().First(l => l.Type == (int)EmitType.Point);
            float unit = wl.Intensity.X * 255f / (flat ? wl.ConstantAttn : wl.QuadraticAttn);
            float scale = 1f / unit;
            return new DoorResponseEmitter(
                ResponseFaces(run.Bounce[hdr ? 1 : 0], open, scale),
                ResponseAmbient(run.Lit, hdr, scale),
                ResponseProps(run.Props, hdr, scale));
        }

        return (Range(false), Range(true));
    }

    /// <summary>Each face's bounced light, at half resolution, scaled; faces whose every value is below the prune left out.</summary>
    private static DoorResponseFace[] ResponseFaces(float[]?[] bounce, BspData open, float scale)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(open[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(open[BspLump.TexInfo]);
        float peak = 0;
        foreach (float[]? values in bounce)
        {
            if (values is not null)
            {
                foreach (float v in values)
                {
                    peak = MathF.Max(peak, v);
                }
            }
        }

        List<DoorResponseFace> kept = [];
        for (int f = 0; f < bounce.Length; f++)
        {
            if (bounce[f] is not { } values || !values.Any(v => v > Prune * peak))
            {
                continue;
            }

            int w = faces[f].LightmapTextureSizeInLuxels[0] + 1, h = faces[f].LightmapTextureSizeInLuxels[1] + 1;
            int pages = Pages(texInfos, faces[f]);
            (int hw, int hh) = Half(w, h);
            Half[] half = new Half[pages * hw * hh * 3];
            for (int page = 0; page < pages; page++)
            {
                for (int j = 0; j < hh; j++)
                {
                    for (int i = 0; i < hw; i++)
                    {
                        for (int ch = 0; ch < 3; ch++)
                        {
                            float sum = 0;
                            int n = 0;
                            for (int dt = 0; dt < 2 && (2 * j) + dt < h; dt++)
                            {
                                for (int ds = 0; ds < 2 && (2 * i) + ds < w; ds++)
                                {
                                    sum += values[(((page * w * h) + (((2 * j) + dt) * w) + (2 * i) + ds) * 3) + ch];
                                    n++;
                                }
                            }

                            half[(((page * hw * hh) + (j * hw) + i) * 3) + ch] = (Half)(sum / n * scale);
                        }
                    }
                }
            }

            kept.Add(new DoorResponseFace(f, half));
        }

        return [.. kept];
    }

    /// <summary>A response run's leaf ambient samples of one range, scaled; samples below the prune left out.</summary>
    private static DoorResponseSample[] ResponseAmbient(BspData lit, bool hdr, float scale)
    {
        DLeaf[] leaves = AmbientScene.ReadLeaves(lit);
        ReadOnlySpan<DLeafAmbientIndex> index = BspStructView.As<DLeafAmbientIndex>(lit[hdr ? BspLump.LeafAmbientIndexHdr : BspLump.LeafAmbientIndex]);
        ReadOnlySpan<DLeafAmbientLighting> samples = BspStructView.As<DLeafAmbientLighting>(lit[hdr ? BspLump.LeafAmbientLightingHdr : BspLump.LeafAmbientLighting]);
        float peak = 0;
        foreach (DLeafAmbientLighting sample in samples)
        {
            for (int side = 0; side < 6; side++)
            {
                Vec3 c = sample.Cube.Color[side].ToLinear();
                peak = MathF.Max(peak, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
            }
        }

        List<DoorResponseSample> kept = [];
        for (int l = 0; l < index.Length && l < leaves.Length; l++)
        {
            Vec3 mins = new(leaves[l].Mins[0], leaves[l].Mins[1], leaves[l].Mins[2]);
            Vec3 size = new Vec3(leaves[l].Maxs[0], leaves[l].Maxs[1], leaves[l].Maxs[2]) - mins;
            for (int i = 0; i < index[l].AmbientSampleCount; i++)
            {
                DLeafAmbientLighting sample = samples[index[l].FirstAmbientSample + i];
                Half[] cube = new Half[RoomLighting.AmbientHalves];
                bool any = false;
                for (int side = 0; side < 6; side++)
                {
                    Vec3 c = sample.Cube.Color[side].ToLinear();
                    any |= MathF.Max(c.X, MathF.Max(c.Y, c.Z)) > Prune * peak;
                    cube[side * 3] = (Half)(c.X * scale);
                    cube[(side * 3) + 1] = (Half)(c.Y * scale);
                    cube[(side * 3) + 2] = (Half)(c.Z * scale);
                }

                if (any)
                {
                    kept.Add(new DoorResponseSample(
                        l, mins + new Vec3(size.X * sample.X / 255f, size.Y * sample.Y / 255f, size.Z * sample.Z / 255f), cube));
                }
            }
        }

        return [.. kept];
    }

    /// <summary>A response run's lit props of one range, scaled, by prop index.</summary>
    private static DoorResponseProp[] ResponseProps(List<(bool Hdr, StaticPropLightingResult Result)> props, bool hdr, float scale)
    {
        List<DoorResponseProp> kept = [];
        foreach ((bool passHdr, StaticPropLightingResult result) in props)
        {
            if (passHdr != hdr)
            {
                continue;
            }

            foreach (StaticPropVhvFile file in result.Files)
            {
                if (file.Colors is not { } colours)
                {
                    continue;
                }

                List<Half> halves = [];
                foreach ((_, Vec3[] mesh) in colours.Meshes)
                {
                    foreach (Vec3 c in mesh)
                    {
                        halves.Add((Half)(c.X * scale));
                        halves.Add((Half)(c.Y * scale));
                        halves.Add((Half)(c.Z * scale));
                    }
                }

                kept.Add(new DoorResponseProp(file.PropIndex, [.. halves]));
            }
        }

        kept.Sort((a, b) => a.Prop.CompareTo(b.Prop));
        return [.. kept];
    }

    /// <summary>The bounce's transfers of one room's response runs, shared between them and dropped with the bake.</summary>
    private sealed class MemoryTransferCache : ITransferCache
    {
        private readonly Dictionary<string, TransferSet> _sets = new(StringComparer.Ordinal);

        public ValueTask<TransferSet?> TryGetAsync(string key, int patchCount, CancellationToken cancellationToken)
        {
            lock (_sets)
            {
                return ValueTask.FromResult(_sets.TryGetValue(key, out TransferSet? set) && set.PatchCount == patchCount ? set : null);
            }
        }

        public ValueTask StoreAsync(string key, TransferSet transfers, long costMs, CancellationToken cancellationToken)
        {
            lock (_sets)
            {
                _sets[key] = transfers;
            }

            return ValueTask.CompletedTask;
        }
    }
}
