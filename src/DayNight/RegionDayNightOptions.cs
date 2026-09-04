using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Menu.Remix;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace DryCycle.DayNight;

/// <summary>
/// DryCycle Remix configuration. The original region/day-night controls remain here,
/// and RopeSpear gameplay controls share the same OptionInterface because Rain World
/// only registers one Remix interface per mod ID.
/// </summary>
internal sealed class RegionDayNightOptions : OptionInterface
{
    private const float RowHeight = 32f;

    private static RegionDayNightOptions _instance;

    private readonly Dictionary<string, Configurable<bool>> _regionEnabled =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _regionOrder = new();

    private Configurable<bool> _legacyIndividualPlacedObjectViewer;
    private Configurable<bool> _legacyFadePaletteCombiner;

    private Configurable<bool> _ropeSpearEightWayThrow;
    private Configurable<bool> _ropeSpearAimIndicator;
    private Configurable<int> _ropeSpearAimHoldFrames;
    private Configurable<bool> _ropeSpearModeSwitch;

    internal static bool EnableLegacyIndividualPlacedObjectViewer
        => _instance?._legacyIndividualPlacedObjectViewer?.Value ?? false;

    internal static bool EnableLegacyFadePaletteCombiner
        => _instance?._legacyFadePaletteCombiner?.Value ?? false;

    internal static bool RopeSpearEightWayThrowEnabled
        => _instance?._ropeSpearEightWayThrow?.Value ?? true;

    internal static bool RopeSpearAimIndicatorEnabled
        => _instance?._ropeSpearAimIndicator?.Value ?? true;

    internal static int RopeSpearAimHoldFrames
        => Mathf.Clamp(_instance?._ropeSpearAimHoldFrames?.Value ?? 8, 1, 20);

    internal static bool RopeSpearModeSwitchEnabled
        => _instance?._ropeSpearModeSwitch?.Value ?? true;

    internal static void Register()
    {
        _instance ??= new RegionDayNightOptions();
        _instance.BindKnownRegions();
        _instance.BindCompatibilityOptions();
        _instance.BindRopeSpearOptions();

        // MachineConnector keys option interfaces by Rain World's modinfo.json ID,
        // not by the BepInEx plugin GUID. DryCycle is shipped inside Ancient Site,
        // whose actual Rain World mod ID is NR.B5.
        if (!MachineConnector.SetRegisteredOI(Plugin.RainWorldModId, _instance))
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle could not register its Remix option interface for " +
                $"Rain World mod '{Plugin.RainWorldModId}'; DryCycle settings will " +
                "use their default values.");
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
        BindCompatibilityOptions();
        BindRopeSpearOptions();

        Tabs = new[]
        {
            new OpTab(this, Translate("Regions")),
            new OpTab(this, Translate("RopeSpear")),
            new OpTab(this, Translate("Compatibility"))
        };

        BuildRopeSpearTab(Tabs[1]);
        BuildCompatibilityTab(Tabs[2]);

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

    private void BuildRopeSpearTab(OpTab tab)
    {
        OpLabel title = new(
            new Vector2(150f, 530f),
            new Vector2(300f, 30f),
            "RopeSpear Controls",
            FLabelAlignment.Center,
            bigText: true);

        OpLabel description = new(
            new Vector2(30f, 455f),
            new Vector2(540f, 66f),
            "Tap Throw for the normal horizontal cast. Hold Throw to enter eight-direction aim, press any movement direction to select N/NE/E/SE/S/SW/W/NW, then release Throw. Releasing the movement keys keeps the last selected direction. Double-tap Alt switches long/short rope mode.",
            FLabelAlignment.Left);

        OpCheckBox eightWay = new(_ropeSpearEightWayThrow, new Vector2(32f, 390f));
        eightWay.description =
            "Enable RopeSpear hold-to-aim eight-direction throwing. When disabled, RopeSpear uses normal Rain World horizontal throwing.";
        OpLabel eightWayLabel = new(72f, 390f, "Eight-Direction Throwing")
        {
            bumpBehav = eightWay.bumpBehav,
            description = eightWay.description
        };

        OpCheckBox indicator = new(_ropeSpearAimIndicator, new Vector2(32f, 340f));
        indicator.description =
            "Show the eight-direction world-space aim guide while RopeSpear aim mode is active.";
        OpLabel indicatorLabel = new(72f, 340f, "Show Aim Guide")
        {
            bumpBehav = indicator.bumpBehav,
            description = indicator.description
        };

        OpSlider holdFrames = new(
            _ropeSpearAimHoldFrames,
            new Vector2(32f, 275f),
            250);
        holdFrames.description =
            "How many gameplay frames Throw must be held before RopeSpear enters eight-direction aim. Rain World normally updates gameplay at about 40 frames per second.";
        OpLabel holdFramesLabel = new(312f, 278f, "Aim Hold Delay (frames)")
        {
            bumpBehav = holdFrames.bumpBehav,
            description = holdFrames.description
        };

        OpCheckBox modeSwitch = new(_ropeSpearModeSwitch, new Vector2(32f, 210f));
        modeSwitch.description =
            "Allow double-tapping Alt while holding RopeSpear to switch between long and short rope modes. Disabling this forces held RopeSpears back to long mode.";
        OpLabel modeSwitchLabel = new(72f, 210f, "Long / Short Rope Mode Switch")
        {
            bumpBehav = modeSwitch.bumpBehav,
            description = modeSwitch.description
        };

        OpLabel safety = new(
            new Vector2(30f, 105f),
            new Vector2(540f, 70f),
            "Directional throws preserve the vanilla throw force and rotate the projectile velocity rather than replacing it with a fixed speed. Any-angle RopeSpear wall sticking remains active so diagonal and vertical throws can interact with terrain.",
            FLabelAlignment.Left);

        UIfocusable.MutualVerticalFocusableBind(eightWay, indicator);
        UIfocusable.MutualVerticalFocusableBind(indicator, holdFrames);
        UIfocusable.MutualVerticalFocusableBind(holdFrames, modeSwitch);

        tab.AddItems(
            title,
            description,
            eightWay,
            eightWayLabel,
            indicator,
            indicatorLabel,
            holdFrames,
            holdFramesLabel,
            modeSwitch,
            modeSwitchLabel,
            safety);
    }

