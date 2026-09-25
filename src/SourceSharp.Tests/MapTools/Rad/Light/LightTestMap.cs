using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SurfaceFlags = SourceSharp.MapTools.Materials.SurfaceFlags;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// A BSP built in memory from axial rectangles, for the unit tier: every lump
/// the lighting path reads, and nothing it does not.
/// </summary>
/// <remarks>
/// The tree has NO nodes, so every point is in leaf 0, cluster 0 -- the
/// shape <see cref="MapTools.Rad.Light.CompiledBspTree"/> answers for an empty
/// node lump. Visibility is absent unless asked for, which makes the map
/// "no vis" to vrad (direct light only, 0.1 ambient) exactly as a real one.
/// </remarks>
internal sealed class LightTestMap
{
    private readonly List<Vec3> _vertexes = [];
    private readonly List<DEdge> _edges = [default];
    private readonly List<int> _surfEdges = [];
    private readonly List<DPlane> _planes = [];
    private readonly List<DFace> _faces = [];
    private readonly List<TexInfo> _texInfos = [];
    private readonly List<DTexData> _texDatas = [];
    private readonly List<string> _names = [];
    private readonly List<(int Id, Vec3 A, Vec3 B, Vec3 C, Vec3 D)> _casters = [];

    public List<BspEntity> Entities { get; } = [Entity(("classname", "worldspawn"))];

    public int FaceCount => _faces.Count;

    /// <summary>A node lump, when a test needs a real tree; null for none.</summary>
    public List<DNode>? Nodes { get; set; }

    /// <summary>A leaf lump, replacing the single all-covering leaf.</summary>
    public List<DLeaf>? Leaves { get; set; }

    /// <summary>A leaf-face lump to go with <see cref="Leaves"/>.</summary>
    public List<ushort>? LeafFaces { get; set; }

    /// <summary>The raw visibility lump, or null for a map with no vis.</summary>
    public byte[]? Visibility { get; set; }

    /// <summary>How many AREAS records to write.</summary>
    public int AreaCount { get; set; }

    /// <summary>Adds a plane that no face uses (for nodes).</summary>
    public int AddPlane(Vec3 normal, float dist)
    {
        int type = normal.X is 1 or -1 ? 0 : normal.Y is 1 or -1 ? 1 : normal.Z is 1 or -1 ? 2 : 3;
        _planes.Add(new DPlane { Normal = normal, Dist = dist, Type = type });
        return _planes.Count - 1;
    }

    /// <summary>
    /// A visibility lump where every cluster sees exactly the clusters its
    /// row lists, stored uncompressed (no zero runs are needed when every
    /// byte is written literally except zeros, which are run-length coded).
    /// </summary>
    public static byte[] VisLump(bool[][] sees)
    {
        int n = sees.Length;
        int rowBytes = (n + 7) / 8;
        List<byte> rows = [];
        int[] offsets = new int[n];
        int header = 4 + (n * 8);
        for (int c = 0; c < n; c++)
        {
            offsets[c] = header + rows.Count;
            for (int b = 0; b < rowBytes; b++)
            {
                byte v = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int other = (b * 8) + bit;
                    if (other < n && sees[c][other])
                    {
                        v |= (byte)(1 << bit);
                    }
                }

                if (v == 0)
                {
                    rows.Add(0);
                    rows.Add(1);
                }
                else
                {
                    rows.Add(v);
                }
            }
        }

        List<byte> lump = [.. BitConverter.GetBytes(n)];
        for (int c = 0; c < n; c++)
        {
            lump.AddRange(BitConverter.GetBytes(offsets[c]));
            lump.AddRange(BitConverter.GetBytes(offsets[c]));
        }

