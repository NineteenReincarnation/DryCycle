using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Adds visibly moving haze inside Fog/DenseFog RainMeter markers. The authoritative
/// colored circle remains owned by RainMeterRoundPipRuntime; this pass only overlays
/// shader-driven fog sheets behind the white ring.
///
/// Rain World's already-compiled NewVultureSmoke shader is used deliberately instead
/// of introducing a new AssetBundle just for a five-pixel HUD marker. Each pip gets a
/// stable phase seed, while several sheets travel across it at different speeds and
/// directions so neighboring forecast cells never move in lockstep.
/// </summary>
internal static class FogForecastFlowRuntime
{
    private const float GameTicksPerSecond = 40f;
    private const int FogSheetCount = 4;

    private sealed class FogPipVisual
    {
        private readonly FSprite _whiteRing;
        private readonly FSprite[] _sheets;

        internal FogPipVisual(RainWorld rainWorld, FContainer container, FSprite whiteRing)
        {
            _whiteRing = whiteRing;
            _sheets = new FSprite[FogSheetCount];

            for (int i = 0; i < _sheets.Length; i++)
            {
                FSprite sheet = new("Futile_White")
                {
                    shader = rainWorld.Shaders["NewVultureSmoke"],
                    isVisible = false,
                    alpha = 0f
                };
                _sheets[i] = sheet;
                container.AddChild(sheet);
            }

            EnsureLayering();
        }

        internal void Draw(
            WeatherForecastVisualKind kind,
            Vector2 center,
            float hudFade,
            float remaining,
            float animationSeconds,
            int pipSeed)
        {
            if (kind != WeatherForecastVisualKind.Fog &&
                kind != WeatherForecastVisualKind.DenseFog)
            {
                Hide();
                return;
            }

            float visibility = Mathf.Clamp01(hudFade * remaining);
            if (visibility <= 0.001f)
            {
                Hide();
                return;
            }

            EnsureLayering();

            bool dense = kind == WeatherForecastVisualKind.DenseFog;
            Color fogColor = WeatherForecastVisualCatalog.Get(kind).FillColor;
            float sizeFade = Mathf.Clamp01(hudFade);
            sizeFade *= sizeFade;

            for (int i = 0; i < _sheets.Length; i++)
            {
                // Fog keeps three broad sheets; DenseFog uses all four and moves them
                // faster. The travel is intentionally large relative to a 6 px pip so
                // the motion reads immediately instead of looking like a static fill.
                if (!dense && i == _sheets.Length - 1)
                {
                    _sheets[i].isVisible = false;
                    continue;
                }

                float seed = pipSeed * 0.173f + i * 0.271f;
                float speed = dense
                    ? 0.82f + i * 0.17f
                    : 0.48f + i * 0.11f;
                float phase = Mathf.Repeat(animationSeconds * speed + seed, 1f);
                bool reverse = (i & 1) != 0;
                float travel = reverse ? 1f - phase : phase;

                float edgeIn = Smooth01(Mathf.InverseLerp(0f, 0.15f, phase));
                float edgeOut = 1f - Smooth01(Mathf.InverseLerp(0.85f, 1f, phase));
                float travelEnvelope = Mathf.Min(edgeIn, edgeOut);

                float span = dense ? 2.65f : 2.35f;
                float x = center.x + Mathf.Lerp(-span, span, travel);
                float yWave = Mathf.Sin(
                    (animationSeconds * (dense ? 1.75f : 1.10f) + seed * 2.7f)
                    * Mathf.PI * 2f);
                float yWave2 = Mathf.Sin(
                    (animationSeconds * (dense ? 2.9f : 1.7f) - seed * 1.9f)
                    * Mathf.PI * 2f);
                float y = center.y + (yWave * 0.62f + yWave2 * 0.28f) * sizeFade;

                float pulse = 0.5f + 0.5f * Mathf.Sin(
                    (animationSeconds * (dense ? 2.35f : 1.35f) + seed)
                    * Mathf.PI * 2f);
                float diameter = dense
                    ? Mathf.Lerp(4.55f, 5.95f, pulse)
                    : Mathf.Lerp(4.10f, 5.35f, pulse);
                float stretch = dense
                    ? 1.18f + 0.18f * yWave2
                    : 1.10f + 0.12f * yWave2;

                FSprite sheet = _sheets[i];
                sheet.SetPosition(new Vector2(x, y));
                sheet.scaleX = diameter * stretch * sizeFade / 16f;
                sheet.scaleY = diameter / Mathf.Max(0.75f, stretch) * sizeFade / 16f;
                sheet.rotation = (reverse ? -1f : 1f) *
                                 (animationSeconds * (dense ? 105f : 62f) + seed * 180f);

                // The smoke shader respects vertex color. Slight per-sheet brightness
                // variation makes the movement readable even when the marker is tiny.
                float brighten = dense
                    ? Mathf.Lerp(-0.08f, 0.12f, (float)i / (_sheets.Length - 1))
                    : Mathf.Lerp(0.02f, 0.18f, (float)i / (_sheets.Length - 2));
                sheet.color = brighten >= 0f
                    ? Color.Lerp(fogColor, Color.white, brighten)
                    : Color.Lerp(fogColor, Color.black, -brighten);

                float baseAlpha = dense ? 0.72f : 0.54f;
                sheet.alpha = visibility * baseAlpha *
                              Mathf.Lerp(0.72f, 1f, pulse) *
                              Mathf.Lerp(0.35f, 1f, travelEnvelope);
                sheet.isVisible = sheet.alpha > 0.01f;
            }
        }

