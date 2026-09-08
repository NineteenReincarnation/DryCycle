using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R4 formal combat execution owner. Target discovery/motivation is migrated separately in B4;
/// this runtime owns combat phase progression, AttackSlot participation, contact state and
/// Attach/Interfere special physics. Ordinary approach/dive flight still goes through
/// DB_FlightMotor; contact pinning/impulses are explicit special physics.
/// </summary>
internal sealed class DB_CombatRuntime
{
    private readonly DesertBatflyAI ai;
    private readonly DesertBatfly fly;

    private Creature target;
    private Creature attacker;
    private int memory;
    private int retaliationCharges;
    private int retaliationRecovery;
    private Creature scanCandidate;
    private Player rememberedCandidate;
    private float closestCandidate = float.MaxValue;

    private int ticks;
    private int unseen;
    private int interest;
    private bool hasSlot;
    private float drainedWater;
    private Vector2 attachOffset;
    private Vector2 retaliationDirection;
    private BodyChunk attachedChunk;

    internal DB_CombatRuntime(DesertBatflyAI ai, DesertBatfly fly)
    {
        this.ai = ai;
        this.fly = fly;
    }

    internal enum SelectionResult
    {
        Ready,
        NoTarget,
        Cooldown
    }

    internal Creature Target => target;
    internal Creature Attacker => attacker;
    internal int Memory => memory;
    internal int RetaliationCharges => retaliationCharges;
    internal int RetaliationRecovery => retaliationRecovery;
    internal bool HasSlot => hasSlot;
    internal int InterestTicks => interest;
    internal int UnseenTicks => unseen;
    internal int PhaseTicks => ticks;
    internal bool PullingUp => ai.Mode == DesertBatflyAI.Activity.FakeDive &&
                               ticks > DB_Tuning.FakeDivePullUpTicks;
    internal bool FormalAttack => hasSlot && ai.Mode is
        DesertBatflyAI.Activity.Approach or DesertBatflyAI.Activity.Circle or
        DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
        DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere;

    internal void Reset()
    {
        target = null;
        attacker = null;
        memory = 0;
        retaliationCharges = 0;
        retaliationRecovery = 0;
        scanCandidate = null;
        rememberedCandidate = null;
        closestCandidate = float.MaxValue;
        ticks = 0;
        unseen = 0;
        interest = 0;
        hasSlot = false;
        drainedWater = 0f;
        attachOffset = Vector2.zero;
        retaliationDirection = Vector2.zero;
        attachedChunk = null;
    }

    internal void OnModeChanged(DesertBatflyAI.Activity next)
    {
        ticks = 0;
        if (next is not (DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.Interfere))
        {
            attachedChunk = null;
            attachOffset = Vector2.zero;
        }
        if (next is not (DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
            DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
            DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.RetaliationCharge or
            DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.Interfere))
        {
            hasSlot = false;
            unseen = 0;
            interest = 0;
            drainedWater = 0f;
        }
    }

    internal void ClearAttackState()
    {
        hasSlot = false;
        attachedChunk = null;
        attachOffset = Vector2.zero;
        retaliationDirection = Vector2.zero;
        drainedWater = 0f;
        interest = 0;
        unseen = 0;
    }

    internal void ClearVisibilityTracking() => unseen = 0;

    internal void TickMemory()
    {
        if (memory > 0 && --memory == 0) attacker = null;
        if (retaliationRecovery > 0) retaliationRecovery--;
    }

    internal void RecordAttacker(Creature source, float retaliationStrength = 0f)
    {
        if (source == null || source == fly || source is DesertBatfly) return;
        attacker = source;
        memory = Mathf.Max(memory, DB_Tuning.AttackerMemory);
        if (retaliationStrength > 0f && source is Player player)
            ArmRetaliation(player, retaliationStrength);
    }

