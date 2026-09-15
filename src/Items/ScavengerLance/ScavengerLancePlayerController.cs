using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal enum PlayerLanceAction
{
    Carry,
    PendingThrust,
    Brace,
    QuickThrust,
    Lunge,
    AirAttack,
    Recover
}

internal readonly struct PlayerLancePose
{
    internal PlayerLancePose(Vector2 direction, Vector2 primaryHandOffset,
        bool useSupportHand = false, float supportHandDistance = 9f, float supportBlend = 0.8f,
        float bodyCompression = 0f, float bodyLean = 0f, float headAim = 0f,
        float weaponTension = 0f)
    {
        Direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        PrimaryHandOffset = primaryHandOffset;
        UseSupportHand = useSupportHand;
        SupportHandDistance = supportHandDistance;
        SupportBlend = supportBlend;
        BodyCompression = Mathf.Max(0f, bodyCompression);
        BodyLean = Mathf.Max(0f, bodyLean);
        HeadAim = Mathf.Clamp01(headAim);
        WeaponTension = Mathf.Clamp01(weaponTension);
    }

    internal readonly Vector2 Direction;
    internal readonly Vector2 PrimaryHandOffset;
    internal readonly bool UseSupportHand;
    internal readonly float SupportHandDistance;
    internal readonly float SupportBlend;
    internal readonly float BodyCompression;
    internal readonly float BodyLean;
    internal readonly float HeadAim;
    internal readonly float WeaponTension;
}

/// <summary>
/// Player-side input/state adapter for ScavengerLance.
/// It reads only Player.InputPackage so keyboard, controller, remapping and Jolly players all use
/// the same native Rain World input path. The weapon/collision implementation remains on the lance.
/// </summary>
internal static class ScavengerLancePlayerController
{
    private const int BraceThresholdFrames = 9;
    private const int BraceFormFrames = 16;
    private const int BraceTensionFrames = 26;
    private const int FullBraceFrames = 32;
    private const int FullBraceLockFrames = 4;

    private const int QuickThrustFrames = 10;
    private const int QuickActionFrames = 14;
    private const int RecoverFrames = 7;
    private const int QuickCooldownFrames = 22;
    private const float QuickMaxDamage = 0.85f;
    private const float AirQuickMaxDamage = 0.95f;
    private const float QuickExtension = 15f;

    private const float MinimumChargedDamage = 0.95f;
    private const float MaximumChargedDamage = LanceCombatMath.PlayerThrustMaxDamage;
    private const float MinimumChargedExtension = 17f;
    private const float MaximumChargedExtension = 23f;
    private const float FullChargeGroundBoost = 0.90f;
    private const float FullChargeAirBoost = 0.70f;

    private const float BraceBaseRetraction = 4f;
    private const float BraceFullRetraction = 7.5f;
    private const int AirSweepFrames = 6;
    private const float AirSweepTriggerAngle = 48f;
    private const float AirSweepMaxArc = 88f;

    private sealed class State
    {
        internal ScavengerLance Lance;
        internal int Grasp = -1;
        internal PlayerLanceAction Action;
        internal int HoldFrames;
        internal int ActionAge;
        internal int ActionDuration;
        internal int ActiveThrustFrames;
        internal float MaxExtension;
        internal float ReleasedBracePower;
        internal Vector2 Direction = Vector2.right;
        internal bool Charged;
        internal bool FullBraceReached;
        internal int FullBraceLockAge;
        internal bool AirSweepSpent;
        internal bool AirSweepActive;
        internal int AirSweepAge;
        internal Vector2 AirSweepStart = Vector2.right;
        internal Vector2 AirSweepEnd = Vector2.right;
    }

    private static readonly ConditionalWeakTable<Player, State> States = new();

