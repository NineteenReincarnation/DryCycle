using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Frontend lifetime owner for the retained V2 scene projection.
/// Phase 1 owns scene synchronization only; rendering remains on the legacy path until later phases.
/// </summary>
internal static class WorldMapRetainedV2Runtime
{
    private static readonly WorldMapScene SceneState = new();
    private static readonly WorldMapSceneSynchronizer Synchronizer = new();
    private static readonly WorldMapRoomResourceStore RoomResources = new();
    private static readonly WorldMapDirtySet pendingRoomResourceDirty = new();
    private static WorldMapDirtySet lastDirty = new();

    internal static WorldMapScene Scene => SceneState;
    internal static WorldMapDirtySet LastDirty => lastDirty;
    internal static WorldMapRoomResourceStore Resources => RoomResources;

    internal static void Synchronize(
        EditorMapPresentationSnapshot snapshot,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        long layoutRevision,
        int interactiveRoom,
        WorldMapViewTransform viewTransform)
    {
        lastDirty = Synchronizer.Synchronize(
            SceneState,
            snapshot,
            localPositions,
            layoutRevision,
            interactiveRoom,
            viewTransform);

        pendingRoomResourceDirty.MergeFrom(lastDirty);
    }

    internal static void UpdateMainThread()
    {
        EditorSession session = DevToolRuntime.ActiveSession;
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;

        if (!pendingRoomResourceDirty.IsEmpty)
        {
            RoomResources.ApplyDirty(SceneState, pendingRoomResourceDirty);
            pendingRoomResourceDirty.Clear();
        }

        RoomResources.UpdateMainThread(session, snapshot, SceneState);
    }

    internal static void ResetRetainedState()
    {
        Synchronizer.Reset();
        SceneState.Reset();
        RoomResources.Reset();
        pendingRoomResourceDirty.Clear();
        lastDirty = new WorldMapDirtySet();
    }

    internal static void DrawToolbarDiagnostics()
    {
        ImGui.SameLine(0f, 12f);
        ImGui.TextDisabled(
            "· V2 P1 scene " +
            SceneState.Rooms.Count + "/" +
            SceneState.Connections.Count);

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("World Map Retained V2 · Phase 1");
        ImGui.TextUnformatted("rooms: " + SceneState.Rooms.Count);
        ImGui.TextUnformatted("connections: " + SceneState.Connections.Count);
        ImGui.TextUnformatted(
            "room resources: " + RoomResources.Count +
            " · thumbnails " + RoomResources.CommittedThumbnailCount);
        ImGui.TextUnformatted("scene revision: " + SceneState.SceneRevision);
        ImGui.TextUnformatted("view revision: " + SceneState.ViewRevision);
        ImGui.TextUnformatted("last scene dirty count: " + lastDirty.ChangeCount);
        ImGui.TextDisabled("pan/zoom changes only the view revision");
        ImGui.EndTooltip();
    }
}
