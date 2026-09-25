using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Ambient;

/// <summary>
/// Which of the two lighting universes a vrad pass is computing.
/// </summary>
/// <remarks>
/// <c>SetHDRMode</c> swaps four things at once --
/// the lightmap lump, the worldlight lump, and both ambient output lumps -- and
/// Swaps the face lump alongside. A pass that mixed them
/// would read HDR lightmaps at LDR face offsets and produce plausible garbage,
/// so the mode is one value threaded through rather than four independent
/// choices.
/// </remarks>
public enum LightingMode
{
    /// <summary>The low-dynamic-range pass.</summary>
    Ldr,

    /// <summary>The high-dynamic-range pass.</summary>
    Hdr,
}

/// <summary>
/// Everything leaf ambient reads out of a compiled map, gathered once.
/// </summary>
/// <remarks>
/// <para>
/// READ-ONLY, and that is the point rather than a convention. Stock reaches
/// twenty-odd file-scope arrays and one thread index from inside the innermost
/// sample loop, which is why its per-thread state (<c>s_DispTested</c>) had to
/// be an array indexed by worker. Gathering the inputs into one immutable object
/// makes the whole stage a pure function of it, and that is what lets the port
/// run leaves in any order, on any number of threads, or on a GPU, and get the
/// same bytes.
/// </para>
/// <para>
/// WHAT THIS DELIBERATELY DOES NOT HOLD: any output. The ambient samples, the
/// index and the lighting lump are the builder's return value, not fields
/// mutated in place. Stock accumulates into <c>g_LeafAmbientSamples</c>, a
/// global sized to <c>numleafs</c> and written by whichever worker drew the
/// leaf; there is no reason for the port to have a shared writable array at
/// all.
/// </para>
/// <para>
/// The spans are over the loaded map's own memory and do not copy it. The
/// object therefore lives no longer than the <see cref="BspData"/> it was built
/// from, which is the whole of one vrad pass.
/// </para>
/// </remarks>
public sealed class AmbientScene
{
    /// <summary>The map.</summary>
    private readonly BspData _bsp;

    /// <summary>The lightmap bytes for this mode.</summary>
    private readonly ReadOnlyMemory<byte> _lightData;

    /// <summary>The face lump for this mode.</summary>
    private readonly ReadOnlyMemory<byte> _faces;

    /// <summary>The worldlight lump for this mode.</summary>
    private readonly ReadOnlyMemory<byte> _worldLights;

    /// <summary>Gathers a map.</summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="mode">Which pass this is.</param>
    /// <param name="tracer">
    /// The surface tracer to use, already built over the same map's tree.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// The tracer is injected rather than built here because it is the seam a
    /// GPU implementation replaces: everything else in this class is lump
    /// bytes, and a GPU cannot help with those.
    /// </remarks>
    public AmbientScene(BspData bsp, LightingMode mode, AmbientRayTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(tracer);

        _bsp = bsp;
        Mode = mode;
        Tracer = tracer;

        _lightData = mode == LightingMode.Hdr
            ? bsp[BspLump.LightingHdr].Data
            : bsp[BspLump.Lighting].Data;

        // The HDR pass uses the HDR face lump, which carries its
        // own lightofs and styles; when the map has none, stock copies the LDR
        // faces into it, so falling back to the LDR lump is the same behaviour.
        ReadOnlyMemory<byte> hdrFaces = bsp[BspLump.FacesHdr].Data;
        _faces = mode == LightingMode.Hdr && !hdrFaces.IsEmpty
            ? hdrFaces
            : bsp[BspLump.Faces].Data;

        _worldLights = mode == LightingMode.Hdr
            ? bsp[BspLump.WorldLightsHdr].Data
            : bsp[BspLump.WorldLights].Data;

        // Copied once: the inner loop reads these per ray, and a span re-derived
        // from the lump each time costs a lookup, a length check and a cast.
        _planes = BspStructView.As<DPlane>(bsp[BspLump.Planes]).ToArray();
        _nodes = BspStructView.As<DNode>(bsp[BspLump.Nodes]).ToArray();
        _leaves = ReadLeaves(bsp);
        _faceArray = BspStructView.As<DFace>(_faces.Span).ToArray();
        _texInfo = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
        _texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
        _brushes = BspStructView.As<DBrush>(bsp[BspLump.Brushes]).ToArray();
        _brushSides = BspStructView.As<DBrushSide>(bsp[BspLump.BrushSides]).ToArray();
        _leafBrushes = BspStructView.As<ushort>(bsp[BspLump.LeafBrushes]).ToArray();

        _shades = new FaceShade[_faceArray.Length];
        _averages = new Vec3[_faceArray.Length * 4];
        BuildShades();

        Parents = new BspParents(Nodes, Leaves.Length);
    }

    private readonly DPlane[] _planes;
    private readonly DNode[] _nodes;
    private readonly DLeaf[] _leaves;
    private readonly DFace[] _faceArray;
    private readonly TexInfo[] _texInfo;
    private readonly DTexData[] _texData;
    private readonly DBrush[] _brushes;
    private readonly DBrushSide[] _brushSides;
    private readonly ushort[] _leafBrushes;
    private readonly FaceShade[] _shades;
    private readonly Vec3[] _averages;

