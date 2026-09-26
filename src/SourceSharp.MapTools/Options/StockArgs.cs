//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Options;

/// <summary>
/// Reads a stock vbsp, vvis or vrad command line into this port's typed
/// options.
/// </summary>
/// <remarks>
/// <para>
/// Public on purpose. The typed option records are the real interface -- a host
/// that knows what it wants should say so and never build an <c>argv</c> -- but
/// a host that genuinely HAS a command line, because Hammer's "Run Map" dialog
/// produced one or because a build script has carried the same string for ten
/// years, should not have to write this translation itself and get the corners
/// subtly wrong.
/// </para>
/// <para>
/// Three behaviours are deliberately not stock's:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Nothing exits and nothing throws for a bad line.</b> Stock's
/// <c>Error()</c> calls <c>exit()</c> and an unknown option makes vbsp print
/// its usage and quit. Everything here is a
/// <see cref="CompileDiagnostic"/> and parsing continues to the end, so three
/// mistakes produce three messages. The only exception is a null argument
/// array, which is a bug in the caller rather than a bad command line.
/// </description></item>
/// <item><description>
/// <b>Numbers are read culture-invariantly, always.</b> Stock's
/// <c>atof</c> follows the C locale, which in a tools process is "C" and so is
/// invariant by accident. A .NET host on a machine whose culture writes
/// <c>3,5</c> would otherwise read <c>-chop 3.5</c> as 3 or as 35 depending on
/// the overload, and produce a different map from the same command line. Every
/// parse here passes <see cref="CultureInfo.InvariantCulture"/> explicitly.
/// </description></item>
/// <item><description>
/// <b>A missing value is caught.</b> Stock vbsp and vvis read
/// <c>argv[i+1]</c> without checking, so a line ending in <c>-micro</c> reads
/// past the end of the array. vrad checks, and its messages are what these
/// diagnostics are worded after.
/// </description></item>
/// </list>
/// <para>
/// Flag matching is case-insensitive, as it is in the reference.
/// It is specifically ORDINAL case-insensitivity, not the current culture's:
/// under a Turkish culture a culture-aware comparison does not fold <c>I</c>
/// to <c>i</c>, and <c>-FINAL</c> would stop being <c>-final</c>. Stock has two
/// flags that are accidentally case-SENSITIVE because they use plain
/// <c>strcmp</c> -- vbsp's <c>-minluxelscale</c> and
/// vrad's <c>-dump</c> -- and both are treated
/// case-insensitively here, because a typo in one branch of a 45-branch
/// <c>else if</c> chain is not a contract.
/// </para>
/// <para>
/// These methods are synchronous. Parsing a command line is constant-time work
/// on strings that are already in memory; an <c>…Async</c> here would be a
/// <c>Task</c> allocation wrapping no I/O at all.
/// </para>
/// </remarks>
public static class StockArgs
{
    /// <summary>Reads a stock vbsp command line.</summary>
    /// <param name="args">
    /// The arguments, WITHOUT the program name -- stock's loops start at
    /// <c>argv[1]</c>, and this list corresponds to that tail.
    /// </param>
    /// <returns>
    /// The options, the map path, and everything the parser wants to say.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static StockArgsResult<VbspOptions> ParseVbsp(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ArgCursor cursor = new(args, "vbsp");
        VbspOptions options = VbspOptions.Default;
        int? requestedThreads = null;
        FormatOverrides format = FormatOverrides.None;
        string? presetName = null;
        bool noFormatDetect = false;
        bool noToolsArgs = false;
        bool incremental = false;
        string? cachePath = null;
        bool noCache = false;

