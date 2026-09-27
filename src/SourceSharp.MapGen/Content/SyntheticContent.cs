//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapGen.Content;

/// <summary>
/// Stand-ins for the game content <c>maps/ss_sandbox.vmf</c> mounts from
/// Steam, generated so a compile on a machine without that content runs the
/// same material, texture, texlight, surface-property, detail and static-prop
/// logic it would with the real files.
/// </summary>
/// <remarks>
/// <para>
/// Every material the map's brushes use is here with the compile keys that
/// decide its branch: the tool textures' <c>%compile*</c> flags, water with
/// its <c>$bottommaterial</c>, a translucent window, bump-mapped and
/// <c>$envmap</c> surfaces, an explicit <c>$reflectivity</c>, and a
/// <c>%detailtype</c> floor. Each has a VTF of a realistic size whose
/// reflectivity comes from its pixels. The six skybox faces match the
/// format and flags the default cubemap is built from.
/// </para>
/// <para>
/// The eight models the map names are here as <c>$staticprop</c> models
/// with real geometry, apart from two that deliberately take the refusal
/// branches: the explosive drum is not a static prop, and the door says
/// <c>allowstatic 0</c>. The map itself has no <c>prop_static</c>, so
/// <see cref="WithStaticPropsAsync"/> makes a variant that places one beside every
/// model entity; that is what gets vbsp's static-prop lump and vrad's
/// static-prop lighting and shadows to run.
/// </para>
/// <para>
/// The pixels are procedural and seeded by the file name, so the output is
/// the same bytes on every run and every machine.
/// </para>
/// </remarks>
public static class SyntheticContent
{
    /// <summary>The detail type the carpet floor names in <c>detail.vbsp</c>.</summary>
    public const string DetailType = "ss_carpet_fuzz";

