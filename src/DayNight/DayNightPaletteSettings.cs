using System;
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

    internal sealed class Values
    {
        public int DuskPalette = DefaultDuskPalette;
        public int NightPalette = DefaultNightPalette;
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

    public static void Save(RoomSettings roomSettings)
    {
        if (roomSettings == null || string.IsNullOrEmpty(roomSettings.filePath))
        {
            return;
        }

        Values values = Get(roomSettings);

        try
        {
            // Vanilla RoomSettings.Save rewrites the settings file from scratch.
            // These DryCycle lines are appended afterwards by the DevUI save hook.
            using StreamWriter writer = File.AppendText(roomSettings.filePath);
            writer.WriteLine(DuskKey + ": " + values.DuskPalette.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(NightKey + ": " + values.NightPalette.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"DryCycle DayNight: failed to save palette settings for {roomSettings.name}: {ex}");
        }
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
