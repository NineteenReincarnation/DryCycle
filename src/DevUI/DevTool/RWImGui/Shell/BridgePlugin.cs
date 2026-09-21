using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using RWIMGUI.API;

namespace DryCycle.DevUI.DevTool.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("rwimgui", BepInDependency.DependencyFlags.HardDependency)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    private readonly DevToolRetainedViewLifecycle retainedViewLifecycle = new();
    public const string PluginId = "DryCycle.DevTool.RWImGui";
    public const string PluginName = "DryCycle DevTool RWImGui Frontend";
    public const string PluginVersion = "0.1.1";

    private static ManualLogSource log;
    private static bool callbackRegistered;
    private static bool nativeImGuiReady;
    private bool bridgeEnabled;
    private bool sessionWasVisible;
    private bool sessionWasPaused;
    private bool focusTransitionActive;
    private bool applicationFocused;
    private bool creatureCatalogFallbackChecked;
    private bool ownsCreatureCatalogRuntime;

    private void OnEnable()
    {
        log = Logger;
        bridgeEnabled = false;
        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin.OnEnable", "ENTER");

        try
        {
            nativeImGuiReady = false;
            sessionWasVisible = false;
            sessionWasPaused = false;
            focusTransitionActive = false;
            applicationFocused = UnityEngine.Application.isFocused;
            creatureCatalogFallbackChecked = false;
            ownsCreatureCatalogRuntime = false;
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/EditorUiModeState.SetOverlayHidden", () => EditorUiModeState.SetOverlayHidden(false));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/EditorInputRouter.SetFrontendAttached", () => EditorInputRouter.SetFrontendAttached(true));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/DevToolFrontend.SetLogger", () => DevToolFrontend.SetLogger(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/DevToolFrontend.SetApplicationFocused", () => DevToolFrontend.SetApplicationFocusedFromMainThread(applicationFocused));

            // The room inspector is composed by the bridge itself, so its authoring sections must share
            // the bridge lifetime as well. Dedicated helper plugins may also call these methods; both
            // Enable paths are idempotent.
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldCreatureSpawnInspector.Enable", () => WorldCreatureSpawnInspector.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldLineageInspector.Enable", () => WorldLineageInspector.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/ScopedScrollChrome.Enable", () => ScopedScrollChrome.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapBackgroundBudget.Enable", () => WorldMapBackgroundBudget.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapRetainedV2Phase0.Enable", () => WorldMapRetainedV2Phase0.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapRetainedV2Runtime.Enable", () => WorldMapRetainedV2Runtime.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapImGuiPresentationFallback.Enable", () => WorldMapImGuiPresentationFallback.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapPresentationCorrectness.Enable", () => WorldMapPresentationCorrectness.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapRenderOrder.Enable", () => WorldMapRenderOrder.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapThumbnailVisibility.Enable", () => WorldMapThumbnailVisibility.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldMapPipeLayers.Enable", () => WorldMapPipeLayers.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/WorldInspectorReadability.Enable", () => WorldInspectorReadability.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/UserFacingCopyCleanup.Enable", () => DevToolUserFacingCopyCleanup.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/PlayerMapFrontendLifecycle.Enable", () => PlayerMapFrontendLifecycle.Enable(Logger));
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/RetainedViewLifecycle.Enable", retainedViewLifecycle.Enable);

            // Never call ImGui.* from BepInEx OnEnable. RWImGui has been chainloaded at this point, but
            // its RainWorld.Start hook has not necessarily installed the native ImGui function pointers
            // yet. Calling GetFrameCount/GetIO here can jump through an uninitialised native binding and
            // terminate the process before BepInEx has a chance to print a managed exception.
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/Hook RainWorld.Start", () => On.RainWorld.Start += RainWorld_Start);
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/Subscribe BeforePreModsInit", () => global::DryCycle.DryCycleLifecycleEvents.BeforePreModsInit += DryCycle_BeforePreModsInit);
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/Subscribe AfterPreModsInit", () => global::DryCycle.DryCycleLifecycleEvents.AfterPreModsInit += DryCycle_AfterPreModsInit);
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/Subscribe BeforeModsInit", () => global::DryCycle.DryCycleLifecycleEvents.BeforeModsInit += DryCycle_BeforeModsInit);
            global::DryCycle.StartupDiagnostics.Step("BridgePlugin/Subscribe AfterModsInit", () => global::DryCycle.DryCycleLifecycleEvents.AfterModsInit += DryCycle_AfterModsInit);

            bridgeEnabled = true;
            global::DryCycle.StartupDiagnostics.Marker("BridgePlugin.OnEnable", "EXIT");
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure("BridgePlugin.OnEnable", error);
            Logger?.LogError(
                "DryCycle DevTool RWImGui frontend failed during OnEnable and has been isolated; Rain World startup will continue.");
            ShutdownBridgeState();
        }
    }

    private void LateUpdate()
    {
        if (!bridgeEnabled) return;
        WorldMapImGuiPresentationFallback.LateUpdate();
        WorldMapPresentationCorrectness.LateUpdate();
        retainedViewLifecycle.LateUpdate();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!bridgeEnabled) return;
        applicationFocused = hasFocus;

        // Unity may stop calling Update while the window is unfocused. Remember the transition in
        // the focus callback itself so the very first frame after Alt+Tab cannot mistake a transient
        // DevUI/game-state mismatch for an actual DevTools shutdown and release the ImGui context.
        if (!hasFocus && sessionWasVisible)
            focusTransitionActive = true;

        DevToolFrontend.SetApplicationFocusedFromMainThread(hasFocus);
    }

    private void Update()
    {
        if (!bridgeEnabled) return;

        WorldMapRetainedV2Phase0.UpdateMainThread();

        // The rebuilt World Map intentionally keeps vanilla MapPage drawing/updating quiescent.
        // Pump the bounded source-recovery backend here so missing RoomRepresentation MapTex
        // thumbnails are still generated incrementally without invoking vanilla MapObject.Update().
        if (WorldMapBackgroundBudget.AllowSourceRecovery())
            MapRoomGeometryPresentationHub.RecoverMissingSources(DevToolRuntime.ActiveSession);

        // V2 room resources capture only from the main thread after legacy MapTex/source recovery.
        // Draw never scans RoomPanel/MapPage.
        WorldMapRetainedV2Runtime.UpdateMainThread();

        EnsureCreatureCatalogRuntime();
        if (ownsCreatureCatalogRuntime)
            WorldCreatureCatalogPicker.PumpMainThread();

        // If RWImGui created the shared context after RainWorld.Start returned, this gives the font
        // registration one final safe pre-render opportunity. The helper refuses to touch a missing
        // context and DevToolFontCatalog refuses to mutate an atlas once the first frame has begun.
        if (nativeImGuiReady && !DevToolFontCatalog.RegistrationAttempted)
            TryRegisterLocalFontsDuringSafeStartup();

        // Snapshot availability is not authoritative for lifetime: once H destroys vanilla
        // DevUI, DevUI.Update stops and the last presentation snapshot remains cached.
        EditorSession session = DevToolSessionHub.Current;
        RainWorldGame game = session?.Owner?.game;
        // Session lifetime is authoritative here. Vanilla presentation intentionally does not
        // publish rebuilt presentation snapshots, so requiring Current.Available makes the tiny
        // Vanilla -> New UI return panel disappear after H closes and recreates DevUI.
        bool rawSessionVisible = DevToolSessionHub.IsCurrentSessionLive;
        bool definitelyClosed = IsSessionDefinitelyClosed(session, game);

        // Do not use a frame-count grace period here. In exclusive/fullscreen transitions Unity can
        // stop Update entirely while unfocused and Rain World may take an arbitrary number of frames
        // to restore game.devUI/room after focus returns. Keep the same consumer context until the
        // live signal comes back or we have positive evidence that DevTools really closed.
        if (rawSessionVisible)
            focusTransitionActive = false;
        else if (!applicationFocused && sessionWasVisible)
            focusTransitionActive = true;
        else if (focusTransitionActive && definitelyClosed)
            focusTransitionActive = false;

        bool preserveAcrossFocusTransition =
            sessionWasVisible && focusTransitionActive && !definitelyClosed;
        bool sessionVisible = rawSessionVisible || preserveAcrossFocusTransition;
        bool sessionPaused = sessionVisible && game?.GamePaused == true;

        // Escape must actually get the rebuilt overlay out of the way while Rain World's pause /
        // Warp Menu owns the screen. Do not reopen merely because Escape was released or because
        // RWImGui currently has no context: that was the old one-frame hide bug. Restore only when
        // the pause/menu closes or when DevTools itself is closed and opened again.
        if (EditorUiModeState.OverlayHidden && sessionVisible)
        {
            bool sessionReturned = !sessionWasVisible;
            bool resumedFromPause = sessionWasPaused && !sessionPaused;
            if (sessionReturned || resumedFromPause)
                EditorUiModeState.SetOverlayHidden(false);
        }

        sessionWasVisible = sessionVisible;
        sessionWasPaused = sessionPaused;

        // Keep the RWImGui frontend alive in Vanilla presentation mode so the tiny New UI /
        // Vanilla switch remains reachable. Alt+Tab never calls SetVisible(false): the context stays
        // attached and its ImGui window positions/sizes/open-state survive the focus transition.
        bool frontendVisible = sessionVisible && !EditorUiModeState.OverlayHidden;
        DevToolFrontend.SetVisibleFromMainThread(frontendVisible);
    }

    private void EnsureCreatureCatalogRuntime()
    {
        if (creatureCatalogFallbackChecked) return;
        creatureCatalogFallbackChecked = true;

        // WorldCreatureCatalogRuntimePlugin normally owns the expensive main-thread catalog pump.
        // Some BepInEx/loader combinations can leave auxiliary plugin types from the same assembly
        // undiscovered even while the primary BridgePlugin is alive. Detect that once all plugins
        // have had a chance to load and let the bridge own the runtime only as a fallback.
        if (Chainloader.PluginInfos.TryGetValue(WorldCreatureCatalogRuntimePlugin.PluginId, out var info) &&
            info?.Instance != null)
            return;

        WorldCreatureCatalogPicker.Initialize(Logger);
        ownsCreatureCatalogRuntime = true;
        Logger.LogWarning("Creature catalog runtime helper was not loaded; BridgePlugin activated the built-in fallback pump.");
    }

    private static bool IsSessionDefinitelyClosed(EditorSession session, RainWorldGame game)
    {
        if (session == null || game == null) return true;
        if (!game.processActive || !game.devToolsActive) return true;

        // currentMainLoop changing is an actual process transition. By contrast game.devUI == null,
        // a temporary owner mismatch, or owner.room == null can occur around Alt+Tab and therefore
        // must not be used as reasons to destroy the frontend context.
        if (game.manager?.currentMainLoop != null && !ReferenceEquals(game.manager.currentMainLoop, game))
            return true;

        return false;
    }

    private void OnDisable()
    {
        ShutdownBridgeState();
    }

    private void ShutdownBridgeState()
    {
        bridgeEnabled = false;

        SafeFrontendCleanup("RainWorld.Start hook", () => On.RainWorld.Start -= RainWorld_Start);
        SafeFrontendCleanup(
            "AfterModsInit lifecycle subscription",
            () => global::DryCycle.DryCycleLifecycleEvents.AfterModsInit -= DryCycle_AfterModsInit);
        SafeFrontendCleanup(
            "BeforeModsInit lifecycle subscription",
            () => global::DryCycle.DryCycleLifecycleEvents.BeforeModsInit -= DryCycle_BeforeModsInit);
        SafeFrontendCleanup(
            "AfterPreModsInit lifecycle subscription",
            () => global::DryCycle.DryCycleLifecycleEvents.AfterPreModsInit -= DryCycle_AfterPreModsInit);
        SafeFrontendCleanup(
            "BeforePreModsInit lifecycle subscription",
            () => global::DryCycle.DryCycleLifecycleEvents.BeforePreModsInit -= DryCycle_BeforePreModsInit);

        nativeImGuiReady = false;
        SafeFrontendCleanup("frontend visibility", () => DevToolFrontend.SetVisibleFromMainThread(false));
        SafeFrontendCleanup(
            "frontend focus state",
            () => DevToolFrontend.SetApplicationFocusedFromMainThread(true));
        SafeFrontendCleanup("overlay state", () => EditorUiModeState.SetOverlayHidden(false));
        sessionWasVisible = false;
        sessionWasPaused = false;
        focusTransitionActive = false;
        applicationFocused = true;
        SafeFrontendCleanup("frontend input attachment", () => EditorInputRouter.SetFrontendAttached(false));
        SafeFrontendCleanup("RWImGui callback", TryUnregisterCallback);

        if (ownsCreatureCatalogRuntime)
            SafeFrontendCleanup("creature catalog fallback", WorldCreatureCatalogPicker.Shutdown);
        ownsCreatureCatalogRuntime = false;
        creatureCatalogFallbackChecked = false;
        SafeFrontendCleanup("world lineage inspector", WorldLineageInspector.Disable);
        SafeFrontendCleanup("scoped scroll chrome", ScopedScrollChrome.Disable);
        SafeFrontendCleanup("world map background budget", WorldMapBackgroundBudget.Disable);
        SafeFrontendCleanup("world map retained v2 phase 0", WorldMapRetainedV2Phase0.Disable);
        SafeFrontendCleanup("world map retained v2 runtime", WorldMapRetainedV2Runtime.Disable);
        SafeFrontendCleanup("world map presentation fallback", WorldMapImGuiPresentationFallback.Disable);
        SafeFrontendCleanup("world map presentation correctness", WorldMapPresentationCorrectness.Disable);
        SafeFrontendCleanup("world map render order", WorldMapRenderOrder.Disable);
        SafeFrontendCleanup("world map thumbnail visibility", WorldMapThumbnailVisibility.Disable);
        SafeFrontendCleanup("world map pipe layers", WorldMapPipeLayers.Disable);
        SafeFrontendCleanup("player map frontend lifecycle", PlayerMapFrontendLifecycle.Disable);
        SafeFrontendCleanup("world inspector readability", WorldInspectorReadability.Disable);
        SafeFrontendCleanup("user-facing copy cleanup", DevToolUserFacingCopyCleanup.Disable);
        SafeFrontendCleanup("retained view lifecycle", retainedViewLifecycle.Disable);
        SafeFrontendCleanup("world map source recovery", MapRoomGeometryPresentationHub.ResetSourceRecovery);
        SafeFrontendCleanup("world creature inspector", WorldCreatureSpawnInspector.Disable);
    }

    private void SafeFrontendCleanup(string name, Action action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception cleanupError)
        {
            global::DryCycle.StartupDiagnostics.Failure("BridgePlugin.Cleanup/" + name, cleanupError);
            Logger?.LogWarning("DryCycle DevTool frontend cleanup failed for " + name + ": " + cleanupError);
        }
    }

    private static void RainWorld_Start(On.RainWorld.orig_Start orig, RainWorld self)
    {
        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin/RainWorld.Start", "ENTER");

        // Let RWImGui's own Start hook run first. Its native function-pointer bootstrap is the
        // boundary after which calling ImGui.NET is valid. Only then probe the shared font atlas.
        global::DryCycle.StartupDiagnostics.Step(
            "BridgePlugin/RainWorld.Start/orig",
            () => orig(self));
        nativeImGuiReady = true;
        global::DryCycle.StartupDiagnostics.Optional(
            "BridgePlugin/RainWorld.Start/RegisterLocalFonts",
            () => TryRegisterLocalFontsDuringSafeStartup());
        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin/RainWorld.Start", "EXIT");
    }

    private static void DryCycle_BeforePreModsInit(RainWorld self)
    {
        // Different RWImGui releases create/configure the shared atlas at slightly different
        // points. Preserve the old outer-hook timing without stacking another RainWorld hook.
        TryRegisterLocalFontsDuringSafeStartup();
    }

    private static void DryCycle_AfterPreModsInit(RainWorld self)
    {
        TryRegisterLocalFontsDuringSafeStartup();
    }

    private static void DryCycle_BeforeModsInit(RainWorld self)
    {
        TryRegisterLocalFontsDuringSafeStartup();
    }

    private static void DryCycle_AfterModsInit(RainWorld self)
    {
        TryRegisterLocalFontsDuringSafeStartup();
        TryRegisterCallback();
    }

    private static unsafe bool TryRegisterLocalFontsDuringSafeStartup()
    {
        if (!nativeImGuiReady || DevToolFontCatalog.RegistrationSucceeded) return false;

        try
        {
            // GetCurrentContext is the only native probe permitted before touching IO/Fonts. A null
            // context is normal during startup and must remain retryable instead of consuming the
            // catalog's one registration attempt.
            if (ImGui.GetCurrentContext() == IntPtr.Zero) return false;

            if (ImGui.GetFrameCount() != 0)
            {
                // The context is valid but the mutation window is already closed. Let the catalog
                // record the diagnostic once; it exits before touching a live atlas in this case.
                return DevToolFontCatalog.TryRegisterLocalFonts(log);
            }

            ImGuiIOPtr io = ImGui.GetIO();
            if (io.Fonts.NativePtr == null || io.Fonts.Locked || io.Fonts.TexID != 0UL)
                return false;

            return DevToolFontCatalog.TryRegisterLocalFonts(log);
        }
        catch (Exception error)
        {
            // A managed binding/context mismatch should not take the whole game down. Keep the
            // startup probe retryable and leave a concrete diagnostic in LogOutput.
            global::DryCycle.StartupDiagnostics.Failure(
                "BridgePlugin/TryRegisterLocalFontsDuringSafeStartup",
                error);
            log?.LogWarning("DryCycle DevTool deferred local font registration: " + error.Message);
            return false;
        }
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
            global::DryCycle.StartupDiagnostics.Failure(
                "BridgePlugin/TryRegisterCallback",
                error);
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
            global::DryCycle.StartupDiagnostics.Failure(
                "BridgePlugin/TryUnregisterCallback",
                error);
            log?.LogWarning("DryCycle DevTool RWImGui callback removal failed: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static class DevToolFrontend
{
    private sealed class FontCandidate
    {
        internal ImFontPtr Font;
        internal string Name;
        internal int Weight;
    }

    // Do not construct a consumer IMGUIContext merely because BepInEx loads the bridge assembly.
    // Context creation is deferred until the DevTool is actually visible and RWImGui reports that
    // no other context owns input. This keeps the entire BepInEx/RainWorld startup path free of
    // consumer context construction.
    private static DevToolInputContext inputContext;
    private static readonly List<FontCandidate> CjkFonts = new();
    private static ManualLogSource log;
    private static volatile bool visible;
    private static volatile bool applicationFocused = true;
    private static int contextBusyLogged;
    private static int drawFailureLogged;
    private static int cjkFontLogged;
    private static int cjkFontMissingLogged;
    private static bool cjkFontsScanned;
    private static ImFontPtr cjkFont;
    private static string resolvedFontName = string.Empty;
    private static int resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
    private static int resolvedFontWeightVariantCount = 1;
    private static bool cjkSelectionValid;
    private static string projectedCjkFamily = string.Empty;
    private static int projectedCjkWeight = int.MinValue;

    internal static string ResolvedFontName => resolvedFontName;
    internal static int ResolvedFontWeight => resolvedFontWeight;
    internal static int ResolvedFontWeightVariantCount => resolvedFontWeightVariantCount;

    internal static void SetLogger(ManualLogSource value) => log = value;

    internal static void SetApplicationFocusedFromMainThread(bool value)
    {
        applicationFocused = value;
        if (!value)
            EditorInputRouter.SetFrontendCapture(false, false, false);
    }

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
        // RWImGui context Render lifecycle.
    }

    private static void EnsureContext()
    {
        try
        {
            DevToolInputContext context = inputContext;
            if (context != null && ReferenceEquals(ImGUIAPI.CurrentContext, context))
                return;

            if (ImGUIAPI.HasContext)
            {
                EditorInputRouter.SetFrontendCapture(false, false, false);
                if (Interlocked.Exchange(ref contextBusyLogged, 1) == 0)
                    log?.LogWarning("DevTool UI is waiting because another RWImGui context owns input.");
                return;
            }

            if (context == null)
            {
                context = new DevToolInputContext();
                inputContext = context;
            }

            // Never mutate io.Fonts here. Once the renderer is alive, adding fonts invalidates the
            // already-built atlas and Dear ImGui will assert on the next NewFrame.
            ImGUIAPI.SwitchContext(context);
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
            DevToolInputContext context = inputContext;
            if (context != null && ReferenceEquals(ImGUIAPI.CurrentContext, context))
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

        // While the OS owns focus, keep the consumer context alive but submit no ImGui windows.
        // This prevents temporary fullscreen/display-size changes and stale mouse input from moving,
        // snapping, resizing or recreating any DevTool window during Alt+Tab.
        if (!applicationFocused)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        // Vanilla mode deliberately renders only the tiny return panel and therefore does not
        // require a rebuilt presentation snapshot. New UI surfaces still require a valid snapshot.
        bool needsPresentationSnapshot = !EditorUiModeState.UseVanilla;
        if (!visible || (needsPresentationSnapshot && !snapshot.Available) || EditorUiModeState.OverlayHidden)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        using DevToolFrontendPerformanceMonitor.Scope frontendFrameScope =
            DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.FrontendFrameTotal);

        try
        {
            ImGuiIOPtr io = ImGui.GetIO();
            DevToolUiFrameContext frameContext = new(io);

            // Rain World's developer cursor remains authoritative. Do not toggle Unity's cursor
            // visibility and do not draw a second ImGui software cursor; both approaches fight
            // the vanilla DevUI and cause visible flicker. Tooltip placement is handled separately.
            io.MouseDrawCursor = false;

            FloatingWindowSnap.BeginFrame(frameContext);

            bool pushedChineseFont = TryPushChineseFont();
            float oldGlobalScale = io.FontGlobalScale;
            float baseFontSize = ResolveActiveBaseFontSize(pushedChineseFont);
            float fontScale = ResolveUiFontScale(baseFontSize);
            float layoutScale = ResolveLayoutScale();

            // Font size and widget geometry are driven from the same absolute presentation scale.
            // This prevents large text from being laid out against stale small paddings/scrollbars.
            io.FontGlobalScale = fontScale;

            int pushedLayoutVars = PushScaledLayout(layoutScale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, DevToolUiSettings.WindowOutlineWidth);
            ImGui.PushStyleColor(ImGuiCol.Text, DevToolUiSettings.TextColor);
            ImGui.PushStyleColor(ImGuiCol.TextDisabled, DevToolUiSettings.DisabledTextColor);
            ImGui.PushStyleColor(ImGuiCol.Border, new System.Numerics.Vector4(0f, 0f, 0f, 1f));
            try
            {
                // The two-way mode switch is always visible while DevUI itself is alive. Vanilla
                // presentation hides rebuilt editor panels, not the control used to return.
                using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.UiModeSwitch))
                    UiModeSwitch.Draw();

                // Switching from Vanilla to New UI can happen inside UiModeSwitch.Draw(). If the
                // recreated session has not published its first snapshot yet, wait one frame rather
                // than feeding an unavailable snapshot into rebuilt editor windows.
                if (!EditorUiModeState.UseVanilla && snapshot.Available)
                {
                    using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.FontSettings))
                        FontSettingsWindow.Draw(frameContext.DisplaySize);
                    using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.Overlay))
                        DevToolOverlay.Draw(snapshot, frameContext);
                    // Gate structurally inactive Scene surfaces before entering their timing scopes.
                    // The windows retain their own defensive guards, but stable frames in Focus mode,
                    // unsupported tools, or Left placement should not pay measurement/call overhead.
                    bool sceneSurfaceSupported = !snapshot.FocusMode && ScenePlacementWindow.Supports(snapshot.ToolMode);
                    if (sceneSurfaceSupported && DevToolUiSettings.SceneInCenter)
                    {
                        using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.SceneWorkspace))
                            SceneWorkspaceWindow.Draw(snapshot, frameContext.DisplaySize);
                    }
                    if (sceneSurfaceSupported)
                    {
                        using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.ScenePlacement))
                            ScenePlacementWindow.Draw(snapshot, frameContext.DisplaySize);
                    }
                    using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.ActionToast))
                        ActionToastOverlay.Draw(snapshot, frameContext);
                }

                FloatingWindowSnap.EndFrame();
            }
            finally
            {
                ImGui.PopStyleColor(3);
                ImGui.PopStyleVar(pushedLayoutVars + 1);
                io.FontGlobalScale = oldGlobalScale;
                if (pushedChineseFont) ImGui.PopFont();
            }

            // A marquee can begin over empty room pixels, where ImGui itself would normally report
            // WantCaptureMouse=false. Reserve the mouse explicitly so selection never clicks or
            // drags a vanilla world-space DevInterface handle underneath the layout gesture.
            EditorInputRouter.SetFrontendCapture(
                io.WantCaptureMouse ||
                FloatingWindowSnap.OwnsMouse ||
                NativeObjectGizmoView.OwnsMouse ||
                NativeSpatialGizmoView.OwnsMouse ||
                ObjectMarqueeSelectionView.OwnsMouse,
                io.WantCaptureKeyboard,
                io.WantTextInput);
        }
        catch (Exception error)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DevTool RWImGui draw failed: " + error);
        }
    }

    private static unsafe float ResolveActiveBaseFontSize(bool pushedChineseFont)
    {
        if (pushedChineseFont && cjkFont.NativePtr != null && cjkFont.FontSize > 0.01f)
            return cjkFont.FontSize;

        ImFontPtr active = ImGui.GetFont();
        return active.NativePtr != null && active.FontSize > 0.01f ? active.FontSize : 13f;
    }

    private static float ResolveUiFontScale(float baseFontSize)
    {
        if (baseFontSize <= 0.01f) return 1f;
        float scale = DevToolUiSettings.FontSize / baseFontSize;
        return Math.Max(0.65f, Math.Min(4.0f, scale));
    }

    private static float ResolveLayoutScale()
    {
        float scale = DevToolUiSettings.FontSize / DevToolUiSettings.ReferenceFontSize;
        return Math.Max(0.70f, Math.Min(3.50f, scale));
    }

    private static int PushScaledLayout(float scale)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, style.WindowPadding * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, style.FramePadding * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, style.ItemSpacing * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, style.ItemInnerSpacing * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, style.IndentSpacing * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, style.ScrollbarSize * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, style.GrabMinSize * scale);
        return 7;
    }

    private static unsafe bool TryPushChineseFont()
    {
        if (!DevToolUiSettings.IsChinese)
        {
            resolvedFontName = "Default";
            resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
            resolvedFontWeightVariantCount = 1;
            return false;
        }

        ResolveCjkFont();
        if (cjkFont.NativePtr == null)
        {
            // Do not leave the editor full of missing-glyph boxes. English remains available as
            // a deterministic fallback on old RWImGUI installations without a CJK atlas font.
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);
            return false;
        }

        ImGui.PushFont(cjkFont);
        return true;
    }

    private static unsafe void ResolveCjkFont()
    {
        EnsureCjkFontsScanned();
        if (CjkFonts.Count == 0)
        {
            cjkFont = default;
            resolvedFontName = string.Empty;
            resolvedFontWeight = DevToolUiSettings.FontWeight;
            resolvedFontWeightVariantCount = 0;
            return;
        }

        string preferredFamily = DevToolUiSettings.ChineseFontFamily ?? string.Empty;
        int preferredWeight = DevToolUiSettings.FontWeight;
        if (cjkSelectionValid &&
            projectedCjkWeight == preferredWeight &&
            string.Equals(projectedCjkFamily, preferredFamily, StringComparison.OrdinalIgnoreCase))
            return;

        projectedCjkFamily = preferredFamily;
        projectedCjkWeight = preferredWeight;
        cjkSelectionValid = true;

        bool preferredAvailable = false;
        for (int i = 0; i < CjkFonts.Count; i++)
        {
            if (!DevToolFontCatalog.IsFamilyMatch(CjkFonts[i].Name, preferredFamily)) continue;
            preferredAvailable = true;
            break;
        }

        FontCandidate best = null;
        int bestWeightDistance = int.MaxValue;
        float bestSizeDistance = float.MaxValue;
        HashSet<int> weights = new();
        for (int i = 0; i < CjkFonts.Count; i++)
        {
            FontCandidate candidate = CjkFonts[i];
            bool familyMatch = DevToolFontCatalog.IsFamilyMatch(candidate.Name, preferredFamily);
            if (preferredAvailable && !familyMatch) continue;
            weights.Add(candidate.Weight);

            // Font size must never choose a different atlas font while the developer drags the
            // size slider. Family selection is applied first, weight picks the nearest family
            // variant, and baked size only breaks equal-weight ties against the stable reference
            // size. Visual size is handled exclusively by scale.
            int weightDistance = Math.Abs(candidate.Weight - preferredWeight);
            float sizeDistance = Math.Abs(candidate.Font.FontSize - DevToolUiSettings.ReferenceFontSize);
            if (weightDistance > bestWeightDistance ||
                (weightDistance == bestWeightDistance && sizeDistance >= bestSizeDistance))
                continue;

            bestWeightDistance = weightDistance;
            bestSizeDistance = sizeDistance;
            best = candidate;
        }

        if (best == null) return;
        cjkFont = best.Font;
        resolvedFontName = best.Name;
        resolvedFontWeight = best.Weight;
        resolvedFontWeightVariantCount = weights.Count;
    }

    private static unsafe void EnsureCjkFontsScanned()
    {
        if (cjkFontsScanned) return;
        cjkFontsScanned = true;
        CjkFonts.Clear();

        ImVector<ImFontPtr> fonts = ImGui.GetIO().Fonts.Fonts;
        for (int i = 0; i < fonts.Size; i++)
        {
            ImFontPtr candidate = fonts[i];
            if (candidate.NativePtr == null) continue;

            string name;
            int weight;
            if (!DevToolFontCatalog.TryGetRegisteredFace(candidate, out name, out weight))
            {
                name = ReadFontName(candidate, i);
                weight = InferFontWeight(name);
            }

            if (!DevToolFontCatalog.IsChineseUiSelectable(candidate, name))
                continue;

            CjkFonts.Add(new FontCandidate
            {
                Font = candidate,
                Name = name,
                Weight = weight
            });
        }

        if (CjkFonts.Count > 0)
        {
            if (Interlocked.Exchange(ref cjkFontLogged, 1) == 0)
                log?.LogInfo($"DryCycle DevTool discovered {CjkFonts.Count} Chinese-UI selectable ImGui atlas font(s).");
            return;
        }

        if (Interlocked.Exchange(ref cjkFontMissingLogged, 1) == 0)
            log?.LogWarning(
                "DryCycle DevTool could not find a selectable Simplified Chinese UI font in RWImGUI's font atlas; " +
                "falling back to English UI.");
    }

    private static unsafe string ReadFontName(ImFontPtr font, int index)
    {
        if (font.NativePtr == null || font.NativePtr->ConfigData == null)
            return "CJK Font #" + index;

        byte* name = font.NativePtr->ConfigData->Name;
        int length = 0;
        while (length < 80 && name[length] != 0) length++;
        if (length == 0) return "CJK Font #" + index;

        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = name[i];
        string value = Encoding.UTF8.GetString(bytes).Trim();
        return string.IsNullOrEmpty(value) ? "CJK Font #" + index : value;
    }

    private static int InferFontWeight(string name)
    {
        string value = (name ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        if (value.Contains("black") || value.Contains("heavy")) return 900;
        if (value.Contains("extrabold") || value.Contains("ultrabold")) return 800;
        if (value.Contains("semibold") || value.Contains("demibold")) return 600;
        if (value.Contains("bold")) return 700;
        if (value.Contains("medium")) return 500;
        if (value.Contains("extralight") || value.Contains("ultralight")) return 200;
        if (value.Contains("light")) return 300;
        if (value.Contains("thin")) return 100;
        return 400;
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

internal sealed class DevToolRetainedViewLifecycle
{
    private bool observedLiveSession;
    private RainWorldGame observedGame;

    internal void Enable() =>
        InitializeState();

    private void InitializeState()
    {
        observedLiveSession = false;
        observedGame = null;
    }

    internal void LateUpdate()
    {
        if (DevToolSessionHub.IsCurrentSessionLive)
        {
            EditorSession session = DevToolSessionHub.Current;
            observedGame = session?.Owner?.game ?? observedGame;
            observedLiveSession = true;

            // Page lifecycle follows the editor ToolMode even while RWImGui is temporarily hidden or
            // Vanilla UI is primary. The standalone debug workspace is different: it deliberately
            // replaces every normal page surface, so a normal page must stay deactivated for the
            // entire debug lifetime instead of being reactivated here from the stale ToolMode.
            if (DevToolOverlay.SuppressesSharedPageSurfaces)
                DevToolPageViewRegistry.DeactivateActive();
            else if (session != null)
                DevToolPageViewRegistry.SynchronizeActive(session.ToolMode);
            return;
        }

        if (!observedLiveSession)
            return;

        // A temporary owner/page mismatch can occur around Alt+Tab and fullscreen transitions.
        // Release only when the RainWorldGame itself gives positive evidence that the editor lifetime
        // ended. This mirrors BridgePlugin's frontend-lifetime rule without creating a core ->
        // frontend dependency.
        if (!IsDefinitelyClosed(observedGame))
            return;

        ReleaseRetainedState();
        observedLiveSession = false;
        observedGame = null;
    }

    internal void Disable() =>
        Shutdown();

    private void Shutdown()
    {
        ReleaseRetainedState();
        observedLiveSession = false;
        observedGame = null;
    }

    private static bool IsDefinitelyClosed(RainWorldGame game)
    {
        if (game == null) return true;
        if (!game.processActive || !game.devToolsActive) return true;

        return game.manager?.currentMainLoop != null &&
               !object.ReferenceEquals(game.manager.currentMainLoop, game);
    }

    private static void ReleaseRetainedState()
    {
        DevToolNumericWidgets.Reset();
        DevToolOverlay.ResetRetainedState();
        ScenePlacementWindow.ResetRetainedState();

        // Registered pages are the sole owners of page-specific retained projections. Resetting the
        // registry replaces the old frontend fan-out and guarantees newly registered pages cannot be
        // forgotten by this lifetime edge.
        DevToolPageViewRegistry.ResetAll();

        UniversalDevUiMirrorView.ResetRetainedState();
    }
}