    internal static bool BeginThrowInput(Player player, int grasp, ScavengerLance lance)
    {
        if (player == null || lance == null || grasp < 0 || grasp >= player.grasps.Length ||
            player.grasps[grasp]?.grabbed != lance || !player.Consious)
            return false;

        State state = States.GetValue(player, _ => new State());
        state.Lance = lance;
        state.Grasp = grasp;
        state.Action = PlayerLanceAction.PendingThrust;
        state.HoldFrames = 0;
        state.ActionAge = 0;
        state.ActionDuration = 0;
        state.ActiveThrustFrames = 0;
        state.MaxExtension = 0f;
        state.ReleasedBracePower = 0f;
        state.Charged = false;
        state.FullBraceReached = false;
        state.FullBraceLockAge = 0;
        state.AirSweepActive = false;
        state.AirSweepAge = 0;

        Vector2 desired = DesiredAim(player, state.Direction);
        state.Direction = state.Direction.sqrMagnitude > 0.001f
            ? RotateTowards(state.Direction, desired, 28f)
            : desired;
        return true;
    }

    internal static void Update(Player player, bool eu)
    {
        if (player == null || !States.TryGetValue(player, out State state)) return;
        if (!ValidateHeldLance(player, state))
        {
            States.Remove(player);
            return;
        }

        if (!player.Consious || player.enteringShortCut.HasValue || player.inShortcut)
        {
            ResetToCarry(state);
            return;
        }

        bool airborne = IsAirborne(player);
        if (!airborne)
            state.AirSweepSpent = false;

        switch (state.Action)
        {
            case PlayerLanceAction.Carry:
                return;

            case PlayerLanceAction.PendingThrust:
                state.Direction = RotateTowards(state.Direction, DesiredAim(player, state.Direction), 14f);
                if (player.input[0].thrw)
                {
                    state.HoldFrames++;
                    if (state.HoldFrames >= BraceThresholdFrames)
                    {
                        state.Action = PlayerLanceAction.Brace;
                        state.ActionAge = 0;
                    }
                    return;
                }
                StartAttack(player, state, charged: false, airborne);
                return;

            case PlayerLanceAction.Brace:
            {
                if (player.input[0].thrw)
                {
                    if (state.HoldFrames < int.MaxValue)
                        state.HoldFrames++;

                    if (!state.FullBraceReached && state.HoldFrames >= FullBraceFrames)
                    {
                        state.FullBraceReached = true;
                        state.FullBraceLockAge = 0;
                    }
                    else if (state.FullBraceReached && state.FullBraceLockAge < FullBraceLockFrames)
                    {
                        state.FullBraceLockAge++;
                    }

                    float power = BracePower(state);
                    float turnRate = Mathf.Lerp(11f, 5.5f, power);
                    state.Direction = RotateTowards(state.Direction, DesiredAim(player, state.Direction), turnRate);
                    if (!airborne)
                        ApplyBraceMobility(player, power);
                    return;
                }

                StartAttack(player, state, charged: true, airborne);
                return;
            }

            case PlayerLanceAction.QuickThrust:
            case PlayerLanceAction.Lunge:
            case PlayerLanceAction.AirAttack:
                state.ActionAge++;
                if (state.Action == PlayerLanceAction.AirAttack)
                    UpdateAirSweep(player, state);
                state.Lance.TrackPlayerAttackDirection(state.Direction);
                if (state.ActionAge >= state.ActionDuration)
                {
                    state.Action = PlayerLanceAction.Recover;
                    state.ActionAge = 0;
                }
                return;

            case PlayerLanceAction.Recover:
                state.ActionAge++;
                state.Direction = RotateTowards(state.Direction, DefaultFacing(player), 12f);
                if (state.ActionAge >= RecoverFrames)
                    ResetToCarry(state);
                return;
        }
    }

