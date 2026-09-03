using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

internal readonly struct FoehnRenderFrame
{
    internal readonly Vector2 RoomSizePx;
    internal readonly float Intensity;
    internal readonly float Time;
    internal readonly Vector2 WindDirection;
    internal readonly float GustSeed;
    internal readonly Texture2D TerrainField;

    internal FoehnRenderFrame(
        Vector2 roomSizePx,
        float intensity,
        float time,
        Vector2 windDirection,
        float gustSeed,
        Texture2D terrainField)
    {
        RoomSizePx = new Vector2(Mathf.Max(1f, roomSizePx.x), Mathf.Max(1f, roomSizePx.y));
        Intensity = Mathf.Clamp01(intensity);
        Time = time;
        WindDirection = windDirection.sqrMagnitude > 0.0001f
            ? windDirection.normalized
            : new Vector2(1f, -0.16f).normalized;
        GustSeed = Mathf.Repeat(gustSeed, 1f);
        TerrainField = terrainField;
    }
}

/// <summary>
/// Foehn background resolve. The atmosphere pass sits at the front of Midground so its
/// GrabPass only sees already-rendered background/midground scenery. Players, items,
/// foreground props and the point-grain dust are rendered afterwards and therefore are
/// never turned into a full-screen jelly/refraction field. The shader paints moving
/// hot-air/dust sheets across the scenery with soft edges and modest internal refraction.
/// </summary>
internal static class FoehnRenderPipeline
{
    internal const int AtmosphereSprite = 0;
    internal const int ParticleSpriteOffset = 1;
    internal const int SpriteCount = 1 + FoehnParticleField.ParticleCount;

    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int ScreenSizeId = Shader.PropertyToID("_screenSize");
    private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleFoehnRoomSizePx");
    private static readonly int IntensityId = Shader.PropertyToID("_DryCycleFoehnIntensity");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleFoehnTime");
    private static readonly int WindDirectionId = Shader.PropertyToID("_DryCycleFoehnWindDir");
    private static readonly int GustSeedId = Shader.PropertyToID("_DryCycleFoehnGustSeed");
    private static readonly int FlowFieldId = Shader.PropertyToID("_DryCycleFoehnFlowField");
    private static readonly int StreakFieldId = Shader.PropertyToID("_DryCycleFoehnStreakField");
    private static readonly int DustFieldId = Shader.PropertyToID("_DryCycleFoehnDustField");
    private static readonly int TerrainFieldId = Shader.PropertyToID("_DryCycleFoehnTerrainField");
    private static readonly int HasWindTexturesId = Shader.PropertyToID("_DryCycleHasFoehnTextures");
    private static readonly int HasDustFieldId = Shader.PropertyToID("_DryCycleHasFoehnDustField");
    private static readonly int HasTerrainFieldId = Shader.PropertyToID("_DryCycleHasFoehnTerrainField");
    private static readonly int DebugModeId = Shader.PropertyToID("_DryCycleFoehnDebugMode");

