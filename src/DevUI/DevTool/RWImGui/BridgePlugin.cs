using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
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
    private static bool nativeImGuiReady;
    private bool sessionWasVisible;
    private bool sessionWasPaused;
    private bool focusTransitionActive;
    private bool applicationFocused;

    private void OnEnable()
    {
        log = Logger;
        nativeImGuiReady = false;
        sessionWasVisible = false;
        sessionWasPaused = false;
        focusTransitionActive = false;
        applicationFocused = UnityEngine.Application.isFocused;
        EditorUiModeState.SetOverlayHidden(false);
        EditorInputRouter.SetFrontendAttached(true);
        DevToolFrontend.SetLogger(Logger);
        DevToolFrontend.SetApplicationFocusedFromMainThread(applicationFocused);

        // Never call ImGui.* from BepInEx OnEnable. RWImGui has been chainloaded at this point, but
        // its RainWorld.Start hook has not necessarily installed the native ImGui function pointers
        // yet. Calling GetFrameCount/GetIO here can jump through an uninitialised native binding and
        // terminate the process before BepInEx has a chance to print a managed exception.
        On.RainWorld.Start += RainWorld_Start;
        On.RainWorld.PreModsInit += RainWorld_PreModsInit;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
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
        // If RWImGui created the shared context after RainWorld.Start returned, this gives the font
        // registration one final safe pre-render opportunity. The helper refuses to touch a missing
        // context and DevToolFontCatalog refuses to mutate an atlas once the first frame has begun.
        if (nativeImGuiReady && !DevToolFontCatalog.RegistrationAttempted)
            TryRegisterLocalFontsDuringSafeStartup();

        // Snapshot availability is not authoritative for lifetime: once H destroys vanilla
        // DevUI, DevUI.Update stops and the last presentation snapshot remains cached.
        EditorSession session = DevToolSessionHub.Current;
        RainWorldGame game = session?.Owner?.game;
        bool rawSessionVisible = EditorPresentationHub.Current.Available && DevToolSessionHub.IsCurrentSessionLive;
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
        On.RainWorld.Start -= RainWorld_Start;
        On.RainWorld.PreModsInit -= RainWorld_PreModsInit;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        nativeImGuiReady = false;
        DevToolFrontend.SetVisibleFromMainThread(false);
        DevToolFrontend.SetApplicationFocusedFromMainThread(true);
        EditorUiModeState.SetOverlayHidden(false);
        sessionWasVisible = false;
        sessionWasPaused = false;
        focusTransitionActive = false;
        applicationFocused = true;
        EditorInputRouter.SetFrontendAttached(false);
        TryUnregisterCallback();
    }

    private static void RainWorld_Start(On.RainWorld.orig_Start orig, RainWorld self)
    {
        // Let RWImGui's own Start hook run first. Its native function-pointer bootstrap is the
        // boundary after which calling ImGui.NET is valid. Only then probe the shared font atlas.
        orig(self);
        nativeImGuiReady = true;
        TryRegisterLocalFontsDuringSafeStartup();
    }

    private static void RainWorld_PreModsInit(On.RainWorld.orig_PreModsInit orig, RainWorld self)
    {
        // Different RWImGui releases create/configure the shared atlas at slightly different
        // points. Probe both sides of PreModsInit, but the helper remains a no-op until Start has
        // established that ImGui.NET's native function pointers are safe to call.
        TryRegisterLocalFontsDuringSafeStartup();
        orig(self);
        TryRegisterLocalFontsDuringSafeStartup();
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        TryRegisterLocalFontsDuringSafeStartup();
        orig(self);
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

        if (!visible || !snapshot.Available || EditorUiModeState.OverlayHidden)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            return;
        }

        try
        {
            ImGuiIOPtr io = ImGui.GetIO();

            // Rain World's developer cursor remains authoritative. Do not toggle Unity's cursor
            // visibility and do not draw a second ImGui software cursor; both approaches fight
            // the vanilla DevUI and cause visible flicker. Tooltip placement is handled separately.
            io.MouseDrawCursor = false;

            FloatingWindowSnap.BeginFrame(io.DisplaySize);

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
                UiModeSwitch.Draw();

                if (!EditorUiModeState.UseVanilla)
                {
                    FontSettingsWindow.Draw(io.DisplaySize);
                    DevToolOverlay.Draw(snapshot);
                    SceneWorkspaceWindow.Draw(snapshot, io.DisplaySize);
                    ScenePlacementWindow.Draw(snapshot, io.DisplaySize);
                    ActionToastOverlay.Draw(snapshot, io.DisplaySize);
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
                io.WantCaptureMouse || FloatingWindowSnap.OwnsMouse,
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

        string preferredFamily = DevToolUiSettings.ChineseFontFamily;
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
            int weightDistance = Math.Abs(candidate.Weight - DevToolUiSettings.FontWeight);
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