    internal static bool TryGetPose(Player player, ScavengerLance lance, out PlayerLancePose pose)
    {
        pose = default;
        if (player == null || lance == null || !States.TryGetValue(player, out State state) || state.Lance != lance)
            return false;

        Vector2 direction = state.Direction.sqrMagnitude > 0.001f ? state.Direction.normalized : DefaultFacing(player);
        switch (state.Action)
        {
            case PlayerLanceAction.PendingThrust:
            {
                float readiness = Mathf.Clamp01((float)state.HoldFrames / BraceThresholdFrames);
                float eased = Mathf.SmoothStep(0f, 1f, readiness);
                pose = new PlayerLancePose(direction,
                    -direction * (2.5f * eased),
                    bodyCompression: 0.45f * eased,
                    headAim: 0.18f * eased,
                    weaponTension: 0.10f * eased);
                return true;
            }

            case PlayerLanceAction.Brace:
            {
                float form = SmoothRange(state.HoldFrames, BraceThresholdFrames, BraceFormFrames);
                float spread = SmoothRange(state.HoldFrames, BraceThresholdFrames + 2, BraceTensionFrames);
                float tension = SmoothRange(state.HoldFrames, BraceFormFrames, FullBraceFrames);
                float power = BracePower(state);
                float lockPulse = state.FullBraceReached
                    ? 1f - Mathf.Clamp01((float)state.FullBraceLockAge / FullBraceLockFrames)
                    : 0f;
                float breathing = state.FullBraceReached
                    ? Mathf.Sin((state.HoldFrames - FullBraceFrames) * 0.20f) * 0.22f
                    : 0f;

                float retraction = Mathf.Lerp(BraceBaseRetraction, BraceFullRetraction, tension) + lockPulse * 0.8f;
                float compression = Mathf.Lerp(1.15f, 3.75f, spread) + lockPulse * 0.85f + breathing;
                float bodyLean = Mathf.Lerp(1.0f, 4.15f, tension);
                float supportDistance = Mathf.Lerp(9.5f, 19f, spread);
                float supportBlend = Mathf.Lerp(0.72f, 0.95f, form);
                float headAim = Mathf.Lerp(0.28f, 0.88f, form);
                float weaponTension = Mathf.Clamp01(Mathf.Lerp(0.20f, 1f, tension) + lockPulse * 0.08f);

                pose = new PlayerLancePose(direction,
                    -direction * retraction + Vector2.down * Mathf.Lerp(0.7f, 1.45f, power),
                    useSupportHand: true,
                    supportHandDistance: supportDistance,
                    supportBlend: supportBlend,
                    bodyCompression: compression,
                    bodyLean: bodyLean,
                    headAim: headAim,
                    weaponTension: weaponTension);
                return true;
            }

            case PlayerLanceAction.QuickThrust:
            {
                float extension = AttackExtension(state.ActionAge, state.ActiveThrustFrames, state.MaxExtension);
                pose = new PlayerLancePose(direction, direction * extension,
                    bodyLean: Mathf.Max(0f, extension) * 0.05f,
                    headAim: 0.35f);
                return true;
            }

            case PlayerLanceAction.Lunge:
            case PlayerLanceAction.AirAttack:
            {
                float extension = AttackExtension(state.ActionAge, state.ActiveThrustFrames, state.MaxExtension);
                bool twoHands = state.Charged;
                float release = state.Charged
                    ? Mathf.Clamp01(1f - state.ActionAge / 3f) * state.ReleasedBracePower
                    : 0f;
                float attackLean = state.Charged
                    ? Mathf.Lerp(1.4f, 3.8f, state.ReleasedBracePower)
                    : 0.7f;
                pose = new PlayerLancePose(direction, direction * extension,
                    useSupportHand: twoHands,
                    supportHandDistance: 10f + Mathf.Max(0f, extension) * 0.18f,
                    supportBlend: 0.92f,
                    bodyCompression: state.Charged ? 0.8f * (1f - Mathf.Clamp01(state.ActionAge / 6f)) : 0f,
                    bodyLean: attackLean * Mathf.Clamp01(Mathf.Max(0f, extension) / Mathf.Max(1f, state.MaxExtension)),
                    headAim: state.Charged ? 0.75f : 0.42f,
                    weaponTension: release);
                return true;
            }

            case PlayerLanceAction.Recover:
            {
                float recovery = 1f - Mathf.Clamp01((float)state.ActionAge / RecoverFrames);
                pose = new PlayerLancePose(direction, Vector2.zero,
                    bodyCompression: state.Charged ? 0.55f * recovery : 0f,
                    bodyLean: state.Charged ? 0.8f * state.ReleasedBracePower * recovery : 0f,
                    headAim: 0.18f * recovery,
                    weaponTension: 0f);
                return true;
            }

            default:
                return false;
        }
    }