        private void EnsureLayering()
        {
            if (_whiteRing == null)
            {
                return;
            }

            // Called after RainMeterRoundPipRuntime's Draw pass. Moving every active
            // sheet immediately behind the ring puts the flowing haze above the solid
            // family color while preserving the untouched white time outline.
            for (int i = 0; i < _sheets.Length; i++)
            {
                _sheets[i].MoveBehindOtherNode(_whiteRing);
            }
        }

        internal void Hide()
        {
            for (int i = 0; i < _sheets.Length; i++)
            {
                _sheets[i].isVisible = false;
            }
        }

        internal void Remove()
        {
            for (int i = 0; i < _sheets.Length; i++)
            {
                _sheets[i].RemoveFromContainer();
            }
        }
    }

    private sealed class MeterState
    {
        internal readonly global::HUD.RainMeter Meter;
        internal readonly FContainer Container;
        internal FogPipVisual[] Pips;
        internal int AnimationTicks;

        internal MeterState(
            global::HUD.RainMeter meter,
            FContainer container,
            FogPipVisual[] pips)
        {
            Meter = meter;
            Container = container;
            Pips = pips;
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

        On.HUD.RainMeter.ctor += RainMeter_ctor;
        On.HUD.RainMeter.Update += RainMeter_Update;
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

        On.HUD.RainMeter.ctor -= RainMeter_ctor;
        On.HUD.RainMeter.Update -= RainMeter_Update;
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

    private static void RainMeter_ctor(
        On.HUD.RainMeter.orig_ctor orig,
        global::HUD.RainMeter self,
        global::HUD.HUD hud,
        FContainer fContainer)
    {
        orig(self, hud, fContainer);
        CreateState(self, fContainer);
    }

    private static void RainMeter_Update(
        On.HUD.RainMeter.orig_Update orig,
        global::HUD.RainMeter self)
    {
        orig(self);

        if (self != null && _states.TryGetValue(self, out MeterState state))
        {
            state.AnimationTicks++;
            EnsureCapacity(state);
        }
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        // Let the authoritative RainMeter renderer finish circle placement, weather
        // fill and white ring first; fog sheets are then layered immediately beneath
        // those rings.
        orig(self, timeStacker);

        if (self == null || !_states.TryGetValue(self, out MeterState state))
        {
            return;
        }

        EnsureCapacity(state);
        DrawFogForecast(state, Mathf.Clamp01(timeStacker));
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

    private static void CreateState(global::HUD.RainMeter meter, FContainer container)
    {
        if (meter?.circles == null ||
            meter.hud?.rainWorld == null ||
            container == null ||
            _states.TryGetValue(meter, out _))
        {
            return;
        }

        FogPipVisual[] pips = new FogPipVisual[meter.circles.Length];
        for (int i = 0; i < pips.Length; i++)
        {
            pips[i] = new FogPipVisual(
                meter.hud.rainWorld,
                container,
                meter.circles[i]?.sprite);
        }

        MeterState state = new(meter, container, pips);
        _states.Add(meter, state);
        LiveStates.Add(state);
    }

    private static void EnsureCapacity(MeterState state)
    {
        global::HUD.RainMeter meter = state?.Meter;
        if (meter?.circles == null ||
            meter.hud?.rainWorld == null ||
            state.Container == null ||
            state.Pips == null ||
            state.Pips.Length >= meter.circles.Length)
        {
            return;
        }

        FogPipVisual[] expanded = new FogPipVisual[meter.circles.Length];
        Array.Copy(state.Pips, expanded, state.Pips.Length);
        for (int i = state.Pips.Length; i < expanded.Length; i++)
        {
            expanded[i] = new FogPipVisual(
                meter.hud.rainWorld,
                state.Container,
                meter.circles[i]?.sprite);
        }
        state.Pips = expanded;
    }

    private static void DrawFogForecast(MeterState state, float timeStacker)
    {
        global::HUD.RainMeter meter = state?.Meter;
        Player player = meter?.hud?.owner as Player;
        World world = player?.abstractCreature?.world;
        RainWorldGame game = world?.game;

        if (meter?.circles == null ||
            state.Pips == null ||
            game == null ||
            !game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            HideAll(state);
            return;
        }

        WeatherScheduleRuntime.Synchronize(world);
        if (!WeatherScheduleRuntime.TryGetCurrentSchedule(
                world,
                out WeatherPhaseSchedule schedule) ||
            schedule == null ||
            schedule.Phase != CurrentPhase(clock))
        {
            HideAll(state);
            return;
        }

        int capacity = Math.Min(meter.circles.Length, state.Pips.Length);
        int activePips = ActivePhasePipCount(clock, capacity);
        float hudFade = Mathf.Clamp01(Mathf.Lerp(meter.lastFade, meter.fade, timeStacker));
        float animationSeconds = (state.AnimationTicks + timeStacker) / GameTicksPerSecond;

        for (int i = 0; i < capacity; i++)
        {
            if (i >= activePips)
            {
                state.Pips[i]?.Hide();
            }
        }

        for (int chronologicalPip = 1; chronologicalPip <= activePips; chronologicalPip++)
        {
            int index = CircleIndex(clock, activePips, chronologicalPip);
            if (index < 0 || index >= capacity)
            {
                continue;
            }

            global::HUD.HUDCircle circle = meter.circles[index];
            FogPipVisual visual = state.Pips[index];
            if (circle == null ||
                circle.sprite == null ||
                !circle.sprite.isVisible ||
                visual == null)
            {
                visual?.Hide();
                continue;
            }

            float remaining = 1f - PipElapsed(clock, chronologicalPip);
            if (remaining <= 0.001f ||
                !TryGetFogMarker(schedule, chronologicalPip, out WeatherForecastVisualKind kind))
            {
                visual.Hide();
                continue;
            }

            Vector2 center = Vector2.Lerp(circle.lastPos, circle.pos, timeStacker);
            visual.Draw(
                kind,
                center,
                hudFade,
                remaining,
                animationSeconds,
                chronologicalPip + (clock.IsNight ? 101 : 0));
        }
    }

    private static bool TryGetFogMarker(
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
                zeroBasedPip >= scheduled.EndPipExclusive ||
                !WeatherForecastVisualCatalog.TryResolve(
                    scheduled.Candidate.Id,
                    scheduled.Candidate.Kind,
                    out WeatherForecastVisualKind resolved))
            {
                continue;
            }

            if (resolved == WeatherForecastVisualKind.Fog ||
                resolved == WeatherForecastVisualKind.DenseFog)
            {
                kind = resolved;
                return true;
            }
        }

        return false;
    }

