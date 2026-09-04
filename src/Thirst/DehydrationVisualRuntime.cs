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
                           !player.isNPC &&
                           !player.dead;
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
        private static readonly Color DryWashColor = new(0.82f, 0.75f, 0.59f);
        private static readonly Color DryDustColor = new(0.92f, 0.82f, 0.62f);

        private readonly RainWorldGame _game;
        private readonly int _seed;
        private readonly FContainer _root;
        private readonly FSprite _wash;
        private readonly TriangleMesh _vignette;
        private readonly FSprite[] _dust = new FSprite[DustCount];
        private readonly FSprite _blackout;

        private float _smoothedDebt;
        private float _smoothedExertion;
        private float _lastWidth = -1f;
        private float _lastHeight = -1f;

        internal CameraVisualState(RoomCamera camera, int seed)
        {
            _game = camera.game;
            _seed = seed;
            _root = new FContainer { isVisible = false };

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
            float targetDebt = Mathf.Clamp(debt, 0f, HydrationWeakness.LethalDebt);
            _smoothedDebt = Mathf.Lerp(_smoothedDebt, targetDebt, targetDebt > _smoothedDebt ? 0.055f : 0.09f);

            float targetExertion = player == null ? 0f : Mathf.Clamp01(player.aerobicLevel);
            _smoothedExertion = Mathf.Lerp(_smoothedExertion, targetExertion, 0.08f);

            if (_smoothedDebt <= 0.01f)
            {
                _smoothedDebt = 0f;
                _root.isVisible = false;
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

            _root.isVisible = true;
            _root.x = 0f;
            _root.y = 0f;

            UpdateFullscreenSprite(_wash, width, height);
            _wash.alpha = Mathf.Clamp01(
                0.018f * mild +
                0.026f * moderate +
                0.035f * severe +
                0.045f * collapse +
                0.060f * dying +
                0.018f * exertionDrive);
            _wash.isVisible = _wash.alpha > Epsilon;

            float vignetteAlpha = Mathf.Clamp01(
                0.055f * mild +
                0.11f * moderate +
                0.17f * severe +
                0.26f * collapse +
                0.27f * dying +
                0.035f * pulse * collapse);
            float apertureDrive = Mathf.Clamp01(
                0.18f * mild +
                0.24f * moderate +
                0.24f * severe +
                0.22f * collapse +
                0.22f * dying);
            UpdateVignette(width, height, apertureDrive, vignetteAlpha);

            UpdateDust(width, height, time, moderate, severe, collapse, dying);

            UpdateFullscreenSprite(_blackout, width, height);
            _blackout.alpha = ComputeBlackout(time, collapse, dying, pulse);
            _blackout.isVisible = _blackout.alpha > Epsilon;

            if (player != null && severe > Epsilon)
            {
                float instability = severe * Mathf.Lerp(0.008f, 0.055f, _smoothedExertion);
                instability += collapse * 0.012f * pulse;
                camera.microShake = Mathf.Max(camera.microShake, instability);
            }
        }

        internal void Destroy()
        {
            _root.RemoveFromContainer();
        }

        private void UpdateVignette(
            float width,
            float height,
            float apertureDrive,
            float alpha)
        {
            float insetX = width * Mathf.Lerp(0.08f, 0.34f, apertureDrive);
            float insetY = height * Mathf.Lerp(0.10f, 0.31f, apertureDrive);
            Color outer = new(0.075f, 0.042f, 0.022f, alpha);
            Color inner = new(0.075f, 0.042f, 0.022f, 0f);

            SetQuad(
                _vignette,
                0,
                new Vector2(0f, 0f),
                new Vector2(width, 0f),
                new Vector2(0f, insetY),
                new Vector2(width, insetY),
                outer,
                outer,
                inner,
                inner);
            SetQuad(
                _vignette,
                4,
                new Vector2(0f, height - insetY),
                new Vector2(width, height - insetY),
                new Vector2(0f, height),
                new Vector2(width, height),
                inner,
                inner,
                outer,
                outer);
            SetQuad(
                _vignette,
                8,
                new Vector2(0f, insetY),
                new Vector2(insetX, insetY),
                new Vector2(0f, height - insetY),
                new Vector2(insetX, height - insetY),
                outer,
                inner,
                outer,
                inner);
            SetQuad(
                _vignette,
                12,
                new Vector2(width - insetX, insetY),
                new Vector2(width, insetY),
                new Vector2(width - insetX, height - insetY),
                new Vector2(width, height - insetY),
                inner,
                outer,
                inner,
                outer);

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
                0.025f * moderate +
                0.06f * severe +
                0.10f * collapse +
                0.12f * dying);

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

                float size = 0.07f + 0.11f * Hash01(i * 43 + _seed * 19 + 3);
                speck.scaleX = size * (0.7f + a * 1.5f);
                speck.scaleY = size * (0.45f + b * 0.75f);
                speck.alpha = strength * (0.45f + 0.55f * Mathf.Sin(time * 0.31f + i * 2.17f) * 0.5f + 0.275f);
                speck.isVisible = strength > Epsilon;
            }
        }

        private float ComputeBlackout(
            float time,
            float collapse,
            float dying,
            float pulse)
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
                blink = close * Mathf.Lerp(0.16f, 0.88f, critical);
            }

            float dimPulse = collapse * Mathf.Lerp(0.012f, 0.075f, dying) * pulse;
            return Mathf.Clamp01(Mathf.Max(blink, dimPulse));
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
            if (Mathf.Abs(_lastWidth - width) > Epsilon ||
                Mathf.Abs(_lastHeight - height) > Epsilon)
            {
                _lastWidth = width;
                _lastHeight = height;
            }

            sprite.x = 0f;
            sprite.y = 0f;
            sprite.scaleX = width / 16f;
            sprite.scaleY = height / 16f;
        }

        private static TriangleMesh CreateVignetteMesh()
        {
            TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[8];
            for (int i = 0; i < 4; i++)
            {
                int vertex = i * 4;
                triangles[i * 2] = new TriangleMesh.Triangle(vertex, vertex + 1, vertex + 2);
                triangles[i * 2 + 1] = new TriangleMesh.Triangle(vertex + 1, vertex + 3, vertex + 2);
            }

            TriangleMesh mesh = new("Futile_White", triangles, customColor: true)
            {
                isVisible = false
            };
            return mesh;
        }

        private static void SetQuad(
            TriangleMesh mesh,
            int start,
            Vector2 bottomLeft,
            Vector2 bottomRight,
            Vector2 topLeft,
            Vector2 topRight,
            Color bottomLeftColor,
            Color bottomRightColor,
            Color topLeftColor,
            Color topRightColor)
        {
            mesh.vertices[start] = bottomLeft;
            mesh.vertices[start + 1] = bottomRight;
            mesh.vertices[start + 2] = topLeft;
            mesh.vertices[start + 3] = topRight;
            mesh.verticeColors[start] = bottomLeftColor;
            mesh.verticeColors[start + 1] = bottomRightColor;
            mesh.verticeColors[start + 2] = topLeftColor;
            mesh.verticeColors[start + 3] = topRightColor;
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