    private static void StartAttack(Player player, State state, bool charged, bool airborne)
    {
        float bracePower = charged ? BracePower(state) : 0f;
        bool fullyCharged = charged && state.FullBraceReached;

        state.Charged = charged;
        state.ReleasedBracePower = bracePower;
        state.ActionAge = 0;
        state.AirSweepActive = false;
        state.AirSweepAge = 0;
        state.Direction = RotateTowards(state.Direction, DesiredAim(player, state.Direction), charged ? 10f : 20f);

        int thrustFrames = charged
            ? Mathf.RoundToInt(Mathf.Lerp(10f, 13f, bracePower))
            : QuickThrustFrames;
        int cooldown = charged
            ? Mathf.RoundToInt(Mathf.Lerp(24f, 30f, bracePower))
            : QuickCooldownFrames;
        float damage = charged
            ? Mathf.Lerp(MinimumChargedDamage, MaximumChargedDamage, bracePower)
            : airborne ? AirQuickMaxDamage : QuickMaxDamage;
        float extension = charged
            ? Mathf.Lerp(MinimumChargedExtension, MaximumChargedExtension, bracePower)
            : QuickExtension;

        if (!state.Lance.RequestPlayerAttack(state.Direction, damage, thrustFrames, cooldown))
        {
            state.Action = PlayerLanceAction.Recover;
            state.ActionAge = 0;
            state.ActionDuration = RecoverFrames;
            return;
        }

        state.ActiveThrustFrames = thrustFrames;
        state.MaxExtension = extension;
        state.ActionDuration = charged
            ? Mathf.RoundToInt(Mathf.Lerp(14f, 17f, bracePower))
            : QuickActionFrames;
        state.Action = airborne ? PlayerLanceAction.AirAttack :
            charged ? PlayerLanceAction.Lunge : PlayerLanceAction.QuickThrust;

        ApplyAttackImpulse(player, state.Direction, charged, airborne, bracePower, fullyCharged);
        player.room?.PlaySound(SoundID.Slugcat_Throw_Spear, player.mainBodyChunk.pos,
            charged ? Mathf.Lerp(0.62f, 0.78f, bracePower) : 0.48f,
            charged ? Mathf.Lerp(0.93f, 0.82f, bracePower) : 1.12f);
    }

    private static void UpdateAirSweep(Player player, State state)
    {
        if (state.AirSweepActive)
        {
            state.AirSweepAge++;
            float t = Mathf.Clamp01((float)state.AirSweepAge / AirSweepFrames);
            float eased = Mathf.SmoothStep(0f, 1f, t);
            state.Direction = LerpDirection(state.AirSweepStart, state.AirSweepEnd, eased);
            if (state.AirSweepAge >= AirSweepFrames)
                state.AirSweepActive = false;
            return;
        }

        if (state.AirSweepSpent || state.ActionAge < 2 || state.ActionAge > 9 ||
            !TryDirectionalInput(player, out Vector2 desired))
            return;

        float currentAngle = DirectionAngle(state.Direction);
        float desiredAngle = DirectionAngle(desired);
        float delta = Mathf.DeltaAngle(currentAngle, desiredAngle);
        if (Mathf.Abs(delta) < AirSweepTriggerAngle) return;

        state.AirSweepSpent = true;
        state.AirSweepActive = true;
        state.AirSweepAge = 0;
        state.AirSweepStart = state.Direction;
        state.AirSweepEnd = AngleDirection(currentAngle + Mathf.Clamp(delta, -AirSweepMaxArc, AirSweepMaxArc));
    }

