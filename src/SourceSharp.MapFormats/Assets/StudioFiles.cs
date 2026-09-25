using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Assets;

/// <summary>Thrown when a stream is not a studio model file this port can read.</summary>
public sealed class InvalidStudioException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public InvalidStudioException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong with the file.</param>
    public InvalidStudioException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What was wrong with the file.</param>
    /// <param name="innerException">The underlying failure.</param>
    public InvalidStudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Shared helpers for reading a studio file's offset-linked structures.
/// </summary>
internal static class StudioReader
{
    /// <summary>The struct at <paramref name="offset"/>, bounds checked.</summary>
    internal static T At<T>(ReadOnlySpan<byte> bytes, int offset, string what)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (offset < 0 || offset + size > bytes.Length)
        {
            throw new InvalidStudioException(
                $"{what} would span {offset}..{offset + size} of a {bytes.Length}-byte file");
        }

        return MemoryMarshal.Read<T>(bytes[offset..]);
    }

    /// <summary>A run of structs at <paramref name="offset"/>, bounds checked.</summary>
    internal static ReadOnlySpan<T> Run<T>(
        ReadOnlySpan<byte> bytes,
        int offset,
        int count,
        string what)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (offset < 0 || count < 0 || (long)offset + ((long)count * size) > bytes.Length)
        {
            throw new InvalidStudioException(
                $"{what}: {count} entries of {size} bytes at offset {offset} do not fit a "
                + $"{bytes.Length}-byte file");
        }

        return MemoryMarshal.Cast<byte, T>(bytes.Slice(offset, count * size));
    }

    /// <summary>The NUL-terminated string at <paramref name="offset"/>.</summary>
    internal static string StringAt(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset <= 0 || offset >= bytes.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> tail = bytes[offset..];
        int end = tail.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? tail : tail[..end]);
    }

    /// <summary>A fixed-width NUL-padded name field as a string.</summary>
    internal static string FixedString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}

