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
perception = read('Behavior/Perception/DB_PerceptionRuntime.cs')
visibility = read('Behavior/Perception/DB_VisibilityPolicy.cs')
signal_runtime = read('Behavior/Signals/DB_SignalRuntime.cs')
signal_types = read('Behavior/Signals/DB_SignalTypes.cs')
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

# Visibility must cull range before terrain LOS.
distance_gate = visibility.find('if ((targetPosition - origin).sqrMagnitude > range * range)')
los_gate = visibility.find('return observer.room.VisualContact(origin, targetPosition);')
if min(distance_gate, los_gate) < 0 or distance_gate > los_gate:
    failures.append('visibility must reject effective range before Room.VisualContact terrain LOS')
if 'if (!observer.room.VisualContact(origin, targetPosition))' in visibility:
    failures.append('visibility reintroduced terrain LOS before exact distance rejection')

# Perception R2 keeps expensive creature work staggered and owns current projectile ranking.
for token in (
    'internal const int ScanIntervalTicks = 8;',
    'ResetScanPhase();',
    'ScanPhase(int visualSeed)',
    'return 1 + (int)(x % (uint)ScanIntervalTicks);',
):
    if token not in perception:
        failures.append('Perception R2 stagger contract missing: ' + token)
perception_visibility = perception.find('if (!DB_VisibilityPolicy.CanObserve(')
perception_distance = perception.find('float distance = Vector2.Distance(', perception_visibility)
if min(perception_visibility, perception_distance) < 0 or perception_visibility > perception_distance:
    failures.append('Perception R2 creature scan must validate visibility before exact Vector2.Distance sqrt')
if 'context?.ThrownWeapons' not in perception or 'DB_PerceptionScoring.ProjectileRisk' not in perception:
    failures.append('Perception R2 projectile ranking must consume cached thrown weapons through central scoring')
if 'DB_WeaponPerception.TryFindIncomingProjectile(' in perception:
    failures.append('Perception R2 must not delegate current projectile selection back to the retired selector')
if 'internal void UpdateScan()' in perception:
    failures.append('Perception R2 retained the transitional UpdateScan facade')
for retired in ('ReceivePacket(', 'TryGetInfluence(', 'TryGetDebugState(', 'DB_SignalInfluence', 'DB_SignalPerception', 'DB_SignalDebugState'):
    if retired in signal_runtime or retired in signal_types:
        failures.append('Signal transport retained receiver compatibility surface: ' + retired)
if 'internal bool ReceiveSignal(DB_SignalPacket packet, out bool relayAlarm)' not in perception or \
   'internal bool TryGetSignalContext(out DB_PerceptionSignalContext context)' not in perception:
    failures.append('Perception R2 no longer owns direct Signal receiver belief APIs')

# FrameContext only copies Perception R2 facts; it must never rescan the room/projectiles.
for retired in ('VisiblePlayerCount', 'NearestVisiblePlayer', 'PredatorCandidateCount', 'NearestPredator'):
    if retired in frame_context:
        failures.append('FrameContext retained unused per-frame visibility fact: ' + retired)
for token in ('roomContext.Players', 'roomContext.Creatures', 'DB_RoomContext.For(room)', 'DB_WeaponPerception.TryFindIncomingProjectile('):
    if token in frame_context:
        failures.append('FrameContext reintroduced perception scanning: ' + token)
if 'DB_PerceptionSnapshot perception = ai?.Perception?.Snapshot ?? default;' not in frame_context:
    failures.append('FrameContext no longer consumes Perception R2 snapshot')
if 'bool incomingProjectile = perception.HasIncomingProjectile;' not in frame_context:
    failures.append('FrameContext no longer copies incoming-projectile availability from Perception R2')
if 'perception.IncomingProjectile.Observation' not in frame_context:
    failures.append('FrameContext no longer copies ranked projectile observation from Perception R2')

if 'DB_RoomContext.For(bat?.room)' not in fear or 'roomContext.Creatures' not in fear:
    failures.append('Fear trauma target scan no longer uses shared DB_RoomContext creatures')
