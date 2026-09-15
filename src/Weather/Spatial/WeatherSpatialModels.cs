using System;
using System.Collections.Generic;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather.Spatial;

internal enum WeatherSpatialRule
{
    Inherit = 0,
    Allow = 1,
    Deny = 2
}

internal readonly struct WeatherSpatialMember
{
    internal WeatherScheduleEventKind Kind { get; }
    internal string Id { get; }
    internal string Key => WeatherSpatialCatalog.WeatherKey(Kind, Id);

    internal WeatherSpatialMember(WeatherScheduleEventKind kind, string id)
    {
        Kind = kind;
        Id = id ?? string.Empty;
    }

    public override string ToString() => Key;
}

internal sealed class WeatherSpatialFamily
{
    internal string Id { get; }
    internal IReadOnlyList<WeatherSpatialMember> Members { get; }
    internal WeatherSpatialMember Preview { get; }

    internal WeatherSpatialFamily(
        string id,
        WeatherSpatialMember preview,
        params WeatherSpatialMember[] members)
    {
        Id = id ?? string.Empty;
        Preview = preview;
        Members = members ?? Array.Empty<WeatherSpatialMember>();
    }
}

internal readonly struct WeatherSpatialTarget
{
    internal bool IsFamily { get; }
    internal string FamilyId { get; }
    internal WeatherScheduleEventKind Kind { get; }
    internal string WeatherId { get; }
    internal string DisplayName { get; }
    internal string Key { get; }

    internal WeatherSpatialTarget(string familyId, string displayName)
    {
        IsFamily = true;
        FamilyId = familyId ?? string.Empty;
        Kind = WeatherScheduleEventKind.Weather;
        WeatherId = null;
        DisplayName = displayName ?? familyId ?? string.Empty;
        Key = "Family/" + FamilyId;
    }

    internal WeatherSpatialTarget(
        WeatherScheduleEventKind kind,
        string weatherId,
        string displayName)
    {
        IsFamily = false;
        FamilyId = null;
        Kind = kind;
        WeatherId = weatherId ?? string.Empty;
        DisplayName = displayName ?? weatherId ?? string.Empty;
        Key = WeatherSpatialCatalog.WeatherKey(kind, WeatherId);
    }
}

