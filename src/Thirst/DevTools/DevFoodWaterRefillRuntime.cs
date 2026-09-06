using DryCycle.HUD;
using UnityEngine;

namespace DryCycle.Thirst;

/// <summary>
/// Extends Rain World's existing DevTools Q refill with one independent hydration pip.
///
/// Food remains entirely owned by vanilla Player.Update. This hook only observes the
/// same rising-edge condition before vanilla consumes FLYEATBUTTON, then adds water
/// after the rest of Player.Update has completed. Food and water therefore never gate
/// each other: a full food meter can still gain water, and a full water meter does not
/// interfere with vanilla food refill.
/// </summary>
internal static class DevFoodWaterRefillRuntime
{
    private const float WaterPerDevRefill = 1f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.Update += Player_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.Update -= Player_Update;
        _enabled = false;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        // Vanilla updates FLYEATBUTTON inside Player.Update, so the rising-edge state
        // must be captured before calling orig. Keep the eligibility test deliberately
        // aligned with vanilla's own DevTools Q food refill.
        bool refillWater = ShouldMirrorVanillaDevRefill(self);

        orig(self, eu);

        if (!refillWater)
        {
            return;
        }

        float beforeWater = ThirstStore.GetRuntimeWater(self);
        if (!ThirstStore.AddRuntime(self, WaterPerDevRefill))
        {
            // Water is already full. Vanilla food refill has already run independently.
            return;
        }

        float afterWater = ThirstStore.GetRuntimeWater(self);
        ThirstMeter.ShowHydrationGain(self, beforeWater, afterWater);
    }

    private static bool ShouldMirrorVanillaDevRefill(Player player)
    {
        if (player == null || player.isNPC || player.room == null)
        {
            return false;
        }

        RainWorldGame game = player.room.game;
        if (game == null || !game.IsStorySession || !game.devToolsActive)
        {
            return false;
        }

        if (!Input.GetKey(KeyCode.Q) || player.FLYEATBUTTON)
        {
            return false;
        }

        // Match vanilla's Jolly behavior exactly: with coop available, only the current
        // FirstAlivePlayer receives the developer Q refill.
        if (ModManager.CoopAvailable)
        {
            AbstractCreature firstAlivePlayer = game.FirstAlivePlayer;
            if (firstAlivePlayer == null || firstAlivePlayer != player.abstractCreature)
            {
                return false;
            }
        }

        return true;
    }
}
