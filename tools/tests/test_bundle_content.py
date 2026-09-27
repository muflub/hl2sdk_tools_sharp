"""Facts for tools/bundle_content.py.

    python3 -m unittest discover -s tools/tests
"""

import os
import sys
import tempfile
import time
import unittest
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import bundle_content as bc  # noqa: E402

GAMEINFO = b'"GameInfo" { FileSystem { SteamAppId 243750 SearchPaths { game+mod |gameinfo_path|. } } }\n'


def manifest(command, *lines):
    return ("# ssmap content bundle\n# command: " + command + "\n# kind\tpath\tsha256\tbytes\tsource\n"
            + "".join(line + "\n" for line in lines))


def write_zip(path, files, gameinfo=GAMEINFO):
    with zipfile.ZipFile(path, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)
        if gameinfo is not None:
            z.writestr(bc.GAMEINFO, gameinfo)


def read_zip(path):
    with zipfile.ZipFile(path) as z:
        return {i.filename: z.read(i) for i in z.infolist()}


class CombosTests(unittest.TestCase):
    def test_the_combinations_cover_the_plain_compile_and_static_prop_lighting_in_ldr_hdr_and_both(self):
        self.assertEqual([], bc.COMBOS[0][1])
        flags = [set(args) for _, args in bc.COMBOS]
        for wanted in ("-ldr", "-hdr", "-both", "-final", "-textureshadows"):
            self.assertTrue(any(wanted in f for f in flags), wanted)
        for f in flags[1:]:
            self.assertTrue({"-StaticPropLighting", "-StaticPropPolys"} <= f)

    def test_every_combination_puts_its_vrad_flags_after_the_vrad_marker(self):
        for name, args in bc.COMBOS[1:]:
            self.assertLess(args.index("--vrad"), args.index("-StaticPropLighting"), name)

    def test_names_are_unique(self):
        names = [n for n, _ in bc.COMBOS]
        self.assertEqual(len(names), len(set(names)))


class RunCommandTests(unittest.TestCase):
    def test_the_command_records_without_writing_the_map(self):
        cmd = bc.run_command("dotnet", "ssmap.dll", "/m.vmf", "/g", "/w/a.zip", None, ["--vrad", "-hdr"])
        self.assertEqual(["dotnet", "ssmap.dll", "all", "/m.vmf", "-game", "/g", "--no-write",
                          "--record-content", "/w/a.zip", "--vrad", "-hdr"], cmd)

    def test_threads_are_a_chain_option(self):
        cmd = bc.run_command("dotnet", "ssmap.dll", "/m.vmf", "/g", "/w/a.zip", 4, [])
        self.assertEqual(["-threads", "4"], cmd[6:8])


