//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The base bake's library, compiled and lit once for a class: the hub, lit
/// by a light, holding a static prop and a door (<c>func_door</c>), and no
/// sky; and the other room, with a sky ceiling and a switchable light. The
/// library's sun stands in the gap. The rooms are lit with static prop
/// lighting on, so the props' vertex colours are baked too. The same library
/// compiled unlit is beside it, and each level's links and full compiles are
/// built once and shared.
/// </summary>
public sealed class LitRoomsFixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BspData>>> _maps = new(StringComparer.Ordinal);

    private readonly Lazy<Task<RoomLibrary>> _doorLit = new(() => RoomLightHarness.CompileAsync(Library, options: Options, doorLight: true));

    /// <summary>The switches the fixture's rooms are lit with.</summary>
    public static VradOptions Options { get; } = RoomLightHarness.Options with { StaticPropLighting = true };

    /// <summary>The hub's light, room-local.</summary>
    public static readonly Vec3 HubLight = new(128, 128, 150);

    /// <summary>The library VMF.</summary>
    public static VmfDocument Library { get; } = RoomLightHarness.Library(
        true,
        [1],
        (0, RoomLightHarness.Light(800, HubLight)),
        (0, RoomPropHarness.Prop(801, RoomPropHarness.BoxModel, new Vec3(60, 70, 16), "0 30 0")),
        (0, RoomBrushHarness.Door(802, new Vec3(150, 40, 16), new Vec3(190, 56, 120))),
        (1, RoomLightHarness.Light(810, new Vec3(100, 60, 120), "cxry_lamp")));

    /// <summary>The rooms, lit.</summary>
    public RoomLibrary Lit { get; private set; } = null!;

    /// <summary>The same rooms, unlit.</summary>
    public RoomLibrary Unlit { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Lit = await RoomLightHarness.CompileAsync(Library, options: Options);
        Unlit = await RoomLightHarness.CompileAsync(Library, light: false);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The same rooms lit with their door light (PR 10), compiled on first
    /// use so that the classes that do not need it do not pay for it.
    /// </summary>
    public Task<RoomLibrary> DoorLitAsync() => _doorLit.Value;

    /// <summary>A level of the given rows linked from the rooms lit with their door light.</summary>
    public Task<BspData> DoorLinkedAsync(string row) => MapAsync("door:" + row, async () => (await RoomLightHarness.LinkAsync(await DoorLitAsync(), RoomPropHarness.Level(row))).Bsp);

    /// <summary>A level of the given rows linked from the lit rooms.</summary>
    public Task<BspData> LinkedAsync(string row) => MapAsync("lit:" + row, async () => (await RoomLightHarness.LinkAsync(Lit, RoomPropHarness.Level(row))).Bsp);

    /// <summary>The level linked from the unlit rooms, then lit by vrad as it stands.</summary>
    public Task<BspData> RelitAsync(string row) => MapAsync("relit:" + row, async () =>
        await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(Unlit, RoomPropHarness.Level(row))).Bsp, Options));

    /// <summary>The level flattened and compiled whole: vbsp, vvis and vrad with the rooms' switches.</summary>
    public Task<BspData> FlatAsync(string row) => MapAsync("flat:" + row, () => RoomLightHarness.CompileFlatLitAsync(Library, RoomPropHarness.Level(row), Options));

    private Task<BspData> MapAsync(string key, Func<Task<BspData>> make) =>
        _maps.GetOrAdd(key, _ => new Lazy<Task<BspData>>(make)).Value;
}

/// <summary>How two lit maps compare, luxel by luxel.</summary>
internal static class LitCompare
{
    /// <summary>A face whose lightmap is one luxel across in some direction: its luxel sits on the face's edges.</summary>
    public static bool Thin(DFace face) =>
        face.LightmapTextureSizeInLuxels[0] == 0 || face.LightmapTextureSizeInLuxels[1] == 0;

    /// <summary>The lighting lump's colours of one range.</summary>
    public static ColorRgbExp32[] Colours(BspData bsp, bool hdr = false) =>
        MemoryMarshal.Cast<byte, ColorRgbExp32>((hdr ? bsp[BspLump.LightingHdr] : bsp[BspLump.Lighting]).Data.Span).ToArray();

    /// <summary>
    /// Two maps with the same faces, face by face: whether every face has
    /// the same styles, and how many luxels of faces more than one luxel
    /// across differ, and how many of the thin faces'.
    /// </summary>
    public static (bool Styles, int Luxels, int Differ, int ThinDiffer) SameFaces(BspData a, BspData b) => SameFaces(a, b, null);

    /// <summary><see cref="SameFaces(BspData, BspData)"/>, telling each differing luxel on a face more than one luxel across (face, luxel, both colours).</summary>
    public static (bool Styles, int Luxels, int Differ, int ThinDiffer) SameFaces(BspData a, BspData b, Action<int, int, Vec3, Vec3>? differs)
    {
        DFace[] fa = BspStructView.As<DFace>(a[BspLump.Faces]).ToArray();
        DFace[] fb = BspStructView.As<DFace>(b[BspLump.Faces]).ToArray();
        Assert.Equal(fa.Length, fb.Length);
        ColorRgbExp32[] la = Colours(a);
        ColorRgbExp32[] lb = Colours(b);
        bool styles = true;
        int luxels = 0, differ = 0, thin = 0;
        for (int f = 0; f < fa.Length; f++)
        {
            for (int k = 0; k < 4; k++)
            {
                styles &= fa[f].Styles[k] == fb[f].Styles[k];
            }

            Assert.Equal(fa[f].LightOfs < 0, fb[f].LightOfs < 0);
            if (fa[f].LightOfs < 0)
            {
                continue;
            }

            int count = Styles(fa[f]) * ((fa[f].LightmapTextureSizeInLuxels[0] + 1) * (fa[f].LightmapTextureSizeInLuxels[1] + 1));
            for (int i = 0; i < count; i++)
            {
                luxels++;
                if (!Same(la[(fa[f].LightOfs / 4) + i], lb[(fb[f].LightOfs / 4) + i]))
                {
                    if (Thin(fa[f]))
                    {
                        thin++;
                    }
                    else
                    {
                        differ++;
                        differs?.Invoke(f, i, la[(fa[f].LightOfs / 4) + i].ToLinear(), lb[(fb[f].LightOfs / 4) + i].ToLinear());
                    }
                }
            }
        }

        return (styles, luxels, differ, thin);
    }

