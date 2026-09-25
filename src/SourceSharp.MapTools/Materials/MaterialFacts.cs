using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// Everything <c>vbsp</c> and <c>vrad</c> read out of a material.
/// </summary>
/// <remarks>
/// <para>
/// <c>src/utils/common/utilmatlib.cpp</c> is 184 lines and is the ENTIRE
/// dependency both compilers have on the material system:
/// <c>FindMaterial</c>, one generic string getter, the shader name, the
/// dimensions, the reflectivity, and two shader questions. This type is the
/// answer to all of it, computed once per material with no material system
/// underneath — a VMT read, a VTF header read, and a table.
/// </para>
/// <para>
/// Built by <see cref="MaterialFactsReader"/>, which does the IO. Every field
/// here is a value already resolved: nothing on this type reads a file, so a
/// compile stage can hold a dictionary of these and never touch the disk in an
/// inner loop.
/// </para>
/// </remarks>
public sealed class MaterialFacts
{
    private readonly KeyValuesNode? _material;

    internal MaterialFacts(
        string name,
        KeyValuesNode? material,
        Vec3 reflectivity,
        bool reflectivityFromVar,
        int width,
        int height,
        bool hasPreviewImage,
        VPath? baseTexture,
        bool useBumpmapping)
    {
        Name = name;
        _material = material;
        Reflectivity = reflectivity;
        ReflectivityFromVar = reflectivityFromVar;
        Width = width;
        Height = height;
        HasPreviewImage = hasPreviewImage;
        BaseTexture = baseTexture;

        if (material is null)
        {
            ShaderName = string.Empty;
            return;
        }

        ShaderName = material.Name;

        MaterialCompileFlags compile = MaterialCompileFlags.None;
        foreach (MaterialCompileVar var in MaterialCompileVars.All)
        {
            if (MaterialCompileVars.IsTrue(material.GetString(var.Name)))
            {
                compile |= var.Flag;
            }
        }

        CompileFlags = compile;

        MaterialShaderRule rule = MaterialShaderTable.RuleFor(material.Name);
        NeedsLightmap = rule.NeedsLightmap(material);
        NeedsBumpedLightmaps = rule.NeedsBumpedLightmaps(material, useBumpmapping);
        Opacity = ComputeOpacity(material);
    }

    /// <summary>The material as it was asked for, without a path or extension.</summary>
    public string Name { get; }

    /// <summary>
    /// True when the material resolved to a VMT.
    /// </summary>
    /// <remarks>
    /// The <c>pFound</c> out-parameter of <c>FindMaterial</c>
    /// (<c>utilmatlib.cpp:71-85</c>): false means the material system returned
    /// its error material, and vbsp warns "Material not found!" and carries on
    /// with a texture that has no flags at all.
    /// </remarks>
    public bool Found => _material is not null;

    /// <summary>
    /// <c>GetMaterialShaderName</c>: the VMT's root key.
    /// </summary>
    /// <remarks>
    /// The empty string for a material that was not found. See
    /// <see cref="MaterialShaderTable"/> for how this differs from the real
    /// call, which reports the shader a <c>DEFINE_FALLBACK_SHADER</c> chain
    /// ended at.
    /// </remarks>
    public string ShaderName { get; }

    /// <summary>The <c>%</c> compile variables this material sets.</summary>
    public MaterialCompileFlags CompileFlags { get; }

    /// <summary>
    /// <c>GetMaterialReflectivity</c>: <c>$reflectivity</c> when the material
    /// has one, else the base texture's VTF header value.
    /// </summary>
    /// <remarks>
    /// This is what vbsp writes into <c>TEXDATA</c>
    /// (<c>vbsp/textures.cpp:442</c>), so it is directly checkable against a
    /// map stock compiled.
    /// </remarks>
    public Vec3 Reflectivity { get; }

    /// <summary>
    /// True when <see cref="Reflectivity"/> came from <c>$reflectivity</c>
    /// rather than from the VTF header.
    /// </summary>
    public bool ReflectivityFromVar { get; }

    /// <summary>
    /// <c>GetMaterialDimensions</c>' width, written into <c>TEXDATA</c>.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// <c>GetMaterialDimensions</c>' height, written into <c>TEXDATA</c>.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// True when a base-texture VTF supplied the dimensions.
    /// </summary>
    /// <remarks>
    /// False means <c>GetPreviewImageProperties</c> did not return
    /// <c>MATERIAL_PREVIEW_IMAGE_OK</c> and <c>utilmatlib.cpp:100-101</c>
    /// substituted 128 by 128 — which is then what lands in the BSP, so it is
    /// a real value and not a failure.
    /// </remarks>
    public bool HasPreviewImage { get; }

