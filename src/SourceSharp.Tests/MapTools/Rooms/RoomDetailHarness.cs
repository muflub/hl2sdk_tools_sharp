//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Rooms with detail props: a grass material whose <c>%detailtype</c> names
/// a harness detail type, the <c>detail.vbsp</c> that defines it (sprites, a
/// cross-shaped sprite and a model, in two alpha groups so a displacement's
/// alpha picks between them), grass slabs on the harness rooms' floors, a
/// grass displacement, and the room compile, link and flattened compile the
/// detail prop facts run.
/// </summary>
/// <remarks>
/// The rooms are <see cref="RoomPropHarness.Hub"/> and
/// <see cref="RoomPropHarness.Other"/>, on the walkable kit, whose floor's
/// top is at z = 16. Every file the detail props need is in the compile's
/// content; the links get a context without them, so every fact that links
/// also shows the link needs no game files (decision D1).
/// </remarks>
internal static class RoomDetailHarness
{
    /// <summary>The grass material: a lightmapped surface with a detail type.</summary>
    public const string Grass = "unit/grass";

    /// <summary>The detail type the grass names.</summary>
    public const string DetailType = "roomgrass";

    /// <summary>The detail model the dictionary places, and the one <c>prop_detail</c> entities name.</summary>
    public const string TuftModel = "models/detail_test/tuft.mdl";

    /// <summary>A grass slab's brush id: its sides are this times 8.</summary>
    public const int SlabBrush = 7000;

    /// <summary>
    /// The harness's detail types. Group <c>low</c> (alpha 0) holds a sprite
    /// and the model, group <c>high</c> (alpha 1) a random-scaled sprite and
    /// a cross; the model conforms to its surface, the sprites stand
    /// upright; and a surface steeper than 20 degrees takes fewer props, one
    /// steeper than 40 none, so every placement path of the emitter runs.
    /// </summary>
    public const string DetailFile =
        "detail.vbsp\n{\n"
        + "\t\"" + DetailType + "\"\n\t{\n"
        + "\t\t\"density\" \"12000.0\"\n"
        + "\t\t\"low\"\n\t\t{\n"
        + "\t\t\t\"alpha\" \"0\"\n"
        + "\t\t\t\"tuft\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"0 0 64 64 512\"\n\t\t\t\t\"spritesize\" \"0.5 0 24 24\"\n\t\t\t\t\"amount\" \"0.6\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t\t\"minAngle\" \"20\"\n\t\t\t\t\"maxAngle\" \"40\"\n\t\t\t\t\"detailOrientation\" \"2\"\n\t\t\t}\n"
        + "\t\t\t\"clump\"\n\t\t\t{\n\t\t\t\t\"model\" \"" + TuftModel + "\"\n\t\t\t\t\"amount\" \"0.4\"\n\t\t\t\t\"minAngle\" \"20\"\n\t\t\t\t\"maxAngle\" \"40\"\n\t\t\t}\n"
        + "\t\t}\n"
        + "\t\t\"high\"\n\t\t{\n"
        + "\t\t\t\"alpha\" \"1\"\n"
        + "\t\t\t\"blade\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"64 0 64 64 512\"\n\t\t\t\t\"spritesize\" \"0.5 0 16 32\"\n\t\t\t\t\"spriterandomscale\" \"0.2\"\n\t\t\t\t\"amount\" \"0.5\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t\t\"detailOrientation\" \"1\"\n\t\t\t}\n"
        + "\t\t\t\"cross\"\n\t\t\t{\n\t\t\t\t\"sprite\" \"128 0 64 64 512\"\n\t\t\t\t\"sprite_shape\" \"cross\"\n\t\t\t\t\"spritesize\" \"0.5 0 16 32\"\n\t\t\t\t\"amount\" \"0.5\"\n\t\t\t\t\"upright\" \"1\"\n\t\t\t}\n"
        + "\t\t}\n"
        + "\t}\n}\n";

