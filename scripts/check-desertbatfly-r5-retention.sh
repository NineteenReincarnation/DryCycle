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
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/RainWorld/DB_RuntimePatch.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/Sandbox/DB_Sandbox.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/Warp/DB_WarpCompatibility.cs'

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

grep -q 'ApplySecondaryLightRainMoisture(bat, state, tick)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs
grep -q 'ApplyNativeHomeAndBurrow(bat, state.Influence)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs
grep -q 'ObserveLocalShelterFailure(state)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalRoomRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldSuppressNewMigration(world, source)' src/Creatures/DesertBatfly/Colony/DB_ColonyRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldRecallHomeForSandstorm' src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs
grep -q 'DB_EnvironmentalPolicy.CanConsiderSandstormOutwardRefuge' src/Creatures/DesertBatfly/Travel/DB_RefugePolicy.cs
grep -q 'DB_EnvironmentalPolicy.AcceptSandstormEmergencyRefuge' src/Creatures/DesertBatfly/Travel/DB_RefugePolicy.cs
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/Environmental
! grep -RIn --include='*.cs' 'new TravelIntent\|RequestPermanentMigration\|ConvertToReturnHome' src/Creatures/DesertBatfly/Environmental

grep -q 'AnonymousAlarmEscapeThreshold = 0.34f' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
grep -q 'ThreatAlarmEscapeThreshold = 0.30f' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
grep -q 'DesertBatflyThreatMemoryStore.For(receiver.DesertState, slot)' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
test "$(grep -c 'DesertBatflySignalRuntime.EmitAcuteAlarm' src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs)" -ge 3
grep -q 'DesertBatflySignalRuntime.EmitDistress' src/Creatures/DesertBatfly/Runtime/DB_EventConsumers.cs
grep -q 'DesertBatflySignalRuntime.EmitRally' src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs
grep -q 'IsDirectDeathWitness(observer, victim, killer)' src/Creatures/DesertBatfly/Social/DB_SocialBond.cs
! grep -RIn --include='*.cs' 'DesertBatflyThreatMemoryStore.AddEvidence' src/Creatures/DesertBatfly/Signals
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/Signals

mapfile -t runtime_patch_users < <(grep -RIl --include='*.cs' 'DB_RuntimePatch' src/Creatures/DesertBatfly | sort)
test "${#runtime_patch_users[@]}" -eq 3
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/RainWorld/DB_RuntimePatch.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/Sandbox/DB_Sandbox.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/Integration/Warp/DB_WarpCompatibility.cs'

echo 'R5 retention audit passed: no internal Bridge/RuntimeDetour debt; reflection is limited to Sandbox/Warp integration.'
