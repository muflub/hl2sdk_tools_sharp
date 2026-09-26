//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Vis;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap vvis</c>: the visibility stage, spelled the way stock spells it.
/// </summary>
/// <remarks>
/// <para>
/// A thin client of <see cref="Vvis"/>. Everything this renders is a property
/// of the <see cref="VisResult"/> the library returned, so a host gets the same
/// numbers as data.
/// </para>
/// <para>
/// The command line is stock's, parsed by
/// <see cref="StockArgs.ParseVvis"/>, with two additions that belong to the
/// acceptance instruments rather than to vvis. They are spelled with two dashes
/// precisely so they cannot be mistaken for switches stock has, and they are
/// removed before the stock parser ever sees the arguments.
/// </para>
/// </remarks>
public static class VvisCommand
{
    /// <summary>The exit code for a map that could not be vised.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// Writes the decompressed visibility of the finished map to a file.
    /// </summary>
    /// <remarks>
    /// The BSP comparison instrument needs the BITS, and the
    /// lump holds them run-length coded against a cluster count, so "diff the
    /// two lumps" answers a question about the coder as much as about the
    /// visibility. This writes what the engine would decompress: a small header
    /// and then every PVS row followed by every PAS row.
    /// </remarks>
    public const string DumpVisSwitch = "--dump-vis";

    /// <summary>
    /// Skips the computation, so <see cref="DumpVisSwitch"/> describes the map
    /// as it arrived.
    /// </summary>
    /// <remarks>
    /// This is how STOCK's answer is read out for the comparison: run stock
    /// vvis, then dump the map it wrote. Without it there would be no way to
    /// compare against stock except by reimplementing the reader in a script.
    /// </remarks>
    public const string NoComputeSwitch = "--no-compute";

    /// <summary>
    /// Prints how long each stage took, to standard error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vvis scaling gate is judged on a serial-tail FRACTION, and a fraction
    /// cannot be measured from a total. Fitting <c>T(n) = S + P/n</c> to two
    /// wall times infers the tail from an assumption that the parallel half
    /// scales perfectly; this reads it off the clock instead, which is what
    /// makes "the tail is now x% of the wall time" a measurement rather than a
    /// model.
    /// </para>
    /// <para>
    /// Timed from the progress stream, so nothing in the library pays for it
    /// and nothing about the compile changes. <see cref="Vvis"/> reports a
    /// <c>0 of N</c> at the head of every stage precisely so this can be right:
    /// without it a stage of ONE item reports only when it has finished, and
    /// the whole of its duration is charged to whatever ran before it. That is
    /// not a hypothetical -- the first run of this switch charged a 2,480
    /// cluster merge to the portal flow and printed <c>Merge 0.001s</c>.
    /// </para>
    /// </remarks>
    public const string BenchSwitch = "--bench";

    /// <summary>
    /// Turns <see cref="VvisOptions.Tighten"/> on, explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE dash, unlike the instrument switches above, because it is a vvis
    /// option rather than an instrument: the tightening pass names it
    /// <c>-tighten</c>. Stock has no such switch, which is why
    /// <see cref="StockArgs.ParseVvis"/> deliberately does not know it (a fact
    /// pins that); it is taken off the command line here, before the stock
    /// parser sees it, the same way the instrument switches are.
    /// </para>
    /// <para>
    /// The tightened walk is the promoted default since the vis-repair lane
    /// (plan 2c: "promoted to default in Phase 5 only if that delta never
    /// ADDS a bit anywhere" — it never does; it is bit-identical to stock at
    /// <c>-threads 1</c>). This switch is the explicit re-assertion of the
    /// default, kept so scripts written against the pre-promotion CLI keep
    /// working and so the pair with <see cref="LooseSwitch"/> reads as an
    /// explicit choice rather than an absence.
    /// </para>
    /// </remarks>
    public const string TightenSwitch = "-tighten";

