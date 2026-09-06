using System;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Debugging.AI;
using ImGuiNET;
using RWIMGUI.API;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("rwimgui", BepInDependency.DependencyFlags.HardDependency)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.AIObservatory.RWImGui";
    public const string PluginName = "DryCycle AI Observatory RWImGUI Bridge";
    public const string PluginVersion = "0.1.3";

    private static ManualLogSource log;
    private static bool callbackRegistered;

    private void OnEnable()
    {
        log = Logger;
        ProbeMenu.SetLogger(Logger);
        AIDebugPresentationBridgeStatus.MarkBridgeLoaded(PluginVersion);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        Logger.LogInfo(
            "DryCycle RWImGUI bridge loaded. RWImGUI is a hard dependency for this optional bridge; " +
            "waiting for RainWorld.OnModsInit before registering AddAlwaysCallback.");
    }

    private void Update()
    {
        // Unity/Rain World state is copied only on the Unity main thread. The RWImGUI
        // Present callback consumes this bool and never reads Unity input or live Rain
        // World objects directly from the graphics hook.
        ProbeMenu.Visible = AIDebuggerRuntime.Visible;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        ProbeMenu.Enabled = false;
        ProbeMenu.Visible = false;
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
            ProbeMenu.Enabled = true;
            return;
        }

        try
        {
            Version apiVersion = typeof(ImGUIAPI).Assembly.GetName().Version;
            Version imguiVersion = typeof(ImGui).Assembly.GetName().Version;

            // Verified against the user's RWImGUI 1.12.0 assembly. AddAlwaysCallback takes
            // delegate*<ref IntPtr, ref uint, ref uint, void> and is invoked unconditionally
            // after ImGui.NewFrame in idxgiswapchain_present_hook_impl, before context Render.
            ImGUIAPI.AddAlwaysCallback(&ProbeMenu.FrameCallback);

            callbackRegistered = true;
            ProbeMenu.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(
                apiVersion?.ToString(),
                imguiVersion?.ToString());

            log?.LogInfo(
                "DryCycle RWImGUI AddAlwaysCallback registered directly through RWIMGUI.API.ImGUIAPI. " +
                $"api={apiVersion}, imgui={imguiVersion}, hasContext={ImGUIAPI.HasContext}. " +
                "The callback does not require the RWImGUI menu to be opened; press F7 directly.");
        }
        catch (Exception error)
        {
            ProbeMenu.Enabled = false;
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
            ImGUIAPI.RemoveAlwaysCallback(&ProbeMenu.FrameCallback);
            callbackRegistered = false;
            log?.LogInfo("DryCycle RWImGUI AddAlwaysCallback unregistered.");
        }
        catch (Exception error)
        {
            // The callback itself is already disabled, so a removal failure is harmless at
            // shutdown and must not interfere with the rest of DryCycle.
            log?.LogWarning("DryCycle RWImGUI callback removal failed during shutdown: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static class ProbeMenu
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
                return;

            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo("DryCycle RWImGUI F7-visible frame reached Present; drawing minimal proof window.");

            ImGui.SetNextWindowPos(new Num.Vector2(24f, 24f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Num.Vector2(420f, 150f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(
                    "DryCycle AI Observatory - RWImGUI Probe###DryCycleRWImGuiProbe",
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.TextColored(new Num.Vector4(0.35f, 0.9f, 0.48f, 1f), "RWImGUI backend connected");
                ImGui.Separator();
                ImGui.Text("F7 visibility state: ON");
                ImGui.Text("Present callback: OK (AddAlwaysCallback)");
                ImGui.Text("Renderer owner: RWImGUI Win32 + DX11");
                ImGui.Text("Legacy DryCycle Unity/Futile renderer: DISABLED");
            }

            ImGui.End();
        }
        catch (Exception error)
        {
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
            {
                log?.LogError(
                    "DryCycle RWImGUI probe draw failed. The callback remains registered for diagnostics. " + error);
            }
        }
    }
}
