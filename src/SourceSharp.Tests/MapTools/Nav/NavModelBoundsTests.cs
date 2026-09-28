//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapGen.Content;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>A prop's movement hull, read from its model's header in the game's content.</summary>
public sealed class NavModelBoundsTests
{
    /// <summary>A studio header's first bytes with a hull: enough for the navigation.</summary>
    internal static byte[] Mdl(Vec3 mins, Vec3 maxs, string ident = "IDST")
    {
        byte[] bytes = new byte[160];
        System.Text.Encoding.ASCII.GetBytes(ident).CopyTo(bytes, 0);
        float[] values = [mins.X, mins.Y, mins.Z, maxs.X, maxs.Y, maxs.Z];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(104 + (i * 4)), values[i]);
        }

        return bytes;
    }

    [Fact]
    public void AHullIsReadFromTheHeaderAndABadOneIsNone()
    {
        Assert.Equal((new Vec3(-8, -4, 0), new Vec3(8, 4, 20)), NavModelBounds.Hull(Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20))));
        Assert.Null(NavModelBounds.Hull(Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20), "XXXX")));
        Assert.Null(NavModelBounds.Hull(Mdl(new Vec3(0, 0, 0), new Vec3(0, 4, 20))));
        Assert.Null(NavModelBounds.Hull(new byte[50]));
    }

    [Fact]
    public async Task HullsAreReadFromTheContentForTheModelsAMapsPropsName()
    {
        await using ContentFileSystem content = await StudioModelWriterTests.MountAsync(new Dictionary<string, byte[]>
        {
            ["models/crate.mdl"] = Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20)),
        });
        BspData bsp = new();
        List<BspEntity> entities = [new(), new(), new(), new(), new()];
        entities[0].Pairs.Add(new BspKeyValue("classname", "worldspawn"));
        entities[1].Pairs.Add(new BspKeyValue("classname", "prop_physics"));
        entities[1].Pairs.Add(new BspKeyValue("model", "models/crate.mdl"));
        entities[2].Pairs.Add(new BspKeyValue("classname", "prop_dynamic"));
        entities[2].Pairs.Add(new BspKeyValue("model", "models/gone.mdl"));
        entities[3].Pairs.Add(new BspKeyValue("classname", "prop_physics"));
        entities[3].Pairs.Add(new BspKeyValue("model", "models/crate.mdl"));
        entities[4].Pairs.Add(new BspKeyValue("classname", "func_door"));
        entities[4].Pairs.Add(new BspKeyValue("model", "*1"));
        bsp.SetLump(BspLump.Entities, EntityLump.Write(entities).Data);

        IReadOnlyList<string> models = NavModelBounds.PropModels(bsp);
        Assert.Equal(["models/crate.mdl", "models/gone.mdl"], models);
        IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?> hulls = await NavModelBounds.LoadAsync(content, [.. models, "not a path\0"]);
        Assert.Equal((new Vec3(-8, -4, 0), new Vec3(8, 4, 20)), hulls["models/crate.mdl"]);
        Assert.Null(hulls["models/gone.mdl"]);
        Assert.Null(hulls["not a path\0"]);
    }
}
