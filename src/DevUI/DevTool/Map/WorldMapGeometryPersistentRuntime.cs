using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

internal static partial class MapRoomGeometryPresentationHub
{
    private const float PersistentSaveDebounceSeconds = 4f;

    private sealed class PersistentEntryState
    {
        internal bool RestoreAttempted;
        internal bool RasterRestored;
        internal bool NodesRestored;
        internal MapViewFileStamp RoomSource;
        internal MapViewFileStamp SettingsSource;
        internal string RasterSignature = string.Empty;
    }

    private static readonly Dictionary<string, MapViewPersistentRoom> persistentRooms =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, PersistentEntryState> persistentEntryStates = new();

    private static global::World persistentWorld;
    private static string persistentContextKey = string.Empty;
    private static string persistentCachePath = string.Empty;
    private static long persistentTemplateFingerprint;
    private static long loadedTemplateFingerprint = long.MinValue;
    private static bool persistentDirty;
    private static float persistentDirtyAt;

    private static bool PersistentContextChanged(global::World world)
    {
        string next = BuildPersistentContextKey(world);
        return !string.Equals(persistentContextKey, next, StringComparison.Ordinal);
    }

    private static void PersistentBeforeRegionReset()
    {
        PersistentTryScheduleSave(force: true);
        persistentRooms.Clear();
        persistentEntryStates.Clear();
    }

    private static void PersistentAfterRegionReset(global::World world)
    {
        persistentWorld = world;
        persistentContextKey = BuildPersistentContextKey(world);
        persistentCachePath = MapViewPersistentCacheStore.ResolveCachePath(persistentContextKey);
        persistentTemplateFingerprint = ComputePersistentTemplateFingerprint(world?.region);
        loadedTemplateFingerprint = long.MinValue;
        persistentDirty = false;
        persistentDirtyAt = 0f;

        MapViewPersistentSnapshot snapshot =
            MapViewPersistentCacheStore.Load(persistentCachePath, persistentContextKey);
        if (snapshot == null) return;

        loadedTemplateFingerprint = snapshot.TemplateFingerprint;
        for (int i = 0; i < snapshot.Rooms.Count; i++)
        {
            MapViewPersistentRoom room = snapshot.Rooms[i];
            if (room == null || string.IsNullOrWhiteSpace(room.RoomName)) continue;
            persistentRooms[room.RoomName] = room;
        }

        global::DryCycle.Plugin.Logger?.LogInfo(
            "WorldMap persistent cache loaded (" + persistentRooms.Count + " rooms).");
    }

    private static void PersistentBeforeClear()
    {
        PersistentTryScheduleSave(force: true);
        persistentRooms.Clear();
        persistentEntryStates.Clear();
        persistentWorld = null;
        persistentContextKey = string.Empty;
        persistentCachePath = string.Empty;
        persistentTemplateFingerprint = 0L;
        loadedTemplateFingerprint = long.MinValue;
        persistentDirty = false;
        persistentDirtyAt = 0f;
    }

    private static void PersistentTryRestore(
        CacheEntry entry,
        global::World world,
        AbstractRoom room,
        MapObject.RoomRepresentation roomRep)
    {
        if (entry == null || room == null) return;

        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        if (state.RestoreAttempted) return;
        state.RestoreAttempted = true;

        if (string.IsNullOrWhiteSpace(entry.RoomName) ||
            !persistentRooms.TryGetValue(entry.RoomName, out MapViewPersistentRoom stored))
            return;

        MapViewFileStamp roomSource = MapViewFileStamp.Capture(ResolveRoomGeometryPath(world, room));
        MapViewFileStamp settingsSource = MapViewFileStamp.Capture(ResolveRoomSettingsPath(world, room));
        bool roomValid = !string.IsNullOrWhiteSpace(roomSource.Path) && roomSource.Matches(stored.RoomSource);
        bool settingsValid = settingsSource.Matches(stored.SettingsSource);
        bool templateValid = loadedTemplateFingerprint == persistentTemplateFingerprint;
        bool restored = false;

        state.RoomSource = roomSource;
        state.SettingsSource = settingsSource;
        state.RasterSignature = stored.RasterSignature ?? string.Empty;

        if (roomValid)
        {
            entry.WidthTiles = Math.Max(1f, stored.WidthTiles);
            entry.HeightTiles = Math.Max(1f, stored.HeightTiles);

            if (stored.RasterInitialized)
            {
                entry.RasterInitialized = true;
                entry.RasterWidth = stored.RasterWidth;
                entry.RasterHeight = stored.RasterHeight;
                entry.BaseRasterRuns = stored.BaseRasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
                entry.NextRasterPollFrame = Time.frameCount + RasterPollIntervalFrames + Math.Abs(entry.RoomIndex % 37);
                state.RasterRestored = true;
                restored = true;
            }

            if (stored.NodesInitialized)
            {
                entry.NodesInitialized = true;
                entry.NodeFingerprint = stored.NodeFingerprint;
                entry.Nodes = stored.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
                entry.NextNodePollFrame = Time.frameCount + NodePollIntervalFrames + Math.Abs(entry.RoomIndex % 11);
                state.NodesRestored = true;
                restored = true;
            }
        }

        if (roomValid && settingsValid && templateValid && stored.CurvesInitialized)
        {
            entry.CurvesInitialized = true;
            entry.SettingsFingerprint = stored.SettingsFingerprint;
            entry.SettingsPath = settingsSource.Path;
            entry.SettingsWriteTimeUtc = settingsSource.WriteTicks > 0L
                ? new DateTime(settingsSource.WriteTicks, DateTimeKind.Utc)
                : DateTime.MinValue;
            entry.NextSettingsPollFrame = Time.frameCount + SettingsPollIntervalFrames + Math.Abs(entry.RoomIndex % 30);
            entry.NextLiveSettingsPollFrame = Time.frameCount + LiveSettingsPollIntervalFrames;
            entry.TerrainFillRuns = stored.TerrainFillRuns ?? Array.Empty<EditorMapRectSnapshot>();
            entry.Curves = stored.Curves ?? Array.Empty<EditorMapPolylineSnapshot>();
            restored = true;
        }

        if (restored)
            entry.Revision++;
    }

