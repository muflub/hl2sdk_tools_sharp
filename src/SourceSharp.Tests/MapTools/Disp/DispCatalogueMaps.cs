//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Disp;

/// <summary>
/// The displacement stock-gate maps, built from code (plan §2a: map-first).
/// </summary>
/// <remarks>
/// <para>
/// Each map is a sealed 2048-unit room with one or more displacement brushes
/// on its floor, walls or a ramp, chosen to reach every branch the lump
/// comparison can see: each power alone; equal and mixed-power grids (the
/// only way <c>SetupAllowedVerts</c> clears a bit); a wide edge against two
/// narrow ones (<c>CORNER_TO_MIDPOINT</c>/<c>MIDPOINT_TO_CORNER</c>);
/// different start corners (orientations other than <c>CCW_0</c>); point-only
/// contact, flat and bumped (corner lists, and the displaced-corner rule);
/// a slope and a wall; a lightmap that clamps at 125 luxels; a rectangle
/// whose texture runs along its short side (the swapped texinfo); triangle
/// tags and flags; and non-zero offsets.
/// </para>
/// <para>
/// Every number written is exactly representable (integers and quarters),
/// so stock's <c>atof</c>-then-narrow and the managed float parser read the
/// same bits: the gate compares output, not two parsers.
/// </para>
/// <para>
/// Recipe (the stock side runs capped, one wine at a time):
/// <c>DISP_CATALOGUE_EMIT_DIR=&lt;the emission directory&gt; dotnet test --filter
/// TheDisplacementCatalogueEmits</c>, then stock x64 <c>vbsp -v</c> over each
/// VMF, then <c>DISP_STOCK_DIR=</c> the same directory for the gate.
/// </para>
/// </remarks>
public sealed class DispCatalogueMaps
{
    /// <summary>Where <see cref="TheDisplacementCatalogueEmits"/> writes, when set.</summary>
    public const string EmitDirectoryVariable = "DISP_CATALOGUE_EMIT_DIR";

    private const string Floor = "concrete/concretefloor001a";
    private const string Blend = "nature/blendgrassgravel001a";

