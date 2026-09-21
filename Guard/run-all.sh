#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "========== Guard: build-safety =========="
bash Guard/build-safety.sh

echo
echo "All Guard categories passed."
