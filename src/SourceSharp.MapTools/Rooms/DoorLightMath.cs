//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A source of light that reaches a doorway, as the door light carries it
/// from the room it leaves (where the pack records it, in that room's frame)
/// to the room it enters (where the link evaluates it, in that room's
/// door-local frame): one of the room's world lights, the sun through the
/// room's own sky, or a point standing for the light a patch of the room's
/// surfaces or sky sends through the opening.
/// </summary>
/// <param name="Type">
/// The kind of source: <see cref="EmitType.Point"/>, <see cref="EmitType.Spotlight"/>,
/// <see cref="EmitType.Surface"/> or <see cref="EmitType.SkyLight"/> (the sun).
/// A point standing for surfaces or sky is a point.
/// </param>
/// <param name="Origin">Where it is; for the sun, a point a room's width back along its rays, used only to place it on the response grid.</param>
/// <param name="Normal">The way a spot or surface light faces, or the direction the sun's light travels.</param>
/// <param name="Intensity">Its intensity in lightmap units: what a luxel facing it squarely at unit falloff receives.</param>
/// <param name="Style">Its light style in the room it leaves (0 for surfaces and sky).</param>
/// <param name="ConstantAttn">The constant term of its falloff's denominator.</param>
/// <param name="LinearAttn">The linear term.</param>
/// <param name="QuadraticAttn">The quadratic term.</param>
/// <param name="StopDot">A spot's inner cone, as a cosine.</param>
/// <param name="StopDot2">A spot's outer cone, as a cosine.</param>
/// <param name="Exponent">A spot's falloff exponent.</param>
/// <param name="Cells">Which cells of the opening it reaches from inside its own room, one bit a cell (<see cref="DoorLightMath.Cell"/>), in its own room's across order.</param>
internal readonly record struct DoorSource(
    EmitType Type,
    Vec3 Origin,
    Vec3 Normal,
    Vec3 Intensity,
    int Style,
    float ConstantAttn,
    float LinearAttn,
    float QuadraticAttn,
    float StopDot,
    float StopDot2,
    float Exponent,
    UInt128 Cells)
{
    /// <summary>
    /// Whether it stands for a patch of its room's surfaces or sky rather
    /// than for a light: such light reaches a leaf ambient sample as a
    /// surface its rays meet (vrad's cubes gather surfaces and sky, and the
    /// engine adds lights at run time), where a light's never does.
    /// </summary>
    public bool StandIn { get; init; }

    /// <summary>Whether it does not fall off with distance: the sun, or a light with only a constant term.</summary>
    public bool Flat => Type == EmitType.SkyLight
        || (Type is EmitType.Point or EmitType.Spotlight && QuadraticAttn == 0 && LinearAttn == 0);
}

