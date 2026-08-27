using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

/// <summary>
/// Draws the cyan hydration hibernation divider inside the vanilla FoodMeter.
/// Its position comes from the current character's SlugBase WaterPips feature
/// (or DryCycle's built-in defaults for vanilla slugcats).
///
/// Rain World's own survival-limit divider does not merely draw a line between
/// two normally spaced circles. MeterCircle.XAdd creates an extra half-circle
/// distance on the right side of the divider, leaving visible air on both sides.
/// DryCycle mirrors that layout by adding the same half-distance to every circle
/// on the right side of the hydration divider.
/// </summary>
internal static class HydrationDivider
{
    private const float LineThickness = 2f;
    private const float LineHeight = 34.5f;

    // Keep this exactly in sync with the full-water material in ThirstMeter.
    private static readonly Color WaterColor = new(0.03f, 0.9f, 0.95f);

    private sealed class DividerSprite
    {
        public DividerSprite(FContainer container)
        {
            Sprite = new FSprite("pixel")
            {
                color = WaterColor,
                alpha = 0f,
                isVisible = false,
                scaleX = LineThickness,
                scaleY = LineHeight
            };

            container.AddChild(Sprite);
        }

        public readonly FSprite Sprite;

        public void Hide()
        {
            Sprite.isVisible = false;
        }

        public void Clear()
        {
            Sprite.RemoveFromContainer();
        }
    }

