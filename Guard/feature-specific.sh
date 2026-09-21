#!/usr/bin/env bash
set -euo pipefail

# Feature-specific Guards
# Consolidated Guard category. Keep checks invariant-focused; implementation-specific checks should be removed or rewritten.


# ============================================================================
# Migrated from Guard/scripts/check-desertbatfly-function-retention.sh
# ============================================================================
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


# ============================================================================
# Migrated from Guard/scripts/check-desertbatfly-r5-retention.sh
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

mapfile -t bridges < <(find src/Creatures/DesertBatfly -type f -name '*Bridge.cs' -print | sort)
test "${#bridges[@]}" -eq 0

mapfile -t detours < <(grep -RIl --include='*.cs' 'MonoMod.RuntimeDetour' src/Creatures/DesertBatfly | sort || true)
test "${#detours[@]}" -eq 0

mapfile -t reflection < <(grep -RIl --include='*.cs' 'System.Reflection' src/Creatures/DesertBatfly | sort || true)
test "${#reflection[@]}" -eq 3
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_RuntimePatch.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_Sandbox.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_WarpCompatibility.cs'

for name in \
  DesertBatflyEnvironmentalDenseFogBridge \
  DesertBatflyEnvironmentalVengeanceBridge \
  DesertBatflyThreatVengeanceBridge \
  DesertBatflyEnvironmentalIntegration \
  DesertBatflyEnvironmentalSocialBridge \
  DesertBatflyEnvironmentalTask09Bridge \
  DesertBatflyEnvironmentalSurvivalBridge \
  DesertBatflySignalIntegration \
  DesertBatflySignalAcuteBridge \
  DesertBatflySignalDirectWitnessBridge \
  DesertBatflySignalThreatBridge \
  DesertBatflySignalVengeanceBridge; do
  ! grep -RIn --include='*.cs' "$name" src/Creatures/DesertBatfly
 done

grep -q 'ApplySecondaryLightRainMoisture(bat, state, tick)' src/Creatures/DesertBatfly/World/Environment/DB_EnvironmentRuntime.cs
grep -q 'ApplyNativeHomeAndBurrow(bat, state.Influence)' src/Creatures/DesertBatfly/World/Environment/DB_EnvironmentRuntime.cs
grep -q 'ObserveLocalShelterFailure(state)' src/Creatures/DesertBatfly/World/Environment/DB_EnvironmentRoomRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldSuppressNewMigration(world, source)' src/Creatures/DesertBatfly/World/Colony/DB_ColonyRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldRecallHomeForSandstorm' src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs
grep -q 'DB_EnvironmentalPolicy.CanConsiderSandstormOutwardRefuge' src/Creatures/DesertBatfly/World/Travel/DB_RefugePolicy.cs
grep -q 'DB_EnvironmentalPolicy.AcceptSandstormEmergencyRefuge' src/Creatures/DesertBatfly/World/Travel/DB_RefugePolicy.cs
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/World/Environment
! grep -RIn --include='*.cs' 'new DB_TravelIntent\|RequestPermanentMigration\|ConvertToReturnHome' src/Creatures/DesertBatfly/World/Environment

# R2 moved receiver interpretation out of SignalRuntime. Preserve the old alarm thresholds and
# receiver-local threat-memory modulation at their new Perception owner, while keeping Signals
# free of receiver behavior and persistent-memory writes.
PERCEPTION='src/Creatures/DesertBatfly/Behavior/Perception/DB_PerceptionRuntime.cs'
SIGNAL='src/Creatures/DesertBatfly/Behavior/Signals/DB_SignalRuntime.cs'
grep -q 'ReportedAnonymousHazardThreshold = 0.34f' "$PERCEPTION"
grep -q 'ReportedThreatDangerThreshold = 0.30f' "$PERCEPTION"
grep -q 'DB_ThreatMemoryStore.For(fly.DesertState, slot)' "$PERCEPTION"
grep -q 'internal bool ReceiveSignal(DB_SignalPacket packet, out bool relayAlarm)' "$PERCEPTION"
! grep -q 'ThreatenedAt' "$SIGNAL"
! grep -q 'DB_SocialRuntime.CancelForPriority' "$SIGNAL"
test "$(grep -c 'DB_SignalRuntime.EmitAcuteAlarm' src/Creatures/DesertBatfly/Behavior/Threat/DB_ThreatRuntime.cs)" -ge 3
grep -q 'DB_SignalRuntime.EmitDistress' src/Creatures/DesertBatfly/Core/Runtime/DB_EventConsumers.cs
# Rally is a Vengeance-domain action: it must remain on the arming path after the Fear/Vengeance split.
grep -q 'DB_SignalRuntime.EmitRally' src/Creatures/DesertBatfly/Behavior/DB_VengeanceRuntime.cs
grep -q 'IsDirectDeathWitness(observer, victim, killer)' src/Creatures/DesertBatfly/Behavior/Social/DB_SocialBond.cs
! grep -RIn --include='*.cs' 'DB_ThreatMemoryStore.AddEvidence' src/Creatures/DesertBatfly/Behavior/Signals
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/Behavior/Signals

