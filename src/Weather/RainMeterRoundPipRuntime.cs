using DryCycle.DayNight;

namespace DryCycle.Weather;

/// <summary>
/// Rain World's HUDCircle snaps a completely solid timer pip to the tiny Circle4
/// atlas element. At this scale that pixel sprite can read as a diamond. DryCycle
/// keeps the exact same radius/timing state but asks HUDCircle to stay on its native
/// procedural VectorCircleFadable path instead, producing a genuinely round pip.
/// Disabled regions are never touched and therefore retain their original HUD logic.
/// </summary>
internal static class RainMeterRoundPipRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.HUD.RainMeter.Draw += RainMeter_Draw;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.HUD.RainMeter.Draw -= RainMeter_Draw;
        _enabled = false;
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        orig(self, timeStacker);

        Player player = self?.hud?.owner as Player;
        World world = player?.abstractCreature?.world;
        if (self?.circles == null ||
            world == null ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out _))
        {
            return;
        }

        for (int i = 0; i < self.circles.Length; i++)
        {
            global::HUD.HUDCircle circle = self.circles[i];
            if (circle?.sprite == null ||
                !circle.sprite.isVisible ||
                circle.snapGraphic != global::HUD.HUDCircle.SnapToGraphic.Circle4)
            {
                continue;
            }

            // Do not change radius, thickness, position, fade or color. Only prevent
            // the fully-solid state from snapping to Circle4. Calling Draw again
            // updates the same FSprite before Futile renders the frame; it does not
            // create or layer a second HUD circle.
            circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.None;
            circle.snapRad = -1f;
            circle.snapThickness = -1f;
            circle.Draw(timeStacker);
        }
    }
}