    /// <summary>
    /// Gathers a map with its own tracer: the BSP walk plus the map's
    /// displacements.
    /// </summary>
    /// <param name="bsp">The compiled map.</param>
    /// <param name="mode">Which pass this is.</param>
    /// <returns>The scene.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    /// <remarks>
    /// The walk's geometry is read from <c>LUMP_FACES</c> in both modes: the
    /// HDR face lump differs only in <c>lightofs</c> and styles, which the walk
    /// never reads, and the scene reads those from the mode's own lump.
    /// </remarks>
    public static AmbientScene Create(BspData bsp, LightingMode mode)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        AmbientRayTracer tracer = new(BspTraceGeometry.Build(bsp), DispCollisionSet.Build(bsp));
        return new AmbientScene(bsp, mode, tracer);
    }

    /// <summary>
    /// The map's leaves at version 1, converting a version-0 lump
    /// (<c>dleaf_version_0_t</c>, which carries a per-leaf ambient cube the
    /// newer layout moved to lumps 55/56) field by field, as
    /// The reference implementation's leaf loader does.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The leaves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bsp"/> is null.</exception>
    public static DLeaf[] ReadLeaves(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        if (bsp[BspLump.Leafs].Version != 0)
        {
            return BspStructView.As<DLeaf>(bsp[BspLump.Leafs]).ToArray();
        }

        ReadOnlySpan<DLeafVersion0> old = BspStructView.As<DLeafVersion0>(bsp[BspLump.Leafs]);
        DLeaf[] leaves = new DLeaf[old.Length];
        for (int i = 0; i < old.Length; i++)
        {
            ref readonly DLeafVersion0 o = ref old[i];
            ref DLeaf n = ref leaves[i];
            n.Contents = o.Contents;
            n.Cluster = o.Cluster;
            n.AreaFlags = o.AreaFlags;
            n.Mins = o.Mins;
            n.Maxs = o.Maxs;
            n.FirstLeafFace = o.FirstLeafFace;
            n.NumLeafFaces = o.NumLeafFaces;
            n.FirstLeafBrush = o.FirstLeafBrush;
            n.NumLeafBrushes = o.NumLeafBrushes;
            n.LeafWaterDataId = o.LeafWaterDataId;
        }

        return leaves;
    }

    /// <summary>Which pass this is.</summary>
    public LightingMode Mode { get; }

    /// <summary>The BSP-walking surface tracer <c>CLightSurface</c> becomes, with displacements.</summary>
    public AmbientRayTracer Tracer { get; }

    /// <summary>The upward tree links.</summary>
    public BspParents Parents { get; }

    /// <summary>The map's planes.</summary>
    public ReadOnlySpan<DPlane> Planes => _planes;

    /// <summary>The map's nodes.</summary>
    public ReadOnlySpan<DNode> Nodes => _nodes;

    /// <summary>The map's leaves.</summary>
    public ReadOnlySpan<DLeaf> Leaves => _leaves;

    /// <summary>This mode's faces.</summary>
    public ReadOnlySpan<DFace> Faces => _faceArray;

    /// <summary>The map's texinfos.</summary>
    public ReadOnlySpan<TexInfo> TexInfo => _texInfo;

    /// <summary>The map's texdatas, which carry reflectivity.</summary>
    public ReadOnlySpan<DTexData> TexData => _texData;

    /// <summary>The map's brushes.</summary>
    public ReadOnlySpan<DBrush> Brushes => _brushes;

    /// <summary>The map's brush sides.</summary>
    public ReadOnlySpan<DBrushSide> BrushSides => _brushSides;

    /// <summary>The leaf-to-brush index.</summary>
    public ReadOnlySpan<ushort> LeafBrushes => _leafBrushes;

    /// <summary>This mode's world lights, as the lump holds them.</summary>
    public ReadOnlySpan<DWorldLight> WorldLights => BspStructView.As<DWorldLight>(_worldLights.Span);

    /// <summary>This mode's lightmap bytes.</summary>
    public ReadOnlySpan<byte> LightData => _lightData.Span;

    /// <summary>
    /// The lightmap samples at a byte offset into <see cref="LightData"/>.
    /// </summary>
    /// <param name="offset">The offset, from a face's <c>LightOfs</c>.</param>
    /// <param name="count">How many samples to view.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The run does not lie inside the lump.
    /// </exception>
    /// <remarks>
    /// Bounds-checked, where stock casts a raw pointer. A face whose
    /// <c>lightofs</c> points outside the lump is a mis-split HDR/LDR pass, and
    /// the symptom without this check is a plausible ambient cube built from
    /// whatever followed the lump.
    /// </remarks>
    public ReadOnlySpan<ColorRgbExp32> LightSamples(int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        const int SampleSize = 4;
        ReadOnlySpan<byte> all = LightData;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (long)offset + ((long)count * SampleSize), all.Length, nameof(count));

        return BspStructView.As<ColorRgbExp32>(all.Slice(offset, count * SampleSize));
    }

    /// <summary>A face's average light colour for one lightstyle slot.</summary>
    /// <param name="face">The face.</param>
    /// <param name="lightStyleIndex">Which of the face's four style slots.</param>
    /// <returns>The average sample.</returns>
    /// <remarks>
    /// <c>dface_AvgLightColor</c>: the averages live
    /// BEFORE the face's samples, at <c>lightofs - (n+1) * 4</c>.
    /// </remarks>
    public ColorRgbExp32 AverageLightColor(ref readonly DFace face, int lightStyleIndex) =>
        LightSamples(face.LightOfs - ((lightStyleIndex + 1) * 4), 1)[0];

    /// <summary>A texinfo's material reflectivity.</summary>
    /// <param name="texInfoIndex">Which texinfo.</param>
    /// <returns>The reflectivity, per channel.</returns>
    public Vec3 Reflectivity(int texInfoIndex) =>
        _texData[_texInfo[texInfoIndex].TexData].Reflectivity;

    /// <summary>What shading one face needs, gathered once.</summary>
    /// <param name="face">The face.</param>
    /// <returns>Its shading record.</returns>
    internal ref readonly FaceShade Shade(int face) => ref _shades[face];

    /// <summary>
    /// A face's style-slot average, already decoded and tinted:
    /// <c>TexLightToLinear</c> then <c>ComputeAmbientFromSurface</c>'s
    /// Reflectivity multiply. The same
    /// floats stock computes per hit, computed once.
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="slot">The style slot, below <see cref="FaceShade.StyleCount"/>.</param>
    /// <returns>The colour.</returns>
    internal Vec3 AverageTinted(int face, int slot) => _averages[(face * 4) + slot];

    /// <summary>Builds the per-face shading records.</summary>
    private void BuildShades()
    {
        ReadOnlySpan<byte> light = LightData;
        for (int f = 0; f < _faceArray.Length; f++)
        {
            ref readonly DFace face = ref _faceArray[f];
            ref FaceShade s = ref _shades[f];
            ref readonly TexInfo tex = ref _texInfo[face.TexInfo];

            s.LightOfs = face.LightOfs;
            s.Smax = face.LightmapTextureSizeInLuxels[0] + 1;
            s.Tmax = face.LightmapTextureSizeInLuxels[1] + 1;
            s.Sky = (tex.Flags & RayAmbientLighting.SurfSky) != 0;

            // SurfHasBumpedLightmaps.
            bool bumped = (tex.Flags & 0x0800) != 0 && (tex.Flags & RayAmbientLighting.SurfNoLight) == 0;
            s.Stride = s.Smax * s.Tmax * (bumped ? 4 : 1);
            s.Reflectivity = _texData[tex.TexData].Reflectivity;

            int count = 0;
            while (count < RayAmbientLighting.MaxLightmaps && face.Styles[count] != RayAmbientLighting.StyleUnused)
            {
                count++;
            }

            s.StyleCount = count;
            s.Style0 = face.Styles[0];
            s.Style1 = face.Styles[1];
            s.Style2 = face.Styles[2];
            s.Style3 = face.Styles[3];

            if (s.Sky)
            {
                continue;
            }

            for (int maps = 0; maps < count; maps++)
            {
                int offset = face.LightOfs - ((maps + 1) * 4);
                if (offset < 0 || offset + 4 > light.Length)
                {
                    // Stock reads whatever precedes the lump here; a face with
                    // styles and no lightmap is refused when it is HIT, not now.
                    s.BadAverages = true;
                    continue;
                }

                ColorRgbExp32 avg = BspStructView.As<ColorRgbExp32>(light.Slice(offset, 4))[0];
                Vec3 c = StockLightColor.TexLightToLinear(avg);
                Vec3 r = s.Reflectivity;
                _averages[(f * 4) + maps] = new Vec3(c.X * r.X, c.Y * r.Y, c.Z * r.Z);
            }
        }
    }
}

