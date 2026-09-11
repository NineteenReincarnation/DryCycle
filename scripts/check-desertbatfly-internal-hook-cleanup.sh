#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from collections import Counter
from pathlib import Path
import re
import sys

src = Path('src/Creatures/DesertBatfly')
hooks_path = src / 'Integration' / 'DB_RainWorldHooks.cs'
failures = []

if not src.is_dir():
    raise SystemExit('missing DesertBatfly source tree')
if not hooks_path.is_file():
    raise SystemExit('missing DesertBatfly Rain World integration adapter')

hooks = hooks_path.read_text(encoding='utf-8')

# The integration adapter may grow new hooks as the species grows, but every subscription must
# have a matching unsubscribe. We intentionally do not freeze the exact hook set or helper names.
adds = Counter(re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*\+=\s*([A-Za-z0-9_]+)', hooks))
removes = Counter(re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*-=\s*([A-Za-z0-9_]+)', hooks))
if adds != removes:
    missing_remove = adds - removes
    missing_add = removes - adds
    if missing_remove:
        failures.append('hook subscription(s) without matching unsubscribe: ' + repr(dict(missing_remove)))
    if missing_add:
        failures.append('hook unsubscribe(s) without matching subscription: ' + repr(dict(missing_add)))

# Integration is an adapter boundary, not a second AI/physics implementation. These checks are
# semantic anti-regressions rather than a whitelist of current classes or call order.
for token, reason in (
    ('physicalObjects', 'direct room physical-object scan'),
    ('abstractRoom.creatures', 'direct abstract-creature scan'),
):
    if token in hooks:
        failures.append('integration adapter regained ' + reason)

if re.search(r'\bmainBodyChunk\.vel\s*=', hooks):
    failures.append('integration adapter regained direct velocity ownership')
if re.search(r'\b(?:self|desert|bat)\.AI\.localGoal\s*=', hooks):
    failures.append('integration adapter regained direct localGoal ownership')

# Reflection against our own DesertBatfly implementation is brittle and turns private refactors
# into compatibility contracts. External compatibility adapters may still use reflection in their
# own files when no stable external API exists.
if 'System.Reflection' in hooks or re.search(r'\b(?:BindingFlags|FieldInfo|MethodInfo|PropertyInfo)\b', hooks):
    failures.append('central Rain World integration adapter reflects DesertBatfly/private state')

# Obsolete audit-marker files should not silently become part of production again. Their exact
# historical helper/file topology is no longer guarded here.
for marker in (
    Path('src/DesertBatfly_InternalHookAudit.md'),
    Path('src/Creatures/DesertBatfly/DesertBatfly_InternalHookAudit.md'),
):
    if marker.exists():
        failures.append('temporary internal-hook audit marker returned: ' + marker.as_posix())

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print(f'DesertBatfly integration-boundary guard passed: {sum(adds.values())} hook subscriptions are symmetric and the adapter remains thin.')
PY
