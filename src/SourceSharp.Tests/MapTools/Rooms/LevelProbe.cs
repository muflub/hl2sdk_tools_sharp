//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Asks a compiled map the questions the game asks it: what is at this point,
/// how far does a line get, which faces draw. Written from the file format
/// alone, so it reads a linked map and a vbsp map the same way and shares no
/// code with the linker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Points</b> are walked down model 0's tree the way the engine walks
/// them: an axial plane is read by its one coordinate, as if its normal were
/// the positive axis, which is why <see cref="NegativeAxialNodePlanes"/> must
/// be empty for the answer to mean anything.
/// </para>
/// <para>
/// <b>Lines</b> are clipped against the brushes of the leaves the segment
/// passes through, splitting it at each node plane: the engine's world trace
/// for a point-sized box. The same clip against every brush in the brush lump
/// (<see cref="TraceAllBrushes"/>) is the answer with no tree at all; a map
/// whose leaves list their brushes correctly gives the same fraction both
/// ways.
/// </para>
/// </remarks>
internal sealed class LevelProbe
{
    /// <summary><c>DIST_EPSILON</c>: the engine's clip nudge.</summary>
    private const float DistEpsilon = 0.03125f;

    private readonly DNode[] _nodes;
    private readonly DPlane[] _planes;
    private readonly DLeaf[] _leafs;
    private readonly ushort[] _leafBrushes;
    private readonly DBrush[] _brushes;
    private readonly DBrushSide[] _sides;

    public LevelProbe(BspData bsp)
    {
        Bsp = bsp;
        _nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        _planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        _leafs = BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        _leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();
        _brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
        _sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
    }

    public BspData Bsp { get; }

    public IReadOnlyList<DLeaf> Leafs => _leafs;

    /// <summary>The node planes an engine walk would misread: axial, with a negative normal.</summary>
    public IEnumerable<int> NegativeAxialNodePlanes() =>
        _nodes.Select(n => n.PlaneNum).Where(p =>
            _planes[p].Type switch
            {
                0 => _planes[p].Normal.X < 0,
                1 => _planes[p].Normal.Y < 0,
                2 => _planes[p].Normal.Z < 0,
                _ => false,
            });

    /// <summary>The leaf holding a point, walked as the engine walks.</summary>
    public int Leaf(Vec3 p)
    {
        int index = 0;
        while (index >= 0)
        {
            DNode node = _nodes[index];
            DPlane plane = _planes[node.PlaneNum];
            float d = plane.Type switch
            {
                0 => p.X - plane.Dist,
                1 => p.Y - plane.Dist,
                2 => p.Z - plane.Dist,
                _ => Vec3.Dot(p, plane.Normal) - plane.Dist,
            };
            index = d < 0 ? node.Children[1] : node.Children[0];
        }

        return ~index;
    }

    /// <summary>The contents the engine reports at a point: its leaf's.</summary>
    public int Contents(Vec3 p) => _leafs[Leaf(p)].Contents;

    /// <summary>
    /// How far along a segment the first brush matching <paramref name="mask"/>
    /// is, through the tree: 1 when nothing blocks it, 0 when it starts inside.
    /// </summary>
    public float Trace(Vec3 start, Vec3 end, int mask)
    {
        HashSet<int> leaves = [];
        Walk(0, start, end, leaves);
        HashSet<int> brushes = [];
        foreach (int leaf in leaves)
        {
            DLeaf l = _leafs[leaf];
            for (int b = 0; b < l.NumLeafBrushes; b++)
            {
                brushes.Add(_leafBrushes[l.FirstLeafBrush + b]);
            }
        }

        return Clip(brushes, start, end, mask);
    }

