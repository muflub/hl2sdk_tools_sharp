#!/usr/bin/env bash
# Times and profiles ssmap across the compile settings that change its speed.
# The work is in tools/compile_perf.py; run with --help for the options.
#
#   tools/compile-perf.sh --map maps/ss_sandbox.vmf --game game/mod_sharp
#
# Needs python3, and for the profilers:
#   dotnet tool install -g dotnet-trace dotnet-counters dotnet-gcdump
# (perf stat and perf record are used when perf is installed.)
set -euo pipefail
exec python3 "$(cd "$(dirname "$0")" && pwd)/compile_perf.py" "$@"
