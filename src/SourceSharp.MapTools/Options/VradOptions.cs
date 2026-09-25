namespace SourceSharp.MapTools.Options;

/// <summary>
/// Which lighting range or ranges a vrad compile produces.
/// </summary>
/// <remarks>
/// <para>
/// Stock has no such option, because stock cannot express it. The DLL has one
/// global mode, set by <c>SetHDRMode</c> (<c>bsplib.cpp:3823</c>), defaulted to
/// LDR at <c>vrad.cpp:2387</c> and flipped by <c>-hdr</c>
/// (<c>vrad.cpp:2624</c>) or <c>-ldr</c> (<c>vrad.cpp:2628</c>). To get both,
/// <c>vrad_launcher</c> scans for <c>-both</c> at
/// <c>vrad_launcher.cpp:68</c> and runs the ENTIRE DLL twice, which recomputes
/// the geometry, the KD-tree, the patch subdivision and the transfer matrix
/// from scratch for the second range -- everything except the light colours is
/// identical work done twice.
/// </para>
/// <para>
/// plan_maptools.md 4p shares all of that between the two passes, so
/// <see cref="VradLightingRange.Both"/> is a mode of one compile and not two runs. That is why
/// this is an option on <see cref="VradOptions"/> and not a loop in a CLI.
/// </para>
/// </remarks>
public enum VradLightingRange
{
    /// <summary>
    /// Low dynamic range only. Stock's default, <c>SetHDRMode(false)</c> at
    /// <c>vrad.cpp:2387</c>, and stock's <c>-ldr</c>.
    /// </summary>
    Ldr,

    /// <summary>High dynamic range only. Stock's <c>-hdr</c>.</summary>
    Hdr,

    /// <summary>
    /// Both ranges from one compile, sharing every range-independent result.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-both</c>, which is not a vrad flag at all but a
    /// <c>vrad_launcher</c> flag that runs the DLL twice. The parser here
    /// accepts the same spelling and produces this single value.
    /// </remarks>
    Both,
}

