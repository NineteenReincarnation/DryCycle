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
frame_context = read('Core/Runtime/DB_FrameContext.cs')
performance_probe = read('Core/Runtime/DB_PerformanceProbe.cs')
fear = read('Behavior/DB_FearRuntime.cs')
threat = read('Behavior/Threat/DB_ThreatRuntime.cs')
perception = read('Behavior/Perception/DB_CreaturePerception.cs')
weapon = read('Behavior/Perception/DB_WeaponPerception.cs')
visibility = read('Behavior/Perception/DB_VisibilityPolicy.cs')
arbiter = read('Core/Runtime/DB_BehaviorArbiter.cs')
hooks = read('Integration/DB_RainWorldHooks.cs')
social_room = read('Behavior/Social/DB_SocialRoomState.cs')
environment_room = read('World/Environment/DB_EnvironmentRoomRuntime.cs')
feeding = read('Behavior/Feeding/DB_FeedingCoordinator.cs')
feeding_runtime = read('Behavior/Feeding/DB_DehydrationFeedingRuntime.cs')
feeding_grip = read('Behavior/Feeding/DB_DehydrationGripRuntime.cs')
tuning = read('Core/DB_Tuning.cs')
debug_environment_path = Path('src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs')
if not debug_environment_path.exists():
    failures.append('missing DB_EnvironmentDebugSource observability source')
    debug_environment = ''
else:
    debug_environment = debug_environment_path.read_text(encoding='utf-8')

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

# Visibility is called from several O(bats*candidates) paths. Reject impossible distance
# pairs before asking Rain World to traverse terrain for VisualContact; this is semantics-
# preserving because an out-of-range pair is false regardless of line of sight.
distance_gate = visibility.find('if ((targetPosition - origin).sqrMagnitude > range * range)')
los_gate = visibility.find('return observer.room.VisualContact(origin, targetPosition);')
if min(distance_gate, los_gate) < 0 or distance_gate > los_gate:
    failures.append('visibility must reject effective range before Room.VisualContact terrain LOS')
if 'if (!observer.room.VisualContact(origin, targetPosition))' in visibility:
    failures.append('visibility reintroduced terrain LOS before exact distance rejection')

# Base creature perception is also an O(bats*creatures) scan. A freshly realized swarm
# must not lock-step every eighth tick, and exact distance sqrt belongs after visibility.
for token in (
    'internal const int ScanIntervalTicks = 8;',
    'ResetScanPhase();',
    'ScanPhase(int visualSeed)',
    'return 1 + (int)(x % (uint)ScanIntervalTicks);',
):
    if token not in perception:
        failures.append('creature perception stagger contract missing: ' + token)
perception_visibility = perception.find('if (!DB_VisibilityPolicy.CanObserve(')
perception_distance = perception.find('float distance = Vector2.Distance(', perception_visibility)
if min(perception_visibility, perception_distance) < 0 or perception_visibility > perception_distance:
    failures.append('creature perception must validate visibility before exact Vector2.Distance sqrt')

# FrameContext is captured for every realized bat every AI frame. It may copy already-owned
# domain facts, but it must not recreate the old per-bat player/predator room scan merely for
# unused snapshot fields. Immediate projectile recognition remains a real arbiter input.
for retired in (
    'VisiblePlayerCount', 'NearestVisiblePlayer',
    'PredatorCandidateCount', 'NearestPredator',
):
    if retired in frame_context:
        failures.append('FrameContext retained unused per-frame visibility fact: ' + retired)
for token in ('roomContext.Players', 'roomContext.Creatures', 'DB_RoomContext.For(room)'):
    if token in frame_context:
        failures.append('FrameContext reintroduced per-bat room visibility scanning: ' + token)
if 'DB_WeaponPerception.TryFindIncomingProjectile(' not in frame_context:
    failures.append('FrameContext lost the real incoming-projectile fact consumed by arbitration')

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
feeding_update = hooks.find('DB_FeedingCoordinator.UpdateRoom(self)', lazy)
if min(lazy, bats_gate, signal, environment, feeding_update) < 0 or not (
        lazy < bats_gate < signal < environment < feeding_update):
    failures.append('Room.Update lazy DB activation ordering changed')

# Dehydration feeding remains room-aggregated and consumes the shared room player snapshot.
if 'DB_RoomContext.For(room)' not in feeding or 'context.Players' not in feeding:
    failures.append('Feeding target discovery no longer uses shared DB_RoomContext players')
if feeding.count('PlayerDehydrationFacts.ApplyPredationStress') != 1:
    failures.append('Feeding aggregate drain authority changed')
for label, domain_text in (
        ('coordinator', feeding), ('runtime', feeding_runtime), ('grip', feeding_grip)):
    if 'physicalObjects' in domain_text or 'abstractRoom.creatures' in domain_text:
        failures.append(f'Feeding {label} reintroduced direct realized-room scanning')
    if 'HydrationWeakness' in domain_text or 'ThirstStore.' in domain_text:
        failures.append(f'Feeding {label} bypasses PlayerDehydrationFacts physiology boundary')
if 'DB_RoomContext' in feeding_runtime or 'DB_RoomContext' in feeding_grip:
    failures.append('per-bat Feeding/Grip runtime reintroduced room aggregation')
feeding_refresh = re.search(r'FeedingCoordinatorRefreshTicks\s*=\s*(\d+)', tuning)
if not feeding_refresh or not (8 <= int(feeding_refresh.group(1)) <= 30):
    failures.append('Feeding coordinator refresh cadence must remain bounded to 8..30 ticks')