    /// <summary>Every catalogue map, name to VMF text.</summary>
    public static IReadOnlyList<(string Name, string Vmf)> All =>
    [
        ("p3f_p2_flat", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 2, Flat))),
        ("p3f_p3_bump", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 3, Bumps(7)))),
        ("p3f_p4_random", Map(b => b.FloorDisp(new(-256, -256), 512, 512, 4, Random(11, 48)))),
        ("p3f_p2_blend", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 2, Bumps(3), material: Blend,
            alpha: (x, y) => ((x * 61) + (y * 17)) % 256))),
        ("p3f_grid_same", Map(b => Grid(b, [3, 3, 3, 3]))),
        ("p3f_grid_mixed", Map(b => Grid(b, [2, 3, 4, 3]))),
        ("p3f_half_edge", Map(b => HalfEdge(b, 3, 3, 3))),
        ("p3f_half_edge_mixed", Map(b => HalfEdge(b, 2, 4, 3))),
        ("p3f_rotated", Map(b =>
        {
            b.FloorDisp(new(-256, -256), 256, 256, 2, EdgeZero(5), startCorner: 0);
            b.FloorDisp(new(0, -256), 256, 256, 2, EdgeZero(6), startCorner: 1);
            b.FloorDisp(new(-256, 0), 256, 256, 3, EdgeZero(8), startCorner: 2);
            b.FloorDisp(new(0, 0), 256, 256, 2, EdgeZero(9), startCorner: 3);
        })),
        ("p3f_corner_only", Map(b =>
        {
            b.FloorDisp(new(-512, -512), 256, 256, 2, Flat);
            b.FloorDisp(new(-256, -256), 256, 256, 2, Flat);
            b.FloorDisp(new(256, 256), 256, 256, 3, Random(4, 16));
            b.FloorDisp(new(512, 512), 256, 256, 3, Random(5, 16));
        })),
        ("p3f_slope", Map(b => b.RampDisp(new(-256, -128), 512, 256, 16, 400, 3, Bumps(2)))),
        ("p3f_wall", Map(b => b.WallDisp(new(-128, 256), 256, 256, 3, Random(8, 24)))),
        ("p3f_lm8_clamp", Map(b => b.FloorDisp(new(-500, -128), 1000, 256, 4, Bumps(4), lightmapScale: 8))),
        ("p3f_swap", Map(b => b.FloorDisp(new(-256, -64), 512, 128, 2, Bumps(5), rotateTexture: true))),
        ("p3f_strip", Map(b =>
        {
            int[] powers = [2, 3, 4, 2];
            for (int i = 0; i < 4; i++)
            {
                b.FloorDisp(new(-512 + (i * 256), -128), 256, 256, powers[i], EdgeZero(20 + i));
            }
        })),
        ("p3f_tags_flags", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 3, Bumps(6), flags: 2,
            tags: t => new[] { 0, 1, 8, 9, 2, 6, 16, 48, 11, 63 }[t % 10]))),
        ("p3f_offsets", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 2, Bumps(9),
            offset: (x, y) => new Vec3(x - 2, (y % 2) * 0.5f, 0.25f * x)))),
        ("p3f_smooth_mintess", Map(b => b.FloorDisp(new(-128, -128), 256, 256, 3, Random(12, 20),
            smooth: 30, minTess: 2))),
    ];

    /// <summary>
    /// Emits every map, and checks each file is what the generator wrote. Runs
    /// the same whether or not the directory is set: unset, it writes to a
    /// temporary directory and deletes it, so the fact always examines output.
    /// </summary>
    [Fact]
    public void TheDisplacementCatalogueEmits()
    {
        string? asked = Environment.GetEnvironmentVariable(EmitDirectoryVariable);
        bool keep = !string.IsNullOrEmpty(asked);
        string directory = keep ? asked! : Path.Combine(Path.GetTempPath(), "p3f-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            foreach ((string name, string vmf) in All)
            {
                string path = Path.Combine(directory, name + ".vmf");
                File.WriteAllText(path, vmf, Encoding.ASCII);
                Assert.Equal(vmf, File.ReadAllText(path, Encoding.ASCII));
            }

            Assert.Equal(All.Count, All.Select(e => e.Name).Distinct().Count());
        }
        finally
        {
            if (!keep)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Every map holds at least one dispinfo chunk.</summary>
    [Fact]
    public void EveryCatalogueMapHasADisplacement()
    {
        Assert.All(All, e => Assert.Contains("dispinfo", e.Vmf, StringComparison.Ordinal));
    }

    private static void Grid(Builder b, int[] powers)
    {
        for (int i = 0; i < 4; i++)
        {
            b.FloorDisp(new(-256 + ((i % 2) * 256), -256 + ((i / 2) * 256)), 256, 256, powers[i], EdgeZero(30 + i));
        }
    }

    private static void HalfEdge(Builder b, int big, int lower, int upper)
    {
        b.FloorDisp(new(-512, -256), 512, 512, big, EdgeZero(40));
        b.FloorDisp(new(0, -256), 256, 256, lower, EdgeZero(41));
        b.FloorDisp(new(0, 0), 256, 256, upper, EdgeZero(42));
    }

    private static float Flat(int x, int y, int side) => 0;

    private static Func<int, int, int, float> Bumps(int seed) => (x, y, side) =>
        Quarter(8 + (seed * MathF.Sin((x * 0.9f) + seed) * MathF.Cos(y * 0.7f)));

    private static Func<int, int, int, float> Random(int seed, int amplitude) => (x, y, side) =>
    {
        uint h = (uint)((x * 73856093) ^ (y * 19349663) ^ (seed * 83492791));
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h % (uint)(amplitude * 4)) / 4.0f;
    };

    /// <summary>Heights that vanish on the boundary, so neighbours' corners always agree.</summary>
    private static Func<int, int, int, float> EdgeZero(int seed) => (x, y, side) =>
        (x == 0 || y == 0 || x == side - 1 || y == side - 1) ? 0 : Random(seed, 32)(x, y, side);

    private static float Quarter(float v) => MathF.Round(v * 4) / 4;

    private static string Map(Action<Builder> content)
    {
        Builder b = new();
        b.Room();
        content(b);
        return b.Finish();
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class Builder
    {
        private readonly StringBuilder _solids = new();
        private int _id = 2;

        public void Room()
        {
            const int r = 1040;
            const int t = 16;
            Box(new(-r, -r, -t), new(r, r, 0), Floor);
            Box(new(-r, -r, 512), new(r, r, 512 + t), Floor);
            Box(new(-r - t, -r, 0), new(-r, r, 512), Floor);
            Box(new(r, -r, 0), new(r + t, r, 512), Floor);
            Box(new(-r, -r - t, 0), new(r, -r, 512), Floor);
            Box(new(-r, r, 0), new(r, r + t, 512), Floor);
        }

        public void FloorDisp(
            (float X, float Y) min,
            float sizeX,
            float sizeY,
            int power,
            Func<int, int, int, float> height,
            string material = Floor,
            Func<int, int, float>? alpha = null,
            int startCorner = 0,
            int lightmapScale = 16,
            bool rotateTexture = false,
            int flags = 0,
            Func<int, int>? tags = null,
            Func<int, int, Vec3>? offset = null,
            float smooth = 0,
            int minTess = 0)
        {
            Vec3 lo = new(min.X, min.Y, 0);
            Vec3 hi = new(min.X + sizeX, min.Y + sizeY, 16);

            // The four corners of the top face, for the start position.
            Vec3[] corners =
            [
                new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z),
            ];

            string disp = DispInfo(
                power, corners[startCorner], new Vec3(0, 0, 1), height, alpha, flags, tags, offset, smooth, minTess);

            string u = rotateTexture ? "[0 1 0 0] 0.25" : "[1 0 0 0] 0.25";
            string v = rotateTexture ? "[1 0 0 0] 0.25" : "[0 -1 0 0] 0.25";

            BeginSolid();
            Side($"({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(lo.Y)} {F(hi.Z)})",
                material, u, v, lightmapScale, disp);
            BoxSidesExceptTop(lo, hi, material, lightmapScale);
            EndSolid();
        }

        public void RampDisp(
            (float X, float Y) min, float sizeX, float sizeY, float lowZ, float highZ, int power,
            Func<int, int, int, float> height)
        {
            Vec3 lo = new(min.X, min.Y, 0);
            Vec3 hi = new(min.X + sizeX, min.Y + sizeY, highZ);

            Vec3 start = new(lo.X, lo.Y, lowZ);
            Vec3 up = Vec3.Cross(new Vec3(0, sizeY, 0), new Vec3(sizeX, 0, highZ - lowZ));
            if (up.Z < 0)
            {
                up = -up;
            }

            Vec3 n = up.Normalise().Normalised;
            string disp = DispInfo(power, start, n, height, null, 0, null, null, 0, 0);

            BeginSolid();
            // Top: through (lo.X, *, lowZ) and (hi.X, *, highZ).
            Side($"({F(lo.X)} {F(hi.Y)} {F(lowZ)}) ({F(hi.X)} {F(hi.Y)} {F(highZ)}) ({F(hi.X)} {F(lo.Y)} {F(highZ)})",
                Floor, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", 16, disp);
            Side($"({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)})",
                Floor, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            Side($"({F(lo.X)} {F(lo.Y)} {F(hi.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(hi.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            EndSolid();
        }

        public void WallDisp((float Y, float Z) min, float sizeY, float sizeZ, int power, Func<int, int, int, float> height)
        {
            // A 16-thick slab against the +x wall, displacement on its -x face.
            Vec3 lo = new(1024, min.Y, min.Z);
            Vec3 hi = new(1040, min.Y + sizeY, min.Z + sizeZ);
            string disp = DispInfo(power, new Vec3(lo.X, lo.Y, lo.Z), new Vec3(-1, 0, 0), height, null, 0, null, null, 0, 0);

            BeginSolid();
            Side($"({F(lo.X)} {F(lo.Y)} {F(hi.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", 16, disp);
            Side($"({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(lo.Y)} {F(hi.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", 16, null);
            Side($"({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)})",
                Floor, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            Side($"({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(hi.Z)})",
                Floor, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", 16, null);
            EndSolid();
        }

        public string Finish()
        {
            StringBuilder s = new();
            s.Append("versioninfo\n{\n\t\"editorversion\" \"400\"\n\t\"editorbuild\" \"8075\"\n\t\"mapversion\" \"1\"\n\t\"formatversion\" \"100\"\n\t\"prefab\" \"0\"\n}\n");
            s.Append("world\n{\n\t\"id\" \"1\"\n\t\"mapversion\" \"1\"\n\t\"classname\" \"worldspawn\"\n\t\"skyname\" \"sky_day01_01\"\n");
            s.Append(_solids);
            s.Append("}\n");
            s.Append("entity\n{\n\t\"id\" \"").Append(_id++).Append("\"\n\t\"classname\" \"info_player_start\"\n\t\"origin\" \"0 0 300\"\n\t\"angles\" \"0 0 0\"\n}\n");
            s.Append("entity\n{\n\t\"id\" \"").Append(_id++).Append("\"\n\t\"classname\" \"light\"\n\t\"origin\" \"0 0 400\"\n\t\"_light\" \"255 255 255 200\"\n}\n");
            return s.ToString();
        }

        private void Box(Vec3 lo, Vec3 hi, string material)
        {
            BeginSolid();
            Side($"({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(lo.Y)} {F(hi.Z)})",
                material, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", 16, null);
            BoxSidesExceptTop(lo, hi, material, 16);
            EndSolid();
        }

        private void BoxSidesExceptTop(Vec3 lo, Vec3 hi, string material, int lightmapScale)
        {
            Side($"({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)})",
                material, "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", lightmapScale, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(hi.X)} {F(hi.Y)} {F(lo.Z)}) ({F(hi.X)} {F(lo.Y)} {F(lo.Z)})",
                material, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", lightmapScale, null);
            Side($"({F(lo.X)} {F(lo.Y)} {F(hi.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                material, "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", lightmapScale, null);
            Side($"({F(hi.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(hi.Z)}) ({F(lo.X)} {F(hi.Y)} {F(lo.Z)})",
                material, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", lightmapScale, null);
            Side($"({F(hi.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(lo.Z)}) ({F(lo.X)} {F(lo.Y)} {F(hi.Z)})",
                material, "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", lightmapScale, null);
        }

        private void BeginSolid() => _solids.Append("\tsolid\n\t{\n\t\t\"id\" \"").Append(_id++).Append("\"\n");

        private void EndSolid() => _solids.Append("\t}\n");

        private void Side(string plane, string material, string u, string v, int lightmapScale, string? disp)
        {
            _solids.Append("\t\tside\n\t\t{\n\t\t\t\"id\" \"").Append(_id++).Append("\"\n")
                .Append("\t\t\t\"plane\" \"").Append(plane).Append("\"\n")
                .Append("\t\t\t\"material\" \"").Append(material).Append("\"\n")
                .Append("\t\t\t\"uaxis\" \"").Append(u).Append("\"\n")
                .Append("\t\t\t\"vaxis\" \"").Append(v).Append("\"\n")
                .Append("\t\t\t\"rotation\" \"0\"\n")
                .Append("\t\t\t\"lightmapscale\" \"").Append(lightmapScale).Append("\"\n")
                .Append("\t\t\t\"smoothing_groups\" \"0\"\n");
            if (disp is not null)
            {
                _solids.Append(disp);
            }

            _solids.Append("\t\t}\n");
        }

        private static string DispInfo(
            int power,
            Vec3 start,
            Vec3 normal,
            Func<int, int, int, float> height,
            Func<int, int, float>? alpha,
            int flags,
            Func<int, int>? tags,
            Func<int, int, Vec3>? offset,
            float smooth,
            int minTess)
        {
            int side = (1 << power) + 1;
            StringBuilder s = new();
            s.Append("\t\t\tdispinfo\n\t\t\t{\n")
                .Append("\t\t\t\t\"power\" \"").Append(power).Append("\"\n")
                .Append("\t\t\t\t\"startposition\" \"[").Append(F(start.X)).Append(' ').Append(F(start.Y)).Append(' ').Append(F(start.Z)).Append("]\"\n")
                .Append("\t\t\t\t\"flags\" \"").Append(flags).Append("\"\n")
                .Append("\t\t\t\t\"elevation\" \"0\"\n")
                .Append("\t\t\t\t\"subdiv\" \"0\"\n")
                .Append("\t\t\t\t\"mintess\" \"").Append(minTess).Append("\"\n")
                .Append("\t\t\t\t\"smooth\" \"").Append(F(smooth)).Append("\"\n");

            Rows(s, "normals", side, (x, y) => $"{F(normal.X)} {F(normal.Y)} {F(normal.Z)}");
            Rows(s, "distances", side, (x, y) => F(height(x, y, side)));
            Rows(s, "offsets", side, (x, y) =>
            {
                Vec3 o = offset?.Invoke(x, y) ?? Vec3.Zero;
                return $"{F(o.X)} {F(o.Y)} {F(o.Z)}";
            });
            Rows(s, "offset_normals", side, (x, y) => $"{F(normal.X)} {F(normal.Y)} {F(normal.Z)}");
            Rows(s, "alphas", side, (x, y) => F(alpha?.Invoke(x, y) ?? 0));

            int triCols = 1 << power;
            s.Append("\t\t\t\ttriangle_tags\n\t\t\t\t{\n");
            for (int r = 0; r < triCols; r++)
            {
                s.Append("\t\t\t\t\t\"row").Append(r).Append("\" \"");
                for (int c = 0; c < triCols * 2; c++)
                {
                    if (c > 0)
                    {
                        s.Append(' ');
                    }

                    s.Append(tags?.Invoke((r * triCols * 2) + c) ?? 9);
                }

                s.Append("\"\n");
            }

            s.Append("\t\t\t\t}\n");
            s.Append("\t\t\t}\n");
            return s.ToString();
        }

        private static void Rows(StringBuilder s, string name, int side, Func<int, int, string> cell)
        {
            s.Append("\t\t\t\t").Append(name).Append("\n\t\t\t\t{\n");
            for (int y = 0; y < side; y++)
            {
                s.Append("\t\t\t\t\t\"row").Append(y).Append("\" \"");
                for (int x = 0; x < side; x++)
                {
                    if (x > 0)
                    {
                        s.Append(' ');
                    }

                    s.Append(cell(x, y));
                }

                s.Append("\"\n");
            }

            s.Append("\t\t\t\t}\n");
        }
    }
}
