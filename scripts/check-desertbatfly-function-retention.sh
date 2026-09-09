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
SIGNALS="$SRC/Behavior/Signals"
SIGNAL_DEF="$SIGNALS/DB_SignalDefinition.cs"
SIGNAL_RT="$SIGNALS/DB_SignalRuntime.cs"
SIGNAL_PACKET="$SIGNALS/DB_SignalPacket.cs"
SIGNAL_ROOM="$SIGNALS/DB_SignalRoomState.cs"
OBSERVATORY='src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs'

# -----------------------------------------------------------------------------
# HB-01 — Severe Injury recovery keeps one native-Dijkstra + FlightMotor path.
# -----------------------------------------------------------------------------
grep -q 'ProgressLocalGoalAlongDijkstraMap(dijkstraInput, bestMap)' "$INJURY"
grep -q 'DB_FlightMotor.TrySteer(' "$INJURY"
grep -q 'DB_BehaviorOwner.InjuryRecovery' "$INJURY"
grep -q 'preserveDijkstra: true' "$INJURY"
grep -q 'internal void ClearLocalTarget()' "$INJURY"

# -----------------------------------------------------------------------------
# HB-02 — realized Travel is invoked once by the enclosing AI frame.
# Keep nested Rain/fallback hooks from creating a second same-frame progression path.
# -----------------------------------------------------------------------------
mapfile -t travel_drivers < <(grep -RIn --include='*.cs' 'DB_TravelRuntime\.TryDriveRealized(' "$SRC" || true)
test "${#travel_drivers[@]}" -eq 1
printf '%s\n' "${travel_drivers[@]}" | grep -q '^.*DB_RainWorldHooks.cs:'

# -----------------------------------------------------------------------------
# HB-03 — one life -> one mortality semantic event, with Damage -> Mortality order.
# Rock remains excluded from lethal attribution. The owned DB_Creature virtual lifecycle
# must enter and complete the EventHub transaction directly; raw Creature hooks are not
# part of the retention contract anymore.
# -----------------------------------------------------------------------------
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

# -----------------------------------------------------------------------------
# HB-04 — Peach tongue / grasp transfer stays one capture session; true recapture gets a
# new serial. Detector duplication must not become semantic-event duplication.
# -----------------------------------------------------------------------------
grep -q 'internal int Serial;' "$EVENTS"
grep -q 'bool priorStillActive = CaptureStillActive(victim, state.Capture);' "$EVENTS"
grep -q 'if (!state.Capture.Accept(captor, priorStillActive)) return;' "$EVENTS"
grep -q 'afterState == LizardTongue.State.AttachedInSmallObject' "$EVENTS"
grep -q 'session.Tongue.state == LizardTongue.State.AttachedInSmallObject' "$EVENTS"

# -----------------------------------------------------------------------------
# HB-05 — CorpseWarning transient objects are tracked and destroyed on runtime reset.
# -----------------------------------------------------------------------------
grep -q 'internal static void TrackRoom(Room room)' "$CORPSE"
grep -q 'internal static void Reset()' "$CORPSE"
grep -q 'item.Destroy();' "$CORPSE"
grep -q 'type.DeclaringType == typeof(DB_FearRuntime)' "$CORPSE"
grep -q 'DB_CorpseWarningRuntime.TrackRoom' "$CONSUMERS"

# -----------------------------------------------------------------------------
# HB-06 — no temporary Thirst spoofing. Legitimate persistent writes remain limited to
# baseline thirst accumulation, approved combat relief, aggregated dehydration-feeding relief,
# and real rain moisture relief.
# -----------------------------------------------------------------------------
mapfile -t thirst_writers < <(grep -RIlE --include='*.cs' 'DesertState\.Thirst\s*=' "$SRC" | sort)
test "${#thirst_writers[@]}" -eq 4
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Behavior/Combat/DB_CombatRuntime.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Behavior/Feeding/DB_FeedingCoordinator.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/Core/Runtime/DB_Runtime.cs"
printf '%s\n' "${thirst_writers[@]}" | grep -qx "$SRC/World/Environment/DB_EnvironmentRuntime.cs"

# -----------------------------------------------------------------------------
# HB-07 — physical-object/weapon observation has one room scan authority.
# -----------------------------------------------------------------------------
mapfile -t physical_scanners < <(grep -RIl --include='*.cs' 'room\.physicalObjects' "$SRC" | sort)
test "${#physical_scanners[@]}" -eq 1
test "${physical_scanners[0]}" = "$SRC/Core/Runtime/DB_RoomContext.cs"
grep -q 'context.ThrownWeapons' "$SRC/Behavior/Perception/DB_WeaponPerception.cs"

