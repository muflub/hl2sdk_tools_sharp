using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// The shape that makes the second half of <c>AddBrushBevels</c> do work — the
/// gap Phase 3a handed this lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> <c>map.cpp:533-610</c> adds an EDGE bevel for every
/// non-axial edge whose slanted-axial supporting plane is outside the hull.
/// Across all 27 reference maps it never fires once: the catalogue's only
/// non-axial brush is <c>l0_nonaxial_wedge</c> (2 box bevels, 0 edge bevels),
/// the 46,794-line corpus map produces none, and Phase 3a could not construct
/// a shape that produced one. A stage that adds collision planes to brushes is
/// not gated by a corpus in which it does nothing.
/// </para>
/// <para>
/// <b>Why a wedge, a pyramid and a corner-cut box all fail, which is what made
/// the shape hard to find.</b> The candidate plane is
/// <c>cross(edge, axis)</c> — a plane containing the edge and parallel to one
/// world axis — and it is accepted only if it is not already a plane of the
/// brush and the whole brush is behind it. For a brush with ONE slanted face,
/// every non-axial edge of that face is shared with an axial face, and the
/// three candidates from it come out either axial (already a box bevel) or
/// equal to the slanted face's own plane (already a side). There is nothing
/// left to add. The shape has to have an edge between TWO slanted faces, so
/// that the supporting planes through it lie strictly between two face normals
/// and are therefore neither.
/// </para>
/// <para>
/// <b>The shape that works is a regular octahedron.</b> Vertices on the six
/// axes at ±128; faces the eight planes <c>±x ±y ±z = 128</c>. Every one of
/// its twelve edges lies between two slanted faces, has a non-axial direction
/// such as <c>(-1, 1, 0)/√2</c>, and has exactly one supporting plane of the
/// form <c>cross(edge, axis)</c> — for that edge,
/// <c>cross(e, Z) = (1, 1, 0)/√2</c> at distance <c>128/√2</c>, which touches
/// the hull along the edge and is none of the eight faces nor any of the six
/// box bevels. Twelve edges, twelve such planes:
/// <c>(±1, ±1, 0)</c>, <c>(±1, 0, ±1)</c>, <c>(0, ±1, ±1)</c>, each over √2.
/// </para>
/// <para>
/// Every edge is walked TWICE, once from each of its two faces, and the second
/// visit finds the plane already present and stops — so the count is twelve
/// and not twenty-four.
/// </para>
/// </remarks>
public class EdgeBevelShapes
{
    /// <summary>The environment variable naming where to write them.</summary>
    public const string EmitDirectoryVariable = "CATALOGUE_EMIT_DIR";

    /// <summary>
    /// A regular octahedron of radius 128, centred at (512, 512, 0) — well
    /// inside one 1024-unit block.
    /// </summary>
    /// <remarks>
    /// Eight slanted faces and no axial plane at all, so
    /// <c>AddBrushBevels</c> adds all six box bevels AND then finds twelve edge
    /// bevels: 1 brush, 26 total sides, 6 boxbevels, 12 edgebevels.
    /// </remarks>
    public const string Octahedron = "x0_octahedron";

    /// <summary>
    /// The same octahedron centred on the WORLD ORIGIN, where its six vertices
    /// land exactly on the block grid's boundary planes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately pathological, and kept because it found something.</b>
    /// At the origin every one of the eight faces has two of its three
    /// vertices on <c>x = 0</c>, <c>y = 0</c> or both, so the block clip cuts
    /// four of them into zero-area slivers. Stock keeps both slivers in block
    /// (0,-1) and this port keeps one: 4 visible faces against 3, while the
    /// tree itself — 2 visible nodes, 3 leaves — agrees exactly.
    /// </para>
    /// <para>
    /// The cause is not a decision either compiler makes. Because
    /// <c>MakeBrushWindings</c> clips with <c>BRUSH_CLIP_EPSILON</c>
    /// (<c>map.cpp:644</c>), the vertices are not at 0 but within about 0.004
    /// of it — the loaded brush bounds are
    /// <c>(-128.00195, -128.0039, -128.00293)</c> — and which side of the
    /// block plane a sliver's vertex falls on is then decided by the last bits
    /// of those coordinates. It is the one place this lane has found where
    /// stock and this port can legitimately differ, and it is a face COUNT on a
    /// zero-area polygon, not geometry anyone can see.
    /// </para>
    /// <para>
    /// It is excluded from the block gate by name, with that reason, and it is
    /// still held to stock on the load counts — which is where the twelve edge
    /// bevels are measured and where it agrees exactly.
    /// </para>
    /// </remarks>
    public const string OctahedronOnTheSeam = "x0_octahedron_seam";