mapfile -t runtime_patch_users < <(grep -RIl --include='*.cs' 'DB_RuntimePatch' src/Creatures/DesertBatfly | sort)
test "${#runtime_patch_users[@]}" -eq 3
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_RuntimePatch.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_Sandbox.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/DB_WarpCompatibility.cs'

echo 'R5 retention audit passed: no internal Bridge/RuntimeDetour debt; reflection is limited to Sandbox/Warp integration; Signal receiver semantics are retained by Perception R2.'


# ============================================================================
# Migrated from Guard/scripts/check-desertbatfly-r6-b1.sh
# ============================================================================
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
from collections import Counter

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

# External lifecycle integration remains explicit. Accept either a direct call or a method-group
# passed through the startup/rollback diagnostics wrapper; the semantic requirement is that Plugin
# owns both lifecycle edges, not a specific call syntax.
enable_owned = (
    'DB_RainWorldHooks.Enable()' in plugin or
    re.search(r'StartupDiagnostics\.Step\([^\n]*DB_RainWorldHooks\.Enable\s*\)', plugin)
)
disable_owned = (
    'DB_RainWorldHooks.Disable()' in plugin or
    re.search(r'SafeBootstrapCleanup\([^\n]*DB_RainWorldHooks\.Disable\s*\)', plugin)
)
if not enable_owned:
    failures.append('Plugin no longer enables DesertBatfly integration')
if not disable_owned:
    failures.append('Plugin no longer disables DesertBatfly integration')

# Hook subscriptions in the central integration adapter must stay symmetric. New hook types are
# allowed without updating this script; only leaked subscriptions/orphan removals fail.
adds = re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*\+=\s*([A-Za-z0-9_]+)', hooks)
removes = re.findall(r'\b(On\.[A-Za-z0-9_.]+)\s*-=\s*([A-Za-z0-9_]+)', hooks)
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

# Reflection is allowed only at explicit engine/external compatibility edges. RuntimePatch is an
# engine-patching boundary, while Sandbox/Warp are external integration boundaries. Core,
# Behavior and World code must not start depending on reflection contracts.
allowed_reflection = {
    'Integration/DB_RuntimePatch.cs',
    'Integration/DB_Sandbox.cs',
    'Integration/DB_WarpCompatibility.cs',
}
for path in files:
    text = path.read_text(encoding='utf-8')
    if 'System.Reflection' not in text and 'BindingFlags' not in text and 'FieldInfo' not in text and 'MethodInfo' not in text and 'PropertyInfo' not in text:
        continue
    rel = path.relative_to(src).as_posix()
    if rel not in allowed_reflection:
        failures.append('reflection escaped explicit integration/patch boundary: ' + rel)

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


# ============================================================================
# Migrated from Guard/scripts/check-desertbatfly-r7-performance.sh
# ============================================================================
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
# proliferate across many per-bat domains. Enforce a generous authority budget rather than one
# historical owner/file, leaving room for future specialized caches.
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

# Detect obvious allocation-heavy collection materialization in Update-like sources. Advisory only:
# bounded allocations can be valid, and R7 must not freeze private implementation choices.
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
        warnings.append(f'potential hot-path allocation in {path.relative_to(root)}: ' + ', '.join(hits))

# String interpolation in high-frequency code is another common accidental allocator. Keep it
# advisory because status/debug strings can be legitimate.
for path, text in texts.items():
    if hot_method.search(text) and '$"' in text:
        warnings.append(f'interpolated string exists in Update/Refresh/Tick/Act source: {path.relative_to(root)}')

# Reflection is not itself a performance failure at explicit setup/compatibility boundaries.
# RuntimePatch performs engine patch discovery and Sandbox/Warp are compatibility adapters; these
# are exempt as long as reflection does not spread into ordinary Behavior/Core/World hot paths.
allowed_reflection = {
    'Integration/DB_RuntimePatch.cs',
    'Integration/DB_Sandbox.cs',
    'Integration/DB_WarpCompatibility.cs',
}
for path, text in texts.items():
    if 'System.Reflection' not in text and not re.search(r'\b(?:BindingFlags|FieldInfo|MethodInfo|PropertyInfo)\b', text):
        continue
    rel = path.relative_to(root).as_posix()
    if rel not in allowed_reflection:
        failures.append('reflection in ordinary DesertBatfly gameplay/runtime code: ' + rel)

# A growing species should still centralize at least some room-level work. Capability check only:
# no specific cache class, table type or cadence is prescribed.
room_cache_signals = (
    'ConditionalWeakTable<Room',
    'Dictionary<Room',
    'Dictionary<int, Room',
    'RoomState',
)
if not any(signal in joined for signal in room_cache_signals):
    warnings.append('no obvious room-level cache/state host detected; review repeated per-bat room work')

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
    'no global discovery/blocking calls and broad room scans remain within growth budgets.'
)
PY