if 'bat.room.abstractRoom.creatures' in fear:
    failures.append('Fear reintroduced a per-bat abstractRoom.creatures scan')

for directory in (root / 'Behavior', root / 'Core'):
    for path in sorted(directory.rglob('*.cs')):
        if path.name == 'DB_RoomContext.cs':
            continue
        text = path.read_text(encoding='utf-8')
        if re.search(r'\b(?:bat|room|self)\.room\.abstractRoom\.creatures\b|\broom\.abstractRoom\.creatures\b', text):
            failures.append(f'duplicate realized creature scan outside DB_RoomContext: {path.relative_to(root)}')

if '$"rejected by higher-priority {winner.Owner}"' in arbiter:
    failures.append('Arbiter reintroduced per-frame interpolated rejection reason allocation')
if 'HigherPriorityRejectionReason(winner.Owner)' not in arbiter:
    failures.append('Arbiter zero-allocation rejection reason authority missing')

lazy = hooks.find('DB_RoomContext.TryGetExisting(self, out DB_RoomContext context)')
bats_gate = hooks.find('context.Bats.Count == 0', lazy)
signal = hooks.find('DB_SignalRoomRuntime.For(self)', lazy)
environment = hooks.find('DB_EnvironmentRoomRuntime.Update(self)', lazy)
feeding_update = hooks.find('DB_FeedingCoordinator.UpdateRoom(self)', lazy)
if min(lazy, bats_gate, signal, environment, feeding_update) < 0 or not (lazy < bats_gate < signal < environment < feeding_update):
    failures.append('Room.Update lazy DB activation ordering changed')

if 'DB_RoomContext.For(room)' not in feeding or 'context.Players' not in feeding:
    failures.append('Feeding target discovery no longer uses shared DB_RoomContext players')
if feeding.count('PlayerDehydrationFacts.ApplyPredationStress') != 1:
    failures.append('Feeding aggregate drain authority changed')
for label, domain_text in (('coordinator', feeding), ('runtime', feeding_runtime), ('grip', feeding_grip)):
    if 'physicalObjects' in domain_text or 'abstractRoom.creatures' in domain_text:
        failures.append(f'Feeding {label} reintroduced direct realized-room scanning')
    if 'HydrationWeakness' in domain_text or 'ThirstStore.' in domain_text:
        failures.append(f'Feeding {label} bypasses PlayerDehydrationFacts physiology boundary')
if 'DB_RoomContext' in feeding_runtime or 'DB_RoomContext' in feeding_grip:
    failures.append('per-bat Feeding/Grip runtime reintroduced room aggregation')
feeding_refresh = re.search(r'FeedingCoordinatorRefreshTicks\s*=\s*(\d+)', tuning)
if not feeding_refresh or not (8 <= int(feeding_refresh.group(1)) <= 30):
    failures.append('Feeding coordinator refresh cadence must remain bounded to 8..30 ticks')
for token in ('TryPeekTarget(Player target, out DB_FeedingTargetDebugState debug)', 'rooms.TryGetValue(room, out RoomState state)', 'PromoteCloudReservations(state);'):
    if token not in feeding:
        failures.append('Feeding aggregation/observability contract missing: ' + token)

social_match = re.search(r'RefreshInterval\s*=\s*(\d+)', social_room)
if not social_match or not (10 <= int(social_match.group(1)) <= 40):
    failures.append('Social room refresh cadence must remain bounded to 10..40 ticks')
for name in ('WeatherSampleInterval', 'CrowdingSampleInterval'):
    m = re.search(rf'{name}\s*=\s*(\d+)', environment_room)
    if not m or not (10 <= int(m.group(1)) <= 40):
        failures.append(f'{name} must remain bounded to 10..40 ticks')

for token in ('internal bool CuePhasePending = true;', 'state.CueRefresh = phase;', 'CueRefreshPhase(int visualSeed)', 'return 1 + (int)(x % (uint)CueRefreshTicks);'):
    if token not in threat:
        failures.append('Threat cue stagger contract missing: ' + token)
