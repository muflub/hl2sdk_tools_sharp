namespace SourceSharp.MapTools.Options;

/// <summary>
/// Which lighting range or ranges a vrad compile produces.
/// </summary>
/// <remarks>
/// <para>
/// Stock has no such option, because stock cannot express it. The DLL has one
/// global mode, set by <c>SetHDRMode</c>, defaulted to
/// LDR before parsing and flipped by <c>-hdr</c>
/// or <c>-ldr</c>. To get both,
/// <c>vrad_launcher</c> scans for <c>-both</c>
/// and runs the ENTIRE DLL twice, which recomputes
/// the geometry, the KD-tree, the patch subdivision and the transfer matrix
/// from scratch for the second range -- everything except the light colours is
/// identical work done twice.
/// </para>
/// <para>
/// The lighting-range design shares all of that between the two passes, so
/// <see cref="VradLightingRange.Both"/> is a mode of one compile and not two runs. That is why
/// this is an option on <see cref="VradOptions"/> and not a loop in a CLI.
/// </para>
/// </remarks>
public enum VradLightingRange
{
    /// <summary>
    /// Low dynamic range only. Stock's default — <c>SetHDRMode(false)</c>
    /// runs before parsing — and stock's <c>-ldr</c>.
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
/// <c>numthreads</c>; but parallelism in this port is
/// <see cref="SourceSharp.MapTools.Parallel.CompileParallelism"/>, taken uniformly by every stage,
/// so a per-tool thread count would be a second answer to the same question.
/// The parser accepts <c>-threads n</c> and says so in a diagnostic.
/// </para>
/// <para>
/// Options stock parses but this port drops, with reasons from
/// the option audit. The parser ACCEPTS each of these and reports a
/// diagnostic rather than failing:
/// <list type="bullet">
/// <item><description>
/// Everything VMPI: the <c>-mpi</c> prefix branch,
/// which sits behind an <c>MPI</c> define and so is not even compiled in the
/// reference build, plus <c>-mpi_pw</c> and <c>-mpi_ListParams</c> from the usage text.
/// Windows-only cluster mode.
/// </description></item>
/// <item><description>
/// <c>-dump</c>, <c>-dumpnormals</c>,
/// <c>-dumptrace</c> and
/// <c>-dumppropmaps</c>: debug writes of intermediate
/// state to files beside the map. <c>-dump</c> is also stock vrad's only
/// case-SENSITIVE flag -- <c>strcmp</c>, not <c>Q_stricmp</c> -- which is a
/// typo, not a contract.
/// </description></item>
/// <item><description>
/// <c>-loghash</c>, which writes the sample hash table
/// to <c>samplehash.txt</c>; and <c>-StopOnExit</c>,
/// which waits for a keypress, in a library.
/// </description></item>
/// <item><description>
/// <c>-low</c>, <c>-FullMinidumps</c>,
/// <c>-novconfig</c>,
/// <c>-allowdebug</c>/<c>-steam</c>: process
/// priority and launcher plumbing; the last three are already no-ops inside
/// stock's own parse loop.
/// </description></item>
/// <item><description>
/// <c>-incremental</c> and the <c>.r0</c> files of
/// the reference's incremental-lighting path. Note that the reference build's parse loop does
/// not contain an <c>-incremental</c> branch at all, so the path is already
/// unreachable from the command line. The IDEA -- reuse the previous compile's
/// light contributions when only some lights moved -- is kept and generalised
/// in the collision cache; the file format is not.
/// </description></item>
/// <item><description>
/// Hammer's in-editor <c>IVRadDLL</c> interface and
/// the <c>g_bInterrupt</c> flag it drives. A library that returns a value does
/// not need an interface for being told to stop; cancellation is a
/// <c>CancellationToken</c>.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Six more are not in a release build: <c>-scale</c>, <c>-ambient</c>,
/// <c>-dlight</c>, <c>-sky</c>, <c>-notexscale</c> and <c>-coring</c> all sit
/// inside a debug-options guard that a release build defines as false. The four whose
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
/// the reference.
/// </para>
/// </remarks>
public sealed record VradOptions
{
    /// <summary>Which lighting range or ranges to produce.</summary>
    /// <remarks>
    /// Stock's <c>-ldr</c> / <c>-hdr</c> / <c>-both</c>. Defaults to
    /// <see cref="VradLightingRange.Ldr"/>, which is what
    /// the reference's <c>SetHDRMode(false)</c> does before parsing
    /// anything.
    /// </remarks>
    public VradLightingRange Range { get; init; } = VradLightingRange.Ldr;

