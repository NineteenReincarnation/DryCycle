using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Always-visible Player Map input health summary. Unlike Render progress, this panel evaluates the
/// current editor snapshot before a render job starts, so authors can fix missing bakes, invalid
/// multi-pipe endpoints and unsafe output dimensions without producing a failed render attempt.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapWorkspaceIntegrationPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapPreflightPanelPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.PreflightPanel";
    public const string PluginName = "DryCycle Player Map Preflight Panel";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapPreflightPanel.Enable(Logger);
    private void OnDisable() => PlayerMapPreflightPanel.Disable();
}

internal static class PlayerMapPreflightPanel
{
    private delegate void OrigDrawInspector(PlayerMapPresentationSnapshot snapshot);
    private delegate void HookDrawInspector(OrigDrawInspector orig, PlayerMapPresentationSnapshot snapshot);

    private static readonly HookDrawInspector DrawInspectorHookDelegate = DrawInspectorHook;
    private static IDisposable inspectorHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo inspector = typeof(PlayerMapWorkspaceView).GetMethod(
                "DrawInspector",
                flags,
                null,
                new[] { typeof(PlayerMapPresentationSnapshot) },
                null);
            if (inspector == null)
                throw new MissingMethodException("PlayerMapWorkspaceView.DrawInspector was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            inspectorHook = constructor.Invoke(new object[] { inspector, DrawInspectorHookDelegate }) as IDisposable;
            if (inspectorHook == null)
                throw new InvalidOperationException("Player Map preflight panel hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map live preflight inspector enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map preflight inspector could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { inspectorHook?.Dispose(); }
        catch { }
        inspectorHook = null;
        enabled = false;
        log = null;
    }

    private static void DrawInspectorHook(OrigDrawInspector orig, PlayerMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        if (!enabled || snapshot?.Available != true) return;

        EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
        PlayerMapPreflightSnapshot preflight = PlayerMapPreflightDiagnostics.Evaluate(snapshot, world);
        if (!preflight.Available) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 预检", "RENDER PREFLIGHT"));
        string state = preflight.CanRender
            ? DevToolUiSettings.T("可 Render", "Ready to Render")
            : DevToolUiSettings.T("需要处理", "Needs Attention");
        ImGui.TextUnformatted(state);

        DrawMetric("Rooms",
            preflight.ReadyRooms + " ready / " +
            preflight.PendingRooms + " pending / " +
            preflight.MissingRooms + " missing / " +
            preflight.FailedRooms + " failed");
        DrawMetric("Pipes",
            preflight.ExactConnections + " exact / " + preflight.AmbiguousConnections + " unresolved");
        if (preflight.InvalidEndpoints > 0)
            DrawMetric("Invalid endpoints", preflight.InvalidEndpoints.ToString());
        if (preflight.DuplicateEndpointClaims > 0)
            DrawMetric("Endpoint conflicts", preflight.DuplicateEndpointClaims.ToString());
        DrawMetric("Overlaps", preflight.OverlapPairs.ToString());
        if (preflight.Width > 0 && preflight.Height > 0)
            DrawMetric("Output", preflight.Width + " × " + preflight.Height);

        int errorLimit = Math.Min(6, preflight.Errors.Length);
        for (int i = 0; i < errorLimit; i++)
            ImGui.TextWrapped("ERROR · " + preflight.Errors[i]);
        if (preflight.Errors.Length > errorLimit)
            ImGui.TextDisabled("… +" + (preflight.Errors.Length - errorLimit) + " errors");

        int warningLimit = Math.Min(5, preflight.Warnings.Length);
        for (int i = 0; i < warningLimit; i++)
            ImGui.TextWrapped("WARN · " + preflight.Warnings[i]);
        if (preflight.Warnings.Length > warningLimit)
            ImGui.TextDisabled("… +" + (preflight.Warnings.Length - warningLimit) + " warnings");
    }

    private static void DrawMetric(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value ?? string.Empty);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