/// <summary>
/// What vrad was asked to do. Defaults are stock's defaults.
/// </summary>
/// <remarks>
/// <para>
/// A typed immutable record rather than an <c>argv</c>, for the same reasons as
/// <see cref="VbspOptions"/>. The stock-spelling parser is
/// <see cref="StockArgs.ParseVrad"/>.
/// </para>
/// <para>
/// Every default is the initialiser of stock's corresponding global, cited on
/// the option. Where stock stores a TRANSFORM of what was typed -- a cosine for
/// <c>-smooth</c>, a sine for <c>-softsun</c>, a reciprocal for
/// <c>-luxeldensity</c> -- the option here holds what was typed and the stage
/// applies the transform, so that a value read back is the value asked for.
/// </para>
/// <para>
/// <b>There is deliberately no thread count here</b>, exactly as on
/// <see cref="VbspOptions"/>. Unlike vbsp, stock vrad really does use
/// <c>numthreads</c> (<c>vrad.cpp:2465</c>); but parallelism in this port is
/// <see cref="SourceSharp.MapTools.Parallel.CompileParallelism"/>, taken uniformly by every stage,
/// so a per-tool thread count would be a second answer to the same question.
/// The parser accepts <c>-threads n</c> and says so in a diagnostic.
/// </para>
/// <para>
/// Options stock parses but this port drops, with reasons from
/// plan_maptools.md 9. The parser ACCEPTS each of these and reports a
/// diagnostic rather than failing:
/// <list type="bullet">
/// <item><description>
/// Everything VMPI: the <c>-mpi</c> prefix branch at <c>vrad.cpp:2774</c>,
/// which is inside <c>#ifdef MPI</c> and so is not even compiled in this tree,
/// plus <c>-mpi_pw</c> and <c>-mpi_ListParams</c> from the usage text.
/// Windows-only cluster mode.
/// </description></item>
/// <item><description>
/// <c>-dump</c> (<c>vrad.cpp:2415</c>), <c>-dumpnormals</c>
/// (<c>vrad.cpp:2427</c>), <c>-dumptrace</c> (<c>vrad.cpp:2431</c>) and
/// <c>-dumppropmaps</c> (<c>vrad.cpp:2439</c>): debug writes of intermediate
/// state to files beside the map. <c>-dump</c> is also stock vrad's only
/// case-SENSITIVE flag -- <c>strcmp</c>, not <c>Q_stricmp</c> -- which is a
/// typo, not a contract.
/// </description></item>
/// <item><description>
/// <c>-loghash</c> (<c>vrad.cpp:2568</c>), which writes the sample hash table
/// to <c>samplehash.txt</c>; and <c>-StopOnExit</c> (<c>vrad.cpp:2602</c>),
/// which waits for a keypress, in a library.
/// </description></item>
/// <item><description>
/// <c>-low</c> (<c>vrad.cpp:2564</c>), <c>-FullMinidumps</c>
/// (<c>vrad.cpp:2620</c>), <c>-novconfig</c> (<c>vrad.cpp:2613</c>),
/// <c>-allowdebug</c>/<c>-steam</c> (<c>vrad.cpp:2606,2609</c>): process
/// priority and launcher plumbing; the last three are already no-ops inside
/// stock's own parse loop.
/// </description></item>
/// <item><description>
/// <c>-incremental</c> and the <c>.r0</c> files of
/// <c>src/utils/vrad/incremental.cpp</c>. Note that SDK 2013's parse loop does
/// not contain an <c>-incremental</c> branch at all, so the file is already
/// unreachable from the command line. The IDEA -- reuse the previous compile's
/// light contributions when only some lights moved -- is kept and generalised
/// in plan_maptools.md 10a; the file format is not.
/// </description></item>
/// <item><description>
/// Hammer's in-editor <c>IVRadDLL</c> (<c>src/utils/vrad/vraddll.cpp</c>) and
/// the <c>g_bInterrupt</c> flag it drives. A library that returns a value does
/// not need an interface for being told to stop; cancellation is a
/// <c>CancellationToken</c>.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Six more are not in a release build: <c>-scale</c>, <c>-ambient</c>,
/// <c>-dlight</c>, <c>-sky</c>, <c>-notexscale</c> and <c>-coring</c> all sit
/// inside <c>#if ALLOWDEBUGOPTIONS</c> (<c>vrad.cpp:2702</c>), and
/// <c>vrad.cpp:23</c> defines that as <c>(0 || _DEBUG)</c>. The four whose
/// variables the lighting reads are options here (<see cref="LightScale"/>,
/// <see cref="Ambient"/>, <see cref="DLightThreshold"/>, <see cref="TexScale"/>)
/// and the parser accepts their debug-build spellings; <c>-sky</c> and
/// <c>-coring</c> set <c>indirect_sun</c> and <c>coring</c>, which nothing in
/// this drop reads, and stay unknown, as in a release vrad.
/// </para>
/// <para>
/// Two spellings that sound like stock options are not in this tree at all, and
/// so have nothing to model: <c>-extra</c> (there is only <c>-noextra</c>,
/// which turns OFF the supersampling that is on by default, and
/// <c>-extrasky</c>, which is a ray multiplier) and
/// <c>-staticproplightingfinal</c>, which appears nowhere in
/// <c>src/utils</c>.
/// </para>
/// </remarks>
public sealed record VradOptions
{
    /// <summary>Which lighting range or ranges to produce.</summary>
    /// <remarks>
    /// Stock's <c>-ldr</c> / <c>-hdr</c> / <c>-both</c>. Defaults to
    /// <see cref="VradLightingRange.Ldr"/>, which is what
    /// <c>vrad.cpp:2387</c>'s <c>SetHDRMode(false)</c> does before parsing
    /// anything.
    /// </remarks>
    public VradLightingRange Range { get; init; } = VradLightingRange.Ldr;

    /// <summary>Emit the per-stage commentary stock's <c>-v</c> emits.</summary>
    /// <remarks>Stock's <c>-v</c> or <c>-verbose</c>, <c>vrad.cpp:2461</c>. Default false.</remarks>
    public bool Verbose { get; init; }

    /// <summary>How many radiosity bounces to gather.</summary>
    /// <remarks>
    /// Stock's <c>-bounce</c>, <c>vrad.cpp:2443</c>. Default 100, from
    /// <c>unsigned numbounce = 100;</c> at <c>vrad.cpp:51</c> -- whose trailing
    /// comment records that it was once 25 and originally 8. Stock refuses a
    /// negative value; zero is legal and means direct light only.
    /// </remarks>
    public int Bounces { get; init; } = 100;

    /// <summary>Quick and dirty lighting.</summary>
    /// <remarks>
    /// Stock's <c>-fast</c>, <c>vrad.cpp:2506</c>, <c>do_fast</c> false at
    /// <c>vrad.cpp:102</c>. The iteration setting: what a designer uses while
    /// building, with <see cref="Range"/> left at LDR.
    /// </remarks>
    public bool Fast { get; init; }