/// <summary>
/// The sample cells of one lit face, room-local, as vrad lays a face's
/// samples: one cell between each four luxels (a face one luxel across in a
/// direction has one cell there, on the luxels), with the face's normal and,
/// on a bumped face, its three bump normals.
/// </summary>
/// <param name="Face">The face.</param>
/// <param name="Width">Luxels across (<c>LightmapTextureSizeInLuxels[0] + 1</c>).</param>
/// <param name="Height">Luxels up.</param>
/// <param name="Origin">The point of lightmap coordinates (0, 0) on the face's plane.</param>
/// <param name="AxisS">One lightmap unit along s, on the plane.</param>
/// <param name="AxisT">One lightmap unit along t, on the plane.</param>
/// <param name="MinS">The face's lightmap mins along s.</param>
/// <param name="MinT">The face's lightmap mins along t.</param>
/// <param name="Normal">The face's normal: its plane's.</param>
/// <param name="Bumps">The three bump normals of a bumped face, else empty.</param>
/// <remarks>
/// <para>
/// <b>Displacements.</b> A displacement's lightmap is not laid on its base
/// face's plane: vrad spreads its luxels over the displaced surface, the
/// luxel at lightmap column <c>s</c> standing at the surface's point
/// <c>u = (s − mins) / (width − 1)</c>, with the surface's blended normal
/// there. So a displacement face's cells carry its surface
/// (<see cref="Surface"/>) and each cell's normal and bump normals
/// (<see cref="CellNormals"/>, <see cref="CellBumps"/>), and every point and
/// normal read through <see cref="At"/>, <see cref="NormalAt"/> and
/// <see cref="BumpsAt"/> is the surface's; a brush face's are its plane's, as
/// they always were.
/// </para>
/// </remarks>
internal sealed record DoorFaceCells(
    int Face, int Width, int Height, Vec3 Origin, Vec3 AxisS, Vec3 AxisT, int MinS, int MinT, Vec3 Normal, Vec3[] Bumps)
{
    /// <summary>The displaced surface the face's luxels lie on, or null for a brush face.</summary>
    public Rad.Displacement.VradDispSurface? Surface { get; init; }

    /// <summary>A displacement face's normal at each cell's centre, or null for a brush face (whose cells share <see cref="Normal"/>).</summary>
    public Vec3[]? CellNormals { get; init; }

    /// <summary>A bumped displacement face's bump normals at each cell, or null (a brush face's cells share <see cref="Bumps"/>).</summary>
    public Vec3[][]? CellBumps { get; init; }

    /// <summary>Cells across: one between each two luxels, or one on a face a luxel across.</summary>
    public int CellsAcross => Math.Max(Width - 1, 1);

    /// <summary>Cells up.</summary>
    public int CellsUp => Math.Max(Height - 1, 1);

    /// <summary>How many cells.</summary>
    public int Count => CellsAcross * CellsUp;

    /// <summary>How many pages each style of the face holds: four on a bumped face.</summary>
    public int Pages => Bumps.Length == 0 ? 1 : 4;

    /// <summary>One cell's centre in lightmap coordinates (s, t).</summary>
    public (float S, float T) Coordinates(int cell) => (
        MinS + (cell % CellsAcross) + (Width > 1 ? 0.5f : 0f),
        MinT + (cell / CellsAcross) + (Height > 1 ? 0.5f : 0f));

    /// <summary>One cell's point on the face, offset by a fraction of a luxel along s and t (0 is the centre).</summary>
    public Vec3 Point(int cell, float ds = 0, float dt = 0)
    {
        (float s, float t) = Coordinates(cell);
        return At(s + ds, t + dt);
    }

    /// <summary>
    /// The face's point at lightmap coordinates (s, t): on its plane for a
    /// brush face, on its displaced surface for a displacement.
    /// </summary>
    public Vec3 At(float s, float t)
    {
        if (Surface is not { } surface)
        {
            return Origin + (AxisS * s) + (AxisT * t);
        }

        Vec3 point = Vec3.Zero;
        surface.DispUVToSurfPoint(SurfaceU(s, MinS, Width), SurfaceU(t, MinT, Height), 1.0f, ref point);
        return point;
    }

    /// <summary>A cell's normal: the face's, or a displacement's at the cell.</summary>
    public Vec3 NormalAt(int cell) => CellNormals is { } normals ? normals[cell] : Normal;

    /// <summary>A cell's bump normals: the face's, or a displacement's at the cell.</summary>
    public Vec3[] BumpsAt(int cell) => CellBumps is { } bumps ? bumps[cell] : Bumps;

    /// <summary>
    /// A lightmap coordinate as a fraction of the displaced surface, as vrad
    /// lays a displacement's luxels: its first luxel at 0, its last at 1;
    /// clamped, and 0 on a face one luxel across.
    /// </summary>
    internal static float SurfaceU(float coordinate, int min, int luxels) =>
        luxels <= 1 ? 0f : Math.Clamp((coordinate - min) / (luxels - 1), 0f, 1f);

    /// <summary>
    /// The same cells laid on a displacement's surface (<see cref="Surface"/>):
    /// each cell's normal the surface's blended normal at its centre, and on
    /// a bumped face its bump normals built on that normal from the face's
    /// texture axes, as a displacement's luxels take theirs.
    /// </summary>
    internal DoorFaceCells OnSurface(Rad.Displacement.VradDispSurface surface, in TexInfo tex)
    {
        Vec3[] normals = new Vec3[Count];
        Vec3[][]? bumps = Bumps.Length == 0 ? null : new Vec3[Count][];
        Vec3 sVector = new(tex.TextureVecsTexelsPerWorldUnits[0], tex.TextureVecsTexelsPerWorldUnits[1], tex.TextureVecsTexelsPerWorldUnits[2]);
        Vec3 tVector = new(tex.TextureVecsTexelsPerWorldUnits[4], tex.TextureVecsTexelsPerWorldUnits[5], tex.TextureVecsTexelsPerWorldUnits[6]);
        for (int c = 0; c < Count; c++)
        {
            (float s, float t) = Coordinates(c);
            Vec3 normal = Normal;
            surface.DispUVToSurfNormal(SurfaceU(s, MinS, Width), SurfaceU(t, MinT, Height), ref normal);
            normals[c] = normal;
            if (bumps is not null)
            {
                bumps[c] = new Vec3[BumpBasis.Count];
                BumpBasis.Build(sVector, tVector, normal, normal, bumps[c], stockNormalise: false);
            }
        }

        return this with { Surface = surface, CellNormals = normals, CellBumps = bumps };
    }
}

