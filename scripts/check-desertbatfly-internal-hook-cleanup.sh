#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

AI='src/Creatures/DesertBatfly/Behavior/DB_AI.cs'
PERCEPTION='src/Creatures/DesertBatfly/Behavior/Perception/DB_CreaturePerception.cs'
INJURY='src/Creatures/DesertBatfly/Behavior/Injury/DB_InjuryRecovery.cs'
EXECUTION='src/Creatures/DesertBatfly/Core/Runtime/DB_BehaviorExecution.cs'
SWARM='src/Creatures/DesertBatfly/Behavior/DB_SwarmLifecycleRuntime.cs'
HIVE='src/Creatures/DesertBatfly/World/Hive/DB_SwarmRoom.cs'
HOOKS='src/Creatures/DesertBatfly/Integration/DB_RainWorldHooks.cs'
OBS='src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs'
POLICY='src/Creatures/DesertBatfly/Behavior/Restraint/DB_RestraintPolicy.cs'

for file in "$AI" "$PERCEPTION" "$INJURY" "$EXECUTION" "$SWARM" "$HIVE" "$HOOKS" "$OBS" "$POLICY"; do
    test -f "$file"
done

# Project-owned state must be read directly. Observatory must not reflect private DB_AI
# implementation fields or keep a second copy of the restraint classifier.
! grep -q 'using System.Reflection' "$OBS"
! grep -q -E '\b(FieldInfo|BindingFlags)\b' "$OBS"
! grep -q 'private static bool RestrainedByNonFly' "$OBS"
! grep -q 'Read<' "$OBS"
grep -q 'ai.RetreatTicks' "$OBS"
grep -q 'ai.Perception.PursuitTicks' "$OBS"
grep -q 'ai.EscapeFrom' "$OBS"
grep -q 'DB_RestraintPolicy.IsRestrainedByNonFly(bat)' "$OBS"
grep -q 'internal int PursuitTicks => pursuit;' "$PERCEPTION"
grep -q 'internal int RetreatTicks => retreat;' "$AI"
grep -q 'internal Vector2 EscapeFrom => escapeFrom;' "$AI"

# DB_AI may coordinate its own state, but pure forwarding facades to formal domain owners are
# forbidden. Injury and restraint enter their canonical owners directly.
! grep -q 'ExecuteCombatOwned' "$AI"
! grep -q 'ExecuteInjuryRecoveryOwned' "$AI"
! grep -q 'internal bool RestrainedByNonFly' "$AI"
grep -q 'internal DB_InjuryRecovery InjuryRecovery => injuryRecovery;' "$AI"
grep -q 'bat.DesertAI.InjuryRecovery.ExecuteOwned()' "$EXECUTION"
grep -q 'DB_RestraintPolicy.IsRestrainedByNonFly(fly)' "$INJURY"
grep -q 'internal static bool IsRestrainedByNonFly(DB_Creature bat)' "$POLICY"

# Travel/native-rain ownership is resolved by the enclosing FlyAI.Update owner. There must be
# no nested FleeFromRainUpdate hook that re-resolves the same frame. Proposal-driven social
# suppression is preserved at the accepted native owner instead.
! grep -q 'On.FlyAI.FleeFromRainUpdate' "$HOOKS"
! grep -q 'private static void Rain(' "$HOOKS"
grep -q 'ownership.WinningProposal.SuppressSocial' "$HOOKS"
grep -q 'DB_SocialRuntime.CancelForPriority(desert, "R3 PrimaryOwner=" + owner)' "$HOOKS"

# Rain World's IdleUpdate, SwarmUpdate and UpdateFollowDijsktra are nonvirtual integration
# boundaries, so the hooks stay. They must be thin adapters: species rules live in their
# Behavior/Hive domains and must not leak back into Integration.
grep -q 'DB_SwarmLifecycleRuntime.AfterNativeIdleUpdate(self, desert)' "$HOOKS"
grep -q 'DB_SwarmLifecycleRuntime.AfterNativeSwarmUpdate(self, desert)' "$HOOKS"
grep -q 'DB_SwarmRoom.TryHandleNativeFollowDijkstra(self, desert)' "$HOOKS"
! grep -q 'DB_SwarmRoom.IsDB_SwarmRoom' "$HOOKS"
! grep -q 'ValidSwarmPosition' "$HOOKS"
! grep -q 'followingDijkstraMap' "$HOOKS"

grep -q 'internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)' "$SWARM"
grep -q 'internal static void AfterNativeSwarmUpdate(FlyAI ai, DB_Creature bat)' "$SWARM"
grep -q 'internal static bool TryHandleNativeFollowDijkstra(FlyAI ai, DB_Creature bat)' "$HIVE"
grep -q 'IsDB_SwarmRoom(ai.room.abstractRoom)' "$HIVE"

# The temporary audit document is a work queue, not permanent production documentation. Once
# this guard exists, completing this cleanup requires the marker file to be removed.
if test -e src/DesertBatfly_InternalHookAudit.md; then
    echo 'DesertBatfly internal-hook audit marker still exists; cleanup is not complete.' >&2
    exit 1
fi

echo 'DesertBatfly internal hook/facade cleanup guard passed.'