    private static bool PersistentTryBindRestoredRaster(
        CacheEntry entry,
        MapObject.RoomRepresentation roomRep,
        RasterSourceInfo source)
    {
        if (entry == null || !entry.RasterInitialized) return false;
        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        if (!state.RasterRestored) return false;

        string currentSignature = BuildPersistentRasterSignature(roomRep, source);
        bool compatible = entry.RasterWidth == source.Width &&
                          entry.RasterHeight == source.Height &&
                          string.Equals(state.RasterSignature, currentSignature, StringComparison.Ordinal);

        state.RasterRestored = false;
        if (!compatible)
        {
            entry.RasterInitialized = false;
            entry.BaseRasterRuns = Array.Empty<EditorMapRectSnapshot>();
            state.RasterSignature = currentSignature;
            entry.Revision++;
            PersistentMarkDirty();
            return false;
        }

        entry.RasterSourceKey = source.SourceKey;
        entry.NextRasterPollFrame = Time.frameCount + RasterPollIntervalFrames + Math.Abs(entry.RoomIndex % 37);
        state.RasterSignature = currentSignature;
        return true;
    }

    private static void PersistentOnRasterRebuilt(
        CacheEntry entry,
        MapObject.RoomRepresentation roomRep,
        RasterSourceInfo source)
    {
        if (entry == null) return;
        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        state.RasterRestored = false;
        state.RasterSignature = BuildPersistentRasterSignature(roomRep, source);
        state.RoomSource = MapViewFileStamp.Capture(ResolveRoomGeometryPath(persistentWorld, entry.Room));
        PersistentMarkDirty();
    }

    private static bool PersistentShouldKeepRestoredNodes(
        CacheEntry entry,
        Vector2[] positions)
    {
        if (entry == null || !entry.NodesInitialized) return false;
        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        if (!state.NodesRestored) return false;

        if (HasMeaningfulNodePositions(positions))
            return false;

        entry.NextNodePollFrame = Time.frameCount + NodePollIntervalFrames + Math.Abs(entry.RoomIndex % 11);
        return true;
    }

    private static void PersistentOnNodesObserved(CacheEntry entry, bool changed)
    {
        if (entry == null) return;
        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        state.NodesRestored = false;
        state.RoomSource = MapViewFileStamp.Capture(ResolveRoomGeometryPath(persistentWorld, entry.Room));
        if (changed) PersistentMarkDirty();
    }

