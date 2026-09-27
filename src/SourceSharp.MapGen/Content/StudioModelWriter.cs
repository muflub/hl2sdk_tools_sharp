//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapGen.Content;

/// <summary>One vertex of a generated mesh.</summary>
/// <param name="Position">Model-space position.</param>
/// <param name="Normal">Unit normal.</param>
/// <param name="U">Texture U.</param>
/// <param name="V">Texture V.</param>
public readonly record struct MeshVertex(Vec3 Position, Vec3 Normal, float U, float V);

/// <summary>One mesh: a material slot and a triangle list over its own vertices.</summary>
/// <param name="Material">Index into the model's materials.</param>
/// <param name="Vertices">The vertices; at most 65,535.</param>
/// <param name="Triangles">Three indices per triangle, into <paramref name="Vertices"/>.</param>
public sealed record MeshSpec(int Material, IReadOnlyList<MeshVertex> Vertices, IReadOnlyList<int> Triangles);

/// <summary>What <see cref="StudioModelWriter"/> writes.</summary>
public sealed record StudioModelSpec
{
    /// <summary>The path under <c>models/</c>, e.g. <c>props_junk/wood_crate001a.mdl</c>.</summary>
    /// <remarks>
    /// Written into the header as well: vbsp finds the <c>.vvd</c> from the
    /// header's name, not from the path it opened, so the two must agree.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>The material names the meshes' slots index, without folder.</summary>
    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>The folders under <c>materials/</c> those names are looked up in, each ending in a slash.</summary>
    public required IReadOnlyList<string> MaterialSearchPaths { get; init; }

    /// <summary>The meshes, one convex collision hull each.</summary>
    public required IReadOnlyList<MeshSpec> Meshes { get; init; }

    /// <summary>The model's surface property.</summary>
    public string SurfaceProp { get; init; } = "default";

    /// <summary>The header flags; <see cref="StaticPropFlag"/> makes it usable as a <c>prop_static</c>.</summary>
    public int Flags { get; init; } = StaticPropFlag;

    /// <summary>
    /// The model's keyvalue text as studiomdl writes it, under an
    /// <c>mdlkeyvalue</c> root, e.g. <c>mdlkeyvalue { prop_data { "allowstatic" "0" } }</c>; or null.
    /// </summary>
    public string? KeyValues { get; init; }

    /// <summary>How many LODs the VTX and VVD declare; every LOD repeats LOD 0's geometry.</summary>
    public int Lods { get; init; } = 1;

    /// <summary>The model's mass, for the header and the collision key data.</summary>
    public float Mass { get; init; } = 10;

    /// <summary><c>STUDIOHDR_FLAGS_STATIC_PROP</c>.</summary>
    public const int StaticPropFlag = 0x10;

    /// <summary><c>STUDIOHDR_FLAGS_CAST_TEXTURE_SHADOWS</c>: vrad traces the materials' alpha.</summary>
    public const int CastTextureShadowsFlag = 0x40000;
}

/// <summary>
/// Writes a studio model the compile tools can read: <c>.mdl</c>,
/// <c>.vvd</c>, <c>.dx80.vtx</c>, <c>.dx90.vtx</c> and <c>.phy</c>.
/// </summary>
/// <remarks>
/// <para>
/// The model is what a <c>$staticprop</c> compile of simple geometry gives:
/// one bone, one body part, one model, a mesh per material slot, and in the
/// VTX one strip group of one triangle-list strip per mesh. Every file
/// carries the same checksum, which vbsp checks between the MDL and the VVD
/// (a mismatch is fatal) and vrad between the MDL and the VTX.
/// </para>
/// <para>
/// No animation, sequence, hitbox or attachment data is written: nothing in
/// a map compile reads them. The <c>.phy</c> has one solid per mesh whose
/// payload is the mesh's points; vrad only counts the solids, and vbsp
/// cooks its own hulls from the VVD.
/// </para>
/// </remarks>
public static class StudioModelWriter
{
    private const int VertexBytes = 48;

