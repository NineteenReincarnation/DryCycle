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
[BepInDependency("rwimgui", "1.12.0")]
public sealed class BridgePlugin : BaseUnityPlugin
{
    private readonly DevToolRetainedViewLifecycle retainedViewLifecycle = new();
    public const string PluginId = "DryCycle.DevTool.RWImGui";
    public const string PluginName = "DryCycle DevTool RWImGui Frontend";
    public const string PluginVersion = "0.1.1";

    private static ManualLogSource log;
    private static bool callbackRegistered;
    private static bool callbackRegistrationAllowed;
    private static float nextCallbackRegistrationAttemptAt;
    private static int callbackRegistrationFailureLogged;
    private bool bridgeEnabled;
    private bool sessionWasVisible;
    private bool sessionWasPaused;
    private bool focusTransitionActive;
    private bool applicationFocused;
    private bool creatureCatalogFallbackChecked;
    private bool ownsCreatureCatalogRuntime;
    private bool frontendInputAttached;
    private bool retainedFrontendActive;
    private bool mapFrontendWorkFaulted;
    private bool creatureCatalogWorkFaulted;
    private bool legacyVisualGuardFaulted;
    private bool retainedLifecycleFaulted;

    private void OnEnable()
    {
        log = Logger;
        bridgeEnabled = false;
        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin.OnEnable", "ENTER");

        try
        {
            sessionWasVisible = false;
            sessionWasPaused = false;
            focusTransitionActive = false;
            applicationFocused = UnityEngine.Application.isFocused;
            creatureCatalogFallbackChecked = false;
            ownsCreatureCatalogRuntime = false;
            frontendInputAttached = false;
            retainedFrontendActive = false;
            mapFrontendWorkFaulted = false;
            creatureCatalogWorkFaulted = false;
            legacyVisualGuardFaulted = false;
            retainedLifecycleFaulted = false;
            callbackRegistrationAllowed = false;
            nextCallbackRegistrationAttemptAt = 0f;
            Interlocked.Exchange(ref callbackRegistrationFailureLogged, 0);

            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/EditorUiModeState.SetOverlayHidden",
                () => EditorUiModeState.SetOverlayHidden(false));
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/EditorInputRouter.SetFrontendAttached",
                () => EditorInputRouter.SetFrontendAttached(false));
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/DevToolFrontend.SetLogger",
                () => DevToolFrontend.SetLogger(Logger));
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/DevToolFrontend.ResetNativeReadiness",
                DevToolFrontend.ResetNativeReadinessFromMainThread);
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/DevToolFrontend.SetApplicationFocused",
                () => DevToolFrontend.SetApplicationFocusedFromMainThread(applicationFocused));

            // Only lifecycle wiring belongs to the fatal shell transaction. Map helpers, inspectors,
            // caches and diagnostics are optional and are enabled independently below.
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/Hook RainWorld.Start",
                () => On.RainWorld.Start += RainWorld_Start);
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/Hook RainWorld.OnModsInit",
                () => On.RainWorld.OnModsInit += RainWorld_OnModsInit);
            global::DryCycle.StartupDiagnostics.Step(
                "BridgePlugin/Subscribe AfterModsInit",
                () => global::DryCycle.DryCycleLifecycleEvents.AfterModsInit += DryCycle_AfterModsInit);

            bridgeEnabled = true;
            global::DryCycle.StartupDiagnostics.Marker("BridgePlugin.OnEnable", "CORE-READY");
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure("BridgePlugin.OnEnable", error);
            Logger?.LogError(
                "DryCycle DevTool RWImGui core shell failed during OnEnable; Rain World startup will continue. " +
                error);
            ShutdownBridgeState();
            return;
        }

        TryEnableOptionalFrontendFeature(
            "WorldCreatureSpawnInspector",
            () => WorldCreatureSpawnInspector.Enable(Logger),
            WorldCreatureSpawnInspector.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldLineageInspector",
            () => WorldLineageInspector.Enable(Logger),
            WorldLineageInspector.Disable);
        TryEnableOptionalFrontendFeature(
            "ScopedScrollChrome",
            () => ScopedScrollChrome.Enable(Logger),
            ScopedScrollChrome.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapUpdateThrottle",
            () => WorldMapUpdateThrottle.Enable(Logger),
            WorldMapUpdateThrottle.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapBackgroundBudget",
            () => WorldMapBackgroundBudget.Enable(Logger),
            WorldMapBackgroundBudget.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapRasterReadbackFallback",
            () => WorldMapRasterReadbackFallback.Enable(Logger),
            WorldMapRasterReadbackFallback.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapExactShortcuts",
            () => WorldMapExactShortcuts.Enable(Logger),
            WorldMapExactShortcuts.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapLegacyVisualGuard",
            () => WorldMapLegacyVisualGuard.Enable(Logger),
            WorldMapLegacyVisualGuard.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapRetainedV2Runtime",
            () => WorldMapRetainedV2Runtime.Enable(Logger),
            WorldMapRetainedV2Runtime.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapPresentationCorrectness",
            () => WorldMapPresentationCorrectness.Enable(Logger),
            WorldMapPresentationCorrectness.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapRenderOrder",
            () => WorldMapRenderOrder.Enable(Logger),
            WorldMapRenderOrder.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapThumbnailVisibility",
            () => WorldMapThumbnailVisibility.Enable(Logger),
            WorldMapThumbnailVisibility.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldMapPipeLayers",
            () => WorldMapPipeLayers.Enable(Logger),
            WorldMapPipeLayers.Disable);
        TryEnableOptionalFrontendFeature(
            "WorldInspectorReadability",
            () => WorldInspectorReadability.Enable(Logger),
            WorldInspectorReadability.Disable);
        TryEnableOptionalFrontendFeature(
            "UserFacingCopyCleanup",
            () => DevToolUserFacingCopyCleanup.Enable(Logger),
            DevToolUserFacingCopyCleanup.Disable);
        TryEnableOptionalFrontendFeature(
            "PlayerMapFrontendLifecycle",
            () => PlayerMapFrontendLifecycle.Enable(Logger),
            PlayerMapFrontendLifecycle.Disable);
        TryEnableOptionalFrontendFeature(
            "RetainedViewLifecycle",
            retainedViewLifecycle.Enable,
            retainedViewLifecycle.Disable);

        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin.OnEnable", "EXIT");
    }

    private void TryEnableOptionalFrontendFeature(
        string name,
        Action enable,
        Action rollback)
    {
        try
        {
            enable?.Invoke();
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "BridgePlugin/Optional/" + name,
                error);
            Logger?.LogError(
                "DryCycle DevTool optional frontend feature '" + name +
                "' failed; the New UI shell remains enabled. " + error);

            if (rollback != null)
                SafeFrontendCleanup("optional " + name, rollback);
        }
    }

    private void LateUpdate()
    {
        if (!bridgeEnabled) return;

        if (!legacyVisualGuardFaulted)
        {
            try
            {
                WorldMapLegacyVisualGuard.LateUpdate();
            }
            catch (Exception error)
            {
                legacyVisualGuardFaulted = true;
                Logger?.LogError(
                    "WorldMapLegacyVisualGuard failed and has been disabled for this frontend lifetime. " +
                    error);
            }
        }

        bool shouldRunRetainedFrontend =
            DevToolFrontend.NativeBackendReady &&
            !EditorUiModeState.UseVanilla &&
            DevToolSessionHub.IsCurrentSessionLive;

        if (shouldRunRetainedFrontend && !retainedLifecycleFaulted)
        {
            try
            {
                retainedViewLifecycle.LateUpdate();
                retainedFrontendActive = true;
            }
            catch (Exception error)
            {
                retainedLifecycleFaulted = true;
                retainedFrontendActive = false;
                Logger?.LogError(
                    "DevTool retained view lifecycle failed; core New UI rendering remains enabled. " +
                    error);
            }
        }
        else if (retainedFrontendActive)
        {
            try
            {
                DevToolPageViewRegistry.DeactivateActive();
            }
            catch (Exception error)
            {
                Logger?.LogWarning(
                    "DevTool retained page deactivation failed: " + error);
            }
            retainedFrontendActive = false;
        }
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

        // Callback registration is retryable. A single timing/API failure during OnModsInit must not
        // leave NativeBackendReady=false for the rest of the process.
        TryRegisterCallback();

        bool nativeFrontendReady = DevToolFrontend.NativeBackendReady;
        if (frontendInputAttached != nativeFrontendReady)
        {
            frontendInputAttached = nativeFrontendReady;
            EditorInputRouter.SetFrontendAttached(nativeFrontendReady);
        }

        bool sessionLiveNow = DevToolSessionHub.IsCurrentSessionLive;

        // Backend readiness is transient; the user's presentation choice is not. While RWImGui is
        // unavailable FrontendAttached=false already keeps vanilla DevUI visible and rebuilt
        // snapshot production asleep. Do not permanently rewrite UseVanilla just because Present
        // was late for one frame. When the heartbeat arrives, New UI can activate automatically.
        EditorSession session = DevToolSessionHub.Current;
        RainWorldGame game = session?.Owner?.game;
        bool rawSessionVisible = DevToolSessionHub.IsCurrentSessionLive;
        bool definitelyClosed = IsSessionDefinitelyClosed(session, game);

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
        bool sessionReturned = sessionVisible && !sessionWasVisible;

        if (sessionReturned)
        {
            mapFrontendWorkFaulted = false;
            creatureCatalogWorkFaulted = false;
        }

        if (EditorUiModeState.OverlayHidden && sessionVisible)
        {
            bool resumedFromPause = sessionWasPaused && !sessionPaused;
            if (sessionReturned || resumedFromPause)
                EditorUiModeState.SetOverlayHidden(false);
        }

        sessionWasVisible = sessionVisible;
        sessionWasPaused = sessionPaused;

        bool feedbackHold = EditorShortcutFeedback.PresentationHoldActive;
        bool frontendVisible =
            sessionVisible &&
            (!EditorUiModeState.OverlayHidden || feedbackHold);

        // Core shell/context activation happens before Map caches, image pumps or catalog work.
        // Optional page code can no longer prevent this call from being reached.
        DevToolFrontend.SetVisibleFromMainThread(frontendVisible);

        if (!sessionVisible)
        {
            mapFrontendWorkFaulted = false;
            creatureCatalogWorkFaulted = false;
            return;
        }

        bool rebuiltFrontendWorkActive =
            nativeFrontendReady &&
            !EditorUiModeState.UseVanilla &&
            sessionLiveNow;
        if (!rebuiltFrontendWorkActive)
            return;

        PumpOptionalFrontendWork();
    }

    private void PumpOptionalFrontendWork()
    {
        EditorSession mapSession = DevToolRuntime.ActiveSession;
        if (mapSession?.ToolMode == EditorToolMode.Map && !mapFrontendWorkFaulted)
        {
            try
            {
                if (WorldMapBackgroundBudget.AllowSourceRecovery())
                    MapRoomGeometryPresentationHub.RecoverMissingSources(mapSession);

                MapRoomGeometryPresentationHub.Prime(mapSession);
                int selectedRoomIndex =
                    MapEditorStateHub.Get(mapSession)?.SelectedRoomIndex ?? -1;
                WorldMapShortcutPresentation.Prime(mapSession, selectedRoomIndex);
                WorldMapExactShortcuts.UpdateMainThread(mapSession, selectedRoomIndex);
                WorldMapRetainedV2Runtime.UpdateMainThread();
                CartographyCanvasImages.UpdateMainThread();
            }
            catch (Exception error)
            {
                mapFrontendWorkFaulted = true;
                Logger?.LogError(
                    "DevTool optional Map frontend pump failed and is disabled until DevTools is reopened. " +
                    error);
            }
        }

        if (!creatureCatalogWorkFaulted)
        {
            try
            {
                EnsureCreatureCatalogRuntime();
                if (ownsCreatureCatalogRuntime)
                    WorldCreatureCatalogPicker.PumpMainThread();
            }
            catch (Exception error)
            {
                creatureCatalogWorkFaulted = true;
                Logger?.LogError(
                    "DevTool optional creature catalog pump failed and is disabled until DevTools is reopened. " +
                    error);
            }
        }
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
        SafeFrontendCleanup("RainWorld.OnModsInit hook", () => On.RainWorld.OnModsInit -= RainWorld_OnModsInit);
        SafeFrontendCleanup(
            "AfterModsInit lifecycle subscription",
            () => global::DryCycle.DryCycleLifecycleEvents.AfterModsInit -= DryCycle_AfterModsInit);
        SafeFrontendCleanup("frontend visibility", () => DevToolFrontend.SetVisibleFromMainThread(false));
        SafeFrontendCleanup(
            "frontend focus state",
            () => DevToolFrontend.SetApplicationFocusedFromMainThread(true));
        SafeFrontendCleanup("overlay state", () => EditorUiModeState.SetOverlayHidden(false));
        sessionWasVisible = false;
        sessionWasPaused = false;
        focusTransitionActive = false;
        applicationFocused = true;
        mapFrontendWorkFaulted = false;
        creatureCatalogWorkFaulted = false;
        legacyVisualGuardFaulted = false;
        retainedLifecycleFaulted = false;
        callbackRegistrationAllowed = false;
        nextCallbackRegistrationAttemptAt = 0f;
        Interlocked.Exchange(ref callbackRegistrationFailureLogged, 0);
        SafeFrontendCleanup("frontend input attachment", () => EditorInputRouter.SetFrontendAttached(false));
        SafeFrontendCleanup("RWImGui callback", TryUnregisterCallback);

        if (ownsCreatureCatalogRuntime)
            SafeFrontendCleanup("creature catalog fallback", WorldCreatureCatalogPicker.Shutdown);
        ownsCreatureCatalogRuntime = false;
        creatureCatalogFallbackChecked = false;
        frontendInputAttached = false;
        retainedFrontendActive = false;
        SafeFrontendCleanup("world lineage inspector", WorldLineageInspector.Disable);
        SafeFrontendCleanup("scoped scroll chrome", ScopedScrollChrome.Disable);
        SafeFrontendCleanup("world map legacy visual guard", WorldMapLegacyVisualGuard.Disable);
        SafeFrontendCleanup("world map exact shortcuts", WorldMapExactShortcuts.Disable);
        SafeFrontendCleanup("world map raster readback fallback", WorldMapRasterReadbackFallback.Disable);
        SafeFrontendCleanup("world map background budget", WorldMapBackgroundBudget.Disable);
        SafeFrontendCleanup(
            "world map persistent cache flush",
            MapRoomGeometryPresentationHub.FlushPersistentCache);
        SafeFrontendCleanup("world map update throttle", WorldMapUpdateThrottle.Disable);
        SafeFrontendCleanup("world map retained v2 runtime", WorldMapRetainedV2Runtime.Disable);
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

        // RWImGUI installs native bindings here. DryCycle deliberately does not touch the shared
        // ImGui context/font atlas during game startup. DevTool fonts belong to DevToolInputContext
        // and are installed only when that dedicated consumer context is first activated.
        global::DryCycle.StartupDiagnostics.Step(
            "BridgePlugin/RainWorld.Start/orig",
            () => orig(self));

        // Do not touch ImGUIAPI context/font state from RainWorld.Start.
        // RWImGUI can return from its Start hook even when native D3D11 initialization failed.
        // Calling HasContext/SwitchContext in that state can cross an uninitialized native binding
        // and terminate the whole process before a managed exception can be logged.
        //
        // Font registration therefore remains deferred to the normal consumer-context lifecycle,
        // where RWImGUI has already established a usable context. Game startup must always win over
        // optional DevTool font prewarming.
        global::DryCycle.StartupDiagnostics.Marker("BridgePlugin/RainWorld.Start", "EXIT");
    }

    private static void RainWorld_OnModsInit(
        On.RainWorld.orig_OnModsInit orig,
        RainWorld self)
    {
        orig(self);
        AllowCallbackRegistration("RainWorld.OnModsInit");
    }

    private static void DryCycle_AfterModsInit(RainWorld self)
    {
        AllowCallbackRegistration("DryCycle.AfterModsInit");
    }

    private static void AllowCallbackRegistration(string source)
    {
        callbackRegistrationAllowed = true;
        nextCallbackRegistrationAttemptAt = 0f;
        log?.LogInfo(
            "DryCycle DevTool RWImGui callback registration enabled by " + source + ".");
    }

    private static unsafe void TryRegisterCallback()
    {
        if (callbackRegistered || !callbackRegistrationAllowed)
            return;

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < nextCallbackRegistrationAttemptAt)
            return;

        try
        {
            ImGUIAPI.AddAlwaysCallback(&DevToolFrontend.FrameCallback);
            callbackRegistered = true;
            nextCallbackRegistrationAttemptAt = 0f;
            Interlocked.Exchange(ref callbackRegistrationFailureLogged, 0);
            log?.LogInfo(
                "DryCycle DevTool RWImGui frontend callback registered. " +
                "Waiting for the first healthy Present heartbeat.");
        }
        catch (Exception error)
        {
            nextCallbackRegistrationAttemptAt = now + 1.0f;

            if (Interlocked.Exchange(ref callbackRegistrationFailureLogged, 1) == 0)
            {
                global::DryCycle.StartupDiagnostics.Failure(
                    "BridgePlugin/TryRegisterCallback",
                    error);
                log?.LogError(
                    "DryCycle DevTool RWImGui callback registration failed; retrying once per second. " +
                    error);
            }
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
    // Do not construct a consumer IMGUIContext merely because BepInEx loads the bridge assembly.
    // Context creation is deferred until the DevTool is actually visible and RWImGui reports that
    // no other context owns input. This keeps the entire BepInEx/RainWorld startup path free of
    // consumer context construction.
    private static DevToolInputContext inputContext;
    private static ManualLogSource log;
    private static volatile bool visible;
    private static volatile bool applicationFocused = true;
    private static int contextBusyLogged;
    private static int drawFailureLogged;
    private static int firstPresentLogged;
    private static int contextAttachedLogged;
    private static int firstRenderLogged;
    private static int contextRebuildRequested;
    private static int textureFrameFailureLogged;
    private static int cjkFontLogged;
    private static int cjkFontMissingLogged;
    private static int fontPushFailureLogged;
    private static int contextActivationFailureLogged;
    private static int rwimguiPresentObserved;
    private static int backendUnavailableLogged;
    private static bool contextAttached;
    private static float nextContextAttemptAt;
    private static ImFontPtr activeFont;
    private static string resolvedFontName = string.Empty;
    private static int resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
    private static int resolvedFontWeightVariantCount = 1;
    private static DevToolUiLanguage projectedFontLanguage = (DevToolUiLanguage)(-1);
    private static string projectedFontFamily = string.Empty;
    private static int projectedFontWeight = int.MinValue;

    internal static bool NativeBackendReady => Volatile.Read(ref rwimguiPresentObserved) != 0;
    internal static string ResolvedFontName => resolvedFontName;
    internal static int ResolvedFontWeight => resolvedFontWeight;
    internal static int ResolvedFontWeightVariantCount => resolvedFontWeightVariantCount;

    internal static void ResetNativeReadinessFromMainThread()
    {
        contextAttached = false;
        nextContextAttemptAt = 0f;
        Interlocked.Exchange(ref rwimguiPresentObserved, 0);
        Interlocked.Exchange(ref backendUnavailableLogged, 0);
        Interlocked.Exchange(ref contextActivationFailureLogged, 0);
        Interlocked.Exchange(ref contextBusyLogged, 0);
        Interlocked.Exchange(ref drawFailureLogged, 0);
        Interlocked.Exchange(ref firstPresentLogged, 0);
        Interlocked.Exchange(ref contextAttachedLogged, 0);
        Interlocked.Exchange(ref firstRenderLogged, 0);
        Interlocked.Exchange(ref contextRebuildRequested, 0);
        Interlocked.Exchange(ref textureFrameFailureLogged, 0);
        ResetFontProjectionForNewContext();
        EditorInputRouter.SetFrontendCapture(false, false, false);
    }

    internal static void SetLogger(ManualLogSource value) => log = value;

    internal static void RequestContextRebuildForLanguageChange()
    {
        // Language buttons are clicked on RWImGui's render thread. Only publish intent here; the
        // Unity main thread performs SwitchContext(null) on the next Update.
        Interlocked.Exchange(ref contextRebuildRequested, 1);
    }

    internal static void SetApplicationFocusedFromMainThread(bool value)
    {
        applicationFocused = value;
        if (!value)
            EditorInputRouter.SetFrontendCapture(false, false, false);
    }

    internal static void SetVisibleFromMainThread(bool value)
    {
        bool wasVisible = visible;
        visible = value;

        if (Interlocked.Exchange(ref contextRebuildRequested, 0) != 0)
        {
            if (contextAttached)
                ReleaseContext();

            inputContext = null;
            ResetFontProjectionForNewContext();
        }

        if (!value)
        {
            if (contextAttached || wasVisible)
                ReleaseContext();

            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        if (contextAttached)
            return;

        // Do not call any native-backed RWImGUI context API unless its Present callback has run.
        // The user's crash log shows RWImGUI failing D3D11CreateDeviceAndSwapChain with
        // DXGI_ERROR_UNSUPPORTED while Unity is on "Microsoft Basic Render Driver". In that state
        // HasContext/SwitchContext can terminate the process rather than throwing managed errors.
        if (Volatile.Read(ref rwimguiPresentObserved) == 0)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);

            if (Interlocked.Exchange(ref backendUnavailableLogged, 1) == 0)
            {
                log?.LogWarning(
                    "DryCycle DevTool New UI is waiting for a healthy RWImGUI Present. " +
                    "No native context calls will be attempted. Unity graphics device='" +
                    UnityEngine.SystemInfo.graphicsDeviceName +
                    "'.");
            }

            return;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < nextContextAttemptAt)
            return;

        EnsureContext(now);
    }

    public static void FrameCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        // This is the only native-health signal we trust. AddAlwaysCallback reaching Present means
        // RWImGUI completed enough of its D3D11 path for consumer context APIs to be used safely.
        Interlocked.Exchange(ref rwimguiPresentObserved, 1);
        Interlocked.Exchange(ref backendUnavailableLogged, 0);

        if (Interlocked.Exchange(ref firstPresentLogged, 1) == 0)
        {
            log?.LogInfo(
                "DryCycle DevTool RWImGui first healthy Present observed. " +
                "Consumer context activation is now allowed.");
        }
    }

    private static void EnsureContext(float now)
    {
        try
        {
            // This path runs only when the rebuilt DevTool is actually visible, i.e. well after
            // RainWorld.Start. Do not gate it on the Always callback: some RWImGUI builds can keep
            // consumer contexts usable even when that callback is unavailable, and the old gate
            // permanently prevented New UI from opening.
            if (ImGUIAPI.HasContext)
            {
                EditorInputRouter.SetFrontendCapture(false, false, false);

                nextContextAttemptAt = now + 0.25f;

                if (Interlocked.Exchange(ref contextBusyLogged, 1) == 0)
                {
                    log?.LogWarning(
                        "DevTool UI is waiting because another RWImGui context owns input.");
                }

                return;
            }

            DevToolInputContext context = inputContext;
            if (context == null)
            {
                context = new DevToolInputContext();
                inputContext = context;
            }

            ImGUIAPI.SwitchContext(context);
            contextAttached = true;
            nextContextAttemptAt = 0f;

            // English is the startup-safe default and must not touch the native font atlas.
            // Switching to Chinese requests a fresh context on the main thread; only that new
            // context registers the single fixed HarmonyOS face before its first Render.
            if (DevToolUiSettings.IsChinese &&
                !DevToolFontCatalog.RegistrationAttempted)
            {
                DevToolFontCatalog.TryRegisterLocalFonts(log);
            }

            Interlocked.Exchange(ref contextBusyLogged, 0);
            Interlocked.Exchange(ref contextActivationFailureLogged, 0);

            if (Interlocked.Exchange(ref contextAttachedLogged, 1) == 0)
            {
                log?.LogInfo(
                    "DryCycle DevTool RWImGui consumer context activated. language=" +
                    DevToolUiSettings.Language +
                    ", localFontRegistration=" +
                    DevToolFontCatalog.RegistrationAttempted +
                    ".");
            }
        }
        catch (Exception error)
        {
            // SwitchContext can fail after RWImGui has already changed part of its ownership state.
            // Best-effort release prevents the next retry from being stuck forever behind HasContext.
            try
            {
                ImGUIAPI.SwitchContext(null);
            }
            catch (Exception cleanupError)
            {
                log?.LogWarning(
                    "DevTool RWImGui context activation cleanup also failed: " +
                    cleanupError);
            }

            contextAttached = false;
            nextContextAttemptAt = now + 1.0f;
            inputContext = null;
            ResetFontProjectionForNewContext();
            EditorInputRouter.SetFrontendCapture(false, false, false);

            if (Interlocked.Exchange(ref contextActivationFailureLogged, 1) == 0)
            {
                log?.LogError(
                    "DevTool RWImGui context activation failed; retrying at low frequency. " +
                    error);
            }
        }
    }

    private static void ReleaseContext()
    {
        if (!contextAttached)
            return;

        try
        {
            ImGUIAPI.SwitchContext(null);
        }
        catch (Exception error)
        {
            log?.LogWarning("DevTool RWImGui context release failed: " + error);
        }
        finally
        {
            contextAttached = false;
            nextContextAttemptAt = 0f;
            EditorInputRouter.SetFrontendCapture(false, false, false);
        }
    }

    internal static void NotifyContextDestroyedFromRwImGui()
    {
        contextAttached = false;
        nextContextAttemptAt = 0f;
        inputContext = null;
        ResetFontProjectionForNewContext();
        EditorInputRouter.SetFrontendCapture(false, false, false);
    }

    private static void ResetFontProjectionForNewContext()
    {
        activeFont = default;
        resolvedFontName = string.Empty;
        resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
        resolvedFontWeightVariantCount = 1;
        projectedFontLanguage = (DevToolUiLanguage)(-1);
        projectedFontFamily = string.Empty;
        projectedFontWeight = int.MinValue;
        Interlocked.Exchange(ref cjkFontLogged, 0);
        Interlocked.Exchange(ref cjkFontMissingLogged, 0);
        Interlocked.Exchange(ref fontPushFailureLogged, 0);
        Interlocked.Exchange(ref contextAttachedLogged, 0);
        Interlocked.Exchange(ref firstRenderLogged, 0);
        Interlocked.Exchange(ref drawFailureLogged, 0);
        DevToolFontCatalog.ResetConsumerContextState();
        DevToolGlyphs.ResetCache();
    }

    internal static void RenderFromContext(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        try
        {
            WorldMapTextureFrame.Begin();
            Interlocked.Exchange(ref textureFrameFailureLogged, 0);
        }
        catch (Exception error)
        {
            // Texture leases are a World Map implementation detail. A bad/stale COM texture handle
            // must not escape IMGUIContext.Render and take the entire editor shell with it.
            if (Interlocked.Exchange(ref textureFrameFailureLogged, 1) == 0)
            {
                log?.LogError(
                    "DevTool World Map texture-frame cleanup failed; continuing to render the UI shell. " +
                    error);
            }
        }

        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;

        if (Interlocked.Exchange(ref firstRenderLogged, 1) == 0)
        {
            log?.LogInfo(
                "DryCycle DevTool RWImGui context Render reached Present. visible=" +
                visible +
                ", snapshotAvailable=" +
                snapshot.Available +
                ", vanilla=" +
                EditorUiModeState.UseVanilla +
                ".");
        }

        // While the OS owns focus, keep the consumer context alive but submit no ImGui windows.
        // This prevents temporary fullscreen/display-size changes and stale mouse input from moving,
        // snapping, resizing or recreating any DevTool window during Alt+Tab.
        if (!applicationFocused)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        // A shortcut may hide the normal editor UI (Esc) while its top-center acknowledgement is
        // still animating. Keep a feedback-only frame alive for that brief hold without reopening
        // any editor windows or requiring a presentation snapshot.
        bool feedbackOnly =
            EditorUiModeState.OverlayHidden &&
            EditorShortcutFeedback.PresentationHoldActive;
        if (!visible)
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

            if (!feedbackOnly)
                FloatingWindowSnap.BeginFrame(frameContext);

            bool pushedActiveFont = TryPushActiveFont();
            float oldGlobalScale = io.FontGlobalScale;
            float baseFontSize = ResolveActiveBaseFontSize();
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
                if (!feedbackOnly)
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
                        // Draw the core editor chrome before secondary typography/settings windows.
                        // A Font window bug must not prevent the main Control Center/activity shell
                        // from being submitted in the same frame.
                        using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.Overlay))
                            DevToolOverlay.Draw(snapshot, frameContext);
                        using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.FontSettings))
                            FontSettingsWindow.Draw(frameContext.DisplaySize);
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
                }

                // Every keyboard shortcut uses one page-independent feedback surface. It is drawn
                // last so global commands and view-local commands have the same top-center animation.
                using (DevToolFrontendPerformanceMonitor.Measure(DevToolFrontendPerformanceMetric.ActionToast))
                    GlobalShortcutFeedbackOverlay.Draw(frameContext.DisplaySize);

                if (!feedbackOnly)
                    FloatingWindowSnap.EndFrame();
            }
            finally
            {
                ImGui.PopStyleColor(3);
                ImGui.PopStyleVar(pushedLayoutVars + 1);
                io.FontGlobalScale = oldGlobalScale;
                if (pushedActiveFont) ImGui.PopFont();
            }

            // A marquee can begin over empty room pixels, where ImGui itself would normally report
            // WantCaptureMouse=false. Reserve the mouse explicitly so selection never clicks or
            // drags a vanilla world-space DevInterface handle underneath the layout gesture.
            if (feedbackOnly)
            {
                EditorInputRouter.SetFrontendCapture(false, false, false);
            }
            else
            {
                EditorInputRouter.SetFrontendCapture(
                    io.WantCaptureMouse ||
                    ShortcutWindow.OwnsMouse ||
                    FloatingWindowSnap.OwnsMouse ||
                    NativeObjectGizmoView.OwnsMouse ||
                    NativeSpatialGizmoView.OwnsMouse ||
                    ObjectMarqueeSelectionView.OwnsMouse,
                    io.WantCaptureKeyboard,
                    io.WantTextInput);
            }
        }
        catch (Exception error)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DevTool RWImGui draw failed: " + error);
        }
    }

    private static unsafe float ResolveActiveBaseFontSize()
    {
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

    internal static unsafe bool TryPushRegisteredFont(ImFontPtr font, string usage)
    {
        if (font.NativePtr == null)
            return false;

        try
        {
            ImGui.PushFont(font);
            return true;
        }
        catch (Exception error)
        {
            if (Interlocked.Exchange(ref fontPushFailureLogged, 1) == 0)
            {
                log?.LogWarning(
                    "DryCycle DevTool could not PushFont for " +
                    (string.IsNullOrWhiteSpace(usage) ? "a local font" : usage) +
                    "; falling back to the RWImGui context default font: " +
                    error.Message);
            }

            return false;
        }
    }

    private static unsafe bool TryPushActiveFont()
    {
        DevToolUiLanguage language = DevToolUiSettings.Language;
        int preferredWeight = DevToolUiSettings.FontWeight;

        // English is the startup-safe path: use the font that RWImGui created for this exact
        // consumer context. Avoid carrying a local ImFontPtr when no CJK coverage is required.
        if (language == DevToolUiLanguage.English)
        {
            if (projectedFontLanguage != language ||
                projectedFontWeight != preferredWeight ||
                projectedFontFamily.Length != 0)
            {
                projectedFontLanguage = language;
                projectedFontFamily = string.Empty;
                projectedFontWeight = preferredWeight;
                activeFont = default;
                resolvedFontName = "Default";
                resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
                resolvedFontWeightVariantCount = 1;
            }

            return false;
        }

        string family = DevToolUiSettings.ChineseFontFamily;

        if (projectedFontLanguage != language ||
            projectedFontWeight != preferredWeight ||
            !string.Equals(
                projectedFontFamily,
                family,
                StringComparison.OrdinalIgnoreCase))
        {
            projectedFontLanguage = language;
            projectedFontFamily = family ?? string.Empty;
            projectedFontWeight = preferredWeight;
            activeFont = default;

            bool requireChinese =
                language == DevToolUiLanguage.Chinese;
            if (DevToolFontCatalog.TryResolveRegisteredFace(
                    family,
                    preferredWeight,
                    requireChinese,
                    out ImFontPtr resolved,
                    out string name,
                    out int actualWeight,
                    out int variantCount))
            {
                activeFont = resolved;
                resolvedFontName = name;
                resolvedFontWeight = actualWeight;
                resolvedFontWeightVariantCount =
                    Math.Max(1, variantCount);

                if (requireChinese)
                {
                    string resolvedFamily =
                        DevToolFontCatalog.FamilyFromName(name);
                    if (!string.IsNullOrWhiteSpace(resolvedFamily) &&
                        !string.Equals(
                            resolvedFamily,
                            DevToolUiSettings.ChineseFontFamily,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        DevToolUiSettings.ChineseFontFamily =
                            resolvedFamily;
                    }
                }

                if (requireChinese &&
                    Interlocked.Exchange(ref cjkFontLogged, 1) == 0)
                {
                    log?.LogInfo(
                        "DryCycle DevTool selected local CJK font: " +
                        name +
                        " | weight " +
                        actualWeight +
                        ".");
                }
            }
            else
            {
                resolvedFontName = "Default";
                resolvedFontWeight =
                    language == DevToolUiLanguage.Chinese
                        ? DevToolUiSettings.DefaultChineseFontWeight
                        : DevToolUiSettings.DefaultFontWeight;
                resolvedFontWeightVariantCount = 1;

                if (requireChinese &&
                    Interlocked.Exchange(
                        ref cjkFontMissingLogged,
                        1) == 0)
                {
                    log?.LogWarning(
                        "DryCycle DevTool could not resolve a local Simplified Chinese font from " +
                        DevToolFontCatalog.FontDirectory +
                        ". The UI remains active with the context default font.");
                }
            }
        }

        if (activeFont.NativePtr == null)
            return false;

        if (TryPushRegisteredFont(activeFont, "active UI font"))
            return true;

        activeFont = default;
        resolvedFontName = "Default";
        resolvedFontWeight = DevToolUiSettings.DefaultFontWeight;
        resolvedFontWeightVariantCount = 1;
        DevToolGlyphs.ResetCache();
        return false;
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
        DevToolFrontend.NotifyContextDestroyedFromRwImGui();
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
