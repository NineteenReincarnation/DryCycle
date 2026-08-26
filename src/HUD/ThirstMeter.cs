using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

/// <summary>
/// Hydration renderer for the vanilla FoodMeter.
///
/// DryCycle does not create a second row of HUD circles. Hydration is packed
/// into the vanilla food pips themselves. Static water is shown in empty / half
/// / full states, while replenishing water animates continuously upward through
/// the active pip with a small moving wave on its surface.
/// </summary>
internal static class ThirstMeter
{
    private const int FillSegments = 20;
    private const float FillRadiusInset = 0.7f;
    private const float FillAlpha = 0.9f;
    private const float WaveAmplitude = 0.085f;
    private const float WaveFrequency = 5.2f;
    private const float WavePhaseSpeed = 0.32f;
    private const int GainWaveHoldFrames = 24;

    private static readonly Color WaterColor = new(0.03f, 0.9f, 0.95f);

    private sealed class MeterState
    {
        public MeterState()
        {
        }

        public bool UseFixedWater;
        public float DisplayWater;
        public float TargetWater;
        public int SleepConsumeSteps;
        public int SleepConsumeDelay;
        public SleepAndDeathScreen SleepScreen;

        public bool GameplayInitialized;
        public float LastActualWater;
        public float WaveStrength;
        public float WavePhase;
        public int GainWaveFrames;
    }

    private sealed class RejectState
    {
        public RejectState()
        {
        }

        public int Counter;
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

    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, MeterState> MeterStates = new();
    private static readonly ConditionalWeakTable<global::HUD.FoodMeter.MeterCircle, WaterFill> CircleFills = new();
    private static readonly ConditionalWeakTable<Player, RejectState> RejectStates = new();

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.HUD.FoodMeter.Update += FoodMeter_Update;
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
        state.TargetWater = ThirstStore.GetSaved(saveState);
        state.DisplayWater = state.TargetWater;
        state.SleepConsumeSteps = 0;
        state.SleepConsumeDelay = 0;
        state.WaveStrength = 0f;

        if (animateHibernateCost)
        {
            state.DisplayWater = Mathf.Min(
                ThirstConstants.MaxWater,
                state.TargetWater + ThirstConstants.HibernateCost);

            state.SleepConsumeSteps = Mathf.RoundToInt(
                Mathf.Max(0f, state.DisplayWater - state.TargetWater));
            state.SleepConsumeDelay = 65;
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
        state.TargetWater = Mathf.Clamp(water, 0f, ThirstConstants.MaxWater);
        state.DisplayWater = state.TargetWater;
        state.SleepConsumeSteps = 0;
        state.SleepConsumeDelay = 0;
        state.WaveStrength = 0f;
    }

    public static void ShowHydrationGain(Player player)
    {
        if (player == null)
        {
            return;
        }

        player.showKarmaFoodRainTime = Mathf.Max(
            player.showKarmaFoodRainTime,
            ThirstConstants.HydrationGainHudHoldFrames);
    }

    public static void TryReject(Player player)
    {
        if (player == null)
        {
            return;
        }

        RejectStates.GetOrCreateValue(player).Counter = ThirstConstants.RejectHudHoldFrames;
        player.showKarmaFoodRainTime = Mathf.Max(
            player.showKarmaFoodRainTime,
            ThirstConstants.RejectHudHoldFrames);
    }

