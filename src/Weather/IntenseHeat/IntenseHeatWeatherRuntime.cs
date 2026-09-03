using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.TemperatureSystem;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

/// <summary>
/// Scheduled DangerType owner for disaster-grade direct solar heat.
///
/// IntenseHeat is not HeatWave with a larger scalar. It owns a direct-sun exposure
/// field, a separate atmosphere shader, creature scorching and additional player solar
/// heat. HeatWave's deterministic optical textures are reused only as low-level noise
/// resources so the two weather types remain visually related without sharing identity.
/// </summary>
internal static class IntenseHeatWeatherRuntime
{
    private const float Epsilon = 0.0001f;

    private static ConditionalWeakTable<Room, IntenseHeatController> _controllers = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        IntenseHeatCreatureExposure.Enable();
        IntenseHeatDebugRuntime.Enable();
        On.Room.Loaded += Room_Loaded;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        IntenseHeatDebugRuntime.Disable();
        IntenseHeatCreatureExposure.Disable();
        _controllers = new ConditionalWeakTable<Room, IntenseHeatController>();
        _enabled = false;
    }

    internal static bool TryEvaluate(Room room, out float intensity)
    {
        intensity = 0f;
        if (!_enabled)
        {
            return false;
        }

        if (IntenseHeatDebugRuntime.TryGetForcedIntensity(room, out float forced))
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
            WeatherScheduleEventKind.DangerType,
            "IntenseHeat");
        return intensity > Epsilon;
    }

    internal static float GetAmbientHeatInfluence(Room room)
    {
        return TryEvaluate(room, out float intensity)
            ? Mathf.Clamp01(intensity)
            : 0f;
    }

    internal static float GetDirectExposureAt(Room room, Vector2 worldPos)
    {
        if (!TryEvaluate(room, out float intensity))
        {
            return 0f;
        }

        return Mathf.Clamp01(
            IntenseHeatSolarField.SampleExposure(room, worldPos) * intensity);
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

        IntenseHeatController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private sealed class IntenseHeatController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private float _lastIntensity;
        private float _intensity;
        private float _lastSolar;
        private float _solar;
        private float _visualTime;
        private Texture2D _solarField;
        private Texture2D _surfaceField;
        private bool _fieldsAttempted;
        private bool _disposed;

        internal IntenseHeatController(Room ownerRoom)
        {
            room = ownerRoom;
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

            if (!TryEvaluate(room, out float scheduled))
            {
                scheduled = 0f;
            }

            _intensity = Mathf.Clamp01(scheduled);
            _solar = EvaluateSolar(room);
            _visualTime += 1f / 40f;

            if (_intensity > Epsilon)
            {
                HeatWaveNoiseField.Ensure();
                EnsureFields();
            }

            IntenseHeatCreatureExposure.UpdateRoom(room, _intensity);
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);
            sLeaser.sprites = IntenseHeatRenderPipeline.CreateSprites(rCam);
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
            float solar = Mathf.Lerp(_lastSolar, _solar, timeStacker);
            bool debugVisible = IntenseHeatDebugRuntime.DebugMode > 0;
            bool active = intensity > Epsilon || debugVisible;

            if (!active)
            {
                IntenseHeatRenderPipeline.Hide(sLeaser.sprites);
                base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
                return;
            }

            HeatWaveNoiseField.Ensure();
            EnsureFields();

            Vector2 roomSizePx = new(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);

            IntenseHeatRenderFrame frame = new(
                roomSizePx,
                intensity,
                solar,
                _visualTime,
                _solarField,
                _surfaceField,
                active);

            IntenseHeatRenderPipeline.Draw(
                sLeaser.sprites,
                rCam,
                frame,
                IntenseHeatDebugRuntime.DebugMode);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            IntenseHeatRenderPipeline.AddToContainers(sLeaser.sprites, rCam);
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
                IntenseHeatSolarField.Dispose(_solarField);
                HeatWaveSurfaceField.Dispose(_surfaceField);
                _solarField = null;
                _surfaceField = null;
            }

            if (room != null)
            {
                _controllers.Remove(room);
            }

            base.Destroy();
        }

        private void EnsureFields()
        {
            if (_fieldsAttempted)
            {
                return;
            }

            _fieldsAttempted = true;
            _solarField = IntenseHeatSolarField.Build(room);
            _surfaceField = HeatWaveSurfaceField.Build(room);
        }

        private static float EvaluateSolar(Room targetRoom)
        {
            WorldClock clock = null;
            if (targetRoom?.world != null)
            {
                WorldClockHooks.TryGetClock(targetRoom.world, out clock);
            }

            if (clock?.IsNight == true)
            {
                return 0f;
            }

            float directLight = Mathf.Clamp01(clock?.Lighting.DirectLight ?? 1f);
            float roomShade = Mathf.Clamp01(SolarEnvironment.GetRoomShade(targetRoom));
            float authoredSun = Mathf.Clamp01(SolarEnvironment.GetSunlightIntensity(targetRoom));
            float transmission = 1f - roomShade;

            // IntenseHeat's identity is brutal daytime direct sun. As long as the room
            // is not authored as strongly enclosed, daylight is pushed close to full
            // solar load rather than inheriting a weak ordinary-weather sun value.
            float daylight = Mathf.Lerp(0.88f, 1f, Mathf.Max(directLight, authoredSun));
            return Mathf.Clamp01(daylight * transmission);
        }
    }
}
