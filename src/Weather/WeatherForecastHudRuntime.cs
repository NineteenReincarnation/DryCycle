using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Draws weather information inside RainMeter pips without changing the white time
/// outline. The base marker is always a true circular atlas sprite; animated rain
/// droplets are built from Rain World's VectorCircle shader so their edges stay smooth
/// at HUD scale and no diamond/pixel-square marker is needed.
/// </summary>
internal static class WeatherForecastHudRuntime
{
    private const float GameTicksPerSecond = 40f;
    private const float FillDiameterPixels = 5.30f;
    private const int MaxDripGlyphs = 2;

    private sealed class DripGlyph
    {
        internal readonly FSprite Head;
        internal readonly FSprite Tail;

        internal DripGlyph(RainWorld rainWorld, FContainer container)
        {
            // VectorCircle procedurally rounds the Futile_White quad. Non-uniform
            // scaling turns it into a smooth ellipse; a bulb + narrow upper ellipse
            // reads as a tiny water drop without importing a low-resolution icon.
            Head = new FSprite("Futile_White")
            {
                shader = rainWorld.Shaders["VectorCircle"],
                isVisible = false,
                alpha = 1f
            };

            Tail = new FSprite("Futile_White")
            {
                shader = rainWorld.Shaders["VectorCircle"],
                isVisible = false,
                alpha = 1f
            };

            container.AddChild(Tail);
            container.AddChild(Head);
        }

        internal void PutBehind(FSprite ring)
        {
            if (ring == null)
            {
                return;
            }

            Tail.MoveBehindOtherNode(ring);
            Head.MoveBehindOtherNode(ring);
        }

        internal void Hide()
        {
            Head.isVisible = false;
            Tail.isVisible = false;
        }

        internal void Remove()
        {
            Head.RemoveFromContainer();
            Tail.RemoveFromContainer();
        }
    }

    private sealed class ForecastPipVisual
    {
        private readonly FSprite _fill;
        private readonly FSprite _whiteRing;
        private readonly DripGlyph[] _drips;
        private readonly float _fillBaseScale;
        private bool _layeringConfirmed;

        internal ForecastPipVisual(RainWorld rainWorld, FContainer container, FSprite whiteRing)
        {
            _whiteRing = whiteRing;

            // Circle20 is a genuine round atlas element, unlike using tiny Circle4 as
            // a colored center where pixel geometry can read as a diamond. Keep a
            // circular fallback for unusually stripped atlas setups.
            string fillElement = Futile.atlasManager.DoesContainElementWithName("Circle20")
                ? "Circle20"
                : "deerEyeB";
            FAtlasElement element = Futile.atlasManager.GetElementWithName(fillElement);
            float sourceDiameter = Mathf.Max(
                1f,
                Mathf.Max(element.sourcePixelSize.x, element.sourcePixelSize.y));
            _fillBaseScale = FillDiameterPixels / sourceDiameter;

            _fill = new FSprite(fillElement)
            {
                shader = rainWorld.Shaders["Basic"],
                isVisible = false
            };
            container.AddChild(_fill);

            _drips = new DripGlyph[MaxDripGlyphs];
            for (int i = 0; i < _drips.Length; i++)
            {
                _drips[i] = new DripGlyph(rainWorld, container);
            }

            // Do this once now and once on first draw. The second pass makes layering
            // independent of MonoMod hook ordering: all legacy test sprites are known
            // to exist by then, so our true circle ends immediately behind the ring.
            EnsureLayering();
            _layeringConfirmed = false;
        }

        internal void Draw(
            WeatherForecastVisualKind kind,
            Vector2 center,
            float hudFade,
            float solid,
            float animationSeconds,
            int pipSeed)
        {
            EnsureLayering();

            WeatherForecastVisualStyle style = WeatherForecastVisualCatalog.Get(kind);
            float visibility = Mathf.Clamp01(hudFade * solid);
            if (kind == WeatherForecastVisualKind.None || visibility <= 0.001f)
            {
                Hide();
                return;
            }

            float sizeFade = Mathf.Clamp01(hudFade);
            sizeFade *= sizeFade;

            Vector2 fillCenter = center;
            if (style.Animation == WeatherForecastAnimation.VerticalShake)
            {
                // DeathRain is intentionally restrained: two incommensurate waves
                // avoid robotic bobbing while staying below a pixel at full strength.
                float seed = pipSeed * 0.371f;
                float waveA = Mathf.Sin((animationSeconds * 6.2f + seed) * Mathf.PI * 2f);
                float waveB = Mathf.Sin((animationSeconds * 9.7f + seed * 1.73f) * Mathf.PI * 2f);
                float wave = waveA * 0.72f + waveB * 0.28f;
                fillCenter.y += wave * style.ShakeAmplitudePixels * sizeFade * visibility;
            }

            _fill.SetPosition(fillCenter);
            _fill.scale = _fillBaseScale * sizeFade;
            _fill.color = style.FillColor;
            _fill.alpha = visibility;
            _fill.isVisible = true;

            if (style.Animation != WeatherForecastAnimation.Drip &&
                style.Animation != WeatherForecastAnimation.FastDrip)
            {
                HideDrips();
                return;
            }

            DrawDrips(style, center, visibility, sizeFade, animationSeconds, pipSeed);
        }

