"""Facts for tools/compile_perf.py and tools/compile_perf_summary.py.

    python3 -m unittest discover -s tools/tests
"""

import itertools
import json
import os
import sys
import tempfile
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
            os.makedirs(out)

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


if __name__ == "__main__":
    unittest.main()