    /// <summary>
    /// How many times the standard number of rays to trace for indirect light
    /// and sky ambient.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-extrasky n</c>, <c>vrad.cpp:2518</c>, default 1.0 at
    /// <c>vrad.cpp:89</c>. Stock's <c>-final</c> (<c>vrad.cpp:2514</c>) is
    /// exactly <c>-extrasky 16</c> and sets nothing else, which its own usage
    /// text at <c>vrad.cpp:2823</c> confirms: "equivalent to -extrasky 16".
    /// There is therefore no separate <c>Final</c> flag here -- it would be a
    /// second name for one number -- and the parser maps <c>-final</c> onto
    /// this.
    /// </remarks>
    public float SkySampleScale { get; init; } = 1.0f;

    /// <summary>Supersample lightmaps.</summary>
    /// <remarks>
    /// Stock has no flag to turn this ON, only <c>-noextra</c> to turn it off
    /// (<c>vrad.cpp:2494</c>); <c>do_extra</c> is true at <c>vrad.cpp:100</c>.
    /// Modelled in the positive sense, defaulting true, so the default of the
    /// record is the default of stock and a reader does not have to hold a
    /// double negative in their head.
    /// </remarks>
    public bool Supersample { get; init; } = true;

    /// <summary>
    /// Bake the supersampling pattern into the lightmaps so it can be seen
    /// in-game.
    /// </summary>
    /// <remarks>Stock's <c>-debugextra</c>, <c>vrad.cpp:2498</c>, false at <c>vrad.cpp:101</c>.</remarks>
    public bool DebugExtra { get; init; }

    /// <summary>Sample per-leaf ambient at lower quality to save time.</summary>
    /// <remarks>Stock's <c>-fastambient</c>, <c>vrad.cpp:2502</c>, false at <c>vrad.cpp:61</c>.</remarks>
    public bool FastAmbient { get; init; }

    /// <summary>
    /// The angle, in degrees, below which adjacent faces are smoothed into one
    /// lighting group.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-smooth</c>, <c>vrad.cpp:2534</c>. Default 45, which is what
    /// <c>vrad.cpp:105</c> stores pre-cosined:
    /// <c>smoothing_threshold = 0.7071067</c> with the comment
    /// <c>cos(45.0*(M_PI/180))</c>. Stock converts on the way in; this holds
    /// the degrees and leaves the cosine to the stage.
    /// </remarks>
    public float SmoothingAngleDegrees { get; init; } = 45.0f;

    /// <summary>Move lightmap sample points to face centres.</summary>
    /// <remarks>Stock's <c>-centersamples</c>, <c>vrad.cpp:2530</c>, false at <c>vrad.cpp:103</c>.</remarks>
    public bool CenterSamples { get; init; }

    /// <summary>
    /// Put direct lighting in a different lightmap from the radiosity result.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-dlightmap</c>, <c>vrad.cpp:2546</c>, <c>dlight_map</c> 0 at
    /// <c>vrad.cpp:109</c>. An <c>int</c> in stock that only ever holds 0 or 1,
    /// so a bool here.
    /// </remarks>
    public bool SeparateDirectLightmap { get; init; }

    /// <summary>Rescale every luxel on the map by this factor.</summary>
    /// <remarks>
    /// Stock's <c>-luxeldensity</c>, <c>vrad.cpp:2550</c>, default 1.0 at
    /// <c>vrad.cpp:111</c>. Stock RECIPROCATES a value greater than 1 --
    /// <c>if (luxeldensity &gt; 1.0) luxeldensity = 1.0 / luxeldensity;</c> --
    /// while its usage text at <c>vrad.cpp:2857-2858</c> claims a value above 1.0
    /// "will be ignored". Neither is true of the other: 2.0 is silently turned
    /// into 0.5, not ignored. The option holds what was typed and the stage
    /// reciprocates, so the contradiction is visible instead of buried.
    /// </remarks>
    public float LuxelDensity { get; init; } = 1.0f;

    /// <summary>
    /// The sun's angular diameter in degrees, which softens its shadows.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-softsun</c>, <c>vrad.cpp:2576</c>, default 0 at
    /// <c>vrad.cpp:87</c>: a point sun with hard shadows. Stock stores
    /// <c>sin(radians(n))</c> in the same global it parsed the degrees into;
    /// this holds the degrees. Stock's usage suggests 0 to 5.
    /// </remarks>
    public float SunAngularExtentDegrees { get; init; }

