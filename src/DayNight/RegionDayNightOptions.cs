using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Menu.Remix;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace DryCycle.DayNight;

/// <summary>
/// Remix configuration for opting individual registered regions into DryCycle's
/// day/night world clock and the weather system built on top of it.
/// </summary>
internal sealed class RegionDayNightOptions : OptionInterface
{
    private const float RowHeight = 32f;

    private static RegionDayNightOptions _instance;

    private readonly Dictionary<string, Configurable<bool>> _regionEnabled =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _regionOrder = new();

    internal static void Register()
    {
        _instance ??= new RegionDayNightOptions();
        _instance.BindKnownRegions();

        if (!MachineConnector.SetRegisteredOI(Plugin.ModId, _instance))
        {
            Plugin.Logger?.LogWarning(
                "DryCycle could not register its Remix option interface; " +
                "region day/night settings will use their default enabled state.");
        }
    }

    /// <summary>
    /// Returns whether DryCycle owns the day/night clock and weather for this world.
    /// Worlds without a registered Region are deliberately left to vanilla logic.
    /// </summary>
    internal static bool IsEnabled(World world)
    {
        if (world?.region == null || string.IsNullOrWhiteSpace(world.region.name))
        {
            return false;
        }

        return IsEnabled(world.region.name);
    }

    internal static bool IsEnabled(string regionId)
    {
        string normalized = NormalizeRegionId(regionId);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Before Remix has registered/loaded the interface, preserve DryCycle's
        // existing behavior. Every discovered region is bound with true by default.
        if (_instance == null ||
            !_instance._regionEnabled.TryGetValue(normalized, out Configurable<bool> setting))
        {
            return true;
        }

        return setting.Value;
    }

    public override void Initialize()
    {
        base.Initialize();
        BindKnownRegions();

        Tabs = new[]
        {
            new OpTab(this, Translate("Regions"))
        };

        float contentHeight = Math.Max(600f, 125f + _regionOrder.Count * RowHeight);
        OpScrollBox scroll = new(Tabs[0], contentHeight);

        float y = contentHeight - 48f;
        OpLabel title = new(
            new Vector2(150f, y),
            new Vector2(300f, 30f),
            "DryCycle Regions",
            FLabelAlignment.Center,
            bigText: true);
        scroll.AddItems(title);

        y -= 48f;
        OpLabel description = new(
            new Vector2(30f, y),
            new Vector2(520f, 38f),
            "Enable DryCycle day/night and weather per registered region. " +
            "Disabled regions keep their original Rain World / DLC / mod logic.",
            FLabelAlignment.Left);
        scroll.AddItems(description);
        y -= 48f;

        if (_regionOrder.Count == 0)
        {
            scroll.AddItems(new OpLabel(30f, y, "No registered regions were found."));
            return;
        }

        UIfocusable previous = null;
        for (int i = 0; i < _regionOrder.Count; i++)
        {
            string regionId = _regionOrder[i];
            if (!_regionEnabled.TryGetValue(regionId, out Configurable<bool> setting))
            {
                continue;
            }

            OpCheckBox checkBox = new(setting, new Vector2(32f, y));
            string displayName = DisplayName(regionId);
            string tooltip =
                $"Enable DryCycle day/night and weather in {displayName}. " +
                "When disabled, this region uses its original cycle, palette and weather behavior.";

            checkBox.description = tooltip;
            if (previous != null)
            {
                UIfocusable.MutualVerticalFocusableBind(previous, checkBox);
            }
            previous = checkBox;

            OpLabel label = new(72f, y, displayName)
            {
                bumpBehav = checkBox.bumpBehav,
                description = tooltip
            };

            scroll.AddItems(checkBox, label);
            y -= RowHeight;
        }
    }

    private void BindKnownRegions()
    {
        List<string> regions = EnumerateRegisteredRegions();
        for (int i = 0; i < regions.Count; i++)
        {
            string regionId = NormalizeRegionId(regions[i]);
            if (regionId.Length == 0)
            {
                continue;
            }

            if (!_regionOrder.Exists(x => string.Equals(x, regionId, StringComparison.OrdinalIgnoreCase)))
            {
                _regionOrder.Add(regionId);
            }

            if (_regionEnabled.ContainsKey(regionId))
            {
                continue;
            }

            string key = ConfigKey(regionId);
            Configurable<bool> setting = config.Bind(
                key,
                defaultValue: true,
                new ConfigurableInfo(
                    $"Enable DryCycle day/night and weather in region {regionId}. " +
                    "Disable to keep that region's original logic.",
                    null,
                    "Regions",
                    regionId));

            _regionEnabled.Add(regionId, setting);
        }
    }

    private static List<string> EnumerateRegisteredRegions()
    {
        List<string> regions = new();

        // This gives the canonical vanilla/MSC order and also includes the normal
        // modded-region path when Rain World's ModdedRegionsEnabled flag is active.
        try
        {
            List<string> ordered = Region.GetFullRegionOrder();
            if (ordered != null)
            {
                regions.AddRange(ordered);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"DryCycle failed to query Region.GetFullRegionOrder: {ex.Message}");
        }

        // World loading itself reads the merged World/regions.txt. Read it directly as
        // well so official DLC (including Watcher) and active region mods are not lost
        // behind GetFullRegionOrder's conditional ModdedRegionsEnabled branch.
        try
        {
            string path = AssetManager.ResolveFilePath(
                "World" + Path.DirectorySeparatorChar + "regions.txt");
            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i]?.Trim();
                    if (!string.IsNullOrEmpty(line) && !line.StartsWith("#"))
                    {
                        regions.Add(line);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"DryCycle failed to read the merged region registry: {ex.Message}");
        }

        List<string> unique = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < regions.Count; i++)
        {
            string normalized = NormalizeRegionId(regions[i]);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                unique.Add(normalized);
            }
        }

        return unique;
    }

    private static string DisplayName(string regionId)
    {
        string fullName;
        try
        {
            fullName = Region.GetRegionFullName(regionId, null);
        }
        catch
        {
            fullName = null;
        }

        if (string.IsNullOrWhiteSpace(fullName) ||
            string.Equals(fullName, "Unknown Region", StringComparison.OrdinalIgnoreCase))
        {
            return regionId;
        }

        return $"{regionId} — {fullName}";
    }

    private static string NormalizeRegionId(string regionId)
    {
        return (regionId ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string ConfigKey(string regionId)
    {
        // Remix config keys only allow letters, digits and underscores. UTF-8 hex is
        // stable, collision-free and also supports unusually named modded regions.
        byte[] bytes = Encoding.UTF8.GetBytes(regionId);
        StringBuilder builder = new("RegionDayNight_");
        for (int i = 0; i < bytes.Length; i++)
        {
            builder.Append(bytes[i].ToString("X2"));
        }

        return builder.ToString();
    }
}