class NeedsBuildTests(unittest.TestCase):
    def test_a_missing_build_needs_one(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertTrue(bc.needs_build(os.path.join(d, "ssmap.dll"), d))

    def test_a_source_newer_than_the_build_needs_one_and_bin_obj_are_ignored(self):
        with tempfile.TemporaryDirectory() as d:
            dll = os.path.join(d, "ssmap.dll")
            open(dll, "w").close()
            old = time.time() - 100
            os.utime(dll, (old, old))
            os.makedirs(os.path.join(d, "obj"))
            open(os.path.join(d, "obj", "x.cs"), "w").close()
            self.assertFalse(bc.needs_build(dll, d))
            open(os.path.join(d, "A.cs"), "w").close()
            self.assertTrue(bc.needs_build(dll, d))


class ManifestTests(unittest.TestCase):
    def test_a_manifest_parses_into_commands_and_entries(self):
        commands, entries = bc.parse_manifest(manifest("ssmap all m.vmf", "read\tmaterials/a.vmt\tab\t3\tvpk"))
        self.assertEqual(["ssmap all m.vmf"], commands)
        self.assertEqual({"materials/a.vmt": ("read", "ab", "3", "vpk")}, entries)

    def test_the_strongest_kind_wins(self):
        into = {"a": ("missing", "-", "-", "-"), "b": ("read", "h", "1", "m")}
        bc.merge_entries(into, {"a": ("read", "h", "1", "m"), "b": ("resolved", "h", "1", "m"),
                                "lights.rad": ("loose", "l", "2", "fallback: x")})
        self.assertEqual("read", into["a"][0])
        self.assertEqual("read", into["b"][0])
        self.assertEqual("loose", into["lights.rad"][0])

    def test_a_loose_file_beats_a_miss_at_its_path(self):
        into = {"lights.rad": ("missing", "-", "-", "-")}
        bc.merge_entries(into, {"lights.rad": ("loose", "l", "2", "fallback: x")})
        self.assertEqual("loose", into["lights.rad"][0])

    def test_two_hashes_for_one_path_are_refused(self):
        with self.assertRaises(bc.MergeError):
            bc.merge_entries({"a": ("read", "h1", "1", "m")}, {"a": ("read", "h2", "1", "m")})

    def test_the_merged_manifest_lists_every_command_and_sorts_paths(self):
        text = bc.format_manifest(["one", "two"], {"b": ("read", "h", "1", "m"), "a": ("missing", "-", "-", "-")}, 2)
        lines = text.splitlines()
        self.assertIn("# command: one", lines)
        self.assertIn("# command: two", lines)
        self.assertEqual(["missing\ta\t-\t-\t-", "read\tb\th\t1\tm"], lines[-2:])


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def path(self, name):
        return os.path.join(self.dir.name, name)

    def test_files_are_deduplicated_by_path_and_the_manifests_merged(self):
        write_zip(self.path("a.zip"), {
            "materials/a.vmt": b"a",
            bc.MANIFEST: manifest("run a", "read\tmaterials/a.vmt\tha\t1\tvpk", "missing\tmaterials/x.vmt\t-\t-\t-"),
        })
        write_zip(self.path("b.zip"), {
            "materials/a.vmt": b"a",
            "models/p.mdl": b"mdl",
            bc.MANIFEST: manifest("run b", "read\tmaterials/a.vmt\tha\t1\tvpk", "read\tmodels/p.mdl\thp\t3\tvpk",
                                  "missing\tmaterials/x.vmt\t-\t-\t-"),
        })

        files, size, misses = bc.merge([self.path("a.zip"), self.path("b.zip")], self.path("out/m.zip"))

        self.assertEqual((2, 4, 1), (files, size, misses))
        merged = read_zip(self.path("out/m.zip"))
        self.assertEqual(sorted(["materials/a.vmt", "models/p.mdl", bc.GAMEINFO, bc.MANIFEST]), sorted(merged))
        self.assertEqual(GAMEINFO, merged[bc.GAMEINFO])
        text = merged[bc.MANIFEST].decode("utf-8")
        self.assertIn("# command: run a", text)
        self.assertIn("# command: run b", text)
        self.assertIn("read\tmodels/p.mdl\thp\t3\tvpk", text)
        self.assertFalse(os.path.exists(self.path("out/m.zip.tmp")))

    def test_different_bytes_for_one_path_are_refused(self):
        write_zip(self.path("a.zip"), {"f": b"1", bc.MANIFEST: manifest("a")})
        write_zip(self.path("b.zip"), {"f": b"2", bc.MANIFEST: manifest("b")})
        with self.assertRaises(bc.MergeError):
            bc.merge([self.path("a.zip"), self.path("b.zip")], self.path("m.zip"))

    def test_different_gameinfos_are_refused(self):
        write_zip(self.path("a.zip"), {bc.MANIFEST: manifest("a")})
        write_zip(self.path("b.zip"), {bc.MANIFEST: manifest("b")}, gameinfo=b"other")
        with self.assertRaises(bc.MergeError):
            bc.merge([self.path("a.zip"), self.path("b.zip")], self.path("m.zip"))

    def test_no_gameinfo_is_refused(self):
        write_zip(self.path("a.zip"), {bc.MANIFEST: manifest("a")}, gameinfo=None)
        with self.assertRaises(bc.MergeError):
            bc.merge([self.path("a.zip")], self.path("m.zip"))

    def test_the_same_inputs_make_the_same_zip_bytes(self):
        write_zip(self.path("a.zip"), {"f": b"1", bc.MANIFEST: manifest("a", "read\tf\th\t1\tm")})
        bc.merge([self.path("a.zip")], self.path("one.zip"))
        bc.merge([self.path("a.zip")], self.path("two.zip"))
        with open(self.path("one.zip"), "rb") as one, open(self.path("two.zip"), "rb") as two:
            self.assertEqual(one.read(), two.read())


class MainTests(unittest.TestCase):
    def test_a_missing_map_is_refused_before_anything_runs(self):
        self.assertEqual(2, bc.main(["--map", "/no/such/map.vmf", "--game", bc.REPO]))

    def test_a_game_without_gameinfo_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            vmf = os.path.join(d, "m.vmf")
            open(vmf, "w").close()
            self.assertEqual(2, bc.main(["--map", vmf, "--game", d]))

    def test_sizes_read_as_people_write_them(self):
        self.assertEqual("12 bytes", bc.human(12))
        self.assertEqual("1.5 KB", bc.human(1536))
        self.assertEqual("2.0 MB", bc.human(2 * 1024 * 1024))


if __name__ == "__main__":
    unittest.main()
