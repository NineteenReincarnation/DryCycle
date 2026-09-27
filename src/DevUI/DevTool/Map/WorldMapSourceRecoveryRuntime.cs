using System;
using System.Collections.Generic;
using System.Diagnostics;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

internal static partial class MapRoomGeometryPresentationHub
{
    private static readonly WorldMapRoomSourceQueue sourceQueue = new();
    private static MapObject sourceRecoveryMapObject;
    private static int sourceDimensionCursor, lastSourceRecoveryFrame = -1;
    private static readonly HashSet<int> recoveryFailures = new();
    private static long recoveryPerfTotalTicks, recoveryPerfPeakTicks;
    private static int recoveryPerfSamples, recoveryPerfCompletedRooms;
    private static long recoverySessionStartedTicks, recoverySessionCompletedTicks;
    private static int recoverySessionInitialMissing, recoverySessionRemaining;
    private static bool recoverySessionComplete;

    internal static int SourceRecoveryBackoffCount => recoveryFailures.Count;
    internal static int SourceRecoveryCompletedRooms => recoveryPerfCompletedRooms;
    internal static double SourceRecoveryAverageMilliseconds => recoveryPerfSamples == 0 ? 0d :
        recoveryPerfTotalTicks * 1000d / Stopwatch.Frequency / recoveryPerfSamples;
    internal static double SourceRecoveryPeakMilliseconds => recoveryPerfPeakTicks * 1000d / Stopwatch.Frequency;
    internal static int SourceRecoverySessionInitialMissingRooms => recoverySessionInitialMissing;
    internal static int SourceRecoverySessionRemainingRooms => recoverySessionRemaining;
    internal static bool SourceRecoverySessionComplete => recoverySessionComplete;
    internal static bool SourceRecoverySessionReadyAtEntry => recoverySessionStartedTicks > 0 && recoverySessionInitialMissing == 0;
    internal static double SourceRecoverySessionElapsedMilliseconds => recoverySessionStartedTicks == 0 ? 0d :
        Math.Max(0L, (recoverySessionCompletedTicks > 0 ? recoverySessionCompletedTicks : Stopwatch.GetTimestamp()) -
            recoverySessionStartedTicks) * 1000d / Stopwatch.Frequency;

    internal static bool TryGetDecodedRoomSource(int roomIndex, out WorldMapRoomSource source)
    {
        source = cache.TryGetValue(roomIndex, out CacheEntry entry) ? entry.DecodedSource : null;
        return source != null;
    }

    private static bool EnsureDecodedRoomSource(CacheEntry entry, global::World world)
    {
        if (entry?.Room == null || entry.Room.offScreenDen) return false;
        int frame = Time.frameCount;
        if (frame >= entry.NextSourceAuditFrame)
        {
            entry.SourceRoomStamp = MapViewFileStamp.Capture(ResolveRoomGeometryPath(world, entry.Room));
            entry.SourceSettingsStamp = MapViewFileStamp.Capture(ResolveRoomSettingsPath(world, entry.Room));
            entry.NextSourceAuditFrame = frame + 240 + Math.Abs(entry.RoomIndex % 37);
            entry.SourceFailed = false;
            if (entry.DecodedSource != null &&
                (!entry.DecodedSource.RoomStamp.Matches(entry.SourceRoomStamp) ||
                 !entry.DecodedSource.SettingsStamp.Matches(entry.SourceSettingsStamp)))
                entry.DecodedSource = null;
        }
        if (entry.DecodedSource != null) return true;
        if (entry.SourceFailed) return false;
        if (entry.SourceRoomStamp.Length <= 0)
        {
            entry.SourceFailed = true;
            if (recoveryFailures.Add(entry.RoomIndex))
                global::DryCycle.Plugin.Logger?.LogWarning("WorldMap room source not found: " + entry.RoomName);
            return false;
        }
        if (!sourceQueue.TryGet(entry.RoomIndex, entry.RoomName, entry.SourceRoomStamp, entry.SourceSettingsStamp,
                out WorldMapRoomSource decoded, out Exception error))
        {
            if (error != null)
            {
                entry.SourceFailed = true;
                recoveryFailures.Add(entry.RoomIndex);
                global::DryCycle.Plugin.Logger?.LogError("WorldMap background room load failed for " + entry.RoomName + ": " + error);
            }
            return false;
        }
        entry.DecodedSource = decoded;
        recoveryFailures.Remove(entry.RoomIndex);
        entry.WidthTiles = decoded.Bake.Width;
        entry.HeightTiles = decoded.Bake.Height;
        entry.Revision++;
        return true;
    }