    /// <summary>The detail props' files: the grass material, the dictionary and the model, over the prop harness's models.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files()
    {
        Dictionary<string, byte[]> files = new(RoomBrushHarness.Files(), StringComparer.Ordinal)
        {
            [$"materials/{Grass}.vmt"] = Encoding.ASCII.GetBytes(
                "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%detailtype\" \"" + DetailType + "\"\n}\n"),
            ["detail.vbsp"] = Encoding.ASCII.GetBytes(DetailFile),
        };

        StudioModelSpec tuft = new()
        {
            Name = "detail_test/tuft.mdl",
            Materials = ["tuft"],
            MaterialSearchPaths = ["models/detail_test/"],
            Meshes = [Primitives.Box(0, new Vec3(-4, -4, 0), new Vec3(4, 4, 12))],
        };
        foreach ((string path, byte[] bytes) in StudioModelWriter.Write(tuft))
        {
            files[path] = bytes;
        }

        return files;
    }

    /// <summary>
    /// A brush standing on the floor whose top side is grass (the others
    /// the plain material, so only the top grows props).
    /// </summary>
    public static VmfChunk Slab(int id, Box box)
    {
        VmfChunk solid = RoomModel.Slab(RoomHarness.Plain, box.Mins, box.Maxs, id);
        SetMaterial(solid.Chunks.First(), Grass);
        return solid;
    }

    /// <summary>A displacement patch (<see cref="RoomDisplacementHarness.Patch"/>) whose displaced top is grass.</summary>
    public static VmfChunk GrassPatch(int id, Box box, int power = 2, int seed = 0)
    {
        VmfChunk solid = RoomDisplacementHarness.Patch(id, box, power, seed, offsets: false);
        SetMaterial(solid.Chunks.First(), Grass);
        return solid;
    }

    /// <summary>The hub's grass slab, off the cell's centre and clear of every doorway.</summary>
    public static Box HubSlab { get; } = new(new Vec3(40, 48, 16), new Vec3(168, 160, 20));

    /// <summary>The other room's grass slab, a different shape, so the two rooms' props differ.</summary>
    public static Box OtherSlab { get; } = new(new Vec3(96, 32, 16), new Vec3(208, 200, 18));

    /// <summary>The hub's grass displacement, beside its slab.</summary>
    public static Box HubPatch { get; } = new(new Vec3(176, 40, 16), new Vec3(224, 136, 22));

    /// <summary>The hub's grass (a slab and a displacement) and the other room's (a slab), as library brushes of rooms 0 and 1.</summary>
    public static (int Room, VmfChunk Solid)[] Grounds =>
    [
        (0, Slab(SlabBrush, HubSlab)),
        (0, GrassPatch(SlabBrush + 1, HubPatch, seed: 3)),
        (1, Slab(SlabBrush + 2, OtherSlab)),
    ];

    /// <summary>A <c>prop_detail</c> of the harness model at a room-local origin.</summary>
    public static VmfChunk DetailProp(int id, Vec3 origin, string angles = "0 30 0") =>
        RoomPropHarness.Entity("prop_detail", id, origin, ("model", TuftModel), ("angles", angles), ("detailOrientation", "0"));

    /// <summary>A <c>prop_detail_sprite</c> at a room-local origin.</summary>
    public static VmfChunk DetailSprite(int id, Vec3 origin) =>
        RoomPropHarness.Entity(
            "prop_detail_sprite", id, origin,
            ("angles", "0 75 0"),
            ("detailOrientation", "1"),
            ("position_ul", "-12 24 0"),
            ("position_lr", "12 0 0"),
            ("tex_ul", "0 64 0"),
            ("tex_size", "64 64 0"),
            ("tex_total_size", "512"));

    /// <summary>The harness's detail entities: a model and a sprite in the hub, a model in the other room.</summary>
    public static (int Room, VmfChunk Entity)[] Entities =>
    [
        (0, DetailProp(9100, new Vec3(200, 200, 16))),
        (0, DetailSprite(9101, new Vec3(60, 200, 16))),
        (1, DetailProp(9102, new Vec3(60, 60, 16), "0 250 0")),
    ];

    /// <summary>The harness library with the grounds and entities (or the ones given).</summary>
    public static VmfDocument Library((int Room, VmfChunk Solid)[]? solids = null, (int Room, VmfChunk Entity)[]? entities = null) =>
        RoomDisplacementHarness.Library(solids ?? Grounds, entities ?? Entities);

