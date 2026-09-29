#!/usr/bin/env bash
# Times the stock compile tools, Tools++ and ssmap on the same maps.
# See tools/toolchain_bench.py (or --help) for the options.
set -euo pipefail
exec python3 "$(cd "$(dirname "$0")" && pwd)/toolchain_bench.py" "$@"
