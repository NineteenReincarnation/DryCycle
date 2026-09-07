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

    internal bool HasSlot => hasSlot;
    internal bool PullingUp => ai.Mode == DesertBatflyAI.Activity.FakeDive &&
                               ticks > DesertBatflyTuning.FakeDivePullUpTicks;
    internal bool FormalAttack => hasSlot && ai.Mode is
        DesertBatflyAI.Activity.Approach or DesertBatflyAI.Activity.Circle or
        DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
        DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere;

    internal void Reset()
    {
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

    internal bool TryExecuteOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat) ||
            fly.room == null || fly.dead || !fly.Consious || ai.RestrainedByNonFly() ||
            fly.inShortcut || fly.Injury.BlocksCombat || !ai.Valid(ai.Target) ||
            ai.Mode is not (DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere))
            return false;

        ticks++;
        DB_VisibilityChannel targetChannel = ai.Target is Player
            ? DB_VisibilityChannel.Player
            : DB_VisibilityChannel.Creature;
        if (!DB_VisibilityPolicy.CanObserve(fly, ai.Target.mainBodyChunk.pos, 430f, targetChannel))
            unseen++;
        else
            unseen = 0;

        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(fly.mainBodyChunk.pos, ai.Target.mainBodyChunk.pos, 430f))
        {
            Finish(false);
            return true;
        }

        Vector2 center = ai.Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        switch (ai.Mode)
        {
            case DesertBatflyAI.Activity.Observe:
                ai.SteerOwned(center + Orbit(150f, 90f), 4.5f, DB_BehaviorOwner.Combat);
                if (ticks > fly.Personality.ObserveDuration)
                {
                    bool counter = ai.Target == ai.CombatAttacker && ai.CombatMemory > 0;
                    bool grudge = ai.Target is Player targetPlayer &&
                                  ai.IsRememberedPlayer(targetPlayer) &&
                                  !ai.IsTraumatizedPlayer(targetPlayer);

                    if ((counter || grudge) && ai.Target is Player retaliationTarget &&
                        !ai.IsTraumatizedPlayer(retaliationTarget) &&
                        ai.CombatRetaliationCharges > 0 && ai.CombatRetaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge ? fly.DesertState.GrabMemoryStrength * 0.18f : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance * fly.Injury.AggressionScale + memoryBoost) &&
                            AcquireSlot())
                        {
                            ai.CombatRetaliationCharges--;
                            retaliationDirection = Custom.DirVec(
                                fly.mainBodyChunk.pos,
                                retaliationTarget.mainBodyChunk.pos);
                            ai.SetMode(DesertBatflyAI.Activity.RetaliationCharge);
                            break;
                        }
                    }

                    float effectiveAttackThirst = Mathf.Lerp(
                        DesertBatflyTuning.AttackThirst,
                        DesertBatflyTuning.ObserveThirst,
                        fly.Personality.AggressionDrive * 0.35f);
                    bool thirsty = fly.DesertState.Thirst * fly.DesertState.GriefAttackScale *
                                   fly.Injury.AggressionScale > effectiveAttackThirst;
                    bool revengeDrink = grudge && fly.DesertState.GrabMemoryStrength > 0.12f;
                    bool wantsRealAttack = thirsty || counter || revengeDrink;

                    float fakeChance = Mathf.Clamp01(fly.Personality.FakeDiveChance);
                    if (grudge)
                        fakeChance *= Mathf.Lerp(0.8f, 0.48f, fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;
                    if (ai.Target is Player learnedTarget)
                        fakeChance = DesertBatflyThreatTactics.AdjustFakeDiveChance(
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
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f)
                    ai.SetMode(DesertBatflyAI.Activity.Circle);
                break;

            case DesertBatflyAI.Activity.Circle:
                ai.SteerOwned(
                    center + Orbit(95f, 65f),
                    6.5f + fly.Personality.AggressionDrive,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.CircleTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Dive);
                break;

            case DesertBatflyAI.Activity.FakeDive:
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(DesertBatflyTuning.FakeDivePullUpTicks + 1, ticks);
                ai.SteerOwned(
                    PullingUp
                        ? center + Vector2.up * 160f + Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.FakeDiveTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Observe);
                break;

            case DesertBatflyAI.Activity.Dive:
                ai.SteerOwned(
                    center + ai.Target.mainBodyChunk.vel * 1.5f,
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
                else if (ticks > DesertBatflyTuning.DiveTicks)
                    Finish(false);
                break;

            case DesertBatflyAI.Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks)
                    Finish(drainedWater > 0.001f);
                break;

            case DesertBatflyAI.Activity.RetaliationCharge:
                if (ai.Target is not Player chargeTarget || ai.IsTraumatizedPlayer(chargeTarget))
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
                else if (ticks > DesertBatflyTuning.RetaliationChargeTicks)
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
        if (!ai.Valid(ai.Target) || attachedChunk == null || !fly.Consious ||
            ai.RestrainedByNonFly() || fly.inShortcut || ai.Target.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 70f))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        if (ai.Target is Player attachedPlayer && ai.IsTraumatizedPlayer(attachedPlayer))
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

        if (ticks >= DesertBatflyTuning.DrainStartTicks && ticks <= DesertBatflyTuning.DrainEndTicks)
        {
            float amount = DesertBatflyTuning.AttackWaterPerSecond / ThirstConstants.SimulationTicksPerSecond;
            bool transferred = true;
            if (ai.Target is Player player)
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
                float fullWindowWater = DesertBatflyTuning.AttackWaterPerSecond *
                    (DesertBatflyTuning.DrainEndTicks - DesertBatflyTuning.DrainStartTicks + 1f) /
                    ThirstConstants.SimulationTicksPerSecond;
                fly.DesertState.Thirst = Mathf.Max(
                    0f,
                    fly.DesertState.Thirst - DesertBatflyTuning.DrainRelief *
                    (amount / Mathf.Max(0.001f, fullWindowWater)));
                fly.DesertState.Cooldown = DesertBatflyTuning.Cooldown;
            }
        }
    }

    private BodyChunk FindContact()
    {
        if (ai.Target?.bodyChunks == null) return null;
        foreach (BodyChunk chunk in ai.Target.bodyChunks)
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
        if (ai.Target is not Player player || ai.IsTraumatizedPlayer(player) ||
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
        Vector2 from = ai.Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ai.ClearCombatTarget();
        fly.DesertState.Cooldown = Mathf.Max(fly.DesertState.Cooldown, DesertBatflyTuning.RetaliationCooldown);
        ai.CombatRetaliationRecovery = success ? 120 : 75;
        ai.BeginCombatEscape(from, success ? 55 : 40);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5.5f + Vector2.up * 2.5f;
    }

    private void Finish(bool success)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;
        Vector2 from = ai.Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ai.ClearCombatTarget();
        fly.DesertState.Cooldown = Mathf.Max(
            fly.DesertState.Cooldown,
            success ? DesertBatflyTuning.Cooldown : DesertBatflyTuning.FailedCooldown);
        ai.BeginCombatEscape(from, 75);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5f + Vector2.up * 3f;
    }

    private bool AcquireSlot()
    {
        if (fly.Injury.BlocksCombat) { hasSlot = false; return false; }
        if (ai.Target is Player player && ai.IsTraumatizedPlayer(player))
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
                    other.grabbedBy.Count != 0 || other.DesertAI.Target != ai.Target ||
                    !other.DesertAI.FormalAttack)
                    continue;
                count++;
            }
        }

        hasSlot = count < DesertBatflyTuning.AttackSlots;
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