    /// <summary>The half-extent of the octahedron, in world units.</summary>
    public const int Radius = 128;

    /// <summary>Where <see cref="Octahedron"/> is centred.</summary>
    public static (int X, int Y, int Z) Centre => (512, 512, 0);

    /// <summary>
    /// Writes the VMF next to the catalogue's, when
    /// <c>CATALOGUE_EMIT_DIR</c> says where.
    /// </summary>
    [Fact]
    public void BothOctahedraEmit()
    {
        string? directory = Environment.GetEnvironmentVariable(EmitDirectoryVariable);

        if (string.IsNullOrEmpty(directory))
        {
            Assert.NotEmpty(Document().Chunks);
            Assert.NotEmpty(Document(onTheSeam: true).Chunks);
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, Octahedron + ".vmf"), Document().ToBytes());
        File.WriteAllBytes(
            Path.Combine(directory, OctahedronOnTheSeam + ".vmf"),
            Document(onTheSeam: true).ToBytes());

        Assert.True(File.Exists(Path.Combine(directory, Octahedron + ".vmf")));
        Assert.True(File.Exists(Path.Combine(directory, OctahedronOnTheSeam + ".vmf")));
    }

    /// <summary>The VMF.</summary>
    /// <param name="onTheSeam">
    /// True for <see cref="OctahedronOnTheSeam"/>, centred on the origin;
    /// false for <see cref="Octahedron"/>, centred at <see cref="Centre"/>.
    /// </param>
    /// <returns>The document.</returns>
    public static VmfDocument Document(bool onTheSeam = false)
    {
        (int cx, int cy, int cz) = onTheSeam ? (0, 0, 0) : Centre;
        VmfDocument document = new();

        VmfChunk version = new("versioninfo");
        version.AddKey("editorversion", "400");
        version.AddKey("mapversion", "1");
        version.AddKey("formatversion", "100");
        version.AddKey("prefab", "0");
        document.Chunks.Add(version);

        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("mapversion", "1");
        world.AddKey("classname", "worldspawn");
        world.AddKey("skyname", "sky_day01_01");

        VmfChunk solid = world.AddChunk(MapFileLoader.SolidChunk);
        solid.AddKey("id", "2");

        int id = 1;
        foreach ((int SX, int SY, int SZ) octant in Octants)
        {
            (int X, int Y, int Z) vx = (cx + (octant.SX * Radius), cy, cz);
            (int X, int Y, int Z) vy = (cx, cy + (octant.SY * Radius), cz);
            (int X, int Y, int Z) vz = (cx, cy, cz + (octant.SZ * Radius));

            // PlaneFromPoints is (p0-p1) x (p2-p1) (map.cpp:384), which for
            // (vx, vz, vy) gives a normal proportional to
            // sx*sy*sz * (sx, sy, sz). So the order flips with the octant's
            // parity, or half the faces point inwards and the brush is
            // inside out.
            (int X, int Y, int Z)[] points = octant.SX * octant.SY * octant.SZ > 0
                ? [vx, vz, vy]
                : [vx, vy, vz];

            VmfChunk side = solid.AddChunk(MapFileLoader.SideChunk);
            side.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
            side.AddKey(
                "plane", $"({P(points[0])}) ({P(points[1])}) ({P(points[2])})");
            side.AddKey("material", "DEV/DEV_MEASUREGENERIC01B");
            side.AddKey("uaxis", "[1 0 0 0] 0.25");
            side.AddKey("vaxis", "[0 -1 0 0] 0.25");
            side.AddKey("rotation", "0");
            side.AddKey("lightmapscale", "16");
            side.AddKey("smoothing_groups", "0");
        }

        document.Chunks.Add(world);
        return document;
    }

    /// <summary>The eight sign triples, in a fixed order.</summary>
    public static IReadOnlyList<(int SX, int SY, int SZ)> Octants { get; } =
    [
        (1, 1, 1), (-1, 1, 1), (1, -1, 1), (-1, -1, 1),
        (1, 1, -1), (-1, 1, -1), (1, -1, -1), (-1, -1, -1),
    ];

    private static string P((int X, int Y, int Z) p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z}");
}