    /// <summary>Emit the per-stage commentary stock's <c>-v</c> emits.</summary>
    /// <remarks>Stock's <c>-v</c> or <c>-verbose</c>. Default false.</remarks>
    public bool Verbose { get; init; }

    /// <summary>How many radiosity bounces to gather.</summary>
    /// <remarks>
    /// Stock's <c>-bounce</c>, default 100;
    /// the reference's global carries a trailing comment
    /// recording that it was once 25 and originally 8. Stock refuses a
    /// negative value; zero is legal and means direct light only.
    /// </remarks>
    public int Bounces { get; init; } = 100;

    /// <summary>Quick and dirty lighting.</summary>
    /// <remarks>
    /// Stock's <c>-fast</c>, default false. The iteration setting: what a designer uses while
    /// building, with <see cref="Range"/> left at LDR.
    /// </remarks>
    public bool Fast { get; init; }

    /// <summary>
    /// How many times the standard number of rays to trace for indirect light
    /// and sky ambient.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-extrasky n</c>, default 1.0.
    /// Stock's <c>-final</c> is
    /// exactly <c>-extrasky 16</c> and sets nothing else, which its own usage
    /// text confirms: "equivalent to -extrasky 16".
    /// There is therefore no separate <c>Final</c> flag here -- it would be a
    /// second name for one number -- and the parser maps <c>-final</c> onto
    /// this.
    /// </remarks>
    public float SkySampleScale { get; init; } = 1.0f;

    /// <summary>Supersample lightmaps.</summary>
    /// <remarks>
    /// Stock has no flag to turn this ON, only <c>-noextra</c> to turn it off;
    /// supersampling is on in the reference's defaults.
    /// Modelled in the positive sense, defaulting true, so the default of the
    /// record is the default of stock and a reader does not have to hold a
    /// double negative in their head.
    /// </remarks>
    public bool Supersample { get; init; } = true;

    /// <summary>
    /// Bake the supersampling pattern into the lightmaps so it can be seen
    /// in-game.
    /// </summary>
    /// <remarks>Stock's <c>-debugextra</c>, default false.</remarks>
    public bool DebugExtra { get; init; }

    /// <summary>Sample per-leaf ambient at lower quality to save time.</summary>
    /// <remarks>Stock's <c>-fastambient</c>, default false.</remarks>
    public bool FastAmbient { get; init; }

    /// <summary>
    /// The angle, in degrees, below which adjacent faces are smoothed into one
    /// lighting group.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-smooth</c>, default 45, which is what
    /// the reference stores pre-cosined:
    /// <c>smoothing_threshold = 0.7071067</c> with the comment
    /// <c>cos(45.0*(M_PI/180))</c>. Stock converts on the way in; this holds
    /// the degrees and leaves the cosine to the stage.
    /// </remarks>
    public float SmoothingAngleDegrees { get; init; } = 45.0f;

    /// <summary>Move lightmap sample points to face centres.</summary>
    /// <remarks>Stock's <c>-centersamples</c>, default false.</remarks>
    public bool CenterSamples { get; init; }

    /// <summary>
    /// Put direct lighting in a different lightmap from the radiosity result.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-dlightmap</c>, default false. An <c>int</c> in stock that only ever holds 0 or 1,
    /// so a bool here.
    /// </remarks>
    public bool SeparateDirectLightmap { get; init; }