    /// <summary>A compile context whose content holds the detail files, with the cooker given.</summary>
    public static async Task<VbspContext> ContextAsync(string mapBase = "roomtest", int degree = 1, ManagedCollisionCooker? cooker = null)
    {
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: Files());
        context.MapBase = mapBase;
        context.Parallelism = new CompileParallelism { MaxDegree = degree };
        context.CollisionCooker = cooker;
        return context;
    }

    /// <summary>The library's rooms compiled as <c>ssmap room</c> compiles them (unlit), cooked.</summary>
    public static async Task<RoomLibrary> CompileAsync(VmfDocument library, int degree = 1)
    {
        RoomLibrarySplit split = RoomLibraryVmf.SplitLibrary(library);
        RoomLibrary compiled = new(split.Rooms[0].Definition.Kit, split.Rooms[0].Definition.CellSize)
        {
            LibraryEntities = split.LibraryEntities,
            Options = split.Options,
        };
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        foreach (LibraryRoom room in split.Rooms)
        {
            compiled.Add(await RoomCompiler.CompileAsync(room.Document, room.Definition, await ContextAsync(room.Definition.Name, degree, cooker)));
        }

        return compiled;
    }

    /// <summary>The level flattened and compiled whole, against the detail files.</summary>
    public static async Task<BspData> CompileFlatAsync(VmfDocument library, LevelGrid level)
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        VbspResult whole = await RoomHarness.CompileAsync(LevelFlattener.Flatten(level, library), await ContextAsync("flat", 1, cooker));
        Assert.NotNull(whole.Bsp);
        return whole.Bsp!;
    }

    /// <summary>A map's detail prop lump, or an empty one when it has none.</summary>
    public static DetailPropLump Lump(BspData bsp)
    {
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            if (entry.Id == GameLumpId.MakeId(GameLumpId.DetailProps))
            {
                return DetailPropLump.Read(entry);
            }
        }

        return new DetailPropLump();
    }

    /// <summary>A map's game lump ids, in directory order.</summary>
    public static string[] GameLumpIds(BspData bsp) => [.. bsp.GameLumps.Select(g => g.IdString())];

    /// <summary>
    /// One prop as a line of what the engine reads from it, bit for bit:
    /// its dictionary entry by content (a model's name, a sprite's corners),
    /// origin, angles, leaf, lighting, style run, sway, shape, orientation,
    /// type and scale.
    /// </summary>
    public static string Line(DetailPropLump lump, DetailObjectLump p, bool leaf = true, bool lighting = true) => string.Join(
        " | ",
        p.Type == 0 ? lump.ModelNames[p.DetailModel] : Sprite(lump.Sprites[p.DetailModel]),
        V(p.Origin),
        V(p.Angles),
        leaf ? p.Leaf.ToString(CultureInfo.InvariantCulture) : "-",
        lighting ? $"{p.Lighting.R} {p.Lighting.G} {p.Lighting.B} {p.Lighting.Exponent} {p.LightStyles} {p.LightStyleCount}" : "-",
        $"{p.SwayAmount} {p.ShapeAngle} {p.ShapeSize} {p.Orientation} {p.Type} {F(p.Scale)}");

    /// <summary>A sprite's dictionary entry by its bits.</summary>
    public static string Sprite(DetailSpriteDictLump s) =>
        $"sprite {F(s.UpperLeft[0])} {F(s.UpperLeft[1])} {F(s.LowerRight[0])} {F(s.LowerRight[1])} {F(s.TexUpperLeft[0])} {F(s.TexUpperLeft[1])} {F(s.TexLowerRight[0])} {F(s.TexLowerRight[1])}";

    /// <summary>A vector by its bits.</summary>
    public static string V(Vec3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

    /// <summary>A float by its bits.</summary>
    public static string F(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);

    private static void SetMaterial(VmfChunk side, string material)
    {
        foreach (VmfKey key in side.Keys)
        {
            if (string.Equals(key.Name, "material", StringComparison.Ordinal))
            {
                key.Value = material;
            }
        }
    }
}
