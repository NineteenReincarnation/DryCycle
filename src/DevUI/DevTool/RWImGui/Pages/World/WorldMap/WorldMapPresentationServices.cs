using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Snapshot-local lookup/cache service for World Map presentation.
/// This replaces the old catch-all performance plugin without owning routing or rendering policy.
/// </summary>
internal static class WorldMapPresentationIndex
{
    private sealed class SnapshotIndex
    {
        internal EditorMapPresentationSnapshot Snapshot;
        internal readonly Dictionary<int, EditorMapRoomSnapshot> RoomsByIndex = new();
        internal readonly Dictionary<string, EditorMapConnectionSnapshot> ConnectionsById =
            new(StringComparer.Ordinal);
        internal readonly Dictionary<long, EditorMapConnectionSnapshot> ConnectionsByEndpoint = new();
    }

    private static readonly object BuildGate = new();
    private static SnapshotIndex indexed;
    private static int visualResetGeneration;

    [ThreadStatic] private static Dictionary<int, EditorMapRoomVisualSnapshot> threadRoomVisuals;
    [ThreadStatic] private static int threadGeometryGeneration;
    [ThreadStatic] private static int threadVisualResetGeneration;

    internal static EditorMapRoomVisualSnapshot GetRoomVisual(int roomIndex)
    {
        int geometryGeneration =
            MapRoomGeometryPresentationHub.PublishedGeneration;
        int resetGeneration = Volatile.Read(ref visualResetGeneration);
        Dictionary<int, EditorMapRoomVisualSnapshot> cache =
            threadRoomVisuals ??= new Dictionary<int, EditorMapRoomVisualSnapshot>();

        if (threadGeometryGeneration != geometryGeneration ||
            threadVisualResetGeneration != resetGeneration)
        {
            threadGeometryGeneration = geometryGeneration;
            threadVisualResetGeneration = resetGeneration;
            cache.Clear();
        }

        if (cache.TryGetValue(roomIndex, out EditorMapRoomVisualSnapshot cached))
            return cached;

        EditorMapRoomVisualSnapshot visual =
            MapRoomGeometryPresentationHub.Get(roomIndex) ??
            EditorMapRoomVisualSnapshot.Empty;
        cache[roomIndex] = visual;
        return visual;
    }

    internal static EditorMapRoomSnapshot FindRoom(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex)
    {
        SnapshotIndex state = Ensure(snapshot);
        return state.RoomsByIndex.TryGetValue(
            roomIndex,
            out EditorMapRoomSnapshot room)
                ? room
                : null;
    }

    internal static EditorMapConnectionSnapshot FindConnection(
        EditorMapPresentationSnapshot snapshot,
        string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        SnapshotIndex state = Ensure(snapshot);
        return state.ConnectionsById.TryGetValue(
            id,
            out EditorMapConnectionSnapshot connection)
                ? connection
                : null;
    }

    internal static EditorMapConnectionSnapshot FindConnectionAtEndpoint(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex)
    {
        SnapshotIndex state = Ensure(snapshot);
        return state.ConnectionsByEndpoint.TryGetValue(
            EndpointKey(roomIndex, nodeIndex),
            out EditorMapConnectionSnapshot connection)
                ? connection
                : null;
    }

    internal static bool IsEndpointFree(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node)
    {
        if (node == null || !node.Exit || node.ConnectedRoomIndex >= 0)
            return false;

        SnapshotIndex state = Ensure(snapshot);
        return !state.ConnectionsByEndpoint.ContainsKey(
            EndpointKey(roomIndex, node.NodeIndex));
    }

    internal static void Reset()
    {
        Volatile.Write(ref indexed, null);
        Interlocked.Increment(ref visualResetGeneration);
    }

    private static SnapshotIndex Ensure(EditorMapPresentationSnapshot snapshot)
    {
        SnapshotIndex current = Volatile.Read(ref indexed);
        if (current != null && ReferenceEquals(current.Snapshot, snapshot))
            return current;

        lock (BuildGate)
        {
            current = Volatile.Read(ref indexed);
            if (current != null && ReferenceEquals(current.Snapshot, snapshot))
                return current;

            SnapshotIndex built = Build(snapshot);
            Volatile.Write(ref indexed, built);
            return built;
        }
    }

