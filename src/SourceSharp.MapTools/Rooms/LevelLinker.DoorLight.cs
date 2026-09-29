//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// What the door light adds to one placement of a level, in one range, in
/// its room's own frame: per face the light of each neighbour's style
/// (the neighbour's placement and its room's style number, which the link
/// renumbers), per leaf ambient sample of its stored turn a cube, per lit
/// prop its vertices' colours.
/// </summary>
internal sealed class DoorLightTerms
{
    /// <summary>Per room face, the terms: the placement they came from, its room's style, and the pages (per page and luxel, three floats).</summary>
    public Dictionary<int, List<(int Source, int Style, float[] Pages)>> Faces { get; } = [];

    /// <summary>Per ambient sample of the placement's stored turn, its six faces' additions, or null when nothing reached it.</summary>
    public Vec3[]?[] Ambient { get; set; } = [];

    /// <summary>Per lit prop (by the room's prop index), its vertices' additions.</summary>
    public Dictionary<int, Vec3[]> Props { get; } = [];

    /// <summary>Whether anything reached the placement.</summary>
    public bool Any => Faces.Count > 0 || Props.Count > 0 || Ambient.Any(a => a is not null);
}

/// <summary>
/// A level's door light (the rooms design, 9.1 part 4): per placement and
/// range, what its jointed neighbours' light adds (<see cref="DoorLightTerms"/>).
/// </summary>
/// <param name="Ldr">Per placement, the LDR terms, or null where nothing reached it.</param>
/// <param name="Hdr">Per placement, the HDR terms.</param>
internal sealed record LevelDoorLight(DoorLightTerms?[] Ldr, DoorLightTerms?[] Hdr)
{
    /// <summary>One range's terms of one placement, or null.</summary>
    public DoorLightTerms? For(bool hdr, int placement) => (hdr ? Hdr : Ldr)[placement];
}

