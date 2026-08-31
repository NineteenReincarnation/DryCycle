using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.Weather.Climate;

internal sealed class ClimateChanceEntry
{
    internal string Id { get; }
    internal float ChancePercent { get; }

    internal ClimateChanceEntry(string id, float chancePercent)
    {
        Id = id;
        ChancePercent = Math.Max(0f, Math.Min(100f, chancePercent));
    }
}

internal sealed class WeatherFamilyClimateEntry : ClimateChanceEntry
{
    private readonly List<ClimateChanceEntry> _variants = new();

    internal IReadOnlyList<ClimateChanceEntry> Variants => _variants;

    internal WeatherFamilyClimateEntry(string id, float chancePercent)
        : base(id, chancePercent)
    {
    }

    internal void AddVariant(ClimateChanceEntry variant)
    {
        if (variant != null)
        {
            _variants.Add(variant);
        }
    }
}

internal sealed class RegionClimateProfile
{
    private readonly List<WeatherFamilyClimateEntry> _weather = new();
    private readonly List<ClimateChanceEntry> _danger = new();

    internal string RegionId { get; }
    internal IReadOnlyList<WeatherFamilyClimateEntry> Weather => _weather;
    internal IReadOnlyList<ClimateChanceEntry> DangerTypes => _danger;

    internal RegionClimateProfile(string regionId)
    {
        RegionId = regionId;
    }

    internal void AddWeather(WeatherFamilyClimateEntry entry)
    {
        if (entry != null)
        {
            _weather.Add(entry);
        }
    }

    internal void AddDanger(ClimateChanceEntry entry)
    {
        if (entry != null)
        {
            _danger.Add(entry);
        }
    }

