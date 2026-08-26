using System;
using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

/// <summary>
/// Hydration renderer for the vanilla FoodMeter.
///
/// DryCycle does not create a second row of HUD circles. Hydration is packed
/// into the vanilla food pips themselves. Water is distributed from left to
/// right across the food meter, and every food pip has three visual water
/// states: empty, lower-half full, or completely full. Vanilla full/quarter
/// food graphics stay on top of the cyan hydration material.
/// </summary>
internal static class ThirstMeter
{
    private const int FillBands = 16;
    private const float FillRadiusInset = 0.7f;
    private const float FillAlpha = 0.9f;

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
    }

    public static void TryReject(Player player)
    {
        if (player != null)
        {
            RejectStates.GetOrCreateValue(player).Counter = 55;
        }
    }

    private static void FoodMeter_Update(On.HUD.FoodMeter.orig_Update orig, global::HUD.FoodMeter self)
    {
        orig(self);

        if (MeterStates.TryGetValue(self, out MeterState state) &&
            state.UseFixedWater &&
            state.SleepScreen != null &&
            state.SleepConsumeSteps > 0 &&
            state.SleepScreen.AllowFoodMeterTick)
        {
            state.SleepConsumeDelay--;

            if (state.SleepConsumeDelay <= 0)
            {
                state.DisplayWater = Mathf.Max(state.TargetWater, state.DisplayWater - 1f);
                state.SleepConsumeSteps--;
                state.SleepConsumeDelay = 40;
                self.hud.PlaySound(SoundID.HUD_Food_Meter_Deplete_Plop_A);
            }
        }

        if (self?.hud?.owner is Player player &&
            RejectStates.TryGetValue(player, out RejectState reject) &&
            reject.Counter > 0)
        {
            reject.Counter--;
        }
    }

    private static void MeterCircle_AddCircles(
        On.HUD.FoodMeter.MeterCircle.orig_AddCircles orig,
        global::HUD.FoodMeter.MeterCircle self)
    {
        orig(self);
        EnsureFill(self);
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

        if (!TryGetDisplayWater(self.meter, out float water) ||
            self.circles == null ||
            self.circles.Length < 2 ||
            self.circles[0]?.sprite == null ||
            self.circles[1]?.sprite == null)
        {
            fill.Mesh.isVisible = false;
            return;
        }

        // Hydration is distributed pip-by-pip from left to right. Each pip is
        // deliberately quantized to 0, 1/2, or 1 so water uses two halves while
        // vanilla food continues to use its four quarter-pip states.
        float waterLevel = GetPipWaterLevel(water, self.number);
        float alpha = self.circles[0].sprite.alpha * FillAlpha;
        float outerRadius = Mathf.Lerp(
            self.circles[0].lastRad,
            self.circles[0].rad,
            timeStacker);
        float radius = Mathf.Max(0f, outerRadius - FillRadiusInset);

        if (!self.circles[0].sprite.isVisible ||
            alpha <= 0.001f ||
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
            waterLevel);

        fill.Mesh.color = color;
        fill.Mesh.alpha = alpha;
        fill.Mesh.isVisible = true;

        // Vanilla circle[0] is the food-meter outline layer and circle[1] is
        // the filled-food layer. Hydration sits behind the food fill but inside
        // the outline. Because the hydration radius is slightly larger than the
        // normal food fill, a thin cyan rim can still be visible around a full
        // food pip, matching the supplied design reference.
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
            // Quarter food is a separate vanilla sprite; explicitly keep it in
            // front of the hydration material.
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

    private static bool TryGetDisplayWater(global::HUD.FoodMeter meter, out float water)
    {
        water = 0f;

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
            player.room?.game != null &&
            player.room.game.IsStorySession &&
            !player.isSlugpup)
        {
            water = ThirstStore.For(player).Water;
            return true;
        }

        return false;
    }

    private static float GetPipWaterLevel(float totalWater, int pipNumber)
    {
        if (pipNumber < 0 || pipNumber >= ThirstConstants.MaxPips)
        {
            return 0f;
        }

        float localWater = Mathf.Clamp01(totalWater - pipNumber);

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
        float level)
    {
        float surfaceY = Mathf.Lerp(-1f, 1f, Mathf.Clamp01(level));

        for (int i = 0; i <= FillBands; i++)
        {
            float t = i / (float)FillBands;
            float normalizedY = Mathf.Lerp(-1f, surfaceY, t);
            float halfWidth = Mathf.Sqrt(Mathf.Max(0f, 1f - normalizedY * normalizedY)) * radius;
            float y = center.y + normalizedY * radius;

            mesh.MoveVertice(i * 2, new Vector2(center.x - halfWidth, y));
            mesh.MoveVertice(i * 2 + 1, new Vector2(center.x + halfWidth, y));
        }
    }

    private static TriangleMesh.Triangle[] BuildTriangles()
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[FillBands * 2];

        for (int i = 0; i < FillBands; i++)
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
