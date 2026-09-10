#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SRC='src/Creatures/DesertBatfly'
TESTS='tests/DesertBatfly'
HOOKS="$SRC/Integration/DB_RainWorldHooks.cs"
INJURY="$SRC/Behavior/Injury/DB_InjuryRecovery.cs"
ROOST="$SRC/Behavior/DB_RoostPolicy.cs"
EVENTS="$SRC/Core/Runtime/DB_EventHub.cs"
CORPSE="$SRC/Core/Runtime/DB_CorpseWarningRuntime.cs"
CONSUMERS="$SRC/Core/Runtime/DB_EventConsumers.cs"
PERCEPTION="$SRC/Behavior/Perception/DB_PerceptionRuntime.cs"
SIGNALS="$SRC/Behavior/Signals"
SIGNAL_DEF="$SIGNALS/DB_SignalDefinition.cs"
SIGNAL_RT="$SIGNALS/DB_SignalRuntime.cs"
SIGNAL_PACKET="$SIGNALS/DB_SignalPacket.cs"
SIGNAL_ROOM="$SIGNALS/DB_SignalRoomState.cs"
OBSERVATORY='src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs'

grep -q 'ProgressLocalGoalAlongDijkstraMap(dijkstraInput, bestMap)' "$INJURY"
grep -q 'DB_FlightMotor.TrySteer(' "$INJURY"
grep -q 'DB_BehaviorOwner.InjuryRecovery' "$INJURY"
grep -q 'preserveDijkstra: true' "$INJURY"
grep -q 'internal void ClearLocalTarget()' "$INJURY"

mapfile -t travel_drivers < <(grep -RIn --include='*.cs' 'DB_TravelRuntime\.TryDriveRealized(' "$SRC" || true)
test "${#travel_drivers[@]}" -eq 1
printf '%s\n' "${travel_drivers[@]}" | grep -q '^.*DB_RainWorldHooks.cs:'

grep -q 'internal bool TryMarkMortality()' "$EVENTS"
grep -q 'if (MortalityPublished) return false;' "$EVENTS"
grep -q '!transaction.state.TryMarkMortality()' "$EVENTS"
grep -q 'state.ViolenceDepth++' "$EVENTS"
grep -q 'transaction.state.PendingMortality = mortality;' "$EVENTS"
grep -q 'FlushPendingMortality(transaction.state);' "$EVENTS"
grep -q 'sourceObject is not Rock' "$EVENTS"
CREATURE="$SRC/Core/DB_Creature.cs"
grep -q 'DB_EventHub.BeginViolence(' "$CREATURE"
grep -q 'DB_EventHub.EndViolence(' "$CREATURE"
grep -q 'DB_EventHub.PrepareMortality(' "$CREATURE"
grep -q 'DB_EventHub.CompleteMortality(' "$CREATURE"
if grep -q 'RockSurvivalHealthFloor = 0.12f;' "$CREATURE"; then
    echo 'ERROR: HB-03 regressed to the old inflated Rock health floor.'
    exit 1
fi

grep -q 'internal int Serial;' "$EVENTS"
grep -q 'bool priorStillActive = CaptureStillActive(victim, state.Capture);' "$EVENTS"
grep -q 'if (!state.Capture.Accept(captor, priorStillActive)) return;' "$EVENTS"
grep -q 'afterState == LizardTongue.State.AttachedInSmallObject' "$EVENTS"
grep -q 'session.Tongue.state == LizardTongue.State.AttachedInSmallObject' "$EVENTS"

grep -q 'internal static void TrackRoom(Room room)' "$CORPSE"
grep -q 'internal static void Reset()' "$CORPSE"
grep -q 'item.Destroy();' "$CORPSE"
grep -q 'type.DeclaringType == typeof(DB_FearRuntime)' "$CORPSE"
grep -q 'DB_CorpseWarningRuntime.TrackRoom' "$CONSUMERS"

mapfile -t thirst_writers < <(grep -RIlE --include='*.cs' 'DesertState\.Thirst\s*=' "$SRC" | sort)
test "${#thirst_writers[@]}" -eq 4
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Behavior/Combat/DB_CombatRuntime.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Behavior/Feeding/DB_FeedingCoordinator.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Core/Runtime/DB_Runtime.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/World/Environment/DB_EnvironmentRuntime.cs"

