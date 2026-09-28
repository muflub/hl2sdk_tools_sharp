#!/usr/bin/env bash
# Records every game file a map's compiles read, under several ssmap flag
# sets, and merges them into one zip that mounts as a game directory.
# The work is in tools/bundle_content.py; run with --help for the options.
#
#   tools/bundle-content.sh                     # 2fort against game/mod_tf
#   tools/bundle-content.sh --map maps/x.vmf --game game/mod_sharp --out x.zip --threads 8
#
# Needs bash, python3 and dotnet (it builds ssmap in Release when needed).
set -euo pipefail
exec python3 "$(cd "$(dirname "$0")" && pwd)/bundle_content.py" "$@"
