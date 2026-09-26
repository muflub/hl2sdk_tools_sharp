//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// The vrad state a <c>lights.rad</c> parse depends on.
/// </summary>
/// <param name="Hdr">
/// <c>g_bHDR</c>. Decides whether <c>hdr:</c> lines are kept and <c>ldr:</c>
/// ones dropped, and which half of an
/// eight-number value is used.
/// </param>
/// <param name="LightScale">
/// <c>lightscale</c>, the global <c>-scale</c> multiplier, applied to every
/// texlight LAST. Defaults to 1.
/// </param>
/// <param name="SourceFile">
/// Which file is being read. Recorded on each <see cref="TexLight"/> so a
/// later merge can tell "redefined in the same file" from "overridden by a
/// later file".
/// </param>
public sealed record RadLightOptions(
    bool Hdr = false,
    float LightScale = 1.0f,
    string SourceFile = "lights.rad")
{
    /// <summary>LDR, unscaled, from the global file.</summary>
    public static RadLightOptions Default { get; } = new();
}