        private void DrawDrips(
            WeatherForecastVisualStyle style,
            Vector2 center,
            float visibility,
            float sizeFade,
            float animationSeconds,
            int pipSeed)
        {
            int count = Math.Min(style.DripCount, _drips.Length);

            for (int i = 0; i < _drips.Length; i++)
            {
                if (i >= count)
                {
                    _drips[i].Hide();
                    continue;
                }

                // Stable per-pip offsets keep neighboring forecast balls from looking
                // like one synchronized conveyor belt while remaining deterministic.
                float phaseOffset = (float)i / count + pipSeed * 0.137f;
                float phase = Mathf.Repeat(
                    animationSeconds * style.DripCyclesPerSecond + phaseOffset,
                    1f);

                // A short ease-in/ease-out is done by shrinking the procedural glyph,
                // not by changing VectorCircle alpha (its alpha channel represents
                // circle thickness). The fall itself accelerates quadratically.
                float spawn = Smooth01(Mathf.InverseLerp(0f, 0.11f, phase));
                float vanish = 1f - Smooth01(Mathf.InverseLerp(0.80f, 1f, phase));
                float envelope = Mathf.Min(spawn, vanish) * visibility * sizeFade;
                if (envelope <= 0.015f)
                {
                    _drips[i].Hide();
                    continue;
                }

                float fall = phase * phase;
                float lane = count <= 1
                    ? 0f
                    : Mathf.Lerp(-1.05f, 1.05f, (float)i / (count - 1));
                float tinySway = Mathf.Sin((phase + pipSeed * 0.071f) * Mathf.PI) * 0.10f;
                float y = center.y + 1.70f - fall * style.DripTravelPixels;
                float x = center.x + lane + tinySway;

                DripGlyph drip = _drips[i];
                drip.Head.SetPosition(new Vector2(x, y));
                drip.Tail.SetPosition(new Vector2(
                    x,
                    y + 0.52f + (1f - phase) * 0.18f));

                // HeavyRain and BulletRain use exactly the same droplet geometry.
                // BulletRain differs only through DripCyclesPerSecond in the catalog.
                float headRadiusX = 0.54f * envelope;
                float headRadiusY = 0.78f * envelope;
                float tailRadiusX = 0.19f * envelope;
                float tailRadiusY = 0.54f * envelope;

                // VectorCircle's reference scale is radius / 8. The paired ellipses
                // form a readable teardrop at only a few screen pixels.
                drip.Head.scaleX = headRadiusX / 8f;
                drip.Head.scaleY = headRadiusY / 8f;
                drip.Tail.scaleX = tailRadiusX / 8f;
                drip.Tail.scaleY = tailRadiusY / 8f;
                drip.Head.color = style.DropColor;
                drip.Tail.color = style.DropColor;
                drip.Head.isVisible = true;
                drip.Tail.isVisible = true;
            }
        }

        private void EnsureLayering()
        {
            if (_layeringConfirmed || _whiteRing == null)
            {
                return;
            }

            _fill.MoveBehindOtherNode(_whiteRing);
            for (int i = 0; i < _drips.Length; i++)
            {
                _drips[i].PutBehind(_whiteRing);
            }

            _layeringConfirmed = true;
        }

        internal void Hide()
        {
            _fill.isVisible = false;
            HideDrips();
        }

        private void HideDrips()
        {
            for (int i = 0; i < _drips.Length; i++)
            {
                _drips[i].Hide();
            }
        }

        internal void Remove()
        {
            _fill.RemoveFromContainer();
            for (int i = 0; i < _drips.Length; i++)
            {
                _drips[i].Remove();
            }
        }

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }

    private sealed class MeterState
    {
        internal readonly global::HUD.RainMeter Meter;
        internal readonly ForecastPipVisual[] Pips;
        internal int AnimationTicks;

