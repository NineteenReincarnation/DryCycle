using System;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using ImGuiNET;
using RWIMGUI.API;
using UnityEngine;

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
        EditorInputRouter.SetFrontendAttached(true);
        DevToolFrontend.SetLogger(Logger);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void Update()
    {
        // The presentation snapshot is intentionally not authoritative for lifetime: once H
        // destroys vanilla DevUI, DevUI.Update stops and the last snapshot remains cached.
        // Poll the live RainWorldGame/DevUI relationship from the main thread instead so H,
        // O and process transitions all hide and release the frontend immediately.
        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;
        bool visible = snapshot.Available && DevToolSessionHub.IsCurrentSessionLive;
        DevToolFrontend.SetVisibleFromMainThread(visible);
        DevToolFrontend.SetCursorModeFromMainThread(visible, EditorUiModeState.UseVanilla);
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        if (DevToolSessionHub.IsCurrentSessionLive)
            Cursor.visible = true;
        DevToolFrontend.SetVisibleFromMainThread(false);
        EditorInputRouter.SetFrontendAttached(false);
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

    internal static void SetCursorModeFromMainThread(bool sessionVisible, bool vanillaMode)
    {
        if (!sessionVisible) return;

        // Rain World forces the operating-system cursor visible when H opens DevUI. RWImGui
        // already owns the editor pointer/hover presentation, so keeping both produces the
        // doubled cursor and obscures tooltips. Only hide the OS cursor while New UI is the
        // active presentation; Vanilla mode keeps Rain World's original behavior intact.
        Cursor.visible = vanillaMode;
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
        if (!visible || !snapshot.Available)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        try
        {
            // The switch is intentionally always available while DevTools are open. In
            // Vanilla mode it is the only RWImGui window left on screen, so returning to
            // the rebuilt editor never depends on an original DevInterface control.
            UiModeSwitch.Draw();
            if (!EditorUiModeState.UseVanilla)
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
