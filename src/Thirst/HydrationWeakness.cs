using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DryCycle.TemperatureSystem;
using RWCustom;
using UnityEngine;

namespace DryCycle.Thirst;

/// <summary>
/// Custom dehydration weakness. Water itself never goes below zero: once the bar is
/// empty, a separate DehydrationDebt state consumes the cycle's resistance and then
/// rises toward a lethal 600-point threshold. All weakness effects are driven directly
/// by debt and never use Rain World's Malnourished flag.
/// </summary>
internal static class HydrationWeakness
{
    private sealed class DehydrationState
    {
        public float Debt;
        public float Resistance = InitialResistance;

        public int FailureTicks;
        public int FailureCooldownTicks;
        public int PendingRecoveryLockTicks;
        public int RecoveryLockTicks;
        public int CollapseTicks;

        public int RunLoadTicks;
        public int PoleLoadTicks;
        public int SwimLoadTicks;
        public int HighAerobicLoadTicks;
        public bool FailureCheckedThisUpdate;

        public bool IsWeak => Debt > 0.0001f;

        public void ResetForNewCycle()
        {
            Debt = 0f;
            Resistance = InitialResistance;
            FailureTicks = 0;
            FailureCooldownTicks = 0;
            PendingRecoveryLockTicks = 0;
            RecoveryLockTicks = 0;
            CollapseTicks = 0;
            RunLoadTicks = 0;
            PoleLoadTicks = 0;
            SwimLoadTicks = 0;
            HighAerobicLoadTicks = 0;
            FailureCheckedThisUpdate = false;
        }
    }

    internal const float LethalDebt = 600f;
    internal const float MildEndDebt = 150f;
    internal const float ModerateEndDebt = 300f;
    internal const float SevereEndDebt = 450f;
    internal const float CollapseStartDebt = 525f;
    internal const float DyingStartDebt = 560f;
    internal const float FinalStruggleDebt = 590f;

    internal const float BaseDebtGainPerSecond = 5f;
    internal const float BaseDebtRecoveryPerSecond = 10f;
    internal const float InitialResistance = 50f;

    private const float TickSeconds = 1f / ThirstConstants.SimulationTicksPerSecond;
    private const string CarrySaveKey = "DRYCYCLEDEHYDRATIONV1";

