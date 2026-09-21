#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from pathlib import Path
import re
import sys

src = Path('src/Creatures/DesertBatfly')
failures = []
if not src.is_dir():
    raise SystemExit('missing DesertBatfly source tree')

files = sorted(src.rglob('*.cs'))
texts = {p: p.read_text(encoding='utf-8') for p in files}
joined = '\n'.join(texts.values())

def require_literal(value, reason):
    if value not in joined:
        failures.append(reason + ': ' + value)

# Persistent/external compatibility. These values affect saves, world files and external mods.
for literal, reason in (
    ('"DesertBatfly"', 'creature external identity missing'),
    ('DCDesertBatflyV1', 'save identity missing'),
    ('DESERTSWARMROOM', 'world tag identity missing'),
):
    require_literal(literal, reason)

# The species must still have one explicit Rain World integration surface, but private helper
# names, exact owner order and file topology are free to evolve.
hooks_path = src / 'Integration' / 'DB_RainWorldHooks.cs'
if not hooks_path.exists():
    failures.append('missing central Rain World integration adapter')
else:
    hooks = texts[hooks_path]
    for token in ('On.FlyAI.Update', 'On.Fly.ReportToFliesRoomAI'):
        if token not in hooks:
            failures.append('required Rain World integration boundary missing: ' + token)

# Hive lifecycle safety is gameplay-facing and worth retaining independent of implementation.
# Do not reintroduce vanilla FliesRoomAI.Update() as the DesertBatfly passive emergence driver;
# its per-fly random roll scales emergence rate with colony size.
swarm_candidates = [p for p in files if p.name == 'DB_SwarmRoom.cs']
if swarm_candidates:
    swarm = texts[swarm_candidates[0]]
    if re.search(r'\bHive\.Update\s*\(', swarm):
        failures.append('DesertBatfly passive hive lifecycle calls vanilla FliesRoomAI.Update again')

# Successful hive emergence must have an explicit species-owned post-emergence path somewhere.
# We intentionally check the concept rather than an exact method signature.
if 'FlyEmergeFromHive' in joined and 'HiveDeparture' not in joined:
    failures.append('hive emergence exists without a species-owned HiveDeparture lifecycle')

# Sandstorm contraction and passive emergence suppression must remain connected conceptually.
if ('Sandstorm' in joined or 'DeathSandstorm' in joined) and 'ShouldSuppressPassiveHiveEmergence' not in joined:
    failures.append('weather-aware passive hive emergence suppression is missing')

# Rock impacts are a deliberate non-finisher gameplay rule. Protect the behavior, not the old
# exact health-floor value or method layout.
creature_candidates = [p for p in files if p.name == 'DB_Creature.cs']
if creature_candidates:
    creature = texts[creature_candidates[0]]
    if 'resolvingRockViolence' not in creature or 'source?.owner is Rock' not in creature:
        failures.append('rock non-finisher handling disappeared from DB_Creature')

# Optional managed tests may come and go. If present, only reject stale positive reflection
# references that point to types no longer declared in production.
tests = Path('tests/DesertBatfly')
if tests.is_dir():
    decl = set(re.findall(r'\b(?:class|struct|enum|interface)\s+(DB_[A-Za-z0-9_]+)\b', joined))
    positive = re.compile(r'GetType\("DryCycle\.Creatures\.DesertBatfly\.(DB_[A-Za-z0-9_]+)"')
    for path in tests.rglob('*.cs'):
        for name in positive.findall(path.read_text(encoding='utf-8')):
            if name not in decl:
                failures.append(f'stale managed-test reflection target {name}: {path}')

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print('R6 gameplay retention passed: external identities and durable gameplay contracts are intact.')
PY
