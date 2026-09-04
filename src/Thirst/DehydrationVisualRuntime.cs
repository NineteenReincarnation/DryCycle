using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Thirst;

/// <summary>
/// Camera-space dehydration presentation driven by HydrationWeakness' existing debt
/// ladder. The overlay lives behind the HUD so the hydration meter remains readable.
/// Shift+Alt+Period empties the followed player's water bar for testing.
/// </summary>
internal static class DehydrationVisualRuntime
{
    private const int DustCount = 14;
    private const int VignetteSegments = 48;
    private const int VignetteRings = 4;
    private const float Epsilon = 0.0001f;

    private static readonly Dictionary<RoomCamera, CameraVisualState> CameraStates = new();
    private static readonly List<RoomCamera> CameraRemovalBuffer = new();

    private static bool _enabled;
    private static int _nextSeed;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainWorldGame.Update += RainWorldGame_Update;
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
        On.RoomCamera.Update += RoomCamera_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.RainWorldGame.ShutDownProcess -= RainWorldGame_ShutDownProcess;
        On.RoomCamera.Update -= RoomCamera_Update;
        DestroyAllCameraStates();
        DehydrationVisualTextures.Dispose();
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame game)
    {
        orig(game);

        if (!_enabled || game == null || !game.IsStorySession)
        {
            return;
        }

        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (!shift || !alt || !Input.GetKeyDown(KeyCode.Period))
        {
            return;
        }

        Player player = FindFollowedPlayer(game);
        if (player == null)
        {
            return;
        }

        float before = ThirstStore.For(player).Water;
        ThirstStore.RemoveRuntime(player, ThirstStore.GetMaxWaterPips(player) + 1f);
        player.showKarmaFoodRainTime = Math.Max(
            player.showKarmaFoodRainTime,
            ThirstConstants.HydrationLossHudHoldFrames * 2);

        Plugin.Logger?.LogInfo(
            $"Dehydration test shortcut: P{player.playerState?.playerNumber ?? 0} " +
            $"water {before:0.###} -> {ThirstStore.For(player).Water:0.###}.");
    }

    private static void RoomCamera_Update(
        On.RoomCamera.orig_Update orig,
        RoomCamera camera)
    {
        orig(camera);

        if (!_enabled || camera?.game == null)
        {
            return;
        }

        Player player = camera.followAbstractCreature?.realizedCreature as Player;
        bool validPlayer = camera.game.IsStorySession &&
                           player != null &&
                           !player.isNPC;
        float debt = validPlayer ? HydrationWeakness.GetDebt(player) : 0f;

        CameraVisualState state = GetOrCreateCameraState(camera);
        if (state == null)
        {
            return;
        }

        state.Update(camera, player, debt);
    }

    private static void RainWorldGame_ShutDownProcess(
        On.RainWorldGame.orig_ShutDownProcess orig,
        RainWorldGame game)
    {
        DestroyCameraStates(game);
        orig(game);
    }

    private static Player FindFollowedPlayer(RainWorldGame game)
    {
        if (game?.cameras != null)
        {
            for (int i = 0; i < game.cameras.Length; i++)
            {
                Player followed = game.cameras[i]?.followAbstractCreature?.realizedCreature as Player;
                if (followed != null && !followed.isNPC)
                {
                    return followed;
                }
            }
        }

        if (game?.Players != null)
        {
            for (int i = 0; i < game.Players.Count; i++)
            {
                if (game.Players[i]?.realizedCreature is Player player && !player.isNPC)
                {
                    return player;
                }
            }
        }

        return null;
    }

    private static CameraVisualState GetOrCreateCameraState(RoomCamera camera)
    {
        if (CameraStates.TryGetValue(camera, out CameraVisualState existing))
        {
            return existing;
        }

        try
        {
            CameraVisualState created = new(camera, _nextSeed++);
            CameraStates[camera] = created;
            return created;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle dehydration visuals could not create a camera overlay: " + ex.Message);
            return null;
        }
    }

    private static void DestroyCameraStates(RainWorldGame game)
    {
        CameraRemovalBuffer.Clear();
        foreach (KeyValuePair<RoomCamera, CameraVisualState> pair in CameraStates)
        {
            if (game == null || pair.Key?.game == game)
            {
                pair.Value.Destroy();
                CameraRemovalBuffer.Add(pair.Key);
            }
        }

        for (int i = 0; i < CameraRemovalBuffer.Count; i++)
        {
            CameraStates.Remove(CameraRemovalBuffer[i]);
        }
        CameraRemovalBuffer.Clear();
    }

    private static void DestroyAllCameraStates()
    {
        foreach (CameraVisualState state in CameraStates.Values)
        {
            state.Destroy();
        }
        CameraStates.Clear();
        CameraRemovalBuffer.Clear();
    }

    private sealed class CameraVisualState
    {
        private static readonly Color DryWashColor = new(0.93f, 0.87f, 0.73f);
        private static readonly Color DryDustColor = new(0.92f, 0.82f, 0.62f);

        private readonly RainWorldGame _game;
        private readonly int _seed;
        private readonly FContainer _root;
        private readonly FSprite _composite;
        private readonly FSprite _wash;
        private readonly TriangleMesh _vignette;
        private readonly FSprite[] _dust = new FSprite[DustCount];
        private readonly FSprite _blackout;

        private float _smoothedDebt;
        private float _smoothedExertion;
        private bool _deathLocked;
        private float _lockedDebt;

        internal CameraVisualState(RoomCamera camera, int seed)
        {
            _game = camera.game;
            _seed = seed;
            _root = new FContainer { isVisible = false };
            _composite = DehydrationRenderPipeline.CreateSprite(camera);
            DehydrationRenderPipeline.AddToContainer(_composite, camera);

            _wash = CreateFullscreenSprite(DryWashColor);
            _vignette = CreateVignetteMesh();
            _blackout = CreateFullscreenSprite(Color.black);

            _root.AddChild(_wash);
            _root.AddChild(_vignette);
            for (int i = 0; i < _dust.Length; i++)
            {
                FSprite speck = new("Futile_White")
                {
                    anchorX = 0.5f,
                    anchorY = 0.5f,
                    color = DryDustColor,
                    alpha = 0f,
                    isVisible = false
                };
                _dust[i] = speck;
                _root.AddChild(speck);
            }
            _root.AddChild(_blackout);

            FContainer hud = camera.ReturnFContainer("HUD");
            hud.AddChild(_root);
            _root.MoveToBack();
        }

        internal void Update(RoomCamera camera, Player player, float debt)
        {
            if (player != null && !player.isNPC)
            {
                if (player.dead && (debt > Epsilon || _smoothedDebt > Epsilon))
                {
                    _deathLocked = true;
                    _lockedDebt = Mathf.Max(_lockedDebt, Mathf.Max(debt, _smoothedDebt));
                }
                else if (!player.dead && _deathLocked)
                {
                    // A living followed player means a new attempt/session owns this
                    // camera. Only then may a dehydration-death image clear.
                    _deathLocked = false;
                    _lockedDebt = 0f;
                }
            }

            float targetDebt = _deathLocked
                ? _lockedDebt
                : Mathf.Clamp(debt, 0f, HydrationWeakness.LethalDebt);
            _smoothedDebt = Mathf.Lerp(_smoothedDebt, targetDebt, targetDebt > _smoothedDebt ? 0.055f : 0.09f);

            float targetExertion = player == null ? 0f : Mathf.Clamp01(player.aerobicLevel);
            _smoothedExertion = Mathf.Lerp(_smoothedExertion, targetExertion, 0.08f);

            if (_smoothedDebt <= 0.01f)
            {
                _smoothedDebt = 0f;
                _root.isVisible = false;
                DehydrationRenderPipeline.Hide(_composite);
                return;
            }

            float width = Mathf.Max(1f, camera.game.rainWorld.options.ScreenSize.x);
            float height = Mathf.Max(1f, camera.game.rainWorld.options.ScreenSize.y);
            float time = Time.unscaledTime;

            float mild = StageAmount(0f, HydrationWeakness.MildEndDebt, _smoothedDebt);
            float moderate = StageAmount(
                HydrationWeakness.MildEndDebt,
                HydrationWeakness.ModerateEndDebt,
                _smoothedDebt);
            float severe = StageAmount(
                HydrationWeakness.ModerateEndDebt,
                HydrationWeakness.SevereEndDebt,
                _smoothedDebt);
            float collapse = StageAmount(
                HydrationWeakness.SevereEndDebt,
                HydrationWeakness.DyingStartDebt,
                _smoothedDebt);
            float dying = StageAmount(
                HydrationWeakness.DyingStartDebt,
                HydrationWeakness.LethalDebt,
                _smoothedDebt);

            float pulse = 0.5f + 0.5f * Mathf.Sin(
                time * Mathf.Lerp(1.35f, 2.8f, dying) + _seed * 0.71f);
            float exertionDrive = severe * _smoothedExertion;
            // The final fraction of the dying stage is a one-way eyelid closure.
            // Periodic weakness blinks may reopen; terminal closure may not, and a
            // dehydration death holds it at fully closed until a living player owns
            // the camera again.
            float terminalClosure = _deathLocked
                ? 1f
                : StageAmount(0.72f, 1f, dying);
            float blink = Mathf.Max(
                ComputeBlink(time, collapse, dying),
                terminalClosure);

            DehydrationRenderFrame renderFrame = new(
                mild,
                moderate,
                severe,
                collapse,
                dying,
                _smoothedExertion,
                blink,
                pulse,
                _deathLocked,
                time,
                active: true);
            if (DehydrationRenderPipeline.Draw(_composite, camera, renderFrame))
            {
                // The GrabPass owns the complete presentation when available. Keep the
                // mesh/sprite implementation as a true compatibility fallback only.
                _root.isVisible = false;
                ApplyMinimalInstability(camera, player, collapse, dying, pulse);
                return;
            }

            _root.isVisible = true;
            _root.x = 0f;
            _root.y = 0f;

            UpdateFullscreenSprite(_wash, width, height);
            _wash.alpha = Mathf.Clamp01(
                0.035f * mild +
                0.045f * moderate +
                0.050f * severe +
                0.045f * collapse +
                0.045f * dying +
                0.012f * exertionDrive);
            _wash.isVisible = _wash.alpha > Epsilon;

            float vignetteAlpha = Mathf.Clamp01(
                0.16f * mild +
                0.20f * moderate +
                0.20f * severe +
                0.20f * collapse +
                0.14f * dying +
                0.025f * pulse * collapse);
            float apertureDrive = Mathf.Clamp01(
                0.18f * mild +
                0.24f * moderate +
                0.24f * severe +
                0.22f * collapse +
                0.22f * dying);
            UpdateVignette(
                width,
                height,
                time,
                apertureDrive,
                Mathf.Max(vignetteAlpha, blink * 0.96f),
                blink,
                severe,
                collapse,
                dying);

            UpdateDust(width, height, time, moderate, severe, collapse, dying);

            UpdateFullscreenSprite(_blackout, width, height);
            // Fainting closes the detailed vignette like eyelids. A small central dim
            // remains, but the old generic full-screen black flash is deliberately gone.
            float fallbackDim = Mathf.Clamp01(
                collapse * 0.012f +
                dying * 0.045f * pulse +
                blink * dying * 0.055f);
            // The mesh fallback still reaches complete darkness at the terminal
            // point. The custom shader renders the preferred curved upper/lower lids.
            _blackout.alpha = Mathf.Max(
                fallbackDim,
                terminalClosure * terminalClosure * terminalClosure);
            _blackout.isVisible = _blackout.alpha > Epsilon;

            ApplyMinimalInstability(camera, player, collapse, dying, pulse);
        }

        internal void Destroy()
        {
            _composite.RemoveFromContainer();
            _root.RemoveFromContainer();
        }

        private void ApplyMinimalInstability(
            RoomCamera camera,
            Player player,
            float collapse,
            float dying,
            float pulse)
        {
            if (player == null || player.dead || collapse <= Epsilon)
            {
                return;
            }

            // Dehydration reads primarily through ocular failure. Camera motion remains
            // below the visual pipeline and appears only under late-stage exertion.
            float instability = collapse * Mathf.Lerp(0.002f, 0.010f, _smoothedExertion);
            instability += dying * 0.002f * pulse;
            camera.microShake = Mathf.Max(camera.microShake, instability);
        }

        private void UpdateVignette(
            float width,
            float height,
            float time,
            float apertureDrive,
            float alpha,
            float blink,
            float severe,
            float collapse,
            float dying)
        {
            Vector2 center = new(
                width * 0.5f,
                height * (0.5f + Mathf.Sin(time * 0.37f + _seed) * 0.004f * collapse));
            float innerRadiusX = width * Mathf.Lerp(0.46f, 0.20f, apertureDrive);
            float innerRadiusY = height * Mathf.Lerp(0.43f, 0.15f, apertureDrive);

            // Eye closure compresses vertically and only slightly horizontally. This
            // produces an organic narrowing slit instead of a uniform black opacity.
            innerRadiusX *= Mathf.Lerp(1f, 0.82f, blink);
            innerRadiusY *= Mathf.Lerp(1f, 0.018f, blink);

            float outerRadiusX = width * 0.79f;
            float outerRadiusY = height * 0.79f;
            float irregularity = 0.008f * severe + 0.014f * collapse + 0.018f * dying;

            for (int ring = 0; ring < VignetteRings; ring++)
            {
                float ringT = ring / (float)(VignetteRings - 1);
                float shapedT = ringT * ringT * (3f - 2f * ringT);
                float radiusX = Mathf.Lerp(innerRadiusX, outerRadiusX, shapedT);
                float radiusY = Mathf.Lerp(innerRadiusY, outerRadiusY, shapedT);
                float ringAlpha = ring == 0
                    ? 0f
                    : ring == 1
                        ? alpha * 0.20f
                        : ring == 2
                            ? alpha * 0.58f
                            : alpha;

                for (int segment = 0; segment <= VignetteSegments; segment++)
                {
                    float angle = segment / (float)VignetteSegments * Mathf.PI * 2f;
                    float dryEdge = 1f + irregularity * (
                        Mathf.Sin(angle * 3f + _seed * 0.83f) * 0.55f +
                        Mathf.Sin(angle * 7f - _seed * 1.17f) * 0.30f +
                        Mathf.Sin(angle * 11f + time * 0.12f) * 0.15f);
                    int index = ring * (VignetteSegments + 1) + segment;
                    _vignette.vertices[index] = center + new Vector2(
                        Mathf.Cos(angle) * radiusX * dryEdge,
                        Mathf.Sin(angle) * radiusY * dryEdge);
                    _vignette.verticeColors[index] = new Color(
                        0.035f,
                        0.018f,
                        0.008f,
                        ringAlpha);
                }
            }

            _vignette.isVisible = alpha > Epsilon;
            _vignette.Refresh();
        }

        private void UpdateDust(
            float width,
            float height,
            float time,
            float moderate,
            float severe,
            float collapse,
            float dying)
        {
            float strength = Mathf.Clamp01(
                0.035f +
                0.045f * moderate +
                0.060f * severe +
                0.080f * collapse +
                0.080f * dying);

            for (int i = 0; i < _dust.Length; i++)
            {
                FSprite speck = _dust[i];
                float a = Hash01(i * 17 + _seed * 101);
                float b = Hash01(i * 29 + _seed * 53 + 7);
                float drift = Mathf.Sin(time * (0.18f + a * 0.22f) + i * 1.73f) * 4f;
                int edge = i % 4;

                if (edge == 0)
                {
                    speck.x = a * width;
                    speck.y = 5f + b * height * 0.16f + drift;
                }
                else if (edge == 1)
                {
                    speck.x = a * width;
                    speck.y = height - 5f - b * height * 0.16f + drift;
                }
                else if (edge == 2)
                {
                    speck.x = 5f + b * width * 0.12f + drift;
                    speck.y = a * height;
                }
                else
                {
                    speck.x = width - 5f - b * width * 0.12f + drift;
                    speck.y = a * height;
                }

                float size = 0.09f + 0.14f * Hash01(i * 43 + _seed * 19 + 3);
                speck.scaleX = size * (0.7f + a * 1.5f);
                speck.scaleY = size * (0.45f + b * 0.75f);
                speck.alpha = strength * (0.45f + 0.55f * Mathf.Sin(time * 0.31f + i * 2.17f) * 0.5f + 0.275f);
                speck.isVisible = strength > Epsilon;
            }
        }

        private float ComputeBlink(
            float time,
            float collapse,
            float dying)
        {
            if (collapse <= Epsilon)
            {
                return 0f;
            }

            float critical = Mathf.Clamp01(collapse * 0.72f + dying * 0.55f);
            float interval = Mathf.Lerp(7.5f, 2.6f, dying);
            float duration = Mathf.Lerp(0.22f, 0.78f, dying);
            float phase = Mathf.Repeat(time + _seed * 1.37f, interval);
            float blink = 0f;
            if (phase < duration)
            {
                float close = Mathf.Sin(Mathf.Clamp01(phase / duration) * Mathf.PI);
                blink = close * Mathf.Lerp(0.30f, 1f, critical);
            }
            return Mathf.Clamp01(blink);
        }

        private static FSprite CreateFullscreenSprite(Color color)
        {
            return new FSprite("Futile_White")
            {
                anchorX = 0f,
                anchorY = 0f,
                color = color,
                alpha = 0f,
                isVisible = false
            };
        }

        private void UpdateFullscreenSprite(FSprite sprite, float width, float height)
        {
            sprite.x = 0f;
            sprite.y = 0f;
            sprite.scaleX = width / 16f;
            sprite.scaleY = height / 16f;
        }

        private static TriangleMesh CreateVignetteMesh()
        {
            TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[
                (VignetteRings - 1) * VignetteSegments * 2];
            int triangleIndex = 0;
            int row = VignetteSegments + 1;
            for (int ring = 0; ring < VignetteRings - 1; ring++)
            {
                for (int segment = 0; segment < VignetteSegments; segment++)
                {
                    int innerLeft = ring * row + segment;
                    int innerRight = innerLeft + 1;
                    int outerLeft = innerLeft + row;
                    int outerRight = outerLeft + 1;
                    triangles[triangleIndex++] = new TriangleMesh.Triangle(
                        innerLeft,
                        innerRight,
                        outerLeft);
                    triangles[triangleIndex++] = new TriangleMesh.Triangle(
                        innerRight,
                        outerRight,
                        outerLeft);
                }
            }

            TriangleMesh mesh = new("Futile_White", triangles, customColor: true)
            {
                isVisible = false
            };
            return mesh;
        }

        private static float StageAmount(float start, float end, float value)
        {
            float t = Mathf.InverseLerp(start, end, value);
            return t * t * (3f - 2f * t);
        }

        private static float Hash01(int value)
        {
            unchecked
            {
                uint x = (uint)value;
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return (x & 0x00ffffffU) / 16777215f;
            }
        }
    }
}
