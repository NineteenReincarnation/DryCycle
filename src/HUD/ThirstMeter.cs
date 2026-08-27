using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

internal static class ThirstMeter
{
    private const int FillSegments = 20;
    private const float FillRadiusInset = 0.7f;
    private const float FillAlpha = 0.9f;
    private const float WaveAmplitude = 0.085f;
    private const float WaveFrequency = 5.2f;
    private const float WavePhaseSpeed = 0.32f;
    private const float IdleWaveStrength = 0.45f;
    private const int GainWaveHoldFrames = 24;
    private const int OverflowPipLifetime = 100;
    private const float OverflowPipScale = 0.5f;
    private const float SleepDrainPerFrame = 0.05f;

    private static readonly Color WaterColor = new(0.03f, 0.9f, 0.95f);

    private sealed class MeterState
    {
        public bool UseFixedWater;
        public float MaxWater;
        public float DisplayWater;
        public float TargetWater;
        public float SleepDrainRemaining;
        public int SleepConsumeDelay;
        public int SleepLastVisiblePipCount;
        public SleepAndDeathScreen SleepScreen;

        public bool GameplayInitialized;
        public int GameplayPlayerNumber = -1;
        public float LastActualWater;
        public float WaveStrength;
        public float WavePhase;
        public int GainWaveFrames;
        public int OverflowTimer;

        public readonly Dictionary<int, int> LastFoodGainSerialByPlayer = new();
        public readonly Dictionary<int, int> LastOverflowSerialByPlayer = new();
    }

    private sealed class RejectState
    {
        public int Counter;
    }

    private sealed class FoodGainState
    {
        public int Serial;
        public float StartWater;
        public float TargetWater;
    }

    private sealed class OverflowEatState
    {
        public int Serial;
    }

    private sealed class RefuseLatchState
    {
        public bool WarningIssued;
        public bool CalledSinceLastUpdate;
    }

    private sealed class WaterFill
    {
        public WaterFill(FContainer container)
        {
            Mesh = new TriangleMesh("Futile_White", BuildTriangles(), false)
            {
                color = WaterColor,
                alpha = 0f,
                isVisible = false
            };

            container.AddChild(Mesh);
        }

        public readonly TriangleMesh Mesh;
    }

    private sealed class OverflowFoodPip
    {
        public OverflowFoodPip(FContainer container)
        {
            Inner = new FSprite("FoodCircleB")
            {
                color = Color.white,
                alpha = 0f,
                isVisible = false
            };

            Outer = new FSprite("FoodCircleA")
            {
                color = Color.white,
                alpha = 0f,
                isVisible = false
            };

            container.AddChild(Inner);
            container.AddChild(Outer);
            Outer.MoveInFrontOfOtherNode(Inner);
        }

        public readonly FSprite Outer;
        public readonly FSprite Inner;

        public void Hide()
        {
            Outer.isVisible = false;
            Inner.isVisible = false;
        }

