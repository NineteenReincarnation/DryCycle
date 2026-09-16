using System;
using System.Collections.Generic;
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
/// rasters are prepared through the vanilla RoomPreparer state machine one non-blocking step per
/// frame. This deliberately does not call MapObject.Update(): vanilla's implementation can execute
/// up to 1000 preparation polls with Thread.Sleep(1), which is not acceptable on the editor frame.
/// </summary>
internal static partial class MapRoomGeometryPresentationHub
{
    private const int SourceDimensionReadsPerFrame = 12;

    private static readonly HashSet<int> sourceDimensionsResolved = new();
    private static global::World sourceRecoveryWorld;
    private static MapObject sourceRecoveryMapObject;
    private static bool sourceRecoveryMapActive;
    private static int sourceDimensionCursor;
    private static int lastSourceRecoveryFrame = -1;

    internal static void RecoverMissingSources(EditorSession session)
    {
        if (lastSourceRecoveryFrame == Time.frameCount) return;
        lastSourceRecoveryFrame = Time.frameCount;

        MapPage page = session?.Owner?.activePage as MapPage;
        if (page?.world == null || page.mapObject == null)
        {
            // RoomPreparer owns a worker thread that can busy-wait for its main-thread handshake.
            // If the user leaves Map while one room is being prepared, keep polling that one job
            // until it terminates, but never start another room while Map is dormant.
            if (sourceRecoveryMapObject?.roomPrep != null)
                AdvanceMapTexturePreparation(sourceRecoveryMapObject, allowStartNew: false);
            else
                sourceRecoveryMapObject = null;

            sourceRecoveryMapActive = false;
            return;
        }

        // Do not orphan a preparer if DevUI replaces the MapPage/MapObject while changing context.
        if (sourceRecoveryMapObject != null &&
            !ReferenceEquals(sourceRecoveryMapObject, page.mapObject) &&
            sourceRecoveryMapObject.roomPrep != null)
        {
            AdvanceMapTexturePreparation(sourceRecoveryMapObject, allowStartNew: false);
            return;
        }

        bool newMapSession = !sourceRecoveryMapActive ||
                             !ReferenceEquals(sourceRecoveryWorld, page.world) ||
                             !ReferenceEquals(sourceRecoveryMapObject, page.mapObject);
        if (newMapSession)
        {
            sourceRecoveryWorld = page.world;
            sourceRecoveryMapObject = page.mapObject;
            sourceRecoveryMapActive = true;
            sourceDimensionsResolved.Clear();
            sourceDimensionCursor = 0;
        }

        // Prime() owns cache structure creation. If plugin Update happens before Prime on the first
        // frame, simply wait for the following frame rather than inventing another room registry.
        if (cache.Count > 0)
            RecoverSourceDimensions(page.world);

        AdvanceMapTexturePreparation(page.mapObject, allowStartNew: true);
    }

    internal static void ResetSourceRecovery()
    {
        sourceRecoveryWorld = null;
        sourceDimensionsResolved.Clear();
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
                Publish(entry);
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
        Publish(entry);
    }

    private static void ApplyPreparedRoomDimensions(Room room)
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

        // The loop only skips already-resolved rooms and can finalize at most one active preparer.
        // It never spins a pending preparer: one Update poll is issued and control returns to Unity.
        int guard = roomCount + 1;
        while (guard-- > 0)
        {
            if (mapObject.roomPrep != null)
            {
                RoomPreparer preparer = mapObject.roomPrep;
                ApplyPreparedRoomDimensions(preparer.room);

                try
                {
                    if (!preparer.done)
                        preparer.Update();
                    ApplyPreparedRoomDimensions(preparer.room);
                }
                catch (Exception error)
                {
                    global::DryCycle.Plugin.Logger?.LogWarning(
                        "WorldMap RoomPreparer update failed for " +
                        (preparer.room?.abstractRoom?.name ?? "?") + ": " + error.Message);
                    return;
                }

                if (!preparer.done) return;
                FinishPreparedMapTexture(mapObject, preparer, roomCount);
                if (!allowStartNew) return;
                continue;
            }

            if (!allowStartNew || mapObject.roomLoaderIndex >= roomCount) return;

            int localIndex = mapObject.roomLoaderIndex;
            MapObject.RoomRepresentation roomRep = mapObject.roomReps[localIndex];
            if (roomRep == null || roomRep.room == null)
            {
                mapObject.roomLoaderIndex++;
                continue;
            }

            if (roomRep.texture != null || roomRep.mapTex != null)
            {
                RefreshRecoveredRoomEntry(roomRep);
                mapObject.roomLoaderIndex++;
                continue;
            }

            Room realized = roomRep.room.realizedRoom;
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
                        "WorldMap realized-room MapTex recovery failed for " + roomRep.room.name + ": " + error.Message);
                }

                mapObject.roomLoaderIndex++;
                continue;
            }

            try
            {
                Room preparedRoom = new(null, mapObject.world, roomRep.room);
                RoomPreparer preparer = new(preparedRoom, loadAiHeatMaps: true, falseBake: false, shortcutsOnly: false);
                mapObject.roomPrep = preparer;
                ApplyPreparedRoomDimensions(preparedRoom);

                if (!preparer.done)
                    preparer.Update();
                if (!preparer.done) return;

                FinishPreparedMapTexture(mapObject, preparer, roomCount);
            }
            catch (Exception error)
            {
                global::DryCycle.Plugin.Logger?.LogWarning(
                    "WorldMap could not start MapTex recovery for " + roomRep.room.name + ": " + error.Message);
                mapObject.roomPrep = null;
                mapObject.roomLoaderIndex++;
            }
        }
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
                roomRep?.CreateMapTexture(preparer.room);
            else
                global::DryCycle.Plugin.Logger?.LogWarning(
                    "WorldMap RoomPreparer reported failure for " +
                    (roomRep?.room?.name ?? preparer.room?.abstractRoom?.name ?? "?"));

            MarkVanillaMapRoomRefresh(mapObject, localIndex);
            RefreshRecoveredRoomEntry(roomRep);
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "WorldMap MapTex finalization failed for " +
                (roomRep?.room?.name ?? "?") + ": " + error.Message);
        }
        finally
        {
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
        Publish(entry);
    }
}