    private static void FoodMeter_Update(On.HUD.FoodMeter.orig_Update orig, global::HUD.FoodMeter self)
    {
        orig(self);

        if (MeterStates.TryGetValue(self, out MeterState fixedState) &&
            fixedState.UseFixedWater &&
            fixedState.SleepScreen != null &&
            fixedState.SleepConsumeSteps > 0 &&
            fixedState.SleepScreen.AllowFoodMeterTick)
        {
            fixedState.SleepConsumeDelay--;

            if (fixedState.SleepConsumeDelay <= 0)
            {
                fixedState.DisplayWater = Mathf.Max(fixedState.TargetWater, fixedState.DisplayWater - 1f);
                fixedState.SleepConsumeSteps--;
                fixedState.SleepConsumeDelay = 40;
                self.hud.PlaySound(SoundID.HUD_Food_Meter_Deplete_Plop_A);
            }
        }

        if (self?.hud?.owner is Player player &&
            TryGetPlayerGame(player, out RainWorldGame game) &&
            game.IsStorySession &&
            !player.isSlugpup)
        {
            UpdateGameplayAnimation(self, player);

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
        float actualWater = Mathf.Clamp(thirst.Water, 0f, ThirstConstants.MaxWater);

        if (!state.GameplayInitialized)
        {
            state.GameplayInitialized = true;
            state.DisplayWater = actualWater;
            state.TargetWater = actualWater;
            state.LastActualWater = actualWater;
            state.WaveStrength = 0f;
            state.WavePhase = 0f;
            state.GainWaveFrames = 0;
            return;
        }

        bool gainedWater = actualWater > state.LastActualWater + 0.0001f;

        if (gainedWater || thirst.IsDrinking)
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
            state.DisplayWater = actualWater;
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

        float targetWave = animateGain ? 1f : 0f;
        float waveLerp = targetWave > state.WaveStrength ? 0.25f : 0.12f;
        state.WaveStrength = Mathf.Lerp(state.WaveStrength, targetWave, waveLerp);

        if (!animateGain && state.WaveStrength < 0.01f)
        {
            state.WaveStrength = 0f;
        }

        state.LastActualWater = actualWater;
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
                out float wavePhase,
                out bool continuousFill) ||
            self.circles == null ||
            self.circles.Length < 2 ||
            self.circles[0]?.sprite == null ||
            self.circles[1]?.sprite == null)
        {
            fill.Mesh.isVisible = false;
            return;
        }

        float waterLevel = GetPipWaterLevel(water, self.number, continuousFill);

        // Hydration visibility is tied to the FoodMeter as a whole rather than
        // the food-fill sprite, because the fill sprite can fade during eating.
        // Size follows the animated OUTER circle. Scale the inset with that
        // circle too, matching vanilla's own quarter-pip scale behavior instead
        // of leaving a fixed-pixel gap while the circle pops larger/smaller.
        float alpha = Mathf.Clamp01(self.meter.fade) * FillAlpha;
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
        out float wavePhase,
        out bool continuousFill)
    {
        water = 0f;
        waveStrength = 0f;
        wavePhase = 0f;
        continuousFill = false;

        if (meter == null || meter.IsPupFoodMeter)
        {
            return false;
        }

        if (MeterStates.TryGetValue(meter, out MeterState fixedState) &&
            fixedState.UseFixedWater)
        {
            water = Mathf.Clamp(fixedState.DisplayWater, 0f, ThirstConstants.MaxWater);
            return true;
        }

        if (meter.hud?.owner is Player player &&
            TryGetPlayerGame(player, out RainWorldGame game) &&
            game.IsStorySession &&
            !player.isSlugpup)
        {
            MeterState state = MeterStates.GetOrCreateValue(meter);
            ThirstState thirst = ThirstStore.For(player);

            if (!state.GameplayInitialized)
            {
                float actual = Mathf.Clamp(thirst.Water, 0f, ThirstConstants.MaxWater);
                state.GameplayInitialized = true;
                state.DisplayWater = actual;
                state.TargetWater = actual;
                state.LastActualWater = actual;
            }

            water = Mathf.Clamp(state.DisplayWater, 0f, ThirstConstants.MaxWater);
            waveStrength = state.WaveStrength;
            wavePhase = state.WavePhase;
            continuousFill = state.WaveStrength > 0.02f ||
                             state.TargetWater > state.DisplayWater + 0.002f;
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

    private static float GetPipWaterLevel(float totalWater, int pipNumber, bool continuousFill)
    {
        if (pipNumber < 0 || pipNumber >= ThirstConstants.MaxPips)
        {
            return 0f;
        }

        float localWater = Mathf.Clamp01(totalWater - pipNumber);

        if (continuousFill)
        {
            return localWater;
        }

        if (localWater >= 0.999f)
        {
            return 1f;
        }

        if (localWater >= 0.5f)
        {
            return 0.5f;
        }

        return 0f;
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
