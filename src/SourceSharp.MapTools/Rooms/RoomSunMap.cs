//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Light;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A library's 3D skybox as the sun sees it (the rooms design, the skybox
/// parallax, D36): for every line through the skybox along the sun's
/// direction, the heights at which a point on it stops or starts seeing the
/// sun through the skybox. The link reads it to move a sky room's sun from
/// its bakes' cell to the cell it is placed at, without tracing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> vrad of a level recasts a sky ray that leaves a
/// room through its sky from <c>camera + p / scale</c> into the skybox, and
/// counts the sky as blocked when that ray meets anything but sky there. A
/// sky room is baked per turn at one cell (<see cref="RoomSkybox"/>), so a
/// room placed elsewhere sees the skybox from its bakes' cell, off by its
/// cell's offset over the scale. The sun is the term that moves most: its
/// rays all run one way, so where they are stopped in the skybox is a
/// function of the recast start alone, which this map holds for every
/// start. The sky ambient's own parallax, whose rays run every way, and
/// the bounce of the sun's change are left as they were baked: the
/// design's measured residual.
/// </para>
/// <para>
/// <b>What a texel holds.</b> The skybox's shadow casters (loaded as a
/// room's bake loads them) are traced along the direction towards the sun,
/// <see cref="Toward"/>, which the skybox's own bake gave its sun
/// (<see cref="RoomLighting.SkyLdr"/>, turn 0, the world's frame: the
/// skybox never turns). Every line with that direction is named by where it
/// crosses the plane <c>z = </c><see cref="PlaneZ"/> (the camera's height),
/// and the plane is cut into one-unit texels, one a skybox unit, which is
/// one luxel of a room at the usual scale of 16 and lightmap scale of 16.
/// Along the line through a texel's centre the tracer's hits are found one
/// after another, bottom to top; a point on the line sees the sun when the
/// next hit above it is sky or there is none, as vrad's recast answers it.
/// So visibility along a line is a step function of height, stored as its
/// value below every hit (<see cref="Heads"/>' low bit) and the heights
/// where it changes (<see cref="Toggles"/>). A line that meets nothing
/// needs no toggle, and outside the grid every point sees the sun.
/// </para>
/// <para>
/// <b>Evaluation</b> (<see cref="Visibility"/>) projects a skybox point
/// along the direction onto the plane and blends the four nearest texels'
/// answers at the point's height, bilinearly: the soft edge a luxel's own
/// supersampling gives a shadow edge, a luxel wide.
/// </para>
/// <para>
/// <b>What it leaves out</b>: a sun's spread (every sample is the sun's
/// central direction) and coverage short of full (a partly covering caster
/// counts as sky); both are part of the residual.
/// </para>
/// <para>
/// <b>The section</b> (<see cref="SectionTag"/>, <c>SUNM</c>) sits with the
/// skybox room's entry, written only for a lit library with a skybox and a
/// sun; the link sections' framing, Brotli-coded: revision, the direction,
/// the camera and scale, the plane's height, the grid's origin and size,
/// one head byte a texel (toggle count times two, plus the value below),
/// then every toggle as a float, texel after texel. An older build skips
/// the tag and links as before.
/// </para>
/// </remarks>
internal sealed class RoomSunMap
{
    /// <summary>The tag of the skybox room's sun map section.</summary>
    public const string SectionTag = "SUNM";

    /// <summary>The most hits one line is followed through; a line of more stores what it found.</summary>
    public const int MaxHits = 64;

    /// <summary>The most texels a map may hold: past it the sun's slant over the skybox is too long to store, and no map is made.</summary>
    public const long MaxTexels = 1L << 22;

    /// <summary>How far past a hit, in skybox units of height, the next trace along a line starts.</summary>
    internal const float Step = 1.0e-2f;

    private readonly int[] _starts;