    internal static void RecoverMissingSources(EditorSession session)
    {
        if (lastSourceRecoveryFrame == Time.frameCount) return;
        lastSourceRecoveryFrame = Time.frameCount;
        MapPage page = session?.Owner?.activePage as MapPage;
        // A preparer started by vanilla owns a thread/handshake. Poll it once, never wait/spin.
        if (sourceRecoveryMapObject?.roomPrep != null)
        {
            RoomPreparer prep = sourceRecoveryMapObject.roomPrep;
            prep.Update();
            if (prep.done) sourceRecoveryMapObject.roomPrep = null;
            else return;
        }
        if (page?.world == null || page.map == null) return;
        if (!ReferenceEquals(persistentWorld, page.world)) return;
        if (!ReferenceEquals(sourceRecoveryMapObject, page.map))
        {
            sourceRecoveryMapObject = page.map;
            recoverySessionStartedTicks = Stopwatch.GetTimestamp();
            recoverySessionCompletedTicks = 0;
            recoverySessionInitialMissing = CountMissingMapTextures(page.map);
            recoverySessionRemaining = recoverySessionInitialMissing;
            recoverySessionComplete = recoverySessionRemaining == 0;
            if (recoverySessionComplete) recoverySessionCompletedTicks = recoverySessionStartedTicks;
        }
        long started = Stopwatch.GetTimestamp();
        long deadline = started + Stopwatch.Frequency * 2 / 1000;
        int commits = 0, inspected = 0;
        // Visible rooms first; both scheduling and Unity texture uploads obey the same frame budget.
        for (int i = 0; i < priorityRooms.Count && commits < 2 && Stopwatch.GetTimestamp() < deadline; i++)
            if (cache.TryGetValue(priorityRooms[i], out CacheEntry priority) && RecoverRoom(priority, page.world)) commits++;
        while (roomOrder.Count > 0 && inspected++ < roomOrder.Count && commits < 2 && Stopwatch.GetTimestamp() < deadline)
        {
            if (sourceDimensionCursor >= roomOrder.Count) sourceDimensionCursor = 0;
            if (cache.TryGetValue(roomOrder[sourceDimensionCursor++], out CacheEntry entry) && RecoverRoom(entry, page.world)) commits++;
        }
        if (commits > 0 || sourceQueue.ActiveWorkers > 0)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            recoveryPerfTotalTicks += elapsed; recoveryPerfPeakTicks = Math.Max(recoveryPerfPeakTicks, elapsed);
            recoveryPerfSamples++; recoveryPerfCompletedRooms += commits;
        }
        recoverySessionRemaining = CountMissingMapTextures(page.map);
        if (!recoverySessionComplete && recoverySessionRemaining == 0)
        {
            recoverySessionComplete = true; recoverySessionCompletedTicks = Stopwatch.GetTimestamp();
            global::DryCycle.Plugin.Logger?.LogInfo("WorldMap thumbnail recovery complete: " + recoverySessionInitialMissing +
                " rooms, " + SourceRecoverySessionElapsedMilliseconds.ToString("F0") + " ms; main-thread avg " +
                SourceRecoveryAverageMilliseconds.ToString("F2") + " ms, peak " + SourceRecoveryPeakMilliseconds.ToString("F2") +
                " ms; decoded " + sourceQueue.Loads + ", warm hits " + sourceQueue.Hits + ".");
        }
    }

    private static bool RecoverRoom(CacheEntry entry, global::World world)
    {
        MapObject.RoomRepresentation rep = entry.RoomRep;
        if (rep?.room == null || rep.room.offScreenDen) return false;
        if ((rep.texture != null || rep.mapTex != null) && entry.OwnedSourceTexture == null) return false;
        if (!EnsureDecodedRoomSource(entry, world) || ReferenceEquals(entry.UploadedSource, entry.DecodedSource)) return false;
        WorldMapRoomSource data = entry.DecodedSource;
        Texture2D texture = new(data.Bake.Width, data.Bake.Height, TextureFormat.RGBA32, false)
        { name = "DryCycleMap_" + entry.RoomName, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
        try { texture.SetPixels(data.Pixels); texture.Apply(false, false); }
        catch { UnityEngine.Object.Destroy(texture); throw; }
        Texture2D previous = entry.OwnedSourceTexture;
        rep.texture = texture;
        entry.OwnedSourceTexture = texture;
        entry.UploadedSource = data;
        rep.waterLevel = data.WaterLevel;
        // Node indices come from the shared semantic compiler, used by Player Map and Cartography.
        foreach (var anchor in data.Bake.NodeAnchors)
            if (anchor.NodeIndex >= 0 && anchor.NodeIndex < rep.nodePositions.Length)
                rep.nodePositions[anchor.NodeIndex] = new Vector2(anchor.EntranceX - .5f, anchor.EntranceY - .5f);
        RefreshNodes(entry, rep, force: true);
        entry.BaseRasterRuns = data.Raster;
        entry.RasterWidth = data.Bake.Width; entry.RasterHeight = data.Bake.Height;
        entry.RasterInitialized = true;
        if (TryGetRasterSourceInfo(rep, out RasterSourceInfo raster))
        {
            entry.RasterSourceKey = raster.SourceKey;
            PersistentOnRasterRebuilt(entry, rep, raster);
        }
        entry.Revision++;
        Publish(entry, allowRasterReadback: false);
        if (previous != null) UnityEngine.Object.Destroy(previous);
        return true;
    }

    private static int CountMissingMapTextures(MapObject map)
    {
        int count = 0;
        if (map?.roomReps == null) return count;
        foreach (MapObject.RoomRepresentation rep in map.roomReps)
            if (rep?.room != null && !rep.room.offScreenDen && rep.texture == null && rep.mapTex == null) count++;
        return count;
    }

    private static void ClearRecoveryFailure(int roomIndex)
    {
        recoveryFailures.Remove(roomIndex);
        if (cache.TryGetValue(roomIndex, out CacheEntry entry))
        { entry.DecodedSource = null; entry.SourceFailed = false; entry.NextSourceAuditFrame = 0; }
    }

    private static void ReleaseSourceTextures()
    {
        sourceQueue.Reset();
        foreach (CacheEntry entry in cache.Values)
        {
            if (entry.OwnedSourceTexture == null) continue;
            if (entry.RoomRep?.texture == entry.OwnedSourceTexture) entry.RoomRep.texture = null;
            UnityEngine.Object.Destroy(entry.OwnedSourceTexture);
        }
        ResetSourceRecovery();
    }

    internal static void ResetSourceRecovery()
    {
        if (sourceRecoveryMapObject?.roomPrep == null) sourceRecoveryMapObject = null;
        recoveryFailures.Clear(); sourceDimensionCursor = 0; lastSourceRecoveryFrame = -1;
        recoveryPerfTotalTicks = recoveryPerfPeakTicks = recoverySessionStartedTicks = recoverySessionCompletedTicks = 0;
        recoveryPerfSamples = recoveryPerfCompletedRooms = recoverySessionInitialMissing = recoverySessionRemaining = 0;
        recoverySessionComplete = false;
    }
}
