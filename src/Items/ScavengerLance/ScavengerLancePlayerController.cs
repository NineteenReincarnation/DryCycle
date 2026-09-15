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
        bool useSupportHand = false, float supportHandDistance = 9f, float supportBlend = 0.8f)
    {
        Direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        PrimaryHandOffset = primaryHandOffset;
        UseSupportHand = useSupportHand;
        SupportHandDistance = supportHandDistance;
        SupportBlend = supportBlend;
    }

    internal readonly Vector2 Direction;
    internal readonly Vector2 PrimaryHandOffset;
    internal readonly bool UseSupportHand;
    internal readonly float SupportHandDistance;
    internal readonly float SupportBlend;
}

/// <summary>
/// Player-side input/state adapter for ScavengerLance.
/// It reads only Player.InputPackage so keyboard, controller, remapping and Jolly players all use
/// the same native Rain World input path. The weapon/collision implementation remains on the lance.
/// </summary>
internal static class ScavengerLancePlayerController
{
    private const int BraceThresholdFrames = 9;
    private const int QuickThrustFrames = 10;
    private const int ChargedThrustFrames = 12;
    private const int QuickActionFrames = 14;
    private const int ChargedActionFrames = 16;
    private const int RecoverFrames = 7;
    private const int QuickCooldownFrames = 22;
    private const int ChargedCooldownFrames = 28;
    private const float QuickMaxDamage = 0.85f;
    private const float AirQuickMaxDamage = 0.95f;
    private const float ChargedMaxDamage = LanceCombatMath.PlayerThrustMaxDamage;
    private const float QuickExtension = 15f;
    private const float ChargedExtension = 20f;
    private const float BraceRetraction = 4f;
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
        internal Vector2 Direction = Vector2.right;
        internal bool Charged;
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
        state.Charged = false;
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
                state.Direction = RotateTowards(state.Direction, DesiredAim(player, state.Direction), 9f);
                if (!airborne)
                    ApplyBraceMobility(player);
                if (!player.input[0].thrw)
                    StartAttack(player, state, charged: true, airborne);
                return;

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
                pose = new PlayerLancePose(direction, -direction * (2f * readiness));
                return true;
            }
            case PlayerLanceAction.Brace:
                pose = new PlayerLancePose(direction,
                    -direction * BraceRetraction + Vector2.down * 1.25f,
                    useSupportHand: true, supportHandDistance: 10f, supportBlend: 0.86f);
                return true;

            case PlayerLanceAction.QuickThrust:
            {
                float extension = AttackExtension(state.ActionAge, state.ActiveThrustFrames, state.MaxExtension);
                pose = new PlayerLancePose(direction, direction * extension);
                return true;
            }
            case PlayerLanceAction.Lunge:
            case PlayerLanceAction.AirAttack:
            {
                float extension = AttackExtension(state.ActionAge, state.ActiveThrustFrames, state.MaxExtension);
                bool twoHands = state.Charged;
                pose = new PlayerLancePose(direction, direction * extension,
                    useSupportHand: twoHands, supportHandDistance: 10f + Mathf.Max(0f, extension) * 0.12f,
                    supportBlend: 0.9f);
                return true;
            }
            case PlayerLanceAction.Recover:
                pose = new PlayerLancePose(direction, Vector2.zero);
                return true;
            default:
                return false;
        }
    }

    private static void StartAttack(Player player, State state, bool charged, bool airborne)
    {
        state.Charged = charged;
        state.ActionAge = 0;
        state.AirSweepActive = false;
        state.AirSweepAge = 0;
        state.Direction = RotateTowards(state.Direction, DesiredAim(player, state.Direction), charged ? 12f : 20f);

        int thrustFrames = charged ? ChargedThrustFrames : QuickThrustFrames;
        int cooldown = charged ? ChargedCooldownFrames : QuickCooldownFrames;
        float damage = charged ? ChargedMaxDamage : airborne ? AirQuickMaxDamage : QuickMaxDamage;
        float extension = charged ? ChargedExtension : QuickExtension;

        if (!state.Lance.RequestPlayerAttack(state.Direction, damage, thrustFrames, cooldown))
        {
            state.Action = PlayerLanceAction.Recover;
            state.ActionAge = 0;
            state.ActionDuration = RecoverFrames;
            return;
        }

        state.ActiveThrustFrames = thrustFrames;
        state.MaxExtension = extension;
        state.ActionDuration = charged ? ChargedActionFrames : QuickActionFrames;
        state.Action = airborne ? PlayerLanceAction.AirAttack :
            charged ? PlayerLanceAction.Lunge : PlayerLanceAction.QuickThrust;

        ApplyAttackImpulse(player, state.Direction, charged, airborne);
        player.room?.PlaySound(SoundID.Slugcat_Throw_Spear, player.mainBodyChunk.pos,
            charged ? 0.72f : 0.48f, charged ? 0.86f : 1.12f);
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

    private static void ApplyBraceMobility(Player player)
    {
        // Never freeze input. A braced player can still advance/retreat, but cannot sprint at full
        // speed while holding a long weapon in a committed line.
        for (int i = 0; i < player.bodyChunks.Length; i++)
            player.bodyChunks[i].vel.x *= 0.92f;
    }

    private static void ApplyAttackImpulse(Player player, Vector2 direction, bool charged, bool airborne)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : DefaultFacing(player);
        if (airborne)
        {
            float impulse = charged ? 1.65f : 0.72f;
            Vector2 addition = dir * impulse;
            for (int i = 0; i < player.bodyChunks.Length; i++)
                player.bodyChunks[i].vel += addition;
            return;
        }

        float horizontal = dir.x * (charged ? 3.4f : 0.75f);
        player.bodyChunks[0].vel.x += horizontal;
        player.bodyChunks[1].vel.x += horizontal * 0.82f;
        if (dir.y > 0.45f)
            player.bodyChunks[0].vel.y += dir.y * (charged ? 0.75f : 0.25f);
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
        state.Charged = false;
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