    internal void RecordGrabber(Player player)
    {
        if (player == null) return;
        attacker = player;
        memory = Mathf.Max(memory, DB_Tuning.AttackerMemory);
    }

    internal void ArmRetaliation(Player player, float strength)
    {
        if (fly.Injury.BlocksCombat || !DB_EnvironmentalPolicy.AggressionAuthorized(fly) || player == null ||
            ai.IsTraumatizedPlayer(player))
            return;

        float drive = fly.Personality.AggressionDrive;
        float secondPassChance = Mathf.Clamp01((drive - 0.62f) / 0.38f) *
            Mathf.Lerp(0.25f, 0.65f, Mathf.Clamp01(strength));
        int passes = Random.value < secondPassChance ? 2 : 1;
        retaliationCharges = Mathf.Max(retaliationCharges, passes);
        retaliationRecovery = 0;
    }

    internal void ClearRetaliation()
    {
        retaliationCharges = 0;
        retaliationRecovery = 0;
    }

    internal void ClearMemoryAndRetaliation()
    {
        attacker = null;
        memory = 0;
        ClearRetaliation();
    }

    internal void SuppressHostility(Creature source)
    {
        if (source == null) return;
        if (target == source)
        {
            ClearAttackState();
            target = null;
            if (ai.Mode is DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere)
                ai.SetMode(DesertBatflyAI.Activity.Flight);
        }
        if (attacker == source)
        {
            attacker = null;
            memory = 0;
            ClearRetaliation();
        }
        ClearVisibilityTracking();
    }

    internal void SetTarget(Creature value) => target = value;

    internal void ClearTarget()
    {
        target = null;
        scanCandidate = null;
        rememberedCandidate = null;
    }

    internal void BeginCandidateScan()
    {
        scanCandidate = null;
        rememberedCandidate = null;
        closestCandidate = DB_Tuning.SightRange;
    }

    internal void ConsiderCandidate(Creature creature, float distance)
    {
        if (!ai.Valid(creature)) return;
        if (creature is Player player && DB_EnvironmentalPolicy.AggressionAuthorized(fly) &&
            ai.IsRememberedPlayer(player) && !ai.IsTraumatizedPlayer(player))
            rememberedCandidate = player;

        if (distance < closestCandidate && CanHarass(creature))
        {
            closestCandidate = distance;
            scanCandidate = creature;
        }
    }

    internal void CompleteCandidateScan(bool retreatActive)
    {
        if (target != null || !DB_EnvironmentalPolicy.AggressionAuthorized(fly) || retreatActive) return;

        bool retaliationPending = retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && !retaliationPending) return;
        if (!GriefAllowsHarass()) return;

        Player socialCandidate = FindSocialHarassTarget();
        float observeThreshold = Mathf.Lerp(
            DB_Tuning.ObserveThirst,
            0.18f,
            fly.Personality.AggressionDrive * 0.45f);
        float socialMotivationScale = socialCandidate != null
            ? Mathf.Lerp(1f, 0.72f, fly.Personality.Conformity)
            : 1f;
        float combatMotivation = DB_EnvironmentalPolicy.CombatMotivation(fly);
        bool motivated = combatMotivation > observeThreshold * socialMotivationScale ||
                         memory > 0 || rememberedCandidate != null;
        if (!motivated) return;

        if (ai.Valid(attacker) && CanHarass(attacker))
            target = attacker;
        else if (rememberedCandidate != null)
            target = rememberedCandidate;
        else if (socialCandidate != null)
            target = socialCandidate;
        else
            target = scanCandidate;

