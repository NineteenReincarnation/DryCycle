using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Owns the editable room environment profiles stored in world/TemperatureSets.json.
/// Runtime queries and the MapPage authoring panel share this in-memory snapshot.
/// </summary>
internal static class TemperatureSetsLoader
{
    internal const string FileName = "TemperatureSets.json";

    private const int CurrentVersion = 1;
    private const int PollIntervalTicks = 120;
    private const int MaxAssemblyParentSearchDepth = 8;

    private static Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> _profilesByRegion =
        CreateRegionTable();

    private static bool _enabled;
    private static int _ticksUntilPoll;
    private static DateTime _loadedWriteUtc = DateTime.MinValue;
    private static long _loadedLength = -1L;
    private static readonly List<string> _warnings = new();

    internal static string LoadedPath { get; private set; } = string.Empty;
    internal static bool Dirty { get; private set; }
    internal static string LoadError { get; private set; }
    internal static IReadOnlyList<string> Warnings => _warnings;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        _ticksUntilPoll = 0;
        Reload();
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
        LoadedPath = string.Empty;
        Dirty = false;
        LoadError = null;
        _warnings.Clear();
        ResetFileTracking();
    }

    internal static void Reload()
    {
        if (Dirty)
        {
            return;
        }

        LoadedPath = ResolveTemperatureSetsPath(forSave: true) ?? string.Empty;
        LoadError = null;
        _warnings.Clear();
        ResetFileTracking();

        if (LoadedPath.Length == 0 || !File.Exists(LoadedPath))
        {
            _profilesByRegion = CreateRegionTable();
            Dirty = false;
            global::DryCycle.Plugin.Logger?.LogInfo(
                $"TemperatureSets: '{LoadedPath}' does not exist yet; neutral room defaults are active.");
            return;
        }

        if (TryLoadFile(LoadedPath, out Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> parsed, out string error))
        {
            _profilesByRegion = parsed;
            Dirty = false;
            RememberFileState(LoadedPath);
            global::DryCycle.Plugin.Logger?.LogInfo(
                $"TemperatureSets: loaded {CountProfiles(parsed)} room environment profile(s) from '{LoadedPath}'.");
            return;
        }

        string backupPath = LoadedPath + ".bak";
        string backupError = null;
        if (File.Exists(backupPath) &&
            TryLoadFile(backupPath, out parsed, out backupError))
        {
            _profilesByRegion = parsed;
            Dirty = true;
            LoadError = $"Primary {FileName} is invalid ({error}); recovered the editable snapshot from {Path.GetFileName(backupPath)}.";
            RememberFileState(LoadedPath);
            global::DryCycle.Plugin.Logger?.LogWarning("TemperatureSets: " + LoadError);
            return;
        }

        _profilesByRegion = CreateRegionTable();
        Dirty = false;
        LoadError = File.Exists(backupPath)
            ? $"{FileName} and its backup are invalid. Primary: {error}; backup: {backupError}"
            : $"{FileName} is invalid and has no backup: {error}";
        RememberFileState(LoadedPath);
        global::DryCycle.Plugin.Logger?.LogError("TemperatureSets: " + LoadError);
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

    internal static bool HasProfile(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out _);
    }

    internal static RoomEnvironmentProfile GetProfileOrDefault(string regionName, string roomName)
    {
        return TryGetProfile(regionName, roomName, out RoomEnvironmentProfile profile)
            ? CloneProfile(profile)
            : new RoomEnvironmentProfile();
    }

    internal static bool SetProfile(
        string regionName,
        string roomName,
        RoomEnvironmentProfile profile)
    {
        string regionKey = NormalizeRegion(regionName);
        string roomKey = NormalizeRoom(roomName);
        if (regionKey.Length == 0 || roomKey.Length == 0 || profile == null)
        {
            return false;
        }

        if (!_profilesByRegion.TryGetValue(regionKey, out Dictionary<string, RoomEnvironmentProfile> rooms))
        {
            rooms = CreateRoomTable();
            _profilesByRegion[regionKey] = rooms;
        }

        RoomEnvironmentProfile next = CloneProfile(profile);
        if (rooms.TryGetValue(roomKey, out RoomEnvironmentProfile current) && ProfilesEqual(current, next))
        {
            return false;
        }

        rooms[roomKey] = next;
        Dirty = true;
        return true;
    }

    internal static bool RemoveProfile(string regionName, string roomName)
    {
        string regionKey = NormalizeRegion(regionName);
        string roomKey = NormalizeRoom(roomName);
        if (!_profilesByRegion.TryGetValue(regionKey, out Dictionary<string, RoomEnvironmentProfile> rooms) ||
            !rooms.Remove(roomKey))
        {
            return false;
        }

        if (rooms.Count == 0)
        {
            _profilesByRegion.Remove(regionKey);
        }
        Dirty = true;
        return true;
    }

    internal static bool Save()
    {
        string path = LoadedPath.Length > 0
            ? LoadedPath
            : ResolveTemperatureSetsPath(forSave: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            global::DryCycle.Plugin.Logger?.LogError(
                $"TemperatureSets: could not resolve a writable world/{FileName} path.");
            return false;
        }

        string tempPath = path + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(tempPath, Json.Serialize(BuildJsonRoot()));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.Delete(path);
            }
            File.Move(tempPath, path);

            LoadedPath = path;
            Dirty = false;
            LoadError = null;
            _warnings.Clear();
            RememberFileState(path);
            global::DryCycle.Plugin.Logger?.LogInfo("TemperatureSets saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogError("TemperatureSets save failed: " + ex);
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    private static bool TryGetProfile(
        string regionName,
        string roomName,
        out RoomEnvironmentProfile profile)
    {
        profile = null;
        string regionKey = NormalizeRegion(regionName);
        string roomKey = NormalizeRoom(roomName);
        return regionKey.Length > 0 &&
               roomKey.Length > 0 &&
               _profilesByRegion.TryGetValue(regionKey, out Dictionary<string, RoomEnvironmentProfile> rooms) &&
               rooms.TryGetValue(roomKey, out profile);
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame game)
    {
        orig(game);

        if (!_enabled || Dirty)
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
            string resolved = ResolveTemperatureSetsPath(forSave: true) ?? string.Empty;
            if (!string.Equals(resolved, LoadedPath, StringComparison.OrdinalIgnoreCase))
            {
                LoadedPath = resolved;
                Reload();
                return;
            }

            if (LoadedPath.Length == 0 || !File.Exists(LoadedPath))
            {
                if (_loadedLength >= 0L)
                {
                    Reload();
                }
                return;
            }

            FileInfo info = new(LoadedPath);
            if (info.LastWriteTimeUtc != _loadedWriteUtc || info.Length != _loadedLength)
            {
                Reload();
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "TemperatureSets hot-reload check failed: " + ex.Message);
        }
    }

    private static bool TryLoadFile(
        string path,
        out Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> profiles,
        out string error)
    {
        profiles = CreateRegionTable();
        error = null;
        try
        {
            object parsed = Json.Deserialize(File.ReadAllText(path));
            if (parsed is not Dictionary<string, object> root)
            {
                error = "root JSON value is not an object";
                return false;
            }

            if (root.TryGetValue("version", out object versionObject) &&
                TryFiniteNumber(versionObject, out float version) &&
                (int)version != CurrentVersion)
            {
                _warnings.Add(
                    $"{FileName} version {(int)version} differs from supported version {CurrentVersion}; known fields were loaded.");
            }

            if (!root.TryGetValue("regions", out object regionsObject) || regionsObject == null)
            {
                return true;
            }
            if (regionsObject is not Dictionary<string, object> regions)
            {
                error = "'regions' is not an object";
                return false;
            }

            foreach (KeyValuePair<string, object> regionPair in regions)
            {
                string regionId = NormalizeRegion(regionPair.Key);
                if (regionId.Length == 0 || regionPair.Value is not Dictionary<string, object> regionObject)
                {
                    _warnings.Add($"Skipped malformed region entry '{regionPair.Key}'.");
                    continue;
                }

                if (!regionObject.TryGetValue("rooms", out object roomsObject) || roomsObject == null)
                {
                    continue;
                }
                if (roomsObject is not Dictionary<string, object> rooms)
                {
                    _warnings.Add($"{regionId}: 'rooms' is not an object and was ignored.");
                    continue;
                }

                Dictionary<string, RoomEnvironmentProfile> parsedRooms = CreateRoomTable();
                foreach (KeyValuePair<string, object> roomPair in rooms)
                {
                    string roomName = NormalizeRoom(roomPair.Key);
                    if (roomName.Length == 0 || roomPair.Value is not Dictionary<string, object> roomObject)
                    {
                        _warnings.Add($"{regionId}: skipped malformed room entry '{roomPair.Key}'.");
                        continue;
                    }

                    if (TryParseProfile(regionId, roomName, roomObject, out RoomEnvironmentProfile profile))
                    {
                        parsedRooms[roomName] = profile;
                    }
                }

                if (parsedRooms.Count > 0)
                {
                    profiles[regionId] = parsedRooms;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseProfile(
        string regionId,
        string roomName,
        Dictionary<string, object> roomObject,
        out RoomEnvironmentProfile profile)
    {
        profile = null;
        float roomHeat = RoomHeatFactor.DefaultHeat;
        float sunlight = RoomEnvironmentProfile.DefaultSunlightIntensity;
        float roomShade = RoomEnvironmentProfile.DefaultRoomShade;
        float humidity = RoomEnvironmentProfile.DefaultHumidity;

        if (!TryReadField(roomObject, "roomHeat", regionId, roomName, ref roomHeat) ||
            !TryReadField(roomObject, "sunlightIntensity", regionId, roomName, ref sunlight) ||
            !TryReadField(roomObject, "roomShade", regionId, roomName, ref roomShade) ||
            !TryReadField(roomObject, "humidity", regionId, roomName, ref humidity))
        {
            return false;
        }

        float clampedHeat = RoomHeatFactor.ClampHeat(roomHeat);
        float clampedSunlight = RoomEnvironmentProfile.ClampUnit(sunlight);
        float clampedShade = RoomEnvironmentProfile.ClampUnit(roomShade);
        float clampedHumidity = RoomEnvironmentProfile.ClampSigned(humidity);
        WarnIfClamped(regionId, roomName, "roomHeat", roomHeat, clampedHeat, "[-1, 1]");
        WarnIfClamped(regionId, roomName, "sunlightIntensity", sunlight, clampedSunlight, "[0, 1]");
        WarnIfClamped(regionId, roomName, "roomShade", roomShade, clampedShade, "[0, 1]");
        WarnIfClamped(regionId, roomName, "humidity", humidity, clampedHumidity, "[-1, 1]");

        profile = new RoomEnvironmentProfile(
            clampedHeat,
            clampedSunlight,
            clampedShade,
            clampedHumidity);
        return true;
    }

    private static bool TryReadField(
        Dictionary<string, object> roomObject,
        string field,
        string regionId,
        string roomName,
        ref float value)
    {
        if (!roomObject.TryGetValue(field, out object fieldObject) || fieldObject == null)
        {
            return true;
        }
        if (TryFiniteNumber(fieldObject, out float parsed))
        {
            value = parsed;
            return true;
        }

        _warnings.Add($"{regionId}/{roomName}: '{field}' is not a finite number; the room entry was ignored.");
        return false;
    }

    private static Dictionary<string, object> BuildJsonRoot()
    {
        Dictionary<string, object> root = new()
        {
            ["version"] = CurrentVersion
        };
        Dictionary<string, object> regionsObject = new();

        List<string> regions = new(_profilesByRegion.Keys);
        regions.Sort(StringComparer.OrdinalIgnoreCase);
        for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            string regionId = regions[regionIndex];
            Dictionary<string, object> roomsObject = new();
            List<string> rooms = new(_profilesByRegion[regionId].Keys);
            rooms.Sort(StringComparer.OrdinalIgnoreCase);

            for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
            {
                string roomName = rooms[roomIndex];
                RoomEnvironmentProfile profile = _profilesByRegion[regionId][roomName];
                roomsObject[roomName] = new Dictionary<string, object>
                {
                    ["roomHeat"] = profile.RoomHeat,
                    ["sunlightIntensity"] = profile.SunlightIntensity,
                    ["roomShade"] = profile.RoomShade,
                    ["humidity"] = profile.Humidity
                };
            }

            regionsObject[regionId] = new Dictionary<string, object>
            {
                ["rooms"] = roomsObject
            };
        }

        root["regions"] = regionsObject;
        return root;
    }

    private static string ResolveTemperatureSetsPath(bool forSave)
    {
        string assemblyOwnedPath = ResolvePathFromContainingMod();
        if (!string.IsNullOrWhiteSpace(assemblyOwnedPath) && (forSave || File.Exists(assemblyOwnedPath)))
        {
            return assemblyOwnedPath;
        }

        try
        {
            if (ModManager.ActiveMods != null)
            {
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    ModManager.Mod mod = ModManager.ActiveMods[i];
                    if (mod == null ||
                        !string.Equals(mod.id, global::DryCycle.Plugin.RainWorldModId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] roots = { mod.path, mod.NewestPath, mod.TargetedPath, mod.basePath };
                    string firstCandidate = null;
                    for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                    {
                        if (string.IsNullOrWhiteSpace(roots[rootIndex]))
                        {
                            continue;
                        }
                        string candidate = Path.Combine(roots[rootIndex], "world", FileName);
                        firstCandidate ??= candidate;
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    if (forSave && firstCandidate != null)
                    {
                        return firstCandidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "TemperatureSets direct path lookup failed: " + ex.Message);
        }

        string resolved = AssetManager.ResolveFilePath("world/" + FileName);
        return forSave || File.Exists(resolved) ? resolved : null;
    }

    private static string ResolvePathFromContainingMod()
    {
        try
        {
            string assemblyPath = typeof(global::DryCycle.Plugin).Assembly.Location;
            string assemblyDirectoryPath = string.IsNullOrWhiteSpace(assemblyPath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (string.IsNullOrWhiteSpace(assemblyDirectoryPath))
            {
                return null;
            }

            DirectoryInfo directory = new(assemblyDirectoryPath);
            for (int depth = 0;
                 directory != null && depth < MaxAssemblyParentSearchDepth;
                 depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "modinfo.json")))
                {
                    return Path.Combine(directory.FullName, "world", FileName);
                }
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "TemperatureSets assembly path lookup failed: " + ex.Message);
        }
        return null;
    }

    private static bool TryFiniteNumber(object value, out float number)
    {
        number = 0f;
        try
        {
            number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return !float.IsNaN(number) && !float.IsInfinity(number);
        }
        catch
        {
            return false;
        }
    }

    private static void WarnIfClamped(
        string regionId,
        string roomName,
        string field,
        float original,
        float clamped,
        string range)
    {
        if (Math.Abs(original - clamped) > 0.0001f)
        {
            _warnings.Add(
                $"{regionId}/{roomName}: {field} {original.ToString(CultureInfo.InvariantCulture)} was clamped to {range}.");
        }
    }

    private static RoomEnvironmentProfile CloneProfile(RoomEnvironmentProfile profile)
    {
        return new RoomEnvironmentProfile(
            profile?.RoomHeat ?? RoomHeatFactor.DefaultHeat,
            profile?.SunlightIntensity ?? RoomEnvironmentProfile.DefaultSunlightIntensity,
            profile?.RoomShade ?? RoomEnvironmentProfile.DefaultRoomShade,
            profile?.Humidity ?? RoomEnvironmentProfile.DefaultHumidity);
    }

    private static bool ProfilesEqual(RoomEnvironmentProfile a, RoomEnvironmentProfile b)
    {
        return a != null && b != null &&
               Math.Abs(a.RoomHeat - b.RoomHeat) <= 0.0001f &&
               Math.Abs(a.SunlightIntensity - b.SunlightIntensity) <= 0.0001f &&
               Math.Abs(a.RoomShade - b.RoomShade) <= 0.0001f &&
               Math.Abs(a.Humidity - b.Humidity) <= 0.0001f;
    }

    private static string NormalizeRegion(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizeRoom(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static int CountProfiles(
        Dictionary<string, Dictionary<string, RoomEnvironmentProfile>> profiles)
    {
        int count = 0;
        foreach (Dictionary<string, RoomEnvironmentProfile> rooms in profiles.Values)
        {
            count += rooms.Count;
        }
        return count;
    }

    private static void RememberFileState(string path)
    {
        try
        {
            FileInfo info = new(path);
            _loadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            _loadedLength = info.Exists ? info.Length : -1L;
        }
        catch
        {
            _loadedWriteUtc = DateTime.MinValue;
            _loadedLength = -1L;
        }
    }

    private static void ResetFileTracking()
    {
        _loadedWriteUtc = DateTime.MinValue;
        _loadedLength = -1L;
        _ticksUntilPoll = 0;
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

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup after a failed save.
        }
    }
}
