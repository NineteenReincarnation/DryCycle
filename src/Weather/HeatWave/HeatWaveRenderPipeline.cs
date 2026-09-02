using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveRenderFrame
{
    internal readonly Vector2 RoomSizePx;
    internal readonly float Intensity;
    internal readonly float SolarIntensity;
    internal readonly float ToneAmount;
    internal readonly float LevelHeatAmount;
    internal readonly float Time;
    internal readonly Texture2D SurfaceField;
    internal readonly bool Active;

    internal HeatWaveRenderFrame(
        Vector2 roomSizePx,
        float intensity,
        float solarIntensity,
        float toneAmount,
        float levelHeatAmount,
        float time,
        Texture2D surfaceField,
        bool active)
    {
        RoomSizePx = new Vector2(
            Mathf.Max(1f, roomSizePx.x),
            Mathf.Max(1f, roomSizePx.y));
        Intensity = Mathf.Clamp01(intensity);
        SolarIntensity = Mathf.Clamp01(solarIntensity);
        ToneAmount = Mathf.Clamp01(toneAmount);
        LevelHeatAmount = Mathf.Clamp01(levelHeatAmount);
        Time = time;
        SurfaceField = surfaceField;
        Active = active;
    }
}

/// <summary>
/// HeatWave atmosphere resolve.
///
/// Rain World's LevelHeat owns terrain-level melting. This single GrabPass resolve owns
/// the air itself: room-space flow advection, base/detail refractive normals, mirage
/// vertical remapping, geometry-aware ground shimmer, optical focusing, directional
/// softening and dry-hot color grading. Runtime optical textures remain anchored to room
/// space; a small per-room surface field only guides where ground mirage should gather.
/// </summary>
internal static class HeatWaveRenderPipeline
{
    internal const int AtmosphereLayer = 0;
    internal const int LayerCount = 1;

    // The shader is authored in pixel-like displacement units. HeatWave deliberately
    // resolves those units slightly larger than physical pixels at high intensity so a
    // broad open-air region visibly trembles instead of only producing sub-pixel shimmer.
    private const float MinimumDistortionScale = 1.08f;
    private const float MaximumDistortionScale = 1.28f;

    // Thermal sheets already move in world space. Their original rates were intentionally
    // conservative and could leave a warm band near the same height for too long. This
    // accelerates only the HeatWave shader clock; gameplay time and audio timing stay put.
    private const float MinimumShaderTimeScale = 1.55f;
    private const float MaximumShaderTimeScale = 2.25f;

    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int ScreenSizeId = Shader.PropertyToID("_screenSize");
    private static readonly int RoomSizePxId = Shader.PropertyToID("_DryCycleHeatRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleHeatWaveIntensity");
    private static readonly int SolarIntensityId = Shader.PropertyToID("_DryCycleHeatSolarIntensity");
    private static readonly int ToneAmountId = Shader.PropertyToID("_DryCycleHeatToneAmount");
    private static readonly int LevelHeatAmountId = Shader.PropertyToID("_DryCycleHeatLevelAmount");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleHeatTime");
    private static readonly int FlowFieldId = Shader.PropertyToID("_DryCycleHeatFlowField");
    private static readonly int NormalFieldId = Shader.PropertyToID("_DryCycleHeatNormalField");
    private static readonly int MirageFieldId = Shader.PropertyToID("_DryCycleHeatMirageField");
    private static readonly int SurfaceFieldId = Shader.PropertyToID("_DryCycleHeatSurfaceField");
    private static readonly int HasHeatTexturesId = Shader.PropertyToID("_DryCycleHasHeatTextures");
    private static readonly int HasSurfaceFieldId = Shader.PropertyToID("_DryCycleHasHeatSurfaceField");
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

