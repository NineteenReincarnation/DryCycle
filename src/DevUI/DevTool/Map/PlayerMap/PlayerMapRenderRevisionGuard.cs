using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Owns all stale-source invalidation for incremental Player Map Render jobs. A frozen render is
/// cancelled if World Layout, exact topology, the static room bake, or authored RoomSettings terrain
/// changes before commit. This guard never advances vanilla Map/Room update logic.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapIncrementalRenderPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(PlayerMapTerrainBakeBridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
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
    private static string renderRegion = string.Empty;
    private static long mapRevision;
    private static int bakeRevision;
    private static int terrainRevision;
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
        if (enabled && snapshot?.Available == true)
        {
            // Establish a complete terrain baseline before the frozen Render snapshot is accepted.
            // Later budgeted audits can then distinguish a genuine change from merely observing an
            // untouched room for the first time.
            PlayerMapTerrainSemanticRevision.AuditAll(snapshot.RegionName, snapshot.Rooms);
        }

        bool started = orig(session, page, snapshot, export);
        if (enabled && started)
        {
            renderSession = session;
            renderRegion = snapshot?.RegionName ?? page?.world?.name ?? string.Empty;
            mapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
            bakeRevision = RoomMapBakeCache.Revision;
            terrainRevision = PlayerMapTerrainSemanticRevision.Revision;
        }
        return started;
    }

    private static void StepHook(OrigStep orig, EditorSession session)
    {
        if (enabled && PlayerMapRenderScheduler.IsRunning && ReferenceEquals(renderSession, session))
        {
            PlayerMapPresentationSnapshot current = PlayerMapWorkspaceRuntime.GetPresentation(session);
            if (current?.Available == true &&
                string.Equals(current.RegionName ?? string.Empty, renderRegion, StringComparison.OrdinalIgnoreCase))
            {
                // Render keeps checking terrain even if the user switches away from Player Map while
                // the job continues. The audit is bounded so background progress remains predictable.
                PlayerMapTerrainSemanticRevision.Audit(renderRegion, current.Rooms, 24);
            }

            long currentMapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
            if (currentMapRevision != mapRevision)
                PlayerMapRenderScheduler.RequestCancel();
            else if (RoomMapBakeCache.Revision != bakeRevision)
                PlayerMapRenderScheduler.RequestCancel();
            else if (PlayerMapTerrainSemanticRevision.Revision != terrainRevision)
                PlayerMapRenderScheduler.RequestCancel();
        }

        orig(session);
        if (!PlayerMapRenderScheduler.IsRunning)
            ClearObservedRender();
    }

    private static void ClearObservedRender()
    {
        renderSession = null;
        renderRegion = string.Empty;
        mapRevision = 0L;
        bakeRevision = 0;
        terrainRevision = 0;
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
