using RWCustom;
using UnityEngine;
using DryCycle.Thirst;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatflyAI
{
    internal enum Activity
    {
        Flight,
        Observe,
        Approach,
        Circle,
        FakeDive,
        Dive,
        Attach,
        RetaliationCharge,
        Interfere,
        Escape,
        Cooldown,
        Roost,
        InjuryRecovery
    }

    private readonly DesertBatfly fly;
    private readonly DB_CombatRuntime combat;
    internal DB_CombatRuntime Combat => combat;
    internal bool HasImmediateDanger => danger != null || retreat > 0 || Mode == Activity.Escape;
    internal Activity Mode { get; private set; }
    internal Creature Target => combat.Target;

    private Creature danger;
    private int retreat, ticks, scan, pursuit;
    private int recoverySearchCooldown;
    private bool hasRoost;
    private Vector2 escapeFrom, roost;
    private Vector2? recoveryRoostTarget;

    internal bool PullingUp => combat.PullingUp;
    internal bool FormalAttack => combat.FormalAttack;
    internal Creature CombatAttacker => combat.Attacker;
    internal int CombatMemory => combat.Memory;
    internal int CombatRetaliationCharges => combat.RetaliationCharges;
    internal int CombatRetaliationRecovery => combat.RetaliationRecovery;

    internal DesertBatflyAI(DesertBatfly fly)
    {
        this.fly = fly;
        combat = new DB_CombatRuntime(this, fly);
    }

    internal void TickMemory()
    {
        combat.TickMemory();

        if (!fly.Consious || RestrainedByNonFly() || fly.inShortcut)
        {
            if (IsInFlyChain(fly))
                BreakHangChain(null, DesertBatflyTuning.RetreatTicks);
            else if (Mode == Activity.Roost)
                StopRoost(false);
            CancelAttack();
            return;
        }

        TickGrabMemory();
        if (retreat > 0) retreat--;
    }

    internal bool RestrainedByNonFly()
    {
        for (int i = 0; i < fly.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = fly.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }

    private void TickGrabMemory()
    {
        DesertBatflyState state = fly.DesertState;
        if (state.GrabMemoryTicks <= 0) return;
        state.GrabMemoryTicks--;
        if (state.GrabMemoryTicks > 0) return;
        state.GrabMemoryPlayer = -1;
        state.GrabMemoryStrength = 0f;
    }

    internal void ResetRoom()
    {
        fly.Injury.ClearTransient();
        if (Mode == Activity.Roost) StopRoost(false);
        CancelAttack();
        combat.Reset();
        danger = null;
        retreat = pursuit = 0;
        recoverySearchCooldown = 0;
        recoveryRoostTarget = null;
        hasRoost = false;
    }

    internal void Threatened(
        Creature source,
        bool directAttack = false,
        bool emitAlarm = true)
    {
        Vector2 origin = source?.mainBodyChunk != null
            ? source.mainBodyChunk.pos
            : fly.mainBodyChunk.pos - Vector2.up * 20f;
        ThreatenedAt(source, origin, directAttack, emitAlarm);
    }

    internal void ThreatenedAt(
        Creature source,
        Vector2 origin,
        bool directAttack = false,
        bool emitAlarm = true)
    {
        ClearRecoveryNavigation();
        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "immediate threat / escape");
        if (source != null && source != fly && source is not DesertBatfly)
            combat.RecordAttacker(source, directAttack ? 1f : 0f);
        escapeFrom = origin;

        if (IsInFlyChain(fly))
            BreakHangChain(source, DesertBatflyTuning.RetreatTicks);
        else
        {
            retreat = DesertBatflyTuning.RetreatTicks;
            CancelAttack();
            SetMode(Activity.Escape);
        }

        if (emitAlarm)
            RaiseLocalAlarm(source, origin,
                source == null ? "direct anonymous danger -> Task12 AlarmFlutter" :
                    "direct threat -> Task12 AlarmFlutter");
    }

    internal void PlayerGrabbed(Player player)
    {
        if (player == null) return;
        RememberGrabber(player, DesertBatflyTuning.GrabMemoryGain);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        if (IsInFlyChain(fly))
            BreakHangChain(player, DesertBatflyTuning.RetreatTicks);
        else
        {
            retreat = Mathf.Max(retreat, DesertBatflyTuning.RetreatTicks);
            CancelAttack();
            SetMode(Activity.Escape);
        }
        RaiseLocalAlarm(player, player.mainBodyChunk.pos,
            "direct player grab -> Task12 AlarmFlutter");
    }

    internal void PlayerReleased(Player player, float releaseSpeed)
    {
        if (player == null || fly.dead) return;

        bool thrown = releaseSpeed >= DesertBatflyTuning.GrabThrowSpeed;
        if (thrown)
            RememberGrabber(player, DesertBatflyTuning.GrabThrowBonus);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        float trauma = PlayerTraumaStrength(player);
        bool traumaBlocksAggression = trauma >= DesertBatflyTuning.TraumaAggressionBlock;
        if (fly.Personality.Aggressive && !traumaBlocksAggression)
        {
            combat.ArmRetaliation(
                player,
                fly.DesertState.GrabMemoryStrength + (thrown ? 0.25f : 0f));
            retreat = Mathf.Clamp(retreat, 35, 60);
        }
        else
        {
            float fear = Mathf.Max(fly.DesertState.GrabMemoryStrength, trauma) *
                         Mathf.Lerp(1.15f, 0.8f, fly.Personality.Nerve);
            retreat = Mathf.Max(
                retreat,
                Mathf.RoundToInt(Mathf.Lerp(90f, 210f, Mathf.Clamp01(fear))));
            combat.ClearRetaliation();
            if (traumaBlocksAggression)
                combat.SuppressHostility(player);
        }

        CancelAttack();
        SetMode(Activity.Escape);
    }

    private void RememberGrabber(Player player, float gain)
    {
        DesertBatflyState state = fly.DesertState;
        int playerNumber = PlayerNumber(player);

        if (state.GrabMemoryPlayer != playerNumber)
        {
            state.GrabMemoryPlayer = playerNumber;
            state.GrabMemoryStrength = 0f;
            state.GrabMemoryTicks = 0;
        }

        state.GrabMemoryStrength = Mathf.Clamp01(
            state.GrabMemoryStrength + Mathf.Max(0f, gain));
        int duration = Mathf.RoundToInt(Mathf.Lerp(
            DesertBatflyTuning.GrabMemoryMinTicks,
            DesertBatflyTuning.GrabMemoryMaxTicks,
            state.GrabMemoryStrength));
        duration = Mathf.RoundToInt(
            duration * Mathf.Lerp(0.95f, 1.12f, fly.Personality.Temperament));
        state.GrabMemoryTicks = Mathf.Clamp(
            Mathf.Max(state.GrabMemoryTicks, duration),
            0,
            DesertBatflyTuning.GrabMemoryMaxTicks);
    }

    private void RaiseLocalAlarm(Creature threat, Vector2 origin, string reason)
    {
        if (fly.room == null) return;
        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, origin);
        DesertBatflySignalRuntime.EmitAlarm(
            fly,
            threat,
            origin,
            direction,
            Mathf.Lerp(0.58f, 0.92f, 1f - fly.Personality.Nerve),
            reason);
    }

    private void DisturbedByApproach(Creature source)
    {
        // Perception only: record an escape fact. R3 ImmediateDanger owns the actual chain
        // release/steering later in the same FlyAI frame.
        escapeFrom = source?.mainBodyChunk.pos ??
                     fly.mainBodyChunk.pos - Vector2.up * 20f;
        retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
    }

    internal void CancelPhysicalAttack()
    {
        if (Target != null || combat.HasSlot) CancelAttack();
        combat.ClearRetaliation();
    }

    internal bool ExecuteInjuryRecoveryOwned()
    {
        DesertBatflyInjury injury = fly.Injury;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))
            return false;

        if (!injury.IsSeverelyInjured)
        {
            ClearRecoveryNavigation();
            injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold");
            if (Mode == Activity.InjuryRecovery) SetMode(Activity.Flight);
            return false;
        }

        // Higher-priority preemption must not erase recovery state or Task09 intent.
        if (fly.dead || !fly.Consious || fly.room == null || RestrainedByNonFly() ||
            fly.inShortcut || fly.Emergence?.Active == true || HasImmediateDanger)
            return false;

        CancelPhysicalAttack();
        SetMode(Activity.InjuryRecovery);

        if (fly.AI.behavior == FlyAI.Behavior.Chain)
        {
            Vector2 target = fly.burrowOrHangSpot ?? fly.mainBodyChunk.pos;
            injury.SetRecovery(InjuryRecoveryState.Roost, target, "severe injury; resting in existing legal chain/roost");
            return true;
        }

        if (recoverySearchCooldown > 0) recoverySearchCooldown--;
        if (recoveryRoostTarget.HasValue && !RecoveryRoostTargetValid(recoveryRoostTarget.Value))
            recoveryRoostTarget = null;

        if (!recoveryRoostTarget.HasValue && recoverySearchCooldown <= 0)
        {
            recoverySearchCooldown = 30;
            if (TryFindRecoveryRoost(out Vector2 candidate))
                recoveryRoostTarget = candidate;
        }

        if (recoveryRoostTarget.HasValue)
        {
            Vector2 target = recoveryRoostTarget.Value;
            if (Custom.DistLess(fly.mainBodyChunk.pos, target, 26f))
            {
                BeginRecoveryRoost(target);
                injury.SetRecovery(InjuryRecoveryState.Roost, target, "severe injury; reached legal local roost");
            }
            else
            {
                SteerOwned(target, 4.2f, DB_BehaviorOwner.InjuryRecovery);
                SetMode(Activity.InjuryRecovery);
                injury.SetRecovery(InjuryRecoveryState.Roost, target, "severe injury; approaching legal local roost");
            }
            return true;
        }

        if (TryDriveRecoveryHive(out Vector2 hiveTarget))
        {
            SetMode(Activity.InjuryRecovery);
            injury.SetRecovery(InjuryRecoveryState.Hive, hiveTarget, "severe injury; native hive dijkstra recovery route");
            return true;
        }

        Vector2 safeGoal = fly.AI.localGoal;
        if (safeGoal == Vector2.zero || fly.room.GetTile(safeGoal).Solid ||
            !fly.room.VisualContact(fly.mainBodyChunk.pos, safeGoal))
            safeGoal = fly.mainBodyChunk.pos + Vector2.up * 60f;
        SteerOwned(safeGoal, 3.8f, DB_BehaviorOwner.InjuryRecovery);
        SetMode(Activity.InjuryRecovery);
        injury.SetRecovery(InjuryRecoveryState.SafeFlight, safeGoal, "severe injury; no reachable local roost or hive; low-risk flight");
        return true;
    }

    private void ClearRecoveryNavigation()
    {
        recoveryRoostTarget = null;
        recoverySearchCooldown = 0;
        if (fly?.AI != null && fly.Injury.RecoveryState == InjuryRecoveryState.Hive &&
            !fly.AI.fleeFromRain && fly.AI.behavior != FlyAI.Behavior.Burrow)
            fly.AI.followingDijkstraMap = -1;
    }

    private bool RecoveryRoostTargetValid(Vector2 target)
    {
        if (fly.room == null || !Custom.DistLess(fly.mainBodyChunk.pos, target, 220f) ||
            !fly.room.VisualContact(fly.mainBodyChunk.pos, target))
            return false;
        IntVector2 tile = fly.room.GetTilePosition(target);
        return tile.x > 0 && tile.x < fly.room.TileWidth - 1 &&
               tile.y >= 4 && tile.y < fly.room.TileHeight - 1 &&
               TryGetRoostSpot(tile, out _);
    }

    private bool TryFindRecoveryRoost(out Vector2 spot)
    {
        spot = default;
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
            if (!TryGetRoostSpot(tile, out Vector2 candidate) ||
                !Custom.DistLess(fly.mainBodyChunk.pos, candidate, 190f) ||
                !fly.room.VisualContact(fly.mainBodyChunk.pos, candidate))
                continue;

            float score = (candidate - fly.mainBodyChunk.pos).sqrMagnitude;
            if (score >= best) continue;
            best = score;
            spot = candidate;
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
            fly.AI.ChangeBehavior(FlyAI.Behavior.Burrow);
            fly.burrowOrHangSpot = fly.mainBodyChunk.pos;
            fly.movMode = Fly.MovementMode.Burrow;
            fly.AI.afraid = Mathf.Max(fly.AI.afraid, 0.8f);
            hasRoost = false;
            return true;
        }

        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            fly.AI.behavior = FlyAI.Behavior.Idle;
        fly.movMode = Fly.MovementMode.BatFlight;
        hasRoost = false;

        Vector2 dijkstraInput = fly.AI.localGoal;
        if (dijkstraInput == Vector2.zero || fly.room.GetTile(dijkstraInput).Solid)
            dijkstraInput = fly.mainBodyChunk.pos;
        Vector2 next = fly.AI.ProgressLocalGoalAlongDijkstraMap(dijkstraInput, bestMap);
        return DB_FlightMotor.TrySteer(
            fly,
            DB_BehaviorOwner.InjuryRecovery,
            next,
            4.2f,
            preserveDijkstra: true,
            response: 0.20f);
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

    private void BeginRecoveryRoost(Vector2 spot)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery)) return;
        recoveryRoostTarget = spot;
        roost = spot;
        hasRoost = true;
        fly.AI.followingDijkstraMap = -1;
        fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        fly.burrowOrHangSpot = roost;
        fly.movMode = Fly.MovementMode.Hang;
        fly.mainBodyChunk.vel *= 0.5f;
        SetMode(Activity.InjuryRecovery);
    }

    internal void CancelAttack()
    {
        combat.ClearAttackState();
        combat.ClearTarget();
        SetMode(Activity.Flight);
    }

    internal void BeginGriefResponse()
    {
        CancelAttack();
        combat.ClearMemoryAndRetaliation();
    }

    internal void SuppressHostility(Creature source)
    {
        if (source == null) return;
        combat.SuppressHostility(source);
        pursuit = 0;
    }

    internal void SetMode(Activity next)
    {
        if (Mode == next) return;
        Mode = next;
        ticks = 0;
        combat.OnModeChanged(next);
    }

    internal void ClearCombatTarget() => combat.ClearTarget();

    internal void BeginCombatEscape(Vector2 from, int retreatTicks)
    {
        escapeFrom = from;
        retreat = Mathf.Max(retreat, retreatTicks);
        SetMode(Activity.Escape);
    }

    internal void RefreshDecisionState()
    {
        if (fly.room == null) return;

        // This phase may refresh species state and perception, but it must not steer ordinary
        // locomotion. All localGoal/velocity writes live behind the selected R3 owner below.
        if (fly.Emergence.Active || RestrainedByNonFly() ||
            !fly.Consious || fly.inShortcut)
        {
            if (Mode == Activity.Roost)
            {
                hasRoost = false;
                SetMode(Activity.Flight);
            }
            if (Mode == Activity.InjuryRecovery)
            {
                recoveryRoostTarget = null;
                recoverySearchCooldown = 0;
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "unavailable / restraint / shortcut");
            }
            CancelAttack();
            return;
        }

        if (++scan >= 8)
        {
            scan = 0;
            ScanCreatures();
        }

        bool recoveryBurrow = fly.AI.behavior == FlyAI.Behavior.Burrow &&
                              fly.Injury.RecoveryState == InjuryRecoveryState.Hive;
        if (fly.AI.fleeFromRain || (!recoveryBurrow && fly.AI.behavior == FlyAI.Behavior.Burrow) ||
            fly.AI.luredCounter > 0 || fly.safariControlled)
        {
            if (Mode == Activity.Roost)
            {
                hasRoost = false;
                SetMode(Activity.Flight);
            }
            if (Mode == Activity.InjuryRecovery)
            {
                recoveryRoostTarget = null;
                recoverySearchCooldown = 0;
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "native special behavior owns frame");
            }
            CancelAttack();
            return;
        }

        if (danger != null && retreat <= 0)
        {
            escapeFrom = danger.mainBodyChunk.pos;
            retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
        }

        if (danger != null || retreat > 0)
        {
            recoveryRoostTarget = null;
            recoverySearchCooldown = 0;
            fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "danger / escape");
            hasRoost = false;
            combat.ClearAttackState();
            combat.ClearTarget();
            SetMode(Activity.Escape);
            if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
            return;
        }

        if (recoveryBurrow)
        {
            if (!fly.Injury.IsSeverelyInjured)
            {
                ClearRecoveryNavigation();
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold while entering hive");
                fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
                fly.burrowOrHangSpot = null;
                fly.movMode = Fly.MovementMode.BatFlight;
                SetMode(Activity.Flight);
            }
            else
            {
                SetMode(Activity.InjuryRecovery);
                return;
            }
        }

        if (Mode == Activity.Escape) SetMode(Activity.Flight);

        // Severe recovery itself is executed only by DB_InjuryRecoveryExecutor. A milder
        // injury may still suppress combat and allow a normal roost proposal.
        if (fly.Injury.BlocksCombat)
        {
            CancelPhysicalAttack();
            if (Mode != Activity.Roost) CancelAttack();
            TryPlanRoost();
            return;
        }

        DB_CombatRuntime.SelectionResult combatSelection = combat.PrepareSelection();
        if (combatSelection == DB_CombatRuntime.SelectionResult.Cooldown)
            return;
        if (combatSelection != DB_CombatRuntime.SelectionResult.Ready)
        {
            TryPlanRoost();
            return;
        }

        // Combat selected a valid target/mode, but locomotion remains frozen until Arbiter
        // actually grants PrimaryOwner=Combat.
        hasRoost = false;
    }

    // Compatibility surface for old callers/tests. R3 hooks use RefreshDecisionState directly;
    // this alias never executes locomotion.
    internal void Update() => RefreshDecisionState();

    internal bool ExecuteImmediateDangerOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.ImmediateDanger) ||
            fly.room == null || fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;
        if (danger == null && retreat <= 0 && Mode != Activity.Escape)
            return false;

        ClearRecoveryNavigation();
        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "R3 PrimaryOwner=ImmediateDanger");
        hasRoost = false;
        combat.ClearAttackState();
        Target = null;
        SetMode(Activity.Escape);
        if (danger != null) escapeFrom = danger.mainBodyChunk.pos;

        Vector2 goal = fly.mainBodyChunk.pos +
                       Custom.DirVec(escapeFrom, fly.mainBodyChunk.pos) * 160f +
                       Vector2.up * 50f;
        return SteerOwned(goal, 8f, DB_BehaviorOwner.ImmediateDanger);
    }

    internal bool ExecuteFearOwned(in DB_BehaviorResolution resolution)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.FearResponse) ||
            resolution.PrimaryOwner != DB_BehaviorOwner.FearResponse || fly.room == null ||
            fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;

        CancelPhysicalAttack();
        if (resolution.WinningProposal.PreserveGoal || !resolution.FinalGoal.HasValue)
            return true;

        Vector2 goal = resolution.FinalGoal.Value;
        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;
        escapeFrom = fly.mainBodyChunk.pos - direction * 80f;
        retreat = Mathf.Max(retreat, 60);
        SetMode(Activity.Escape);
        return SteerOwned(
            goal,
            Mathf.Max(6f, resolution.WinningProposal.NominalSpeed),
            DB_BehaviorOwner.FearResponse);
    }

    internal bool ExecuteCombatOwned()
        => combat.TryExecuteOwned();

    internal bool ExecuteRoostOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Roost) ||
            fly.room == null || fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;

        if (Mode == Activity.Roost)
        {
            ticks++;
            int roostDuration = DB_EnvironmentalPolicy.AdjustRoostDuration(fly, fly.Personality.RoostDuration);
            if (!hasRoost || ticks > roostDuration || fly.AI.fleeFromRain)
            {
                StopRoost(true);
                return true;
            }

            if (fly.AI.behavior != FlyAI.Behavior.Chain)
                fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
            fly.burrowOrHangSpot = roost;
            fly.movMode = Fly.MovementMode.Hang;
            fly.mainBodyChunk.vel *= 0.5f;
            fly.AI.HangInChainUpdate();
            return true;
        }

        // A native/social Fly chain is still a Roost owner. Preserve vanilla chain physics
        // without running the rest of FlyAI.Update, which could choose an unrelated goal.
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
        {
            fly.movMode = Fly.MovementMode.Hang;
            fly.AI.HangInChainUpdate();
            return true;
        }

        return false;
    }

    // Compatibility surface for older tests/callers. Production calls Combat.AfterPhysics directly.
    internal void AfterPhysics(bool eu)
        => combat.AfterPhysics(eu);

    internal bool Valid(Creature creature)
    {
        return creature != null && !creature.dead &&
               !creature.slatedForDeletetion && creature.room == fly.room &&
               !creature.inShortcut && creature.grabbedBy.Count == 0 &&
               (creature.abstractCreature.rippleLayer == fly.abstractCreature.rippleLayer ||
                creature.abstractCreature.rippleBothSides ||
                fly.abstractCreature.rippleBothSides);
    }

    private void ScanCreatures()
    {
        danger = null;
        combat.BeginCandidateScan();

        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var creatures = context?.Creatures;
        if (creatures == null) return;

        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            if (creature == fly || creature is DesertBatfly || !Valid(creature))
                continue;

            float distance = Vector2.Distance(fly.mainBodyChunk.pos, creature.mainBodyChunk.pos);
            DB_VisibilityChannel channel = creature is Player
                ? DB_VisibilityChannel.Player
                : DB_VisibilityChannel.Creature;
            if (distance > DesertBatflyTuning.SightRange ||
                !DB_VisibilityPolicy.CanObserve(
                    fly, creature.mainBodyChunk.pos, DesertBatflyTuning.SightRange, channel))
                continue;

            CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
            CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
            bool predator = creature is not Player &&
                (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
                 reverse.type == CreatureTemplate.Relationship.Type.Eats ||
                 reverse.type == CreatureTemplate.Relationship.Type.Attacks);

            if (predator)
            {
                float ordinaryThreatDistance = Mathf.Lerp(90f, 260f, Mathf.Clamp01(creature.TotalMass));
                float nerveScale = Mathf.Lerp(1.15f, 0.58f, fly.Personality.Nerve);
                float threatDistance = Mathf.Max(55f, ordinaryThreatDistance * nerveScale);
                if (distance < threatDistance) danger = creature;
            }

            if (creature is Player player)
            {
                bool traumatized = IsTraumatizedPlayer(player);
                bool remembered = !traumatized && IsRememberedPlayer(player);
                if (remembered && !fly.Personality.Aggressive)
                {
                    float fearDistance = Mathf.Lerp(
                        DesertBatflyTuning.GrabFearMinDistance,
                        DesertBatflyTuning.GrabFearMaxDistance,
                        fly.DesertState.GrabMemoryStrength);
                    fearDistance *= Mathf.Lerp(1.12f, 0.72f, fly.Personality.Nerve);
                    if (distance < fearDistance) danger = player;
                }

                float reactionDistance = Mathf.Lerp(125f, 78f, fly.Personality.Nerve);
                float closingThreshold = Mathf.Lerp(2.1f, 4.4f, fly.Personality.Nerve);
                int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(16f, 44f, fly.Personality.Nerve));
                if (remembered && DB_EnvironmentalPolicy.AggressionAuthorized(fly))
                {
                    reactionDistance *= 0.72f;
                    closingThreshold *= 1.25f;
                    pursuitThreshold = Mathf.RoundToInt(pursuitThreshold * 1.35f);
                }

                if (distance < reactionDistance)
                {
                    float closing = Vector2.Dot(
                        player.mainBodyChunk.vel,
                        Custom.DirVec(player.mainBodyChunk.pos, fly.mainBodyChunk.pos));
                    if (closing > closingThreshold) pursuit += 8;
                    else pursuit = Mathf.Max(0, pursuit - 4);
                    if (pursuit >= pursuitThreshold)
                    {
                        DisturbedByApproach(player);
                        pursuit = 0;
                    }
                }
                else
                {
                    pursuit = Mathf.Max(0, pursuit - 2);
                }
            }

            combat.ConsiderCandidate(creature, distance);
        }

        combat.CompleteCandidateScan(retreat > 0);
    }

    internal bool IsRememberedPlayer(Player player)
    {
        DesertBatflyState state = fly.DesertState;
        return player != null && state.GrabMemoryTicks > 0 &&
            state.GrabMemoryStrength > 0f &&
            state.GrabMemoryPlayer == PlayerNumber(player);
    }

    internal bool IsTraumatizedPlayer(Player player)
    {
        return PlayerTraumaStrength(player) >=
               DesertBatflyTuning.TraumaAggressionBlock;
    }

    private float PlayerTraumaStrength(Player player)
    {
        if (player == null) return 0f;
        DesertBatflyState state = fly.DesertState;
        int playerNumber = PlayerNumber(player);
        return state.PlayerTraumaTicks > 0 &&
               state.PlayerTraumaPlayer == playerNumber
            ? state.PlayerTraumaStrength
            : 0f;
    }

    private static int PlayerNumber(Player player)
    {
        return player?.playerState?.playerNumber ?? 0;
    }

    internal bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)
    {
        if (!DB_FlightMotor.TrySteer(fly, owner, goal, speed)) return false;
        hasRoost = false;
        return true;
    }

    private void TryPlanRoost()
    {
        if (Mode == Activity.Roost || hasRoost || fly.AI.behavior == FlyAI.Behavior.Chain)
            return;
        if (scan != 0 || Random.value >
            fly.Personality.RoostChance * DB_EnvironmentalPolicy.RoostChanceScale(fly) *
            DesertBatflySocialBond.RoostScale(fly) * fly.Injury.RoostScale)
            return;
        if (!TryFindRoost(out Vector2 spot)) return;

        // State/proposal preparation only. DB_RoostExecutor performs the Chain/Hang writes.
        roost = spot;
        hasRoost = true;
        SetMode(Activity.Roost);
    }

    private bool TryFindRoost(out Vector2 spot)
    {
        IntVector2 tile = fly.room.GetTilePosition(fly.mainBodyChunk.pos);
        return TryGetRoostSpot(tile, out spot);
    }

    private bool TryGetRoostSpot(IntVector2 tile, out Vector2 spot)
    {
        spot = default;
        if (fly.room == null || fly.AI == null ||
            tile.x <= 0 || tile.x >= fly.room.TileWidth - 1 ||
            tile.y < 4 || tile.y >= fly.room.TileHeight - 1 ||
            !fly.AI.ChainTile(tile))
            return false;

        Room.Tile current = fly.room.GetTile(tile);
        Room.Tile above = fly.room.GetTile(tile.x, tile.y + 1);
        Vector2 middle = fly.room.MiddleOfTile(tile);

        if (current.horizontalBeam)
            spot = new Vector2(middle.x, middle.y - 4f);
        else if (above.verticalBeam && !current.verticalBeam)
            spot = middle + Vector2.up * 10f;
        else
            spot = middle + Vector2.up * 10f;
        return true;
    }

    private void StopRoost(bool releaseWholeChain)
    {
        if (releaseWholeChain && IsInFlyChain(fly))
        {
            ReleaseHangChain(false, null, 0);
            return;
        }

        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        hasRoost = false;
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        fly.movMode = Fly.MovementMode.BatFlight;
        SetMode(Activity.Flight);
    }

    private static bool IsInFlyChain(Fly member)
    {
        return member?.AI != null &&
               member.AI.behavior == FlyAI.Behavior.Chain;
    }

    private void BreakHangChain(Creature source, int retreatTicks)
    {
        ReleaseHangChain(true, source, retreatTicks);
    }

    private void ReleaseHangChain(
        bool frightened,
        Creature source,
        int retreatTicks)
    {
        if (fly == null) return;

        Fly member = fly.FirstInChain();
        int guard = 0;
        Vector2 sourcePos = source?.mainBodyChunk.pos ??
                            fly.mainBodyChunk.pos - Vector2.up * 20f;

        while (member != null && guard++ < 32)
        {
            Fly next = member.NextInChain();
            member.LoseAllGrasps();
            member.burrowOrHangSpot = null;
            if (member.AI != null &&
                member.AI.behavior == FlyAI.Behavior.Chain)
            {
                member.AI.ChangeBehavior(FlyAI.Behavior.Idle);
            }
            member.movMode = Fly.MovementMode.BatFlight;

            if (member is DesertBatfly desert)
            {
                DesertBatflyAI brain = desert.DesertAI;
                brain.hasRoost = false;
                brain.combat.ClearAttackState();
                brain.combat.ClearTarget();

                if (frightened)
                {
                    brain.escapeFrom = sourcePos;
                    brain.retreat = Mathf.Max(
                        brain.retreat,
                        retreatTicks);
                    brain.SetMode(Activity.Escape);
                }
                else
                {
                    brain.SetMode(Activity.Flight);
                }
            }

            member = next;
        }
    }
}
