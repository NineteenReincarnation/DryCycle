using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Scheduled Foehn owner. Foehn is intentionally distinct from HeatWave: the dominant
/// signature is fast directional air, coherent gust sheets, lee wakes/nozzles and
/// wind-carried mineral streaks. Any hot-air refraction is subordinate to that flow.
/// </summary>
internal static class FoehnWeatherRuntime
{
    private const float Epsilon = 0.0001f;

    private static ConditionalWeakTable<Room, FoehnController> _controllers = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Loaded += Room_Loaded;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        _controllers = new ConditionalWeakTable<Room, FoehnController>();
        _enabled = false;
    }

    internal static bool TryEvaluate(Room room, out float intensity)
    {
        intensity = 0f;
        if (!_enabled)
        {
            return false;
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
            "Foehn");
        return intensity > Epsilon;
    }

    internal static Vector2 ResolveWindDirection(Room room)
    {
        if (room != null)
        {
            Vector2 authored = room.windDirection;
            if (authored.sqrMagnitude > 0.01f)
            {
                return authored.normalized;
            }
        }

        // Stable default: strong descending lee-side flow. Rooms can still author
        // WindDirection and Foehn will honor it without requiring a new RoomSettings UI.
        return new Vector2(1f, -0.16f).normalized;
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

        FoehnController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private sealed class FoehnController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private readonly FoehnParticleField _particles;

        private float _lastIntensity;
        private float _intensity;
        private float _visualTime;
        private Vector2 _windDirection;
        private FoehnTerrainField _terrainField;
        private bool _terrainFieldAttempted;
        private bool _disposed;

        internal FoehnController(Room ownerRoom)
        {
            room = ownerRoom;
            _windDirection = ResolveWindDirection(ownerRoom);
            _particles = new FoehnParticleField(ownerRoom);
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
            _intensity = TryEvaluate(room, out float scheduled)
                ? Mathf.Clamp01(scheduled)
                : 0f;
            _windDirection = ResolveWindDirection(room);
            _visualTime += 1f / 40f;

            if (_intensity > Epsilon)
            {
                FoehnWindField.Ensure();
                EnsureTerrainField();
            }

            _particles.Update(
                room,
                _intensity,
                _windDirection,
                _terrainField,
                _visualTime);
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);
            sLeaser.sprites = FoehnRenderPipeline.CreateSprites(rCam);
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
            if (intensity <= Epsilon)
            {
                FoehnRenderPipeline.Hide(sLeaser.sprites);
                base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
                return;
            }

            FoehnWindField.Ensure();
            EnsureTerrainField();

            Vector2 roomSizePx = new(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);
            FoehnRenderFrame frame = new(
                roomSizePx,
                intensity,
                _visualTime,
                _windDirection,
                _terrainField?.Texture);

            _particles.Draw(
                sLeaser.sprites,
                FoehnRenderPipeline.ParticleSpriteOffset,
                timeStacker,
                camPos,
                intensity,
                _windDirection,
                _terrainField);
            FoehnRenderPipeline.DrawAtmosphere(sLeaser.sprites, rCam, frame);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            FoehnRenderPipeline.AddToContainers(sLeaser.sprites, rCam);
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
                _terrainField?.Dispose();
                _terrainField = null;
            }

            if (room != null)
            {
                _controllers.Remove(room);
            }

            base.Destroy();
        }

        private void EnsureTerrainField()
        {
            if (_terrainFieldAttempted)
            {
                return;
            }

            _terrainFieldAttempted = true;
            _terrainField = FoehnTerrainField.Build(room, _windDirection);
        }
    }
}