        if (target != null)
            ai.SetMode(DesertBatflyAI.Activity.Observe);
    }

    internal SelectionResult PrepareSelection()
    {
        bool retaliationReady = DB_EnvironmentalPolicy.AggressionAuthorized(fly) &&
            retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && ai.Mode != DesertBatflyAI.Activity.Attach &&
            ai.Mode != DesertBatflyAI.Activity.Interfere && !retaliationReady)
        {
            ClearAttackState();
            target = null;
            ai.SetMode(DesertBatflyAI.Activity.Cooldown);
            return SelectionResult.Cooldown;
        }

        if (!DB_EnvironmentalPolicy.AggressionAuthorized(fly) || !GriefAllowsHarass())
        {
            if (ai.Mode != DesertBatflyAI.Activity.Roost)
            {
                ClearAttackState();
                target = null;
                ai.SetMode(DesertBatflyAI.Activity.Flight);
            }
            return SelectionResult.NoTarget;
        }

        if (!ai.Valid(target))
        {
            ClearAttackState();
            target = null;
            ai.SetMode(DesertBatflyAI.Activity.Flight);
            if (memory > 0 && ai.Valid(attacker) && CanHarass(attacker))
                target = attacker;
            if (target == null)
                return SelectionResult.NoTarget;
            ai.SetMode(DesertBatflyAI.Activity.Observe);
        }

        if (ai.Mode is DesertBatflyAI.Activity.Flight or DesertBatflyAI.Activity.Cooldown or
            DesertBatflyAI.Activity.Roost)
            ai.SetMode(DesertBatflyAI.Activity.Observe);
        return SelectionResult.Ready;
    }

    private bool GriefAllowsHarass()
    {
        float motivation = DB_EnvironmentalPolicy.CombatMotivation(fly);
        return !fly.Injury.BlocksCombat &&
            (fly.DesertState.GriefStrength <= 0f ||
             motivation * fly.DesertState.GriefAttackScale >= DB_Tuning.ObserveThirst) &&
            (fly.Injury.AggressionScale >= 0.99f ||
             motivation * fly.Injury.AggressionScale >= DB_Tuning.ObserveThirst);
    }

    private bool CanHarass(Creature creature)
    {
        if (creature == fly || creature is DesertBatfly || !ai.Valid(creature)) return false;
        if (!GriefAllowsHarass()) return false;
        if (creature is Player player)
            return !ai.IsTraumatizedPlayer(player) &&
                   DB_EnvironmentalPolicy.AllowsHarassCandidate(fly, player);

        CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
        CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
        bool legal = creature.TotalMass <= DB_Tuning.LightTargetMass &&
                     relation.type != CreatureTemplate.Relationship.Type.Afraid &&
                     reverse.type != CreatureTemplate.Relationship.Type.Eats &&
                     reverse.type != CreatureTemplate.Relationship.Type.Attacks;
        return legal && DB_EnvironmentalPolicy.AllowsHarassCandidate(fly, creature);
    }

    private Player FindSocialHarassTarget()
    {
        if (fly.room == null || fly.Injury.BlocksCombat ||
            DB_FearRuntime.HasActiveFearSuppression(fly) ||
            !DB_SignalRuntime.TryGetInfluence(fly, out DB_SignalInfluence influence) ||
            influence.HarassInterest < 0.20f)
            return null;

        Player target = influence.HarassTarget;
        if (target == null || target.dead || target.room != fly.room || !CanHarass(target) ||
            !DB_VisibilityPolicy.CanObserve(
                fly, target.mainBodyChunk.pos, DB_Tuning.SightRange, DB_VisibilityChannel.Player))
            return null;

        if (DesertBatflyThreatRuntime.TryGetDebugState(fly, out DesertBatflyThreatDebugState threat))
        {
            float caution = threat.CounterKillPressure * 0.55f +
                            threat.PiercingPressure * 0.30f +
                            threat.GrabCapturePressure * 0.15f;
            float courage = fly.Personality.Nerve * 0.55f +
                            fly.Personality.Temperament * 0.45f;
            if (caution * threat.Confidence > courage + 0.18f)
                return null;
        }

        return target;
    }

    internal bool TryExecuteOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat) ||
            fly.room == null || fly.dead || !fly.Consious || ai.RestrainedByNonFly() ||
            fly.inShortcut || fly.Injury.BlocksCombat || !ai.Valid(target) ||
            ai.Mode is not (DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere))
            return false;

        ticks++;
        DB_VisibilityChannel targetChannel = target is Player
            ? DB_VisibilityChannel.Player
            : DB_VisibilityChannel.Creature;
        if (!DB_VisibilityPolicy.CanObserve(fly, target.mainBodyChunk.pos, 430f, targetChannel))
            unseen++;
        else
            unseen = 0;

        if (++interest > DB_Tuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(fly.mainBodyChunk.pos, target.mainBodyChunk.pos, 430f))
        {
            Finish(false);
            return true;
        }

        Vector2 center = target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        switch (ai.Mode)
        {
            case DesertBatflyAI.Activity.Observe:
                ai.SteerOwned(center + Orbit(150f, 90f), 4.5f, DB_BehaviorOwner.Combat);
                if (ticks > fly.Personality.ObserveDuration)
                {
                    bool counter = target == attacker && memory > 0;
                    bool grudge = target is Player targetPlayer &&
                                  ai.IsRememberedPlayer(targetPlayer) &&
                                  !ai.IsTraumatizedPlayer(targetPlayer);

                    if ((counter || grudge) && target is Player retaliationTarget &&
                        !ai.IsTraumatizedPlayer(retaliationTarget) &&
                        retaliationCharges > 0 && retaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge ? fly.DesertState.GrabMemoryStrength * 0.18f : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance * fly.Injury.AggressionScale + memoryBoost) &&
                            AcquireSlot())
                        {
                            retaliationCharges--;
                            retaliationDirection = Custom.DirVec(
                                fly.mainBodyChunk.pos,
                                retaliationTarget.mainBodyChunk.pos);
                            ai.SetMode(DesertBatflyAI.Activity.RetaliationCharge);
                            break;
                        }
                    }

                    float effectiveAttackThirst = Mathf.Lerp(
                        DB_Tuning.AttackThirst,
                        DB_Tuning.ObserveThirst,
                        fly.Personality.AggressionDrive * 0.35f);
                    float combatMotivation = DB_EnvironmentalPolicy.CombatMotivation(fly);
                    bool thirsty = combatMotivation * fly.DesertState.GriefAttackScale *
                                   fly.Injury.AggressionScale > effectiveAttackThirst;
                    bool revengeDrink = grudge && fly.DesertState.GrabMemoryStrength > 0.12f;
                    bool wantsRealAttack = thirsty || counter || revengeDrink;

                    float fakeChance = Mathf.Clamp01(fly.Personality.FakeDiveChance);
                    if (grudge)
                        fakeChance *= Mathf.Lerp(0.8f, 0.48f, fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;
                    if (target is Player learnedTarget)
                        fakeChance = DB_ThreatTactics.AdjustFakeDiveChance(
                            fly, learnedTarget, fakeChance);

                    if (!wantsRealAttack || Random.value < fakeChance)
                        ai.SetMode(DesertBatflyAI.Activity.FakeDive);
                    else if (AcquireSlot())
                        ai.SetMode(DesertBatflyAI.Activity.Approach);
                    else
                        ticks = fly.Personality.ObserveDuration / 2;
                }
                break;

            case DesertBatflyAI.Activity.Approach:
                ai.SteerOwned(
                    center + Vector2.up * 100f,
                    6f + fly.Personality.AggressionDrive * 1.2f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DB_Tuning.ApproachTicks || distance < 110f)
                    ai.SetMode(DesertBatflyAI.Activity.Circle);
                break;

            case DesertBatflyAI.Activity.Circle:
                ai.SteerOwned(
                    center + Orbit(95f, 65f),
                    6.5f + fly.Personality.AggressionDrive,
                    DB_BehaviorOwner.Combat);
                if (ticks > DB_Tuning.CircleTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Dive);
                break;

            case DesertBatflyAI.Activity.FakeDive:
                if (distance < 52f || ticks > DB_Tuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(DB_Tuning.FakeDivePullUpTicks + 1, ticks);
                ai.SteerOwned(
                    PullingUp
                        ? center + Vector2.up * 160f + Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DB_Tuning.FakeDiveTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Observe);
                break;

            case DesertBatflyAI.Activity.Dive:
                ai.SteerOwned(
                    center + target.mainBodyChunk.vel * 1.5f,
                    12f + fly.Personality.AggressionDrive * 1.5f,
                    DB_BehaviorOwner.Combat);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(contact.pos, fly.mainBodyChunk.pos) *
                        (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    ai.SetMode(DesertBatflyAI.Activity.Attach);
                }
                else if (ticks > DB_Tuning.DiveTicks)
                    Finish(false);
                break;

            case DesertBatflyAI.Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DB_Tuning.AttachTicks)
                    Finish(drainedWater > 0.001f);
                break;

            case DesertBatflyAI.Activity.RetaliationCharge:
                if (target is not Player chargeTarget || ai.IsTraumatizedPlayer(chargeTarget))
                {
                    FinishRetaliation(false);
                    break;
                }

                Vector2 predicted = chargeTarget.mainBodyChunk.pos + chargeTarget.mainBodyChunk.vel * 1.15f;
                ai.SteerOwned(predicted, fly.Personality.RetaliationSpeed, DB_BehaviorOwner.Combat);
                BodyChunk retaliationContact = FindContact();
                if (retaliationContact != null && unseen == 0)
                {
                    attachedChunk = retaliationContact;
                    retaliationDirection = fly.mainBodyChunk.vel.sqrMagnitude > 0.5f
                        ? fly.mainBodyChunk.vel.normalized
                        : Custom.DirVec(fly.mainBodyChunk.pos, retaliationContact.pos);
                    attachOffset = Custom.DirVec(retaliationContact.pos, fly.mainBodyChunk.pos) *
                        (retaliationContact.rad + fly.mainBodyChunk.rad * 0.45f);
                    ApplyInitialRetaliationImpact(chargeTarget);
                    ai.SetMode(DesertBatflyAI.Activity.Interfere);
                }
                else if (ticks > DB_Tuning.RetaliationChargeTicks)
                    FinishRetaliation(false);
                break;

            case DesertBatflyAI.Activity.Interfere:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= fly.Personality.RetaliationContactDuration)
                    FinishRetaliation(true);
                break;
        }
        return true;
    }

    internal void AfterPhysics(bool eu)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;

        if (ai.Mode == DesertBatflyAI.Activity.Interfere)
        {
            UpdateInterference(eu);
            return;
        }

        if (ai.Mode != DesertBatflyAI.Activity.Attach) return;
        if (!ai.Valid(target) || attachedChunk == null || !fly.Consious ||
            ai.RestrainedByNonFly() || fly.inShortcut || target.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 70f))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        if (target is Player attachedPlayer && ai.IsTraumatizedPlayer(attachedPlayer))
        {
            ai.SuppressHostility(attachedPlayer);
            ai.BeginCombatEscape(attachedPlayer.mainBodyChunk.pos, 80);
            return;
        }

        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid || !fly.room.VisualContact(fly.mainBodyChunk.pos, position))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        if (ticks >= DB_Tuning.DrainStartTicks && ticks <= DB_Tuning.DrainEndTicks)
        {
            float amount = DB_Tuning.AttackWaterPerSecond / ThirstConstants.SimulationTicksPerSecond;
            bool transferred = true;
            if (target is Player player)
            {
                transferred = ThirstStore.RemoveRuntime(
                    player,
                    amount / ThirstConstants.WaterValuePerPip);
                player.showKarmaFoodRainTime = Mathf.Max(
                    player.showKarmaFoodRainTime,
                    ThirstConstants.HydrationLossHudHoldFrames);
            }

            if (transferred)
            {
                drainedWater += amount;
                float fullWindowWater = DB_Tuning.AttackWaterPerSecond *
                    (DB_Tuning.DrainEndTicks - DB_Tuning.DrainStartTicks + 1f) /
                    ThirstConstants.SimulationTicksPerSecond;
                fly.DesertState.Thirst = Mathf.Max(
                    0f,
                    fly.DesertState.Thirst - DB_Tuning.DrainRelief *
                    (amount / Mathf.Max(0.001f, fullWindowWater)));
                fly.DesertState.Cooldown = DB_Tuning.Cooldown;
            }
        }
    }

    private BodyChunk FindContact()
    {
        if (target?.bodyChunks == null) return null;
        foreach (BodyChunk chunk in target.bodyChunks)
            if (Custom.DistLess(
                    chunk.pos,
                    fly.mainBodyChunk.pos,
                    chunk.rad + fly.mainBodyChunk.rad + 3f))
                return chunk;
        return null;
    }

    private void ApplyInitialRetaliationImpact(Player player)
    {
        if (player?.bodyChunks == null) return;
        Vector2 impulse = retaliationDirection * fly.Personality.RetaliationImpact;
        foreach (BodyChunk chunk in player.bodyChunks)
            chunk.vel += impulse;
    }

    private void UpdateInterference(bool eu)
    {
        if (target is not Player player || ai.IsTraumatizedPlayer(player) ||
            !ai.Valid(player) || attachedChunk == null || !fly.Consious ||
            ai.RestrainedByNonFly() || fly.inShortcut || player.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 75f))
        {
            FinishRetaliation(false);
            return;
        }

        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid)
        {
            FinishRetaliation(false);
            return;
        }

        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        float drag = fly.Personality.RetaliationDrag;
        Vector2 push = retaliationDirection * fly.Personality.RetaliationPush;
        foreach (BodyChunk chunk in player.bodyChunks)
        {
            chunk.vel.x *= 1f - drag;
            chunk.vel.y *= 1f - drag * 0.22f;
            chunk.vel += push;
        }
    }

    private void FinishRetaliation(bool success)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;
        Vector2 from = target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ClearTarget();
        fly.DesertState.Cooldown = Mathf.Max(fly.DesertState.Cooldown, DB_Tuning.RetaliationCooldown);
        retaliationRecovery = success ? 120 : 75;
        ai.BeginCombatEscape(from, success ? 55 : 40);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5.5f + Vector2.up * 2.5f;
    }

    private void Finish(bool success)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;
        Vector2 from = target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ClearTarget();
        fly.DesertState.Cooldown = Mathf.Max(
            fly.DesertState.Cooldown,
            success ? DB_Tuning.Cooldown : DB_Tuning.FailedCooldown);
        ai.BeginCombatEscape(from, 75);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5f + Vector2.up * 3f;
    }

    private bool AcquireSlot()
    {
        if (fly.Injury.BlocksCombat) { hasSlot = false; return false; }
        if (target is Player player && ai.IsTraumatizedPlayer(player))
        {
            hasSlot = false;
            return false;
        }

        int count = 0;
        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var bats = context?.Bats;
        if (bats != null)
        {
            for (int i = 0; i < bats.Count; i++)
            {
                DesertBatfly other = bats[i];
                if (other == null || other == fly || !other.Consious ||
                    other.grabbedBy.Count != 0 || other.DesertAI.Target != target ||
                    !other.DesertAI.FormalAttack)
                    continue;
                count++;
            }
        }

        hasSlot = count < DB_Tuning.AttackSlots;
        return hasSlot;
    }

    private Vector2 Orbit(float width, float height)
    {
        float angle = (fly.room.game.clock + (fly.Personality.VisualSeed & 1023)) * 0.025f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            55f + Mathf.Sin(angle) * height * 0.45f);
    }
}
