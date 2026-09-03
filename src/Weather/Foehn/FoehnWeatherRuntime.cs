using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Scheduled Foehn owner. Foehn is intentionally distinct from HeatWave: the dominant
/// signature is fast directional air, coherent gust fronts, lee wakes/nozzles and
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
        FoehnDebugRuntime.Enable();
        FoehnWindPhysics.Enable();
        On.Room.Loaded += Room_Loaded;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        FoehnWindPhysics.Disable();
        FoehnDebugRuntime.Disable();
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

        if (FoehnDebugRuntime.TryGetForcedIntensity(room, out float forced))
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

    internal static bool TrySampleWind(
        Room room,
        Vector2 worldPosition,
        out FoehnWindSample sample)
    {
        sample = default;
        return _enabled &&
               room != null &&
               _controllers.TryGetValue(room, out FoehnController controller) &&
               controller.TrySampleWind(worldPosition, out sample);
    }

    internal static bool TryGetDebugSnapshot(Room room, out FoehnDebugSnapshot snapshot)
    {
        snapshot = default;
        return room != null &&
               _controllers.TryGetValue(room, out FoehnController controller) &&
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

        FoehnController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private sealed class FoehnController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private readonly FoehnParticleField _particles;
        private readonly FoehnAudio _audio;
        private readonly float _gustSeed;

        private float _lastIntensity;
        private float _intensity;
        private float _visualTime;
        private Vector2 _windDirection;
        private Vector2 _terrainFieldDirection;
        private FoehnTerrainField _terrainField;
        private bool _terrainFieldAttempted;
        private bool _disposed;

        internal FoehnController(Room ownerRoom)
        {
            room = ownerRoom;
            _windDirection = ResolveWindDirection(ownerRoom);
            _terrainFieldDirection = _windDirection;
            _gustSeed = FoehnGustField.BuildRoomSeed(ownerRoom);
            _particles = new FoehnParticleField(ownerRoom);
            _audio = new FoehnAudio(this);
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

            Vector2 nextWindDirection = ResolveWindDirection(room);
            if (_terrainField != null &&
                Vector2.Dot(nextWindDirection, _terrainFieldDirection) < 0.94f)
            {
                _terrainField.Dispose();
                _terrainField = null;
                _terrainFieldAttempted = false;
            }

            _windDirection = nextWindDirection;
            _visualTime += 1f / 40f;

            bool debugVisible = FoehnDebugRuntime.DebugMode > 0;
            if (_intensity > Epsilon || debugVisible)
            {
                FoehnWindField.Ensure();
                EnsureTerrainField();
            }

            ResolveCameraViewport(out Vector2 cameraPos, out Vector2 cameraSize);
            FoehnGustSample cameraGust = FoehnGustField.Sample(
                cameraPos + cameraSize * 0.5f,
                _visualTime,
                _intensity,
                _windDirection,
                _gustSeed);

            _particles.Update(
                room,
                _intensity,
                _windDirection,
                _terrainField,
                _visualTime,
                _gustSeed,
                cameraPos,
                cameraSize);
            _audio.Update(
                _intensity,
                cameraGust.Body,
                cameraGust.Front,
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
            bool debugVisible = FoehnDebugRuntime.DebugMode > 0;
            if (intensity <= Epsilon && !debugVisible)
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
                _gustSeed,
                _terrainField?.Texture);

            _particles.Draw(
                sLeaser.sprites,
                FoehnRenderPipeline.ParticleSpriteOffset,
                timeStacker,
                camPos,
                intensity,
                _windDirection,
                _terrainField);
            FoehnRenderPipeline.DrawAtmosphere(
                sLeaser.sprites,
                rCam,
                frame,
                FoehnDebugRuntime.DebugMode);

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
                _audio.Dispose();
                _terrainField?.Dispose();
                _terrainField = null;
            }

            if (room != null)
            {
                _controllers.Remove(room);
            }

            base.Destroy();
        }

        internal bool TrySampleWind(
            Vector2 worldPosition,
            out FoehnWindSample sample)
        {
            sample = default;
            if (_disposed || room == null || _intensity <= Epsilon)
            {
                return false;
            }

            FoehnTerrainSample terrain =
                _terrainField?.Sample(worldPosition) ?? FoehnTerrainSample.OpenAir;
            FoehnGustSample gust = FoehnGustField.Sample(
                worldPosition,
                _visualTime,
                _intensity,
                _windDirection,
                _gustSeed);
            sample = new FoehnWindSample(
                _windDirection,
                _intensity,
                gust,
                terrain);
            return true;
        }

        internal bool TryGetDebugSnapshot(out FoehnDebugSnapshot snapshot)
        {
            snapshot = new FoehnDebugSnapshot(
                _intensity,
                _windDirection,
                _terrainField != null);
            return true;
        }

        private void EnsureTerrainField()
        {
            if (_terrainFieldAttempted)
            {
                return;
            }

            _terrainFieldAttempted = true;
            _terrainFieldDirection = _windDirection;
            _terrainField = FoehnTerrainField.Build(room, _windDirection);
        }

        private void ResolveCameraViewport(out Vector2 cameraPos, out Vector2 cameraSize)
        {
            cameraSize = new Vector2(1366f, 768f);
            try
            {
                if (room?.game?.rainWorld?.options != null)
                {
                    cameraSize = new Vector2(
                        Mathf.Max(1f, room.game.rainWorld.options.ScreenSize.x),
                        Mathf.Max(1f, room.game.rainWorld.options.ScreenSize.y));
                }
            }
            catch
            {
                cameraSize = new Vector2(1366f, 768f);
            }

            RoomCamera camera = room?.game?.cameras != null &&
                                room.game.cameras.Length > 0
                ? room.game.cameras[0]
                : null;
            if (camera?.room == room)
            {
                cameraPos = camera.pos;
                return;
            }

            Vector2 roomSize = new(
                Mathf.Max(20f, (room?.TileWidth ?? 1) * 20f),
                Mathf.Max(20f, (room?.TileHeight ?? 1) * 20f));
            cameraPos = Vector2.Max(
                Vector2.zero,
                roomSize * 0.5f - cameraSize * 0.5f);
        }
    }
}
