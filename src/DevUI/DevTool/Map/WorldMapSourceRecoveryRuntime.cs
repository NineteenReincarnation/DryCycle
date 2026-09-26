using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

/// <summary>
/// Repairs the two pieces of vanilla MapObject source generation that disappear when the rebuilt
/// DevTool keeps MapPage's legacy Update/Draw lifecycle quiescent.
///
/// Missing room bounds are recovered directly from the room source header, while missing MapTex
/// rasters are prepared incrementally. Newly-created recovery jobs only run the shortcut mapper,
/// because MapTex generation needs tile/shortcut data but does not need a full AI map bake.
/// This deliberately does not call MapObject.Update(): vanilla's implementation can execute up to
/// 1000 preparation polls with Thread.Sleep(1), which is not acceptable on the editor frame.
/// </summary>
internal static partial class MapRoomGeometryPresentationHub
{
    private const int SourceDimensionReadsPerFrame = 12;
    private const int ShortcutMapperMinStepsPerFrame = 64;
    private const int ShortcutMapperMaxStepsPerFrame = 512;
    private const float ShortcutMapperBudgetSeconds = 0.0015f;

    private static readonly HashSet<int> sourceDimensionsResolved = new();
    private static global::World sourceRecoveryWorld;
    private static MapObject sourceRecoveryMapObject;
    private static bool sourceRecoveryMapActive;
    private static int sourceDimensionCursor;
    private static int lastSourceRecoveryFrame = -1;
    private static readonly Dictionary<int, int> recoveryRetryFrame = new();
    private static readonly Dictionary<int, byte> recoveryFailureCount = new();
    private static readonly Dictionary<int, int> recoveryRoomIndices = new();
    private static long recoveryPerfTotalTicks;
    private static long recoveryPerfPeakTicks;
    private static int recoveryPerfSamples;
    private static int recoveryPerfCompletedRooms;
    private static long recoverySessionStartedTicks;
    private static long recoverySessionCompletedTicks;
    private static int recoverySessionInitialMissing;
    private static int recoverySessionRemaining;
    private static bool recoverySessionComplete;
    private static int recoverySessionNextCompletionCheckFrame;

    internal static int SourceRecoveryBackoffCount => recoveryRetryFrame.Count;
    internal static int SourceRecoveryCompletedRooms => recoveryPerfCompletedRooms;
    internal static double SourceRecoveryAverageMilliseconds =>
        recoveryPerfSamples <= 0
            ? 0d
            : recoveryPerfTotalTicks * 1000d / Stopwatch.Frequency / recoveryPerfSamples;
    internal static double SourceRecoveryPeakMilliseconds =>
        recoveryPerfPeakTicks * 1000d / Stopwatch.Frequency;
    internal static int SourceRecoverySessionInitialMissingRooms =>
        recoverySessionInitialMissing;
    internal static int SourceRecoverySessionRemainingRooms =>
        recoverySessionRemaining;
    internal static bool SourceRecoverySessionComplete =>
        recoverySessionComplete;
    internal static bool SourceRecoverySessionReadyAtEntry =>
        recoverySessionStartedTicks > 0L &&
        recoverySessionInitialMissing == 0;
    internal static double SourceRecoverySessionElapsedMilliseconds
    {
        get
        {
            long started = recoverySessionStartedTicks;
            if (started <= 0L) return 0d;
            long ended =
                recoverySessionCompletedTicks > 0L
                    ? recoverySessionCompletedTicks
                    : Stopwatch.GetTimestamp();
            return Math.Max(0L, ended - started) * 1000d / Stopwatch.Frequency;
        }
    }