    /// <summary>Rescale every luxel on the map by this factor.</summary>
    /// <remarks>
    /// Stock's <c>-luxeldensity</c>, default 1.0. Stock RECIPROCATES a value greater than 1 --
    /// <c>if (luxeldensity &gt; 1.0) luxeldensity = 1.0 / luxeldensity;</c> --
    /// while its usage text claims a value above 1.0
    /// "will be ignored". Neither is true of the other: 2.0 is silently turned
    /// into 0.5, not ignored. The option holds what was typed and the stage
    /// reciprocates, so the contradiction is visible instead of buried.
    /// </remarks>
    public float LuxelDensity { get; init; } = 1.0f;

    /// <summary>
    /// The sun's angular diameter in degrees, which softens its shadows.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-softsun</c>, default 0: a point sun with hard shadows. Stock stores
    /// <c>sin(radians(n))</c> in the same global it parsed the degrees into;
    /// this holds the degrees. Stock's usage suggests 0 to 5.
    /// </remarks>
    public float SunAngularExtentDegrees { get; init; }

    /// <summary>Do not recurse into the 3D skybox for shadows on the world.</summary>
    /// <remarks>Stock's <c>-noskyboxrecurse</c>, default false.</remarks>
    public bool NoSkyboxRecurse { get; init; }

    /// <summary>Bake per-vertex lighting into static props.</summary>
    /// <remarks>Stock's <c>-StaticPropLighting</c>, default false.</remarks>
    public bool StaticPropLighting { get; init; }

    /// <summary>Shadow-test static props at polygon precision, not bounding box.</summary>
    /// <remarks>Stock's <c>-StaticPropPolys</c>, default false.</remarks>
    public bool StaticPropPolys { get; init; }

    /// <summary>Light only static props, directly, and nothing else.</summary>
    /// <remarks>
    /// Stock's <c>-OnlyStaticProps</c>, default false. A debug mode, as stock's usage says.
    /// </remarks>
    public bool OnlyStaticProps { get; init; }

    /// <summary>Render static props' normals instead of lighting them.</summary>
    /// <remarks>
    /// Stock's <c>-StaticPropNormals</c>, default false. A debug mode that changes the output lightmaps, so
    /// it is an option and not a dump.
    /// </remarks>
    public bool StaticPropNormals { get; init; }

    /// <summary>Turn off static props shadowing themselves, everywhere.</summary>
    /// <remarks>Stock's <c>-nossprops</c>, default false.</remarks>
    public bool DisablePropSelfShadowing { get; init; }

    /// <summary>
    /// Let a material's alpha channel block light, sampling the texture where a
    /// ray crosses it.
    /// </summary>
    /// <remarks>Stock's <c>-textureshadows</c>, default false.</remarks>
    public bool TextureShadows { get; init; }

    /// <summary>Do not light detail props.</summary>
    /// <remarks>Stock's <c>-nodetaillight</c>, default false.</remarks>
    public bool NoDetailLighting { get; init; }

    /// <summary>Light only detail props and per-leaf ambient.</summary>
    /// <remarks>
    /// Stock's <c>-onlydetail</c>. Not a global in stock
    /// at all: it is an out-parameter of the reference's
    /// <c>ParseCommandLine</c>, initialised to false.
    /// </remarks>
    public bool OnlyDetail { get; init; }

    /// <summary>Show lighting errors as red instead of turning them black.</summary>
    /// <remarks>
    /// Stock's <c>-rederrors</c>. The stock global is
    /// inverted -- <c>bRed2Black</c>, true in the reference's defaults, and the flag
    /// clears it -- so false here means stock's default of "turn red errors
    /// black". Stock's usage text spells the flag <c>-rederror</c>, singular;
    /// the parser matches the plural, which is what
    /// the code matches.
    /// </remarks>
    public bool ShowErrorsInRed { get; init; }