        while (cursor.MoveNext(out string arg))
        {
            if (Is(arg, "-v") || Is(arg, "-verbose"))
            {
                options = options with { Verbose = true };
            }
            else if (Is(arg, "-verboseentities"))
            {
                options = options with { VerboseEntities = true };
            }
            else if (Is(arg, "-noweld"))
            {
                options = options with { NoWeld = true };
            }
            else if (Is(arg, "-nocsg"))
            {
                options = options with { NoCsg = true };
            }
            else if (Is(arg, "-noshare"))
            {
                options = options with { NoShare = true };
            }
            else if (Is(arg, "-notjunc"))
            {
                options = options with { NoTJunc = true };
            }
            else if (Is(arg, "-nowater"))
            {
                options = options with { NoWater = true };
            }
            else if (Is(arg, "-noopt"))
            {
                options = options with { NoOpt = true };
            }
            else if (Is(arg, "-noprune"))
            {
                options = options with { NoPrune = true };
            }
            else if (Is(arg, "-nomerge"))
            {
                options = options with { NoMerge = true };
            }
            else if (Is(arg, "-nomergewater"))
            {
                options = options with { NoMergeWater = true };
            }
            else if (Is(arg, "-nosubdiv"))
            {
                options = options with { NoSubdiv = true };
            }
            else if (Is(arg, "-nodetail"))
            {
                options = options with { NoDetail = true };
            }
            else if (Is(arg, "-fulldetail"))
            {
                options = options with { FullDetail = true };
            }
            else if (Is(arg, "-onlyents"))
            {
                options = options with { OnlyEnts = true };
            }
            else if (Is(arg, "-onlyprops"))
            {
                options = options with { OnlyProps = true };
            }
            else if (Is(arg, "-micro"))
            {
                if (cursor.TryFloat(arg, out float micro))
                {
                    options = options with { MicroVolume = micro };
                }
            }
            else if (Is(arg, "-leaktest"))
            {
                options = options with { LeakTest = true };
            }
            else if (Is(arg, "-snapaxial"))
            {
                options = options with { SnapAxialPlanes = true };
            }
            else if (Is(arg, "-block"))
            {
                if (cursor.TryInt(arg, out int bx) && cursor.TryInt(arg, out int by))
                {
                    options = options with { Blocks = BspBlockGrid.Single(bx, by) };
                }
            }
            else if (Is(arg, "-blocks"))
            {
                if (cursor.TryInt(arg, out int xl) && cursor.TryInt(arg, out int yl)
                    && cursor.TryInt(arg, out int xh) && cursor.TryInt(arg, out int yh))
                {
                    options = options with { Blocks = new BspBlockGrid(xl, yl, xh, yh) };
                }
            }
            else if (Is(arg, "-forceskyvis"))
            {
                options = options with { ForceSkyVis = true };
            }
            else if (Is(arg, "-luxelscale"))
            {
                if (cursor.TryFloat(arg, out float scale))
                {
                    options = options with { LuxelScale = scale };
                }
            }
            else if (Is(arg, "-minluxelscale"))
            {
                if (cursor.TryFloat(arg, out float minScale))
                {
                    // Stock clamps as it parses.
                    options = options with { MinLuxelScale = Math.Max(minScale, 1.0f) };
                }
            }
            else if (Is(arg, "-dxlevel"))
            {
                if (cursor.TryInt(arg, out int dxLevel))
                {
                    options = options with { DxLevel = dxLevel };
                }
            }
            else if (Is(arg, "-bumpall"))
            {
                options = options with { BumpAll = true };
            }
            else if (Is(arg, "-lightifmissing"))
            {
                options = options with { LightIfMissing = true };
            }
            else if (Is(arg, "-keepstalezip"))
            {
                options = options with { KeepStaleZip = true };
            }
            else if (Is(arg, "-allowdetailcracks"))
            {
                options = options with { AllowDetailCracks = true };
            }
            else if (Is(arg, "-novirtualmesh"))
            {
                options = options with { NoVirtualMesh = true };
            }
            else if (Is(arg, "-replacematerials"))
            {
                options = options with { ReplaceMaterials = true };
            }
            else if (Is(arg, "-nodrawtriggers"))
            {
                options = options with { NoDrawTriggers = true };
            }
            else if (Is(arg, "-embed"))
            {
                if (cursor.TryValue(arg, out string dir))
                {
                    options = options with { EmbedDirectory = dir };
                }
            }
            else if (Is(arg, "-threads"))
            {
                // RECORDED, as vvis and vrad do. Stock parses <c>-threads</c>
                // and then overwrites it with numthreads = 1,
                // so stock vbsp is serial whatever it is told; this port's
                // parallel stages (plan 3p) honour it, and write the same
                // bytes at every degree. The host maps it onto
                // VbspContext.Parallelism.
                if (cursor.TryInt(arg, out int threads))
                {
                    requestedThreads = threads;
                }
            }
            else if (Is(arg, "-glview") || Is(arg, "-dumpcollide") || Is(arg, "-dumpstaticprop"))
            {
                cursor.Drop(arg, "debug dumps to files are reported as diagnostics instead");
            }
            else if (Is(arg, "-tmpout"))
            {
                cursor.Drop(arg, "where output goes is the host's, through IFileSystem");
            }
            else if (Is(arg, "-low"))
            {
                cursor.Drop(arg, "process priority belongs to whoever owns the process");
            }
            else if (Is(arg, "-xbox"))
            {
                cursor.Drop(arg, "console byte-swap paths are dropped; see -nodrawtriggers");
            }
            else if (Is(arg, "-compliance"))
            {
                if (TryCompliance(cursor, arg, out ComplianceOptions compliance))
                {
                    options = options with { Compliance = compliance };
                }
            }
            else if (Is(arg, "-cooker"))
            {
                if (TryCooker(cursor, arg, out CollisionCookerKind cooker))
                {
                    options = options with { Cooker = cooker };
                }
            }
            else if (Is(arg, "-vphysics"))
            {
                if (cursor.TryValue(arg, out string library))
                {
                    options = options with { VPhysicsLibrary = library };
                }
            }
            else if (arg.Length > 1 && arg[0] == '-'
                && MapFormatPreset.TryByName(arg, out MapFormatPreset? preset))
            {
                // The preset flags apply the moment their token is seen, storing
                // only their own fields, so two preset flags compose
                // last-writer-wins per field and the NAME provenance is the
                // last one named.
                format = preset!.ToOverrides().ApplyOver(format);
                presetName = preset.Name;
            }
            else if (Is(arg, "-bspformat"))
            {
                // The value is consumed ONCE: a second cursor.TryValue would
                // read the token after the flag, so the complaint would name
                // the map path instead of the value the user typed.
                if (cursor.TryValue(arg, out string text))
                {
                    if (FormatResolution.TryParseBspFormat(text, out int version))
                    {
                        format = format with { BspVersion = version };
                    }
                    else
                    {
                        cursor.Add(
                            StockArgsCodes.ValueOutOfRange,
                            DiagnosticSeverity.Error,
                            $"vbsp: -bspformat {text} is not a version this port can write "
                            + "(19, 20 or 21).");
                    }
                }
            }
            else if (Is(arg, "-lightformat"))
            {
                if (cursor.TryValue(arg, out string text))
                {
                    if (FormatResolution.TryParseLightFormat(text, out int version))
                    {
                        format = format with { WorldLightVersion = version };
                    }
                    else
                    {
                        cursor.Add(
                            StockArgsCodes.ValueOutOfRange,
                            DiagnosticSeverity.Error,
                            $"vbsp: -lightformat {text} is not a lightgroups version "
                            + "(0 or 1).");
                    }
                }
            }
            else if (Is(arg, "-staticpropformat"))
            {
                if (cursor.TryValue(arg, out string token))
                {
                    if (FormatResolution.IsKnownStaticPropsToken(token))
                    {
                        format = format with { StaticPropsToken = token };
                    }
                    else
                    {
                        // "Unrecognized prop format %s", matching the flag
                        // family this option mirrors.
                        cursor.Add(
                            FormatResolution.UnrecognizedPropFormatCode,
                            DiagnosticSeverity.Error,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                FormatResolution.UnrecognizedPropFormatMessage,
                                token));
                    }
                }
            }
            else if (Is(arg, "-matsyscompat"))
            {
                format = format with { MatsysCompat = true };
            }
            else if (Is(arg, "-simpleladders"))
            {
                format = format with { SimpleLadders = true };
            }
            else if (Is(arg, "-nodisp4virtualmesh"))
            {
                format = format with { NoDisp4VirtualMesh = true };
            }
            else if (Is(arg, "-noineligiblevertexlitprops"))
            {
                format = format with { NoIneligibleVertexLitProps = true };
            }
            else if (Is(arg, "-csgoclipcontents"))
            {
                format = format with { CsgoClipContents = true };
            }
            else if (Is(arg, "-maxdispinfo"))
            {
                // "Set maximum displacement limit": the same state the csgo
                // preset's tail store writes with 32768.
                if (cursor.TryInt(arg, out int limit))
                {
                    if (limit > 0)
                    {
                        format = format with { DispInfoLimit = limit };
                    }
                    else
                    {
                        cursor.Add(
                            StockArgsCodes.ValueOutOfRange,
                            DiagnosticSeverity.Error,
                            "vbsp: -maxdispinfo needs a positive limit.");
                    }
                }
            }
            else if (Is(arg, "-noformatdetect"))
            {
                noFormatDetect = true;
            }
            else if (Is(arg, "-notoolsargs"))
            {
                noToolsArgs = true;
            }
            else if (Is(arg, "-incremental"))
            {
                incremental = true;
            }
            else if (Is(arg, "-cache-dir"))
            {
                // The directory the cache file goes in; the file keeps the Q7
                // name (<map>.sscache.db). Not stock's vocabulary — the plan's
                // 10a host verb, recorded for the host like -threads is.
                if (cursor.TryValue(arg, out string dir))
                {
                    cachePath = dir;
                }
            }
            else if (Is(arg, "-nocache"))
            {
                noCache = true;
            }
            else
            {
                Common(cursor, arg, options.Verbose);
            }
        }

        cursor.RequireMapPath();

        if (options.EmbedDirectory is not null && (options.OnlyEnts || options.OnlyProps))
        {
            // Stock prints the same advice and then calls CmdLib_Exit(1).
            // Here it is an error diagnostic on a result the
            // caller still holds.
            cursor.Add(
                StockArgsCodes.ConflictingOptions,
                DiagnosticSeverity.Error,
                "vbsp: -embed only makes sense alongside full BSP compiles, "
                + "not -onlyents or -onlyprops. Use bspzip to update embedded files.");
        }

        if (options.NoDetail && options.FullDetail)
        {
            cursor.Add(
                StockArgsCodes.ConflictingOptions,
                DiagnosticSeverity.Warning,
                "vbsp: -nodetail and -fulldetail ask for opposite things. "
                + "Stock lets both stand and the later stages decide.");
        }

        return cursor.Finish(options) with
        {
            Threads = requestedThreads,
            Format = format.IsEmpty ? null : format,
            PresetName = presetName,
            NoFormatDetect = noFormatDetect,
            NoToolsArgs = noToolsArgs,
            Incremental = incremental,
            CachePath = cachePath,
            NoCache = noCache,
        };
    }

    /// <summary>Reads a stock vvis command line.</summary>
    /// <param name="args">
    /// The arguments, WITHOUT the program name.
    /// </param>
    /// <returns>
    /// The options, the map path, and everything the parser wants to say.
    /// </returns>
    /// <remarks>
    /// <see cref="VvisOptions.Tighten"/> has no stock spelling -- it is not a
    /// stock option at all -- so it can only be set on the record, never
    /// through here.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static StockArgsResult<VvisOptions> ParseVvis(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ArgCursor cursor = new(args, "vvis");
        int? requestedThreads = null;
        VvisOptions options = VvisOptions.Default;

        while (cursor.MoveNext(out string arg))
        {
            if (Is(arg, "-fast"))
            {
                options = options with { Fast = true };
            }
            else if (Is(arg, "-v") || Is(arg, "-verbose"))
            {
                options = options with { Verbose = true };
            }
            else if (Is(arg, "-nosort"))
            {
                options = options with { NoSort = true };
            }
            else if (Is(arg, "-radius_override"))
            {
                // Stock squares it on the way in, so that the
                // per-cluster distance test can stay squared. The option holds
                // the radius that was asked for.
                if (cursor.TryFloat(arg, out float radius))
                {
                    options = options with { RadiusOverride = radius };
                }
            }
            else if (Is(arg, "-trace"))
            {
                if (cursor.TryInt(arg, out int from) && cursor.TryInt(arg, out int to))
                {
                    options = options with { Trace = (from, to) };
                }
            }
            else if (Is(arg, "-threads"))
            {
                // RECORDED, not dropped. It does not belong on the options
                // record -- parallelism is CompileParallelism's -- but throwing
                // it away meant `-threads N` was accepted and silently had no
                // effect. The host maps this onto MaxDegree.
                if (cursor.TryInt(arg, out int threads))
                {
                    requestedThreads = threads;
                }
            }
            else if (Is(arg, "-compliance"))
            {
                if (TryCompliance(cursor, arg, out ComplianceOptions compliance))
                {
                    options = options with { Compliance = compliance };
                }
            }
            else if (Is(arg, "-tmpin"))
            {
                cursor.Drop(arg, "where input comes from is the host's, through IFileSystem");
            }
            else if (Is(arg, "-low"))
            {
                cursor.Drop(arg, "process priority belongs to whoever owns the process");
            }
            else
            {
                Common(cursor, arg, options.Verbose);
            }
        }

        cursor.RequireMapPath();
        return cursor.Finish(options) with { Threads = requestedThreads };
    }

    /// <summary>Reads a stock vrad command line.</summary>
    /// <param name="args">
    /// The arguments, WITHOUT the program name. <c>-both</c> is accepted here
    /// even though it is a <c>vrad_launcher</c> flag rather than a vrad one,
    /// because a host holding a real command line is holding the launcher's.
    /// </param>
    /// <returns>
    /// The options, the map path, and everything the parser wants to say.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static StockArgsResult<VradOptions> ParseVrad(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ArgCursor cursor = new(args, "vrad");
        int? requestedThreads = null;
        VradOptions options = VradOptions.Default;
        string? gpuDeviceMatch = null;
        int? gpuRaysPerSlab = null;

        while (cursor.MoveNext(out string arg))
        {
            if (Is(arg, "-v") || Is(arg, "-verbose"))
            {
                options = options with { Verbose = true };
            }
            else if (Is(arg, "-hdr"))
            {
                options = options with { Range = VradLightingRange.Hdr };
            }
            else if (Is(arg, "-ldr"))
            {
                options = options with { Range = VradLightingRange.Ldr };
            }
            else if (Is(arg, "-both"))
            {
                options = options with { Range = VradLightingRange.Both };
            }
            else if (Is(arg, "-fast"))
            {
                options = options with { Fast = true };
            }
            else if (Is(arg, "-final"))
            {
                // -final sets g_flSkySampleScale and nothing
                // else, so it is exactly -extrasky 16.
                options = options with { SkySampleScale = 16.0f };
            }
            else if (Is(arg, "-extrasky"))
            {
                if (cursor.TryFloat(arg, out float sky))
                {
                    options = options with { SkySampleScale = sky };
                }
            }
            else if (Is(arg, "-noextra"))
            {
                options = options with { Supersample = false };
            }
            else if (Is(arg, "-debugextra"))
            {
                options = options with { DebugExtra = true };
            }
            else if (Is(arg, "-fastambient"))
            {
                options = options with { FastAmbient = true };
            }
            else if (Is(arg, "-bounce"))
            {
                if (cursor.TryInt(arg, out int bounces))
                {
                    if (bounces < 0)
                    {
                        cursor.OutOfRange(arg, "must not be negative");
                    }
                    else
                    {
                        options = options with { Bounces = bounces };
                    }
                }
            }
            else if (Is(arg, "-smooth"))
            {
                // Stock cosines it on the way in.
                if (cursor.TryFloat(arg, out float degrees))
                {
                    options = options with { SmoothingAngleDegrees = degrees };
                }
            }
            else if (Is(arg, "-centersamples"))
            {
                options = options with { CenterSamples = true };
            }
            else if (Is(arg, "-dlightmap"))
            {
                options = options with { SeparateDirectLightmap = true };
            }
            else if (Is(arg, "-luxeldensity"))
            {
                // Stock reciprocates a value above 1.
                if (cursor.TryFloat(arg, out float density))
                {
                    options = options with { LuxelDensity = density };
                }
            }
            else if (Is(arg, "-softsun"))
            {
                // Stock stores sin(radians(n)) on the way in.
                if (cursor.TryFloat(arg, out float extent))
                {
                    options = options with { SunAngularExtentDegrees = extent };
                }
            }
            else if (Is(arg, "-noskyboxrecurse"))
            {
                options = options with { NoSkyboxRecurse = true };
            }
            else if (Is(arg, "-onlydetail"))
            {
                options = options with { OnlyDetail = true };
            }
            else if (Is(arg, "-nodetaillight"))
            {
                options = options with { NoDetailLighting = true };
            }
            else if (Is(arg, "-rederrors"))
            {
                options = options with { ShowErrorsInRed = true };
            }
            else if (Is(arg, "-StaticPropLighting"))
            {
                options = options with { StaticPropLighting = true };
            }
            else if (Is(arg, "-StaticPropPolys"))
            {
                options = options with { StaticPropPolys = true };
            }
            else if (Is(arg, "-StaticPropNormals"))
            {
                options = options with { StaticPropNormals = true };
            }
            else if (Is(arg, "-OnlyStaticProps"))
            {
                options = options with { OnlyStaticProps = true };
            }
            else if (Is(arg, "-nossprops"))
            {
                options = options with { DisablePropSelfShadowing = true };
            }
            else if (Is(arg, "-textureshadows"))
            {
                options = options with { TextureShadows = true };
            }
            else if (Is(arg, "-LargeDispSampleRadius"))
            {
                options = options with { LargeDispSampleRadius = true };
            }
            else if (Is(arg, "-ambientocclusion") || Is(arg, "-ao"))
            {
                // Both spellings drive the same state, so -ao is a true alias,
                // not a distinct knob. Parse-accepted; the AO gather itself is
                // T5 debt (no published reference output exists for the mode).
                options = options with { AmbientOcclusion = true };
            }
            else if (Is(arg, "-aoradius"))
            {
                // Default 40.0.
                if (cursor.TryFloat(arg, out float aoRadius))
                {
                    options = options with { AoRadius = aoRadius };
                }
            }
            else if (Is(arg, "-aoscale"))
            {
                // Default 0.5.
                if (cursor.TryFloat(arg, out float aoScale))
                {
                    options = options with { AoScale = aoScale };
                }
            }
            else if (Is(arg, "-aofacesamples"))
            {
                // Default 32.
                // No range validation: the reference's int parser
                // stores whatever the platform conversion yields.
                if (cursor.TryInt(arg, out int faceSamples))
                {
                    options = options with { AoFaceSamples = faceSamples };
                }
            }
            else if (Is(arg, "-aopropsamples"))
            {
                // Default 16.
                if (cursor.TryInt(arg, out int propSamples))
                {
                    options = options with { AoPropSamples = propSamples };
                }
            }
            else if (Is(arg, "-aodebug"))
            {
                // The flag's handler writes exactly three globals: AO on,
                // AO-debug on, and zero into the SAME global -noextra controls
                // -- i.e. supersampling off, not the separate -scale
                // lightscale. See VradOptions.AoDebug.
                options = options with
                {
                    AmbientOcclusion = true,
                    AoDebug = true,
                    Supersample = false,
                };
            }
            else if (Is(arg, "-StaticPropSampleScale"))
            {
                // Default 1.0.
                // Parse-only: the scale multiplies the fast-path-gated branch
                // of the three prop-gather sample-count sites, and that branch
                // is armed ONLY by the -fast preset's side effect, whose
                // fast-path plumbing this port does not
                // rename -- so at every port call site the scale has no
                // observable effect. T5 §2.
                if (cursor.TryFloat(arg, out float sampleScale))
                {
                    options = options with { StaticPropSampleScale = sampleScale };
                }
            }
            else if (Is(arg, "-StaticPropIndirectMode"))
            {
                // Default 0; usage "0 - default, 1 - distance-exact,
                // 2 - unattenuated". Any int is
                // accepted: a value outside 0..2 takes NONE of the weighting
                // branches and falls through to
                // accumulate the raw lightmap. Reproduced in
                // PropIndirectLighting.Compute.
                if (cursor.TryInt(arg, out int mode))
                {
                    options = options with { StaticPropIndirectMode = mode };
                }
            }
            else if (Is(arg, "-worldtextureshadows") || Is(arg, "-translucentshadows"))
            {
                // Both flags drive the SAME state, so either one flips one
                // gate. The model keeps both
                // names for record fidelity and lights both properties, which
                // a consumer can only read as the one bit. The separate
                // translucent SAMPLING parameter has no
                // flag reaching it -- permanently false, unreachable. Parse-
                // only: the port has no world-geometry alpha-sampling path
                // (T5 debt).
                options = options with { WorldTextureShadows = true, TranslucentShadows = true };
            }
            else if (Is(arg, "-supportslightdirectional"))
            {
                // No-op BY CONSTRUCTION: it disables the light_directional ->
                // light_spot classname rename, and this port
                // never renames entity classnames (vrad writes no entity
                // lump). Recorded, never silently dropped. See VradOptions.
                options = options with { SupportsLightDirectional = true };
            }
            else if (Is(arg, "-supportslightprojected"))
            {
                // Same rename-disable shape for light_projected;
                // no-op by
                // construction for the same reason.
                options = options with { SupportsLightProjected = true };
            }
            else if (Is(arg, "-sphericalharmonics"))
            {
                // STAGED parse-only by design: the flag swaps the whole
                // final-lighting stage to an SH9 kernel, which the plan (T5)
                // gates as its own
                // sub-lane. Accepted and recorded; behavior absent is the
                // documented gap, never a silent drop.
                options = options with { SphericalHarmonics = true };
            }
            else if (Is(arg, "-maxchop"))
            {
                if (cursor.TryFloat(arg, out float maxChop))
                {
                    if (maxChop < 1.0f)
                    {
                        cursor.OutOfRange(arg, "must be at least 1");
                    }
                    else
                    {
                        options = options with { MaxChop = maxChop };
                    }
                }
            }
            else if (Is(arg, "-chop"))
            {
                if (cursor.TryFloat(arg, out float minChop))
                {
                    if (minChop < 1.0f)
                    {
                        cursor.OutOfRange(arg, "must be at least 1");
                    }
                    else
                    {
                        // Stock clamps against maxchop AS IT STANDS,
                        // which makes -chop and -maxchop order-dependent. Kept.
                        options = options with { MinChop = Math.Min(minChop, options.MaxChop) };
                    }
                }
            }
            else if (Is(arg, "-dispchop"))
            {
                if (cursor.TryFloat(arg, out float dispChop))
                {
                    if (dispChop < 1.0f)
                    {
                        cursor.OutOfRange(arg, "must be at least 1");
                    }
                    else
                    {
                        options = options with { DispChop = dispChop };
                    }
                }
            }
            else if (Is(arg, "-disppatchradius"))
            {
                if (cursor.TryFloat(arg, out float radius))
                {
                    if (radius < 10.0f)
                    {
                        cursor.OutOfRange(arg, "must be at least 10");
                    }
                    else
                    {
                        options = options with { MaxDispPatchRadius = radius };
                    }
                }
            }
            else if (Is(arg, "-maxdispsamplesize"))
            {
                if (cursor.TryFloat(arg, out float sampleSize))
                {
                    options = options with { MaxDispSampleSize = sampleSize };
                }
            }
            else if (Is(arg, "-lights"))
            {
                if (cursor.TryValue(arg, out string lights))
                {
                    options = options with { LightsFile = lights };
                }
            }
            else if (Is(arg, "-threads"))
            {
                // RECORDED, not dropped. It does not belong on the options
                // record -- parallelism is CompileParallelism's -- but throwing
                // it away meant `-threads N` was accepted and silently had no
                // effect. The host maps this onto MaxDegree.
                if (cursor.TryInt(arg, out int threads))
                {
                    requestedThreads = threads;
                }
            }
            else if (Is(arg, "-dump") || Is(arg, "-dumpnormals")
                || Is(arg, "-dumptrace") || Is(arg, "-dumppropmaps") || Is(arg, "-loghash"))
            {
                cursor.Drop(arg, "debug dumps to files are reported as diagnostics instead");
            }
            else if (Is(arg, "-low"))
            {
                cursor.Drop(arg, "process priority belongs to whoever owns the process");
            }
            else if (Is(arg, "-StopOnExit"))
            {
                cursor.Drop(arg, "a library does not wait for a keypress");
            }
            else if (Is(arg, "-scale"))
            {
                // This and the next three are debug-build
                // flags (a guard a release build defines as false): a release
                // stock vrad rejects them.
                // They are accepted here because the options they set exist
                // on VradOptions; -sky and -coring, which set variables
                // nothing reads, stay unknown.
                if (cursor.TryFloat(arg, out float scale))
                {
                    options = options with { LightScale = scale };
                }
            }
            else if (Is(arg, "-dlight"))
            {
                if (cursor.TryFloat(arg, out float threshold))
                {
                    options = options with { DLightThreshold = threshold };
                }
            }
            else if (Is(arg, "-ambient"))
            {
                // Three values, each times 128.
                if (cursor.TryFloat(arg, out float r)
                    && cursor.TryFloat(arg, out float g)
                    && cursor.TryFloat(arg, out float b))
                {
                    options = options with
                    {
                        Ambient = new SourceSharp.MapFormats.Geometry.Vec3(r * 128, g * 128, b * 128),
                    };
                }
            }
            else if (Is(arg, "-notexscale"))
            {
                options = options with { TexScale = false };
            }
            else if (Is(arg, "-compliance"))
            {
                if (TryCompliance(cursor, arg, out ComplianceOptions compliance))
                {
                    options = options with { Compliance = compliance };
                }
            }
            else if (Is(arg, "-gpu"))
            {
                // Opt-in GPU ray tracing (plan 10c). The value pins the
                // device by substring; an empty pin means "any capable
                // device". Not stock's vocabulary — the host's seam is
                // CompileRequest.TracerFactory; recorded like -threads is.
                if (cursor.TryValue(arg, out string match))
                {
                    gpuDeviceMatch = match;
                }
            }
            else if (Is(arg, "-gpu_slabs"))
            {
                if (cursor.TryInt(arg, out int slab))
                {
                    if (slab < 1)
                    {
                        cursor.OutOfRange(arg, "must be at least 1");
                    }
                    else
                    {
                        gpuRaysPerSlab = slab;
                    }
                }
            }
            else
            {
                Common(cursor, arg, options.Verbose);
            }
        }

        cursor.RequireMapPath();
        return cursor.Finish(options) with
        {
            Threads = requestedThreads,
            GpuDeviceMatch = gpuDeviceMatch,
            GpuRaysPerSlab = gpuRaysPerSlab,
        };
    }

    /// <summary>
    /// The branches every tool shares: content paths, launcher no-ops, VMPI,
    /// the map path, and finally the unknown-option diagnostic.
    /// </summary>
    private static void Common(ArgCursor cursor, string arg, bool verbose)
    {
        if (Is(arg, "-game") || Is(arg, "-vproject"))
        {
            if (cursor.TryValue(arg, out string dir))
            {
                cursor.SetGameDirectory(dir, verbose);
            }
        }
        else if (Is(arg, "-insert_search_path"))
        {
            if (cursor.TryValue(arg, out string path))
            {
                cursor.AddSearchPath(path);
            }
        }
        else if (Is(arg, "-novconfig") || Is(arg, "-allowdebug") || Is(arg, "-steam")
            || Is(arg, "-FullMinidumps"))
        {
            cursor.Drop(arg, "launcher plumbing, already a no-op in stock's own parse loop");
        }
        else if (Is(arg, "-mpi_pw"))
        {
            cursor.DropWithValue(arg, "VMPI is Windows-only cluster mode and is dropped");
        }
        else if (arg.StartsWith("-mpi", StringComparison.OrdinalIgnoreCase))
        {
            cursor.Drop(arg, "VMPI is Windows-only cluster mode and is dropped");
        }
        else if (Is(arg, "-listcompliance"))
        {
            // Not a stock flag either; the partner of -compliance. The host
            // prints ComplianceCatalogue.Format for the tool and compiles
            // nothing, so a map path is not required alongside it.
            cursor.ListCompliance = true;
        }
        else if (arg.StartsWith('-'))
        {
            cursor.Unknown(arg);
        }
        else
        {
            cursor.SetMapPath(arg);
        }
    }

    /// <summary>
    /// Reads <c>-compliance &lt;mode&gt;[,&lt;+/-Quirk&gt;…]</c>: the baseline
    /// policy first, then per-quirk exceptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT a stock flag -- stock has no such concept, because stock has nothing
    /// to be compliant with. It is spelled like one so that a single command
    /// line can carry it alongside real stock options, which is how every host
    /// here drives the tools.
    /// </para>
    /// <para>
    /// A <c>+Quirk</c> token demands the quirk behave AS STOCK and <c>-Quirk</c>
    /// demands it behave correctly, whichever way the baseline falls — the
    /// tokens state the desired side; <see cref="ComplianceOptions.Except"/>
    /// carries the ones that differ from the baseline, which is exactly the
    /// machinery the flip facts already exercise
    /// (<see cref="ComplianceOptions.Emulates"/>), no new arithmetic. Quirk
    /// names match <see cref="StockQuirk"/> case-insensitively.
    /// </para>
    /// <para>
    /// An unrecognised mode or quirk is an error and leaves the policy alone,
    /// so <c>-compliance stcok</c> compiles nothing rather than quietly
    /// compiling the wrong thing. That is the opposite of stock's habit with
    /// <c>atoi</c>, and deliberately so: this flag decides what the output
    /// means.
    /// </para>
    /// </remarks>
    private static bool TryCompliance(ArgCursor cursor, string arg, out ComplianceOptions value)
    {
        value = ComplianceOptions.Correct;
        if (!cursor.TryValue(arg, out string text))
        {
            return false;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            cursor.Malformed(arg, text, "'correct' or 'stock', optionally followed by ,+Quirk or ,-Quirk");
            return false;
        }

        CompliancePolicy policy;
        if (string.Equals(parts[0], "correct", StringComparison.OrdinalIgnoreCase))
        {
            policy = CompliancePolicy.Correct;
        }
        else if (string.Equals(parts[0], "stock", StringComparison.OrdinalIgnoreCase))
        {
            policy = CompliancePolicy.Stock;
        }
        else
        {
            cursor.Malformed(arg, parts[0], "'correct' or 'stock'");
            return false;
        }

        HashSet<StockQuirk> except = [];
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            char sign = part[0];
            if (sign is not ('+' or '-'))
            {
                cursor.Malformed(
                    arg,
                    part,
                    "a quirk name with a leading '+' (behave as stock) or '-' (behave correctly)");
                return false;
            }

            if (!Enum.TryParse(part[1..], ignoreCase: true, out StockQuirk quirk)
                || !Enum.IsDefined(quirk))
            {
                cursor.Add(
                    StockArgsCodes.ValueOutOfRange,
                    DiagnosticSeverity.Error,
                    $"-compliance quirk \"{part[1..]}\" is not a known quirk (see -listcompliance).");
                return false;
            }

            // The token names the wanted side; Except holds the quirks whose
            // side differs from the baseline. A later token for the same quirk
            // overwrites the earlier one.
            bool wantStock = sign == '+';
            bool baselineStock = policy == CompliancePolicy.Stock;
            if (wantStock != baselineStock)
            {
                except.Add(quirk);
            }
            else
            {
                except.Remove(quirk);
            }
        }

        value = new ComplianceOptions { Policy = policy, Except = except };
        return true;
    }

    /// <summary>
    /// Parses <c>-cooker native|vphysics|managed|none</c> (vbsp only; not a stock option;
    /// <c>vphysics</c> is an alias of <c>native</c>). Anything else is malformed rather than
    /// silently native, like <c>-compliance</c>.
    /// </summary>
    private static bool TryCooker(ArgCursor cursor, string arg, out CollisionCookerKind value)
    {
        value = CollisionCookerKind.Native;
        if (!cursor.TryValue(arg, out string text))
        {
            return false;
        }

        switch (text.ToLowerInvariant())
        {
            case "native":
            case "vphysics":
                return true;
            case "managed":
                value = CollisionCookerKind.Managed;
                return true;
            case "none":
                value = CollisionCookerKind.None;
                return true;
            default:
                cursor.Malformed(arg, text, "'native', 'vphysics', 'managed' or 'none'");
                return false;
        }
    }

    private static bool Is(string arg, string flag) =>
        string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A position in an argument list, plus everything the parse has learned so
    /// far.
    /// </summary>
    /// <remarks>
    /// An instance, never a static: two command lines may be parsed at once on
    /// two threads, and the library has no mutable static state at all -- a
    /// reflection fact in the test suite says so.
    /// </remarks>
    private sealed class ArgCursor(IReadOnlyList<string> args, string tool)
    {
        private readonly List<CompileDiagnostic> diagnostics = [];
        private readonly List<string> searchPaths = [];
        private int index;

        private string? mapPath;
        private string? gameDirectory;

        internal bool ListCompliance { get; set; }

        internal bool MoveNext(out string arg)
        {
            if (index >= args.Count)
            {
                arg = string.Empty;
                return false;
            }

            arg = At(index++);
            return true;
        }

        internal bool TryValue(string flag, out string value)
        {
            if (index >= args.Count)
            {
                value = string.Empty;
                Add(
                    StockArgsCodes.MissingValue,
                    DiagnosticSeverity.Error,
                    Say("expected a value after '" + flag + "'"));
                return false;
            }

            value = At(index++);
            if (value.Length == 0)
            {
                Add(
                    StockArgsCodes.MissingValue,
                    DiagnosticSeverity.Error,
                    Say("expected a value after '" + flag + "'"));
                return false;
            }

            return true;
        }

        internal bool TryInt(string flag, out int value)
        {
            value = 0;
            if (!TryValue(flag, out string text))
            {
                return false;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                Malformed(flag, text, "a whole number");
                return false;
            }

            return true;
        }

        internal bool TryFloat(string flag, out float value)
        {
            value = 0.0f;
            if (!TryValue(flag, out string text))
            {
                return false;
            }

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                Malformed(flag, text, "a number");
                return false;
            }

            return true;
        }

        internal void Unknown(string arg) =>
            Add(
                StockArgsCodes.UnknownOption,
                DiagnosticSeverity.Error,
                Say("unknown option '" + arg + "'"));

        internal void Drop(string flag, string reason) =>
            Add(
                StockArgsCodes.DroppedOption,
                DiagnosticSeverity.Warning,
                Say("'" + flag + "' is accepted and ignored: " + reason));

        internal void DropWithValue(string flag, string reason)
        {
            // Consume the value so it is not mistaken for the map path, but do
            // not complain about its contents: nothing will read it.
            // Stock vbsp and vvis consume the next token unconditionally;
            // vrad checks and errors. Consuming
            // only a NON-flag token differs from stock exactly on a line stock
            // itself mis-parses -- "-threads -v map" eats the -v there.
            if (index < args.Count && !At(index).StartsWith('-'))
            {
                index++;
            }

            Drop(flag, reason);
        }

        internal void OutOfRange(string flag, string requirement) =>
            Add(
                StockArgsCodes.ValueOutOfRange,
                DiagnosticSeverity.Error,
                Say("the value of '" + flag + "' " + requirement));

        internal void SetGameDirectory(string dir, bool verbose)
        {
            gameDirectory = dir;
            if (verbose)
            {
                Add(
                    StockArgsCodes.DroppedOption,
                    DiagnosticSeverity.Info,
                    Say("content is mounted by the host through IContentFileSystem; "
                        + "the game directory is recorded, not acted on"));
            }
        }

        internal void AddSearchPath(string path) => searchPaths.Add(path);

        internal void SetMapPath(string path)
        {
            if (mapPath is null)
            {
                mapPath = path;
                return;
            }

            Add(
                StockArgsCodes.TooManyMapPaths,
                DiagnosticSeverity.Error,
                Say("more than one map was named; keeping '" + mapPath + "' and ignoring '"
                    + path + "'"));
        }

        internal void RequireMapPath()
        {
            if (mapPath is null && !ListCompliance)
            {
                Add(
                    StockArgsCodes.MissingMapPath,
                    DiagnosticSeverity.Error,
                    Say("no map file was named"));
            }
        }

        internal void Add(string code, DiagnosticSeverity severity, string message) =>
            diagnostics.Add(new CompileDiagnostic(code, severity, message));

        internal StockArgsResult<TOptions> Finish<TOptions>(TOptions options) =>
            new(options, mapPath, diagnostics.ToArray(), gameDirectory, searchPaths.ToArray())
            {
                ListCompliance = ListCompliance,
            };

        private string At(int i)
        {
            string? value = args[i];
            return value ?? string.Empty;
        }

        internal void Malformed(string flag, string text, string expected) =>
            Add(
                StockArgsCodes.MalformedValue,
                DiagnosticSeverity.Error,
                Say("'" + flag + "' expected " + expected + " but was given '" + text + "'"));

        private string Say(string message) => tool + ": " + message;
    }
}
