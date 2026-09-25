using System.Buffers.Binary;
using SourceSharp.MapGen;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// The unit tier's fixture for Phase 3g: a map built by MapGen, loaded by the
/// real 3a reader over an in-memory file system that also holds a handful of
/// materials and skybox textures. No game content, no wine.
/// </summary>
internal static class SurfaceUnit
{
    public const string Specular = "unit/specular";
    public const string Plain = "unit/plain";
    public const string Water = "unit/water";
    public const string Beneath = "unit/beneath";
    public const string SelfDependent = "unit/selfdep";
    public const string PatchOfSpecular = "unit/patchofspecular";
    public const string SkyName = "unitsky";

    /// <summary>The loaded map and everything a 3g emitter needs beside it.</summary>
    public sealed record Loaded(VbspContext Context, MapFile Map, MaterialPatcher Patcher, InMemoryFileSystem Files);

    public static async Task<Loaded> LoadAsync(VmfMap vmf, ComplianceOptions? compliance = null, Action<InMemoryFileSystem>? extra = null)
    {
        InMemoryFileSystem files = new();
        AddMaterials(files);
        extra?.Invoke(files);
        files.AddText("maps/unit.vmf", vmf.Write());

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        ContentFileSystem content = new([mount]);

        VbspContext context = new(VbspOptions.Default with { Compliance = compliance ?? ComplianceOptions.Correct }, content)
        {
            MapBase = "unit",
        };

        MapFile map = await new MapFileReader(context, files).LoadAsync(VPath.Create("maps/unit.vmf"));
        return new Loaded(context, map, new MaterialPatcher(content, new MapPakFile(), context.Options.Compliance), files);
    }

    /// <summary>A sealed room with every side <paramref name="material"/>, nothing else.</summary>
    public static VmfMap Room(string material)
    {
        VmfMap map = SourceSharp.MapGen.Catalog.TestMapCatalog.SealedRoom();
        foreach (VmfSolid solid in map.WorldSolids)
        {
            foreach (VmfSide side in solid.Sides)
                side.Material = material;
        }

        return map;
    }

    /// <summary>A minimal 7.4 VTF header: enough for LoadSrcVTFFiles' reads.</summary>
    public static byte[] Vtf(int width, int height, int format, uint flags, int frames = 1)
    {
        byte[] file = new byte[88];
        Span<byte> s = file;
        "VTF\0"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 7);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 88);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)frames);
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], format);
        s[56] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(s[57..], -1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[63..], 1);
        return file;
    }

    /// <summary>Six skybox faces of one shape, as VMT plus VTF.</summary>
    public static void AddSky(InMemoryFileSystem files, string sky, int format, uint flags, Func<string, uint>? flagsFor = null)
    {
        foreach (string face in new[] { "rt", "lf", "bk", "ft", "up", "dn" })
        {
            files.AddText($"materials/skybox/{sky}{face}.vmt",
                $"\"UnlitGeneric\"\n{{\n\t\"$basetexture\" \"skybox/{sky}{face}\"\n}}\n");
            files.AddFile($"materials/skybox/{sky}{face}.vtf", Vtf(512, 512, format, flagsFor?.Invoke(face) ?? flags));
        }
    }

    private static void AddMaterials(InMemoryFileSystem files)
    {
        files.AddText($"materials/{Specular}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"$envmap\" \"env_cubemap\"\n}\n");
        files.AddText($"materials/{Plain}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText($"materials/{Water}.vmt",
            "\"Water\"\n{\n\t\"%compilewater\" \"1\"\n\t\"$envmap\" \"env_cubemap\"\n" +
            $"\t\"$bottommaterial\" \"{Beneath}\"\n}}\n");
        files.AddText($"materials/{Beneath}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$envmap\" \"env_cubemap\"\n}\n");
        files.AddText($"materials/{SelfDependent}.vmt",
            $"\"LightmappedGeneric\"\n{{\n\t\"$bottommaterial\" \"{SelfDependent}\"\n}}\n");
        files.AddText($"materials/{PatchOfSpecular}.vmt",
            $"\"patch\"\n{{\n\t\"include\" \"materials/{Specular}.vmt\"\n\t\"insert\"\n\t{{\n\t\t\"$a\" \"1\"\n\t}}\n}}\n");
    }
}
