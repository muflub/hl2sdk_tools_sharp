//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapCompile;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bench;

/// <summary>
/// The Phase 12 harness's own machinery: the run-reduction math, the CSV
/// schema (every arm speaks it), the cache-arm isolation rule, and the
/// refusal rows that stand where a number cannot.
/// </summary>
/// <remarks>
/// Everything here is a pure function over fabricated samples — no compile,
/// no clock, no disk beyond a temp ledger. What the harness MEASURES is
/// proven by the numbers themselves; what it must get right
/// without measuring is proven here.
/// </remarks>
public sealed class BenchHarnessTests
{
    private const string Cell = "m1|managed_jit|chain|default|8|off";

    private static BenchSample Sample(
        string cell = Cell,
        int run = 0,
        bool timed = true,
        bool ok = true,
        double wall = 1.0,
        double cpu = 1.0,
        long rss = 1024 * 1024,
        double gc = 0.001,
        string[]? stages = null,
        string[]? outputs = null,
        int reused = 0,
        int cooked = 0,
        string? failure = null) =>
        new(cell, run, timed, ok, wall, cpu, rss, gc,
            stages ?? ["load|0.1", "vbsp|0.2", "vvis|0.3", "vrad|0.4", "write|0.01"],
            outputs ?? ["m1.bsp|" + new string('a', 64)],
            reused, cooked, failure);

    // ------------------------------------------------------------- median math

    [Fact]
    public void TheMedianOfAnOddCountIsTheMiddleValue()
    {
        Assert.Equal(3.0, BenchCommand.Median([5, 1, 3, 2, 4]));
    }

    [Fact]
    public void TheMedianOfAnEvenCountIsTheMeanOfTheTwoMiddles()
    {
        Assert.Equal(2.5, BenchCommand.Median([4, 1, 2, 3]));
    }

    [Fact]
    public void TheMedianOfNothingIsRefusedNotZero()
    {
        Assert.Throws<ArgumentException>(() => BenchCommand.Median([]));
    }

    [Fact]
    public void ScalingEfficiencyIsSpeedupOverIdeal()
    {
        // 8x ideal speedup is 1.0; 4x out of 8 threads is 0.5.
        Assert.Equal(1.0, BenchCommand.ScalingEfficiency(80.0, 10.0, 8), 9);
        Assert.Equal(0.5, BenchCommand.ScalingEfficiency(80.0, 20.0, 8), 9);
    }

