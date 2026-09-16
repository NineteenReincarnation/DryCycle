using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Keeps normal authoring surfaces user-facing. Runtime/cache/GPU/profiling implementation details
/// belong in diagnostics, logs or dedicated debug workspaces, not in ordinary editors.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class DevToolUserFacingCopyCleanupPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.UserFacingCopyCleanup";
    public const string PluginName = "DryCycle DevTool User-Facing Copy Cleanup";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => DevToolUserFacingCopyCleanup.Enable(Logger);

    private void Update() => DevToolUserFacingCopyCleanup.RemoveWorldMapToolbarDiagnostics();

    private void OnDisable() => DevToolUserFacingCopyCleanup.Disable();
}

internal static class DevToolUserFacingCopyCleanup
{
    private delegate void OrigMutedText(string text, bool wrapped);
    private delegate void HookMutedText(OrigMutedText orig, string text, bool wrapped);

    private delegate void OrigDrawInterfaceCard();
    private delegate void HookDrawInterfaceCard(OrigDrawInterfaceCard orig);

    private delegate void OrigDrawPerformanceDiagnostics(Num.Vector2 display);
    private delegate void HookDrawPerformanceDiagnostics(OrigDrawPerformanceDiagnostics orig, Num.Vector2 display);

    private static readonly HookMutedText MutedTextHookDelegate = MutedTextHook;
    private static readonly HookDrawInterfaceCard InterfaceCardHookDelegate = DrawInterfaceCardHook;
    private static readonly HookDrawPerformanceDiagnostics PerformanceDiagnosticsHookDelegate = DrawPerformanceDiagnosticsHook;