public static partial class LevelLinker
{
    /// <summary>
    /// The door light of a lit level: for every joint, both ways, what the
    /// room across it sends through the door into this one, from the two
    /// rooms' door light (<see cref="RoomDoorLight"/>); null when no two
    /// jointed rooms both carry it (a pack built before it, or with it off).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a placement B and each joint to a neighbour A, in B's frame,
    /// door-local to B's socket:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Direct.</b> Every source A's capture recorded for the turn A
    /// is placed at (its lights, of every style, and the stand-ins for its
    /// surfaces and sky) is evaluated at every sample cell of every face of
    /// B that sees B's opening, at <see cref="DoorLightMath.Subsamples"/>
    /// squared points of the cell (<see cref="DoorLightMath.Through"/>: only
    /// through a cell of B's opening the point sees and a cell of A's the
    /// source reaches), for the face's normal and its bump normals; a luxel
    /// is the mean of the cells around it. No ray is traced: both sides'
    /// visibility was traced at pack time.</item>
    /// <item><b>Ambient.</b> The stand-ins alone, at every leaf ambient
    /// sample of B's stored turn that sees B's opening, one cube face at a
    /// time: vrad's cubes gather what rays meet (surfaces and sky), and the
    /// engine adds lights at run time.</item>
    /// <item><b>Bounced.</b> When B stores responses, the style-0 sources'
    /// flux through A's opening is shared over B's response emitters
    /// (<see cref="DoorLightMath.Project"/>) and each emitter's response
    /// added: the light bounced onto B's faces (from half resolution, each
    /// luxel the value of the half-resolution luxel it lies in), each B
    /// sample the cube of the emitter's sample nearest it in its leaf, and
    /// the props' colours. Bounced light is style 0 alone, as vrad bounces
    /// only style 0.</item>
    /// </list>
    /// <para>
    /// Reach is one door (D3): nothing crosses a second joint. Every
    /// placement's terms are its own work item, reading only the rooms'
    /// stored data, so the terms are the same at any thread count.
    /// </para>
    /// </remarks>
    internal static async Task<LevelDoorLight?> PlanDoorLightAsync(
        ResolvedPlacement[] resolved, LevelLight lit, CompileParallelism parallelism, CancellationToken cancellationToken)
    {
        Dictionary<(int X, int Y), ResolvedPlacement> byCell = new(resolved.Length);
        foreach (ResolvedPlacement placement in resolved)
        {
            byCell[(placement.Instance.Placement.CellX, placement.Instance.Placement.CellY)] = placement;
        }

        // Each receiving placement's joints to a neighbour with door light.
        List<(int Mine, int Placement, int Theirs)>[] joints = new List<(int, int, int)>[resolved.Length];
        bool any = false;
        for (int b = 0; b < resolved.Length; b++)
        {
            joints[b] = [];
            ResolvedPlacement mine = resolved[b];
            if (mine.Room.DoorLightOfCompile is null)
            {
                continue;
            }

            foreach ((string socketName, string neighborSocket) in mine.Instance.Joints)
            {
                RoomSocket socket = Socket(mine.Room, socketName, mine.Instance);
                RoomTransform transform = new(mine.Instance.Placement, mine.Room.Definition.CellSize);
                (int axis, int sign) = transform.WorldNormal(socket.Facing);
                (int X, int Y) cell = (mine.Instance.Placement.CellX + (axis == 0 ? sign : 0), mine.Instance.Placement.CellY + (axis == 1 ? sign : 0));
                if (!byCell.TryGetValue(cell, out ResolvedPlacement? other) || other.Room.DoorLightOfCompile is null)
                {
                    continue;
                }

                RoomSocket theirs = Socket(other.Room, neighborSocket, other.Instance);
                joints[b].Add((SocketIndex(mine.Room.Definition, socket.Name), other.Index, SocketIndex(other.Room.Definition, theirs.Name)));
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        // What depends on the room alone, once a room.
        Dictionary<string, DoorFaceCells?[]> cells = new(StringComparer.Ordinal);
        Dictionary<string, DLeaf[]> leaves = new(StringComparer.Ordinal);
        foreach (ResolvedPlacement placement in resolved)
        {
            string name = placement.Room.Definition.Name;
            if (placement.Room.DoorLightOfCompile is not null && !cells.ContainsKey(name))
            {
                cells[name] = RoomDoorLight.FaceCellsOf(placement.Room.Bsp);
                leaves[name] = AmbientScene.ReadLeaves(placement.Room.Bsp);
            }
        }

        DoorLightTerms?[] ldr = new DoorLightTerms?[resolved.Length];
        DoorLightTerms?[] hdr = new DoorLightTerms?[resolved.Length];
        using (WorkQueue queue = new(parallelism))
        {
            await queue.RunAsync(
                resolved.Length,
                (b, _) =>
                {
                    if (joints[b].Count == 0)
                    {
                        return;
                    }

                    if (lit.Ldr)
                    {
                        ldr[b] = Receive(resolved, b, joints[b], hdr: false, cells, leaves);
                    }

                    if (lit.Hdr)
                    {
                        hdr[b] = Receive(resolved, b, joints[b], hdr: true, cells, leaves);
                    }
                },
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        return new LevelDoorLight(ldr, hdr);
    }

    /// <summary>One placement's terms in one range, from each of its joints in turn.</summary>
    private static DoorLightTerms? Receive(
        ResolvedPlacement[] resolved,
        int b,
        List<(int Mine, int Placement, int Theirs)> joints,
        bool hdr,
        Dictionary<string, DoorFaceCells?[]> cells,
        Dictionary<string, DLeaf[]> leaves)
    {
        ResolvedPlacement receiver = resolved[b];
        RoomObject room = receiver.Room;
        RoomDoorLight door = room.DoorLightOfCompile!;
        RoomLighting lighting = room.LightingOfCompile!;
        if (door.Range(hdr) is not { } range || lighting.For(receiver.Instance.Placement.NormalizedRotation).Range(hdr) is not { } own)
        {
            return null;
        }

        string name = room.Definition.Name;
        int payload = receiver.Instance.Placement.NormalizedRotation % lighting.RotationCount;
        (int Leaf, Vec3 Position)[] samples = RoomDoorLight.SamplePositions(own, leaves[name]);
        DoorLightTerms terms = new() { Ambient = new Vec3[]?[samples.Length] };
        float cell = room.Definition.CellSize, wall = room.Definition.Kit.Depth;
        double[] nodeFlux = DoorLightMath.NodeFlux(room.Definition.Kit.Width, room.Definition.Kit.Height, cell, wall);
        foreach ((int mine, int a, int theirs) in joints)
        {
            ResolvedPlacement sender = resolved[a];
            RoomDoorLight senderDoor = sender.Room.DoorLightOfCompile!;
            RoomLighting senderLighting = sender.Room.LightingOfCompile!;
            if (senderDoor.Range(hdr) is not { } senderRange)
            {
                continue;
            }

            int senderPayload = sender.Instance.Placement.NormalizedRotation % senderLighting.RotationCount;
            DoorFrame from = DoorFrame.Of(sender.Room.Definition, sender.Room.Definition.Sockets[theirs]);
            DoorFrame to = DoorFrame.Of(room.Definition, room.Definition.Sockets[mine]);
            DoorSource[] sources = [.. senderRange.Captures[senderPayload][theirs].Select(s => DoorLightMath.ToNeighbour(s, from))];
            if (sources.Length == 0)
            {
                continue;
            }

            Direct(terms, a, sources, door.Receivers[mine], cells[name], to);
            Ambient(terms, sources, range.Ambient[payload][mine], samples, to);
            if (range.Responses[mine].Length > 0)
            {
                Bounced(terms, a, sources, range.Responses[mine], nodeFlux, room, to, samples);
            }
        }

        return terms.Any ? terms : null;
    }

    /// <summary>The direct light of each source at every receiving face's cells, turned into luxels, by the source's style.</summary>
    private static void Direct(DoorLightTerms terms, int sender, DoorSource[] sources, DoorReceiverFace[] faces, DoorFaceCells?[] cells, DoorFrame to)
    {
        int[] styles = [.. sources.Select(s => s.Style).Distinct().Order()];
        const int n = DoorLightMath.Subsamples;
        Span<Vec3> normals = stackalloc Vec3[4];
        foreach (DoorReceiverFace receiver in faces)
        {
            DoorFaceCells face = cells[receiver.Face]!;
            int pages = face.Pages;
            normals[0] = to.DirectionToLocal(face.Normal);
            for (int k = 0; k < face.Bumps.Length; k++)
            {
                normals[k + 1] = to.DirectionToLocal(face.Bumps[k]);
            }

            UInt128[] masks = receiver.Seen.Masks();
            foreach (int style in styles)
            {
                float[] cellLight = new float[face.Count * pages * 3];
                bool lit = false;
                for (int c = 0; c < face.Count; c++)
                {
                    if (masks[c] == UInt128.Zero)
                    {
                        continue;
                    }

                    for (int sy = 0; sy < n; sy++)
                    {
                        for (int sx = 0; sx < n; sx++)
                        {
                            Vec3 point = to.ToLocal(face.Point(c, ((sx + 0.5f) / n) - 0.5f, ((sy + 0.5f) / n) - 0.5f));
                            foreach (DoorSource source in sources)
                            {
                                if (source.Style != style)
                                {
                                    continue;
                                }

                                for (int page = 0; page < pages; page++)
                                {
                                    float e = DoorLightMath.Through(source, point, normals[page], to.Width, to.Height, to.Depth, masks[c]);
                                    if (e > 0)
                                    {
                                        Vec3 add = source.Intensity * (e / (n * n));
                                        int at = ((c * pages) + page) * 3;
                                        cellLight[at] += add.X;
                                        cellLight[at + 1] += add.Y;
                                        cellLight[at + 2] += add.Z;
                                        lit = true;
                                    }
                                }
                            }
                        }
                    }
                }

                if (lit)
                {
                    float[] luxels = new float[pages * face.Width * face.Height * 3];
                    DoorLightMath.CellsToLuxels(face, cellLight, luxels);
                    AddFace(terms, receiver.Face, sender, style, luxels);
                }
            }
        }
    }

    /// <summary>
    /// A leaf ambient cube's value per luxel value of the same light: vrad
    /// gathers a cube from the colours it reads off what its rays meet (the
    /// sky ambient's intensity, a surface's average times its reflectivity)
    /// in world-light units, where a lightmap holds 255 of them, so a
    /// stand-in's light, measured in luxels, is a 255th of that in a cube.
    /// </summary>
    internal const float AmbientPerLuxel = 1f / 255f;

    /// <summary>The stand-ins' light at every leaf ambient sample that sees the opening, a cube face at a time (<see cref="AmbientPerLuxel"/>).</summary>
    private static void Ambient(DoorLightTerms terms, DoorSource[] sources, DoorSeen seen, (int Leaf, Vec3 Position)[] samples, DoorFrame to)
    {
        UInt128[] masks = seen.Masks();
        Span<Vec3> axes = stackalloc Vec3[6];
        ReadOnlySpan<Vec3> room = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        for (int side = 0; side < 6; side++)
        {
            axes[side] = to.DirectionToLocal(room[side]);
        }

        for (int s = 0; s < samples.Length && s < masks.Length; s++)
        {
            if (masks[s] == UInt128.Zero)
            {
                continue;
            }

            Vec3 point = to.ToLocal(samples[s].Position);
            foreach (DoorSource source in sources)
            {
                if (!source.StandIn)
                {
                    continue;
                }

                for (int side = 0; side < 6; side++)
                {
                    float e = DoorLightMath.Through(source, point, axes[side], to.Width, to.Height, to.Depth, masks[s]);
                    if (e > 0)
                    {
                        (terms.Ambient[s] ??= new Vec3[6])[side] += source.Intensity * (e * AmbientPerLuxel);
                    }
                }
            }
        }
    }

    /// <summary>The style-0 sources' flux shared over the response emitters, and each emitter's response added.</summary>
    private static void Bounced(
        DoorLightTerms terms,
        int sender,
        DoorSource[] sources,
        DoorResponseEmitter[] emitters,
        double[] nodeFlux,
        RoomObject room,
        DoorFrame to,
        (int Leaf, Vec3 Position)[] samples)
    {
        float cell = room.Definition.CellSize, wall = room.Definition.Kit.Depth;
        Span<Vec3> shares = stackalloc Vec3[DoorLightMath.EmitterCount];
        shares.Clear();
        foreach (DoorSource source in sources)
        {
            if (source.Style != 0)
            {
                continue;
            }

            double flux = DoorLightMath.SourceFlux(source with { Intensity = new Vec3(1, 1, 1) }, to.Width, to.Height, to.Depth);
            if (flux > 0)
            {
                DoorLightMath.Project(source, source.Intensity * (float)flux, nodeFlux, cell, wall, shares);
            }
        }

        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(room.Bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(room.Bsp[BspLump.TexInfo]);
        for (int e = 0; e < emitters.Length; e++)
        {
            Vec3 share = shares[e];
            if (share == Vec3.Zero)
            {
                continue;
            }

            foreach (DoorResponseFace response in emitters[e].Faces)
            {
                DFace face = faces[response.Face];
                int w = face.LightmapTextureSizeInLuxels[0] + 1, h = face.LightmapTextureSizeInLuxels[1] + 1;
                int pages = RoomDoorLight.Pages(texInfos, face);
                (int hw, int hh) = RoomDoorLight.Half(w, h);
                float[] luxels = new float[pages * w * h * 3];
                for (int page = 0; page < pages; page++)
                {
                    for (int t = 0; t < h; t++)
                    {
                        for (int u = 0; u < w; u++)
                        {
                            int from = ((page * hw * hh) + ((t / 2) * hw) + (u / 2)) * 3;
                            int at = ((page * w * h) + (t * w) + u) * 3;
                            luxels[at] = (float)response.Values[from] * share.X;
                            luxels[at + 1] = (float)response.Values[from + 1] * share.Y;
                            luxels[at + 2] = (float)response.Values[from + 2] * share.Z;
                        }
                    }
                }

                AddFace(terms, response.Face, sender, 0, luxels);
            }

            AddAmbient(terms, emitters[e].Ambient, samples, share);
            foreach (DoorResponseProp prop in emitters[e].Props)
            {
                int vertices = prop.Colours.Length / 3;
                if (!terms.Props.TryGetValue(prop.Prop, out Vec3[]? colours))
                {
                    terms.Props[prop.Prop] = colours = new Vec3[vertices];
                }

                for (int v = 0; v < vertices; v++)
                {
                    colours[v] += new Vec3(
                        (float)prop.Colours[v * 3] * share.X,
                        (float)prop.Colours[(v * 3) + 1] * share.Y,
                        (float)prop.Colours[(v * 3) + 2] * share.Z);
                }
            }
        }
    }

    /// <summary>Each sample the cube of the response sample nearest it in its own leaf, scaled.</summary>
    private static void AddAmbient(DoorLightTerms terms, DoorResponseSample[] response, (int Leaf, Vec3 Position)[] samples, Vec3 share)
    {
        if (response.Length == 0)
        {
            return;
        }

        Dictionary<int, List<DoorResponseSample>> byLeaf = [];
        foreach (DoorResponseSample sample in response)
        {
            if (!byLeaf.TryGetValue(sample.Leaf, out List<DoorResponseSample>? list))
            {
                byLeaf[sample.Leaf] = list = [];
            }

            list.Add(sample);
        }

        for (int s = 0; s < samples.Length; s++)
        {
            if (!byLeaf.TryGetValue(samples[s].Leaf, out List<DoorResponseSample>? list))
            {
                continue;
            }

            DoorResponseSample nearest = list[0];
            float best = (nearest.Position - samples[s].Position).LengthSquared();
            for (int i = 1; i < list.Count; i++)
            {
                float d = (list[i].Position - samples[s].Position).LengthSquared();
                if (d < best)
                {
                    best = d;
                    nearest = list[i];
                }
            }

            Vec3[] cube = terms.Ambient[s] ??= new Vec3[6];
            for (int side = 0; side < 6; side++)
            {
                cube[side] += new Vec3(
                    (float)nearest.Cube[side * 3] * share.X,
                    (float)nearest.Cube[(side * 3) + 1] * share.Y,
                    (float)nearest.Cube[(side * 3) + 2] * share.Z);
            }
        }
    }

    /// <summary>
    /// The lighting lump with a block of its own for every face the door
    /// light reached, after the shared blocks: the face's stored styles
    /// (their luxels decoded from the bake's linear halves) with each
    /// neighbour's door terms summed into the slot of the same linked style,
    /// or into a new slot; at most four, the weakest door-only styles dropped
    /// with a warning. The per-style averages before the luxels are each
    /// style's median, as vrad writes them. The face's offset and styles in
    /// <paramref name="rangeFaces"/> are moved to the new block.
    /// </summary>
    private static byte[] DoorFaces(
        BspData linked,
        List<(RoomPlan Plan, int Face)> faceOwners,
        DFace[] rangeFaces,
        byte[] lighting,
        bool hdr,
        LevelDoorLight door,
        LevelLightStyles styles,
        List<string>? warnings)
    {
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(linked[BspLump.TexInfo]);
        using MemoryStream extra = new();
        List<float> red = [], green = [], blue = [];
        for (int i = 0; i < rangeFaces.Length; i++)
        {
            (RoomPlan plan, int roomFace) = faceOwners[i];
            if (door.For(hdr, plan.Placement.Index) is not { } terms
                || !terms.Faces.TryGetValue(roomFace, out List<(int Source, int Style, float[] Pages)>? added)
                || plan.Lit!.Range(hdr) is not { } own
                || own.LightOffsets[roomFace] < 0)
            {
                continue;
            }

            DFace face = rangeFaces[i];
            int luxels = RoomDoorLight.PageLuxels(face);
            int block = RoomDoorLight.Pages(texInfos, face) * luxels;
            List<(int Style, float[] Values, bool Door)> slots = [];
            int stored = own.LightOffsets[roomFace] / RoomLighting.LuxelBytes;
            for (int k = 0; k < 4 && own.Styles[(roomFace * 4) + k] != 255; k++)
            {
                float[] values = new float[block * 3];
                for (int v = 0; v < values.Length; v++)
                {
                    values[v] = (float)own.Luxels[((stored + (k * block)) * 3) + v];
                }

                slots.Add((styles.Remap(plan.Placement.Index, own.Styles[(roomFace * 4) + k]), values, false));
            }

            foreach ((int source, int style, float[] pages) in added)
            {
                int linkedStyle = styles.Remap(source, style);
                int slot = slots.FindIndex(x => x.Style == linkedStyle);
                if (slot < 0)
                {
                    slots.Add((linkedStyle, new float[block * 3], true));
                    slot = slots.Count - 1;
                }

                float[] values = slots[slot].Values;
                for (int v = 0; v < values.Length && v < pages.Length; v++)
                {
                    values[v] += pages[v];
                }
            }

            while (slots.Count > 4)
            {
                int weakest = -1;
                double least = double.MaxValue;
                for (int k = 0; k < slots.Count; k++)
                {
                    double sum = slots[k].Door ? slots[k].Values.Sum(v => (double)v) : double.MaxValue;
                    if (sum < least)
                    {
                        least = sum;
                        weakest = k;
                    }
                }

                warnings?.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"room {plan.Placement.Room.Definition.Name} at cell ({plan.Placement.Instance.Placement.CellX}, {plan.Placement.Instance.Placement.CellY}):"
                    + $" face {roomFace} would need {slots.Count} light styles with its neighbours' door light; style {slots[weakest].Style} was left out."));
                slots.RemoveAt(weakest);
            }

            // The averages, last style first, then the pages, style after style.
            long start = lighting.LongLength + extra.Length;
            for (int k = slots.Count - 1; k >= 0; k--)
            {
                red.Clear();
                green.Clear();
                blue.Clear();
                for (int v = 0; v < luxels; v++)
                {
                    red.Add(slots[k].Values[v * 3]);
                    green.Add(slots[k].Values[(v * 3) + 1]);
                    blue.Add(slots[k].Values[(v * 3) + 2]);
                }

                Write(extra, StockLightColor.Encode(new Vec3(
                    Rad.Final.FinalLightFace.Median(red), Rad.Final.FinalLightFace.Median(green), Rad.Final.FinalLightFace.Median(blue))));
            }

            foreach ((_, float[] values, _) in slots)
            {
                for (int v = 0; v < block; v++)
                {
                    Write(extra, StockLightColor.Encode(new Vec3(values[v * 3], values[(v * 3) + 1], values[(v * 3) + 2])));
                }
            }

            long offset = start + (slots.Count * RoomLighting.LuxelBytes);
            Limit(plan, "lighting bytes", offset + ((long)block * slots.Count * RoomLighting.LuxelBytes), int.MaxValue);

            rangeFaces[i].LightOfs = (int)offset;
            for (int k = 0; k < 4; k++)
            {
                rangeFaces[i].Styles[k] = k < slots.Count ? (byte)slots[k].Style : (byte)255;
            }
        }

        if (extra.Length == 0)
        {
            return lighting;
        }

        byte[] all = new byte[lighting.LongLength + extra.Length];
        lighting.CopyTo(all, 0);
        extra.ToArray().CopyTo(all, lighting.Length);
        return all;

        static void Write(MemoryStream to, ColorRgbExp32 c)
        {
            to.WriteByte(c.R);
            to.WriteByte(c.G);
            to.WriteByte(c.B);
            to.WriteByte(unchecked((byte)c.Exponent));
        }
    }

    /// <summary>Adds a face's pages to its terms from one neighbour and style, summing with what that neighbour and style already gave it.</summary>
    private static void AddFace(DoorLightTerms terms, int face, int sender, int style, float[] luxels)
    {
        if (!terms.Faces.TryGetValue(face, out List<(int Source, int Style, float[] Pages)>? list))
        {
            terms.Faces[face] = list = [];
        }

        int at = list.FindIndex(t => t.Source == sender && t.Style == style);
        if (at < 0)
        {
            list.Add((sender, style, luxels));
            return;
        }

        float[] sum = list[at].Pages;
        for (int i = 0; i < sum.Length; i++)
        {
            sum[i] += luxels[i];
        }
    }
}
