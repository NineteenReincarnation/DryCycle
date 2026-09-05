using System;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebuggerRuntime
{
    private static GameObject hostObject;
    private static AIDebuggerHost host;

    internal static void Install(RainWorld rainWorld, ManualLogSource logger)
    {
        if (host != null)
        {
            host.Bind(rainWorld, logger);
            return;
        }

        AIDebugRegistry.Initialize();
        hostObject = new GameObject("DryCycle AI Observatory")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        UnityEngine.Object.DontDestroyOnLoad(hostObject);

        Camera camera = hostObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.cullingMask = 0;
        camera.depth = 10000f;
        camera.orthographic = true;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        host = hostObject.AddComponent<AIDebuggerHost>();
        host.Bind(rainWorld, logger);
    }

    internal static void Uninstall()
    {
        AIDebugTrace.Reset();
        if (hostObject != null) UnityEngine.Object.Destroy(hostObject);
        hostObject = null;
        host = null;
    }
}

internal sealed class AIDebuggerHost : MonoBehaviour
{
    private RainWorld rainWorld;
    private ManualLogSource logger;
    private Camera overlayCamera;
    private AIDebugImGuiBackend backend;
    private readonly AIDebuggerWindow window = new();
    private bool visible;
    private bool backendFailed;
    private double overheadMs;

    internal void Bind(RainWorld rw, ManualLogSource log)
    {
        rainWorld = rw;
        logger = log;
        overlayCamera = GetComponent<Camera>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            visible = !visible;
            AIDebugTrace.SetVisible(visible);
            if (overlayCamera != null) overlayCamera.enabled = visible;
        }

        if (!visible) return;
        if (Input.GetKeyDown(KeyCode.F6)) window.FullMode = !window.FullMode;
        if (!EnsureBackend()) return;

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            backend.BeginFrame();
            RainWorldGame game = rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            window.Draw(game, overheadMs);
            backend.EndFrame();
        }
        catch (Exception error)
        {
            FailBackend("frame", error);
            return;
        }
        finally
        {
            watch.Stop();
        }

        overheadMs = overheadMs <= 0.0 ? watch.Elapsed.TotalMilliseconds
            : overheadMs * 0.88 + watch.Elapsed.TotalMilliseconds * 0.12;
    }

    private void OnPostRender()
    {
        if (!visible || backend == null || backendFailed) return;
        try
        {
            backend.Render();
        }
        catch (Exception error)
        {
            FailBackend("render", error);
        }
    }

    private bool EnsureBackend()
    {
        if (backend != null) return true;
        if (backendFailed) return false;
        try
        {
            backend = new AIDebugImGuiBackend();
            logger?.LogInfo("DryCycle AI Observatory initialized. F7 toggle, F6 compact/full, Alt+LMB world pick.");
            return true;
        }
        catch (Exception error)
        {
            FailBackend("initialization", error);
            return false;
        }
    }

    private void FailBackend(string phase, Exception error)
    {
        backendFailed = true;
        visible = false;
        AIDebugTrace.SetVisible(false);
        if (overlayCamera != null) overlayCamera.enabled = false;
        logger?.LogError($"DryCycle AI Observatory {phase} failed: {error}");
        backend?.Dispose();
        backend = null;
    }

    private void OnDestroy()
    {
        AIDebugTrace.Reset();
        backend?.Dispose();
        backend = null;
    }
}