/// <summary>
/// The arithmetic of the door light (the rooms design, 9.1 parts 2 to 4, as
/// PR 10 chose it from the prototype): the cells an opening is divided into,
/// the direct light a source beyond a joint sends a receiver through both
/// rooms' openings, and the grid of emitters a room's bounced response to
/// door light is measured on. Pure functions, shared by the pack, which
/// records what they read, and the link, which evaluates them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why direct light is evaluated, not projected.</b> The prototype (9.7)
/// measured the design's basis on the opening (patches times directional
/// lobes, and a grid of point emitters behind it) against vrad of the linked
/// level: a lamp's light through a door is a sharp beam, and a coarse basis
/// blurs its edges to errors of 100% on 5% of the luxels near a joint and
/// beyond. What decides whether a light reaches a luxel through a joint is
/// only which part of each opening the two can see, so the pack records
/// that on both sides (the capture: which cells of its opening each of a
/// room's lights reaches; the receivers: which cells of its opening each of
/// a room's sample cells sees) and the link evaluates each light's falloff
/// at each receiver, the way vrad's direct lighting does, with no ray traced.
/// </para>
/// <para>
/// <b>The cells.</b> Every opening is divided into <see cref="Across"/> by
/// <see cref="Up"/> cells (16 units on the kit's 96 by 224 opening), a bit
/// each in a <see cref="UInt128"/>; the sweep measured 3 by 7 nearly as good
/// and 12 by 28 no better, at four times the bytes.
/// </para>
/// </remarks>
internal static class DoorLightMath
{
    /// <summary>Cells across an opening.</summary>
    public const int Across = 6;

    /// <summary>Cells up an opening.</summary>
    public const int Up = 14;

    /// <summary>Cells in an opening.</summary>
    public const int CellCount = Across * Up;

    /// <summary>
    /// Subsamples per cell along each axis: the prototype measured one to
    /// miss 40% of the luxels a beam's edge crosses (vrad antialiases the
    /// edge by filtering its samples and supersampling), three within 10%.
    /// </summary>
    public const int Subsamples = 3;

    /// <summary>Response emitters per axis of the grid behind an opening.</summary>
    public const int GridNodes = 2;

    /// <summary>Response emitters per falloff class: <see cref="GridNodes"/> cubed.</summary>
    public const int NodesPerClass = GridNodes * GridNodes * GridNodes;

    /// <summary>Response emitters: an inverse-square set and a constant set.</summary>
    public const int EmitterCount = 2 * NodesPerClass;

    /// <summary>Every cell of an opening.</summary>
    public static UInt128 AllCells => (UInt128.One << CellCount) - UInt128.One;

    /// <summary>The cell holding a door-local point of an opening (across, up), or -1 outside it.</summary>
    public static int Cell(float across, float up, float width, float height)
    {
        float u = (across + (width / 2)) / width;
        float v = (up + (height / 2)) / height;
        return u < 0 || u >= 1 || v < 0 || v >= 1 ? -1 : ((int)(v * Up) * Across) + (int)(u * Across);
    }

    /// <summary>A cell's centre, door-local, at depth <paramref name="x"/> from the opening's plane.</summary>
    public static Vec3 CellCentre(int cell, float width, float height, float x) => new(
        x,
        (((cell % Across) + 0.5f) / Across * width) - (width / 2),
        (((cell / Across) + 0.5f) / Up * height) - (height / 2));

    /// <summary>Whether a mask holds a cell.</summary>
    public static bool Has(UInt128 mask, int cell) => ((mask >> cell) & UInt128.One) != UInt128.Zero;

