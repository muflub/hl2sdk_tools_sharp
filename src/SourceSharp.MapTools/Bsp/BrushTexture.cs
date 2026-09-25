using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Bsp;

/// <summary>
/// One brush side's texture placement as the VMF spells it:
/// <c>brush_texture_t</c>.
/// </summary>
/// <remarks>
/// <para>
/// A struct, and copied by value, because stock copies it by value in the
/// places that matter: <c>side_brushtextures[nummapbrushsides] = pSideInfo-&gt;td</c>
/// Keeps a snapshot of the side's placement so an origin
/// brush found later in the same entity can rebuild the texinfo from it
/// And <c>MergeBrushSides</c> takes a local copy before
/// rotating the axes so the instance's own table is left
/// alone.
/// </para>
/// <para>
/// Stock's <c>rotate</c> field is carried even though the VMF never sets it:
/// nothing parses a <c>rotate</c> key, so on the v220+ path it is always zero
/// and only the pre-220 <c>.MAP</c> path in
/// <see cref="TextureBuilder.TexinfoForBrushTextureAsync"/> reads it. Dropping it
/// would quietly delete that path's only input.
/// </para>
/// </remarks>
public struct BrushTexture
{
    /// <summary>The texture's U axis in world space, from the VMF's <c>uaxis</c>.</summary>
    public Vec3 UAxis;

    /// <summary>The texture's V axis in world space, from the VMF's <c>vaxis</c>.</summary>
    public Vec3 VAxis;

    /// <summary>The U shift, the fourth number in <c>uaxis</c>.</summary>
    public float ShiftU;

    /// <summary>The V shift, the fourth number in <c>vaxis</c>.</summary>
    public float ShiftV;

    /// <summary>
    /// The texture rotation in degrees, for pre-220 <c>.MAP</c> files only.
    /// </summary>
    public float Rotate;

    /// <summary>World units per texel along U, the number after <c>uaxis</c>.</summary>
    public float TextureWorldUnitsPerTexelU;

    /// <summary>World units per texel along V, the number after <c>vaxis</c>.</summary>
    public float TextureWorldUnitsPerTexelV;

    /// <summary>
    /// World units per luxel, from the side's <c>lightmapscale</c> key.
    /// </summary>
    /// <remarks>
    /// Zero when the side has no <c>lightmapscale</c>, because
    /// <c>FindMiptex</c> initialises <c>textureref[].lightmapWorldUnitsPerLuxel</c>
    /// to <c>0.0f</c> and never writes it again — the
    /// only writer in the whole compiler is the key handler at
    /// <see cref="TextureBuilder"/> divides by this with no
    /// guard, exactly as does.
    /// </remarks>
    public float LightmapWorldUnitsPerLuxel;

    /// <summary>The material name, as the VMF spelled it.</summary>
    /// <remarks>
    /// Never lowercased and never normalised here: this string is what reaches
    /// TEXDATA_STRING_DATA through
    /// <see cref="TexDataTable.FindOrCreateAsync"/>, so its casing is in the file.
    /// </remarks>
    public string Name;

    /// <summary>The <c>SURF_*</c> flags the side carries.</summary>
    public int Flags;
}
