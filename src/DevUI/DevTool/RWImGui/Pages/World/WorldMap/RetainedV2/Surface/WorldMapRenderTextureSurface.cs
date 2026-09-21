using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Off-screen map surface.
///
/// The RWImGUI Present callback and Unity's main-thread map renderer may run concurrently. The state
/// gate therefore protects only pointer/state exchange; Camera.Render and ImGui AddImage never run
/// while holding the same lock. Unity resource destruction is deferred to the main-thread pump.
///
/// Zoom changes the camera transform only. RenderTexture allocation depends on canvas pixel size,
/// never zoom. A replacement target is not considered committed until RWImGUI successfully presents
/// it; presentation failure keeps the previous last-known-good surface visible.
/// </summary>
internal sealed class WorldMapRenderTextureSurface
{
    internal const int RenderLayer = 31;
    private const int RejectedResizeRetryFrames = 30;

    private readonly object gate = new();
    private readonly WorldMapTextureBridge bridge = new();
    private readonly List<RenderTexture> pendingRelease = new();

    private Camera camera;
    private GameObject cameraObject;

    // Front texture visible to RWImGUI. "retired" is the previous front retained until the new
    // candidate has actually been accepted by the texture bridge.
    private RenderTexture presented;
    private RenderTexture retired;
    private int width;
    private int height;
    private int retiredWidth;
    private int retiredHeight;

    private int rejectedWidth;
    private int rejectedHeight;
    private int resizeRetryAfterFrame;

    private bool presentedValid;
    private bool renderInvalidated = true;
    private bool rollbackRequested;
    private bool releaseRetiredRequested;
    private bool initialized;
    private int presentReaders;

    private string error = string.Empty;
    private ManualLogSource log;

    internal bool Ready
    {
        get
        {
            lock (gate)
                return presentedValid && presented != null;
        }
    }

    internal bool NeedsRender
    {
        get
        {
            lock (gate)
            {
                return !presentedValid ||
                       renderInvalidated ||
                       rollbackRequested ||
                       (rejectedWidth > 0 &&
                        rejectedHeight > 0 &&
                        Time.frameCount >= resizeRetryAfterFrame &&
                        (width != rejectedWidth || height != rejectedHeight));
            }
        }
    }

    internal string Error
    {
        get
        {
            lock (gate)
                return string.IsNullOrEmpty(error) ? bridge.Error : error;
        }
    }

    internal Camera Camera => camera;

    /// <summary>
    /// Main-thread surface pump. It is intentionally cheap on stable frames.
    /// </summary>
    internal void Initialize(ManualLogSource logger)
    {
        log = logger;

        if (!initialized)
        {
            bridge.Initialize(logger);
            EnsureCamera();
            initialized = true;
        }

        ApplyPresentationFeedbackMainThread();
        ValidatePresentedMainThread();
        DrainPendingReleasesMainThread();
    }