/// <summary>
/// A studio model's <c>.mdl</c> file: the header, bones, body parts and
/// textures.
/// </summary>
/// <remarks>
/// Read-only, and deliberately partial. See <see cref="StudioHeader"/> for the
/// list of what is not ported and why.
/// </remarks>
public sealed class MdlFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private MdlFile(ReadOnlyMemory<byte> bytes, StudioHeader header)
    {
        _bytes = bytes;
        Header = header;
    }

    /// <summary>The file header.</summary>
    public StudioHeader Header { get; }

    /// <summary>The model's own name from the header.</summary>
    public string Name
    {
        get
        {
            // A local because Header is a property: an inline array can only
            // be viewed as a span through a variable, not through a call's
            // temporary.
            ByteArray64 name = Header.Name;
            return StudioReader.FixedString(name);
        }
    }

    /// <summary>The checksum the VVD, VTX and PHY must all match.</summary>
    public int Checksum => Header.Checksum;

    /// <summary>The model's movement hull, which vbsp uses for a prop's bounds.</summary>
    public (Vec3 Min, Vec3 Max) HullBounds => (Header.HullMin, Header.HullMax);

    /// <summary>The model's render bounding box.</summary>
    public (Vec3 Min, Vec3 Max) ViewBounds => (Header.ViewBbMin, Header.ViewBbMax);

    /// <summary>The model's default surface property name.</summary>
    public string SurfaceProp => StudioReader.StringAt(_bytes.Span, Header.SurfacePropIndex);

    /// <summary>
    /// The model's keyvalue text, where <c>prop_data</c> lives.
    /// </summary>
    /// <returns>The text with its trailing NULs removed, or an empty string.</returns>
    public string KeyValueText()
    {
        if (Header.KeyValueIndex <= 0 || Header.KeyValueSize <= 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> span = _bytes.Span;
        if (Header.KeyValueIndex + Header.KeyValueSize > span.Length)
        {
            throw new InvalidStudioException(
                $"the keyvalue block spans {Header.KeyValueIndex}.."
                + $"{Header.KeyValueIndex + Header.KeyValueSize} of a {span.Length}-byte file");
        }

        ReadOnlySpan<byte> block = span.Slice(Header.KeyValueIndex, Header.KeyValueSize);
        int end = block.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? block : block[..end]);
    }

    /// <summary>Reads an MDL from a stream.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not an MDL this port can read.</exception>
    public static async Task<MdlFile> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses an MDL already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not an MDL this port can read.</exception>
    public static MdlFile Parse(ReadOnlyMemory<byte> bytes)
    {
        StudioHeader header = StudioReader.At<StudioHeader>(bytes.Span, 0, "the MDL header");

        if (header.Id != StudioIdents.Mdl && header.Id != StudioIdents.AnimationGroup)
        {
            throw new InvalidStudioException(
                $"the ident is 0x{header.Id:X8}, neither IDST nor IDAG; "
                + "the reference loader compares exactly those two literals");
        }

        if (header.Version != StudioIdents.MdlVersion)
        {
            throw new InvalidStudioException(
                $"MDL version {header.Version} is not {StudioIdents.MdlVersion}; "
                + "the reference loader refuses anything else");
        }

        return new MdlFile(bytes, header);
    }

    /// <summary>The model's bones.</summary>
    /// <returns>A span over the file, never a copy.</returns>
    public ReadOnlySpan<StudioBone> Bones() =>
        StudioReader.Run<StudioBone>(_bytes.Span, Header.BoneIndex, Header.NumBones, "bones");

    /// <summary>One bone's name.</summary>
    /// <param name="index">The bone's index.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    /// <remarks>
    /// <c>sznameindex</c> is relative to the BONE, not to the file: the
    /// reference layout's accessor is
    /// <c>((char *)this) + sznameindex</c>, which is the easiest offset in the
    /// studio format to get wrong.
    /// </remarks>
    public string BoneName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Header.NumBones);

        int boneAt = Header.BoneIndex + (index * Unsafe.SizeOf<StudioBone>());
        return StudioReader.StringAt(_bytes.Span, boneAt + Bones()[index].NameIndex);
    }

    /// <summary>The model's body parts.</summary>
    /// <returns>A span over the file.</returns>
    public ReadOnlySpan<StudioBodyParts> BodyParts() =>
        StudioReader.Run<StudioBodyParts>(
            _bytes.Span, Header.BodyPartIndex, Header.NumBodyParts, "body parts");

    /// <summary>The models of one body part.</summary>
    /// <param name="bodyPart">The body part's index.</param>
    /// <returns>A span over the file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public ReadOnlySpan<StudioModel> Models(int bodyPart)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyPart);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bodyPart, Header.NumBodyParts);

        int at = Header.BodyPartIndex + (bodyPart * Unsafe.SizeOf<StudioBodyParts>());
        StudioBodyParts part = BodyParts()[bodyPart];
        return StudioReader.Run<StudioModel>(
            _bytes.Span, at + part.ModelIndex, part.NumModels, "models");
    }

    /// <summary>The meshes of one model.</summary>
    /// <param name="bodyPart">The body part's index.</param>
    /// <param name="model">The model's index within the body part.</param>
    /// <returns>A span over the file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either index is out of range.</exception>
    public ReadOnlySpan<StudioMesh> Meshes(int bodyPart, int model)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(model);
        ReadOnlySpan<StudioModel> models = Models(bodyPart);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(model, models.Length);

        int partAt = Header.BodyPartIndex + (bodyPart * Unsafe.SizeOf<StudioBodyParts>());
        int modelAt = partAt + BodyParts()[bodyPart].ModelIndex
            + (model * Unsafe.SizeOf<StudioModel>());

        return StudioReader.Run<StudioMesh>(
            _bytes.Span, modelAt + models[model].MeshIndex, models[model].NumMeshes, "meshes");
    }

    /// <summary>The model's texture references.</summary>
    /// <returns>A span over the file.</returns>
    public ReadOnlySpan<StudioTexture> Textures() =>
        StudioReader.Run<StudioTexture>(
            _bytes.Span, Header.TextureIndex, Header.NumTextures, "textures");

    /// <summary>One texture's material name.</summary>
    /// <param name="index">The texture's index.</param>
    /// <returns>The name, without a path or extension.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public string TextureName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Header.NumTextures);

        int at = Header.TextureIndex + (index * Unsafe.SizeOf<StudioTexture>());
        return StudioReader.StringAt(_bytes.Span, at + Textures()[index].NameIndex);
    }

    /// <summary>The model's material search paths.</summary>
    /// <returns>The paths, in the order the file lists them.</returns>
    public IReadOnlyList<string> MaterialSearchPaths()
    {
        ReadOnlySpan<byte> span = _bytes.Span;
        ReadOnlySpan<int> offsets = StudioReader.Run<int>(
            span, Header.CdTextureIndex, Header.NumCdTextures, "material search paths");

        List<string> paths = new(offsets.Length);
        foreach (int offset in offsets)
        {
            paths.Add(StudioReader.StringAt(span, offset));
        }

        return paths;
    }
}