    /// <summary>Writes every file of a model.</summary>
    /// <param name="spec">The model.</param>
    /// <returns>Path under the game directory to bytes.</returns>
    public static IReadOnlyDictionary<string, byte[]> Write(StudioModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Meshes.Count == 0 || spec.Lods is < 1 or > StudioIdents.MaxLods)
        {
            throw new ArgumentException($"{spec.Name}: a model needs a mesh and 1..{StudioIdents.MaxLods} LODs");
        }

        foreach (MeshSpec mesh in spec.Meshes)
        {
            if (mesh.Vertices.Count is 0 or > ushort.MaxValue || mesh.Triangles.Count % 3 != 0
                || mesh.Material < 0 || mesh.Material >= spec.Materials.Count
                || mesh.Triangles.Any(i => i < 0 || i >= mesh.Vertices.Count))
            {
                throw new ArgumentException($"{spec.Name}: a mesh is empty, not triangles, or indexes out of range");
            }
        }

        // The same bytes always give the same checksum, so regenerating
        // content leaves every file identical.
        int checksum = Checksum(spec);
        string stem = "models/" + spec.Name[..^".mdl".Length];
        return new Dictionary<string, byte[]>
        {
            [stem + ".mdl"] = Mdl(spec, checksum),
            [stem + ".vvd"] = Vvd(spec, checksum),
            [stem + ".dx80.vtx"] = Vtx(spec, checksum),
            [stem + ".dx90.vtx"] = Vtx(spec, checksum),
            [stem + ".phy"] = Phy(spec, checksum),
        };
    }

    private static int Checksum(StudioModelSpec spec)
    {
        uint hash = 2166136261;
        void Mix(string s)
        {
            foreach (char c in s)
            {
                hash = (hash ^ c) * 16777619;
            }
        }

        Mix(spec.Name);
        foreach (MeshSpec mesh in spec.Meshes)
        {
            foreach (MeshVertex v in mesh.Vertices)
            {
                Mix(string.Create(CultureInfo.InvariantCulture, $"{v.Position.X},{v.Position.Y},{v.Position.Z}"));
            }
        }

        return unchecked((int)hash);
    }

    private static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<MeshVertex> vertices)
    {
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (MeshVertex v in vertices)
        {
            min = new Vec3(Math.Min(min.X, v.Position.X), Math.Min(min.Y, v.Position.Y), Math.Min(min.Z, v.Position.Z));
            max = new Vec3(Math.Max(max.X, v.Position.X), Math.Max(max.Y, v.Position.Y), Math.Max(max.Z, v.Position.Z));
        }

        return (min, max);
    }

    private static void Name64(ref ByteArray64 field, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        for (int i = 0; i < Math.Min(bytes.Length, 63); i++)
        {
            field[i] = bytes[i];
        }
    }

    private static byte[] Mdl(StudioModelSpec spec, int checksum)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        int boneSize = Unsafe.SizeOf<StudioBone>();
        int textureSize = Unsafe.SizeOf<StudioTexture>();
        int partSize = Unsafe.SizeOf<StudioBodyParts>();
        int modelSize = Unsafe.SizeOf<StudioModel>();
        int meshSize = Unsafe.SizeOf<StudioMesh>();

        // Fixed-size records first, then a string table they all point into.
        int boneAt = headerSize;
        int textureAt = boneAt + boneSize;
        int cdAt = textureAt + (spec.Materials.Count * textureSize);
        int partAt = cdAt + (spec.MaterialSearchPaths.Count * sizeof(int));
        int modelAt = partAt + partSize;
        int meshAt = modelAt + modelSize;
        int stringsAt = meshAt + (spec.Meshes.Count * meshSize);

        List<byte> strings = [];
        int Str(string s)
        {
            int at = stringsAt + strings.Count;
            strings.AddRange(Encoding.Latin1.GetBytes(s));
            strings.Add(0);
            return at;
        }

        int boneName = Str("static_prop");
        int[] textureNames = [.. spec.Materials.Select(Str)];
        int[] cdNames = [.. spec.MaterialSearchPaths.Select(Str)];
        int partName = Str("body");
        int surfaceProp = Str(spec.SurfaceProp);
        int keyValues = spec.KeyValues is null ? 0 : Str(spec.KeyValues);
        int keyValueSize = spec.KeyValues is null ? 0 : Encoding.Latin1.GetByteCount(spec.KeyValues) + 1;

        int total = stringsAt + strings.Count;
        byte[] bytes = new byte[total];
        Span<byte> span = bytes;
        (Vec3 min, Vec3 max) = Bounds(spec.Meshes.SelectMany(m => m.Vertices));
        Vec3 centre = new((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);

        StudioHeader header = default;
        header.Id = StudioIdents.Mdl;
        header.Version = StudioIdents.MdlVersion;
        header.Checksum = checksum;
        Name64(ref header.Name, spec.Name);
        header.Length = total;
        header.IllumPosition = centre;
        header.HullMin = min;
        header.HullMax = max;
        header.ViewBbMin = min;
        header.ViewBbMax = max;
        header.Flags = spec.Flags;
        header.NumBones = 1;
        header.BoneIndex = boneAt;
        header.NumTextures = spec.Materials.Count;
        header.TextureIndex = textureAt;
        header.NumCdTextures = spec.MaterialSearchPaths.Count;
        header.CdTextureIndex = cdAt;
        header.NumSkinRef = spec.Materials.Count;
        header.NumSkinFamilies = 1;
        header.NumBodyParts = 1;
        header.BodyPartIndex = partAt;
        header.SurfacePropIndex = surfaceProp;
        header.KeyValueIndex = keyValues;
        header.KeyValueSize = keyValueSize;
        header.Mass = spec.Mass;
        header.Contents = 1; // CONTENTS_SOLID
        header.RootLod = 0;
        header.NumAllowedRootLods = 0;
        MemoryMarshal.Write(span, in header);

        // One identity bone that every vertex is fully weighted to.
        StudioBone bone = default;
        bone.NameIndex = boneName - boneAt;
        bone.Parent = -1;
        for (int i = 0; i < 6; i++)
        {
            bone.BoneController[i] = -1;
        }

        bone.Quat[3] = 1;
        bone.QAlignment[3] = 1;
        bone.PosScale = new Vec3(1, 1, 1);
        bone.RotScale = new Vec3(1, 1, 1);
        bone.PoseToBone[0] = 1;
        bone.PoseToBone[5] = 1;
        bone.PoseToBone[10] = 1;
        bone.Contents = 1;
        MemoryMarshal.Write(span[boneAt..], in bone);

        for (int i = 0; i < spec.Materials.Count; i++)
        {
            int at = textureAt + (i * textureSize);
            StudioTexture texture = default;
            texture.NameIndex = textureNames[i] - at;
            MemoryMarshal.Write(span[at..], in texture);
        }

        for (int i = 0; i < cdNames.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(cdAt + (i * sizeof(int)))..], cdNames[i]);
        }

        StudioBodyParts part = default;
        part.NameIndex = partName - partAt;
        part.NumModels = 1;
        part.Base = 1;
        part.ModelIndex = modelAt - partAt;
        MemoryMarshal.Write(span[partAt..], in part);

        StudioModel model = default;
        Name64(ref model.Name, spec.Name[..^".mdl".Length]);
        model.BoundingRadius = Length(new Vec3(max.X - centre.X, max.Y - centre.Y, max.Z - centre.Z));
        model.NumMeshes = spec.Meshes.Count;
        model.MeshIndex = meshAt - modelAt;
        model.NumVertices = spec.Meshes.Sum(m => m.Vertices.Count);
        model.VertexIndex = 0;
        model.TangentsIndex = 0;
        MemoryMarshal.Write(span[modelAt..], in model);

        int vertexOffset = 0;
        for (int i = 0; i < spec.Meshes.Count; i++)
        {
            MeshSpec m = spec.Meshes[i];
            int at = meshAt + (i * meshSize);
            (Vec3 mmin, Vec3 mmax) = Bounds(m.Vertices);
            StudioMesh mesh = default;
            mesh.Material = m.Material;
            mesh.ModelIndex = modelAt - at;
            mesh.NumVertices = m.Vertices.Count;
            mesh.VertexOffset = vertexOffset;
            mesh.MeshId = i;
            mesh.Center = new Vec3((mmin.X + mmax.X) / 2, (mmin.Y + mmax.Y) / 2, (mmin.Z + mmax.Z) / 2);
            for (int lod = 0; lod < StudioIdents.MaxLods; lod++)
            {
                mesh.NumLodVertexes[lod] = m.Vertices.Count;
            }

            MemoryMarshal.Write(span[at..], in mesh);
            vertexOffset += m.Vertices.Count;
        }

        strings.ToArray().CopyTo(span[stringsAt..]);
        return bytes;
    }

    private static float Length(Vec3 v) => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

    // Every mesh's vertices in order, one block, no fixups: each LOD shares it.
    private static byte[] Vvd(StudioModelSpec spec, int checksum)
    {
        int headerSize = Unsafe.SizeOf<VertexFileHeader>();
        int count = spec.Meshes.Sum(m => m.Vertices.Count);
        byte[] bytes = new byte[headerSize + (count * VertexBytes)];
        Span<byte> span = bytes;

        VertexFileHeader header = default;
        header.Id = StudioIdents.Vvd;
        header.Version = StudioIdents.VvdVersion;
        header.Checksum = checksum;
        header.NumLods = spec.Lods;
        for (int lod = 0; lod < spec.Lods; lod++)
        {
            header.NumLodVertexes[lod] = count;
        }

        header.VertexDataStart = headerSize;
        MemoryMarshal.Write(span, in header);

        int at = headerSize;
        foreach (MeshVertex v in spec.Meshes.SelectMany(m => m.Vertices))
        {
            StudioVertex vertex = default;
            vertex.BoneWeights.Weight[0] = 1;
            vertex.BoneWeights.NumBones = 1;
            vertex.Position = v.Position;
            vertex.Normal = v.Normal;
            vertex.TexCoord[0] = v.U;
            vertex.TexCoord[1] = v.V;
            MemoryMarshal.Write(span[at..], in vertex);
            at += VertexBytes;
        }

        return bytes;
    }

    // Body part > model > LODs > meshes > one strip group > one triangle-list strip.
    private static byte[] Vtx(StudioModelSpec spec, int checksum)
    {
        int headerSize = Unsafe.SizeOf<VtxFileHeader>();
        int partSize = Unsafe.SizeOf<VtxBodyPartHeader>();
        int modelSize = Unsafe.SizeOf<VtxModelHeader>();
        int lodSize = Unsafe.SizeOf<VtxModelLodHeader>();
        int meshSize = Unsafe.SizeOf<VtxMeshHeader>();
        int groupSize = Unsafe.SizeOf<VtxStripGroupHeader>();
        int vertexSize = Unsafe.SizeOf<VtxVertex>();
        int stripSize = Unsafe.SizeOf<VtxStripHeader>();

        int partAt = headerSize;
        int modelAt = partAt + partSize;
        int lodAt = modelAt + modelSize;
        int meshesAt = lodAt + (spec.Lods * lodSize);
        int blocksAt = meshesAt + (spec.Lods * spec.Meshes.Count * meshSize);

        int BlockSize(MeshSpec m) =>
            groupSize + (m.Triangles.Count * sizeof(ushort)) + (m.Vertices.Count * vertexSize) + stripSize;

        int perLod = spec.Meshes.Sum(BlockSize);
        byte[] bytes = new byte[blocksAt + (spec.Lods * perLod)];
        Span<byte> span = bytes;

        VtxFileHeader header = default;
        header.Version = StudioIdents.VtxVersion;
        header.VertCacheSize = 24;
        header.MaxBonesPerStrip = 53;
        header.MaxBonesPerTri = 9;
        header.MaxBonesPerVert = StudioIdents.MaxBonesPerVert;
        header.CheckSum = checksum;
        header.NumLods = spec.Lods;
        header.NumBodyParts = 1;
        header.BodyPartOffset = partAt;
        MemoryMarshal.Write(span, in header);

        VtxBodyPartHeader part = default;
        part.NumModels = 1;
        part.ModelOffset = modelAt - partAt;
        MemoryMarshal.Write(span[partAt..], in part);

        VtxModelHeader model = default;
        model.NumLods = spec.Lods;
        model.LodOffset = lodAt - modelAt;
        MemoryMarshal.Write(span[modelAt..], in model);

        int blockAt = blocksAt;
        for (int lod = 0; lod < spec.Lods; lod++)
        {
            int thisLod = lodAt + (lod * lodSize);
            int thisMeshes = meshesAt + (lod * spec.Meshes.Count * meshSize);
            VtxModelLodHeader lodHeader = default;
            lodHeader.NumMeshes = spec.Meshes.Count;
            lodHeader.MeshOffset = thisMeshes - thisLod;
            lodHeader.SwitchPoint = lod * 500f;
            MemoryMarshal.Write(span[thisLod..], in lodHeader);

            for (int i = 0; i < spec.Meshes.Count; i++)
            {
                MeshSpec m = spec.Meshes[i];
                int meshHeaderAt = thisMeshes + (i * meshSize);
                int indicesAt = blockAt + groupSize;
                int verticesAt = indicesAt + (m.Triangles.Count * sizeof(ushort));
                int stripAt = verticesAt + (m.Vertices.Count * vertexSize);

                VtxMeshHeader mesh = default;
                mesh.NumStripGroups = 1;
                mesh.StripGroupHeaderOffset = blockAt - meshHeaderAt;
                MemoryMarshal.Write(span[meshHeaderAt..], in mesh);

                VtxStripGroupHeader group = default;
                group.NumVerts = m.Vertices.Count;
                group.VertOffset = verticesAt - blockAt;
                group.NumIndices = m.Triangles.Count;
                group.IndexOffset = indicesAt - blockAt;
                group.NumStrips = 1;
                group.StripOffset = stripAt - blockAt;
                MemoryMarshal.Write(span[blockAt..], in group);

                for (int k = 0; k < m.Triangles.Count; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span[(indicesAt + (k * sizeof(ushort)))..], (ushort)m.Triangles[k]);
                }

                for (int k = 0; k < m.Vertices.Count; k++)
                {
                    VtxVertex vertex = default;
                    vertex.NumBones = 1;
                    vertex.OrigMeshVertId = (ushort)k;
                    MemoryMarshal.Write(span[(verticesAt + (k * vertexSize))..], in vertex);
                }

                VtxStripHeader strip = default;
                strip.NumIndices = m.Triangles.Count;
                strip.NumVerts = m.Vertices.Count;
                strip.NumBones = 1;
                strip.Flags = (byte)VtxStripFlags.IsTriList;
                MemoryMarshal.Write(span[stripAt..], in strip);

                blockAt += BlockSize(m);
            }
        }

        return bytes;
    }

    private static byte[] Phy(StudioModelSpec spec, int checksum)
    {
        List<byte> bytes = [];
        byte[] header = new byte[Unsafe.SizeOf<PhyHeader>()];
        PhyHeader h = new() { Size = header.Length, Id = 0, SolidCount = spec.Meshes.Count, CheckSum = checksum };
        MemoryMarshal.Write(header, in h);
        bytes.AddRange(header);

        foreach (MeshSpec mesh in spec.Meshes)
        {
            byte[] solid = new byte[4 + (mesh.Vertices.Count * 12)];
            "VPHY"u8.CopyTo(solid);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                Vec3 p = mesh.Vertices[i].Position;
                BinaryPrimitives.WriteSingleLittleEndian(solid.AsSpan(4 + (i * 12)), p.X);
                BinaryPrimitives.WriteSingleLittleEndian(solid.AsSpan(8 + (i * 12)), p.Y);
                BinaryPrimitives.WriteSingleLittleEndian(solid.AsSpan(12 + (i * 12)), p.Z);
            }

            bytes.AddRange(BitConverter.GetBytes(solid.Length));
            bytes.AddRange(solid);
        }

        StringBuilder keys = new();
        for (int i = 0; i < spec.Meshes.Count; i++)
        {
            keys.Append(CultureInfo.InvariantCulture,
                $"solid {{\n\"index\" \"{i}\"\n\"name\" \"static_prop\"\n\"mass\" \"{spec.Mass / spec.Meshes.Count:0.###}\"\n\"surfaceprop\" \"{spec.SurfaceProp}\"\n}}\n");
        }

        keys.Append("editparams {\n\"rootname\" \"\"\n\"totalmass\" \"")
            .Append(spec.Mass.ToString("0.###", CultureInfo.InvariantCulture)).Append("\"\n}\n");
        bytes.AddRange(Encoding.Latin1.GetBytes(keys.ToString()));
        bytes.Add(0);
        return [.. bytes];
    }
}
