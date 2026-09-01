using System;
using DryCycle.DayNight;
using DryCycle.Rendering;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// WorldClock-driven HeatWave owner. The weather has no RoomSettings effect and does
/// not borrow any native Rain World heat-haze object. Each eligible room receives one
/// persistent controller whose GPU state survives frame to frame; the schedule only
/// drives its energy input and fade envelope.
/// </summary>
internal static class HeatWaveWeatherRuntime
{
    private const float Epsilon = 0.0001f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        HeatColumnHooks.Enable();
        On.Room.Loaded += Room_Loaded;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        HeatColumnHooks.Disable();
        _enabled = false;
    }

    internal static bool TryEvaluate(Room room, out float intensity)
    {
        intensity = 0f;
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

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        string region = self?.world?.region?.name;
        if (self?.game == null ||
            !self.game.IsStorySession ||
            string.IsNullOrWhiteSpace(region) ||
            !RegionClimateRegistry.RegionCanUseWeather(region, "HeatWave"))
        {
            return;
        }

        self.AddObject(new HeatWaveController(self));
    }

    private sealed class HeatWaveController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private static readonly MaterialPropertyBlock MaterialProperties = new();
        private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleRoomSizePx");
        private static readonly int IntensityId = Shader.PropertyToID("_DryCycleHeatWaveIntensity");
        private static readonly int WhiteHeatId = Shader.PropertyToID("_DryCycleWhiteHeat");
        private static readonly int BurstId = Shader.PropertyToID("_DryCycleHeatBurst");
        private static readonly int StillnessId = Shader.PropertyToID("_DryCycleHeatStillness");
        private static readonly int TimeId = Shader.PropertyToID("_DryCycleHeatTime");
        private static readonly int HasSimulationId = Shader.PropertyToID("_DryCycleHasHeatSimulation");
        private static readonly int OpticalTextureId = Shader.PropertyToID("_DryCycleHeatOpticalTex");
        private static readonly int ThermalTextureId = Shader.PropertyToID("_DryCycleHeatThermalTex");
        private static readonly int VelocityTextureId = Shader.PropertyToID("_DryCycleHeatVelocityTex");
        private static readonly int TerrainTextureId = Shader.PropertyToID("_DryCycleHeatTerrainTex");

        private readonly HeatWaveBurstController _burst;
        private HeatWaveTerrainField _terrain;
        private HeatWaveThermalSimulation _simulation;
        private bool _resourcesDisposed;

        private float _lastIntensity;
        private float _intensity;
        private float _lastWhiteHeat;
        private float _whiteHeat;
        private float _visualTime;
        private float _cooldown;

        internal HeatWaveController(Room ownerRoom)
        {
            room = ownerRoom;
            _burst = new HeatWaveBurstController(ownerRoom);

            try
            {
                _terrain = new HeatWaveTerrainField(ownerRoom);
                _simulation = new HeatWaveThermalSimulation(ownerRoom, _terrain);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle could not construct HeatWave resources for " +
                    $"'{ownerRoom?.abstractRoom?.name ?? "unknown"}'.");
                Plugin.Logger?.LogError(ex);
                DisposeResources();
            }
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

            if (!TryEvaluate(room, out float scheduled))
            {
                scheduled = 0f;
            }
            _intensity = Mathf.Clamp01(scheduled);

            float solar = _terrain?.RoomSolarIntensity ?? 0f;
            _burst.Update(1f / 40f, _intensity, solar);

            float whiteBase = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 1f, _intensity));
            _whiteHeat = Mathf.Clamp01(
                whiteBase * Mathf.Lerp(0.42f, 1f, solar) +
                _burst.BurstStrength * 0.10f);

            _visualTime += 1f / 40f;

            if (_intensity > Epsilon)
            {
                _cooldown = 12f;
            }
            else
            {
                _cooldown = Mathf.Max(0f, _cooldown - 1f / 40f);
            }

            if ((_intensity > Epsilon || _cooldown > 0f) &&
                _simulation?.IsAvailable == true)
            {
                _simulation.Step(1f / 40f, _intensity, _burst);
            }
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);

            float screenWidth = rCam.game.rainWorld.options.ScreenSize.x;
            float screenHeight = rCam.game.rainWorld.options.ScreenSize.y;
            FSprite composite = new("Futile_White")
            {
                anchorX = 0f,
                anchorY = 0f,
                scaleX = screenWidth / 16f,
                scaleY = screenHeight / 16f,
                alpha = 1f,
                isVisible = false
            };

            if (DryCycleShaderAssets.HasHeatWaveComposite)
            {
                composite.shader = DryCycleShaderAssets.HeatWaveComposite;
            }
            else
            {
                composite.shader = rCam.game.rainWorld.Shaders["Basic"];
            }

            sLeaser.sprites = new[] { composite };
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

            FSprite composite = sLeaser.sprites[0];
            composite.x = 0f;
            composite.y = 0f;

            float intensity = Mathf.Lerp(_lastIntensity, _intensity, timeStacker);
            float whiteHeat = Mathf.Lerp(_lastWhiteHeat, _whiteHeat, timeStacker);
            composite.isVisible = DryCycleShaderAssets.HasHeatWaveComposite &&
                                  (intensity > Epsilon || _cooldown > 0f);

            if (composite.isVisible)
            {
                ApplyCompositeProperties(composite, intensity, whiteHeat);
            }

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            sLeaser.sprites[0].RemoveFromContainer();
            rCam.ReturnFContainer("GrabShaders").AddChild(sLeaser.sprites[0]);
        }

        public void RoomUnloaded()
        {
            DisposeResources();
            Destroy();
        }

        public override void Destroy()
        {
            DisposeResources();
            base.Destroy();
        }

        private void ApplyCompositeProperties(
            FSprite sprite,
            float intensity,
            float whiteHeat)
        {
            Renderer renderer = sprite?._renderLayer?._meshRenderer;
            if (renderer == null)
            {
                return;
            }

            Vector2 roomSize = _terrain?.RoomSizePixels ?? new Vector2(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);

            MaterialProperties.Clear();
            renderer.GetPropertyBlock(MaterialProperties);
            MaterialProperties.SetVector(RoomSizeId, new Vector4(
                roomSize.x,
                roomSize.y,
                0f,
                0f));
            MaterialProperties.SetFloat(IntensityId, intensity);
            MaterialProperties.SetFloat(WhiteHeatId, whiteHeat);
            MaterialProperties.SetFloat(BurstId, _burst.BurstStrength);
            MaterialProperties.SetFloat(StillnessId, _burst.Stillness);
            MaterialProperties.SetFloat(TimeId, _visualTime);
            MaterialProperties.SetFloat(
                HasSimulationId,
                _simulation?.IsAvailable == true ? 1f : 0f);
            MaterialProperties.SetTexture(
                OpticalTextureId,
                _simulation?.OpticalTexture ?? Texture2D.blackTexture);
            MaterialProperties.SetTexture(
                ThermalTextureId,
                _simulation?.ThermalTexture ?? Texture2D.blackTexture);
            MaterialProperties.SetTexture(
                VelocityTextureId,
                _simulation?.VelocityTexture ?? Texture2D.blackTexture);
            MaterialProperties.SetTexture(
                TerrainTextureId,
                _terrain?.Texture ?? Texture2D.blackTexture);
            renderer.SetPropertyBlock(MaterialProperties);
        }

        private void DisposeResources()
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
            _simulation?.Dispose();
            _simulation = null;
            _terrain?.Dispose();
            _terrain = null;
        }
    }
}
