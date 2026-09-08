#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
from pathlib import Path
import re
import sys

root = Path('src/Creatures/DesertBatfly')
failures = []


def read(rel):
    path = root / rel
    if not path.exists():
        failures.append(f'missing performance contract source: {rel}')
        return ''
    return path.read_text(encoding='utf-8')

room_context = read('Core/Runtime/DB_RoomContext.cs')
fear = read('Behavior/DB_FearRuntime.cs')
weapon = read('Behavior/Perception/DB_WeaponPerception.cs')
arbiter = read('Core/Runtime/DB_BehaviorArbiter.cs')
hooks = read('Integration/DB_RainWorldHooks.cs')
social_room = read('Behavior/Social/DB_SocialRoomState.cs')
environment_room = read('World/Environment/DB_EnvironmentRoomRuntime.cs')

# Shared room observation remains bounded and is the sole ordinary physicalObjects scanner.
match = re.search(r'RefreshIntervalTicks\s*=\s*(\d+)', room_context)
if not match or not (4 <= int(match.group(1)) <= 20):
    failures.append('DB_RoomContext refresh cadence must remain bounded to 4..20 ticks')

physical_scan_owners = []
for path in sorted(root.rglob('*.cs')):
    rel = path.relative_to(root).as_posix()
    for line in path.read_text(encoding='utf-8').splitlines():
        code = line.split('//', 1)[0]
        if re.search(r'\b(?:room|Room|self)\.physicalObjects\b', code):
            physical_scan_owners.append(rel)
            break
if physical_scan_owners != ['Core/Runtime/DB_RoomContext.cs']:
    failures.append('physicalObjects scan authority changed: ' + ', '.join(physical_scan_owners))

if 'context.ThrownWeapons' not in weapon or 'context.Weapons' not in weapon:
    failures.append('DB_WeaponPerception no longer consumes shared DB_RoomContext weapon views')

# Realized Fear/PTSD target discovery must reuse the room snapshot instead of doing a per-bat room scan.
if 'DB_RoomContext.For(bat?.room)' not in fear or 'roomContext.Creatures' not in fear:
    failures.append('Fear trauma target scan no longer uses shared DB_RoomContext creatures')
if 'bat.room.abstractRoom.creatures' in fear:
    failures.append('Fear reintroduced a per-bat abstractRoom.creatures scan')

# Behavior/Core hot paths may not grow another direct realized-room creature scanner.
for directory in (root / 'Behavior', root / 'Core'):
    for path in sorted(directory.rglob('*.cs')):
        if path.name == 'DB_RoomContext.cs':
            continue
        text = path.read_text(encoding='utf-8')
        if re.search(r'\b(?:bat|room|self)\.room\.abstractRoom\.creatures\b|\broom\.abstractRoom\.creatures\b', text):
            failures.append(f'duplicate realized creature scan outside DB_RoomContext: {path.relative_to(root)}')

# Arbiter may retain detailed reject reasons, but building them cannot allocate interpolated strings every bat/frame.
if '$"rejected by higher-priority {winner.Owner}"' in arbiter:
    failures.append('Arbiter reintroduced per-frame interpolated rejection reason allocation')
if 'HigherPriorityRejectionReason(winner.Owner)' not in arbiter:
    failures.append('Arbiter zero-allocation rejection reason authority missing')

# Ordinary rooms stay lazy: room Update may not construct Signal/Environment DB runtimes before an existing active context is proven.
lazy = hooks.find('DB_RoomContext.TryGetExisting(self, out DB_RoomContext context)')
bats_gate = hooks.find('context.Bats.Count == 0', lazy)
signal = hooks.find('DB_SignalRoomRuntime.For(self)', lazy)
environment = hooks.find('DB_EnvironmentRoomRuntime.Update(self)', lazy)
if min(lazy, bats_gate, signal, environment) < 0 or not (lazy < bats_gate < signal < environment):
    failures.append('Room.Update lazy DB activation ordering changed')

# Room-scoped social/environment work remains cadence-bounded rather than per-bat/per-frame full refresh.
social_match = re.search(r'RefreshInterval\s*=\s*(\d+)', social_room)
if not social_match or not (10 <= int(social_match.group(1)) <= 40):
    failures.append('Social room refresh cadence must remain bounded to 10..40 ticks')
for name in ('WeatherSampleInterval', 'CrowdingSampleInterval'):
    m = re.search(rf'{name}\s*=\s*(\d+)', environment_room)
    if not m or not (10 <= int(m.group(1)) <= 40):
        failures.append(f'{name} must remain bounded to 10..40 ticks')

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print('R7 static performance retention passed: shared scans, lazy room activation, bounded cadences, and arbiter hot-path allocation guard.')
PY
