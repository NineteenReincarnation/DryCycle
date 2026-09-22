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
/// Exact renders use a guarded view around the visible canvas when the verified RWImGUI AddImage
/// contract supports UV sub-rects. Active view-only pan/zoom reprojects that committed surface
/// without Camera.Render; after interaction settles the main thread renders one exact replacement.
/// RenderTexture allocation depends on canvas pixel size, never zoom. A replacement target is not
/// considered committed until RWImGUI successfully presents it; presentation failure keeps the
/// previous last-known-good surface visible.
/// </summary>
internal sealed class WorldMapRenderTextureSurface
{
    internal const int RenderLayer = 31;
    private const int RejectedResizeRetryFrames = 30;
    private const float GuardBandScale = 1.5f;
    private const float CoverageEpsilonPixels = 0.75f;

    private readonly object gate = new();
    private readonly object presentationGate = new();
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
    private WorldMapViewTransform presentedTransform;
    private WorldMapViewTransform retiredTransform;
    private bool presentedTransformValid;
    private bool retiredTransformValid;

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

    internal void GetRenderWorldBounds(
        WorldMapViewTransform viewport,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        GetTargetSize(viewport, out int targetWidth, out int targetHeight);
        WorldMapViewTransform renderTransform =
            BuildRenderTransform(viewport, targetWidth, targetHeight);
        renderTransform.GetVisibleWorldBounds(out min, out max);
    }

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

        GetTargetSize(transform, out int targetWidth, out int targetHeight);
        WorldMapViewTransform renderTransform =
            BuildRenderTransform(transform, targetWidth, targetHeight);

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
            ConfigureCamera(renderTransform, target);
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
                    retiredTransform = presentedTransform;
                    retiredTransformValid = presentedTransformValid;

