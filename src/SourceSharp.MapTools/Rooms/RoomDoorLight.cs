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
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// One socket's doorway as the door light sees it: the opening's centre on
/// the room's inner wall plane (the plug's inner face), the unit normal out
/// of the room through it, the horizontal axis across it, and the kit's
/// size.
/// </summary>
/// <param name="Centre">The opening's centre on the plug's inner face, room-local.</param>
/// <param name="Out">The unit normal out of the room through the opening.</param>
/// <param name="Across">The horizontal unit axis along the wall: up cross out.</param>
/// <param name="Width">The opening's width.</param>
/// <param name="Height">The opening's height.</param>
/// <param name="Depth">The plug's depth: half the doorway's length at a joint.</param>
/// <remarks>
/// <para>
/// <b>Door-local coordinates</b> are (along <see cref="Out"/>, along
/// <see cref="Across"/>, up) from <see cref="Centre"/>: a point inside the
/// room has a negative first coordinate. Two jointed rooms' frames face
/// each other: their <see cref="Out"/>s and <see cref="Across"/>es are
/// opposite in the world, their centres <c>2 x Depth</c> apart (the two
/// plugs' length), and their up axes and centres' heights the same (every
/// socket is centred on its cell face). So a point at door-local
/// <c>(x, y, z)</c> of one room is at <c>(2 Depth - x, -y, z)</c> of the
/// other, whatever the two placements' turns: <see cref="ToNeighbour"/>.
/// </para>
/// </remarks>
internal readonly record struct DoorFrame(Vec3 Centre, Vec3 Out, Vec3 Across, float Width, float Height, float Depth)
{
    /// <summary>The world's up, which a turn keeps.</summary>
    public static Vec3 Up => new(0, 0, 1);

    /// <summary>The frame of one socket of a room.</summary>
    public static DoorFrame Of(RoomDefinition definition, RoomSocket socket)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Box plug = RoomLinter.SealBox(definition, socket, definition.CellSize);
        Vec3 @out = socket.Facing switch
        {
            RoomFacing.PositiveX => new Vec3(1, 0, 0),
            RoomFacing.NegativeX => new Vec3(-1, 0, 0),
            RoomFacing.PositiveY => new Vec3(0, 1, 0),
            _ => new Vec3(0, -1, 0),
        };
        Vec3 centre = ((plug.Mins + plug.Maxs) * 0.5f) - (@out * (definition.Kit.Depth * 0.5f));
        return new DoorFrame(centre, @out, Vec3.Cross(Up, @out), definition.Kit.Width, definition.Kit.Height, definition.Kit.Depth);
    }

    /// <summary>A room-local point in door-local coordinates.</summary>
    public Vec3 ToLocal(Vec3 point)
    {
        Vec3 d = point - Centre;
        return new Vec3(Vec3.Dot(d, Out), Vec3.Dot(d, Across), d.Z);
    }

    /// <summary>A room-local direction in door-local coordinates.</summary>
    public Vec3 DirectionToLocal(Vec3 direction) => new(Vec3.Dot(direction, Out), Vec3.Dot(direction, Across), direction.Z);

    /// <summary>A door-local point in room-local coordinates.</summary>
    public Vec3 ToRoom(Vec3 local) => Centre + (Out * local.X) + (Across * local.Y) + (Up * local.Z);

    /// <summary>A door-local direction in room-local coordinates.</summary>
    public Vec3 DirectionToRoom(Vec3 local) => (Out * local.X) + (Across * local.Y) + (Up * local.Z);

    /// <summary>A door-local point of this room in the door-local coordinates of the room jointed to it.</summary>
    public Vec3 ToNeighbour(Vec3 local) => new((2 * Depth) - local.X, -local.Y, local.Z);

    /// <summary>A door-local direction of this room in the door-local coordinates of the room jointed to it.</summary>
    public static Vec3 DirectionToNeighbour(Vec3 local) => new(-local.X, -local.Y, local.Z);
}

/// <summary>
/// One sample of the light leaving a room through a doorway: where it came
/// from, where it crossed the opening, which way it travels, its style and
/// its flux, in the door-local coordinates of the room it left.
/// </summary>
/// <param name="Source">Where the light came from: a light's origin, or the surface point a ray reached.</param>
/// <param name="Point">Where it crossed the opening's plane.</param>
/// <param name="Direction">Its unit direction of travel, out of the room.</param>
/// <param name="Style">Its light style.</param>
/// <param name="Flux">Its flux through the opening, in lightmap units times area.</param>
/// <param name="Falloff">How its source falls off with distance: 0 as the inverse square (a surface, a quadratic light), 1 not at all (a constant light, the sun), 2 linearly.</param>
/// <param name="Direct">Whether it came straight from a light (else from a surface or the sky a ray met).</param>
internal readonly record struct DoorFlux(Vec3 Source, Vec3 Point, Vec3 Direction, int Style, Vec3 Flux, int Falloff = 0, bool Direct = false);