        public void Clear()
        {
            Outer.RemoveFromContainer();
            Inner.RemoveFromContainer();
        }
    }

    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, MeterState> MeterStates = new();
    private static readonly ConditionalWeakTable<global::HUD.FoodMeter.MeterCircle, WaterFill> CircleFills = new();
    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, OverflowFoodPip> OverflowPips = new();
    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, RefuseLatchState> RefuseLatchStates = new();
    private static readonly ConditionalWeakTable<Player, RejectState> RejectStates = new();
    private static readonly ConditionalWeakTable<Player, FoodGainState> FoodGainStates = new();
    private static readonly ConditionalWeakTable<Player, OverflowEatState> OverflowEatStates = new();

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.HUD.FoodMeter.Update += FoodMeter_Update;
        On.HUD.FoodMeter.Draw += FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites += FoodMeter_ClearSprites;
        On.HUD.FoodMeter.RefuseFood += FoodMeter_RefuseFood;
        On.HUD.FoodMeter.MeterCircle.AddCircles += MeterCircle_AddCircles;
        On.HUD.FoodMeter.MeterCircle.Draw += MeterCircle_Draw;
        On.HUD.FoodMeter.MeterCircle.ClearSprites += MeterCircle_ClearSprites;
        On.HUD.FoodMeter.QuarterPipShower.Draw += QuarterPipShower_Draw;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.HUD.FoodMeter.Update -= FoodMeter_Update;
        On.HUD.FoodMeter.Draw -= FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites -= FoodMeter_ClearSprites;
        On.HUD.FoodMeter.RefuseFood -= FoodMeter_RefuseFood;
        On.HUD.FoodMeter.MeterCircle.AddCircles -= MeterCircle_AddCircles;
        On.HUD.FoodMeter.MeterCircle.Draw -= MeterCircle_Draw;
        On.HUD.FoodMeter.MeterCircle.ClearSprites -= MeterCircle_ClearSprites;
        On.HUD.FoodMeter.QuarterPipShower.Draw -= QuarterPipShower_Draw;
    }

    public static void ConfigureSleep(
        global::HUD.FoodMeter meter,
        SaveState saveState,
        SleepAndDeathScreen screen,
        bool animateHibernateCost)
    {
        if (meter == null || saveState == null)
        {
            return;
        }

        MeterState state = MeterStates.GetOrCreateValue(meter);
        state.UseFixedWater = true;
        state.SleepScreen = screen;
        state.MaxWater = ThirstStore.GetMaxWaterPips(saveState.saveStateNumber);
        state.TargetWater = Mathf.Clamp(ThirstStore.GetSaved(saveState, 0), 0f, state.MaxWater);
        state.DisplayWater = state.TargetWater;
        state.SleepDrainRemaining = 0f;
        state.SleepConsumeDelay = 0;
        state.SleepLastVisiblePipCount = Mathf.CeilToInt(Mathf.Max(0f, state.DisplayWater - 0.0001f));
        state.WaveStrength = HasPartialPip(state.DisplayWater) ? IdleWaveStrength : 0f;
        state.WavePhase = 0f;
        state.OverflowTimer = 0;

        if (animateHibernateCost)
        {
            int hibernateCost = SlugBaseHydrationFeatures.GetWaterPips(saveState.saveStateNumber);

            state.DisplayWater = Mathf.Min(
                state.MaxWater,
                state.TargetWater + hibernateCost);

            state.SleepDrainRemaining = Mathf.Max(0f, state.DisplayWater - state.TargetWater);
            state.SleepConsumeDelay = 65;
            state.SleepLastVisiblePipCount = Mathf.CeilToInt(
                Mathf.Max(0f, state.DisplayWater - 0.0001f));
            state.WaveStrength = HasPartialPip(state.DisplayWater) ? IdleWaveStrength : 0f;
        }
    }

    public static void ConfigureCharacterSelect(global::HUD.FoodMeter meter, float water)
    {
        if (meter == null)
        {
            return;
        }

        MeterState state = MeterStates.GetOrCreateValue(meter);
        state.UseFixedWater = true;
        state.SleepScreen = null;
        state.MaxWater = meter.circles?.Count ?? Mathf.CeilToInt(Mathf.Max(0f, water));
        state.TargetWater = Mathf.Clamp(water, 0f, state.MaxWater);
        state.DisplayWater = state.TargetWater;
        state.SleepDrainRemaining = 0f;
        state.SleepConsumeDelay = 0;
        state.SleepLastVisiblePipCount = Mathf.CeilToInt(Mathf.Max(0f, state.DisplayWater - 0.0001f));
        state.WaveStrength = HasPartialPip(state.DisplayWater) ? IdleWaveStrength : 0f;
        state.WavePhase = 0f;
        state.OverflowTimer = 0;
    }

    public static void ShowDrinking(Player player)
    {
        ShowHudForPlayer(player, ThirstConstants.UnderwaterHudHoldFrames);
    }

    public static void ShowHydrationGain(Player player, float beforeWater, float afterWater)
    {
        if (player == null)
        {
            return;
        }

        float maxWater = ThirstStore.GetMaxWaterPips(player);
        float start = Mathf.Clamp(beforeWater, 0f, maxWater);
        float target = Mathf.Clamp(afterWater, 0f, maxWater);

        if (target > start + 0.0001f)
        {
            FoodGainState gain = FoodGainStates.GetOrCreateValue(player);
            gain.Serial++;
            gain.StartWater = start;
            gain.TargetWater = target;
        }

        ShowHudForPlayer(player, ThirstConstants.HydrationGainHudHoldFrames);
    }

    public static void ShowOverflowFoodEat(Player player)
    {
        if (player == null)
        {
            return;
        }

        OverflowEatStates.GetOrCreateValue(player).Serial++;
        ShowHudForPlayer(player, OverflowPipLifetime);
    }

    public static void TryReject(Player player)
    {
        if (player == null)
        {
            return;
        }

        RejectStates.GetOrCreateValue(player).Counter = ThirstConstants.RejectHudHoldFrames;
        ShowHudForPlayer(player, ThirstConstants.RejectHudHoldFrames);

        if (TryGetPlayerGame(player, out RainWorldGame game) &&
            game.cameras != null &&
            game.cameras.Length > 0 &&
            game.cameras[0]?.hud?.jollyMeter?.playerIcons != null)
        {
            int playerNumber = player.playerState?.playerNumber ?? 0;
            if (playerNumber > 0 && playerNumber < game.cameras[0].hud.jollyMeter.playerIcons.Count)
            {
                game.cameras[0].hud.jollyMeter.playerIcons[playerNumber].blinkRed = 20;
            }
        }
    }

    private static void ShowHudForPlayer(Player player, int holdFrames)
    {
        if (player == null)
        {
            return;
        }

        player.showKarmaFoodRainTime = Mathf.Max(
            player.showKarmaFoodRainTime,
            holdFrames);
    }

    private static void FoodMeter_RefuseFood(
        On.HUD.FoodMeter.orig_RefuseFood orig,
        global::HUD.FoodMeter self)
    {
        RefuseLatchState latch = RefuseLatchStates.GetOrCreateValue(self);
        latch.CalledSinceLastUpdate = true;

        if (latch.WarningIssued)
        {
            return;
        }

        latch.WarningIssued = true;
        orig(self);
    }

    private static void FoodMeter_Update(On.HUD.FoodMeter.orig_Update orig, global::HUD.FoodMeter self)
    {
        orig(self);

        if (RefuseLatchStates.TryGetValue(self, out RefuseLatchState refuseLatch))
        {
            if (!refuseLatch.CalledSinceLastUpdate)
            {
                refuseLatch.WarningIssued = false;
            }

            refuseLatch.CalledSinceLastUpdate = false;
        }

        if (MeterStates.TryGetValue(self, out MeterState fixedState) && fixedState.UseFixedWater)
        {
            if (fixedState.SleepScreen != null &&
                fixedState.SleepDrainRemaining > 0.0001f &&
                fixedState.SleepScreen.AllowFoodMeterTick)
            {
                if (fixedState.SleepConsumeDelay > 0)
                {
                    fixedState.SleepConsumeDelay--;
                }
                else
                {
                    float drain = Mathf.Min(SleepDrainPerFrame, fixedState.SleepDrainRemaining);
                    fixedState.DisplayWater = Mathf.Max(
                        fixedState.TargetWater,
                        fixedState.DisplayWater - drain);
                    fixedState.SleepDrainRemaining = Mathf.Max(
                        0f,
                        fixedState.DisplayWater - fixedState.TargetWater);

                    int visiblePipCount = Mathf.CeilToInt(
                        Mathf.Max(0f, fixedState.DisplayWater - 0.0001f));

                    if (visiblePipCount < fixedState.SleepLastVisiblePipCount)
                    {
                        self.hud.PlaySound(SoundID.HUD_Food_Meter_Deplete_Plop_A);
                        fixedState.SleepLastVisiblePipCount = visiblePipCount;
                    }

                    if (fixedState.SleepDrainRemaining <= 0.0001f)
                    {
                        fixedState.DisplayWater = fixedState.TargetWater;
                        fixedState.SleepDrainRemaining = 0f;
                    }
                }
            }

            fixedState.WavePhase += WavePhaseSpeed;
            float fixedTargetWave = HasPartialPip(fixedState.DisplayWater)
                ? IdleWaveStrength
                : 0f;
            float fixedWaveLerp = fixedTargetWave > fixedState.WaveStrength ? 0.18f : 0.10f;
            fixedState.WaveStrength = Mathf.Lerp(
                fixedState.WaveStrength,
                fixedTargetWave,
                fixedWaveLerp);

            if (fixedTargetWave <= 0f && fixedState.WaveStrength < 0.01f)
            {
                fixedState.WaveStrength = 0f;
            }
        }

        if (self?.hud?.owner is Player player &&
            TryGetPlayerGame(player, out RainWorldGame game) &&
            game.IsStorySession &&
            !player.isNPC)
        {
            UpdateGameplayAnimation(self, player);
            UpdateOverflowState(self, player, decrementTimer: true);

            if (RejectStates.TryGetValue(player, out RejectState reject) &&
                reject.Counter > 0)
            {
                reject.Counter--;
            }
        }
    }

    private static void UpdateGameplayAnimation(global::HUD.FoodMeter meter, Player player)
    {
        MeterState state = MeterStates.GetOrCreateValue(meter);
        ThirstState thirst = ThirstStore.For(player);
        float maxWater = ThirstStore.GetMaxWaterPips(player);
        float actualWater = Mathf.Clamp(thirst.Water, 0f, maxWater);
        int playerNumber = player.playerState?.playerNumber ?? 0;

        int lastFoodGainSerial = state.LastFoodGainSerialByPlayer.TryGetValue(
            playerNumber,
            out int rememberedFoodSerial)
                ? rememberedFoodSerial
                : 0;

        bool hasFoodGain = FoodGainStates.TryGetValue(player, out FoodGainState foodGain) &&
                           foodGain.Serial != lastFoodGainSerial;

        if (!state.GameplayInitialized || state.GameplayPlayerNumber != playerNumber)
        {
            ResetGameplayState(state, playerNumber, actualWater);

            if (hasFoodGain)
            {
                state.DisplayWater = Mathf.Clamp(foodGain.StartWater, 0f, maxWater);
                state.TargetWater = actualWater;
                state.GainWaveFrames = GainWaveHoldFrames;
                state.LastFoodGainSerialByPlayer[playerNumber] = foodGain.Serial;
            }
            else
            {
                return;
            }
        }
        else if (hasFoodGain)
        {
            state.DisplayWater = Mathf.Min(
                state.DisplayWater,
                Mathf.Clamp(foodGain.StartWater, 0f, maxWater));
            state.TargetWater = actualWater;
            state.GainWaveFrames = GainWaveHoldFrames;
            state.LastFoodGainSerialByPlayer[playerNumber] = foodGain.Serial;
        }

        bool gainedWater = actualWater > state.LastActualWater + 0.0001f;

        if (gainedWater || thirst.IsDrinking || hasFoodGain)
        {
            state.GainWaveFrames = GainWaveHoldFrames;
        }
        else if (state.GainWaveFrames > 0)
        {
            state.GainWaveFrames--;
        }

        state.TargetWater = actualWater;

        if (actualWater < state.DisplayWater)
        {
            // Passive WaterLossRate already decreases the true value continuously.
            // Follow it closely instead of snapping to half/full display states, so
            // the visible surface can be watched sinking while the HUD is open.
            state.DisplayWater = Mathf.Lerp(state.DisplayWater, actualWater, 0.24f);

            if (Mathf.Abs(state.DisplayWater - actualWater) < 0.0005f)
            {
                state.DisplayWater = actualWater;
            }
        }
        else
        {
            float follow = (thirst.IsDrinking || state.GainWaveFrames > 0) ? 0.22f : 0.4f;
            state.DisplayWater = Mathf.Lerp(state.DisplayWater, actualWater, follow);

            if (Mathf.Abs(state.DisplayWater - actualWater) < 0.002f)
            {
                state.DisplayWater = actualWater;
            }
        }

        state.WavePhase += WavePhaseSpeed;

        bool animateGain = thirst.IsDrinking ||
                           state.GainWaveFrames > 0 ||
                           state.TargetWater > state.DisplayWater + 0.002f;

        float targetWave = animateGain
            ? 1f
            : (HasPartialPip(state.DisplayWater) ? IdleWaveStrength : 0f);
        float waveLerp = targetWave > state.WaveStrength ? 0.25f : 0.12f;
        state.WaveStrength = Mathf.Lerp(state.WaveStrength, targetWave, waveLerp);

        if (targetWave <= 0f && state.WaveStrength < 0.01f)
        {
            state.WaveStrength = 0f;
        }

        state.LastActualWater = actualWater;
    }

    private static void UpdateOverflowState(
        global::HUD.FoodMeter meter,
        Player player,
        bool decrementTimer)
    {
        MeterState state = MeterStates.GetOrCreateValue(meter);
        int playerNumber = player.playerState?.playerNumber ?? 0;

        if (state.GameplayPlayerNumber != playerNumber)
        {
            state.OverflowTimer = 0;
        }

        if (OverflowEatStates.TryGetValue(player, out OverflowEatState overflow))
        {
            int lastSerial = state.LastOverflowSerialByPlayer.TryGetValue(
                playerNumber,
                out int rememberedSerial)
                    ? rememberedSerial
                    : 0;

            if (overflow.Serial != lastSerial)
            {
                state.LastOverflowSerialByPlayer[playerNumber] = overflow.Serial;
                state.OverflowTimer = OverflowPipLifetime;
            }
        }

        if (decrementTimer && state.OverflowTimer > 0)
        {
            state.OverflowTimer--;
        }
    }

    private static void ResetGameplayState(MeterState state, int playerNumber, float actualWater)
    {
        state.GameplayInitialized = true;
        state.GameplayPlayerNumber = playerNumber;
        state.DisplayWater = actualWater;
        state.TargetWater = actualWater;
        state.LastActualWater = actualWater;
        state.WaveStrength = HasPartialPip(actualWater) ? IdleWaveStrength : 0f;
        state.WavePhase = 0f;
        state.GainWaveFrames = 0;
        state.OverflowTimer = 0;
    }

    private static void FoodMeter_Draw(
        On.HUD.FoodMeter.orig_Draw orig,
        global::HUD.FoodMeter self,
        float timeStacker)
    {
        orig(self, timeStacker);
        DrawOverflowFoodPip(self, timeStacker);
    }

    private static void FoodMeter_ClearSprites(
        On.HUD.FoodMeter.orig_ClearSprites orig,
        global::HUD.FoodMeter self)
    {
        if (self != null && OverflowPips.TryGetValue(self, out OverflowFoodPip pip))
        {
            pip.Clear();
            OverflowPips.Remove(self);
        }

        orig(self);
    }

    private static void DrawOverflowFoodPip(global::HUD.FoodMeter meter, float timeStacker)
    {
        if (meter == null || meter.IsPupFoodMeter || meter.hud?.owner is not Player player)
        {
            HideOverflowPip(meter);
            return;
        }

        MeterState state = MeterStates.GetOrCreateValue(meter);
        UpdateOverflowState(meter, player, decrementTimer: false);

        if (state.OverflowTimer <= 0 || meter.circles == null || meter.circles.Count == 0)
        {
            HideOverflowPip(meter);
            return;
        }

        OverflowFoodPip pip = EnsureOverflowPip(meter);
        if (pip == null)
        {
            return;
        }

        global::HUD.FoodMeter.MeterCircle lastCircle = meter.circles[meter.circles.Count - 1];
        Vector2 center = lastCircle.DrawPos(timeStacker) +
                         new Vector2(meter.CircleDistance(timeStacker) * 0.75f, 0f);

        float elapsed = OverflowPipLifetime - state.OverflowTimer + timeStacker;
        float remaining = state.OverflowTimer - timeStacker;
        float meterFade = Mathf.Lerp(meter.lastFade, meter.fade, timeStacker);
        float fadeIn = Mathf.InverseLerp(0f, 5f, elapsed);
        float fadeOut = Mathf.InverseLerp(0f, 16f, remaining);
        float alpha = Mathf.Clamp01(meterFade) * Mathf.Min(fadeIn, fadeOut);

        if (alpha <= 0.001f)
        {
            pip.Hide();
            return;
        }

        float popT = Mathf.Clamp01(elapsed / 12f);
        float popScale = 1f + 0.28f * Mathf.Sin(popT * Mathf.PI);
        float fillT = Mathf.Clamp01((elapsed - 2f) / 8f);

        pip.Outer.x = center.x;
        pip.Outer.y = center.y;
        pip.Inner.x = center.x;
        pip.Inner.y = center.y;
        pip.Outer.scale = OverflowPipScale * popScale;
        pip.Inner.scale = OverflowPipScale * fillT * popScale;
        pip.Outer.alpha = alpha;
        pip.Inner.alpha = alpha;
        pip.Outer.isVisible = true;
        pip.Inner.isVisible = fillT > 0.001f;
        pip.Outer.MoveInFrontOfOtherNode(pip.Inner);
    }

    private static OverflowFoodPip EnsureOverflowPip(global::HUD.FoodMeter meter)
    {
        if (meter?.fContainer == null)
        {
            return null;
        }

        if (OverflowPips.TryGetValue(meter, out OverflowFoodPip existing))
        {
            return existing;
        }

        OverflowFoodPip created = new(meter.fContainer);
        OverflowPips.Add(meter, created);
        return created;
    }

    private static void HideOverflowPip(global::HUD.FoodMeter meter)
    {
        if (meter != null && OverflowPips.TryGetValue(meter, out OverflowFoodPip pip))
        {
            pip.Hide();
        }
    }

    private static void MeterCircle_AddCircles(
        On.HUD.FoodMeter.MeterCircle.orig_AddCircles orig,
        global::HUD.FoodMeter.MeterCircle self)
    {
        orig(self);
        EnsureFill(self);
    }

    private static void MeterCircle_ClearSprites(
        On.HUD.FoodMeter.MeterCircle.orig_ClearSprites orig,
        global::HUD.FoodMeter.MeterCircle self)
    {
        if (self != null && CircleFills.TryGetValue(self, out WaterFill fill))
        {
            fill.Mesh?.RemoveFromContainer();
            CircleFills.Remove(self);
        }

        orig(self);
    }

    private static void MeterCircle_Draw(
        On.HUD.FoodMeter.MeterCircle.orig_Draw orig,
        global::HUD.FoodMeter.MeterCircle self,
        float timeStacker)
    {
        orig(self, timeStacker);

        WaterFill fill = EnsureFill(self);
        if (fill == null)
        {
            return;
        }

        if (!TryGetDisplayWater(
                self.meter,
                out float water,
                out float waveStrength,
                out float wavePhase) ||
            self.circles == null ||
            self.circles.Length < 2 ||
            self.circles[0]?.sprite == null ||
            self.circles[1]?.sprite == null)
        {
            fill.Mesh.isVisible = false;
            return;
        }

        float waterLevel = GetPipWaterLevel(water, self.number);

        float outerFade = Mathf.Lerp(
            self.circles[0].lastFade,
            self.circles[0].fade,
            timeStacker);
        float alpha = Mathf.Clamp01(outerFade) * FillAlpha;
        float animatedOuterRadius = Mathf.Lerp(
            self.circles[0].lastRad,
            self.circles[0].rad,
            timeStacker);
        float snapRadius = self.circles[0].snapRad;
        float radiusScale = snapRadius > 0.001f
            ? animatedOuterRadius / snapRadius
            : 0f;
        float radius = Mathf.Max(
            0f,
            (snapRadius - FillRadiusInset) * radiusScale);

        if (alpha <= 0.001f ||
            waterLevel <= 0.001f ||
            radius <= 0.001f)
        {
            fill.Mesh.isVisible = false;
            return;
        }

        Color color = WaterColor;
        if (self.meter.hud?.owner is Player player &&
            RejectStates.TryGetValue(player, out RejectState reject) &&
            reject.Counter > 0 &&
            (reject.Counter / 5) % 2 == 0)
        {
            color = Color.red;
        }

        UpdateFillGeometry(
            fill.Mesh,
            self.DrawPos(timeStacker),
            radius,
            waterLevel,
            waveStrength,
            wavePhase + self.number * 0.8f);

        fill.Mesh.color = color;
        fill.Mesh.alpha = alpha;
        fill.Mesh.isVisible = true;

        fill.Mesh.MoveBehindOtherNode(self.circles[1].sprite);
        self.circles[0].sprite.MoveInFrontOfOtherNode(fill.Mesh);
    }

    private static void QuarterPipShower_Draw(
        On.HUD.FoodMeter.QuarterPipShower.orig_Draw orig,
        global::HUD.FoodMeter.QuarterPipShower self,
        float timeStacker)
    {
        orig(self, timeStacker);

        if (self?.owner?.circles == null || self.quarterPips == null)
        {
            return;
        }

        int circleIndex = self.owner.showCount;
        if (circleIndex < 0 || circleIndex >= self.owner.circles.Count)
        {
            return;
        }

        if (CircleFills.TryGetValue(self.owner.circles[circleIndex], out WaterFill fill) &&
            fill.Mesh != null)
        {
            self.quarterPips.MoveInFrontOfOtherNode(fill.Mesh);
        }
    }

    private static WaterFill EnsureFill(global::HUD.FoodMeter.MeterCircle circle)
    {
        if (circle?.meter?.fContainer == null)
        {
            return null;
        }

        if (CircleFills.TryGetValue(circle, out WaterFill existing))
        {
            return existing;
        }

        WaterFill created = new(circle.meter.fContainer);
        CircleFills.Add(circle, created);
        return created;
    }

    private static bool TryGetDisplayWater(
        global::HUD.FoodMeter meter,
        out float water,
        out float waveStrength,
        out float wavePhase)
    {
        water = 0f;
        waveStrength = 0f;
        wavePhase = 0f;

        if (meter == null || meter.IsPupFoodMeter)
        {
            return false;
        }

        if (MeterStates.TryGetValue(meter, out MeterState fixedState) &&
            fixedState.UseFixedWater)
        {
            water = Mathf.Clamp(fixedState.DisplayWater, 0f, fixedState.MaxWater);
            waveStrength = fixedState.WaveStrength;
            wavePhase = fixedState.WavePhase;
            return true;
        }

        if (meter.hud?.owner is Player player &&
            TryGetPlayerGame(player, out RainWorldGame game) &&
            game.IsStorySession &&
            !player.isNPC)
        {
            MeterState state = MeterStates.GetOrCreateValue(meter);
            ThirstState thirst = ThirstStore.For(player);
            float maxWater = ThirstStore.GetMaxWaterPips(player);
            float actual = Mathf.Clamp(thirst.Water, 0f, maxWater);
            int playerNumber = player.playerState?.playerNumber ?? 0;

            int lastFoodGainSerial = state.LastFoodGainSerialByPlayer.TryGetValue(
                playerNumber,
                out int rememberedFoodSerial)
                    ? rememberedFoodSerial
                    : 0;

            if (!state.GameplayInitialized || state.GameplayPlayerNumber != playerNumber)
            {
                ResetGameplayState(state, playerNumber, actual);

                if (FoodGainStates.TryGetValue(player, out FoodGainState foodGain) &&
                    foodGain.Serial != lastFoodGainSerial)
                {
                    state.DisplayWater = Mathf.Clamp(foodGain.StartWater, 0f, maxWater);
                    state.TargetWater = actual;
                    state.GainWaveFrames = GainWaveHoldFrames;
                    state.LastFoodGainSerialByPlayer[playerNumber] = foodGain.Serial;
                }
            }

            water = Mathf.Clamp(state.DisplayWater, 0f, maxWater);
            waveStrength = state.WaveStrength;
            wavePhase = state.WavePhase;
            return true;
        }

        return false;
    }

    private static bool TryGetPlayerGame(Player player, out RainWorldGame game)
    {
        game = player?.room?.game;

        if (game == null)
        {
            game = player?.abstractCreature?.world?.game;
        }

        return game != null;
    }

    private static float GetPipWaterLevel(float totalWater, int pipNumber)
    {
        if (pipNumber < 0)
        {
            return 0f;
        }

        // Every pip now reflects the real scalar hydration amount continuously.
        // There is no empty/half/full quantization: 2.37 water means two full pips
        // followed by a third pip whose liquid surface is at 37% height.
        return Mathf.Clamp01(totalWater - pipNumber);
    }

    private static bool HasPartialPip(float totalWater)
    {
        if (totalWater <= 0.001f)
        {
            return false;
        }

        float fractional = totalWater - Mathf.Floor(totalWater);
        return fractional > 0.001f && fractional < 0.999f;
    }

    private static void UpdateFillGeometry(
        TriangleMesh mesh,
        Vector2 center,
        float radius,
        float level,
        float waveStrength,
        float wavePhase)
    {
        float clampedLevel = Mathf.Clamp01(level);
        float baseSurface = Mathf.Lerp(-1f, 1f, clampedLevel);

        float levelEnvelope = Mathf.Sin(clampedLevel * Mathf.PI);
        float amplitude = WaveAmplitude * waveStrength * levelEnvelope;

        for (int i = 0; i <= FillSegments; i++)
        {
            float t = i / (float)FillSegments;
            float normalizedX = Mathf.Lerp(-1f, 1f, t);
            float circleHalfHeight = Mathf.Sqrt(Mathf.Max(0f, 1f - normalizedX * normalizedX));
            float bottom = -circleHalfHeight;
            float top = circleHalfHeight;
            float wave = Mathf.Sin(wavePhase + normalizedX * WaveFrequency) * amplitude;
            float liquidTop = Mathf.Clamp(baseSurface + wave, bottom, top);
            float x = center.x + normalizedX * radius;

            mesh.MoveVertice(
                i * 2,
                new Vector2(x, center.y + bottom * radius));
            mesh.MoveVertice(
                i * 2 + 1,
                new Vector2(x, center.y + liquidTop * radius));
        }
    }

    private static TriangleMesh.Triangle[] BuildTriangles()
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[FillSegments * 2];

        for (int i = 0; i < FillSegments; i++)
        {
            int a = i * 2;
            int b = a + 1;
            int c = a + 2;
            int d = a + 3;

            triangles[i * 2] = new TriangleMesh.Triangle(a, b, c);
            triangles[i * 2 + 1] = new TriangleMesh.Triangle(b, d, c);
        }

        return triangles;
    }
}