    /// <summary>
    /// Gather bounced light for displacements across a wider area, at a cost in
    /// compile time.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-LargeDispSampleRadius</c>, default false. The fix for splotchy bounced light on terrain.
    /// </remarks>
    public bool LargeDispSampleRadius { get; init; }

    /// <summary>
    /// The coarsest patch size, in luxel widths, allowed in a face's interior.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-maxchop</c>, default 4. Stock refuses a value below 1.
    /// </remarks>
    public float MaxChop { get; init; } = 4.0f;

    /// <summary>
    /// The tightest patch size, in luxel widths, used along face edges.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-chop</c> -- note the name does not match the global, which
    /// is <c>minchop</c>, default 4. Stock refuses a value
    /// below 1 and then clamps the result down to <see cref="MaxChop"/> with
    /// <c>minchop = min(minchop, maxchop)</c> after parsing both, so the
    /// two flags are ORDER-DEPENDENT on the command line: <c>-chop 8 -maxchop
    /// 2</c> leaves min at 8, while <c>-maxchop 2 -chop 8</c> leaves it at 2.
    /// The parser reproduces that order dependence exactly, because a host
    /// pasting a working Hammer command line must get the compile it has been
    /// getting.
    /// </remarks>
    public float MinChop { get; init; } = 4.0f;

    /// <summary>Patch size, in luxel widths, for displacement surfaces.</summary>
    /// <remarks>
    /// Stock's <c>-dispchop</c>, default 8.0.
    /// Stock refuses a value below 1 -- and misspells the
    /// flag as <c>-dipschop</c> in that error message.
    /// </remarks>
    public float DispChop { get; init; } = 8.0f;

    /// <summary>The largest radius, in world units, allowed for a displacement patch.</summary>
    /// <remarks>
    /// Stock's <c>-disppatchradius</c>, default 1500.0. Stock refuses a value below 10.
    /// </remarks>
    public float MaxDispPatchRadius { get; init; } = 1500.0f;

    /// <summary>The largest displacement lighting sample, in world units.</summary>
    /// <remarks>
    /// Stock's <c>-maxdispsamplesize</c>, default 512.0
    /// in the reference -- which its usage text agrees with. Stock validates nothing here.
    /// </remarks>
    public float MaxDispSampleSize { get; init; } = 512.0f;

    /// <summary>
    /// An extra lights file to load alongside <c>lights.rad</c> and the level's
    /// own, or null for none.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-lights &lt;file&gt;</c>, empty in the reference's default
    /// (<c>designer_lights</c>). A path the compile READS,
    /// so it is resolved through <see cref="SourceSharp.MapTools.Io.IContentFileSystem"/> and not
    /// with the process's working directory.
    /// </remarks>
    public string? LightsFile { get; init; }

    /// <summary>
    /// <c>lightscale</c>: a factor on every light's intensity, texlights
    /// included.
    /// </summary>
    /// <remarks>Stock's <c>-scale &lt;n&gt;</c>, default 1.0.</remarks>
    public float LightScale { get; init; } = 1.0f;

    /// <summary>
    /// <c>dlight_threshold</c>: a texlight patch dimmer than this emits no
    /// direct light of its own.
    /// </summary>
    /// <remarks>Stock's <c>-dlight &lt;n&gt;</c>, default 0.1.</remarks>
    public float DLightThreshold { get; init; } = 0.1f;

    /// <summary>
    /// <c>ambient</c>: a flat light added to every sample, in vrad's internal
    /// scale.
    /// </summary>
    /// <remarks>
    /// Stock's <c>-ambient r g b</c>, which MULTIPLIES
    /// each argument by 128 on the way in; this holds the product, so
    /// <c>-ambient 1 1 1</c> is (128, 128, 128). Default zero.
    /// A map without vis overrides it with 0.1, unscaled.
    /// </remarks>
    public SourceSharp.MapFormats.Geometry.Vec3 Ambient { get; init; }

