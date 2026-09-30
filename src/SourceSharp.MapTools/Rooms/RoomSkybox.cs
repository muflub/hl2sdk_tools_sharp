//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A library's compiled 3D skybox as its sky rooms' bakes see it (the rooms
/// design, 4.12: every room's bake includes the skybox geometry for the
/// sky-ray recast): for each quarter turn a room is lit at, the skybox
/// placed where a level places it relative to that room, and its camera.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the level puts it.</b> The link and the flatten place the
/// skybox one cell below the level's south-west cell, unturned
/// (<see cref="LevelLinker.SkyboxPlacement"/>), and vrad of the level
/// recasts a sky ray from a sample at <c>p</c> to <c>camera + p / scale</c>.
/// Relative to the skybox that start moves with the sample's place in the
/// world, not only with its place in its room, so a room's recast depends on
/// the cell it stands in as well as its turn. A bake is made once per turn,
/// not per cell, so it takes the one placement every level of a single room
/// has: the room at cell (0, 0) at that turn, the skybox below it. A room
/// linked there, alone, links to vrad of its own linked map exactly; a room
/// elsewhere in a level sees the skybox from its bake's cell, off by its
/// cell's offset over the camera's scale (16 units of skybox per 256-unit
/// cell at the usual scale of 16), the parallax a 3D skybox shows between
/// two points of a level, which the bake cannot hold per cell; the link
/// moves the room's sun to its cell from the room's sun layer and the
/// skybox's sun map (<see cref="RoomSunLayer"/>, <see cref="RoomSunMap"/>,
/// the rooms design's D36) and leaves the rest as baked. Stored at
/// four turns, as a sky room's bake already is (D16): the skybox never
/// turns while the room does, so each turn sees it from another side.
/// </para>
/// <para>
/// <b>Into the room's frame.</b> A bake lights the room in its own frame
/// (<see cref="BakeFrame"/>), so the skybox is moved by the inverse of the
/// room's placement: a skybox point <c>s</c> stands at <c>s + (0, 0, -cell)</c>
/// in the level, and the level point <c>w</c> at <c>R⁻¹(w - T)</c> in the
/// room's frame, <c>R</c> and <c>T</c> the placement's turn and offset
/// (<see cref="RoomTransform"/>). The recast scales the sample's position
/// about the frame's origin, which the move does not keep, so the camera
/// is shifted by <c>R⁻¹T / scale</c> on top of the move: a sample at
/// <c>q</c> in the room recasts to <c>camera' + q / scale</c>, which is the
/// move of the level's <c>camera + (R q + T) / scale</c>. Every step is a
/// whole-unit offset or a quarter turn, so the triangles land bit for bit
/// where the level's own tracer holds them.
/// </para>
/// <para>
/// <b>Which bakes.</b> Only a room whose sky rays can reach the sky: one
/// stored at four turns (a sky face and a library sun,
/// <see cref="RoomLighting.HasSkyFace"/>). A room without a sky face never
/// sends a ray past its own walls, so its bake is the same bytes with or
/// without the skybox, and is left as it was; the skybox room itself holds
/// the camera, and vrad recasts nothing from a camera's own area, so its
/// bake is its own.
/// </para>
/// </remarks>
internal sealed class RoomSkybox
{
    private RoomSkybox(RoomObject room, (Vec3 Origin, float Scale)[] cameras)
    {
        Room = room;
        LocalCameras = cameras;
    }

    /// <summary>The compiled skybox room.</summary>
    public RoomObject Room { get; }

    /// <summary>The skybox room's name.</summary>
    public string Name => Room.Definition.Name;

    /// <summary>
    /// Its <c>sky_camera</c>s as vrad reads them from its map (origin,
    /// skybox-local, and <c>scale</c>), those with a scale of zero or less
    /// left out as vrad leaves them out; the split refuses a skybox without
    /// exactly one camera, so this is one camera.
    /// </summary>
    public (Vec3 Origin, float Scale)[] LocalCameras { get; }

