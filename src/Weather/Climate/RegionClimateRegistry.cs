using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.Weather.Climate;

internal class ClimateChanceEntry
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
/// Loader for Ancient Site/world/RegionClimate.txt.
/// Hierarchy is defined only by { } blocks; tabs and spaces are presentation-only.
/// [Weather] supports Region -> Family -> Variant, while [DangerType] supports
/// Region -> DangerType. Probabilities are independent 0..100 rolls and // starts a
/// comment. [defaultWeather]/[defaultDangerType] are documentation-only sections.
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

    private enum BlockKind
    {
        Region,
        WeatherFamily
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
                "DryCycle weather climate file was not found. Expected Ancient Site/world/RegionClimate.txt.");
            return;
        }

        try
        {
            LoadedPath = Path.GetFullPath(path);
            Parse(File.ReadAllLines(path));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError(
                $"DryCycle failed reading RegionClimate.txt from '{path}': {ex}");
            Profiles.Clear();
            LoadedPath = null;
            return;
        }

        Plugin.Logger?.LogInfo(
            $"DryCycle loaded {Profiles.Count} region climate profile(s) from '{LoadedPath}'.");

        if (Profiles.TryGetValue("SU", out RegionClimateProfile su))
        {
            LogProfile(su);
        }
        else
        {
            Plugin.Logger?.LogWarning(
                "DryCycle RegionClimate.txt loaded, but no [Weather]/[DangerType] SU profile was parsed.");
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
        try
        {
            if (ModManager.ActiveMods != null)
            {
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    ModManager.Mod mod = ModManager.ActiveMods[i];
                    if (mod == null ||
                        !string.Equals(mod.id, Plugin.RainWorldModId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] directCandidates =
                    {
                        Path.Combine(mod.path, "world", "RegionClimate.txt"),
                        Path.Combine(mod.NewestPath, "world", "RegionClimate.txt"),
                        Path.Combine(mod.TargetedPath, "world", "RegionClimate.txt"),
                        Path.Combine(mod.basePath ?? string.Empty, "world", "RegionClimate.txt")
                    };

                    for (int candidateIndex = 0; candidateIndex < directCandidates.Length; candidateIndex++)
                    {
                        string candidate = directCandidates[candidateIndex];
                        if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle failed direct Ancient Site RegionClimate lookup: {ex.Message}");
        }

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
        Stack<BlockKind> blocks = new();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string text = StripComment(lines[lineIndex] ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (TryParseSectionHeader(text, out Section parsedSection))
            {
                if (blocks.Count > 0 && section != Section.ReservedDefault)
                {
                    Warn(lineIndex,
                        $"section started before {blocks.Count} open block(s) were closed; parser state was reset");
                }

                blocks.Clear();
                currentRegion = null;
                currentFamily = null;
                section = parsedSection;
                continue;
            }

            // Documentation/example sections are intentionally ignored wholesale.
            // Their brace layout can evolve without affecting runtime parsing.
            if (section == Section.ReservedDefault)
            {
                continue;
            }

            if (section != Section.Weather && section != Section.DangerType)
            {
                Warn(lineIndex, "entry appears outside [Weather] or [DangerType]");
                continue;
            }

            // Allow one or more closing braces on the same line ("}" or "}}") so
            // formatting remains flexible. Any content after them is parsed normally.
            while (text.StartsWith("}", StringComparison.Ordinal))
            {
                CloseBlock(
                    lineIndex,
                    blocks,
                    ref currentRegion,
                    ref currentFamily);
                text = text.Substring(1).TrimStart();
            }

            if (text.Length == 0)
            {
                continue;
            }

            if (text.IndexOf('}') >= 0)
            {
                Warn(lineIndex, "closing '}' must appear before any other content on its line");
                continue;
            }

            bool opensBlock = text.EndsWith("{", StringComparison.Ordinal);
            if (opensBlock)
            {
                text = text.Substring(0, text.Length - 1).TrimEnd();
            }
            else if (text.IndexOf('{') >= 0)
            {
                Warn(lineIndex, "opening '{' must appear at the end of an entry");
                continue;
            }

            if (text.Length == 0)
            {
                Warn(lineIndex, "standalone '{' has no parent entry");
                continue;
            }

            if (opensBlock)
            {
                ParseBlockStart(
                    lineIndex,
                    section,
                    text,
                    blocks,
                    ref currentRegion,
                    ref currentFamily);
                continue;
            }

            ParseLeafEntry(
                lineIndex,
                section,
                text,
                currentRegion,
                currentFamily);
        }

        if (blocks.Count > 0)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle RegionClimate.txt ended with {blocks.Count} unclosed '{{' block(s).");
        }
    }

    private static void ParseBlockStart(
        int lineIndex,
        Section section,
        string text,
        Stack<BlockKind> blocks,
        ref RegionClimateProfile currentRegion,
        ref WeatherFamilyClimateEntry currentFamily)
    {
        bool hasChance = text.IndexOf(':') >= 0;

        if (!hasChance)
        {
            if (blocks.Count != 0 || currentRegion != null)
            {
                Warn(lineIndex, "region blocks cannot be nested inside another block");
                return;
            }

            string regionId = NormalizeId(text);
            if (regionId.Length == 0)
            {
                Warn(lineIndex, "region ID is empty");
                return;
            }

            currentRegion = GetOrCreateProfile(regionId);
            currentFamily = null;
            blocks.Push(BlockKind.Region);
            return;
        }

        if (section != Section.Weather)
        {
            Warn(lineIndex, "[DangerType] entries cannot open child blocks");
            return;
        }

        if (currentRegion == null || blocks.Count == 0)
        {
            Warn(lineIndex, "weather family block has no active region");
            return;
        }

        if (currentFamily != null || blocks.Peek() != BlockKind.Region)
        {
            Warn(lineIndex, "weather variant blocks are not supported");
            return;
        }

        if (!TryParseChanceEntry(text, out ClimateChanceEntry parsed))
        {
            Warn(lineIndex, "expected 'WeatherFamily : probability {'");
            return;
        }

        currentFamily = new WeatherFamilyClimateEntry(parsed.Id, parsed.ChancePercent);
        currentRegion.AddWeather(currentFamily);
        blocks.Push(BlockKind.WeatherFamily);
    }

    private static void ParseLeafEntry(
        int lineIndex,
        Section section,
        string text,
        RegionClimateProfile currentRegion,
        WeatherFamilyClimateEntry currentFamily)
    {
        if (currentRegion == null)
        {
            Warn(lineIndex, "entry has no active region block");
            return;
        }

        if (!TryParseChanceEntry(text, out ClimateChanceEntry parsed))
        {
            Warn(lineIndex, "expected 'Id : probability'");
            return;
        }

        if (section == Section.DangerType)
        {
            currentRegion.AddDanger(parsed);
            return;
        }

        if (currentFamily != null)
        {
            currentFamily.AddVariant(parsed);
            return;
        }

        // A Weather entry without its own child block is a complete family with no
        // variants, e.g. B5 { SandStorm : 100 } written over multiple lines.
        currentRegion.AddWeather(
            new WeatherFamilyClimateEntry(parsed.Id, parsed.ChancePercent));
    }

    private static void CloseBlock(
        int lineIndex,
        Stack<BlockKind> blocks,
        ref RegionClimateProfile currentRegion,
        ref WeatherFamilyClimateEntry currentFamily)
    {
        if (blocks.Count == 0)
        {
            Warn(lineIndex, "unexpected closing '}'");
            return;
        }

        BlockKind closed = blocks.Pop();
        if (closed == BlockKind.WeatherFamily)
        {
            currentFamily = null;
            return;
        }

        currentFamily = null;
        currentRegion = null;
    }

    private static bool TryParseSectionHeader(string text, out Section section)
    {
        section = Section.None;
        if (text.Length < 3 || text[0] != '[' || text[text.Length - 1] != ']')
        {
            return false;
        }

        string name = text.Substring(1, text.Length - 2).Trim();
        if (string.Equals(name, "Weather", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Weather;
        }
        else if (string.Equals(name, "DangerType", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.DangerType;
        }
        else if (string.Equals(name, "defaultWeather", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "defaultDangerType", StringComparison.OrdinalIgnoreCase))
        {
            section = Section.ReservedDefault;
        }
        else
        {
            section = Section.None;
        }

        return true;
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

        // This grammar intentionally has one ':' per chance entry. Reject additional
        // separators now rather than silently accepting a malformed future syntax.
        if (text.IndexOf(':', colon + 1) >= 0)
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

    private static string NormalizeId(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static void Warn(int zeroBasedLine, string message)
    {
        Plugin.Logger?.LogWarning(
            $"DryCycle RegionClimate.txt line {zeroBasedLine + 1}: {message}.");
    }

    private static void LogProfile(RegionClimateProfile profile)
    {
        Plugin.Logger?.LogInfo(
            $"DryCycle climate {profile.RegionId}: " +
            $"weatherFamilies={profile.Weather.Count}, dangerTypes={profile.DangerTypes.Count}.");

        for (int i = 0; i < profile.Weather.Count; i++)
        {
            WeatherFamilyClimateEntry family = profile.Weather[i];
            Plugin.Logger?.LogInfo(
                $"  Weather {family.Id}: {family.ChancePercent:0.##}% " +
                $"({family.Variants.Count} variant(s))");

            for (int j = 0; j < family.Variants.Count; j++)
            {
                ClimateChanceEntry variant = family.Variants[j];
                Plugin.Logger?.LogInfo(
                    $"    Variant {variant.Id}: {variant.ChancePercent:0.##}%");
            }
        }

        for (int i = 0; i < profile.DangerTypes.Count; i++)
        {
            ClimateChanceEntry danger = profile.DangerTypes[i];
            Plugin.Logger?.LogInfo(
                $"  Danger {danger.Id}: {danger.ChancePercent:0.##}%");
        }
    }
}