    private static void ApplyBraceMobility(Player player, float bracePower)
    {
        if (player.bodyMode != Player.BodyModeIndex.Stand &&
            player.bodyMode != Player.BodyModeIndex.Default &&
            player.bodyMode != Player.BodyModeIndex.Crawl)
            return;

        // Bracing is controlled movement, not exponential braking. Preserve normal input below the
        // stance speed cap, then bleed excess sprint velocity away progressively as the stance firms.
        float maxHorizontalSpeed = Mathf.Lerp(4.2f, 2.35f, Mathf.Clamp01(bracePower));
        float deceleration = Mathf.Lerp(0.28f, 0.62f, Mathf.Clamp01(bracePower));
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            float target = Mathf.Clamp(player.bodyChunks[i].vel.x, -maxHorizontalSpeed, maxHorizontalSpeed);
            player.bodyChunks[i].vel.x = Mathf.MoveTowards(player.bodyChunks[i].vel.x, target, deceleration);
        }
    }

    private static void ApplyAttackImpulse(Player player, Vector2 direction, bool charged, bool airborne,
        float bracePower, bool fullyCharged)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : DefaultFacing(player);
        if (airborne)
        {
            float impulse = charged ? Mathf.Lerp(1.35f, 2.20f, bracePower) : 0.72f;
            if (fullyCharged) impulse += FullChargeAirBoost;
            Vector2 addition = dir * impulse;
            for (int i = 0; i < player.bodyChunks.Length; i++)
                player.bodyChunks[i].vel += addition;
            return;
        }

        if (!charged)
        {
            float horizontal = dir.x * 0.75f;
            player.bodyChunks[0].vel.x += horizontal;
            player.bodyChunks[1].vel.x += horizontal * 0.82f;
            if (dir.y > 0.45f)
                player.bodyChunks[0].vel.y += dir.y * 0.25f;
            return;
        }

        float lunge = Mathf.Lerp(2.35f, 4.35f, bracePower);
        float horizontalLunge = dir.x * lunge;
        player.bodyChunks[0].vel.x += horizontalLunge;
        player.bodyChunks[1].vel.x += horizontalLunge * 0.86f;

        if (dir.y > 0.35f)
        {
            float lift = Mathf.Lerp(0.42f, 0.95f, bracePower) * dir.y;
            player.bodyChunks[0].vel.y += lift;
            player.bodyChunks[1].vel.y += lift * 0.55f;
        }

        // Reaching the end of the brace now has an unmistakable gameplay payoff: the release gets
        // one extra forward impulse. It is intentionally a launch assist rather than another damage
        // multiplier, so motion, weapon speed and the existing collision model remain the source of
        // the heavy hit.
        if (fullyCharged && Mathf.Abs(dir.x) > 0.12f)
        {
            float boost = Mathf.Sign(dir.x) * FullChargeGroundBoost;
            player.bodyChunks[0].vel.x += boost;
            player.bodyChunks[1].vel.x += boost * 0.90f;
        }
    }

    private static float BracePower(State state)
    {
        float t = SmoothRange(state.HoldFrames, BraceThresholdFrames, FullBraceFrames);
        return Mathf.Lerp(0.62f, 1f, t);
    }

    private static float SmoothRange(float value, float from, float to)
    {
        if (to <= from) return value >= to ? 1f : 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, value));
    }

    private static bool ValidateHeldLance(Player player, State state)
    {
        if (state.Lance == null) return false;
        for (int i = 0; i < player.grasps.Length; i++)
        {
            if (player.grasps[i]?.grabbed != state.Lance) continue;
            state.Grasp = i;
            return true;
        }
        return false;
    }

    private static bool IsAirborne(Player player)
    {
        if (player.bodyMode == Player.BodyModeIndex.ZeroG) return true;
        if (player.bodyMode != Player.BodyModeIndex.Default) return false;
        for (int i = 0; i < player.bodyChunks.Length; i++)
            if (player.bodyChunks[i].ContactPoint.x != 0 || player.bodyChunks[i].ContactPoint.y != 0)
                return false;
        return true;
    }

    private static Vector2 DesiredAim(Player player, Vector2 fallback)
    {
        return TryDirectionalInput(player, out Vector2 inputDirection)
            ? inputDirection
            : fallback.sqrMagnitude > 0.001f ? fallback.normalized : DefaultFacing(player);
    }

    private static bool TryDirectionalInput(Player player, out Vector2 direction)
    {
        Player.InputPackage input = player.input[0];
        if (input.gamePad && input.analogueDir.sqrMagnitude > 0.0625f)
        {
            direction = input.analogueDir.normalized;
            return true;
        }

        if (input.x == 0 && input.y == 0)
        {
            direction = Vector2.zero;
            return false;
        }

        // Digital diagonals are intentionally flatter than 45 degrees so a normal running thrust
        // stays readable and useful while still giving direct up/down access.
        if (input.x != 0 && input.y != 0)
            direction = new Vector2(input.x, input.y * 0.70f).normalized;
        else
            direction = new Vector2(input.x, input.y).normalized;
        return true;
    }

    private static Vector2 DefaultFacing(Player player)
    {
        int face = player.ThrowDirection;
        if (face == 0) face = player.flipDirection;
        if (face == 0) face = 1;
        return new Vector2(face, 0f);
    }

    private static void ResetToCarry(State state)
    {
        state.Action = PlayerLanceAction.Carry;
        state.HoldFrames = 0;
        state.ActionAge = 0;
        state.ActionDuration = 0;
        state.ActiveThrustFrames = 0;
        state.MaxExtension = 0f;
        state.ReleasedBracePower = 0f;
        state.Charged = false;
        state.FullBraceReached = false;
        state.FullBraceLockAge = 0;
        state.AirSweepActive = false;
        state.AirSweepAge = 0;
    }

    private static float AttackExtension(int age, int thrustFrames, float maximum)
    {
        if (thrustFrames <= 1 || maximum <= 0f) return 0f;
        float t = Mathf.Clamp01((float)age / (thrustFrames - 1));
        if (t < 0.18f)
            return Mathf.Lerp(0f, -2.5f, Mathf.SmoothStep(0f, 1f, t / 0.18f));
        if (t < 0.58f)
        {
            float u = Mathf.InverseLerp(0.18f, 0.58f, t);
            float eased = 1f - (1f - u) * (1f - u);
            return Mathf.Lerp(-2.5f, maximum, eased);
        }
        return Mathf.Lerp(maximum, 0f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.58f, 1f, t)));
    }

    private static Vector2 RotateTowards(Vector2 from, Vector2 to, float degrees)
    {
        if (to.sqrMagnitude < 0.001f) return from.sqrMagnitude > 0.001f ? from.normalized : Vector2.right;
        if (from.sqrMagnitude < 0.001f) return to.normalized;
        float angle = Mathf.MoveTowardsAngle(DirectionAngle(from), DirectionAngle(to), Mathf.Max(0f, degrees));
        return AngleDirection(angle);
    }

    private static Vector2 LerpDirection(Vector2 from, Vector2 to, float t)
    {
        float angle = Mathf.LerpAngle(DirectionAngle(from), DirectionAngle(to), Mathf.Clamp01(t));
        return AngleDirection(angle);
    }

    private static float DirectionAngle(Vector2 direction) =>
        Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

    private static Vector2 AngleDirection(float angle)
    {
        float radians = angle * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
    }
}
