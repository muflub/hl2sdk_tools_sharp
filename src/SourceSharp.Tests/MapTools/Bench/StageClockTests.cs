//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapCompile;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Vis;

using Xunit;

using StageClock = SourceSharp.MapCompile.VvisCommand.StageClock;

namespace SourceSharp.Tests.MapTools.Bench;

/// <summary>
/// <see cref="VvisCommand.StageClock"/>, the <c>--bench</c> per-stage ledger.
/// </summary>
/// <remarks>
/// <para>
/// The clock is fed <c>CompileProgress</c> by every vvis worker on every
/// completed item (WorkQueue's report line, the one stock keeps inside its
/// global critical section), and its <c>bench</c> rows are what the bench
/// ledger, the P12 CSVs and the T15 findings quote. Two contracts, each its
/// own fact below:
/// </para>
/// <para>
/// (1) The ROWS are exactly <c>bench &lt;stage&gt; &lt;F3&gt;s</c> in first-seen
/// order plus a final <c>bench total &lt;F3&gt;s</c>, keyed by the seven
/// <see cref="Vvis"/> stage names — the text consumers parse.
/// </para>
/// <para>
/// (2) Reading the ledger must be FREE: <c>Report</c> is a snapshot, never a
/// mutation. The first implementation closed the open segment inside the
/// snapshot, so a read mid-run re-opened the next segment from the read time
/// and the interval between the read and the next transition was billed to
/// nobody — a host polling progress silently ate wall time out of the rows,
/// and on the hot path every concurrent reader made workers re-enter the
/// shared lock for a stage they were already in. That is the defect this file
/// pins; the scripted-clock facts below reproduce the mis-attribution exactly
/// (T15-findings §4).
/// </para>
/// </remarks>
public class StageClockTests
{
    /// <summary>A clock the fact drives; one unit is one millisecond.</summary>
    private sealed class ScriptedClock
    {
        private long _now;

        public const long Frequency = 1_000;

        public long Now => Interlocked.Read(ref _now);

        public void AdvanceTo(long milliseconds) =>
            Volatile.Write(ref _now, milliseconds);

        public StageClock New() => new(() => Volatile.Read(ref _now) * Frequency / 1000, Frequency);
    }

    private static CompileProgress At(string stage, long done = 1, long total = 10) =>
        new(stage, done, total);

    [Fact]
    public void TheLedgerRowsAreThePinnedShapeInFirstSeenOrder()
    {
        // The text P12's summarize and the findings tables parse: one
        // `bench <stage> <seconds:F3>s` row per stage in run order, then
        // `bench total`, culture-invariant, no matter the host culture.
        ScriptedClock clock = new();
        StageClock stage = clock.New();

        stage.Report(At(Vvis.BaseStage));   // t=0
        clock.AdvanceTo(1_250);
        stage.Report(At(Vvis.FlowStage));   // base ran 0..1.250
        clock.AdvanceTo(3_500);
        stage.Report(At(Vvis.ClusterMergeStage));
        clock.AdvanceTo(4_000);
        stage.Report(At(Vvis.BaseStage));   // a second visit folds into the row
        clock.AdvanceTo(6_000);

        string[] lines = [.. stage.Report()];

        Assert.Equal(
            [
                // Base = [0,1.250] + the still-open [4.000,6.000] tail; Flow
                // and ClusterMerge closed at their transitions. Text captured
                // from the pre-change implementation on this exact script.
                "bench vvis.BasePortalVis 3.250s",
                "bench vvis.PortalFlow 2.250s",
                "bench vvis.ClusterMerge 0.500s",
                "bench total 6.000s",
            ],
            lines);
    }

    [Fact]
    public void ReadingTheLedgerMidRunBillsNobodyToNobody()
    {
        // The RED fact for T15's defect class. Items for a stage keep flowing
        // while a host polls the ledger; polling must not interrupt the
        // running segment. The first implementation closed the segment inside
        // the read, so the wall between the read and the next report of that
        // stage belonged to no stage at all.
        ScriptedClock clock = new();
        StageClock stage = clock.New();

        stage.Report(At(Vvis.FlowStage));            // flow starts at t=0
        clock.AdvanceTo(60);
        _ = stage.Report().ToArray();                // a host polls at t=60
        clock.AdvanceTo(90);
        stage.Report(At(Vvis.FlowStage));            // still flow, items landing
        clock.AdvanceTo(120);
        string[] lines = [.. stage.Report()];

        Assert.Equal("bench vvis.PortalFlow 0.120s", lines[0]);
        Assert.Equal("bench total 0.120s", lines[^1]);
    }

