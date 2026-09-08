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
    private readonly DB_CreaturePerception perception;
    private readonly DB_InjuryRecovery injuryRecovery;
    internal DB_CombatRuntime Combat => combat;
    internal DB_CreaturePerception Perception => perception;
    internal bool HasImmediateDanger => perception.Danger != null || retreat > 0 || Mode == Activity.Escape;
    internal Activity Mode { get; private set; }
    internal Creature Target => combat.Target;
    internal bool RetreatActive => retreat > 0;

    private int retreat, ticks;
    private bool hasRoost;
    private Vector2 escapeFrom, roost;

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
        perception = new DB_CreaturePerception(this, fly);
        injuryRecovery = new DB_InjuryRecovery(this, fly);
    }

    internal void TickMemory()
    {
        combat.TickMemory();

        if (!fly.Consious || RestrainedByNonFly() || fly.inShortcut)
        {
            if (IsInFlyChain(fly))
                BreakHangChain(null, DB_Tuning.RetreatTicks);
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
        DB_State state = fly.DesertState;
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
        perception.Reset();
        retreat = 0;
        injuryRecovery.ResetRoom();
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
        injuryRecovery.ClearNavigation();
        fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "immediate threat / escape");
        if (source != null && source != fly && source is not DesertBatfly)
            combat.RecordAttacker(source, directAttack ? 1f : 0f);
        escapeFrom = origin;

        if (IsInFlyChain(fly))
            BreakHangChain(source, DB_Tuning.RetreatTicks);
        else
        {
            retreat = DB_Tuning.RetreatTicks;
            CancelAttack();
            SetMode(Activity.Escape);
        }

        if (emitAlarm)
            RaiseLocalAlarm(source, origin,
                source == null ? "direct anonymous danger -> AlarmFlutter" :
                    "direct threat -> AlarmFlutter");
    }

    internal void PlayerGrabbed(Player player)
    {
        if (player == null) return;
        RememberGrabber(player, DB_Tuning.GrabMemoryGain);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        if (IsInFlyChain(fly))
            BreakHangChain(player, DB_Tuning.RetreatTicks);
        else
        {
            retreat = Mathf.Max(retreat, DB_Tuning.RetreatTicks);
            CancelAttack();
            SetMode(Activity.Escape);
        }
        RaiseLocalAlarm(player, player.mainBodyChunk.pos,
            "direct player grab -> AlarmFlutter");
    }

    internal void PlayerReleased(Player player, float releaseSpeed)
    {
        if (player == null || fly.dead) return;

        bool thrown = releaseSpeed >= DB_Tuning.GrabThrowSpeed;
        if (thrown)
            RememberGrabber(player, DB_Tuning.GrabThrowBonus);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        float trauma = PlayerTraumaStrength(player);
        bool traumaBlocksAggression = trauma >= DB_Tuning.TraumaAggressionBlock;
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
        DB_State state = fly.DesertState;
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
            DB_Tuning.GrabMemoryMinTicks,
            DB_Tuning.GrabMemoryMaxTicks,
            state.GrabMemoryStrength));
        duration = Mathf.RoundToInt(
            duration * Mathf.Lerp(0.95f, 1.12f, fly.Personality.Temperament));
        state.GrabMemoryTicks = Mathf.Clamp(
            Mathf.Max(state.GrabMemoryTicks, duration),
            0,
            DB_Tuning.GrabMemoryMaxTicks);
    }

    private void RaiseLocalAlarm(Creature threat, Vector2 origin, string reason)
    {
        if (fly.room == null) return;
        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, origin);
        DB_SignalRuntime.EmitAlarm(
            fly,
            threat,
            origin,
            direction,
            Mathf.Lerp(0.58f, 0.92f, 1f - fly.Personality.Nerve),
            reason);
    }

    internal void DisturbedByApproach(Creature source)
    {
        // Perception only: record an escape fact. R3 ImmediateDanger owns the actual chain
        // release/steering later in the same FlyAI frame.
        escapeFrom = source?.mainBodyChunk.pos ??
                     fly.mainBodyChunk.pos - Vector2.up * 20f;
        retreat = Mathf.Max(retreat, DB_Tuning.ApproachRetreatTicks);
    }

    internal void CancelPhysicalAttack()
    {
        if (Target != null || combat.HasSlot) CancelAttack();
        combat.ClearRetaliation();
    }

    internal bool ExecuteInjuryRecoveryOwned() => injuryRecovery.ExecuteOwned();

    internal void SetRecoveryRoostClaim(Vector2 spot)
    {
        roost = spot;
        hasRoost = true;
    }

    internal void ClearRoostClaim() => hasRoost = false;

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
        perception.ClearPursuit();
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
                injuryRecovery.ClearLocalTarget();
                fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "unavailable / restraint / shortcut");
            }
            CancelAttack();
            return;
        }

        perception.UpdateScan();

        bool recoveryBurrow = fly.AI.behavior == FlyAI.Behavior.Burrow &&
                              fly.Injury.RecoveryState == DB_InjuryRecoveryState.Hive;
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
                injuryRecovery.ClearLocalTarget();
                fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "native special behavior owns frame");
            }
            CancelAttack();
            return;
        }

        if (perception.Danger != null && retreat <= 0)
        {
            escapeFrom = perception.Danger.mainBodyChunk.pos;
            retreat = Mathf.Max(retreat, DB_Tuning.ApproachRetreatTicks);
        }

        if (perception.Danger != null || retreat > 0)
        {
            recoveryRoostTarget = null;
            recoverySearchCooldown = 0;
            fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "danger / escape");
            hasRoost = false;
            combat.ClearAttackState();
            combat.ClearTarget();
            SetMode(Activity.Escape);
            if (perception.Danger != null) escapeFrom = perception.Danger.mainBodyChunk.pos;
            return;
        }

        if (recoveryBurrow)
        {
            if (!fly.Injury.IsSeverelyInjured)
            {
                injuryRecovery.ClearNavigation();
                fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "recovered below severe threshold while entering hive");
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
        if (perception.Danger == null && retreat <= 0 && Mode != Activity.Escape)
            return false;

        injuryRecovery.ClearNavigation();
        fly.Injury.SetRecovery(DB_InjuryRecoveryState.None, null, "R3 PrimaryOwner=ImmediateDanger");
        hasRoost = false;
        combat.ClearAttackState();
        combat.ClearTarget();
        SetMode(Activity.Escape);
        if (perception.Danger != null) escapeFrom = perception.Danger.mainBodyChunk.pos;

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

    internal bool Valid(Creature creature) => perception.Valid(creature);

    internal bool IsRememberedPlayer(Player player)
    {
        DB_State state = fly.DesertState;
        return player != null && state.GrabMemoryTicks > 0 &&
            state.GrabMemoryStrength > 0f &&
            state.GrabMemoryPlayer == PlayerNumber(player);
    }

    internal bool IsTraumatizedPlayer(Player player)
    {
        return PlayerTraumaStrength(player) >=
               DB_Tuning.TraumaAggressionBlock;
    }

    private float PlayerTraumaStrength(Player player)
    {
        if (player == null) return 0f;
        DB_State state = fly.DesertState;
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
        if (!perception.IsScanFrame || Random.value >
            fly.Personality.RoostChance * DB_EnvironmentalPolicy.RoostChanceScale(fly) *
            DB_SocialBond.RoostScale(fly) * fly.Injury.RoostScale)
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
        return DB_RoostPolicy.TryGetSpot(fly, tile, out spot);
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