        if (!frame.Active || !DryCycleShaderAssets.HasHeatWaveAtmosphere)
        {
            // Never fake missing assets with a translucent white fullscreen sprite.
            // Vanilla LevelHeat remains the safe visual fallback.
            sprite.isVisible = false;
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

        ApplyProperties(
            sprite,
            frame,
            debugMode,
            screenWidth,
            screenHeight);
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

    private static void ApplyProperties(
        FSprite sprite,
        in HeatWaveRenderFrame frame,
        int debugMode,
        float screenWidth,
        float screenHeight)
    {
        bool hasTextures = HeatWaveNoiseField.IsAvailable;
        bool hasSurfaceField = frame.SurfaceField != null;
        Texture flowTexture = hasTextures
            ? HeatWaveNoiseField.FlowTexture
            : Texture2D.grayTexture;
        Texture normalTexture = hasTextures
            ? HeatWaveNoiseField.NormalTexture
            : Texture2D.grayTexture;
        Texture mirageTexture = hasTextures
            ? HeatWaveNoiseField.MirageTexture
            : Texture2D.grayTexture;
        Texture surfaceTexture = hasSurfaceField
            ? frame.SurfaceField
            : Texture2D.blackTexture;

        float heatDrive = Mathf.Pow(Mathf.Clamp01(frame.Intensity), 0.72f);
        float distortionScale = Mathf.Lerp(
            MinimumDistortionScale,
            MaximumDistortionScale,
            heatDrive);
        float shaderTimeScale = Mathf.Lerp(
            MinimumShaderTimeScale,
            MaximumShaderTimeScale,
            heatDrive);

        // _screenSize is a shared Rain World uniform, so the HeatWave-specific scale must
        // stay on this renderer's property block. Do not mirror this value globally.
        Vector4 effectiveScreenSize = new(
            Mathf.Max(1f, screenWidth / distortionScale),
            Mathf.Max(1f, screenHeight / distortionScale),
            0f,
            0f);
        Vector4 roomSize = new(
            frame.RoomSizePx.x,
            frame.RoomSizePx.y,
            0f,
            0f);
        float shaderTime = frame.Time * shaderTimeScale;

        ApplyGlobalProperties(
            roomSize,
            frame,
            shaderTime,
            debugMode,
            hasTextures,
            hasSurfaceField,
            flowTexture,
            normalTexture,
            mirageTexture,
            surfaceTexture);

        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(ScreenSizeId, effectiveScreenSize);
        MaterialProperties.SetVector(RoomSizePxId, roomSize);
        MaterialProperties.SetFloat(IntensityId, frame.Intensity);
        MaterialProperties.SetFloat(SolarIntensityId, frame.SolarIntensity);
        MaterialProperties.SetFloat(ToneAmountId, frame.ToneAmount);
        MaterialProperties.SetFloat(LevelHeatAmountId, frame.LevelHeatAmount);
        MaterialProperties.SetFloat(TimeId, shaderTime);
        MaterialProperties.SetTexture(FlowFieldId, flowTexture);
        MaterialProperties.SetTexture(NormalFieldId, normalTexture);
        MaterialProperties.SetTexture(MirageFieldId, mirageTexture);
        MaterialProperties.SetTexture(SurfaceFieldId, surfaceTexture);
        MaterialProperties.SetFloat(HasHeatTexturesId, hasTextures ? 1f : 0f);
        MaterialProperties.SetFloat(HasSurfaceFieldId, hasSurfaceField ? 1f : 0f);
        MaterialProperties.SetInt(DebugModeId, debugMode);
        renderer.SetPropertyBlock(MaterialProperties);
    }

    private static void ApplyGlobalProperties(
        Vector4 roomSize,
        in HeatWaveRenderFrame frame,
        float shaderTime,
        int debugMode,
        bool hasTextures,
        bool hasSurfaceField,
        Texture flowTexture,
        Texture normalTexture,
        Texture mirageTexture,
        Texture surfaceTexture)
    {
        // Futile can rebuild render layers. Mirror DryCycle-owned uniforms globally as a
        // resilience path; shared Rain World uniforms such as _screenSize stay untouched.
        Shader.SetGlobalVector(RoomSizePxId, roomSize);
        Shader.SetGlobalFloat(IntensityId, frame.Intensity);
        Shader.SetGlobalFloat(SolarIntensityId, frame.SolarIntensity);
        Shader.SetGlobalFloat(ToneAmountId, frame.ToneAmount);
        Shader.SetGlobalFloat(LevelHeatAmountId, frame.LevelHeatAmount);
        Shader.SetGlobalFloat(TimeId, shaderTime);
        Shader.SetGlobalTexture(FlowFieldId, flowTexture);
        Shader.SetGlobalTexture(NormalFieldId, normalTexture);
        Shader.SetGlobalTexture(MirageFieldId, mirageTexture);
        Shader.SetGlobalTexture(SurfaceFieldId, surfaceTexture);
        Shader.SetGlobalFloat(HasHeatTexturesId, hasTextures ? 1f : 0f);
        Shader.SetGlobalFloat(HasSurfaceFieldId, hasSurfaceField ? 1f : 0f);
        Shader.SetGlobalInt(DebugModeId, debugMode);
    }
}