    /// <summary>
    /// <c>texscale</c>: whether a patch's chop size follows its texture's
    /// scale.
    /// </summary>
    /// <remarks>Stock's <c>-notexscale</c> clears it, default true.</remarks>
    public bool TexScale { get; init; } = true;

    // ---------------------------------------------------------------------
    // Extended vrad feature family (T5).
    //
    // These are additive features, not stock defects, so they are feature
    // members here and never StockQuirk members. Each records the default,
    // what consumes it, and what it changes, so an unset option reproduces
    // the plain-compile bytes exactly.
    // ---------------------------------------------------------------------

    /// <summary>Ambient-occlusion sampling for a prop's direct-light gather.</summary>
    /// <remarks>
    /// <c>-ambientocclusion</c> / <c>-ao</c> (alias). Consumed by the prop
    /// direct-light gather and the prop indirect gather, where it replaces
    /// the fixed sample counts with the AO sample knobs. Default false.
    /// </remarks>
    public bool AmbientOcclusion { get; init; }

    /// <summary>Radius, in world units, of the ambient-occlusion hemisphere trace.</summary>
    /// <remarks>
    /// <c>-aoradius</c>, default 40.0. The AO gather consumers scale the
    /// trace end to this radius instead of <c>MAX_TRACE_LENGTH</c>.
    /// </remarks>
    public float AoRadius { get; init; } = 40.0f;

    /// <summary>Multiplier on the ambient-occlusion weight.</summary>
    /// <remarks>
    /// <c>-aoscale</c> ("Ambient Occlusion intensity multiplier"), default 0.5.
    /// </remarks>
    public float AoScale { get; init; } = 0.5f;

    /// <summary>Ambient-occlusion sample count for brush-face gathers.</summary>
    /// <remarks>
    /// <c>-aofacesamples</c>, default 32; the usage text adds "(128 on -final,
    /// 16 on -fast)", which the <c>-final</c> and <c>-fast</c> presets set
    /// explicitly.
    /// </remarks>
    public int AoFaceSamples { get; init; } = 32;

    /// <summary>Ambient-occlusion sample count for static-prop gathers.</summary>
    /// <remarks>
    /// <c>-aopropsamples</c>, default 16; usage text "(32 on -final, 12 on -fast)".
    /// </remarks>
    public int AoPropSamples { get; init; } = 16;

    /// <summary>Visualize ambient occlusion only, by disabling all lights.</summary>
    /// <remarks>
    /// <c>-aodebug</c> means three things at once: AO on, AO-debug on, and
    /// supersampling off — the flag's debug write lands in the SAME global
    /// <c>-noextra</c> controls, the supersample-enable global, not the
    /// separate <c>-scale</c> lightscale global.
    /// "Disables all lights" is the usage wording that ships with the flag;
    /// the pinned effects are these three. Modeled as
    /// <see cref="AmbientOcclusion"/> and <see cref="AoDebug"/> set and
    /// <see cref="Supersample"/> cleared.
    /// </remarks>
    public bool AoDebug { get; init; }

    /// <summary>
    /// Scale factor on the sample counts of the non-SSE prop gathers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-StaticPropSampleScale</c>, default 1.0. Consumed by the three prop
    /// gather sample-count sites: sun disk (base 7), ambient sky (base 40)
    /// and detail-prop indirect (base 30).
    /// </para>
    /// <para>
    /// The multiplication applies only on the fast path (each site picks
    /// <c>extrasoft * base</c>, 30/162, when the scale is unset); with
    /// <c>extrasoft = 1</c> those ARE the stock counts, so the scale is what
    /// the fast branch multiplies, and at the default 1.0 every count is
    /// today's count.
    /// </para>
    /// </remarks>
    public float StaticPropSampleScale { get; init; } = 1.0f;