    /// <summary>How many styles a face lights.</summary>
    public static int Styles(DFace face)
    {
        int n = 0;
        while (n < 4 && face.Styles[n] != 255)
        {
            n++;
        }

        return n;
    }

    /// <summary>Two encoded colours are the same bytes.</summary>
    public static bool Same(ColorRgbExp32 a, ColorRgbExp32 b) =>
        a.R == b.R && a.G == b.G && a.B == b.B && a.Exponent == b.Exponent;

    /// <summary>
    /// A map's luxels by where they are: every drawn face's luxels keyed by
    /// the luxel's point in the world (on the face's plane, at its integer
    /// lightmap coordinates, to a hundredth of a unit), its outward normal
    /// and its style, so faces cut differently by two compiles, and whose
    /// lightmap coordinates count from different offsets, still meet at the
    /// same keys; each key's colours, and whether any face it came from is
    /// one luxel across.
    /// </summary>
    public static Dictionary<(long, long, long, int, int, int, int), (List<ColorRgbExp32> Colours, bool Thin)> Lattice(BspData bsp)
    {
        DFace[] faces = BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray();
        DPlane[] planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        TexInfo[] texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        ColorRgbExp32[] light = Colours(bsp);
        Dictionary<(long, long, long, int, int, int, int), (List<ColorRgbExp32>, bool)> map = [];
        foreach (DFace face in faces)
        {
            TexInfo tex = texInfos[face.TexInfo];
            if (face.LightOfs < 0 || (tex.Flags & (int)SourceSharp.MapTools.Materials.SurfaceFlags.NoDraw) != 0)
            {
                continue;
            }

            DPlane plane = planes[face.PlaneNum];
            Vec3 n = face.Side != 0 ? -plane.Normal : plane.Normal;
            int w = face.LightmapTextureSizeInLuxels[0] + 1;
            int h = face.LightmapTextureSizeInLuxels[1] + 1;
            double[] sv = [tex.LightmapVecsLuxelsPerWorldUnits[0], tex.LightmapVecsLuxelsPerWorldUnits[1], tex.LightmapVecsLuxelsPerWorldUnits[2], tex.LightmapVecsLuxelsPerWorldUnits[3]];
            double[] tv = [tex.LightmapVecsLuxelsPerWorldUnits[4], tex.LightmapVecsLuxelsPerWorldUnits[5], tex.LightmapVecsLuxelsPerWorldUnits[6], tex.LightmapVecsLuxelsPerWorldUnits[7]];
            double[] pv = [plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.Dist];
            for (int k = 0; k < Styles(face); k++)
            {
                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        (double x, double y, double z) = Solve(sv, tv, pv, face.LightmapTextureMinsInLuxels[0] + i, face.LightmapTextureMinsInLuxels[1] + j);
                        (long, long, long, int, int, int, int) key = (
                            (long)Math.Round(x * 100), (long)Math.Round(y * 100), (long)Math.Round(z * 100),
                            (int)MathF.Round(n.X * 1000), (int)MathF.Round(n.Y * 1000), (int)MathF.Round(n.Z * 1000), face.Styles[k]);
                        if (!map.TryGetValue(key, out (List<ColorRgbExp32> Colours, bool Thin) entry))
                        {
                            entry = ([], false);
                        }

                        entry.Colours.Add(light[(face.LightOfs / 4) + (k * w * h) + (j * w) + i]);
                        map[key] = (entry.Colours, entry.Thin || Thin(face));
                    }
                }
            }
        }

        return map;

        // The point with lightmap coordinates (s, t) on the plane: three
        // equations, solved by Cramer's rule in doubles.
        static (double, double, double) Solve(double[] a, double[] b, double[] c, double s, double t)
        {
            double r0 = s - a[3], r1 = t - b[3], r2 = c[3];
            double det = Det(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
            return (
                Det(r0, a[1], a[2], r1, b[1], b[2], r2, c[1], c[2]) / det,
                Det(a[0], r0, a[2], b[0], r1, b[2], c[0], r2, c[2]) / det,
                Det(a[0], a[1], r0, b[0], b[1], r1, c[0], c[1], r2) / det);
        }

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
    }

    /// <summary>The largest relative difference of two linear colours, against the brighter's largest channel.</summary>
    public static double Relative(Vec3 a, Vec3 b)
    {
        double scale = Math.Max(1e-3, Math.Max(Math.Max(Math.Max(a.X, a.Y), a.Z), Math.Max(Math.Max(b.X, b.Y), b.Z)));
        return Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z))) / scale;
    }

    /// <summary>A sorted list's quantile.</summary>
    public static double Quantile(List<double> sorted, double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
}
