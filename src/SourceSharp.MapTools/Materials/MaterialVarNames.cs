namespace SourceSharp.MapTools.Materials;

/// <summary>
/// The VMT variables the two compilers name, spelled as they spell them.
/// </summary>
/// <remarks>
/// The compile-only <c>%</c> variables are not here; they are in
/// <see cref="MaterialCompileVars"/> with the bit each one sets.
/// </remarks>
public static class MaterialVarNames
{
    /// <summary>
    /// <c>$reflectivity</c> — an explicit override for what would otherwise be
    /// read out of the VTF header (<c>utilmatlib.cpp:112-127</c>).
    /// </summary>
    public const string Reflectivity = "$reflectivity";

    /// <summary>
    /// <c>$basetexture</c> — the representative texture, whose VTF header
    /// supplies the dimensions and the reflectivity.
    /// </summary>
    public const string BaseTexture = "$basetexture";

    /// <summary><c>$bumpmap</c>.</summary>
    public const string BumpMap = "$bumpmap";

    /// <summary><c>$normalmap</c>, which <c>Water</c> uses for its bump.</summary>
    public const string NormalMap = "$normalmap";

    /// <summary>
    /// <c>$nodiffusebumplighting</c>: non-zero suppresses bumped lightmaps even
    /// with a bump map present.
    /// </summary>
    public const string NoDiffuseBumpLighting = "$nodiffusebumplighting";

    /// <summary>
    /// <c>$translucent</c> — <c>MATERIAL_VAR_TRANSLUCENT</c>
    /// (<c>imaterial.h:377</c>).
    /// </summary>
    public const string Translucent = "$translucent";

    /// <summary>
    /// <c>$alphatest</c> — <c>MATERIAL_VAR_ALPHATEST</c>
    /// (<c>imaterial.h:364</c>).
    /// </summary>
    public const string AlphaTest = "$alphatest";

    /// <summary>
    /// <c>$alpha</c>: the material's alpha modulation. Below 1 the material
    /// system reports translucent whatever the flag says.
    /// </summary>
    public const string Alpha = "$alpha";

    /// <summary><c>$surfaceprop</c> (<c>textures.cpp:351</c>).</summary>
    public const string SurfaceProp = "$surfaceprop";

    /// <summary><c>$surfaceprop2</c> (<c>textures.cpp:374</c>).</summary>
    public const string SurfaceProp2 = "$surfaceprop2";

    /// <summary>
    /// <c>$subdivsize</c> (<c>vbsp/faces.cpp:1745</c>). A MATERIAL variable,
    /// not a command-line option.
    /// </summary>
    public const string SubdivSize = "$subdivsize";

    /// <summary><c>%detailtype</c> (<c>vbsp/detailobjects.cpp:858</c>).</summary>
    public const string DetailType = "%detailtype";

    /// <summary><c>$macro_texture</c> (<c>vbsp/writebsp.cpp:1180</c>).</summary>
    public const string MacroTexture = "$macro_texture";

    /// <summary><c>%chop</c> (<c>vrad/vrad.cpp:653</c>).</summary>
    public const string Chop = "%chop";
}