    /// <summary>Makes a map from its parts; <paramref name="heads"/> and <paramref name="toggles"/> must agree.</summary>
    internal RoomSunMap(Vec3 toward, Vec3 camera, float scale, float planeZ, int originU, int originV, int width, int height, byte[] heads, float[] toggles)
    {
        Toward = toward;
        Camera = camera;
        Scale = scale;
        PlaneZ = planeZ;
        OriginU = originU;
        OriginV = originV;
        Width = width;
        Height = height;
        Heads = heads;
        Toggles = toggles;
        _starts = new int[heads.Length + 1];
        for (int i = 0; i < heads.Length; i++)
        {
            _starts[i + 1] = _starts[i] + (heads[i] >> 1);
        }

        if (heads.Length != (long)width * height || _starts[^1] != toggles.Length)
        {
            throw new ArgumentException("a sun map's heads and toggles disagree with its size");
        }
    }

    /// <summary>The unit direction towards the sun, skybox-local (the world's: the skybox never turns); its z is above zero.</summary>
    public Vec3 Toward { get; }

    /// <summary>The skybox's camera, skybox-local.</summary>
    public Vec3 Camera { get; }

    /// <summary>The camera's scale: a world unit is <c>1 / scale</c> skybox units.</summary>
    public float Scale { get; }

    /// <summary>The height of the plane lines are named on.</summary>
    public float PlaneZ { get; }

    /// <summary>The grid's first texel column, in skybox units along x.</summary>
    public int OriginU { get; }

    /// <summary>The grid's first texel row, in skybox units along y.</summary>
    public int OriginV { get; }

    /// <summary>Texels along x.</summary>
    public int Width { get; }

    /// <summary>Texels along y.</summary>
    public int Height { get; }

    /// <summary>Per texel, row by row: its toggle count times two, plus 1 when a point below every hit sees the sun.</summary>
    public byte[] Heads { get; }

    /// <summary>Every texel's toggle heights, ascending within a texel, texel after texel.</summary>
    public float[] Toggles { get; }

    /// <summary>
    /// Where a world point's sky ray is recast to in the skybox, as vrad
    /// recasts it: <c>camera + p / scale</c>, the scale's reciprocal taken
    /// once as vrad takes it.
    /// </summary>
    public Vec3 Recast(Vec3 world) => Camera + (world * (1.0f / Scale));

    /// <summary>
    /// How much a skybox point sees the sun through the skybox, 0 to 1: the
    /// four nearest texels' lines at the point's height, blended bilinearly.
    /// </summary>
    /// <param name="point">A skybox-local point (a recast start).</param>
    public float Visibility(Vec3 point)
    {
        float along = (point.Z - PlaneZ) / Toward.Z;
        float u = point.X - (Toward.X * along) - OriginU - 0.5f;
        float v = point.Y - (Toward.Y * along) - OriginV - 0.5f;
        float fu = MathF.Floor(u), fv = MathF.Floor(v);
        float a = u - fu, b = v - fv;
        int i = (int)Math.Clamp(fu, -2f, Width + 1f), j = (int)Math.Clamp(fv, -2f, Height + 1f);
        return ((1 - a) * (1 - b) * Line(i, j, point.Z))
            + (a * (1 - b) * Line(i + 1, j, point.Z))
            + ((1 - a) * b * Line(i, j + 1, point.Z))
            + (a * b * Line(i + 1, j + 1, point.Z));
    }

    /// <summary>Whether a point at height <paramref name="z"/> on texel (i, j)'s line sees the sun: 1 or 0; 1 off the grid.</summary>
    internal float Line(int i, int j, float z)
    {
        if (i < 0 || j < 0 || i >= Width || j >= Height)
        {
            return 1f;
        }

        int texel = (j * Width) + i;
        bool sees = (Heads[texel] & 1) != 0;
        for (int t = _starts[texel]; t < _starts[texel + 1] && Toggles[t] <= z; t++)
        {
            sees = !sees;
        }

        return sees ? 1f : 0f;
    }

    /// <summary>
    /// The heights where visibility changes along a line, from its hits in
    /// order of height (each with whether it is sky), and the value below
    /// them: a point sees the sun when the next hit above it is sky or there
    /// is none.
    /// </summary>
    internal static (bool Below, List<float> Toggles) StepFunction(IReadOnlyList<(float Z, bool Sky)> hits)
    {
        bool below = hits.Count == 0 || hits[0].Sky;
        List<float> toggles = [];
        bool current = below;
        for (int k = 0; k < hits.Count; k++)
        {
            bool above = k + 1 >= hits.Count || hits[k + 1].Sky;
            if (above != current)
            {
                toggles.Add(hits[k].Z);
                current = above;
            }
        }

        return (below, toggles);
    }