    /// <summary>
    /// Turns <see cref="VvisOptions.Tighten"/> off: the conservative walk that
    /// prunes with <c>portalflood</c> only, as this command shipped before the
    /// Phase-5 promotion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The opt-out the promotion needs. Not a stock spelling either — stock
    /// has no tightening to opt out of — so like <see cref="TightenSwitch"/>
    /// it is taken off the command line before <see cref="StockArgs"/> sees
    /// it, and <see cref="StockArgs.ParseVvis"/> deliberately knows neither.
    /// The untightened walk prunes strictly less than stock does at ANY
    /// thread count (2fort: 569.6M chains against the tightened walk's
    /// 145.4M, per the vis-repair ledger), so it exists for the equal-arms
    /// comparisons and the superset facts, not for shipping.
    /// </para>
    /// <para>
    /// Giving this and <see cref="TightenSwitch"/> is a usage error rather
    /// than a last-wins: the two arms differ by a factor of four in work, so
    /// silently picking one would be a guess at intent.
    /// </para>
    /// </remarks>
    public const string LooseSwitch = "-loose";

    /// <summary>The magic a dump file starts with.</summary>
    public const string DumpMagic = "SSVIS1";

    /// <summary>Runs one <c>vvis</c> invocation.</summary>
    /// <param name="fileSystem">Where maps are read and written.</param>
    /// <param name="args">The arguments after <c>vvis</c>.</param>
    /// <param name="output">Where the running commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>
    /// <see cref="Program.ExitSuccess"/>, <see cref="ExitFailed"/> or
    /// <see cref="Program.ExitUsage"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        List<string> remaining = [];
        string? dumpPath = null;
        bool compute = true;
        bool bench = false;
        bool? tighten = null;

        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], DumpVisSwitch, StringComparison.Ordinal) && i + 1 < args.Count)
            {
                dumpPath = args[++i];
            }
            else if (string.Equals(args[i], NoComputeSwitch, StringComparison.Ordinal))
            {
                compute = false;
            }
            else if (string.Equals(args[i], BenchSwitch, StringComparison.Ordinal))
            {
                bench = true;
            }
            else if (string.Equals(args[i], TightenSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (tighten is false)
                {
                    await output.WriteLineAsync(
                        "ssmap vvis: give -tighten or -loose, not both").ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                tighten = true;
            }
            else if (string.Equals(args[i], LooseSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (tighten is true)
                {
                    await output.WriteLineAsync(
                        "ssmap vvis: give -tighten or -loose, not both").ConfigureAwait(false);
                    return Program.ExitUsage;
                }

                tighten = false;
            }
            else
            {
                // -threads used to be pre-scanned here as well as parsed by
                // StockArgs, which dropped it with the diagnostic "accepted and
                // ignored: use CompileParallelism". The flag WAS honoured -- the
                // pre-scan saw to that -- so the message was simply false, and
                // it cost real time: a round of crash probing concluded that
                // every thread count had run at the same degree, and discarded
                // a genuine signal on that basis. Measured afterwards on one
                // map: 0.595 s at -threads 1, 0.286 at 4, 0.271 at 32.
                //
                // StockArgs now RECORDS the value on the result instead of
                // dropping it, so there is one parser, no duplicate scan, and
                // no diagnostic claiming the opposite of what happens.
                remaining.Add(args[i]);
            }
        }

        StockArgsResult<VvisOptions> parsed = StockArgs.ParseVvis(remaining);

        if (parsed.ListCompliance && !parsed.HasErrors)
        {
            await output.WriteAsync(ComplianceCatalogue.Format(CompileTools.Vvis)).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}")
                .ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync("usage: ssmap vvis [stock options] <map.bsp>")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        string mapArgument = parsed.MapPath;
        string bspPath = Path.GetFullPath(
            mapArgument.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)
                ? mapArgument
                : mapArgument + ".bsp");
        string prtPath = Path.ChangeExtension(bspPath, ".prt");

        if (!VPath.TryCreate(bspPath, out VPath bsp))
        {
            await output.WriteLineAsync($"ssmap vvis: \"{bspPath}\" is not a usable path")
                .ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!await fileSystem.ExistsAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync($"ssmap vvis: no such file: {bspPath}").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        await output.WriteLineAsync($"reading {bspPath}").ConfigureAwait(false);

        BspData map;
        await using (Stream stream =
            await fileSystem.OpenReadAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            map = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        VisResult? result = null;

        if (compute)
        {
            if (!VPath.TryCreate(prtPath, out VPath prt) ||
                !await fileSystem.ExistsAsync(prt, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"ssmap vvis: couldn't read {prtPath}").ConfigureAwait(false);
                return ExitFailed;
            }

            await output.WriteLineAsync($"reading {prtPath}").ConfigureAwait(false);

            PortalFile portalFile;
            await using (Stream stream =
                await fileSystem.OpenReadAsync(prt, cancellationToken).ConfigureAwait(false))
            {
                portalFile = await PortalFile.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync(
                $"{portalFile.ClusterCount,4} portalclusters").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"{portalFile.Portals.Count,4} numportals").ConfigureAwait(false);

            PortalSet portals = PortalSet.FromPortalFile(portalFile);

            StageClock? clock = bench ? new StageClock() : null;

            VisContext context = new()
            {
                // `tighten is null` leaves the parsed record alone, which is
                // the promoted default: tightening on.
                Options = tighten is null
                    ? parsed.Options
                    : parsed.Options with { Tighten = tighten.Value },
                Parallelism = parsed.Threads is int degree && degree > 0
                    ? new CompileParallelism { MaxDegree = degree }
                    : CompileParallelism.Default,
                Progress = clock,
            };

            result = await Vvis.ComputeAsync(map, portals, context, cancellationToken)
                .ConfigureAwait(false);

            if (clock is not null)
            {
                foreach (string line in clock.Report())
                {
                    await output.WriteLineAsync(line).ConfigureAwait(false);
                }

                VisWorkCounters work = result.Work;
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"bench work chains={work.Chains} candidates={work.Candidates} "
                    + $"separators={work.SeparatorClips} rays={work.BaseRays}")).ConfigureAwait(false);
            }

            if (result.Trace is not null)
            {
                await output.WriteLineAsync(
                    $"traced {result.Trace.Count} points from cluster "
                    + $"{parsed.Options.Trace!.Value.From} to {parsed.Options.Trace!.Value.To}")
                    .ConfigureAwait(false);

                // Stock writes no .bsp for a trace.
                return Program.ExitSuccess;
            }

            await WriteStatisticsAsync(result, output).ConfigureAwait(false);

            await output.WriteLineAsync($"writing {bspPath}").ConfigureAwait(false);
            await using Stream write =
                await fileSystem.OpenWriteAsync(bsp, cancellationToken).ConfigureAwait(false);
            await BspFile.SaveAsync(map, write, BspWriteMode.Canonical, cancellationToken)
                .ConfigureAwait(false);
        }

        if (dumpPath is not null)
        {
            string full = Path.GetFullPath(dumpPath);
            if (!VPath.TryCreate(full, out VPath dump))
            {
                await output.WriteLineAsync($"ssmap vvis: \"{dumpPath}\" is not a usable path")
                    .ConfigureAwait(false);
                return Program.ExitUsage;
            }

            await DumpAsync(fileSystem, map, result, dump, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"dumped visibility to {full}").ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    /// <summary>
    /// Turns the progress stream into a per-stage wall clock, for
    /// <see cref="BenchSwitch"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every worker reports after every completed item, so this is called
    /// millions of times from every thread at once, and a bench run that spent
    /// even tens of nanoseconds of contention per report would measure its own
    /// instrument (T15-findings §4). The design therefore keeps the hot path
    /// entirely off any lock: one volatile read of the current segment, one
    /// reference compare against the reported stage, return. The stage names
    /// are literal constants on <see cref="Vvis"/>, so reference equality
    /// holds for every report of the same stage. The read is VOLATILE, which
    /// the first implementation's was not: a cached or hoisted read of a stage
    /// another thread has already replaced makes a worker think it is looking
    /// at a transition and walk into the shared bookkeeping — millions of
    /// times, behind one lock, in <c>Monitor</c> spin that burns exactly the
    /// CPU the bench is trying to measure.
    /// </para>
    /// <para>
    /// State lives in one immutable segment record (the running stage and the
    /// tick its current stretch began) plus per-stage accumulators that only
    /// ever move by <c>Interlocked.Add</c> on the slot's tick field. A transition
    /// is a compare-and-swap of the segment: the winner charges the stretch it
    /// ended to that stage's accumulator and publishes the new stage; losers
    /// re-check and return. Because the stage and its start tick are published
    /// together in one reference, there is no window in which a racing winner
    /// sees a new stage beside an old start tick and bills a phantom interval,
    /// and no lock holder can be preempted holding the pen. Transitions are a
    /// handful per run (seven stages); everything else completes without
    /// touching shared writable state.
    /// </para>
    /// <para>
    /// <see cref="Report()"/> is a pure snapshot: the open stretch is added to
    /// its owner's row without closing it, so reading the ledger mid-run — a
    /// service polling progress — costs no time to anyone. Closing it, as the
    /// first implementation did, re-opened the stage's next stretch from the
    /// read time and billed the wall between the read and the next report to
    /// nobody.
    /// </para>
    /// <para>
    /// The stage ORDER is the order stages were first seen, which is the order
    /// the compile ran them in. A stage that appears twice is folded into one
    /// row with its total, so a tail split into several passes still adds up.
    /// </para>
    /// </remarks>
    public sealed class StageClock : IProgress<CompileProgress>
    {
        /// <summary>One running stage and when its current stretch began.</summary>
        /// <param name="Stage">The stage, or null before the first report.</param>
        /// <param name="StartTicks">The tick the stretch opened at.</param>
        private sealed record Segment(string? Stage, long StartTicks);

        /// <summary>One stage's finished ticks; moves only by <c>Interlocked.Add</c>.</summary>
        private sealed class StageSlot
        {
            public long Ticks;
        }

        private readonly Func<long> _nowProvider;
        private readonly long _frequency;
        private readonly long _startTicks;
        private readonly object _register = new();

        // Published only under _register, never mutated afterwards: reads go
        // through Volatile.Read and are lock-free because the only writer
        // swaps in a fresh dictionary with Volatile.Write.
        private Dictionary<string, StageSlot> _slots = new(StringComparer.Ordinal);
        private string[] _order = [];

        private Segment _segment;

        /// <summary>Wall clock on the real tick source.</summary>
        public StageClock()
            : this(static () => System.Diagnostics.Stopwatch.GetTimestamp(),
                   System.Diagnostics.Stopwatch.Frequency)
        {
        }

        /// <summary>
        /// A clock whose time source is supplied by the caller.
        /// </summary>
        /// <param name="nowProvider">Returns the current tick count.</param>
        /// <param name="frequency">Ticks per second for that count.</param>
        /// <remarks>
        /// The seam exists so a fact can script a run — reports at named
        /// timestamps — and assert the exact ledger text without measuring a
        /// wall, which on a shared box no deterministic fact may do.
        /// </remarks>
        public StageClock(Func<long> nowProvider, long frequency)
        {
            ArgumentNullException.ThrowIfNull(nowProvider);
            ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1L);
            _nowProvider = nowProvider;
            _frequency = frequency;
            _startTicks = nowProvider();
            _segment = new Segment(null, _startTicks);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The hot path — a report for the stage already running — is one
        /// volatile read and one reference compare, no lock and no
        /// allocation. Only a real stage change runs any further.
        /// </remarks>
        public void Report(CompileProgress value)
        {
            if (ReferenceEquals(Volatile.Read(ref _segment).Stage, value.Stage))
            {
                return;
            }

            Advance(value.Stage);
        }

        /// <summary>
        /// Claims the transition to <paramref name="stage"/> and bills the
        /// stretch it ends, or returns when another thread already did.
        /// </summary>
        private void Advance(string stage)
        {
            while (true)
            {
                Segment observed = Volatile.Read(ref _segment);
                long now = _nowProvider();
                if (ReferenceEquals(observed.Stage, stage))
                {
                    return; // someone else moved to my stage while I read the clock
                }

                // The stage must have a home before its segment is visible.
                Register(stage);
                if (Interlocked.CompareExchange(
                        ref _segment, new Segment(stage, now), observed) != observed)
                {
                    continue; // a rival transition won; reconsider against its segment
                }

                if (observed.Stage is string closed)
                {
                    Interlocked.Add(ref Slot(closed).Ticks, Math.Max(0, now - observed.StartTicks));
                }

                return;
            }
        }

        /// <summary>The ledger rows: one per stage in run order, then the total.</summary>
        /// <returns>The <c>bench</c> lines, culture-invariant.</returns>
        /// <remarks>
        /// A snapshot, never a mutation: the open stretch is added to its
        /// owner's row for the print without closing it.
        /// </remarks>
        public IEnumerable<string> Report()
        {
            long now = _nowProvider();
            Segment open = Volatile.Read(ref _segment);
            string[] order = Volatile.Read(ref _order);
            Dictionary<string, StageSlot> slots = Volatile.Read(ref _slots);

            List<string> lines = [];
            foreach (string stage in order)
            {
                long ticks = Interlocked.Read(ref slots[stage].Ticks);
                if (ReferenceEquals(open.Stage, stage))
                {
                    ticks += Math.Max(0, now - open.StartTicks);
                }

                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"bench {stage} {ticks / (double)_frequency:F3}s"));
            }

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"bench total {(now - _startTicks) / (double)_frequency:F3}s"));
            return lines;
        }

        /// <summary>
        /// Makes a stage name known, once. Cold: runs only the first time a
        /// stage is seen, under a lock no hot-path report ever takes.
        /// </summary>
        private void Register(string stage)
        {
            if (Volatile.Read(ref _slots).ContainsKey(stage))
            {
                return;
            }

            lock (_register)
            {
                if (_slots.ContainsKey(stage))
                {
                    return;
                }

                Dictionary<string, StageSlot> slots = new(_slots, StringComparer.Ordinal);
                slots[stage] = new StageSlot();

                // Slots first: a reader that sees the new order must always
                // find the row it names.
                Volatile.Write(ref _slots, slots);
                Volatile.Write(ref _order, [.. _order, stage]);
            }
        }

        private StageSlot Slot(string stage) => Volatile.Read(ref _slots)[stage];
    }

    private static async Task WriteStatisticsAsync(VisResult result, TextWriter output)
    {
        double percent = result.TotalVisibleClusters == 0
            ? 0
            : result.OptimizedClusters * 100.0 / result.TotalVisibleClusters;

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Optimized: {result.OptimizedClusters} visible clusters ({percent:F2}%)"))
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Total clusters visible: {result.TotalVisibleClusters}").ConfigureAwait(false);

        if (result.ClusterCount > 0)
        {
            await output.WriteLineAsync(
                $"Average clusters visible: {result.TotalVisibleClusters / result.ClusterCount}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Average clusters audible: {result.TotalAudibleClusters / result.ClusterCount}")
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync($"visdatasize:{result.VisDataSize}").ConfigureAwait(false);
        await output.WriteLineAsync($"deepest portal flow: {result.DeepestFlow}").ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the decompressed visibility of a map: the magic, the cluster
    /// count, the row length, then every PVS row and every PAS row.
    /// </summary>
    private static async Task DumpAsync(
        IFileSystem fileSystem,
        BspData map,
        VisResult? result,
        VPath path,
        CancellationToken cancellationToken)
    {
        int clusters;
        int rowBytes;
        byte[] payload;

        if (result is not null)
        {
            clusters = result.ClusterCount;
            rowBytes = result.RowBytes;
            payload = new byte[clusters * rowBytes * 2];
            for (int c = 0; c < clusters; c++)
            {
                result.Pvs(c).CopyTo(payload.AsSpan(c * rowBytes, rowBytes));
                result.Pas(c).CopyTo(payload.AsSpan(((clusters + c) * rowBytes), rowBytes));
            }
        }
        else
        {
            VisibilityLump? lump = VisibilityLump.Read(map[BspLump.Visibility]);
            if (lump is null)
            {
                clusters = 0;
                rowBytes = 0;
                payload = [];
            }
            else
            {
                clusters = lump.NumClusters;
                rowBytes = lump.RowBytes();
                payload = new byte[clusters * rowBytes * 2];
                ReadOnlySpan<byte> bytes = map[BspLump.Visibility].Data.Span;

                for (int c = 0; c < clusters; c++)
                {
                    lump.DecompressRow(
                        bytes[lump.BitOffset(c, VisibilityLump.Pvs)..],
                        payload.AsSpan(c * rowBytes, rowBytes));
                    lump.DecompressRow(
                        bytes[lump.BitOffset(c, VisibilityLump.Pas)..],
                        payload.AsSpan((clusters + c) * rowBytes, rowBytes));
                }
            }
        }

        byte[] header = new byte[DumpMagic.Length + 8];
        for (int i = 0; i < DumpMagic.Length; i++)
        {
            header[i] = (byte)DumpMagic[i];
        }

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(DumpMagic.Length, 4), clusters);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(DumpMagic.Length + 4, 4), rowBytes);

        await using Stream file =
            await fileSystem.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await file.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await file.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
