using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Loads world/TemperatureSets.txt and hot-reloads it while the game is running.
///
/// The loader owns only authored room-base heat. It keeps the last complete snapshot
/// while an editor is in the middle of saving and swaps dictionaries only after the
/// file metadata is stable across two polls.
/// </summary>
internal static class TemperatureSetsLoader
{
    private const string FileName = "TemperatureSets.txt";
    private const int PollIntervalTicks = 8;
    private const int MissingFileClearPolls = 5;
    private const int MaxAssemblyParentSearchDepth = 8;

    private static Dictionary<string, Dictionary<string, float>> _roomHeatByRegion =
        CreateRegionTable();

    private static bool _enabled;
    private static int _ticksUntilPoll;
    private static string _filePath = string.Empty;

    private static DateTime _loadedWriteUtc = DateTime.MinValue;
    private static long _loadedLength = -1L;

    private static bool _pendingSignature;
    private static DateTime _pendingWriteUtc;
    private static long _pendingLength;
    private static int _missingPolls;
    private static bool _missingLogged;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        _ticksUntilPoll = 0;
        _filePath = ResolveTemperatureSetsPath();
        ResetFileTracking();
        global::DryCycle.Plugin.Logger?.LogInfo(
            $"TemperatureSets: resolved data path '{_filePath}'.");
        LoadInitialSnapshot();
        On.RainWorldGame.Update += RainWorldGame_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        _roomHeatByRegion = CreateRegionTable();
        _filePath = string.Empty;
        ResetFileTracking();
    }

    internal static float GetRoomHeat(string regionName, string roomName)
    {
        if (string.IsNullOrWhiteSpace(regionName) || string.IsNullOrWhiteSpace(roomName))
        {
            return RoomHeatFactor.DefaultHeat;
        }

        if (_roomHeatByRegion.TryGetValue(regionName.Trim(), out Dictionary<string, float> rooms) &&
            rooms.TryGetValue(roomName.Trim(), out float heat))
        {
            return heat;
        }

        return RoomHeatFactor.DefaultHeat;
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame game)
    {
        orig(game);

        if (!_enabled)
        {
            return;
        }

        _ticksUntilPoll--;
        if (_ticksUntilPoll > 0)
        {
            return;
        }

        _ticksUntilPoll = PollIntervalTicks;
        PollForChanges();
    }

    private static void LoadInitialSnapshot()
    {
        if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
        {
            _roomHeatByRegion = CreateRegionTable();
            LogMissingFile();
            return;
        }

        FileInfo info = new(_filePath);
        if (!TryLoadSnapshot(
                _filePath,
                info.LastWriteTimeUtc,
                info.Length,
                out Dictionary<string, Dictionary<string, float>> snapshot,
                out int entryCount))
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: initial load failed; room heat defaults to 0. Path: {_filePath}");
            return;
        }

        ApplySnapshot(snapshot, info.LastWriteTimeUtc, info.Length, entryCount, hotReload: false);
    }

    private static void PollForChanges()
    {
        try
        {
            string resolvedPath = ResolveTemperatureSetsPath();
            if (!string.Equals(resolvedPath, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                _filePath = resolvedPath;
                ResetFileTracking();
                global::DryCycle.Plugin.Logger?.LogInfo(
                    $"TemperatureSets: data path changed to '{_filePath}'.");
                LoadInitialSnapshot();
                return;
            }

            if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
            {
                HandleMissingFile();
                return;
            }

            _missingPolls = 0;
            _missingLogged = false;

            FileInfo info = new(_filePath);
            DateTime writeUtc = info.LastWriteTimeUtc;
            long length = info.Length;

            if (writeUtc == _loadedWriteUtc && length == _loadedLength)
            {
                _pendingSignature = false;
                return;
            }

            // Wait for the same changed signature twice. This avoids swapping in a
            // half-written file when an editor saves in place instead of atomically.
            if (!_pendingSignature ||
                writeUtc != _pendingWriteUtc ||
                length != _pendingLength)
            {
                _pendingSignature = true;
                _pendingWriteUtc = writeUtc;
                _pendingLength = length;
                return;
            }

            if (!TryLoadSnapshot(
                    _filePath,
                    writeUtc,
                    length,
                    out Dictionary<string, Dictionary<string, float>> snapshot,
                    out int entryCount))
            {
                _pendingSignature = false;
                return;
            }

            ApplySnapshot(snapshot, writeUtc, length, entryCount, hotReload: true);
            _pendingSignature = false;
        }
        catch (Exception ex)
        {
            // Editing the file must never be able to break the running game. Keep the
            // last valid snapshot and retry on the next poll.
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: hot-reload check failed, keeping previous values. {ex.Message}");
        }
    }

    private static bool TryLoadSnapshot(
        string path,
        DateTime expectedWriteUtc,
        long expectedLength,
        out Dictionary<string, Dictionary<string, float>> snapshot,
        out int entryCount)
    {
        snapshot = CreateRegionTable();
        entryCount = 0;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: cannot read '{path}', keeping previous values. {ex.Message}");
            return false;
        }

        string currentRegion = null;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex]?.Trim();
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                LogParseWarning(lineIndex, "missing ':'", line);
                continue;
            }

            string left = line.Substring(0, colon).Trim();
            string right = line.Substring(colon + 1).Trim();

            if (left.Length == 0)
            {
                LogParseWarning(lineIndex, "empty name before ':'", line);
                continue;
            }

            // A line such as "B5:" starts/changes the current region section.
            if (right.Length == 0)
            {
                currentRegion = left;
                if (!snapshot.ContainsKey(currentRegion))
                {
                    snapshot[currentRegion] = CreateRoomTable();
                }
                continue;
            }

            if (string.IsNullOrEmpty(currentRegion))
            {
                LogParseWarning(lineIndex, "room value appears before a region header", line);
                continue;
            }

            if (!float.TryParse(
                    right,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsedHeat) ||
                float.IsNaN(parsedHeat) ||
                float.IsInfinity(parsedHeat))
            {
                LogParseWarning(lineIndex, "invalid numeric room heat", line);
                continue;
            }

            float heat = RoomHeatFactor.ClampHeat(parsedHeat);
            if (Math.Abs(heat - parsedHeat) > 0.0001f)
            {
                global::DryCycle.Plugin.Logger?.LogWarning(
                    $"TemperatureSets line {lineIndex + 1}: {parsedHeat.ToString(CultureInfo.InvariantCulture)} " +
                    $"is outside [-1, 1] and was clamped to {heat.ToString(CultureInfo.InvariantCulture)}.");
            }

            snapshot[currentRegion][left] = heat;
            entryCount++;
        }

        // Verify that the file did not change while it was being read and parsed.
        try
        {
            FileInfo afterRead = new(path);
            if (!afterRead.Exists ||
                afterRead.LastWriteTimeUtc != expectedWriteUtc ||
                afterRead.Length != expectedLength)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static void ApplySnapshot(
        Dictionary<string, Dictionary<string, float>> snapshot,
        DateTime writeUtc,
        long length,
        int entryCount,
        bool hotReload)
    {
        _roomHeatByRegion = snapshot;
        _loadedWriteUtc = writeUtc;
        _loadedLength = length;
        _missingPolls = 0;
        _missingLogged = false;

        string action = hotReload ? "hot-reloaded" : "loaded";
        global::DryCycle.Plugin.Logger?.LogInfo(
            $"TemperatureSets: {action} {entryCount} room heat value(s) from '{_filePath}'.");
    }

    private static void HandleMissingFile()
    {
        _missingPolls++;
        if (_missingPolls < MissingFileClearPolls)
        {
            return;
        }

        if (_loadedLength >= 0L || _roomHeatByRegion.Count > 0)
        {
            _roomHeatByRegion = CreateRegionTable();
            _loadedWriteUtc = DateTime.MinValue;
            _loadedLength = -1L;
            _pendingSignature = false;
        }

        LogMissingFile();
    }

    private static void LogMissingFile()
    {
        if (_missingLogged)
        {
            return;
        }

        _missingLogged = true;
        global::DryCycle.Plugin.Logger?.LogWarning(
            $"TemperatureSets: '{_filePath}' was not found. All room heat values default to 0.");
    }

    private static string ResolveTemperatureSetsPath()
    {
        // First bind the data file to the actual mod that physically contains this
        // DryCycle assembly. This is important during development: the plugin can be
        // hosted by a larger region mod (for example NR.B5 / Ancient Site) whose
        // modinfo id intentionally differs from DryCycle's BepInEx plugin GUID.
        string assemblyOwnedPath = ResolvePathFromContainingMod();
        if (!string.IsNullOrWhiteSpace(assemblyOwnedPath))
        {
            return assemblyOwnedPath;
        }

        // Legacy/standalone fallback: if DryCycle is packaged as its own active mod,
        // continue supporting the original id-based lookup.
        if (ModManager.ActiveMods != null)
        {
            for (int i = ModManager.ActiveMods.Count - 1; i >= 0; i--)
            {
                ModManager.Mod mod = ModManager.ActiveMods[i];
                if (mod == null ||
                    !string.Equals(mod.id, global::DryCycle.Plugin.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string rootPath = Path.Combine(mod.path, "world", FileName);
                if (File.Exists(rootPath))
                {
                    return rootPath;
                }

                if (mod.hasTargetedVersionFolder)
                {
                    string targetedPath = Path.Combine(mod.TargetedPath, "world", FileName);
                    if (File.Exists(targetedPath))
                    {
                        return targetedPath;
                    }
                }

                if (mod.hasNewestFolder)
                {
                    string newestPath = Path.Combine(mod.NewestPath, "world", FileName);
                    if (File.Exists(newestPath))
                    {
                        return newestPath;
                    }
                }

                return rootPath;
            }
        }

        return AssetManager.ResolveFilePath("world/" + FileName);
    }

    private static string ResolvePathFromContainingMod()
    {
        try
        {
            string assemblyPath = typeof(global::DryCycle.Plugin).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return null;
            }

            string assemblyDirectoryPath = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (string.IsNullOrWhiteSpace(assemblyDirectoryPath))
            {
                return null;
            }

            DirectoryInfo directory = new(assemblyDirectoryPath);
            for (int depth = 0;
                 directory != null && depth < MaxAssemblyParentSearchDepth;
                 depth++, directory = directory.Parent)
            {
                // modinfo.json marks the owning Rain World mod root. Starting from
                // e.g. Ancient Site/newest/plugins/DryCycle.dll, this walks through
                // plugins -> newest -> Ancient Site and stops there.
                string modInfoPath = Path.Combine(directory.FullName, "modinfo.json");
                if (!File.Exists(modInfoPath))
                {
                    continue;
                }

                // Return the intended root path even if TemperatureSets.txt has not
                // been created yet. The hot-reload poller will notice it later.
                return Path.Combine(directory.FullName, "world", FileName);
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: failed to resolve owning mod from plugin path. {ex.Message}");
        }

        return null;
    }

    private static void ResetFileTracking()
    {
        _loadedWriteUtc = DateTime.MinValue;
        _loadedLength = -1L;
        _pendingSignature = false;
        _pendingWriteUtc = DateTime.MinValue;
        _pendingLength = -1L;
        _missingPolls = 0;
        _missingLogged = false;
    }

    private static Dictionary<string, Dictionary<string, float>> CreateRegionTable()
    {
        return new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, float> CreateRoomTable()
    {
        return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    }

    private static void LogParseWarning(int zeroBasedLine, string reason, string line)
    {
        global::DryCycle.Plugin.Logger?.LogWarning(
            $"TemperatureSets line {zeroBasedLine + 1}: {reason}. Ignored: '{line}'");
    }
}