    private static void PersistentPrepareSettingsPath(
        CacheEntry entry,
        global::World world,
        AbstractRoom room)
    {
        if (entry == null || room == null || !entry.CurvesInitialized) return;
        string resolved = ResolveRoomSettingsPath(world, room);
        if (string.Equals(entry.SettingsPath ?? string.Empty, resolved ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return;

        entry.CurvesInitialized = false;
        entry.Curves = Array.Empty<EditorMapPolylineSnapshot>();
        entry.TerrainFillRuns = Array.Empty<EditorMapRectSnapshot>();
        entry.SettingsPath = resolved ?? string.Empty;
        entry.SettingsWriteTimeUtc = FileWriteTime(entry.SettingsPath);
        entry.NextSettingsPollFrame = 0;
        entry.Revision++;
        PersistentMarkDirty();
    }

    private static void PersistentOnCurvesRebuilt(CacheEntry entry)
    {
        if (entry == null) return;
        PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
        state.SettingsSource = MapViewFileStamp.Capture(entry.SettingsPath);
        PersistentMarkDirty();
    }

    private static void PersistentOnRoomInvalidated(CacheEntry entry)
    {
        if (entry != null)
        {
            PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
            state.RasterRestored = false;
            state.NodesRestored = false;
        }
        PersistentMarkDirty();
    }

    private static void PersistentOnRoomRemoved(int roomIndex)
    {
        persistentEntryStates.Remove(roomIndex);
        PersistentMarkDirty();
    }

    private static void PersistentMarkDirty()
    {
        if (string.IsNullOrWhiteSpace(persistentContextKey)) return;
        persistentDirty = true;
        persistentDirtyAt = Time.realtimeSinceStartup;
    }

    private static void PersistentTryScheduleSave(bool force)
    {
        if (!persistentDirty || string.IsNullOrWhiteSpace(persistentCachePath)) return;
        if (!force && Time.realtimeSinceStartup - persistentDirtyAt < PersistentSaveDebounceSeconds) return;

        MapViewPersistentSnapshot snapshot = CapturePersistentSnapshot();
        if (snapshot == null) return;
        persistentDirty = false;
        MapViewPersistentCacheStore.QueueWrite(persistentCachePath, snapshot);
    }

    private static MapViewPersistentSnapshot CapturePersistentSnapshot()
    {
        if (persistentWorld == null || string.IsNullOrWhiteSpace(persistentContextKey)) return null;

        MapViewPersistentSnapshot snapshot = new()
        {
            ContextKey = persistentContextKey,
            TemplateFingerprint = persistentTemplateFingerprint
        };

        foreach (KeyValuePair<int, CacheEntry> pair in cache)
        {
            CacheEntry entry = pair.Value;
            if (entry?.Room == null || string.IsNullOrWhiteSpace(entry.RoomName)) continue;

            PersistentEntryState state = GetPersistentEntryState(entry.RoomIndex);
            if (string.IsNullOrWhiteSpace(state.RoomSource.Path))
                state.RoomSource = MapViewFileStamp.Capture(ResolveRoomGeometryPath(persistentWorld, entry.Room));
            if (string.IsNullOrWhiteSpace(state.SettingsSource.Path))
                state.SettingsSource = MapViewFileStamp.Capture(
                    !string.IsNullOrWhiteSpace(entry.SettingsPath)
                        ? entry.SettingsPath
                        : ResolveRoomSettingsPath(persistentWorld, entry.Room));

            string resolvedSettings = ResolveRoomSettingsPath(persistentWorld, entry.Room);
            bool curvesCurrent = entry.CurvesInitialized &&
                                 string.Equals(
                                     entry.SettingsPath ?? string.Empty,
                                     resolvedSettings ?? string.Empty,
                                     StringComparison.OrdinalIgnoreCase);

            snapshot.Rooms.Add(new MapViewPersistentRoom
            {
                RoomName = entry.RoomName,
                RoomSource = state.RoomSource,
                SettingsSource = curvesCurrent
                    ? MapViewFileStamp.Capture(resolvedSettings)
                    : state.SettingsSource,
                RasterSignature = state.RasterSignature,
                RasterInitialized = entry.RasterInitialized,
                NodesInitialized = entry.NodesInitialized,
                CurvesInitialized = curvesCurrent,
                RasterWidth = entry.RasterWidth,
                RasterHeight = entry.RasterHeight,
                NodeFingerprint = entry.NodeFingerprint,
                SettingsFingerprint = entry.SettingsFingerprint,
                WidthTiles = entry.WidthTiles,
                HeightTiles = entry.HeightTiles,
                BaseRasterRuns = entry.RasterInitialized
                    ? entry.BaseRasterRuns ?? Array.Empty<EditorMapRectSnapshot>()
                    : Array.Empty<EditorMapRectSnapshot>(),
                TerrainFillRuns = curvesCurrent
                    ? entry.TerrainFillRuns ?? Array.Empty<EditorMapRectSnapshot>()
                    : Array.Empty<EditorMapRectSnapshot>(),
                Curves = curvesCurrent
                    ? entry.Curves ?? Array.Empty<EditorMapPolylineSnapshot>()
                    : Array.Empty<EditorMapPolylineSnapshot>(),
                Nodes = entry.NodesInitialized
                    ? entry.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>()
                    : Array.Empty<EditorMapNodeVisualSnapshot>()
            });
        }

        return snapshot;
    }

    private static PersistentEntryState GetPersistentEntryState(int roomIndex)
    {
        if (!persistentEntryStates.TryGetValue(roomIndex, out PersistentEntryState state))
        {
            state = new PersistentEntryState();
            persistentEntryStates[roomIndex] = state;
        }
        return state;
    }

    private static string BuildPersistentContextKey(global::World world)
    {
        string regionName = world?.name ?? string.Empty;
        string mapName = regionName;
        string timeline = string.Empty;
        try
        {
            mapName = WorldLoader.MapNameManipulator(regionName, world?.game) ?? regionName;
            timeline = world?.game?.TimelinePoint?.value ?? string.Empty;
        }
        catch
        {
        }
        return regionName.ToUpperInvariant() + "|" + mapName + "|" + timeline;
    }

    private static string ResolveRoomGeometryPath(global::World world, AbstractRoom room)
    {
        if (world == null || room == null) return string.Empty;
        try
        {
            string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
            return WorldLoader.FindRoomFile(roomName, false, ".txt", false) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveRoomSettingsPath(global::World world, AbstractRoom room)
    {
        if (world == null || room == null) return string.Empty;
        try
        {
            string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
            string path = null;
            SlugcatStats.Timeline timeline = world.game != null ? world.game.TimelinePoint : null;
            if (timeline != null)
                path = WorldLoader.FindRoomFile(roomName, false, "_settings-" + timeline.value + ".txt", false);

            if (path == null && ModManager.MSC && roomName.EndsWith("-2", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = roomName.Substring(0, roomName.Length - 2);
                path = WorldLoader.FindRoomFile(baseName, false, "-2_settings.txt", false);
                if (path == null)
                    path = WorldLoader.FindRoomFile(baseName, false, "_settings.txt", false);
            }
            else if (path == null)
            {
                path = WorldLoader.FindRoomFile(roomName, false, "_settings.txt", false);
            }

            if (path == null)
            {
                string roomPath = WorldLoader.FindRoomFile(roomName, false, ".txt", false);
                if (!string.IsNullOrWhiteSpace(roomPath) && roomPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    path = roomPath.Substring(0, roomPath.Length - 4) + "_settings.txt";
            }

            return path ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long ComputePersistentTemplateFingerprint(Region region)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            RoomSettings[] templates = region?.roomSettingsTemplates;
            hash = PersistentMix(hash, templates?.Length ?? 0);
            if (templates == null) return (long)hash;

            for (int i = 0; i < templates.Length; i++)
            {
                RoomSettings settings = templates[i];
                hash = PersistentMixString(hash, settings?.name);
                MapViewFileStamp stamp = MapViewFileStamp.Capture(settings?.filePath);
                hash = PersistentMixString(hash, stamp.Path);
                hash = PersistentMix(hash, stamp.Length);
                hash = PersistentMix(hash, stamp.WriteTicks);
            }
            return (long)hash;
        }
    }

    private static string BuildPersistentRasterSignature(
        MapObject.RoomRepresentation roomRep,
        RasterSourceInfo source)
    {
        try
        {
            if (roomRep?.mapTex != null)
            {
                FAtlasElement element = roomRep.mapTex;
                Rect uv = element.uvRect;
                Texture texture = element.atlas?.texture;
                return "atlas|" + (element.name ?? string.Empty) + "|" +
                       (texture?.name ?? string.Empty) + "|" +
                       (texture?.width ?? 0) + "x" + (texture?.height ?? 0) + "|" +
                       uv.x.GetHashCode() + "," + uv.y.GetHashCode() + "," +
                       uv.width.GetHashCode() + "," + uv.height.GetHashCode() + "|" +
                       source.Width + "x" + source.Height;
            }

            Texture2D runtimeTexture = roomRep?.texture;
            return "runtime|" + (runtimeTexture?.name ?? string.Empty) + "|" +
                   source.Width + "x" + source.Height;
        }
        catch
        {
            return source.Width + "x" + source.Height;
        }
    }

    private static bool HasMeaningfulNodePositions(Vector2[] positions)
    {
        if (positions == null || positions.Length == 0) return false;
        for (int i = 0; i < positions.Length; i++)
        {
            if (Math.Abs(positions[i].x) > 0.001f || Math.Abs(positions[i].y) > 0.001f)
                return true;
        }
        return false;
    }

    private static ulong PersistentMix(ulong hash, long value)
    {
        unchecked
        {
            hash = (hash ^ (uint)value) * 1099511628211UL;
            hash = (hash ^ (uint)(value >> 32)) * 1099511628211UL;
            return hash;
        }
    }

    private static ulong PersistentMixString(ulong hash, string value)
    {
        unchecked
        {
            if (string.IsNullOrEmpty(value)) return PersistentMix(hash, 0L);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash = (hash ^ (byte)c) * 1099511628211UL;
                hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
            }
            return hash;
        }
    }
}