    /// <summary>
    /// A source recorded in the room it leaves, carried across the joint of
    /// <paramref name="from"/> into the door-local frame of the room it
    /// enters (<see cref="DoorFrame.ToNeighbour"/>).
    /// </summary>
    public static DoorSource ToNeighbour(in DoorSource source, DoorFrame from) => source with
    {
        Origin = from.ToNeighbour(from.ToLocal(source.Origin)),
        Normal = DoorFrame.DirectionToNeighbour(from.DirectionToLocal(source.Normal)),
    };

    /// <summary>
    /// A source's falloff toward a receiver as vrad's direct lighting has it,
    /// times the receiver's cosine: point, spot and surface lights fall off
    /// with their own terms, the sun not at all.
    /// </summary>
    /// <param name="source">The source, in the receiver's frame.</param>
    /// <param name="toSource">From the receiver toward the source (unnormalised; the sun's is the negated direction of its rays).</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <returns>The irradiance per unit intensity; zero where the light cannot reach.</returns>
    public static float Falloff(in DoorSource source, Vec3 toSource, Vec3 normal)
    {
        float dist2 = toSource.LengthSquared();
        if (!(dist2 > 0))
        {
            return 0;
        }

        float length = MathF.Sqrt(dist2);
        Vec3 dir = toSource * (1f / length);
        float cosine = Vec3.Dot(normal, dir);
        if (!(cosine > 0))
        {
            return 0;
        }

        if (source.Type == EmitType.SkyLight)
        {
            return cosine;
        }

        float dist = MathF.Max(length, 1f);
        float falloff;
        if (source.Type == EmitType.Surface)
        {
            falloff = MathF.Max(0, -Vec3.Dot(dir, source.Normal)) / MathF.Max(dist2, 1f);
        }
        else
        {
            falloff = 1f / ((source.QuadraticAttn * dist * dist) + (source.LinearAttn * dist) + source.ConstantAttn);
            if (source.Type == EmitType.Spotlight)
            {
                float dot2 = -Vec3.Dot(dir, source.Normal);
                if (!(dot2 > source.StopDot2))
                {
                    return 0;
                }

                float mult = 1;
                if (dot2 <= source.StopDot)
                {
                    mult = Math.Clamp((dot2 - source.StopDot2) / (source.StopDot - source.StopDot2), 0f, 1f);
                    if (source.Exponent != 0f && source.Exponent != 1f)
                    {
                        mult = DetMathF.Pow(mult, source.Exponent);
                    }
                }

                falloff *= dot2 * mult;
            }
        }

        return falloff > 0 ? falloff * cosine : 0;
    }

    /// <summary>
    /// The direct light a receiver in the room a joint enters gets from one
    /// source of the room it leaves, through both openings: nothing unless
    /// the segment toward the source crosses this room's opening in a cell
    /// the receiver sees and the neighbour's opening in a cell the source
    /// reaches; else the source's falloff and the receiver's cosine.
    /// </summary>
    /// <param name="source">The source, door-local to this room (<see cref="ToNeighbour"/>).</param>
    /// <param name="point">The receiver, door-local.</param>
    /// <param name="normal">Its unit normal, door-local.</param>
    /// <param name="width">The opening's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="depth">The plug's depth: the neighbour's opening is two of these beyond this one.</param>
    /// <param name="seen">The cells of this room's opening the receiver sees.</param>
    /// <returns>The irradiance per unit intensity.</returns>
    public static float Through(in DoorSource source, Vec3 point, Vec3 normal, float width, float height, float depth, UInt128 seen)
    {
        Vec3 d = source.Type == EmitType.SkyLight ? -source.Normal : source.Origin - point;
        if (!(d.X > 0) || point.X >= 0)
        {
            return 0;
        }

        Vec3 here = point + (d * (-point.X / d.X));
        int mine = Cell(here.Y, here.Z, width, height);
        if (mine < 0 || !Has(seen, mine))
        {
            return 0;
        }

        Vec3 there = point + (d * (((2 * depth) - point.X) / d.X));

        // The neighbour's cells are in its own across order, which runs the
        // other way (DoorFrame: its across axis is this one's negated).
        int theirs = Cell(-there.Y, there.Z, width, height);
        return theirs < 0 || !Has(source.Cells, theirs) ? 0 : Falloff(source, d, normal);
    }

