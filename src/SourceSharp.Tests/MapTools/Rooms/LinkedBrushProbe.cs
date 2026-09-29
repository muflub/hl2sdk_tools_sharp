//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Reads the brushes of a linked map back as geometry: which room brushes a
/// placement strips, a brush as its resolved planes and contents, a collision
/// ledge's box, so facts can compare a linked brush with the room brush it
/// came from without trusting any index the link wrote.
/// </summary>
internal static class LinkedBrushProbe
{
    /// <summary>IVP's length unit is the metre; the map's is the inch.</summary>
    private const float MetersPerInch = 0.0254f;

    /// <summary>The room-local plug brushes a placement's joints strip, from the room's census.</summary>
    public static HashSet<int> Stripped(RoomObject room, RoomInstance instance)
    {
        IReadOnlyList<SocketCensus> sockets = LevelLinker.ComputeShared(room).Sockets;
        HashSet<int> stripped = [];
        foreach ((string socket, _) in instance.Joints)
        {
            int index = -1;
            for (int s = 0; s < room.Definition.Sockets.Count && index < 0; s++)
            {
                index = room.Definition.Sockets[s].Name == socket ? s : -1;
            }

            stripped.UnionWith(sockets[index].StrippedBrushes);
        }

        return stripped;
    }

    /// <summary>
    /// A brush as its contents and its sides' planes (normal and distance, in
    /// side order) and bevel flags: equal for two brushes exactly when a
    /// trace cannot tell them apart.
    /// </summary>
    public static string Geometry(DBrush brush, ReadOnlySpan<DBrushSide> sides, Func<int, DPlane> plane)
    {
        System.Text.StringBuilder text = new();
        text.Append(brush.Contents).Append(':');
        for (int s = 0; s < brush.NumSides; s++)
        {
            DBrushSide side = sides[brush.FirstSide + s];
            DPlane p = plane(side.PlaneNum);
            text.Append(LevelLinker.PlaneKey.Of(p.Normal, p.Dist, p.Type)).Append(" b").Append(side.Bevel);
        }

        return text.ToString();
    }

