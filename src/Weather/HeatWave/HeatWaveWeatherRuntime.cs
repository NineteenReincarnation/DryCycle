using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// WorldClock-driven HeatWave owner. HeatWave has no RoomSettings weather effect and
/// does not borrow a native Rain World heat-haze object. A tiny controller may exist in
/// every DryCycle story room, but expensive terrain/GPU resources are allocated lazily
/// only when the scheduled weather (or developer force mode) actually becomes active.
/// </summary>
internal static class HeatWaveWeatherRuntime
{
    private const float Epsilon = 0.0001f;
    private const float ResidualSeconds = 14f;

    private static ConditionalWeakTable<Room, HeatWaveController> _controllers = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        HeatColumnHooks.Enable();
        HeatWaveDebugRuntime.Enable();
        On.Room.Loaded += Room_Loaded;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        HeatWaveDebugRuntime.Disable();
        HeatColumnHooks.Disable();
        _controllers = new ConditionalWeakTable<Room, HeatWaveController>();
        _enabled = false;
    }

    internal static bool TryEvaluate(Room room, out float intensity)
    {
        intensity = 0f;
        if (HeatWaveDebugRuntime.TryGetForcedIntensity(room, out float forced))
        {
            intensity = forced;
            return true;
        }

        World world = room?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return false;
        }

        intensity = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "HeatWave");
        return intensity > Epsilon;
    }

    /// <summary>
    /// Gameplay systems may use this scalar, but never the GPU thermal texture, as a
    /// deterministic room-wide HeatWave influence. Keeping gameplay authoritative on
    /// schedule data prevents graphics support or simulation resolution changing rules.
    /// </summary>
    internal static float GetAmbientHeatInfluence(Room room)
    {
        if (!TryEvaluate(room, out float intensity))
        {
            return 0f;
        }

        return Mathf.Clamp01(intensity);
    }

    internal static void DebugForceBurst(Room room)
    {
        if (room != null && _controllers.TryGetValue(room, out HeatWaveController controller))
        {
            controller.DebugForceBurst();
        }
    }

    internal static bool TryGetDebugSnapshot(Room room, out HeatWaveDebugSnapshot snapshot)
    {
        snapshot = default;
        return room != null &&
               _controllers.TryGetValue(room, out HeatWaveController controller) &&
               controller.TryGetDebugSnapshot(out snapshot);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (self?.game == null ||
            !self.game.IsStorySession ||
            self.world == null ||
            !RegionDayNightOptions.IsEnabled(self.world) ||
            _controllers.TryGetValue(self, out _))
        {
            return;
        }

        HeatWaveController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private sealed class HeatWaveController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private readonly HeatWaveBurstController _burst;
        private readonly HeatWaveAudio _audio;

        private HeatWaveTerrainField _terrain;
        private HeatWaveThermalSimulation _simulation;
        private bool _resourcesInitialized;
        private bool _resourcesDisposed;

        private float _lastIntensity;
        private float _intensity;
        private float _lastWhiteHeat;
        private float _whiteHeat;
        private float _lastSolar;
        private float _solar;
        private float _visualTime;
        private float _cooldown;

        internal HeatWaveController(Room ownerRoom)
        {
            room = ownerRoom;
            _burst = new HeatWaveBurstController(ownerRoom);
            _audio = new HeatWaveAudio(this);
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            if (!_enabled)
            {
                Destroy();
                return;
            }

            _lastIntensity = _intensity;
            _lastWhiteHeat = _whiteHeat;
            _lastSolar = _solar;

            if (!TryEvaluate(room, out float scheduled))
            {
                scheduled = 0f;
            }
            _intensity = Mathf.Clamp01(scheduled);

            if (_intensity > Epsilon)
            {
                EnsureResources();
                _cooldown = ResidualSeconds;
            }
            else
            {
                _cooldown = Mathf.Max(0f, _cooldown - 1f / 40f);
            }

            WorldClock clock = null;
            if (room?.world != null)
            {
                WorldClockHooks.TryGetClock(room.world, out clock);
            }

            _solar = EvaluateSolar(clock);
            _burst.Update(1f / 40f, _intensity, _solar);

            float whiteBase = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(0.16f, 1f, _intensity));
            float solarWhite = Mathf.Pow(Mathf.Clamp01(_solar), 0.62f);
            _whiteHeat = Mathf.Clamp01(
                whiteBase * solarWhite * 0.94f +
                _burst.BurstStrength * solarWhite * 0.08f);

            _visualTime += 1f / 40f;
            _audio.Update(
                _intensity,
                _solar,
                _burst.Stillness,
                _burst.BurstStrength,
                _burst.BurstKick,
                _visualTime);

            if ((_intensity > Epsilon || _cooldown > 0f) &&
                _simulation?.IsAvailable == true)
            {
                _simulation.Step(
                    1f / 40f,
                    _intensity,
                    _solar,
                    _burst);
            }
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);
            sLeaser.sprites = HeatWaveRenderPipeline.CreateSprites(rCam);
            AddToContainer(sLeaser, rCam, null);
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            if (room == null || room != rCam.room)
            {
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            float intensity = Mathf.Lerp(_lastIntensity, _intensity, timeStacker);
            float whiteHeat = Mathf.Lerp(_lastWhiteHeat, _whiteHeat, timeStacker);
            float solar = Mathf.Lerp(_lastSolar, _solar, timeStacker);
            bool debugVisible = HeatWaveDebugRuntime.DebugMode > 0;
            bool active =
                intensity > Epsilon ||
                _cooldown > 0f ||
                whiteHeat > Epsilon ||
                debugVisible;

            if (!active)
            {
                HeatWaveRenderPipeline.Hide(sLeaser.sprites);
                base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
                return;
            }

            // Debug force/view can be enabled before the scheduled weather has ever
            // allocated this room's resources. Initialize lazily at the first draw.
            EnsureResources();

            Vector2 roomSize = _terrain?.RoomSizePixels ?? new Vector2(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);

            HeatWaveRenderFrame frame = new(
                roomSize,
                intensity,
                whiteHeat,
                solar,
                _burst.BurstStrength,
                _burst.BurstKick,
                _burst.Stillness,
                _visualTime,
                active,
                _simulation?.IsAvailable == true,
                _simulation?.OpticalTexture,
                _simulation?.ThermalTexture,
                _simulation?.VelocityTexture,
                _terrain?.Texture);

            HeatWaveRenderPipeline.Draw(
                sLeaser.sprites,
                rCam,
                frame,
                HeatWaveDebugRuntime.DebugMode);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            HeatWaveRenderPipeline.AddToContainers(sLeaser.sprites, rCam);
        }

        public void RoomUnloaded()
        {
            DisposeResources();
            Destroy();
        }

        public override void Destroy()
        {
            DisposeResources();
            if (room != null)
            {
                _controllers.Remove(room);
            }
            base.Destroy();
        }

        internal void DebugForceBurst()
        {
            EnsureResources();
            _burst.DebugTrigger();
        }

        internal bool TryGetDebugSnapshot(out HeatWaveDebugSnapshot snapshot)
        {
            int emitters = _simulation?.EmitterCount ?? CountPlacedEmitters();
            snapshot = new HeatWaveDebugSnapshot(
                _intensity,
                _solar,
                _whiteHeat,
                _burst.Instability,
                _burst.Stillness,
                _burst.BurstStrength,
                _burst.BurstKick,
                _simulation?.IsAvailable == true,
                _burst.PhaseName,
                emitters);
            return true;
        }

        private float EvaluateSolar(WorldClock clock)
        {
            if (_terrain != null)
            {
                return _terrain.EvaluateSolar(clock);
            }

            float directLight = clock?.Lighting.DirectLight ?? 1f;
            return Mathf.Clamp01(directLight * 0.62f);
        }

        private int CountPlacedEmitters()
        {
            if (room?.roomSettings?.placedObjects == null || HeatColumnHooks.PlacedType == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
            {
                PlacedObject placed = room.roomSettings.placedObjects[i];
                if (placed != null &&
                    placed.active &&
                    placed.type == HeatColumnHooks.PlacedType &&
                    placed.data is HeatColumnData)
                {
                    count++;
                }
            }
            return count;
        }

        private void EnsureResources()
        {
            if (_resourcesInitialized || _resourcesDisposed || room == null)
            {
                return;
            }

            _resourcesInitialized = true;
            try
            {
                _terrain = new HeatWaveTerrainField(room);
                _simulation = new HeatWaveThermalSimulation(room, _terrain);
                HeatWaveNoiseField.Ensure();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle could not construct HeatWave resources for " +
                    $"'{room?.abstractRoom?.name ?? "unknown"}'. " +
                    "The weather will retain its safe visual fallback.");
                Plugin.Logger?.LogError(ex);
                _simulation?.Dispose();
                _simulation = null;
                _terrain?.Dispose();
                _terrain = null;
            }
        }

        private void DisposeResources()
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
            _audio.Dispose();
            _simulation?.Dispose();
            _simulation = null;
            _terrain?.Dispose();
            _terrain = null;
        }
    }
}
