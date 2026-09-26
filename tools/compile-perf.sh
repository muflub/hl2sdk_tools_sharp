#!/usr/bin/env bash
# Captures compile performance on a local run: the whole chain first, then
# vbsp, vvis and vrad each on its own, plus a per-stage breakdown of vrad.
#
#   tools/compile-perf.sh --map <path/to/map.vmf> [--game <dir>] [options]
#
# The .vmf is copied into the output folder and compiled there, so nothing
# beside the original map is written. Timing goes through `ssmap bench`, which
# restores each tool's input before every run and discards a warm-up run.
#
# Order, per thread count:
#   1. chain   `ssmap all` from the .vmf
#   2. vbsp    from the .vmf; its .bsp/.prt feed step 3
#   3. vvis    on vbsp's output; its .bsp feeds steps 4 and 5
#   4. vrad    on vvis's output
#   5. vrad --bench once on the same input: vrad's own stages and ray counts
#
# The output folder gets:
#   env.txt                     machine, git revision, .NET runtime, DOTNET_* variables
#   <stage>-t<N>.jsonl          every run's wall, CPU, peak memory, GC pause, sub-stages
#   <stage>-t<N>.log            ssmap bench's per-run lines
#   vrad-stages-t<N>.log        step 5's output (`bench <stage> <seconds>` lines)
#   summary.md / summary.json   medians per tool and thread count, vrad's stages
#   profile.speedscope.json     with --trace: a sampled CPU profile of one vrad run,
#                               viewable at https://www.speedscope.app
#   profile.md                  with --trace: the busiest functions
#
# Options:
#   --runs N            timed runs per cell, after one warm-up (default 3)
#   --threads LIST      comma-separated thread counts; "max" is nproc (default max)
#   --stages LIST       any of chain,vbsp,vvis,vrad (default all four); vvis needs
#                       vbsp and vrad needs vvis, run earlier in the same call
#   --vbsp-args "..."   extra stock options for vbsp (also given to the chain)
#   --vvis-args "..."   extra stock options for vvis (also given to the chain)
#   --vrad-args "..."   extra stock options for vrad (also given to the chain)
#   --trace             profile one vrad run with dotnet-trace
#                       (install: dotnet tool install -g dotnet-trace)
#   --out DIR           results folder (default perf-results/<timestamp>)
#   --dotnet PATH       dotnet host; default ~/.dotnet/dotnet when present, since
#                       Ubuntu's packaged runtime is known to be unreliable (see
#                       src/SourceSharp.MapCompile/SourceSharp.MapCompile.csproj)
#   --no-build          use the existing Release build
#
# Runtime switches pass through the environment and are recorded in env.txt:
#   DOTNET_TieredCompilation=0 tools/compile-perf.sh --map maps/ss_sandbox.vmf --game game/mod_sharp
set -euo pipefail

usage() { sed -n '2,48p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

repo="$(cd "$(dirname "$0")/.." && pwd)"
map="" game="" runs=3 threads="max" stages="chain,vbsp,vvis,vrad" trace=0 out="" build=1 dotnet=""
vbsp_args="" vvis_args="" vrad_args=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --map) map="$2"; shift 2 ;;
    --game) game="$2"; shift 2 ;;
    --runs) runs="$2"; shift 2 ;;
    --threads) threads="$2"; shift 2 ;;
    --stages) stages="$2"; shift 2 ;;
    --vbsp-args) vbsp_args="$2"; shift 2 ;;
    --vvis-args) vvis_args="$2"; shift 2 ;;
    --vrad-args) vrad_args="$2"; shift 2 ;;
    --trace) trace=1; shift ;;
    --out) out="$2"; shift 2 ;;
    --dotnet) dotnet="$2"; shift 2 ;;
    --no-build) build=0; shift ;;
    -h|--help) usage ;;
    *) echo "compile-perf: unknown option $1" >&2; usage ;;
  esac
done

[[ -n "$map" && -f "$map" && "$map" == *.vmf ]] || { echo "compile-perf: --map must name an existing .vmf" >&2; usage; }
map="$(cd "$(dirname "$map")" && pwd)/$(basename "$map")"
[[ -z "$game" ]] || game="$(cd "$game" && pwd)"
if [[ -z "$dotnet" ]]; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then dotnet="$HOME/.dotnet/dotnet"; else dotnet="$(command -v dotnet)"; fi
fi

out="${out:-$repo/perf-results/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
dll="$repo/src/SourceSharp.MapCompile/bin/net10.0/ssmap.dll"
ssmap() { "$dotnet" "$dll" "$@"; }

if [[ $build -eq 1 ]]; then
  echo "building Release..."
  "$dotnet" build "$repo/src/SourceSharp.MapCompile/SourceSharp.MapCompile.csproj" -c Release >"$out/build.log" 2>&1 \
    || { echo "compile-perf: build failed, see $out/build.log" >&2; exit 1; }
fi
[[ -f "$dll" ]] || { echo "compile-perf: no build at $dll" >&2; exit 1; }