    /// <summary>A linked brush's geometry (<see cref="Geometry"/>) through the linked plane table.</summary>
    public static string Geometry(BspData bsp, int brush)
    {
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        return Geometry(
            BspStructView.As<DBrush>(bsp[BspLump.Brushes])[brush],
            BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]),
            p => planes[p]);
    }

    /// <summary>The box an axial brush's sides bound.</summary>
    public static Box BrushBox(BspData bsp, int brush)
    {
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        DBrush b = BspStructView.As<DBrush>(bsp[BspLump.Brushes])[brush];
        ReadOnlySpan<DBrushSide> sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]);
        float[] lo = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
        float[] hi = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        for (int s = 0; s < b.NumSides; s++)
        {
            DPlane p = planes[sides[b.FirstSide + s].PlaneNum];
            float[] n = [p.Normal.X, p.Normal.Y, p.Normal.Z];
            for (int a = 0; a < 3; a++)
            {
                if (n[a] == 1 && n[(a + 1) % 3] == 0 && n[(a + 2) % 3] == 0)
                {
                    hi[a] = MathF.Min(hi[a], p.Dist);
                }
                else if (n[a] == -1 && n[(a + 1) % 3] == 0 && n[(a + 2) % 3] == 0)
                {
                    lo[a] = MathF.Max(lo[a], -p.Dist);
                }
            }
        }

        return new Box(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
    }

    /// <summary>Every leaf ledge of a linked map's world collision, with the contents class of its solid.</summary>
    public static List<(int Contents, IvpCompactLedge Ledge)> Ledges(BspData bsp)
    {
        List<(int, IvpCompactLedge)> ledges = [];
        foreach ((int contents, byte[] blob) in LevelLinker.ReadRoomCollide(bsp, "linked").Solids)
        {
            foreach (IvpCompactLedge ledge in IvpCollideQueries.Leaves(IvpCollideQueries.Surface(blob)))
            {
                ledges.Add((contents, ledge));
            }
        }

        return ledges;
    }

    /// <summary>A ledge's points' box, back in map axes and inches.</summary>
    public static Box LedgeBox(IvpCompactLedge ledge)
    {
        Vec3 lo = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vec3 hi = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int p = 0; p < ledge.PointCount; p++)
        {
            (float x, float y, float z) = ledge.Point(p);
            Vec3 map = new(x / MetersPerInch, z / MetersPerInch, -y / MetersPerInch);
            lo = new Vec3(MathF.Min(lo.X, map.X), MathF.Min(lo.Y, map.Y), MathF.Min(lo.Z, map.Z));
            hi = new Vec3(MathF.Max(hi.X, map.X), MathF.Max(hi.Y, map.Y), MathF.Max(hi.Z, map.Z));
        }

        return new Box(lo, hi);
    }

    /// <summary>Whether two boxes agree to within a tolerance on every bound.</summary>
    public static bool Near(Box a, Box b, float tolerance) =>
        MathF.Abs(a.Mins.X - b.Mins.X) <= tolerance && MathF.Abs(a.Mins.Y - b.Mins.Y) <= tolerance
        && MathF.Abs(a.Mins.Z - b.Mins.Z) <= tolerance && MathF.Abs(a.Maxs.X - b.Maxs.X) <= tolerance
        && MathF.Abs(a.Maxs.Y - b.Maxs.Y) <= tolerance && MathF.Abs(a.Maxs.Z - b.Maxs.Z) <= tolerance;

    /// <summary>
    /// The map as its plug brushes used to be kept: the linked map with each
    /// placement's stripped plug brushes put back where the room had them,
    /// empty (contents 0) with the room's sides, and every leaf brush entry
    /// renumbered to match. It is the brush layout the link wrote before it
    /// dropped the plugs, rebuilt from the rooms, so a fact can trace both.
    /// </summary>
    public static BspData WithPlugsKept(LinkedLevel link, RoomLibrary library) => Rebuild(link, library).Map;

    /// <summary>
    /// Per linked brush, its index in the map with the plugs kept
    /// (<see cref="WithPlugsKept"/>).
    /// </summary>
    public static int[] PlugsKeptNumbering(LinkedLevel link, RoomLibrary library) => Rebuild(link, library).NewToOld;

    /// <summary>
    /// The linked collision lump with every leaf ledge's client data put
    /// back to the brush numbering that kept the plugs: the lump the link
    /// wrote before, if nothing but the numbering moved.
    /// </summary>
    public static byte[] CollisionWithNumbering(BspData bsp, int[] newToOld)
    {
        List<PhysCollideModel> models = [];
        foreach (PhysCollideModel model in PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span))
        {
            List<byte[]> solids = [];
            foreach (byte[] solid in model.Solids)
            {
                byte[] copy = (byte[])solid.Clone();
                foreach (int at in IvpCollideQueries.LeafOffsets(IvpCollideQueries.Surface(copy)))
                {
                    Span<byte> client = copy.AsSpan(VphyWriter.HeaderSize + at + 4, 4);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                        client, newToOld[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(client)]);
                }

                solids.Add(copy);
            }

            models.Add(model with { Solids = solids });
        }

        return PhysCollideLump.Write(models);
    }

    private static (BspData Map, int[] NewToOld) Rebuild(LinkedLevel link, RoomLibrary library)
    {
        BspData bsp = link.Bsp;
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        Dictionary<LevelLinker.PlaneKey, int> planeIndex = [];
        for (int p = 0; p < planes.Length; p++)
        {
            planeIndex.TryAdd(LevelLinker.PlaneKey.Of(planes[p].Normal, planes[p].Dist, planes[p].Type), p);
        }

        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        Dictionary<string, int> infoIndex = [];
        for (int t = infos.Length - 1; t >= 0; t--)
        {
            infoIndex[InfoKey(bsp, infos[t])] = t;
        }

        DBrush[] brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
        DBrushSide[] sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
        List<DBrush> oldBrushes = [];
        List<DBrushSide> oldSides = [];
        int[] newToOld = new int[brushes.Length];
        int cursor = 0;
        foreach (RoomInstance instance in link.Plan.Layout.Rooms)
        {
            RoomObject room = library.Get(instance.Placement.Room);
            HashSet<int> stripped = Stripped(room, instance);
            RoomTransform transform = new(instance.Placement, link.Plan.Layout.CellSize);
            (DPlane[] moved, bool[] swapped) = LevelLinker.TransformPlanes(BspStructView.As<DPlane>(room.Bsp[BspLump.Planes]).ToArray(), transform);
            DBrush[] roomBrushes = BspStructView.As<DBrush>(room.Bsp[BspLump.Brushes]).ToArray();
            DBrushSide[] roomSides = BspStructView.As<DBrushSide>(room.Bsp[BspLump.BrushSides]).ToArray();
            TexInfo[] roomInfos = LevelLinker.TransformTexInfos(BspStructView.As<TexInfo>(room.Bsp[BspLump.TexInfo]).ToArray(), transform);
            for (int b = 0; b < roomBrushes.Length; b++)
            {
                DBrush brush;
                if (stripped.Contains(b))
                {
                    brush = roomBrushes[b];
                    brush.Contents = 0;
                    brush.FirstSide = oldSides.Count;
                    for (int s = 0; s < roomBrushes[b].NumSides; s++)
                    {
                        DBrushSide side = roomSides[roomBrushes[b].FirstSide + s];
                        int p = side.PlaneNum;
                        DPlane plane = moved[swapped[p >> 1] ? p ^ 1 : p];
                        side.PlaneNum = (ushort)planeIndex[LevelLinker.PlaneKey.Of(plane.Normal, plane.Dist, plane.Type)];
                        if (side.TexInfo >= 0)
                        {
                            side.TexInfo = (short)infoIndex[InfoKey(room.Bsp, roomInfos[side.TexInfo])];
                        }

                        oldSides.Add(side);
                    }
                }
                else
                {
                    brush = brushes[cursor];
                    newToOld[cursor++] = oldBrushes.Count;
                    int first = oldSides.Count;
                    for (int s = 0; s < brush.NumSides; s++)
                    {
                        oldSides.Add(sides[brush.FirstSide + s]);
                    }

                    brush.FirstSide = first;
                }

                oldBrushes.Add(brush);
            }
        }

        ushort[] leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
        for (int i = 0; i < leafBrushes.Length; i++)
        {
            leafBrushes[i] = (ushort)newToOld[leafBrushes[i]];
        }

        BspData old = new() { FileVersion = bsp.FileVersion };
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            old[i] = bsp[i];
        }

        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            old.GameLumps.Add(entry);
        }

        old.SetLump(BspLump.Brushes, BspStructView.ToLump<DBrush>([.. oldBrushes], 0).Data);
        old.SetLump(BspLump.BrushSides, BspStructView.ToLump<DBrushSide>([.. oldSides], 0).Data);
        old.SetLump(BspLump.LeafBrushes, BspStructView.ToLump<ushort>(leafBrushes, 0).Data);
        return (old, newToOld);
    }

    /// <summary>
    /// A texinfo as a lookup key across maps: its axes and flags, and its
    /// texdata by name (the index is each map's own).
    /// </summary>
    private static string InfoKey(BspData bsp, TexInfo info)
    {
        DTexData data = BspStructView.As<DTexData>(bsp[BspLump.TexData])[info.TexData];
        int offset = BspStructView.As<int>(bsp[BspLump.TexDataStringTable])[data.NameStringTableId];
        ReadOnlySpan<byte> name = bsp[BspLump.TexDataStringData].Data.Span[offset..];
        name = name[..name.IndexOf((byte)0)];
        // Floats by value, -0 as 0: the link shares texinfos by value.
        System.Text.StringBuilder key = new();
        for (int i = 0; i < 8; i++)
        {
            key.Append(LevelLinker.Bits(info.TextureVecsTexelsPerWorldUnits[i])).Append(',')
                .Append(LevelLinker.Bits(info.LightmapVecsLuxelsPerWorldUnits[i])).Append(',');
        }

        return key.Append(info.Flags).Append('|').Append(System.Text.Encoding.ASCII.GetString(name)).ToString();
    }
}
