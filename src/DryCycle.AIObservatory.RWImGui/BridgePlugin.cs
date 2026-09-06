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
    public const string PluginVersion = "0.2.1";

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
        ObservatoryFrontend.ReleaseInputContext();
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
    private static readonly ObservatoryInputContext InputContext = new();

    private static ManualLogSource log;
    private static int firstPresentLogged;
    private static int firstVisibleDrawLogged;
    private static int drawFailureLogged;
    private static int inputContextLogged;
    private static int cjkFontLogged;
    private static int cjkFontMissingLogged;
    private static volatile bool visible;
    private static bool cjkFontResolved;
    private static ImFontPtr cjkFont;

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
                ReleaseInputContext();
                AIDebugPresentationHub.SetCaptureState(false, false);
                return;
            }

            EnsureInputContext();

            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo("DryCycle RWImGUI F7-visible frame reached Present; drawing functional snapshot/command-queue Observatory UI.");

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
            {
                log?.LogError(
                    "DryCycle RWImGUI Observatory draw failed. The callback remains registered for diagnostics. " + error);
            }
        }
    }

    private static void EnsureInputContext()
    {
        // RWImGUI only feeds Win32 input into ImGui while an IMGUIContext exists. An
        // AddAlwaysCallback without a context therefore renders perfectly but cannot be
        // clicked. Install a tiny logical context while F7 is open. It intentionally does
        // not block the game's WndProc; DryCycle's existing PlayerInput gate neutralizes
        // gameplay commands when ImGui actually wants mouse/keyboard input, and F7 remains
        // visible to Unity so the Observatory can always be closed.
        if (ReferenceEquals(ImGUIAPI.CurrentContext, InputContext)) return;
        if (ImGUIAPI.HasContext) return; // Respect RWImGUI's own menu or another consumer.

        ImGUIAPI.SwitchContext(InputContext);
        if (Interlocked.Exchange(ref inputContextLogged, 1) == 0)
            log?.LogInfo("DryCycle RWImGUI Observatory input context activated; widgets are now interactive without opening the RWImGUI menu.");
    }

    internal static void ReleaseInputContext()
    {
        try
        {
            if (ReferenceEquals(ImGUIAPI.CurrentContext, InputContext))
                ImGUIAPI.SwitchContext(null);
        }
        catch (Exception error)
        {
            log?.LogWarning("DryCycle RWImGUI Observatory input context release failed: " + error.Message);
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

            // RWImGUI 1.12 merges NotoSansSC into one of its heading fonts. The default
            // FiraCode font has no CJK, which is why Chinese text previously became '?'.
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
                log?.LogInfo($"DryCycle RWImGUI CJK font selected from RWImGUI atlas: index={bestIndex}, size={cjkFont.FontSize:0.##}. Chinese UI glyphs are available.");
            return;
        }

        if (Interlocked.Exchange(ref cjkFontMissingLogged, 1) == 0)
            log?.LogWarning(
                "DryCycle RWImGUI could not find a font containing Simplified Chinese glyphs in RWImGUI's atlas. " +
                "RWImGUI 1.12 normally merges data/fonts/NotoSansSC-Regular.ttf into its H3 font. " +
                "The Observatory will remain usable in English.");
    }
}

// Logical RWImGUI input owner. Drawing stays in AddAlwaysCallback so it remains independent
// from RWImGUI's menu lifecycle. Returning false from BlockWMEvent is deliberate: the Win32
// backend still receives input, but Unity also receives F7 and DryCycle's main-thread input
// gate decides whether gameplay commands are neutralized.
internal sealed class ObservatoryInputContext : IMGUIContext
{
    public override void Render(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
    }

    public override bool BlockWMEvent() => false;

    public override void OnDestroyed()
    {
        AIDebugPresentationHub.SetCaptureState(false, false);
    }
}
