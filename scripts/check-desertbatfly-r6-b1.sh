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
  src/Creatures/DesertBatfly/Core/Runtime/DB_Runtime.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelRuntime.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelIntent.cs \
  src/Creatures/DesertBatfly/World/Travel/DB_TravelDebugState.cs \
  src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs \
  src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs \
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

grep -q 'internal sealed class DB_SandSpitRuntime' src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs
grep -q 'DB_SandBurst.Emit' src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs
grep -q '^            bat,$' src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs
! grep -q '^            this,$' src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Behavior/Combat/DB_SandSpitRuntime.cs
if grep -n -E 'playerHolder|sandStruggleMeter|sandSpitThreshold|sandSpitCooldown|sandSpitWindup|sandSpitCycle|EmitSandSpit|PrepareNextSandThreshold|TrackPlayerRelease|UpdateHeldSandStruggle' src/Creatures/DesertBatfly/Core/DB_Creature.cs; then
  echo 'Creature shell regained SandSpit runtime ownership.' >&2
  exit 1
fi

grep -q 'internal sealed class DB_Runtime' src/Creatures/DesertBatfly/Core/Runtime/DB_Runtime.cs
grep -q 'Runtime.BeforeVanillaUpdate()' src/Creatures/DesertBatfly/Core/DB_Creature.cs
grep -q 'Runtime.AfterVanillaUpdate(eu, previousFlightVelocity)' src/Creatures/DesertBatfly/Core/DB_Creature.cs
! grep -q 'base.Update' src/Creatures/DesertBatfly/Core/Runtime/DB_Runtime.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Core/Runtime/DB_Runtime.cs

grep -q 'internal sealed class DB_CreaturePerception' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
grep -q 'internal Creature Danger' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
grep -q 'DB_RoomContext context' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
grep -q 'DB_VisibilityPolicy.CanObserve' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
! grep -n -E '\bdanger[[:space:]]*=' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
! grep -n 'mainBodyChunk.vel[[:space:]]*=' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
! grep -n '\.localGoal[[:space:]]*=' src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs
! grep -q 'private void ScanCreatures' src/Creatures/DesertBatfly/Behavior/DB_AI.cs
! grep -q 'DB_RoomContext context' src/Creatures/DesertBatfly/Behavior/DB_AI.cs

# Dehydration Feeding is a formal domain. It may read Thirst only through
# PlayerDehydrationFacts, and room aggregation belongs only to DB_FeedingCoordinator.
for f in \
  src/Creatures/DesertBatfly/Behavior/Feeding/DB_FeedingCoordinator.cs \
  src/Creatures/DesertBatfly/Behavior/Feeding/DB_DehydrationFeedingRuntime.cs \
  src/Creatures/DesertBatfly/Behavior/Feeding/DB_DehydrationGripRuntime.cs \
  src/Thirst/PlayerDehydrationFacts.cs; do test -f "$f"; done

grep -q 'DB_BehaviorOwner.Feeding' src/Creatures/DesertBatfly/Core/Runtime/DB_BehaviorArbiter.cs
grep -q 'bat.Feeding.ApplyOwnedBehavior()' src/Creatures/DesertBatfly/Core/Runtime/DB_BehaviorExecution.cs
grep -q 'DB_SpecialPhysicsOwner.FeedingAttach' src/Creatures/DesertBatfly/Core/Runtime/DB_FrameContext.cs
grep -q 'bat.Feeding?.Attached == true' src/Creatures/DesertBatfly/Core/Runtime/DB_FrameContext.cs
grep -q 'desert.Feeding.YieldAttachmentForHigherPriority()' src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs
grep -q 'DB_FeedingCoordinator.UpdateRoom(self)' src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs
grep -q 'DB_DehydrationGripRuntime.Enable()' src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs
grep -q 'DB_DehydrationGripRuntime.Disable()' src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs
grep -q '"FeedingRole"' src/Creatures/DesertBatfly/Debug/DB_Trace.cs
grep -q '"FeedingGroup"' src/Creatures/DesertBatfly/Debug/DB_Trace.cs
grep -q 'DB_FeedingCoordinator.TryPeekTarget' src/Creatures/DesertBatfly/Debug/DB_Trace.cs

if grep -RIn --include='*.cs' -E 'HydrationWeakness|ThirstStore\.' src/Creatures/DesertBatfly/Behavior/Feeding; then
  echo 'Feeding bypassed PlayerDehydrationFacts physiology boundary.' >&2
  exit 1
fi
if grep -RIn --include='*.cs' -E 'physicalObjects|abstractRoom\.creatures' src/Creatures/DesertBatfly/Behavior/Feeding; then
  echo 'Feeding reintroduced a direct room scan.' >&2
  exit 1
fi
! grep -q 'DB_RoomContext' src/Creatures/DesertBatfly/Behavior/Feeding/DB_DehydrationFeedingRuntime.cs
! grep -q 'DB_RoomContext' src/Creatures/DesertBatfly/Behavior/Feeding/DB_DehydrationGripRuntime.cs

python3 - <<'PY'
from pathlib import Path
import re
facts = Path('src/Thirst/PlayerDehydrationFacts.cs').read_text(encoding='utf-8')
coordinator = Path('src/Creatures/DesertBatfly/Behavior/Feeding/DB_FeedingCoordinator.cs').read_text(encoding='utf-8')
if re.search(r'(?:>=|<=|>|<)\s*PlayerDehydrationStage\.', facts + coordinator):
    raise SystemExit('PlayerDehydrationStage relational comparison is not legal C#')
if coordinator.count('PlayerDehydrationFacts.ApplyPredationStress') != 1:
    raise SystemExit('Feeding must have exactly one aggregate predation-stress ingress')
if 'DB_RoomContext.For(room)' not in coordinator or 'context.Players' not in coordinator:
    raise SystemExit('Feeding coordinator must consume the shared room player snapshot')
PY

echo 'R6 source retention audit passed.'
