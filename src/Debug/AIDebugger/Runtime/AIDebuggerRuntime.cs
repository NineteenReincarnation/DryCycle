using System;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Transitional main-thread host for the RWImGUI migration.
//
// The old DryCycle-owned ImGui renderer (Camera -> Mesh -> CommandBuffer -> Futile) is
// intentionally not constructed anymore. F7 visibility, trace visibility, session export,
// input-gate installation and world-step installation remain owned by DryCycle. The
// optional DryCycle.AIObservatory.RWImGui bridge reads this state and presents the UI via
// Rawra's Win32 + DX11 Present backend.
internal static class AIDebuggerRuntime
{
    private const string BridgeAssemblyName = "DryCycle.AIObservatory.RWImGui";
    private const string RWImGuiAssemblyName = "rain-world-imgui-api";
    private static GameObject hostObject;
    private static AIDebuggerHost host;

    internal static bool Visible => host?.Visible == true;

    // These stay false until the RWImGUI frontend publishes its capture state back to the
    // main thread. Returning false is safer than letting the retired renderer steal input.
    internal static bool WantsMouse => false;
    internal static bool WantsKeyboard => false;
    internal static bool BlocksPlayerInput => false;

    internal static void Install(RainWorld rainWorld, ManualLogSource logger)
    {
        AIDebugSettings.Load(logger);

        bool bridgeAssemblyLoaded = IsAssemblyLoaded(BridgeAssemblyName);
        bool rwimguiAssemblyLoaded = IsAssemblyLoaded(RWImGuiAssemblyName);
        logger?.LogInfo($"DryCycle AI Observatory install requested. AutoOpen={AIDebugSettings.AutoOpen}, existingHost={host != null}, " +
                        $"presentation=RWImGUI, bridgeAssemblyLoaded={bridgeAssemblyLoaded}, rwimguiApiLoaded={rwimguiAssemblyLoaded}.");

        if (!rwimguiAssemblyLoaded)
        {
            logger?.LogWarning("DryCycle AI Observatory presentation is unavailable: Rain World ImGUI API is not loaded by BepInEx. " +
                               "The official ImGUI API Workshop item (3417372413) requires Rawra's Library Loader (3326331909). " +
                               "Install/enable both mods and restart Rain World. DryCycle gameplay systems will continue normally.");
        }
        else if (!bridgeAssemblyLoaded)
        {
            logger?.LogWarning("DryCycle AI Observatory presentation is unavailable: RWImGUI is loaded, but " +
                               "DryCycle.AIObservatory.RWImGui.dll is not loaded. Rebuild DryCycle with the RWImGUI dependency available " +
                               "and verify that the bridge DLL is present in Ancient Site/newest/plugins.");
        }

        // These are independent main-thread systems. Their failure must not prevent the
        // RWImGUI frontend from drawing.
        AIDebugInputGate.Install(logger);
        AIDebugSimulationControl.Install(logger);

        if (host != null)
        {
            host.Bind(rainWorld, logger);
            logger?.LogInfo("DryCycle AI Observatory rebound to the current RainWorld instance.");
            return;
        }

        AIDebugRegistry.Initialize(logger);
        hostObject = new GameObject("DryCycle AI Observatory Controller")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        UnityEngine.Object.DontDestroyOnLoad(hostObject);

        // Deliberately do not add a Camera. RWImGUI owns presentation through the game's
        // DX11 swap-chain Present path. Keeping a second Unity/Futile renderer active would
        // duplicate ImGui contexts and reintroduce the visibility problems this migration
        // is designed to remove.
        host = hostObject.AddComponent<AIDebuggerHost>();
        host.Bind(rainWorld, logger);
        host.SetStartupVisible(AIDebugSettings.AutoOpen);
        logger?.LogInfo($"DryCycle AI Observatory controller created. active={hostObject.activeInHierarchy}, startupVisible={AIDebugSettings.AutoOpen}, " +
                        $"legacyRenderer=disabled, overlayCamera=none, bridge={AIDebugPresentationBridgeStatus.Describe()}.");
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

    private static bool IsAssemblyLoaded(string name)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AIDebuggerHost : MonoBehaviour
{
    private RainWorld rainWorld;
    private ManualLogSource logger;
    private bool visible;
    private bool lifecycleLogged;
    private bool missingBridgeWarningLogged;

    internal bool Visible => visible;

    internal void Bind(RainWorld rw, ManualLogSource log)
    {
        rainWorld = rw;
        logger = log;
        logger?.LogInfo($"DryCycle AI Observatory controller Bind completed. rainWorld={(rainWorld != null ? "yes" : "no")}, " +
                        $"presentationState={AIDebugPresentationBridgeStatus.Describe()}.");
    }

    internal void SetStartupVisible(bool value)
    {
        visible = value;
        AIDebugTrace.SetVisible(value);
    }

    private void Update()
    {
        if (!lifecycleLogged)
        {
            lifecycleLogged = true;
            logger?.LogInfo($"DryCycle AI Observatory controller Update is running. visible={visible}, enabled={enabled}, " +
                            $"active={gameObject.activeInHierarchy}, presentationState={AIDebugPresentationBridgeStatus.Describe()}.");
        }

        if (Input.GetKeyDown(KeyCode.F7))
        {
            bool before = visible;
            visible = !visible;
            AIDebugTrace.SetVisible(visible);
            string presentation = AIDebugPresentationBridgeStatus.CallbackRegistered
                ? (AIDebugPresentationBridgeStatus.PresentSeen ? "RWImGUI-connected" : "RWImGUI-callback-waiting-for-Present")
                : "UNAVAILABLE";
            logger?.LogInfo($"DryCycle AI Observatory F7 detected. visible {before} -> {visible}. presentation={presentation}; " +
                            $"{AIDebugPresentationBridgeStatus.Describe()}.");

            if (visible && !AIDebugPresentationBridgeStatus.CallbackRegistered && !missingBridgeWarningLogged)
            {
                missingBridgeWarningLogged = true;
                logger?.LogWarning("DryCycle AI Observatory F7 state is ON, but no RWImGUI callback is registered, so no UI can appear. " +
                                   "Check that BepInEx loads 'Rain World ImGUI API' and 'DryCycle AI Observatory RWImGUI Bridge'. " +
                                   "ImGUI API also requires Rawra's Library Loader. Workshop IDs: ImGUI API=3417372413, Library Loader=3326331909.");
            }
        }

        // Whole-session export is intentionally independent of the presentation frontend.
        if (Input.GetKeyDown(KeyCode.F8) &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
        {
            TryExportSession();
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

    private void OnDestroy()
    {
        AIDebugSettings.Save();
        AIDebugTrace.Reset();
    }
}