    /// <summary>
    /// The map of a compiled skybox under a sun, or null when there is none
    /// to make: no camera, a sun at or below the horizon, or a grid past
    /// <see cref="MaxTexels"/>.
    /// </summary>
    /// <param name="skybox">The skybox room's compile.</param>
    /// <param name="sky">The sun's world lights its bake gave, turn 0.</param>
    /// <param name="options">The switches its casters are loaded with (the rooms' own).</param>
    /// <param name="content">The game's content, for its props' collision.</param>
    /// <param name="parallelism">The pool the lines are traced on; any degree gives the same bytes.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static async Task<RoomSunMap?> BuildAsync(
        RoomObject skybox,
        DWorldLight[]? sky,
        VradOptions options,
        IContentFileSystem content,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skybox);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parallelism);
        if (TowardSun(sky) is not { } toward || RoomSkybox.Of(skybox).LocalCameras is not [(Vec3 camera, float scale), ..])
        {
            return null;
        }

        ShadowCasterLoadReport loaded = await ShadowCasterLoader.LoadAsync(
            skybox.Bsp, options, content, NullPropCollisionSource.Instance, cancellationToken: cancellationToken).ConfigureAwait(false);
        ShadowCasterSet casters = loaded.Set;
        float planeZ = camera.Z;
        if (Grid(casters.Triangles, toward, planeZ) is not { } grid)
        {
            return null;
        }

        // Built on the first line traced: a skybox without casters has no
        // lines and needs none.
        Lazy<KdRayTracer> tracer = new(() => casters.BuildTracer(options.Compliance));
        (int originU, int originV, int width, int height, float bottom, float top) = grid;
        (bool Below, List<float> Toggles)[][] rows = new (bool, List<float>)[height][];
        using (WorkQueue queue = new(parallelism))
        {
            await queue.RunAsync(
                height,
                (j, _) =>
                {
                    var row = new (bool, List<float>)[width];
                    for (int i = 0; i < width; i++)
                    {
                        row[i] = StepFunction(Hits(tracer.Value, toward, planeZ, originU + i + 0.5f, originV + j + 0.5f, bottom, top));
                    }

                    rows[j] = row;
                },
                options: null,
                cancellationToken).ConfigureAwait(false);
        }

        byte[] heads = new byte[width * height];
        List<float> toggles = [];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                (bool below, List<float> own) = rows[j][i];
                heads[(j * width) + i] = (byte)((own.Count << 1) | (below ? 1 : 0));
                toggles.AddRange(own);
            }
        }

        return new RoomSunMap(toward, camera, scale, planeZ, originU, originV, width, height, heads, [.. toggles]);
    }

    /// <summary>
    /// The grid over casters' projection along the sun onto the plane, one
    /// texel of margin round it, and the heights a line is traced between
    /// (a unit below the lowest caster to one above the highest); an empty
    /// grid for no casters; null for a grid past <see cref="MaxTexels"/>.
    /// </summary>
    internal static (int OriginU, int OriginV, int Width, int Height, float Bottom, float Top)? Grid(
        ReadOnlySpan<TracedTriangle> triangles, Vec3 toward, float planeZ)
    {
        if (triangles.IsEmpty)
        {
            return (0, 0, 0, 0, 0f, 0f);
        }

        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (TracedTriangle t in triangles)
        {
            foreach (Vec3 p in (ReadOnlySpan<Vec3>)[t.V0, t.V1, t.V2])
            {
                float along = (p.Z - planeZ) / toward.Z;
                float u = p.X - (toward.X * along), v = p.Y - (toward.Y * along);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
                minZ = Math.Min(minZ, p.Z);
                maxZ = Math.Max(maxZ, p.Z);
            }
        }

        long originU = (long)MathF.Floor(minU) - 1, originV = (long)MathF.Floor(minV) - 1;
        long width = (long)MathF.Ceiling(maxU) - originU + 1, height = (long)MathF.Ceiling(maxV) - originV + 1;
        return width * height > MaxTexels ? null : ((int)originU, (int)originV, (int)width, (int)height, minZ - 1f, maxZ + 1f);
    }

    /// <summary>The unit direction towards the sun from its world lights (the sky light's, reversed), or null for none or one at or below the horizon.</summary>
    internal static Vec3? TowardSun(DWorldLight[]? sky)
    {
        foreach (DWorldLight light in sky ?? [])
        {
            if (light.Type == (int)EmitType.SkyLight)
            {
                Vec3 toward = -light.Normal;
                return toward.Z > 0f ? toward : null;
            }
        }

        return null;
    }

    /// <summary>A line's hits, bottom to top, each with whether it is sky, from below every caster to above them all.</summary>
    private static List<(float Z, bool Sky)> Hits(KdRayTracer tracer, Vec3 toward, float planeZ, float u, float v, float bottom, float top)
    {
        List<(float, bool)> hits = [];
        Span<HitId> hit = stackalloc HitId[1];
        Vec3 end = At(top);
        float z = bottom;
        while (hits.Count < MaxHits && z < top)
        {
            Vec3 start = At(z);
            Ray[] ray = [LightRayLog.MakeRay(start, end)];
            tracer.TraceClosest(ray, hit, RayTraceOptions.StockExact);
            if (!LightRayLog.IsBlocking(hit[0]))
            {
                break;
            }

            float at = start.Z + ((end.Z - start.Z) * hit[0].Fraction);
            hits.Add((at, LightRayLog.Occlusion(hit[0]) < 1.0f));
            z = Math.Max(at, z) + Step;
        }

        return hits;

        Vec3 At(float height)
        {
            float along = (height - planeZ) / toward.Z;
            return new Vec3(u + (toward.X * along), v + (toward.Y * along), height);
        }
    }

    /// <summary>The map as its pack section.</summary>
    public RoomPackSectionData ToSection()
    {
        RoomLinkSections.Writer w = new();
        w.Int(RoomLinkSections.Revision);
        w.Structs<Vec3>([Toward, Camera], counted: false);
        w.Structs<float>([Scale, PlaneZ], counted: false);
        w.Int(OriginU);
        w.Int(OriginV);
        w.Int(Width);
        w.Int(Height);
        w.Raw(Heads);
        w.Structs<float>(Toggles);
        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.Brotli));
    }

    /// <summary>A map read back from its section, or null for none (absent, or a revision this build does not read).</summary>
    /// <exception cref="LinkException">The section is damaged or out of shape.</exception>
    public static RoomSunMap? Read(ArraySegment<byte>? section, string room)
    {
        if (RoomLinkSections.Open(section, room, SectionTag) is not { } r)
        {
            return null;
        }

        Vec3[] vectors = r.Structs<Vec3>("vectors", 2, counted: false);
        float[] floats = r.Structs<float>("scalars", 2, counted: false);
        int originU = r.Int(), originV = r.Int(), width = r.Int(), height = r.Int();
        if (width < 0 || height < 0 || (long)width * height > MaxTexels)
        {
            throw r.Mismatch($"a sun map of {width} by {height} texels");
        }

        if (!(vectors[0].Z > 0f) || !(floats[0] > 0f) || !float.IsFinite(floats[1]))
        {
            throw r.Mismatch("a sun map whose sun, scale or plane is out of range");
        }

        byte[] heads = r.Structs<byte>("texel heads", width * height, counted: false);
        long toggleCount = heads.Sum(h => (long)(h >> 1));
        float[] toggles = r.Structs<float>("toggles", r.Count("toggles"), counted: false);
        if (toggles.Length != toggleCount)
        {
            throw r.Mismatch($"{toggles.Length} toggles where its texels count {toggleCount}");
        }

        r.End();
        return new RoomSunMap(vectors[0], vectors[1], floats[0], floats[1], originU, originV, width, height, heads, toggles);
    }
}