/// <summary>What one vrad run of a room gives one range, linear, by face, leaf sample and prop.</summary>
/// <param name="Faces">Per face its style-0 luxels, every page, three floats a luxel; null for a face with none.</param>
/// <param name="Ambient">Per leaf its samples: position (room-local) and six cube faces.</param>
/// <param name="Props">Per lit prop its vertex colours, strip group after strip group.</param>
internal sealed record LitField(float[]?[] Faces, (Vec3 Position, Vec3[] Cube)[][] Ambient, Dictionary<int, Vec3[]> Props);

/// <summary>
/// The door capture and response of the rooms design (9.1 parts 2 and 3):
/// the machinery that measures light leaving a room through a doorway and a
/// room's answer to light entering through one.
/// </summary>
internal static class RoomDoorLight
{
    /// <summary>How far inside the room a capture's sample points and a response's door emitters sit.</summary>
    internal const float Inset = 0.5f;

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
        HashSet<int> plugFaces = [];
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
        foreach (int f in plugFaces.Order())
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

    /// <summary>
    /// One vrad run of an open room with the given entities, returning the lit
    /// copy; the room's own map is not changed.
    /// </summary>
    internal static async Task<(BspData Lit, List<(bool Hdr, StaticPropLightingResult Result)> Props, float[]?[] Bounce)> LightAsync(
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
        float[]?[] bounce = new float[faceStructs.Length][];
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
                if (hdr)
                {
                    return;
                }

                // Each face is finished on one worker: its array is its own.
                float[] values = bounce[face] ??= new float[pages[face] * pageLuxels[face] * 3];
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
        return (lit, props, bounce);
    }