    /// <summary>Do not recurse into the 3D skybox for shadows on the world.</summary>
    /// <remarks>Stock's <c>-noskyboxrecurse</c>, <c>vrad.cpp:2510</c>, false at <c>vrad.cpp:62</c>.</remarks>
    public bool NoSkyboxRecurse { get; init; }

    /// <summary>Bake per-vertex lighting into static props.</summary>
    /// <remarks>Stock's <c>-StaticPropLighting</c>, <c>vrad.cpp:2391</c>, false at <c>vrad.cpp:118</c>.</remarks>
    public bool StaticPropLighting { get; init; }

    /// <summary>Shadow-test static props at polygon precision, not bounding box.</summary>
    /// <remarks>Stock's <c>-StaticPropPolys</c>, <c>vrad.cpp:2403</c>, false at <c>vrad.cpp:119</c>.</remarks>
    public bool StaticPropPolys { get; init; }

    /// <summary>Light only static props, directly, and nothing else.</summary>
    /// <remarks>
    /// Stock's <c>-OnlyStaticProps</c>, <c>vrad.cpp:2399</c>, false at
    /// <c>vrad.cpp:93</c>. A debug mode, as stock's usage says.
    /// </remarks>
    public bool OnlyStaticProps { get; init; }

    /// <summary>Render static props' normals instead of lighting them.</summary>
    /// <remarks>
    /// Stock's <c>-StaticPropNormals</c>, <c>vrad.cpp:2395</c>, false at
    /// <c>vrad.cpp:94</c>. A debug mode that changes the output lightmaps, so
    /// it is an option and not a dump.
    /// </remarks>
    public bool StaticPropNormals { get; init; }

    /// <summary>Turn off static props shadowing themselves, everywhere.</summary>
    /// <remarks>Stock's <c>-nossprops</c>, <c>vrad.cpp:2407</c>, <c>g_bDisablePropSelfShadowing</c> false at <c>vrad.cpp:121</c>.</remarks>
    public bool DisablePropSelfShadowing { get; init; }

    /// <summary>
    /// Let a material's alpha channel block light, sampling the texture where a
    /// ray crosses it.
    /// </summary>
    /// <remarks>Stock's <c>-textureshadows</c>, <c>vrad.cpp:2411</c>, false at <c>vrad.cpp:120</c>.</remarks>
    public bool TextureShadows { get; init; }

    /// <summary>Do not light detail props.</summary>
    /// <remarks>Stock's <c>-nodetaillight</c>, <c>vrad.cpp:2419</c>, false at <c>vrad.cpp:116</c>.</remarks>
    public bool NoDetailLighting { get; init; }

    /// <summary>Light only detail props and per-leaf ambient.</summary>
    /// <remarks>
    /// Stock's <c>-onlydetail</c>, <c>vrad.cpp:2572</c>. Not a global in stock
    /// at all: it is an out-parameter of <c>ParseCommandLine</c>
    /// (<c>vrad.cpp:2380</c>) initialised to false at <c>vrad.cpp:2382</c>.
    /// </remarks>
    public bool OnlyDetail { get; init; }

    /// <summary>Show lighting errors as red instead of turning them black.</summary>
    /// <remarks>
    /// Stock's <c>-rederrors</c>, <c>vrad.cpp:2423</c>. The stock global is
    /// inverted -- <c>bRed2Black</c>, true at <c>vrad.cpp:60</c>, and the flag
    /// clears it -- so false here means stock's default of "turn red errors
    /// black". Stock's usage text spells the flag <c>-rederror</c>, singular,
    /// at <c>vrad.cpp:2829</c>; the parser matches the plural, which is what
    /// the code matches.
    /// </remarks>
    public bool ShowErrorsInRed { get; init; }

    /// <summary>
    /// Gather bounced light for displacements across a wider area, at a cost in
    /// compile time.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-LargeDispSampleRadius</c>, <c>vrad.cpp:2435</c>, false at
    /// <c>vrad.cpp:91</c>. The fix for splotchy bounced light on terrain.
    /// </remarks>
    public bool LargeDispSampleRadius { get; init; }

    /// <summary>
    /// The coarsest patch size, in luxel widths, allowed in a face's interior.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-maxchop</c>, <c>vrad.cpp:2632</c>, default 4 at
    /// <c>vrad.cpp:53</c>. Stock refuses a value below 1.
    /// </remarks>
    public float MaxChop { get; init; } = 4.0f;

