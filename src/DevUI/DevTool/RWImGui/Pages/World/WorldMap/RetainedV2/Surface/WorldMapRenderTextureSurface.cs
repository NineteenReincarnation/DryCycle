using System;
using BepInEx.Logging;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Off-screen map surface. Zoom changes camera transform only; RenderTexture allocation depends on
/// canvas pixel size, never zoom.
///
/// A replacement target is rendered successfully before it replaces the last-known-good presented
/// surface, preventing black frames during resize/recreation.
/// </summary>
internal sealed class WorldMapRenderTextureSurface
{
    internal const int RenderLayer = 31;

    private readonly WorldMapTextureBridge bridge = new();
    private Camera camera;
    private GameObject cameraObject;
    private RenderTexture presented;
    private RenderTexture retired;
    private int width;
    private int height;
    private string error = string.Empty;
    private ManualLogSource log;

    internal bool Ready => presented != null && presented.IsCreated();
    internal string Error => string.IsNullOrEmpty(error) ? bridge.Error : error;
    internal Camera Camera => camera;

    internal void Initialize(ManualLogSource logger)
    {
        log = logger;
        bridge.Initialize(logger);
        EnsureCamera();
    }

    internal bool Render(
        WorldMapViewTransform transform,
        Action<Camera> prepareScene)
    {
        if (transform.CanvasSize.X < 2f || transform.CanvasSize.Y < 2f)
            return false;

        EnsureCamera();

        int targetWidth = Math.Max(2, (int)Math.Ceiling(transform.CanvasSize.X));
        int targetHeight = Math.Max(2, (int)Math.Ceiling(transform.CanvasSize.Y));
        bool resize = presented == null ||
                      !presented.IsCreated() ||
                      width != targetWidth ||
                      height != targetHeight;

        RenderTexture target = presented;
        bool ownsCandidate = false;
        if (resize)
        {
            target = CreateTarget(targetWidth, targetHeight);
            if (target == null) return false;
            ownsCandidate = true;
        }

        try
        {
            ConfigureCamera(transform, target);
            prepareScene?.Invoke(camera);
            camera.targetTexture = target;
            camera.Render();

            if (ownsCandidate)
            {
                RenderTexture previous = presented;
                presented = target;
                width = targetWidth;
                height = targetHeight;
                ownsCandidate = false;

                // The texture bridge may still own a registration for the previously presented
                // target. Keep one retired target alive until the new surface is actually presented.
                ReleaseTarget(retired);
                retired = previous;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception renderError)
        {
            error = "V2 RenderTexture render failed: " + renderError.Message;
            log?.LogError("World Map V2 RenderTexture render failed: " + renderError);
            return false;
        }
        finally
        {
            camera.targetTexture = null;
            if (ownsCandidate)
                ReleaseTarget(target);
        }
    }

    internal bool TryPresent(
        ImDrawListPtr draw,
        Num.Vector2 min,
        Num.Vector2 max)
    {
        if (!Ready) return false;
        bool success = bridge.TryPresent(draw, presented, min, max);
        if (success && retired != null)
        {
            ReleaseTarget(retired);
            retired = null;
        }
        return success;
    }

    internal void Reset()
    {
        bridge.Reset();
        ReleaseTarget(presented);
        ReleaseTarget(retired);
        presented = null;
        retired = null;
        width = 0;
        height = 0;

        if (cameraObject != null)
            UnityEngine.Object.Destroy(cameraObject);
        camera = null;
        cameraObject = null;
        error = string.Empty;
        log = null;
    }

    private void EnsureCamera()
    {
        if (camera != null) return;

        cameraObject = new GameObject("DryCycle.WorldMapV2.Camera")
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = RenderLayer
        };
        camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.cullingMask = 1 << RenderLayer;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 500f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
    }

    private void ConfigureCamera(
        WorldMapViewTransform transform,
        RenderTexture target)
    {
        float zoom = Math.Max(0.0001f, transform.Zoom);
        float visibleWidth = target.width / zoom;
        float visibleHeight = target.height / zoom;
        float left = -transform.Pan.X / zoom;
        float top = -transform.Pan.Y / zoom;
        float centerX = left + visibleWidth * 0.5f;
        float centerMapY = top + visibleHeight * 0.5f;

        camera.aspect = target.width / (float)Math.Max(1, target.height);
        camera.orthographicSize = visibleHeight * 0.5f;
        camera.transform.position = new Vector3(centerX, -centerMapY, -100f);
        camera.transform.rotation = Quaternion.identity;
    }

    private RenderTexture CreateTarget(int targetWidth, int targetHeight)
    {
        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            target = new RenderTexture(
                targetWidth,
                targetHeight,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "DryCycle.WorldMapV2.Surface",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };

            if (!target.Create() || !target.IsCreated())
                throw new InvalidOperationException("RenderTexture.Create returned an invalid target.");

            RenderTexture.active = target;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            return target;
        }
        catch (Exception createError)
        {
            error = "V2 RenderTexture creation failed: " + createError.Message;
            log?.LogError("World Map V2 RenderTexture creation failed: " + createError);
            ReleaseTarget(target);
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    private static void ReleaseTarget(RenderTexture target)
    {
        if (target == null) return;
        try
        {
            if (target.IsCreated()) target.Release();
        }
        finally
        {
            UnityEngine.Object.Destroy(target);
        }
    }
}
