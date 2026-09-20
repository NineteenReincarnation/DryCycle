using System;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Owns stale-source invalidation for incremental Player Map Render jobs through explicit scheduler
/// phase calls. No PlayerMapRenderScheduler method is RuntimeDetoured.
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
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map Render revision guard enabled through direct scheduler phases; no self-detours attached.");
    }

    internal static void Disable()
    {
        ClearObservedRender();
        enabled = false;
        log = null;
    }

    internal static void BeforeBegin(PlayerMapPresentationSnapshot snapshot)
    {
        if (enabled && snapshot?.Available == true)
            PlayerMapTerrainSemanticRevision.AuditAll(snapshot.RegionName, snapshot.Rooms);
    }

    internal static void AfterBegin(
        bool started,
        EditorSession session,
        MapPage page,
        PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || !started) return;

        renderSession = session;
        renderRegion = snapshot?.RegionName ?? page?.world?.name ?? string.Empty;
        mapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        bakeRevision = RoomMapBakeCache.Revision;
        terrainRevision = PlayerMapTerrainSemanticRevision.Revision;
    }

    internal static void BeforeStep(EditorSession session)
    {
        if (!enabled || !PlayerMapRenderScheduler.IsRunning || !ReferenceEquals(renderSession, session))
            return;

        PlayerMapPresentationSnapshot current = PlayerMapWorkspaceRuntime.GetPresentation(session);
        if (current?.Available == true &&
            string.Equals(current.RegionName ?? string.Empty, renderRegion, StringComparison.OrdinalIgnoreCase))
        {
            PlayerMapTerrainSemanticRevision.Audit(renderRegion, current.Rooms, 24);
        }

        long currentMapRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        if (currentMapRevision != mapRevision ||
            RoomMapBakeCache.Revision != bakeRevision ||
            PlayerMapTerrainSemanticRevision.Revision != terrainRevision)
            PlayerMapRenderScheduler.RequestCancel();
    }

    internal static void AfterStep()
    {
        if (enabled && !PlayerMapRenderScheduler.IsRunning)
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
}
