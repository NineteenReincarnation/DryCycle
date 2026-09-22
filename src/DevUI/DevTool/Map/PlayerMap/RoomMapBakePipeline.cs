using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Direct static-room loader used by Player Map baking. It deliberately does not construct Room,
/// RoomPreparer, ShortcutMapper, MiniMap, or call any of their Update methods.
/// </summary>
internal static class RoomMapSourceLoader
{
    internal static bool TryLoad(string roomName, out RoomMapSource source, out string sourcePath, out string error)
    {
        source = null;
        sourcePath = string.Empty;
        error = null;
        try
        {
            sourcePath = WorldLoader.FindRoomFile(roomName, includeRootDirectory: false, ".txt", showWarning: false);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "Room source file was not found.";
                return false;
            }

            string[] lines = File.ReadAllLines(sourcePath);
            if (lines == null || lines.Length < 12)
            {
                error = "Room source has fewer than 12 lines.";
                return false;
            }

            // VersionFix is a pure text-shape compatibility helper. It does not instantiate or
            // advance Rain World's room runtime state.
            RoomPreprocessor.VersionFix(ref lines);
            source = RoomMapTextDecoder.Parse(lines);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            source = null;
            return false;
        }
    }

}

internal sealed class RoomMapBakeCacheEntry
{
    internal int RoomIndex;
    internal string RoomName = string.Empty;
    internal RoomMapBakeStatus Status;
    internal RoomMapBake Bake;
    internal string Error = string.Empty;
    internal bool Queued;
    internal string LastSourcePath = string.Empty;
    internal long LastSourceLength;
    internal DateTime LastSourceWriteTimeUtc;
    internal int NextSourceAuditFrame;
}

internal static class RoomMapBakeCache
{
    private static readonly Dictionary<int, RoomMapBakeCacheEntry> Entries = new();
    private static readonly Queue<int> Pending = new();
    private static int revision = 1;

    internal static int Revision => revision;

    internal static void Request(int roomIndex, string roomName)
    {
        if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry))
        {
            entry = new RoomMapBakeCacheEntry
            {
                RoomIndex = roomIndex,
                RoomName = roomName ?? string.Empty,
                Status = RoomMapBakeStatus.Pending,
                Queued = true
            };
            Entries.Add(roomIndex, entry);
            Pending.Enqueue(roomIndex);
            revision++;
            return;
        }

        entry.RoomName = roomName ?? entry.RoomName;
        int frame = Time.frameCount;
        if (entry.Status == RoomMapBakeStatus.Ready && frame >= entry.NextSourceAuditFrame)
        {
            entry.NextSourceAuditFrame = frame + 120 + Math.Abs(roomIndex % 31);
            if (SourceChanged(entry))
            {
                entry.Status = RoomMapBakeStatus.Pending;
                entry.Queued = true;
                Pending.Enqueue(roomIndex);
                revision++;
            }
        }
        else if ((entry.Status == RoomMapBakeStatus.Missing || entry.Status == RoomMapBakeStatus.Failed) &&
                 !entry.Queued && frame >= entry.NextSourceAuditFrame)
        {
            entry.NextSourceAuditFrame = frame + 120 + Math.Abs(roomIndex % 31);
            string path = WorldLoader.FindRoomFile(entry.RoomName, false, ".txt", false);
            if (!string.Equals(path ?? string.Empty, entry.LastSourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                SourceInfoChanged(path, entry.LastSourceLength, entry.LastSourceWriteTimeUtc))
            {
                entry.Status = RoomMapBakeStatus.Pending;
                entry.Queued = true;
                Pending.Enqueue(roomIndex);
                revision++;
            }
        }
    }

    internal static void ProcessPending(int budget)
    {
        budget = Math.Max(1, budget);
        while (budget-- > 0 && Pending.Count > 0)
        {
            int roomIndex = Pending.Dequeue();
            if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry) || !entry.Queued) continue;
            entry.Queued = false;
            Build(entry);
        }
    }

    internal static RoomMapBakeSnapshot GetSnapshot(int roomIndex)
    {
        if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry))
            return new RoomMapBakeSnapshot { Status = RoomMapBakeStatus.Missing };
        RoomMapBake bake = entry.Bake;
        RoomMapBakeSnapshot source = new()
        {
            Status = entry.Status,
            Width = bake?.Width ?? 0,
            Height = bake?.Height ?? 0,
            Error = entry.Error ?? string.Empty,
            Runs = bake?.Runs ?? Array.Empty<RoomMapPreviewRun>(),
            NodeAnchors = bake?.NodeAnchors ?? Array.Empty<RoomMapNodeAnchorSnapshot>()
        };
        return PlayerMapTerrainBakeBridge.ProjectSnapshot(roomIndex, source, bake);
    }

    internal static bool TryGetReady(int roomIndex, out RoomMapBake bake)
    {
        bake = null;
        if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry) ||
            entry.Status != RoomMapBakeStatus.Ready ||
            entry.Bake == null)
            return false;

        return PlayerMapTerrainBakeBridge.TryEnhanceReady(roomIndex, entry.Bake, out bake);
    }

    internal static void Clear()
    {
        Entries.Clear();
        Pending.Clear();
        revision++;
    }

    private static void Build(RoomMapBakeCacheEntry entry)
    {
        if (!RoomMapSourceLoader.TryLoad(entry.RoomName, out RoomMapSource source, out string path, out string error))
        {
            entry.Bake = null;
            entry.Status = string.IsNullOrWhiteSpace(path) ? RoomMapBakeStatus.Missing : RoomMapBakeStatus.Failed;
            entry.Error = error ?? "Unknown room source error.";
            UpdateSourceEvidence(entry, path);
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
            revision++;
            return;
        }

        try
        {
            entry.Bake = RoomMapSemanticCompiler.Compile(entry.RoomIndex, entry.RoomName, source, path);
            entry.Status = RoomMapBakeStatus.Ready;
            entry.Error = string.Empty;
            entry.LastSourcePath = path;
            entry.LastSourceLength = entry.Bake.SourceLength;
            entry.LastSourceWriteTimeUtc = entry.Bake.SourceWriteTimeUtc;
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
        }
        catch (Exception exception)
        {
            entry.Bake = null;
            entry.Status = RoomMapBakeStatus.Failed;
            entry.Error = exception.Message;
            UpdateSourceEvidence(entry, path);
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
        }
        revision++;
    }

    private static bool SourceChanged(RoomMapBakeCacheEntry entry)
    {
        string path = WorldLoader.FindRoomFile(entry.RoomName, false, ".txt", false);
        if (!string.Equals(path ?? string.Empty, entry.LastSourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return true;
        return SourceInfoChanged(path, entry.LastSourceLength, entry.LastSourceWriteTimeUtc);
    }

    private static bool SourceInfoChanged(string path, long length, DateTime writeTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return length != 0L || writeTimeUtc != DateTime.MinValue;
        FileInfo info = new(path);
        return info.Length != length || info.LastWriteTimeUtc != writeTimeUtc;
    }

    private static void UpdateSourceEvidence(RoomMapBakeCacheEntry entry, string path)
    {
        entry.LastSourcePath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            entry.LastSourceLength = 0L;
            entry.LastSourceWriteTimeUtc = DateTime.MinValue;
            return;
        }
        FileInfo info = new(path);
        entry.LastSourceLength = info.Length;
        entry.LastSourceWriteTimeUtc = info.LastWriteTimeUtc;
    }
}
