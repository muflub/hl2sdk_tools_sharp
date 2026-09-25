using System.Globalization;

using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// The map that makes <c>FixupAreaportalWaterBrushes</c> do work — the second
/// gap this lane found, and the same shape of gap as the edge bevels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> The CSG stage's ONLY path into the BRUSHES and
/// BRUSHSIDES lumps is <c>FixupAreaportalWaterBrushes</c>
/// (<c>csg.cpp:345</c>), and
/// <see cref="StockBrushLumpTests.RunningTheWholeWorldPassChangesNeitherLumpOnAnyCorpusMap"/>
/// measures that it fires on none of the 29 reference maps —
/// <c>l2_areaportal_between_pools</c> has areaportals and pools, and the
/// areaportal is between them rather than in one. So the exact lump
/// comparisons are measuring Phase 3a and 3e, and this stage's contribution to
/// them is untested by the corpus.
/// </para>
/// <para>
/// <b>The shape.</b> A sealed box of world brushes with a water volume in the
/// floor, split into two pools by a wall, and a <c>func_areaportal</c> whose
/// brush fills the gap in that wall — so the areaportal is INSIDE the water
/// rather than beside it. That is the configuration stock's comment describes
/// ("This is a hack to allow areaportals to work in water") and the one that
/// otherwise floods the whole water volume.
/// </para>
/// </remarks>
public class AreaportalWaterShape
{
    /// <summary>The environment variable naming where to write it.</summary>
    public const string EmitDirectoryVariable = "CATALOGUE_EMIT_DIR";

    /// <summary>The map's name.</summary>
    public const string Name = "x0_areaportal_in_water";

    private const string Wall = "TOOLS/TOOLSNODRAW";
    private const string WaterMaterial = "nature/water_canals_cheap001";
    private const string AreaPortalMaterial = "TOOLS/TOOLSAREAPORTAL";

    /// <summary>Writes the VMF, when <c>CATALOGUE_EMIT_DIR</c> says where.</summary>
    [Fact]
    public void TheAreaportalInWaterMapEmits()
    {
        string? directory = Environment.GetEnvironmentVariable(EmitDirectoryVariable);

        if (string.IsNullOrEmpty(directory))
        {
            Assert.NotEmpty(Document().Chunks);
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, Name + ".vmf"), Document().ToBytes());

        Assert.True(File.Exists(Path.Combine(directory, Name + ".vmf")));
    }

    /// <summary>The VMF.</summary>
    /// <returns>The document.</returns>
    public static VmfDocument Document()
    {
        VmfDocument document = new();

        VmfChunk version = new("versioninfo");
        version.AddKey("editorversion", "400");
        version.AddKey("mapversion", "1");
        version.AddKey("formatversion", "100");
        version.AddKey("prefab", "0");
        document.Chunks.Add(version);

        int id = 1;

        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
        world.AddKey("mapversion", "1");
        world.AddKey("classname", "worldspawn");
        world.AddKey("skyname", "sky_day01_01");

        // A sealed shell: 256 x 512 x 256 inside, walls 16 thick.
        Add(world, ref id, Wall, (-272, -272, -16), (272, 272, 0));      // floor
        Add(world, ref id, Wall, (-272, -272, 256), (272, 272, 272));    // ceiling
        Add(world, ref id, Wall, (-272, -272, 0), (-256, 272, 256));     // -X
        Add(world, ref id, Wall, (256, -272, 0), (272, 272, 256));       // +X
        Add(world, ref id, Wall, (-256, -272, 0), (256, -256, 256));     // -Y
        Add(world, ref id, Wall, (-256, 256, 0), (256, 272, 256));       // +Y

        // The divider, with a 64-wide gap from z = 0 to z = 64.
        Add(world, ref id, Wall, (-8, -256, 64), (8, 256, 256));
        Add(world, ref id, Wall, (-8, -256, 0), (8, -32, 64));
        Add(world, ref id, Wall, (-8, 32, 0), (8, 256, 64));

        // One water volume spanning BOTH sides of the divider and the gap in
        // it, so the areaportal below sits inside the water.
        Add(world, ref id, WaterMaterial, (-256, -256, 0), (256, 256, 48));

        document.Chunks.Add(world);

        // The areaportal fills the gap exactly, and is therefore inside the
        // water volume above.
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", (id++).ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", "func_areaportal");
        entity.Children.Add(Solid(ref id, AreaPortalMaterial, (-8, -32, 0), (8, 32, 64)));
        document.Chunks.Add(entity);

        return document;
    }

    private static void Add(
        VmfChunk world,
        ref int id,
        string material,
        (float X, float Y, float Z) mins,
        (float X, float Y, float Z) maxs) =>
        world.Children.Add(Solid(ref id, material, mins, maxs));

    private static VmfChunk Solid(
        ref int id,
        string material,
        (float X, float Y, float Z) mins,
        (float X, float Y, float Z) maxs) =>
        UnitMap.Box(material, mins, maxs, id++);
}
