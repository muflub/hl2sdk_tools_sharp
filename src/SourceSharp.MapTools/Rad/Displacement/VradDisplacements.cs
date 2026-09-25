using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Disp;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// Every displacement of one vrad pass: stock's <c>CVRadDispMgr</c>
/// (the <c>StaticDispMgr</c> singleton) as an
/// explicit, immutable context.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Load"/> is <c>CVRadDispMgr::Init</c>:
/// <c>UnserializeDisps</c> -- here <see cref="DispLightingLoader.Load"/>, which
/// builds, creates and sews every core -- then one
/// <see cref="VradDispSurface"/> per displacement (<c>CVRADDispColl::Create</c>)
/// and the leaf index (<c>InsertDispIntoTree</c>).
/// </para>
/// </remarks>
public sealed class VradDisplacements
{
    private readonly VradDispSurface?[] _surfaces;

    private VradDisplacements(VradDispSurface?[] surfaces, DispLeafIndex leaves, IReadOnlyList<string> warnings)
    {
        _surfaces = surfaces;
        Leaves = leaves;
        Warnings = warnings;
    }

    /// <summary>
    /// One surface per LUMP_DISPINFO entry; null for an entry no valid face
    /// points at (stock's <c>Create</c> refuses it and its tree stays empty).
    /// </summary>
    public IReadOnlyList<VradDispSurface?> Surfaces => _surfaces;

    /// <summary><c>m_DispTrees.Size()</c>: the "N Displacements" line.</summary>
    public int Count => _surfaces.Length;

    /// <summary>The per-leaf index for <c>ClipRayToDispInLeaf</c>.</summary>
    public DispLeafIndex Leaves { get; }

    /// <summary>"Patch Sample Radius Clamped!" and the like, in displacement order.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The surface of a face, through its <c>dispinfo</c>; null for a brush face.</summary>
    /// <param name="geometry">The map.</param>
    /// <param name="faceNum">The face.</param>
    /// <returns>The displacement or null.</returns>
    public VradDispSurface? ForFace(LightGeometry geometry, int faceNum)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        int disp = geometry.Faces[faceNum].DispInfo;
        return disp >= 0 && disp < _surfaces.Length ? _surfaces[disp] : null;
    }

    /// <summary>
    /// <c>CVRadDispMgr::Init</c>: loads and builds every displacement.
    /// </summary>
    /// <param name="bsp">The map.</param>
    /// <param name="geometry">Its lighting geometry (texinfo, tree).</param>
    /// <param name="settings">The switches.</param>
    /// <returns>The displacements.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static VradDisplacements Load(BspData bsp, LightGeometry geometry, DirectLightingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(settings);

        CoreDispInfo[] cores = DispLightingLoader.Load(bsp, settings.Compliance);
        VradDispSurface?[] surfaces = new VradDispSurface?[cores.Length];
        List<string> warnings = [];
        for (int i = 0; i < cores.Length; i++)
        {
            CoreDispInfo core = cores[i];
            int face = core.Surface.Handle;
            if (face < 0 || face >= geometry.Faces.Length)
            {
                continue;
            }

            ref readonly TexInfo tex = ref geometry.TexInfos[geometry.Faces[face].TexInfo];
            VradDispSurface s = VradDispSurface.Create(core, tex, settings, settings.StockNormalise);
            if (s.PatchRadiusClamped)
            {
                warnings.Add("Patch Sample Radius Clamped!");
            }

            surfaces[i] = s;
        }

        return new VradDisplacements(surfaces, DispLeafIndex.Build(geometry, surfaces), warnings);
    }
}
