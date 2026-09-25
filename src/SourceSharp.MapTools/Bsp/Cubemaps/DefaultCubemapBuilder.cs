using System.Buffers;
using System.Buffers.Binary;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Cubemaps;

/// <summary>
/// <c>Cubemap_CreateDefaultCubemaps</c>:
/// the placeholder cubemap VTFs written into the pak so an env_cubemap reads
/// as something other than the pink checkerboard until <c>buildcubemaps</c> runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT STOCK ACTUALLY WRITES IS BLACK.</b> The copy loop at
/// Does <c>memset(pDstBits, 0, iSize); continue;</c>
/// before any of the face-copying code ("Set this to black until HDR cubemaps
/// are built properly!"), so every cube face of every mip is zero and the
/// skybox's pixels are never read. What the skybox still decides is the
/// file's SHAPE: face 0's format and frame count, the union of the six faces'
/// flags, and whether the six faces agree well enough for any file to be
/// written at all (<c>LoadSrcVTFFiles</c>). That is all this
/// reads, and it writes the file stock's VTF library would serialise for a
/// zero cube: a 7.4 header with one resource, seven faces (the six plus the
/// sphere map), 32x32 and six mips.
/// </para>
/// <para>
/// <b>The LDR sphere map is black too</b>: the cube is converted to RGBA8888,
/// <c>GenerateSpheremap</c> computes the sphere map from black faces, and the
/// result is converted back. <b>The HDR sphere map is NOT reproducible</b>:
/// with a float format <c>GenerateSpheremap</c> skips
/// <c>ComputeSpheremapFrame</c> and mips a buffer
/// it never initialised (<c>new unsigned char[]</c>) into face
/// 6, so stock's bytes there are heap garbage. This writes zeros, and the
/// stock gate compares faces 0-5 of the HDR file exactly and excludes face 6.
/// </para>
/// <para>
/// <b>Flags</b> are the union of the six source faces, plus
/// <c>TEXTUREFLAGS_ENVMAP</c>; for the LDR file the two
/// <c>ConvertImageFormat</c> round trips then
/// rewrite the alpha flags from the formats' alpha bit counts
/// </para>
/// <para>
/// UNVERIFIED: a block-compressed skybox (DXT). The LDR data is compressed
/// back to that format from black; this writes zero blocks, which decode to
/// black but may not be the bytes stock's compressor emits. Every skybox the
/// catalogue uses is BGR888.
/// </para>
/// </remarks>
public static class DefaultCubemapBuilder
{
    /// <summary><c>DEFAULT_CUBEMAP_SIZE</c>.</summary>
    public const int Size = 32;

    /// <summary><c>TEXTUREFLAGS_ONEBITALPHA</c>.</summary>
    public const uint OneBitAlpha = 0x1000;

    /// <summary><c>TEXTUREFLAGS_EIGHTBITALPHA</c>.</summary>
    public const uint EightBitAlpha = 0x2000;

    /// <summary><c>TEXTUREFLAGS_ENVMAP</c>.</summary>
    public const uint EnvMap = 0x4000;

    /// <summary><c>CUBEMAP_FACE_COUNT</c>: six faces and the sphere map.</summary>
    public const int FaceCount = 7;

    /// <summary>The skybox face suffixes, in cube-face order.</summary>
    public static IReadOnlyList<string> FaceSuffixes { get; } = ["rt", "lf", "bk", "ft", "up", "dn"];