    private static int ActivePhasePipCount(WorldClock clock, int capacity)
    {
        if (clock == null || capacity <= 0)
        {
            return 0;
        }

        int physicalPips = WeatherPhaseScheduler.FullPipsFromTicks(clock.CurrentHalfLength);
        return Math.Max(0, Math.Min(capacity, physicalPips));
    }

    private static WeatherSchedulePhase CurrentPhase(WorldClock clock)
    {
        return clock != null && clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;
    }

    private static long CurrentPhaseTicks(WorldClock clock)
    {
        if (clock == null)
        {
            return 0;
        }

        return (long)Math.Round(
            Mathf.Clamp01(clock.HalfProgress) * clock.CurrentHalfLength);
    }

    private static float PipElapsed(WorldClock clock, int chronologicalPip)
    {
        if (clock == null || chronologicalPip < 1)
        {
            return 1f;
        }

        long phaseTicks = CurrentPhaseTicks(clock);
        long start = (long)(chronologicalPip - 1) * WeatherPhaseScheduler.PipTicks;
        float elapsed = (phaseTicks - start) / (float)WeatherPhaseScheduler.PipTicks;
        return Smooth01(Mathf.Clamp01(elapsed));
    }

    private static int CircleIndex(
        WorldClock clock,
        int activePips,
        int chronologicalPip)
    {
        return clock != null && clock.IsNight
            ? chronologicalPip - 1
            : activePips - chronologicalPip;
    }

    private static void HideAll(MeterState state)
    {
        if (state?.Pips == null)
        {
            return;
        }

        for (int i = 0; i < state.Pips.Length; i++)
        {
            state.Pips[i]?.Hide();
        }
    }

    private static void RemoveState(MeterState state)
    {
        if (state?.Pips == null)
        {
            return;
        }

        for (int i = 0; i < state.Pips.Length; i++)
        {
            state.Pips[i]?.Remove();
        }
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
