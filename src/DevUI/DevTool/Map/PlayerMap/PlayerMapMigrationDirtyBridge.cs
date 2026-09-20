using System.Runtime.CompilerServices;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Integrates migration-stream dirty state with the Player Map presentation through explicit runtime
/// calls. No Save/GetPresentation/Touch method is RuntimeDetoured.
/// </summary>
internal static class PlayerMapMigrationDirtyBridge
{
    private sealed class State
    {
        internal bool Dirty;
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Player Map migration dirty bridge enabled through direct runtime calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        states = new ConditionalWeakTable<EditorSession, State>();
        enabled = false;
    }

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, State>();

    internal static void MarkDirty(EditorSession session)
    {
        if (enabled && session != null)
            states.GetValue(session, _ => new State()).Dirty = true;
    }

    internal static void OnSaveSucceeded(MapPage page)
    {
        if (!enabled || page == null) return;

        EditorSession session = DevToolSessionHub.Current;
        if (session != null && ReferenceEquals(session.Owner?.activePage, page))
            states.GetValue(session, _ => new State()).Dirty = false;
    }

    internal static PlayerMapPresentationSnapshot Project(
        EditorSession session,
        PlayerMapPresentationSnapshot source)
    {
        if (!enabled || session == null || source?.Available != true ||
            !states.TryGetValue(session, out State state) || !state.Dirty || source.Dirty)
            return source;

        return new PlayerMapPresentationSnapshot
        {
            Available = source.Available,
            RegionName = source.RegionName,
            Dirty = true,
            Revision = source.Revision,
            SelectedRoomIndex = source.SelectedRoomIndex,
            Rooms = source.Rooms,
            DefaultMaterials = source.DefaultMaterials,
            RenderReport = source.RenderReport,
            Preview = source.Preview
        };
    }
}