    internal static void RecoverMissingSources(EditorSession session)
    {
        if (lastSourceRecoveryFrame == Time.frameCount) return;
        lastSourceRecoveryFrame = Time.frameCount;

        MapPage page = session?.Owner?.activePage as MapPage;
        if (page?.world == null || page.map == null)
        {
            // Never orphan an already-started vanilla preparer. Recovery jobs created below do not
            // own worker threads, but a MapObject may already contain one started by legacy code.
            // Keep polling only that active job while Map is dormant and never begin another room.
            if (sourceRecoveryMapObject?.roomPrep != null)
                AdvanceMapTexturePreparation(sourceRecoveryMapObject, allowStartNew: false);
            else
                sourceRecoveryMapObject = null;

            sourceRecoveryMapActive = false;
            return;
        }

        // Do not orphan a preparer if DevUI replaces the MapPage/MapObject while changing context.
        if (sourceRecoveryMapObject != null &&
            !ReferenceEquals(sourceRecoveryMapObject, page.map) &&
            sourceRecoveryMapObject.roomPrep != null)
        {
            AdvanceMapTexturePreparation(sourceRecoveryMapObject, allowStartNew: false);
            return;
        }

        bool newMapSession = !sourceRecoveryMapActive ||
                             !ReferenceEquals(sourceRecoveryWorld, page.world) ||
                             !ReferenceEquals(sourceRecoveryMapObject, page.map);
        if (newMapSession)
        {
            sourceRecoveryWorld = page.world;
            sourceRecoveryMapObject = page.map;
            sourceRecoveryMapActive = true;
            sourceDimensionsResolved.Clear();
            recoveryRetryFrame.Clear();
            recoveryFailureCount.Clear();
            recoveryRoomIndices.Clear();
            for (int i = 0; i < page.map.roomReps.Length; i++)
                if (page.map.roomReps[i]?.room != null) recoveryRoomIndices[page.map.roomReps[i].room.index] = i;
            sourceDimensionCursor = 0;
            BeginSourceRecoverySession(page.map);
        }

        // Prime() owns cache structure creation. If plugin Update happens before Prime on the first
        // frame, simply wait for the following frame rather than inventing another room registry.
        if (cache.Count > 0)
            RecoverSourceDimensions(page.world);

        AdvanceMapTexturePreparation(page.map, allowStartNew: true);
    }

    internal static void ResetSourceRecovery()
    {
        sourceRecoveryWorld = null;
        sourceDimensionsResolved.Clear();
        recoveryRetryFrame.Clear();
        recoveryFailureCount.Clear();
        recoveryRoomIndices.Clear();
        recoveryPerfTotalTicks = 0L;
        recoveryPerfPeakTicks = 0L;
        recoveryPerfSamples = 0;
        recoveryPerfCompletedRooms = 0;
        recoverySessionStartedTicks = 0L;
        recoverySessionCompletedTicks = 0L;
        recoverySessionInitialMissing = 0;
        recoverySessionRemaining = 0;
        recoverySessionComplete = false;
        recoverySessionNextCompletionCheckFrame = 0;
        sourceDimensionCursor = 0;
        lastSourceRecoveryFrame = -1;
        sourceRecoveryMapActive = false;
        if (sourceRecoveryMapObject?.roomPrep == null)
            sourceRecoveryMapObject = null;
    }

    private static void RecoverSourceDimensions(global::World world)
    {
        int count = roomOrder.Count;
        if (count <= 0) return;
        if (sourceDimensionCursor >= count) sourceDimensionCursor = 0;

        int reads = 0;
        int inspected = 0;
        while (inspected < count && reads < SourceDimensionReadsPerFrame)
        {
            if (sourceDimensionCursor >= count) sourceDimensionCursor = 0;
            int roomIndex = roomOrder[sourceDimensionCursor++];
            inspected++;

            if (sourceDimensionsResolved.Contains(roomIndex) ||
                !cache.TryGetValue(roomIndex, out CacheEntry entry) ||
                entry?.Room == null)
                continue;

            MapObject.RoomRepresentation roomRep = entry.RoomRep;
            if (roomRep?.texture != null || roomRep?.mapTex != null)
            {
                RefreshDimensions(entry, roomRep);
                Publish(entry, allowRasterReadback: false);
                sourceDimensionsResolved.Add(roomIndex);
                continue;
            }

            reads++;
            if (TryReadSourceDimensions(world, entry.Room, out int width, out int height))
                ApplyRecoveredDimensions(entry, width, height);

            // A missing/malformed source will not become valid repeatedly during the same Map
            // session. Avoid reopening the same file every few frames; re-entering Map or changing
            // World/context resets this set and gives late replacements another chance.
            sourceDimensionsResolved.Add(roomIndex);
        }
    }