    /// <summary>
    /// Whether a point is inside a brush whose contents are in
    /// <paramref name="mask"/>, found through the tree: the brushes the
    /// point's leaf lists. Unlike <see cref="Contents"/>, this sees a player
    /// clip or a detail brush, which leave the leaf they are in empty.
    /// </summary>
    public bool InsideBrush(Vec3 p, int mask)
    {
        DLeaf leaf = _leafs[Leaf(p)];
        for (int b = 0; b < leaf.NumLeafBrushes; b++)
        {
            DBrush brush = _brushes[_leafBrushes[leaf.FirstLeafBrush + b]];
            if ((brush.Contents & mask) == 0)
            {
                continue;
            }

            bool inside = true;
            for (int s = 0; s < brush.NumSides && inside; s++)
            {
                DBrushSide side = _sides[brush.FirstSide + s];
                DPlane plane = _planes[side.PlaneNum];
                inside = side.Bevel != 0 || Vec3.Dot(p, plane.Normal) - plane.Dist <= 0;
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The same trace against every brush of the map, with no tree.</summary>
    public float TraceAllBrushes(Vec3 start, Vec3 end, int mask) =>
        Clip(Enumerable.Range(0, _brushes.Length), start, end, mask);

    private float Clip(IEnumerable<int> brushes, Vec3 start, Vec3 end, int mask)
    {
        float best = 1f;
        foreach (int b in brushes)
        {
            if ((_brushes[b].Contents & mask) != 0)
            {
                best = Math.Min(best, ClipToBrush(_brushes[b], start, end));
            }
        }

        return best;
    }

    private void Walk(int node, Vec3 a, Vec3 b, HashSet<int> leaves)
    {
        if (node < 0)
        {
            leaves.Add(~node);
            return;
        }

        DNode n = _nodes[node];
        DPlane plane = _planes[n.PlaneNum];
        float da = Vec3.Dot(a, plane.Normal) - plane.Dist;
        float db = Vec3.Dot(b, plane.Normal) - plane.Dist;
        if (da >= DistEpsilon && db >= DistEpsilon)
        {
            Walk(n.Children[0], a, b, leaves);
        }
        else if (da < -DistEpsilon && db < -DistEpsilon)
        {
            Walk(n.Children[1], a, b, leaves);
        }
        else if (Math.Abs(da - db) < 1e-6f)
        {
            // Lying in the plane: both sides hold it.
            Walk(n.Children[0], a, b, leaves);
            Walk(n.Children[1], a, b, leaves);
        }
        else
        {
            // Split at the crossing, each part to its side, and a sliver
            // either way of the crossing to both, as the engine's epsilons do.
            float t = da / (da - db);
            float lo = Math.Clamp(t - (DistEpsilon / Math.Abs(da - db)), 0f, 1f);
            float hi = Math.Clamp(t + (DistEpsilon / Math.Abs(da - db)), 0f, 1f);
            Vec3 mLo = a + ((b - a) * lo);
            Vec3 mHi = a + ((b - a) * hi);
            if (da >= 0)
            {
                Walk(n.Children[0], a, mHi, leaves);
                Walk(n.Children[1], mLo, b, leaves);
            }
            else
            {
                Walk(n.Children[1], a, mHi, leaves);
                Walk(n.Children[0], mLo, b, leaves);
            }
        }
    }

    /// <summary>A point segment against one brush: the entry fraction, 0 inside, 1 clear.</summary>
    private float ClipToBrush(DBrush brush, Vec3 p1, Vec3 p2)
    {
        float enter = -1f;
        float leave = 1f;
        bool startOut = false;
        for (int s = 0; s < brush.NumSides; s++)
        {
            DBrushSide side = _sides[brush.FirstSide + s];
            if (side.Bevel != 0)
            {
                continue;
            }

            DPlane plane = _planes[side.PlaneNum];
            float d1 = Vec3.Dot(p1, plane.Normal) - plane.Dist;
            float d2 = Vec3.Dot(p2, plane.Normal) - plane.Dist;
            if (d1 > 0 && d2 > 0)
            {
                return 1f;
            }

            if (d1 > 0)
            {
                startOut = true;
            }

            if (d1 <= 0 && d2 <= 0)
            {
                continue;
            }

            float f = d1 > d2 ? (d1 - DistEpsilon) / (d1 - d2) : (d1 + DistEpsilon) / (d1 - d2);
            if (d1 > d2)
            {
                enter = Math.Max(enter, f);
            }
            else
            {
                leave = Math.Min(leave, f);
            }
        }

        if (!startOut)
        {
            return 0f;
        }

        return enter < leave && enter > -1f && enter < 1f ? Math.Max(enter, 0f) : 1f;
    }

    /// <summary>
    /// The drawn face area of the map, by oriented plane and material: every
    /// face of the face lump whose texinfo is not nodraw.
    /// </summary>
    /// <remarks>
    /// Totals, not faces: the two compiles split, merge and T-junction the
    /// same surface differently, and none of that changes how much of each
    /// plane draws in each material. Planes are keyed by the winding's own
    /// normal, so the answer does not depend on how a map orients its plane
    /// pairs.
    /// </remarks>
    public Dictionary<(int Nx, int Ny, int Nz, int Dist, string Material), double> DrawnArea()
    {
        ReadOnlySpan<DFace> faces = BspStructView.As<DFace>(Bsp[BspLump.Faces]);
        ReadOnlySpan<TexInfo> texInfos = BspStructView.As<TexInfo>(Bsp[BspLump.TexInfo]);
        Dictionary<(int, int, int, int, string), double> area = [];
        foreach (DFace face in faces)
        {
            if (face.TexInfo < 0 || (texInfos[face.TexInfo].Flags & (int)SurfaceFlags.NoDraw) != 0)
            {
                continue;
            }

            List<Vec3> points = RoomHarness.FaceVertices(Bsp, face);
            Vec3 sum = Vec3.Zero;
            for (int i = 1; i + 1 < points.Count; i++)
            {
                sum += Vec3.Cross(points[i + 1] - points[0], points[i] - points[0]);
            }

            float length = MathF.Sqrt(Vec3.Dot(sum, sum));
            if (length < 1e-3f)
            {
                continue;
            }

            Vec3 normal = sum * (1f / length);
            var key = ((int)MathF.Round(normal.X), (int)MathF.Round(normal.Y), (int)MathF.Round(normal.Z),
                (int)MathF.Round(Vec3.Dot(normal, points[0])), MaterialOf(texInfos[face.TexInfo].TexData));
            area[key] = area.GetValueOrDefault(key) + (length / 2);
        }

        return area;
    }

    /// <summary>The material name a texdata names, as the string table holds it.</summary>
    public string MaterialOf(int texData)
    {
        DTexData data = BspStructView.As<DTexData>(Bsp[BspLump.TexData])[texData];
        int offset = BspStructView.As<int>(Bsp[BspLump.TexDataStringTable])[data.NameStringTableId];
        ReadOnlySpan<byte> strings = Bsp[BspLump.TexDataStringData].Data.Span[offset..];
        return Encoding.ASCII.GetString(strings[..strings.IndexOf((byte)0)]).ToLowerInvariant();
    }
}