    private static SnapshotIndex Build(EditorMapPresentationSnapshot snapshot)
    {
        SnapshotIndex built = new() { Snapshot = snapshot };

        EditorMapRoomSnapshot[] rooms =
            snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room != null)
                built.RoomsByIndex[room.RoomIndex] = room;
        }

        EditorMapConnectionSnapshot[] connections =
            snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null) continue;

            if (!string.IsNullOrEmpty(connection.ConnectionId))
                built.ConnectionsById[connection.ConnectionId] = connection;

            long from = EndpointKey(
                connection.FromRoomIndex,
                connection.FromNodeIndex);
            if (!built.ConnectionsByEndpoint.ContainsKey(from))
                built.ConnectionsByEndpoint[from] = connection;

            if (connection.ToNodeIndex < 0) continue;
            long to = EndpointKey(
                connection.ToRoomIndex,
                connection.ToNodeIndex);
            if (!built.ConnectionsByEndpoint.ContainsKey(to))
                built.ConnectionsByEndpoint[to] = connection;
        }

        return built;
    }

    private static long EndpointKey(int roomIndex, int nodeIndex) =>
        ((long)(uint)roomIndex << 32) | (uint)nodeIndex;
}

/// <summary>
/// Low-frequency publication/prime throttle for detached map presentation data.
/// It is an explicit frontend service owned by BridgePlugin, not a self-starting compatibility plugin.
/// </summary>
internal static class WorldMapUpdateThrottle
{
    private const int SnapshotIntervalFrames = 2;
    private const int PreviewPrimeIntervalFrames = 2;

    private static bool enabled;

    private static int lastPublishFrame = -1000;
    private static string lastPublishRegion = string.Empty;
    private static int lastPublishSelection = int.MinValue;
    private static int lastPublishCurrentRoom = int.MinValue;

    private static int lastGeometryPrimeFrame = -1000;
    private static string lastGeometryRegion = string.Empty;
    private static int lastGeometrySelection = int.MinValue;
    private static int lastGeometryCurrentRoom = int.MinValue;

    private static int lastShortcutPrimeFrame = -1000;
    private static string lastShortcutRegion = string.Empty;
    private static int lastShortcutSelection = int.MinValue;
    private static int lastShortcutCurrentRoom = int.MinValue;

    internal static void Enable(ManualLogSource log)
    {
        if (enabled) return;
        enabled = true;
        ResetState();
        WorldMapPresentationIndex.Reset();
        WorldMapFrontendBridge.RegisterShouldPublish(ShouldPublish);
        WorldMapFrontendBridge.RegisterShouldPrimeGeometry(ShouldPrimeGeometry);
        log?.LogInfo("World Map V2 presentation throttle enabled.");
    }

    internal static void Disable()
    {
        if (enabled)
        {
            WorldMapFrontendBridge.UnregisterShouldPrimeGeometry(ShouldPrimeGeometry);
            WorldMapFrontendBridge.UnregisterShouldPublish(ShouldPublish);
        }

        enabled = false;
        WorldMapPresentationIndex.Reset();
        ResetState();
    }

    internal static bool ShouldPublish(EditorSession session)
    {
        if (!enabled) return true;

        if (session?.ToolMode != EditorToolMode.Map)
        {
            lastPublishFrame = -1000;
            lastPublishRegion = string.Empty;
            lastPublishSelection = int.MinValue;
            lastPublishCurrentRoom = int.MinValue;
            WorldMapPresentationIndex.Reset();
            return true;
        }

        string region = session.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        bool urgent =
            !MapEditorPresentationHub.Current.Available ||
            !string.Equals(region, lastPublishRegion, StringComparison.OrdinalIgnoreCase) ||
            selected != lastPublishSelection ||
            currentRoom != lastPublishCurrentRoom;

        if (!urgent && WorldMapBackgroundBudget.InteractionActive)
            return false;

        if (!urgent &&
            Time.frameCount - lastPublishFrame < SnapshotIntervalFrames)
            return false;

        lastPublishFrame = Time.frameCount;
        lastPublishRegion = region;
        lastPublishSelection = selected;
        lastPublishCurrentRoom = currentRoom;
        return true;
    }

    internal static bool ShouldPrimeGeometry(EditorSession session)
    {
        if (!enabled) return true;
        if (WorldMapBackgroundBudget.InteractionActive) return false;

        string region = session?.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent =
            !string.Equals(region, lastGeometryRegion, StringComparison.OrdinalIgnoreCase) ||
            selected != lastGeometrySelection ||
            currentRoom != lastGeometryCurrentRoom;

        if (!urgent &&
            Time.frameCount - lastGeometryPrimeFrame < PreviewPrimeIntervalFrames)
            return false;

        lastGeometryPrimeFrame = Time.frameCount;
        lastGeometryRegion = region;
        lastGeometrySelection = selected;
        lastGeometryCurrentRoom = currentRoom;
        return true;
    }

