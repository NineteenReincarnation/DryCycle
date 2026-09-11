#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SRC='src/Creatures/DesertBatfly'
HOOKS="$SRC/Integration/DB_RainWorldHooks.cs"
DEFINITION="$SRC/Core/DB_Definition.cs"
STATE="$SRC/Core/DB_State.cs"
PLUGIN='src/Plugin.cs'

for path in "$SRC" "$HOOKS" "$DEFINITION" "$STATE" "$PLUGIN"; do
    test -e "$path" || { echo "missing required DesertBatfly architecture surface: $path" >&2; exit 1; }
done

python3 - <<'PY'
from pathlib import Path
import re
import sys

src = Path('src/Creatures/DesertBatfly')
hooks_path = src / 'Integration' / 'DB_RainWorldHooks.cs'
plugin_path = Path('src/Plugin.cs')
failures = []

files = sorted(src.rglob('*.cs'))
if not files:
    failures.append('DesertBatfly production tree contains no C# source files')

hooks = hooks_path.read_text(encoding='utf-8')
plugin = plugin_path.read_text(encoding='utf-8')
joined = '\n'.join(path.read_text(encoding='utf-8') for path in files)

# External lifecycle integration remains explicit. Internal implementation is free to move.
if 'DB_RainWorldHooks.Enable()' not in plugin:
    failures.append('Plugin no longer enables DesertBatfly integration')
if 'DB_RainWorldHooks.Disable()' not in plugin:
    failures.append('Plugin no longer disables DesertBatfly integration')

# Hook subscriptions in the central integration adapter must stay symmetric. This catches leaked
# hooks without prescribing which hooks the species is allowed to use.
adds = re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*\+=\s*([A-Za-z0-9_]+)', hooks)
removes = re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*-=\s*([A-Za-z0-9_]+)', hooks)
from collections import Counter
if Counter(adds) != Counter(removes):
    missing_remove = Counter(adds) - Counter(removes)
    missing_add = Counter(removes) - Counter(adds)
    if missing_remove:
        failures.append('hook subscriptions without matching unsubscribe: ' + repr(dict(missing_remove)))
    if missing_add:
        failures.append('hook unsubscriptions without matching subscribe: ' + repr(dict(missing_add)))

# Integration may adapt Rain World callbacks, but it should not become a second room scanner or
# physics controller. Domain code is free to be refactored behind this boundary.
for token, label in (
    ('physicalObjects', 'direct physicalObjects room scan'),
    ('abstractRoom.creatures', 'direct abstract creature scan'),
):
    if token in hooks:
        failures.append('DB_RainWorldHooks regained ' + label)
if re.search(r'\bmainBodyChunk\.vel\s*=', hooks):
    failures.append('DB_RainWorldHooks regained direct velocity ownership')
if re.search(r'\b(?:self|desert|bat)\.AI\.localGoal\s*=', hooks):
    failures.append('DB_RainWorldHooks regained direct localGoal ownership')

# Reflection is allowed only at explicit external-compatibility edges. Core/Behavior/World code
# must not start depending on private reflection contracts.
for path in files:
    text = path.read_text(encoding='utf-8')
    if 'System.Reflection' not in text and 'BindingFlags' not in text and 'FieldInfo' not in text and 'MethodInfo' not in text:
        continue
    rel = path.relative_to(src).as_posix()
    if rel not in {'Integration/DB_Sandbox.cs', 'Integration/DB_WarpCompatibility.cs'}:
        failures.append('reflection escaped compatibility boundary: ' + rel)

# Keep externally serialized/world-authored identities stable. Private class names and directory
# layout are deliberately not checked here.
for literal in ('DesertBatfly', 'DCDesertBatflyV1', 'DESERTSWARMROOM'):
    if literal not in joined:
        failures.append('compatibility identity missing: ' + literal)

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print(f'R6 living architecture guard passed: {len(files)} production files; durable boundaries only.')
PY
