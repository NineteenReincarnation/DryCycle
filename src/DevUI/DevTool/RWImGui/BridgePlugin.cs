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
        EditorInputRouter.SetFrontendAttached(true);
        DevToolFrontend.SetLogger(Logger);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void Update()
    {
        // Snapshot availability is not authoritative for lifetime: once H destroys vanilla
        // DevUI, DevUI.Update stops and the last presentation snapshot remains cached.
        bool sessionVisible = EditorPresentationHub.Current.Available && DevToolSessionHub.IsCurrentSessionLive;
        bool rebuiltVisible = sessionVisible &&
                              !EditorUiModeState.UseVanilla &&
                              !EditorUiModeState.OverlayHidden;

        // Vanilla mode and Escape-hidden mode release the RWImGUI context completely. This is
        // important for Warp Menu and other RWImGUI consumers: an invisible DryCycle editor
        // must not keep ownership of their mouse/keyboard input context.
        DevToolFrontend.SetVisibleFromMainThread(rebuiltVisible);
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
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
    private static int cjkFontLogged;
    private static int cjkFontMissingLogged;
    private static bool cjkFontResolved;
    private static ImFontPtr cjkFont;

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
        // RWImGui context Render lifecycle.
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
        if (!visible || !snapshot.Available || EditorUiModeState.UseVanilla || EditorUiModeState.OverlayHidden)
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
            io.FontGlobalScale = ResolveUiFontScale(pushedChineseFont);
            try
            {
                UiModeSwitch.Draw();
                DevToolOverlay.Draw(snapshot);
            }
            finally
            {
                io.FontGlobalScale = oldGlobalScale;
                if (pushedChineseFont) ImGui.PopFont();
            }

            EditorInputRouter.SetFrontendCapture(io.WantCaptureMouse, io.WantCaptureKeyboard, io.WantTextInput);
        }
        catch (Exception error)
        {
            EditorInputRouter.SetFrontendCapture(false, false, false);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DevTool RWImGui draw failed: " + error);
        }
    }

    private static unsafe float ResolveUiFontScale(bool chineseFontActive)
    {
        if (!DevToolUiSettings.IsChinese || !chineseFontActive || cjkFont.NativePtr == null || cjkFont.FontSize <= 0.01f)
            return 1f;

        // Chinese glyphs become noticeably harder to read at the small sizes commonly used by
        // developer overlays. Keep the visual size around 18 px while preserving the atlas font.
        float scale = DevToolUiSettings.PreferredChineseFontSize / cjkFont.FontSize;
        return Math.Max(1f, Math.Min(1.35f, scale));
    }

    private static unsafe bool TryPushChineseFont()
    {
        if (!DevToolUiSettings.IsChinese) return false;

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
        if (cjkFontResolved) return;
        cjkFontResolved = true;

        float targetSize = DevToolUiSettings.PreferredChineseFontSize;
        float bestDistance = float.MaxValue;
        int bestIndex = -1;

        ImVector<ImFontPtr> fonts = ImGui.GetIO().Fonts.Fonts;
        for (int i = 0; i < fonts.Size; i++)
        {
            ImFontPtr candidate = fonts[i];
            if (candidate.NativePtr == null) continue;

            if (candidate.FindGlyphNoFallback((ushort)'中').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'文').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'房').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'间').NativePtr == null)
                continue;

            float distance = Math.Abs(candidate.FontSize - targetSize);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = i;
            cjkFont = candidate;
        }

        if (cjkFont.NativePtr != null)
        {
            if (Interlocked.Exchange(ref cjkFontLogged, 1) == 0)
                log?.LogInfo(
                    $"DryCycle DevTool selected CJK atlas font index={bestIndex}, size={cjkFont.FontSize:0.##}. " +
                    "Noto Sans SC / compatible Simplified Chinese atlas glyphs will be used.");
            return;
        }

        if (Interlocked.Exchange(ref cjkFontMissingLogged, 1) == 0)
            log?.LogWarning(
                "DryCycle DevTool could not find Simplified Chinese glyphs in RWImGUI's font atlas. " +
                "RWImGUI 1.12 normally includes NotoSansSC-Regular.ttf; falling back to English UI.");
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
