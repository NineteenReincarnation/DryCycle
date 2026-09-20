using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Projects the latest World Layout and authored-terrain semantics into immutable Player Map
/// snapshots. Derived rooms remain Convert(WorldLayoutPosition) + PlayerMapOffset; Absolute rooms
/// remain independent.
///
/// Stable frames audit only a small terrain batch. A semantic terrain change causes one full snapshot
/// projection so curved/custom terrain cannot remain stale in the Player Map preview.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapDerivedLayoutBridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.DerivedLayoutBridge";
    public const string PluginName = "DryCycle Player Map Derived Layout Bridge";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapDerivedLayoutBridge.Enable(Logger);
    private void OnDisable() => PlayerMapDerivedLayoutBridge.Disable();
}

internal static class PlayerMapDerivedLayoutBridge
{
    private static ManualLogSource log;
    private static bool enabled;

    private static PlayerMapRoomSnapshot[] cachedSourceRooms;
    private static EditorMapRoomSnapshot[] cachedWorldRooms;
    private static PlayerMapRoomSnapshot[] cachedProjectedRooms;
    private static int cachedTerrainRevision;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live derived-layout/terrain projection enabled through direct runtime calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        cachedSourceRooms = null;
        cachedWorldRooms = null;
        cachedProjectedRooms = null;
        cachedTerrainRevision = 0;
        PlayerMapTerrainSemanticRevision.Reset();
        enabled = false;
        log = null;
    }

    internal static PlayerMapPresentationSnapshot Project(EditorSession session, PlayerMapPresentationSnapshot source)
    {
        if (!enabled || source?.Available != true) return source;

        EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
        if (world?.Available != true ||
            !string.Equals(source.RegionName ?? string.Empty, world.RegionName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return source;

        PlayerMapTerrainSemanticRevision.Audit(source.RegionName, source.Rooms, 12);
        int terrainRevision = PlayerMapTerrainSemanticRevision.Revision;
        PlayerMapRoomSnapshot[] projected = ProjectRooms(source.Rooms, world.Rooms, terrainRevision);
        if (ReferenceEquals(projected, source.Rooms)) return source;

        long mapRevision = session == null ? 0L : EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        long projectedRevision;
        unchecked
        {
            projectedRevision = source.Revision * 397L + mapRevision;
            projectedRevision = projectedRevision * 397L + terrainRevision;
        }

        return new PlayerMapPresentationSnapshot
        {
            Available = source.Available,
            RegionName = source.RegionName,
            Dirty = source.Dirty,
            Revision = projectedRevision,
            SelectedRoomIndex = world.SelectedRoomIndex,
            Rooms = projected,
            DefaultMaterials = source.DefaultMaterials,
            RenderReport = source.RenderReport,
            Preview = source.Preview
        };
    }

    private static PlayerMapRoomSnapshot[] ProjectRooms(
        PlayerMapRoomSnapshot[] sourceRooms,
        EditorMapRoomSnapshot[] worldRooms,
        int terrainRevision)
    {
        sourceRooms ??= Array.Empty<PlayerMapRoomSnapshot>();
        worldRooms ??= Array.Empty<EditorMapRoomSnapshot>();
        if (ReferenceEquals(cachedSourceRooms, sourceRooms) &&
            ReferenceEquals(cachedWorldRooms, worldRooms) &&
            cachedTerrainRevision == terrainRevision &&
            cachedProjectedRooms != null)
            return cachedProjectedRooms;

        Dictionary<int, EditorMapRoomSnapshot> worldByIndex = new(worldRooms.Length);
        for (int i = 0; i < worldRooms.Length; i++)
        {
            EditorMapRoomSnapshot room = worldRooms[i];
            if (room != null) worldByIndex[room.RoomIndex] = room;
        }

        PlayerMapRoomSnapshot[] result = null;
        for (int i = 0; i < sourceRooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = sourceRooms[i];
            if (room == null || !worldByIndex.TryGetValue(room.RoomIndex, out EditorMapRoomSnapshot world))
                continue;

            Vector2 worldPosition = new(world.X, world.Y);
            Vector2 derivedBase = PlayerMapCoordinateSystem.WorldLayoutToCanon(worldPosition);
            Vector2 effective = room.Mode == PlayerMapPlacementMode.Derived
                ? derivedBase + room.Offset
                : room.AbsolutePosition;
            RoomMapBakeSnapshot currentBake = RoomMapBakeCache.GetSnapshot(room.RoomIndex) ?? room.Bake;

            bool changed =
                (room.WorldPosition - worldPosition).sqrMagnitude > 0.000001f ||
                (room.DerivedBasePosition - derivedBase).sqrMagnitude > 0.000001f ||
                (room.EffectivePosition - effective).sqrMagnitude > 0.000001f ||
                room.Layer != world.Layer ||
                room.Disabled != world.Disabled ||
                room.Selected != world.Selected ||
                !BakeEquivalent(room.Bake, currentBake);
            if (!changed) continue;

            result ??= (PlayerMapRoomSnapshot[])sourceRooms.Clone();
            result[i] = new PlayerMapRoomSnapshot
            {
                RoomIndex = room.RoomIndex,
                Name = room.Name,
                Mode = room.Mode,
                WorldPosition = worldPosition,
                DerivedBasePosition = derivedBase,
                Offset = room.Offset,
                AbsolutePosition = room.AbsolutePosition,
                EffectivePosition = effective,
                Layer = world.Layer,
                Disabled = world.Disabled,
                Selected = world.Selected,
                Bake = currentBake
            };
        }

        cachedSourceRooms = sourceRooms;
        cachedWorldRooms = worldRooms;
        cachedTerrainRevision = terrainRevision;
        cachedProjectedRooms = result ?? sourceRooms;
        return cachedProjectedRooms;
    }

    private static bool BakeEquivalent(RoomMapBakeSnapshot a, RoomMapBakeSnapshot b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.Status == b.Status &&
               a.Width == b.Width &&
               a.Height == b.Height &&
               string.Equals(a.Error ?? string.Empty, b.Error ?? string.Empty, StringComparison.Ordinal) &&
               ReferenceEquals(a.Runs, b.Runs) &&
               ReferenceEquals(a.NodeAnchors, b.NodeAnchors);
    }

}
