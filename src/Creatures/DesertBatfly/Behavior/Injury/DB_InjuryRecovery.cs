using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Severe-injury local recovery owner. It searches legal local roosts, drives the
/// native hive Dijkstra fallback and performs the owned low-risk recovery steering.
/// Cross-room travel remains DB_TravelRuntime authority.
/// </summary>
internal sealed class DB_InjuryRecovery
{
    private readonly DB_AI brain;
    private readonly DB_Creature fly;
    private int recoverySearchCooldown;
    private DB_RoostAnchor? recoveryRoostTarget;

    internal DB_InjuryRecovery(DB_AI brain, DB_Creature fly)
    {
        this.brain = brain;
        this.fly = fly;
    }

    internal void ClearLocalTarget()
    {
        recoveryRoostTarget = null;
        recoverySearchCooldown = 0;
    }

    internal void ResetRoom() => ClearLocalTarget();
    internal bool ExecuteOwned()
    {
        DB_Injury injury = fly.Injury;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))
            return false;

        if (!injury.IsSeverelyInjured)
        {
            ClearNavigation();
            injury.SetRecovery(DB_InjuryRecoveryState.None, null, "recovered below severe threshold");
            if (brain.Mode == DB_AI.Activity.InjuryRecovery) brain.SetMode(DB_AI.Activity.Flight);
            return false;
        }

        // Higher-priority preemption must not erase recovery state or an active travel intent.
        if (fly.dead || !fly.Consious || fly.room == null ||
            DB_RestraintPolicy.IsRestrainedByNonFly(fly) || fly.inShortcut ||
            fly.Emergence?.Active == true || brain.HasImmediateDanger)
            return false;

        brain.CancelPhysicalAttack();
        brain.SetMode(DB_AI.Activity.InjuryRecovery);

        if (fly.AI.behavior == FlyAI.Behavior.Chain)
        {
            Vector2 target = fly.burrowOrHangSpot ?? fly.mainBodyChunk.pos;
            injury.SetRecovery(DB_InjuryRecoveryState.Roost, target, "severe injury; resting in existing legal chain/roost");
            return true;
        }

        if (recoverySearchCooldown > 0) recoverySearchCooldown--;
        if (recoveryRoostTarget.HasValue && !RecoveryRoostTargetValid(recoveryRoostTarget.Value))
            recoveryRoostTarget = null;

        if (!recoveryRoostTarget.HasValue && recoverySearchCooldown <= 0)
        {
            recoverySearchCooldown = 30;
            if (TryFindRecoveryRoost(out DB_RoostAnchor candidate))
                recoveryRoostTarget = candidate;
        }

        if (recoveryRoostTarget.HasValue)
        {
            DB_RoostAnchor anchor = recoveryRoostTarget.Value;
            Vector2 target = anchor.Spot;
            if (Custom.DistLess(fly.mainBodyChunk.pos, target, 26f))
            {
                BeginRecoveryRoost(anchor);
                injury.SetRecovery(DB_InjuryRecoveryState.Roost, target, "severe injury; reached legal local roost");
            }
            else
            {
                brain.SteerOwned(target, 4.2f, DB_BehaviorOwner.InjuryRecovery);
                brain.SetMode(DB_AI.Activity.InjuryRecovery);
                injury.SetRecovery(DB_InjuryRecoveryState.Roost, target, "severe injury; approaching legal local roost");
            }
            return true;
        }

        if (TryDriveRecoveryHive(out Vector2 hiveTarget))
        {
            brain.SetMode(DB_AI.Activity.InjuryRecovery);
            injury.SetRecovery(
                DB_InjuryRecoveryState.Hive,
                hiveTarget,
                DB_HiveTraffic.IsAdmitted(fly)
                    ? "severe injury; native hive dijkstra recovery route"
                    : "severe injury; waiting at distributed BatHive ingress hold");
            return true;
        }

        Vector2 safeGoal = fly.AI.localGoal;
        if (safeGoal == Vector2.zero || fly.room.GetTile(safeGoal).Solid ||
            !fly.room.VisualContact(fly.mainBodyChunk.pos, safeGoal))
            safeGoal = fly.mainBodyChunk.pos + Vector2.up * 60f;
        brain.SteerOwned(safeGoal, 3.8f, DB_BehaviorOwner.InjuryRecovery);
        brain.SetMode(DB_AI.Activity.InjuryRecovery);
        injury.SetRecovery(DB_InjuryRecoveryState.SafeFlight, safeGoal, "severe injury; no reachable local roost or hive; low-risk flight");
        return true;
    }

    internal void ClearNavigation()
    {
        recoveryRoostTarget = null;
        recoverySearchCooldown = 0;
        if (fly?.AI != null && fly.Injury.RecoveryState == DB_InjuryRecoveryState.Hive &&
            !fly.AI.fleeFromRain && fly.AI.behavior != FlyAI.Behavior.Burrow)
            fly.AI.followingDijkstraMap = -1;
        DB_HiveTraffic.Forget(fly);
    }

    private bool RecoveryRoostTargetValid(DB_RoostAnchor anchor)
    {
        Vector2 target = anchor.Spot;
        return fly.room != null &&
               Custom.DistLess(fly.mainBodyChunk.pos, target, 220f) &&
               fly.room.VisualContact(fly.mainBodyChunk.pos, target) &&
               DB_RoostPolicy.IsStillValid(fly, anchor);
    }

    private bool TryFindRecoveryRoost(out DB_RoostAnchor anchor)
    {
        anchor = default;
        if (fly.room == null || fly.AI == null) return false;
        IntVector2 origin = fly.room.GetTilePosition(fly.mainBodyChunk.pos);
        float best = float.MaxValue;
        bool found = false;
        const int radius = 7;

        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            IntVector2 tile = new IntVector2(origin.x + x, origin.y + y);
            if (tile.x <= 0 || tile.x >= fly.room.TileWidth - 1 ||
                tile.y < 4 || tile.y >= fly.room.TileHeight - 1)
                continue;
            if (!DB_RoostPolicy.TryGetAnchor(fly, tile, out DB_RoostAnchor candidate) ||
                !Custom.DistLess(fly.mainBodyChunk.pos, candidate.Spot, 190f) ||
                !fly.room.VisualContact(fly.mainBodyChunk.pos, candidate.Spot))
                continue;

            float score = (candidate.Spot - fly.mainBodyChunk.pos).sqrMagnitude;
            if (score >= best) continue;
            best = score;
            anchor = candidate;
            found = true;
        }
        return found;
    }

    private bool TryDriveRecoveryHive(out Vector2 target)
    {
        target = default;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))
            return false;
        if (fly.room?.aimap == null || fly.room.hives == null || fly.room.hives.Length == 0)
            return false;

        IntVector2 current = fly.room.GetTilePosition(fly.mainBodyChunk.pos);
        int bestHive = -1;
        int bestMap = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < fly.room.hives.Length; i++)
        {
            if (fly.room.hives[i] == null || fly.room.hives[i].Length == 0) continue;
            int map = fly.room.exitAndDenIndex.Length + i;
            int distance = fly.room.aimap.ExitDistanceForCreature(current, map, fly.Template);
            if (distance < 0 || distance >= bestDistance) continue;
            bestDistance = distance;
            bestHive = i;
            bestMap = map;
        }
        if (bestHive < 0) return false;

        target = ClosestHivePoint(bestHive);
        fly.AI.leaveRoomDijkstra = -1;
        fly.AI.followingDijkstraMap = bestMap;

        if (fly.room.GetTile(fly.mainBodyChunk.pos).hive)
        {
            brain.ClearRoostClaim();
            return DB_HiveDocking.TryHandleInjuryRecovery(fly);
        }

        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            fly.AI.behavior = FlyAI.Behavior.Idle;
        fly.movMode = Fly.MovementMode.BatFlight;
        brain.ClearRoostClaim();

        Vector2 dijkstraInput = fly.AI.localGoal;
        if (dijkstraInput == Vector2.zero || fly.room.GetTile(dijkstraInput).Solid)
            dijkstraInput = fly.mainBodyChunk.pos;
        Vector2 next = fly.AI.ProgressLocalGoalAlongDijkstraMap(dijkstraInput, bestMap);
        bool guided = DB_FlightMotor.TrySteer(
            fly,
            DB_BehaviorOwner.InjuryRecovery,
            next,
            4.2f,
            preserveDijkstra: true,
            response: 0.20f);
        if (guided && !DB_HiveTraffic.IsAdmitted(fly))
            target = fly.AI.localGoal;
        return guided;
    }

    private Vector2 ClosestHivePoint(int hiveIndex)
    {
        IntVector2[] tiles = fly.room.hives[hiveIndex];
        Vector2 best = fly.mainBodyChunk.pos;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < tiles.Length; i++)
        {
            Vector2 candidate = fly.room.MiddleOfTile(tiles[i]);
            float distance = (candidate - fly.mainBodyChunk.pos).sqrMagnitude;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }
        return best;
    }

    private void BeginRecoveryRoost(DB_RoostAnchor anchor)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery)) return;
        recoveryRoostTarget = anchor;
        DB_HiveTraffic.Forget(fly);
        brain.SetRoostClaim(anchor);
        fly.AI.followingDijkstraMap = -1;
        fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        fly.burrowOrHangSpot = anchor.Spot;
        fly.movMode = Fly.MovementMode.Hang;
        fly.mainBodyChunk.vel *= 0.5f;
        brain.SetMode(DB_AI.Activity.InjuryRecovery);
    }
}
