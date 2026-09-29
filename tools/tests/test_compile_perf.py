"""Facts for tools/compile_perf.py and tools/compile_perf_summary.py.

    python3 -m unittest discover -s tools/tests
"""

import itertools
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import compile_perf as cp  # noqa: E402
import compile_perf_summary as cs  # noqa: E402

MATRIX = os.path.join(cp.REPO, "tools", "compile-perf-matrix.json")


def write_json(path, value):
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(value, fh)


def read_text(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def axes(**values):
    return {a: {"stages": ["chain"], "values": v} for a, v in values.items()}


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.matrix = cp.load_matrix(MATRIX)

    def test_the_shipped_matrix_loads_and_its_baseline_is_one_of_each_axis(self):
        for axis, spec in self.matrix["axes"].items():
            self.assertIn(self.matrix["baseline"][axis], spec["values"])

    def test_no_value_splits_faces_past_vrads_sample_limit(self):
        # -nosubdiv leaves faces too big for vrad's per-face sample buffer:
        # every lit compile with it fails, so it can only waste a cell.
        for axis, spec in self.matrix["axes"].items():
            for value, v in spec["values"].items():
                for opts in (v.get(k, []) for k in ("chain", "vbsp", "vvis", "vrad")):
                    self.assertNotIn("-nosubdiv", opts, f"{axis}={value}")

    def test_a_missing_requirement_skips_the_value_and_says_why(self):
        kept, skipped = cp.narrow(self.matrix, {}, set())
        self.assertNotIn("gpu", kept["tracer"]["values"])
        self.assertIn("tracer=gpu (needs gpu)", skipped)
        kept, skipped = cp.narrow(self.matrix, {}, {"gpu"})
        self.assertIn("gpu", kept["tracer"]["values"])

    def test_set_keeps_only_the_named_values(self):
        kept, _ = cp.narrow(self.matrix, {"threads": ["1"]}, set())
        self.assertEqual(["1"], kept["threads"]["values"])

    def test_set_refuses_a_value_the_axis_does_not_have(self):
        with self.assertRaises(ValueError):
            cp.narrow(self.matrix, {"threads": ["lots"]}, set())

    def test_sweep_is_the_baseline_and_each_value_alone(self):
        a = axes(x=["a", "b", "c"], y=["p", "q"])
        cells = cp.cells_for(a, {"x": "a", "y": "p"}, "chain", "sweep")
        self.assertEqual([{"x": "a", "y": "p"}, {"x": "b", "y": "p"}, {"x": "c", "y": "p"}, {"x": "a", "y": "q"}],
                         [c["settings"] for c in cells])

    def test_full_is_every_combination(self):
        a = axes(x=["a", "b", "c"], y=["p", "q"])
        self.assertEqual(6, len(cp.cells_for(a, {"x": "a", "y": "p"}, "chain", "full")))

    def test_pairwise_covers_every_pair_and_starts_at_the_baseline(self):
        a = axes(w=["1", "2", "3"], x=["a", "b", "c"], y=["p", "q"], z=["m", "n", "o", "r"])
        base = {"w": "1", "x": "a", "y": "p", "z": "m"}
        cells = cp.cells_for(a, base, "chain", "pairwise")
        self.assertEqual(base, cells[0]["settings"])
        for (p, q) in itertools.combinations(a, 2):
            for vp in a[p]["values"]:
                for vq in a[q]["values"]:
                    self.assertTrue(any(c["settings"][p] == vp and c["settings"][q] == vq for c in cells),
                                    f"{p}={vp} {q}={vq} not covered")
        self.assertLess(len(cells), 3 * 3 * 2 * 4)

    def test_an_axis_applies_only_to_its_stages(self):
        kept, _ = cp.narrow(self.matrix, {}, set())
        self.assertNotIn("overlap", cp.stage_axes(kept, "vrad"))
        self.assertIn("overlap", cp.stage_axes(kept, "chain"))

    def test_cell_ids_name_what_differs_from_the_baseline(self):
        base = {"x": "a", "y": "p"}
        self.assertEqual("chain__baseline", cp.cell_id({"stage": "chain", "settings": base}, base))
        self.assertEqual("chain__y-q", cp.cell_id({"stage": "chain", "settings": {"x": "a", "y": "q"}}, base))

    def test_threads_resolve_against_the_machine(self):
        self.assertEqual(16, cp.thread_count("max", 16))
        self.assertEqual(8, cp.thread_count("half", 16))
        self.assertEqual(1, cp.thread_count("half", 1))
        self.assertEqual(3, cp.thread_count("3", 16))


class CommandTests(unittest.TestCase):
    def setUp(self):
        self.matrix = cp.load_matrix(MATRIX)
        self.base = dict(self.matrix["baseline"])

    def cmd(self, stage, **settings):
        s = {a: v for a, v in dict(self.base, **settings).items() if stage in self.matrix["axes"][a]["stages"]}
        return cp.cell_command(self.matrix, {"stage": stage, "settings": s}, {"gpu": "RTX", "vphysics": "tf"})

    def test_the_baseline_passes_nothing(self):
        self.assertEqual(([], {}, "off"), self.cmd("chain"))

    def test_a_chain_puts_tool_options_in_their_sections(self):
        opts, _, _ = self.cmd("chain", vrad="final", vvis="fast", overlap="on")
        self.assertEqual(["-overlap", "--vvis", "-fast", "--vrad", "-final"], opts)

    def test_a_value_with_chain_options_uses_them_in_a_chain(self):
        opts, _, _ = self.cmd("chain", vvis="loose")
        self.assertEqual(["-loose"], opts)
        opts, _, _ = self.cmd("vvis", vvis="loose")
        self.assertEqual(["-loose"], opts)

    def test_a_single_tool_takes_its_own_options(self):
        opts, _, _ = self.cmd("vrad", vrad="nobounce", compliance="stock", tracer="gpu")
        self.assertEqual(["-compliance", "stock", "-gpu", "RTX", "-bounce", "0"], opts)

    def test_runtime_values_are_environment_and_cache_values_are_bench_modes(self):
        _, env, cache = self.cmd("chain", runtime="workstation-gc", cache="warm")
        self.assertEqual({"DOTNET_gcServer": "0", "DOTNET_gcConcurrent": "0"}, env)
        self.assertEqual("warm", cache)

    def test_a_profiled_chain_puts_its_store_before_the_sections(self):
        class R:
            def ssmap(self, build):
                return ["ssmap"]
        cmd = cp.direct_command(R(), "chain", "jit", "m.vmf", ["-game", "g"], 4,
                                ["-overlap", "--vrad", "-final"], "store")
        self.assertEqual(["ssmap", "all", "m.vmf", "-game", "g", "-threads", "4", "-overlap",
                          "-incremental", "-cache-dir", "store", "--vrad", "-final"], cmd)

    def test_a_profiled_tool_puts_the_map_last(self):
        class R:
            def ssmap(self, build):
                return ["ssmap"]
        self.assertEqual(["ssmap", "vrad", "-threads", "2", "--bench", "-fast", "m"],
                         cp.direct_command(R(), "vrad", "jit", "m", [], 2, ["--bench", "-fast"]))


class GameCopyTests(unittest.TestCase):
    def test_the_copy_leaves_the_original_alone_and_strips_only_when_asked(self):
        with tempfile.TemporaryDirectory() as d:
            game = os.path.join(d, "mod")
            os.makedirs(game)
            info = "SearchPaths\n{\n\tgame |appid_1|x.vpk\n\tgame |gameinfo_path|.\n}\n"
            with open(os.path.join(game, "gameinfo.txt"), "w") as fh:
                fh.write(info)
            out = os.path.join(d, "out")
            cp.claim_out(out)

            copy = cp.game_copy(game, out, strip=False)
            self.assertIn("appid_1", read_text(os.path.join(copy, "gameinfo.txt")))
            copy = cp.game_copy(game, out, strip=True)
            self.assertNotIn("appid_1", read_text(os.path.join(copy, "gameinfo.txt")))
            self.assertIn("gameinfo_path", read_text(os.path.join(copy, "gameinfo.txt")))
            self.assertEqual(info, read_text(os.path.join(game, "gameinfo.txt")))

    def test_synthetic_content_needs_a_game_to_copy(self):
        with tempfile.NamedTemporaryFile(suffix=".vmf", delete=False) as fh:
            vmf = fh.name
        try:
            with self.assertRaises(SystemExit):
                cp.parse_args(["--map", vmf, "--synthetic"])
            self.assertTrue(cp.parse_args(["--map", vmf, "--synthetic", "--game", "g"]).synthetic)
        finally:
            os.remove(vmf)


class QuickTests(unittest.TestCase):
    def setUp(self):
        with tempfile.NamedTemporaryFile(suffix=".vmf", delete=False) as fh:
            self.vmf = fh.name

    def tearDown(self):
        os.remove(self.vmf)

    def test_without_quick_the_full_defaults_apply(self):
        a = cp.parse_args(["--map", self.vmf])
        self.assertEqual((list(cp.STAGES), "pairwise", 3, 1, "all"),
                         (a.stages, a.matrix, a.runs, a.warmups, a.profile_cells))
        self.assertEqual(list(cp.DEFAULT_PROFILERS), a.profile)

    def test_quick_covers_every_stage_once_per_setting_with_one_run(self):
        a = cp.parse_args(["--map", self.vmf, "--quick"])
        self.assertEqual((list(cp.STAGES), "sweep", 1, 0, "baseline"),
                         (a.stages, a.matrix, a.runs, a.warmups, a.profile_cells))
        self.assertEqual(["stages", "rusage", "cpu", "gc"], a.profile)

    def test_explicit_options_win_over_quick(self):
        a = cp.parse_args(["--map", self.vmf, "--quick", "--runs", "2", "--stages", "vrad",
                           "--profile", "cpu", "--matrix", "pairwise"])
        self.assertEqual((["vrad"], "pairwise", 2, 0), (a.stages, a.matrix, a.runs, a.warmups))
        self.assertEqual(["cpu"], a.profile)

    def test_quick_plans_a_sweep_of_every_stage_and_profiles_only_the_baselines(self):
        a = cp.parse_args(["--map", self.vmf, "--quick"])
        _, _, _, cells = cp.plan(a)
        full = cp.plan(cp.parse_args(["--map", self.vmf]))[3]
        self.assertEqual(set(cp.STAGES), {c["stage"] for c in cells})
        self.assertLess(len(cells), len(full))
        profiled = [c for c in cells if c["profile"]]
        self.assertEqual(sorted(cp.STAGES), sorted(c["stage"] for c in profiled))
        self.assertTrue(all(c["id"].endswith("baseline") for c in profiled))


class GpuOptionTests(unittest.TestCase):
    def test_an_empty_gpu_match_is_refused_at_once(self):
        with tempfile.NamedTemporaryFile(suffix=".vmf", delete=False) as fh:
            vmf = fh.name
        try:
            for bad in ("", "  "):
                with self.assertRaises(SystemExit):
                    cp.parse_args(["--map", vmf, "--gpu", bad])
            self.assertEqual("RTX", cp.parse_args(["--map", vmf, "--gpu", "RTX"]).gpu)
        finally:
            os.remove(vmf)

    def test_vrads_decline_line_is_found(self):
        log = "lighting...\nWarning VRAD0707: gpu tracer declined: no device matches \"foo\" -- CPU KD tracer for this run\n"
        self.assertIn("no device matches", cp.gpu_decline(log))
        self.assertIsNone(cp.gpu_decline("vrad 3.0 seconds elapsed\n"))


class PerfProbeTests(unittest.TestCase):
    def fake_perf(self, d, stat_rc, record_rc):
        path = os.path.join(d, "perf")
        with open(path, "w") as fh:
            fh.write(f"#!/bin/sh\ncase \"$1\" in stat) exit {stat_rc};; record) exit {record_rc};; esac\nexit 0\n")
        os.chmod(path, 0o755)
        return path

    def test_each_mode_is_probed_on_its_own(self):
        with tempfile.TemporaryDirectory() as d:
            perf = self.fake_perf(d, 0, 1)
            self.assertTrue(cp.perf_allows(perf, "stat"))
            self.assertFalse(cp.perf_allows(perf, "record"))

    def test_no_perf_allows_nothing(self):
        self.assertFalse(cp.perf_allows(None, "stat"))


class ParserTests(unittest.TestCase):
    def test_a_heap_report_is_read_largest_first(self):
        report = "  1,024  2  System.Byte[]\n 4,096  1  Foo.Bar\nnot a row\n"
        rows = cp.parse_heap_report(report)
        self.assertEqual(["Foo.Bar", "System.Byte[]"], [r["type"] for r in rows])
        self.assertEqual(5120, cp.heap_total(report))

    def test_perf_stat_gives_ipc_and_marks_unsupported_events(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as fh:
            fh.write("# started\n2000,,cycles,1,100\n3000,,instructions,1,100\n<not supported>,,cache-misses,0,0\n")
        stat = cp.read_perf_stat(fh.name)
        os.remove(fh.name)
        self.assertEqual(1.5, stat["ipc"])
        self.assertIsNone(stat["cache-misses"])

    def test_perf_script_folds_root_first(self):
        text = "ssmap 1 1.0: cycles:\n\tffff1 leaf+0x3e4 ([kernel.kallsyms])\n\t2 mid (lib)\n\t3 root (lib)\n\nssmap 1 2.0: cycles:\n\t1 leaf (lib)\n\t2 mid (lib)\n\t3 root (lib)\n"
        self.assertEqual({"root;mid;leaf": 2}, cp.fold_perf_script(text))

    def test_folded_stacks_rank_their_leaves(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as fh:
            fh.write("a;b 3\na;c 1\n")
        r = cs.read_folded(fh.name)
        os.remove(fh.name)
        self.assertEqual("b", r["self"][0]["function"])
        self.assertEqual(0.75, r["self"][0]["share"])

    def test_the_inclusive_ranking_skips_the_pool_plumbing(self):
        frames = [{"name": n} for n in ("SourceSharp.MapTools!SourceSharp.MapTools.Parallel.CompilePool.Loop()",
                                        "SourceSharp.MapTools!SourceSharp.MapTools.Tracing.KdRayTracer.Trace4Rays()",
                                        "CPU_TIME")]
        trace = {"shared": {"frames": frames}, "profiles": [{"events": [
            {"type": "O", "frame": 0, "at": 0}, {"type": "O", "frame": 1, "at": 0}, {"type": "O", "frame": 2, "at": 0},
            {"type": "C", "frame": 2, "at": 10}, {"type": "C", "frame": 1, "at": 10}, {"type": "C", "frame": 0, "at": 10}]}]}
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            json.dump(trace, fh)
        r = cs.read_speedscope(fh.name)
        os.remove(fh.name)
        self.assertEqual(["SourceSharp.MapTools.Tracing.KdRayTracer.Trace4Rays"], [x["function"] for x in r["inclusive"]])
        self.assertEqual(1.0, r["self"][0]["share"])

    def test_counters_give_peaks_and_totals(self):
        rows = ["Timestamp,Provider,Counter Name,Counter Type,Mean/Increment",
                "t,S,dotnet.process.memory.working_set (By),Metric,1048576",
                "t,S,dotnet.process.memory.working_set (By),Metric,3145728",
                "t,S,dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen2],Rate,2",
                "t,S,dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen2],Rate,1"]
        with tempfile.NamedTemporaryFile("w", delete=False) as fh:
            fh.write("\n".join(rows))
        c = cs.read_counters(fh.name)
        os.remove(fh.name)
        self.assertEqual(3, c["working_set_peak_mb"])
        self.assertEqual(3, c["gen2"])

    def test_tool_stage_lines_are_read(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as fh:
            fh.write("noise\nbench vrad.Load 0.250s\nbench total 1.000s\nbench work ldr samples=5\n")
        s = cs.read_stages(fh.name)
        os.remove(fh.name)
        self.assertEqual({"vrad.Load": 0.25, "total": 1.0}, s["stages_s"])
        self.assertEqual(["ldr samples=5"], s["work"])

    def test_vbsp_stage_lines_are_read_and_keep_their_parents_in_the_heading(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as fh:
            fh.write("bench vbsp.world.write 0.100s\nbench vbsp.write 0.020s\nbench total 1.000s\n")
        s = cs.read_stages(fh.name)
        os.remove(fh.name)
        self.assertEqual({"vbsp.world.write": 0.1, "vbsp.write": 0.02, "total": 1.0}, s["stages_s"])
        self.assertEqual("world.write", cs.stage_column("vbsp", "vbsp.world.write"))
        self.assertEqual("write", cs.stage_column("vbsp", "vbsp.write"))
        self.assertEqual("total", cs.stage_column("vbsp", "total"))
        self.assertEqual("Load", cs.stage_column("vrad", "vrad.Load"))


def cell(id_, stage, settings, wall, rss=100, threads=None, status="ok", alloc=None):
    c = {"id": id_, "stage": stage, "settings": settings, "status": status, "threads_n": threads,
         "bench": {"wall_s": wall, "peak_rss_mb": rss, "substages_s": {}}}
    if alloc:
        c["gc"] = {"allocations": {"estimated_bytes": alloc, "by_site": [], "large_object_bytes": 0},
                   "contention": {"by_site": [], "count": 0, "total_ms": 0}}
    return c


class SummaryTests(unittest.TestCase):
    def test_an_effect_compares_twins_that_differ_only_in_that_setting(self):
        cells = [cell("vrad__baseline", "vrad", {"t": "max", "q": "default"}, 10, alloc=100),
                 cell("vrad__q-final", "vrad", {"t": "max", "q": "final"}, 20, alloc=300),
                 cell("vrad__t-1", "vrad", {"t": "1", "q": "default"}, 40),
                 cell("vrad__t-1__q-final", "vrad", {"t": "1", "q": "final"}, 80)]
        rows = cs.effects(cells, {"t": {"values": ["1", "max"]}, "q": {"values": ["default", "final"]}})
        final = next(r for r in rows if r["value"] == "final")
        self.assertEqual(2, final["pairs"])
        self.assertAlmostEqual(2.0, final["wall"])
        self.assertAlmostEqual(3.0, final["alloc"])

    def test_a_failed_cell_is_no_ones_twin(self):
        cells = [cell("vrad__baseline", "vrad", {"q": "default"}, 10),
                 cell("vrad__q-final", "vrad", {"q": "final"}, 20, status="failed")]
        self.assertEqual([], cs.effects(cells, {"q": {"values": ["default", "final"]}}))

    def test_scaling_needs_a_one_thread_twin(self):
        cells = [cell("a", "vvis", {"threads": "1"}, 8, threads=1),
                 cell("b", "vvis", {"threads": "max"}, 2, threads=8),
                 cell("c", "vrad", {"threads": "max"}, 2, threads=8)]
        rows = cs.scaling(cells)
        self.assertEqual(1, len(rows))
        self.assertAlmostEqual(4.0, rows[0]["speedup"])
        self.assertAlmostEqual(0.5, rows[0]["efficiency"])

    def test_hot_spots_rank_by_the_mean_share_over_profiled_cells(self):
        a = {"id": "a", "cpu": {"self": [{"function": "Everywhere", "share": 0.3}, {"function": "OneCorner", "share": 0.5}],
                                "inclusive": []}}
        b = {"id": "b", "cpu": {"self": [{"function": "Everywhere", "share": 0.3}], "inclusive": []}}
        hot = cs.hot_spots([a, b])
        self.assertEqual("Everywhere", hot["cpu_self"][0]["name"])
        self.assertEqual("a", hot["cpu_self"][1]["peak_cell"])

    def test_a_profiler_that_exited_non_zero_is_reported_and_not_read(self):
        with tempfile.TemporaryDirectory() as d:
            meta = {"mode": "sweep", "axes": {}, "skipped": [],
                    "cells": [{"id": "vrad__baseline", "stage": "vrad", "settings": {}}]}
            write_json(os.path.join(d, "matrix.json"), meta)
            cd = os.path.join(d, "cells", "vrad__baseline")
            os.makedirs(cd)
            write_json(os.path.join(cd, "cell.json"),
                       {"status": "ok", "threads": 4, "profiles": {"rusage": {"exit": 1, "file": "rusage.json"}}})
            write_json(os.path.join(cd, "rusage.json"), {"exit": 1, "wall_s": 0.01, "user_s": 0, "sys_s": 0,
                                                          "max_rss_mb": 1, "minor_faults": 0, "major_faults": 0,
                                                          "voluntary_switches": 0, "involuntary_switches": 0,
                                                          "block_in": 0, "block_out": 0})
            with open(os.path.join(cd, "bench.jsonl"), "w") as fh:
                fh.write(json.dumps({"Timed": True, "Ok": True, "WallSeconds": 1.0, "CpuSeconds": 2.0,
                                     "PeakRssBytes": 1048576, "GcPauseSeconds": 0.1, "Stages": []}) + "\n")
            cs.summarise(d)
            self.assertIn("**rusage** failed in 1 cell(s)", read_text(os.path.join(d, "summary.md")))
            self.assertNotIn("## Process (one plain run)", read_text(os.path.join(cd, "report.md")))

    def test_a_folder_summarises_end_to_end(self):
        with tempfile.TemporaryDirectory() as d:
            meta = {"mode": "sweep", "axes": {"q": {"stages": ["vrad"], "values": ["default", "final"]}},
                    "skipped": [], "cells": [{"id": "vrad__baseline", "stage": "vrad", "settings": {"q": "default"}},
                                             {"id": "vrad__q-final", "stage": "vrad", "settings": {"q": "final"}}]}
            write_json(os.path.join(d, "matrix.json"), meta)
            for i, m in enumerate(meta["cells"]):
                cd = os.path.join(d, "cells", m["id"])
                os.makedirs(cd)
                write_json(os.path.join(cd, "cell.json"), {"status": "ok", "threads": 4})
                with open(os.path.join(cd, "bench.jsonl"), "w") as fh:
                    fh.write(json.dumps({"Timed": True, "Ok": True, "WallSeconds": 1.0 + i, "CpuSeconds": 2.0,
                                         "PeakRssBytes": 1048576, "GcPauseSeconds": 0.1, "Stages": ["vrad|1.0"]}) + "\n")
            cs.summarise(d)
            md = read_text(os.path.join(d, "summary.md"))
            self.assertIn("| vrad | q=final | default | 1 | +100% |", md)
            self.assertTrue(os.path.exists(os.path.join(d, "cells", "vrad__q-final", "report.md")))
            self.assertTrue(os.path.exists(os.path.join(d, "cells.csv")))


class BenchCommandTests(unittest.TestCase):
    class Runner:
        def ssmap(self, build):
            return ["ssmap"]

    class Args:
        runs, warmups, game_dir = 3, 1, "/g"

    def commands(self, cache):
        restored = []

        def restore():
            restored.append(1)
            return "/work/m.vmf"

        runs = cp.bench_commands(self.Runner(), "chain", "jit", 8, cache, "chain__x", "/cells/x",
                                 self.Args(), ["-v"], "/work", restore)
        return [(name, factory()) for name, factory in runs], restored

    @staticmethod
    def opt(cmd, flag):
        return cmd[cmd.index(flag) + 1]

    def test_off_and_cold_are_one_timed_bench(self):
        for cache in ("off", "cold"):
            runs, restored = self.commands(cache)
            self.assertEqual(["bench"], [n for n, _ in runs])
            cmd = runs[0][1]
            self.assertEqual((cache, "3", "1"), (self.opt(cmd, "--cache"), self.opt(cmd, "--runs"), self.opt(cmd, "--warmups")))
            self.assertEqual("/cells/x/bench.jsonl", self.opt(cmd, "--out"))
            self.assertEqual(["-v"], cmd[cmd.index("--") + 1:])
            self.assertEqual(1, len(restored))

    def test_warm_first_builds_the_store_its_timed_run_reads(self):
        runs, restored = self.commands("warm")
        self.assertEqual(["bench-prime", "bench"], [n for n, _ in runs])
        prime, timed = runs[0][1], runs[1][1]
        # The prime is one untimed-for-us cold run under the same label and
        # store, so bench's warm run finds the cold run 0 store it looks for.
        self.assertEqual(("cold", "1", "0"), (self.opt(prime, "--cache"), self.opt(prime, "--runs"), self.opt(prime, "--warmups")))
        self.assertEqual(("warm", "3", "1"), (self.opt(timed, "--cache"), self.opt(timed, "--runs"), self.opt(timed, "--warmups")))
        for flag in ("--options", "--cache-base", "--stages", "--threads", "--arm", "--game", "--workdir"):
            self.assertEqual(self.opt(prime, flag), self.opt(timed, flag), flag)
        self.assertEqual("/cells/x/bench-prime.jsonl", self.opt(prime, "--out"))
        self.assertEqual("/cells/x/bench.jsonl", self.opt(timed, "--out"))
        # Each run starts from a freshly restored input.
        self.assertEqual(2, len(restored))


def claimed(d, name="out"):
    out = os.path.join(d, name)
    cp.claim_out(out)
    return out


class OutFolderTests(unittest.TestCase):
    """--out is deleted inside (game/, work/, a retried cell), so only a folder the tool made is used."""

    def test_a_new_folder_is_made_and_marked(self):
        with tempfile.TemporaryDirectory() as d:
            out = os.path.join(d, "a", "b")
            cp.claim_out(out)
            self.assertTrue(os.path.isfile(os.path.join(out, cp.OUT_MARKER)))

    def test_an_empty_existing_folder_is_taken(self):
        with tempfile.TemporaryDirectory() as d:
            cp.claim_out(d)
            self.assertTrue(os.path.isfile(os.path.join(d, cp.OUT_MARKER)))

    def test_a_folder_the_tool_made_is_taken_again_for_resume(self):
        with tempfile.TemporaryDirectory() as d:
            out = claimed(d)
            os.makedirs(os.path.join(out, "cells", "x"))
            cp.claim_out(out)

    def test_a_folder_with_someone_elses_files_is_refused_and_left_alone(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "game", "mine"))
            with self.assertRaises(SystemExit):
                cp.claim_out(d)
            self.assertTrue(os.path.isdir(os.path.join(d, "game", "mine")))
            self.assertFalse(os.path.exists(os.path.join(d, cp.OUT_MARKER)))

    def test_a_file_is_refused(self):
        with tempfile.NamedTemporaryFile() as fh:
            with self.assertRaises(SystemExit):
                cp.claim_out(fh.name)

    def test_the_game_copy_will_not_delete_a_game_folder_it_does_not_own(self):
        # `--out .` from the repo root once meant deleting the repo's game/.
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, "src")
            os.makedirs(src)
            theirs = os.path.join(d, "repo")
            os.makedirs(os.path.join(theirs, "game"))
            with open(os.path.join(theirs, "game", "keep.txt"), "w") as fh:
                fh.write("x")
            with self.assertRaises(RuntimeError):
                cp.game_copy(src, theirs, strip=False)
            self.assertTrue(os.path.exists(os.path.join(theirs, "game", "keep.txt")))

    def test_the_work_folder_will_not_delete_a_folder_it_does_not_own(self):
        with tempfile.TemporaryDirectory() as d:
            theirs = os.path.join(d, "repo")
            os.makedirs(os.path.join(theirs, "work"))
            with open(os.path.join(theirs, "work", "keep.txt"), "w") as fh:
                fh.write("x")
            vmf = os.path.join(d, "m.vmf")
            open(vmf, "w").close()
            with self.assertRaises(RuntimeError):
                cp.Inputs(None, theirs, vmf, [])
            self.assertTrue(os.path.exists(os.path.join(theirs, "work", "keep.txt")))

    def test_owned_rmtree_refuses_a_path_outside_the_folder(self):
        with tempfile.TemporaryDirectory() as d:
            out = claimed(d)
            outside = os.path.join(d, "elsewhere")
            os.makedirs(outside)
            for path in (outside, os.path.join(out, "..", "elsewhere"), out):
                with self.assertRaises(RuntimeError):
                    cp.owned_rmtree(out, path)
            self.assertTrue(os.path.isdir(outside))
            inside = os.path.join(out, "work")
            os.makedirs(inside)
            cp.owned_rmtree(out, inside)
            self.assertFalse(os.path.exists(inside))
            cp.owned_rmtree(out, inside)   # already gone is fine


class FakeRunner:
    """Stands in for Runner: each command's outcome comes from a script keyed by the log's name."""

    def __init__(self, script):
        self.script, self.calls = script, []

    def ssmap(self, build):
        return ["ssmap"]

    def run(self, cmd, log, env=None, cwd=None, timeout=None):
        name = os.path.basename(log)[:-4]
        self.calls.append(name)
        code, text, ledger = self.script[name]
        with open(log, "ab") as fh:
            fh.write(text.encode())
        if ledger is not None:
            with open(cmd[cmd.index("--out") + 1], "w") as fh:
                fh.write("".join(json.dumps(s) + "\n" for s in ledger))
        return code, 0.1, {}


def sample(ok=True, timed=True, stored=0, cooked=0):
    return {"Timed": timed, "Ok": ok, "WallSeconds": 1.0, "CacheBytesStored": stored, "CacheCooked": cooked}


class RunCellTests(unittest.TestCase):
    class Inputs:
        work = "/nonexistent-work"

        def restore(self, stage):
            return "m.vmf"

    class Args:
        runs, warmups, game_dir, profile = 1, 0, None, []

    def run_cell(self, d, script, cache="off"):
        out = claimed(d)
        matrix = cp.load_matrix(MATRIX)
        settings = {a: v for a, v in matrix["baseline"].items() if "chain" in matrix["axes"][a]["stages"]}
        settings["cache"] = cache
        cid = "chain__cache-" + cache
        cdir = os.path.join(out, "cells", cid)
        runner = FakeRunner(script)
        rec = cp.run_cell(runner, self.Inputs(), {"stage": "chain", "settings": settings}, cid, cdir, self.Args(),
                          matrix, {"gpu": "", "vphysics": ""}, [], False)
        return rec, runner, cdir

    def test_a_retried_cell_is_judged_on_its_own_attempt_not_the_last_ones(self):
        # --resume re-runs a cell whose cell.json is not ok; its old bench.log
        # said FAILED, and appending to it once failed the cell forever.
        with tempfile.TemporaryDirectory() as d:
            cdir = os.path.join(claimed(d), "cells", "chain__cache-off")
            os.makedirs(cdir)
            with open(os.path.join(cdir, "bench.log"), "w") as fh:
                fh.write("chain run=0 FAILED boom\n")
            rec, _, cdir = self.run_cell(d, {"bench": (0, "chain run=0 wall=1.0\n", [sample()])})
            self.assertEqual("ok", rec["status"], rec.get("failure"))
            self.assertNotIn("FAILED", read_text(os.path.join(cdir, "bench.log")))

    def test_a_failed_run_in_this_attempt_still_fails_the_cell(self):
        with tempfile.TemporaryDirectory() as d:
            rec, _, _ = self.run_cell(d, {"bench": (0, "chain run=0 FAILED boom\n", [sample(ok=False)])})
            self.assertEqual("failed", rec["status"])
            rec, _, _ = self.run_cell(d, {"bench": (1, "", [sample(ok=False)])})
            self.assertEqual("failed", rec["status"])

    def test_a_warm_cell_whose_prime_stored_nothing_fails_instead_of_timing_a_cold_compile(self):
        with tempfile.TemporaryDirectory() as d:
            rec, runner, _ = self.run_cell(d, {"bench-prime": (0, "", [sample(stored=0)]),
                                               "bench": (0, "", [sample()])}, cache="warm")
            self.assertEqual("failed", rec["status"])
            self.assertIn("prime", rec["failure"])
            self.assertEqual(["bench-prime"], runner.calls)

    def test_a_warm_cell_whose_prime_exited_non_zero_fails(self):
        with tempfile.TemporaryDirectory() as d:
            rec, runner, _ = self.run_cell(d, {"bench-prime": (1, "", [sample(ok=False)]),
                                               "bench": (0, "", [sample()])}, cache="warm")
            self.assertEqual("failed", rec["status"])
            self.assertIn("exited 1", rec["failure"])
            self.assertEqual(["bench-prime"], runner.calls)

    def test_a_warm_cell_whose_prime_filled_its_store_is_timed(self):
        with tempfile.TemporaryDirectory() as d:
            rec, runner, _ = self.run_cell(d, {"bench-prime": (0, "", [sample(stored=4096)]),
                                               "bench": (0, "", [sample()])}, cache="warm")
            self.assertEqual("ok", rec["status"], rec.get("failure"))
            self.assertEqual(["bench-prime", "bench"], runner.calls)


class PrimeLedgerTests(unittest.TestCase):
    def problem(self, lines):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "bench-prime.jsonl")
            if lines is not None:
                with open(path, "w") as fh:
                    fh.write("".join(json.dumps(s) + "\n" for s in lines))
            return cp.prime_problem(path)

    def test_each_way_a_prime_can_leave_no_store(self):
        self.assertIn("no ledger", self.problem(None))
        self.assertIn("no timed run", self.problem([sample(timed=False, stored=10)]))
        self.assertIn("failed", self.problem([sample(ok=False, stored=10)]))
        self.assertIn("stored nothing", self.problem([sample()]))

    def test_a_prime_that_stored_rows_or_cooked_models_is_good(self):
        self.assertIsNone(self.problem([sample(stored=1)]))
        self.assertIsNone(self.problem([sample(cooked=2)]))


class GpuCheckTests(unittest.TestCase):
    class Inputs:
        work = "/"

        def restore(self, stage):
            return "m"

    def test_a_previous_attempts_decline_line_does_not_stop_a_corrected_run(self):
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "gpu-check.log")
            with open(log, "w") as fh:
                fh.write("Warning VRAD0707: gpu tracer declined: no device matches \"foo\"\n")
            runner = FakeRunner({"gpu-check": (0, "vrad done\n", None)})
            cp.check_gpu(runner, self.Inputs(), [], "RTX", log)   # no SystemExit
            self.assertNotIn("VRAD0707", read_text(log))

    def test_a_decline_in_this_attempt_stops_the_run(self):
        with tempfile.TemporaryDirectory() as d:
            runner = FakeRunner({"gpu-check": (0, "Warning VRAD0707: gpu tracer declined: nope\n", None)})
            with self.assertRaises(SystemExit):
                cp.check_gpu(runner, self.Inputs(), [], "RTX", os.path.join(d, "gpu-check.log"))

    def plan(self, stages):
        with tempfile.NamedTemporaryFile(suffix=".vmf", delete=False) as fh:
            vmf = fh.name
        try:
            return cp.plan(cp.parse_args(["--map", vmf, "--gpu", "RTX", "--stages", stages]))[3]
        finally:
            os.remove(vmf)

    def test_the_check_runs_only_when_a_chosen_stage_has_a_gpu_cell(self):
        # The tracer axis has a gpu value whatever --stages says; only the
        # cells tell whether any stage that runs will use it.
        self.assertFalse(cp.needs_gpu_check(self.plan("vbsp,vvis")))
        self.assertTrue(cp.needs_gpu_check(self.plan("vrad")))
        self.assertTrue(cp.needs_gpu_check(self.plan("chain")))


class RunnerTimeoutTests(unittest.TestCase):
    class Args:
        dotnet = "dotnet"

    def test_a_command_past_its_timeout_is_stopped_and_reported(self):
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "t.log")
            start = time.monotonic()
            code, _, _ = cp.Runner(self.Args()).run(["sh", "-c", "sleep 30"], log, timeout=0.3)
            self.assertLess(time.monotonic() - start, 10)
            self.assertEqual(cp.TIMEOUT_EXIT, code)
            self.assertIn("timed out after 0.3 s", read_text(log))

    def test_a_timeout_stops_the_commands_children_too(self):
        # dotnet-trace and perf run the compile as their child; stopping only
        # the wrapper would leave the compile running.
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "t.log")
            pid = os.path.join(d, "pid")
            cp.Runner(self.Args()).run(["sh", "-c", f"sleep 30 & echo $! > {pid}; wait"], log, timeout=0.5)
            child = int(read_text(pid))
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline and alive(child):
                time.sleep(0.05)
            self.assertFalse(alive(child))

    def test_a_command_inside_its_timeout_keeps_its_own_exit_code(self):
        with tempfile.TemporaryDirectory() as d:
            log = os.path.join(d, "t.log")
            code, _, ru = cp.Runner(self.Args()).run(["sh", "-c", "echo hi; exit 3"], log, timeout=30)
            self.assertEqual(3, code)
            self.assertIn("hi", read_text(log))
            self.assertIn("user_s", ru)
            code, _, _ = cp.Runner(self.Args()).run(["sh", "-c", "exit 0"], log)
            self.assertEqual(0, code)


