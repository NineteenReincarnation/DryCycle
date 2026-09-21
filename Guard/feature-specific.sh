#!/usr/bin/env bash
set -euo pipefail

# Feature-specific Guards
#
# Keep only durable, externally visible compatibility identities here.
# Gameplay behavior, hook topology, private ownership, reflection placement and performance
# characteristics must be verified by compilation/tests/profiling rather than source-text snapshots.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from pathlib import Path
import sys

root = Path("src/Creatures/DesertBatfly")
if not root.is_dir():
    raise SystemExit("missing DesertBatfly source tree")

files = sorted(root.rglob("*.cs"))
if not files:
    raise SystemExit("DesertBatfly source tree contains no C# files")

joined = "\n".join(path.read_text(encoding="utf-8", errors="ignore") for path in files)

# These identifiers are authored outside the private implementation and therefore must not change
# accidentally: creature identity, serialized/save identity and world authoring tag.
required_external_identities = {
    '"DesertBatfly"': "creature external identity",
    "DCDesertBatflyV1": "serialized/save identity",
    "DESERTSWARMROOM": "world authoring tag",
}

missing = [
    f"{label} missing: {literal}"
    for literal, label in required_external_identities.items()
    if literal not in joined
]

if missing:
    print("\n".join(missing), file=sys.stderr)
    sys.exit(1)

print("Feature-specific guard passed: DesertBatfly external compatibility identities are intact.")
PY
