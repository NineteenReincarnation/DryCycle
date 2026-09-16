using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Integrates the rebuilt Canon/Player Map editor into the existing World Workspace without routing
/// any authoring or rendering back through vanilla MapPage/MiniMap UI logic. World Layout remains
/// the default workspace; Player Map is an explicit sibling view backed by the new PlayerMap runtime.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapWorkspaceIntegrationPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.WorkspaceIntegration";
    public const string PluginName = "DryCycle Player Map Workspace Integration";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    // This is the Player Map frontend root. One discovered entry point explicitly owns every helper
    // module so the feature cannot partially load when a BepInEx build skips auxiliary plugin types.
    private void OnEnable() => PlayerMapFrontendLifecycle.Enable(Logger);
    private void OnDisable() => PlayerMapFrontendLifecycle.Disable();
}

internal static class PlayerMapWorkspaceIntegration
{
    private delegate void OrigDrawToolbar(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawToolbar(OrigDrawToolbar orig, EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot);
    private delegate void OrigDrawBody(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawBody(OrigDrawBody orig, EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot);

    private static readonly HookDrawToolbar DrawToolbarHookDelegate = DrawToolbarHook;
    private static readonly HookDrawBody DrawBodyHookDelegate = DrawBodyHook;

    private static IDisposable toolbarHook;
    private static IDisposable bodyHook;
    private static FieldInfo workspaceModeField;
    private static ManualLogSource log;
    private static bool enabled;
    private static bool playerMapActive;

    internal static bool Active => enabled && playerMapActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type workspace = typeof(WorldWorkspaceView);
            MethodInfo toolbar = workspace.GetMethod("DrawToolbar", flags, null,
                new[] { typeof(EditorPresentationSnapshot), typeof(EditorMapPresentationSnapshot) }, null);
            MethodInfo body = workspace.GetMethod("DrawBody", flags, null,
                new[] { typeof(EditorPresentationSnapshot), typeof(EditorMapPresentationSnapshot) }, null);
            workspaceModeField = workspace.GetField("workspaceMode", flags);
            if (toolbar == null || body == null || workspaceModeField == null)
                throw new MissingMemberException("World Workspace Player Map integration targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            toolbarHook = constructor.Invoke(new object[] { toolbar, DrawToolbarHookDelegate }) as IDisposable;
            bodyHook = constructor.Invoke(new object[] { body, DrawBodyHookDelegate }) as IDisposable;
            if (toolbarHook == null || bodyHook == null)
                throw new InvalidOperationException("Player Map World Workspace integration hooks were not created.");

            enabled = true;
            log?.LogInfo("Player Map integrated into World Workspace.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map workspace integration could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref bodyHook);
        Dispose(ref toolbarHook);
        workspaceModeField = null;
        playerMapActive = false;
        PlayerMapActivityGate.Reset();
        enabled = false;
        log = null;
    }

    private static void DrawToolbarHook(
        OrigDrawToolbar orig,
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        orig(editor, snapshot);
        if (!enabled || snapshot?.Available != true) return;

        // WorldData/Validation are mutually exclusive with Player Map. If one of the native rebuilt
        // workspace modes is active, honor it rather than leaving the toolbar and body disagreeing.
        if (playerMapActive && ReadWorkspaceMode() != 0)
        {
            playerMapActive = false;
            PlayerMapActivityGate.Reset();
        }

        ImGui.SameLine(0f, 8f);
        string label = playerMapActive
            ? DevToolUiSettings.T("返回世界地图", "Back to World Map")
            : DevToolUiSettings.T("玩家地图", "Player Map");
        if (DevToolWidgets.ActionButton(
                label,
                "WorldWorkspacePlayerMap",
                playerMapActive ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            playerMapActive = !playerMapActive;
            if (playerMapActive)
                WriteWorkspaceMode(0); // Keep the sibling mode anchored to the World Map workspace.
            else
                PlayerMapActivityGate.Reset();
        }
    }

    private static void DrawBodyHook(
        OrigDrawBody orig,
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || !playerMapActive)
        {
            orig(editor, snapshot);
            return;
        }

        PlayerMapActivityGate.MarkVisible();
        PlayerMapWorkspaceView.DrawBody(editor, snapshot);
    }

    private static int ReadWorkspaceMode()
    {
        try
        {
            object value = workspaceModeField?.GetValue(null);
            return value == null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteWorkspaceMode(int value)
    {
        try
        {
            if (workspaceModeField == null) return;
            workspaceModeField.SetValue(null, Enum.ToObject(workspaceModeField.FieldType, value));
        }
        catch (Exception error)
        {
            log?.LogDebug("Player Map could not synchronize World Workspace mode: " + error.Message);
        }
    }

    private static void Dispose(ref IDisposable hook)
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
