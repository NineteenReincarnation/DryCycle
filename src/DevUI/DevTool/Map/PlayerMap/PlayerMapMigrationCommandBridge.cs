using System;
using System.Reflection;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Drains migration-stream commands at the same main-thread boundary as ordinary Player Map commands.
/// This keeps ImGui input as a pure producer and prevents stream mutations from occurring during Draw.
/// </summary>
internal static class PlayerMapMigrationCommandBridge
{
    private delegate void OrigProcess(EditorSession session);
    private delegate void HookProcess(OrigProcess orig, EditorSession session);

    private static readonly HookProcess ProcessHookDelegate = ProcessHook;
    private static IDisposable processHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo process = typeof(PlayerMapCommandQueue).GetMethod(
                "Process", flags, null, new[] { typeof(EditorSession) }, null);
            if (process == null)
                throw new MissingMethodException("PlayerMapCommandQueue.Process was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            processHook = constructor.Invoke(new object[] { process, ProcessHookDelegate }) as IDisposable;
            if (processHook == null)
                throw new InvalidOperationException("Player Map migration command bridge hook was not created.");
            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map migration command bridge could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { processHook?.Dispose(); }
        catch { }
        processHook = null;
        PlayerMapMigrationCommandQueue.Clear();
        enabled = false;
    }

    private static void ProcessHook(OrigProcess orig, EditorSession session)
    {
        orig(session);
        if (enabled) PlayerMapMigrationStreamRuntime.Process(session);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
