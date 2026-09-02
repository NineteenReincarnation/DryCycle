using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.TemperatureSystem;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Scheduled HeatWave owner.
///
/// The global weather follows Rain World's proven split: LevelHeat is the primary scene
/// deformation, a single atmosphere pass supplies whole-air shimmer/color/exposure, and
/// mapper-authored HeatColumn objects use local HeatDistortion. There is no thermal-fluid
/// compute simulation, plume field or burst state in the weather core.
/// </summary>
internal static class HeatWaveWeatherRuntime
{
    private const float Epsilon = 0.0001f;

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
    /// Deterministic gameplay influence. Rendering state never participates in gameplay
    /// temperature calculations.
    /// </summary>
    internal static float GetAmbientHeatInfluence(Room room)
    {
        return TryEvaluate(room, out float intensity)
            ? Mathf.Clamp01(intensity)
            : 0f;
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
        HeatColumnVisualRuntime.AttachToRoom(self);
    }

    private sealed class HeatWaveController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private readonly HeatWaveAudio _audio;

        private float _lastIntensity;
        private float _intensity;
        private float _lastSolar;
        private float _solar;
        private float _lastToneAmount;
        private float _toneAmount;
        private float _levelHeatAmount;
        private float _visualTime;
        private bool _disposed;

        internal HeatWaveController(Room ownerRoom)
        {
            room = ownerRoom;
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
            _lastSolar = _solar;
            _lastToneAmount = _toneAmount;

            if (!TryEvaluate(room, out float scheduled))
            {
                scheduled = 0f;
            }
            _intensity = Mathf.Clamp01(scheduled);

            WorldClock clock = null;
            if (room?.world != null)
            {
                WorldClockHooks.TryGetClock(room.world, out clock);
            }

            _solar = EvaluateSolar(clock);
            _toneAmount = EvaluateToneAmount(_intensity, _solar);
            _levelHeatAmount = HeatWaveLevelEffect.EvaluateWeatherAmount(_intensity, _solar);
            _visualTime += 1f / 40f;

            if (_intensity > Epsilon)
            {
                HeatWaveNoiseField.Ensure();
            }

            _audio.Update(_intensity, _solar, _visualTime);
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
                HeatWaveLevelEffect.Release(rCam, room);
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            float intensity = Mathf.Lerp(_lastIntensity, _intensity, timeStacker);
            float solar = Mathf.Lerp(_lastSolar, _solar, timeStacker);
            float toneAmount = Mathf.Lerp(_lastToneAmount, _toneAmount, timeStacker);
            float levelHeatAmount = HeatWaveLevelEffect.EvaluateWeatherAmount(intensity, solar);
            bool debugVisible = HeatWaveDebugRuntime.DebugMode > 0;
            bool active = intensity > Epsilon || debugVisible;

            if (intensity > Epsilon)
            {
                HeatWaveLevelEffect.Apply(rCam, room, intensity, solar);
            }
            else
            {
                HeatWaveLevelEffect.Release(rCam, room);
            }

            if (!active)
            {
                HeatWaveRenderPipeline.Hide(sLeaser.sprites);
                base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
                return;
            }

            Vector2 roomSize = new(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);

            HeatWaveRenderFrame frame = new(
                roomSize,
                intensity,
                solar,
                toneAmount,
                levelHeatAmount,
                _visualTime,
                active);

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
            Destroy();
        }

        public override void Destroy()
        {
            if (!_disposed)
            {
                _disposed = true;
                _audio.Dispose();
                HeatWaveLevelEffect.RestoreForRoom(room);
            }

            if (room != null)
            {
                _controllers.Remove(room);
            }

            base.Destroy();
        }

        internal bool TryGetDebugSnapshot(out HeatWaveDebugSnapshot snapshot)
        {
            snapshot = new HeatWaveDebugSnapshot(
                _intensity,
                _solar,
                _toneAmount,
                _levelHeatAmount,
                HeatWaveLevelEffect.IsApplied(room),
                CountPlacedEmitters());
            return true;
        }

        private float EvaluateSolar(WorldClock clock)
        {
            float directLight = Mathf.Clamp01(clock?.Lighting.DirectLight ?? 1f);
            float roomShade = Mathf.Clamp01(SolarEnvironment.GetRoomShade(room));
            float authoredSun = Mathf.Clamp01(SolarEnvironment.GetSunlightIntensity(room));
            float roomTransmission = 1f - roomShade;
            float outdoorBaseline = Mathf.Lerp(0.72f, 1f, authoredSun);
            return Mathf.Clamp01(directLight * roomTransmission * outdoorBaseline);
        }

        private static float EvaluateToneAmount(float intensity, float solar)
        {
            float heat = Mathf.Clamp01(intensity);
            if (heat <= Epsilon)
            {
                return 0f;
            }

            // Heat remains perceptible when the sun is weak, while the bleached noon
            // state becomes dominant under direct light.
            float solarDrive = Mathf.Pow(Mathf.Clamp01(solar), 0.72f);
            return Mathf.Clamp01(heat * Mathf.Lerp(0.34f, 1f, solarDrive));
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
    }
}
