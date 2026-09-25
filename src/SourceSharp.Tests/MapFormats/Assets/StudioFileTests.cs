using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The MDL, VVD and VTX readers, against files built byte by byte from the
/// headers.
/// </summary>
/// <remarks>
/// <para>
/// Synthetic rather than a committed model, and deliberately so: this tree
/// commits no <c>.mdl</c>, and a fact that skips when one is absent would
/// answer "pass" in exactly the trees where it matters. Every file here is
/// assembled from the struct layout the facts in
/// <see cref="StudioStructTests"/> already pin, so the two together are a
/// closed argument.
/// </para>
/// <para>
/// These readers WERE additionally run against real version 48 content during
/// development -- Team Fortress 2's <c>props_movies/hs_sign</c>, whose header
/// length field, cross-file checksums, VVD block offsets and VTX vertex stride
/// all came out exactly right. That check cannot be committed, because the
/// content is not in this repository.
/// </para>
/// </remarks>
public class StudioFileTests
{
    private static void Put<T>(List<byte> into, in T value)
        where T : unmanaged
    {
        byte[] bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write<T>(bytes, in value);
        into.AddRange(bytes);
    }

    /// <summary>
    /// A minimal MDL: one bone, one body part with one model with one mesh,
    /// one texture, and a strings block.
    /// </summary>
    private static byte[] BuildMdl(out int boneNameAt)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        int boneSize = Unsafe.SizeOf<StudioBone>();
        int partSize = Unsafe.SizeOf<StudioBodyParts>();
        int modelSize = Unsafe.SizeOf<StudioModel>();
        int meshSize = Unsafe.SizeOf<StudioMesh>();
        int textureSize = Unsafe.SizeOf<StudioTexture>();

        int boneAt = headerSize;
        int partAt = boneAt + boneSize;
        int modelAt = partAt + partSize;
        int meshAt = modelAt + modelSize;
        int textureAt = meshAt + meshSize;
        int cdTableAt = textureAt + textureSize;
        int stringsAt = cdTableAt + 4;

