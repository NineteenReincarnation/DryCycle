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
        Roost
    }

    private readonly DesertBatfly fly;
    internal readonly DesertBatflySocialRoles Roles;
    internal bool HasImmediateDanger => danger != null || retreat > 0 || Mode == Activity.Escape;
    internal Activity Mode { get; private set; }
    internal Creature Target { get; private set; }

    private Creature attacker;
    private Creature danger;
    private int memory, retreat, ticks, scan, pursuit, unseen, interest;
    private int retaliationCharges, retaliationRecovery;
    private bool hasSlot, hasRoost;
    private float drainedWater;
    private Vector2 escapeFrom, attachOffset, roost, retaliationDirection;
    private BodyChunk attachedChunk;

    internal bool PullingUp => Mode == Activity.FakeDive &&
                               ticks > DesertBatflyTuning.FakeDivePullUpTicks;
    internal bool FormalAttack => hasSlot && Mode is
        Activity.Approach or Activity.Circle or Activity.Dive or Activity.Attach or
        Activity.RetaliationCharge or Activity.Interfere;

    internal DesertBatflyAI(DesertBatfly fly)
    {
        this.fly = fly;
        Roles = new DesertBatflySocialRoles(fly);
    }

    internal void TickMemory()
    {
        if (memory > 0 && --memory == 0) attacker = null;
        if (retaliationRecovery > 0) retaliationRecovery--;

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

    private bool RestrainedByNonFly()
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
        Roles.Reset();
        if (Mode == Activity.Roost) StopRoost(false);
        CancelAttack();
        attacker = danger = null;
        memory = retreat = pursuit = unseen = 0;
        retaliationCharges = retaliationRecovery = 0;
        hasRoost = false;
    }

    internal void Threatened(Creature source, bool directAttack = false)
    {
        if (source != null && source != fly && source is not DesertBatfly)
        {
            attacker = source;
            memory = DesertBatflyTuning.AttackerMemory;
            escapeFrom = source.mainBodyChunk.pos;
            if (directAttack && source is Player player && fly.Personality.Aggressive &&
                !IsTraumatizedPlayer(player))
            {
                ArmRetaliation(player, 1f);
            }
        }
        else
        {
            escapeFrom = fly.mainBodyChunk.pos - Vector2.up * 20f;
        }

        if (IsInFlyChain(fly))
        {
            BreakHangChain(source, DesertBatflyTuning.RetreatTicks);
        }
        else
        {
            retreat = DesertBatflyTuning.RetreatTicks;
            CancelAttack();
            SetMode(Activity.Escape);
        }

        Roles.CheckSuppression();
        RaiseLocalAlarm();
    }

    internal void PlayerGrabbed(Player player)
    {
        if (player == null) return;

        RememberGrabber(player, DesertBatflyTuning.GrabMemoryGain);
        attacker = player;
        memory = Mathf.Max(memory, DesertBatflyTuning.AttackerMemory);
        escapeFrom = player.mainBodyChunk.pos;

        if (IsInFlyChain(fly))
        {
            BreakHangChain(player, DesertBatflyTuning.RetreatTicks);
        }
        else
        {
            retreat = Mathf.Max(retreat, DesertBatflyTuning.RetreatTicks);
            CancelAttack();
            SetMode(Activity.Escape);
        }

        Roles.CheckSuppression();
        RaiseLocalAlarm();
    }

    internal void PlayerReleased(Player player, float releaseSpeed)
    {
        if (player == null || fly.dead) return;

        bool thrown = releaseSpeed >= DesertBatflyTuning.GrabThrowSpeed;
        if (thrown)
            RememberGrabber(player, DesertBatflyTuning.GrabThrowBonus);

        attacker = player;
        memory = Mathf.Max(memory, DesertBatflyTuning.AttackerMemory);
        escapeFrom = player.mainBodyChunk.pos;

        float trauma = PlayerTraumaStrength(player);
        bool traumaBlocksAggression = trauma >= DesertBatflyTuning.TraumaAggressionBlock;
        if (fly.Personality.Aggressive && !traumaBlocksAggression)
        {
            ArmRetaliation(
                player,
                fly.DesertState.GrabMemoryStrength + (thrown ? 0.25f : 0f));
            retreat = Mathf.Clamp(retreat, 35, 60);
        }
        else
        {
            // Severe trauma changes the meaning of a repeat grab even for a vicious bat:
            // it is treated as another danger signal, not an invitation to revenge.
            float fear = Mathf.Max(fly.DesertState.GrabMemoryStrength, trauma) *
                         Mathf.Lerp(1.15f, 0.8f, fly.Personality.Nerve);
            retreat = Mathf.Max(
                retreat,
                Mathf.RoundToInt(Mathf.Lerp(90f, 210f, Mathf.Clamp01(fear))));
            retaliationCharges = 0;
            if (traumaBlocksAggression)
            {
                attacker = null;
                memory = 0;
            }
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

    private void ArmRetaliation(Player player, float strength)
    {
        if (!fly.Personality.Aggressive || player == null || IsTraumatizedPlayer(player))
            return;

        float drive = fly.Personality.AggressionDrive;
        float secondPassChance = Mathf.Clamp01((drive - 0.62f) / 0.38f) *
            Mathf.Lerp(0.25f, 0.65f, Mathf.Clamp01(strength));
        int passes = Random.value < secondPassChance ? 2 : 1;
        retaliationCharges = Mathf.Max(retaliationCharges, passes);
        retaliationRecovery = 0;
    }

    private void RaiseLocalAlarm()
    {
        if (fly.room == null) return;
        foreach (Fly other in DesertSwarmRoom.For(fly.room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat == fly || bat.dead || bat.slatedForDeletetion ||
                bat.room != fly.room || bat.inShortcut ||
                !Custom.DistLess(
                    fly.mainBodyChunk.pos,
                    bat.mainBodyChunk.pos,
                    DesertBatflyTuning.AlarmRadius))
                continue;

            bat.DesertAI.escapeFrom = escapeFrom;
            bat.DesertAI.retreat = Mathf.Max(bat.DesertAI.retreat, 25);
            bat.DesertAI.Roles.CheckSuppression();
        }
    }

    private void DisturbedByApproach(Creature source)
    {
        escapeFrom = source?.mainBodyChunk.pos ??
                     fly.mainBodyChunk.pos - Vector2.up * 20f;

        if (IsInFlyChain(fly))
        {
            BreakHangChain(source, DesertBatflyTuning.ApproachRetreatTicks);
            return;
        }

        retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
        CancelAttack();
        SetMode(Activity.Escape);
    }

    internal void CancelAttack()
    {
        hasSlot = false;
        attachedChunk = null;
        Target = null;
        drainedWater = 0f;
        interest = 0;
        SetMode(Activity.Flight);
    }

    internal void BeginGriefResponse()
    {
        Roles.CheckSuppression();
        CancelAttack();
        attacker = null;
        memory = 0;
        retaliationCharges = retaliationRecovery = 0;
    }

    internal void SuppressHostility(Creature source)
    {
        if (source == null) return;
        if (Target == source) CancelAttack();
        if (attacker == source)
        {
            attacker = null;
            memory = 0;
            retaliationCharges = 0;
            retaliationRecovery = 0;
        }
        pursuit = 0;
        unseen = 0;
    }

    private void SetMode(Activity next)
    {
        if (Mode == next) return;
        Mode = next;
        ticks = 0;
    }

    internal void Update()
    {
        if (fly.room == null) return;
        Roles.CheckSuppression();
        ticks++;

        if (fly.Emergence.Active || RestrainedByNonFly() ||
            !fly.Consious || fly.inShortcut)
        {
            if (Mode == Activity.Roost) StopRoost(true);
            CancelAttack();
            fly.movMode = Fly.MovementMode.Passive;
            return;
        }

        if (++scan >= 8)
        {
            scan = 0;
            ScanCreatures();
            ScanWeapons();
            Roles.CheckSuppression();
        }

        if (fly.AI.fleeFromRain || fly.AI.behavior == FlyAI.Behavior.Burrow ||
            fly.AI.luredCounter > 0 || fly.safariControlled)
        {
            if (Mode == Activity.Roost) StopRoost(true);
            CancelAttack();
            return;
        }

        if (danger != null && retreat <= 0)
            DisturbedByApproach(danger);

        if (danger != null || retreat > 0)
        {
            hasSlot = false;
            attachedChunk = null;
            Target = null;
            SetMode(Activity.Escape);
            if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
            Steer(
                fly.mainBodyChunk.pos +
                Custom.DirVec(escapeFrom, fly.mainBodyChunk.pos) * 160f +
                Vector2.up * 50f,
                8f);
            return;
        }

        if (Mode == Activity.Escape) SetMode(Activity.Flight);
        var flock = DesertSwarmRoom.For(fly.room).Flock;
        Roles.Evaluate(flock);
        Roles.BiasOrdinaryFlight(flock);

        bool retaliationReady = fly.Personality.Aggressive &&
            retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && Mode != Activity.Attach &&
            Mode != Activity.Interfere && !retaliationReady)
        {
            CancelAttack();
            SetMode(Activity.Cooldown);
            return;
        }

        if (!fly.Personality.Aggressive || !GriefAllowsHarass())
        {
            if (Mode != Activity.Roost) CancelAttack();
            UpdateRoost();
            return;
        }

        if (!Valid(Target))
        {
            CancelAttack();
            if (memory > 0 && Valid(attacker) && CanHarass(attacker))
                Target = attacker;
            if (Target == null)
            {
                UpdateRoost();
                return;
            }
            SetMode(Activity.Observe);
        }

        if (!fly.room.VisualContact(
                fly.mainBodyChunk.pos,
                Target.mainBodyChunk.pos))
            unseen++;
        else
            unseen = 0;

        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(
                fly.mainBodyChunk.pos,
                Target.mainBodyChunk.pos,
                430f))
        {
            Finish(false);
            return;
        }

        Vector2 center = Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost)
        {
            if (Mode == Activity.Roost) StopRoost(true);
            SetMode(Activity.Observe);
        }

        switch (Mode)
        {
            case Activity.Observe:
                Steer(center + Orbit(Roles.ObserveRadius, 90f), 4.5f);
                if (ticks > fly.Personality.ObserveDuration * Roles.ObserveDurationScale)
                {
                    bool counter = Target == attacker && memory > 0;
                    bool grudge = Target is Player targetPlayer &&
                                  IsRememberedPlayer(targetPlayer) &&
                                  !IsTraumatizedPlayer(targetPlayer);

                    if ((counter || grudge) && Target is Player retaliationTarget &&
                        !IsTraumatizedPlayer(retaliationTarget) &&
                        retaliationCharges > 0 && retaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge
                            ? fly.DesertState.GrabMemoryStrength * 0.18f
                            : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance + memoryBoost) &&
                            AcquireSlot())
                        {
                            retaliationCharges--;
                            retaliationDirection = Custom.DirVec(
                                fly.mainBodyChunk.pos,
                                retaliationTarget.mainBodyChunk.pos);
                            SetMode(Activity.RetaliationCharge);
                            break;
                        }
                    }

                    float effectiveAttackThirst = Mathf.Lerp(
                        DesertBatflyTuning.AttackThirst,
                        DesertBatflyTuning.ObserveThirst,
                        fly.Personality.AggressionDrive * 0.35f);
                    bool thirsty = fly.DesertState.Thirst * fly.DesertState.GriefAttackScale > effectiveAttackThirst;
                    bool revengeDrink = grudge &&
                                        fly.DesertState.GrabMemoryStrength > 0.12f;
                    bool wantsRealAttack = thirsty || counter || revengeDrink;

                    float fakeChance = Mathf.Clamp01(fly.Personality.FakeDiveChance + Roles.FakeDiveBonus);
                    if (grudge)
                        fakeChance *= Mathf.Lerp(
                            0.8f,
                            0.48f,
                            fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;

                    if (!wantsRealAttack || Random.value < fakeChance)
                        SetMode(Activity.FakeDive);
                    else if (AcquireSlot())
                        SetMode(Activity.Approach);
                    else
                        ticks = fly.Personality.ObserveDuration / 2;
                }
                break;

            case Activity.Approach:
                Steer(
                    center + Vector2.up * 100f,
                    6f + fly.Personality.AggressionDrive * 1.2f);
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f)
                    SetMode(Activity.Circle);
                break;

            case Activity.Circle:
                Steer(
                    center + Orbit(95f, 65f),
                    6.5f + fly.Personality.AggressionDrive);
                if (ticks > DesertBatflyTuning.CircleTicks)
                    SetMode(Activity.Dive);
                break;

            case Activity.FakeDive:
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(
                        DesertBatflyTuning.FakeDivePullUpTicks + 1,
                        ticks);
                Steer(
                    PullingUp
                        ? center + Vector2.up * 160f +
                          Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f);
                if (ticks > DesertBatflyTuning.FakeDiveTicks)
                    SetMode(Activity.Observe);
                break;

            case Activity.Dive:
                Steer(
                    center + Target.mainBodyChunk.vel * 1.5f,
                    12f + fly.Personality.AggressionDrive * 1.5f);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(
                            contact.pos,
                            fly.mainBodyChunk.pos) *
                        (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    SetMode(Activity.Attach);
                }
                else if (ticks > DesertBatflyTuning.DiveTicks)
                {
                    Finish(false);
                }
                break;

            case Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks)
                    Finish(drainedWater > 0.001f);
                break;

            case Activity.RetaliationCharge:
                if (Target is not Player chargeTarget ||
                    IsTraumatizedPlayer(chargeTarget))
                {
                    FinishRetaliation(false);
                    break;
                }

                Vector2 predicted = chargeTarget.mainBodyChunk.pos +
                                    chargeTarget.mainBodyChunk.vel * 1.15f;
                Steer(predicted, fly.Personality.RetaliationSpeed);
                BodyChunk retaliationContact = FindContact();
                if (retaliationContact != null && unseen == 0)
                {
                    attachedChunk = retaliationContact;
                    retaliationDirection = fly.mainBodyChunk.vel.sqrMagnitude > 0.5f
                        ? fly.mainBodyChunk.vel.normalized
                        : Custom.DirVec(
                            fly.mainBodyChunk.pos,
                            retaliationContact.pos);
                    attachOffset = Custom.DirVec(
                            retaliationContact.pos,
                            fly.mainBodyChunk.pos) *
                        (retaliationContact.rad + fly.mainBodyChunk.rad * 0.45f);
                    ApplyInitialRetaliationImpact(chargeTarget);
                    SetMode(Activity.Interfere);
                }
                else if (ticks > DesertBatflyTuning.RetaliationChargeTicks)
                {
                    FinishRetaliation(false);
                }
                break;

            case Activity.Interfere:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= fly.Personality.RetaliationContactDuration)
                    FinishRetaliation(true);
                break;
        }
    }

    private BodyChunk FindContact()
    {
        if (Target?.bodyChunks == null) return null;
        foreach (BodyChunk chunk in Target.bodyChunks)
        {
            if (Custom.DistLess(
                    chunk.pos,
                    fly.mainBodyChunk.pos,
                    chunk.rad + fly.mainBodyChunk.rad + 3f))
                return chunk;
        }
        return null;
    }

    internal void AfterPhysics(bool eu)
    {
        if (Mode == Activity.Interfere)
        {
            UpdateInterference(eu);
            return;
        }

        if (Mode != Activity.Attach) return;
        if (!Valid(Target) || attachedChunk == null || !fly.Consious ||
            RestrainedByNonFly() || fly.inShortcut || Target.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 70f))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        if (Target is Player attachedPlayer && IsTraumatizedPlayer(attachedPlayer))
        {
            SuppressHostility(attachedPlayer);
            escapeFrom = attachedPlayer.mainBodyChunk.pos;
            retreat = Mathf.Max(retreat, 80);
            SetMode(Activity.Escape);
            return;
        }

        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid ||
            !fly.room.VisualContact(fly.mainBodyChunk.pos, position))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        if (ticks >= DesertBatflyTuning.DrainStartTicks &&
            ticks <= DesertBatflyTuning.DrainEndTicks)
        {
            float amount = DesertBatflyTuning.AttackWaterPerSecond /
                           ThirstConstants.SimulationTicksPerSecond;
            bool transferred = true;

            if (Target is Player player)
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
                    (DesertBatflyTuning.DrainEndTicks -
                     DesertBatflyTuning.DrainStartTicks + 1f) /
                    ThirstConstants.SimulationTicksPerSecond;
                fly.DesertState.Thirst = Mathf.Max(
                    0f,
                    fly.DesertState.Thirst -
                    DesertBatflyTuning.DrainRelief *
                    (amount / Mathf.Max(0.001f, fullWindowWater)));
                fly.DesertState.Cooldown = DesertBatflyTuning.Cooldown;
            }
        }
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
        if (Target is not Player player || IsTraumatizedPlayer(player) ||
            !Valid(player) || attachedChunk == null || !fly.Consious ||
            RestrainedByNonFly() || fly.inShortcut || player.inShortcut ||
            !hasSlot ||
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
        Vector2 from = Target?.mainBodyChunk.pos ??
                       fly.mainBodyChunk.pos - Vector2.up;
        CancelAttack();
        fly.DesertState.Cooldown = Mathf.Max(
            fly.DesertState.Cooldown,
            DesertBatflyTuning.RetaliationCooldown);
        retaliationRecovery = success ? 120 : 75;
        escapeFrom = from;
        retreat = success ? 55 : 40;
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5.5f + Vector2.up * 2.5f;
        SetMode(Activity.Escape);
    }

    private void Finish(bool success)
    {
        Vector2 from = Target?.mainBodyChunk.pos ??
                       fly.mainBodyChunk.pos - Vector2.up;
        CancelAttack();
        fly.DesertState.Cooldown = Mathf.Max(
            fly.DesertState.Cooldown,
            success
                ? DesertBatflyTuning.Cooldown
                : DesertBatflyTuning.FailedCooldown);
        escapeFrom = from;
        retreat = 75;
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5f + Vector2.up * 3f;
        SetMode(Activity.Escape);
    }

    private bool AcquireSlot()
    {
        if (Target is Player player && IsTraumatizedPlayer(player))
        {
            hasSlot = false;
            return false;
        }

        int count = 0;
        foreach (AbstractCreature abstractCreature in fly.room.abstractRoom.creatures)
        {
            if (abstractCreature.realizedCreature is DesertBatfly other && other != fly &&
                other.room == fly.room && other.Consious &&
                other.grabbedBy.Count == 0 &&
                other.DesertAI.Target == Target && other.DesertAI.FormalAttack)
            {
                count++;
            }
        }

        hasSlot = count < DesertBatflyTuning.AttackSlots;
        return hasSlot;
    }

    private bool Valid(Creature creature)
    {
        return creature != null && !creature.dead &&
               !creature.slatedForDeletetion && creature.room == fly.room &&
               !creature.inShortcut && creature.grabbedBy.Count == 0 &&
               (creature.abstractCreature.rippleLayer == fly.abstractCreature.rippleLayer ||
                creature.abstractCreature.rippleBothSides ||
                fly.abstractCreature.rippleBothSides);
    }

    private bool GriefAllowsHarass() => fly.DesertState.GriefStrength <= 0f ||
        fly.DesertState.Thirst * fly.DesertState.GriefAttackScale >= DesertBatflyTuning.ObserveThirst;

    private bool CanHarass(Creature creature)
    {
        if (creature == fly || creature is DesertBatfly || !Valid(creature))
            return false;
        if (!GriefAllowsHarass()) return false;
        if (creature is Player player)
            return !IsTraumatizedPlayer(player);

        CreatureTemplate.Relationship relation =
            fly.Template.CreatureRelationship(creature.Template);
        CreatureTemplate.Relationship reverse =
            creature.Template.CreatureRelationship(fly.Template);
        return creature.TotalMass <= DesertBatflyTuning.LightTargetMass &&
               relation.type != CreatureTemplate.Relationship.Type.Afraid &&
               reverse.type != CreatureTemplate.Relationship.Type.Eats &&
               reverse.type != CreatureTemplate.Relationship.Type.Attacks;
    }

    private void ScanCreatures()
    {
        danger = null;
        Roles.BeginVisibleScan();
        Creature candidate = null;
        Player rememberedCandidate = null;
        float closest = DesertBatflyTuning.SightRange;

        foreach (AbstractCreature abs in fly.room.abstractRoom.creatures)
        {
            Creature creature = abs.realizedCreature;
            if (creature == fly || creature is DesertBatfly || !Valid(creature))
                continue;

            float distance = Vector2.Distance(
                fly.mainBodyChunk.pos,
                creature.mainBodyChunk.pos);
            if (distance > DesertBatflyTuning.SightRange ||
                !fly.room.VisualContact(
                    fly.mainBodyChunk.pos,
                    creature.mainBodyChunk.pos))
                continue;

            CreatureTemplate.Relationship relation =
                fly.Template.CreatureRelationship(creature.Template);
            CreatureTemplate.Relationship reverse =
                creature.Template.CreatureRelationship(fly.Template);
            bool predator = creature is not Player &&
                (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
                 reverse.type == CreatureTemplate.Relationship.Type.Eats ||
                 reverse.type == CreatureTemplate.Relationship.Type.Attacks);

            Roles.ObserveVisible(creature, distance, predator);

            if (predator)
            {
                float ordinaryThreatDistance = Mathf.Lerp(
                    90f,
                    260f,
                    Mathf.Clamp01(creature.TotalMass));
                float nerveScale = Mathf.Lerp(
                    1.15f,
                    0.58f,
                    fly.Personality.Nerve);
                float threatDistance = Mathf.Max(
                    55f,
                    ordinaryThreatDistance * nerveScale);
                if (distance < threatDistance)
                    danger = creature;
            }

            if (creature is Player player)
            {
                bool traumatized = IsTraumatizedPlayer(player);
                bool remembered = !traumatized && IsRememberedPlayer(player);
                if (remembered)
                {
                    if (fly.Personality.Aggressive)
                    {
                        rememberedCandidate = player;
                    }
                    else
                    {
                        float fearDistance = Mathf.Lerp(
                            DesertBatflyTuning.GrabFearMinDistance,
                            DesertBatflyTuning.GrabFearMaxDistance,
                            fly.DesertState.GrabMemoryStrength);
                        fearDistance *= Mathf.Lerp(
                            1.12f,
                            0.72f,
                            fly.Personality.Nerve);
                        if (distance < fearDistance)
                            danger = player;
                    }
                }

                float reactionDistance = Mathf.Lerp(
                    125f,
                    78f,
                    fly.Personality.Nerve);
                float closingThreshold = Mathf.Lerp(
                    2.1f,
                    4.4f,
                    fly.Personality.Nerve);
                int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(
                    16f,
                    44f,
                    fly.Personality.Nerve));

                if (remembered && fly.Personality.Aggressive)
                {
                    reactionDistance *= 0.72f;
                    closingThreshold *= 1.25f;
                    pursuitThreshold = Mathf.RoundToInt(
                        pursuitThreshold * 1.35f);
                }

                if (distance < reactionDistance)
                {
                    float closing = Vector2.Dot(
                        player.mainBodyChunk.vel,
                        Custom.DirVec(
                            player.mainBodyChunk.pos,
                            fly.mainBodyChunk.pos));
                    if (closing > closingThreshold)
                        pursuit += 8;
                    else
                        pursuit = Mathf.Max(0, pursuit - 4);

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

            if (distance < closest && CanHarass(creature))
            {
                closest = distance;
                candidate = creature;
            }
        }

        Roles.EndVisibleScan(DesertSwarmRoom.For(fly.room).Flock);
        if (danger != null) Roles.CheckSuppression();
        if (Roles.WatchesInsteadOfInitiating && memory <= 0 && retaliationCharges == 0) return;
        if (Target != null || !fly.Personality.Aggressive || retreat > 0)
            return;

        bool retaliationPending = retaliationCharges > 0 &&
                                  retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && !retaliationPending)
            return;

        if (!GriefAllowsHarass()) return;
        Player socialCandidate = FindSocialHarassTarget();
        float observeThreshold = Mathf.Lerp(
            DesertBatflyTuning.ObserveThirst,
            0.18f,
            fly.Personality.AggressionDrive * 0.45f);

        // Conformity can lower the motivation threshold when another nearby bat is
        // already making an obvious harassment decision, but it never bypasses the
        // individual's Aggressive gate, cooldown, fear, trauma, or AttackSlots.
        float socialMotivationScale = socialCandidate != null
            ? Mathf.Lerp(1f, 0.72f, fly.Personality.Conformity)
            : 1f;
        bool motivated = fly.DesertState.Thirst >
                          observeThreshold * socialMotivationScale * Roles.HarassThresholdScale ||
                          memory > 0 || rememberedCandidate != null;
        if (!motivated) return;

        if (Valid(attacker) && CanHarass(attacker))
            Target = attacker;
        else if (rememberedCandidate != null)
            Target = rememberedCandidate;
        else if (socialCandidate != null)
            Target = socialCandidate;
        else
            Target = candidate;

        if (Target != null)
            SetMode(Activity.Observe);
    }

    private Player FindSocialHarassTarget()
    {
        if (fly.Personality.Conformity < 0.42f || fly.room == null)
            return null;

        float socialDrive =
            fly.Personality.Conformity * 0.55f +
            fly.Personality.AggressionDrive * 0.25f +
            fly.Personality.Nerve * 0.20f;
        if (socialDrive < 0.52f) return null;

        Player best = null;
        float bestScore = float.MinValue;
        foreach (Fly other in DesertSwarmRoom.For(fly.room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat == fly || bat.dead ||
                bat.room != fly.room || !bat.Consious ||
                bat.DesertAI.Target is not Player target ||
                !CanHarass(target))
                continue;

            if (bat.DesertAI.Mode is not (
                Activity.Observe or Activity.Approach or Activity.Circle or
                Activity.FakeDive or Activity.Dive))
                continue;

            float neighbourDistance = Vector2.Distance(
                fly.mainBodyChunk.pos,
                bat.mainBodyChunk.pos);
            if (neighbourDistance > 210f ||
                (neighbourDistance > 95f &&
                 !fly.room.VisualContact(
                     fly.mainBodyChunk.pos,
                     bat.mainBodyChunk.pos)))
                continue;

            float score =
                socialDrive * 1.2f -
                neighbourDistance / 420f +
                bat.Personality.AggressionDrive * 0.18f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = target;
        }
        return best;
    }

    private void ScanWeapons()
    {
        foreach (var layer in fly.room.physicalObjects)
        foreach (PhysicalObject obj in layer)
        {
            if (obj is not Weapon weapon || weapon.thrownBy == fly)
                continue;

            if (weapon is Spear && weapon.grabbedBy.Count > 0 &&
                Custom.DistLess(
                    weapon.firstChunk.pos,
                    fly.mainBodyChunk.pos,
                    65f) &&
                (weapon.firstChunk.pos - weapon.firstChunk.lastPos).sqrMagnitude > 36f)
            {
                Threatened(weapon.grabbedBy[0].grabber, false);
                continue;
            }

            if (weapon.mode != Weapon.Mode.Thrown) continue;
            Vector2 delta = fly.mainBodyChunk.pos - weapon.firstChunk.pos;
            Vector2 velocity = weapon.firstChunk.vel;
            float time = Mathf.Clamp(
                Vector2.Dot(delta, velocity) /
                Mathf.Max(1f, velocity.sqrMagnitude),
                0f,
                5f);
            if ((delta - velocity * time).sqrMagnitude < 32f * 32f &&
                delta.sqrMagnitude < 170f * 170f)
            {
                Threatened(weapon.thrownBy, false);
                break;
            }
        }
    }

    private bool IsRememberedPlayer(Player player)
    {
        DesertBatflyState state = fly.DesertState;
        return player != null && state.GrabMemoryTicks > 0 &&
            state.GrabMemoryStrength > 0f &&
            state.GrabMemoryPlayer == PlayerNumber(player);
    }

    private bool IsTraumatizedPlayer(Player player)
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

    private Vector2 Orbit(float width, float height)
    {
        float angle =
            (fly.room.game.clock + (fly.Personality.VisualSeed & 1023)) * 0.025f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            55f + Mathf.Sin(angle) * height * 0.45f);
    }

    private void Steer(Vector2 goal, float speed)
    {
        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            fly.AI.behavior = FlyAI.Behavior.Idle;
        fly.AI.followingDijkstraMap = -1;
        fly.movMode = Fly.MovementMode.BatFlight;
        hasRoost = false;

        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, goal);
        if (fly.room.GetTile(
                fly.mainBodyChunk.pos + direction * 25f).Solid ||
            (fly.room.terrain != null &&
             fly.room.terrain.Contains(
                 fly.mainBodyChunk.pos + direction * 25f)))
        {
            goal = fly.mainBodyChunk.pos + Vector2.up * 70f;
            speed = 4f;
        }

        fly.AI.localGoal = goal;
        fly.mainBodyChunk.vel = Vector2.Lerp(
            fly.mainBodyChunk.vel,
            Custom.DirVec(fly.mainBodyChunk.pos, goal) * speed,
            0.22f);
    }

    private void UpdateRoost()
    {
        if (Mode == Activity.Roost)
        {
            if (!hasRoost || ticks > fly.Personality.RoostDuration ||
                fly.AI.fleeFromRain)
            {
                StopRoost(true);
                return;
            }

            if (fly.AI.behavior != FlyAI.Behavior.Chain)
                fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
            fly.burrowOrHangSpot = roost;
            fly.movMode = Fly.MovementMode.Hang;
            fly.mainBodyChunk.vel *= 0.5f;
            return;
        }

        if (fly.AI.behavior == FlyAI.Behavior.Chain) return;
        if (scan != 0 || Random.value > fly.Personality.RoostChance * DesertBatflySocialBond.RoostScale(fly)) return;
        if (!TryFindRoost(out Vector2 spot)) return;

        roost = spot;
        hasRoost = true;
        fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        fly.burrowOrHangSpot = roost;
        fly.movMode = Fly.MovementMode.Hang;
        SetMode(Activity.Roost);
    }

    private bool TryFindRoost(out Vector2 spot)
    {
        spot = default;
        IntVector2 tile = fly.room.GetTilePosition(fly.mainBodyChunk.pos);
        if (!fly.AI.ChainTile(tile)) return false;

        Room.Tile current = fly.room.GetTile(tile);
        Room.Tile above = fly.room.GetTile(tile.x, tile.y + 1);
        Vector2 middle = fly.room.MiddleOfTile(tile);

        if (current.horizontalBeam)
            spot = new Vector2(fly.mainBodyChunk.pos.x, middle.y - 4f);
        else if (above.verticalBeam && !current.verticalBeam)
            spot = middle + Vector2.up * 10f;
        else
            spot = new Vector2(fly.mainBodyChunk.pos.x, middle.y + 10f);

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
                brain.hasSlot = false;
                brain.attachedChunk = null;
                brain.Target = null;
                brain.drainedWater = 0f;
                brain.interest = 0;

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