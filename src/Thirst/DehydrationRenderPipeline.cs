using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Thirst;

internal readonly struct DehydrationRenderFrame
{
    internal readonly float Mild;
    internal readonly float Moderate;
    internal readonly float Severe;
    internal readonly float Collapse;
    internal readonly float Dying;
    internal readonly float Exertion;
    internal readonly float Blink;
    internal readonly float Pulse;
    internal readonly float DeathLock;
    internal readonly float Time;
    internal readonly bool Active;

    internal DehydrationRenderFrame(
        float mild,
        float moderate,
        float severe,
        float collapse,
        float dying,
        float exertion,
        float blink,
        float pulse,
        bool deathLock,
        float time,
        bool active)
    {
        Mild = Mathf.Clamp01(mild);
        Moderate = Mathf.Clamp01(moderate);
        Severe = Mathf.Clamp01(severe);
        Collapse = Mathf.Clamp01(collapse);
        Dying = Mathf.Clamp01(dying);
        Exertion = Mathf.Clamp01(exertion);
        Blink = Mathf.Clamp01(blink);
        Pulse = Mathf.Clamp01(pulse);
        DeathLock = deathLock ? 1f : 0f;
        Time = time;
        Active = active;
    }
}

/// <summary>
/// Owns the late full-scene dehydration pass. It is deliberately placed at the front of
/// GrabShaders so scheduled fog/heat/rain have already resolved, while Rain World's HUD
/// remains untouched and readable above the altered scene.
/// </summary>
internal static class DehydrationRenderPipeline
{
    private static readonly MaterialPropertyBlock MaterialProperties = new();

    private static readonly int ScreenSizeId = Shader.PropertyToID("_screenSize");
    private static readonly int MildId = Shader.PropertyToID("_DryCycleDehydrationMild");
    private static readonly int ModerateId = Shader.PropertyToID("_DryCycleDehydrationModerate");
    private static readonly int SevereId = Shader.PropertyToID("_DryCycleDehydrationSevere");
    private static readonly int CollapseId = Shader.PropertyToID("_DryCycleDehydrationCollapse");
    private static readonly int DyingId = Shader.PropertyToID("_DryCycleDehydrationDying");
    private static readonly int ExertionId = Shader.PropertyToID("_DryCycleDehydrationExertion");
    private static readonly int BlinkId = Shader.PropertyToID("_DryCycleDehydrationBlink");
    private static readonly int PulseId = Shader.PropertyToID("_DryCycleDehydrationPulse");
    private static readonly int DeathLockId = Shader.PropertyToID("_DryCycleDehydrationDeathLock");
    private static readonly int TimeId = Shader.PropertyToID("_DryCycleDehydrationTime");
    private static readonly int TearFilmId = Shader.PropertyToID("_DryCycleDehydrationTearFilm");
    private static readonly int RetinalNoiseId = Shader.PropertyToID("_DryCycleDehydrationRetinalNoise");

    internal static FSprite CreateSprite(RoomCamera camera)
    {
        float width = camera.game.rainWorld.options.ScreenSize.x;
        float height = camera.game.rainWorld.options.ScreenSize.y;
        return new FSprite("Futile_White")
        {
            anchorX = 0f,
            anchorY = 0f,
            x = 0f,
            y = 0f,
            scaleX = width / 16f,
            scaleY = height / 16f,
            alpha = 1f,
            color = Color.white,
            shader = DryCycleShaderAssets.HasDehydrationComposite
                ? DryCycleShaderAssets.DehydrationComposite
                : camera.game.rainWorld.Shaders["Basic"],
            isVisible = false
        };
    }

    internal static void AddToContainer(FSprite sprite, RoomCamera camera)
    {
        if (sprite == null || camera == null)
        {
            return;
        }
        sprite.RemoveFromContainer();
        camera.ReturnFContainer("GrabShaders").AddChild(sprite);
        sprite.MoveToFront();
    }

