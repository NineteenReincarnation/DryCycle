using System;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Debugging.AI;
using ImGuiNET;
using RWIMGUI.API;

namespace DryCycle.AIObservatory.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("rwimgui", BepInDependency.DependencyFlags.HardDependency)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.AIObservatory.RWImGui";
    public const string PluginName = "DryCycle AI Observatory RWImGUI Bridge";
    public const string PluginVersion = "0.2.0";

    private static ManualLogSource log;
    private static bool callbackRegistered;

    private void OnEnable()
    {
        log = Logger;
        ObservatoryFrontend.SetLogger(Logger);
        AIDebugPresentationBridgeStatus.MarkBridgeLoaded(PluginVersion);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        Logger.LogInfo(
            "DryCycle RWImGUI bridge loaded. RWImGUI is a hard dependency for this optional bridge; " +
            "waiting for RainWorld.OnModsInit before registering AddAlwaysCallback.");
    }

    private void Update()
    {
        // Only copy the visibility bit from Unity's main thread. All actual Observatory
        // data reaches the Present callback through AIDebugPresentationHub's immutable
        // presentation snapshot.
        ObservatoryFrontend.Visible = AIDebuggerRuntime.Visible;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        ObservatoryFrontend.Enabled = false;
        ObservatoryFrontend.Visible = false;
        AIDebugPresentationHub.SetCaptureState(false, false);
        TryUnregisterCallback();
        AIDebugPresentationBridgeStatus.MarkFailure("RWImGUI bridge disabled");
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        TryRegisterCallback();
    }

    private static unsafe void TryRegisterCallback()
    {
        if (callbackRegistered)
        {
            ObservatoryFrontend.Enabled = true;
            return;
        }

        try
        {
            Version apiVersion = typeof(ImGUIAPI).Assembly.GetName().Version;
            Version imguiVersion = typeof(ImGui).Assembly.GetName().Version;

            // Verified against RWImGUI 1.12.0: this callback is invoked after the backend
            // NewFrame calls and before RWImGUI renders the frame, regardless of whether
            // RWImGUI's own menu is visible.
            ImGUIAPI.AddAlwaysCallback(&ObservatoryFrontend.FrameCallback);

            callbackRegistered = true;
            ObservatoryFrontend.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(
                apiVersion?.ToString(),
                imguiVersion?.ToString());

            log?.LogInfo(
                "DryCycle RWImGUI AddAlwaysCallback registered directly through RWIMGUI.API.ImGUIAPI. " +
                $"api={apiVersion}, imgui={imguiVersion}, hasContext={ImGUIAPI.HasContext}. " +
                "The Observatory frontend now consumes main-thread snapshots; press F7 directly.");
        }
        catch (Exception error)
        {
            ObservatoryFrontend.Enabled = false;
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            log?.LogError(
                "DryCycle RWImGUI AddAlwaysCallback registration failed. " +
                "DryCycle gameplay systems remain active. " + error);
        }
    }

    private static unsafe void TryUnregisterCallback()
    {
        if (!callbackRegistered)
            return;

        try
        {
            ImGUIAPI.RemoveAlwaysCallback(&ObservatoryFrontend.FrameCallback);
            callbackRegistered = false;
            log?.LogInfo("DryCycle RWImGUI AddAlwaysCallback unregistered.");
        }
        catch (Exception error)
        {
            log?.LogWarning("DryCycle RWImGUI callback removal failed during shutdown: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static class ObservatoryFrontend
{
    private static ManualLogSource log;
    private static int firstPresentLogged;
    private static int firstVisibleDrawLogged;
    private static int drawFailureLogged;
    private static volatile bool visible;

    internal static bool Enabled { get; set; }

    internal static bool Visible
    {
        get => visible;
        set => visible = value;
    }

    internal static void SetLogger(ManualLogSource value) => log = value;

    public static void FrameCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled)
            return;

        try
        {
            AIDebugPresentationBridgeStatus.MarkPresentSeen();
            if (Interlocked.Exchange(ref firstPresentLogged, 1) == 0)
            {
                log?.LogInfo(
                    $"DryCycle RWImGUI AddAlwaysCallback reached Present. swapChain=0x{idxgiSwapChain:X}, " +
                    $"syncInterval={syncInterval}, flags={flags}.");
            }

            if (!Visible)
            {
                AIDebugPresentationHub.SetCaptureState(false, false);
                return;
            }

            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo("DryCycle RWImGUI F7-visible frame reached Present; drawing functional snapshot/command-queue Observatory UI.");

            ObservatoryView.Draw(AIDebugPresentationHub.Current);

            ImGuiIOPtr io = ImGui.GetIO();
            AIDebugPresentationHub.SetCaptureState(io.WantCaptureMouse, io.WantCaptureKeyboard);
        }
        catch (Exception error)
        {
            AIDebugPresentationHub.SetCaptureState(false, false);
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
            {
                log?.LogError(
                    "DryCycle RWImGUI Observatory draw failed. The callback remains registered for diagnostics. " + error);
            }
        }
    }
}