    private static readonly ConditionalWeakTable<global::HUD.FoodMeter, DividerSprite> Dividers = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.HUD.FoodMeter.Draw += FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites += FoodMeter_ClearSprites;
        On.HUD.FoodMeter.MeterCircle.DrawPos += MeterCircle_DrawPos;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.HUD.FoodMeter.Draw -= FoodMeter_Draw;
        On.HUD.FoodMeter.ClearSprites -= FoodMeter_ClearSprites;
        On.HUD.FoodMeter.MeterCircle.DrawPos -= MeterCircle_DrawPos;
    }

    private static Vector2 MeterCircle_DrawPos(
        On.HUD.FoodMeter.MeterCircle.orig_DrawPos orig,
        global::HUD.FoodMeter.MeterCircle self,
        float timeStacker)
    {
        Vector2 result = orig(self, timeStacker);

        if (self?.meter != null &&
            TryGetDividerPips(self.meter, out int dividerPips) &&
            self.number >= dividerPips)
        {
            // Vanilla's survival-limit gap is exactly CircleDistance / 2.
            // Normal FoodMeter distance is 30 px, so this contributes 15 px of
            // additional space to the right side of the cyan divider.
            result.x += self.meter.CircleDistance(timeStacker) / 2f;
        }

        return result;
    }

    private static void FoodMeter_Draw(
        On.HUD.FoodMeter.orig_Draw orig,
        global::HUD.FoodMeter self,
        float timeStacker)
    {
        orig(self, timeStacker);

        if (TryGetDividerPips(self, out int dividerPips) &&
            self.lineSprite != null &&
            self.ShowSurvivalLimit > dividerPips)
        {
            // The vanilla white survival line is positioned directly from the
            // FoodMeter origin rather than from MeterCircle.DrawPos. Since all
            // circles to its right were shifted by the hydration gap above, move
            // the white line by the same amount so its own spacing stays intact.
            self.lineSprite.x += self.CircleDistance(timeStacker) / 2f;
        }

        DrawDivider(self, timeStacker);
    }

    private static void FoodMeter_ClearSprites(
        On.HUD.FoodMeter.orig_ClearSprites orig,
        global::HUD.FoodMeter self)
    {
        if (self != null && Dividers.TryGetValue(self, out DividerSprite divider))
        {
            divider.Clear();
            Dividers.Remove(self);
        }

        orig(self);
    }

    private static void DrawDivider(global::HUD.FoodMeter meter, float timeStacker)
    {
        if (!TryGetDividerPips(meter, out int dividerPips) ||
            meter.circles == null ||
            meter.circles.Count == 0 ||
            dividerPips > meter.circles.Count)
        {
            Hide(meter);
            return;
        }

        int leftIndex = dividerPips - 1;
        if (leftIndex < 0 || leftIndex >= meter.circles.Count)
        {
            Hide(meter);
            return;
        }

        global::HUD.FoodMeter.MeterCircle left = meter.circles[leftIndex];
        Vector2 center;

        if (dividerPips < meter.circles.Count)
        {
            global::HUD.FoodMeter.MeterCircle right = meter.circles[dividerPips];

            // MeterCircle_DrawPos has already inserted the same half-distance gap
            // used by Rain World's own survival-limit divider. Taking the midpoint
            // therefore places the cyan line in the middle of that enlarged gap.
            center = (left.DrawPos(timeStacker) + right.DrawPos(timeStacker)) * 0.5f;
        }
        else
        {
            // If WaterPips equals the complete meter length, there is no circle on
            // the right to average with. Reproduce the same 22.5 px placement on
            // a normal 30 px meter: half a normal step plus half the extra gap.
            center = left.DrawPos(timeStacker) +
                     new Vector2(meter.CircleDistance(timeStacker) * 0.75f, 0f);
        }

        float alpha = Mathf.Clamp01(Mathf.Lerp(meter.lastFade, meter.fade, timeStacker));

        if (alpha <= 0.001f)
        {
            Hide(meter);
            return;
        }

        DividerSprite divider = Ensure(meter);
        if (divider == null)
        {
            return;
        }

        divider.Sprite.x = center.x;
        divider.Sprite.y = center.y;
        divider.Sprite.scaleX = LineThickness;
        divider.Sprite.scaleY = LineHeight;
        divider.Sprite.color = WaterColor;
        divider.Sprite.alpha = alpha;
        divider.Sprite.isVisible = true;
    }

    private static bool TryGetDividerPips(global::HUD.FoodMeter meter, out int dividerPips)
    {
        dividerPips = 0;

        if (meter == null || meter.IsPupFoodMeter || meter.hud?.owner == null)
        {
            return false;
        }

        global::HUD.HUD.OwnerType ownerType = meter.hud.owner.GetOwnerType();

        if (ownerType == global::HUD.HUD.OwnerType.Player)
        {
            if (meter.hud.owner is not Player player || player.isNPC)
            {
                return false;
            }

            RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
            if (game == null || !game.IsStorySession)
            {
                return false;
            }

            dividerPips = SlugBaseHydrationFeatures.GetWaterPips(player);
            return dividerPips > 0;
        }

        if (ownerType == global::HUD.HUD.OwnerType.SleepScreen &&
            meter.hud.owner is SleepAndDeathScreen sleepScreen &&
            sleepScreen.saveState?.saveStateNumber != null)
        {
            dividerPips = SlugBaseHydrationFeatures.GetWaterPips(sleepScreen.saveState.saveStateNumber);
            return dividerPips > 0;
        }

        if (ownerType == global::HUD.HUD.OwnerType.CharacterSelect &&
            meter.hud.owner is SlugcatSelectMenu.SlugcatPageContinue page &&
            page.slugcatNumber != null)
        {
            dividerPips = SlugBaseHydrationFeatures.GetWaterPips(page.slugcatNumber);
            return dividerPips > 0;
        }

        return false;
    }

    private static DividerSprite Ensure(global::HUD.FoodMeter meter)
    {
        if (meter?.fContainer == null)
        {
            return null;
        }

        if (Dividers.TryGetValue(meter, out DividerSprite existing))
        {
            return existing;
        }

        DividerSprite created = new(meter.fContainer);
        Dividers.Add(meter, created);
        return created;
    }

    private static void Hide(global::HUD.FoodMeter meter)
    {
        if (meter != null && Dividers.TryGetValue(meter, out DividerSprite divider))
        {
            divider.Hide();
        }
    }
}
