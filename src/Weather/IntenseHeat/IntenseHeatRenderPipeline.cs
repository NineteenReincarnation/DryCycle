using DryCycle.Rendering;
using DryCycle.Weather.HeatWave;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

internal readonly struct IntenseHeatRenderFrame
{
    internal readonly Vector2 RoomSizePx;
    internal readonly float Intensity;
    internal readonly float SolarIntensity;
    internal readonly float Time;
    internal readonly Texture2D SolarField;
    internal readonly Texture2D SurfaceField;
    internal readonly bool Active;

    internal IntenseHeatRenderFrame(
        Vector2 roomSizePx,
        float intensity,
        float solarIntensity,
        float time,
        Texture2D solarField,
        Texture2D surfaceField,
        bool active)
    {
        RoomSizePx = new Vector2(
            Mathf.Max(1f, roomSizePx.x),
            Mathf.Max(1f, roomSizePx.y));
        Intensity = Mathf.Clamp01(intensity);
        SolarIntensity = Mathf.Clamp01(solarIntensity);
        Time = time;
        SolarField = solarField;
        SurfaceField = surfaceField;
        Active = active;
    }
}

/// <summary>
/// Disaster-grade direct-sun atmosphere resolve.
///
/// IntenseHeat reuses HeatWave's deterministic optical texture vocabulary, but owns a
/// separate shader and solar-occlusion field. It is intentionally more color-destructive,
/// more ground-mirage-heavy and more optically unstable than normal HeatWave.
/// </summary>
internal static class IntenseHeatRenderPipeline
{
    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int ScreenSizeId = Shader.PropertyToID("_screenSize");
    private static readonly int RoomSizePxId = Shader.PropertyToID("_DryCycleIntenseRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleIntenseHeatIntensity");
    private static readonly int SolarIntensityId = Shader.PropertyToID("_DryCycleIntenseSolarIntensity");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleIntenseHeatTime");
    private static readonly int FlowFieldId = Shader.PropertyToID("_DryCycleIntenseFlowField");
    private static readonly int NormalFieldId = Shader.PropertyToID("_DryCycleIntenseNormalField");
    private static readonly int MirageFieldId = Shader.PropertyToID("_DryCycleIntenseMirageField");
    private static readonly int SurfaceFieldId = Shader.PropertyToID("_DryCycleIntenseSurfaceField");
    private static readonly int SolarFieldId = Shader.PropertyToID("_DryCycleIntenseSolarField");
    private static readonly int HasOpticalTexturesId = Shader.PropertyToID("_DryCycleIntenseHasOpticalTextures");
    private static readonly int HasSurfaceFieldId = Shader.PropertyToID("_DryCycleIntenseHasSurfaceField");
    private static readonly int HasSolarFieldId = Shader.PropertyToID("_DryCycleIntenseHasSolarField");
    private static readonly int DebugModeId = Shader.PropertyToID("_DryCycleIntenseDebugMode");