    /// <summary>
    /// Why a source's light cannot reach a receiver point through a joint,
    /// as bits (zero when it may): 1, the source is not beyond this room's
    /// opening; 2, the point is not in front of it; else where the segment
    /// toward the source crosses this room's opening (4 left of it, 8 right,
    /// 16 below, 32 above) and the neighbour's (64, 128, 256, 512, in the
    /// neighbour's own across order). The tests are those of
    /// <see cref="Through"/>, bit for bit.
    /// </summary>
    /// <remarks>
    /// What it is for: the receivers of one sample cell are points of a
    /// small planar patch, and a source projects the patch onto either
    /// opening's plane centrally (the sun, in parallel), which keeps it
    /// convex; so when the codes of the patch's four corner receivers share
    /// a bit, every receiver of the patch misses the same edge of the same
    /// opening (the first two bits are linear in the point, so they carry
    /// over too), and the link skips the source for that cell without
    /// evaluating its nine receivers.
    /// </remarks>
    public static int Outcode(in DoorSource source, Vec3 point, float width, float height, float depth)
    {
        Vec3 d = source.Type == EmitType.SkyLight ? -source.Normal : source.Origin - point;
        int code = (d.X > 0 ? 0 : 1) | (point.X >= 0 ? 2 : 0);
        if (code != 0)
        {
            return code;
        }

        Vec3 here = point + (d * (-point.X / d.X));
        Vec3 there = point + (d * (((2 * depth) - point.X) / d.X));
        return Side(here.Y, here.Z, width, height) | (Side(-there.Y, there.Z, width, height) << 4);

        // The same fractions Cell tests, as edge bits.
        static int Side(float across, float up, float width, float height)
        {
            float u = (across + (width / 2)) / width;
            float v = (up + (height / 2)) / height;
            return (u < 0 ? 4 : 0) | (u >= 1 ? 8 : 0) | (v < 0 ? 16 : 0) | (v >= 1 ? 32 : 0);
        }
    }