    internal static bool Draw(
        FSprite sprite,
        RoomCamera camera,
        in DehydrationRenderFrame frame)
    {
        if (sprite == null || camera?.game?.rainWorld == null)
        {
            return false;
        }

        if (!frame.Active || !DryCycleShaderAssets.HasDehydrationComposite)
        {
            sprite.isVisible = false;
            return false;
        }

        DehydrationVisualTextures.Ensure();
        if (!DehydrationVisualTextures.IsAvailable)
        {
            sprite.isVisible = false;
            return false;
        }

        float width = Mathf.Max(1f, camera.game.rainWorld.options.ScreenSize.x);
        float height = Mathf.Max(1f, camera.game.rainWorld.options.ScreenSize.y);
        sprite.x = 0f;
        sprite.y = 0f;
        sprite.scaleX = width / 16f;
        sprite.scaleY = height / 16f;
        sprite.alpha = 1f;
        sprite.color = Color.white;
        sprite.shader = DryCycleShaderAssets.DehydrationComposite;
        sprite.isVisible = true;
        sprite.MoveToFront();

        Vector4 screenSize = new(width, height, 0f, 0f);
        ApplyGlobalProperties(screenSize, frame);

        Renderer renderer = sprite?._renderLayer?._meshRenderer;
        if (renderer == null)
        {
            return true;
        }

        MaterialProperties.Clear();
        renderer.GetPropertyBlock(MaterialProperties);
        MaterialProperties.SetVector(ScreenSizeId, screenSize);
        MaterialProperties.SetFloat(MildId, frame.Mild);
        MaterialProperties.SetFloat(ModerateId, frame.Moderate);
        MaterialProperties.SetFloat(SevereId, frame.Severe);
        MaterialProperties.SetFloat(CollapseId, frame.Collapse);
        MaterialProperties.SetFloat(DyingId, frame.Dying);
        MaterialProperties.SetFloat(ExertionId, frame.Exertion);
        MaterialProperties.SetFloat(BlinkId, frame.Blink);
        MaterialProperties.SetFloat(PulseId, frame.Pulse);
        MaterialProperties.SetFloat(DeathLockId, frame.DeathLock);
        MaterialProperties.SetFloat(TimeId, frame.Time);
        MaterialProperties.SetTexture(TearFilmId, DehydrationVisualTextures.TearFilm);
        MaterialProperties.SetTexture(RetinalNoiseId, DehydrationVisualTextures.RetinalNoise);
        renderer.SetPropertyBlock(MaterialProperties);
        return true;
    }

    internal static void Hide(FSprite sprite)
    {
        if (sprite != null)
        {
            sprite.isVisible = false;
        }
    }

    private static void ApplyGlobalProperties(
        Vector4 screenSize,
        in DehydrationRenderFrame frame)
    {
        // Futile may rebuild render layers. Globals keep the pass deterministic during
        // that frame; the renderer property block remains authoritative per camera.
        Shader.SetGlobalVector(ScreenSizeId, screenSize);
        Shader.SetGlobalFloat(MildId, frame.Mild);
        Shader.SetGlobalFloat(ModerateId, frame.Moderate);
        Shader.SetGlobalFloat(SevereId, frame.Severe);
        Shader.SetGlobalFloat(CollapseId, frame.Collapse);
        Shader.SetGlobalFloat(DyingId, frame.Dying);
        Shader.SetGlobalFloat(ExertionId, frame.Exertion);
        Shader.SetGlobalFloat(BlinkId, frame.Blink);
        Shader.SetGlobalFloat(PulseId, frame.Pulse);
        Shader.SetGlobalFloat(DeathLockId, frame.DeathLock);
        Shader.SetGlobalFloat(TimeId, frame.Time);
        Shader.SetGlobalTexture(TearFilmId, DehydrationVisualTextures.TearFilm);
        Shader.SetGlobalTexture(RetinalNoiseId, DehydrationVisualTextures.RetinalNoise);
    }
}