    internal static FSprite[] CreateSprites(RoomCamera camera)
    {
        float width = camera.game.rainWorld.options.ScreenSize.x;
        float height = camera.game.rainWorld.options.ScreenSize.y;

        FSprite atmosphere = new("Futile_White")
        {
            anchorX = 0f,
            anchorY = 0f,
            scaleX = width / 16f,
            scaleY = height / 16f,
            alpha = 1f,
            isVisible = false,
            shader = DryCycleShaderAssets.HasIntenseHeatAtmosphere
                ? DryCycleShaderAssets.IntenseHeatAtmosphere
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

        FSprite atmosphere = sprites[0];
        atmosphere.RemoveFromContainer();
        camera.ReturnFContainer("GrabShaders").AddChild(atmosphere);
        atmosphere.MoveToFront();
    }

    internal static void Draw(
        FSprite[] sprites,
        RoomCamera camera,
        in IntenseHeatRenderFrame frame,
        int debugMode)
    {
        if (sprites == null || sprites.Length == 0 || camera == null)
        {
            return;
        }

        FSprite sprite = sprites[0];
        if (sprite == null)
        {
            return;
        }

        if (!frame.Active || !DryCycleShaderAssets.HasIntenseHeatAtmosphere)
        {
            sprite.isVisible = false;
            return;
        }

        HeatWaveNoiseField.Ensure();

        float width = camera.game.rainWorld.options.ScreenSize.x;
        float height = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = DryCycleShaderAssets.IntenseHeatAtmosphere;
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = width / 16f;
        sprite.scaleY = height / 16f;
        sprite.alpha = 1f;
        sprite.color = Color.white;
        sprite.isVisible = true;
        sprite.MoveToFront();

        ApplyProperties(sprite, frame, debugMode, width, height);
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
        in IntenseHeatRenderFrame frame,
        int debugMode,
        float screenWidth,
        float screenHeight)
    {
        bool hasOptics = HeatWaveNoiseField.IsAvailable;
        bool hasSurface = frame.SurfaceField != null;
        bool hasSolar = frame.SolarField != null;

        Texture flow = hasOptics ? HeatWaveNoiseField.FlowTexture : Texture2D.grayTexture;
        Texture normal = hasOptics ? HeatWaveNoiseField.NormalTexture : Texture2D.grayTexture;
        Texture mirage = hasOptics ? HeatWaveNoiseField.MirageTexture : Texture2D.grayTexture;
        Texture surface = hasSurface ? frame.SurfaceField : Texture2D.blackTexture;
        Texture solar = hasSolar ? frame.SolarField : Texture2D.whiteTexture;

        float heatDrive = Mathf.Pow(Mathf.Clamp01(frame.Intensity), 0.58f);
        float distortionScale = Mathf.Lerp(1.24f, 1.56f, heatDrive);
        float shaderTime = frame.Time * Mathf.Lerp(2.35f, 3.45f, heatDrive);

        Vector4 effectiveScreen = new(
            Mathf.Max(1f, screenWidth / distortionScale),
            Mathf.Max(1f, screenHeight / distortionScale),
            0f,
            0f);
        Vector4 roomSize = new(frame.RoomSizePx.x, frame.RoomSizePx.y, 0f, 0f);

        Shader.SetGlobalVector(RoomSizePxId, roomSize);
        Shader.SetGlobalFloat(IntensityId, frame.Intensity);
        Shader.SetGlobalFloat(SolarIntensityId, frame.SolarIntensity);
        Shader.SetGlobalFloat(TimeId, shaderTime);
        Shader.SetGlobalTexture(FlowFieldId, flow);
        Shader.SetGlobalTexture(NormalFieldId, normal);
        Shader.SetGlobalTexture(MirageFieldId, mirage);
        Shader.SetGlobalTexture(SurfaceFieldId, surface);
        Shader.SetGlobalTexture(SolarFieldId, solar);
        Shader.SetGlobalFloat(HasOpticalTexturesId, hasOptics ? 1f : 0f);
        Shader.SetGlobalFloat(HasSurfaceFieldId, hasSurface ? 1f : 0f);
        Shader.SetGlobalFloat(HasSolarFieldId, hasSolar ? 1f : 0f);
        Shader.SetGlobalInt(DebugModeId, debugMode);

        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(ScreenSizeId, effectiveScreen);
        MaterialProperties.SetVector(RoomSizePxId, roomSize);
        MaterialProperties.SetFloat(IntensityId, frame.Intensity);
        MaterialProperties.SetFloat(SolarIntensityId, frame.SolarIntensity);
        MaterialProperties.SetFloat(TimeId, shaderTime);
        MaterialProperties.SetTexture(FlowFieldId, flow);
        MaterialProperties.SetTexture(NormalFieldId, normal);
        MaterialProperties.SetTexture(MirageFieldId, mirage);
        MaterialProperties.SetTexture(SurfaceFieldId, surface);
        MaterialProperties.SetTexture(SolarFieldId, solar);
        MaterialProperties.SetFloat(HasOpticalTexturesId, hasOptics ? 1f : 0f);
        MaterialProperties.SetFloat(HasSurfaceFieldId, hasSurface ? 1f : 0f);
        MaterialProperties.SetFloat(HasSolarFieldId, hasSolar ? 1f : 0f);
        MaterialProperties.SetInt(DebugModeId, debugMode);
        renderer.SetPropertyBlock(MaterialProperties);
    }
}