internal static class WeatherSpatialCatalog
{
    private static readonly WeatherSpatialFamily[] Families =
    {
        new(
            "Rain",
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "HeavyRain"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "LightRain"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "HeavyRain"),
            new WeatherSpatialMember(WeatherScheduleEventKind.DangerType, "DeathRain")),
        new(
            "Fog",
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "Fog"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "Fog"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "DenseFog")),
        new(
            "Heat",
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "HeatWave"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "HeatWave"),
            new WeatherSpatialMember(WeatherScheduleEventKind.DangerType, "IntenseHeat")),
        new(
            "Sand",
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "SandStorm"),
            new WeatherSpatialMember(WeatherScheduleEventKind.Weather, "SandStorm"),
            new WeatherSpatialMember(WeatherScheduleEventKind.DangerType, "DeathSandStorm"))
    };

    private static readonly WeatherSpatialTarget[] Targets = BuildTargets();

    internal static IReadOnlyList<WeatherSpatialFamily> AllFamilies => Families;
    internal static IReadOnlyList<WeatherSpatialTarget> AllTargets => Targets;

    internal static string NormalizeId(string id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;

        int start = 0;
        int end = id.Length;
        while (start < end && char.IsWhiteSpace(id[start])) start++;
        while (end > start && char.IsWhiteSpace(id[end - 1])) end--;

        bool needsRewrite = start != 0 || end != id.Length;
        for (int i = start; i < end && !needsRewrite; i++)
        {
            char c = id[i];
            if (c == '_' || c == '-' || char.ToUpperInvariant(c) != c)
                needsRewrite = true;
        }
        if (!needsRewrite) return id;

        char[] buffer = new char[end - start];
        int count = 0;
        for (int i = start; i < end; i++)
        {
            char c = id[i];
            if (c == '_' || c == '-') continue;
            buffer[count++] = char.ToUpperInvariant(c);
        }
        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }

    internal static string WeatherKey(WeatherScheduleEventKind kind, string id)
    {
        string canonical = CanonicalWeatherId(kind, id);
        if (kind == WeatherScheduleEventKind.DangerType)
        {
            return canonical switch
            {
                "DeathRain" => "DangerType/DeathRain",
                "IntenseHeat" => "DangerType/IntenseHeat",
                "DeathSandStorm" => "DangerType/DeathSandStorm",
                _ => "DangerType/" + canonical
            };
        }

        return canonical switch
        {
            "LightRain" => "Weather/LightRain",
            "HeavyRain" => "Weather/HeavyRain",
            "Fog" => "Weather/Fog",
            "DenseFog" => "Weather/DenseFog",
            "HeatWave" => "Weather/HeatWave",
            "SandStorm" => "Weather/SandStorm",
            _ => "Weather/" + canonical
        };
    }

    internal static string CanonicalWeatherId(WeatherScheduleEventKind kind, string id)
    {
        for (int i = 0; i < Families.Length; i++)
        {
            IReadOnlyList<WeatherSpatialMember> members = Families[i].Members;
            for (int j = 0; j < members.Count; j++)
            {
                WeatherSpatialMember member = members[j];
                if (member.Kind == kind && NormalizedEquals(member.Id, id))
                    return member.Id;
            }
        }
        return (id ?? string.Empty).Trim();
    }

    internal static bool TryParseWeatherKey(
        string key,
        out WeatherScheduleEventKind kind,
        out string id)
    {
        kind = WeatherScheduleEventKind.Weather;
        id = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        int slash = key.IndexOf('/');
        if (slash <= 0 || slash >= key.Length - 1)
        {
            return false;
        }

        string prefix = key.Substring(0, slash).Trim();
        string rawId = key.Substring(slash + 1).Trim();
        if (prefix.Equals("Weather", StringComparison.OrdinalIgnoreCase))
        {
            kind = WeatherScheduleEventKind.Weather;
        }
        else if (prefix.Equals("DangerType", StringComparison.OrdinalIgnoreCase))
        {
            kind = WeatherScheduleEventKind.DangerType;
        }
        else
        {
            return false;
        }

        if (!IsKnownWeather(kind, rawId))
        {
            return false;
        }

        id = CanonicalWeatherId(kind, rawId);
        return true;
    }

    internal static bool IsKnownFamily(string familyId) => TryGetFamily(familyId, out _);

    internal static bool IsKnownWeather(WeatherScheduleEventKind kind, string id) =>
        TryGetFamily(kind, id, out _);

    internal static bool TryGetFamily(string familyId, out WeatherSpatialFamily family)
    {
        family = null;
        for (int i = 0; i < Families.Length; i++)
        {
            if (NormalizedEquals(Families[i].Id, familyId))
            {
                family = Families[i];
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetFamily(
        WeatherScheduleEventKind kind,
        string weatherId,
        out WeatherSpatialFamily family)
    {
        family = null;
        for (int i = 0; i < Families.Length; i++)
        {
            IReadOnlyList<WeatherSpatialMember> members = Families[i].Members;
            for (int j = 0; j < members.Count; j++)
            {
                if (members[j].Kind == kind && NormalizedEquals(members[j].Id, weatherId))
                {
                    family = Families[i];
                    return true;
                }
            }
        }
        return false;
    }

    internal static WeatherSpatialMember PreviewFor(in WeatherSpatialTarget target)
    {
        if (!target.IsFamily)
        {
            return new WeatherSpatialMember(target.Kind, target.WeatherId);
        }

        return TryGetFamily(target.FamilyId, out WeatherSpatialFamily family)
            ? family.Preview
            : new WeatherSpatialMember(WeatherScheduleEventKind.Weather, string.Empty);
    }

    private static bool NormalizedEquals(string canonical, string candidate)
    {
        canonical ??= string.Empty;
        candidate ??= string.Empty;

        int start = 0;
        int end = candidate.Length;
        while (start < end && char.IsWhiteSpace(candidate[start])) start++;
        while (end > start && char.IsWhiteSpace(candidate[end - 1])) end--;

        int left = 0;
        int right = start;
        while (true)
        {
            while (left < canonical.Length && (canonical[left] == '_' || canonical[left] == '-')) left++;
            while (right < end && (candidate[right] == '_' || candidate[right] == '-')) right++;

            bool leftDone = left >= canonical.Length;
            bool rightDone = right >= end;
            if (leftDone || rightDone) return leftDone && rightDone;

            if (char.ToUpperInvariant(canonical[left]) != char.ToUpperInvariant(candidate[right]))
                return false;
            left++;
            right++;
        }
    }

    private static WeatherSpatialTarget[] BuildTargets()
    {
        List<WeatherSpatialTarget> result = new();
        for (int i = 0; i < Families.Length; i++)
        {
            WeatherSpatialFamily family = Families[i];
            result.Add(new WeatherSpatialTarget(family.Id, "[Family] " + family.Id));
            for (int j = 0; j < family.Members.Count; j++)
            {
                WeatherSpatialMember member = family.Members[j];
                result.Add(new WeatherSpatialTarget(
                    member.Kind,
                    member.Id,
                    "  " + member.Id +
                    (member.Kind == WeatherScheduleEventKind.DangerType ? " [Danger]" : string.Empty)));
            }
        }
        return result.ToArray();
    }
}

internal sealed class WeatherSpatialRoomRules
{
    internal readonly Dictionary<string, WeatherSpatialRule> Families =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, WeatherSpatialRule> Weather =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool IsEmpty => Families.Count == 0 && Weather.Count == 0;

    internal WeatherSpatialRoomRules Clone()
    {
        WeatherSpatialRoomRules copy = new();
        foreach (KeyValuePair<string, WeatherSpatialRule> pair in Families)
        {
            copy.Families[pair.Key] = pair.Value;
        }
        foreach (KeyValuePair<string, WeatherSpatialRule> pair in Weather)
        {
            copy.Weather[pair.Key] = pair.Value;
        }
        return copy;
    }
}

internal sealed class WeatherSpatialRegionRules
{
    internal readonly Dictionary<string, WeatherSpatialRule> FamilyDefaults =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, WeatherSpatialRule> WeatherDefaults =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, WeatherSpatialRoomRules> Rooms =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool IsEmpty =>
        FamilyDefaults.Count == 0 &&
        WeatherDefaults.Count == 0 &&
        Rooms.Count == 0;
}

internal sealed class WeatherSpatialValidationIssue
{
    internal bool IsError { get; }
    internal string Message { get; }

    internal WeatherSpatialValidationIssue(bool isError, string message)
    {
        IsError = isError;
        Message = message ?? string.Empty;
    }

    public override string ToString() => (IsError ? "ERROR: " : "WARN: ") + Message;
}

internal sealed class WeatherSpatialValidationResult
{
    internal readonly List<WeatherSpatialValidationIssue> Issues = new();
    internal int ErrorCount;
    internal int WarningCount;

    internal void Error(string message)
    {
        ErrorCount++;
        Issues.Add(new WeatherSpatialValidationIssue(true, message));
    }

    internal void Warn(string message)
    {
        WarningCount++;
        Issues.Add(new WeatherSpatialValidationIssue(false, message));
    }
}