# -----------------------------------------------------------------------------
# HB-08 — ordinary unrelated rooms do not eagerly activate specialized DB environment
# ecology. Swarm-room spawning remains intentionally independent of this gate.
# -----------------------------------------------------------------------------
grep -q 'DB_RoomContext.TryGetExisting(self, out DB_RoomContext context)' "$HOOKS"
grep -q 'context.Bats.Count == 0' "$HOOKS"
mapfile -t env_room_updates < <(grep -RIn --include='*.cs' 'DB_EnvironmentRoomRuntime\.Update(' "$SRC" || true)
test "${#env_room_updates[@]}" -eq 1
printf '%s\n' "${env_room_updates[@]}" | grep -q '^.*DB_RainWorldHooks.cs:'

# -----------------------------------------------------------------------------
# HB-09 — one signal transport authority. These are the exact pre-centralization values;
# this guard is a behavior-retention check, not a tuning policy.
# -----------------------------------------------------------------------------
test -f "$SIGNAL_DEF"
grep -q 'DB_SignalKind.AlarmFlutter => new(300f, 95f, 135, 135, 38, 2, 0.56f, 0.31f)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.DistressCall => new(250f, 108f, 120, 120, 52)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.RallySignal => new(235f, 0f, 84, 90, 42)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.RoostCall => new(215f, 0f, 110, 110, 30)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.HarassSignal => new(235f, 0f, 90, 90, 30)' "$SIGNAL_DEF"
grep -q 'DB_SignalKind.SafeSignal => new(195f, 0f, 90, 90, 30)' "$SIGNAL_DEF"
grep -q 'definition.VisualRange' "$SIGNAL_RT"
grep -q 'definition.CloseAcousticRange' "$SIGNAL_RT"
grep -q 'definition.AmbientTtlTicks' "$SIGNAL_RT"
grep -q 'DB_SignalDefinition.For(kind).MaxRelayHops' "$SIGNAL_PACKET"
grep -q 'definition.RelayScale(packet.Hop)' "$SIGNAL_ROOM"
grep -q 'definition.RootTtlTicks' "$SIGNAL_ROOM"
! grep -qE 'MaxAlarmHop|AlarmTtlTicks|AlarmHop1Scale|AlarmHop2Scale|NeutralSignalTtl|internal static float VisualRadius' "$SIGNAL_RT"
! grep -RInE 'DB_SignalRuntime\.(MaxAlarmHop|AlarmTtlTicks|AlarmHop1Scale|AlarmHop2Scale)' "$SIGNALS"

# Formal Vengeance ownership must also be reflected by managed regression probes.
! grep -RIn 'intimidation.GetMethod("ArmVengeance"' "$TESTS"
! grep -RIn 'intimidation.GetMethod("ForceFlight"' "$TESTS"
grep -q 'vengeance.GetMethod("ArmVengeance", Flags)' "$TESTS/Program.Signals.cs"
grep -q 'vengeance.GetNestedType("Participation", Flags)' "$TESTS/Program.Signals.cs"

# -----------------------------------------------------------------------------
# HB-10 — Observatory reports the actual Arbiter result/rejections, not a local heuristic.
# -----------------------------------------------------------------------------
grep -q 'DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution)' "$OBSERVATORY"
grep -q 'resolution.PrimaryOwner' "$OBSERVATORY"
grep -q 'resolution.WinningProposal.BehaviorKind' "$OBSERVATORY"
grep -q 'debug.Rejected' "$OBSERVATORY"

# -----------------------------------------------------------------------------
# HB-11 — Floor roost ownership stays on the Floor tile while the realized hang coordinate
# stays on that tile's underside. Do not regress Floor.Middle - 10 back to Floor.Top.
# -----------------------------------------------------------------------------
grep -q 'DB_RoostAnchorKind.FloorUnderside' "$ROOST"
grep -q 'room.MiddleOfTile(floorTile) + Vector2.down \* 10f' "$ROOST"
! grep -q 'room.MiddleOfTile(floorTile) + Vector2.up \* 10f' "$ROOST"

echo 'DesertBatfly function-retention audit passed: HB-01..HB-11 protected.'