    /// <summary>
    /// Writes the LDR set then the HDR set, as
    /// <c>Cubemap_CreateDefaultCubemaps</c> does.
    /// </summary>
    /// <param name="skyName">
    /// worldspawn's <c>skyname</c>; null when the map has no worldspawn, which
    /// stock prints as <c>(null)</c>.
    /// </param>
    /// <param name="mapBase">The map name.</param>
    /// <param name="defaultCubemapNames">
    /// <see cref="CubemapFixups.DefaultCubemapNames"/>, in order.
    /// </param>
    /// <param name="materials">The compile's material cache.</param>
    /// <param name="content">Where the skybox VTFs are read from.</param>
    /// <param name="pak">The pak to write into.</param>
    /// <param name="diagnostics">Receives the warnings stock prints.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task.</returns>
    public static async Task CreateAsync(
        string? skyName,
        string mapBase,
        IReadOnlyList<string> defaultCubemapNames,
        MaterialFactsCache materials,
        IContentFileSystem content,
        MapPakFile pak,
        ICollection<CompileDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        foreach (bool hdr in new[] { false, true })
        {
            byte[]? vtf = await BuildAsync(skyName, hdr, materials, content, diagnostics, cancellationToken)
                .ConfigureAwait(false);

            if (vtf is null)
            {
                continue;
            }

            Write(vtf, hdr, mapBase, defaultCubemapNames, pak);
        }
    }

    /// <summary>
    /// One default cubemap: <c>CreateDefaultCubemaps(bHDR)</c> up to
    /// <c>Serialize</c>.
    /// </summary>
    /// <param name="skyName">worldspawn's <c>skyname</c>, or null.</param>
    /// <param name="hdr">True for the HDR file.</param>
    /// <param name="materials">The material cache.</param>
    /// <param name="content">Where the VTFs are read from.</param>
    /// <param name="diagnostics">Receives the warnings.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The VTF bytes, or null when stock writes nothing.</returns>
    public static async Task<byte[]?> BuildAsync(
        string? skyName,
        bool hdr,
        MaterialFactsCache materials,
        IContentFileSystem content,
        ICollection<CompileDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string skybox = $"skybox/{skyName ?? "(null)"}";

        VtfHeader[] faces = new VtfHeader[FaceSuffixes.Count];
        uint unionFlags = 0;

        for (int i = 0; i < FaceSuffixes.Count; i++)
        {
            MaterialFacts facts = await materials.GetAsync(skybox + FaceSuffixes[i], cancellationToken)
                .ConfigureAwait(false);

            // FindMaterial then FindVar("$basetexture"): a missing material or
            // var yields a name that reads no file.
            string? baseTexture = facts.Material()?.GetString(MaterialVarNames.BaseTexture);
            VtfHeader? header = baseTexture is null
                ? null
                : await ReadHeaderAsync(content, $"materials/{baseTexture}.vtf", cancellationToken).ConfigureAwait(false);

            if (header is null)
            {
                if (baseTexture is not null && await ExistsAsync(content, $"materials/{baseTexture}.vtf", cancellationToken).ConfigureAwait(false))
                {
                    diagnostics.Add(new CompileDiagnostic(
                        SurfaceContentDiagnostics.DefaultCubemapSkyboxUnreadable,
                        DiagnosticSeverity.Warning,
                        $"*** Error unserializing skybox texture: {skybox}"));
                }

                diagnostics.Add(SkyboxMissing(skybox));
                return null;
            }

            faces[i] = header.Value;
            unionFlags |= header.Value.Flags;

            // Face 0 may be half height (a side texture)
            // and any face may be 4x4; flags must agree except for alpha.
            uint noAlpha = header.Value.Flags & ~(EightBitAlpha | OneBitAlpha);
            uint firstNoAlpha = faces[0].Flags & ~(EightBitAlpha | OneBitAlpha);
            bool widthBad = header.Value.Width != faces[0].Width && header.Value.Width != 4;
            bool heightBad = header.Value.Height != faces[0].Height &&
                             header.Value.Height != faces[0].Height * 2 &&
                             header.Value.Height != 4;

            if (widthBad || heightBad || noAlpha != firstNoAlpha)
            {
                diagnostics.Add(new CompileDiagnostic(
                    SurfaceContentDiagnostics.DefaultCubemapSkyboxMismatch,
                    DiagnosticSeverity.Warning,
                    $"*** Error: Skybox vtf files for {skybox} weren't compiled with the same size texture and/or same flags!"));
                diagnostics.Add(SkyboxMissing(skybox));
                return null;
            }
        }

        // HDR converts every source face to RGBA16161616F first
        // And the destination takes face 0's format.
        ImageFormat format = hdr ? ImageFormat.Rgba16161616F : (ImageFormat)faces[0].ImageFormat;
        uint flags = unionFlags | EnvMap;
        // ConvertImageFormat to a format the texture is already in returns
        // before touching the flags.
        if (!hdr && format != ImageFormat.Rgba8888)
        {
            flags = AfterConversion(flags, ImageFormat.Rgba8888);
            flags = AfterConversion(flags, format);
        }

        int frames = faces[0].NumFrames;
        return Serialize(format, flags, frames);
    }