    [Fact]
    public void RepeatedLedgerReadsAreIdempotent()
    {
        ScriptedClock clock = new();
        StageClock stage = clock.New();

        stage.Report(At(Vvis.PasStage));
        clock.AdvanceTo(250);
        string[] first = [.. stage.Report()];
        string[] again = [.. stage.Report()];

        Assert.Equal(first, again);
        Assert.Equal("bench vvis.CalcPAS 0.250s", first[0]);
    }

    [Fact]
    public void ConcurrentReportsSettledOnOneStageNeverBlockTheLedger()
    {
        // The hot path: sixteen workers report the SAME stage reference after
        // every completed item. Every report must complete without touching
        // the lock the ledger reader uses, and a concurrent reader hammering
        // Report must not force workers through the transition path. The
        // bound is generous — the assertion is liveness, not timing.
        const int Workers = 16;
        const int Reports = 200_000;

        StageClock stage = new();
        using ManualResetEventSlim go = new(false);
        using ManualResetEventSlim stop = new(false);
        int[] counts = new int[Workers];
        Exception[] failure = new Exception[Workers];

        Thread[] threads =
        [
            .. Enumerable.Range(0, Workers).Select(worker => new Thread(() =>
            {
                try
                {
                    go.Wait();
                    CompileProgress value = At(Vvis.FlowStage);
                    while (!stop.IsSet)
                    {
                        stage.Report(value);
                        Interlocked.Increment(ref counts[worker]);
                    }

                }
                catch (Exception ex)
                {
                    failure[worker] = ex;
                }
            })
            { IsBackground = true }),
        ];

        Thread reader = new(() =>
        {
            go.Wait();
            while (!stop.IsSet)
            {
                _ = stage.Report().ToArray();
            }
        })
        { IsBackground = true };

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        reader.Start();
        go.Set();
        // Stop only once every worker is on-CPU and reporting: on a loaded
        // box the fast threads can burn through the report budget before a
        // late-scheduled worker ever enters its loop.
        bool settled = SpinWait.SpinUntil(
            () => counts.Min() > 0 && counts.Sum() >= Reports,
            TimeSpan.FromSeconds(60));
        stop.Set();
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "worker never finished");
        }

        reader.Join(TimeSpan.FromSeconds(30));
        Assert.All(failure, ex => Assert.Null(ex));
        Assert.True(settled, "the report hot path stalled before the budget");
        Assert.True(counts.Min() > 0, "a worker made no progress at all");
    }

    [Fact]
    public void ConcurrentTransitionsKeepEveryStageRowAndEveryTick()
    {
        // Workers straddling a boundary: threads hammer two settled stages
        // while transitions to the remaining vvis stages race beneath them.
        // The ledger must still show every stage that ran, in first-seen
        // order, with non-negative rows whose sum never exceeds the total.
        // no double billing that outruns the wall, no stage swallowed by a
        // lost CAS.
        string[] stages = [Vvis.BaseStage, Vvis.FlowStage, Vvis.ClusterMergeStage,
                           Vvis.CrosscheckStage, Vvis.PasStage, Vvis.LumpStage, Vvis.WaterStage];
        StageClock clock = new();
        using ManualResetEventSlim go = new(false);
        Exception?[] failure = new Exception?[stages.Length + 1];

        int done = 0;
        Thread[] threads =
        [
            .. stages.Select((stage, index) => new Thread(() =>
            {
                try
                {
                    go.Wait();
                    CompileProgress value = At(stage);
                    for (int i = 0; i < 50_000; i++)
                    {
                        clock.Report(value);
                    }

                    Interlocked.Increment(ref done);
                }
                catch (Exception ex)
                {
                    failure[index] = ex;
                }
            })
            { IsBackground = true }),
        ];

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        go.Set();
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref done) == stages.Length, TimeSpan.FromSeconds(30)),
            "a stage thread hung");
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a stage thread hung");
        }

        Assert.All(failure, ex => Assert.Null(ex));

        string[] lines = [.. clock.Report()];
        Assert.Equal(stages.Length + 1, lines.Length); // every stage ran, plus total
        Assert.EndsWith("s", lines[^1], StringComparison.Ordinal);
        Assert.StartsWith("bench total ", lines[^1], StringComparison.Ordinal);

        // The ledger renders every value to F3, so this fact can only compare
        // what the text shows: whole milliseconds, exactly. Rounding each of
        // the seven rows UP by at most half a quantum and the total DOWN by
        // the same leaves (rows + 1) / 2 ms of slack the rendering itself
        // creates — 4 ms here — and any larger excess is a real lost or
        // double-billed tick. Measured classification (30x Release, clean
        // box, 4/30 failures against the old +0.001 tolerance): the worst
        // genuine excess was 2 ms, inside this ceiling and beneath every
        // tick the clock could misplace — the old fact was wrong at ~14 ms
        // runs because 7 x 0.5 ms of render slack outran a 1 ms tolerance,
        // not because the clock misbilled. The tolerance-free form of this
        // invariant (sum <= wall, ZERO slack, mutation-proven) is the
        // stepped-clock fact below, where the scripted clock removes the
        // rendering from the comparison.
        long total = (long)Math.Round(ParseTotal(lines[^1]) * 1_000);
        long ceiling = (lines.Length * 5 + 5) / 10; // (rows + 1) * 0.5 ms, rounded
        long sum = 0;
        foreach (string line in lines[..^1])
        {
            (string Stage, double Seconds) row = ParseRow(line);
            Assert.Contains(row.Stage, stages, StringComparer.Ordinal);
            long rowMillis = (long)Math.Round(row.Seconds * 1_000);
            Assert.InRange(rowMillis, 0, total + 1); // one quantum each way
            sum += rowMillis;
        }

        // Segments are contiguous from the first transition, so the stages can
        // never bill more wall than the run had, once the render quantisation
        // the text itself imposes is accounted for.
        Assert.True(
            sum <= total + ceiling,
            $"stages billed {sum}ms over a {total}ms wall, beyond the {ceiling}ms the F3 rendering can invent");
        foreach (string stage in stages)
        {
            Assert.Contains(lines, line => line.StartsWith("bench " + stage + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ConcurrentTransitionsUnderASteppedClockNeverBillMoreThanTheWall()
    {
        // Deterministic twin of the wall-clock hammering fact: the same seven
        // stage threads race the same transitions, but the clock under test
        // steps one whole tick — one millisecond — per reading, so every row
        // lands on the exact grid the F3 ledger renders and contiguity is
        // checked with NO tolerance: a sum over the wall is a lost or extra
        // tick, never a rendering artefact. This is what the scripted-clock
        // seam the public constructor exposes exists for (T15-findings §4):
        // a deterministic fact may not measure a wall.
        string[] stages = [Vvis.BaseStage, Vvis.FlowStage, Vvis.ClusterMergeStage,
                           Vvis.CrosscheckStage, Vvis.PasStage, Vvis.LumpStage, Vvis.WaterStage];
        long ticks = 0;
        StageClock clock = new(() => Interlocked.Add(ref ticks, 1L), 1_000);
        using ManualResetEventSlim go = new(false);
        Exception?[] failure = new Exception?[stages.Length + 1];

        int done = 0;
        Thread[] threads =
        [
            .. stages.Select((stage, index) => new Thread(() =>
            {
                try
                {
                    go.Wait();
                    CompileProgress value = At(stage);
                    for (int i = 0; i < 50_000; i++)
                    {
                        clock.Report(value);
                    }

                    Interlocked.Increment(ref done);
                }
                catch (Exception ex)
                {
                    failure[index] = ex;
                }
            })
            { IsBackground = true }),
        ];

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        go.Set();
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref done) == stages.Length, TimeSpan.FromSeconds(30)),
            "a stage thread hung");
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a stage thread hung");
        }

        Assert.All(failure, ex => Assert.Null(ex));

        string[] lines = [.. clock.Report()];
        Assert.Equal(stages.Length + 1, lines.Length); // every stage ran, plus total
        Assert.StartsWith("bench total ", lines[^1], StringComparison.Ordinal);

        // Stepped clock: every value sits on the millisecond grid, so parse
        // back to whole milliseconds and compare integers — no quantisation
        // slack for a lost tick to hide in.
        long total = (long)Math.Round(ParseTotal(lines[^1]) * 1_000);
        long sum = 0;
        foreach (string line in lines[..^1])
        {
            (string Stage, double Seconds) row = ParseRow(line);
            Assert.Contains(row.Stage, stages, StringComparer.Ordinal);
            long rowMillis = (long)Math.Round(row.Seconds * 1_000);
            Assert.InRange(rowMillis, 0, total);
            sum += rowMillis;
        }

        Assert.True(sum <= total, $"stages billed {sum}ms over a {total}ms wall");
        foreach (string stage in stages)
        {
            Assert.Contains(lines, line => line.StartsWith("bench " + stage + " ", StringComparison.Ordinal));
        }
    }

    private static double ParseTotal(string line) =>
        double.Parse(line["bench total ".Length..^1], CultureInfo.InvariantCulture);

    private static (string Stage, double Seconds) ParseRow(string line)
    {
        // "bench vvis.X 1.234s" -> ("vvis.X", 1.234)
        int firstSpace = line.IndexOf(' ');
        int secondSpace = line.IndexOf(' ', firstSpace + 1);
        return (line[(firstSpace + 1)..secondSpace],
                double.Parse(line[(secondSpace + 1)..^1], CultureInfo.InvariantCulture));
    }
}
