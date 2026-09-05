using System;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebuggerRuntime
{
    private static GameObject hostObject;
    private static AIDebuggerHost host;

    internal static bool Visible => host?.Visible == true;
    internal static bool WantsMouse => host?.WantsMouse == true;
    internal static bool WantsKeyboard => host?.WantsKeyboard == true;
    internal static bool BlocksPlayerInput => host?.BlocksPlayerInput == true;

    internal static void Install(RainWorld rainWorld, ManualLogSource logger)
    {
        AIDebugSettings.Load(logger);
        AIDebugInputGate.Install(logger);
        AIDebugSimulationControl.Install(logger);
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
        camera.enabled = AIDebugSettings.AutoOpen;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.cullingMask = 0;
        camera.depth = 10000f;
        camera.orthographic = true;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        host = hostObject.AddComponent<AIDebuggerHost>();
        host.Bind(rainWorld, logger);
        host.SetStartupVisible(AIDebugSettings.AutoOpen);
    }

    internal static void Uninstall()
    {
        AIDebugSettings.Save();
        AIDebugTrace.Reset();
        AIDebugSimulationControl.Uninstall();
        AIDebugInputGate.Uninstall();
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
    private readonly AIDebuggerWindowV3 window = new();
    private bool visible;
    private bool backendFailed;
    private double overheadMs;

    internal bool Visible => visible;
    internal bool WantsMouse => visible && backend?.WantsMouse == true;
    internal bool WantsKeyboard => visible && (window.InteractMode || backend?.WantsKeyboard == true);
    internal bool BlocksPlayerInput => visible &&
        (window.InteractMode || backend?.WantsKeyboard == true || backend?.WantsMouse == true);

    internal void Bind(RainWorld rw, ManualLogSource log)
    {
        rainWorld = rw;
        logger = log;
        overlayCamera = GetComponent<Camera>();
    }

    internal void SetStartupVisible(bool value)
    {
        visible = value;
        AIDebugTrace.SetVisible(value);
        if (overlayCamera != null) overlayCamera.enabled = value;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            if (backendFailed)
            {
                logger?.LogWarning("DryCycle AI Observatory is disabled for this session because its ImGui backend previously failed. Check the earlier error and restart Rain World after fixing the runtime files.");
                visible = false;
                AIDebugTrace.SetVisible(false);
                if (overlayCamera != null) overlayCamera.enabled = false;
                return;
            }

            visible = !visible;
            AIDebugTrace.SetVisible(visible);
            if (overlayCamera != null) overlayCamera.enabled = visible;
        }

        // Whole-session export is intentionally independent of the Dock layout. It can
        // still be used after closing F7 as long as the retained trace buffers exist.
        if (Input.GetKeyDown(KeyCode.F8) &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
        {
            TryExportSession();
        }

        if (!visible) return;
        if (Input.GetKeyDown(KeyCode.F6)) window.FullMode = !window.FullMode;
        // Do not steal Tab from an active ImGui text/navigation widget.
        if (Input.GetKeyDown(KeyCode.Tab) && backend?.WantsKeyboard != true) window.ToggleInteract();
        if (!EnsureBackend()) return;

        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            backend.BeginFrame();
            AIDebugStyleController.Apply();
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
            AIDebugStyleController.Reset();
            backend = new AIDebugImGuiBackend();
            logger?.LogInfo("DryCycle AI Observatory V3 initialized. F7 toggle, F6 compact/full, Tab live/interact, Alt+LMB world pick, Ctrl+Shift+F8 session export, whole-world pause/step enabled.");
            return true;
        }
        catch (Exception error)
        {
            FailBackend("initialization", error);
            return false;
        }
    }

    private void TryExportSession()
    {
        try
        {
            string path = AIDebugSessionExporter.Export();
            logger?.LogInfo("DryCycle AI Observatory session exported: " + path);
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory session export failed: " + error);
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
        AIDebugStyleController.Reset();
    }

    private void OnDestroy()
    {
        try
        {
            if (backend != null) AIDebugDockingNative.SaveLayout();
        }
        catch { }
        AIDebugSettings.Save();
        AIDebugTrace.Reset();
        backend?.Dispose();
        backend = null;
        AIDebugStyleController.Reset();
    }
}
