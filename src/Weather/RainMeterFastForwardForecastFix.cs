using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// DevTools raises RainWorldGame.framesPerSecond to 400 while S is held. At that
/// update rate vanilla HUDCircle interpolation can briefly render a forecast pip as
/// a solid white circle after DryCycle has already drawn its colored center behind
/// the circle. This final overlay pass keeps the weather color visible without
/// changing the normal RainMeter or weather simulation.
/// </summary>
internal static class RainMeterFastForwardForecastFix
{
    private const float OverlayDiameterPixels = 4.45f;

    private sealed class MeterState
    {
        internal readonly global::HUD.RainMeter Meter;
        internal FSprite[] Fills = Array.Empty<FSprite>();
        internal float BaseScale;

        internal MeterState(global::HUD.RainMeter meter)
        {
            Meter = meter;
        }
    }

    private static ConditionalWeakTable<global::HUD.RainMeter, MeterState> _states = new();
    private static readonly List<MeterState> LiveStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.HUD.RainMeter.Draw += RainMeter_Draw;
        On.HUD.RainMeter.ClearSprites += RainMeter_ClearSprites;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.HUD.RainMeter.Draw -= RainMeter_Draw;
        On.HUD.RainMeter.ClearSprites -= RainMeter_ClearSprites;

        for (int i = LiveStates.Count - 1; i >= 0; i--)
        {
            RemoveState(LiveStates[i]);
        }

        LiveStates.Clear();
        _states = new ConditionalWeakTable<global::HUD.RainMeter, MeterState>();
        _enabled = false;
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        // This runtime is enabled after RainMeterRoundPipRuntime, so orig completes
        // the authoritative DryCycle/vanilla draw chain before this last overlay.
        orig(self, timeStacker);

        if (!TryGetContext(
                self,
                out World world,
                out WorldClock clock,
                out WeatherPhaseSchedule schedule))
        {
            Hide(self);
            return;
        }

        MeterState state = GetOrCreateState(self);
        if (state == null)
        {
            return;
        }

