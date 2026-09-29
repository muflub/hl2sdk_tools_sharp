"""Facts for tools/toolchain_bench.py.

    python3 -m unittest discover -s tools/tests
"""

import contextlib
import io
import os
import shutil
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import toolchain_bench as tb  # noqa: E402

LIBRARYFOLDERS = '''"libraryfolders"
{
\t"0"
\t{
\t\t"path"\t\t"/steam/a"
\t\t"apps"
\t\t{
\t\t\t"440"\t\t"100"
\t\t}
\t}
\t"1"
\t{
\t\t"path"\t\t"/steam/b"
\t\t"apps"
\t\t{
\t\t\t"243750"\t\t"200"
\t\t}
\t}
}
'''

GAMEINFO = '''"GameInfo"
{
\tgame\t"Test"
\thidden_maps
\t{
\t\t"test_speakers"\t\t1
\t}
\tFileSystem
\t{
\t\tSteamAppId\t\t\t\t243750
\t\t// a comment with "quotes" in it
\t\tSearchPaths
\t\t{
\t\t\tgame+mod\t\t\tmod_x/custom/*
\t\t\tmod+mod_write\t\t|gameinfo_path|.
\t\t\tdefault_write_path\t|gameinfo_path|.
\t\t\tgame\t\t\t\t|appid_243750|hl2/hl2_textures.vpk
\t\t\tgame\t\t\t\t|appid_999|missing
\t\t\t"platform"\t\t"|appid_243750|platform"
\t\t}
\t}
}
'''


def fake_read(files):
    return lambda path: files.get(path.replace("\\", "/"))


def bsp_bytes(lumps):
    """A minimal BSP: the header with the given {lump: length} and no data behind it."""
    data = bytearray(b"VBSP" + struct.pack("<i", 20))
    for i in range(64):
        data += struct.pack("<iiii", 0, lumps.get(i, 0), 0, 0)
    data += struct.pack("<i", 1)
    return bytes(data)


class SteamTests(unittest.TestCase):
    def test_libraries_list_each_folder_with_its_apps(self):
        read = fake_read({"/root/steamapps/libraryfolders.vdf": LIBRARYFOLDERS})
        self.assertEqual(tb.steam_libraries(["/root"], read), [("/steam/a", {440}), ("/steam/b", {243750})])

    def test_libraries_are_not_repeated_across_roots(self):
        files = {"/r1/steamapps/libraryfolders.vdf": LIBRARYFOLDERS,
                 "/r2/steamapps/libraryfolders.vdf": LIBRARYFOLDERS}
        self.assertEqual(len(tb.steam_libraries(["/r1", "/r2"], fake_read(files))), 2)

    def test_a_missing_root_is_skipped(self):
        self.assertEqual(tb.steam_libraries(["/nowhere"], fake_read({})), [])

    def test_find_app_reads_the_manifest_installdir(self):
        files = {"/root/steamapps/libraryfolders.vdf": LIBRARYFOLDERS,
                 "/steam/b/steamapps/appmanifest_243750.acf": '"AppState" { "appid" "243750" "installdir" "SDK Base" }'}
        found = tb.find_app(243750, ["/root"], fake_read(files))
        self.assertEqual(found.replace("\\", "/"), "/steam/b/steamapps/common/SDK Base")

    def test_find_app_is_none_when_no_library_lists_it(self):
        read = fake_read({"/root/steamapps/libraryfolders.vdf": LIBRARYFOLDERS})
        self.assertIsNone(tb.find_app(620, ["/root"], read))

    def test_find_app_is_none_without_a_manifest(self):
        read = fake_read({"/root/steamapps/libraryfolders.vdf": LIBRARYFOLDERS})
        self.assertIsNone(tb.find_app(440, ["/root"], read))


