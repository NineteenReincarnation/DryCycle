#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from pathlib import Path
import re
import sys

root = Path('src/Creatures/DesertBatfly')
failures = []
warnings = []

if not root.is_dir():
    raise SystemExit('missing DesertBatfly source tree')

files = sorted(root.rglob('*.cs'))
if not files:
    raise SystemExit('DesertBatfly source tree contains no C# files')
texts = {p: p.read_text(encoding='utf-8') for p in files}
joined = '\n'.join(texts.values())

# R7 is a performance budget, not a frozen implementation snapshot. Private class names,
# exact cache locations, refresh constants, debug field names and task-era architecture are
# deliberately not checked. The guard only blocks broad regressions that are expensive by
# construction and reports softer smells as warnings.

# Engine/global object discovery is not acceptable in creature hot code. If such discovery is
# genuinely needed later, it should live behind a room/world cache outside per-creature updates.
forbidden_global_searches = (
    'FindObjectsOfType',
    'FindObjectOfType',
    'Resources.FindObjectsOfTypeAll',
    'GameObject.Find(',
    'Object.FindObjectsByType',
)
for path, text in texts.items():
    for token in forbidden_global_searches:
        if token in text:
            failures.append(f'global object discovery in DesertBatfly runtime: {path.relative_to(root)} -> {token}')

# Explicit forced collections/sleeps are always invalid in gameplay code.
for path, text in texts.items():
    for token in ('GC.Collect(', 'Thread.Sleep(', 'Task.Delay('):
        if token in text:
            failures.append(f'blocking/forced-runtime operation in gameplay source: {path.relative_to(root)} -> {token}')

# Direct full-room scans are allowed when architecture genuinely requires them, but they must not
# proliferate across many per-bat domains. We enforce a generous authority budget rather than one
# historical owner/file. This leaves room for future specialized caches without letting every AI
# subsystem scan the room independently.
scan_patterns = {
    'physicalObjects': re.compile(r'\b(?:room|self|bat\.room|fly\.room)\.physicalObjects\b'),
    'abstractRoom.creatures': re.compile(r'\b(?:room|self|bat\.room|fly\.room)?\.?abstractRoom\.creatures\b'),
    'room.updateList': re.compile(r'\b(?:room|self|bat\.room|fly\.room)\.updateList\b'),
}
scan_budgets = {
    'physicalObjects': 3,
    'abstractRoom.creatures': 4,
    'room.updateList': 4,
}
for label, pattern in scan_patterns.items():
    owners = []
    for path, text in texts.items():
        if pattern.search(text):
            owners.append(path.relative_to(root).as_posix())
    if len(owners) > scan_budgets[label]:
        failures.append(
            f'{label} direct-scan authority spread to {len(owners)} files '
            f'(budget {scan_budgets[label]}): ' + ', '.join(owners))
    elif len(owners) > 1:
        warnings.append(f'{label} is read directly by {len(owners)} files: ' + ', '.join(owners))

# Detect obvious allocation-heavy collection materialization in files that contain Update-like
# methods. This is advisory because an occasional bounded allocation can be a valid design choice;
# R7 should inform refactors, not forbid them based on a private method name.
hot_method = re.compile(r'\b(?:override\s+)?(?:void|bool|int|float|Vector2|[A-Za-z0-9_<>?, ]+)\s+(?:Update|Refresh|Tick|Act)\s*\(')
allocation_tokens = (
    '.ToList(', '.ToArray(', '.OrderBy(', '.OrderByDescending(',
    'new List<', 'new Dictionary<', 'new HashSet<',
)
for path, text in texts.items():
    if not hot_method.search(text):
        continue
    hits = [token for token in allocation_tokens if token in text]
    if hits:
        warnings.append(
            f'potential hot-path allocation in {path.relative_to(root)}: ' + ', '.join(hits))

# String interpolation in high-frequency code is another common accidental allocator. Do not fail
# the build: debug/status strings can be legitimate, but surface the file for review.
for path, text in texts.items():
    if hot_method.search(text) and '$"' in text:
        warnings.append(f'interpolated string exists in Update/Refresh/Tick/Act source: {path.relative_to(root)}')

# Reflection in core behavior tends to hide expensive or brittle calls. External compatibility
# adapters are exempt; R6 separately protects the integration boundary.
for path, text in texts.items():
    if 'System.Reflection' not in text and not re.search(r'\b(?:BindingFlags|FieldInfo|MethodInfo|PropertyInfo)\b', text):
        continue
    rel = path.relative_to(root).as_posix()
    if rel not in {'Integration/DB_Sandbox.cs', 'Integration/DB_WarpCompatibility.cs'}:
        failures.append('reflection in DesertBatfly gameplay/runtime code: ' + rel)

# A growing species should still centralize at least some room-level work. This is intentionally a
# capability check: it does not prescribe DB_RoomContext, ConditionalWeakTable, a cadence number,
# or any particular cache implementation.
room_cache_signals = (
    'ConditionalWeakTable<Room',
    'Dictionary<Room',
    'Dictionary<int, Room',
    'RoomState',
)
if not any(signal in joined for signal in room_cache_signals):
    warnings.append('no obvious room-level cache/state host detected; review repeated per-bat room work')

# Keep the guard useful in logs even when all hard budgets pass.
if warnings:
    print('R7 advisory warnings:', file=sys.stderr)
    for warning in warnings:
        print('  - ' + warning, file=sys.stderr)

if failures:
    print('R7 hard performance regressions:', file=sys.stderr)
    for failure in failures:
        print('  - ' + failure, file=sys.stderr)
    sys.exit(1)

print(
    f'R7 performance budget passed for {len(files)} production files: '
    'no global object discovery/blocking calls and direct room scans remain within growth budgets.'
)
PY
