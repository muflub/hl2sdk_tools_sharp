using SourceSharp.MapTools.Diagnostics;

namespace SourceSharp.MapTools.Options;

/// <summary>
/// The diagnostic codes <see cref="StockArgs"/> produces.
/// </summary>
/// <remarks>
/// <para>
/// One <c>ARGS</c> family rather than three tool-specific ones, because the
/// parser is one piece of code: "unknown option" means the same thing whichever
/// tool was asked, and a host that wants to key on it should not have to key on
/// three codes. Which tool produced it is in the message.
/// </para>
/// <para>
/// These are stable identifiers, so a code may never be reused for a different
/// meaning -- see <see cref="CompileDiagnostic.Code"/>. The message text may be
/// reworded freely.
/// </para>
/// </remarks>
public static class StockArgsCodes
{
    /// <summary>An option this tool does not have.</summary>
    /// <remarks>
    /// Error. Stock prints its usage and calls <c>exit()</c> here; this port
    /// reports and keeps parsing, so a command line with three typos in it
    /// produces three diagnostics and not one.
    /// </remarks>
    public const string UnknownOption = "ARGS0001";

    /// <summary>An option that takes a value was the last thing on the line.</summary>
    /// <remarks>
    /// Error. Stock vbsp and vvis do not check this at all -- they read
    /// <c>argv[i+1]</c> past the end of the array -- so this is a diagnostic
    /// where stock has undefined behaviour.
    /// </remarks>
    public const string MissingValue = "ARGS0002";

    /// <summary>An option's value is not a number.</summary>
    /// <remarks>
    /// Error. Stock uses <c>atoi</c>/<c>atof</c>, which silently return 0 for
    /// text that is not a number, so <c>-bounce banana</c> is a quiet request
    /// for zero bounces in stock and a diagnostic naming the flag here.
    /// </remarks>
    public const string MalformedValue = "ARGS0003";

    /// <summary>An option's value is outside the range stock accepts.</summary>
    /// <remarks>Error, matching the ranges stock's own parse loop enforces.</remarks>
    public const string ValueOutOfRange = "ARGS0004";

    /// <summary>No map was named on the command line.</summary>
    /// <remarks>Error.</remarks>
    public const string MissingMapPath = "ARGS0005";

    /// <summary>More than one map was named on the command line.</summary>
    /// <remarks>Error. The first is kept, so the rest of the parse still runs.</remarks>
    public const string TooManyMapPaths = "ARGS0006";

    /// <summary>
    /// A real stock option that this port does not implement, accepted and
    /// ignored.
    /// </summary>
    /// <remarks>
    /// Warning, not an error: a Hammer command line carrying <c>-low</c> or
    /// <c>-FullMinidumps</c> should still compile the map. The message says why
    /// the option is gone. The full list, with reasons, is in the remarks on
    /// <see cref="VbspOptions"/>, <see cref="VvisOptions"/> and
    /// <see cref="VradOptions"/>.
    /// </remarks>
    public const string DroppedOption = "ARGS0007";

    /// <summary>Two options that contradict each other, or that stock refuses together.</summary>
    /// <remarks>Error where stock exits, warning where stock merely does something useless.</remarks>
    public const string ConflictingOptions = "ARGS0008";
}