class GameInfoTests(unittest.TestCase):
    def test_reads_app_id_and_search_paths_in_order(self):
        app_id, paths = tb.read_gameinfo(GAMEINFO)
        self.assertEqual(app_id, 243750)
        self.assertEqual(paths, [
            ("game+mod", "mod_x/custom/*"),
            ("mod+mod_write", "|gameinfo_path|."),
            ("default_write_path", "|gameinfo_path|."),
            ("game", "|appid_243750|hl2/hl2_textures.vpk"),
            ("game", "|appid_999|missing"),
            ("platform", "|appid_243750|platform"),
        ])

    def test_nested_blocks_outside_search_paths_are_not_paths(self):
        _, paths = tb.read_gameinfo(GAMEINFO)
        self.assertNotIn(("test_speakers", "1"), paths)

    def test_no_file_system_block_reads_as_nothing(self):
        self.assertEqual(tb.read_gameinfo('"GameInfo" { game "x" }'), (None, []))

    def test_translation_roots_every_kind_of_location(self):
        game = os.path.abspath("/games/mod_x")
        base = os.path.dirname(game)
        present = {os.path.normpath(p) for p in (
            os.path.join(base, "mod_x/custom"), game,
            "/sdk/hl2/hl2_textures_dir.vpk", "/sdk/platform")}
        _, paths = tb.read_gameinfo(GAMEINFO)
        kept, dropped = tb.translate_search_paths(
            paths, game, lambda a: "/sdk" if a == 243750 else None, lambda p: os.path.normpath(p) in present)
        self.assertEqual(kept, [
            ("game+mod", os.path.normpath(os.path.join(base, "mod_x/custom/*"))),
            ("mod", os.path.normpath(game)),
            ("game", os.path.normpath("/sdk/hl2/hl2_textures.vpk")),
            ("platform", os.path.normpath("/sdk/platform")),
        ])
        self.assertEqual([loc for loc, _ in dropped], ["|appid_999|missing"])
        self.assertIn("not installed", dropped[0][1])

    def test_a_location_not_on_disk_is_dropped_with_where_it_was_looked_for(self):
        kept, dropped = tb.translate_search_paths(
            [("game", "|gameinfo_path|download")], "/g/mod", lambda a: None, lambda p: False)
        self.assertEqual(kept, [])
        self.assertIn("download", dropped[0][1])

    def test_absolute_and_all_source_engine_paths(self):
        kept, _ = tb.translate_search_paths(
            [("game", "/abs/dir"), ("game", "|all_source_engine_paths|hl2")],
            "/g/mod", lambda a: None, lambda p: True)
        self.assertEqual([loc for _, loc in kept],
                         [os.path.normpath("/abs/dir"), os.path.normpath(os.path.join(os.path.abspath("/g"), "hl2"))])

    def test_written_gameinfo_puts_its_own_write_path_first(self):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d)
        path = tb.write_tool_gameinfo(d, 440, [("game", "/content")], os.path.join(d, "w"))
        with open(path, encoding="utf-8") as f:
            text = f.read()
        app_id, paths = tb.read_gameinfo(text)
        self.assertEqual(app_id, 440)
        self.assertEqual(paths[0], ("mod+mod_write+default_write_path", tb.wine_path(os.path.join(d, "w"))))
        self.assertEqual(paths[1], ("game", tb.wine_path("/content")))

    def test_written_gameinfo_defaults_the_app_id(self):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d)
        with open(tb.write_tool_gameinfo(d, None, [], os.path.join(d, "w")), encoding="utf-8") as f:
            self.assertEqual(tb.read_gameinfo(f.read())[0], tb.SDK_BASE_MP)


class OutputCheckTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)

    def write(self, name, data):
        path = os.path.join(self.dir, name)
        with open(path, "wb") as f:
            f.write(data)
        return path

    def test_reads_vis_and_lighting_lump_sizes(self):
        path = self.write("m.bsp", bsp_bytes({tb.LUMP_VISIBILITY: 34, tb.LUMP_LIGHTING: 1000, tb.LUMP_LIGHTING_HDR: 7}))
        out = tb.check_output(path, [])
        self.assertEqual((out["vis_bytes"], out["light_bytes"], out["light_hdr_bytes"]), (34, 1000, 7))
        self.assertEqual(len(out["sha256"]), 64)

    def test_not_a_bsp_has_no_lumps(self):
        self.assertIsNone(tb.bsp_lumps(b"nope"))
        out = tb.check_output(self.write("m.bsp", b"x" * 2000), [])
        self.assertIsNone(out["light_bytes"])

    def test_missing_output(self):
        out = tb.check_output(os.path.join(self.dir, "none.bsp"), [])
        self.assertIsNone(out["bsp_bytes"])

    def test_counts_not_found_lines_in_every_way_the_tools_word_them(self):
        log = self.write("a.log", b"Material not found!: x\nWarning: Couldn't open texlight file\n"
                                  b"Can't load skybox file\nfine\n")
        self.assertEqual(tb.check_output(os.path.join(self.dir, "none.bsp"), [log])["not_found"], 3)