    private void BuildCompatibilityTab(OpTab tab)
    {
        OpLabel title = new(
            new Vector2(150f, 530f),
            new Vector2(300f, 30f),
            "RegionKit Fallbacks",
            FLabelAlignment.Center,
            bigText: true);

        OpLabel description = new(
            new Vector2(30f, 470f),
            new Vector2(520f, 44f),
            "Temporary standalone replacements for RegionKit utilities. Keep these disabled when RegionKit is working to avoid duplicate DevTools hooks/UI.",
            FLabelAlignment.Left);

        OpCheckBox objectViewer = new(_legacyIndividualPlacedObjectViewer, new Vector2(32f, 410f));
        objectViewer.description =
            "Enable DryCycle's standalone replacement for RegionKit IndividualPlacedObjectViewer. Requires a mod reload/restart after changing.";
        OpLabel objectViewerLabel = new(72f, 410f, "Legacy Individual Placed Object Viewer")
        {
            bumpBehav = objectViewer.bumpBehav,
            description = objectViewer.description
        };

        OpCheckBox fadeCombiner = new(_legacyFadePaletteCombiner, new Vector2(32f, 360f));
        fadeCombiner.description =
            "Enable DryCycle's standalone replacement for RegionKit FadePaletteCombiner. Requires a mod reload/restart after changing.";
        OpLabel fadeCombinerLabel = new(72f, 360f, "Legacy Fade Palette Combiner")
        {
            bumpBehav = fadeCombiner.bumpBehav,
            description = fadeCombiner.description
        };

        UIfocusable.MutualVerticalFocusableBind(objectViewer, fadeCombiner);
        tab.AddItems(title, description, objectViewer, objectViewerLabel, fadeCombiner, fadeCombinerLabel);
    }

    private void BindRopeSpearOptions()
    {
        _ropeSpearEightWayThrow ??= config.Bind(
            "RopeSpearEightWayThrow",
            defaultValue: true,
            new ConfigurableInfo(
                "Enable RopeSpear hold-to-aim eight-direction throwing. Disable to keep normal horizontal throwing.",
                null,
                "RopeSpear",
                "Eight-Direction Throwing"));

        _ropeSpearAimIndicator ??= config.Bind(
            "RopeSpearAimIndicator",
            defaultValue: true,
            new ConfigurableInfo(
                "Show a world-space eight-direction guide while RopeSpear aim mode is active.",
                null,
                "RopeSpear",
                "Show Aim Guide"));

        _ropeSpearAimHoldFrames ??= config.Bind(
            "RopeSpearAimHoldFrames",
            defaultValue: 8,
            new ConfigurableInfo(
                "Gameplay frames Throw must be held before RopeSpear enters eight-direction aim.",
                new ConfigAcceptableRange<int>(1, 20),
                "RopeSpear",
                "Aim Hold Delay"));

        _ropeSpearModeSwitch ??= config.Bind(
            "RopeSpearModeSwitch",
            defaultValue: true,
            new ConfigurableInfo(
                "Enable the double-Alt long/short RopeSpear mode switch. Disable to force long mode.",
                null,
                "RopeSpear",
                "Long / Short Rope Mode Switch"));
    }

    private void BindCompatibilityOptions()
    {
        _legacyIndividualPlacedObjectViewer ??= config.Bind(
            "LegacyIndividualPlacedObjectViewer",
            defaultValue: false,
            new ConfigurableInfo(
                "Enable DryCycle's temporary standalone replacement for RegionKit IndividualPlacedObjectViewer. Leave disabled when RegionKit is available.",
                null,
                "Compatibility",
                "Legacy Individual Placed Object Viewer"));

        _legacyFadePaletteCombiner ??= config.Bind(
            "LegacyFadePaletteCombiner",
            defaultValue: false,
            new ConfigurableInfo(
                "Enable DryCycle's temporary standalone replacement for RegionKit FadePaletteCombiner. Leave disabled when RegionKit is available.",
                null,
                "Compatibility",
                "Legacy Fade Palette Combiner"));
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