    internal bool ContainsWeatherId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        for (int i = 0; i < _weather.Count; i++)
        {
            WeatherFamilyClimateEntry family = _weather[i];
            if (string.Equals(family.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (int j = 0; j < family.Variants.Count; j++)
            {
                if (string.Equals(family.Variants[j].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal bool ContainsDangerId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        for (int i = 0; i < _danger.Count; i++)
        {
            if (string.Equals(_danger[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Strict loader for World/RegionClimate.txt.
///
/// Grammar used by the live scheduling sections:
/// 0 tabs: [Weather] / [DangerType]
/// 1 tab : RegionId:
/// 2 tabs: WeatherFamily : chance OR DangerType : chance
/// 3 tabs: WeatherVariant : chance
///
/// A chance is an independent 0..100 percentage. A trailing '%' is optional.
/// // starts a line comment. Spaces are deliberately not treated as indentation;
/// the authored format requires literal tabs so malformed hierarchy is visible.
/// defaultWeather/defaultDangerType are accepted as reserved sections but are not
/// applied until their inheritance semantics are authored explicitly.
/// </summary>
internal static class RegionClimateRegistry
{
    private enum Section
    {
        None,
        Weather,
        DangerType,
        ReservedDefault
    }

    private static readonly Dictionary<string, RegionClimateProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase);

    internal static string LoadedPath { get; private set; }

    internal static void Reload()
    {
        Profiles.Clear();
        LoadedPath = null;

        string path = ResolveClimatePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Plugin.Logger?.LogWarning(
                "DryCycle weather climate file was not found at World/RegionClimate.txt.");
            return;
        }

        LoadedPath = path;
        Parse(File.ReadAllLines(path));

        Plugin.Logger?.LogInfo(
            $"DryCycle loaded {Profiles.Count} region climate profile(s) from '{path}'.");

        if (Profiles.TryGetValue("SU", out RegionClimateProfile su))
        {
            Plugin.Logger?.LogInfo(
                $"DryCycle SU climate ready: {su.Weather.Count} weather family/families, " +
                $"{su.DangerTypes.Count} danger type(s).");
        }
    }

    internal static bool TryGetProfile(string regionId, out RegionClimateProfile profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(regionId))
        {
            return false;
        }

        return Profiles.TryGetValue(regionId.Trim(), out profile);
    }

    internal static bool RegionCanUseWeather(string regionId, string weatherId)
    {
        return TryGetProfile(regionId, out RegionClimateProfile profile) &&
               profile.ContainsWeatherId(weatherId);
    }

    internal static bool RegionCanUseDanger(string regionId, string dangerId)
    {
        return TryGetProfile(regionId, out RegionClimateProfile profile) &&
               profile.ContainsDangerId(dangerId);
    }

    private static string ResolveClimatePath()
    {
        string[] assetPaths =
        {
            "World/RegionClimate.txt",
            "world/RegionClimate.txt"
        };

        for (int i = 0; i < assetPaths.Length; i++)
        {
            try
            {
                string resolved = AssetManager.ResolveFilePath(assetPaths[i]);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                {
                    return resolved;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning(
                    $"DryCycle failed resolving '{assetPaths[i]}': {ex.Message}");
            }
        }

        return null;
    }

    private static void Parse(string[] lines)
    {
        Section section = Section.None;
        RegionClimateProfile currentRegion = null;
        WeatherFamilyClimateEntry currentFamily = null;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string raw = StripComment(lines[lineIndex] ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            int tabs = CountLeadingTabs(raw);
            if (tabs < raw.Length && raw[tabs] == ' ')
            {
                Warn(lineIndex, "indentation must use literal TAB characters, not spaces");
                continue;
            }

            string text = raw.Substring(tabs).TrimEnd();
            if (tabs == 0 && text.StartsWith("[") && text.EndsWith("]"))
            {
                section = ParseSection(text);
                currentRegion = null;
                currentFamily = null;
                continue;
            }

            if (section == Section.ReservedDefault)
            {
                continue;
            }

            if (section != Section.Weather && section != Section.DangerType)
            {
                Warn(lineIndex, "entry appears outside [Weather] or [DangerType]");
                continue;
            }

            if (tabs == 1)
            {
                if (!text.EndsWith(":"))
                {
                    Warn(lineIndex, "region entry must end with ':'");
                    currentRegion = null;
                    currentFamily = null;
                    continue;
                }

                string regionId = NormalizeId(text.Substring(0, text.Length - 1));
                if (regionId.Length == 0)
                {
                    Warn(lineIndex, "region ID is empty");
                    continue;
                }

                currentRegion = GetOrCreateProfile(regionId);
                currentFamily = null;
                continue;
            }

            if (currentRegion == null)
            {
                Warn(lineIndex, "weather entry has no active region");
                continue;
            }

            if (tabs == 2)
            {
                if (!TryParseChanceEntry(text, out ClimateChanceEntry parsed))
                {
                    Warn(lineIndex, "expected 'Id : probability'");
                    currentFamily = null;
                    continue;
                }

                if (section == Section.Weather)
                {
                    currentFamily = new WeatherFamilyClimateEntry(
                        parsed.Id,
                        parsed.ChancePercent);
                    currentRegion.AddWeather(currentFamily);
                }
                else
                {
                    currentRegion.AddDanger(parsed);
                    currentFamily = null;
                }

                continue;
            }

            if (tabs == 3 && section == Section.Weather)
            {
                if (currentFamily == null)
                {
                    Warn(lineIndex, "weather variant has no parent weather family");
                    continue;
                }

                if (!TryParseChanceEntry(text, out ClimateChanceEntry variant))
                {
                    Warn(lineIndex, "expected 'VariantId : probability'");
                    continue;
                }

                currentFamily.AddVariant(variant);
                continue;
            }

            Warn(lineIndex, $"unexpected indentation level {tabs}");
        }
    }

    private static Section ParseSection(string text)
    {
        string name = text.Substring(1, text.Length - 2).Trim();
        if (string.Equals(name, "Weather", StringComparison.OrdinalIgnoreCase))
        {
            return Section.Weather;
        }

        if (string.Equals(name, "DangerType", StringComparison.OrdinalIgnoreCase))
        {
            return Section.DangerType;
        }

        if (string.Equals(name, "defaultWeather", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "defaultDangerType", StringComparison.OrdinalIgnoreCase))
        {
            return Section.ReservedDefault;
        }

        return Section.None;
    }

    private static RegionClimateProfile GetOrCreateProfile(string regionId)
    {
        if (!Profiles.TryGetValue(regionId, out RegionClimateProfile profile))
        {
            profile = new RegionClimateProfile(regionId);
            Profiles.Add(regionId, profile);
        }

        return profile;
    }

    private static bool TryParseChanceEntry(string text, out ClimateChanceEntry entry)
    {
        entry = null;
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon >= text.Length - 1)
        {
            return false;
        }

        string id = NormalizeId(text.Substring(0, colon));
        string value = text.Substring(colon + 1).Trim();
        if (value.EndsWith("%", StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - 1).Trim();
        }

        if (id.Length == 0 ||
            !float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float chance))
        {
            return false;
        }

        entry = new ClimateChanceEntry(id, chance);
        return true;
    }

    private static string StripComment(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line.Substring(0, comment) : line;
    }

    private static int CountLeadingTabs(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == '\t')
        {
            count++;
        }

        return count;
    }

    private static string NormalizeId(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static void Warn(int zeroBasedLine, string message)
    {
        Plugin.Logger?.LogWarning(
            $"DryCycle RegionClimate.txt line {zeroBasedLine + 1}: {message}.");
    }
}