/// <summary>What <c>CalcRayAmbientLighting</c> reads off a face it hit.</summary>
internal struct FaceShade
{
    /// <summary><c>lightofs</c>, in bytes, or -1.</summary>
    public int LightOfs;

    /// <summary><c>m_LightmapTextureSizeInLuxels[0] + 1</c>.</summary>
    public int Smax;

    /// <summary><c>m_LightmapTextureSizeInLuxels[1] + 1</c>.</summary>
    public int Tmax;

    /// <summary>Samples from one style slot to the next.</summary>
    public int Stride;

    /// <summary>How many leading style slots are not 255.</summary>
    public int StyleCount;

    /// <summary>The texdata's reflectivity.</summary>
    public Vec3 Reflectivity;

    /// <summary>The four style numbers.</summary>
    public byte Style0, Style1, Style2, Style3;

    /// <summary><c>SURF_SKY</c>.</summary>
    public bool Sky;

    /// <summary>A style slot's average lies outside the lump.</summary>
    public bool BadAverages;

    /// <summary>One style number by slot.</summary>
    /// <param name="slot">0..3.</param>
    /// <returns>The style.</returns>
    public readonly byte Style(int slot) => slot switch
    {
        0 => Style0,
        1 => Style1,
        2 => Style2,
        _ => Style3,
    };
}