class ToolsetTests(unittest.TestCase):
    def test_wine_tools_take_options_before_the_map_and_run_in_their_folder(self):
        t = tb.WineToolset("pp", "/pp", tb.PP_EXE, "/wine", "/games/x", tb.PP_DEFAULT_ARGS)
        argv, cwd = t.stage("vbsp", "/w", "m", ["-threads", "4"])
        self.assertEqual(cwd, "/pp")
        self.assertEqual(argv[0], "/wine")
        self.assertTrue(argv[1].endswith("vbspplusplus.exe"))
        self.assertEqual(argv[2:], ["-game", tb.wine_path("/games/x"), "-matsyscompat", "-threads", "4",
                                    tb.wine_path(os.path.join("/w", "m.vmf"))])

    def test_later_stages_take_the_bsp(self):
        t = tb.WineToolset("stock", "/s", {s: s + ".exe" for s in tb.STAGES}, "/wine", "/g")
        argv, _ = t.stage("vrad", "/w", "m", [])
        self.assertEqual(argv[-1], tb.wine_path(os.path.join("/w", "m.bsp")))
        self.assertNotIn("-matsyscompat", argv)

    def test_ssmap_reads_the_original_game_directory(self):
        argv, cwd = tb.SsmapToolset("ssmap", ["dotnet", "ssmap.dll"], "/game/mod").stage("vvis", "/w", "m", [])
        self.assertEqual(argv, ["dotnet", "ssmap.dll", "vvis", "-game", "/game/mod", os.path.join("/w", "m.bsp")])
        self.assertEqual(cwd, "/w")

    def test_aot_ssmap_is_the_executable_alone(self):
        argv, _ = tb.SsmapToolset("ssmap-aot", ["/bin/aot/ssmap"], "/g").stage("vbsp", "/w", "m", ["-threads", "2"])
        self.assertEqual(argv, ["/bin/aot/ssmap", "vbsp", "-game", "/g", "-threads", "2", os.path.join("/w", "m.vmf")])

    def test_fast_toolsets_add_the_flag_to_vvis_only(self):
        sets = tb.ssmap_toolsets(["ssmap-fast", "ssmap-aot-fast"], "dotnet", "ssmap.dll", "/aot/ssmap", "/g")
        self.assertEqual(sets["ssmap-fast"].command, ["dotnet", "ssmap.dll"])
        self.assertEqual(sets["ssmap-aot-fast"].command, ["/aot/ssmap"])
        for t in sets.values():
            vbsp, _ = t.stage("vbsp", "/w", "m", ["-threads", "2"])
            vvis, _ = t.stage("vvis", "/w", "m", ["-threads", "2"])
            vrad, _ = t.stage("vrad", "/w", "m", ["-threads", "2"])
            self.assertEqual(vvis[-5:], ["/g", tb.SSMAP_FAST_FLAG, "-threads", "2", os.path.join("/w", "m.bsp")])
            self.assertNotIn(tb.SSMAP_FAST_FLAG, vbsp)
            self.assertNotIn(tb.SSMAP_FAST_FLAG, vrad)

    def test_plain_ssmap_toolsets_have_no_flag(self):
        sets = tb.ssmap_toolsets(["stock", "ssmap", "ssmap-aot"], "dotnet", "ssmap.dll", "/aot/ssmap", "/g")
        self.assertEqual(sorted(sets), ["ssmap", "ssmap-aot"])
        for t in sets.values():
            for stage in tb.STAGES:
                self.assertNotIn(tb.SSMAP_FAST_FLAG, t.stage(stage, "/w", "m", [])[0])

    def test_toolsets_parse_in_order_and_name_the_unknown(self):
        self.assertEqual(tb.parse_toolsets("ssmap-fast, stock,,ssmap-aot-fast"),
                         (["ssmap-fast", "stock", "ssmap-aot-fast"], []))
        self.assertEqual(tb.parse_toolsets("ssmap,ssmap-slow"), (["ssmap", "ssmap-slow"], ["ssmap-slow"]))

    def test_the_default_toolsets_are_all_six(self):
        self.assertEqual(tb.parse_toolsets(tb.parse_args([]).toolsets)[0],
                         ["stock", "pp", "ssmap", "ssmap-aot", "ssmap-fast", "ssmap-aot-fast"])

    def test_aot_rid_follows_the_platform(self):
        self.assertEqual(tb.aot_rid("x86_64", "linux"), "linux-x64")
        self.assertEqual(tb.aot_rid("aarch64", "linux"), "linux-arm64")
        self.assertEqual(tb.aot_rid("arm64", "darwin"), "osx-arm64")
        self.assertEqual(tb.aot_rid("x86_64", "darwin"), "osx-x64")
        self.assertEqual(tb.aot_rid("AMD64", "win32"), "win-x64")

    def test_wine_path_is_rooted_at_z(self):
        self.assertTrue(tb.wine_path("/a/b").startswith("Z:"))


class DryRunTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.vmf = os.path.join(self.dir, "m.vmf")
        with open(self.vmf, "w") as f:
            f.write("versioninfo {}")
        self.game = os.path.join(self.dir, "game")
        os.makedirs(self.game)
        with open(os.path.join(self.game, "gameinfo.txt"), "w") as f:
            f.write(GAMEINFO)

    def dry_run(self, toolsets):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = tb.main(["--dry-run", "--toolsets", toolsets, "--map", f"m={self.vmf}:{self.game}",
                            "--out", os.path.join(self.dir, "out"), "--dotnet", "dotnet", "--vvis=-v"])
        self.assertEqual(code, 0)
        return out.getvalue().splitlines()

    def test_prints_one_chain_per_toolset_with_the_flag_on_fast_vvis_only(self):
        lines = self.dry_run("ssmap,ssmap-fast,ssmap-aot-fast")
        self.assertEqual(len(lines), 9)
        by = {(line.split("]")[0].split("/")[1], line.split(") ")[1].split()[2 if "dotnet" in line else 1]): line
              for line in lines}
        self.assertNotIn(tb.SSMAP_FAST_FLAG, by[("ssmap", "vvis")])
        self.assertIn(f"-game {self.game} {tb.SSMAP_FAST_FLAG} -v ", by[("ssmap-fast", "vvis")])
        self.assertIn(f"{tb.SSMAP_FAST_FLAG} -v ", by[("ssmap-aot-fast", "vvis")])
        for name in ("ssmap-fast", "ssmap-aot-fast"):
            self.assertNotIn(tb.SSMAP_FAST_FLAG, by[(name, "vbsp")])
            self.assertNotIn(tb.SSMAP_FAST_FLAG, by[(name, "vrad")])

    def test_an_unknown_toolset_is_a_usage_error(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(tb.main(["--dry-run", "--toolsets", "ssmap-turbo"]), 2)
        self.assertIn("ssmap-aot-fast", err.getvalue())


class FakeToolset(tb.Toolset):
    """Each stage is a Python one-liner; vrad writes a BSP with the given lighting."""

    def __init__(self, light, fail_stage=None):
        super().__init__("fake")
        self.light, self.fail_stage = light, fail_stage

    def stage(self, stage, work, stem, extra):
        if stage == self.fail_stage:
            return [sys.executable, "-c", "import sys; sys.exit(3)"], work
        if stage != "vrad":
            return [sys.executable, "-c", "pass"], work
        data = bsp_bytes({tb.LUMP_LIGHTING: self.light})
        script = f"open({os.path.join(work, stem + '.bsp')!r}, 'wb').write({data!r})"
        return [sys.executable, "-c", script], work


class ChainTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.vmf = os.path.join(self.dir, "m.vmf")
        with open(self.vmf, "w") as f:
            f.write("versioninfo {}")

    def run_chain(self, toolset):
        return tb.run_chain(toolset, dict(os.environ), self.vmf, os.path.join(self.dir, "work"),
                            {s: [] for s in tb.STAGES})

    def test_a_lit_chain_is_ok_and_timed_per_stage(self):
        record = self.run_chain(FakeToolset(light=100))
        self.assertTrue(record["ok"])
        self.assertEqual(list(record["stages"]), tb.STAGES)
        self.assertEqual(record["light_bytes"], 100)

    def test_an_unlit_output_is_a_failure_not_a_timing(self):
        record = self.run_chain(FakeToolset(light=0))
        self.assertFalse(record["ok"])
        self.assertIn("no lighting", record["failure"])

    def test_a_failing_stage_stops_the_chain(self):
        record = self.run_chain(FakeToolset(light=100, fail_stage="vvis"))
        self.assertFalse(record["ok"])
        self.assertEqual(list(record["stages"]), ["vbsp", "vvis"])
        self.assertIn("vvis exited 3", record["failure"])

    def test_the_work_folder_starts_empty(self):
        work = os.path.join(self.dir, "work")
        os.makedirs(work)
        with open(os.path.join(work, "stale.bsp"), "w") as f:
            f.write("old")
        self.run_chain(FakeToolset(light=1))
        self.assertFalse(os.path.exists(os.path.join(work, "stale.bsp")))


class SummaryTests(unittest.TestCase):
    @staticmethod
    def run_record(walls, ok=True):
        return {"ok": ok, "stages": {s: {"wall": w} for s, w in zip(tb.STAGES, walls)}}

    def test_medians_skip_failed_runs(self):
        results = {"m": {"stock": [self.run_record([1, 1, 1]), self.run_record([3, 3, 3]),
                                   self.run_record([100, 100, 100], ok=False)]}}
        row = tb.summarise(results, ["stock"])["m"]["stock"]
        self.assertEqual(row["vbsp"][0], 2)
        self.assertEqual(row["total"], (6, 3, 9))

    def test_a_toolset_with_no_ok_run_has_no_numbers(self):
        row = tb.summarise({"m": {"pp": [self.run_record([1, 1, 1], ok=False)]}}, ["pp"])["m"]["pp"]
        self.assertEqual(row["total"], (None, None, None))

    def test_markdown_compares_with_stock_and_lists_dropped_paths(self):
        results = {"m": {"stock": [dict(self.run_record([1, 1, 2]), bsp_bytes=1, vis_bytes=1, light_bytes=5,
                                        light_hdr_bytes=0, not_found=0)],
                         "ssmap": [dict(self.run_record([1, 0.5, 0.5]), bsp_bytes=1, vis_bytes=1, light_bytes=5,
                                        light_hdr_bytes=0, not_found=0)]}}
        md = tb.render_markdown(["h"], tb.summarise(results, ["stock", "ssmap"]), results, ["stock", "ssmap"],
                                {"m": [("|appid_1|x", "Steam app 1 is not installed")]})
        self.assertIn("| ssmap | 1.00 | 0.50 | 0.50 | 2.00 | 2.00–2.00 | 0.50× | 1/1 |", md)
        self.assertIn("| ssmap | 1 | 1 | 5 | 0 | 0 |  |", md)
        self.assertIn("Steam app 1 is not installed", md)

    def test_markdown_puts_the_fast_rows_vis_bytes_beside_plain_ssmaps(self):
        def record(vis):
            return dict(self.run_record([1, 1, 1]), bsp_bytes=9, vis_bytes=vis, light_bytes=5,
                        light_hdr_bytes=0, not_found=0)
        results = {"m": {"ssmap": [record(700)], "ssmap-fast": [record(720)]}}
        md = tb.render_markdown(["h"], tb.summarise(results, ["ssmap", "ssmap-fast"]), results,
                                ["ssmap", "ssmap-fast"], {})
        self.assertIn("| ssmap | 9 | 700 | 5 |", md)
        self.assertIn("| ssmap-fast | 9 | 720 | 5 |", md)

    def test_map_spec_splits_at_the_last_colon(self):
        self.assertEqual(tb.parse_map_spec("a=C:/x.vmf:game/mod"), ("a", "C:/x.vmf", "game/mod"))
        with self.assertRaises(ValueError):
            tb.parse_map_spec("nothing")


if __name__ == "__main__":
    unittest.main()