    /// <summary>
    /// The tightest patch size, in luxel widths, used along face edges.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-chop</c> -- note the name does not match the global, which
    /// is <c>minchop</c> (<c>vrad.cpp:54</c>, default 4). Stock refuses a value
    /// below 1 and then clamps the result down to <see cref="MaxChop"/> with
    /// <c>minchop = min(minchop, maxchop)</c> at <c>vrad.cpp:2659</c>, so the
    /// two flags are ORDER-DEPENDENT on the command line: <c>-chop 8 -maxchop
    /// 2</c> leaves min at 8, while <c>-maxchop 2 -chop 8</c> leaves it at 2.
    /// The parser reproduces that order dependence exactly, because a host
    /// pasting a working Hammer command line must get the compile it has been
    /// getting.
    /// </remarks>
    public float MinChop { get; init; } = 4.0f;

    /// <summary>Patch size, in luxel widths, for displacement surfaces.</summary>
    /// <remarks>
    /// Stock's <c>-dispchop</c>, <c>vrad.cpp:2667</c>, default 8.0 at
    /// <c>vrad.cpp:55</c>. Stock refuses a value below 1 -- and misspells the
    /// flag as <c>-dipschop</c> in that error message, at <c>vrad.cpp:2674</c>.
    /// </remarks>
    public float DispChop { get; init; } = 8.0f;

    /// <summary>The largest radius, in world units, allowed for a displacement patch.</summary>
    /// <remarks>
    /// Stock's <c>-disppatchradius</c>, <c>vrad.cpp:2684</c>, default 1500.0 at
    /// <c>vrad.cpp:56</c>. Stock refuses a value below 10.
    /// </remarks>
    public float MaxDispPatchRadius { get; init; } = 1500.0f;

    /// <summary>The largest displacement lighting sample, in world units.</summary>
    /// <remarks>
    /// Stock's <c>-maxdispsamplesize</c>, <c>vrad.cpp:2590</c>, default 512.0
    /// at <c>vrad_dispcoll.cpp:18</c> -- which its usage text at
    /// <c>vrad.cpp:2861</c> agrees with. Stock validates nothing here.
    /// </remarks>
    public float MaxDispSampleSize { get; init; } = 512.0f;

    /// <summary>
    /// An extra lights file to load alongside <c>lights.rad</c> and the level's
    /// own, or null for none.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-lights &lt;file&gt;</c>, <c>vrad.cpp:2482</c>, empty at
    /// <c>vrad.cpp:78</c> (<c>designer_lights</c>). A path the compile READS,
    /// so it is resolved through <see cref="SourceSharp.MapTools.Io.IContentFileSystem"/> and not
    /// with the process's working directory.
    /// </remarks>
    public string? LightsFile { get; init; }

    /// <summary>
    /// <c>lightscale</c>: a factor on every light's intensity, texlights
    /// included.
    /// </summary>
    /// <remarks>Stock's <c>-scale &lt;n&gt;</c>, <c>vrad.cpp:2703</c>; default 1.0 (<c>vrad.cpp:70</c>).</remarks>
    public float LightScale { get; init; } = 1.0f;

    /// <summary>
    /// <c>dlight_threshold</c>: a texlight patch dimmer than this emits no
    /// direct light of its own.
    /// </summary>
    /// <remarks>Stock's <c>-dlight &lt;n&gt;</c>, <c>vrad.cpp:2729</c>; default 0.1 (<c>vrad.cpp:71</c>).</remarks>
    public float DLightThreshold { get; init; } = 0.1f;

    /// <summary>
    /// <c>ambient</c>: a flat light added to every sample, in vrad's internal
    /// scale.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-ambient r g b</c>, <c>vrad.cpp:2715</c>, which MULTIPLIES
    /// each argument by 128 on the way in; this holds the product, so
    /// <c>-ambient 1 1 1</c> is (128, 128, 128). Default zero
    /// (<c>vrad.cpp:68</c>). A map without vis overrides it with 0.1, unscaled
    /// (<c>vrad.cpp:2247</c>).
    /// </remarks>
    public SourceSharp.MapFormats.Geometry.Vec3 Ambient { get; init; }

    /// <summary>
    /// <c>texscale</c>: whether a patch's chop size follows its texture's
    /// scale.
    /// </summary>
    /// <remarks>Stock's <c>-notexscale</c> clears it, <c>vrad.cpp:2753</c>; default true (<c>vrad.cpp:108</c>).</remarks>
    public bool TexScale { get; init; } = true;