    internal bool Render(
        WorldMapViewTransform transform,
        Action<Camera> prepareScene)
    {
        if (transform.CanvasSize.X < 2f || transform.CanvasSize.Y < 2f)
            return false;

        Initialize(log);
        EnsureCamera();

        int targetWidth = Math.Max(2, (int)Math.Ceiling(transform.CanvasSize.X));
        int targetHeight = Math.Max(2, (int)Math.Ceiling(transform.CanvasSize.Y));

        RenderTexture current;
        bool currentValid;
        bool hasUnconfirmedCandidate;
        bool frontInUse;
        int currentWidth;
        int currentHeight;
        int localRejectedWidth;
        int localRejectedHeight;
        int localRetryAfter;

        lock (gate)
        {
            current = presented;
            currentValid = presentedValid;
            hasUnconfirmedCandidate = retired != null;
            frontInUse = presentReaders > 0;
            currentWidth = width;
            currentHeight = height;
            localRejectedWidth = rejectedWidth;
            localRejectedHeight = rejectedHeight;
            localRetryAfter = resizeRetryAfterFrame;
        }

        bool sizeMismatch =
            currentValid &&
            (currentWidth != targetWidth || currentHeight != targetHeight);
        bool rejectedResizeCoolingDown =
            sizeMismatch &&
            targetWidth == localRejectedWidth &&
            targetHeight == localRejectedHeight &&
            Time.frameCount < localRetryAfter;

        // Never stack replacement surfaces. Until a candidate is either accepted or rejected by
        // RWImGUI, keep rendering that candidate and retain exactly one last-known-good fallback.
        bool resize =
            !currentValid ||
            (!hasUnconfirmedCandidate &&
             sizeMismatch &&
             !rejectedResizeCoolingDown);

        RenderTexture target = current;
        bool ownsCandidate = false;
        if (resize)
        {
            target = CreateTarget(targetWidth, targetHeight);
            if (target == null)
                return false;
            ownsCandidate = true;
        }
        else if (frontInUse)
        {
            // Do not block Unity Update behind RWImGUI Present and do not render into a texture that
            // the Present thread is currently consuming. The revision remains dirty, so the next
            // main-thread frame retries automatically.
            return false;
        }

        if (target == null)
            return false;

        try
        {
            ConfigureCamera(transform, target);
            prepareScene?.Invoke(camera);
            camera.targetTexture = target;
            camera.Render();

            lock (gate)
            {
                if (ownsCandidate)
                {
                    // A render-thread rollback can only target a previously published candidate.
                    // This freshly rendered target is not visible yet, so swapping it in is safe.
                    if (retired != null && !ReferenceEquals(retired, presented))
                        QueueReleaseLocked(retired);

                    retired = presented;
                    retiredWidth = width;
                    retiredHeight = height;

                    presented = target;
                    presentedValid = true;
                    width = targetWidth;
                    height = targetHeight;
                    rollbackRequested = false;
                    releaseRetiredRequested = false;
                    ownsCandidate = false;
                }

                renderInvalidated = false;
                error = string.Empty;
            }

            return true;
        }
        catch (Exception renderError)
        {
            lock (gate)
            {
                renderInvalidated = true;
                error = "V2 RenderTexture render failed: " + renderError.Message;
            }

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

    /// <summary>
    /// Render-thread presentation. No Unity object is destroyed here and the shared state gate is
    /// never held while invoking RWImGUI.
    /// </summary>
    internal bool TryPresent(
        ImDrawListPtr draw,
        Num.Vector2 min,
        Num.Vector2 max)
    {
        RenderTexture current;
        RenderTexture fallback;
        bool valid;

        lock (gate)
        {
            current = presented;
            fallback = retired;
            valid = presentedValid && current != null;
            if (valid)
                presentReaders++;
        }

        if (!valid)
            return false;

        try
        {
            if (bridge.TryPresent(draw, current, min, max))
            {
                if (fallback != null)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(presented, current) &&
                            ReferenceEquals(retired, fallback))
                            releaseRetiredRequested = true;
                    }
                }
                return true;
            }

            if (fallback == null)
                return false;

            // The candidate failed to bind/present. Draw the old committed surface for this frame,
            // then let Unity's main thread atomically restore it and dispose the rejected candidate.
            if (!bridge.TryPresent(draw, fallback, min, max))
                return false;

            lock (gate)
            {
                if (ReferenceEquals(presented, current) &&
                    ReferenceEquals(retired, fallback))
                {
                    rollbackRequested = true;
                    releaseRetiredRequested = false;
                }
            }

            return true;
        }
        finally
        {
            lock (gate)
                presentReaders = Math.Max(0, presentReaders - 1);
        }
    }

    internal void Reset()
    {
        GameObject oldCamera;

        lock (gate)
        {
            QueueReleaseLocked(presented);
            QueueReleaseLocked(retired);
            oldCamera = cameraObject;

            presented = null;
            retired = null;
            presentedValid = false;
            width = 0;
            height = 0;
            retiredWidth = 0;
            retiredHeight = 0;
            rejectedWidth = 0;
            rejectedHeight = 0;
            resizeRetryAfterFrame = 0;
            renderInvalidated = true;
            rollbackRequested = false;
            releaseRetiredRequested = false;

            camera = null;
            cameraObject = null;
            error = string.Empty;
            initialized = false;
        }

        // Bridge.Reset serializes with an in-flight bridge presentation. Texture destruction itself
        // remains main-thread-only and is delayed if the outer surface Present call still holds a
        // reader reference after returning from the bridge.
        bridge.Reset();
        DrainPendingReleasesMainThread();

        if (oldCamera != null)
            UnityEngine.Object.Destroy(oldCamera);

        log = null;
    }

    private void ApplyPresentationFeedbackMainThread()
    {
        RenderTexture release = null;
        bool restored = false;

        lock (gate)
        {
            if (rollbackRequested)
            {
                rollbackRequested = false;

                if (retired != null)
                {
                    RenderTexture rejected = presented;
                    int failedWidth = width;
                    int failedHeight = height;

                    presented = retired;
                    presentedValid = presented != null;
                    width = retiredWidth;
                    height = retiredHeight;

                    retired = null;
                    retiredWidth = 0;
                    retiredHeight = 0;

                    rejectedWidth = failedWidth;
                    rejectedHeight = failedHeight;
                    resizeRetryAfterFrame =
                        Time.frameCount + RejectedResizeRetryFrames;
                    renderInvalidated = true;
                    releaseRetiredRequested = false;

                    release = rejected;
                    restored = true;
                }
            }
            else if (releaseRetiredRequested)
            {
                releaseRetiredRequested = false;
                release = retired;
                retired = null;
                retiredWidth = 0;
                retiredHeight = 0;
                rejectedWidth = 0;
                rejectedHeight = 0;
                resizeRetryAfterFrame = 0;
            }

            if (release != null)
                QueueReleaseLocked(release);
        }

        if (restored)
        {
            log?.LogWarning(
                "World Map V2 rejected a replacement RenderTexture presentation; " +
                "restored the last-known-good surface and will retry the resize later.");
        }
    }

    private void ValidatePresentedMainThread()
    {
        lock (gate)
        {
            if (presented == null)
            {
                presentedValid = false;
                return;
            }

            bool valid;
            try
            {
                valid = presented.IsCreated();
            }
            catch
            {
                valid = false;
            }

            if (valid)
            {
                presentedValid = true;
                return;
            }

            // Device loss / invalid candidate: if a last-known-good target is still retained,
            // restore it immediately on the Unity thread instead of allocating over the fallback.
            if (retired != null)
            {
                QueueReleaseLocked(presented);
                presented = retired;
                width = retiredWidth;
                height = retiredHeight;
                retired = null;
                retiredWidth = 0;
                retiredHeight = 0;
                rollbackRequested = false;
                releaseRetiredRequested = false;

                bool fallbackValid;
                try
                {
                    fallbackValid = presented.IsCreated();
                }
                catch
                {
                    fallbackValid = false;
                }

                presentedValid = fallbackValid;
                renderInvalidated = true;
                return;
            }

            presentedValid = false;
            renderInvalidated = true;
        }
    }

    private void QueueReleaseLocked(RenderTexture target)
    {
        if (target == null)
            return;

        for (int i = 0; i < pendingRelease.Count; i++)
            if (ReferenceEquals(pendingRelease[i], target))
                return;

        pendingRelease.Add(target);
    }

    private void DrainPendingReleasesMainThread()
    {
        RenderTexture[] release;
        lock (gate)
        {
            if (pendingRelease.Count == 0 || presentReaders > 0)
                return;

            release = pendingRelease.ToArray();
            pendingRelease.Clear();
        }

        for (int i = 0; i < release.Length; i++)
            ReleaseTarget(release[i]);
    }

    private void EnsureCamera()
    {
        if (camera != null)
            return;

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
                throw new InvalidOperationException(
                    "RenderTexture.Create returned an invalid target.");

            RenderTexture.active = target;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            return target;
        }
        catch (Exception createError)
        {
            lock (gate)
            {
                error = "V2 RenderTexture creation failed: " + createError.Message;
                renderInvalidated = true;
            }

            log?.LogError(
                "World Map V2 RenderTexture creation failed: " + createError);
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
        if (target == null)
            return;

        try
        {
            if (target.IsCreated())
                target.Release();
        }
        finally
        {
            UnityEngine.Object.Destroy(target);
        }
    }
}
