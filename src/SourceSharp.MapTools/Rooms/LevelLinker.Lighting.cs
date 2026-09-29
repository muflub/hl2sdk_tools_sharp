//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad.Props;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>
    /// What a lit level's rooms agree on: which ranges they were lit in,
    /// whether a sun lit them, the map flags vrad wrote, and the sun's world
    /// lights (from the first placed room's turn 0, the world's frame).
    /// </summary>
    internal sealed record LevelLight(bool Ldr, bool Hdr, bool Sun, uint MapFlags, DWorldLight[]? SkyLdr, DWorldLight[]? SkyHdr)
    {
        /// <summary>The sun's world lights of one range, or none.</summary>
        public DWorldLight[] Sky(bool hdr) => (hdr ? SkyHdr : SkyLdr) ?? [];
    }

    /// <summary>
    /// Whether a level links lit, from its placed rooms' lighting: null when
    /// no room was lit (the level links exactly as before the bake), else
    /// what they agree on.
    /// </summary>
    /// <exception cref="LinkException">
    /// Some rooms were lit and some not, or two rooms were lit with
    /// different settings (ranges, sun or map flags): one level is lit one
    /// way, as one vrad run would light it.
    /// </exception>
    internal static LevelLight? PlanLighting(ResolvedPlacement[] resolved)
    {
        RoomLighting? first = null;
        string? firstRoom = null;
        string? unlit = null;
        foreach (ResolvedPlacement placement in resolved)
        {
            string name = placement.Room.Definition.Name;
            if (placement.Room.LightingOfCompile is not { } lighting)
            {
                unlit ??= name;
                continue;
            }

            if (first is null)
            {
                first = lighting;
                firstRoom = name;
                continue;
            }

            RoomLightingPayload a = first.Payloads[0];
            RoomLightingPayload b = lighting.Payloads[0];
            if ((a.Ldr is null) != (b.Ldr is null) || (a.Hdr is null) != (b.Hdr is null)
                || first.HasSun != lighting.HasSun || first.MapFlags != lighting.MapFlags)
            {
                throw new LinkException(
                    $"rooms {firstRoom} and {name} were lit with different settings (ranges, sun or map flags);"
                    + " a level's rooms are lit alike. Recompile the library with ssmap room.");
            }
        }

        if (first is null)
        {
            return null;
        }

        if (unlit is not null)
        {
            throw new LinkException(
                $"room {unlit} has no baked lighting, but room {firstRoom} of the same level has; a level's rooms are lit alike."
                + " Recompile the library with ssmap room.");
        }

        return new LevelLight(first.Payloads[0].Ldr is not null, first.Payloads[0].Hdr is not null, first.HasSun, first.MapFlags, first.SkyLdr, first.SkyHdr);
    }

    /// <summary>
    /// A room's compile with its bake laid over it for one placement: the
    /// faces' styles and lightmap offsets of the LDR range (when the room was
    /// lit in LDR), vrad's vertex normals and the map flags. The lighting
    /// lump itself stays empty here: the level's is written whole, each
    /// stored turn once (<see cref="WriteLighting"/>).
    /// </summary>
    private static BspData LitOverlay(BspData compiled, RoomLighting lighting, RoomLightingPayload payload)
    {
        BspData lit = RoomLighting.Copy(compiled);
        if (payload.Ldr is { } ldr)
        {
            byte[] faces = compiled[BspLump.Faces].Data.ToArray();
            Span<DFace> structs = MemoryMarshal.Cast<byte, DFace>(faces.AsSpan());
            for (int f = 0; f < structs.Length; f++)
            {
                for (int k = 0; k < 4; k++)
                {
                    structs[f].Styles[k] = ldr.Styles[(f * 4) + k];
                }

                structs[f].LightOfs = ldr.LightOffsets[f];
            }

            lit.SetLump(BspLump.Faces, faces, compiled[BspLump.Faces].Version == 0 ? Rad.Final.RadLumpWriter.FacesVersion : compiled[BspLump.Faces].Version);
        }

        lit.SetLump(BspLump.VertNormals, MemoryMarshal.AsBytes(lighting.VertNormals.AsSpan()).ToArray(), compiled[BspLump.VertNormals].Version);
        lit.SetLump(BspLump.VertNormalIndices, MemoryMarshal.AsBytes(lighting.VertNormalIndices.AsSpan()).ToArray(), compiled[BspLump.VertNormalIndices].Version);
        byte[] flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, lighting.MapFlags);
        lit.SetLump(BspLump.MapFlags, flags, compiled[BspLump.MapFlags].Version);
        return lit;
    }

    /// <summary>
    /// A baked direction (a vertex normal, a light's) turned with its
    /// placement, a negative zero written as zero: a quarter turn negates a
    /// component, and negating a zero gives the negative zero, where vrad,
    /// lighting the turned map, computes each direction afresh from the
    /// turned planes and writes a plain zero. Adding zero changes nothing
    /// else (the sum of a nonzero float and zero is that float).
    /// </summary>
    internal static Vec3 TurnDirection(Vec3 direction, int rotation)
    {
        Vec3 turned = RoomTransform.Rotate(direction, rotation);
        return new Vec3(turned.X + 0f, turned.Y + 0f, turned.Z + 0f);
    }

    /// <summary>
    /// Where each placement's lightmaps start in the level's lighting lumps:
    /// every stored turn of a room is written once, however many placements
    /// take it (a room placed five times at one turn, or a room no sun reaches
    /// placed at any turn, points five placements' faces at one block). A
    /// face reads only its own luxels through its offset, so sharing the
    /// bytes changes nothing a face sees, and it keeps a large level's
    /// lighting to the size of its distinct rooms.
    /// </summary>
    /// <returns>The distinct blocks in first-placed order, each with its room's lighting and turn.</returns>
    private static List<(RoomLightingPayload Payload, int LdrBase, int HdrBase)> AssignLightBases(RoomPlan[] plans)
    {
        Dictionary<(string Room, int Payload), (int Ldr, int Hdr)> bases = [];
        List<(RoomLightingPayload, int, int)> blocks = [];
        long ldr = 0, hdr = 0;
        foreach (RoomPlan plan in plans)
        {
            RoomLighting lighting = plan.Lighting!;
            int payload = plan.Transform.Placement.NormalizedRotation % lighting.RotationCount;
            (string, int) key = (plan.Placement.Room.Definition.Name, payload);
            if (!bases.TryGetValue(key, out (int Ldr, int Hdr) at))
            {
                RoomLightingPayload stored = lighting.Payloads[payload];
                at = ((int)ldr, (int)hdr);
                bases[key] = at;
                blocks.Add((stored, at.Ldr, at.Hdr));
                ldr += (stored.Ldr?.Luxels.Length ?? 0) / 3 * RoomLighting.LuxelBytes;
                hdr += (stored.Hdr?.Luxels.Length ?? 0) / 3 * RoomLighting.LuxelBytes;
                Limit(plan, "lighting bytes", Math.Max(ldr, hdr), int.MaxValue);
            }

            plan.LightBase = at.Ldr;
            plan.LightBaseHdr = at.Hdr;
        }

        return blocks;
    }

    /// <summary>
    /// The level's vertex normals when it is lit: every placement's baked
    /// normals, turned, each distinct normal (by its bits) once, in the order
    /// first met, and per placement the map from its room's normal to the
    /// level's. A vertex-normal index is 16 bits, so a level of many rooms
    /// could not hold their normals end to end; vrad already writes each
    /// distinct normal of a map once, and so does the link.
    /// </summary>
    private static List<Vec3> InternNormals(RoomPlan[] plans)
    {
        Dictionary<(int, int, int), int> seen = [];
        List<Vec3> normals = [];
        foreach (RoomPlan plan in plans)
        {
            int[] map = new int[plan.VertNormals.Length];
            for (int n = 0; n < map.Length; n++)
            {
                Vec3 normal = plan.VertNormals[n];
                (int, int, int) bits = (
                    BitConverter.SingleToInt32Bits(normal.X), BitConverter.SingleToInt32Bits(normal.Y), BitConverter.SingleToInt32Bits(normal.Z));
                if (!seen.TryGetValue(bits, out int index))
                {
                    index = normals.Count;
                    Limit(plan, "vertex normals", index + 1, ushort.MaxValue + 1);
                    seen[bits] = index;
                    normals.Add(normal);
                }

                map[n] = index;
            }

            plan.NormalMap = map;
        }

        return normals;
    }

    /// <summary>
    /// Writes a lit level's lighting into the assembled map: the lightmap
    /// lumps (each stored turn once, encoded from its linear values), the HDR
    /// faces, every face's switchable styles renumbered, the world lights
    /// moved, the leaf ambient samples turned and indexed by linked leaf (a
    /// carved doorway taking its facing leaf's), the sky flags (pass one
    /// from the rooms, pass two over the linked PVS), and the map flags.
    /// </summary>
    /// <param name="linked">The assembled map; its faces, leaves and visibility are final.</param>
    /// <param name="plans">The placements, in link order.</param>
    /// <param name="lit">What the rooms agree on.</param>
    /// <param name="blocks">The distinct lightmap blocks (<see cref="AssignLightBases"/>).</param>
    /// <param name="doorways">Each carved doorway leaf, its placement and the room cluster it joins.</param>
    /// <param name="styles">The level's switchable styles.</param>
    /// <param name="pvs">The linked PVS, one row a cluster.</param>
    /// <param name="rowBytes">Bytes a row.</param>
    internal static void WriteLighting(
        BspData linked,
        RoomPlan[] plans,
        LevelLight lit,
        List<(RoomLightingPayload Payload, int LdrBase, int HdrBase)> blocks,
        List<(int Leaf, int Placement, int Cluster)> doorways,
        LevelLightStyles styles,
        byte[] pvs,
        int rowBytes)
    {
        List<(RoomPlan Plan, int Face)> faceOwners = FaceOwners(plans);
        DFace[] faces = BspStructView.As<DFace>(linked[BspLump.Faces]).ToArray();
        int facesVersion = linked[BspLump.Faces].Version;

        foreach (bool hdr in (ReadOnlySpan<bool>)[false, true])
        {
            if (!(hdr ? lit.Hdr : lit.Ldr))
            {
                continue;
            }

            // The lightmaps: each distinct stored turn once, in the order the
            // level first placed it, encoded from its linear values (9.3).
            long bytes = blocks.Sum(b => (long)((b.Payload.Range(hdr)?.Luxels.Length ?? 0) / 3 * RoomLighting.LuxelBytes));
            byte[] lighting = new byte[bytes];
            foreach ((RoomLightingPayload payload, int ldrBase, int hdrBase) in blocks)
            {
                RoomLightRange range = payload.Range(hdr)!;
                int at = hdr ? hdrBase : ldrBase;
                RoomLighting.EncodeColors(
                    range.Luxels,
                    MemoryMarshal.Cast<byte, ColorRgbExp32>(lighting.AsSpan(at, range.Luxels.Length / 3 * RoomLighting.LuxelBytes)));
            }

            linked.SetLump(hdr ? BspLump.LightingHdr : BspLump.Lighting, lighting, Rad.Final.RadLumpWriter.LightingVersion);

            // The faces: the LDR lump already has the bake's offsets moved
            // (the overlay); both take the level's switchable styles, and the
            // HDR lump is the linked faces with the HDR range's.
            DFace[] rangeFaces = (DFace[])faces.Clone();
            for (int i = 0; i < rangeFaces.Length; i++)
            {
                (RoomPlan plan, int roomFace) = faceOwners[i];
                if (hdr)
                {
                    RoomLightRange range = plan.Lit!.Hdr!;
                    int offset = range.LightOffsets[roomFace];
                    rangeFaces[i].LightOfs = offset < 0 ? -1 : offset + plan.LightBaseHdr;
                    for (int k = 0; k < 4; k++)
                    {
                        rangeFaces[i].Styles[k] = range.Styles[(roomFace * 4) + k];
                    }
                }

                for (int k = 0; k < 4; k++)
                {
                    rangeFaces[i].Styles[k] = (byte)styles.Remap(plan.Placement.Index, rangeFaces[i].Styles[k]);
                }
            }

            linked.SetLump(hdr ? BspLump.FacesHdr : BspLump.Faces, MemoryMarshal.AsBytes(rangeFaces.AsSpan()).ToArray(), facesVersion);

            linked.SetLump(
                hdr ? BspLump.WorldLightsHdr : BspLump.WorldLights,
                MemoryMarshal.AsBytes(WorldLights(plans, lit, hdr, styles).AsSpan()).ToArray(),
                0);

            (DLeafAmbientIndex[] index, DLeafAmbientLighting[] samples) = LeafAmbient(linked, plans, hdr, doorways);
            linked.SetLump(hdr ? BspLump.LeafAmbientIndexHdr : BspLump.LeafAmbientIndex, MemoryMarshal.AsBytes(index.AsSpan()).ToArray(), 0);
            linked.SetLump(hdr ? BspLump.LeafAmbientLightingHdr : BspLump.LeafAmbientLighting, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), 1);
        }

        if (lit.Sun)
        {
            SkyFlags(linked, plans, pvs, rowBytes);
        }

        byte[] flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, lit.MapFlags);
        linked.SetLump(BspLump.MapFlags, flags, linked[BspLump.MapFlags].Version);
    }

    /// <summary>
    /// The placement and room face behind each linked face, in the linked
    /// order: every placement's world faces, then every kept brush model's.
    /// </summary>
    private static List<(RoomPlan Plan, int Face)> FaceOwners(RoomPlan[] plans)
    {
        List<(RoomPlan, int)> owners = [];
        foreach (RoomPlan plan in plans)
        {
            for (int f = 0; f < plan.WorldFaceCount; f++)
            {
                owners.Add((plan, f));
            }
        }

        foreach (RoomPlan plan in plans)
        {
            foreach (RoomBrushModel model in KeptModels(plan))
            {
                for (int f = model.Faces.First; f < model.Faces.End; f++)
                {
                    owners.Add((plan, f));
                }
            }
        }

        return owners;
    }

    /// <summary>
    /// The level's world lights in the order vrad lists a full compile's:
    /// its list holds the lights last made first, and a map's are made
    /// surface lights first, then the sun and its ambient (the library's
    /// entity right after the worldspawn), then the rooms' entity lights in
    /// entity order. So: every placement's entity lights, the last placement
    /// first; the sun's two; every placement's surface lights, the last
    /// placement first. Each is turned and moved with its placement, its
    /// cluster rebased and its switchable style renumbered.
    /// </summary>
    private static DWorldLight[] WorldLights(RoomPlan[] plans, LevelLight lit, bool hdr, LevelLightStyles styles)
    {
        List<DWorldLight> lights = [];
        void Add(RoomPlan plan, bool surface)
        {
            int rotation = plan.Transform.Placement.NormalizedRotation;
            foreach (DWorldLight light in plan.Lit!.Range(hdr)!.Lights)
            {
                if ((light.Type == (int)EmitType.Surface) != surface)
                {
                    continue;
                }

                // A point light's normal is the direction its entity's angles
                // name, which nothing lights by; vrad writes it from the
                // entity as the map holds it, and a turned placement's entity
                // keeps a missing angles key missing, so it is kept as baked.
                // A spot's and a surface's normal is the direction the light
                // shines, and turns with the room.
                DWorldLight moved = light;
                moved.Origin = plan.Transform.Apply(light.Origin);
                if (light.Type is (int)EmitType.Spotlight or (int)EmitType.Surface)
                {
                    moved.Normal = TurnDirection(light.Normal, rotation);
                }

                moved.Cluster = light.Cluster < 0 ? light.Cluster : light.Cluster + plan.ClusterBase;
                moved.Style = styles.Remap(plan.Placement.Index, light.Style);
                lights.Add(moved);
            }
        }

        for (int p = plans.Length - 1; p >= 0; p--)
        {
            Add(plans[p], surface: false);
        }

        lights.AddRange(lit.Sky(hdr));
        for (int p = plans.Length - 1; p >= 0; p--)
        {
            Add(plans[p], surface: true);
        }

        if (lights.Count > Rad.Light.WorldLightExporter.MaxWorldLights)
        {
            RoomPlan last = plans[^1];
            LoaderLimit(
                last.Placement.Room.Definition.Name,
                last.Placement.Instance.Placement.CellX,
                last.Placement.Instance.Placement.CellY,
                "world lights",
                lights.Count,
                Rad.Light.WorldLightExporter.MaxWorldLights,
                "MAX_MAP_WORLDLIGHTS");
        }

        return [.. lights];
    }

    /// <summary>
    /// The level's leaf ambient: per linked leaf its samples, each placement's
    /// taken from its stored turn and turned to its placement (the cube's
    /// horizontal faces permute, the position bytes permute with the leaf
    /// box's axes and flip where an axis is negated), written once per room
    /// and turn however many placements share them; a carved doorway takes
    /// its facing leaf's samples (the rooms design, 9.4), and every other new
    /// leaf (the shared solid leaf, the carve's solid fragments) none.
    /// </summary>
    private static (DLeafAmbientIndex[] Index, DLeafAmbientLighting[] Samples) LeafAmbient(
        BspData linked, RoomPlan[] plans, bool hdr, List<(int Leaf, int Placement, int Cluster)> doorways)
    {
        int leafCount = BspStructView.Count<DLeaf>(linked[BspLump.Leafs]);
        DLeafAmbientIndex[] index = new DLeafAmbientIndex[leafCount];
        bool[] own = new bool[leafCount];
        List<DLeafAmbientLighting> samples = [];
        Dictionary<(string Room, int Rotation), int> bases = [];
        foreach (RoomPlan plan in plans)
        {
            RoomLightRange range = plan.Lit!.Range(hdr)!;
            if (range.AmbientIndex.Length == 0)
            {
                continue;
            }

            int rotation = plan.Transform.Placement.NormalizedRotation;
            (string, int) key = (plan.Placement.Room.Definition.Name, rotation);
            if (!bases.TryGetValue(key, out int first))
            {
                first = samples.Count;
                bases[key] = first;
                int count = range.AmbientPositions.Length / 4;
                for (int s = 0; s < count; s++)
                {
                    samples.Add(TurnSample(range, s, rotation));
                }

                Limit(plan, "leaf ambient samples", samples.Count, ushort.MaxValue + 1);
            }

            for (int l = 0; l < range.AmbientIndex.Length; l++)
            {
                if (plan.Models is { } models && RoomModelLayout.Kept(models.OmittedLeaves, l) < 0)
                {
                    continue;
                }

                // A leaf with samples names its first sample; one without
                // names the nearest leaf that has some (vrad's
                // NearestNeighborWithLight, which the engine follows), a leaf
                // of the room's, linked with it; an omitted one, itself.
                DLeafAmbientIndex entry = range.AmbientIndex[l];
                int linkedLeaf = plan.LinkedLeaf(l);
                if (entry.AmbientSampleCount > 0)
                {
                    entry.FirstAmbientSample = (ushort)(entry.FirstAmbientSample + first);
                }
                else
                {
                    int neighbour = entry.FirstAmbientSample;
                    bool kept = neighbour < plan.Leafs.Length
                        && (plan.Models is not { } kept2 || RoomModelLayout.Kept(kept2.OmittedLeaves, neighbour) >= 0);
                    entry.FirstAmbientSample = (ushort)(kept ? plan.LinkedLeaf(neighbour) : linkedLeaf);
                    Limit(plan, "leaves", entry.FirstAmbientSample, ushort.MaxValue);
                }

                index[linkedLeaf] = entry;
                own[linkedLeaf] = true;
            }
        }

        foreach ((int leaf, int placement, int cluster) in doorways)
        {
            RoomPlan plan = plans[placement];
            DLeafAmbientIndex[] roomIndex = plan.Lit!.Range(hdr)!.AmbientIndex;
            for (int l = 0; l < roomIndex.Length; l++)
            {
                DLeaf roomLeaf = plan.Leafs[l];
                if (roomLeaf.Cluster == cluster && (roomLeaf.Contents & (int)BrushContents.Solid) == 0 && roomIndex[l].AmbientSampleCount > 0
                    && (plan.Models is not { } models || RoomModelLayout.Kept(models.OmittedLeaves, l) >= 0))
                {
                    index[leaf] = index[plan.LinkedLeaf(l)];
                    own[leaf] = true;
                    break;
                }
            }
        }

        // Every leaf the link made (the shared solid leaf, the carve's solid
        // fragments, a doorway whose facing side has no samples) names its
        // nearest leaf with samples in the linked tree, as vrad names an
        // empty leaf's.
        ReadOnlySpan<DNode> nodes = BspStructView.As<DNode>(linked[BspLump.Nodes]);
        ReadOnlySpan<DPlane> planes = BspStructView.As<DPlane>(linked[BspLump.Planes]);
        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(linked[BspLump.Leafs]);
        List<int> found = [];
        for (int l = 0; l < leafCount; l++)
        {
            if (!own[l])
            {
                index[l] = new DLeafAmbientIndex { AmbientSampleCount = 0, FirstAmbientSample = (ushort)NearestLit(nodes, planes, leafs, index, l, found) };
            }
        }

        return (index, [.. samples]);
    }

    /// <summary>
    /// The nearest leaf with ambient samples to a leaf, as vrad finds it for
    /// an empty leaf: among the leaves of the box grown by its own size in
    /// every direction, enumerated front child first, the first at the
    /// least box distance; the leaf itself when none is.
    /// </summary>
    private static int NearestLit(
        ReadOnlySpan<DNode> nodes, ReadOnlySpan<DPlane> planes, ReadOnlySpan<DLeaf> leafs, DLeafAmbientIndex[] index, int leaf, List<int> found)
    {
        (Vec3 mins, Vec3 maxs) = Bounds(leafs[leaf]);
        Vec3 size = maxs - mins;
        found.Clear();
        Rad.Ambient.ToolBspTree.EnumerateLeavesInBox(nodes, planes, mins - size, maxs + size, found);
        float best = float.MaxValue;
        int nearest = leaf;
        foreach (int test in found)
        {
            if (index[test].AmbientSampleCount == 0)
            {
                continue;
            }

            (Vec3 testMins, Vec3 testMaxs) = Bounds(leafs[test]);
            float distance = Rad.Ambient.LeafAmbientBuilder.AabbDistance(mins, maxs, testMins, testMaxs);
            if (distance < best)
            {
                best = distance;
                nearest = test;
            }
        }

        return nearest;

        static (Vec3, Vec3) Bounds(DLeaf l) => (new Vec3(l.Mins[0], l.Mins[1], l.Mins[2]), new Vec3(l.Maxs[0], l.Maxs[1], l.Maxs[2]));
    }

    /// <summary>
    /// One stored ambient sample encoded and turned to a placement: the
    /// cube face for world direction <i>d</i> is the stored face for the
    /// room direction the turn takes to <i>d</i>, and the position byte
    /// along a world axis is the stored byte along the room axis the turn
    /// takes to it, flipped (255 minus it) where that axis is negated. Both
    /// are exact: faces move whole, and <c>255 - b</c> is the byte for one
    /// minus its fraction of the box.
    /// </summary>
    internal static DLeafAmbientLighting TurnSample(RoomLightRange range, int sample, int rotation)
    {
        // Room faces +x, -x, +y, -y, +z, -z; the world face at each index.
        ReadOnlySpan<int> source = (rotation & 3) switch
        {
            0 => [0, 1, 2, 3, 4, 5],
            1 => [3, 2, 0, 1, 4, 5],
            2 => [1, 0, 3, 2, 4, 5],
            _ => [2, 3, 1, 0, 4, 5],
        };

        DLeafAmbientLighting turned = default;
        Span<ColorRgbExp32> colour = stackalloc ColorRgbExp32[1];
        for (int side = 0; side < 6; side++)
        {
            RoomLighting.EncodeColors(range.AmbientCubes.AsSpan((sample * RoomLighting.AmbientHalves) + (source[side] * 3), 3), colour);
            turned.Cube.Color[side] = colour[0];
        }

        byte x = range.AmbientPositions[sample * 4];
        byte y = range.AmbientPositions[(sample * 4) + 1];
        (turned.X, turned.Y) = (rotation & 3) switch
        {
            0 => (x, y),
            1 => ((byte)(255 - y), x),
            2 => ((byte)(255 - x), (byte)(255 - y)),
            _ => (y, (byte)(255 - x)),
        };
        turned.Z = range.AmbientPositions[(sample * 4) + 2];
        turned.Pad = range.AmbientPositions[(sample * 4) + 3];
        return turned;
    }

    /// <summary>
    /// The sky flags of a lit level whose library has a sun, as vrad's three
    /// passes set them: pass one from the rooms (each leaf holding a sky
    /// face), pass two over the linked PVS (a leaf that is not solid and not
    /// already 3D sky takes 2D sky when it sees a 2D sky leaf, and 3D sky,
    /// clearing 2D, when it sees a 3D one), and pass three's radial rescue
    /// never, since a room's vis is never radial. Every other flag stays.
    /// </summary>
    /// <remarks>
    /// Pass two is done per cluster, not per leaf pair as vrad walks it: a
    /// leaf sees a sky leaf when the leaf's cluster row holds the sky leaf's
    /// cluster, so the sky clusters are gathered into two row masks and each
    /// row is tested against them once. vrad's walk skips the leaf itself,
    /// which changes nothing: a leaf that is itself 3D sky is not tested, and
    /// one that is 2D sky already has the flag it could gain.
    /// </remarks>
    private static void SkyFlags(BspData linked, RoomPlan[] plans, byte[] pvs, int rowBytes)
    {
        byte[] bytes = linked[BspLump.Leafs].Data.ToArray();
        Span<DLeaf> leafs = MemoryMarshal.Cast<byte, DLeaf>(bytes.AsSpan());
        LeafFlags[] flags = new LeafFlags[leafs.Length];
        for (int l = 0; l < leafs.Length; l++)
        {
            flags[l] = leafs[l].GetFlags() & ~(LeafFlags.Sky | LeafFlags.Sky2D);
        }

        foreach (RoomPlan plan in plans)
        {
            foreach ((int leaf, LeafFlags sky) in plan.Lighting!.SkyLeaves)
            {
                if (plan.Models is not { } models || RoomModelLayout.Kept(models.OmittedLeaves, leaf) >= 0)
                {
                    flags[plan.LinkedLeaf(leaf)] |= sky;
                }
            }
        }

        byte[] sky3 = new byte[Math.Max(rowBytes, 1)];
        byte[] sky2 = new byte[Math.Max(rowBytes, 1)];
        for (int l = 0; l < leafs.Length; l++)
        {
            int cluster = leafs[l].Cluster;
            if (cluster < 0)
            {
                continue;
            }

            if ((flags[l] & LeafFlags.Sky) != 0)
            {
                sky3[cluster >> 3] |= (byte)(1 << (cluster & 7));
            }

            if ((flags[l] & LeafFlags.Sky2D) != 0)
            {
                sky2[cluster >> 3] |= (byte)(1 << (cluster & 7));
            }
        }

        int clusters = rowBytes == 0 ? 0 : pvs.Length / rowBytes;
        bool[] sees3 = new bool[clusters];
        bool[] sees2 = new bool[clusters];
        for (int c = 0; c < clusters; c++)
        {
            ReadOnlySpan<byte> row = pvs.AsSpan(c * rowBytes, rowBytes);
            for (int b = 0; b < rowBytes; b++)
            {
                sees3[c] |= (row[b] & sky3[b]) != 0;
                sees2[c] |= (row[b] & sky2[b]) != 0;
            }
        }

        for (int l = 0; l < leafs.Length; l++)
        {
            int cluster = leafs[l].Cluster;
            if ((flags[l] & LeafFlags.Sky) != 0 || (leafs[l].Contents & (int)BrushContents.Solid) != 0 || cluster < 0 || cluster >= clusters)
            {
                continue;
            }

            if (sees2[cluster])
            {
                flags[l] |= LeafFlags.Sky2D;
            }

            if (sees3[cluster])
            {
                flags[l] = (flags[l] | LeafFlags.Sky) & ~LeafFlags.Sky2D;
            }
        }

        for (int l = 0; l < leafs.Length; l++)
        {
            leafs[l].SetAreaFlags(leafs[l].GetArea(), flags[l]);
        }

        linked.SetLump(BspLump.Leafs, bytes, linked[BspLump.Leafs].Version);
    }

    /// <summary>
    /// The level's static prop lighting files when it is lit: for every kept
    /// prop the bake lit, its <c>.vhv</c> encoded from the stored turn of its
    /// placement (<see cref="StaticPropLighting.EncodeVhv"/>, from the linear
    /// colours, which give back vrad's bytes) and named by its linked index.
    /// </summary>
    internal static List<(string Room, string Name, byte[] Data)> BakedPropFiles(ResolvedPlacement[] resolved, LevelProps props)
    {
        List<(string, string, byte[])> files = [];
        for (int k = 0; k < props.Props.Count; k++)
        {
            LinkedProp prop = props.Props[k];
            ResolvedPlacement placement = resolved[prop.Placement];
            if (placement.Room.LightingOfCompile is not { } lighting)
            {
                continue;
            }

            RoomLightingPayload payload = lighting.For(placement.Instance.Placement.NormalizedRotation);
            foreach (bool hdr in (ReadOnlySpan<bool>)[false, true])
            {
                if (payload.Range(hdr)?.Props.FirstOrDefault(p => p.Prop == prop.RoomProp) is not { } colours)
                {
                    continue;
                }

                List<(int Lod, Vec3[] Colors)> meshes = [];
                int at = 0;
                for (int m = 0; m < colours.Lods.Length; m++)
                {
                    Vec3[] mesh = new Vec3[colours.Counts[m]];
                    for (int v = 0; v < mesh.Length; v++, at += 3)
                    {
                        mesh[v] = new Vec3((float)colours.Colors[at], (float)colours.Colors[at + 1], (float)colours.Colors[at + 2]);
                    }

                    meshes.Add((colours.Lods[m], mesh));
                }

                files.Add((placement.Room.Definition.Name, StaticPropLighting.FileName(k, hdr), StaticPropLighting.EncodeVhv(colours.Checksum, meshes)));
            }
        }

        return files;
    }
}