    // ---------------------------------------------------------------------
    // tools++ (ZHLT-flavored) vrad family -- plan_toolspp_support.md T5.
    //
    // These are ++ features, not stock defects, so per the plan's ground
    // rule 0 they are feature members here and never StockQuirk members.
    // Their defaults are the ++ binary's .data initialisers, cited per
    // option, so an unset record reproduces today's bytes exactly.
    // vradplusplus.exe addresses: .data base 0x140106000 (file off
    // 0x104400); decompilations cite ~/re/toolsplusplus/decompilations/
    // vrad/all.c lines.
    // ---------------------------------------------------------------------

    /// <summary>Ambient-occlusion sampling for a prop's direct-light gather.</summary>
    /// <remarks>
    /// ++'s <c>-ambientocclusion</c> / <c>-ao</c> (alias), registrar
    /// <c>all.c:43471</c> / <c>all.c:43482</c>, both writing the byte at
    /// <c>0x1417194f9</c>. Consumed by the prop direct-light gather at
    /// <c>all.c:6951</c>, <c>all.c:7345</c> and the prop indirect gather at
    /// <c>all.c:46396</c>, where it replaces the fixed sample counts with
    /// the AO sample knobs. Default false (uninitialised image region).
    /// </remarks>
    public bool AmbientOcclusion { get; init; }

    /// <summary>Radius, in world units, of the ambient-occlusion hemisphere trace.</summary>
    /// <remarks>
    /// ++'s <c>-aoradius</c>, registrar <c>all.c:43485</c>, default 40.0 at
    /// <c>.data 0x1401062d8</c>. The gather consumers above scale the trace
    /// end to this radius instead of <c>MAX_TRACE_LENGTH</c>.
    /// </remarks>
    public float AoRadius { get; init; } = 40.0f;

    /// <summary>Multiplier on the ambient-occlusion weight.</summary>
    /// <remarks>
    /// ++'s <c>-aoscale</c>, registrar <c>all.c:43488</c> ("Ambient Occlusion
    /// intensity multiplier"), default 0.5 at <c>.data 0x1401062d4</c>.
    /// </remarks>
    public float AoScale { get; init; } = 0.5f;

    /// <summary>Ambient-occlusion sample count for brush-face gathers.</summary>
    /// <remarks>
    /// ++'s <c>-aofacesamples</c>, registrar <c>all.c:43491</c>, default 32 at
    /// <c>.data 0x1401062dc</c>; the usage text adds "(128 on -final, 16 on
    /// -fast)", which the <c>-final</c> and <c>-fast</c> presets set
    /// explicitly.
    /// </remarks>
    public int AoFaceSamples { get; init; } = 32;

    /// <summary>Ambient-occlusion sample count for static-prop gathers.</summary>
    /// <remarks>
    /// ++'s <c>-aopropsamples</c>, registrar <c>all.c:43495</c>, default 16 at
    /// <c>.data 0x1401062e0</c>; usage text "(32 on -final, 12 on -fast)".
    /// </remarks>
    public int AoPropSamples { get; init; } = 16;

    /// <summary>Visualize ambient occlusion only, by disabling all lights.</summary>
    /// <remarks>
    /// ++'s <c>-aodebug</c>, a callback registrar at <c>all.c:43498</c> whose
    /// callback (<c>vradplusplus.exe</c> disasm <c>0x14003b140</c>) does
    /// exactly three writes: AO on (<c>0x1417194f9 = 1</c>), an AO-debug
    /// byte (<c>0x1417194fa = 1</c>), and a zero at <c>0x1401062b8</c>.
    /// That last byte is the SAME global <c>-noextra</c> registers
    /// (<c>all.c:43312</c>, the extra/supersample-enable byte, initial
    /// <c>01 00 00 00</c> at <c>.data 0x1401062b8</c> per objdump) — not the
    /// <c>-scale</c> lightscale global, which lives at <c>0x1401062a0</c>
    /// (<c>all.c:43383-43384</c>, initial <c>0000803f</c> = 1.0f). So the
    /// tri-write means: AO on, AO-debug on, supersampling off. "Disables
    /// all lights" is the binary's own usage wording; the pinned bytes are
    /// these three. Modeled as <see cref="AmbientOcclusion"/> and
    /// <see cref="AoDebug"/> set and <see cref="Supersample"/> cleared.
    /// </remarks>
    public bool AoDebug { get; init; }

