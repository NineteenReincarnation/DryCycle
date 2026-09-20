using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// L1/L2 region cache for the retained GPU map.
///
/// The cache now participates through explicit WorldMapGpuCache lifecycle calls rather than
/// RuntimeDetouring DryCycle-owned methods. L1 keeps recently visited immutable cache snapshots in
/// memory; L2 preloads recent .dcwm files on a worker thread.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuPipeBatchPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRegionPreloadPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RegionPreload";
    public const string PluginName = "DryCycle DevTool GPU World Map Region Preload";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRegionPreload.Enable(Logger);
    private void OnDisable() => WorldMapGpuRegionPreload.Disable();
}

internal static class WorldMapGpuRegionPreload
{
    private const int MaxResidentRegions = 8;
    private const long MaxPreloadBytes = 256L * 1024L * 1024L;

    private sealed class ResidentEntry
    {
        internal string Region = string.Empty;
        internal object Snapshot;
        internal string Path = string.Empty;
        internal long Sequence;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ResidentEntry> resident =
        new(StringComparer.OrdinalIgnoreCase);

    private static ManualLogSource log;
    private static CancellationTokenSource cancellation;
    private static string cacheRoot = string.Empty;
    private static long sequence;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;

        log = logger;
        cacheRoot = Path.Combine(Application.persistentDataPath, "DryCycle", "WorldMapGpuCache");
        cancellation = new CancellationTokenSource();
        enabled = true;
        _ = Task.Run(() => PreloadRecentCaches(cancellation.Token), cancellation.Token);
        log?.LogInfo("GPU World Map region memory/preload cache enabled through direct cache lifecycle calls.");
    }

    internal static void Disable()
    {
        try { cancellation?.Cancel(); }
        catch { }

        cancellation?.Dispose();
        cancellation = null;

        lock (Sync) resident.Clear();
        cacheRoot = string.Empty;
        sequence = 0;
        enabled = false;
        log = null;
    }

    internal static void BeforeCacheUpdate(
        EditorSession session,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || session?.ToolMode != EditorToolMode.Map || snapshot?.Available != true)
            return;

        string target = NormalizeRegion(snapshot.RegionName ?? session.World?.name);
        WorldMapGpuCache.CaptureResidentSnapshot(out string activeRaw, out _);
        string active = NormalizeRegion(activeRaw);
        if (target.Length == 0 || string.Equals(active, target, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // Make the old region durable before retaining its immutable snapshot in memory.
            WorldMapGpuCache.FlushNow();
            StashCurrent();

            if (TryTakeResident(target, out ResidentEntry cached) && cached.Snapshot != null)
            {
                WorldMapGpuCache.RestoreResidentSnapshot(
                    target,
                    cached.Path,
                    cached.Snapshot);
            }
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map resident-cache restore skipped: " + error.Message);
        }
    }

    internal static void AfterCacheUpdate()
    {
        if (!enabled) return;
        StashCurrent();
    }

    internal static void OnCacheClearing(string region)
    {
        if (!enabled) return;

        string normalized = NormalizeRegion(region);
        if (normalized.Length == 0) return;
        lock (Sync) resident.Remove(normalized);
    }

    private static void StashCurrent()
    {
        object snapshot = WorldMapGpuCache.CaptureResidentSnapshot(
            out string regionRaw,
            out string path);
        string region = NormalizeRegion(regionRaw);
        if (region.Length == 0 || snapshot == null) return;

        lock (Sync)
        {
            resident[region] = new ResidentEntry
            {
                Region = region,
                Snapshot = snapshot,
                Path = path,
                Sequence = ++sequence
            };
            TrimResidentLocked(region);
        }
    }

    private static bool TryTakeResident(string region, out ResidentEntry entry)
    {
        lock (Sync)
        {
            if (!resident.TryGetValue(region, out entry)) return false;
            entry.Sequence = ++sequence;
            return true;
        }
    }

    private static void PreloadRecentCaches(CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot)) return;
            FileInfo[] files = new DirectoryInfo(cacheRoot).GetFiles("*.dcwm", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            long acceptedBytes = 0;
            int acceptedRegions = 0;
            for (int i = 0; i < files.Length && acceptedRegions < MaxResidentRegions; i++)
            {
                if (token.IsCancellationRequested) return;

                FileInfo file = files[i];
                if (file.Length <= 0 || acceptedBytes + file.Length > MaxPreloadBytes) continue;
                string region = NormalizeRegion(Path.GetFileNameWithoutExtension(file.Name));
                if (region.Length == 0) continue;

                lock (Sync)
                {
                    if (resident.ContainsKey(region)) continue;
                }

                object snapshot;
                try
                {
                    snapshot = WorldMapGpuCache.LoadResidentSnapshot(region, file.FullName);
                }
                catch
                {
                    continue;
                }
                if (snapshot == null) continue;

                lock (Sync)
                {
                    if (!resident.ContainsKey(region))
                    {
                        resident[region] = new ResidentEntry
                        {
                            Region = region,
                            Snapshot = snapshot,
                            Path = file.FullName,
                            Sequence = ++sequence
                        };
                        TrimResidentLocked(string.Empty);
                    }
                }

                acceptedBytes += file.Length;
                acceptedRegions++;
            }
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map background cache preload stopped: " + error.Message);
        }
    }

    private static void TrimResidentLocked(string protectedRegion)
    {
        while (resident.Count > MaxResidentRegions)
        {
            string oldestKey = null;
            long oldest = long.MaxValue;
            foreach (KeyValuePair<string, ResidentEntry> pair in resident)
            {
                if (string.Equals(pair.Key, protectedRegion, StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value.Sequence >= oldest) continue;
                oldest = pair.Value.Sequence;
                oldestKey = pair.Key;
            }

            if (oldestKey == null) break;
            resident.Remove(oldestKey);
        }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