/// <summary>
/// What a stock command line turned out to be asking for.
/// </summary>
/// <typeparam name="TOptions">
/// The tool's option record: <see cref="VbspOptions"/>,
/// <see cref="VvisOptions"/> or <see cref="VradOptions"/>.
/// </typeparam>
/// <param name="Options">
/// The options, with every flag that was not mentioned left at its stock
/// default. Always a usable value, even when <paramref name="Diagnostics"/>
/// contains errors -- there is no partially-constructed state to hand back.
/// </param>
/// <param name="MapPath">
/// The map the command line named, exactly as written and with its extension
/// left alone, or null when none was given. Stock strips the extension and
/// lowercases the basename inside <c>RunVBSP</c>;
/// that is a decision for whoever resolves the path, not for the parser.
/// </param>
/// <param name="Diagnostics">
/// Everything the parser wants to say, in the order it found it. Empty on a
/// clean line.
/// </param>
/// <param name="GameDirectory">
/// The value of <c>-game</c> or <c>-vproject</c>, recorded and not acted on.
/// </param>
/// <param name="ExtraSearchPaths">
/// The values of every <c>-insert_search_path</c>, in order, recorded and not
/// acted on.
/// </param>
/// <remarks>
/// <para>
/// <b>The library does not act on <see cref="GameDirectory"/> or
/// <see cref="ExtraSearchPaths"/>.</b> Stock skips both flags in its parse loop
/// because
/// <c>CmdLib_InitFileSystem</c> has already read them off the global command
/// line and mounted the game's search paths as a side effect. There is no
/// global command line here and no mounting side effect: content comes from the
/// <see cref="SourceSharp.MapTools.Io.IContentFileSystem"/> the host hands the
/// compile. These two fields exist so that a host holding a genuine Hammer
/// command line can see what it said and mount accordingly -- they are an
/// input to the host's decision, not an instruction the library follows.
/// </para>
/// <para>
/// There is no exit code here and nothing throws for a bad line. Stock's
/// <c>Error()</c> calls <c>exit()</c>, which is precisely why the RPG plan
/// needed one process per compile.
/// </para>
/// </remarks>
public sealed record StockArgsResult<TOptions>(
    TOptions Options,
    string? MapPath,
    IReadOnlyList<CompileDiagnostic> Diagnostics,
    string? GameDirectory,
    IReadOnlyList<string> ExtraSearchPaths)
{
    /// <summary>
    /// The value of <c>-threads</c>, for the tools where it means something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT an option on <c>VvisOptions</c> or <c>VradOptions</c>: how much of
    /// the machine to use is <c>CompileParallelism</c>'s, and a stage must not
    /// be able to read a thread count out of its own options record. But it is
    /// not something to discard either, and discarding it was a real defect —
    /// <c>ssmap vvis -threads 8</c> reported the flag as accepted-and-ignored
    /// and then ran at the default degree, which silently invalidated a whole
    /// round of crash probing that thought it was varying the thread count.
    /// </para>
    /// <para>
    /// So it is recorded here, for the host to map onto
    /// <c>CompileParallelism.MaxDegree</c>. Null when the flag was absent.
    /// vbsp records it too, although stock vbsp ignores it — it parses the
    /// flag and then overwrites it with <c>numthreads = 1</c>:
    /// this port's vbsp has parallel stages (plan 3p), and they write the same
    /// bytes at every degree.
    /// </para>
    /// </remarks>
    public int? Threads { get; init; }

    /// <summary>
    /// Whether the line carried <c>-listcompliance</c>: print
    /// <see cref="ComplianceCatalogue.Format"/> for this tool and compile
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Accepted by all three tools. With it, a missing map path is not an
    /// error, the way a usage request is not.
    /// </remarks>
    public bool ListCompliance { get; init; }

    /// <summary>
    /// The format-family flags the vbsp line said, as one overlay
    /// (<see cref="FormatOverrides"/>), or null when it said none.
    /// </summary>
    /// <remarks>
    /// NOT on <see cref="VbspOptions"/>: the RESOLVED format lives there
    /// (<c>VbspOptions.Format</c>), and that field is the host's after
    /// <see cref="FormatResolution.Resolve"/> ran; this field is the raw CLI
    /// overlay the resolver consumes, parse output only. Preset flags and
    /// manual flags share one accumulator in argument order, which is how the
    /// reference stores its state as tokens are walked — <c>-csgo -bspformat 19</c>
    /// writes 19, <c>-bspformat 19 -csgo</c> writes 21.
    /// </remarks>
    public FormatOverrides? Format { get; init; }

    /// <summary>The last preset flag the line named, or null.</summary>
    public string? PresetName { get; init; }

    /// <summary>Whether the line carried <c>-noformatdetect</c>.</summary>
    public bool NoFormatDetect { get; init; }

    /// <summary>Whether the line carried <c>-notoolsargs</c>.</summary>
    public bool NoToolsArgs { get; init; }

    /// <summary>
    /// Whether the line carried <c>-incremental</c>:
    /// reuse the map's cooked-collision cache instead of re-cooking unchanged
    /// models. Opt-in, like every speed-up that changes what is trusted: a
    /// line without it compiles exactly as before.
    /// </summary>
    /// <remarks>
    /// NOT on <see cref="VbspOptions"/>: the cache is the host's, not vbsp's —
    /// stock vbsp has no such option (this is the plan's 10a verb, not a
    /// stock flag), and a stock option record may not grow non-stock members.
    /// Like <see cref="Threads"/> it is recorded here for the host to act on.
    /// </remarks>
    public bool Incremental { get; init; }

    /// <summary>
    /// The directory <c>-cache-dir</c> named for the cache file, or null for
    /// the plan's default (Q7: <c>&lt;map&gt;.sscache.db</c> beside the map).
    /// Null unless <see cref="Incremental"/> is also set.
    /// </summary>
    public string? CachePath { get; init; }

    /// <summary>
    /// Whether the line carried <c>-nocache</c>: run cold even when the
    /// incremental cache would otherwise apply. Wins over
    /// <see cref="Incremental"/>, the way stock's negations win.
    /// </summary>
    public bool NoCache { get; init; }

    /// <summary>
    /// The substring <c>-gpu</c> pinned the ray-tracing device to, or null
    /// when the line asked for no GPU (<c>-gpu</c> is
    /// opt-in; absence of a device is a clean CPU fallback, never an error).
    /// </summary>
    /// <remarks>
    /// NOT on <see cref="VradOptions"/>: the tracer is the host's seam
    /// (<c>CompileRequest.TracerFactory</c>), not a lighting option, and the
    /// options record is stock's vocabulary. Recorded for the host, like
    /// <see cref="Threads"/>.
    /// </remarks>
    public string? GpuDeviceMatch { get; init; }

    /// <summary>
    /// The rays-per-dispatch <c>-gpu_slabs</c> asked for, or null for the
    /// backend's default. Only meaningful with <see cref="GpuDeviceMatch"/>.
    /// </summary>
    public int? GpuRaysPerSlab { get; init; }


    /// <summary>
    /// Whether any diagnostic is an error, and so whether the command line
    /// asked for something that cannot be done.
    /// </summary>
    public bool HasErrors
    {
        get
        {
            for (int i = 0; i < Diagnostics.Count; i++)
            {
                if (Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
