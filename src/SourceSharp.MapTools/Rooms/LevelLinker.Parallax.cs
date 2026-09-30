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

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// The skybox parallax (the rooms design, D36): each sky room placed
    /// away from its bakes' cell has its sun moved to the cell it stands in,
    /// and a level whose sky rooms recast from outside the skybox is warned
    /// of, once, naming the cells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> vrad of the level recasts a sky ray that leaves a room at
    /// <c>p</c> from <c>camera + p / scale</c> in the skybox; a sky room is
    /// baked per turn at cell (0, 0) (<see cref="RoomSkybox"/>), so in any
    /// other cell its bake saw the skybox from the wrong place. The room's
    /// sun layer (<see cref="RoomSunLayer"/>) holds its direct sun with the
    /// skybox left out, and the skybox's sun map (<see cref="RoomSunMap"/>)
    /// how much a recast start sees the sun, so each stored luxel gains its
    /// sun times the map's visibility at its recast start from this cell,
    /// less that from the bakes' cell. At the bakes' cell the change is zero
    /// and the placement takes its bake as it is, byte for byte. No ray is
    /// traced: the map is read.
    /// </para>
    /// <para>
    /// <b>The payload.</b> A corrected placement gets a payload of its own
    /// (<see cref="ResolvedPlacement.Parallax"/>), which its plan, its props'
    /// lighting files and its own lightmap block take in place of the stored
    /// turn's; a placement no luxel of which moves keeps sharing the stored
    /// turn. What is not corrected, the design's residual: the sky
    /// ambient's own parallax and the bounce of the sun's change.
    /// </para>
    /// <para>
    /// <b>The warning.</b> The skybox is one room; the recasts of a room
    /// whose cell lies further from the camera than the skybox's walls,
    /// times the scale, start outside it, where vrad of the level meets
    /// whatever the level holds there, which no bake holds. Such a level is
    /// linked, never refused, and told of once, every such cell named.
    /// </para>
    /// </remarks>
    /// <param name="resolved">The placements; a corrected one is replaced by a copy carrying its payload.</param>
    /// <param name="lit">Whether the level is lit; an unlit level is only warned of.</param>
    /// <param name="warnings">Where the warning goes.</param>
    internal static void PlanSkyboxParallax(ResolvedPlacement[] resolved, bool lit, List<string> warnings)
    {
        int skyboxAt = Array.FindIndex(resolved, p => p.Instance.Placement.Level == -1);
        if (skyboxAt < 0)
        {
            return;
        }

        RoomObject skybox = resolved[skyboxAt].Room;
        if (FarSkyCells(resolved, skybox) is { Count: > 0 } far)
        {
            warnings.Add(FarCellsWarning(skybox, far));
        }

        if (!lit || skybox.LightingOfCompile?.SunMap is not { } map)
        {
            return;
        }

        Dictionary<string, DoorFaceCells?[]> cells = new(StringComparer.Ordinal);
        for (int i = 0; i < resolved.Length; i++)
        {
            RoomObject room = resolved[i].Room;
            if (i == skyboxAt || room.LightingOfCompile is not { SunLayer: { } layer } lighting || !SameSun(layer.Toward, map.Toward))
            {
                continue;
            }

            string name = resolved[i].Instance.Placement.Room;
            if (!cells.TryGetValue(name, out DoorFaceCells?[]? faces))
            {
                faces = RoomDoorLight.FaceCellsOf(room.Bsp);
                cells[name] = faces;
            }

            if (ParallaxPayload(room.Definition.CellSize, lighting, layer, map, resolved[i].Instance.Placement, faces) is { } payload)
            {
                resolved[i] = resolved[i] with { Parallax = payload };
            }
        }
    }

    /// <summary>Whether a layer was baked under the map's sun: the same direction, bit for bit.</summary>
    internal static bool SameSun(Vec3 a, Vec3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    /// <summary>
    /// A placement's stored turn with its sun moved from its bakes' cell to
    /// its own, or null when it stands at its bakes' cell or no luxel and no
    /// vertex moves.
    /// </summary>
    /// <param name="cellSize">The room's grid.</param>
    /// <param name="lighting">The room's lighting.</param>
    /// <param name="layer">Its sun layer.</param>
    /// <param name="map">The skybox's sun map.</param>
    /// <param name="placement">Where the level places it.</param>
    /// <param name="cells">Its faces' luxel geometry (<see cref="RoomDoorLight.FaceCellsOf"/>).</param>
    internal static RoomLightingPayload? ParallaxPayload(
        float cellSize, RoomLighting lighting, RoomSunLayer layer, RoomSunMap map, RoomPlacement placement, DoorFaceCells?[] cells)
    {
        RoomTransform here = new(placement, cellSize);
        RoomTransform baked = new(new RoomPlacement(placement.Room, 0, 0, placement.Rotation), cellSize);
        if (here.Apply(Vec3.Zero) == baked.Apply(Vec3.Zero))
        {
            return null;
        }

        int rotation = placement.NormalizedRotation;
        RoomLightingPayload payload = lighting.For(rotation);
        RoomSunTurn sun = layer.For(rotation);
        RoomLightRange? ldr = Moved(payload.Ldr, sun.Ldr);
        RoomLightRange? hdr = Moved(payload.Hdr, sun.Hdr);
        return ReferenceEquals(ldr, payload.Ldr) && ReferenceEquals(hdr, payload.Hdr) ? null : new RoomLightingPayload(ldr, hdr);

        float Change(Vec3 local) =>
            map.Visibility(map.Recast(here.Apply(local))) - map.Visibility(map.Recast(baked.Apply(local)));

        RoomLightRange? Moved(RoomLightRange? range, RoomSunRange? layerRange)
        {
            if (range is null || layerRange is null)
            {
                return range;
            }

            Half[]? luxels = null;
            foreach (RoomSunFace face in layerRange.Faces)
            {
                if (face.Face >= cells.Length || cells[face.Face] is not { } geometry)
                {
                    continue;
                }

                int page = geometry.Width * geometry.Height;
                float[] change = new float[page];
                for (int at = 0; at < page; at++)
                {
                    change[at] = Change(geometry.At(geometry.MinS + (at % geometry.Width), geometry.MinT + (at / geometry.Width)));
                }

                int count = face.Luxels.Length / 3;
                for (int l = 0; l < count; l++)
                {
                    float d = change[l % page];
                    if (d != 0f)
                    {
                        luxels ??= (Half[])range.Luxels.Clone();
                        Add(luxels, (face.Start + l) * 3, face.Luxels, l * 3, d);
                    }
                }
            }

            RoomPropColors[]? props = null;
            foreach (RoomSunProp prop in layerRange.Props)
            {
                int index = Array.FindIndex(range.Props, p => p.Prop == prop.Prop);
                float d = Change(prop.Origin);
                if (index < 0 || d == 0f || range.Props[index].Colors.Length != prop.Colors.Length)
                {
                    continue;
                }

                props ??= (RoomPropColors[])range.Props.Clone();
                Half[] colors = (Half[])range.Props[index].Colors.Clone();
                for (int v = 0; v < colors.Length; v += 3)
                {
                    Add(colors, v, prop.Colors, v, d);
                }

                props[index] = props[index] with { Colors = colors };
            }

            return luxels is null && props is null ? range : range with { Luxels = luxels ?? range.Luxels, Props = props ?? range.Props };
        }

        static void Add(Half[] into, int at, Half[] sun, int from, float d)
        {
            for (int c = 0; c < 3; c++)
            {
                into[at + c] = (Half)Math.Max(0f, (float)into[at + c] + ((float)sun[from + c] * d));
            }
        }
    }

    /// <summary>
    /// The cells of the placements with a sky face whose recasts start
    /// outside the skybox (its world model's box): a room's box moved to its
    /// placement, recast about the skybox's camera, not inside the skybox's.
    /// </summary>
    internal static List<(int X, int Y)> FarSkyCells(ResolvedPlacement[] resolved, RoomObject skybox)
    {
        List<(int, int)> far = [];
        if (RoomSkybox.Of(skybox).LocalCameras is not [(Vec3 camera, float scale), ..] || WorldBox(skybox.Bsp) is not { } inside)
        {
            return far;
        }

        Dictionary<string, bool> sky = new(StringComparer.Ordinal);
        foreach (ResolvedPlacement placement in resolved)
        {
            RoomPlacement at = placement.Instance.Placement;
            if (at.Level == -1 || WorldBox(placement.Room.Bsp) is not { } box)
            {
                continue;
            }

            if (!sky.TryGetValue(at.Room, out bool hasSky))
            {
                hasSky = RoomLighting.HasSkyFace(placement.Room.Bsp);
                sky[at.Room] = hasSky;
            }

            RoomTransform transform = new(at, placement.Room.Definition.CellSize);
            Vec3 a = camera + (transform.Apply(box.Mins) * (1.0f / scale));
            Vec3 b = camera + (transform.Apply(box.Maxs) * (1.0f / scale));
            Vec3 lo = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), hi = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
            bool within = lo.X >= inside.Mins.X && lo.Y >= inside.Mins.Y && lo.Z >= inside.Mins.Z
                && hi.X <= inside.Maxs.X && hi.Y <= inside.Maxs.Y && hi.Z <= inside.Maxs.Z;
            if (hasSky && !within)
            {
                far.Add((at.CellX, at.CellY));
            }
        }

        return far;
    }

    /// <summary>The warning for a level whose sky rooms recast from outside its skybox, every such cell named.</summary>
    internal static string FarCellsWarning(RoomObject skybox, List<(int X, int Y)> cells)
    {
        string named = string.Join(", ", cells.Select(c => string.Create(CultureInfo.InvariantCulture, $"({c.X}, {c.Y})")));
        return $"the sky rooms at {(cells.Count == 1 ? "cell" : "cells")} {named} recast their sky from outside the 3D skybox \"{skybox.Definition.Name}\":"
            + " vrad of the level meets whatever lies past the skybox's walls there, which no bake holds, so their sky light is the skybox's as seen from outside it."
            + " Keep sky rooms within the cells the skybox covers, or grow the skybox.";
    }

    /// <summary>A map's world model box, or null for a map without one.</summary>
    private static Box? WorldBox(BspData bsp)
    {
        ReadOnlySpan<DModel> models = BspStructView.As<DModel>(bsp[BspLump.Models]);
        return models.IsEmpty ? null : new Box(models[0].Mins, models[0].Maxs);
    }
}
