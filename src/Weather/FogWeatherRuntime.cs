using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// WorldClock-driven Fog / DenseFog renderer. The weather owns two passes of Rain
/// World's native Fog shader instead of replacing authored RoomSettings Fog, so a
/// mapper-authored fog effect and scheduled weather naturally stack on screen.
///
/// The weather passes live inside ForegroundLights. Environmental LightSource sprites
/// are ordered behind the fog while ordinary local lights are ordered in front of it.
/// This preserves every local light's native radius, alpha, flicker and color without
/// maintaining a lantern/creature/mod-object whitelist.
/// </summary>
internal static class FogWeatherRuntime
{
    private const float Epsilon = 0.0001f;

    // One native Fog pass is not dense enough for the weather design. Fog therefore
    // uses a strong base pass plus a lighter second pass; DenseFog nearly saturates
    // both passes. The schedule envelope still supplies the 15 s fade-in/out.
    private const float FogBaseStrength = 0.86f;
    private const float FogExtraStrength = 0.42f;
    private const float DenseFogBaseStrength = 1.00f;
    private const float DenseFogExtraStrength = 0.92f;

    private sealed class CameraFogState
    {
        internal FSprite BackPass;
        internal FSprite FrontPass;
        internal bool Active;
    }

    private sealed class LightOrderingState
    {
        internal FSprite Anchor;
        internal bool Environmental;
    }

    private sealed class FogWeatherController : CosmeticSprite
    {
        private float _lastBaseStrength;
        private float _baseStrength;
        private float _lastExtraStrength;
        private float _extraStrength;

        internal FogWeatherController(Room room)
        {
            this.room = room;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);

            _lastBaseStrength = _baseStrength;
            _lastExtraStrength = _extraStrength;

            if (!TryEvaluate(room, out float fog, out float denseFog))
            {
                _baseStrength = 0f;
                _extraStrength = 0f;
                return;
            }

            _baseStrength = Mathf.Clamp01(
                fog * FogBaseStrength + denseFog * DenseFogBaseStrength);
            _extraStrength = Mathf.Clamp01(
                fog * FogExtraStrength + denseFog * DenseFogExtraStrength);
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);

            sLeaser.sprites = new FSprite[2];
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                FSprite fog = new("Futile_White")
                {
                    anchorX = 0f,
                    anchorY = 0f,
                    scaleX = rCam.game.rainWorld.options.ScreenSize.x / 16f,
                    scaleY = 48f,
                    shader = rCam.game.rainWorld.Shaders["Fog"],
                    alpha = 0f,
                    isVisible = false
                };
                sLeaser.sprites[i] = fog;
            }

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

            float baseStrength = Mathf.Lerp(
                _lastBaseStrength,
                _baseStrength,
                timeStacker);
            float extraStrength = Mathf.Lerp(
                _lastExtraStrength,
                _extraStrength,
                timeStacker);

            FSprite back = sLeaser.sprites[0];
            FSprite front = sLeaser.sprites[1];
            back.x = back.y = 0f;
            front.x = front.y = 0f;
            back.alpha = baseStrength;
            front.alpha = extraStrength;
            back.isVisible = baseStrength > Epsilon;
            front.isVisible = extraStrength > Epsilon;

            RegisterCameraFog(rCam, back, front, baseStrength > Epsilon || extraStrength > Epsilon);
            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            FContainer lights = rCam.ReturnFContainer("ForegroundLights");
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                sLeaser.sprites[i].RemoveFromContainer();
                lights.AddChild(sLeaser.sprites[i]);
            }

            // The first pass is the back boundary and the second pass is the front
            // boundary. Local lights are placed after FrontPass, environmental lights
            // before BackPass.
            sLeaser.sprites[1].MoveInFrontOfOtherNode(sLeaser.sprites[0]);
            RegisterCameraFog(rCam, sLeaser.sprites[0], sLeaser.sprites[1], false);
        }
    }

    private static ConditionalWeakTable<Room, FogWeatherController> _controllers = new();
    private static ConditionalWeakTable<RoomCamera, CameraFogState> _cameraStates = new();
    private static ConditionalWeakTable<LightSource, LightOrderingState> _lightStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Room.Loaded += Room_Loaded;
        On.LightSource.DrawSprites += LightSource_DrawSprites;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        On.LightSource.DrawSprites -= LightSource_DrawSprites;
        _controllers = new ConditionalWeakTable<Room, FogWeatherController>();
        _cameraStates = new ConditionalWeakTable<RoomCamera, CameraFogState>();
        _lightStates = new ConditionalWeakTable<LightSource, LightOrderingState>();
        _enabled = false;
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        World world = self?.world;
        string regionId = world?.region?.name;
        if (self?.game == null ||
            world?.game == null ||
            !self.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            string.IsNullOrWhiteSpace(regionId) ||
            (!RegionClimateRegistry.RegionCanUseWeather(regionId, "Fog") &&
             !RegionClimateRegistry.RegionCanUseWeather(regionId, "DenseFog")) ||
            _controllers.TryGetValue(self, out _))
        {
            return;
        }

        FogWeatherController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private static void LightSource_DrawSprites(
        On.LightSource.orig_DrawSprites orig,
        LightSource self,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        orig(self, sLeaser, rCam, timeStacker, camPos);

        if (self == null ||
            rCam == null ||
            sLeaser?.sprites == null ||
            sLeaser.sprites.Length == 0 ||
            sLeaser.sprites[0] == null ||
            self.LayerName != "ForegroundLights" ||
            !_cameraStates.TryGetValue(rCam, out CameraFogState cameraState) ||
            !cameraState.Active ||
            cameraState.BackPass == null ||
            cameraState.FrontPass == null)
        {
            return;
        }

        LightOrderingState order = _lightStates.GetOrCreateValue(self);
        FSprite anchor = self.environmentalLight
            ? cameraState.BackPass
            : cameraState.FrontPass;

        // Reorder only when the fog sprite or light category changed. The ordering is
        // stable across frames, so Fog does not perform per-frame room/light scans.
        if (order.Anchor == anchor && order.Environmental == self.environmentalLight)
        {
            return;
        }

        if (self.environmentalLight)
        {
            sLeaser.sprites[0].MoveBehindOtherNode(cameraState.BackPass);
        }
        else
        {
            sLeaser.sprites[0].MoveInFrontOfOtherNode(cameraState.FrontPass);
        }

        order.Anchor = anchor;
        order.Environmental = self.environmentalLight;
    }

    private static bool TryEvaluate(Room room, out float fog, out float denseFog)
    {
        fog = 0f;
        denseFog = 0f;

        World world = room?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return false;
        }

        WeatherScheduleRuntime.Synchronize(world);
        fog = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "Fog");
        denseFog = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "DenseFog");
        return fog > Epsilon || denseFog > Epsilon;
    }

    private static void RegisterCameraFog(
        RoomCamera camera,
        FSprite backPass,
        FSprite frontPass,
        bool active)
    {
        if (camera == null)
        {
            return;
        }

        CameraFogState state = _cameraStates.GetOrCreateValue(camera);
        state.BackPass = backPass;
        state.FrontPass = frontPass;
        state.Active = active;
    }
}