def alive(pid):
    """Whether pid is a live process (a zombie counts as gone)."""
    try:
        with open(f"/proc/{pid}/stat") as fh:
            return fh.read().split(")")[-1].split()[0] != "Z"
    except OSError:
        return False


class HeapTimelineTests(unittest.TestCase):
    """Each snapshot is labelled with when it was taken, not n * interval."""

    def test_snapshot_times_are_measured(self):
        with tempfile.TemporaryDirectory() as d:
            gcdump = os.path.join(d, "dotnet-gcdump")
            with open(gcdump, "w") as fh:
                # collect -p PID -o PATH writes the dump; report PATH prints one type row.
                fh.write('#!/bin/sh\nif [ "$1" = collect ]; then sleep 0.2; echo x > "$5"; '
                         'else echo "  1,048,576  1  System.Byte[]"; fi\n')
            os.chmod(gcdump, 0o755)

            class R:
                def tool(self, name):
                    return gcdump

                def ssmap(self, build):
                    return ["sh", "-c", "sleep 1.6"]

            class I:
                work = d

            result = cp.heap_snapshots(R(), d, lambda name: ("m", None), lambda name, store: None, {}, I(), "vrad",
                                       "jit", [], 1, [], 0.5)
            self.assertEqual(0, result["exit"])
            with open(os.path.join(d, "heap.json")) as fh:
                timeline = json.load(fh)["timeline"]
            self.assertGreaterEqual(len(timeline), 2)
            # The first is taken 0.25 s in, not at 0; each later one follows
            # the previous one's collect (0.2 s here) plus the interval.
            self.assertGreaterEqual(timeline[0]["at_s"], 0.2)
            for a, b in zip(timeline, timeline[1:]):
                self.assertGreaterEqual(b["at_s"] - a["at_s"], 0.5 + 0.2 - 0.05)
            self.assertTrue(all(t["collect_s"] >= 0.15 for t in timeline))


