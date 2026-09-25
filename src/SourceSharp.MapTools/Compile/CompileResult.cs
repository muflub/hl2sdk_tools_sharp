using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapTools.Compile;

/// <summary>How long one stage of a chain took.</summary>
/// <param name="Stage">The stage: <c>load</c>, <c>vbsp</c>, <c>vvis</c>, <c>vrad</c> or <c>write</c>.</param>
/// <param name="Elapsed">Its wall time.</param>
public readonly record struct CompileStageTiming(string Stage, TimeSpan Elapsed);

/// <summary>What one <see cref="MapCompiler.CompileAsync"/> produced.</summary>
/// <remarks>
/// Errors in the map are data here (<see cref="Diagnostics"/>,
/// <see cref="Leak"/>), never exceptions: a leaked map is a result with a
/// <see cref="LeakReport"/> whose path a host can draw.
/// </remarks>
public sealed class CompileResult
{
    internal CompileResult(
        string mapName,
        VbspResult vbsp,
        VisResult? vis,
        RadResult? rad,
        IReadOnlyList<CompileDiagnostic> diagnostics,
        IReadOnlyList<CompileStageTiming> timings,
        IReadOnlyList<VPath> written,
        IReadOnlyList<string> log)
    {
        MapName = mapName;
        Vbsp = vbsp;
        Vis = vis;
        Rad = rad;
        Diagnostics = diagnostics;
        Timings = timings;
        Written = written;
        Log = log;
    }

    /// <summary>The map's name, from its source.</summary>
    public string MapName { get; }

    /// <summary>
    /// The run's cache counters (plan_maptools.md ruling Q12: the same data
    /// the printed report carries); null when the run had no cache.
    /// </summary>
    public Compile.Cache.CacheRunCounters? Cache { get; internal set; }

    /// <summary>
    /// The finished map, in memory: vbsp's output after vvis and vrad changed
    /// it in place. Null only when <c>-leaktest</c> stopped vbsp at a leak.
    /// </summary>
    public BspData? Bsp => Vbsp.Bsp;

    /// <summary>The portal file vvis read, or null when the map leaked or has no sealed interior.</summary>
    public PortalFile? Portals => Vbsp.Portals;

    /// <summary>The leak, when the world is not sealed; <c>.lin</c> is one rendering of it.</summary>
    public LeakReport? Leak => Vbsp.Leak;

    /// <summary>vbsp's own result.</summary>
    public VbspResult Vbsp { get; }

    /// <summary>vvis's result, or null when vvis could not run (no portal file) or vbsp stopped.</summary>
    public VisResult? Vis { get; }

    /// <summary>vrad's result, or null when vbsp stopped.</summary>
    public RadResult? Rad { get; }

    /// <summary>
    /// Every diagnostic of the chain, in the order the stages raised them:
    /// vbsp's, the chain's own (<see cref="MapCompilerCodes"/>), vrad's.
    /// </summary>
    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }

    /// <summary>Wall time per stage, in the order they ran.</summary>
    public IReadOnlyList<CompileStageTiming> Timings { get; }

    /// <summary>The files written, in the order they were written; empty for <see cref="CompileOutput.InMemory"/>.</summary>
    public IReadOnlyList<VPath> Written { get; }

    /// <summary>The commentary, line by line: what the <c>.log</c> gained from this compile.</summary>
    public IReadOnlyList<string> Log { get; }

    /// <summary>Whether the chain produced a map: false only when vbsp stopped (<c>-leaktest</c> on a leak).</summary>
    public bool Succeeded => Bsp is not null;

    /// <summary>The sum of <see cref="Timings"/>.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (CompileStageTiming timing in Timings)
            {
                total += timing.Elapsed;
            }

            return total;
        }
    }
}
