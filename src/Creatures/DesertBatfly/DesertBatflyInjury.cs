using DryCycle.Debugging.AI;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum InjuryRecoveryState { None, Roost, Hive, SafeFlight }

// Four separate layers: native health, two persisted wings, temporary shock, existing trauma.
internal sealed class DesertBatflyInjury
{
    private readonly DesertBatfly bat;
    private int eventSerial, shockTicks, recoverySample, impulseGrace;
    private Lizard capturePredator;
    private bool capturePending;
    private float recoveredSinceEvent;
    internal float PostStunShock { get; private set; }
    internal float NominalFlightSpeed;
    internal int MotionTick { get; private set; }
    internal InjuryRecoveryState RecoveryState { get; private set; }
    internal Vector2? RecoveryTarget { get; private set; }
    internal string RecoveryReason { get; private set; } = "no recovery requested";
    internal string LastInjurySource { get; private set; } = "none";
    internal string LastInjuryDamageType { get; private set; } = "none";
    internal int LastInjuryTick { get; private set; } = -1;
    private DesertBatflyState State => bat.DesertState;
    internal float WingMean => State.WingMean;
    internal float WingAsymmetry => State.WingAsymmetry;
    internal float WingBias => State.WingBias;
    internal float WingSeverity => Mathf.SmoothStep(0f, 1f, WingMean);
    internal float PhysicalCapability => Mathf.Clamp01(1f - 0.18f * (1f - Mathf.Clamp01(State.health)) -
        0.46f * WingSeverity - 0.15f * WingAsymmetry - 0.25f * PostStunShock);

    // Task 04 tuning target: structural injury hits turning/lift much harder than straight speed.
    // At WingMean ~= 0.65, symmetric injury lands near 86% forward / 60% turn / 73% lift.
    internal float ForwardControl => Mathf.Clamp(1f - 0.20f * WingSeverity - 0.04f * WingAsymmetry, 0.80f, 1f);
    internal float TurnControl => Mathf.Clamp(1f - 0.55f * WingSeverity - 0.18f * WingAsymmetry -
        0.10f * PostStunShock, 0.48f, 1f);
    internal float LiftControl => Mathf.Clamp(1f - 0.38f * WingSeverity - 0.08f * PostStunShock, 0.68f, 1f);
    internal float AccelerationControl => Mathf.Clamp(1f - 0.32f * WingSeverity - 0.08f * PostStunShock, 0.68f, 1f);
    internal float EffectiveNerve => bat.Personality.Nerve * Mathf.Lerp(0.55f, 1f, PhysicalCapability);
    internal float AggressionScale => PhysicalCapability * (1f - 0.35f * PostStunShock);
    internal float RoostScale => 1f + WingMean * 1.3f + PostStunShock * 0.5f;
    internal bool IsSeverelyInjured => WingMean >= 0.60f || Mathf.Max(State.LeftWingInjury, State.RightWingInjury) >= 0.82f ||
        (WingMean > 0.1f && PhysicalCapability < 0.48f);
    internal bool BlocksCombat => IsSeverelyInjured || PostStunShock >= 0.55f;
    internal bool IsRecovering => RecoveryState != InjuryRecoveryState.None;
    internal string CombatBlockReason
    {
        get
        {
            if (IsSeverelyInjured)
                return $"IsSeverelyInjured=true; WingMean={WingMean:0.000}, MaxWing={Mathf.Max(State.LeftWingInjury, State.RightWingInjury):0.000}, PhysicalCapability={PhysicalCapability:0.000}";
            if (PostStunShock >= 0.55f)
                return $"PostStunShock={PostStunShock:0.000} >= 0.550";
            return "BlocksCombat=false";
        }
    }
    internal float WingAmplitude(int side) => 1f - 0.48f * (side == 0 ? State.LeftWingInjury : State.RightWingInjury);
    internal float BodyTilt => WingBias * 9f;

    internal DesertBatflyInjury(DesertBatfly bat) { this.bat = bat; }

