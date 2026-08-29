using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Small in-game developer readout for temperature/hydration tuning.
/// Press O during gameplay to toggle it. The readout is attached to the normal
/// FoodMeter container and is positioned directly below the hydration pips.
/// </summary>
internal static class TemperatureDeveloperHud
{
    private const float LabelYOffset = 24f;
    private const float LabelScale = 0.62f;
    private const int KeepHudVisibleFrames = 4;

    private sealed class LabelState
    {
        internal FLabel Label;
    }

    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, LabelState> Labels = new();
    private static bool _enabled;
    private static bool _developerMode;

    internal static bool DeveloperMode => _developerMode;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        _developerMode = false;
        On.RainWorldGame.Update += RainWorldGame_Update;
        On.HUD.FoodMeter.Draw += FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites += FoodMeter_ClearSprites;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        _developerMode = false;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.HUD.FoodMeter.Draw -= FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites -= FoodMeter_ClearSprites;
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame game)
    {
        orig(game);

        if (!_enabled || game == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.O))
        {
            _developerMode = !_developerMode;
            global::DryCycle.Plugin.Logger?.LogInfo(
                $"Temperature developer mode: {(_developerMode ? "ON" : "OFF")}");
        }

        if (!_developerMode || game.Players == null)
        {
            return;
        }

        // Keep the normal lower-left HUD visible while debugging so the readout and
        // the hydration pips stay together instead of the text floating by itself.
        for (int i = 0; i < game.Players.Count; i++)
        {
            Player player = game.Players[i]?.realizedCreature as Player;
            if (player == null || player.isNPC)
            {
                continue;
            }

            player.showKarmaFoodRainTime = Mathf.Max(
                player.showKarmaFoodRainTime,
                KeepHudVisibleFrames);
        }
    }

    private static void FoodMeter_Draw(
        On.HUD.FoodMeter.orig_Draw orig,
        global::HUD.FoodMeter meter,
        float timeStacker)
    {
        orig(meter, timeStacker);

        if (!ShouldDraw(meter, out Player player))
        {
            HideLabel(meter);
            return;
        }

        LabelState state = EnsureLabel(meter);
        if (state?.Label == null)
        {
            return;
        }

        Vector2 anchor = meter.circles[0].DrawPos(timeStacker);
        state.Label.text = WaterLossRateDebug.BuildLine(player);
        state.Label.x = anchor.x - 10f;
        state.Label.y = anchor.y - LabelYOffset;
        state.Label.alpha = 1f;
        state.Label.isVisible = true;
    }

    private static void FoodMeter_ClearSprites(
        On.HUD.FoodMeter.orig_ClearSprites orig,
        global::HUD.FoodMeter meter)
    {
        if (meter != null && Labels.TryGetValue(meter, out LabelState state))
        {
            state.Label?.RemoveFromContainer();
            Labels.Remove(meter);
        }

        orig(meter);
    }

    private static bool ShouldDraw(global::HUD.FoodMeter meter, out Player player)
    {
        player = null;

        if (!_developerMode ||
            meter == null ||
            meter.IsPupFoodMeter ||
            meter.circles == null ||
            meter.circles.Count == 0 ||
            meter.hud?.owner is not Player owner ||
            owner.isNPC ||
            owner.room?.game == null ||
            !owner.room.game.IsStorySession)
        {
            return false;
        }

        player = owner;
        return true;
    }

    private static LabelState EnsureLabel(global::HUD.FoodMeter meter)
    {
        LabelState state = Labels.GetOrCreateValue(meter);
        if (state.Label != null)
        {
            return state;
        }

        state.Label = new FLabel(Custom.GetFont(), string.Empty)
        {
            alignment = FLabelAlignment.Left,
            anchorX = 0f,
            anchorY = 0.5f,
            color = Color.white,
            alpha = 0f,
            isVisible = false,
            scale = LabelScale
        };

        meter.fContainer.AddChild(state.Label);
        return state;
    }

    private static void HideLabel(global::HUD.FoodMeter meter)
    {
        if (meter != null &&
            Labels.TryGetValue(meter, out LabelState state) &&
            state.Label != null)
        {
            state.Label.alpha = 0f;
            state.Label.isVisible = false;
        }
    }
}
