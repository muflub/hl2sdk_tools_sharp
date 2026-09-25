using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// Two brushes the catalogue does not contain: shapes that make
/// <c>AddBrushBevels</c> do work.
/// </summary>
/// <remarks>
/// <para>
/// Measuring the catalogue's stock compiles showed that exactly ONE of the 24
/// maps produces any bevel at all — <c>l0_nonaxial_wedge</c>, with two box
/// bevels — and the 46,794-line corpus map produces none. A stage that adds
/// planes to every non-box brush in the game, gated on one map's two planes,
/// is not gated. These two shapes raise that.
/// </para>
/// <para>
/// <b>Edge bevels are still not covered, and saying so is the point.</b>
/// Neither of these produces one, and nor does anything else measured here:
/// the second half of <c>AddBrushBevels</c> wants a
/// brush with an edge that is non-axial AND whose slanted-axial planes are
/// outside the hull, which a wedge, a pyramid and a corner-cut box all fail to
/// be. That half of the function is unexercised by any reference this lane
/// produced. It matters to CSG, so it is written down here rather than left to
/// be discovered by whoever ports it.
/// </para>
/// </remarks>
public class BevelShapes
{
    /// <summary>The environment variable naming where to write them.</summary>
    public const string EmitDirectoryVariable = "CATALOGUE_EMIT_DIR";

    /// <summary>
    /// A square pyramid: base 64x64 at z=0, apex at (32,32,64).
    /// </summary>
    /// <remarks>
    /// Four slanted faces, so five of the six axial planes are missing. Stock
    /// measures 1 brush, 10 total sides, 5 boxbevels, 0 edgebevels, 20 planes.
    /// </remarks>
    public const string Pyramid = "x0_pyramid";

    /// <summary>
    /// A 64-unit box with the far corner sliced off at <c>x + y + z = 128</c>.
    /// </summary>
    /// <remarks>
    /// All six axial planes are still present, so stock measures 1 brush,
    /// 7 total sides, 0 boxbevels, 0 edgebevels, 14 planes — the case that
    /// shows a non-axial FACE is not enough to make a bevel.
    /// </remarks>
    public const string CornerCut = "x0_corner_cut";

    /// <summary>
    /// Writes both VMFs next to the catalogue's, when
    /// <c>CATALOGUE_EMIT_DIR</c> says where.
    /// </summary>
    /// <remarks>
    /// The same shape as the catalogue's own emit fact, and for the same
    /// reason: the stock reference is produced by a human running Wine, and
    /// this is the half of that recipe a machine can do.
    /// </remarks>
    [Fact]
    public void BothBevelShapesEmit()
    {
        string? directory = Environment.GetEnvironmentVariable(EmitDirectoryVariable);

        if (string.IsNullOrEmpty(directory))
        {
            // Not a skip: the fact still checks that both documents BUILD, which
            // is the part that can rot. Only the writing needs a directory.
            Assert.NotEmpty(Document(Pyramid).Chunks);
            Assert.NotEmpty(Document(CornerCut).Chunks);
            return;
        }

        Directory.CreateDirectory(directory);

        foreach (string name in new[] { Pyramid, CornerCut })
        {
            File.WriteAllBytes(Path.Combine(directory, name + ".vmf"), Document(name).ToBytes());
        }

        Assert.True(File.Exists(Path.Combine(directory, Pyramid + ".vmf")));
        Assert.True(File.Exists(Path.Combine(directory, CornerCut + ".vmf")));
    }

    /// <summary>The VMF for one shape.</summary>
    /// <param name="name">
    /// <see cref="Pyramid"/> or <see cref="CornerCut"/>.
    /// </param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The name is neither.</exception>
    public static VmfDocument Document(string name)
    {
        (int X, int Y, int Z)[][] sides = name switch
        {
            Pyramid =>
            [
                [(0, 0, 0), (64, 0, 0), (64, 64, 0)],
                [(64, 0, 0), (0, 0, 0), (32, 32, 64)],
                [(64, 64, 0), (64, 0, 0), (32, 32, 64)],
                [(0, 64, 0), (64, 64, 0), (32, 32, 64)],
                [(0, 0, 0), (0, 64, 0), (32, 32, 64)],
            ],
            CornerCut =>
            [
                [(0, 64, 64), (64, 64, 64), (64, 0, 64)],
                [(0, 0, 0), (64, 0, 0), (64, 64, 0)],
                [(64, 64, 64), (64, 64, 0), (64, 0, 0)],
                [(0, 64, 0), (0, 64, 64), (0, 0, 64)],
                [(64, 64, 64), (0, 64, 64), (0, 64, 0)],
                [(0, 0, 64), (64, 0, 64), (64, 0, 0)],
                [(64, 0, 64), (0, 64, 64), (64, 64, 0)],
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

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

        for (int i = 0; i < sides.Length; i++)
        {
            VmfChunk side = solid.AddChunk(MapFileLoader.SideChunk);
            side.AddKey("id", (i + 1).ToString(CultureInfo.InvariantCulture));
            side.AddKey("plane", $"({P(sides[i][0])}) ({P(sides[i][1])}) ({P(sides[i][2])})");
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

    private static string P((int X, int Y, int Z) p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z}");
}
