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
    public const string PluginVersion = "0.2.2";

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
        // Visibility and RWImGUI context ownership are changed only from Unity's main
        // thread. Do not switch RWImGUI contexts from inside the Present callback.
        ObservatoryFrontend.SetVisibleFromMainThread(AIDebuggerRuntime.Visible);
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        ObservatoryFrontend.Enabled = false;
        ObservatoryFrontend.SetVisibleFromMainThread(false);
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

            // AddAlwaysCallback is now only a Present heartbeat. The actual Observatory UI
            // is rendered by ObservatoryInputContext.Render(), which is RWImGUI's intended
            // context lifecycle and receives the same Win32 input stream.
            ImGUIAPI.AddAlwaysCallback(&ObservatoryFrontend.FrameCallback);

            callbackRegistered = true;
            ObservatoryFrontend.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(
                apiVersion?.ToString(),
                imguiVersion?.ToString());

            log?.LogInfo(
                "DryCycle RWImGUI AddAlwaysCallback registered directly through RWIMGUI.API.ImGUIAPI. " +
                $"api={apiVersion}, imgui={imguiVersion}, hasContext={ImGUIAPI.HasContext}. " +
                "Observatory drawing is owned by its RWImGUI context; press F7 directly.");
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
    // Keep BepInEx/RainWorld startup free of consumer context construction. The context is created
    // only when F7 actually makes the Observatory visible and RWImGui reports no competing owner.
    private static ObservatoryInputContext inputContext;

    private static ManualLogSource log;
    private static int firstPresentLogged;
    private static int firstVisibleDrawLogged;
    private static int drawFailureLogged;
    private static int inputContextLogged;
    private static int contextBusyLogged;
    private static int cjkFontLogged;
    private static int cjkFontMissingLogged;
    private static volatile bool visible;
    private static bool cjkFontResolved;
    private static ImFontPtr cjkFont;

    internal static bool Enabled { get; set; }
    internal static bool Visible => visible;

    internal static void SetLogger(ManualLogSource value) => log = value;

    internal static void SetVisibleFromMainThread(bool value)
    {
        visible = value;

        if (!Enabled || !value)
        {
            ReleaseInputContext();
            AIDebugPresentationHub.SetCaptureState(false, false);
            return;
        }

        EnsureInputContextFromMainThread();
    }

    // This callback deliberately performs no ImGui drawing and no context switching. The
    // 0.2.1 build changed CurrentContext from inside UserAlwaysRendererDispatcher and then
    // drew a second UI path in the same Present frame. The user's runtime log terminates
    // immediately after that transition with no managed exception, which is consistent
    // with a native ImGui/RWImGUI failure. Keep this callback as a minimal heartbeat only.
    public static void FrameCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled)
            return;

        AIDebugPresentationBridgeStatus.MarkPresentSeen();
        if (Interlocked.Exchange(ref firstPresentLogged, 1) == 0)
        {
            log?.LogInfo(
                $"DryCycle RWImGUI AddAlwaysCallback reached Present. swapChain=0x{idxgiSwapChain:X}, " +
                $"syncInterval={syncInterval}, flags={flags}.");
        }
    }

    private static void EnsureInputContextFromMainThread()
    {
        try
        {
            ObservatoryInputContext context = inputContext;
            if (context != null && ReferenceEquals(ImGUIAPI.CurrentContext, context)) return;

            // Never steal another RWImGUI consumer's active context. Once that context is
            // released, the next Unity Update will acquire ours automatically.
            if (ImGUIAPI.HasContext)
            {
                AIDebugPresentationHub.SetCaptureState(false, false);
                if (Interlocked.Exchange(ref contextBusyLogged, 1) == 0)
                    log?.LogWarning(
                        "DryCycle AI Observatory is visible, but another RWImGUI context currently owns input. " +
                        "Close that RWImGUI menu/window and the Observatory will acquire input on the next frame.");
                return;
            }

            if (context == null)
            {
                context = new ObservatoryInputContext();
                inputContext = context;
            }

            ImGUIAPI.SwitchContext(context);
            Interlocked.Exchange(ref contextBusyLogged, 0);
            if (Interlocked.Exchange(ref inputContextLogged, 1) == 0)
                log?.LogInfo(
                    "DryCycle RWImGUI Observatory input context activated on the Unity main thread. " +
                    "RWImGUI will call ObservatoryInputContext.Render during Present.");
        }
        catch (Exception error)
        {
            AIDebugPresentationHub.SetCaptureState(false, false);
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DryCycle RWImGUI Observatory context activation failed: " + error);
        }
    }

    internal static void ReleaseInputContext()
    {
        try
        {
            ObservatoryInputContext context = inputContext;
            if (context != null && ReferenceEquals(ImGUIAPI.CurrentContext, context))
                ImGUIAPI.SwitchContext(null);
        }
        catch (Exception error)
        {
            log?.LogWarning("DryCycle RWImGUI Observatory input context release failed: " + error.Message);
        }
    }

    internal static void RenderFromContext(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled || !Visible)
        {
            AIDebugPresentationHub.SetCaptureState(false, false);
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo(
                    "DryCycle RWImGUI Observatory context Render reached Present; drawing functional snapshot/command-queue UI.");

            AIDebugPresentationSnapshot snapshot = AIDebugPresentationHub.Current;
            bool pushedLanguageFont = TryPushLanguageFont(snapshot.Language);
            try
            {
                ObservatoryView.Draw(snapshot);
            }
            finally
            {
                if (pushedLanguageFont) ImGui.PopFont();
            }

            ImGuiIOPtr io = ImGui.GetIO();
            AIDebugPresentationHub.SetCaptureState(io.WantCaptureMouse, io.WantCaptureKeyboard);
        }
        catch (Exception error)
        {
            AIDebugPresentationHub.SetCaptureState(false, false);
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError(
                    "DryCycle RWImGUI Observatory context draw failed. The context remains installed for diagnostics. " + error);
        }
    }

    private static unsafe bool TryPushLanguageFont(AIDebugLanguage language)
    {
        if (language != AIDebugLanguage.Chinese) return false;

        ResolveCjkFont();
        if (cjkFont.NativePtr == null) return false;

        ImGui.PushFont(cjkFont);
        return true;
    }

    private static unsafe void ResolveCjkFont()
    {
        if (cjkFontResolved) return;
        cjkFontResolved = true;

        ImFontPtr current = ImGui.GetFont();
        float targetSize = current.NativePtr != null ? current.FontSize : 16f;
        float bestDistance = float.MaxValue;
        int bestIndex = -1;

        ImVector<ImFontPtr> fonts = ImGui.GetIO().Fonts.Fonts;
        for (int i = 0; i < fonts.Size; i++)
        {
            ImFontPtr candidate = fonts[i];
            if (candidate.NativePtr == null) continue;

            if (candidate.FindGlyphNoFallback((ushort)'中').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'文').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'实').NativePtr == null ||
                candidate.FindGlyphNoFallback((ushort)'体').NativePtr == null)
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
                    $"DryCycle RWImGUI CJK font selected from RWImGUI atlas: index={bestIndex}, " +
                    $"size={cjkFont.FontSize:0.##}. Chinese UI glyphs are available.");
            return;
        }

        if (Interlocked.Exchange(ref cjkFontMissingLogged, 1) == 0)
            log?.LogWarning(
                "DryCycle RWImGUI could not find a font containing Simplified Chinese glyphs in RWImGUI's atlas. " +
                "RWImGUI 1.12 normally merges data/fonts/NotoSansSC-Regular.ttf into its H3 font. " +
                "The Observatory will remain usable in English.");
    }
}

// ObservatoryInputContext is the sole owner of interactive Observatory drawing. RWImGUI
// invokes this method from its normal CurrentContext.Render slot after NewFrame. Keeping
// the UI here ensures window rendering and Win32 input use one coherent context lifecycle.
internal sealed class ObservatoryInputContext : IMGUIContext
{
    public override void Render(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        ObservatoryFrontend.RenderFromContext(ref idxgiSwapChain, ref syncInterval, ref flags);
    }

    // Keep Unity's WndProc alive so F7 can always close the Observatory. DryCycle's existing
    // PlayerInput gate suppresses gameplay controls only when ImGui reports capture intent.
    public override bool BlockWMEvent() => false;

    public override void OnDestroyed()
    {
        AIDebugPresentationHub.SetCaptureState(false, false);
    }
}