    /// <summary>
    /// The content path of the base texture the dimensions came from, or null.
    /// </summary>
    public VPath? BaseTexture { get; }

    /// <summary>
    /// <c>GetMaterialShaderPropertyBool(UTILMATLIB_NEEDS_LIGHTMAP)</c>.
    /// </summary>
    public bool NeedsLightmap { get; }

    /// <summary>
    /// <c>GetMaterialShaderPropertyBool(UTILMATLIB_NEEDS_BUMPED_LIGHTMAPS)</c>.
    /// </summary>
    public bool NeedsBumpedLightmaps { get; }

    /// <summary>
    /// <c>GetMaterialShaderPropertyInt(UTILMATLIB_OPACITY)</c>.
    /// </summary>
    public MaterialOpacity Opacity { get; }

    /// <summary><c>$surfaceprop</c>, or null when the material has none.</summary>
    public string? SurfaceProp => GetVar(MaterialVarNames.SurfaceProp);

    /// <summary><c>$surfaceprop2</c>, or null when the material has none.</summary>
    public string? SurfaceProp2 => GetVar(MaterialVarNames.SurfaceProp2);

    /// <summary>
    /// <c>$subdivsize</c> as a number, or 0 when absent.
    /// </summary>
    /// <remarks>
    /// <c>vbsp/faces.cpp:1745-1754</c> subdivides only when this is strictly
    /// greater than zero, so 0 and absent are the same answer — which is why
    /// this is a float and not a nullable one.
    /// </remarks>
    public float SubdivSize => MaterialVarValue.ToFloat(GetVar(MaterialVarNames.SubdivSize));

    /// <summary>
    /// <c>%detailtype</c>: which detail-object group this surface sprouts.
    /// </summary>
    public string? DetailType => GetVar(MaterialVarNames.DetailType);

    /// <summary><c>$macro_texture</c> (<c>vbsp/writebsp.cpp:1180</c>).</summary>
    public string? MacroTexture => GetVar(MaterialVarNames.MacroTexture);

    /// <summary>
    /// <c>%chop</c>: a per-material override for vrad's patch chop size.
    /// </summary>
    /// <remarks>
    /// Read here because the material carries it, but note that vrad in THIS
    /// drop does not: <c>vrad/vrad.cpp:644-663</c> is inside a <c>/* */</c>
    /// block, with a comment saying the dependency on the material system was
    /// not worth the file accesses. So a stock 2013 vrad ignores <c>%chop</c>
    /// entirely, and a port that honoured it would differ from stock.
    /// </remarks>
    public string? Chop => GetVar(MaterialVarNames.Chop);

    /// <summary>
    /// <c>GetMaterialVar</c>: a material variable's string value, or null.
    /// </summary>
    /// <param name="name">The variable name, matched without regard to case.</param>
    /// <returns>Its value, or null when the material does not define it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <remarks>
    /// Null and not the empty string, because every caller in vbsp tests the
    /// pointer — <c>utilmatlib.cpp:161-175</c> returns NULL for an undefined
    /// var and <c>textures.cpp</c> is written as
    /// <c>if ( (propVal = GetMaterialVar(...)) &amp;&amp; StringIsTrue(propVal) )</c>.
    /// </remarks>
    public string? GetVar(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _material?.GetString(name);
    }

    /// <summary>
    /// The resolved material: patches followed, so this root names the shader.
    /// </summary>
    /// <returns>The root section, or null when the material was not found.</returns>
    public KeyValuesNode? Material() => _material;

    private static MaterialOpacity ComputeOpacity(KeyValuesNode material)
    {
        // utilmatlib.cpp:147-158, in its order: translucent wins over
        // alpha-tested. Both are MaterialVarFlags_t, so both are read as ints
        // and NOT through vbsp's StringIsTrue.
        if (MaterialVarValue.IsFlagSet(material.GetString(MaterialVarNames.Translucent)) ||
            MaterialVarValue.ToFloat(material.GetString(MaterialVarNames.Alpha), 1f) < 1f)
        {
            return MaterialOpacity.Translucent;
        }

        return MaterialVarValue.IsFlagSet(material.GetString(MaterialVarNames.AlphaTest))
            ? MaterialOpacity.AlphaTest
            : MaterialOpacity.Opaque;
    }
}