# The machine and the build, so two result folders can be compared honestly.
{
  echo "date: $(date -Is)"
  echo "git: $(git -C "$repo" rev-parse HEAD) $(git -C "$repo" status --porcelain | grep -q . && echo dirty || echo clean)"
  echo "branch: $(git -C "$repo" rev-parse --abbrev-ref HEAD)"
  echo "map: $map ($(stat -c %s "$map") bytes, sha256 $(sha256sum "$map" | cut -d' ' -f1))"
  echo "game: ${game:-none}"
  echo "runs: $runs, threads: $threads, stages: $stages"
  echo "vbsp args: $vbsp_args"
  echo "vvis args: $vvis_args"
  echo "vrad args: $vrad_args"
  echo "nproc: $(nproc)"
  echo "uname: $(uname -a)"
  if command -v lscpu >/dev/null; then lscpu | grep -E 'Model name|^CPU\(s\)|Thread|Core|Socket|MHz|L2|L3' || true; fi
  grep -E 'MemTotal' /proc/meminfo || true
  echo "dotnet host: $dotnet"
  "$dotnet" --list-runtimes 2>/dev/null | grep NETCore || true
  echo "DOTNET_ environment:"; env | grep -E '^DOTNET_|^COMPlus_' | sort || true
} >"$out/env.txt"

# The map compiles in its own folder, beside the original's .rad if it has one.
work="$out/work"
rm -rf "$work"; mkdir -p "$work"
cp "$map" "$work/"
[[ -f "${map%.vmf}.rad" ]] && cp "${map%.vmf}.rad" "$work/"
wmap="$work/$(basename "$map")"
wbsp="${wmap%.vmf}.bsp"

read -r -a vbsp_a <<<"$vbsp_args"
read -r -a vvis_a <<<"$vvis_args"
read -r -a vrad_a <<<"$vrad_args"
game_a=(); [[ -n "$game" ]] && game_a=(--game "$game")
game_s=(); [[ -n "$game" ]] && game_s=(-game "$game")

has_stage() { [[ ",$stages," == *",$1,"* ]]; }

bench() {
  local stage="$1" t="$2"; shift 2
  # vvis and vrad take the map's name without its extension (they add .bsp,
  # even to "x.vmf"); vbsp and the chain take the .vmf.
  local m="$wmap"
  [[ "$stage" == vvis || "$stage" == vrad ]] && m="${wmap%.vmf}"
  echo "  $stage, $t threads..."
  ssmap bench --map "$m" "${game_a[@]}" --stages "$stage" --threads "$t" --runs "$runs" \
    --workdir "$work" --out "$out/$stage-t$t.jsonl" -- "$@" >"$out/$stage-t$t.log" 2>&1 \
    || { echo "compile-perf: $stage failed, see $out/$stage-t$t.log" >&2; exit 1; }
  if grep -q FAILED "$out/$stage-t$t.log"; then
    echo "compile-perf: $stage failed, see $out/$stage-t$t.log" >&2; exit 1
  fi
}

IFS=',' read -r -a thread_list <<<"$threads"
last_t=""
for t in "${thread_list[@]}"; do
  [[ "$t" == "max" ]] && t="$(nproc)"
  last_t="$t"
  echo "threads $t:"
  if has_stage chain; then
    bench chain "$t" --vbsp "${vbsp_a[@]}" --vvis "${vvis_a[@]}" --vrad "${vrad_a[@]}"
  fi
  if has_stage vbsp; then bench vbsp "$t" "${vbsp_a[@]}"; fi
  if has_stage vvis; then bench vvis "$t" "${vvis_a[@]}"; fi
  if has_stage vrad; then
    # vvis's output, kept before vrad lights it, for step 5 and the profile.
    cp "$wbsp" "$work/vis.bsp.keep"
    bench vrad "$t" "${vrad_a[@]}"
    echo "  vrad stages, $t threads..."
    cp "$work/vis.bsp.keep" "$wbsp"
    ssmap vrad --bench -threads "$t" "${game_s[@]}" "${vrad_a[@]}" "$wbsp" >"$out/vrad-stages-t$t.log" 2>&1 \
      || { echo "compile-perf: vrad --bench failed, see $out/vrad-stages-t$t.log" >&2; exit 1; }
    cp "$work/vis.bsp.keep" "$wbsp"
  fi
done

if [[ $trace -eq 1 ]]; then
  tracer="$(command -v dotnet-trace || true)"
  [[ -z "$tracer" && -x "$HOME/.dotnet/tools/dotnet-trace" ]] && tracer="$HOME/.dotnet/tools/dotnet-trace"
  if [[ -z "$tracer" ]]; then
    echo "compile-perf: --trace needs dotnet-trace: dotnet tool install -g dotnet-trace" >&2
  elif [[ ! -f "$work/vis.bsp.keep" ]]; then
    echo "compile-perf: --trace profiles vrad, so it needs the vrad stage" >&2
  else
    echo "profiling vrad, $last_t threads..."
    cp "$work/vis.bsp.keep" "$wbsp"
    "$tracer" collect --profile dotnet-sampled-thread-time --format speedscope -o "$out/profile.nettrace" \
      -- "$dotnet" "$dll" vrad --bench -threads "$last_t" "${game_s[@]}" "${vrad_a[@]}" "$wbsp" \
      >"$out/profile.log" 2>&1 || echo "compile-perf: profiling failed, see $out/profile.log" >&2
  fi
fi

rm -rf "$work"
python3 "$repo/tools/compile_perf_summary.py" "$out"
echo "results in $out/summary.md"