    /// <summary>The classnames whose <c>model</c> gets a <c>prop_static</c> twin in <see cref="WithStaticPropsAsync"/>.</summary>
    public static IReadOnlySet<string> PropClasses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "prop_physics", "prop_physics_override", "prop_physics_multiplayer", "prop_physics_respawnable",
        "prop_dynamic", "prop_dynamic_override", "prop_dynamic_ornament", "dynamic_prop", "physics_prop",
        "simple_physics_prop", "prop_sphere", "prop_door_rotating", "physics_cannister", "phys_magnet",
    };

    /// <summary>Every file, as a path under the game directory and its bytes.</summary>
    /// <returns>The files, in a stable order.</returns>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (Material m in Materials())
        {
            files["materials/" + m.Name + ".vmt"] = Encoding.UTF8.GetBytes(m.Vmt);
            foreach ((string texture, Func<byte[]> make) in m.Textures)
            {
                files.TryAdd("materials/" + texture + ".vtf", make());
            }
        }

        foreach (StudioModelSpec model in Models())
        {
            foreach ((string path, byte[] bytes) in StudioModelWriter.Write(model))
            {
                files[path] = bytes;
            }
        }

        files["lights.rad"] = Encoding.UTF8.GetBytes(LightsRad);
        files["scripts/surfaceproperties_manifest.txt"] = Encoding.UTF8.GetBytes(
            "surfaceproperties_manifest\n{\n\t\"file\"\t\"scripts/surfaceproperties.txt\"\n}\n");
        files["scripts/surfaceproperties.txt"] = Encoding.UTF8.GetBytes(SurfaceProperties);
        files["detail.vbsp"] = Encoding.UTF8.GetBytes(DetailVbsp);
        return files;
    }

    // ------------------------------------------------------------ materials

    private sealed record Material(string Name, string Vmt, IReadOnlyList<(string Texture, Func<byte[]> Make)> Textures);

    private static Material Lit(
        string name, string surfaceprop, (int W, int H) size, Pattern pattern, string extra = "", bool bump = false)
    {
        List<(string, Func<byte[]>)> textures = [(name, () => Texture(name, size, pattern))];
        StringBuilder vmt = new();
        vmt.Append("\"LightmappedGeneric\"\n{\n")
            .Append(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{name}\"\n")
            .Append(CultureInfo.InvariantCulture, $"\t\"$surfaceprop\" \"{surfaceprop}\"\n");
        if (bump)
        {
            string normal = name + "_normal";
            vmt.Append(CultureInfo.InvariantCulture, $"\t\"$bumpmap\" \"{normal}\"\n");
            textures.Add((normal, () => Texture(normal, size, Pattern.NormalMap)));
        }

        vmt.Append(extra).Append("}\n");
        return new Material(name, vmt.ToString(), textures);
    }

    private static Material Tool(string name, string flags, Pattern pattern = Pattern.Tool) =>
        new(name,
            $"\"LightmappedGeneric\"\n{{\n\t\"$basetexture\" \"{name}\"\n{flags}}}\n",
            [(name, () => Texture(name, (64, 64), pattern))]);

    private static IEnumerable<Material> Materials()
    {
        // Tools. The map spells these in capitals; the material cache folds
        // case, so the files are lower case as the game's are.
        yield return Tool("tools/toolsnodraw", "\t\"%compilenodraw\" \"1\"\n\t\"%noportal\" \"1\"\n");
        yield return Tool("tools/toolstrigger", "\t\"%compiletrigger\" \"1\"\n\t\"%compilenonsolid\" \"1\"\n");
        yield return Tool("tools/toolsinvisible", "\t\"%compileinvisible\" \"1\"\n\t\"%playerclip\" \"0\"\n");
        yield return Tool("tools/toolsareaportal", "\t\"%compilenodraw\" \"1\"\n");
        yield return Tool("tools/toolsoccluder", "\t\"%compilenodraw\" \"1\"\n\t\"%compilenolight\" \"1\"\n");
        yield return Tool("tools/toolsskybox", "\t\"%compilesky\" \"1\"\n\t\"%compilenolight\" \"1\"\n", Pattern.Sky);

        // Lit world surfaces, each on a different branch.
        yield return Lit("wood/woodwall014a", "wood", (512, 512), Pattern.Planks, bump: true);
        yield return Lit("props/carpetfloor007a", "carpet", (256, 256), Pattern.Carpet,
            $"\t\"%detailtype\" \"{DetailType}\"\n");
        yield return Lit("tile/tilefloor001a", "tile", (256, 256), Pattern.Tiles,
            "\t\"$envmap\" \"env_cubemap\"\n\t\"$envmaptint\" \"[.3 .3 .3]\"\n");
        yield return Lit("plaster/plasterwall021a", "plaster", (256, 256), Pattern.Plaster);
        yield return Lit("plaster/plasterceiling003a", "plaster", (256, 256), Pattern.Ceiling);
        yield return Lit("metal/metalfloor007a", "metal", (256, 256), Pattern.Grating,
            "\t\"$envmap\" \"env_cubemap\"\n", bump: true);
        yield return Lit("concrete/concretefloor001a", "concrete", (512, 512), Pattern.Concrete,
            "\t\"$reflectivity\" \"[.25 .25 .25]\"\n");

        // The computer panel is a texlight in lights.rad.
        yield return Lit("halflife/lab1_cmpm5", "computer", (128, 128), Pattern.Panel);

        // A breakable window: translucent, so window contents and SURF_TRANS,
        // with the crack material cubemap fix-ups treat as a dependent.
        yield return new Material("glass/glasswindowbreak070a",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"glass/glasswindowbreak070a\"\n\t\"$translucent\" \"1\"\n"
            + "\t\"$envmap\" \"env_cubemap\"\n\t\"$surfaceprop\" \"glass\"\n"
            + "\t\"$crackmaterial\" \"glass/glasswindowbreak070b\"\n}\n",
            [("glass/glasswindowbreak070a", () => Texture("glass/glasswindowbreak070a", (128, 128), Pattern.Glass, alpha: true))]);
        yield return new Material("glass/glasswindowbreak070b",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"glass/glasswindowbreak070b\"\n\t\"$translucent\" \"1\"\n"
            + "\t\"$surfaceprop\" \"glass\"\n}\n",
            [("glass/glasswindowbreak070b", () => Texture("glass/glasswindowbreak070b", (128, 128), Pattern.Glass, alpha: true))]);

        // Water, above and beneath: water contents, the bottom texinfo, and
        // the volumes and fog vbsp builds from them.
        yield return new Material("nature/water_canals_cheap001",
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"1\"\n"
            + "\t\"$bottommaterial\" \"nature/water_canals_cheap001_beneath\"\n"
            + "\t\"$normalmap\" \"nature/water_canals_cheap001_normal\"\n\t\"$envmap\" \"env_cubemap\"\n"
            + "\t\"$surfaceprop\" \"water\"\n\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n"
            + "\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n",
            [("nature/water_canals_cheap001_normal", () => Texture("nature/water_canals_cheap001_normal", (256, 256), Pattern.NormalMap))]);
        yield return new Material("nature/water_canals_cheap001_beneath",
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$abovewater\" \"0\"\n"
            + "\t\"$normalmap\" \"nature/water_canals_cheap001_normal\"\n\t\"$surfaceprop\" \"water\"\n"
            + "\t\"$fogenable\" \"1\"\n\t\"$fogcolor\" \"{40 50 40}\"\n\t\"$fogstart\" \"0\"\n\t\"$fogend\" \"400\"\n}\n",
            []);

        // The skybox faces the default cubemap is built from: the format and
        // flags the stock cubemap for this sky came from (BGR888, clamp S/T,
        // sRGB, no mip, no LOD), every face the same size, which the cubemap
        // builder requires.
        foreach (string face in new[] { "rt", "lf", "bk", "ft", "up", "dn" })
        {
            string name = "skybox/sky_day01_01" + face;
            (int w, int h) = (256, 256);
            yield return new Material(name,
                $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\" \"{name}\"\n\t\"$nofog\" \"1\"\n\t\"$ignorez\" \"1\"\n}}\n",
                [(name, () => VtfWriter.Write(w, h, ImageFormat.Bgr888, VtfWriter.SkyboxFlags, Pixels(name, Pattern.Sky, w, h)))]);
        }

        // The detail sprite sheet detail.vbsp's sprite coordinates cut up.
        yield return new Material("detail/detailsprites",
            "\"UnlitGeneric\"\n{\n\t\"$basetexture\" \"detail/detailsprites\"\n\t\"$alphatest\" \"1\"\n}\n",
            [("detail/detailsprites", () => Texture("detail/detailsprites", (512, 512), Pattern.Sprites, alpha: true))]);

        // The models' skins. VertexLitGeneric: unlit in the lightmap sense,
        // read by vrad for static-prop texture shadows.
        foreach ((string name, Pattern pattern, bool alpha) in ModelMaterials)
        {
            string vmt = $"\"VertexLitGeneric\"\n{{\n\t\"$basetexture\" \"{name}\"\n"
                + (alpha ? "\t\"$alphatest\" \"1\"\n\t\"$nocull\" \"1\"\n" : string.Empty) + "}\n";
            yield return new Material(name, vmt, [(name, () => Texture(name, (128, 128), pattern, alpha))]);
        }
    }

    private static readonly (string Name, Pattern Pattern, bool Alpha)[] ModelMaterials =
    [
        ("models/props_junk/wood_crate001a", Pattern.Planks, false),
        ("models/props_junk/popcan01a", Pattern.Paint, false),
        ("models/props_c17/oildrum001", Pattern.Paint, false),
        ("models/props_c17/oildrum001_explosive", Pattern.Paint, false),
        ("models/props_c17/furniturechair001a", Pattern.Planks, false),
        ("models/props_c17/furniturechair001a_weave", Pattern.Weave, true),
        ("models/props_junk/propanecanister001a", Pattern.Paint, false),
        ("models/props_junk/metal_paintcan001a", Pattern.Paint, false),
        ("models/props_doors/door01", Pattern.Planks, false),
    ];

    // ---------------------------------------------------------------- models

    private static StudioModelSpec Model(
        string name, string folder, string[] materials, MeshSpec[] meshes, string surfaceprop, float mass) => new()
        {
            Name = name,
            Materials = materials,
            MaterialSearchPaths = [folder],
            Meshes = meshes,
            SurfaceProp = surfaceprop,
            Mass = mass,
        };

    private static IEnumerable<StudioModelSpec> Models()
    {
        // Two LODs, so every LOD walk in vrad has more than one to walk.
        yield return Model("props_junk/wood_crate001a.mdl", "models/props_junk/", ["wood_crate001a"],
            [Primitives.Box(0, new Vec3(-20, -20, 0), new Vec3(20, 20, 40))], "wood_crate", 40) with { Lods = 2 };

        yield return Model("props_junk/popcan01a.mdl", "models/props_junk/", ["popcan01a"],
            [Primitives.Cylinder(0, 2.5f, 0, 9, 12)], "popcan", 0.5f);

        yield return Model("props_c17/oildrum001.mdl", "models/props_c17/", ["oildrum001"],
            [Primitives.Cylinder(0, 14, 0, 46, 16)], "metal_barrel", 60);

        // Not $staticprop, as the real one is not: a prop_static of it takes
        // vbsp's "must be compiled with $staticprop" branch.
        yield return Model("props_c17/oildrum001_explosive.mdl", "models/props_c17/", ["oildrum001_explosive"],
            [Primitives.Cylinder(0, 14, 0, 46, 16)], "metal_barrel", 60) with { Flags = 0 };

        // Six parts, six hulls, two materials; the woven seat is alpha-tested
        // and the model asks vrad for texture shadows.
        yield return Model("props_c17/furniturechair001a.mdl", "models/props_c17/",
            ["furniturechair001a", "furniturechair001a_weave"],
            [
                Primitives.Box(1, new Vec3(-12, -12, 18), new Vec3(12, 12, 20)),
                Primitives.Box(0, new Vec3(-12, 10, 20), new Vec3(12, 12, 44)),
                Primitives.Box(0, new Vec3(-12, -12, 0), new Vec3(-10, -10, 18)),
                Primitives.Box(0, new Vec3(10, -12, 0), new Vec3(12, -10, 18)),
                Primitives.Box(0, new Vec3(-12, 10, 0), new Vec3(-10, 12, 18)),
                Primitives.Box(0, new Vec3(10, 10, 0), new Vec3(12, 12, 18)),
            ], "wood_furniture", 15) with { Flags = StudioModelSpec.StaticPropFlag | StudioModelSpec.CastTextureShadowsFlag };

        yield return Model("props_junk/propanecanister001a.mdl", "models/props_junk/", ["propanecanister001a"],
            [Primitives.Cylinder(0, 5, 0, 20, 12), Primitives.Cylinder(0, 1.5f, 20, 24, 8)], "metal", 8);

        yield return Model("props_junk/metal_paintcan001a.mdl", "models/props_junk/", ["metal_paintcan001a"],
            [Primitives.Cylinder(0, 5, 0, 12, 12)], "metal", 3);

        // A door refuses to be static through its own keyvalues.
        yield return Model("props_doors/door01_dynamic.mdl", "models/props_doors/", ["door01"],
            [Primitives.Box(0, new Vec3(0, -1, 0), new Vec3(54, 1, 108))], "wood", 50) with
        {
            KeyValues = "mdlkeyvalue\n{\n\tprop_data\n\t{\n\t\t\"base\"\t\"Door.Standard\"\n\t\t\"allowstatic\"\t\"0\"\n\t}\n}\n",
        };
    }

    // ------------------------------------------------------ text resources

    // The panel glows; the crate is forced to cast texture shadows although
    // its model does not ask for them.
    private const string LightsRad =
        "// Generated stand-ins for the texlights the game's lights.rad declares.\n"
        + "halflife/lab1_cmpm5\t120 200 255 800\n"
        + "forcetextureshadow models/props_junk/wood_crate001a.mdl\n";

    private const string SurfaceProperties =
        "\"default\"\n{\n\t\"density\"\t\"2000\"\n\t\"elasticity\"\t\"0.25\"\n\t\"friction\"\t\"0.8\"\n\t\"thickness\"\t\"0\"\n}\n"
        + "\"wood\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"700\"\n\t\"elasticity\"\t\"0.1\"\n\t\"friction\"\t\"0.8\"\n}\n"
        + "\"wood_crate\"\n{\n\t\"base\"\t\"wood\"\n\t\"thickness\"\t\"0.5\"\n}\n"
        + "\"wood_furniture\"\n{\n\t\"base\"\t\"wood\"\n\t\"thickness\"\t\"0.5\"\n}\n"
        + "\"carpet\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"500\"\n\t\"friction\"\t\"0.9\"\n}\n"
        + "\"tile\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"2700\"\n\t\"friction\"\t\"0.6\"\n}\n"
        + "\"plaster\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"1800\"\n}\n"
        + "\"concrete\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"2400\"\n}\n"
        + "\"metal\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"2700\"\n\t\"elasticity\"\t\"0.1\"\n}\n"
        + "\"metal_barrel\"\n{\n\t\"base\"\t\"metal\"\n\t\"thickness\"\t\"0.1\"\n}\n"
        + "\"popcan\"\n{\n\t\"base\"\t\"metal\"\n\t\"thickness\"\t\"0.05\"\n}\n"
        + "\"computer\"\n{\n\t\"base\"\t\"metal\"\n\t\"density\"\t\"1000\"\n}\n"
        + "\"glass\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"2500\"\n\t\"thickness\"\t\"0.5\"\n}\n"
        + "\"water\"\n{\n\t\"base\"\t\"default\"\n\t\"density\"\t\"1000\"\n\t\"friction\"\t\"0.8\"\n}\n";

    // Sprites cut from detail/detailsprites and a pop can as a detail model.
    private static readonly string DetailVbsp =
        "detail.vbsp\n{\n"
        + $"\t\"{DetailType}\"\n\t{{\n\t\t\"density\"\t\"800.0\"\n"
        + "\t\t\"Group1\"\n\t\t{\n\t\t\t\"alpha\"\t\"1.0\"\n"
        + "\t\t\t\"tuft\"\n\t\t\t{\n\t\t\t\t\"sprite\"\t\"0 0 64 64 512\"\n\t\t\t\t\"spritesize\"\t\"0.5 0 12 12\"\n"
        + "\t\t\t\t\"spriterandomscale\"\t\"0.2\"\n\t\t\t\t\"amount\"\t\"0.6\"\n\t\t\t\t\"detailOrientation\"\t\"2\"\n\t\t\t}\n"
        + "\t\t\t\"cross\"\n\t\t\t{\n\t\t\t\t\"sprite\"\t\"64 0 64 64 512\"\n\t\t\t\t\"sprite_shape\"\t\"cross\"\n"
        + "\t\t\t\t\"spritesize\"\t\"0.5 0 8 16\"\n\t\t\t\t\"amount\"\t\"0.3\"\n\t\t\t}\n"
        + "\t\t\t\"can\"\n\t\t\t{\n\t\t\t\t\"model\"\t\"models/props_junk/popcan01a.mdl\"\n\t\t\t\t\"amount\"\t\"0.1\"\n"
        + "\t\t\t\t\"upright\"\t\"1\"\n\t\t\t}\n"
        + "\t\t}\n\t}\n}\n";

    // ------------------------------------------------------------ the map

    /// <summary>
    /// A copy of a map with a <c>prop_static</c> beside every model entity of
    /// <see cref="PropClasses"/>, at its origin and angles.
    /// </summary>
    /// <param name="map">The map; not changed.</param>
    /// <returns>The variant and how many props it adds.</returns>
    /// <remarks>
    /// The originals stay: the compile ignores them, and keeping them leaves
    /// every other entity's logic as it was.
    /// </remarks>
    public static async Task<(VmfDocument Map, int Added)> WithStaticPropsAsync(
        VmfDocument map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        VmfDocument copy = await VmfDocument.ParseAsync(map.ToBytes(), cancellationToken).ConfigureAwait(false);
        int nextId = 1 + copy.GetChunks("entity")
            .Select(e => int.TryParse(e.GetValue("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0)
            .DefaultIfEmpty(0).Max();

        List<VmfChunk> added = [];
        foreach (VmfChunk entity in copy.GetChunks("entity"))
        {
            string? model = entity.GetValue("model");
            if (!PropClasses.Contains(entity.GetValue("classname") ?? string.Empty)
                || model is null || !model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VmfChunk prop = new("entity");
            prop.AddKey("id", (nextId++).ToString(CultureInfo.InvariantCulture));
            prop.AddKey("classname", "prop_static");
            prop.AddKey("angles", entity.GetValue("angles") ?? "0 0 0");
            prop.AddKey("fademindist", "-1");
            prop.AddKey("fadescale", "1");
            prop.AddKey("model", model);
            prop.AddKey("skin", "0");
            prop.AddKey("solid", "6");
            prop.AddKey("origin", entity.GetValue("origin") ?? "0 0 0");
            added.Add(prop);
        }

        foreach (VmfChunk prop in added)
        {
            copy.Chunks.Add(prop);
        }

        return (copy, added.Count);
    }

    // ------------------------------------------------------------- pixels

    private enum Pattern
    {
        Tool,
        Sky,
        Planks,
        Carpet,
        Tiles,
        Plaster,
        Ceiling,
        Grating,
        Concrete,
        Panel,
        Glass,
        NormalMap,
        Sprites,
        Paint,
        Weave,
    }

    private static byte[] Texture(string name, (int W, int H) size, Pattern pattern, bool alpha = false) =>
        VtfWriter.Write(size.W, size.H, alpha ? ImageFormat.Bgra8888 : ImageFormat.Bgr888, 0, Pixels(name, pattern, size.W, size.H));

    // A stable per-name seed, so every run writes the same bytes.
    private static uint Seed(string name)
    {
        uint h = 2166136261;
        foreach (char c in name)
        {
            h = (h ^ c) * 16777619;
        }

        return h;
    }

    private static float Noise(uint seed, int x, int y)
    {
        uint h = seed ^ (uint)(x * 374761393) ^ (uint)(y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    private static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    private static TexelSource Pixels(string name, Pattern pattern, int w, int h)
    {
        uint seed = Seed(name);
        // A base tint per name, so two materials with one pattern still differ.
        float tr = 0.8f + (0.4f * Noise(seed, 1, 0)), tg = 0.8f + (0.4f * Noise(seed, 2, 0)), tb = 0.8f + (0.4f * Noise(seed, 3, 0));
        (byte, byte, byte, byte) C(float r, float g, float b, float a = 1) => (B(r * tr), B(g * tg), B(b * tb), B(a));

        return pattern switch
        {
            Pattern.Tool => (x, y) => ((x / 8) + (y / 8)) % 2 == 0 ? C(0.9f, 0.5f, 0.1f) : C(0.2f, 0.2f, 0.2f),
            Pattern.Sky => (x, y) => C(0.35f + (0.3f * y / h), 0.55f + (0.25f * y / h), 0.9f),
            Pattern.Planks => (x, y) =>
            {
                float grain = 0.08f * MathF.Sin((x * 0.15f) + (6 * Noise(seed, y / 32, 7)));
                float seam = y % 64 < 2 ? 0.5f : 1f;
                float v = (0.45f + grain + (0.08f * Noise(seed, x / 4, y))) * seam;
                return C(v * 1.1f, v * 0.75f, v * 0.45f);
            },
            Pattern.Carpet => (x, y) => { float v = 0.3f + (0.15f * Noise(seed, x, y)); return C(v * 0.7f, v * 0.3f, v * 0.3f); },
            Pattern.Tiles => (x, y) => x % 64 < 3 || y % 64 < 3 ? C(0.35f, 0.35f, 0.33f)
                : C(0.8f + (0.05f * Noise(seed, x / 64, y / 64)), 0.8f, 0.78f),
            Pattern.Plaster => (x, y) => { float v = 0.7f + (0.08f * Noise(seed, x / 2, y / 2)); return C(v, v * 0.97f, v * 0.9f); },
            Pattern.Ceiling => (x, y) => { float v = 0.85f + (0.05f * Noise(seed, x, y)); return C(v, v, v); },
            Pattern.Grating => (x, y) => (x % 16 < 3) || (y % 16 < 3) ? C(0.5f, 0.52f, 0.55f) : C(0.2f, 0.21f, 0.22f),
            Pattern.Concrete => (x, y) => { float v = 0.45f + (0.12f * Noise(seed, x / 3, y / 3)); return C(v, v, v * 0.97f); },
            Pattern.Panel => (x, y) => (y / 8) % 3 == 0 ? C(0.2f, 0.9f, 1f) : C(0.1f, 0.15f, 0.2f),
            Pattern.Glass => (x, y) => C(0.6f, 0.7f, 0.75f, (x + y) % 37 == 0 ? 0.9f : 0.35f),
            Pattern.NormalMap => (x, y) =>
            {
                float nx = 0.15f * MathF.Sin(x * 0.2f), ny = 0.15f * MathF.Cos(y * 0.2f);
                return (B(0.5f + (nx / 2)), B(0.5f + (ny / 2)), B(0.5f + (MathF.Sqrt(1 - (nx * nx) - (ny * ny)) / 2)), 255);
            },
            Pattern.Sprites => (x, y) =>
            {
                float dx = (x % 64) - 32, dy = (y % 64) - 32;
                bool blade = MathF.Abs(dx) < 3 + (dy / 8) && dy > -28;
                return C(0.3f, 0.5f, 0.2f, blade ? 1 : 0);
            },
            Pattern.Paint => (x, y) => { float v = 0.5f + (0.1f * Noise(seed, x / 4, y / 4)); return C(v * 0.9f, v * 0.4f, v * 0.2f); },
            Pattern.Weave => (x, y) => C(0.7f, 0.6f, 0.4f, (x % 8 < 5) ^ (y % 8 < 5) ? 1 : 0),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };
    }
}