/// <summary>
/// A studio model's <c>.vvd</c> file: the shared vertex and tangent blocks.
/// </summary>
public sealed class VvdFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private VvdFile(ReadOnlyMemory<byte> bytes, VertexFileHeader header)
    {
        _bytes = bytes;
        Header = header;
    }

    /// <summary>The file header.</summary>
    public VertexFileHeader Header { get; }

    /// <summary>The checksum that must match the MDL's.</summary>
    public int Checksum => Header.Checksum;

    /// <summary>How many vertices the file holds at LOD 0.</summary>
    public int VertexCount => Header.NumLodVertexes[0];

    /// <summary>Reads a VVD from a stream.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a VVD this port can read.</exception>
    public static async Task<VvdFile> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses a VVD already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a VVD this port can read.</exception>
    public static VvdFile Parse(ReadOnlyMemory<byte> bytes)
    {
        VertexFileHeader header =
            StudioReader.At<VertexFileHeader>(bytes.Span, 0, "the VVD header");

        if (header.Id == StudioIdents.VvdThin)
        {
            // IDCV. thinModelVertices_t is a separate,
            // quantised representation and this port does not read it; saying
            // so is better than reading 48-byte vertices out of a file that
            // does not hold them.
            throw new InvalidStudioException(
                "this is a thin VVD (IDCV); thinModelVertices_t is not ported");
        }

        if (header.Id != StudioIdents.Vvd)
        {
            throw new InvalidStudioException(
                $"the ident is 0x{header.Id:X8}, not IDSV");
        }

        if (header.Version != StudioIdents.VvdVersion)
        {
            throw new InvalidStudioException(
                $"VVD version {header.Version} is not {StudioIdents.VvdVersion}; "
                + "the reference loader refuses anything else");
        }

        return new VvdFile(bytes, header);
    }

    /// <summary>The fixup table.</summary>
    /// <returns>A span over the file, empty when the file has no fixups.</returns>
    public ReadOnlySpan<VertexFileFixup> Fixups() =>
        StudioReader.Run<VertexFileFixup>(
            _bytes.Span, Header.FixupTableStart, Header.NumFixups, "the fixup table");

    /// <summary>The raw vertex block, before any fixups are applied.</summary>
    /// <returns>A span over the file.</returns>
    /// <remarks>
    /// For a file with fixups this is NOT the vertex order the MDL indexes
    /// into. Use <see cref="VerticesForLod"/> unless you specifically want the
    /// file's own order.
    /// </remarks>
    public ReadOnlySpan<StudioVertex> RawVertices() =>
        StudioReader.Run<StudioVertex>(
            _bytes.Span, Header.VertexDataStart, VertexCount, "the vertex block");

    /// <summary>The raw tangent block, one <c>Vector4D</c> per vertex.</summary>
    /// <returns>A span over the file, empty when there is no tangent block.</returns>
    public ReadOnlySpan<FloatArray4> RawTangents() =>
        Header.TangentDataStart <= 0
            ? []
            : StudioReader.Run<FloatArray4>(
                _bytes.Span, Header.TangentDataStart, VertexCount, "the tangent block");

    /// <summary>
    /// The vertices of one LOD, with the fixup table applied.
    /// </summary>
    /// <param name="lod">The LOD level, 0 being the most detailed.</param>
    /// <returns>The vertices in the order the MDL's indices expect.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lod"/> is not a level of this file.</exception>
    /// <remarks>
    /// <para>
    /// The reference vertex loader walks the fixup table in order and copies
    /// each run whose <see cref="VertexFileFixup.Lod"/> is at
    /// least the wanted LOD. Runs for finer LODs than the one asked for are
    /// SKIPPED, which is how one file serves every level.
    /// </para>
    /// <para>
    /// A file with no fixups is already in order and is returned as is.
    /// </para>
    /// </remarks>
    public StudioVertex[] VerticesForLod(int lod)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lod, StudioIdents.MaxLods);

        ReadOnlySpan<StudioVertex> raw = StudioReader.Run<StudioVertex>(
            _bytes.Span, Header.VertexDataStart, Header.NumLodVertexes[0], "the vertex block");

        if (Header.NumFixups == 0)
        {
            return raw[..Header.NumLodVertexes[lod]].ToArray();
        }

        List<StudioVertex> result = new(Header.NumLodVertexes[lod]);
        foreach (VertexFileFixup fixup in Fixups())
        {
            if (fixup.Lod < lod)
            {
                continue;
            }

            if (fixup.SourceVertexId < 0 ||
                fixup.SourceVertexId + fixup.NumVertexes > raw.Length)
            {
                throw new InvalidStudioException(
                    $"a fixup names vertices {fixup.SourceVertexId}.."
                    + $"{fixup.SourceVertexId + fixup.NumVertexes} of {raw.Length}");
            }

            result.AddRange(raw.Slice(fixup.SourceVertexId, fixup.NumVertexes));
        }

        return [.. result];
    }
}