    internal static FSprite[] CreateSprites(RoomCamera camera)
    {
        FSprite[] sprites = new FSprite[SpriteCount];
        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;

        sprites[AtmosphereSprite] = new FSprite("Futile_White")
        {
            anchorX = 0f,
            anchorY = 0f,
            scaleX = screenWidth / 16f,
            scaleY = screenHeight / 16f,
            alpha = 1f,
            isVisible = false,
            shader = DryCycleShaderAssets.HasFoehnAtmosphere
                ? DryCycleShaderAssets.FoehnAtmosphere
                : camera.game.rainWorld.Shaders["Basic"]
        };

        for (int i = 0; i < FoehnParticleField.ParticleCount; i++)
        {
            sprites[ParticleSpriteOffset + i] = new FSprite("pixel")
            {
                anchorX = 0.5f,
                anchorY = 0.5f,
                alpha = 0f,
                isVisible = false,
                shader = camera.game.rainWorld.Shaders["Basic"]
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

        FContainer foreground = camera.ReturnFContainer("Foreground");
        for (int i = 0; i < FoehnParticleField.ParticleCount; i++)
        {
            FSprite particle = sprites[ParticleSpriteOffset + i];
            if (particle == null)
            {
                continue;
            }

            particle.RemoveFromContainer();
            foreground.AddChild(particle);
        }

        FSprite atmosphere = sprites[AtmosphereSprite];
        if (atmosphere != null)
        {
            atmosphere.RemoveFromContainer();
            FContainer midground = camera.ReturnFContainer("Midground");
            midground.AddChild(atmosphere);
            atmosphere.MoveToFront();
        }
    }

    internal static void DrawAtmosphere(
        FSprite[] sprites,
        RoomCamera camera,
        in FoehnRenderFrame frame,
        int debugMode)
    {
        if (sprites == null || camera == null || sprites.Length <= AtmosphereSprite)
        {
            return;
        }

        FSprite sprite = sprites[AtmosphereSprite];
        bool debugVisible = debugMode > 0;
        if (sprite == null ||
            (frame.Intensity <= 0.0001f && !debugVisible) ||
            !DryCycleShaderAssets.HasFoehnAtmosphere)
        {
            if (sprite != null)
            {
                sprite.isVisible = false;
            }
            return;
        }

        FoehnWindField.Ensure();
        FoehnDustField.Ensure();

        float screenWidth = camera.game.rainWorld.options.ScreenSize.x;
        float screenHeight = camera.game.rainWorld.options.ScreenSize.y;
        sprite.shader = DryCycleShaderAssets.FoehnAtmosphere;
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = screenWidth / 16f;
        sprite.scaleY = screenHeight / 16f;
        sprite.alpha = 1f;
        sprite.color = Color.white;
        sprite.isVisible = true;
        sprite.MoveToFront();

        ApplyProperties(sprite, frame, screenWidth, screenHeight, debugMode);
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
        in FoehnRenderFrame frame,
        float screenWidth,
        float screenHeight,
        int debugMode)
    {
        bool hasTextures = FoehnWindField.IsAvailable;
        bool hasDust = FoehnDustField.IsAvailable;
        bool hasTerrain = frame.TerrainField != null;
        Texture flow = hasTextures ? FoehnWindField.FlowTexture : Texture2D.grayTexture;
        Texture streak = hasTextures ? FoehnWindField.StreakTexture : Texture2D.grayTexture;
        Texture dust = hasDust ? FoehnDustField.DustTexture : Texture2D.grayTexture;
        Texture terrain = hasTerrain ? frame.TerrainField : Texture2D.whiteTexture;

        Vector4 roomSize = new(frame.RoomSizePx.x, frame.RoomSizePx.y, 0f, 0f);
        Vector4 windDirection = new(frame.WindDirection.x, frame.WindDirection.y, 0f, 0f);
        Vector4 screenSize = new(Mathf.Max(1f, screenWidth), Mathf.Max(1f, screenHeight), 0f, 0f);

        Shader.SetGlobalVector(RoomSizeId, roomSize);
        Shader.SetGlobalFloat(IntensityId, frame.Intensity);
        Shader.SetGlobalFloat(TimeId, frame.Time);
        Shader.SetGlobalVector(WindDirectionId, windDirection);
        Shader.SetGlobalFloat(GustSeedId, frame.GustSeed);
        Shader.SetGlobalTexture(FlowFieldId, flow);
        Shader.SetGlobalTexture(StreakFieldId, streak);
        Shader.SetGlobalTexture(DustFieldId, dust);
        Shader.SetGlobalTexture(TerrainFieldId, terrain);
        Shader.SetGlobalFloat(HasWindTexturesId, hasTextures ? 1f : 0f);
        Shader.SetGlobalFloat(HasDustFieldId, hasDust ? 1f : 0f);
        Shader.SetGlobalFloat(HasTerrainFieldId, hasTerrain ? 1f : 0f);
        Shader.SetGlobalFloat(DebugModeId, Mathf.Clamp(debugMode, 0, 3));

        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(ScreenSizeId, screenSize);
        MaterialProperties.SetVector(RoomSizeId, roomSize);
        MaterialProperties.SetFloat(IntensityId, frame.Intensity);
        MaterialProperties.SetFloat(TimeId, frame.Time);
        MaterialProperties.SetVector(WindDirectionId, windDirection);
        MaterialProperties.SetFloat(GustSeedId, frame.GustSeed);
        MaterialProperties.SetTexture(FlowFieldId, flow);
        MaterialProperties.SetTexture(StreakFieldId, streak);
        MaterialProperties.SetTexture(DustFieldId, dust);
        MaterialProperties.SetTexture(TerrainFieldId, terrain);
        MaterialProperties.SetFloat(HasWindTexturesId, hasTextures ? 1f : 0f);
        MaterialProperties.SetFloat(HasDustFieldId, hasDust ? 1f : 0f);
        MaterialProperties.SetFloat(HasTerrainFieldId, hasTerrain ? 1f : 0f);
        MaterialProperties.SetFloat(DebugModeId, Mathf.Clamp(debugMode, 0, 3));
        renderer.SetPropertyBlock(MaterialProperties);
    }
}