mapfile -t physical_scanners < <(grep -RIl --include='*.cs' 'room\.physicalObjects' "$SRC" | sort)
test "${#physical_scanners[@]}" -eq 1
test "${physical_scanners[0]}" = "$SRC/Core/Runtime/DB_RoomContext.cs"
grep -q 'context?.ThrownWeapons' "$PERCEPTION"
grep -q 'DB_PerceptionScoring.ProjectileRisk' "$PERCEPTION"

grep -q 'DB_RoomContext.TryGetExisting(self, out DB_RoomContext context)' "$HOOKS"
grep -q 'context.Bats.Count == 0' "$HOOKS"
mapfile -t env_room_updates < <(grep -RIn --include='*.cs' 'DB_EnvironmentRoomRuntime\.Update(' "$SRC" || true)
test "${#env_room_updates[@]}" -eq 1
printf '%s\n' "${env_room_updates[@]}" | grep -q '^.*DB_RainWorldHooks.cs:'

test -f "$SIGNAL_DEF"
grep -q 'DB_SignalKind.AlarmFlutter => new(300f, 95f, 135, 135, 38, 2, 0.56f, 0.31f)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.DistressCall => new(250f, 108f, 120, 120, 52)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.RallySignal => new(235f, 0f, 84, 90, 42)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.RoostCall => new(215f, 0f, 110, 110, 30)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.HarassSignal => new(235f, 0f, 90, 90, 30)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.SafeSignal => new(195f, 0f, 90, 90, 30)' "$SIGNAL_DEF"
grep -q 'definition.VisualRange' "$PERCEPTION"
grep -q 'definition.CloseAcousticRange' "$PERCEPTION"
grep -q 'DB_PerceptionScoring.SignalConfidence' "$PERCEPTION"
grep -q 'definition.AmbientTtlTicks' "$SIGNAL_RT"
grep -q 'DB_SignalDefinition.For(kind).MaxRelayHops' "$SIGNAL_PACKET"
grep -q 'definition.RelayScale(packet.Hop)' "$SIGNAL_ROOM"
grep -q 'definition.RootTtlTicks' "$SIGNAL_ROOM"
grep -q 'perception.ReceiveSignal(packet, out bool relay)' "$SIGNAL_ROOM"
! grep -q 'ThreatenedAt' "$SIGNAL_RT"
! grep -q 'DB_SocialRuntime.CancelForPriority' "$SIGNAL_RT"
! grep -qE 'MaxAlarmHop|AlarmTtlTicks|AlarmHop1Scale|AlarmHop2Scale|NeutralSignalTtl|internal static float VisualRadius' "$SIGNAL_RT"
! grep -RInE 'DB_SignalRuntime\.(MaxAlarmHop|AlarmTtlTicks|AlarmHop1Scale|AlarmHop2Scale)' "$SIGNALS"

! grep -RIn 'intimidation.GetMethod("ArmVengeance"' "$TESTS"
! grep -RIn 'intimidation.GetMethod("ForceFlight"' "$TESTS"
grep -q 'vengeance.GetMethod("ArmVengeance", Flags)' "$TESTS/Program.Signals.cs"
grep -q 'vengeance.GetNestedType("Participation", Flags)' "$TESTS/Program.Signals.cs"

grep -q 'DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution)' "$OBSERVATORY"
grep -q 'resolution.PrimaryOwner' "$OBSERVATORY"
grep -q 'resolution.WinningProposal.BehaviorKind' "$OBSERVATORY"
grep -q 'debug.Rejected' "$OBSERVATORY"

grep -q 'DB_RoostAnchorKind.FloorUnderside' "$ROOST"
grep -q 'room.MiddleOfTile(floorTile) + Vector2.down \* 10f' "$ROOST"
! grep -q 'room.MiddleOfTile(floorTile) + Vector2.up \* 10f' "$ROOST"

python3 "$TESTS/check_social_diversity.py"

echo 'DesertBatfly function-retention audit passed: HB-01..HB-11 plus social-diversity prediction protected.'