    private static ManualLogSource log;
    private static IDisposable mutedTextHook;
    private static IDisposable interfaceCardHook;
    private static IDisposable performanceDiagnosticsHook;
    private static FieldInfo lanceDebugPageField;
    private static FieldInfo worldMapToolbarHookField;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags allStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            MethodInfo mutedText = typeof(DevToolWidgets).GetMethod(
                "MutedText",
                allStatic,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            MethodInfo drawInterfaceCard = typeof(ControlCenterWindow).GetMethod(
                "DrawInterfaceCard",
                allStatic,
                null,
                Type.EmptyTypes,
                null);
            MethodInfo drawPerformanceDiagnostics = typeof(ControlCenterWindow).GetMethod(
                "DrawPerformanceDiagnostics",
                allStatic,
                null,
                new[] { typeof(Num.Vector2) },
                null);

            lanceDebugPageField = typeof(DevToolOverlay).GetField("lanceDebugPage", allStatic);
            worldMapToolbarHookField = typeof(WorldMapGpuRuntime).GetField("toolbarHook", allStatic);

            if (mutedText == null || drawInterfaceCard == null || drawPerformanceDiagnostics == null)
                throw new MissingMemberException("Normal-UI copy cleanup targets were not found.");

            mutedTextHook = constructor.Invoke(new object[] { mutedText, MutedTextHookDelegate }) as IDisposable;
            interfaceCardHook = constructor.Invoke(new object[] { drawInterfaceCard, InterfaceCardHookDelegate }) as IDisposable;
            performanceDiagnosticsHook = constructor.Invoke(
                new object[] { drawPerformanceDiagnostics, PerformanceDiagnosticsHookDelegate }) as IDisposable;

            if (mutedTextHook == null || interfaceCardHook == null || performanceDiagnosticsHook == null)
                throw new InvalidOperationException("Normal-UI copy cleanup hooks were not created.");

            enabled = true;
            RemoveWorldMapToolbarDiagnostics();
            log?.LogInfo("Normal DevTool UI implementation diagnostics hidden; debug/log paths remain unchanged.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Normal DevTool UI copy cleanup could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref performanceDiagnosticsHook);
        DisposeHook(ref interfaceCardHook);
        DisposeHook(ref mutedTextHook);
        lanceDebugPageField = null;
        worldMapToolbarHookField = null;
        enabled = false;
        log = null;
    }

    /// <summary>
    /// The retained World Map renderer used to append GPU/cache counters and a Rebake command to
    /// the normal authoring toolbar. Keep the retained renderer itself; only detach that diagnostic
    /// toolbar decoration.
    /// </summary>
    internal static void RemoveWorldMapToolbarDiagnostics()
    {
        if (!enabled || worldMapToolbarHookField == null) return;
        try
        {
            if (worldMapToolbarHookField.GetValue(null) is not IDisposable hook) return;
            hook.Dispose();
            worldMapToolbarHookField.SetValue(null, null);
            log?.LogInfo("Removed retained World Map GPU/cache diagnostics from the normal toolbar.");
        }
        catch (Exception error)
        {
            log?.LogWarning("Could not remove World Map toolbar diagnostics: " + Unwrap(error).Message);
        }
    }

    private static void MutedTextHook(OrigMutedText orig, string text, bool wrapped)
    {
        if (!enabled || IsDebugWorkspace())
        {
            orig(text, wrapped);
            return;
        }

        if (IsCreatureCatalogInitialLoad(text))
        {
            orig(DevToolUiSettings.T("正在载入生物…", "Loading creatures…"), wrapped);
            return;
        }

        if (IsImplementationFacingCopy(text))
            return;

        orig(text, wrapped);
    }

    /// <summary>
    /// The Control Center is editor chrome. Keep normal UI/language choices here and move profiling
    /// out of this card entirely; diagnostics are not ordinary editing preferences.
    /// </summary>
    private static void DrawInterfaceCardHook(OrigDrawInterfaceCard orig)
    {
        if (!enabled)
        {
            orig();
            return;
        }

        ImGui.TextColored(new Num.Vector4(0.63f, 0.82f, 1.00f, 1f), DevToolUiSettings.T("界面", "INTERFACE"));
        ImGui.Spacing();

        float keyColumn = DevToolUiSettings.IsChinese ? 116f : 108f;

        DevToolWidgets.MutedText(DevToolUiSettings.T("模式", "Mode"));
        ImGui.SameLine(keyColumn);
        bool vanilla = EditorUiModeState.UseVanilla;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新 UI", "New UI"),
                "ControlCenterNewUi",
                vanilla ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            EditorUiModeState.SetVanilla(false);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("原版", "Vanilla"),
                "ControlCenterVanillaUi",
                vanilla ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            EditorUiModeState.SetVanilla(true);

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine(keyColumn);
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        if (DevToolWidgets.ActionButton(
                "中文",
                "ControlCenterChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "English",
                "ControlCenterEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);
    }

    /// <summary>
    /// Do not spawn the profiling window from the always-visible Control Center. Dedicated debug
    /// workspaces may still consume the monitor directly when diagnostics are explicitly requested.
    /// </summary>
    private static void DrawPerformanceDiagnosticsHook(OrigDrawPerformanceDiagnostics orig, Num.Vector2 display)
    {
        // Intentionally empty outside the dedicated debug workflow.
    }

    private static bool IsCreatureCatalogInitialLoad(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.IndexOf("首次建立生物目录缓存", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("Building the creature catalog cache", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsImplementationFacingCopy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string lower = text.ToLowerInvariant();

        // Known normal-editor implementation/status strings.
        if (text.IndexOf("缓存内容立即显示", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("后台增量刷新", StringComparison.Ordinal) >= 0 ||
            lower.Contains("cached content is immediate") ||
            lower.Contains("refresh incrementally"))
            return true;

        if (text.StartsWith("缩略图：", StringComparison.Ordinal) ||
            lower.StartsWith("thumbnail:"))
            return true;

        if (lower.StartsWith("ready ") && lower.Contains("pending ") && lower.Contains("failed"))
            return true;

        if (text.IndexOf("通用 DevInterface 协议镜像", StringComparison.Ordinal) >= 0 ||
            lower.Contains("generic devinterface protocols mirror"))
            return true;

        // Prevent new implementation notes of the same class from leaking into ordinary muted-help
        // copy. Actual Debug workspace copy is exempted by IsDebugWorkspace().
        return text.IndexOf("缓存", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("增量", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("后台刷新", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("性能监控", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("性能采样", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("调试信息", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("诊断信息", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("烘焙", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("栅格", StringComparison.Ordinal) >= 0 ||
               lower.Contains(" cache") ||
               lower.StartsWith("cache") ||
               lower.Contains("incremental") ||
               lower.Contains("profiling") ||
               lower.Contains("performance monitor") ||
               lower.Contains("performance sample") ||
               lower.Contains("debug info") ||
               lower.Contains("diagnostic info") ||
               lower.Contains("gpu ") ||
               lower.StartsWith("gpu") ||
               lower.Contains("rebake") ||
               lower.Contains("raster") ||
               lower.Contains("snapshot") ||
               lower.Contains("revision") ||
               lower.Contains("hot path") ||
               lower.Contains("backend") ||
               lower.Contains("frontend");
    }

    private static bool IsDebugWorkspace()
    {
        try
        {
            return lanceDebugPageField?.GetValue(null) is bool debug && debug;
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
