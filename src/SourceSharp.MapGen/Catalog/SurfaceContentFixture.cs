using System.Text;

namespace SourceSharp.MapGen.Catalog;

/// <summary>
/// The small original content set Phase 3g's catalogue entries compile
/// against as self-contained content: a detail.vbsp and
/// a handful of VMTs, all text, all written here rather than copied from a game.
/// </summary>
/// <remarks>
/// <para>
/// Mounted AHEAD of the game's own content — as the first <c>game</c> search path of a
/// derived gameinfo for the stock compile, and through an in-memory file system
/// for the unit tier. Nothing else in the catalogue needs it; an entry compiled
/// without it still compiles, but its detail materials are missing (a stock
/// "Material not found") and it places no detail props.
/// </para>
/// <para>
/// The textures the VMTs name are the game's own, so no VTF is added.
/// </para>
/// </remarks>
public static class SurfaceContentFixture
{
    /// <summary>The detail definition file the detail entries name through worldspawn's <c>detailvbsp</c>.</summary>
    public const string DetailVbspName = "p3g_detail.vbsp";

    /// <summary>A floor material whose <c>%detailtype</c> is the full sprite-and-model type.</summary>
    public const string DetailFloorMaterial = "p3g/detailfloor";

    /// <summary>A floor material whose <c>%detailtype</c> is a sparse, model-only type.</summary>
    public const string DetailSparseMaterial = "p3g/detailsparse";

    /// <summary>A material whose <c>%detailtype</c> names no type in the file.</summary>
    public const string DetailUnknownMaterial = "p3g/detailunknown";

    /// <summary>A <c>patch</c> VMT over a specular material, inserting a key.</summary>
    public const string PatchedSpecularMaterial = "p3g/patchedmetal";

    /// <summary>Every file, content-relative path first, in a fixed order.</summary>
    public static IReadOnlyList<(string Path, string Text)> Files { get; } =
    [
        (DetailVbspName, DetailVbsp),
        ($"materials/{DetailFloorMaterial}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"concrete/concretefloor001a\"\n\t\"%detailtype\" \"p3g_grass\"\n}\n"),
        ($"materials/{DetailSparseMaterial}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"concrete/concretefloor001a\"\n\t\"%detailtype\" \"p3g_sparse\"\n}\n"),
        ($"materials/{DetailUnknownMaterial}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"plaster/plasterwall021a\"\n\t\"%detailtype\" \"p3g_nosuchtype\"\n}\n"),
        ($"materials/{PatchedSpecularMaterial}.vmt",
            "\"patch\"\n{\n\t\"include\" \"materials/metal/metalwall058a.vmt\"\n\t\"insert\"\n\t{\n\t\t\"$envmaptint\" \"[.25 .25 .25]\"\n\t}\n}\n"),
    ];

    /// <summary>Writes every file under a directory, creating it.</summary>
    /// <param name="directory">The content root.</param>
    public static void EmitTo(string directory)
    {
        foreach ((string path, string text) in Files)
        {
            string full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.Latin1.GetBytes(text));
        }
    }

    // Two types. p3g_grass exercises every branch of stock's detail-group
    // parser: a card sprite with spritesize and a
    // random scale, the cross and tri shapes with their clamps, an upright model,
    // a conforming model, minAngle/maxAngle on the ramp, and amounts that sum to
    // less than one so a draw sometimes picks no detail. Two groups of EQUAL
    // alpha test the insertion order (stock inserts a new group BEFORE
    // equals), and an alpha-0 group gives the group pick two groups to choose from.
    // p3g_sparse sums to more than one, so the amounts are renormalised.
    private const string DetailVbsp =
        "\"p3g_detail\"\n{\n" +
        "\t\"p3g_grass\"\n\t{\n\t\t\"density\" \"4000\"\n" +
        "\t\t\"bright\"\n\t\t{\n\t\t\t\"alpha\" \"1\"\n" +
        "\t\t\t\"card\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"spritesize\" \"0.5 0 32 32\"\n\t\t\t\t\"spriterandomscale\" \"0.2\"\n\t\t\t\t\"sway\" \"0.5\"\n\t\t\t\t\"amount\" \"0.2\"\n\t\t\t\t\"minAngle\" \"30\"\n\t\t\t\t\"maxAngle\" \"60\"\n\t\t\t}\n" +
        "\t\t\t\"cross\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"64 0 64 64 512\"\n\t\t\t\t\"sprite_shape\" \"cross\"\n\t\t\t\t\"shape_size\" \"0.25\"\n\t\t\t\t\"shape_angle\" \"15\"\n\t\t\t\t\"amount\" \"0.2\"\n\t\t\t}\n" +
        "\t\t\t\"tri\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"128 0 64 64 512\"\n\t\t\t\t\"sprite_shape\" \"tri\"\n\t\t\t\t\"shape_size\" \"2\"\n\t\t\t\t\"shape_angle\" \"30\"\n\t\t\t\t\"detailOrientation\" \"1\"\n\t\t\t\t\"amount\" \"0.2\"\n\t\t\t}\n" +
        "\t\t\t\"shrub\"\n\t\t\t{\n\t\t\t\t\"model\" \"models/props_foliage/shrub_01a.mdl\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t\t\"amount\" \"0.15\"\n\t\t\t}\n" +
        "\t\t\t\"fence\"\n\t\t\t{\n\t\t\t\t\"model\" \"models/props_c17/fence01a.mdl\"\n\t\t\t\t\"amount\" \"0.1\"\n\t\t\t\t\"maxAngle\" \"50\"\n\t\t\t}\n" +
        "\t\t}\n" +
        "\t\t\"dark\"\n\t\t{\n\t\t\t\"alpha\" \"0\"\n\t\t\t\"speck\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 64 32 32 512\"\n\t\t\t\t\"amount\" \"0.5\"\n\t\t\t}\n\t\t}\n" +
        "\t\t\"bright2\"\n\t\t{\n\t\t\t\"alpha\" \"1\"\n\t\t\t\"never\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"32 64 32 32 512\"\n\t\t\t\t\"amount\" \"0.5\"\n\t\t\t}\n\t\t}\n" +
        "\t}\n" +
        "\t\"p3g_sparse\"\n\t{\n\t\t\"density\" \"600\"\n" +
        "\t\t\"only\"\n\t\t{\n\t\t\t\"alpha\" \"1\"\n" +
        "\t\t\t\"a\"\n\t\t\t{\n\t\t\t\t\"model\" \"models/props_foliage/shrub_01a.mdl\"\n\t\t\t\t\"amount\" \"1.5\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t}\n" +
        "\t\t\t\"b\"\n\t\t\t{\n\t\t\t\t\"model\" \"models/props_c17/concrete_barrier001a.mdl\"\n\t\t\t\t\"amount\" \"1\"\n\t\t\t}\n" +
        "\t\t}\n\t}\n" +
        "}\n";
}
