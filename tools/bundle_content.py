"""Records the game content a map's compiles read, as one zip.

    tools/bundle-content.sh [--map maps/sdk_ctf_2fort.vmf] [--game game/mod_tf]
                            [--out content-bundles/<map>-content.zip] [--threads N]

Runs `ssmap all --record-content` once per flag combination in COMBOS, so that
the union covers every file any of those compiles opens (vbsp's materials and
props, vrad's lights.rad, textures and static-prop models in LDR and HDR), then
merges the per-run zips into one: files deduplicated by path, manifests merged.
The merged zip is a game directory with its own gameinfo.txt, so the compile
can be replayed with `-game <unzipped dir>` on a machine without Steam.

The compiles run with --no-write, so nothing is written beside the map; the
per-run zips go to a scratch directory that is removed afterwards.
"""

import argparse
import datetime
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.path.join(REPO, "bin", "Release", "ssmap.dll")

GAMEINFO = "gameinfo.txt"
MANIFEST = "content-manifest.txt"

# The flag combinations. Each is (name, arguments after the map). The first is
# the plain compile; the rest cover vrad's static-prop lighting, which reads
# the props' .mdl/.vvd/.vtx (and with -textureshadows their materials and
# textures), in LDR, HDR and both. vvis reads no game content, so those runs
# take vvis -fast to save time without narrowing what is recorded.
COMBOS = [
    ("default", []),
    ("ldr-props", ["--vvis", "-fast", "--vrad", "-ldr", "-StaticPropLighting", "-StaticPropPolys",
                   "-textureshadows"]),
    ("hdr-props", ["--vvis", "-fast", "--vrad", "-hdr", "-StaticPropLighting", "-StaticPropPolys"]),
    ("both-final-props", ["--vvis", "-fast", "--vrad", "-both", "-final", "-StaticPropLighting",
                          "-StaticPropPolys", "-textureshadows"]),
]

# Manifest kinds, weakest first: a path read in any run is read in the merge.
RANK = {"missing": 0, "resolved": 1, "loose": 2, "read": 3}

# Fixed entry time, so the same set always makes the same zip bytes.
ZIP_TIME = (1980, 1, 1, 0, 0, 0)


class MergeError(Exception):
    """Two runs disagree about a file's bytes: the content changed between them."""


def needs_build(dll, source_root):
    """True when the ssmap build is missing or older than any source file."""
    if not os.path.exists(dll):
        return True
    built = os.path.getmtime(dll)
    for root, dirs, files in os.walk(source_root):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in files:
            if name.endswith((".cs", ".csproj", ".props", ".slnx")) and \
                    os.path.getmtime(os.path.join(root, name)) > built:
                return True
    return False


def run_command(dotnet, dll, map_path, game, zip_path, threads, combo_args):
    """The ssmap all command line for one combination."""
    cmd = [dotnet, dll, "all", map_path, "-game", game]
    if threads:
        cmd += ["-threads", str(threads)]
    cmd += ["--no-write", "--record-content", zip_path]
    return cmd + list(combo_args)


def parse_manifest(text):
    """(commands, {path: (kind, hash, bytes, source)}) from a content-manifest.txt."""
    commands = []
    entries = {}
    for line in text.splitlines():
        if line.startswith("# command: "):
            commands.append(line[len("# command: "):])
            continue
        if not line or line.startswith("#"):
            continue
        kind, path, digest, size, source = line.split("\t", 4)
        entries[path] = (kind, digest, size, source)
    return commands, entries


def merge_entries(into, entries):
    """Merges one manifest's entries into another's, keeping each path's strongest kind."""
    for path, entry in entries.items():
        old = into.get(path)
        if old is None or RANK[entry[0]] > RANK[old[0]]:
            into[path] = entry
        elif old[1] != "-" and entry[1] != "-" and old[1] != entry[1]:
            raise MergeError(f"{path}: two runs read different bytes ({old[1][:16]} and {entry[1][:16]})")


def format_manifest(commands, entries, runs):
    lines = [f"# ssmap content bundle, merged from {runs} runs"]
    lines += [f"# command: {c}" for c in commands]
    lines.append("# kind\tpath\tsha256\tbytes\tsource")
    for path in sorted(entries):
        kind, digest, size, source = entries[path]
        lines.append(f"{kind}\t{path}\t{digest}\t{size}\t{source}")
    return "\n".join(lines) + "\n"


