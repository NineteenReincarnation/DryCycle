using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Marks Player Map activity from the actual visible frontend draw. The backend uses this signal to
/// keep hidden Player Map work asleep while retaining a short grace window for commands enqueued by
/// the visible frame.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapActivityPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.Activity";
    public const string PluginName = "DryCycle Player Map Activity Gate";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapActivityBridge.Enable(Logger);
    private void OnDisable() => PlayerMapActivityBridge.Disable();
}

internal static class PlayerMapActivityBridge
{
    private delegate void OrigDrawBody(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot worldSnapshot);
    private delegate void HookDrawBody(OrigDrawBody orig, EditorPresentationSnapshot editor, EditorMapPresentationSnapshot worldSnapshot);

    private static readonly HookDrawBody DrawBodyHookDelegate = DrawBodyHook;
    private static IDisposable drawBodyHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo drawBody = typeof(PlayerMapWorkspaceView).GetMethod(
                "DrawBody",
                flags,
                null,
                new[] { typeof(EditorPresentationSnapshot), typeof(EditorMapPresentationSnapshot) },
                null);
            if (drawBody == null)
                throw new MissingMethodException("PlayerMapWorkspaceView.DrawBody was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawBodyHook = constructor.Invoke(new object[] { drawBody, DrawBodyHookDelegate }) as IDisposable;
            if (drawBodyHook == null)
                throw new InvalidOperationException("Player Map activity hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map visible-workspace activity gate enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map activity gate could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { drawBodyHook?.Dispose(); }
        catch { }
        drawBodyHook = null;
        PlayerMapActivityGate.Reset();
        enabled = false;
        log = null;
    }

    private static void DrawBodyHook(
        OrigDrawBody orig,
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot worldSnapshot)
    {
        PlayerMapActivityGate.MarkVisible();
        orig(editor, worldSnapshot);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