    /// <summary>
    /// <c>CVTFTexture::ConvertImageFormat</c>'s flag rewrite
    /// A conversion to the format it is already
    /// in returns early and changes nothing — the caller decides that.
    /// </summary>
    /// <param name="flags">The flags before.</param>
    /// <param name="format">The format converted to.</param>
    /// <returns>The flags after.</returns>
    public static uint AfterConversion(uint flags, ImageFormat format)
    {
        if (!ImageFormatInfo.IsCompressed(format))
        {
            int alphaBits = AlphaBits(format);
            if (alphaBits > 1)
            {
                flags |= EightBitAlpha;
                flags &= ~OneBitAlpha;
            }

            if (alphaBits <= 1)
            {
                flags &= ~EightBitAlpha;
                if (alphaBits == 0)
                {
                    flags &= ~OneBitAlpha;
                }
            }

            return flags;
        }

        return format is ImageFormat.Dxt1 ? flags & ~(OneBitAlpha | EightBitAlpha) : flags;
    }

    /// <summary>
    /// <c>m_NumAlphaBits</c> from the image format table
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>Its alpha bits.</returns>
    public static int AlphaBits(ImageFormat format) => format switch
    {
        ImageFormat.Rgba8888 or ImageFormat.Abgr8888 or ImageFormat.Ia88 or ImageFormat.A8
            or ImageFormat.Argb8888 or ImageFormat.Bgra8888 or ImageFormat.Dxt3 or ImageFormat.Dxt5
            or ImageFormat.Uvwq8888 or ImageFormat.Uvlx8888 => 8,
        ImageFormat.Bgra4444 => 4,
        ImageFormat.Bgra5551 => 1,
        ImageFormat.Rgba16161616F or ImageFormat.Rgba16161616 => 16,
        ImageFormat.Rgba32323232F => 32,
        _ => 0,
    };