    /// <summary>The compiled skybox, ready to be placed under a room's bakes.</summary>
    /// <param name="room">The skybox room's compile.</param>
    /// <returns>The skybox.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    public static RoomSkybox Of(RoomObject room)
    {
        ArgumentNullException.ThrowIfNull(room);
        List<(Vec3, float)> cameras = [];
        foreach (BspEntity entity in EntityLump.Parse(room.Bsp[BspLump.Entities]))
        {
            if (!string.Equals(EntityKeys.ValueForKey(entity, "classname"), "sky_camera", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float scale = EntityKeys.FloatForKey(entity, "scale");
            if (scale > 0.0f)
            {
                cameras.Add((EntityKeys.GetVectorForKey(entity, "origin"), scale));
            }
        }

        return new RoomSkybox(room, [.. cameras]);
    }

    /// <summary>
    /// Whether a room's bake includes this skybox: a room other than the
    /// skybox whose lighting is stored at four turns, a sky face under a
    /// library sun (<see cref="RoomLighting.BakeAsync"/>'s rule).
    /// </summary>
    /// <param name="room">The room being lit.</param>
    /// <param name="settings">What it is lit with.</param>
    public bool Lights(RoomObject room, RoomLightingSettings settings) =>
        settings.Sun is not null
        && !string.Equals(room.Definition.Name, Name, StringComparison.Ordinal)
        && RoomLighting.HasSkyFace(room.Bsp);

    /// <summary>What <see cref="Move"/> adds to a skybox point before its turn, for a room lit at <paramref name="turn"/>.</summary>
    internal Vec3 Offset(int turn, float cell) => new Vec3(0, 0, -cell) - PlacementOffset(turn, cell);

    /// <summary>A skybox point in the frame of a room lit at <paramref name="turn"/>.</summary>
    internal Vec3 Move(Vec3 point, int turn, float cell) => BakeFrame.ToRoom(point + Offset(turn, cell), turn);

    /// <summary>
    /// The skybox as vrad takes it for a bake of <paramref name="room"/> at
    /// <paramref name="turn"/>: its map, the move, the cameras shifted for
    /// the recast, and its props numbered after the room's.
    /// </summary>
    /// <param name="room">The room being lit.</param>
    /// <param name="turn">The quarter turns of the placement the bake is for.</param>
    /// <returns>The skybox.</returns>
    public VradSkybox For(RoomObject room, int turn)
    {
        ArgumentNullException.ThrowIfNull(room);
        float cell = room.Definition.CellSize;
        return new VradSkybox(Room.Bsp, Offset(turn, cell), turn, Cameras(turn, cell), PropCount(room.Bsp));
    }

    /// <summary>The cameras in the frame of a room lit at <paramref name="turn"/>, each in no area.</summary>
    internal SkyCamera[] Cameras(int turn, float cell)
    {
        Vec3 shift = BakeFrame.ToRoom(PlacementOffset(turn, cell), turn);
        SkyCamera[] cameras = new SkyCamera[LocalCameras.Length];
        for (int i = 0; i < cameras.Length; i++)
        {
            (Vec3 origin, float scale) = LocalCameras[i];
            float worldToSky = 1.0f / scale;
            cameras[i] = new SkyCamera(Move(origin, turn, cell) + (shift * worldToSky), scale, worldToSky, -1);
        }

        return cameras;
    }

    /// <summary>
    /// The skybox's casters in the frame of a room lit at
    /// <paramref name="turn"/>, as a tracer, with its cameras: what the door
    /// light's own sky rays recast into (<see cref="RoomDoorLight"/>), which
    /// vrad does not trace for it.
    /// </summary>
    public async Task<SkyboxRecast> RecastAsync(
        RoomObject room, int turn, VradOptions options, IContentFileSystem content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        VradSkybox skybox = For(room, turn);
        ShadowCasterLoadReport loaded = await ShadowCasterLoader.LoadAsync(
            Room.Bsp, options, content, NullPropCollisionSource.Instance, cancellationToken: cancellationToken).ConfigureAwait(false);
        ShadowCasterSet moved = skybox.AppendTo(new ShadowCasterBuilder().Build(), loaded.Set);
        return new SkyboxRecast(moved.Count == 0 ? null : moved.BuildTracer(options.Compliance), skybox.Cameras);
    }

    /// <summary>A room's static prop count, which the skybox's props are numbered after.</summary>
    private static int PropCount(BspData bsp) => RoomStaticProps.ReadLump(bsp)?.Props.Count ?? 0;

    /// <summary>
    /// The offset of a room at cell (0, 0) at <paramref name="turn"/>: where
    /// <see cref="RoomTransform.Apply"/> puts the room's origin.
    /// </summary>
    private static Vec3 PlacementOffset(int turn, float cell) =>
        new RoomTransform(new RoomPlacement(string.Empty, 0, 0, turn), cell).Apply(Vec3.Zero);
}

/// <summary>
/// A skybox placed under one bake of a room, as a tracer: whether a sky ray
/// that left the room through its sky is stopped in the skybox, as vrad's
/// recast answers it.
/// </summary>
/// <param name="Tracer">The skybox's casters in the room's frame, or null when it has none.</param>
/// <param name="Cameras">Its cameras in the room's frame.</param>
internal sealed record SkyboxRecast(KdRayTracer? Tracer, SkyCamera[] Cameras)
{
    /// <summary>
    /// Whether a ray from <paramref name="point"/> along <paramref name="direction"/>,
    /// having met the room's sky, meets something other than sky once
    /// recast from each camera (<c>camera + point / scale</c>): vrad counts
    /// the sky as occluded then.
    /// </summary>
    /// <param name="point">Where the ray starts, in the room's frame.</param>
    /// <param name="direction">Its direction, unit length.</param>
    public bool Blocks(Vec3 point, Vec3 direction)
    {
        if (Tracer is null)
        {
            return false;
        }

        Span<HitId> hit = stackalloc HitId[1];
        foreach (SkyCamera camera in Cameras)
        {
            Vec3 start = camera.Origin + (point * camera.WorldToSky);
            Ray[] ray = [LightRayLog.MakeRay(start, start + (direction * LightConstants.MaxTraceLength))];
            Tracer.TraceClosest(ray, hit, RayTraceOptions.StockExact);
            if (LightRayLog.Occlusion(hit[0]) >= 1.0f)
            {
                return true;
            }
        }

        return false;
    }
}