        List<byte> strings = [];
        int boneName = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes("static_prop\0"));
        int textureName = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes("models/props/crate\0"));
        int cdName = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes("models\\props\\\0"));
        int surfaceProp = stringsAt + strings.Count;
        strings.AddRange(Encoding.Latin1.GetBytes("metal\0"));
        int keyValues = stringsAt + strings.Count;
        byte[] kv = Encoding.Latin1.GetBytes("prop_data\n{\n\"base\" \"Wooden.Small\"\n}\n\0");
        strings.AddRange(kv);

        boneNameAt = boneName;

        StudioHeader header = new()
        {
            Id = StudioIdents.Mdl,
            Version = StudioIdents.MdlVersion,
            Checksum = 0x1234ABCD,
            Length = stringsAt + strings.Count,
            HullMin = new Vec3(-16, -16, 0),
            HullMax = new Vec3(16, 16, 64),
            ViewBbMin = new Vec3(-16, -16, 0),
            ViewBbMax = new Vec3(16, 16, 64),
            NumBones = 1,
            BoneIndex = boneAt,
            NumBodyParts = 1,
            BodyPartIndex = partAt,
            NumTextures = 1,
            TextureIndex = textureAt,
            NumCdTextures = 1,
            CdTextureIndex = cdTableAt,
            SurfacePropIndex = surfaceProp,
            KeyValueIndex = keyValues,
            KeyValueSize = kv.Length,
            Mass = 42.5f,
        };
        Encoding.Latin1.GetBytes("props/crate.mdl").CopyTo(((Span<byte>)header.Name));

        StudioBone bone = new()
        {
            // RELATIVE to the bone, not to the file: the reference implementation's accessor
            // is ((char *)this) + sznameindex.
            NameIndex = boneName - boneAt,
            Parent = -1,
        };

        StudioBodyParts part = new()
        {
            NameIndex = 0,
            NumModels = 1,
            Base = 1,
            ModelIndex = modelAt - partAt,
        };

        StudioModel model = new()
        {
            NumMeshes = 1,
            MeshIndex = meshAt - modelAt,
            NumVertices = 6,

            // A BYTE offset, not an index: vertex 2 of the VVD.
            VertexIndex = 2 * Unsafe.SizeOf<StudioVertex>(),
        };
        Encoding.Latin1.GetBytes("crate_body").CopyTo(((Span<byte>)model.Name));

        StudioMesh mesh = new()
        {
            Material = 0,
            ModelIndex = modelAt - meshAt,
            NumVertices = 6,

            // An INDEX, unlike the model's byte offset.
            VertexOffset = 0,
        };
        mesh.NumLodVertexes[0] = 6;

        StudioTexture texture = new() { NameIndex = textureName - textureAt };

        List<byte> bytes = [];
        Put(bytes, in header);
        Put(bytes, in bone);
        Put(bytes, in part);
        Put(bytes, in model);
        Put(bytes, in mesh);
        Put(bytes, in texture);
        bytes.AddRange(BitConverter.GetBytes(cdName));
        bytes.AddRange(strings);
        return [.. bytes];
    }

    private static byte[] BuildVvd(int vertexCount, int fixupCount)
    {
        int headerSize = Unsafe.SizeOf<VertexFileHeader>();
        int fixupSize = Unsafe.SizeOf<VertexFileFixup>();
        int vertexSize = Unsafe.SizeOf<StudioVertex>();

        int fixupAt = headerSize;
        int vertexAt = fixupAt + (fixupCount * fixupSize);
        int tangentAt = vertexAt + (vertexCount * vertexSize);

        VertexFileHeader header = new()
        {
            Id = StudioIdents.Vvd,
            Version = StudioIdents.VvdVersion,
            Checksum = 0x1234ABCD,
            NumLods = 2,
            NumFixups = fixupCount,
            FixupTableStart = fixupAt,
            VertexDataStart = vertexAt,
            TangentDataStart = tangentAt,
        };
        header.NumLodVertexes[0] = vertexCount;
        header.NumLodVertexes[1] = fixupCount > 0 ? 2 : vertexCount;

        List<byte> bytes = [];
        Put(bytes, in header);

        if (fixupCount == 2)
        {
            // Two runs in REVERSE file order, and one of them is LOD 1 only.
            // A reader that ignores the table returns the raw order and the
            // right count, which is what makes this bug survive a naive test.
            Put(bytes, new VertexFileFixup { Lod = 0, SourceVertexId = 2, NumVertexes = 2 });
            Put(bytes, new VertexFileFixup { Lod = 1, SourceVertexId = 0, NumVertexes = 2 });
        }

        for (int i = 0; i < vertexCount; i++)
        {
            StudioVertex vertex = new() { Position = new Vec3(i, 0, 0) };
            vertex.BoneWeights.NumBones = 1;
            vertex.BoneWeights.Weight[0] = 1.0f;
            Put(bytes, in vertex);
        }

        for (int i = 0; i < vertexCount; i++)
        {
            Put(bytes, default(FloatArray4));
        }

        return [.. bytes];
    }

    private static byte[] BuildVtx(bool triStrip)
    {
        int headerSize = Unsafe.SizeOf<VtxFileHeader>();
        int partSize = Unsafe.SizeOf<VtxBodyPartHeader>();
        int modelSize = Unsafe.SizeOf<VtxModelHeader>();
        int lodSize = Unsafe.SizeOf<VtxModelLodHeader>();
        int meshSize = Unsafe.SizeOf<VtxMeshHeader>();
        int groupSize = Unsafe.SizeOf<VtxStripGroupHeader>();
        int stripSize = Unsafe.SizeOf<VtxStripHeader>();
        int vertexSize = Unsafe.SizeOf<VtxVertex>();

        const int verts = 4;
        const int indices = 6;

        int partAt = headerSize;
        int modelAt = partAt + partSize;
        int lodAt = modelAt + modelSize;
        int meshAt = lodAt + lodSize;
        int groupAt = meshAt + meshSize;
        int stripAt = groupAt + groupSize;
        int vertexAt = stripAt + stripSize;
        int indexAt = vertexAt + (verts * vertexSize);

        VtxFileHeader header = new()
        {
            Version = StudioIdents.VtxVersion,
            CheckSum = 0x1234ABCD,
            NumLods = 1,
            MaxBonesPerVert = StudioIdents.MaxBonesPerVert,
            NumBodyParts = 1,
            BodyPartOffset = partAt,
        };

        List<byte> bytes = [];
        Put(bytes, in header);
        Put(bytes, new VtxBodyPartHeader { NumModels = 1, ModelOffset = modelAt - partAt });
        Put(bytes, new VtxModelHeader { NumLods = 1, LodOffset = lodAt - modelAt });
        Put(bytes, new VtxModelLodHeader
        {
            NumMeshes = 1,
            MeshOffset = meshAt - lodAt,
            SwitchPoint = 0.0f,
        });
        Put(bytes, new VtxMeshHeader
        {
            NumStripGroups = 1,
            StripGroupHeaderOffset = groupAt - meshAt,
            Flags = 0,
        });
        Put(bytes, new VtxStripGroupHeader
        {
            NumVerts = verts,
            VertOffset = vertexAt - groupAt,
            NumIndices = indices,
            IndexOffset = indexAt - groupAt,
            NumStrips = 1,
            StripOffset = stripAt - groupAt,
            Flags = (byte)VtxStripGroupFlags.IsHwSkinned,
        });
        Put(bytes, new VtxStripHeader
        {
            NumIndices = indices,
            IndexOffset = 0,
            NumVerts = verts,
            VertOffset = 0,
            NumBones = 1,
            Flags = (byte)(triStrip ? VtxStripFlags.IsTriStrip : VtxStripFlags.IsTriList),
        });

        for (int i = 0; i < verts; i++)
        {
            VtxVertex vertex = new() { NumBones = 1, OrigMeshVertId = (ushort)i };
            Put(bytes, in vertex);
        }

        for (int i = 0; i < indices; i++)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)(i % verts)));
        }

        return [.. bytes];
    }

    [Fact]
    public void TheMdlHeaderLengthMatchesTheFileWeBuilt()
    {
        // studiohdr_t::length is the file's own size. This is the cheapest
        // whole-layout check there is, and it is the one that caught nothing
        // on real content either -- because the layout is right.
        byte[] bytes = BuildMdl(out _);
        MdlFile mdl = MdlFile.Parse(bytes);

        Assert.Equal(bytes.Length, mdl.Header.Length);
    }

    [Fact]
    public void ReadsTheModelName()
    {
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal("props/crate.mdl", mdl.Name);
    }

    [Fact]
    public void ReadsTheHullBounds()
    {
        // What vbsp actually wants from an MDL.
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal(new Vec3(-16, -16, 0), mdl.HullBounds.Min);
        Assert.Equal(new Vec3(16, 16, 64), mdl.HullBounds.Max);
    }

    [Fact]
    public void ReadsTheSurfaceProperty()
    {
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal("metal", mdl.SurfaceProp);
    }

    [Fact]
    public void ReadsTheKeyValueText()
    {
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.StartsWith("prop_data", mdl.KeyValueText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABoneNameOffsetIsRelativeToTheBoneAndNotToTheFile()
    {
        // ((char *)this) + sznameindex. Reading it as a file
        // offset lands in the middle of the header on a real model and gives
        // either an empty string or garbage.
        byte[] bytes = BuildMdl(out int boneNameAt);
        MdlFile mdl = MdlFile.Parse(bytes);

        Assert.Equal("static_prop", mdl.BoneName(0));
        Assert.NotEqual(mdl.Bones()[0].NameIndex, boneNameAt);
    }

    [Fact]
    public void ReadsTheTextureName()
    {
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal("models/props/crate", mdl.TextureName(0));
    }

    [Fact]
    public void ReadsTheMaterialSearchPaths()
    {
        // The cdtexture table is an array of ints, each an ABSOLUTE file
        // offset -- unlike almost every other offset in the reference implementation.
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal(@"models\props\", mdl.MaterialSearchPaths()[0]);
    }

    [Fact]
    public void ReadsTheBodyPartsModelsAndMeshes()
    {
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Single(mdl.BodyParts().ToArray());
        Assert.Single(mdl.Models(0).ToArray());
        Assert.Single(mdl.Meshes(0, 0).ToArray());
    }

    [Fact]
    public void AModelsVertexIndexIsAByteOffsetAndAMeshsIsAnIndex()
    {
        // The two fields are spelled differently in the reference format for this reason
        // (vertexindex against vertexoffset) and confusing them scales a
        // model's first vertex by 48.
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));

        Assert.Equal(2 * Unsafe.SizeOf<StudioVertex>(), mdl.Models(0)[0].VertexIndex);
        Assert.Equal(0, mdl.Meshes(0, 0)[0].VertexOffset);
    }

    [Fact]
    public void RejectsAnMdlWithTheWrongIdent()
    {
        byte[] bytes = BuildMdl(out _);
        MemoryMarshal.Write(bytes.AsSpan(), 0);

        Assert.Throws<InvalidStudioException>(() => MdlFile.Parse(bytes));
    }

    [Fact]
    public void RejectsAnMdlOfAnotherVersion()
    {
        // Vbsp refuses anything but STUDIO_VERSION, so
        // reading a Portal 2 model here would be inventing compatibility.
        byte[] bytes = BuildMdl(out _);
        MemoryMarshal.Write(bytes.AsSpan(4), 49);

        Assert.Throws<InvalidStudioException>(() => MdlFile.Parse(bytes));
    }

    [Fact]
    public void AcceptsAnIdagAnimationGroupIdent()
    {
        byte[] bytes = BuildMdl(out _);
        MemoryMarshal.Write(bytes.AsSpan(), StudioIdents.AnimationGroup);

        Assert.Equal(StudioIdents.AnimationGroup, MdlFile.Parse(bytes).Header.Id);
    }

    [Fact]
    public void TheVvdVertexBlockStartsRightAfterTheHeaderWhenThereAreNoFixups()
    {
        VvdFile vvd = VvdFile.Parse(BuildVvd(4, 0));

        Assert.Equal(Unsafe.SizeOf<VertexFileHeader>(), vvd.Header.VertexDataStart);
    }

    [Fact]
    public void TheVvdTangentBlockStartsAfterFortyEightBytesPerVertex()
    {
        // The arithmetic that proves mstudiovertex_t is 48 bytes from inside
        // the file, rather than from the struct.
        VvdFile vvd = VvdFile.Parse(BuildVvd(4, 0));

        Assert.Equal(
            vvd.Header.VertexDataStart + (4 * 48),
            vvd.Header.TangentDataStart);
    }

    [Fact]
    public void ReadsTheVvdVertexPositions()
    {
        VvdFile vvd = VvdFile.Parse(BuildVvd(4, 0));

        Assert.Equal(new Vec3(3, 0, 0), vvd.RawVertices()[3].Position);
    }

    [Fact]
    public void AVvdWithNoFixupsReturnsItsVerticesInFileOrder()
    {
        StudioVertex[] lod0 = VvdFile.Parse(BuildVvd(4, 0)).VerticesForLod(0);

        Assert.Equal<float[]>([0, 1, 2, 3], [.. lod0.Select(v => v.Position.X)]);
    }

    [Fact]
    public void AVvdWithFixupsReordersItsVertices()
    {
        // The fixup table lists run [2..4) before run [0..2), so LOD 0 comes
        // back as 2, 3, 0, 1. A reader that ignores the table returns
        // 0, 1, 2, 3 -- the right COUNT and the wrong vertices.
        StudioVertex[] lod0 = VvdFile.Parse(BuildVvd(4, 2)).VerticesForLod(0);

        Assert.Equal<float[]>([2, 3, 0, 1], [.. lod0.Select(v => v.Position.X)]);
    }

    [Fact]
    public void AFixupForAFinerLodIsSkippedAtACoarserOne()
    {
        // Run 0 is LOD 0 only, so LOD 1 keeps just the LOD 1 run.
        StudioVertex[] lod1 = VvdFile.Parse(BuildVvd(4, 2)).VerticesForLod(1);

        Assert.Equal<float[]>([0, 1], [.. lod1.Select(v => v.Position.X)]);
    }

    [Fact]
    public void RejectsAVvdWithTheWrongIdent()
    {
        byte[] bytes = BuildVvd(4, 0);
        MemoryMarshal.Write(bytes.AsSpan(), 0);

        Assert.Throws<InvalidStudioException>(() => VvdFile.Parse(bytes));
    }

    [Fact]
    public void RejectsAThinVvdRatherThanMisreadingIt()
    {
        // IDCV holds thinModelVertices_t, which is quantised
        // and NOT 48 bytes per vertex. Reading it as if it were produces
        // plausible-looking garbage.
        byte[] bytes = BuildVvd(4, 0);
        MemoryMarshal.Write(bytes.AsSpan(), StudioIdents.VvdThin);

        InvalidStudioException error =
            Assert.Throws<InvalidStudioException>(() => VvdFile.Parse(bytes));
        Assert.Contains("thin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVtxHasNoIdentSoTheVersionIsTheFirstFourBytes()
    {
        VtxFile vtx = VtxFile.Parse(BuildVtx(triStrip: false));

        Assert.Equal(StudioIdents.VtxVersion, vtx.Header.Version);
    }

    [Fact]
    public void RejectsAVtxOfAnotherVersion()
    {
        byte[] bytes = BuildVtx(triStrip: false);
        MemoryMarshal.Write(bytes.AsSpan(), 6);

        Assert.Throws<InvalidStudioException>(() => VtxFile.Parse(bytes));
    }

    [Fact]
    public void WalksTheVtxHierarchyToItsStripGroups()
    {
        VtxFile vtx = VtxFile.Parse(BuildVtx(triStrip: false));
        (int partOffset, VtxBodyPartHeader part) = vtx.BodyParts()[0];
        (int modelOffset, VtxModelHeader model) = vtx.Models(partOffset, part)[0];
        (int lodOffset, VtxModelLodHeader lod) = vtx.Lods(modelOffset, model)[0];
        (int meshOffset, VtxMeshHeader mesh) = vtx.Meshes(lodOffset, lod)[0];

        Assert.Single(vtx.StripGroups(meshOffset, mesh));
    }

    [Fact]
    public void ReadsVtxVerticesAtANineByteStride()
    {
        // With a 10-byte stride the last vertex's id reads as 0 rather than 3,
        // because it picks up the padding of its predecessor.
        VtxFile vtx = VtxFile.Parse(BuildVtx(triStrip: false));
        (int partOffset, VtxBodyPartHeader part) = vtx.BodyParts()[0];
        (int modelOffset, VtxModelHeader model) = vtx.Models(partOffset, part)[0];
        (int lodOffset, VtxModelLodHeader lod) = vtx.Lods(modelOffset, model)[0];
        (int meshOffset, VtxMeshHeader mesh) = vtx.Meshes(lodOffset, lod)[0];
        (int groupOffset, VtxStripGroupHeader group) = vtx.StripGroups(meshOffset, mesh)[0];

        ReadOnlySpan<VtxVertex> vertices = vtx.Vertices(groupOffset, group);
        Assert.Equal(4, vertices.Length);
        Assert.Equal(3, vertices[3].OrigMeshVertId);
    }

    [Fact]
    public void ATriangleListContributesOneTriangleEveryThreeIndices()
    {
        Assert.Equal(2, VtxFile.Parse(BuildVtx(triStrip: false)).TriangleCount(0));
    }

    [Fact]
    public void ATriangleStripContributesIndicesMinusTwo()
    {
        // Counting a strip as a list undercounts a real model by roughly a
        // third, which reads as "the port lost some geometry".
        Assert.Equal(4, VtxFile.Parse(BuildVtx(triStrip: true)).TriangleCount(0));
    }

    [Fact]
    public void TheThreeFilesOfOneModelShareAChecksum()
    {
        // The invariant that binds them. A stale VVD next to a fresh MDL is
        // the classic studiomdl failure and this is how it is caught.
        MdlFile mdl = MdlFile.Parse(BuildMdl(out _));
        VvdFile vvd = VvdFile.Parse(BuildVvd(4, 0));
        VtxFile vtx = VtxFile.Parse(BuildVtx(triStrip: false));

        Assert.Equal(mdl.Checksum, vvd.Checksum);
        Assert.Equal(mdl.Checksum, vtx.Checksum);
    }

    [Fact]
    public void AnOffsetPastTheEndOfTheFileThrowsRatherThanReadingGarbage()
    {
        byte[] bytes = BuildMdl(out _);
        MemoryMarshal.Write(bytes.AsSpan(160), 1 << 20);

        MdlFile mdl = MdlFile.Parse(bytes);
        Assert.Throws<InvalidStudioException>(() => mdl.Bones().Length);
    }
}