class PerfMapTests(unittest.TestCase):
    def test_the_wrapper_records_the_pid_the_command_runs_as(self):
        with tempfile.TemporaryDirectory() as d:
            pidfile = os.path.join(d, "pid")
            out = subprocess.run(cp.pid_wrapped(["sh", "-c", "echo $$"], pidfile), capture_output=True, text=True)
            self.assertEqual(out.stdout.strip(), read_text(pidfile).strip())

    def test_only_the_recorded_processs_map_files_are_removed(self):
        with tempfile.TemporaryDirectory() as d:
            tmp = os.path.join(d, "tmp")
            os.makedirs(tmp)
            mine = ["perf-41.map", "perfinfo-41.map", "jit-41.dump"]
            theirs = ["perf-4.map", "perf-411.map", "perfinfo-42.map"]
            for n in mine + theirs:
                open(os.path.join(tmp, n), "w").close()
            pidfile = os.path.join(d, "pid")
            with open(pidfile, "w") as fh:
                fh.write("41\n")
            self.assertEqual(sorted(mine), sorted(os.path.basename(p) for p in cp.remove_perf_maps(pidfile, tmp)))
            self.assertEqual(sorted(theirs), sorted(os.listdir(tmp)))
            self.assertFalse(os.path.exists(pidfile))

    def test_no_pid_file_removes_nothing(self):
        with tempfile.TemporaryDirectory() as d:
            open(os.path.join(d, "perf-1.map"), "w").close()
            self.assertEqual([], cp.remove_perf_maps(os.path.join(d, "missing"), d))
            self.assertEqual(["perf-1.map"], os.listdir(d))


class ScriptEntryTests(unittest.TestCase):
    def test_running_the_file_as_a_script_runs_every_class(self):
        # unittest.main() once sat above the last class, so running this file
        # directly skipped it; it must stay the last statement.
        with open(os.path.abspath(__file__), encoding="utf-8") as fh:
            tail = [l for l in fh.read().splitlines() if l.strip()][-2:]
        self.assertEqual(['if __name__ == "__main__":', "    unittest.main()"], tail)


if __name__ == "__main__":
    unittest.main()
