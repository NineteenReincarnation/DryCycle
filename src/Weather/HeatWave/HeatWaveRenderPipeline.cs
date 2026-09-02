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
    internal readonly bool HasPlumes;
    internal readonly Texture OpticalTexture;
    internal readonly Texture ThermalTexture;
    internal readonly Texture VelocityTexture;
    internal readonly Texture TerrainTexture;
    internal readonly Texture PlumeTexture;

    internal HeatWaveRenderFrame(
        Vector2 roomSize,
        float intensity,
        float whiteHeat,
        float solarIntensity,
        float time,
        bool active,
        bool hasSimulation,
        bool hasPlumes,
        Texture opticalTexture,
        Texture thermalTexture,
        Texture velocityTexture,
        Texture terrainTexture,
        Texture plumeTexture)
    {
        RoomSize = roomSize;
        Intensity = Mathf.Clamp01(intensity);
        WhiteHeat = Mathf.Clamp01(whiteHeat);
        SolarIntensity = Mathf.Clamp01(solarIntensity);
        Time = time;
        Active = active;
        HasSimulation = hasSimulation;
        HasPlumes = hasPlumes;
        OpticalTexture = opticalTexture;
        ThermalTexture = thermalTexture;
        VelocityTexture = velocityTexture;
        TerrainTexture = terrainTexture;
        PlumeTexture = plumeTexture;
    }
}

/// <summary>
/// One late optical resolve for the whole HeatWave.
///
/// Strong deformation is never global. The shader receives separate masks for exposed
/// ground and persistent thermal plumes; distant atmosphere is allowed only a tiny
/// sub-pixel wander. SceneColor is captured/refracted exactly once.
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
    private static readonly int HasPlumesId = Shader.PropertyToID("_DryCycleHasHeatPlumes");
    private static readonly int OpticalTextureId = Shader.PropertyToID("_DryCycleHeatOpticalTex");
    private static readonly int ThermalTextureId = Shader.PropertyToID("_DryCycleHeatThermalTex");
    private static readonly int VelocityTextureId = Shader.PropertyToID("_DryCycleHeatVelocityTex");
    private static readonly int TerrainTextureId = Shader.PropertyToID("_DryCycleHeatTerrainTex");
    private static readonly int PlumeTextureId = Shader.PropertyToID("_DryCycleHeatPlumeTex");
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
        sprite.color = new Color(1f, 0.98f, 0.91f);

        // If the AssetBundle/composite is missing, make failure visible enough to
        // diagnose instead of silently looking identical to normal gameplay. This is
        // still restrained and is only a compatibility fallback, never the target VFX.
        sprite.alpha = Mathf.Clamp01(frame.WhiteHeat * 0.085f);
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
        bool customNoise = HeatWaveNoiseField.IsAvailable;
        Texture optical = frame.OpticalTexture ?? Texture2D.blackTexture;
        Texture thermal = frame.ThermalTexture ?? Texture2D.blackTexture;
        Texture velocity = frame.VelocityTexture ?? Texture2D.blackTexture;
        Texture terrain = frame.TerrainTexture ?? Texture2D.blackTexture;
        Texture plume = frame.PlumeTexture ?? Texture2D.blackTexture;
        Texture macroNoise = customNoise
            ? HeatWaveNoiseField.MacroTexture
            : Texture2D.grayTexture;
        Texture microNoise = customNoise
            ? HeatWaveNoiseField.MicroTexture
            : Texture2D.grayTexture;
        Vector4 roomSize = new(
            frame.RoomSize.x,
            frame.RoomSize.y,
            0f,
            0f);

        // Futile can rebuild render layers/batches when sprites move between containers
        // or shaders. MaterialPropertyBlock remains the authoritative per-renderer path,
        // but the same DryCycle-unique uniforms are mirrored globally so a transient
        // render-layer rebuild cannot turn the HeatWave shader into an all-zero/no-op
        // frame. The property block still wins whenever it is present.
        ApplyGlobalProperties(
            roomSize,
            frame,
            debugMode,
            customNoise,
            optical,
            thermal,
            velocity,
            terrain,
            plume,
            macroNoise,
            microNoise);

        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(RoomSizeId, roomSize);
        MaterialProperties.SetFloat(IntensityId, frame.Intensity);
        MaterialProperties.SetFloat(WhiteHeatId, frame.WhiteHeat);
        MaterialProperties.SetFloat(SolarIntensityId, frame.SolarIntensity);
        MaterialProperties.SetFloat(TimeId, frame.Time);
        MaterialProperties.SetFloat(HasSimulationId, frame.HasSimulation ? 1f : 0f);
        MaterialProperties.SetFloat(HasPlumesId, frame.HasPlumes ? 1f : 0f);
        MaterialProperties.SetTexture(OpticalTextureId, optical);
        MaterialProperties.SetTexture(ThermalTextureId, thermal);
        MaterialProperties.SetTexture(VelocityTextureId, velocity);
        MaterialProperties.SetTexture(TerrainTextureId, terrain);
        MaterialProperties.SetTexture(PlumeTextureId, plume);
        MaterialProperties.SetTexture(MacroNoiseId, macroNoise);
        MaterialProperties.SetTexture(MicroNoiseId, microNoise);
        MaterialProperties.SetFloat(HasCustomNoiseId, customNoise ? 1f : 0f);
        MaterialProperties.SetInt(DebugModeId, debugMode);
        renderer.SetPropertyBlock(MaterialProperties);
    }

    private static void ApplyGlobalProperties(
        Vector4 roomSize,
        in HeatWaveRenderFrame frame,
        int debugMode,
        bool customNoise,
        Texture optical,
        Texture thermal,
        Texture velocity,
        Texture terrain,
        Texture plume,
        Texture macroNoise,
        Texture microNoise)
    {
        Shader.SetGlobalVector(RoomSizeId, roomSize);
        Shader.SetGlobalFloat(IntensityId, frame.Intensity);
        Shader.SetGlobalFloat(WhiteHeatId, frame.WhiteHeat);
        Shader.SetGlobalFloat(SolarIntensityId, frame.SolarIntensity);
        Shader.SetGlobalFloat(TimeId, frame.Time);
        Shader.SetGlobalFloat(HasSimulationId, frame.HasSimulation ? 1f : 0f);
        Shader.SetGlobalFloat(HasPlumesId, frame.HasPlumes ? 1f : 0f);
        Shader.SetGlobalTexture(OpticalTextureId, optical);
        Shader.SetGlobalTexture(ThermalTextureId, thermal);
        Shader.SetGlobalTexture(VelocityTextureId, velocity);
        Shader.SetGlobalTexture(TerrainTextureId, terrain);
        Shader.SetGlobalTexture(PlumeTextureId, plume);
        Shader.SetGlobalTexture(MacroNoiseId, macroNoise);
        Shader.SetGlobalTexture(MicroNoiseId, microNoise);
        Shader.SetGlobalFloat(HasCustomNoiseId, customNoise ? 1f : 0f);
        Shader.SetGlobalInt(DebugModeId, debugMode);
    }
}
