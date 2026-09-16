using System;
using System.Reflection;
using BepInEx.Logging;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Hard safety boundary for the transitional synchronous renderer that existed while the new
/// incremental scheduler was being introduced. Normal Render commands are intercepted before this
/// method is reachable. If that interception is unavailable for any reason, fail visibly instead of
/// silently running the old all-at-once renderer and producing output through a second code path.
/// </summary>
internal static class PlayerMapLegacyRenderGuard
{
    private delegate PlayerMapRenderOutcome OrigBuild(MapPage page, PlayerMapSessionState state, bool export);
    private delegate PlayerMapRenderOutcome HookBuild(OrigBuild orig, MapPage page, PlayerMapSessionState state, bool export);

    private static readonly HookBuild BuildHookDelegate = BuildHook;
    private static IDisposable buildHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo build = typeof(PlayerMapRenderPipeline).GetMethod(
                "Build",
                flags,
                null,
                new[] { typeof(MapPage), typeof(PlayerMapSessionState), typeof(bool) },
                null);
            if (build == null)
                throw new MissingMethodException("PlayerMapRenderPipeline.Build was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            buildHook = constructor.Invoke(new object[] { build, BuildHookDelegate }) as IDisposable;
            if (buildHook == null)
                throw new InvalidOperationException("Legacy Player Map render guard was not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map legacy-render guard could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { buildHook?.Dispose(); }
        catch { }
        buildHook = null;
        enabled = false;
        log = null;
    }

    private static PlayerMapRenderOutcome BuildHook(
        OrigBuild orig,
        MapPage page,
        PlayerMapSessionState state,
        bool export)
    {
        if (!enabled)
            return orig(page, state, export);

        log?.LogError(
            "Blocked obsolete synchronous Player Map render path. The incremental Render scheduler " +
            "must own Build Preview / Render & Export.");
        return new PlayerMapRenderOutcome
        {
            Report = PlayerMapRenderReport.Failure(
                "Player Map render was blocked before output.",
                "The obsolete synchronous render path was reached instead of the incremental scheduler."),
            Preview = PlayerMapRenderPreview.Empty
        };
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
