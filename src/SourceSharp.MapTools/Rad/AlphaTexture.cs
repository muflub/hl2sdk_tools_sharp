using SourceSharp.MapFormats.Assets;

namespace SourceSharp.MapTools.Rad;

/// <summary>
/// One <c>.vtf</c> reduced to its alpha channel, which is all a shadow needs.
/// </summary>
/// <remarks>
/// <para>
/// <c>alphatexture_t</c> (<c>src/utils/vrad/vradstaticprops.cpp:857-873</c>).
/// <c>LoadVTFRGB8888</c> (<c>:650-684</c>) converts the whole top mip to
/// <c>IMAGE_FORMAT_RGBA8888</c> and <c>InitFromRGB8888</c> (<c>:865-872</c>)
/// then keeps ONE byte per pixel out of the four -- the alpha -- and throws the
/// colour away. So a 2048x2048 texture costs 4MB to decode and 4MB to keep,
/// and nothing downstream can ask this type what colour anything was.
/// </para>
/// <para>
/// Immutable once built. Stock's struct is mutable and is written in two
/// places (<c>InitFromRGB8888</c>, then <c>allowBackface</c>/<c>clampU</c>/
/// <c>clampV</c> poked in by <c>FindOrLoadIfValid</c> at <c>:721-729</c>); here
/// those four values are constructor arguments, because there is no third
/// writer and a shared table of mutable texture records is a race waiting for
/// the day the prop loop is parallelised.
/// </para>
/// </remarks>
public sealed class AlphaTexture
{
    private readonly byte[] _alpha;

    /// <summary>Builds a texture from an alpha plane that is already extracted.</summary>
    /// <param name="width">The texture's width in pixels.</param>
    /// <param name="height">The texture's height in pixels.</param>
    /// <param name="alpha">
    /// <paramref name="width"/> times <paramref name="height"/> alpha bytes,
    /// row-major from the top-left. Copied.
    /// </param>
    /// <param name="allowBackface">
    /// <c>$nocull</c>: whether a ray that hits this triangle from behind still
    /// sees the texture (<c>vradstaticprops.cpp:722-726</c>).
    /// </param>
    /// <param name="clampU">
    /// <c>TEXTUREFLAGS_CLAMPS</c>, read and then IGNORED. See
    /// <see cref="ClampU"/>.
    /// </param>
    /// <param name="clampV">
    /// <c>TEXTUREFLAGS_CLAMPT</c>, read and then IGNORED. See
    /// <see cref="ClampV"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="alpha"/> is not exactly one byte per pixel.
    /// </exception>
    public AlphaTexture(
        int width,
        int height,
        ReadOnlySpan<byte> alpha,
        bool allowBackface = false,
        bool clampU = false,
        bool clampV = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (alpha.Length != width * height)
        {
            throw new ArgumentException(
                $"an {width}x{height} alpha plane is {width * height} bytes, not {alpha.Length}",
                nameof(alpha));
        }

        _alpha = alpha.ToArray();
        Width = width;
        Height = height;
        AllowBackface = allowBackface;
        ClampU = clampU;
        ClampV = clampV;
    }

    /// <summary>The texture's width in pixels.</summary>
    /// <remarks>
    /// Stock stores this as a <c>short</c> (<c>vradstaticprops.cpp:859</c>),
    /// which a 65536-wide texture would overflow. No shipping VTF is that wide
    /// and an <c>int</c> costs nothing, so this port does not reproduce it.
    /// </remarks>
    public int Width { get; }

    /// <summary>The texture's height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// <c>$nocull</c>: whether a backfacing hit still samples the texture.
    /// </summary>
    /// <remarks>
    /// Set from the VMT, not the VTF (<c>vradstaticprops.cpp:722-726</c>), and
    /// carried on the TEXTURE rather than the material entry -- so two
    /// materials sharing one base texture cannot disagree about it, and the
    /// last one loaded does not win either: only the FIRST load creates the
    /// entry, and stock's own comment there ("UNDONE: Support this? Do we need
    /// to emit two triangles?") says the feature was never finished.
    /// </remarks>
    public bool AllowBackface { get; }

    /// <summary>
    /// <c>TEXTUREFLAGS_CLAMPS</c> from the VTF header — READ AND IGNORED.
    /// </summary>
    /// <remarks>
    /// A DELIBERATE REPRODUCTION OF A STOCK DEFECT.
    /// <c>LoadVTFRGB8888</c> reads the flag (<c>vradstaticprops.cpp:669</c>)
    /// and <c>FindOrLoadIfValid</c> stores it (<c>:727</c>), but the only code
    /// that would act on it is inside the <c>#if 0</c> at <c>:844-849</c>:
    /// <c>SampleMaterial</c> takes the <c>#else</c> branch and wraps
    /// unconditionally. So a clamped alpha texture whose UVs run outside
    /// [0,1] tiles in stock's shadows and does not tile on screen. Kept
    /// because vrad's output is the thing being matched, not the thing being
    /// improved; pinned by
    /// <c>ShadowTextureListTests.AClampedTextureStillWrapsBecauseStockIgnoresTheFlag</c>.
    /// </remarks>
    public bool ClampU { get; }

    /// <summary>
    /// <c>TEXTUREFLAGS_CLAMPT</c> from the VTF header — READ AND IGNORED, for
    /// the same reason as <see cref="ClampU"/>.
    /// </summary>
    public bool ClampV { get; }

    /// <summary>The alpha plane, one byte per pixel, row-major.</summary>
    public ReadOnlySpan<byte> Alpha => _alpha;

