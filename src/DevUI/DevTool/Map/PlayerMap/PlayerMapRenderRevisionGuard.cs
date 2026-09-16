using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Extends incremental Render invalidation to edits outside the Player Map command surface. A
/// derived Canon position depends on World Layout, so moving a room there must invalidate the same
/// frozen render job. Room-bake revision changes are also treated as stale source data rather than
/// allowing a render to finish against an older room file.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapIncrementalRenderPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapRenderRevisionGuardPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.RenderRevisionGuard";
    public const string PluginName = "DryCycle Player Map Render Revision Guard";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapRenderRevisionGuard.Enable(Logger);
    private void OnDisable() => PlayerMapRenderRevisionGuard.Disable();
}

internal static class PlayerMapRenderRevisionGuard
{
    private delegate bool OrigBegin(EditorSession session, MapPage page, PlayerMapPresentationSnapshot snapshot, bool export);
    private delegate bool HookBegin(OrigBegin orig, EditorSession session, MapPage page, PlayerMapPresentationSnapshot snapshot, bool export);
    private delegate void OrigStep(EditorSession session);
    private delegate void HookStep(OrigStep orig, EditorSession session);

    private static readonly HookBegin BeginHookDelegate = BeginHook;
    private static readonly HookStep StepHookDelegate = StepHook;
    private static IDisposable beginHook;
    private static IDisposable stepHook;
    private static ManualLogSource log;
    private static EditorSession renderSession;
    private static long mapRevision;
    private static int bakeRevision;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type scheduler = typeof(PlayerMapRenderScheduler);
            MethodInfo begin = scheduler.GetMethod("Begin", flags, null,
                new[] { typeof(EditorSession), typeof(MapPage), typeof(PlayerMapPresentationSnapshot), typeof(bool) }, null);
            MethodInfo step = scheduler.GetMethod("Step", flags, null,
                new[] { typeof(EditorSession) }, null);
            if (begin == null || step == null)
                throw new MissingMemberException("Player Map Render revision guard targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            beginHook = constructor.Invoke(new object[] { begin, BeginHookDelegate }) as IDisposable;
            stepHook = constructor.Invoke(new object[] { step, StepHookDelegate }) as IDisposable;
            if (beginHook == null || stepHook == null)
                throw new InvalidOperationException("Player Map Render revision guard hooks were not created.");

            enabled = true;
            log?.LogInfo("Player Map Render revision guard enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map Render revision guard could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref stepHook);
        Dispose(ref beginHook);
        ClearObservedRender();
        enabled = false;
        log = null;
    }

    private static bool BeginHook(
        OrigBegin orig,
        EditorSession session,
        MapPage page,
        PlayerMapPresentationSnapshot snapshot,
        bool export)
    {
        bool started = orig(session, page, snapshot, export);
        if (enabled && started)
        {
            renderSession = session;
            mapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
            bakeRevision = RoomMapBakeCache.Revision;
        }
        return started;
    }

    private static void StepHook(OrigStep orig, EditorSession session)
    {
        if (enabled && PlayerMapRenderScheduler.IsRunning && ReferenceEquals(renderSession, session))
        {
            long currentMapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
            if (currentMapRevision != mapRevision)
                PlayerMapRenderScheduler.RequestCancel();
            else if (RoomMapBakeCache.Revision != bakeRevision)
                PlayerMapRenderScheduler.RequestCancel();
        }

        orig(session);
        if (!PlayerMapRenderScheduler.IsRunning)
            ClearObservedRender();
    }

    private static void ClearObservedRender()
    {
        renderSession = null;
        mapRevision = 0L;
        bakeRevision = 0;
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