    /// <summary>
    /// The cells of one lit face, from its lightmap axes and plane, or null
    /// for a face vrad does not light (<c>SURF_NOLIGHT</c>, sky, nodraw, or
    /// no lightmap).
    /// </summary>
    public static DoorFaceCells? FaceCells(ReadOnlySpan<DFace> faces, ReadOnlySpan<DPlane> planes, ReadOnlySpan<TexInfo> texInfos, int f)
    {
        DFace face = faces[f];
        if (face.TexInfo < 0 || face.TexInfo >= texInfos.Length)
        {
            return null;
        }

        TexInfo tex = texInfos[face.TexInfo];
        if ((tex.Flags & (int)(SurfaceFlags.NoLight | SurfaceFlags.Sky | SurfaceFlags.Sky2D | SurfaceFlags.NoDraw)) != 0)
        {
            return null;
        }

        DPlane plane = planes[face.PlaneNum];
        double[] s = [tex.LightmapVecsLuxelsPerWorldUnits[0], tex.LightmapVecsLuxelsPerWorldUnits[1], tex.LightmapVecsLuxelsPerWorldUnits[2], tex.LightmapVecsLuxelsPerWorldUnits[3]];
        double[] t = [tex.LightmapVecsLuxelsPerWorldUnits[4], tex.LightmapVecsLuxelsPerWorldUnits[5], tex.LightmapVecsLuxelsPerWorldUnits[6], tex.LightmapVecsLuxelsPerWorldUnits[7]];
        double[] p = [plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.Dist];
        if (Solve(s, t, p, 0, 0) is not { } origin || Solve(s, t, p, 1, 0) is not { } unitS || Solve(s, t, p, 0, 1) is not { } unitT)
        {
            return null;
        }

        Vec3[] bumps = [];
        if ((tex.Flags & (int)SurfaceFlags.BumpLight) != 0)
        {
            bumps = new Vec3[BumpBasis.Count];
            Vec3 sVector = new(tex.TextureVecsTexelsPerWorldUnits[0], tex.TextureVecsTexelsPerWorldUnits[1], tex.TextureVecsTexelsPerWorldUnits[2]);
            Vec3 tVector = new(tex.TextureVecsTexelsPerWorldUnits[4], tex.TextureVecsTexelsPerWorldUnits[5], tex.TextureVecsTexelsPerWorldUnits[6]);
            BumpBasis.Build(sVector, tVector, plane.Normal, plane.Normal, bumps, stockNormalise: false);
        }

        return new DoorFaceCells(
            f,
            face.LightmapTextureSizeInLuxels[0] + 1,
            face.LightmapTextureSizeInLuxels[1] + 1,
            origin,
            unitS - origin,
            unitT - origin,
            face.LightmapTextureMinsInLuxels[0],
            face.LightmapTextureMinsInLuxels[1],
            plane.Normal,
            bumps);

        // The point with lightmap coordinates (s, t) on the plane: three
        // equations, solved by Cramer's rule in doubles; null for axes that
        // lie in the plane's normal direction (no solution).
        static Vec3? Solve(double[] a, double[] b, double[] c, double s0, double t0)
        {
            double r0 = s0 - a[3], r1 = t0 - b[3], r2 = c[3];
            double det = Det(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
            if (det == 0)
            {
                return null;
            }

            return new Vec3(
                (float)(Det(r0, a[1], a[2], r1, b[1], b[2], r2, c[1], c[2]) / det),
                (float)(Det(a[0], r0, a[2], b[0], r1, b[2], c[0], r2, c[2]) / det),
                (float)(Det(a[0], a[1], r0, b[0], b[1], r1, c[0], c[1], r2) / det));
        }

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
    }

    /// <summary>
    /// A face's luxels from its cells: each luxel the mean of the cells
    /// around it, as vrad's radial filter makes a luxel of the samples
    /// around it on a face of whole cells.
    /// </summary>
    /// <param name="cells">The face's cells.</param>
    /// <param name="cellLight">Per cell and page, three floats: cell-major, pages inside.</param>
    /// <param name="luxels">Adds to: per page, per luxel, three floats (the lump's page-major order).</param>
    public static void CellsToLuxels(DoorFaceCells cells, ReadOnlySpan<float> cellLight, Span<float> luxels)
    {
        int pages = cells.Pages;
        int w = cells.Width, h = cells.Height, cw = cells.CellsAcross, ch = cells.CellsUp;
        Span<float> acc = stackalloc float[4 * 3];
        for (int t = 0; t < h; t++)
        {
            for (int u = 0; u < w; u++)
            {
                int count = 0;
                acc.Clear();
                for (int dt = -1; dt <= 0; dt++)
                {
                    for (int du = -1; du <= 0; du++)
                    {
                        int cu = u + du, ct = t + dt;
                        if (h == 1)
                        {
                            ct = 0;
                        }

                        if (w == 1)
                        {
                            cu = 0;
                        }

                        if (cu < 0 || ct < 0 || cu >= cw || ct >= ch || (w == 1 && du != 0) || (h == 1 && dt != 0))
                        {
                            continue;
                        }

                        int at = ((ct * cw) + cu) * pages * 3;
                        for (int k = 0; k < pages * 3; k++)
                        {
                            acc[k] += cellLight[at + k];
                        }

                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                for (int page = 0; page < pages; page++)
                {
                    int at = ((page * w * h) + (t * w) + u) * 3;
                    luxels[at] += acc[page * 3] / count;
                    luxels[at + 1] += acc[(page * 3) + 1] / count;
                    luxels[at + 2] += acc[(page * 3) + 2] / count;
                }
            }
        }
    }

    // ---- the response grid ----------------------------------------------------------------------

    /// <summary>
    /// One node of the grid a room's bounced response to door light is
    /// measured on, door-local: <see cref="GridNodes"/> cubed points evenly
    /// over the interior of the room across the joint, where the light the
    /// response stands for comes from (a node is its cell's centre).
    /// </summary>
    /// <param name="node">The node, 0 to <see cref="NodesPerClass"/> - 1, x fastest.</param>
    /// <param name="cell">The cell size.</param>
    /// <param name="wall">The shell's depth (the kit's plug depth).</param>
    public static Vec3 Node(int node, float cell, float wall)
    {
        (float x0, float x1, float y, float z) = Extent(cell, wall);
        int i = node % GridNodes, j = (node / GridNodes) % GridNodes, k = node / (GridNodes * GridNodes);
        return new Vec3(
            x0 + ((i + 0.5f) / GridNodes * (x1 - x0)),
            -y + ((j + 0.5f) / GridNodes * 2 * y),
            -z + ((k + 0.5f) / GridNodes * 2 * z));
    }

    /// <summary>The neighbour's interior, door-local: its depth range beyond the joint and its half extents across and up.</summary>
    private static (float X0, float X1, float Y, float Z) Extent(float cell, float wall) =>
        (2 * wall, (2 * wall) + cell - (2 * wall), (cell / 2) - wall, (cell / 2) - wall);

    /// <summary>
    /// Each emitter's flux through the neighbour's opening per unit of its
    /// intensity (inverse-square nodes first, then constant ones): what
    /// matches an emitter to the light a source sends through the opening.
    /// </summary>
    public static double[] NodeFlux(float width, float height, float cell, float wall)
    {
        const int su = 48, sv = 112;
        double[] flux = new double[EmitterCount];
        double area = width * height / (double)(su * sv);
        for (int n = 0; n < NodesPerClass; n++)
        {
            Vec3 node = Node(n, cell, wall);
            double inverseSquare = 0, constant = 0;
            for (int b = 0; b < sv; b++)
            {
                for (int a = 0; a < su; a++)
                {
                    Vec3 p = new(2 * wall, ((a + 0.5f) / su * width) - (width / 2), ((b + 0.5f) / sv * height) - (height / 2));
                    Vec3 d = p - node;
                    double len2 = d.LengthSquared();
                    double cosine = -d.X / Math.Sqrt(len2);
                    inverseSquare += cosine / len2 * area;
                    constant += cosine * area;
                }
            }

            flux[n] = inverseSquare;
            flux[NodesPerClass + n] = constant;
        }

        return flux;
    }

    /// <summary>
    /// A source's flux through its own room's opening: its irradiance at the
    /// centre of each cell it reaches, times the cell's area. The source is
    /// door-local to the room it enters, so its own opening is the plane two
    /// plug depths beyond.
    /// </summary>
    public static double SourceFlux(in DoorSource source, float width, float height, float depth)
    {
        double area = width * height / (double)CellCount;
        double flux = 0;
        Vec3 inward = new(-1, 0, 0);
        for (int c = 0; c < CellCount; c++)
        {
            if (!Has(source.Cells, c))
            {
                continue;
            }

            // The cell in the source's own across order, seen from here.
            Vec3 centre = CellCentre(c, width, height, 2 * depth);
            centre = new Vec3(centre.X, -centre.Y, centre.Z);
            Vec3 toSource = source.Type == EmitType.SkyLight ? -source.Normal : source.Origin - centre;
            float e = Falloff(source, toSource, -inward);
            flux += e * area;
        }

        return flux;
    }

    /// <summary>
    /// A source's share of each response emitter: its flux through its own
    /// opening, split over the eight nodes around its position (clamped to
    /// the grid) by trilinear weights, each divided by the node's own flux
    /// per unit intensity so that the emitters together send the same flux
    /// through the opening; the sun and constant lights to the constant set,
    /// every other source to the inverse-square set.
    /// </summary>
    /// <param name="source">The source, door-local to the room it enters.</param>
    /// <param name="flux">Its flux (<see cref="SourceFlux"/>) times its colour: what to share out.</param>
    /// <param name="nodeFlux">The emitters' flux per unit intensity (<see cref="NodeFlux"/>).</param>
    /// <param name="cell">The cell size.</param>
    /// <param name="wall">The shell's depth.</param>
    /// <param name="shares">Adds to: per emitter, the colour it takes.</param>
    public static void Project(in DoorSource source, Vec3 flux, ReadOnlySpan<double> nodeFlux, float cell, float wall, Span<Vec3> shares)
    {
        (float x0, float x1, float y, float z) = Extent(cell, wall);
        Vec3 s = source.Origin;
        float gx = Math.Clamp(((s.X - x0) / (x1 - x0) * GridNodes) - 0.5f, 0, GridNodes - 1);
        float gy = Math.Clamp(((s.Y + y) / (2 * y) * GridNodes) - 0.5f, 0, GridNodes - 1);
        float gz = Math.Clamp(((s.Z + z) / (2 * z) * GridNodes) - 0.5f, 0, GridNodes - 1);
        int ix = Math.Min((int)gx, GridNodes - 2), iy = Math.Min((int)gy, GridNodes - 2), iz = Math.Min((int)gz, GridNodes - 2);
        float fx = gx - ix, fy = gy - iy, fz = gz - iz;
        int offset = source.Flat ? NodesPerClass : 0;
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    float w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
                    if (w == 0)
                    {
                        continue;
                    }

                    int e = offset + (((((iz + dz) * GridNodes) + iy + dy) * GridNodes) + ix + dx);
                    shares[e] += flux * (float)(w / nodeFlux[e]);
                }
            }
        }
    }
}