    /// <summary>
    /// Scale factor on the sample counts of the non-SSE prop gathers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ++'s <c>-StaticPropSampleScale</c>, registrar <c>all.c:435e5-call</c>
    /// at <c>0x14003a5d1</c> (float parser), stored at <c>.data
    /// 0x1401062ac</c>, default 1.0. Consumed by the three prop gather
    /// sample-count sites: <c>all.c:6951</c> (sun disk, base 7),
    /// <c>all.c:7345</c> (ambient sky, base 40) and <c>all.c:8793</c>
    /// (detail-prop indirect, base 30).
    /// </para>
    /// <para>
    /// The decomp says the multiplication applies only on the fast path
    /// (each site picks <c>extrasoft * base</c>, 30/162, when the global is
    /// unset); with <c>extrasoft = 1</c> those ARE the stock counts, so the
    /// scale is what the fast branch multiplies, and at the default 1.0
    /// every count is today's count.
    /// </para>
    /// </remarks>
    public float StaticPropSampleScale { get; init; } = 1.0f;

    /// <summary>Which falloff model the prop indirect gather weights samples by.</summary>
    /// <remarks>
    /// <para>
    /// ++'s <c>-StaticPropIndirectMode</c>, int registrar at
    /// <c>0x14003a5a4</c>, stored at <c>0x1417194ec</c>, default 0. The one
    /// consumer is the prop indirect gatherer <c>FUN_14003ecb0</c>
    /// (<c>ComputeIndirectLightingAtPoint</c> counterpart,
    /// <c>vraddetailprops.cpp:658</c>), whose weighting branches at
    /// <c>all.c:46468</c> (<c>== 0</c>), <c>46478</c> (<c>== 1</c>),
    /// <c>46496</c> (<c>== 2</c>).
    /// </para>
    /// <para>
    /// 0 (default) keeps stock's inverse-square of the traced fraction.
    /// 1 -- the ++ description calls it TF2-flavored -- weights by the
    /// inverse square of the TRUE hit distance (accumulated hit point minus
    /// sample point, over 128): <c>1/(1+|hit-pos|²/128²)</c>.
    /// 2 -- Orangebox-flavored -- drops the inverse-square entirely:
    /// weight 1 times the surface lightmap times reflectivity.
    /// A value outside 0..2 takes none of the branches, so it contributes
    /// nothing at all; the parser reproduces that by accepting any int.
    /// </para>
    /// </remarks>
    public int StaticPropIndirectMode { get; init; }

    /// <summary>
    /// Let world geometry sample a hit material's alpha channel, like
    /// <see cref="TextureShadows"/> does for props.
    /// </summary>
    /// <remarks>
    /// ++'s <c>-worldtextureshadows</c>, registrar at <c>0x14003a634</c>
    /// (disasm-verified), "Same as -textureshadows but for world geometry".
    /// The consumer gates world-face alpha sampling with
    /// <c>worldtextureshadows AND textureshadows</c> (<c>all.c:39871</c>:
    /// the gate is <c>DAT_1417194e5 &amp; DAT_1417194e4</c>), so in practice
    /// it is <c>-textureshadows</c> plus the world half.
    /// </remarks>
    public bool WorldTextureShadows { get; init; }

    /// <summary>
    /// ++'s <c>-translucentshadows</c>. The binary registers it with the
    /// SAME global as <see cref="WorldTextureShadows"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disassembly of the registrar block
    /// (<c>vradplusplus.exe</c> <c>0x14003a634..0x14003a668</c>) loads one
    /// address into <c>r13</c> (<c>0x1417194e5</c>) and passes that same
    /// <c>r13</c> as the target of BOTH the <c>-worldtextureshadows</c> and
    /// the <c>-translucentshadows</c> registrars -- this is byte-level fact,
    /// not a decompiler merge artifact; Ghidra's
    /// <c>all.c:43417-43420</c> shows the same two shared-pointer calls.
    /// </para>
    /// <para>
    /// So in the real binary the flag is an alias: it sets the world-alpha
    /// gate and nothing else. (The translucent-sampling parameter at the
    /// sample-material call, <c>all.c:39880</c>'s fourth argument
    /// <c>DAT_1417194e6</c>, has NO registrar anywhere in the binary --
    /// grep over all.c finds only the two consumer reads at
    /// <c>all.c:39880/39927</c> -- so it is permanently false and
    /// unreachable from the command line.) Implemented as an alias of
    /// <see cref="WorldTextureShadows"/>, per the binary.
    /// </para>
    /// </remarks>
    public bool TranslucentShadows { get; init; }

