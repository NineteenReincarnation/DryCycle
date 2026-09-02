using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveRenderFrame
{
    internal readonly Vector2 RoomSize;
    internal readonly float Intensity;
    internal readonly float SolarIntensity;
    internal readonly float ToneAmount;
    internal readonly float LevelHeatAmount;
    internal readonly float Time;
    internal readonly bool Active;

    internal HeatWaveRenderFrame(
        Vector2 roomSize,
        float intensity,
        float solarIntensity,
        float toneAmount,
        float levelHeatAmount,
        float time,
        bool active)
    {
        RoomSize = roomSize;
        Intensity = Mathf.Clamp01(intensity);
        SolarIntensity = Mathf.Clamp01(solarIntensity);
        ToneAmount = Mathf.Clamp01(toneAmount);
        LevelHeatAmount = Mathf.Clamp01(levelHeatAmount);
        Time = time;
        Active = active;
    }
}

/// <summary>
/// Secondary HeatWave atmosphere pass.
///
/// Rain World's LevelHeat shader owns the primary terrain melt. This pass adds the
/// things LevelHeat deliberately does not own: whole-air meso shimmer, fine edge jitter,
/// distant optical softening and the bleached high-temperature color state. It captures
/// SceneColor once and resolves once; there is no multi-layer recursive distortion and
/// no dependency on thermal/plume compute textures.
/// </summary>
internal static class HeatWaveRenderPipeline
{
    internal const int AtmosphereLayer = 0;
    internal const int LayerCount = 1;

    private const float Epsilon = 0.0001f;
    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleHeatWaveIntensity");
    private static readonly int SolarIntensityId = Shader.PropertyToID("_DryCycleHeatSolarIntensity");
    private static readonly int ToneAmountId = Shader.PropertyToID("_DryCycleHeatToneAmount");
    private static readonly int LevelHeatAmountId = Shader.PropertyToID("_DryCycleHeatLevelAmount");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleHeatTime");
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
            shader = DryCycleShaderAssets.HasHeatWaveAtmosphere
                ? DryCycleShaderAssets.HeatWaveAtmosphere
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

        if (!frame.Active)
        {
            sprite.isVisible = false;
            return;
        }

        if (!DryCycleShaderAssets.HasHeatWaveAtmosphere)
        {
            DrawFallback(sprite, camera, frame);
            return;
        }

        HeatWaveNoiseField.Ensure();

        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = DryCycleShaderAssets.HeatWaveAtmosphere;
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.alpha = 1f;
        sprite.color = Color.white;
        sprite.isVisible = true;
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
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = camera.game.rainWorld.Shaders["Basic"];
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.color = new Color(1f, 0.975f, 0.90f);

        // LevelHeat still provides the core weather even when the custom bundle is
        // absent. This fallback only adds a faint warm-white exposure cue so missing
        // atmosphere assets never collapse the weather back to a visually normal room.
        sprite.alpha = Mathf.Clamp01(frame.ToneAmount * 0.075f);
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

        ApplyGlobalProperties(
            roomSize,
            frame,
            debugMode,
            customNoise,
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
        MaterialProperties.SetFloat(SolarIntensityId, frame.SolarIntensity);
        MaterialProperties.SetFloat(ToneAmountId, frame.ToneAmount);
        MaterialProperties.SetFloat(LevelHeatAmountId, frame.LevelHeatAmount);
        MaterialProperties.SetFloat(TimeId, frame.Time);
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
        Texture macroNoise,
        Texture microNoise)
    {
        // Futile can rebuild render layers. Mirror DryCycle-unique uniforms globally as
        // a resilience path; the per-renderer property block remains authoritative.
        Shader.SetGlobalVector(RoomSizeId, roomSize);
        Shader.SetGlobalFloat(IntensityId, frame.Intensity);
        Shader.SetGlobalFloat(SolarIntensityId, frame.SolarIntensity);
        Shader.SetGlobalFloat(ToneAmountId, frame.ToneAmount);
        Shader.SetGlobalFloat(LevelHeatAmountId, frame.LevelHeatAmount);
        Shader.SetGlobalFloat(TimeId, frame.Time);
        Shader.SetGlobalTexture(MacroNoiseId, macroNoise);
        Shader.SetGlobalTexture(MicroNoiseId, microNoise);
        Shader.SetGlobalFloat(HasCustomNoiseId, customNoise ? 1f : 0f);
        Shader.SetGlobalInt(DebugModeId, debugMode);
    }
}
