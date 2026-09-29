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

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>What a swept box (or point) met in a map's brushes.</summary>
/// <param name="Fraction">How far the sweep got: 1 when nothing stopped it, 0 when it is all in solid.</param>
/// <param name="StartSolid">Whether it began inside a brush.</param>
/// <param name="AllSolid">Whether it began and ended inside one brush.</param>
/// <param name="Contents">
/// The contents of the brush it stopped on (every such brush's, when
/// several stop it at the same fraction, ORed), or of the brushes it began
/// in when it started solid; 0 when it met nothing.
/// </param>
/// <param name="Surfaces">
/// The material and surface flags of the side it stopped on, as
/// <c>name|flags</c>, sorted and distinct over every brush that stops it at
/// that fraction; empty when it met nothing or started solid.
/// </param>
/// <param name="Plane">The plane it stopped on (the first such brush's, in brush order), or null.</param>
internal readonly record struct WorldHit(float Fraction, bool StartSolid, bool AllSolid, int Contents, string Surfaces, DPlane? Plane);

/// <summary>
/// A brush-level trace over a whole map, for comparing two maps' collision
/// geometry: every brush some leaf lists (the only brushes a tree walk can
/// reach), each clipped as the engine clips a swept box against a brush,
/// the nearest stop winning.
/// </summary>
/// <remarks>
/// <para>
/// The clip is the classic enter/leave interval with the 1/32 unit
/// epsilon pulling both ends inward, and the box's extents pushing each
/// plane out along its normal (a point sweep is extents zero). Walking
/// every reachable brush instead of the tree visits a superset of the
/// leaves a real sweep visits, which only matters for which of two equal
/// stops is reported; <see cref="WorldHit.Surfaces"/> and
/// <see cref="WorldHit.Contents"/> carry every tied stop so that order
/// does not decide a comparison.
/// </para>
/// </remarks>
internal sealed class WorldBrushTrace
{
    public const float DistEpsilon = 0.03125f;

    private readonly DPlane[] _planes;
    private readonly DBrush[] _brushes;
    private readonly DBrushSide[] _sides;
    private readonly string[] _surfaceOf;
    private readonly int[] _reachable;

    public WorldBrushTrace(BspData bsp)
    {
        _planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        _brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
        _sides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
        TexInfo[] infos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        DTexData[] datas = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
        int[] table = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray();
        byte[] strings = bsp[BspLump.TexDataStringData].Data.ToArray();
        _surfaceOf = new string[infos.Length];
        for (int t = 0; t < infos.Length; t++)
        {
            int at = table[datas[infos[t].TexData].NameStringTableId];
            int end = Array.IndexOf(strings, (byte)0, at);
            _surfaceOf[t] = $"{Encoding.ASCII.GetString(strings, at, end - at)}|{infos[t].Flags}";
        }

        SortedSet<int> reachable = [];
        foreach (ushort brush in BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]))
        {
            reachable.Add(brush);
        }

        _reachable = [.. reachable];
    }

    /// <summary>Sweeps a box of half-extents <paramref name="extents"/> from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public WorldHit Trace(Vec3 start, Vec3 end, Vec3 extents, int mask = 0x1)
    {
        float best = 1f;
        bool startSolid = false, allSolid = false;
        int startContents = 0;
        List<(int Brush, int Side)> stops = [];
        foreach (int b in _reachable)
        {
            DBrush brush = _brushes[b];
            if ((brush.Contents & mask) == 0)
            {
                continue;
            }

            (Clip clip, float enter, int side) = ClipBrush(brush, start, end, extents);
            if (clip == Clip.StartSolid || clip == Clip.AllSolid)
            {
                startSolid = true;
                allSolid |= clip == Clip.AllSolid;
                startContents |= brush.Contents;
                continue;
            }

            if (clip != Clip.Hit)
            {
                continue;
            }

            if (enter < best)
            {
                best = enter;
                stops.Clear();
            }

            if (enter == best)
            {
                stops.Add((b, side));
            }
        }

        if (allSolid)
        {
            return new WorldHit(0f, true, true, startContents, string.Empty, null);
        }

        if (stops.Count == 0 || best >= 1f)
        {
            return new WorldHit(1f, startSolid, false, startContents, string.Empty, null);
        }

        int contents = 0;
        SortedSet<string> surfaces = new(StringComparer.Ordinal);
        foreach ((int b, int side) in stops)
        {
            contents |= _brushes[b].Contents;
            short texInfo = _sides[side].TexInfo;
            surfaces.Add(texInfo < 0 ? "<none>" : _surfaceOf[texInfo]);
        }

        return new WorldHit(best, startSolid, false, contents | startContents, string.Join(";", surfaces), _planes[_sides[stops[0].Side].PlaneNum]);
    }

    private enum Clip
    {
        Miss,
        Hit,
        StartSolid,
        AllSolid,
    }

    private (Clip Clip, float Enter, int Side) ClipBrush(DBrush brush, Vec3 p1, Vec3 p2, Vec3 extents)
    {
        float enter = -1f, leave = 1f;
        int clipSide = -1;
        bool startOut = false, getOut = false;
        for (int s = 0; s < brush.NumSides; s++)
        {
            int sideIndex = brush.FirstSide + s;
            DPlane plane = _planes[_sides[sideIndex].PlaneNum];
            float offset = (MathF.Abs(plane.Normal.X) * extents.X) + (MathF.Abs(plane.Normal.Y) * extents.Y) + (MathF.Abs(plane.Normal.Z) * extents.Z);
            float dist = plane.Dist + offset;
            float d1 = Vec3.Dot(p1, plane.Normal) - dist;
            float d2 = Vec3.Dot(p2, plane.Normal) - dist;
            if (d2 > 0)
            {
                getOut = true;
            }

            if (d1 > 0)
            {
                startOut = true;
            }

            if (d1 > 0 && (d2 >= DistEpsilon || d2 >= d1))
            {
                return (Clip.Miss, 1f, -1);
            }

            if (d1 <= 0 && d2 <= 0)
            {
                continue;
            }

            if (d1 > d2)
            {
                float f = (d1 - DistEpsilon) / (d1 - d2);
                f = MathF.Max(f, 0f);
                if (f > enter)
                {
                    enter = f;
                    clipSide = sideIndex;
                }
            }
            else
            {
                float f = (d1 + DistEpsilon) / (d1 - d2);
                f = MathF.Min(f, 1f);
                if (f < leave)
                {
                    leave = f;
                }
            }
        }

        if (!startOut)
        {
            return (getOut ? Clip.StartSolid : Clip.AllSolid, 0f, -1);
        }

        return enter < leave && enter > -1f && clipSide >= 0 ? (Clip.Hit, enter, clipSide) : (Clip.Miss, 1f, -1);
    }
}