    /// <summary>
    /// Keep <c>light_directional</c> entities out of ++'s spotlight rename.
    /// </summary>
    /// <remarks>
    /// ++'s <c>-supportslightdirectional</c>, registrar at
    /// <c>0x14003a897</c> (<c>all.c:43461</c>): "Disables classname renaming
    /// of light_directional". Consumer at <c>all.c:22694</c>: without the
    /// flag, a recognized light whose processed kind is not 3 gets its
    /// <c>classname</c> rewritten to <c>light_spot</c> and the original
    /// saved under <c>original_classname</c>; with the flag that rename is
    /// skipped. The managed port never renames entity classnames -- vrad
    /// here does not write the entity lump -- so the flag is accepted and
    /// observed as a no-op BY CONSTRUCTION, and the rename it disables is
    /// ++'s own feature (stock SDK 2013 has no <c>light_directional</c>
    /// handling to rename; <c>src/utils/vrad/vrad.cpp</c> handles only
    /// <c>light</c>/<c>spotlight</c>/<c>sun</c>). Documented as such; see
    /// T5-findings.md.
    /// </remarks>
    public bool SupportsLightDirectional { get; init; }

    /// <summary>
    /// Keep <c>light_projected</c> entities out of ++'s spotlight rename.
    /// </summary>
    /// <remarks>
    /// ++'s <c>-supportslightprojected</c>, registrar at <c>0x14003a8b4</c>
    /// (<c>all.c:43462</c>), consumer <c>all.c:22498</c> -- the same
    /// rename-disable shape as <see cref="SupportsLightDirectional"/> and
    /// the same no-op-by-construction status in this port.
    /// </remarks>
    public bool SupportsLightProjected { get; init; }

    /// <summary>
    /// Gather face lighting through the ++ spherical-harmonics
    /// <c>FinalLightFace</c> instead of the stock transfer-matrix walk.
    /// </summary>
    /// <remarks>
    /// ++'s <c>-sphericalharmonics</c>, registrar at <c>0x14003ab9f</c>
    /// (<c>all.c:43507</c>, "Enable spherical harmonics"). The flag byte is
    /// read at nine consumer sites (<c>all.c:13942, 15390, 15693, 16084,
    /// 16667, 16744, 29220, 42753, 43508</c>) that swap the entire
    /// final-lighting stage between stock's progressive-radiosity walk and
    /// a full SH9 kernel (<c>FUN_14002bf80</c> / <c>FUN_14002bf00</c>).
    /// Staged as parse-accepted with the behavior NOT implemented -- see
    /// T5-findings.md §4 for the exact debt: an SH9 kernel is a whole
    /// alternative lighting algorithm, the plan itself (§T5) lists it as
    /// its own sub-lane with a golden-vs-++ gate, and no ++ oracle exists
    /// for the mode. Setting it with the behavior absent is the honest
    /// documented gap, never a silent drop.
    /// </remarks>
    public bool SphericalHarmonics { get; init; }

    /// <summary>
    /// Whether to reproduce the stock tools' defects or do the right thing.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CompliancePolicy.Correct"/>, which is the one
    /// default on this record that is deliberately NOT stock's behaviour. See
    /// <see cref="ComplianceOptions"/>. Byte-exact comparisons against stock
    /// output must set <see cref="ComplianceOptions.Stock"/>.
    /// </remarks>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>Stock's defaults: LDR, 100 bounces, supersampled.</summary>
    public static VradOptions Default { get; } = new();

    /// <summary>
    /// The iteration setting: <c>-fast</c>, LDR only.
    /// </summary>
    public static VradOptions FastDefault { get; } = new() { Fast = true };

    /// <summary>
    /// The shipping setting: both ranges from one compile, with
    /// <c>-final</c>'s sky sampling.
    /// </summary>
    /// <remarks>
    /// <c>-final</c> is <c>-extrasky 16</c> and nothing else
    /// (<c>vrad.cpp:2514</c>), and <see cref="VradLightingRange.Both"/> is one
    /// compile here rather than <c>vrad_launcher</c>'s two runs.
    /// </remarks>
    public static VradOptions FinalDefault { get; } = new()
    {
        Range = VradLightingRange.Both,
        SkySampleScale = 16.0f,
    };
}