        internal MeterState(global::HUD.RainMeter meter, ForecastPipVisual[] pips)
        {
            Meter = meter;
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

        _enabled = true;
        On.HUD.RainMeter.ctor += RainMeter_ctor;
        On.HUD.RainMeter.Update += RainMeter_Update;
        On.HUD.RainMeter.Draw += RainMeter_Draw;
        On.HUD.RainMeter.ClearSprites += RainMeter_ClearSprites;
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
        WeatherForecastTimeline.Reset();
        _enabled = false;
    }

    private static void RainMeter_ctor(
        On.HUD.RainMeter.orig_ctor orig,
        global::HUD.RainMeter self,
        global::HUD.HUD hud,
        FContainer fContainer)
    {
        // This hook is installed after WorldClockHooks. Let that constructor finish
        // first so its temporary legacy test fill is already behind the ring; our
        // round fill is then placed immediately behind the white ring and fully masks
        // the old low-resolution center while test mode is still retained.
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
        }
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        orig(self, timeStacker);

        if (self == null || !_states.TryGetValue(self, out MeterState state))
        {
            return;
        }

        DrawForecast(state, Mathf.Clamp01(timeStacker));
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

        ForecastPipVisual[] pips = new ForecastPipVisual[meter.circles.Length];
        for (int i = 0; i < pips.Length; i++)
        {
            pips[i] = new ForecastPipVisual(
                meter.hud.rainWorld,
                container,
                meter.circles[i]?.sprite);
        }

        MeterState state = new(meter, pips);
        _states.Add(meter, state);
        LiveStates.Add(state);
    }

    private static void DrawForecast(MeterState state, float timeStacker)
    {
        global::HUD.RainMeter meter = state.Meter;
        Player player = meter?.hud?.owner as Player;
        World world = player?.abstractCreature?.world;
        RainWorldGame game = world?.game;

        if (meter?.circles == null ||
            game == null ||
            !game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            HideAll(state);
            return;
        }

        int count = Math.Min(meter.circles.Length, state.Pips.Length);
        if (count == 0)
        {
            return;
        }

        float hudFade = Mathf.Clamp01(Mathf.Lerp(meter.lastFade, meter.fade, timeStacker));
        float scaledProgress = Mathf.Clamp01(clock.HalfProgress) * count;
        float animationSeconds = (state.AnimationTicks + timeStacker) / GameTicksPerSecond;
        WeatherSchedulePhase phase = clock.IsNight
            ? WeatherSchedulePhase.Night
            : WeatherSchedulePhase.Day;

        for (int chronologicalPip = 1; chronologicalPip <= count; chronologicalPip++)
        {
            int index = clock.IsNight
                ? chronologicalPip - 1
                : count - chronologicalPip;
            if (index < 0 || index >= count)
            {
                continue;
            }

            ForecastPipVisual visual = state.Pips[index];
            global::HUD.HUDCircle circle = meter.circles[index];
            if (visual == null ||
                circle == null ||
                circle.sprite == null ||
                !circle.sprite.isVisible ||
                !TryGetMarker(game, phase, chronologicalPip, out WeatherForecastVisualKind kind))
            {
                visual?.Hide();
                continue;
            }

            // Forecast color marks future cells. Once a chronological half-minute cell
            // is consumed, its colored center disappears while WorldClockHooks keeps
            // the independent white time ring behavior intact.
            float elapsed = Mathf.Clamp01(scaledProgress - (chronologicalPip - 1));
            elapsed = elapsed * elapsed * (3f - 2f * elapsed);
            float solid = 1f - elapsed;

            Vector2 center = Vector2.Lerp(circle.lastPos, circle.pos, timeStacker);
            visual.Draw(
                kind,
                center,
                hudFade,
                solid,
                animationSeconds,
                chronologicalPip + (clock.IsNight ? 101 : 0));
        }
    }

    private static bool TryGetMarker(
        RainWorldGame game,
        WeatherSchedulePhase phase,
        int chronologicalPip,
        out WeatherForecastVisualKind kind)
    {
        // Real generated schedules take priority. The existing second/fourth-pip
        // sandstorm test remains only as a fallback until the climate loader feeds
        // WeatherPhaseScheduler results into WeatherForecastTimeline.
        if (WeatherForecastTimeline.TryGet(game, phase, chronologicalPip, out kind))
        {
            return true;
        }

        if (phase == WeatherSchedulePhase.Day && WorldClockHooks.TestScheduleEnabled)
        {
            if (chronologicalPip == SandstormWeatherRuntime.NormalWeatherPip)
            {
                kind = WeatherForecastVisualKind.SandStorm;
                return true;
            }

            if (chronologicalPip == SandstormWeatherRuntime.HazardWeatherPip)
            {
                kind = WeatherForecastVisualKind.DeathSandStorm;
                return true;
            }
        }

        kind = WeatherForecastVisualKind.None;
        return false;
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
}
