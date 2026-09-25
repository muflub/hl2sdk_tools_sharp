using SourceSharp.MapFormats.Text;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// The material-system configuration a map compile runs under.
/// </summary>
/// <remarks>
/// Every default here is what <c>InitMaterialSystem</c> gives the compilers: a
/// default-constructed <c>MaterialSystem_Config_t</c> handed to
/// <c>OverrideConfig</c> (<c>utilmatlib.cpp:57-62</c>), and vbsp's own copy of
/// patch resolution rather than the engine's.
/// </remarks>
public sealed record MaterialFactsOptions
{
    /// <summary>The defaults a map compile runs with.</summary>
    public static MaterialFactsOptions Default { get; } = new();

    /// <summary>
    /// Which patch algorithm to apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VmtPatchDialect.Compiler"/>, because it is vbsp's copy of
    /// patch resolution — bugs included — that decides what reaches the BSP.
    /// </para>
    /// <para>
    /// With one honest caveat, recorded here rather than smoothed over.
    /// <c>utilmatlib.cpp:71</c> goes through
    /// <c>g_pMaterialSystem-&gt;FindMaterial</c>, which is the ENGINE's
    /// resolver; vbsp's own copy in <c>materialpatch.cpp</c> is what CREATES
    /// the patches vbsp writes into the pak, and what
    /// <c>GetValueFromMaterial</c> uses. So a compile really does run both. The
    /// two disagree only on a patch that carries <c>insert</c> and
    /// <c>replace</c> together, one that nests a block inside either, or a
    /// chain more than one level deep — see <see cref="VmtPatchDialect"/> —
    /// and a material of that shape should be read with both settings before
    /// anything is concluded from either.
    /// </para>
    /// </remarks>
    public VmtPatchDialect PatchDialect { get; init; } = VmtPatchDialect.Compiler;

    /// <summary>
    /// <c>g_pConfig-&gt;UseBumpmapping()</c>.
    /// </summary>
    /// <remarks>
    /// True: <c>MATSYS_VIDCFG_FLAGS_DISABLE_BUMPMAP</c> is clear in a default
    /// config (<c>materialsystem_config.h:62</c>). With this false no material
    /// asks for bumped lightmaps, whatever its bump map says.
    /// </remarks>
    public bool UseBumpmapping { get; init; } = true;

    /// <summary>
    /// The width a material with no readable base texture reports.
    /// </summary>
    /// <remarks>
    /// 128, from <c>utilmatlib.cpp:100</c>. The <c>#if 0</c> above it is the
    /// alternative Valve chose against: erroring out instead.
    /// </remarks>
    public int FallbackWidth { get; init; } = 128;

    /// <summary>
    /// The height a material with no readable base texture reports.
    /// </summary>
    public int FallbackHeight { get; init; } = 128;
}
