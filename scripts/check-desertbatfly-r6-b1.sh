#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
./scripts/check-desertbatfly-r5-retention.sh

for f in \
  src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs \
  src/Creatures/DesertBatfly/Integration/DB_RuntimePatch.cs \
  src/Creatures/DesertBatfly/Integration/DB_Sandbox.cs \
  src/Creatures/DesertBatfly/Integration/DB_WarpCompatibility.cs \
  src/Creatures/DesertBatfly/Runtime/DB_Runtime.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelIntent.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelDebugState.cs \
  src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs \
  src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs \
  src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs \
  src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_SocialDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_ThreatDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_SignalDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs; do test -f "$f"; done

! grep -RIn --include='*.cs' -E 'DesertBatflyHooks|DesertBatflyRuntimePatch|DesertBatflySandbox|DesertBatflyWarpCompatibility|DesertBatflyTask(09|10|11|12|13)DebugSource|DesertBatflyDebugSource|DesertBatflyTravelNavigation|DesertBatflyTravelDebugState' src
grep -q 'DB_RainWorldHooks.Enable()' src/Plugin.cs
grep -q 'DB_RainWorldHooks.Disable()' src/Plugin.cs
grep -q 'AIDebugRegistry.Register(new DB_EnvironmentDebugSource())' src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs
grep -q 'Register(new DB_ObservatorySource())' src/Debug/AIDebugger/Core/AIDebugRegistry.cs
grep -q 'private readonly DB_ObservatorySource inner = new();' src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs
grep -q 'private readonly DB_TravelDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_SocialDebugSource.cs
grep -q 'private readonly DB_SocialDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_ThreatDebugSource.cs
grep -q 'private readonly DB_ThreatDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_SignalDebugSource.cs
grep -q 'private readonly DB_SignalDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs

grep -q 'internal static class DB_TravelRuntime' src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs
grep -q 'bat.AI.LeaveRoom(new WorldCoordinate' src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs

grep -q 'internal sealed class DB_SandSpitRuntime' src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs
grep -q 'DB_SandBurst.Emit' src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs
grep -q '^            bat,$' src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs
! grep -q '^            this,$' src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Combat/DB_SandSpitRuntime.cs
if grep -n -E 'playerHolder|sandStruggleMeter|sandSpitThreshold|sandSpitCooldown|sandSpitWindup|sandSpitCycle|EmitSandSpit|PrepareNextSandThreshold|TrackPlayerRelease|UpdateHeldSandStruggle' src/Creatures/DesertBatfly/DesertBatfly.cs; then
  echo 'Creature shell regained SandSpit runtime ownership.' >&2
  exit 1
fi

grep -q 'internal sealed class DB_Runtime' src/Creatures/DesertBatfly/Runtime/DB_Runtime.cs
grep -q 'Runtime.BeforeVanillaUpdate()' src/Creatures/DesertBatfly/DesertBatfly.cs
grep -q 'Runtime.AfterVanillaUpdate(eu, previousFlightVelocity)' src/Creatures/DesertBatfly/DesertBatfly.cs
! grep -q 'base.Update' src/Creatures/DesertBatfly/Runtime/DB_Runtime.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Runtime/DB_Runtime.cs

grep -q 'internal sealed class DB_CreaturePerception' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
grep -q 'internal Creature Danger' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
grep -q 'DB_RoomContext context' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
grep -q 'DB_VisibilityPolicy.CanObserve' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
! grep -n -E '\bdanger[[:space:]]*=' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
! grep -n '\.localGoal[[:space:]]*=' src/Creatures/DesertBatfly/Perception/DB_CreaturePerception.cs
! grep -q 'private void ScanCreatures' src/Creatures/DesertBatfly/DesertBatflyAI.cs
! grep -q 'DB_RoomContext context' src/Creatures/DesertBatfly/DesertBatflyAI.cs

echo 'R6 source retention audit passed.'
