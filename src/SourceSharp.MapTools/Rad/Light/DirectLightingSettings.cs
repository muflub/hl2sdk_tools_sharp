//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// Every switch the patch and direct-lighting stages read, with stock's
/// defaults: the globals at the top of the reference implementation, gathered.
/// </summary>
/// <remarks>
/// <para>
/// Built from <see cref="VradOptions"/> by <see cref="FromVrad"/>. Four of
/// stock's switches have no <see cref="VradOptions"/> member yet --
/// <c>-scale</c>, <c>-dlight</c>, <c>-ambient</c> and <c>-notexscale</c> --
/// so they are settable here only, at stock's defaults.
/// </para>
/// </remarks>
public sealed record DirectLightingSettings
{
    /// <summary>Light the HDR pass (<c>g_bHDR</c>).</summary>
    public bool Hdr { get; init; }

    /// <summary>
    /// <c>numbounce</c>: 100 by default, and forced to 0 by a map with no
    /// visibility. Zero skips subdivision and patch
    /// light entirely.
    /// </summary>
    public int Bounces { get; init; } = 100;

    /// <summary><c>do_fast</c>.</summary>
    public bool Fast { get; init; }

    /// <summary><c>do_extra</c>: supersample high-gradient samples.</summary>
    public bool Supersample { get; init; } = true;

    /// <summary><c>extrapasses</c>: how many supersampling passes at most.</summary>
    public int ExtraPasses { get; init; } = 4;

    /// <summary><c>debug_extra</c>: paint the supersampling passes into the lightmap.</summary>
    public bool DebugExtra { get; init; }

    /// <summary><c>do_centersamples</c>.</summary>
    public bool CenterSamples { get; init; }

    /// <summary>
    /// <c>smoothing_threshold</c>, a COSINE. Default is stock's literal
    /// 0.7071067, which is not <c>(float)cos(45 degrees)</c> (0.70710677).
    /// </summary>
    public float SmoothingThreshold { get; init; } = LightConstants.DefaultSmoothingThreshold;

    /// <summary><c>maxchop</c>, in luxels.</summary>
    public float MaxChop { get; init; } = 4.0f;

    /// <summary><c>minchop</c>, in luxels (<c>-chop</c>).</summary>
    public float MinChop { get; init; } = 4.0f;

    /// <summary><c>lightscale</c> (<c>-scale</c>).</summary>
    public float LightScale { get; init; } = 1.0f;

    /// <summary><c>dlight_threshold</c> (<c>-dlight</c>): the texlight brightness that makes a surface light.</summary>
    public float DLightThreshold { get; init; } = 0.1f;

    /// <summary><c>ambient</c> (<c>-ambient r g b</c>): added to every style-0 sample.</summary>
    public Vec3 Ambient { get; init; }

    /// <summary><c>texscale</c>; false is <c>-notexscale</c>.</summary>
    public bool TexScale { get; init; } = true;

    /// <summary><c>g_flSkySampleScale</c> (<c>-extrasky</c>, <c>-final</c>).</summary>
    public float SkySampleScale { get; init; } = 1.0f;

    /// <summary>
    /// <c>g_SunAngularExtent</c> from <c>-softsun</c>, already a SINE; a
    /// <c>SunSpreadAngle</c> key on a light_environment overrides it.
    /// </summary>
    public float SunAngularExtent { get; init; }

    /// <summary><c>g_bNoSkyRecurse</c>.</summary>
    public bool NoSkyboxRecurse { get; init; }

    /// <summary><c>dlight_map</c> (<c>-dlightmap</c>): reserve a second lightmap for direct light.</summary>
    public bool SeparateDirectLightmap { get; init; }

    /// <summary>
    /// <c>-dispchop</c>(default 8): the tightest
    /// displacement patch, in luxel widths.
    /// </summary>
    public float DispChop { get; init; } = 8.0f;

    /// <summary>
    /// <c>-maxdisppatchradius</c>(default 1500): the
    /// ceiling on a displacement's patch radial radius.
    /// </summary>
    public float MaxDispPatchRadius { get; init; } = 1500.0f;

    /// <summary>
    /// <c>-maxdispsamplesize</c>(default 512):
    /// the ceiling on a displacement's luxel radial radius.
    /// </summary>
    public float MaxDispSampleSize { get; init; } = 512.0f;

    /// <summary>Which stock bugs to reproduce.</summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>
    /// True when normalises take stock's reciprocal-square-root path
    /// (<see cref="StockQuirk.VradVectorNormalise"/>).
    /// </summary>
    public bool StockNormalise => Compliance.Emulates(StockQuirk.VradVectorNormalise);

    /// <summary>Builds settings from the stage options.</summary>
    /// <param name="options">The vrad options.</param>
    /// <param name="hdr">Which pass: true for HDR.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="VradOptions.LuxelDensity"/> means a density below 1, which
    /// rewrites texinfo and every face's lightmap extents
    /// Before anything here is built; apply
    /// <see cref="Final.LuxelDensity.Apply"/> to the map first.
    /// </exception>
    public static DirectLightingSettings FromVrad(VradOptions options, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Stores 1/n for n > 1, so "-luxeldensity 2" is a
        // density of 0.5 and rewrites the map just as "-luxeldensity 0.5" does.
        if (Final.LuxelDensity.Effective(options.LuxelDensity) < 1.0f)
        {
            throw new NotSupportedException(
                "-luxeldensity rewrites texinfo and every face's lightmap extents "
                + "before the world is built: run Rad.Final.LuxelDensity.Apply "
                + "on the map first (Vrad.LightAsync does) and pass a density of 1 here");
        }

        // Vs: the DEFAULT is a literal, -smooth is a cosine
        // of the argument. 45 is taken to mean "not given".
        float smoothing = options.SmoothingAngleDegrees == 45.0f
            ? LightConstants.DefaultSmoothingThreshold
            : (float)Math.Cos(options.SmoothingAngleDegrees * (Math.PI / 180.0));

        return new DirectLightingSettings
        {
            Hdr = hdr,
            Bounces = options.Bounces,
            Fast = options.Fast,
            Supersample = options.Supersample,
            DebugExtra = options.DebugExtra,
            CenterSamples = options.CenterSamples,
            SmoothingThreshold = smoothing,
            MaxChop = options.MaxChop,
            MinChop = options.MinChop,
            SkySampleScale = options.SkySampleScale,
            LightScale = options.LightScale,
            DLightThreshold = options.DLightThreshold,
            Ambient = options.Ambient,
            TexScale = options.TexScale,

            // Sin((M_PI/180.0)*g), in double, narrowed.
            SunAngularExtent = options.SunAngularExtentDegrees == 0f
                ? 0f
                : (float)Math.Sin(Math.PI / 180.0 * options.SunAngularExtentDegrees),
            NoSkyboxRecurse = options.NoSkyboxRecurse,
            SeparateDirectLightmap = options.SeparateDirectLightmap,
            DispChop = options.DispChop,
            MaxDispPatchRadius = options.MaxDispPatchRadius,
            MaxDispSampleSize = options.MaxDispSampleSize,
            Compliance = options.Compliance,
        };
    }
}