    // Bind physiological debt to the abstract creature rather than the current
    // realized Player instance. Rain World can destroy/recreate Player objects during
    // abstraction, room transitions and Jolly processing; the AbstractCreature is the
    // stable identity that survives those transitions inside a running StorySession.
    private static ConditionalWeakTable<AbstractCreature, DehydrationState> _states = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.Player.MovementUpdate += Player_MovementUpdate;
        On.Player.AerobicIncrease += Player_AerobicIncrease;
        On.Player.Jump += Player_Jump;
        On.Player.WallJump += Player_WallJump;
        On.Weapon.Thrown += Weapon_Thrown;
        On.SaveState.SessionEnded += SaveState_SessionEnded;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        On.Player.MovementUpdate -= Player_MovementUpdate;
        On.Player.AerobicIncrease -= Player_AerobicIncrease;
        On.Player.Jump -= Player_Jump;
        On.Player.WallJump -= Player_WallJump;
        On.Weapon.Thrown -= Weapon_Thrown;
        On.SaveState.SessionEnded -= SaveState_SessionEnded;
        _states = new ConditionalWeakTable<AbstractCreature, DehydrationState>();
    }

    internal static float GetDebt(Player player)
    {
        return TryGetState(player, out DehydrationState state)
            ? state.Debt
            : 0f;
    }

    internal static float GetResistance(Player player)
    {
        return TryGetState(player, out DehydrationState state)
            ? state.Resistance
            : InitialResistance;
    }

    internal static bool IsDehydrated(Player player)
    {
        return TryGetState(player, out DehydrationState state) && state.IsWeak;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!IsStoryPlayer(self))
        {
            orig(self, eu);
            return;
        }

        DehydrationState state = GetOrCreateState(self);
        if (state == null)
        {
            orig(self, eu);
            return;
        }

        state.FailureCheckedThisUpdate = false;
        AdvanceFailureTimers(state);

        float aerobicBefore = self.aerobicLevel;
        float previousVerticalSpeed = GetVerticalSpeed(self);
        bool wasGrounded = IsGrounded(self);

        orig(self, eu);

        if (self.dead)
        {
            return;
        }

        ApplyAerobicRecoveryMultiplier(self, state, aerobicBefore);
        UpdateDebt(self, state);

        if (self.dead)
        {
            return;
        }

        UpdateContinuousFailureChecks(self, state, previousVerticalSpeed, wasGrounded);
        ApplyFailurePostEffects(self, state);
    }

    private static void Player_MovementUpdate(
        On.Player.orig_MovementUpdate orig,
        Player self,
        bool eu)
    {
        if (!IsStoryPlayer(self))
        {
            orig(self, eu);
            return;
        }

        DehydrationState state = GetOrCreateState(self);
        SlugcatStats stats = self.slugcatStats;
        if (state == null || stats == null)
        {
            orig(self, eu);
            return;
        }

        // WatcherUpdate runs before MovementUpdate and can intentionally change
        // runspeedFac (for example levitation). Capture those final values here, apply
        // dehydration only for the movement routine itself, then restore immediately.
        // This also minimizes interference with other character/stat mods.
        float originalRun = stats.runspeedFac;
        float originalPole = stats.poleClimbSpeedFac;
        float originalCorridor = stats.corridorClimbSpeedFac;

        float failureMovement = GetFailureMovementMultiplier(state);
        stats.runspeedFac *= GetRunMultiplier(state.Debt) * failureMovement;
        stats.poleClimbSpeedFac *= GetPoleMultiplier(state.Debt) * failureMovement;
        stats.corridorClimbSpeedFac *= GetCorridorMultiplier(state.Debt) * failureMovement;

        try
        {
            orig(self, eu);
        }
        finally
        {
            stats.runspeedFac = originalRun;
            stats.poleClimbSpeedFac = originalPole;
            stats.corridorClimbSpeedFac = originalCorridor;
        }
    }

    private static void Player_AerobicIncrease(
        On.Player.orig_AerobicIncrease orig,
        Player self,
        float amount)
    {
        if (!IsStoryPlayer(self))
        {
            orig(self, amount);
            return;
        }

        DehydrationState state = GetOrCreateState(self);
        orig(self, amount * GetAerobicAccumulationMultiplier(state?.Debt ?? 0f));
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (!IsStoryPlayer(self))
        {
            orig(self);
            return;
        }

        DehydrationState state = GetOrCreateState(self);
        if (state == null)
        {
            orig(self);
            return;
        }

        if (state.FailureTicks > 0 || state.RecoveryLockTicks > 0)
        {
            return;
        }

        // In the dying range, a high-load jump can itself be the event that causes
        // the body to fail before takeoff. Earlier stages only fail after the jump.
        if (state.Debt >= DyingStartDebt && TryTriggerFailure(self, state, 1f))
        {
            return;
        }

        Vector2 before0 = GetChunkVelocity(self, 0);
        Vector2 before1 = GetChunkVelocity(self, 1);

        orig(self);

        float multiplier = GetJumpMultiplier(state.Debt);
        ScaleChunkImpulse(self, 0, before0, multiplier);
        ScaleChunkImpulse(self, 1, before1, multiplier);
        self.jumpBoost *= multiplier;

        if (state.Debt < DyingStartDebt)
        {
            TryTriggerFailure(self, state, 1f);
        }
    }

    private static void Player_WallJump(
        On.Player.orig_WallJump orig,
        Player self,
        int direction)
    {
        if (!IsStoryPlayer(self))
        {
            orig(self, direction);
            return;
        }

        DehydrationState state = GetOrCreateState(self);
        if (state == null)
        {
            orig(self, direction);
            return;
        }

        if (state.FailureTicks > 0 || state.RecoveryLockTicks > 0)
        {
            return;
        }

        if (state.Debt >= DyingStartDebt && TryTriggerFailure(self, state, 1.1f))
        {
            return;
        }

        Vector2 before0 = GetChunkVelocity(self, 0);
        Vector2 before1 = GetChunkVelocity(self, 1);

        orig(self, direction);

        float multiplier = GetJumpMultiplier(state.Debt);
        ScaleChunkImpulse(self, 0, before0, multiplier);
        ScaleChunkImpulse(self, 1, before1, multiplier);
        self.jumpBoost *= multiplier;

        if (state.Debt < DyingStartDebt)
        {
            TryTriggerFailure(self, state, 1.1f);
        }
    }

    private static void Weapon_Thrown(
        On.Weapon.orig_Thrown orig,
        Weapon self,
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float force,
        bool eu)
    {
        if (thrownBy is not Player player || !IsStoryPlayer(player))
        {
            orig(self, thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, force, eu);
            return;
        }

        DehydrationState state = GetOrCreateState(player);
        if (state == null)
        {
            orig(self, thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, force, eu);
            return;
        }

        float weakenedForce = force * GetThrowForceMultiplier(state.Debt);

        orig(self, thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, weakenedForce, eu);

        if (self?.firstChunk != null)
        {
            self.firstChunk.vel *= GetThrowVelocityMultiplier(state.Debt);
        }

        // Throwing remains reliable: dehydration never cancels the throw or drops the
        // item. It can only trigger a subsequent body-failure episode at severe debt.
        TryTriggerFailure(player, state, 0.85f);
    }

    private static void SaveState_SessionEnded(
        On.SaveState.orig_SessionEnded orig,
        SaveState self,
        RainWorldGame game,
        bool survived,
        bool newMalnourished)
    {
        bool specialWarpSave = self != null && self.sessionEndingFromSpinningTopEncounter;

        if (survived)
        {
            if (specialWarpSave)
            {
                // Watcher's spinning-top/warp-point path ends the current StorySession
                // and can immediately construct a new Game process. Preserve the current
                // physiological state in unrecognized save data so that process switch
                // does not become a free dehydration cure.
                WriteCarryToSave(self, game);
            }
            else
            {
                // A genuine successful hibernation is the only session end that resets
                // dehydration and grants the next cycle's 50-point resistance buffer.
                ClearSavedCarry(self);
                ResetRuntimeStatesForNewCycle(game);
            }
        }

        orig(self, game, survived, newMalnourished);
    }

    private static void UpdateDebt(Player player, DehydrationState state)
    {
        ThirstState thirst = ThirstStore.For(player);
        float bodyHeat = GetAverageBodyHeat(player);

        if (thirst.Water > 0.0001f)
        {
            if (state.Debt > 0f)
            {
                float recovery = BaseDebtRecoveryPerSecond *
                                 GetDebtRecoveryBodyHeatMultiplier(bodyHeat) *
                                 TickSeconds;
                state.Debt = Mathf.Max(0f, state.Debt - recovery);
            }

            return;
        }

        float gain = BaseDebtGainPerSecond *
                     GetDebtGainBodyHeatMultiplier(bodyHeat) *
                     TickSeconds;

        if (state.Resistance > 0f)
        {
            float absorbed = Mathf.Min(state.Resistance, gain);
            state.Resistance -= absorbed;
            gain -= absorbed;
        }

        if (gain <= 0f)
        {
            return;
        }

        state.Debt = Mathf.Min(LethalDebt, state.Debt + gain);
        if (state.Debt >= LethalDebt)
        {
            player.Die();
        }
    }

    private static void ApplyAerobicRecoveryMultiplier(
        Player player,
        DehydrationState state,
        float before)
    {
        // Only intercept ordinary conscious stamina recovery. Sleeping, unconscious
        // states and MSC's Wounded logic can directly force aerobicLevel and must not
        // be mistaken for normal recovery. The oxygen/drowning floor is also preserved.
        if (!player.Consious ||
            player.Sleeping ||
            (ModManager.MSC && player.Wounded) ||
            player.airInLungs < 0.999f ||
            player.aerobicLevel >= before)
        {
            return;
        }

        float recovered = before - player.aerobicLevel;
        float multiplier = GetAerobicRecoveryMultiplier(state.Debt);
        player.aerobicLevel = Mathf.Clamp01(before - recovered * multiplier);
    }

    private static void UpdateContinuousFailureChecks(
        Player player,
        DehydrationState state,
        float previousVerticalSpeed,
        bool wasGrounded)
    {
        if (state.Debt < ModerateEndDebt ||
            player.room == null ||
            player.inShortcut ||
            !player.Consious ||
            player.input == null ||
            player.input.Length == 0)
        {
            ResetLoadCounters(state);
            return;
        }

        bool activeInput = player.input[0].x != 0 || player.input[0].y != 0;
        bool running = player.input[0].x != 0 &&
                       (player.bodyMode == Player.BodyModeIndex.Default ||
                        player.bodyMode == Player.BodyModeIndex.Stand ||
                        player.bodyMode == Player.BodyModeIndex.Crawl);
        bool climbing = IsPoleClimbing(player) && player.input[0].y != 0;
        bool swimming = player.bodyMode == Player.BodyModeIndex.Swimming && activeInput;

        state.RunLoadTicks = running ? state.RunLoadTicks + 1 : 0;
        state.PoleLoadTicks = climbing ? state.PoleLoadTicks + 1 : 0;
        state.SwimLoadTicks = swimming ? state.SwimLoadTicks + 1 : 0;
        state.HighAerobicLoadTicks = activeInput && player.aerobicLevel >= 0.7f
            ? state.HighAerobicLoadTicks + 1
            : 0;

        if (state.RunLoadTicks >= 80)
        {
            state.RunLoadTicks = 0;
            TryTriggerFailure(player, state, 0.65f);
        }

        if (state.PoleLoadTicks >= 40)
        {
            state.PoleLoadTicks = 0;
            TryTriggerFailure(player, state, 0.80f);
        }

        if (state.SwimLoadTicks >= 60)
        {
            state.SwimLoadTicks = 0;
            TryTriggerFailure(player, state, 0.85f);
        }

        if (state.HighAerobicLoadTicks >= 40)
        {
            state.HighAerobicLoadTicks = 0;
            TryTriggerFailure(player, state, 1.20f);
        }

        bool grounded = IsGrounded(player);
        if (!wasGrounded &&
            grounded &&
            previousVerticalSpeed < -12f &&
            activeInput)
        {
            TryTriggerFailure(player, state, 1.25f);
        }
    }

    private static bool TryTriggerFailure(
        Player player,
        DehydrationState state,
        float actionLoad)
    {
        if (state.Debt < ModerateEndDebt ||
            state.FailureTicks > 0 ||
            state.FailureCooldownTicks > 0 ||
            state.FailureCheckedThisUpdate)
        {
            return false;
        }

        state.FailureCheckedThisUpdate = true;

        float probability = GetFailureProbability(state.Debt) * actionLoad;
        probability *= Mathf.Lerp(
            1f,
            1.35f,
            Mathf.InverseLerp(0.5f, 1f, player.aerobicLevel));
        probability = Mathf.Min(0.75f, probability);

        if (UnityEngine.Random.value >= probability)
        {
            return false;
        }

        state.FailureTicks = GetFailureDurationTicks(state.Debt);
        state.FailureCooldownTicks = Mathf.RoundToInt(
            Mathf.Lerp(100f, 60f, Mathf.InverseLerp(ModerateEndDebt, LethalDebt, state.Debt)));

        if (state.Debt >= DyingStartDebt)
        {
            state.PendingRecoveryLockTicks = Mathf.RoundToInt(
                Mathf.Lerp(5f, 15f, Mathf.InverseLerp(DyingStartDebt, FinalStruggleDebt, state.Debt)));
        }

        if (state.Debt >= CollapseStartDebt && IsGrounded(player))
        {
            float collapseChance = Mathf.Lerp(
                0f,
                0.35f,
                Mathf.InverseLerp(CollapseStartDebt, LethalDebt, state.Debt));

            if (UnityEngine.Random.value < collapseChance)
            {
                state.CollapseTicks = Mathf.RoundToInt(
                    Mathf.Lerp(12f, 30f, Mathf.InverseLerp(CollapseStartDebt, LethalDebt, state.Debt)));
            }
        }

        if (state.Debt >= CollapseStartDebt && IsPoleClimbing(player))
        {
            float releaseChance = 0.35f *
                                  Mathf.InverseLerp(CollapseStartDebt, LethalDebt, state.Debt);
            if (UnityEngine.Random.value < releaseChance)
            {
                player.animation = Player.AnimationIndex.None;
                player.bodyMode = Player.BodyModeIndex.Default;
                player.standing = false;
            }
        }

        return true;
    }

    private static void ApplyFailurePostEffects(Player player, DehydrationState state)
    {
        if (state.FailureTicks <= 0 && state.CollapseTicks <= 0)
        {
            return;
        }

        if (state.FailureTicks > 0 && IsPoleClimbing(player) && player.bodyChunks != null)
        {
            float slip = Mathf.Lerp(
                0.05f,
                0.35f,
                Mathf.InverseLerp(ModerateEndDebt, LethalDebt, state.Debt));

            for (int i = 0; i < Math.Min(2, player.bodyChunks.Length); i++)
            {
                if (player.bodyChunks[i] != null)
                {
                    player.bodyChunks[i].vel.y -= slip;
                }
            }
        }

        if (state.CollapseTicks > 0 && IsGrounded(player))
        {
            player.standing = false;
            player.canJump = 0;
            player.wantToJump = 0;

            if (player.bodyMode == Player.BodyModeIndex.Stand ||
                player.bodyMode == Player.BodyModeIndex.Default)
            {
                player.animation = Player.AnimationIndex.DownOnFours;
            }

            if (player.bodyChunks != null)
            {
                for (int i = 0; i < Math.Min(2, player.bodyChunks.Length); i++)
                {
                    if (player.bodyChunks[i] != null)
                    {
                        player.bodyChunks[i].vel.x *= 0.88f;
                    }
                }
            }
        }
    }

    private static void AdvanceFailureTimers(DehydrationState state)
    {
        if (state.FailureCooldownTicks > 0)
        {
            state.FailureCooldownTicks--;
        }

        if (state.CollapseTicks > 0)
        {
            state.CollapseTicks--;
        }

        if (state.FailureTicks > 0)
        {
            state.FailureTicks--;
            if (state.FailureTicks == 0 && state.PendingRecoveryLockTicks > 0)
            {
                state.RecoveryLockTicks = Math.Max(
                    state.RecoveryLockTicks,
                    state.PendingRecoveryLockTicks);
                state.PendingRecoveryLockTicks = 0;
            }
        }
        else if (state.RecoveryLockTicks > 0)
        {
            state.RecoveryLockTicks--;
        }
    }

    private static void ResetLoadCounters(DehydrationState state)
    {
        state.RunLoadTicks = 0;
        state.PoleLoadTicks = 0;
        state.SwimLoadTicks = 0;
        state.HighAerobicLoadTicks = 0;
    }

    private static float GetDebtGainBodyHeatMultiplier(float bodyHeat)
    {
        if (bodyHeat <= 0.25f)
        {
            return 1f;
        }

        if (bodyHeat <= 0.5f)
        {
            return Mathf.Lerp(1f, 1.15f, Mathf.InverseLerp(0.25f, 0.5f, bodyHeat));
        }

        if (bodyHeat <= 1f)
        {
            return Mathf.Lerp(1.15f, 1.5f, Mathf.InverseLerp(0.5f, 1f, bodyHeat));
        }

        if (bodyHeat <= 1.5f)
        {
            return Mathf.Lerp(1.5f, 1.8f, Mathf.InverseLerp(1f, 1.5f, bodyHeat));
        }

        return Mathf.Lerp(1.8f, 2f, Mathf.InverseLerp(1.5f, 2f, bodyHeat));
    }

    private static float GetDebtRecoveryBodyHeatMultiplier(float bodyHeat)
    {
        if (bodyHeat <= 0.25f)
        {
            return 1f;
        }

        if (bodyHeat <= 0.5f)
        {
            return Mathf.Lerp(1f, 0.8f, Mathf.InverseLerp(0.25f, 0.5f, bodyHeat));
        }

        if (bodyHeat <= 1f)
        {
            return Mathf.Lerp(0.8f, 0.5f, Mathf.InverseLerp(0.5f, 1f, bodyHeat));
        }

        if (bodyHeat <= 1.5f)
        {
            return Mathf.Lerp(0.5f, 0.3f, Mathf.InverseLerp(1f, 1.5f, bodyHeat));
        }

        return Mathf.Lerp(0.3f, 0.2f, Mathf.InverseLerp(1.5f, 2f, bodyHeat));
    }

    private static float GetRunMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.92f, 0.74f, 0.52f, 0.28f, 0.20f);
    }

    private static float GetPoleMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.90f, 0.68f, 0.43f, 0.20f, 0.12f);
    }

    private static float GetCorridorMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.90f, 0.72f, 0.50f, 0.28f, 0.20f);
    }

    private static float GetJumpMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.94f, 0.78f, 0.58f, 0.32f, 0.20f);
    }

    private static float GetThrowForceMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.90f, 0.72f, 0.50f, 0.25f, 0.18f);
    }

    private static float GetThrowVelocityMultiplier(float debt)
    {
        return SixPointCurve(debt, 1f, 0.92f, 0.76f, 0.55f, 0.30f, 0.20f);
    }

    private static float GetAerobicAccumulationMultiplier(float debt)
    {
        if (debt <= MildEndDebt)
        {
            return Mathf.Lerp(1f, 1.25f, Mathf.InverseLerp(0f, MildEndDebt, debt));
        }

        if (debt <= ModerateEndDebt)
        {
            return Mathf.Lerp(1.25f, 1.75f, Mathf.InverseLerp(MildEndDebt, ModerateEndDebt, debt));
        }

        if (debt <= SevereEndDebt)
        {
            return Mathf.Lerp(1.75f, 2.5f, Mathf.InverseLerp(ModerateEndDebt, SevereEndDebt, debt));
        }

        return Mathf.Lerp(2.5f, 4f, Mathf.InverseLerp(SevereEndDebt, LethalDebt, debt));
    }

    private static float GetAerobicRecoveryMultiplier(float debt)
    {
        if (debt <= MildEndDebt)
        {
            return Mathf.Lerp(1f, 0.80f, Mathf.InverseLerp(0f, MildEndDebt, debt));
        }

        if (debt <= ModerateEndDebt)
        {
            return Mathf.Lerp(0.80f, 0.50f, Mathf.InverseLerp(MildEndDebt, ModerateEndDebt, debt));
        }

        if (debt <= SevereEndDebt)
        {
            return Mathf.Lerp(0.50f, 0.25f, Mathf.InverseLerp(ModerateEndDebt, SevereEndDebt, debt));
        }

        if (debt <= FinalStruggleDebt)
        {
            return Mathf.Lerp(0.25f, 0.10f, Mathf.InverseLerp(SevereEndDebt, FinalStruggleDebt, debt));
        }

        return Mathf.Lerp(0.10f, 0.05f, Mathf.InverseLerp(FinalStruggleDebt, LethalDebt, debt));
    }

    private static float GetFailureMovementMultiplier(DehydrationState state)
    {
        if (state.FailureTicks <= 0)
        {
            return 1f;
        }

        if (state.Debt <= SevereEndDebt)
        {
            return Mathf.Lerp(
                0.65f,
                0.35f,
                Mathf.InverseLerp(ModerateEndDebt, SevereEndDebt, state.Debt));
        }

        return Mathf.Lerp(
            0.35f,
            0.10f,
            Mathf.InverseLerp(SevereEndDebt, LethalDebt, state.Debt));
    }

    private static float GetFailureProbability(float debt)
    {
        if (debt <= 300f)
        {
            return 0f;
        }

        if (debt <= 350f)
        {
            return Mathf.Lerp(0f, 0.05f, Mathf.InverseLerp(300f, 350f, debt));
        }

        if (debt <= 400f)
        {
            return Mathf.Lerp(0.05f, 0.12f, Mathf.InverseLerp(350f, 400f, debt));
        }

        if (debt <= 450f)
        {
            return Mathf.Lerp(0.12f, 0.20f, Mathf.InverseLerp(400f, 450f, debt));
        }

        if (debt <= 500f)
        {
            return Mathf.Lerp(0.20f, 0.30f, Mathf.InverseLerp(450f, 500f, debt));
        }

        if (debt <= 550f)
        {
            return Mathf.Lerp(0.30f, 0.42f, Mathf.InverseLerp(500f, 550f, debt));
        }

        return Mathf.Lerp(0.42f, 0.55f, Mathf.InverseLerp(550f, LethalDebt, debt));
    }

    private static int GetFailureDurationTicks(float debt)
    {
        if (debt <= SevereEndDebt)
        {
            return Mathf.RoundToInt(
                Mathf.Lerp(6f, 18f, Mathf.InverseLerp(ModerateEndDebt, SevereEndDebt, debt)));
        }

        return Mathf.RoundToInt(
            Mathf.Lerp(18f, 35f, Mathf.InverseLerp(SevereEndDebt, LethalDebt, debt)));
    }

    private static float SixPointCurve(
        float debt,
        float atZero,
        float at150,
        float at300,
        float at450,
        float at590,
        float at600)
    {
        if (debt <= MildEndDebt)
        {
            return Mathf.Lerp(atZero, at150, Mathf.InverseLerp(0f, MildEndDebt, debt));
        }

        if (debt <= ModerateEndDebt)
        {
            return Mathf.Lerp(at150, at300, Mathf.InverseLerp(MildEndDebt, ModerateEndDebt, debt));
        }

        if (debt <= SevereEndDebt)
        {
            return Mathf.Lerp(at300, at450, Mathf.InverseLerp(ModerateEndDebt, SevereEndDebt, debt));
        }

        if (debt <= FinalStruggleDebt)
        {
            return Mathf.Lerp(at450, at590, Mathf.InverseLerp(SevereEndDebt, FinalStruggleDebt, debt));
        }

        return Mathf.Lerp(at590, at600, Mathf.InverseLerp(FinalStruggleDebt, LethalDebt, debt));
    }

    private static DehydrationState GetOrCreateState(Player player)
    {
        AbstractCreature abstractPlayer = player?.abstractCreature;
        if (abstractPlayer == null)
        {
            return null;
        }

        if (_states.TryGetValue(abstractPlayer, out DehydrationState existing))
        {
            return existing;
        }

        DehydrationState created = new();
        RestoreSavedCarry(player, created);
        _states.Add(abstractPlayer, created);
        return created;
    }

    private static bool TryGetState(Player player, out DehydrationState state)
    {
        AbstractCreature abstractPlayer = player?.abstractCreature;
        if (abstractPlayer != null && _states.TryGetValue(abstractPlayer, out state))
        {
            return true;
        }

        state = null;
        return false;
    }

    private static void RestoreSavedCarry(Player player, DehydrationState state)
    {
        RainWorldGame game = player?.room?.game ?? player?.abstractCreature?.world?.game;
        SaveState saveState = game?.GetStorySession?.saveState;
        int playerNumber = player?.playerState?.playerNumber ?? 0;

        if (!TryReadSavedCarry(saveState, playerNumber, out float debt, out float resistance))
        {
            return;
        }

        state.Debt = Mathf.Clamp(debt, 0f, LethalDebt);
        state.Resistance = Mathf.Clamp(resistance, 0f, InitialResistance);
    }

    private static void WriteCarryToSave(SaveState saveState, RainWorldGame game)
    {
        ClearSavedCarry(saveState);

        if (saveState?.unrecognizedSaveStrings == null || game?.Players == null)
        {
            return;
        }

        foreach (AbstractCreature abstractPlayer in game.Players)
        {
            if (abstractPlayer?.state is not PlayerState playerState ||
                !_states.TryGetValue(abstractPlayer, out DehydrationState state))
            {
                continue;
            }

            // Default state needs no serialized marker. Partial resistance depletion is
            // still meaningful even when Debt is zero, so preserve either deviation.
            if (state.Debt <= 0.0001f &&
                Mathf.Abs(state.Resistance - InitialResistance) <= 0.0001f)
            {
                continue;
            }

            saveState.unrecognizedSaveStrings.Add(
                GetCarrySavePrefix(playerState.playerNumber) +
                state.Debt.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                state.Resistance.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static void ResetRuntimeStatesForNewCycle(RainWorldGame game)
    {
        if (game?.Players == null)
        {
            return;
        }

        foreach (AbstractCreature abstractPlayer in game.Players)
        {
            if (abstractPlayer != null &&
                _states.TryGetValue(abstractPlayer, out DehydrationState state))
            {
                state.ResetForNewCycle();
            }
        }
    }

    private static void ClearSavedCarry(SaveState saveState)
    {
        if (saveState?.unrecognizedSaveStrings == null)
        {
            return;
        }

        string mainPrefix = GetCarrySavePrefix(0);
        string coopPrefix = CarrySaveKey + "P";
        saveState.unrecognizedSaveStrings.RemoveAll(entry =>
            entry != null &&
            (entry.StartsWith(mainPrefix, StringComparison.Ordinal) ||
             entry.StartsWith(coopPrefix, StringComparison.Ordinal)));
    }

    private static bool TryReadSavedCarry(
        SaveState saveState,
        int playerNumber,
        out float debt,
        out float resistance)
    {
        debt = 0f;
        resistance = InitialResistance;

        if (saveState?.unrecognizedSaveStrings == null)
        {
            return false;
        }

        string prefix = GetCarrySavePrefix(playerNumber);
        foreach (string entry in saveState.unrecognizedSaveStrings)
        {
            if (entry == null || !entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string payload = entry.Substring(prefix.Length);
            string[] parts = payload.Split(',');
            if (parts.Length < 2 ||
                !float.TryParse(
                    parts[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsedDebt) ||
                !float.TryParse(
                    parts[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsedResistance))
            {
                continue;
            }

            debt = parsedDebt;
            resistance = parsedResistance;
            return true;
        }

        return false;
    }

    private static string GetCarrySavePrefix(int playerNumber)
    {
        return playerNumber <= 0
            ? CarrySaveKey + "<svB>"
            : CarrySaveKey + "P" + playerNumber + "<svB>";
    }

    private static float GetAverageBodyHeat(Player player)
    {
        float bodyHeat0 = PlayerThermalModel.GetBodyHeat(player, 0);
        float bodyHeat1 = PlayerThermalModel.GetBodyHeat(player, 1);
        return Mathf.Clamp((bodyHeat0 + bodyHeat1) * 0.5f, 0f, 2f);
    }

    private static Vector2 GetChunkVelocity(Player player, int index)
    {
        if (player?.bodyChunks == null || index < 0 || index >= player.bodyChunks.Length || player.bodyChunks[index] == null)
        {
            return Vector2.zero;
        }

        return player.bodyChunks[index].vel;
    }

    private static void ScaleChunkImpulse(Player player, int index, Vector2 before, float multiplier)
    {
        if (player?.bodyChunks == null || index < 0 || index >= player.bodyChunks.Length || player.bodyChunks[index] == null)
        {
            return;
        }

        Vector2 delta = player.bodyChunks[index].vel - before;
        player.bodyChunks[index].vel = before + delta * multiplier;
    }

    private static float GetVerticalSpeed(Player player)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0 || player.bodyChunks[0] == null)
        {
            return 0f;
        }

        float speed = player.bodyChunks[0].vel.y;
        if (player.bodyChunks.Length > 1 && player.bodyChunks[1] != null)
        {
            speed = Mathf.Min(speed, player.bodyChunks[1].vel.y);
        }

        return speed;
    }

    private static bool IsGrounded(Player player)
    {
        if (player?.bodyChunks == null)
        {
            return false;
        }

        int count = Math.Min(2, player.bodyChunks.Length);
        for (int i = 0; i < count; i++)
        {
            if (player.bodyChunks[i] != null && player.bodyChunks[i].ContactPoint.y < 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPoleClimbing(Player player)
    {
        if (player == null)
        {
            return false;
        }

        return player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam ||
               player.animation == Player.AnimationIndex.ClimbOnBeam ||
               player.animation == Player.AnimationIndex.HangFromBeam ||
               player.animation == Player.AnimationIndex.HangUnderVerticalBeam;
    }

    private static bool IsStoryPlayer(Player player)
    {
        if (player == null || player.isNPC)
        {
            return false;
        }

        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
        return game != null && game.IsStorySession;
    }
}
