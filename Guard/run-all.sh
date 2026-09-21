#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
categories=(
  "build-compile"
  "devtool-architecture"
  "feature-specific"
)
for category in "${categories[@]}"; do
  echo
  echo "========== Guard: $category =========="
  bash "Guard/${category}.sh"
done
echo
echo "All Guard categories passed."