    /// <summary>Which falloff model the prop indirect gather weights samples by.</summary>
    /// <remarks>
    /// <para>
    /// <c>-StaticPropIndirectMode</c>, default 0. The one consumer is the
    /// prop indirect gatherer (<c>ComputeIndirectLightingAtPoint</c>),
    /// whose weighting branches on the mode value.
    /// </para>
    /// <para>
    /// 0 (default) keeps stock's inverse-square of the traced fraction.
    /// 1 -- distance-exact -- weights by the
    /// inverse square of the TRUE hit distance (accumulated hit point minus
    /// sample point, over 128): <c>1/(1+|hit-pos|²/128²)</c>.
    /// 2 -- unattenuated -- drops the inverse-square entirely:
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
    /// <c>-worldtextureshadows</c>: "Same as -textureshadows but for world
    /// geometry". The consumer gates world-face alpha sampling with
    /// <c>worldtextureshadows AND textureshadows</c>, so in practice it is
    /// <c>-textureshadows</c> plus the world half.
    /// </remarks>
    public bool WorldTextureShadows { get; init; }

    /// <summary>
    /// <c>-translucentshadows</c>. An alias: it drives the
    /// SAME state as <see cref="WorldTextureShadows"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-worldtextureshadows</c> and <c>-translucentshadows</c> share one
    /// state global rather than owning two, so the second flag is an alias:
    /// it sets the world-alpha gate and nothing else. (A separate
    /// translucent-sampling parameter exists in the sampler's signature but
    /// no flag reaches it, so it is permanently false and unreachable from
    /// the command line.) Implemented as an alias of
    /// <see cref="WorldTextureShadows"/>.
    /// </para>
    /// </remarks>
    public bool TranslucentShadows { get; init; }

    /// <summary>
    /// Keep <c>light_directional</c> entities out of the spotlight rename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-supportslightdirectional</c>: "Disables classname renaming
    /// of light_directional". Without the
    /// flag, a recognized light whose processed kind is not spotlight gets its
    /// <c>classname</c> rewritten to <c>light_spot</c> and the original
    /// saved under <c>original_classname</c>; with the flag that rename is
    /// skipped.
    /// </para>
    /// <para>
    /// The rename this flag suppresses is not a reference-build behavior —
    /// the reference handles only <c>light</c>/<c>spotlight</c>/<c>sun</c>
    /// and renames nothing. This vrad never renames entity classnames — it
    /// does not write the entity lump — so the flag is accepted and observed
    /// as a no-op BY CONSTRUCTION. Documented as such.
    /// </para>
    /// </remarks>
    public bool SupportsLightDirectional { get; init; }

    /// <summary>
    /// Keep <c>light_projected</c> entities out of the spotlight rename.
    /// </summary>
    /// <remarks>
    /// <c>-supportslightprojected</c> — the same
    /// rename-disable shape as <see cref="SupportsLightDirectional"/> and
    /// the same no-op-by-construction status in this port.
    /// </remarks>
    public bool SupportsLightProjected { get; init; }

    /// <summary>
    /// Gather face lighting through a spherical-harmonics
    /// <c>FinalLightFace</c> instead of the stock transfer-matrix walk.
    /// </summary>
    /// <remarks>
    /// <c>-sphericalharmonics</c> ("Enable spherical harmonics"). The flag
    /// swaps the entire final-lighting stage between stock's
    /// progressive-radiosity walk and a full SH9 kernel.
    /// Staged as parse-accepted with the behavior NOT implemented: an SH9
    /// kernel is a whole alternative lighting algorithm, tracked as its own
    /// work item with its own golden gate, and no published reference output
    /// exists for the mode. Setting it with the behavior absent is the honest
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
    /// and <see cref="VradLightingRange.Both"/> is one
    /// compile here rather than <c>vrad_launcher</c>'s two runs.
    /// </remarks>
    public static VradOptions FinalDefault { get; } = new()
    {
        Range = VradLightingRange.Both,
        SkySampleScale = 16.0f,
    };
}
