#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "========== Guard: build-compile =========="
bash Guard/build-compile.sh

echo
echo "All Guard categories passed."