        EnsureCapacity(state, self.circles.Length);
        DrawFastForwardForecast(
            state,
            clock,
            schedule,
            Mathf.Clamp01(timeStacker));
    }

    private static void RainMeter_ClearSprites(
        On.HUD.RainMeter.orig_ClearSprites orig,
        global::HUD.RainMeter self)
    {
        if (self != null && _states.TryGetValue(self, out MeterState state))
        {
            RemoveState(state);
            LiveStates.Remove(state);
            _states.Remove(self);
        }

        orig(self);
    }

    private static bool TryGetContext(
        global::HUD.RainMeter meter,
        out World world,
        out WorldClock clock,
        out WeatherPhaseSchedule schedule)
    {
        world = null;
        clock = null;
        schedule = null;

        Player player = meter?.hud?.owner as Player;
        world = player?.abstractCreature?.world;
        RainWorldGame game = world?.game;
        if (game == null ||
            !game.IsStorySession ||
            !game.devToolsActive ||
            !Input.GetKey("s") ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out clock))
        {
            return false;
        }

        WeatherScheduleRuntime.Synchronize(world);
        if (!WeatherScheduleRuntime.TryGetCurrentSchedule(
                world,
                out schedule) ||
            schedule == null)
        {
            return false;
        }

        WeatherSchedulePhase phase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
        return schedule.Phase == phase;
    }

    private static MeterState GetOrCreateState(global::HUD.RainMeter meter)
    {
        if (meter?.circles == null || meter.hud?.rainWorld == null)
        {
            return null;
        }

        if (_states.TryGetValue(meter, out MeterState existing))
        {
            return existing;
        }

        MeterState state = new(meter);
        _states.Add(meter, state);
        LiveStates.Add(state);
        return state;
    }

    private static void EnsureCapacity(MeterState state, int count)
    {
        if (state == null || count <= state.Fills.Length)
        {
            return;
        }

        FSprite[] old = state.Fills;
        FSprite[] fills = new FSprite[count];
        Array.Copy(old, fills, old.Length);

        for (int i = old.Length; i < fills.Length; i++)
        {
            FSprite anchor = state.Meter?.circles != null && i < state.Meter.circles.Length
                ? state.Meter.circles[i]?.sprite
                : null;
            FContainer container = anchor?.container;
            if (container == null)
            {
                continue;
            }

            string elementName = Futile.atlasManager.DoesContainElementWithName("Circle20")
                ? "Circle20"
                : "deerEyeB";
            FAtlasElement element = Futile.atlasManager.GetElementWithName(elementName);
            float diameter = Mathf.Max(
                1f,
                Mathf.Max(element.sourcePixelSize.x, element.sourcePixelSize.y));
            state.BaseScale = OverlayDiameterPixels / diameter;

            FSprite fill = new(elementName)
            {
                shader = state.Meter.hud.rainWorld.Shaders["Basic"],
                isVisible = false
            };
            container.AddChild(fill);
            fills[i] = fill;
        }

        state.Fills = fills;
    }

    private static void DrawFastForwardForecast(
        MeterState state,
        WorldClock clock,
        WeatherPhaseSchedule schedule,
        float timeStacker)
    {
        global::HUD.RainMeter meter = state.Meter;
        int capacity = Math.Min(meter.circles.Length, state.Fills.Length);
        int activePips = Math.Min(
            capacity,
            WeatherPhaseScheduler.FullPipsFromTicks(clock.CurrentHalfLength));
        float hudFade = Mathf.Clamp01(
            Mathf.Lerp(meter.lastFade, meter.fade, timeStacker));
        float sizeFade = hudFade * hudFade;

        float animationSeconds = 0f;
        Player player = meter.hud?.owner as Player;
        if (player?.abstractCreature?.world?.game != null)
        {
            animationSeconds = player.abstractCreature.world.game.clock / 40f;
        }

        for (int i = 0; i < capacity; i++)
        {
            if (state.Fills[i] != null)
            {
                state.Fills[i].isVisible = false;
            }
        }

        for (int chronologicalPip = 1; chronologicalPip <= activePips; chronologicalPip++)
        {
            if (!TryGetMarker(schedule, chronologicalPip, out WeatherForecastVisualKind kind))
            {
                continue;
            }

            float remaining = 1f - PipElapsed(clock, chronologicalPip);
            if (remaining <= 0.001f)
            {
                continue;
            }

            int index = clock.IsNight
                ? chronologicalPip - 1
                : activePips - chronologicalPip;
            if (index < 0 || index >= capacity)
            {
                continue;
            }

            global::HUD.HUDCircle circle = meter.circles[index];
            FSprite fill = state.Fills[index];
            if (circle?.sprite == null || fill == null || !circle.sprite.isVisible)
            {
                continue;
            }

            WeatherForecastVisualStyle style = WeatherForecastVisualCatalog.Get(kind);
            Vector2 center = Vector2.Lerp(circle.lastPos, circle.pos, timeStacker);
            if (style.Animation == WeatherForecastAnimation.VerticalShake)
            {
                float seed = chronologicalPip * 0.371f;
                float waveA = Mathf.Sin((animationSeconds * 6.2f + seed) * Mathf.PI * 2f);
                float waveB = Mathf.Sin((animationSeconds * 9.7f + seed * 1.73f) * Mathf.PI * 2f);
                center.y += (waveA * 0.72f + waveB * 0.28f) *
                            style.ShakeAmplitudePixels * sizeFade;
            }

            fill.SetPosition(center);
            fill.scale = state.BaseScale * sizeFade;
            fill.color = style.FillColor;
            fill.alpha = Mathf.Clamp01(hudFade * remaining);
            fill.isVisible = true;

            // The overlay is intentionally above the interpolated vanilla HUDCircle.
            // Its smaller diameter leaves the white outer rim readable even if the
            // underlying circle was rendered solid during 400-FPS dev fast-forward.
            fill.MoveInFrontOfOtherNode(circle.sprite);
        }
    }

    private static bool TryGetMarker(
        WeatherPhaseSchedule schedule,
        int chronologicalPip,
        out WeatherForecastVisualKind kind)
    {
        kind = WeatherForecastVisualKind.None;
        if (schedule == null || chronologicalPip < 1)
        {
            return false;
        }

        int zeroBasedPip = chronologicalPip - 1;
        for (int i = 0; i < schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            if (scheduled?.Candidate == null ||
                zeroBasedPip < scheduled.StartPip ||
                zeroBasedPip >= scheduled.EndPipExclusive)
            {
                continue;
            }

            return WeatherForecastVisualCatalog.TryResolve(
                       scheduled.Candidate.Id,
                       scheduled.Candidate.Kind,
                       out kind) &&
                   kind != WeatherForecastVisualKind.None;
        }

        return false;
    }

    private static float PipElapsed(WorldClock clock, int chronologicalPip)
    {
        if (clock == null || chronologicalPip < 1)
        {
            return 1f;
        }

        long phaseTicks = (long)Math.Round(
            Mathf.Clamp01(clock.HalfProgress) * clock.CurrentHalfLength);
        long start = (long)(chronologicalPip - 1) * WeatherPhaseScheduler.PipTicks;
        float elapsed = (phaseTicks - start) / (float)WeatherPhaseScheduler.PipTicks;
        float t = Mathf.Clamp01(elapsed);
        t = t * t * (3f - 2f * t);
        return t;
    }

    private static void Hide(global::HUD.RainMeter meter)
    {
        if (meter != null && _states.TryGetValue(meter, out MeterState state))
        {
            for (int i = 0; i < state.Fills.Length; i++)
            {
                if (state.Fills[i] != null)
                {
                    state.Fills[i].isVisible = false;
                }
            }
        }
    }

    private static void RemoveState(MeterState state)
    {
        if (state?.Fills == null)
        {
            return;
        }

        for (int i = 0; i < state.Fills.Length; i++)
        {
            state.Fills[i]?.RemoveFromContainer();
        }
    }
}
