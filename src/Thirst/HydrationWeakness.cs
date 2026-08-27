using System.Runtime.CompilerServices;

namespace DryCycle.Thirst;

/// <summary>
/// Applies Rain World's normal malnourished/weakness state when hydration is
/// critically low. DryCycle uses the temporary malnourishedByCreature channel
/// so recovering hydration does not clear a real starvation-cycle malnourished
/// state that came from vanilla Rain World.
/// </summary>
internal static class HydrationWeakness
{
    private sealed class WeaknessState
    {
        public bool ThresholdActive;
        public bool AppliedCreatureMalnourishment;
    }

    private static readonly ConditionalWeakTable<Player, WeaknessState> States = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!IsStoryPlayer(self))
        {
            return;
        }

        ThirstState thirst = ThirstStore.For(self);
        WeaknessState state = States.GetOrCreateValue(self);
        bool shouldBeWeak = thirst.WaterValue <= ThirstConstants.WeaknessWaterValueThreshold + 0.001f;

        if (shouldBeWeak)
        {
            state.ThresholdActive = true;

            // If vanilla starvation or another system already has the player in
            // Malnourished, do not claim ownership of that state. Otherwise use
            // malnourishedByCreature as DryCycle's temporary weakness flag.
            if (!self.Malnourished)
            {
                self.SetMalnourished(m: true, malnourishedByCreature: true);
                state.AppliedCreatureMalnourishment = true;
            }

            return;
        }

        if (!state.ThresholdActive)
        {
            return;
        }

        // Only remove the temporary creature-style malnourishment if DryCycle
        // was the code that applied it. SetMalnourished(false, true) preserves a
        // real vanilla starvation malnourished flag.
        if (state.AppliedCreatureMalnourishment &&
            self.slugcatStats != null &&
            self.slugcatStats.malnourishedByCreature)
        {
            self.SetMalnourished(m: false, malnourishedByCreature: true);
        }

        state.ThresholdActive = false;
        state.AppliedCreatureMalnourishment = false;
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