    internal static bool ShouldPrimeShortcuts(
        EditorSession session,
        int selectedRoomIndex)
    {
        if (!enabled) return true;
        if (WorldMapBackgroundBudget.InteractionActive) return false;

        string region = session?.World?.name ?? string.Empty;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent =
            !string.Equals(region, lastShortcutRegion, StringComparison.OrdinalIgnoreCase) ||
            selectedRoomIndex != lastShortcutSelection ||
            currentRoom != lastShortcutCurrentRoom;

        if (!urgent &&
            Time.frameCount - lastShortcutPrimeFrame < PreviewPrimeIntervalFrames)
            return false;

        lastShortcutPrimeFrame = Time.frameCount;
        lastShortcutRegion = region;
        lastShortcutSelection = selectedRoomIndex;
        lastShortcutCurrentRoom = currentRoom;
        return true;
    }

    private static void ResetState()
    {
        lastPublishFrame = -1000;
        lastPublishRegion = string.Empty;
        lastPublishSelection = int.MinValue;
        lastPublishCurrentRoom = int.MinValue;
        lastGeometryPrimeFrame = -1000;
        lastGeometryRegion = string.Empty;
        lastGeometrySelection = int.MinValue;
        lastGeometryCurrentRoom = int.MinValue;
        lastShortcutPrimeFrame = -1000;
        lastShortcutRegion = string.Empty;
        lastShortcutSelection = int.MinValue;
        lastShortcutCurrentRoom = int.MinValue;
    }
}

/// <summary>
/// Main-thread background work budget. Interactive navigation gets priority over source recovery and
/// compatibility scanning; stable frames advance those jobs at a bounded cadence.
/// </summary>
internal static class WorldMapBackgroundBudget
{
    private const float DetailedBackgroundZoom = 0.42f;
    private const int GeometrySweepIntervalFrames = 4;
    private const int ShortcutSweepIntervalFrames = 4;
    private const int InteractionCooldownMilliseconds = 90;

    private static volatile bool enabled;
    private static int lastGeometrySweepFrame = -1000;
    private static int lastShortcutSweepFrame = -1000;
    private static string geometryRegion = string.Empty;
    private static string shortcutRegion = string.Empty;
    private static long interactionUntilTimestamp;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        ResetState();
        WorldMapFrontendBridge.RegisterGeometryBackgroundBudget(ShouldProcessGeometry);
        logger?.LogInfo("World Map V2 background budget enabled.");
    }

    internal static void Disable()
    {
        if (enabled)
            WorldMapFrontendBridge.UnregisterGeometryBackgroundBudget(ShouldProcessGeometry);
        enabled = false;
        ResetState();
    }

    internal static bool InteractionActive =>
        enabled &&
        Stopwatch.GetTimestamp() <= Interlocked.Read(ref interactionUntilTimestamp);

    internal static void NoteInteraction()
    {
        if (!enabled) return;

        long cooldown =
            Math.Max(
                1L,
                Stopwatch.Frequency * InteractionCooldownMilliseconds / 1000L);
        long target = Stopwatch.GetTimestamp() + cooldown;
        while (true)
        {
            long current = Interlocked.Read(ref interactionUntilTimestamp);
            if (current >= target) return;
            if (Interlocked.CompareExchange(
                    ref interactionUntilTimestamp,
                    target,
                    current) == current)
                return;
        }
    }

    internal static bool AllowSourceRecovery() =>
        !enabled || !InteractionActive;

    internal static bool ShouldProcessGeometry(global::World world)
    {
        if (!enabled) return true;
        if (InteractionActive) return false;

        string region = world?.name ?? string.Empty;
        if (!string.Equals(region, geometryRegion, StringComparison.OrdinalIgnoreCase))
        {
            geometryRegion = region;
            lastGeometrySweepFrame = Time.frameCount;
            return false;
        }

        if (WorldMapRetainedV2Runtime.LatestZoom < DetailedBackgroundZoom)
            return false;

        if (Time.frameCount - lastGeometrySweepFrame < GeometrySweepIntervalFrames)
            return false;

        lastGeometrySweepFrame = Time.frameCount;
        return true;
    }

    internal static bool ShouldProcessShortcuts()
    {
        if (!enabled) return true;
        if (InteractionActive) return false;

        string region =
            DevToolRuntime.ActiveSession?.World?.name ?? string.Empty;
        if (!string.Equals(
                region,
                shortcutRegion,
                StringComparison.OrdinalIgnoreCase))
        {
            shortcutRegion = region;
            lastShortcutSweepFrame = Time.frameCount;
            return false;
        }

        if (Time.frameCount - lastShortcutSweepFrame < ShortcutSweepIntervalFrames)
            return false;

        lastShortcutSweepFrame = Time.frameCount;
        return true;
    }

    private static void ResetState()
    {
        lastGeometrySweepFrame = -1000;
        lastShortcutSweepFrame = -1000;
        geometryRegion = string.Empty;
        shortcutRegion = string.Empty;
        Interlocked.Exchange(ref interactionUntilTimestamp, 0L);
    }
}