                    presented = target;
                    presentedValid = true;
                    width = targetWidth;
                    height = targetHeight;
                    presentedTransform = renderTransform;
                    presentedTransformValid = true;
                    rollbackRequested = false;
                    releaseRetiredRequested = false;
                    ownsCandidate = false;
                }

                if (!ownsCandidate)
                {
                    presentedTransform = renderTransform;
                    presentedTransformValid = true;
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
        Num.Vector2 max,
        WorldMapViewTransform currentView)
    {
        lock (presentationGate)
            return TryPresentCore(draw, min, max, currentView);
    }

    private bool TryPresentCore(
        ImDrawListPtr draw,
        Num.Vector2 min,
        Num.Vector2 max,
        WorldMapViewTransform currentView)
    {
        RenderTexture current;
        RenderTexture fallback;
        WorldMapViewTransform currentRenderedView;
        WorldMapViewTransform fallbackRenderedView;
        bool currentTransformReady;
        bool fallbackTransformReady;
        int currentWidth;
        int currentHeight;
        int fallbackWidth;
        int fallbackHeight;
        bool valid;

        lock (gate)
        {
            current = presented;
            fallback = retired;
            currentRenderedView = presentedTransform;
            fallbackRenderedView = retiredTransform;
            currentTransformReady = presentedTransformValid;
            fallbackTransformReady = retiredTransformValid;
            currentWidth = width;
            currentHeight = height;
            fallbackWidth = retiredWidth;
            fallbackHeight = retiredHeight;
            valid = presentedValid && current != null && currentTransformReady;
            if (valid)
                presentReaders++;
        }

        if (!valid)
            return false;

        try
        {
            bool currentBridgeAttempted;
            if (TryPresentTexture(
                    draw,
                    current,
                    currentWidth,
                    currentHeight,
                    currentRenderedView,
                    currentView,
                    min,
                    max,
                    out currentBridgeAttempted))
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

            if (fallback == null || !fallbackTransformReady)
                return false;

            // Coverage exhaustion is not a texture failure: it simply lets WorldMapView use its
            // immediate compatibility renderer until the interaction settles. Only a real bridge
            // failure rolls a freshly rendered candidate back to the last-known-good surface.
            if (!TryPresentTexture(
                    draw,
                    fallback,
                    fallbackWidth,
                    fallbackHeight,
                    fallbackRenderedView,
                    currentView,
                    min,
                    max,
                    out bool _))
                return false;

            if (currentBridgeAttempted)
            {
                lock (gate)
                {
                    if (ReferenceEquals(presented, current) &&
                        ReferenceEquals(retired, fallback))
                    {
                        rollbackRequested = true;
                        releaseRetiredRequested = false;
                    }
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
        lock (presentationGate)
            ResetAfterPresentationDrained();
    }

    private void ResetAfterPresentationDrained()
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
            presentedTransform = default;
            retiredTransform = default;
            presentedTransformValid = false;
            retiredTransformValid = false;
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
                    presentedTransform = retiredTransform;
                    presentedTransformValid = retiredTransformValid;

                    retired = null;
                    retiredWidth = 0;
                    retiredHeight = 0;
                    retiredTransform = default;
                    retiredTransformValid = false;

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
                retiredTransform = default;
                retiredTransformValid = false;
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
                presentedTransform = retiredTransform;
                presentedTransformValid = retiredTransformValid;
                retired = null;
                retiredWidth = 0;
                retiredHeight = 0;
                retiredTransform = default;
                retiredTransformValid = false;
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

    private bool TryPresentTexture(
        ImDrawListPtr draw,
        RenderTexture texture,
        int textureWidth,
        int textureHeight,
        WorldMapViewTransform renderedView,
        WorldMapViewTransform currentView,
        Num.Vector2 min,
        Num.Vector2 max,
        out bool bridgeAttempted)
    {
        bridgeAttempted = false;
        if (texture == null || textureWidth <= 0 || textureHeight <= 0)
            return false;

        if (!bridge.SupportsUvSubrect)
        {
            if (!DirectPresentationMatches(renderedView, currentView, textureWidth, textureHeight))
                return false;

            bridgeAttempted = true;
            return bridge.TryPresent(draw, texture, min, max);
        }

        if (!TryCalculateUv(
                renderedView,
                currentView,
                textureWidth,
                textureHeight,
                out Num.Vector2 uvMin,
                out Num.Vector2 uvMax))
            return false;

        bridgeAttempted = true;
        return bridge.TryPresent(draw, texture, min, max, uvMin, uvMax);
    }

    private static bool TryCalculateUv(
        WorldMapViewTransform renderedView,
        WorldMapViewTransform currentView,
        int textureWidth,
        int textureHeight,
        out Num.Vector2 uvMin,
        out Num.Vector2 uvMax)
    {
        uvMin = Num.Vector2.Zero;
        uvMax = Num.Vector2.One;
        if (textureWidth <= 0 || textureHeight <= 0 ||
            renderedView.Zoom <= 0.0001f || currentView.Zoom <= 0.0001f ||
            currentView.CanvasSize.X < 1f || currentView.CanvasSize.Y < 1f)
            return false;

        float ratio = renderedView.Zoom / currentView.Zoom;
        Num.Vector2 sourceMin =
            renderedView.Pan -
            currentView.Pan * ratio;
        Num.Vector2 sourceMax =
            sourceMin +
            currentView.CanvasSize * ratio;

        if (sourceMin.X < -CoverageEpsilonPixels ||
            sourceMin.Y < -CoverageEpsilonPixels ||
            sourceMax.X > textureWidth + CoverageEpsilonPixels ||
            sourceMax.Y > textureHeight + CoverageEpsilonPixels)
            return false;

        sourceMin = Num.Vector2.Max(sourceMin, Num.Vector2.Zero);
        sourceMax = Num.Vector2.Min(
            sourceMax,
            new Num.Vector2(textureWidth, textureHeight));

        uvMin = new Num.Vector2(
            sourceMin.X / textureWidth,
            sourceMin.Y / textureHeight);
        uvMax = new Num.Vector2(
            sourceMax.X / textureWidth,
            sourceMax.Y / textureHeight);
        return uvMax.X > uvMin.X && uvMax.Y > uvMin.Y;
    }

    private static bool DirectPresentationMatches(
        WorldMapViewTransform renderedView,
        WorldMapViewTransform currentView,
        int textureWidth,
        int textureHeight) =>
        Math.Abs(renderedView.Zoom - currentView.Zoom) <= 0.0001f &&
        Num.Vector2.DistanceSquared(renderedView.Pan, currentView.Pan) <= 0.0001f &&
        Math.Abs(textureWidth - currentView.CanvasSize.X) <= 1f &&
        Math.Abs(textureHeight - currentView.CanvasSize.Y) <= 1f;

    private void GetTargetSize(
        WorldMapViewTransform transform,
        out int targetWidth,
        out int targetHeight)
    {
        float scale = bridge.SupportsUvSubrect ? GuardBandScale : 1f;
        targetWidth = Math.Max(
            2,
            (int)Math.Ceiling(Math.Max(2f, transform.CanvasSize.X) * scale));
        targetHeight = Math.Max(
            2,
            (int)Math.Ceiling(Math.Max(2f, transform.CanvasSize.Y) * scale));
    }

    private static WorldMapViewTransform BuildRenderTransform(
        WorldMapViewTransform viewport,
        int targetWidth,
        int targetHeight)
    {
        Num.Vector2 margin = new(
            Math.Max(0f, targetWidth - viewport.CanvasSize.X) * 0.5f,
            Math.Max(0f, targetHeight - viewport.CanvasSize.Y) * 0.5f);

        return new WorldMapViewTransform(
            viewport.CanvasOrigin,
            new Num.Vector2(targetWidth, targetHeight),
            viewport.Pan + margin,
            viewport.Zoom);
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