/// <summary>
/// A studio model's <c>.vtx</c> file: the strip groups a renderer draws.
/// </summary>
/// <remarks>
/// Every offset in a VTX is relative to the STRUCT that holds it, not to the
/// file, so each accessor here takes the parent's own file offset. That is why
/// they are methods rather than properties.
/// </remarks>
public sealed class VtxFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private VtxFile(ReadOnlyMemory<byte> bytes, VtxFileHeader header)
    {
        _bytes = bytes;
        Header = header;
    }

    /// <summary>The file header.</summary>
    public VtxFileHeader Header { get; }

    /// <summary>The checksum that must match the MDL's.</summary>
    public int Checksum => Header.CheckSum;

    /// <summary>Reads a VTX from a stream.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a VTX this port can read.</exception>
    public static async Task<VtxFile> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    /// <summary>Parses a VTX already in memory.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="InvalidStudioException">The bytes are not a VTX this port can read.</exception>
    public static VtxFile Parse(ReadOnlyMemory<byte> bytes)
    {
        VtxFileHeader header = StudioReader.At<VtxFileHeader>(bytes.Span, 0, "the VTX header");

        // A VTX has no ident at all -- the version IS the first four bytes
        // of the file, which is why a corrupt VTX is so much harder to
        // detect than a corrupt MDL.
        if (header.Version != StudioIdents.VtxVersion)
        {
            throw new InvalidStudioException(
                $"VTX version {header.Version} is not {StudioIdents.VtxVersion} "
                + "(the format's OPTIMIZED_MODEL_FILE_VERSION)");
        }

        return new VtxFile(bytes, header);
    }

    /// <summary>The body parts, with the file offset each was read from.</summary>
    /// <returns>One entry per body part.</returns>
    public IReadOnlyList<(int Offset, VtxBodyPartHeader Header)> BodyParts()
    {
        List<(int, VtxBodyPartHeader)> parts = new(Header.NumBodyParts);
        int size = Unsafe.SizeOf<VtxBodyPartHeader>();
        ReadOnlySpan<VtxBodyPartHeader> run = StudioReader.Run<VtxBodyPartHeader>(
            _bytes.Span, Header.BodyPartOffset, Header.NumBodyParts, "VTX body parts");

        for (int i = 0; i < run.Length; i++)
        {
            parts.Add((Header.BodyPartOffset + (i * size), run[i]));
        }

        return parts;
    }

    /// <summary>The models of one body part.</summary>
    /// <param name="bodyPartOffset">The body part's own file offset.</param>
    /// <param name="bodyPart">The body part header.</param>
    /// <returns>One entry per model, with its file offset.</returns>
    public IReadOnlyList<(int Offset, VtxModelHeader Header)> Models(
        int bodyPartOffset,
        VtxBodyPartHeader bodyPart)
    {
        int at = bodyPartOffset + bodyPart.ModelOffset;
        int size = Unsafe.SizeOf<VtxModelHeader>();
        ReadOnlySpan<VtxModelHeader> run = StudioReader.Run<VtxModelHeader>(
            _bytes.Span, at, bodyPart.NumModels, "VTX models");

        List<(int, VtxModelHeader)> models = new(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            models.Add((at + (i * size), run[i]));
        }

        return models;
    }

    /// <summary>The LODs of one model.</summary>
    /// <param name="modelOffset">The model's own file offset.</param>
    /// <param name="model">The model header.</param>
    /// <returns>One entry per LOD, with its file offset.</returns>
    public IReadOnlyList<(int Offset, VtxModelLodHeader Header)> Lods(
        int modelOffset,
        VtxModelHeader model)
    {
        int at = modelOffset + model.LodOffset;
        int size = Unsafe.SizeOf<VtxModelLodHeader>();
        ReadOnlySpan<VtxModelLodHeader> run = StudioReader.Run<VtxModelLodHeader>(
            _bytes.Span, at, model.NumLods, "VTX LODs");

        List<(int, VtxModelLodHeader)> lods = new(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            lods.Add((at + (i * size), run[i]));
        }

        return lods;
    }

    /// <summary>The meshes of one LOD.</summary>
    /// <param name="lodOffset">The LOD's own file offset.</param>
    /// <param name="lod">The LOD header.</param>
    /// <returns>One entry per mesh, with its file offset.</returns>
    public IReadOnlyList<(int Offset, VtxMeshHeader Header)> Meshes(
        int lodOffset,
        VtxModelLodHeader lod)
    {
        int at = lodOffset + lod.MeshOffset;
        int size = Unsafe.SizeOf<VtxMeshHeader>();
        ReadOnlySpan<VtxMeshHeader> run = StudioReader.Run<VtxMeshHeader>(
            _bytes.Span, at, lod.NumMeshes, "VTX meshes");

        List<(int, VtxMeshHeader)> meshes = new(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            meshes.Add((at + (i * size), run[i]));
        }

        return meshes;
    }

    /// <summary>The strip groups of one mesh.</summary>
    /// <param name="meshOffset">The mesh's own file offset.</param>
    /// <param name="mesh">The mesh header.</param>
    /// <returns>One entry per strip group, with its file offset.</returns>
    public IReadOnlyList<(int Offset, VtxStripGroupHeader Header)> StripGroups(
        int meshOffset,
        VtxMeshHeader mesh)
    {
        int at = meshOffset + mesh.StripGroupHeaderOffset;
        int size = Unsafe.SizeOf<VtxStripGroupHeader>();
        ReadOnlySpan<VtxStripGroupHeader> run = StudioReader.Run<VtxStripGroupHeader>(
            _bytes.Span, at, mesh.NumStripGroups, "VTX strip groups");

        List<(int, VtxStripGroupHeader)> groups = new(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            groups.Add((at + (i * size), run[i]));
        }

        return groups;
    }

    /// <summary>The vertices of one strip group.</summary>
    /// <param name="groupOffset">The group's own file offset.</param>
    /// <param name="group">The group header.</param>
    /// <returns>A span over the file.</returns>
    public ReadOnlySpan<VtxVertex> Vertices(int groupOffset, VtxStripGroupHeader group) =>
        StudioReader.Run<VtxVertex>(
            _bytes.Span, groupOffset + group.VertOffset, group.NumVerts, "VTX vertices");

    /// <summary>The indices of one strip group.</summary>
    /// <param name="groupOffset">The group's own file offset.</param>
    /// <param name="group">The group header.</param>
    /// <returns>A span over the file.</returns>
    public ReadOnlySpan<ushort> Indices(int groupOffset, VtxStripGroupHeader group) =>
        StudioReader.Run<ushort>(
            _bytes.Span, groupOffset + group.IndexOffset, group.NumIndices, "VTX indices");

    /// <summary>The strips of one strip group.</summary>
    /// <param name="groupOffset">The group's own file offset.</param>
    /// <param name="group">The group header.</param>
    /// <returns>A span over the file.</returns>
    public ReadOnlySpan<VtxStripHeader> Strips(int groupOffset, VtxStripGroupHeader group) =>
        StudioReader.Run<VtxStripHeader>(
            _bytes.Span, groupOffset + group.StripOffset, group.NumStrips, "VTX strips");

    /// <summary>
    /// How many triangles the whole file describes at one LOD.
    /// </summary>
    /// <param name="lod">The LOD level.</param>
    /// <returns>The triangle count.</returns>
    /// <remarks>
    /// A triangle LIST contributes <c>numIndices / 3</c> and a triangle STRIP
    /// contributes <c>numIndices - 2</c>. Counting every strip as a list is
    /// the standard way to undercount a model by roughly a third.
    /// </remarks>
    public int TriangleCount(int lod)
    {
        int triangles = 0;
        foreach ((int partOffset, VtxBodyPartHeader part) in BodyParts())
        {
            foreach ((int modelOffset, VtxModelHeader model) in Models(partOffset, part))
            {
                IReadOnlyList<(int Offset, VtxModelLodHeader Header)> lods =
                    Lods(modelOffset, model);
                if (lod >= lods.Count)
                {
                    continue;
                }

                (int lodOffset, VtxModelLodHeader lodHeader) = lods[lod];
                foreach ((int meshOffset, VtxMeshHeader mesh) in Meshes(lodOffset, lodHeader))
                {
                    foreach ((int groupOffset, VtxStripGroupHeader group)
                        in StripGroups(meshOffset, mesh))
                    {
                        foreach (VtxStripHeader strip in Strips(groupOffset, group))
                        {
                            triangles += ((VtxStripFlags)strip.Flags).HasFlag(VtxStripFlags.IsTriStrip)
                                ? Math.Max(0, strip.NumIndices - 2)
                                : strip.NumIndices / 3;
                        }
                    }
                }
            }
        }

        return triangles;
    }
}