    /// <summary>
    /// <c>CVTFTexture::Serialize</c> of a
    /// <see cref="Size"/>-square, all-zero cube map with a sphere map.
    /// </summary>
    /// <param name="format">The image format.</param>
    /// <param name="flags">The texture flags.</param>
    /// <param name="frames">The frame count.</param>
    /// <returns>The file.</returns>
    public static byte[] Serialize(ImageFormat format, uint flags, int frames)
    {
        // ImageLoader::GetNumMipMapLevels(32, 32, 1): 32,16,8,4,2,1.
        int mips = 0;
        for (int d = Size; d >= 1; d >>= 1)
        {
            mips++;
        }

        int imageBytes = 0;
        for (int mip = 0; mip < mips; mip++)
        {
            int dim = Math.Max(1, Size >> mip);
            imageBytes += ImageFormatInfo.SizeInBytes(format, dim, dim) * frames * FaceCount;
        }

        const int headerSize = 80;
        const int resourceSize = 8;
        byte[] file = new byte[headerSize + resourceSize + imageBytes];
        Span<byte> s = file;

        // VTFFileHeader_t, memset to zero first.
        "VTF\0"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], VtfFile.MajorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], VtfFile.MinorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], headerSize + resourceSize);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], Size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)frames);
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], 0);          // startFrame

        // Reflectivity (1,1,1) and bump scale 1: the constructor's values
        // Nothing on this path computes reflectivity.
        BinaryPrimitives.WriteSingleLittleEndian(s[32..], 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(s[36..], 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(s[40..], 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(s[48..], 1.0f);
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], (int)format);
        s[56] = (byte)mips;
        BinaryPrimitives.WriteInt32LittleEndian(s[57..], (int)ImageFormat.Unknown); // no low-res image
        s[61] = 0;
        s[62] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(s[63..], 1);         // depth
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 1);         // numResources

        // The one resource: the image, whose data starts after the dictionary.
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], VtfResourceType.Image);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], headerSize + resourceSize);

        return file;
    }

    /// <summary>
    /// The pak half of <c>CreateDefaultCubemaps</c>:
    /// <c>cubemapdefault[.hdr].vtf</c>, then one copy per referenced cubemap
    /// not already in the pak.
    /// </summary>
    /// <param name="vtf">The file.</param>
    /// <param name="hdr">True for the HDR set.</param>
    /// <param name="mapBase">The map name.</param>
    /// <param name="defaultCubemapNames">The per-cubemap names.</param>
    /// <param name="pak">The pak.</param>
    public static void Write(byte[] vtf, bool hdr, string mapBase, IReadOnlyList<string> defaultCubemapNames, MapPakFile pak)
    {
        ArgumentNullException.ThrowIfNull(vtf);
        ArgumentNullException.ThrowIfNull(mapBase);
        ArgumentNullException.ThrowIfNull(defaultCubemapNames);
        ArgumentNullException.ThrowIfNull(pak);

        pak.Add(hdr ? $"materials/maps/{mapBase}/cubemapdefault.hdr.vtf" : $"materials/maps/{mapBase}/cubemapdefault.vtf",
                vtf, textMode: false);

        foreach (string name in defaultCubemapNames)
        {
            string target = HdrName(name, hdr);
            if (!pak.Contains(target))
            {
                pak.Add(target, vtf, textMode: false);
            }
        }
    }

    /// <summary>
    /// <c>VTFNameToHDRVTFName</c>: from the FIRST
    /// <c>.vtf</c> (any case) on, the name becomes <c>.hdr.vtf</c>.
    /// </summary>
    /// <param name="name">The VTF name.</param>
    /// <param name="hdr">False returns the name unchanged.</param>
    /// <returns>The name.</returns>
    public static string HdrName(string name, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!hdr)
        {
            return name;
        }

        int at = name.IndexOf(".vtf", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? name : name[..at] + ".hdr.vtf";
    }

    private static CompileDiagnostic SkyboxMissing(string skybox) => new(
        SurfaceContentDiagnostics.DefaultCubemapSkyboxMissing,
        DiagnosticSeverity.Warning,
        $"Can't load skybox file {skybox} to build the default cubemap!");

    private static async ValueTask<VtfHeader?> ReadHeaderAsync(IContentFileSystem content, string path, CancellationToken cancellationToken)
    {
        if (!VPath.TryCreate(path, out VPath vpath) || vpath.IsEmpty)
        {
            return null;
        }

        using IMemoryOwner<byte>? owner = await content.ReadAsync(vpath, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return null;
        }

        try
        {
            return VtfFile.Parse(owner.Memory.ToArray()).Header;
        }
        catch (InvalidVtfException)
        {
            return null;
        }
    }

    private static async ValueTask<bool> ExistsAsync(IContentFileSystem content, string path, CancellationToken cancellationToken) =>
        VPath.TryCreate(path, out VPath vpath) && !vpath.IsEmpty &&
        await content.ResolveAsync(vpath, cancellationToken).ConfigureAwait(false) is not null;
}