        lump.AddRange(rows);
        return [.. lump];
    }

    public static BspEntity Entity(params (string Key, string Value)[] pairs)
    {
        BspEntity e = new();
        foreach ((string k, string v) in pairs)
        {
            e.Pairs.Add(new BspKeyValue(k, v));
        }

        return e;
    }

    /// <summary>Adds a material and a texinfo using it.</summary>
    /// <param name="name">The material name.</param>
    /// <param name="flags">SURF_* flags.</param>
    /// <param name="luxelsPerUnit">Lightmap scale: 1/16 is lightmapscale 16.</param>
    /// <param name="texelsPerUnit">Texture scale on both axes.</param>
    /// <param name="reflectivity">The texdata reflectivity.</param>
    /// <returns>The texinfo index.</returns>
    public int AddTexture(
        string name,
        SurfaceFlags flags = SurfaceFlags.None,
        float luxelsPerUnit = 1.0f / 16.0f,
        float texelsPerUnit = 0.25f,
        Vec3? reflectivity = null,
        Vec3 sAxis = default,
        Vec3 tAxis = default)
    {
        _names.Add(name);
        _texDatas.Add(new DTexData
        {
            Reflectivity = reflectivity ?? new Vec3(0.5f, 0.5f, 0.5f),
            NameStringTableId = _names.Count - 1,
            Width = 64,
            Height = 64,
            ViewWidth = 64,
            ViewHeight = 64,
        });

        Vec3 s = sAxis == default ? new Vec3(1, 0, 0) : sAxis;
        Vec3 t = tAxis == default ? new Vec3(0, -1, 0) : tAxis;
        TexInfo tex = new() { Flags = (int)flags, TexData = _texDatas.Count - 1 };
        tex.TextureVecsTexelsPerWorldUnits[0] = s.X * texelsPerUnit;
        tex.TextureVecsTexelsPerWorldUnits[1] = s.Y * texelsPerUnit;
        tex.TextureVecsTexelsPerWorldUnits[2] = s.Z * texelsPerUnit;
        tex.TextureVecsTexelsPerWorldUnits[4] = t.X * texelsPerUnit;
        tex.TextureVecsTexelsPerWorldUnits[5] = t.Y * texelsPerUnit;
        tex.TextureVecsTexelsPerWorldUnits[6] = t.Z * texelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = s.X * luxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[1] = s.Y * luxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[2] = s.Z * luxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[4] = t.X * luxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[5] = t.Y * luxelsPerUnit;
        tex.LightmapVecsLuxelsPerWorldUnits[6] = t.Z * luxelsPerUnit;
        _texInfos.Add(tex);
        return _texInfos.Count - 1;
    }

    /// <summary>
    /// Adds a planar convex face from its corners, in the winding order the
    /// face is seen from its front (normal = (b-a) x (c-a) normalised... as given).
    /// </summary>
    public int AddFace(int texInfo, Vec3 normal, params Vec3[] corners) =>
        AddSmoothedFace(texInfo, normal, 0u, corners);

    /// <summary>As <see cref="AddFace(int, Vec3, Vec3[])"/>, with smoothing groups.</summary>
    public int AddSmoothedFace(int texInfo, Vec3 normal, uint smoothingGroups, params Vec3[] corners)
    {
        float dist = Vec3.Dot(normal, corners[0]);
        int type = normal.X is 1 or -1 ? 0 : normal.Y is 1 or -1 ? 1 : normal.Z is 1 or -1 ? 2 : 3;
        _planes.Add(new DPlane { Normal = normal, Dist = dist, Type = type });

        // Corners are WELDED by position, as vbsp's vertex hash does, so faces
        // that meet share vertex indices -- which is all PairEdges looks at.
        int firstEdge = _surfEdges.Count;
        int[] index = new int[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            index[i] = _vertexes.IndexOf(corners[i]);
            if (index[i] < 0)
            {
                _vertexes.Add(corners[i]);
                index[i] = _vertexes.Count - 1;
            }
        }

        for (int i = 0; i < corners.Length; i++)
        {
            DEdge e = default;
            e.V[0] = (ushort)index[i];
            e.V[1] = (ushort)index[(i + 1) % corners.Length];
            _edges.Add(e);
            _surfEdges.Add(_edges.Count - 1);
        }

        // Lightmap extents as vbsp computes them: floor of the least luxel
        // coordinate, ceil of the greatest (CalcFaceExtents).
        TexInfo tex = _texInfos[texInfo];
        float minS = float.MaxValue, maxS = float.MinValue, minT = float.MaxValue, maxT = float.MinValue;
        foreach (Vec3 p in corners)
        {
            float s = (p.X * tex.LightmapVecsLuxelsPerWorldUnits[0]) + (p.Y * tex.LightmapVecsLuxelsPerWorldUnits[1])
                + (p.Z * tex.LightmapVecsLuxelsPerWorldUnits[2]) + tex.LightmapVecsLuxelsPerWorldUnits[3];
            float t = (p.X * tex.LightmapVecsLuxelsPerWorldUnits[4]) + (p.Y * tex.LightmapVecsLuxelsPerWorldUnits[5])
                + (p.Z * tex.LightmapVecsLuxelsPerWorldUnits[6]) + tex.LightmapVecsLuxelsPerWorldUnits[7];
            minS = Math.Min(minS, s);
            maxS = Math.Max(maxS, s);
            minT = Math.Min(minT, t);
            maxT = Math.Max(maxT, t);
        }

        DFace face = new()
        {
            PlaneNum = (ushort)(_planes.Count - 1),
            FirstEdge = firstEdge,
            NumEdges = (short)corners.Length,
            TexInfo = (short)texInfo,
            DispInfo = -1,
            SurfaceFogVolumeId = -1,
            LightOfs = -1,
            SmoothingGroups = smoothingGroups,
        };
        face.Styles[0] = face.Styles[1] = face.Styles[2] = face.Styles[3] = 255;
        face.LightmapTextureMinsInLuxels[0] = (int)MathF.Floor(minS);
        face.LightmapTextureMinsInLuxels[1] = (int)MathF.Floor(minT);
        face.LightmapTextureSizeInLuxels[0] = (int)MathF.Ceiling(maxS) - (int)MathF.Floor(minS);
        face.LightmapTextureSizeInLuxels[1] = (int)MathF.Ceiling(maxT) - (int)MathF.Floor(minT);
        _faces.Add(face);

        if (corners.Length == 4)
        {
            bool sky = (tex.Flags & (int)SurfaceFlags.Sky) != 0;
            _casters.Add((sky ? TraceId.Sky : TraceId.Opaque, corners[0], corners[1], corners[2], corners[3]));
        }

        return _faces.Count - 1;
    }

    /// <summary>An axis-aligned square on z = <paramref name="z"/>, facing +z or -z.</summary>
    public int AddFloor(int texInfo, float x0, float y0, float x1, float y1, float z, bool up = true) =>
        up
            ? AddFace(texInfo, new Vec3(0, 0, 1), new(x0, y1, z), new(x1, y1, z), new(x1, y0, z), new(x0, y0, z))
            : AddFace(texInfo, new Vec3(0, 0, -1), new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    /// <summary>A shadow caster that is not a face: a quad in the tracer only.</summary>
    public void AddOccluder(Vec3 a, Vec3 b, Vec3 c, Vec3 d) => _casters.Add((TraceId.Opaque, a, b, c, d));

    public IRayTracer Tracer()
    {
        List<TracedTriangle> tris = [];
        foreach ((int id, Vec3 a, Vec3 b, Vec3 c, Vec3 d) in _casters)
        {
            tris.Add(new TracedTriangle(id, a, b, c, 0));
            tris.Add(new TracedTriangle(id, a, c, d, 0));
        }

        return KdRayTracer.Build([.. tris]);
    }

    public BspData Build()
    {
        BspData bsp = new();
        bsp.SetLump(BspLump.Vertexes, Bytes(_vertexes), 0);
        bsp.SetLump(BspLump.Edges, Bytes(_edges), 0);
        bsp.SetLump(BspLump.SurfEdges, Bytes(_surfEdges), 0);
        bsp.SetLump(BspLump.Planes, Bytes(_planes), 0);
        bsp.SetLump(BspLump.Faces, Bytes(_faces), 1);
        bsp.SetLump(BspLump.TexInfo, Bytes(_texInfos), 0);
        bsp.SetLump(BspLump.TexData, Bytes(_texDatas), 0);

        List<int> offsets = [];
        List<byte> data = [];
        foreach (string name in _names)
        {
            offsets.Add(data.Count);
            data.AddRange(Encoding.ASCII.GetBytes(name));
            data.Add(0);
        }

        bsp.SetLump(BspLump.TexDataStringTable, Bytes(offsets), 0);
        bsp.SetLump(BspLump.TexDataStringData, data.ToArray(), 0);

        DModel model = new() { FirstFace = 0, NumFaces = _faces.Count, HeadNode = 0 };
        bsp.SetLump(BspLump.Models, Bytes(new[] { model }), 0);

        DLeaf leaf = new() { Contents = 0, Cluster = 0, FirstLeafFace = 0, NumLeafFaces = (ushort)_faces.Count };
        leaf.Mins[0] = leaf.Mins[1] = leaf.Mins[2] = -1024;
        leaf.Maxs[0] = leaf.Maxs[1] = leaf.Maxs[2] = 1024;
        bsp.SetLump(BspLump.Leafs, Leaves is null ? Bytes(new[] { leaf }) : Bytes(Leaves), 1);
        bsp.SetLump(
            BspLump.LeafFaces,
            LeafFaces is null ? Bytes(Enumerable.Range(0, _faces.Count).Select(i => (ushort)i).ToList()) : Bytes(LeafFaces),
            0);
        if (Nodes is not null)
        {
            bsp.SetLump(BspLump.Nodes, Bytes(Nodes), 0);
        }

        if (Visibility is not null)
        {
            bsp.SetLump(BspLump.Visibility, Visibility, 0);
        }

        if (AreaCount > 0)
        {
            bsp.SetLump(BspLump.Areas, new byte[AreaCount * 8], 0);
        }

        bsp.SetLump(BspLump.Entities, EntityLump.Write(Entities).Data, 0);
        return bsp;
    }

    private static byte[] Bytes<T>(List<T> items)
        where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items)).ToArray();

    private static byte[] Bytes<T>(T[] items)
        where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(items.AsSpan()).ToArray();
}
