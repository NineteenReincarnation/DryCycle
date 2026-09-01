using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using MoreSlugcats;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Single authoritative DryCycle RainMeter presentation pass.
/// Scheduler cells, time-pip shapes and colored forecast markers all use the same
/// zero-based 1200-tick grid.
/// </summary>
internal static class RainMeterRoundPipRuntime
{
    private const float GameTicksPerSecond = 40f;
    private const float FillDiameterPixels = 5.30f;
    private const int MaxDripGlyphs = 3;

    private sealed class DripGlyph
    {
        internal readonly FSprite Head;
        internal readonly FSprite Tail;

        internal DripGlyph(RainWorld rainWorld, FContainer container)
        {
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

        internal ForecastPipVisual(
            RainWorld rainWorld,
            FContainer container,
            FSprite whiteRing)
        {
            _whiteRing = whiteRing;

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
            EnsureLayering();

            WeatherForecastVisualStyle style = WeatherForecastVisualCatalog.Get(kind);
            float visibility = Mathf.Clamp01(hudFade * remaining);
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

                float phaseOffset = (float)i / count + pipSeed * 0.137f;
                float phase = Mathf.Repeat(
                    animationSeconds * style.DripCyclesPerSecond + phaseOffset,
                    1f);

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

                float headRadiusX = 0.54f * envelope;
                float headRadiusY = 0.78f * envelope;
                float tailRadiusX = 0.19f * envelope;
                float tailRadiusY = 0.54f * envelope;

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
            if (_whiteRing == null)
            {
                return;
            }

            _fill.MoveBehindOtherNode(_whiteRing);
            for (int i = 0; i < _drips.Length; i++)
            {
                _drips[i].PutBehind(_whiteRing);
            }
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
    }

    private sealed class MeterState
    {
        internal readonly global::HUD.RainMeter Meter;
        internal readonly FContainer Container;
        internal readonly int OriginalCircleCount;
        internal readonly int OriginalTimePerCircle;
        internal ForecastPipVisual[] Pips;
        internal int AnimationTicks;

        internal MeterState(
            global::HUD.RainMeter meter,
            FContainer container,
            ForecastPipVisual[] pips)
        {
            Meter = meter;
            Container = container;
            OriginalCircleCount = meter?.circles?.Length ?? 0;
            OriginalTimePerCircle = meter?.timePerCircle ?? WeatherPhaseScheduler.PipTicks;
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
            RestoreVanillaState(LiveStates[i]);
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

        Player player = hud?.owner as Player;
        World world = player?.abstractCreature?.world;
        if (world != null &&
            VanillaAllowsRainMeterDraw(self) &&
            RegionDayNightOptions.IsEnabled(world) &&
            WorldClockHooks.TryGetClock(world, out WorldClock clock) &&
            _states.TryGetValue(self, out MeterState state))
        {
            EnsureCapacity(state, FullDayPipCount(clock));
        }
    }

    private static void RainMeter_Update(
        On.HUD.RainMeter.orig_Update orig,
        global::HUD.RainMeter self)
    {
        MeterState state = null;
        if (self != null)
        {
            _states.TryGetValue(self, out state);
        }

        World world = null;
        WorldClock clock = null;
        bool dryCycle = state != null && TryGetContext(self, out world, out clock);
        if (!dryCycle)
        {
            RestoreVanillaState(state);
        }

        orig(self);

        if (state == null)
        {
            return;
        }

        state.AnimationTicks++;
        if (!dryCycle)
        {
            HideForecasts(state);
            return;
        }

        EnsureCapacity(state, FullDayPipCount(clock));
        ApplyPhasePipLayout(self, clock);
    }

    private static void RainMeter_Draw(
        On.HUD.RainMeter.orig_Draw orig,
        global::HUD.RainMeter self,
        float timeStacker)
    {
        MeterState state = null;
        if (self != null)
        {
            _states.TryGetValue(self, out state);
        }

        World world = null;
        WorldClock clock = null;
        bool vanillaAllowsDraw = VanillaAllowsRainMeterDraw(self);
        bool dryCycle = state != null && TryGetContext(self, out world, out clock);
        if (!dryCycle)
        {
            RestoreVanillaState(state);
            orig(self, timeStacker);
            HideForecasts(state);

            if (!vanillaAllowsDraw)
            {
                HideCircleSprites(self);
            }
            return;
        }

        EnsureCapacity(state, FullDayPipCount(clock));
        WeatherScheduleRuntime.Synchronize(world);

        WeatherPhaseSchedule schedule = null;
        if (WeatherScheduleRuntime.TryGetCurrentSchedule(
                world,
                out WeatherPhaseSchedule current) &&
            current != null &&
            current.Phase == CurrentPhase(clock))
        {
            schedule = current;
        }

        orig(self, timeStacker);
        ApplyPhasePipLayout(self, clock);
        DrawFinal(state, clock, schedule, Mathf.Clamp01(timeStacker));
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

        MeterState state = new(meter, container, pips);
        _states.Add(meter, state);
        LiveStates.Add(state);
    }

    private static void EnsureCapacity(MeterState state, int requiredPips)
    {
        global::HUD.RainMeter meter = state?.Meter;
        if (meter?.circles == null ||
            meter.hud?.rainWorld == null ||
            state.Container == null)
        {
            return;
        }

        requiredPips = Math.Max(0, requiredPips);
        if (requiredPips <= meter.circles.Length)
        {
            meter.timePerCircle = WeatherPhaseScheduler.PipTicks;
            return;
        }

        global::HUD.HUDCircle[] oldCircles = meter.circles;
        ForecastPipVisual[] oldPips = state.Pips ?? Array.Empty<ForecastPipVisual>();

        global::HUD.HUDCircle[] circles = new global::HUD.HUDCircle[requiredPips];
        ForecastPipVisual[] pips = new ForecastPipVisual[requiredPips];
        Array.Copy(oldCircles, circles, oldCircles.Length);
        Array.Copy(oldPips, pips, Math.Min(oldPips.Length, pips.Length));

        for (int i = oldCircles.Length; i < requiredPips; i++)
        {
            circles[i] = new global::HUD.HUDCircle(
                meter.hud,
                global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle,
                state.Container,
                0);
            pips[i] = new ForecastPipVisual(
                meter.hud.rainWorld,
                state.Container,
                circles[i].sprite);
        }

        meter.circles = circles;
        meter.timePerCircle = WeatherPhaseScheduler.PipTicks;
        state.Pips = pips;

        Plugin.Logger?.LogInfo(
            $"DryCycle RainMeter expanded to {requiredPips} half-minute pips.");
    }

    private static bool TryGetContext(
        global::HUD.RainMeter meter,
        out World world,
        out WorldClock clock)
    {
        Player player = meter?.hud?.owner as Player;
        world = player?.abstractCreature?.world;
        clock = null;

        return VanillaAllowsRainMeterDraw(meter) &&
               world?.game != null &&
               world.game.IsStorySession &&
               RegionDayNightOptions.IsEnabled(world) &&
               WorldClockHooks.TryGetClock(world, out clock);
    }

    private static bool VanillaAllowsRainMeterDraw(global::HUD.RainMeter meter)
    {
        if (!ModManager.MSC)
        {
            return true;
        }

        Player player = meter?.hud?.owner as Player;
        if (player?.abstractCreature?.world?.game == null ||
            player.abstractCreature.world.game.StoryCharacter != MoreSlugcatsEnums.SlugcatStatsName.Saint)
        {
            return true;
        }

        return meter.hud?.map != null && Region.IsRubiconRegion(meter.hud.map.RegionName);
    }

    private static int FullDayPipCount(WorldClock clock)
    {
        return clock == null
            ? 0
            : WeatherPhaseScheduler.FullPipsFromTicks(clock.DayCycleLength);
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

    private static void ApplyPhasePipLayout(
        global::HUD.RainMeter meter,
        WorldClock clock)
    {
        global::HUD.HUDCircle[] circles = meter?.circles;
        if (circles == null || circles.Length == 0 || clock == null)
        {
            return;
        }

        int activePips = ActivePhasePipCount(clock, circles.Length);
        float hudFade = Mathf.Clamp01(meter.fade);
        float sizeFade = hudFade * hudFade;

        for (int i = 0; i < circles.Length; i++)
        {
            global::HUD.HUDCircle circle = circles[i];
            if (circle == null)
            {
                continue;
            }

            circle.forceColor = null;
            if (i >= activePips)
            {
                circle.visible = false;
                circle.rad = 0f;
                continue;
            }

            circle.visible = true;
            int chronologicalPip = clock.IsNight
                ? i + 1
                : activePips - i;
            float elapsed = PipElapsed(clock, chronologicalPip);
            float hollow = clock.IsNight ? 1f - elapsed : elapsed;
            ApplyPipShape(circle, hollow, sizeFade);

            float index01 = activePips > 1 ? (float)i / (activePips - 1) : 0f;
            float angle = (1f - (float)i / activePips)
                * 360f
                * Custom.SCurve(Mathf.Pow(hudFade, 1.5f - index01), 0.6f);
            circle.pos = meter.pos
                + Custom.DegToVec(angle)
                * (meter.hud.karmaMeter.Radius + 8.5f + hollow + 4f * meter.tickPulse);
        }
    }

    private static void DrawFinal(
        MeterState state,
        WorldClock clock,
        WeatherPhaseSchedule schedule,
        float timeStacker)
    {
        global::HUD.RainMeter meter = state?.Meter;
        if (meter?.circles == null || state.Pips == null)
        {
            return;
        }

        int capacity = Math.Min(meter.circles.Length, state.Pips.Length);
        int activePips = ActivePhasePipCount(clock, capacity);
        float hudFade = Mathf.Clamp01(Mathf.Lerp(meter.lastFade, meter.fade, timeStacker));
        float animationSeconds = (state.AnimationTicks + timeStacker) / GameTicksPerSecond;

        for (int i = activePips; i < capacity; i++)
        {
            state.Pips[i]?.Hide();
            if (meter.circles[i]?.sprite != null)
            {
                meter.circles[i].sprite.isVisible = false;
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
            ForecastPipVisual visual = state.Pips[index];
            if (circle == null || visual == null)
            {
                continue;
            }

            float remaining = 1f - PipElapsed(clock, chronologicalPip);
            WeatherForecastVisualKind kind = WeatherForecastVisualKind.None;
            bool hasMarker = remaining > 0.001f &&
                             TryGetMarker(schedule, chronologicalPip, out kind);

            if (hasMarker)
            {
                Vector2 center = Vector2.Lerp(circle.lastPos, circle.pos, timeStacker);
                visual.Draw(
                    kind,
                    center,
                    hudFade,
                    remaining,
                    animationSeconds,
                    chronologicalPip + (clock.IsNight ? 101 : 0));

                circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle;
                circle.snapRad = 3f;
                circle.snapThickness = 1f;
                circle.rad = 3f * hudFade * hudFade;
                circle.thickness = 1f * hudFade * hudFade;
                circle.forceColor = Color.white;
            }
            else
            {
                visual.Hide();
                circle.forceColor = null;

                if (circle.snapGraphic == global::HUD.HUDCircle.SnapToGraphic.Circle4)
                {
                    circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.None;
                    circle.snapRad = -1f;
                    circle.snapThickness = -1f;
                }
            }

            circle.Draw(timeStacker);
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
        if (zeroBasedPip >= schedule.PhasePipCount)
        {
            return false;
        }

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

    private static void RestoreVanillaState(MeterState state)
    {
        global::HUD.RainMeter meter = state?.Meter;
        if (meter?.circles == null)
        {
            return;
        }

        int originalCount = Math.Max(0, state.OriginalCircleCount);
        if (meter.circles.Length > originalCount)
        {
            for (int i = originalCount; i < meter.circles.Length; i++)
            {
                if (state.Pips != null && i < state.Pips.Length)
                {
                    state.Pips[i]?.Remove();
                }
                meter.circles[i]?.ClearSprite();
            }

            global::HUD.HUDCircle[] restoredCircles =
                new global::HUD.HUDCircle[originalCount];
            Array.Copy(meter.circles, restoredCircles, originalCount);
            meter.circles = restoredCircles;

            ForecastPipVisual[] restoredPips = new ForecastPipVisual[originalCount];
            if (state.Pips != null)
            {
                Array.Copy(
                    state.Pips,
                    restoredPips,
                    Math.Min(originalCount, state.Pips.Length));
            }
            state.Pips = restoredPips;
        }

        meter.timePerCircle = state.OriginalTimePerCircle;
        for (int i = 0; i < meter.circles.Length; i++)
        {
            global::HUD.HUDCircle circle = meter.circles[i];
            if (circle == null)
            {
                continue;
            }

            circle.visible = true;
            circle.forceColor = null;
        }
    }

    private static void HideForecasts(MeterState state)
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

    private static void HideCircleSprites(global::HUD.RainMeter meter)
    {
        if (meter?.circles == null)
        {
            return;
        }

        for (int i = 0; i < meter.circles.Length; i++)
        {
            if (meter.circles[i]?.sprite != null)
            {
                meter.circles[i].sprite.isVisible = false;
            }
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

    private static void ApplyPipShape(
        global::HUD.HUDCircle circle,
        float hollow,
        float sizeFade)
    {
        hollow = Mathf.Clamp01(hollow);

        if (hollow <= 0.001f)
        {
            circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.None;
            circle.snapRad = -1f;
            circle.snapThickness = -1f;
            circle.rad = 2f * sizeFade;
            circle.thickness = -1f;
            return;
        }

        circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle;
        circle.snapRad = 3f;
        circle.snapThickness = 1f;

        if (hollow >= 0.999f)
        {
            circle.rad = 3f * sizeFade;
            circle.thickness = 1f * sizeFade;
            return;
        }

        circle.rad = Mathf.Lerp(2f, 3f, hollow) * sizeFade;
        circle.thickness = Mathf.Lerp(3.5f, 1f, hollow) * sizeFade;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
