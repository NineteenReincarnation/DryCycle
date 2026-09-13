using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
/// L1 keeps recently visited immutable WorldMapGpuCache snapshots in memory, so returning to a
/// region never deserializes its .dcwm again. L2 preloads recent .dcwm files on a worker thread.
/// WorldMapGpuCache.Load is intentionally safe for this use: it only performs BinaryReader work and
/// constructs managed snapshot objects; Unity textures and renderer objects remain on the main
/// thread. Source-file validation still runs progressively after activation, so preloading never
/// turns cache freshness checks off.
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

    private delegate void OrigCacheUpdate(EditorSession session, EditorMapPresentationSnapshot snapshot);
    private delegate void HookCacheUpdate(
        OrigCacheUpdate orig,
        EditorSession session,
        EditorMapPresentationSnapshot snapshot);

    private delegate void OrigClearDiskCache(string region);
    private delegate void HookClearDiskCache(OrigClearDiskCache orig, string region);

    private sealed class ResidentEntry
    {
        internal string Region = string.Empty;
        internal object Snapshot;
        internal string Path = string.Empty;
        internal long Sequence;
    }

    private static readonly HookCacheUpdate CacheUpdateHookDelegate = CacheUpdateHook;
    private static readonly HookClearDiskCache ClearDiskHookDelegate = ClearDiskCacheHook;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ResidentEntry> resident =
        new(StringComparer.OrdinalIgnoreCase);

    private static ManualLogSource log;
    private static IDisposable updateHook;
    private static IDisposable clearHook;
    private static MethodInfo loadMethod;
    private static FieldInfo currentField;
    private static FieldInfo activeRegionField;
    private static FieldInfo activePathField;
    private static FieldInfo validatedRoomsField;
    private static FieldInfo liveSignaturesField;
    private static FieldInfo validationCursorField;
    private static FieldInfo captureCursorField;
    private static FieldInfo dirtyFrameField;
    private static FieldInfo generationField;
    private static FieldInfo dirtyField;
    private static FieldInfo lastErrorField;
    private static FieldInfo cacheHitsField;
    private static FieldInfo cacheMissesField;

    private static CancellationTokenSource cancellation;
    private static string cacheRoot = string.Empty;
    private static long sequence;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type cacheType = typeof(WorldMapGpuCache);
            MethodInfo update = cacheType.GetMethod(
                "Update", flags, null,
                new[] { typeof(EditorSession), typeof(EditorMapPresentationSnapshot) }, null);
            MethodInfo clear = cacheType.GetMethod(
                "ClearDiskCache", flags, null, new[] { typeof(string) }, null);
            loadMethod = cacheType.GetMethod(
                "Load", flags, null, new[] { typeof(string), typeof(string) }, null);

            currentField = cacheType.GetField("current", flags);
            activeRegionField = cacheType.GetField("activeRegion", flags);
            activePathField = cacheType.GetField("activePath", flags);
            validatedRoomsField = cacheType.GetField("validatedRooms", flags);
            liveSignaturesField = cacheType.GetField("liveSignatures", flags);
            validationCursorField = cacheType.GetField("validationCursor", flags);
            captureCursorField = cacheType.GetField("captureCursor", flags);
            dirtyFrameField = cacheType.GetField("dirtyFrame", flags);
            generationField = cacheType.GetField("generation", flags);
            dirtyField = cacheType.GetField("dirty", flags);
            lastErrorField = cacheType.GetField("lastError", flags);
            cacheHitsField = cacheType.GetField("cacheHits", flags);
            cacheMissesField = cacheType.GetField("cacheMisses", flags);

            if (update == null || clear == null || loadMethod == null || currentField == null ||
                activeRegionField == null || activePathField == null || validatedRoomsField == null ||
                liveSignaturesField == null || validationCursorField == null || captureCursorField == null ||
                dirtyFrameField == null || generationField == null || dirtyField == null ||
                lastErrorField == null || cacheHitsField == null || cacheMissesField == null)
                throw new MissingMemberException("World Map cache preload targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            updateHook = constructor.Invoke(new object[] { update, CacheUpdateHookDelegate }) as IDisposable;
            clearHook = constructor.Invoke(new object[] { clear, ClearDiskHookDelegate }) as IDisposable;

            cacheRoot = Path.Combine(Application.persistentDataPath, "DryCycle", "WorldMapGpuCache");
            cancellation = new CancellationTokenSource();
            enabled = true;
            _ = Task.Run(() => PreloadRecentCaches(cancellation.Token), cancellation.Token);
            log?.LogInfo("GPU World Map region memory/preload cache enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map region preload could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        try { cancellation?.Cancel(); }
        catch { }
        cancellation?.Dispose();
        cancellation = null;
        DisposeHook(ref clearHook);
        DisposeHook(ref updateHook);
        lock (Sync) resident.Clear();
        loadMethod = null;
        currentField = null;
        activeRegionField = null;
        activePathField = null;
        validatedRoomsField = null;
        liveSignaturesField = null;
        validationCursorField = null;
        captureCursorField = null;
        dirtyFrameField = null;
        generationField = null;
        dirtyField = null;
        lastErrorField = null;
        cacheHitsField = null;
        cacheMissesField = null;
        cacheRoot = string.Empty;
        sequence = 0;
        enabled = false;
        log = null;
    }

    private static void CacheUpdateHook(
        OrigCacheUpdate orig,
        EditorSession session,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || session?.ToolMode != EditorToolMode.Map || snapshot?.Available != true)
        {
            orig(session, snapshot);
            return;
        }

        string target = NormalizeRegion(snapshot.RegionName ?? session.World?.name);
        string active = ReadString(activeRegionField);
        if (target.Length > 0 && !string.Equals(active, target, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Make the old region durable before its immutable snapshot is retained in memory.
                WorldMapGpuCache.FlushNow();
                StashCurrent(active);
                if (TryTakeResident(target, out ResidentEntry cached) && cached.Snapshot != null)
                    Restore(target, cached);
            }
            catch (Exception error)
            {
                log?.LogDebug("World Map resident-cache restore skipped: " + error.Message);
            }
        }

        orig(session, snapshot);
        StashCurrent(NormalizeRegion(ReadString(activeRegionField)));
    }

    private static void ClearDiskCacheHook(OrigClearDiskCache orig, string region)
    {
        string normalized = NormalizeRegion(region);
        lock (Sync)
        {
            if (normalized.Length > 0) resident.Remove(normalized);
        }
        orig(region);
    }

    private static void StashCurrent(string active)
    {
        if (active.Length == 0 || currentField == null) return;
        object snapshot = currentField.GetValue(null);
        if (snapshot == null) return;
        string path = ReadString(activePathField);
        lock (Sync)
        {
            resident[active] = new ResidentEntry
            {
                Region = active,
                Snapshot = snapshot,
                Path = path,
                Sequence = ++sequence
            };
            TrimResidentLocked(active);
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

    private static void Restore(string region, ResidentEntry entry)
    {
        currentField.SetValue(null, entry.Snapshot);
        activeRegionField.SetValue(null, region);
        activePathField.SetValue(null,
            string.IsNullOrWhiteSpace(entry.Path) ? CachePath(region) : entry.Path);

        if (validatedRoomsField.GetValue(null) is HashSet<int> validated) validated.Clear();
        if (liveSignaturesField.GetValue(null) is Dictionary<int, ulong> signatures) signatures.Clear();
        validationCursorField.SetValue(null, 0);
        captureCursorField.SetValue(null, 0);
        dirtyField.SetValue(null, false);
        dirtyFrameField.SetValue(null, -1);
        lastErrorField.SetValue(null, string.Empty);
        cacheHitsField.SetValue(null, 0);
        cacheMissesField.SetValue(null, 0);

        int generation = generationField.GetValue(null) is int value ? value : 0;
        generationField.SetValue(null, unchecked(generation + 1));
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
                    snapshot = loadMethod?.Invoke(null, new object[] { region, file.FullName });
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
            log?.LogDebug("World Map background cache preload stopped: " + Unwrap(error).Message);
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

    private static string CachePath(string region) =>
        Path.Combine(cacheRoot, (region.Length == 0 ? "UNKNOWN" : region) + ".dcwm");

    private static string ReadString(FieldInfo field)
    {
        try { return field?.GetValue(null) as string ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
