using System;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using ImGuiNET;
using RWIMGUI.API;

namespace DryCycle.DevUI.DevTool.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("rwimgui", BepInDependency.DependencyFlags.HardDependency)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui";
    public const string PluginName = "DryCycle DevTool RWImGui Frontend";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource log;
    private static bool callbackRegistered;

    private void OnEnable()
    {
        log = Logger;
        DevToolFrontend.SetLogger(Logger);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void Update()
    {
        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;
        DevToolFrontend.SetVisibleFromMainThread(snapshot.Available && snapshot.DevToolsActive);
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        DevToolFrontend.SetVisibleFromMainThread(false);
        TryUnregisterCallback();
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        TryRegisterCallback();
    }

    private static unsafe void TryRegisterCallback()
    {
        if (callbackRegistered) return;
        try
        {
            ImGUIAPI.AddAlwaysCallback(&DevToolFrontend.FrameCallback);
            callbackRegistered = true;
            log?.LogInfo("DryCycle DevTool RWImGui frontend registered.");
        }
        catch (Exception error)
        {
            log?.LogError("DryCycle DevTool RWImGui registration failed: " + error);
        }
    }

    private static unsafe void TryUnregisterCallback()
    {
        if (!callbackRegistered) return;
        try
        {
            ImGUIAPI.RemoveAlwaysCallback(&DevToolFrontend.FrameCallback);
            callbackRegistered = false;
        }
        catch (Exception error)
        {
            log?.LogWarning("DryCycle DevTool RWImGui callback removal failed: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static class DevToolFrontend
{
    private static readonly DevToolInputContext InputContext = new();
    private static ManualLogSource log;
    private static volatile bool visible;
    private static int contextBusyLogged;
    private static int drawFailureLogged;

    internal static void SetLogger(ManualLogSource value) => log = value;

    internal static void SetVisibleFromMainThread(bool value)
    {
        visible = value;
        if (!value)
        {
            ReleaseContext();
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        EnsureContext();
    }

    public static void FrameCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        // Keep the Always callback intentionally empty. Interactive drawing belongs to the
        // RWImGui context Render lifecycle, matching the stable AI Observatory integration.
    }

    private static void EnsureContext()
    {
        try
        {
            if (ReferenceEquals(ImGUIAPI.CurrentContext, InputContext)) return;
            if (ImGUIAPI.HasContext)
            {
                EditorInputRouter.SetFrontendCapture(false, false, false);
                if (Interlocked.Exchange(ref contextBusyLogged, 1) == 0)
                    log?.LogWarning("DevTool UI is waiting because another RWImGui context owns input.");
                return;
            }

            ImGUIAPI.SwitchContext(InputContext);
            Interlocked.Exchange(ref contextBusyLogged, 0);
        }
        catch (Exception error)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            log?.LogWarning("DevTool RWImGui context activation failed: " + error.Message);
        }
    }

    private static void ReleaseContext()
    {
        try
        {
            if (ReferenceEquals(ImGUIAPI.CurrentContext, InputContext))
                ImGUIAPI.SwitchContext(null);
        }
        catch (Exception error)
        {
            log?.LogWarning("DevTool RWImGui context release failed: " + error.Message);
        }
    }

    internal static void RenderFromContext(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;
        if (!visible || !snapshot.Available || !snapshot.DevToolsActive)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        try
        {
            DevToolOverlay.Draw(snapshot);
            ImGuiIOPtr io = ImGui.GetIO();
            EditorInputRouter.SetFrontendCapture(io.WantCaptureMouse, io.WantCaptureKeyboard, io.WantTextInput);
        }
        catch (Exception error)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DevTool RWImGui draw failed: " + error);
        }
    }
}

internal sealed class DevToolInputContext : IMGUIContext
{
    public override void Render(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        DevToolFrontend.RenderFromContext(ref idxgiSwapChain, ref syncInterval, ref flags);
    }

    public override bool BlockWMEvent() => false;

    public override void OnDestroyed()
    {
        EditorInputRouter.SetFrontendCapture(false, false, false);
    }
}