    /// <summary>
    /// Extracts the alpha channel of an RGBA8888 image.
    /// </summary>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="rgba">Four bytes per pixel, row-major.</param>
    /// <param name="allowBackface"><c>$nocull</c>.</param>
    /// <param name="clampU"><c>TEXTUREFLAGS_CLAMPS</c>, ignored downstream.</param>
    /// <param name="clampV"><c>TEXTUREFLAGS_CLAMPT</c>, ignored downstream.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rgba"/> is not exactly four bytes per pixel.
    /// </exception>
    /// <remarks>
    /// <c>alphatexture_t::InitFromRGB8888</c>
    /// (<c>vradstaticprops.cpp:865-872</c>). Its name says RGB and its input is
    /// RGBA: byte 3 of each pixel is what it keeps.
    /// </remarks>
    public static AlphaTexture FromRgba8888(
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        bool allowBackface = false,
        bool clampU = false,
        bool clampV = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"an {width}x{height} RGBA8888 image is {width * height * 4} bytes, "
                + $"not {rgba.Length}",
                nameof(rgba));
        }

        byte[] alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = rgba[(i * 4) + 3];
        }

        return new AlphaTexture(width, height, alpha, allowBackface, clampU, clampV);
    }

    /// <summary>
    /// Decodes a VTF's top mip and keeps its alpha.
    /// </summary>
    /// <param name="texture">The parsed VTF.</param>
    /// <param name="allowBackface">
    /// <c>$nocull</c>, which comes from the MATERIAL and so cannot be read
    /// here.
    /// </param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texture"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The VTF's image format has no decoder. <c>LoadVTFRGB8888</c> answers
    /// the same case by returning NULL (<c>vradstaticprops.cpp:677-681</c>,
    /// <c>ConvertImageFormat</c> failing), which its caller reads as "no alpha
    /// shadows for this material"; <see cref="ShadowTextureList"/> catches this
    /// and does the same.
    /// </exception>
    /// <remarks>
    /// Mip 0, frame 0, face 0 — <c>ImageData(0, 0, 0, 0, 0, 0)</c>
    /// (<c>vradstaticprops.cpp:664</c>). The FULL-size mip, not a cheap one:
    /// the width and height stock then uses are
    /// <c>pTex-&gt;Width()</c>/<c>Height()</c>, so a coarser level would put
    /// the wrong number of texels behind those dimensions and every UV would
    /// land somewhere else.
    /// </remarks>
    public static AlphaTexture FromVtf(VtfFile texture, bool allowBackface = false)
    {
        ArgumentNullException.ThrowIfNull(texture);

        byte[] rgba = texture.DecodeToRgba8888(mip: 0, frame: 0, face: 0);
        VtfFlags flags = (VtfFlags)texture.Header.Flags;

        return FromRgba8888(
            texture.Width,
            texture.Height,
            rgba,
            allowBackface,
            clampU: (flags & VtfFlags.ClampS) != 0,
            clampV: (flags & VtfFlags.ClampT) != 0);
    }

    /// <summary>
    /// One alpha texel by its integer coordinates, with no wrapping.
    /// </summary>
    /// <param name="u">The column, 0 to <see cref="Width"/> minus one.</param>
    /// <param name="v">The row, 0 to <see cref="Height"/> minus one.</param>
    /// <returns>The alpha value, 0 to 255.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either coordinate is off the texture.</exception>
    /// <remarks>
    /// The raw read <c>ComputeCoverageForTriangle</c> does inside its box loop
    /// (<c>vradstaticprops.cpp:816</c>), where the coordinates have already
    /// been brought into range by the clamp at <c>:795-798</c>.
    /// </remarks>
    public byte Texel(int u, int v)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(u);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(u, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(v);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(v, Height);

        return _alpha[(v * Width) + u];
    }

    /// <summary>
    /// Samples the texture at a UV, the way <c>SampleMaterial</c> does.
    /// </summary>
    /// <param name="u">The u coordinate; 1.0 is the right edge.</param>
    /// <param name="v">The v coordinate; 1.0 is the bottom edge.</param>
    /// <returns>The alpha value, 0 to 255.</returns>
    /// <remarks>
    /// <para>
    /// <c>vradstaticprops.cpp:837-854</c>, exactly: multiply by the dimension
    /// (NOT by dimension minus one — that is what makes <c>u == 1.0</c> land
    /// back on column 0 rather than on the last column), round to nearest, and
    /// mask.
    /// </para>
    /// <para>
    /// The rounding is <c>RoundFloatToInt</c> (<c>mathlib.h:1175</c>), which on
    /// every platform this branch builds for is <c>_mm_cvtss_si32</c> — SSE's
    /// round-to-nearest with ties going to EVEN, not away from zero. .NET's
    /// <c>MathF.Round</c> defaults to the same tie rule, which is why it is
    /// spelled without an explicit mode here and pinned by a fact.
    /// </para>
    /// <para>
    /// The mask is the whole wrap: stock's comment at <c>:842</c> says "asume
    /// power of 2", and for a non-power-of-two texture <c>x &amp; (w - 1)</c>
    /// is not a modulo — it is some other in-range index. It cannot leave the
    /// array, because ANDing only clears bits, so the result is never above
    /// <c>w - 1</c>; it is simply the wrong texel, and this port returns stock's
    /// wrong texel. <see cref="ClampU"/> says why the clamp branch is dead.
    /// </para>
    /// </remarks>
    public byte Sample(float u, float v)
    {
        int texelU = (int)MathF.Round(u * Width);
        int texelV = (int)MathF.Round(v * Height);

        texelU &= Width - 1;
        texelV &= Height - 1;

        return _alpha[(texelV * Width) + texelU];
    }
}
