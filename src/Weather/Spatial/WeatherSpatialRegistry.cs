using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialRegistry
{
    private const int CurrentVersion = 2;
    private const int HotReloadFrames = 120;
    internal const string FileName = "WeatherSpatial.json";

    private static readonly Dictionary<string, WeatherSpatialRegionRules> Regions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ParseWarnings = new();

    private static WeatherSpatialRule _globalDefault = WeatherSpatialRule.Deny;
    private static DateTime _lastWriteUtc;
    private static int _lastPollFrame = int.MinValue;
    private static bool _recoveredFromBackup;

    internal static string LoadedPath { get; private set; }
    internal static bool Dirty { get; private set; }
    internal static string FatalLoadError { get; private set; }
    internal static IReadOnlyList<string> Warnings => ParseWarnings;
    internal static WeatherSpatialRule GlobalDefault => _globalDefault;

    internal static void Reload()
    {
        if (Dirty)
        {
            return;
        }

        string path = ResolvePath(forSave: true);
        LoadedPath = path;
        _lastPollFrame = int.MinValue;
        _recoveredFromBackup = false;
        FatalLoadError = null;
        ParseWarnings.Clear();
        Regions.Clear();
        RegionSchedules.Clear();
        _globalDefault = WeatherSpatialRule.Deny;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Dirty = false;
            _lastWriteUtc = default;
            return;
        }

        if (TryLoadFile(path, out string primaryError))
        {
            Dirty = false;
            RememberWriteTime(path);
            return;
        }

        string backupPath = path + ".bak";
        string primaryWarning = $"Primary {FileName} is invalid: {primaryError}";
        if (File.Exists(backupPath) && TryLoadFile(backupPath, out string backupError))
        {
            ParseWarnings.Insert(0, primaryWarning);
            ParseWarnings.Add($"Recovered {FileName} from '{Path.GetFileName(backupPath)}'. Save from DevTools to repair the primary file.");
            Dirty = true;
            _recoveredFromBackup = true;
            RememberWriteTime(path);
            return;
        }

        Regions.Clear();
        RegionSchedules.Clear();
        _globalDefault = WeatherSpatialRule.Deny;
        Dirty = false;
        FatalLoadError = File.Exists(backupPath)
            ? $"{FileName} and its .bak are both invalid. Primary: {primaryError}; backup could not be parsed."
            : $"{FileName} is invalid and no valid .bak exists: {primaryError}";
        Plugin.Logger?.LogError("DryCycle weather spatial: " + FatalLoadError);
    }

    internal static void PollHotReload(int frame)
    {
        if (Dirty)
        {
            return;
        }

        if (_lastPollFrame != int.MinValue &&
            frame >= _lastPollFrame &&
            frame - _lastPollFrame < HotReloadFrames)
        {
            return;
        }
        _lastPollFrame = frame;

        string path = LoadedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            string resolved = ResolvePath(forSave: true);
            if (!string.Equals(resolved, LoadedPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)))
            {
                Reload();
            }
            return;
        }

        try
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (write != _lastWriteUtc)
            {
                Reload();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("DryCycle weather spatial hot-reload check failed: " + ex.Message);
        }
    }

    internal static WeatherSpatialRule GetDefaultRule(
        string regionId,
        in WeatherSpatialTarget target)
    {
        if (!TryGetRegion(regionId, out WeatherSpatialRegionRules region))
        {
            return WeatherSpatialRule.Inherit;
        }

        if (target.IsFamily)
        {
            return GetRule(region.FamilyDefaults, target.FamilyId);
        }
        return GetRule(region.WeatherDefaults, target.Key);
    }

    internal static WeatherSpatialRule GetRoomRule(
        string regionId,
        string roomName,
        in WeatherSpatialTarget target)
    {
        if (!TryGetRoom(regionId, roomName, out WeatherSpatialRoomRules room))
        {
            return WeatherSpatialRule.Inherit;
        }

        if (target.IsFamily)
        {
            return GetRule(room.Families, target.FamilyId);
        }
        return GetRule(room.Weather, target.Key);
    }

    internal static void SetDefaultRule(
        string regionId,
        in WeatherSpatialTarget target,
        WeatherSpatialRule rule)
    {
        string regionKey = NormalizeRegion(regionId);
        if (regionKey.Length == 0)
        {
            return;
        }

        WeatherSpatialRegionRules region = GetOrCreateRegion(regionKey);
        if (target.IsFamily)
        {
            SetRule(region.FamilyDefaults, target.FamilyId, rule);
        }
        else
        {
            SetRule(region.WeatherDefaults, target.Key, rule);
        }
        TrimRegion(regionKey, region);
        Dirty = true;
    }

    internal static bool CanSetRoomRule(
        string regionId,
        string roomName,
        in WeatherSpatialTarget target,
        WeatherSpatialRule rule)
    {
        string regionKey = NormalizeRegion(regionId);
        string roomKey = (roomName ?? string.Empty).Trim();
        if (regionKey.Length == 0 || roomKey.Length == 0)
        {
            return false;
        }

        // Family rows establish the parent scope. Inherit is always allowed so old
        // orphaned child overrides can still be removed after this prerequisite was added.
        if (target.IsFamily || rule == WeatherSpatialRule.Inherit)
        {
            return true;
        }

        if (!WeatherSpatialCatalog.TryGetFamily(
                target.Kind,
                target.WeatherId,
                out WeatherSpatialFamily family))
        {
            return false;
        }

        return IsFamilyAllowed(regionKey, roomKey, family.Id);
    }

    internal static bool SetRoomRule(
        string regionId,
        string roomName,
        in WeatherSpatialTarget target,
        WeatherSpatialRule rule)
    {
        string regionKey = NormalizeRegion(regionId);
        string roomKey = (roomName ?? string.Empty).Trim();
        if (regionKey.Length == 0 || roomKey.Length == 0)
        {
            return false;
        }

        if (!CanSetRoomRule(regionKey, roomKey, target, rule))
        {
            return false;
        }

        WeatherSpatialRegionRules region = GetOrCreateRegion(regionKey);
        if (!region.Rooms.TryGetValue(roomKey, out WeatherSpatialRoomRules room))
        {
            room = new WeatherSpatialRoomRules();
            region.Rooms[roomKey] = room;
        }

        if (target.IsFamily)
        {
            SetRule(room.Families, target.FamilyId, rule);
        }
        else
        {
            SetRule(room.Weather, target.Key, rule);
        }

        if (room.IsEmpty)
        {
            region.Rooms.Remove(roomKey);
        }
        TrimRegion(regionKey, region);
        Dirty = true;
        return true;
    }
}