if 'RuntimeState state = StateFor(bat);' in threat[threat.find('internal static bool TryGetDebugState'):threat.find('private static void DamageEvent')]:
    failures.append('Threat debug read creates runtime state')
for token in ('internal bool TraumaScanPhasePending = true;', 'state.TraumaThreatScan = phase;', 'TraumaThreatScanPhase(int visualSeed)', 'return 1 + (int)(x % (uint)TraumaThreatScanTicks);', 'state.TraumaScanPhasePending = true;'):
    if token not in fear:
        failures.append('Fear trauma stagger contract missing: ' + token)
if '$"R3 PrimaryOwner={ownership.PrimaryOwner}"' in hooks:
    failures.append('per-frame primary-owner cancellation reintroduced interpolated allocation')
if 'PrimaryOwnerBlockReason(ownership.PrimaryOwner)' not in hooks:
    failures.append('constant primary-owner cancellation reason authority missing')

for token in ('internal const int WarmupTicks = 40;', 'RecordThreatCue(Room room)', 'RecordTraumaThreatScan(Room room)', 'TryPeek(Room room, out DB_PerformanceSnapshot snapshot)', 'rooms.TryGetValue(room, out RoomState state)', 'new DB_PerformanceSnapshot(0, 0, 0, -1, 0, 0, 0, -1)'):
    if token not in performance_probe:
        failures.append('R7 performance probe contract missing: ' + token)
if 'DB_PerformanceProbe.RecordThreatCue(bat.room);' not in threat:
    failures.append('Threat expensive refresh no longer records R7 spike telemetry')
if 'DB_PerformanceProbe.RecordTraumaThreatScan(bat.room);' not in fear:
    failures.append('Fear trauma scan no longer records R7 spike telemetry')
if hooks.count('DB_PerformanceProbe.Reset();') != 2:
    failures.append('R7 performance probe must reset on both enable and disable')
for token in ('DB_PerformanceProbe.TryPeek', 'Performance.SpikeProbeActive', 'Performance.ThreatCueThisTick', 'Performance.ThreatCuePeakAfterWarmup', 'Performance.ThreatCueTotal', 'Performance.TraumaScanThisTick', 'Performance.TraumaScanPeakAfterWarmup', 'Performance.TraumaScanTotal'):
    if token not in debug_environment:
        failures.append('R7 live spike Observatory field missing: ' + token)

for token in ('TryPeekExisting(Room room, out DB_RoomContext context)', 'internal int SnapshotAge', 'internal int WeaponRefreshAge'):
    if token not in room_context:
        failures.append('room cache observability contract missing: ' + token)
for token in ('TryPeekExisting(Room room, out RoomState state)', 'internal int RefreshAge', 'internal int CachedCandidateCount'):
    if token not in social_room:
        failures.append('social cache observability contract missing: ' + token)
for token in ('TryPeekExisting(Room room, out RoomState state)', 'internal int WeatherSampleAge', 'internal int CrowdingSampleAge', 'internal int AnchorBuildCount', 'state.AnchorBuildCount++;'):
    if token not in environment_room:
        failures.append('environment cache observability contract missing: ' + token)
if 'DB_EnvironmentRoomRuntime.For(bat.room)' in debug_environment:
    failures.append('Observatory reintroduced refreshing Environment.For(bat.room) read')
for token in ('DB_RoomContext.TryPeekExisting', 'DB_SocialRoomRuntime.TryPeekExisting', 'DB_EnvironmentRoomRuntime.TryPeekExisting', 'Performance.RoomCacheAge', 'Performance.WeaponRefreshAge', 'Performance.SocialRefreshAge', 'Performance.WeatherRefreshAge', 'Performance.CrowdingRefreshAge', 'Performance.ShelterBuildCount'):
    if token not in debug_environment:
        failures.append('R7 Observatory performance field missing: ' + token)

if failures:
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)

print('R7 static performance retention passed: shared scans, lean FrameContext, staggered Perception R2 creature attention, ranked cached projectiles, distance-cull-before-LOS, lazy room activation, bounded cadences, and arbiter hot-path allocation guard.')
PY
