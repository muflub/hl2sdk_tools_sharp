//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//


using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;

using Xunit;

namespace SourceSharp.Tests.MapGen.Content;

/// <summary>
/// <see cref="StudioModelWriter"/> and <see cref="Primitives"/>: a written
/// model reads back through the studio parsers and through the loaders vbsp
/// and vrad use for static props.
/// </summary>
public sealed class StudioModelWriterTests
{
    private static StudioModelSpec Crate(int flags = StudioModelSpec.StaticPropFlag, string? keyValues = null, int lods = 1) => new()
    {
        Name = "props_test/crate.mdl",
        Materials = ["crate", "lid"],
        MaterialSearchPaths = ["models/props_test/"],
        Meshes =
        [
            Primitives.Box(0, new Vec3(-8, -8, 0), new Vec3(8, 8, 16)),
            Primitives.Cylinder(1, 4, 16, 18, 6),
        ],
        SurfaceProp = "wood_crate",
        Flags = flags,
        KeyValues = keyValues,
        Lods = lods,
    };

    internal static async Task<ContentFileSystem> MountAsync(IReadOnlyDictionary<string, byte[]> files)
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in files)
        {
            disk.AddFile("game/" + path, bytes);
        }

        return new ContentFileSystem([await DirectoryContentMount.MountAsync(disk, VPath.Create("game"))]);
    }

    [Fact]
    public void EveryFileOfTheModelIsWritten()
    {
        Assert.Equal(
            ["models/props_test/crate.dx80.vtx", "models/props_test/crate.dx90.vtx", "models/props_test/crate.mdl",
             "models/props_test/crate.phy", "models/props_test/crate.vvd"],
            StudioModelWriter.Write(Crate()).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheMdlCarriesTheSpec()
    {
        IReadOnlyDictionary<string, byte[]> files = StudioModelWriter.Write(Crate(keyValues: "mdlkeyvalue { a b }"));
        MdlFile mdl = MdlFile.Parse(files["models/props_test/crate.mdl"]);

        Assert.Equal("props_test/crate.mdl", mdl.Name);
        Assert.Equal(StudioModelSpec.StaticPropFlag, mdl.Header.Flags);
        Assert.Equal("crate", mdl.TextureName(0));
        Assert.Equal("lid", mdl.TextureName(1));
        Assert.Equal(["models/props_test/"], mdl.MaterialSearchPaths());
        Assert.Equal("wood_crate", mdl.SurfaceProp);
        Assert.Equal("mdlkeyvalue { a b }", mdl.KeyValueText());
        Assert.Equal(new Vec3(-8, -8, 0), mdl.HullBounds.Min);
        Assert.Equal(new Vec3(8, 8, 18), mdl.HullBounds.Max);
        Assert.Equal(2, mdl.Meshes(0, 0).Length);
        Assert.Equal(24, mdl.Meshes(0, 0)[1].VertexOffset);
    }

    [Fact]
    public void EveryFileSharesTheMdlsChecksum()
    {
        IReadOnlyDictionary<string, byte[]> files = StudioModelWriter.Write(Crate());
        int checksum = MdlFile.Parse(files["models/props_test/crate.mdl"]).Checksum;

        Assert.Equal(checksum, VvdFile.Parse(files["models/props_test/crate.vvd"]).Checksum);
        Assert.Equal(checksum, VtxFile.Parse(files["models/props_test/crate.dx80.vtx"]).Header.CheckSum);
        Assert.Equal(checksum, PhyFile.Parse(files["models/props_test/crate.phy"]).Header.CheckSum);
    }

    [Fact]
    public void TheSameSpecWritesTheSameBytes()
    {
        IReadOnlyDictionary<string, byte[]> a = StudioModelWriter.Write(Crate());
        IReadOnlyDictionary<string, byte[]> b = StudioModelWriter.Write(Crate());

        Assert.All(a, kv => Assert.Equal(kv.Value, b[kv.Key]));
    }

    [Fact]
    public void VbspTakesOneHullPerMeshFromTheVvd()
    {
        IReadOnlyDictionary<string, byte[]> files = StudioModelWriter.Write(Crate());
        List<Vec3[]> hulls = StudioModelCheck.MeshHulls(
            MdlFile.Parse(files["models/props_test/crate.mdl"]), VvdFile.Parse(files["models/props_test/crate.vvd"]));

        Assert.Equal(2, hulls.Count);
        Assert.Equal(24, hulls[0].Length);
        Assert.All(hulls[1], p => Assert.InRange(p.Z, 16, 18));
    }

    [Fact]
    public async Task VbspAcceptsAStaticProp()
    {
        await using ContentFileSystem content = await MountAsync(StudioModelWriter.Write(Crate()));
        List<CompileDiagnostic> diagnostics = [];

        StudioModelLoad load = await StudioModelCheck.LoadAsync(
            content, "models/props_test/crate.mdl", "prop_static", diagnostics, ComplianceOptions.Correct);

        Assert.True(load.IsValid);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task VbspRefusesAModelWithoutTheStaticPropFlag()
    {
        await using ContentFileSystem content = await MountAsync(StudioModelWriter.Write(Crate(flags: 0)));
        List<CompileDiagnostic> diagnostics = [];

        StudioModelLoad load = await StudioModelCheck.LoadAsync(
            content, "models/props_test/crate.mdl", "prop_static", diagnostics, ComplianceOptions.Correct);

        Assert.Equal(StudioModelRejection.NotStaticProp, load.Rejection);
    }

    [Fact]
    public async Task VbspRefusesAModelWhoseKeyValuesForbidStatic()
    {
        StudioModelSpec spec = Crate(keyValues: "mdlkeyvalue\n{\n\tprop_data\n\t{\n\t\t\"allowstatic\" \"0\"\n\t}\n}\n");
        await using ContentFileSystem content = await MountAsync(StudioModelWriter.Write(spec));
        List<CompileDiagnostic> diagnostics = [];

        StudioModelLoad load = await StudioModelCheck.LoadAsync(
            content, "models/props_test/crate.mdl", "prop_static", diagnostics, ComplianceOptions.Correct);

        Assert.Equal(StudioModelRejection.DynamicOnly, load.Rejection);
    }

    [Fact]
    public async Task VradLoadsTheMeshesTheVtxAndThePhy()
    {
        await using ContentFileSystem content = await MountAsync(StudioModelWriter.Write(Crate(lods: 2)));
        StaticPropModel model = await new StaticPropModelLoader(content, NullPropCollisionSource.Instance)
            .LoadAsync("models/props_test/crate.mdl");

        Assert.NotNull(model.Vtx);
        Assert.NotNull(model.Vvd);
        Assert.Equal(2, model.Vtx!.Header.NumLods);
        Assert.Equal(2, model.PhysicsSolidCount);
        Assert.Equal(new Vec3(8, 8, 18), model.HullMax);
    }

    [Fact]
    public void AnInvalidSpecIsRefused()
    {
        StudioModelSpec good = Crate();
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with { Meshes = [] }));
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with { Lods = 0 }));
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with { Lods = 9 }));
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with
        {
            Meshes = [new MeshSpec(0, [new MeshVertex(default, default, 0, 0)], [0, 0, 1])],
        }));
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with
        {
            Meshes = [Primitives.Box(5, default, new Vec3(1, 1, 1))],
        }));
        Assert.Throws<ArgumentException>(() => StudioModelWriter.Write(good with
        {
            Meshes = [new MeshSpec(0, [new MeshVertex(default, default, 0, 0)], [0, 0])],
        }));
    }

    [Fact]
    public void ABoxHasOutwardFacesOfFourVertices()
    {
        MeshSpec box = Primitives.Box(0, new Vec3(-1, -2, -3), new Vec3(1, 2, 3));

        Assert.Equal(24, box.Vertices.Count);
        Assert.Equal(36, box.Triangles.Count);
        Assert.All(box.Vertices, v => Assert.True(Dot(v.Position, v.Normal) > 0));
    }

    [Fact]
    public void ACylinderHasABandAndTwoCappedFans()
    {
        MeshSpec cylinder = Primitives.Cylinder(0, 2, -1, 1, 8);

        Assert.Equal((9 * 2) + (2 * 9), cylinder.Vertices.Count);
        Assert.Equal(3 * ((8 * 2) + (8 * 2)), cylinder.Triangles.Count);
        Assert.All(cylinder.Vertices, v => Assert.True(Dot(v.Position, v.Normal) >= 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Primitives.Cylinder(0, 1, 0, 1, 2));
    }

    private static float Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
