namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The vrad state a <c>lights.rad</c> parse depends on.
/// </summary>
/// <param name="Hdr">
/// <c>g_bHDR</c>. Decides whether <c>hdr:</c> lines are kept and <c>ldr:</c>
/// ones dropped (<c>src/utils/vrad/vrad.cpp:207-222</c>), and which half of an
/// eight-number value is used
/// (<c>src/utils/vrad/lightmap.cpp:1069-1079</c>).
/// </param>
/// <param name="LightScale">
/// <c>lightscale</c>, the global <c>-scale</c> multiplier
/// (<c>vrad.cpp:70,2707</c>), applied to every texlight LAST
/// (<c>lightmap.cpp:1115-1116</c>). Defaults to 1.
/// </param>
/// <param name="SourceFile">
/// Which file is being read. Recorded on each <see cref="TexLight"/> so a
/// later merge can tell "redefined in the same file" from "overridden by a
/// later file" (<c>vrad.cpp:264-277</c>).
/// </param>
public sealed record RadLightOptions(
    bool Hdr = false,
    float LightScale = 1.0f,
    string SourceFile = "lights.rad")
{
    /// <summary>LDR, unscaled, from the global file.</summary>
    public static RadLightOptions Default { get; } = new();
}
