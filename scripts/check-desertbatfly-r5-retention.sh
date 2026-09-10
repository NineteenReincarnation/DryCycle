#!/usr/bin/env bash
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