    [Fact]
    public void ScalingEfficiencyRefusesAnImpossibleThreadCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchCommand.ScalingEfficiency(80, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchCommand.ScalingEfficiency(80, 0, 8));
    }

    // ---------------------------------------------------------- cache isolation

    [Fact]
    public void EveryColdRunGetsItsOwnStoreNoOtherRunShares()
    {
        string? run0 = BenchCommand.StoreDirFor("/base", Cell, "cold", 0, timed: true);
        string? run1 = BenchCommand.StoreDirFor("/base", Cell, "cold", 1, timed: true);
        Assert.NotNull(run0);
        Assert.NotNull(run1);
        Assert.NotEqual(run0, run1);
        Assert.StartsWith("/base", run0, StringComparison.Ordinal);
    }

    [Fact]
    public void AWarmRunReusesPreciselyItsCellsColdSiblingRunZeroStore()
    {
        // The production pair: the cold cell is `...|cold`, the warm cell
        // `...|warm`; every warm run borrows the cold cell's r0 store.
        const string WarmCell = "m1|managed_jit|chain|default|8|warm";
        const string ColdCell = "m1|managed_jit|chain|default|8|cold";
        string? cold = BenchCommand.StoreDirFor("/base", ColdCell, "cold", 0, timed: true);
        string? warm = BenchCommand.StoreDirFor("/base", WarmCell, "warm", 0, timed: true);
        string? warmAgain = BenchCommand.StoreDirFor("/base", WarmCell, "warm", 4, timed: true);
        Assert.Equal(cold, warm);
        Assert.Equal(cold, warmAgain);
    }

    [Fact]
    public void AWarmRunOfOneCellNeverBorrowsAnotherCellsStore()
    {
        const string Warm8 = "m1|managed_jit|chain|default|8|warm";
        const string Warm16 = "m1|managed_jit|chain|default|16|warm";
        string? mine = BenchCommand.StoreDirFor("/base", Warm8, "warm", 0, timed: true);
        string? theirs = BenchCommand.StoreDirFor("/base", Warm16, "warm", 0, timed: true);
        Assert.NotEqual(mine, theirs);
    }

    [Fact]
    public void TheWarmUpRunOfAColdArmWritesToNoTimedRunsStore()
    {
        string? warmup = BenchCommand.StoreDirFor("/base", Cell, "cold", 0, timed: false);
        string? coldRun0 = BenchCommand.StoreDirFor("/base", Cell, "cold", 0, timed: true);
        Assert.NotNull(warmup);
        Assert.NotEqual(coldRun0, warmup);
    }

    [Fact]
    public void NoCacheMeansNoStoreAtAll()
    {
        Assert.Null(BenchCommand.StoreDirFor("/base", Cell, "off", 0, timed: true));
    }

    // ------------------------------------------------------------------ cells

    [Fact]
    public void ACellIdCarriesSixPartsAndANumericThreadCount()
    {
        Assert.Equal(("m1", "managed_jit", "chain", "default", 8, "off"), BenchCommand.ParseCell(Cell));
    }

    [Fact]
    public void ACellIdWithoutItsPartsIsRejectedNotGuessed()
    {
        Assert.Throws<InvalidDataException>(() => BenchCommand.ParseCell("m1|managed_jit|chain"));
        Assert.Throws<InvalidDataException>(() => BenchCommand.ParseCell("m1|managed_jit|chain|default|many|off"));
    }

    // ---------------------------------------------------------------- reduction

    [Fact]
    public void WarmUpRunsNeverCountInTheMedianOrTheSpread()
    {
        List<BenchSample> samples =
        [
            Sample(run: -1, timed: false, wall: 99.0),   // the JIT-warming run: huge, excluded
            Sample(run: 0, wall: 2.0),
            Sample(run: 1, wall: 1.0),
            Sample(run: 2, wall: 3.0),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Equal(3, row.N);
        Assert.Equal(2.0, row.WallMed, 9);
        Assert.Equal(1.0, row.WallMin, 9);
        Assert.Equal(3.0, row.WallMax, 9);
    }

    [Fact]
    public void TheStageColumnsTakeTheMedianPerStageAndKeepNotMeasuredStagesBlank()
    {
        List<BenchSample> samples =
        [
            Sample(run: 0, wall: 9.0, stages: ["load|0.1", "vbsp|0.9", "vvis|0.3", "vrad|0.4", "write|0.01"]),
            Sample(run: 1, wall: 9.0, stages: ["load|0.1", "vbsp|0.5", "vvis|0.3", "vrad|0.4", "write|0.01"]),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Equal(0.7, row.StageWalls[1], 9); // vbsp: median of 0.9 and 0.5
    }

    [Fact]
    public void AStageNoRunReachedIsBlankRatherThanZeroSeconds()
    {
        List<BenchSample> singleTool =
        [
            Sample(run: 0, stages: ["vbsp|0.5"]),
            Sample(run: 1, stages: ["vbsp|0.7"]),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(singleTool));
        Assert.Equal(0.6, row.StageWalls[1], 9);
        Assert.True(double.IsNaN(row.StageWalls[0]));  // load: never measured
        Assert.True(double.IsNaN(row.StageWalls[3]));  // vrad: never measured
    }

    [Fact]
    public void ATimedRunThatFailedIsCountedOutAndNamed()
    {
        List<BenchSample> samples =
        [
            Sample(run: 0, wall: 2.0),
            Sample(run: 1, wall: 2.0),
            Sample(run: 2, ok: false, failure: "VRAD0707: no GPU"),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Equal("ok", row.Rc);          // the cell still has good runs
        Assert.Equal(2, row.N);              // the failed one is out of the medians
        Assert.Contains("1/3 timed run(s) failed", row.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellWhoseEveryTimedRunFailedCarriesTheFailureNotAZero()
    {
        List<BenchSample> samples =
        [
            Sample(run: 0, ok: false, wall: 0.01, failure: "Error: leak"),
            Sample(run: 1, ok: false, wall: 0.01, failure: "Error: leak"),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Equal("failed", row.Rc);
        Assert.Equal(0, row.N);
        Assert.Equal(0.0, row.WallMed);
        Assert.Equal("r:Error: leak", row.Notes);
    }

    [Fact]
    public void ACellWithNoTimedRunAtAllIsReportedNotSilenced()
    {
        // A cell cut off during its only warm-up (a failure there stops the series).
        List<BenchSample> samples = [Sample(run: -1, timed: false, ok: false, failure: "no such map")];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Equal("failed", row.Rc);
        Assert.Contains("no such map", row.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void DivergentOutputsAcrossTimedRunsAreFlaggedNotSilentlyMedianised()
    {
        List<BenchSample> samples =
        [
            Sample(run: 0, outputs: ["m1.bsp|" + new string('a', 64)]),
            Sample(run: 1, outputs: ["m1.bsp|" + new string('b', 64)]),
        ];
        BenchCellResult row = Assert.Single(BenchCommand.Reduce(samples));
        Assert.Contains("sha-divergent", row.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void EachCellOfALedgerGetsItsOwnRow()
    {
        List<BenchSample> samples =
        [
            Sample(run: 0),
            Sample(cell: "m1|managed_jit|chain|default|16|off", run: 0),
        ];
        IReadOnlyList<BenchCellResult> rows = BenchCommand.Reduce(samples);
        Assert.Equal(2, rows.Count);
        Assert.Equal(8, rows[0].Threads);
        Assert.Equal(16, rows[1].Threads);
    }

    [Fact]
    public void ARefusedCellHasNoNumbersAndOneQuotedReason()
    {
        BenchCellResult row = BenchCellResult.Refused(
            "sdk_cp_dustbowl", "stock_x64", "vrad", "-both", 32, "off",
            "stock vrad caps -threads at 16");
        Assert.Equal("refused", row.Rc);
        Assert.Equal(0, row.N);
        Assert.Equal(0.0, row.WallMed);
        Assert.Empty(row.BspSha);
        Assert.StartsWith("r:", row.Notes, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- CSV

    [Fact]
    public void TheHeaderHasOneColumnPerRowField()
    {
        Assert.Equal(BenchCommand.CsvHeader.Split(',').Length,
            BenchCellResult.Refused("m", "a", "chain", "default", 0, "off", "x").ToCsv().Split(',').Length);
    }

    [Fact]
    public void ARowSurvivesTheCsvRoundTrip()
    {
        BenchCellResult row = new(
            "ss_sandbox", "managed_jit", "chain", "-fast", 32, "warm", "ok", 5,
            2.5, 2.4, 2.7, [0.1, 0.9, 0.6, 0.8, 0.02], 4.1, 900_000, 0.012, 17, 0,
            new string('c', 64), "warms cold-r0 store");
        Assert.Equal(row, BenchCellResult.FromCsv(row.ToCsv()));
    }

    [Fact]
    public void ARefusedRowSurvivesWithItsReasonQuoted()
    {
        BenchCellResult row = BenchCellResult.Refused(
            "sdk_pl_goldrush", "stock_x64", "vrad", "-both", 32, "off",
            "gave up, rc=1 \"OOM\"");
        BenchCellResult back = BenchCellResult.FromCsv(row.ToCsv());
        Assert.Equal(row, back);
        Assert.Contains("\"OOM\"", back.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowOfTheWrongShapeIsRejectedAtTheDoor()
    {
        Assert.Throws<InvalidDataException>(() => BenchCellResult.FromCsv("too,few,fields"));
    }

    [Fact]
    public void TheCsvOpensWithProvenanceCommentsBeforeItsHeader()
    {
        string csv = BenchCommand.Csv(
            [("commit", "d818eb53e"), ("box", "ryzen9-9950x")],
            [BenchCellResult.Refused("m", "stock_x64", "chain", "default", 16, "off", "no x64 vvis")]);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# commit: d818eb53e", lines[0]);
        Assert.Equal("# box: ryzen9-9950x", lines[1]);
        Assert.Equal(BenchCommand.CsvHeader, lines[2]);
        Assert.StartsWith("m,stock_x64,chain", lines[3], StringComparison.Ordinal);
        Assert.Contains("r:no x64 vvis", lines[3], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- ledger

    [Fact]
    public void ACompletedRunSurvivesTheLedgerLineRoundTrip()
    {
        BenchSample sample = Sample(run: 3, reused: 5, cooked: 2);
        Assert.Equal(sample, BenchSample.FromJsonLine(sample.ToJsonLine()));
    }

    [Fact]
    public void StageCacheFieldsSurviveTheLedgerLineRoundTrip()
    {
        BenchSample sample = Sample(run: 1, reused: 189) with
        {
            CacheStagesReused = ["vvis", "vrad.transfers"],
            CacheStagesComputed = ["vrad.direct"],
            CacheSavedMs = 5800,
            CacheBytesStored = 155L * 1024 * 1024,
        };

        BenchSample back = BenchSample.FromJsonLine(sample.ToJsonLine());

        Assert.Equal(sample, back);
        Assert.Equal(["vvis", "vrad.transfers"], back.CacheStagesReused);
        Assert.Equal(["vrad.direct"], back.CacheStagesComputed);
        Assert.Equal(5800, back.CacheSavedMs);
        Assert.Equal(155L * 1024 * 1024, back.CacheBytesStored);
    }

    [Fact]
    public void ALedgerLineWrittenBeforeStageCachesReadsAsNoStages()
    {
        string line = Sample(run: 0, reused: 3, cooked: 1).ToJsonLine();
        string old = line[..line.IndexOf(",\"CacheStagesReused\"", StringComparison.Ordinal)] + "}";

        BenchSample back = BenchSample.FromJsonLine(old);

        Assert.Equal(3, back.CacheReused);
        Assert.Empty(back.CacheStagesReused!);
        Assert.Empty(back.CacheStagesComputed!);
        Assert.Equal(0, back.CacheSavedMs);
        Assert.Equal(0, back.CacheBytesStored);
    }

    [Fact]
    public async Task SummarizeWithoutAnInputIsUsageNotACrash()
    {
        int rc = await BenchCommand.RunAsync(
            new SourceSharp.MapTools.Io.PhysicalFileSystem("/"), [], ["summarize"], new StringWriter());
        Assert.Equal(Program.ExitUsage, rc);
    }

    [Fact]
    public async Task SummarizeTurnsAFabricatedLedgerIntoOneProvenancedCsv()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-summarize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string jsonl = Path.Combine(dir, "led.jsonl");
            File.WriteAllLines(jsonl,
            [
                Sample(run: -1, timed: false, wall: 30.0).ToJsonLine(),
                Sample(run: 0, wall: 2.0).ToJsonLine(),
                Sample(run: 1, wall: 2.0).ToJsonLine(),
                Sample(run: 2, wall: 2.0).ToJsonLine(),
            ]);
            string outCsv = Path.Combine(dir, "nested", "matrix.csv");
            int rc = await BenchCommand.RunAsync(
                new SourceSharp.MapTools.Io.PhysicalFileSystem("/"), [],
                ["summarize", "--in", jsonl, "--out", outCsv, "--prov", "commit=abc123"],
                new StringWriter());
            Assert.Equal(Program.ExitSuccess, rc);
            string[] lines = File.ReadAllLines(outCsv);
            Assert.Equal("# commit: abc123", lines[0]);
            Assert.Equal(BenchCommand.CsvHeader, lines[1]);
            BenchCellResult row = BenchCellResult.FromCsv(lines[2]);
            Assert.Equal(3, row.N);                       // the warm-up stayed out
            Assert.Equal(2.0, row.WallMed, 9);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the pristine-input snapshot (the same-bytes-every-run rule) ---.

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "p12snap-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void SnapshotCapturesTheInputsAndRestoreUndoesWhatAMutatingRunDid()
    {
        using TempDir dir = new();
        string bsp = System.IO.Path.Combine(dir.Path, "m1.bsp");
        string prt = System.IO.Path.Combine(dir.Path, "m1.prt");
        File.WriteAllBytes(bsp, [1, 2, 3]);
        File.WriteAllText(prt, "prt-v1");

        (string[] from, string[] to) = BenchCommand.SnapshotStageInputs(bsp, "vvis");
        Assert.Equal([bsp, prt], to);                     // bsp first, then prt
        Assert.Equal(2, from.Length);

        File.WriteAllBytes(bsp, [9]);                      // a run bakes into its input
        File.WriteAllText(prt, "prt-v2");
        Assert.Equal("prt-v1", File.ReadAllText(from[1])); // the snapshot held

        BenchCommand.RestoreStageInputs(from, to);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(bsp));
        Assert.Equal("prt-v1", File.ReadAllText(prt));
    }

    [Fact]
    public void TheChainIsNeverSnapshottedAndAMissingInputSnapshotsNothing()
    {
        using TempDir dir = new();
        string bsp = System.IO.Path.Combine(dir.Path, "m1.bsp");   // exists
        File.WriteAllBytes(bsp, [1]);
        Assert.Empty(BenchCommand.SnapshotStageInputs(bsp, "chain").From);
        Assert.Empty(BenchCommand.SnapshotStageInputs(bsp, "all").To);

        Assert.Empty(BenchCommand.SnapshotStageInputs(
            System.IO.Path.Combine(dir.Path, "nosuch.bsp"), "vrad").From);
    }

    [Fact]
    public void RestoreWithoutASnapshotIsTheChainsNoOpAndMismatchedPairsThrow()
    {
        BenchCommand.RestoreStageInputs([], []);          // idempotent no-op
        Assert.Throws<ArgumentException>(
            () => BenchCommand.RestoreStageInputs(["a"], []));
    }
}
