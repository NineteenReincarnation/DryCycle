using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Loads world/TemperatureSets.txt and hot-reloads it while the game is running.
///
/// Standard room formats:
///   ROOM : RoomHeat
///   ROOM : RoomHeat : SunlightIntensity : RoomShade
///   ROOM : RoomHeat : SunlightIntensity : RoomShade : Humidity
///
/// RoomHeat and Humidity are clamped to [-1,1]. SunlightIntensity and RoomShade
/// are clamped to [0,1]. Omitted values keep their neutral defaults. The former
/// pipe-separated extended form remains readable for old local test files.
/// </summary>
internal static class TemperatureSetsLoader
{
    private const string FileName = "TemperatureSets.txt";
    private const int PollIntervalTicks = 8;
    private const int MissingFileClearPolls = 5;
    private const int MaxAssemblyParentSearchDepth = 8;

    private static Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> _profilesByRegion =
        CreateRegionTable();

    private static bool _enabled;
    private static int _ticksUntilPoll;
    private static string _filePath = string.Empty;
    private static DateTime _loadedWriteUtc = DateTime.MinValue;
    private static long _loadedLength = -1L;
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
        LoadSnapshot(hotReload: false);
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
        _profilesByRegion = CreateRegionTable();
        _filePath = string.Empty;
        ResetFileTracking();
    }

    internal static float GetRoomHeat(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out RoomEnvironmentProfile profile)
            ? profile.RoomHeat
            : RoomHeatFactor.DefaultHeat;
    }

    internal static float GetSunlightIntensity(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out RoomEnvironmentProfile profile)
            ? profile.SunlightIntensity
            : RoomEnvironmentProfile.DefaultSunlightIntensity;
    }

    internal static float GetRoomShade(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out RoomEnvironmentProfile profile)
            ? profile.RoomShade
            : RoomEnvironmentProfile.DefaultRoomShade;
    }

    internal static float GetHumidity(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out RoomEnvironmentProfile profile)
            ? profile.Humidity
            : RoomEnvironmentProfile.DefaultHumidity;
    }

    private static bool TryGetProfile(
        string regionName,
        string roomName,
        out RoomEnvironmentProfile profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(regionName) || string.IsNullOrWhiteSpace(roomName))
        {
            return false;
        }

        return _profilesByRegion.TryGetValue(
                   regionName.Trim(),
                   out Dictionary<string, RoomEnvironmentProfile> rooms) &&
               rooms.TryGetValue(roomName.Trim(), out profile);
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

    private static void PollForChanges()
    {
        try
        {
            string resolved = ResolveTemperatureSetsPath();
            if (!string.Equals(resolved, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                _filePath = resolved;
                ResetFileTracking();
                global::DryCycle.Plugin.Logger?.LogInfo(
                    $"TemperatureSets: data path changed to '{_filePath}'.");
                LoadSnapshot(hotReload: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
            {
                _missingPolls++;
                if (_missingPolls >= MissingFileClearPolls)
                {
                    _profilesByRegion = CreateRegionTable();
                    _loadedWriteUtc = DateTime.MinValue;
                    _loadedLength = -1L;
                    LogMissingFile();
                }
                return;
            }

            _missingPolls = 0;
            _missingLogged = false;

            FileInfo info = new(_filePath);
            if (info.LastWriteTimeUtc == _loadedWriteUtc && info.Length == _loadedLength)
            {
                return;
            }

            LoadSnapshot(hotReload: true);
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: hot-reload check failed, keeping previous values. {ex.Message}");
        }
    }

    private static void LoadSnapshot(bool hotReload)
    {
        if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
        {
            _profilesByRegion = CreateRegionTable();
            LogMissingFile();
            return;
        }

        FileInfo before = new(_filePath);
        DateTime expectedWrite = before.LastWriteTimeUtc;
        long expectedLength = before.Length;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_filePath);
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"TemperatureSets: cannot read '{_filePath}', keeping previous values. {ex.Message}");
            return;
        }

        Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> snapshot =
            CreateRegionTable();
        string currentRegion = null;
        int entryCount = 0;

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

            if (!TryParseRoomProfile(right, lineIndex, line, out RoomEnvironmentProfile profile))
            {
                continue;
            }

            snapshot[currentRegion][left] = profile;
            entryCount++;
        }

        try
        {
            FileInfo after = new(_filePath);
            if (!after.Exists ||
                after.LastWriteTimeUtc != expectedWrite ||
                after.Length != expectedLength)
            {
                // File changed while being read. Leave the previous snapshot in use;
                // the next poll will retry the new stable version.
                return;
            }
        }
        catch
        {
            return;
        }

        _profilesByRegion = snapshot;
        _loadedWriteUtc = expectedWrite;
        _loadedLength = expectedLength;
        _missingPolls = 0;
        _missingLogged = false;

        string action = hotReload ? "hot-reloaded" : "loaded";
        global::DryCycle.Plugin.Logger?.LogInfo(
            $"TemperatureSets: {action} {entryCount} room environment profile(s) from '{_filePath}'.");
    }

    private static bool TryParseRoomProfile(
        string value,
        int lineIndex,
        string fullLine,
        out RoomEnvironmentProfile profile)
    {
        profile = null;

        string[] fields = value.IndexOf(':') >= 0
            ? value.Split(':')
            : (value.IndexOf('|') >= 0 ? value.Split('|') : new[] { value });

        if (fields.Length > 4)
        {
            LogParseWarning(
                lineIndex,
                "too many room fields; expected RoomHeat : SunlightIntensity : RoomShade : Humidity",
                fullLine);
            return false;
        }

        if (!TryParseFinite(fields[0], out float roomHeat))
        {
            LogParseWarning(lineIndex, "invalid RoomHeat", fullLine);
            return false;
        }

        float sunlight = RoomEnvironmentProfile.DefaultSunlightIntensity;
        float roomShade = RoomEnvironmentProfile.DefaultRoomShade;
        float humidity = RoomEnvironmentProfile.DefaultHumidity;

        if (fields.Length >= 2 &&
            !string.IsNullOrWhiteSpace(fields[1]) &&
            !TryParseFinite(fields[1], out sunlight))
        {
            LogParseWarning(lineIndex, "invalid SunlightIntensity", fullLine);
            return false;
        }

        if (fields.Length >= 3 &&
            !string.IsNullOrWhiteSpace(fields[2]) &&
            !TryParseFinite(fields[2], out roomShade))
        {
            LogParseWarning(lineIndex, "invalid RoomShade", fullLine);
            return false;
        }

        if (fields.Length >= 4 &&
            !string.IsNullOrWhiteSpace(fields[3]) &&
            !TryParseFinite(fields[3], out humidity))
        {
            LogParseWarning(lineIndex, "invalid Humidity", fullLine);
            return false;
        }

        float clampedHeat = RoomHeatFactor.ClampHeat(roomHeat);
        float clampedSunlight = RoomEnvironmentProfile.ClampUnit(sunlight);
        float clampedRoomShade = RoomEnvironmentProfile.ClampUnit(roomShade);
        float clampedHumidity = RoomEnvironmentProfile.ClampSigned(humidity);

        LogClampIfNeeded(lineIndex, "RoomHeat", roomHeat, clampedHeat, "[-1, 1]");
        LogClampIfNeeded(lineIndex, "SunlightIntensity", sunlight, clampedSunlight, "[0, 1]");
        LogClampIfNeeded(lineIndex, "RoomShade", roomShade, clampedRoomShade, "[0, 1]");
        LogClampIfNeeded(lineIndex, "Humidity", humidity, clampedHumidity, "[-1, 1]");

        profile = new RoomEnvironmentProfile(
            clampedHeat,
            clampedSunlight,
            clampedRoomShade,
            clampedHumidity);
        return true;
    }

    private static bool TryParseFinite(string value, out float parsed)
    {
        return float.TryParse(
                   value?.Trim(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out parsed) &&
               !float.IsNaN(parsed) &&
               !float.IsInfinity(parsed);
    }

    private static void LogClampIfNeeded(
        int zeroBasedLine,
        string field,
        float original,
        float clamped,
        string range)
    {
        if (Math.Abs(original - clamped) <= 0.0001f)
        {
            return;
        }

        global::DryCycle.Plugin.Logger?.LogWarning(
            $"TemperatureSets line {zeroBasedLine + 1}: {field} " +
            $"{original.ToString(CultureInfo.InvariantCulture)} is outside {range} and was clamped to " +
            $"{clamped.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static string ResolveTemperatureSetsPath()
    {
        string assemblyOwnedPath = ResolvePathFromContainingMod();
        if (!string.IsNullOrWhiteSpace(assemblyOwnedPath))
        {
            return assemblyOwnedPath;
        }

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
                string modInfoPath = Path.Combine(directory.FullName, "modinfo.json");
                if (!File.Exists(modInfoPath))
                {
                    continue;
                }

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
        _missingPolls = 0;
        _missingLogged = false;
    }

    private static Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> CreateRegionTable()
    {
        return new Dictionary<string, Dictionary<string, RoomEnvironmentProfile>>(
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, RoomEnvironmentProfile> CreateRoomTable()
    {
        return new Dictionary<string, RoomEnvironmentProfile>(StringComparer.OrdinalIgnoreCase);
    }

    private static void LogMissingFile()
    {
        if (_missingLogged)
        {
            return;
        }

        _missingLogged = true;
        global::DryCycle.Plugin.Logger?.LogWarning(
            $"TemperatureSets: '{_filePath}' was not found. Room environment values use neutral defaults.");
    }

    private static void LogParseWarning(int zeroBasedLine, string reason, string line)
    {
        global::DryCycle.Plugin.Logger?.LogWarning(
            $"TemperatureSets line {zeroBasedLine + 1}: {reason}. Ignored: '{line}'");
    }
}