def merge(zip_paths, out_path):
    """Merges per-run bundles into one; returns (files, bytes, misses)."""
    files = {}
    commands = []
    entries = {}
    gameinfo = None
    for zip_path in zip_paths:
        with zipfile.ZipFile(zip_path) as z:
            for info in z.infolist():
                data = z.read(info)
                if info.filename == MANIFEST:
                    run_commands, run_entries = parse_manifest(data.decode("utf-8"))
                    commands += run_commands
                    merge_entries(entries, run_entries)
                elif info.filename == GAMEINFO:
                    if gameinfo is not None and gameinfo != data:
                        raise MergeError(f"{zip_path}: its gameinfo.txt differs from the first run's")
                    gameinfo = data
                elif info.filename in files and files[info.filename] != data:
                    raise MergeError(f"{info.filename}: two runs bundled different bytes")
                else:
                    files[info.filename] = data

    if gameinfo is None:
        raise MergeError("no run wrote a gameinfo.txt")

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    temporary = out_path + ".tmp"
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(files):
            z.writestr(zipfile.ZipInfo(name, ZIP_TIME), files[name], zipfile.ZIP_DEFLATED)
        z.writestr(zipfile.ZipInfo(GAMEINFO, ZIP_TIME), gameinfo, zipfile.ZIP_DEFLATED)
        z.writestr(zipfile.ZipInfo(MANIFEST, ZIP_TIME),
                   format_manifest(commands, entries, len(zip_paths)).encode("utf-8"), zipfile.ZIP_DEFLATED)
    os.replace(temporary, out_path)

    misses = sum(1 for e in entries.values() if e[0] == "missing")
    return len(files), sum(len(d) for d in files.values()), misses


def human(size):
    for unit in ("bytes", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size} {unit}" if unit == "bytes" else f"{size:.1f} {unit}"
        size /= 1024.0
    return f"{size:.1f} GB"


def parse_args(argv):
    p = argparse.ArgumentParser(
        prog="tools/bundle-content.sh",
        description="Compile a map with ssmap under several flag sets and bundle every game file they read.")
    p.add_argument("--map", default="maps/sdk_ctf_2fort.vmf", help="the map (default: %(default)s)")
    p.add_argument("--game", default="game/mod_tf", help="the game directory (default: %(default)s)")
    p.add_argument("--out", help="the merged zip (default: content-bundles/<map>-content.zip)")
    p.add_argument("--threads", type=int, help="ssmap -threads (default: every processor)")
    p.add_argument("--dotnet", default="dotnet", help=argparse.SUPPRESS)
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)
    map_path = os.path.abspath(args.map)
    game = os.path.abspath(args.game)
    base = os.path.splitext(os.path.basename(map_path))[0]
    out = os.path.abspath(args.out or os.path.join(REPO, "content-bundles", f"{base}-content.zip"))

    if not os.path.exists(map_path):
        print(f"no such map: {map_path}", file=sys.stderr)
        return 2
    if not os.path.exists(os.path.join(game, GAMEINFO)):
        print(f"no gameinfo.txt in {game}", file=sys.stderr)
        return 2

    if needs_build(DLL, os.path.join(REPO, "src")):
        print("building ssmap (Release)...", flush=True)
        subprocess.run([args.dotnet, "build", os.path.join(REPO, "src", "SourceSharp.MapTools.slnx"),
                        "-c", "Release", "-nologo", "-v", "q"], check=True)

    work = tempfile.mkdtemp(prefix="ssmap-bundle-")
    try:
        zips = []
        failed = []
        for name, combo in COMBOS:
            zip_path = os.path.join(work, f"{name}.zip")
            cmd = run_command(args.dotnet, DLL, map_path, game, zip_path, args.threads, combo)
            started = datetime.datetime.now()
            print(f"[{name}] {' '.join(cmd[2:])}", flush=True)
            with open(os.path.join(work, f"{name}.log"), "w", encoding="utf-8") as log:
                code = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, cwd=work).returncode
            with open(os.path.join(work, f"{name}.log"), encoding="utf-8", errors="replace") as log:
                summary = [line.strip() for line in log if line.startswith("record-content:")]
            took = (datetime.datetime.now() - started).total_seconds()
            print(f"[{name}] exit {code} in {took:.0f} s; {summary[-1] if summary else 'no bundle written'}",
                  flush=True)
            if code != 0:
                failed.append(name)
                with open(os.path.join(work, f"{name}.log"), encoding="utf-8", errors="replace") as log:
                    tail = log.readlines()[-15:]
                print("".join("    " + t for t in tail), end="")
            if os.path.exists(zip_path):
                zips.append(zip_path)

        if not zips:
            print("no run wrote a bundle; nothing to merge", file=sys.stderr)
            return 1

        files, size, misses = merge(zips, out)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print()
    print(f"bundle: {out}")
    print(f"        {human(os.path.getsize(out))} zipped; {files} files ({human(size)}), {misses} misses, "
          f"from {len(zips)} of {len(COMBOS)} runs")
    if failed:
        print(f"warning: these runs failed and their bundles hold only what was read before the failure: "
              f"{', '.join(failed)}")
    print("Upload this zip so the compile can be reproduced without Steam content.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