    private static bool TryReadSourceDimensions(
        global::World world,
        AbstractRoom room,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        string path = ResolveRoomGeometryPath(world, room);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            using StreamReader reader = new(path);
            _ = reader.ReadLine();
            string dimensionsLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(dimensionsLine)) return false;

            string sizePart = dimensionsLine.Split('|')[0];
            string[] size = sizePart.Split('*');
            if (size.Length < 2 ||
                !int.TryParse(size[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(size[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) ||
                width <= 0 || height <= 0)
            {
                width = 0;
                height = 0;
                return false;
            }

            return true;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap room-size recovery failed for " + (room?.name ?? "?") + ": " + error.Message);
            width = 0;
            height = 0;
            return false;
        }
    }

    private static void ApplyRecoveredDimensions(CacheEntry entry, float width, float height)
    {
        if (entry == null || width <= 0f || height <= 0f) return;
        if (Math.Abs(entry.WidthTiles - width) < 0.001f &&
            Math.Abs(entry.HeightTiles - height) < 0.001f)
            return;

        entry.WidthTiles = width;
        entry.HeightTiles = height;
        entry.CurvesInitialized = false;
        entry.Revision++;
        PersistentMarkDirty();
        Publish(entry, allowRasterReadback: false);
    }

    private static void ApplyPreparedRoomDimensions(global::Room room)
    {
        if (room?.abstractRoom == null || room.TileWidth <= 0 || room.TileHeight <= 0) return;
        int roomIndex = room.abstractRoom.index;
        sourceDimensionsResolved.Add(roomIndex);
        if (cache.TryGetValue(roomIndex, out CacheEntry entry))
            ApplyRecoveredDimensions(entry, room.TileWidth, room.TileHeight);
    }

    private static void AdvanceMapTexturePreparation(MapObject mapObject, bool allowStartNew)
    {
        if (mapObject?.world == null || mapObject.roomReps == null || mapObject.roomReps.Length == 0)
            return;

        int roomCount = Math.Min(mapObject.world.NumberOfRooms, mapObject.roomReps.Length);
        if (roomCount <= 0) return;
        if (mapObject.roomLoaderIndex < 0) mapObject.roomLoaderIndex = 0;

        long perfStarted = Stopwatch.GetTimestamp();
        int completed = 0;
        bool didWork = false;
        try
        {
            // Bound the whole queue as well as each incremental shortcut-mapping job.
            int guard = roomCount + 1;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2 / 1000;
            while (guard-- > 0)
            {
                // One budget for the entire queue, not a fresh 1.5 ms budget for every room.
                if (completed >= 2 || Stopwatch.GetTimestamp() >= deadline) return;
                if (mapObject.roomPrep != null)
                {
                    didWork = true;
                    RoomPreparer preparer = mapObject.roomPrep;
                    ApplyPreparedRoomDimensions(preparer.room);

                    try
                    {
                        AdvancePreparerOneFrame(preparer);
                        ApplyPreparedRoomDimensions(preparer.room);
                    }
                    catch (Exception error)
                    {
                        preparer.failed = true;
                        preparer.done = true;
                        global::DryCycle.Plugin.Logger?.LogWarning(
                            "WorldMap RoomPreparer update failed for " +
                            (preparer.room?.abstractRoom?.name ?? "?") + ": " + error);
                    }

                    if (!preparer.done) return;
                    FinishPreparedMapTexture(mapObject, preparer, roomCount);
                    completed++;
                    if (!allowStartNew) return;
                    continue;
                }

                if (!allowStartNew || !SelectNextMissingRoom(mapObject, roomCount)) return;

                didWork = true;
                int localIndex = mapObject.roomLoaderIndex;
                MapObject.RoomRepresentation roomRep = mapObject.roomReps[localIndex];
                if (roomRep == null || roomRep.room == null)
                {
                    mapObject.roomLoaderIndex++;
                    completed++;
                    continue;
                }

                if (roomRep.texture != null || roomRep.mapTex != null)
                {
                    ClearRecoveryFailure(roomRep.room.index);
                    RefreshRecoveredRoomEntry(roomRep);
                    mapObject.roomLoaderIndex++;
                    continue;
                }

                global::Room realized = roomRep.room.realizedRoom;
                if (realized != null && realized.readyForAI)
                {
                    try
                    {
                        roomRep.CreateMapTexture(realized);
                        MarkVanillaMapRoomRefresh(mapObject, localIndex);
                        RefreshRecoveredRoomEntry(roomRep);
                    }
                    catch (Exception error)
                    {
                        global::DryCycle.Plugin.Logger?.LogWarning(
                            "WorldMap realized-room MapTex recovery failed for " + roomRep.room.name + ": " + error);
                    }

                    if (roomRep.texture == null && roomRep.mapTex == null)
                        NoteRecoveryFailure(roomRep.room.index);
                    else
                        ClearRecoveryFailure(roomRep.room.index);
                    mapObject.roomLoaderIndex++;
                    completed++;
                    continue;
                }

                try
                {
                    global::Room preparedRoom = new(null, mapObject.world, roomRep.room);

                    // MapTex only needs loaded tiles and shortcut metadata. Running AImapper and
                    // heatmap decompression here duplicates room-load work for no visual gain.
                    RoomPreparer preparer = new(
                        preparedRoom,
                        loadAiHeatMaps: false,
                        falseBake: false,
                        shortcutsOnly: true);
                    mapObject.roomPrep = preparer;
                    ApplyPreparedRoomDimensions(preparedRoom);

                    AdvancePreparerOneFrame(preparer);
                    if (!preparer.done) return;

                    FinishPreparedMapTexture(mapObject, preparer, roomCount);
                    completed++;
                }
                catch (Exception error)
                {
                    global::DryCycle.Plugin.Logger?.LogWarning(
                        "WorldMap could not start MapTex recovery for " + roomRep.room.name + ": " + error);
                    mapObject.roomPrep = null;
                    NoteRecoveryFailure(roomRep.room.index);
                    mapObject.roomLoaderIndex++;
                }
            }
        }
        finally
        {
            // Do not dilute the diagnostic with thousands of idle no-op frames after the queue has
            // completed. The average/peak should describe actual thumbnail recovery work.
            if (didWork)
            {
                long elapsed = Math.Max(0L, Stopwatch.GetTimestamp() - perfStarted);
                recoveryPerfTotalTicks += elapsed;
                recoveryPerfPeakTicks = Math.Max(recoveryPerfPeakTicks, elapsed);
                recoveryPerfSamples++;
                recoveryPerfCompletedRooms += completed;
            }

            UpdateSourceRecoverySession(mapObject, roomCount, force: didWork || completed > 0);
        }
    }

    private static void BeginSourceRecoverySession(MapObject mapObject)
    {
        recoverySessionStartedTicks = Stopwatch.GetTimestamp();
        recoverySessionCompletedTicks = 0L;
        recoverySessionNextCompletionCheckFrame = 0;
        recoverySessionInitialMissing = CountMissingMapTextures(mapObject);
        recoverySessionRemaining = recoverySessionInitialMissing;
        recoverySessionComplete = recoverySessionRemaining == 0;
        if (recoverySessionComplete)
            recoverySessionCompletedTicks = recoverySessionStartedTicks;
    }

    private static void UpdateSourceRecoverySession(
        MapObject mapObject,
        int roomCount,
        bool force)
    {
        if (recoverySessionStartedTicks <= 0L || recoverySessionComplete)
            return;
        if (!force &&
            Time.frameCount < recoverySessionNextCompletionCheckFrame)
            return;

        recoverySessionNextCompletionCheckFrame =
            Time.frameCount + 15;
        recoverySessionRemaining =
            CountMissingMapTextures(mapObject, roomCount);

        if (recoverySessionRemaining != 0 ||
            mapObject?.roomPrep != null)
            return;

        recoverySessionComplete = true;
        recoverySessionCompletedTicks = Stopwatch.GetTimestamp();

        global::DryCycle.Plugin.Logger?.LogInfo(
            "WorldMap thumbnail recovery complete: " +
            recoverySessionInitialMissing + " room(s) required recovery, " +
            SourceRecoverySessionElapsedMilliseconds.ToString("F0") +
            " ms total, avg active frame " +
            SourceRecoveryAverageMilliseconds.ToString("F2") +
            " ms, peak active frame " +
            SourceRecoveryPeakMilliseconds.ToString("F2") +
            " ms.");
    }

    private static int CountMissingMapTextures(
        MapObject mapObject,
        int explicitCount = -1)
    {
        if (mapObject?.roomReps == null)
            return 0;

        int count =
            explicitCount >= 0
                ? Math.Min(explicitCount, mapObject.roomReps.Length)
                : mapObject.world != null
                    ? Math.Min(mapObject.world.NumberOfRooms, mapObject.roomReps.Length)
                    : mapObject.roomReps.Length;
        int missing = 0;
        for (int i = 0; i < count; i++)
        {
            MapObject.RoomRepresentation roomRep = mapObject.roomReps[i];
            if (roomRep?.room == null)
                continue;
            if (roomRep.texture == null &&
                roomRep.mapTex == null)
                missing++;
        }
        return missing;
    }

    private static bool SelectNextMissingRoom(MapObject mapObject, int count)
    {
        bool Missing(int local) => local >= 0 && local < count && mapObject.roomReps[local]?.room != null &&
            mapObject.roomReps[local].texture == null && mapObject.roomReps[local].mapTex == null &&
            RecoveryRetryReady(mapObject.roomReps[local].room.index);
        for (int p = 0; p < priorityRooms.Count; p++)
            if (recoveryRoomIndices.TryGetValue(priorityRooms[p], out int local) && Missing(local))
            { mapObject.roomLoaderIndex = local; return true; }
        int start = mapObject.roomLoaderIndex % count;
        for (int step = 0; step < count; step++)
        {
            int i = (start + step) % count;
            if (!Missing(i)) continue;
            mapObject.roomLoaderIndex = i; return true;
        }
        return false;
    }

    private static bool RecoveryRetryReady(int roomIndex) =>
        !recoveryRetryFrame.TryGetValue(roomIndex, out int retryFrame) ||
        Time.frameCount >= retryFrame;

    private static void NoteRecoveryFailure(int roomIndex)
    {
        if (roomIndex < 0) return;
        recoveryFailureCount.TryGetValue(roomIndex, out byte failures);
        failures = (byte)Math.Min(byte.MaxValue, failures + 1);
        recoveryFailureCount[roomIndex] = failures;

        int delay =
            failures <= 1 ? 30 :
            failures == 2 ? 120 :
            600;
        recoveryRetryFrame[roomIndex] = Time.frameCount + delay;
    }

    private static void ClearRecoveryFailure(int roomIndex)
    {
        if (roomIndex < 0) return;
        recoveryRetryFrame.Remove(roomIndex);
        recoveryFailureCount.Remove(roomIndex);
    }

    private static void AdvancePreparerOneFrame(RoomPreparer preparer)
    {
        if (preparer == null || preparer.done) return;

        // Recovery jobs created by this class intentionally never call RoomPreparer.Update(). That
        // method starts a worker thread which then busy-waits for main-thread handshakes. Driving the
        // ShortcutMapper directly gives us deterministic frame cost and cannot strand a worker when
        // the user closes or switches the Map page.
        if (preparer.shortcutsOnly && preparer.thread == null)
        {
            AdvanceShortcutOnlyPreparer(preparer);
            return;
        }

        // Respect a preparer that legacy MapObject already started before the rebuilt lifecycle took
        // ownership. It may be a full AI preparation job, so only issue one vanilla poll this frame.
        preparer.Update();
    }

    private static void AdvanceShortcutOnlyPreparer(RoomPreparer preparer)
    {
        ShortcutMapper mapper = preparer.scMapper;
        if (mapper == null)
        {
            preparer.done = true;
            return;
        }

        float startedAt = Time.realtimeSinceStartup;
        int steps = 0;
        while (!mapper.done && steps < ShortcutMapperMaxStepsPerFrame)
        {
            mapper.Update();
            steps++;

            if (steps >= ShortcutMapperMinStepsPerFrame &&
                Time.realtimeSinceStartup - startedAt >= ShortcutMapperBudgetSeconds)
                break;
        }

        if (!mapper.done) return;

        preparer.scMapper = null;
        preparer.room.ShortCutsReady();

        // RoomRepresentation.CreateMapTexture gates on readyForAI even though its actual data needs
        // are tiles, water and shortcut metadata. This Room is detached (game == null) and discarded
        // immediately after MapTex creation, so promote only its local loading marker instead of
        // performing an unused AImapper/heatmap bake.
        if (preparer.room.loadingProgress < 2)
            preparer.room.loadingProgress = 2;

        preparer.status = Math.Max(preparer.status, 1);
        preparer.threadFinished = true;
        preparer.finished = true;
        preparer.done = true;
    }

    private static void FinishPreparedMapTexture(
        MapObject mapObject,
        RoomPreparer preparer,
        int roomCount)
    {
        int localIndex = mapObject.roomLoaderIndex;
        if (localIndex < 0 || localIndex >= roomCount)
        {
            mapObject.roomPrep = null;
            mapObject.roomLoaderIndex = roomCount;
            return;
        }

        MapObject.RoomRepresentation roomRep = mapObject.roomReps[localIndex];
        try
        {
            if (!preparer.failed)
            {
                if (preparer.room != null && preparer.room.shortCutsReady && !preparer.room.readyForAI)
                    preparer.room.loadingProgress = 2;
                roomRep?.CreateMapTexture(preparer.room);
            }
            else
            {
                global::DryCycle.Plugin.Logger?.LogWarning(
                    "WorldMap RoomPreparer reported failure for " +
                    (roomRep?.room?.name ?? preparer.room?.abstractRoom?.name ?? "?"));
            }

            MarkVanillaMapRoomRefresh(mapObject, localIndex);
            RefreshRecoveredRoomEntry(roomRep);
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "WorldMap MapTex finalization failed for " +
                (roomRep?.room?.name ?? "?") + ": " + error);
        }
        finally
        {
            if (roomRep?.room != null)
            {
                if (roomRep.texture == null && roomRep.mapTex == null)
                    NoteRecoveryFailure(roomRep.room.index);
                else
                    ClearRecoveryFailure(roomRep.room.index);
            }
            mapObject.roomPrep = null;
            mapObject.roomLoaderIndex = localIndex + 1;
        }
    }

    private static void MarkVanillaMapRoomRefresh(MapObject mapObject, int localIndex)
    {
        if (mapObject?.toRefreshRooms == null ||
            localIndex < 0 || localIndex >= mapObject.toRefreshRooms.Length)
            return;
        mapObject.toRefreshRooms[localIndex] = 2;
    }

    private static void RefreshRecoveredRoomEntry(MapObject.RoomRepresentation roomRep)
    {
        if (roomRep?.room == null || !cache.TryGetValue(roomRep.room.index, out CacheEntry entry)) return;
        RefreshDimensions(entry, roomRep);
        RefreshNodes(entry, roomRep, force: true);
        entry.NextRasterPollFrame = 0;
        Publish(entry, allowRasterReadback: false);
    }
}