    /// <summary>How many lightmap pages a face's style holds: four on a bumped face, else one.</summary>
    internal static int Pages(ReadOnlySpan<TexInfo> texInfos, DFace face) =>
        (texInfos[face.TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0 ? 4 : 1;

    /// <summary>Luxels in one page of a face.</summary>
    internal static int PageLuxels(DFace face) =>
        (face.LightmapTextureSizeInLuxels[0] + 1) * (face.LightmapTextureSizeInLuxels[1] + 1);

    /// <summary>What one run gave one range: style-0 luxels by face, leaf samples, prop colours.</summary>
    internal static LitField Field(BspData lit, bool hdr, List<(bool Hdr, StaticPropLightingResult Result)> props)
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(lit[hdr ? BspLump.FacesHdr : BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(lit[BspLump.TexInfo]);
        ReadOnlySpan<ColorRgbExp32> light = MemoryMarshal.Cast<byte, ColorRgbExp32>(lit[hdr ? BspLump.LightingHdr : BspLump.Lighting].Data.Span);
        float[]?[] perFace = new float[faces.Length][];
        for (int f = 0; f < faces.Length; f++)
        {
            DFace face = faces[f];
            if (face.LightOfs < 0)
            {
                continue;
            }

            int slot = -1;
            for (int k = 0; k < 4 && face.Styles[k] != 255; k++)
            {
                if (face.Styles[k] == 0)
                {
                    slot = k;
                    break;
                }
            }

            if (slot < 0)
            {
                continue;
            }

            int block = Pages(texInfos, face) * PageLuxels(face);
            float[] values = new float[block * 3];
            int first = (face.LightOfs / RoomLighting.LuxelBytes) + (slot * block);
            for (int i = 0; i < block; i++)
            {
                Vec3 c = light[first + i].ToLinear();
                values[i * 3] = c.X;
                values[(i * 3) + 1] = c.Y;
                values[(i * 3) + 2] = c.Z;
            }

            perFace[f] = values;
        }

        DLeaf[] leaves = AmbientScene.ReadLeaves(lit);
        ReadOnlySpan<DLeafAmbientIndex> index = BspStructView.As<DLeafAmbientIndex>(lit[hdr ? BspLump.LeafAmbientIndexHdr : BspLump.LeafAmbientIndex]);
        ReadOnlySpan<DLeafAmbientLighting> samples = BspStructView.As<DLeafAmbientLighting>(lit[hdr ? BspLump.LeafAmbientLightingHdr : BspLump.LeafAmbientLighting]);
        (Vec3, Vec3[])[][] ambient = new (Vec3, Vec3[])[leaves.Length][];
        for (int l = 0; l < leaves.Length; l++)
        {
            int count = l < index.Length ? index[l].AmbientSampleCount : 0;
            ambient[l] = new (Vec3, Vec3[])[count];
            Vec3 mins = new(leaves[l].Mins[0], leaves[l].Mins[1], leaves[l].Mins[2]);
            Vec3 size = new Vec3(leaves[l].Maxs[0], leaves[l].Maxs[1], leaves[l].Maxs[2]) - mins;
            for (int s = 0; s < count; s++)
            {
                DLeafAmbientLighting sample = samples[index[l].FirstAmbientSample + s];
                Vec3 position = mins + new Vec3(size.X * sample.X / 255f, size.Y * sample.Y / 255f, size.Z * sample.Z / 255f);
                Vec3[] cube = new Vec3[6];
                for (int side = 0; side < 6; side++)
                {
                    cube[side] = sample.Cube.Color[side].ToLinear();
                }

                ambient[l][s] = (position, cube);
            }
        }

        Dictionary<int, Vec3[]> colours = [];
        foreach ((bool passHdr, StaticPropLightingResult result) in props)
        {
            if (passHdr != hdr)
            {
                continue;
            }

            foreach (StaticPropVhvFile file in result.Files)
            {
                if (file.Colors is { } c)
                {
                    colours[file.PropIndex] = [.. c.Meshes.SelectMany(m => m.Colors)];
                }
            }
        }

        return new LitField(perFace, ambient, colours);
    }

    /// <summary>
    /// The light leaving a lit, open room through one doorway, sampled on a
    /// grid of points over the opening (just inside the room): every entity
    /// light and the sun that reach a point directly, and, along a fixed set
    /// of directions over the hemisphere facing into the room, the light of
    /// whatever surface or sky each ray meets (the lightmap it lit, times the
    /// surface's reflectivity, as vrad's leaf ambient gathers it).
    /// </summary>
    /// <param name="lit">The open room lit with its own lights (and the sun).</param>
    /// <param name="hdr">Which range.</param>
    /// <param name="frame">The doorway.</param>
    /// <param name="across">Sample points across the opening.</param>
    /// <param name="up">Sample points up the opening.</param>
    /// <param name="rays">Ray directions per point.</param>
    internal static List<DoorFlux> Capture(BspData lit, bool hdr, DoorFrame frame, int across, int up, int rays)
    {
        AmbientScene scene = AmbientScene.Create(lit, hdr ? LightingMode.Hdr : LightingMode.Ldr);
        DispTestedScratch scratch = new(scene.Tracer.Displacements.Count);
        ReadOnlySpan<DWorldLight> lights = scene.WorldLights;
        Vec3? skyAmbient = RayAmbientLighting.FindSkyAmbient(scene);
        Vec3[] directions = Hemisphere(rays);
        float cellArea = frame.Width * frame.Height / (across * up);
        float solid = 2f * MathF.PI / rays;
        float reach = 4f * (frame.Width + frame.Height + 1024f);
        List<DoorFlux> flux = [];
        Span<Vec3> colour = stackalloc Vec3[RayAmbientLighting.MaxLightStyles];
        for (int j = 0; j < up; j++)
        {
            for (int i = 0; i < across; i++)
            {
                Vec3 local = new(-Inset, ((i + 0.5f) / across * frame.Width) - (frame.Width / 2), ((j + 0.5f) / up * frame.Height) - (frame.Height / 2));
                Vec3 point = frame.ToRoom(local);

                // Direct light from each light the point sees.
                foreach (DWorldLight light in lights)
                {
                    if (DirectAt(scene, scratch, light, point, frame.Out, reach) is not { } direct)
                    {
                        continue;
                    }

                    int falloff = light.Type == (int)EmitType.SkyLight ? 1
                        : light.Type == (int)EmitType.Surface || light.QuadraticAttn > 0 ? 0
                        : light.LinearAttn > 0 ? 2 : 1;
                    flux.Add(new DoorFlux(
                        frame.ToLocal(direct.Source), local, frame.DirectionToLocal(direct.Direction), light.Style, light.Intensity * (direct.Irradiance * cellArea), falloff, true));
                }

                // What every other surface sends through the point.
                foreach (Vec3 d in directions)
                {
                    Vec3 travel = frame.DirectionToRoom(d);
                    Vec3 end = point - (travel * reach);
                    AmbientHit hit = scene.Tracer.Trace(point, end - point, scratch);
                    if (!hit.IsHit)
                    {
                        continue;
                    }

                    colour.Clear();
                    RayAmbientLighting.Accumulate(scene, point, end, 0f, skyAmbient, colour, scratch);
                    Vec3 source = point - (travel * (reach * hit.Fraction));
                    float weight = d.X * solid * cellArea / MathF.PI;
                    for (int s = 0; s < colour.Length; s++)
                    {
                        if (colour[s] != Vec3.Zero)
                        {
                            flux.Add(new DoorFlux(frame.ToLocal(source), local, d, s, colour[s] * weight));
                        }
                    }
                }
            }
        }

        return flux;
    }

    /// <summary>
    /// Which of a lit room's lights reach which cells of one opening: per
    /// light that reaches any, the light (room-local) and one flag per cell
    /// of an <paramref name="across"/> by <paramref name="up"/> grid over the
    /// opening, set when the light reaches the cell's centre unoccluded and
    /// travelling out of the room (the sun through the room's own sky).
    /// </summary>
    internal static List<(DWorldLight Light, bool[] Cells)> CaptureLights(BspData lit, bool hdr, DoorFrame frame, int across, int up)
    {
        AmbientScene scene = AmbientScene.Create(lit, hdr ? LightingMode.Hdr : LightingMode.Ldr);
        DispTestedScratch scratch = new(scene.Tracer.Displacements.Count);
        float reach = 4f * (frame.Width + frame.Height + 1024f);
        List<(DWorldLight, bool[])> found = [];
        foreach (DWorldLight light in scene.WorldLights)
        {
            bool[] cells = new bool[across * up];
            bool any = false;
            for (int j = 0; j < up; j++)
            {
                for (int i = 0; i < across; i++)
                {
                    Vec3 local = new(-Inset, ((i + 0.5f) / across * frame.Width) - (frame.Width / 2), ((j + 0.5f) / up * frame.Height) - (frame.Height / 2));
                    if (DirectAt(scene, scratch, light, frame.ToRoom(local), frame.Out, reach) is not null)
                    {
                        cells[(j * across) + i] = true;
                        any = true;
                    }
                }
            }

            if (any)
            {
                found.Add((light, cells));
            }
        }

        return found;
    }

    /// <summary>
    /// One light's irradiance on the opening's plane at a point, per unit of
    /// its intensity, with vrad's falloff for its type, when it reaches the
    /// point unoccluded and travelling out of the room; its source and the
    /// direction it travels.
    /// </summary>
    private static (Vec3 Source, Vec3 Direction, float Irradiance)? DirectAt(
        AmbientScene scene, DispTestedScratch scratch, DWorldLight light, Vec3 point, Vec3 @out, float reach)
    {
        switch ((EmitType)light.Type)
        {
            case EmitType.Point:
            case EmitType.Spotlight:
            case EmitType.Surface:
            {
                Vec3 delta = point - light.Origin;
                float dist2 = delta.LengthSquared();
                float dist = MathF.Max(MathF.Sqrt(dist2), 1f);
                Vec3 direction = delta * (1f / MathF.Sqrt(MathF.Max(dist2, 1e-12f)));
                float cosine = Vec3.Dot(direction, @out);
                if (cosine <= 0)
                {
                    return null;
                }

                float falloff;
                if (light.Type == (int)EmitType.Surface)
                {
                    falloff = MathF.Max(0, Vec3.Dot(direction, light.Normal)) / MathF.Max(dist2, 1f);
                }
                else
                {
                    falloff = 1f / ((light.QuadraticAttn * dist * dist) + (light.LinearAttn * dist) + light.ConstantAttn);
                    if (light.Type == (int)EmitType.Spotlight)
                    {
                        float dot2 = Vec3.Dot(direction, light.Normal);
                        if (!(dot2 > light.StopDot2))
                        {
                            return null;
                        }

                        float mult = 1f;
                        if (dot2 <= light.StopDot)
                        {
                            mult = Math.Clamp((dot2 - light.StopDot2) / (light.StopDot - light.StopDot2), 0f, 1f);
                            if (light.Exponent != 0f && light.Exponent != 1f)
                            {
                                mult = DetMathF.Pow(mult, light.Exponent);
                            }
                        }

                        falloff *= dot2 * mult;
                    }
                }

                if (falloff <= 0)
                {
                    return null;
                }

                AmbientHit hit = scene.Tracer.Trace(point, light.Origin - point, scratch);
                if (hit.IsHit && hit.Fraction < 0.999f)
                {
                    return null;
                }

                return (light.Origin, direction, falloff * cosine);
            }

            case EmitType.SkyLight:
            {
                Vec3 direction = light.Normal;
                float cosine = Vec3.Dot(direction, @out);
                if (cosine <= 0)
                {
                    return null;
                }

                AmbientHit hit = scene.Tracer.Trace(point, direction * -reach, scratch);
                if (!hit.IsHit || (scene.TexInfo[scene.Faces[hit.Surface].TexInfo].Flags & (int)SurfaceFlags.Sky) == 0)
                {
                    return null;
                }

                // The sky reports no distance; the source stands a room away.
                return (point - (direction * 256f), direction, cosine);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Directions over the hemisphere around door-local +x (out of the
    /// room), equal in solid angle: a Fibonacci spiral, the same for every
    /// point and every run.
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
}
