"""Facts for tools/WarmBench/perf_by_stage.py.

    python3 -m unittest discover -s tools/tests
"""

import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "WarmBench"))

import perf_by_stage as pbs  # noqa: E402

# Wave 0 is the warm-up; wave 1 runs vbsp from 1.0 s, a substage at 1.5 s and
# vrad from 2.0 s. The "wave" lines are the harness's own and carry no stage.
MARKS = """\
500000000 wave0 begin
500000000 w0j0 vbsp
600000000 w0j0 vrad
900000000 wave0 end
1000000000 wave1 begin
1000000000 w1j0 vbsp
1000000000 w1j0 sub vbsp.setup
1500000000 w1j0 sub vbsp.Csg
2000000000 w1j0 vrad
2000000000 w1j0 sub vrad.setup
3000000000 w1j0 write
3000000000 wave1 end
"""

PERF_MAP = "/tmp/perf-123.map"


def sample(seconds, *frames):
    """One `perf script` sample at the given time, leaf frame first."""
    whole, frac = divmod(round(seconds * 1_000_000), 1_000_000)
    lines = [f"dotnet  123 {whole}.{frac:06d}: "]
    for i, (symbol, dso) in enumerate(frames):
        lines.append(f"\t    {i:012x} {symbol} ({dso})")
    return "\n".join(lines) + "\n\n"


OURS = ("[unknown] SourceSharp.MapTools.Bsp.Csg::Chop(int)[Optimized]", PERF_MAP)
LIGHT = ("[unknown] SourceSharp.MapTools.Rad.Light::Gather(int)[Optimized]", PERF_MAP)
GC = ("WKS::gc_heap::mark_object_simple", "/usr/share/dotnet/libcoreclr.so")
JIT = ("Compiler::compCompile", "/usr/share/dotnet/libclrjit.so")
LOOP = ("[unknown] SourceSharp.MapTools.Parallel.WorkQueue::Run()[Optimized]", PERF_MAP)

SCRIPT = (
    sample(0.55, JIT)                 # wave 0: left out by default
    + sample(0.95, OURS)              # between waves: in no stage of wave 1
    + sample(1.2, OURS, LOOP)         # wave 1 vbsp, vbsp.setup
    + sample(1.7, OURS, LOOP)         # wave 1 vbsp, vbsp.Csg
    + sample(1.8, GC)                 # wave 1 vbsp, vbsp.Csg
    + sample(2.5, LIGHT, LOOP)        # wave 1 vrad
)


class ParseTests(unittest.TestCase):
    def test_marks_become_intervals_that_start_each_stage_with_its_setup(self):
        intervals = pbs.read_marks(io.StringIO(MARKS))
        wave1 = [(ns, top, sub) for ns, w, top, sub in intervals if w == 1]
        self.assertEqual(wave1[0], (1_000_000_000, "vbsp", "vbsp.setup"))
        self.assertIn((1_500_000_000, "vbsp", "vbsp.Csg"), wave1)
        self.assertIn((2_000_000_000, "vrad", "vrad.setup"), wave1)
        self.assertEqual([i[0] for i in intervals], sorted(i[0] for i in intervals))

    def test_malformed_mark_lines_are_skipped(self):
        self.assertEqual(pbs.read_marks(io.StringIO("junk\n12 x y\n\n")), [])

    def test_samples_are_read_with_their_times_and_stacks(self):
        samples = list(pbs.read_samples(io.StringIO(SCRIPT)))
        self.assertEqual(len(samples), 6)
        ns, stack = samples[2]
        self.assertEqual(ns, 1_200_000_000)
        self.assertEqual(stack[0], OURS)

    def test_frames_are_named_by_type_and_method_with_their_tier(self):
        self.assertEqual(pbs.clean(*OURS), ("Csg::Chop", "SourceSharp.MapTools.Bsp", "Optimized"))
        self.assertEqual(pbs.clean("[unknown]", "/lib/x.so"), ("[x.so]", "x.so", ""))

    def test_leaf_frames_fall_into_the_right_category(self):
        self.assertEqual(pbs.category("WKS::gc_heap::mark", "", GC[1]), "GC")
        self.assertEqual(pbs.category("x", "", JIT[1]), "JIT")
        self.assertEqual(pbs.category("Csg::Chop", "SourceSharp.MapTools", PERF_MAP), "managed:ours")
        self.assertEqual(pbs.category("List::Add", "System.Collections", PERF_MAP), "managed:bcl")


class BucketTests(unittest.TestCase):
    def tally(self, by, waves=frozenset({1}), groups=()):
        intervals = pbs.read_marks(io.StringIO(MARKS))
        return pbs.bucket(pbs.read_samples(io.StringIO(SCRIPT)), intervals, set(waves), by, groups)

    def test_samples_land_in_the_stage_that_was_running(self):
        t = self.tally("top")
        self.assertEqual(t.total["vbsp"], 3)
        self.assertEqual(t.total["vrad"], 1)
        self.assertEqual(t.total["ALL"], 4)

    def test_substages_split_a_stage(self):
        t = self.tally("sub")
        self.assertEqual(t.total["vbsp.setup"], 1)
        self.assertEqual(t.total["vbsp.Csg"], 2)
        self.assertEqual(t.categories["vbsp.Csg"]["GC"], 1)

    def test_the_warm_up_wave_counts_only_when_asked(self):
        self.assertEqual(self.tally("top").categories["ALL"]["JIT"], 0)
        self.assertEqual(self.tally("top", {0, 1}).categories["ALL"]["JIT"], 1)

    def test_a_group_counts_every_sample_whose_stack_matches(self):
        import re
        t = self.tally("top", groups=[("queue", re.compile(r"WorkQueue::Run"))])
        self.assertEqual(t.grouped["vbsp"]["queue"], 2)
        self.assertEqual(t.grouped["ALL"]["queue"], 3)


class MainTests(unittest.TestCase):
    def test_main_prints_each_stage_biggest_first_without_plumbing_frames(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = os.path.join(tmp, "p.script")
            marks = os.path.join(tmp, "p.marks")
            with open(script, "w", encoding="utf-8") as fh:
                fh.write(SCRIPT)
            with open(marks, "w", encoding="utf-8") as fh:
                fh.write(MARKS)
            out = io.StringIO()
            self.assertEqual(pbs.main([script, marks, "--waves", "1", "--grep", "q=WorkQueue"], out), 0)
        text = out.getvalue()
        self.assertLess(text.index("=== vbsp: 3 samples"), text.index("=== vrad: 1 samples"))
        self.assertIn("=== ALL: 4 samples (0.01 cpu-s per wave)", text)
        self.assertIn("Csg::Chop", text)
        self.assertIn("groups (inclusive): q 66.7%", text)
        inclusive = text.split("-- inclusive")[1].split("===")[0]
        self.assertNotIn("WorkQueue::Run", inclusive)


if __name__ == "__main__":
    unittest.main()
