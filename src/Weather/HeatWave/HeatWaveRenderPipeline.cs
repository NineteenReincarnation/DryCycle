using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveRenderFrame
{
    internal readonly Vector2 RoomSize;
    internal readonly float Intensity;
    internal readonly float WhiteHeat;
    internal readonly float SolarIntensity;
    internal readonly float Burst;
    internal readonly float BurstKick;
    internal readonly float Stillness;
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
        float burst,
        float burstKick,
        float stillness,
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
        Burst = Mathf.Clamp01(burst);
        BurstKick = Mathf.Clamp01(burstKick);
        Stillness = Mathf.Clamp01(stillness);
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
/// Three-stage optical compositor built around Rain World's ordered SpriteLayers.
///
/// Far pass sits at the front of Midground, Mid pass at the front of Items and Near
/// pass at the front of GrabShaders. A background pixel therefore traverses all three
/// refractive slices, an item traverses Mid+Near, and foreground/gameplay content only
/// traverses Near. This deliberately accumulates optical path length instead of trying
/// to infer depth from one final full-screen image. HUD/HUD2 are later containers and
/// remain untouched.
/// </summary>
internal static class HeatWaveRenderPipeline
{
    internal const int FarLayer = 0;
    internal const int MidLayer = 1;
    internal const int NearLayer = 2;
    internal const int LayerCount = 3;

    private const float Epsilon = 0.0001f;

    private readonly struct LayerProfile
    {
        internal readonly string Container;
        internal readonly float OpticalScale;
        internal readonly float MacroScale;
        internal readonly float MicroScale;
        internal readonly float StreakScale;
        internal readonly float ToneWeight;

        internal LayerProfile(
            string container,
            float opticalScale,
            float macroScale,
            float microScale,
            float streakScale,
            float toneWeight)
        {
            Container = container;
            OpticalScale = opticalScale;
            MacroScale = macroScale;
            MicroScale = microScale;
            StreakScale = streakScale;
            ToneWeight = toneWeight;
        }
    }

    private static readonly LayerProfile[] Profiles =
    {
        // Far scenery receives the strongest broad wander and the least micro jitter.
        new("Midground", 0.55f, 1.24f, 0.32f, 0.80f, 0f),
        // Mid-distance objects keep coherent deformation but more readable shimmer.
        new("Items", 0.33f, 0.70f, 0.64f, 0.54f, 0f),
        // Gameplay foreground remains readable. White Heat is applied exactly once here.
        new("GrabShaders", 0.16f, 0.20f, 1.00f, 0.24f, 1f)
    };

    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleHeatWaveIntensity");
    private static readonly int WhiteHeatId = Shader.PropertyToID("_DryCycleWhiteHeat");
    private static readonly int SolarIntensityId = Shader.PropertyToID("_DryCycleHeatSolarIntensity");
    private static readonly int BurstId = Shader.PropertyToID("_DryCycleHeatBurst");
    private static readonly int BurstKickId = Shader.PropertyToID("_DryCycleHeatBurstKick");
    private static readonly int StillnessId = Shader.PropertyToID("_DryCycleHeatStillness");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleHeatTime");
    private static readonly int HasSimulationId = Shader.PropertyToID("_DryCycleHasHeatSimulation");
    private static readonly int OpticalTextureId = Shader.PropertyToID("_DryCycleHeatOpticalTex");
    private static readonly int ThermalTextureId = Shader.PropertyToID("_DryCycleHeatThermalTex");
    private static readonly int VelocityTextureId = Shader.PropertyToID("_DryCycleHeatVelocityTex");
    private static readonly int TerrainTextureId = Shader.PropertyToID("_DryCycleHeatTerrainTex");
    private static readonly int MacroNoiseId = Shader.PropertyToID("_DryCycleHeatMacroNoise");
    private static readonly int MicroNoiseId = Shader.PropertyToID("_DryCycleHeatMicroNoise");
    private static readonly int HasCustomNoiseId = Shader.PropertyToID("_DryCycleHasHeatCustomNoise");
    private static readonly int LayerOpticalScaleId = Shader.PropertyToID("_DryCycleHeatLayerOpticalScale");
    private static readonly int LayerMacroScaleId = Shader.PropertyToID("_DryCycleHeatLayerMacroScale");
    private static readonly int LayerMicroScaleId = Shader.PropertyToID("_DryCycleHeatLayerMicroScale");
    private static readonly int LayerStreakScaleId = Shader.PropertyToID("_DryCycleHeatLayerStreakScale");
    private static readonly int LayerToneWeightId = Shader.PropertyToID("_DryCycleHeatLayerToneWeight");
    private static readonly int DebugModeId = Shader.PropertyToID("_DryCycleHeatDebugMode");

    internal static FSprite[] CreateSprites(RoomCamera camera)
    {
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        FSprite[] sprites = new FSprite[LayerCount];

        for (int i = 0; i < sprites.Length; i++)
        {
            sprites[i] = new FSprite("Futile_White")
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
        }

        return sprites;
    }

    internal static void AddToContainers(FSprite[] sprites, RoomCamera camera)
    {
        if (sprites == null || camera == null)
        {
            return;
        }

        int count = Mathf.Min(sprites.Length, LayerCount);
        for (int i = 0; i < count; i++)
        {
            FSprite sprite = sprites[i];
            if (sprite == null)
            {
                continue;
            }

            sprite.RemoveFromContainer();
            camera.ReturnFContainer(Profiles[i].Container).AddChild(sprite);
            sprite.MoveToFront();
        }
    }

    internal static void Draw(
        FSprite[] sprites,
        RoomCamera camera,
        in HeatWaveRenderFrame frame,
        int debugMode)
    {
        if (sprites == null || camera == null)
        {
            return;
        }

        bool custom = DryCycleShaderAssets.HasHeatWaveComposite;
        if (!custom)
        {
            DrawFallback(sprites, camera, frame);
            return;
        }

        HeatWaveNoiseField.Ensure();
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        int count = Mathf.Min(sprites.Length, LayerCount);

        for (int i = 0; i < count; i++)
        {
            FSprite sprite = sprites[i];
            if (sprite == null)
            {
                continue;
            }

            sprite.x = 0f;
            sprite.y = 0f;
            sprite.scaleX = screenWidth / 16f;
            sprite.scaleY = screenHeight / 16f;
            sprite.alpha = 1f;

            // Debug textures are shown once in the final stage. Running the debug
            // branch through all three captures would recursively distort the visualizer.
            sprite.isVisible = frame.Active && (debugMode <= 0 || i == NearLayer);
            if (!sprite.isVisible)
            {
                continue;
            }

            sprite.MoveToFront();
            ApplyProperties(sprite, frame, Profiles[i], debugMode);
        }
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
        FSprite[] sprites,
        RoomCamera camera,
        in HeatWaveRenderFrame frame)
    {
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null)
            {
                sprites[i].isVisible = false;
            }
        }

        if (!frame.Active || sprites.Length <= NearLayer || sprites[NearLayer] == null)
        {
            return;
        }

        FSprite sprite = sprites[NearLayer];
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = camera.game.rainWorld.Shaders["Basic"];
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.color = new Color(1f, 0.965f, 0.83f);
        sprite.alpha = Mathf.Clamp01(frame.WhiteHeat * 0.105f + frame.Burst * 0.025f);
        sprite.isVisible = sprite.alpha > Epsilon;
        if (sprite.isVisible)
        {
            sprite.MoveToFront();
        }
    }

    private static void ApplyProperties(
        FSprite sprite,
        in HeatWaveRenderFrame frame,
        in LayerProfile profile,
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
        MaterialProperties.SetFloat(BurstId, frame.Burst);
        MaterialProperties.SetFloat(BurstKickId, frame.BurstKick);
        MaterialProperties.SetFloat(StillnessId, frame.Stillness);
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

        MaterialProperties.SetFloat(LayerOpticalScaleId, profile.OpticalScale);
        MaterialProperties.SetFloat(LayerMacroScaleId, profile.MacroScale);
        MaterialProperties.SetFloat(LayerMicroScaleId, profile.MicroScale);
        MaterialProperties.SetFloat(LayerStreakScaleId, profile.StreakScale);
        MaterialProperties.SetFloat(LayerToneWeightId, profile.ToneWeight);
        MaterialProperties.SetInt(DebugModeId, debugMode);
        renderer.SetPropertyBlock(MaterialProperties);
    }
}