for token in (
    'TryPeekTarget(Player target, out DB_FeedingTargetDebugState debug)',
    'rooms.TryGetValue(room, out RoomState state)',
    'PromoteCloudReservations(state);',
):
    if token not in feeding:
        failures.append('Feeding aggregation/observability contract missing: ' + token)

# Room-scoped social/environment work remains cadence-bounded rather than per-bat/per-frame full refresh.
social_match = re.search(r'RefreshInterval\s*=\s*(\d+)', social_room)
if not social_match or not (10 <= int(social_match.group(1)) <= 40):
    failures.append('Social room refresh cadence must remain bounded to 10..40 ticks')
for name in ('WeatherSampleInterval', 'CrowdingSampleInterval'):
    m = re.search(rf'{name}\s*=\s*(\d+)', environment_room)
    if not m or not (10 <= int(m.group(1)) <= 40):
        failures.append(f'{name} must remain bounded to 10..40 ticks')

# Expensive per-bat perception keeps immediate first recognition but disperses steady-state work.
for token in (
    'internal bool CuePhasePending = true;',
    'state.CueRefresh = phase;',
    'CueRefreshPhase(int visualSeed)',
    'return 1 + (int)(x % (uint)CueRefreshTicks);',
):
    if token not in threat:
        failures.append('Threat cue stagger contract missing: ' + token)
if 'RuntimeState state = StateFor(bat);' in threat[threat.find('internal static bool TryGetDebugState'):threat.find('private static void DamageEvent')]:
    failures.append('Threat debug read creates runtime state')
for token in (
    'internal bool TraumaScanPhasePending = true;',
    'state.TraumaThreatScan = phase;',
    'TraumaThreatScanPhase(int visualSeed)',
    'return 1 + (int)(x % (uint)TraumaThreatScanTicks);',
    'state.TraumaScanPhasePending = true;',
):
    if token not in fear:
        failures.append('Fear trauma stagger contract missing: ' + token)
if '$"R3 PrimaryOwner={ownership.PrimaryOwner}"' in hooks:
    failures.append('per-frame primary-owner cancellation reintroduced interpolated allocation')
if 'PrimaryOwnerBlockReason(ownership.PrimaryOwner)' not in hooks:
    failures.append('constant primary-owner cancellation reason authority missing')

# Live R7 spike counters quantify the staggered work without making Observatory a producer.
for token in (
    'internal const int WarmupTicks = 40;',
    'RecordThreatCue(Room room)',
    'RecordTraumaThreatScan(Room room)',
    'TryPeek(Room room, out DB_PerformanceSnapshot snapshot)',
    'rooms.TryGetValue(room, out RoomState state)',
    'new DB_PerformanceSnapshot(0, 0, 0, -1, 0, 0, 0, -1)',
):
    if token not in performance_probe:
        failures.append('R7 performance probe contract missing: ' + token)
if 'DB_PerformanceProbe.RecordThreatCue(bat.room);' not in threat:
    failures.append('Threat expensive refresh no longer records R7 spike telemetry')
if 'DB_PerformanceProbe.RecordTraumaThreatScan(bat.room);' not in fear:
    failures.append('Fear trauma scan no longer records R7 spike telemetry')
if hooks.count('DB_PerformanceProbe.Reset();') != 2:
    failures.append('R7 performance probe must reset on both enable and disable')
for token in (
    'DB_PerformanceProbe.TryPeek',
    'Performance.SpikeProbeActive',
    'Performance.ThreatCueThisTick',
    'Performance.ThreatCuePeakAfterWarmup',
    'Performance.ThreatCueTotal',
    'Performance.TraumaScanThisTick',
    'Performance.TraumaScanPeakAfterWarmup',
    'Performance.TraumaScanTotal',
):
    if token not in debug_environment:
        failures.append('R7 live spike Observatory field missing: ' + token)

# Observatory/profile reads must be side-effect free: peeking may not refresh caches or build anchors.
for token in (
    'TryPeekExisting(Room room, out DB_RoomContext context)',
    'internal int SnapshotAge',
    'internal int WeaponRefreshAge',
):
    if token not in room_context:
        failures.append('room cache observability contract missing: ' + token)
for token in (
    'TryPeekExisting(Room room, out RoomState state)',
    'internal int RefreshAge',
    'internal int CachedCandidateCount',
):
    if token not in social_room:
        failures.append('social cache observability contract missing: ' + token)
for token in (
    'TryPeekExisting(Room room, out RoomState state)',
    'internal int WeatherSampleAge',
    'internal int CrowdingSampleAge',
    'internal int AnchorBuildCount',
    'state.AnchorBuildCount++;',
):
    if token not in environment_room:
        failures.append('environment cache observability contract missing: ' + token)
if 'DB_EnvironmentRoomRuntime.For(bat.room)' in debug_environment:
    failures.append('Observatory reintroduced refreshing Environment.For(bat.room) read')
for token in (
    'DB_RoomContext.TryPeekExisting',
    'DB_SocialRoomRuntime.TryPeekExisting',
    'DB_EnvironmentRoomRuntime.TryPeekExisting',
    'Performance.RoomCacheAge',
    'Performance.WeaponRefreshAge',
    'Performance.SocialRefreshAge',
    'Performance.WeatherRefreshAge',
    'Performance.CrowdingRefreshAge',
    'Performance.ShelterBuildCount',
):
    if token not in debug_environment:
        failures.append('R7 Observatory performance field missing: ' + token)

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print('R7 static performance retention passed: shared scans, lean FrameContext, staggered creature perception, distance-cull-before-LOS, lazy room activation, bounded cadences, and arbiter hot-path allocation guard.')
PY
