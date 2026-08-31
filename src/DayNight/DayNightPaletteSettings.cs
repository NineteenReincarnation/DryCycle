using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace DryCycle.DayNight;

internal static class DayNightPaletteSettings
{
    internal const int DefaultDuskPalette = 23;
    internal const int DefaultNightPalette = 10;

    private const string DuskKey = "DryCycleDuskPalette";
    private const string NightKey = "DryCycleNightPalette";

    private static ConditionalWeakTable<RoomSettings, Values> _values = new();
    private static bool _enabled;

    internal sealed class Values
    {
        public int DuskPalette = DefaultDuskPalette;
        public int NightPalette = DefaultNightPalette;
    }

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RoomSettings.Save_string_bool += RoomSettings_Save_string_bool;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomSettings.Save_string_bool -= RoomSettings_Save_string_bool;
        Reset();
        _enabled = false;
    }

    public static Values Get(RoomSettings roomSettings)
    {
        if (roomSettings == null)
        {
            return new Values();
        }

        return _values.GetValue(roomSettings, Load);
    }

    public static void Reset()
    {
        _values = new ConditionalWeakTable<RoomSettings, Values>();
    }

    private static void RoomSettings_Save_string_bool(
        On.RoomSettings.orig_Save_string_bool orig,
        RoomSettings self,
        string path,
        bool saveAsTemplate)
    {
        orig(self, path, saveAsTemplate);

        if (self == null || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        Values values = Get(self);

        try
        {
            // RoomSettings.Save rewrites the vanilla file, but existing DryCycle lines
            // can survive/accumulate through repeated DevUI saves depending on the
            // surrounding mod stack. Remove every previous instance and write exactly
            // one authoritative pair so the file remains stable over long editing
            // sessions and Load never depends on duplicate-key ordering.
            string[] original = File.ReadAllLines(path);
            List<string> rewritten = new(original.Length + 2);
            for (int i = 0; i < original.Length; i++)
            {
                string line = original[i];
                if (IsDryCyclePaletteLine(line))
                {
                    continue;
                }

                rewritten.Add(line);
            }

            rewritten.Add(
                DuskKey + ": " + values.DuskPalette.ToString(CultureInfo.InvariantCulture));
            rewritten.Add(
                NightKey + ": " + values.NightPalette.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(path, rewritten);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"DryCycle DayNight: failed to save palette settings for {self.name}: {ex}");
        }
    }

    private static bool IsDryCyclePaletteLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        string key = line.Substring(0, separator).Trim();
        return string.Equals(key, DuskKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, NightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static Values Load(RoomSettings roomSettings)
    {
        Values values = new();
        string path = roomSettings.filePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return values;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string rawValue = line.Substring(separator + 1).Trim();
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int palette))
                {
                    continue;
                }

                palette = Math.Max(0, palette);
                if (string.Equals(key, DuskKey, StringComparison.OrdinalIgnoreCase))
                {
                    values.DuskPalette = palette;
                }
                else if (string.Equals(key, NightKey, StringComparison.OrdinalIgnoreCase))
                {
                    values.NightPalette = palette;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"DryCycle DayNight: failed to load palette settings for {roomSettings.name}: {ex}");
        }

        return values;
    }
}