    internal void ApplyShock(float gain)
    {
        if (bat.dead || bat.slatedForDeletetion || float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0f) return;
        float previous = PostStunShock;
        PostStunShock = Mathf.Clamp01(PostStunShock + gain);
        shockTicks = Mathf.Max(shockTicks, Mathf.RoundToInt(Mathf.Lerp(200f, 2400f, PostStunShock)));
        if (BlocksCombat) bat.DesertAI?.CancelPhysicalAttack();
        float applied = PostStunShock - previous;
        if (applied >= 0.02f && AIDebugTrace.IsWatched(bat.abstractCreature))
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "PostStunShock", PostStunShock,
                $"gain={applied:0.000}, durationTicks={shockTicks}");
    }

    internal void OnHealthLoss(float before, Creature.DamageType type, PhysicalObject source, Creature attacker, Vector2? momentum)
    {
        float loss = before - State.health;
        if (bat.dead || bat.slatedForDeletetion || float.IsNaN(loss) || float.IsInfinity(loss) || loss <= 0f) return;
        eventSerial++;
        impulseGrace = 3; // Preserve the external hit impulse; only subsequent self-flight is modified.
        uint seed = unchecked((uint)(bat.Personality.VisualSeed ^ eventSerial * 1103515245));
        seed ^= seed >> 16; seed *= 0x7feb352du; seed ^= seed >> 15;
        float jitter = (seed & 65535u) / 65535f;
        bool left = momentum.HasValue && Mathf.Abs(momentum.Value.x) > 0.1f ? momentum.Value.x > 0f : (seed & 1u) == 0;
        float typeScale = source is Spear || type == Creature.DamageType.Stab ? 1.20f : type == Creature.DamageType.Bite ? 1.10f : 0.90f;
        float impact = momentum.HasValue ? Mathf.Clamp01(momentum.Value.magnitude / 10f) : 0f;
        float gain = Mathf.Clamp01(loss * typeScale * Mathf.Lerp(0.95f, 1.15f, jitter) *
            (1f + impact * 0.10f + (1f - Mathf.Clamp01(State.health)) * 0.15f));
        float oldLeft = State.LeftWingInjury, oldRight = State.RightWingInjury;
        State.LeftWingInjury = Mathf.Clamp01(oldLeft + gain * (left ? 1f : 0.24f));
        State.RightWingInjury = Mathf.Clamp01(oldRight + gain * (left ? 0.24f : 1f));
        float leftGain = State.LeftWingInjury - oldLeft;
        float rightGain = State.RightWingInjury - oldRight;
        ApplyShock(Mathf.Clamp(loss * 0.8f + 0.08f, 0.08f, 0.65f));
        float traumaGain = Mathf.Clamp(loss * 0.35f, 0.02f, 0.25f);
        DesertBatflyIntimidation.AddTrauma(bat, attacker, traumaGain);
        LastInjurySource = source is Weapon weapon ? weapon.GetType().Name : source?.GetType().Name ?? attacker?.GetType().Name ?? "environment";
        LastInjuryDamageType = type?.value ?? "unknown";
        LastInjuryTick = bat.room?.game?.clock ?? MotionTick;
        if (AIDebugTrace.IsWatched(bat.abstractCreature))
        {
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "InjuryApplied", loss,
                $"source={LastInjurySource} type={LastInjuryDamageType} healthLoss={loss:0.000} leftWing+={leftGain:0.000} rightWing+={rightGain:0.000}");
            if (leftGain > 0f)
                AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "WingDamageLeft", State.LeftWingInjury,
                    $"delta={leftGain:0.000}, source={LastInjurySource}");
            if (rightGain > 0f)
                AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "WingDamageRight", State.RightWingInjury,
                    $"delta={rightGain:0.000}, source={LastInjurySource}");
            if (attacker != null && traumaGain > 0f)
                AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "InjuryTraumaEscalated", traumaGain,
                    $"threat={AIDebugFormat.Creature(attacker)}, source={LastInjurySource}");
        }
        bat.DesertAI?.Threatened(attacker, false);
        if (BlocksCombat) bat.DesertAI?.CancelPhysicalAttack();
    }

    internal void BeginCapture(Lizard predator)
    {
        if (bat.dead || predator == null || predator.room != bat.room) return;
        capturePredator = predator;
        capturePending = true;
    }

    internal void CheckCaptureRelease()
    {
        if (!capturePending) return;
        Lizard predator = capturePredator;
        if (bat.dead || bat.slatedForDeletetion || bat.room == null || predator == null || predator.room != bat.room || bat.inShortcut)
        {
            capturePending = false;
            capturePredator = null;
            return;
        }
        if (predator.tongue?.attached?.owner == bat && predator.tongue.state == LizardTongue.State.AttachedInSmallObject) return;
        if (predator.grasps != null)
            foreach (var grasp in predator.grasps)
                if (grasp?.grabbed == bat) return;
        capturePending = false;
        capturePredator = null;
        ApplyShock(0.38f);
        DesertBatflyIntimidation.AddTrauma(bat, predator, 0.16f);
        impulseGrace = 3;
        if (AIDebugTrace.IsWatched(bat.abstractCreature))
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "PeachTongueSurvivor", PostStunShock,
                "released alive; existing PredatorTrauma; no structural wing damage without Violence");
    }

    internal void Tick()
    {
        NominalFlightSpeed = 0f;
        if (bat.dead || bat.slatedForDeletetion)
        {
            ClearTransient();
            return;
        }
        MotionTick++;
        if (impulseGrace > 0) impulseGrace--;
        if (shockTicks > 0)
        {
            PostStunShock *= (shockTicks - 1f) / shockTicks;
            if (--shockTicks == 0) PostStunShock = 0f;
        }
        CheckCaptureRelease();
        if (++recoverySample < 40) return;
        recoverySample = 0;
        if (bat.room == null || !bat.Consious || bat.inShortcut || !DesertBatflySocialBond.CanRespond(bat) ||
            bat.DesertAI.HasImmediateDanger || bat.DesertAI.FormalAttack ||
            DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) || DesertBatflyIntimidation.HasActiveFearSuppression(bat)) return;
        bool roost = bat.AI?.behavior == FlyAI.Behavior.Chain;
        Recover(roost ? 0.004f : 0.0005f);
    }

    internal void Recover(float amount)
    {
        if (bat.dead) return;
        float before = WingMean;
        State.RecoverWings(amount);
        recoveredSinceEvent += before - WingMean;
        if (recoveredSinceEvent >= 0.10f || (before > 0f && WingMean == 0f))
        {
            recoveredSinceEvent = 0f;
            if (AIDebugTrace.IsWatched(bat.abstractCreature))
                AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State, "WingRecovered", WingMean, "recovered >=0.10 or fully recovered");
        }
    }

    internal void SetRecovery(InjuryRecoveryState state, Vector2? target, string reason)
    {
        if (RecoveryState != state && AIDebugTrace.IsWatched(bat.abstractCreature))
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.State,
                state == InjuryRecoveryState.None ? "InjuryRecoveryExited" : "InjuryRecoveryEntered", state, reason);
        RecoveryState = state;
        RecoveryTarget = target;
        RecoveryReason = reason;
    }

    internal void ClearTransient()
    {
        capturePredator = null;
        capturePending = false;
        PostStunShock = 0f;
        shockTicks = 0;
        impulseGrace = 0;
        recoverySample = 0;
        recoveredSinceEvent = 0f;
        NominalFlightSpeed = 0f;
        SetRecovery(InjuryRecoveryState.None, null, "creature lifecycle");
    }

    internal Vector2 ModifyFlight(Vector2 previous, Vector2 requested, float nominalSpeed)
    {
        if (WingMean <= 0f && PostStunShock <= 0f) return requested;
        float speed = Mathf.MoveTowards(previous.magnitude, requested.magnitude,
            Mathf.Abs(requested.magnitude - previous.magnitude) * AccelerationControl);
        Vector2 heading = previous.sqrMagnitude > 0.01f ? previous.normalized : requested.normalized;
        float cross = heading.x * requested.y - heading.y * requested.x;
        float turn = TurnControl * (1f + Mathf.Sign(cross) * WingBias * 0.07f);
        Vector2 direction = Vector3.Slerp(heading, requested.sqrMagnitude > 0.01f ? requested.normalized : heading, Mathf.Clamp01(turn));
        Vector2 velocity = direction * Mathf.Min(speed, nominalSpeed * ForwardControl);
        float lowSpeed = 1f - Mathf.Clamp01(speed / 5f);
        velocity.x += WingBias * (0.035f + lowSpeed * 0.035f);
        velocity.y -= WingMean * 0.035f + lowSpeed * (1f - LiftControl) * 0.08f;
        if (requested.y > previous.y) velocity.y -= (requested.y - previous.y) * (1f - LiftControl) * 0.20f;
        // Phase is deterministic per individual, never a per-frame random jitter.
        velocity.y += Mathf.Sin((MotionTick + (bat.Personality.VisualSeed & 255)) * 0.09f) * WingMean * lowSpeed * 0.025f;
        return velocity;
    }

    internal void ApplyFlight(Vector2 previous)
    {
        if (impulseGrace > 0 || !bat.Consious || bat.dead || bat.room == null || bat.inShortcut ||
            bat.grabbedBy.Count > 0 || bat.Emergence?.Active == true || bat.AI?.behavior == FlyAI.Behavior.Chain ||
            bat.movMode != Fly.MovementMode.BatFlight) return;
        Vector2 request = bat.mainBodyChunk.vel;
        // Explicit AI speed is intent only; injury is applied exactly here, after all controllers.
        float nominal = NominalFlightSpeed > 0f ? NominalFlightSpeed : 12f;
        bat.mainBodyChunk.vel = ModifyFlight(previous, request, nominal);
    }
}