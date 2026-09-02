using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveRenderFrame
{
    internal readonly Vector2 RoomSize;
    internal readonly float Intensity;
    internal readonly float WhiteHeat;
    internal readonly float SolarIntensity;
    internal readonly float Time;
    internal readonly bool Active;
    internal readonly bool HasSimulation;
    internal readonly Texture OpticalTexture;
    internal readonly Texture ThermalTexture;
    internal readonly Texture VelocityTexture;
    internal readonly Texture TerrainTexture;

    internal HeatWaveRenderFrame(
        Vector2 roomSize,
        float intensity,
        float whiteHeat,
        float solarIntensity,
        float time,
        bool active,
        bool hasSimulation,
        Texture opticalTexture,
        Texture thermalTexture,
        Texture velocityTexture,
        Texture terrainTexture)
    {
        RoomSize = roomSize;
        Intensity = Mathf.Clamp01(intensity);
        WhiteHeat = Mathf.Clamp01(whiteHeat);
        SolarIntensity = Mathf.Clamp01(solarIntensity);
        Time = time;
        Active = active;
        HasSimulation = hasSimulation;
        OpticalTexture = opticalTexture;
        ThermalTexture = thermalTexture;
        VelocityTexture = velocityTexture;
        TerrainTexture = terrainTexture;
    }
}

/// <summary>
/// Final HeatWave presentation pass.
///
/// The earlier three-pass compositor recursively refracted the same image and made the
/// whole room read as underwater. HeatWave now uses exactly one late world pass. Depth,
/// exposed ground and the thermal field only decide WHERE air may shimmer; they never
/// multiply the number of scene refractions. HUD/HUD2 are rendered later and remain
/// untouched.
/// </summary>
internal static class HeatWaveRenderPipeline
{
    internal const int AtmosphereLayer = 0;
    internal const int LayerCount = 1;

    private const float Epsilon = 0.0001f;

    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleHeatWaveIntensity");
    private static readonly int WhiteHeatId = Shader.PropertyToID("_DryCycleWhiteHeat");
    private static readonly int SolarIntensityId = Shader.PropertyToID("_DryCycleHeatSolarIntensity");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleHeatTime");
    private static readonly int HasSimulationId = Shader.PropertyToID("_DryCycleHasHeatSimulation");
    private static readonly int OpticalTextureId = Shader.PropertyToID("_DryCycleHeatOpticalTex");
    private static readonly int ThermalTextureId = Shader.PropertyToID("_DryCycleHeatThermalTex");
    private static readonly int VelocityTextureId = Shader.PropertyToID("_DryCycleHeatVelocityTex");
    private static readonly int TerrainTextureId = Shader.PropertyToID("_DryCycleHeatTerrainTex");
    private static readonly int MacroNoiseId = Shader.PropertyToID("_DryCycleHeatMacroNoise");
    private static readonly int MicroNoiseId = Shader.PropertyToID("_DryCycleHeatMicroNoise");
    private static readonly int HasCustomNoiseId = Shader.PropertyToID("_DryCycleHasHeatCustomNoise");
    private static readonly int DebugModeId = Shader.PropertyToID("_DryCycleHeatDebugMode");

    internal static FSprite[] CreateSprites(RoomCamera camera)
    {
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;

        FSprite atmosphere = new("Futile_White")
        {
            anchorX = 0f,
            anchorY = 0f,
            scaleX = screenWidth / 16f,
            scaleY = screenHeight / 16f,
            alpha = 1f,
            isVisible = false,
            shader = DryCycleShaderAssets.HasHeatWaveComposite
                ? DryCycleShaderAssets.HeatWaveComposite
                : camera.game.rainWorld.Shaders["Basic"]
        };

        return new[] { atmosphere };
    }

    internal static void AddToContainers(FSprite[] sprites, RoomCamera camera)
    {
        if (sprites == null || sprites.Length == 0 || camera == null)
        {
            return;
        }

        FSprite atmosphere = sprites[AtmosphereLayer];
        atmosphere.RemoveFromContainer();
        camera.ReturnFContainer("GrabShaders").AddChild(atmosphere);
        atmosphere.MoveToFront();
    }

    internal static void Draw(
        FSprite[] sprites,
        RoomCamera camera,
        in HeatWaveRenderFrame frame,
        int debugMode)
    {
        if (sprites == null || sprites.Length == 0 || camera == null)
        {
            return;
        }

        FSprite sprite = sprites[AtmosphereLayer];
        if (sprite == null)
        {
            return;
        }

        if (!DryCycleShaderAssets.HasHeatWaveComposite)
        {
            DrawFallback(sprite, camera, frame);
            return;
        }

        HeatWaveNoiseField.Ensure();
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;

        sprite.shader = DryCycleShaderAssets.HeatWaveComposite;
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.alpha = 1f;
        sprite.isVisible = frame.Active;

        if (!sprite.isVisible)
        {
            return;
        }

        sprite.MoveToFront();
        ApplyProperties(sprite, frame, debugMode);
    }

    internal static void Hide(FSprite[] sprites)
    {
        if (sprites == null)
        {
            return;
        }

        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null)
            {
                sprites[i].isVisible = false;
            }
        }
    }

    private static void DrawFallback(
        FSprite sprite,
        RoomCamera camera,
        in HeatWaveRenderFrame frame)
    {
        if (!frame.Active)
        {
            sprite.isVisible = false;
            return;
        }

        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = camera.game.rainWorld.Shaders["Basic"];
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.color = new Color(1f, 0.975f, 0.90f);
        sprite.alpha = Mathf.Clamp01(frame.WhiteHeat * 0.055f);
        sprite.isVisible = sprite.alpha > Epsilon;
        if (sprite.isVisible)
        {
            sprite.MoveToFront();
        }
    }

    private static void ApplyProperties(
        FSprite sprite,
        in HeatWaveRenderFrame frame,
        int debugMode)
    {
        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(RoomSizeId, new Vector4(
            frame.RoomSize.x,
            frame.RoomSize.y,
            0f,
            0f));
        MaterialProperties.SetFloat(IntensityId, frame.Intensity);
        MaterialProperties.SetFloat(WhiteHeatId, frame.WhiteHeat);
        MaterialProperties.SetFloat(SolarIntensityId, frame.SolarIntensity);
        MaterialProperties.SetFloat(TimeId, frame.Time);
        MaterialProperties.SetFloat(HasSimulationId, frame.HasSimulation ? 1f : 0f);
        MaterialProperties.SetTexture(
            OpticalTextureId,
            frame.OpticalTexture ?? Texture2D.blackTexture);
        MaterialProperties.SetTexture(
            ThermalTextureId,
            frame.ThermalTexture ?? Texture2D.blackTexture);
        MaterialProperties.SetTexture(
            VelocityTextureId,
            frame.VelocityTexture ?? Texture2D.blackTexture);
        MaterialProperties.SetTexture(
            TerrainTextureId,
            frame.TerrainTexture ?? Texture2D.blackTexture);

        bool customNoise = HeatWaveNoiseField.IsAvailable;
        MaterialProperties.SetTexture(
            MacroNoiseId,
            customNoise ? HeatWaveNoiseField.MacroTexture : Texture2D.grayTexture);
        MaterialProperties.SetTexture(
            MicroNoiseId,
            customNoise ? HeatWaveNoiseField.MicroTexture : Texture2D.grayTexture);
        MaterialProperties.SetFloat(HasCustomNoiseId, customNoise ? 1f : 0f);
        MaterialProperties.SetInt(DebugModeId, debugMode);
        renderer.SetPropertyBlock(MaterialProperties);
    }
}
