using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Binds the DevTool scrollbar expansion to the pane that actually received the mouse wheel.
/// The stock ImGui scrollbar remains compact globally; each scrollable pane owns an independent
/// overlay animation so Browser/Inspector and World Explorer/Inspector cannot trigger each other.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class ScopedScrollChromePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.ScopedScrollChrome";
    public const string PluginName = "DryCycle DevTool Scoped Scroll Chrome";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => ScopedScrollChrome.Enable(Logger);

    private void OnDisable() => ScopedScrollChrome.Disable();
}

internal static class ScopedScrollChrome
{
    private delegate void OrigEditorPane(EditorPresentationSnapshot snapshot);
    private delegate void HookEditorPane(OrigEditorPane orig, EditorPresentationSnapshot snapshot);

    private delegate void OrigWorldPane(EditorMapPresentationSnapshot snapshot);
    private delegate void HookWorldPane(OrigWorldPane orig, EditorMapPresentationSnapshot snapshot);

    private static readonly HookEditorPane BrowserHookDelegate = DrawBrowserContentsHook;
    private static readonly HookEditorPane InspectorHookDelegate = DrawInspectorContentsHook;
    private static readonly HookWorldPane WorldExplorerHookDelegate = DrawWorldExplorerHook;
    private static readonly HookWorldPane WorldCenterHookDelegate = DrawWorldCenterHook;
    private static readonly HookWorldPane WorldInspectorHookDelegate = DrawWorldInspectorHook;

    private static ManualLogSource log;
    private static IDisposable browserHook;
    private static IDisposable inspectorHook;
    private static IDisposable worldExplorerHook;
    private static IDisposable worldCenterHook;
    private static IDisposable worldInspectorHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type overlayType = typeof(DevToolOverlay);
            Type worldType = typeof(WorldWorkspaceView);

            MethodInfo drawBrowser = overlayType.GetMethod(
                "DrawBrowserContents",
                flags,
                null,
                new[] { typeof(EditorPresentationSnapshot) },
                null);
            MethodInfo drawInspector = overlayType.GetMethod(
                "DrawInspectorContents",
                flags,
                null,
                new[] { typeof(EditorPresentationSnapshot) },
                null);
            MethodInfo drawWorldExplorer = worldType.GetMethod(
                "DrawExplorer",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo drawWorldCenter = worldType.GetMethod(
                "DrawCenter",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo drawWorldInspector = worldType.GetMethod(
                "DrawInspector",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);

            if (drawBrowser == null || drawInspector == null || drawWorldExplorer == null ||
                drawWorldCenter == null || drawWorldInspector == null)
                throw new MissingMethodException("One or more DevTool scroll-pane draw methods were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            browserHook = constructor.Invoke(new object[] { drawBrowser, BrowserHookDelegate }) as IDisposable;
            inspectorHook = constructor.Invoke(new object[] { drawInspector, InspectorHookDelegate }) as IDisposable;
            worldExplorerHook = constructor.Invoke(new object[] { drawWorldExplorer, WorldExplorerHookDelegate }) as IDisposable;
            worldCenterHook = constructor.Invoke(new object[] { drawWorldCenter, WorldCenterHookDelegate }) as IDisposable;
            worldInspectorHook = constructor.Invoke(new object[] { drawWorldInspector, WorldInspectorHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("DevTool scoped scrollbar animation enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("DevTool scoped scrollbar animation could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref worldInspectorHook);
        DisposeHook(ref worldCenterHook);
        DisposeHook(ref worldExplorerHook);
        DisposeHook(ref inspectorHook);
        DisposeHook(ref browserHook);
        enabled = false;
        log = null;
    }

    private static void DrawBrowserContentsHook(OrigEditorPane orig, EditorPresentationSnapshot snapshot)
    {
        orig(snapshot);
        Draw("Browser");
    }

    private static void DrawInspectorContentsHook(OrigEditorPane orig, EditorPresentationSnapshot snapshot)
    {
        orig(snapshot);
        Draw("Inspector");
    }

    private static void DrawWorldExplorerHook(OrigWorldPane orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        Draw("WorldExplorer");
    }

    private static void DrawWorldCenterHook(OrigWorldPane orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        Draw("WorldCenter");
    }

    private static void DrawWorldInspectorHook(OrigWorldPane orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        Draw("WorldInspector");
        DevToolScrollChrome.PruneInactiveStates();
    }

    private static void Draw(string key)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        DevToolScrollChrome.DrawCurrentRegion(key, ImGui.GetIO(), scale);
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try
        {
            hook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
