using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.DevUI.DevTool.World;

internal sealed class WorldLineageStageRecord
{
    internal string Creature = "NONE";
    internal float Chance;
    internal string SpawnData = string.Empty;

    internal WorldLineageStageRecord Clone() => new()
    {
        Creature = Creature,
        Chance = Chance,
        SpawnData = SpawnData
    };
}

internal sealed class WorldLineageRecord
{
    internal int Id;
    internal string RoomName = string.Empty;
    internal int DenNode = -1;
    internal readonly List<WorldLineageStageRecord> Stages = new();
    internal string TimelineFilter = string.Empty;
    internal bool ExcludeTimeline;
    internal bool NightCreature;
    internal string NightToken = "NIGHT";

    internal string SourceRaw = string.Empty;
    internal int SourceOccurrence;
    internal bool IsNew;
    internal bool Changed;
    internal bool Removed;

    internal WorldLineageRecord Clone()
    {
        WorldLineageRecord clone = new()
        {
            Id = Id,
            RoomName = RoomName,
            DenNode = DenNode,
            TimelineFilter = TimelineFilter,
            ExcludeTimeline = ExcludeTimeline,
            NightCreature = NightCreature,
            NightToken = NightToken,
            SourceRaw = SourceRaw,
            SourceOccurrence = SourceOccurrence,
            IsNew = IsNew,
            Changed = Changed,
            Removed = Removed
        };
        for (int i = 0; i < Stages.Count; i++) clone.Stages.Add(Stages[i].Clone());
        return clone;
    }
}

/// <summary>
/// Lossless editor for LINEAGE rows inside world_XX.txt / CREATURES.
/// Existing lines are anchored by raw text + occurrence so comments, ordering and unknown
/// non-lineage mod directives stay untouched.
/// </summary>
internal static class WorldLineageRegistry
{
    private static readonly List<WorldLineageRecord> records = new();
    private static readonly Dictionary<string, WorldLineageRecord[]> roomSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private static string loadedRegion = string.Empty;
    private static string loadedPath = string.Empty;
    private static int nextId = 1;

    internal static bool Dirty { get; private set; }
    internal static string LoadError { get; private set; }

    internal static bool EnsureLoaded(string region)
    {
        if (string.IsNullOrWhiteSpace(region)) return false;
        if (!WorldTextRegistry.EnsureLoaded(region))
        {
            LoadError = WorldTextRegistry.LoadError;
            return false;
        }

        string path = WorldTextRegistry.LoadedPath ?? string.Empty;
        if (string.Equals(loadedRegion, region, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalized = NormalizeRegion(region);
        if (normalized.Length == 0) return false;
        if (string.Equals(loadedRegion, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Dirty)
        {
            LoadError = "Cannot switch lineage documents while unsaved lineage edits exist.";
            return false;
        }
        return LoadFromPath(normalized, path);
    }

    /// <summary>
    /// Returns a cached defensive snapshot for the room. Lineage records and their stage lists are
    /// deep-cloned only when that room changes, not once per immediate-mode inspector frame.
    /// </summary>
    internal static WorldLineageRecord[] GetLineages(string region, string roomName)
    {
        if (!EnsureLoaded(region)) return Array.Empty<WorldLineageRecord>();
        string normalizedRoom = NormalizeRoom(roomName);
        if (roomSnapshots.TryGetValue(normalizedRoom, out WorldLineageRecord[] cached))
            return cached;

        List<WorldLineageRecord> result = new();
        for (int i = 0; i < records.Count; i++)
        {
            WorldLineageRecord record = records[i];
            if (!record.Removed && string.Equals(record.RoomName, normalizedRoom, StringComparison.OrdinalIgnoreCase))
                result.Add(record.Clone());
        }

        WorldLineageRecord[] snapshot = result.Count == 0
            ? Array.Empty<WorldLineageRecord>()
            : result.ToArray();
        roomSnapshots[normalizedRoom] = snapshot;
        return snapshot;
    }

    internal static bool TryAdd(
        string region,
        string roomName,
        int denNode,
        IReadOnlyList<WorldLineageStageRecord> stages,
        string timelineFilter,
        bool excludeTimeline,
        bool nightCreature,
        out int id,
        out string error)
    {
        id = -1;
        error = null;
        if (!EnsureLoaded(region))
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(roomName) || denNode < 0 || stages == null || stages.Count == 0)
        {
            error = "Lineage needs a room, a valid Den node and at least one stage.";
            return false;
        }

        WorldLineageRecord record = new()
        {
            Id = nextId++,
            RoomName = roomName.Trim(),
            DenNode = denNode,
            TimelineFilter = NormalizeCsv(timelineFilter),
            ExcludeTimeline = excludeTimeline,
            NightCreature = nightCreature,
            IsNew = true,
            Changed = true
        };
        if (!CopyStages(stages, record.Stages, out error)) return false;
        records.Add(record);
        InvalidateRoomSnapshot(record.RoomName);
        Dirty = true;
        id = record.Id;
        WorldCreatureLiveReload.ReloadRoom(region, record.RoomName);
        return true;
    }

    internal static bool TryUpdate(
        string region,
        int id,
        int denNode,
        IReadOnlyList<WorldLineageStageRecord> stages,
        string timelineFilter,
        bool excludeTimeline,
        bool nightCreature,
        out string error)
    {
        error = null;
        if (!EnsureLoaded(region))
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        WorldLineageRecord record = Find(id);
        if (record == null || record.Removed)
        {
            error = "Lineage entry was not found.";
            return false;
        }
        List<WorldLineageStageRecord> next = new();
        if (denNode < 0 || !CopyStages(stages, next, out error)) return false;

        record.DenNode = denNode;
        record.Stages.Clear();
        record.Stages.AddRange(next);
        record.TimelineFilter = NormalizeCsv(timelineFilter);
        record.ExcludeTimeline = excludeTimeline;
        record.NightCreature = nightCreature;
        record.Changed = true;
        InvalidateRoomSnapshot(record.RoomName);
        Dirty = true;
        WorldCreatureLiveReload.ReloadRoom(region, record.RoomName);
        return true;
    }

    internal static bool TryDelete(string region, int id, out string error)
    {
        error = null;
        if (!EnsureLoaded(region))
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        WorldLineageRecord record = Find(id);
        if (record == null || record.Removed)
        {
            error = "Lineage entry was not found.";
            return false;
        }
        record.Removed = true;
        record.Changed = true;
        InvalidateRoomSnapshot(record.RoomName);
        Dirty = true;
        WorldCreatureLiveReload.ReloadRoom(region, record.RoomName);
        return true;
    }

    internal static bool Save(out string error)
    {
        error = null;
        if (!Dirty) return true;
        if (string.IsNullOrWhiteSpace(loadedPath) || !File.Exists(loadedPath))
        {
            error = "world.txt is unavailable for lineage save.";
            return false;
        }

        try
        {
            string text = File.ReadAllText(loadedPath);
            string newline = DetectNewLine(text);
            bool terminalNewline = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
            string[] source = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = source.Length;
            if (terminalNewline && count > 0 && source[count - 1].Length == 0) count--;

            Dictionary<string, WorldLineageRecord> anchors = new(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                WorldLineageRecord record = records[i];
                if (!record.IsNew && !string.IsNullOrEmpty(record.SourceRaw))
                    anchors[AnchorKey(record.SourceRaw, record.SourceOccurrence)] = record;
            }

            Dictionary<string, int> occurrence = new(StringComparer.Ordinal);
            List<string> output = new(count + 8);
            bool inCreatures = false;
            bool foundEnd = false;
            for (int i = 0; i < count; i++)
            {
                string raw = source[i];
                if (string.Equals(raw, "CREATURES", StringComparison.Ordinal)) inCreatures = true;
                if (inCreatures && string.Equals(raw, "END CREATURES", StringComparison.Ordinal))
                {
                    AppendNew(output);
                    output.Add(raw);
                    foundEnd = true;
                    inCreatures = false;
                    continue;
                }

                if (inCreatures && TryParseLineage(raw, out _))
                {
                    occurrence.TryGetValue(raw, out int nth);
                    occurrence[raw] = nth + 1;
                    if (anchors.TryGetValue(AnchorKey(raw, nth), out WorldLineageRecord record))
                    {
                        if (record.Removed) continue;
                        output.Add(record.Changed ? BuildLine(record) : raw);
                        continue;
                    }
                }
                output.Add(raw);
            }

            if (!foundEnd)
            {
                output.Add("CREATURES");
                AppendNew(output);
                output.Add("END CREATURES");
            }

            string result = string.Join(newline, output);
            if (terminalNewline) result += newline;
            WorldAuthoringPathResolver.AtomicWriteAllText(loadedPath, result);
            Dirty = false;
            return LoadFromPath(loadedRegion, loadedPath);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            error = ex.Message;
            return false;
        }
    }

    internal static void Clear()
    {
        if (Dirty) return;
        records.Clear();
        roomSnapshots.Clear();
        loadedRegion = string.Empty;
        loadedPath = string.Empty;
        nextId = 1;
        LoadError = null;
    }

    private static bool LoadFromPath(string region, string path)
    {
        records.Clear();
        roomSnapshots.Clear();
        nextId = 1;
        loadedRegion = region;
        loadedPath = path ?? string.Empty;
        LoadError = null;
        Dirty = false;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            string[] source = File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Dictionary<string, int> occurrence = new(StringComparer.Ordinal);
            bool inCreatures = false;
            for (int i = 0; i < source.Length; i++)
            {
                string raw = source[i];
                if (string.Equals(raw, "CREATURES", StringComparison.Ordinal)) { inCreatures = true; continue; }
                if (string.Equals(raw, "END CREATURES", StringComparison.Ordinal)) { inCreatures = false; continue; }
                if (!inCreatures || !TryParseLineage(raw, out WorldLineageRecord record)) continue;
                occurrence.TryGetValue(raw, out int nth);
                occurrence[raw] = nth + 1;
                record.Id = nextId++;
                record.SourceRaw = raw;
                record.SourceOccurrence = nth;
                records.Add(record);
            }
            return true;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            records.Clear();
            roomSnapshots.Clear();
            return false;
        }
    }

    private static void AppendNew(List<string> output)
    {
        for (int i = 0; i < records.Count; i++)
            if (records[i].IsNew && !records[i].Removed) output.Add(BuildLine(records[i]));
    }

    private static void InvalidateRoomSnapshot(string roomName)
    {
        string normalized = NormalizeRoom(roomName);
        if (normalized.Length > 0) roomSnapshots.Remove(normalized);
    }

    private static WorldLineageRecord Find(int id)
    {
        for (int i = 0; i < records.Count; i++) if (records[i].Id == id) return records[i];
        return null;
    }

    private static bool TryParseLineage(string raw, out WorldLineageRecord record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string text = raw.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal)) return false;

        string timeline = string.Empty;
        bool exclude = false;
        if (text.StartsWith("(", StringComparison.Ordinal))
        {
            int close = text.IndexOf(')');
            if (close <= 1) return false;
            timeline = text.Substring(1, close - 1).Trim();
            if (timeline.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                exclude = true;
                timeline = timeline.Substring(2).Trim();
            }
            text = text.Substring(close + 1).TrimStart();
        }

        string[] parts = text.Split(new[] { " : " }, StringSplitOptions.None);
        if (parts.Length < 4 || !string.Equals(parts[0].Trim(), "LINEAGE", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int den) || den < 0) return false;
        List<string> entries = SplitOutsideBraces(parts[3], ',');
        if (entries.Count == 0) return false;

        WorldLineageRecord parsed = new()
        {
            RoomName = parts[1].Trim(),
            DenNode = den,
            TimelineFilter = NormalizeCsv(timeline),
            ExcludeTimeline = exclude,
            NightCreature = parts.Length > 4,
            NightToken = parts.Length > 4 ? string.Join(" : ", parts, 4, parts.Length - 4).Trim() : "NIGHT"
        };
        for (int i = 0; i < entries.Count; i++)
        {
            if (!TryParseStage(entries[i], out WorldLineageStageRecord stage)) return false;
            parsed.Stages.Add(stage);
        }
        record = parsed;
        return true;
    }

    private static bool TryParseStage(string raw, out WorldLineageStageRecord stage)
    {
        stage = null;
        List<string> parts = SplitOutsideBraces(raw, '-');
        if (parts.Count == 0 || string.IsNullOrWhiteSpace(parts[0])) return false;
        float chance = 0f;
        string spawnData = string.Empty;
        for (int i = 1; i < parts.Count; i++)
        {
            string token = parts[i].Trim();
            if (token.StartsWith("{", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal))
                spawnData = TrimBraces(token);
            else if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out chance))
                return false;
        }
        stage = new WorldLineageStageRecord
        {
            Creature = parts[0].Trim(),
            Chance = Math.Max(0f, Math.Min(1f, chance)),
            SpawnData = spawnData
        };
        return true;
    }

    private static string BuildLine(WorldLineageRecord record)
    {
        System.Text.StringBuilder builder = new();
        if (!string.IsNullOrEmpty(record.TimelineFilter))
        {
            builder.Append('(');
            if (record.ExcludeTimeline) builder.Append("X-");
            builder.Append(record.TimelineFilter);
            builder.Append(')');
        }
        builder.Append("LINEAGE : ").Append(record.RoomName).Append(" : ")
            .Append(record.DenNode.ToString(CultureInfo.InvariantCulture)).Append(" : ");
        for (int i = 0; i < record.Stages.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            WorldLineageStageRecord stage = record.Stages[i];
            builder.Append(string.IsNullOrWhiteSpace(stage.Creature) ? "NONE" : stage.Creature.Trim());
            if (!string.IsNullOrWhiteSpace(stage.SpawnData)) builder.Append("-{").Append(TrimBraces(stage.SpawnData)).Append('}');
            builder.Append('-').Append(stage.Chance.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (record.NightCreature)
            builder.Append(" : ").Append(string.IsNullOrWhiteSpace(record.NightToken) ? "NIGHT" : record.NightToken.Trim());
        return builder.ToString();
    }

    private static bool CopyStages(IReadOnlyList<WorldLineageStageRecord> source, List<WorldLineageStageRecord> target, out string error)
    {
        error = null;
        if (source == null || source.Count == 0)
        {
            error = "Lineage needs at least one stage.";
            return false;
        }
        for (int i = 0; i < source.Count; i++)
        {
            WorldLineageStageRecord stage = source[i];
            if (stage == null || string.IsNullOrWhiteSpace(stage.Creature))
            {
                error = "Every lineage stage needs a creature id or NONE.";
                return false;
            }
            target.Add(new WorldLineageStageRecord
            {
                Creature = stage.Creature.Trim(),
                Chance = Math.Max(0f, Math.Min(1f, stage.Chance)),
                SpawnData = TrimBraces(stage.SpawnData)
            });
        }
        return true;
    }

    private static List<string> SplitOutsideBraces(string value, char separator)
    {
        List<string> result = new();
        value ??= string.Empty;
        int depth = 0;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '{') depth++;
            else if (c == '}' && depth > 0) depth--;
            else if (c == separator && depth == 0)
            {
                string token = value.Substring(start, i - start).Trim();
                if (token.Length > 0) result.Add(token);
                start = i + 1;
            }
        }
        string tail = value.Substring(start).Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    private static string TrimBraces(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string text = value.Trim();
        if (text.Length >= 2 && text[0] == '{' && text[text.Length - 1] == '}')
            text = text.Substring(1, text.Length - 2).Trim();
        return text;
    }

    private static string NormalizeCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string[] parts = value.Split(',');
        List<string> result = new();
        for (int i = 0; i < parts.Length; i++)
        {
            string item = parts[i].Trim();
            if (item.Length == 0) continue;
            bool exists = false;
            for (int j = 0; j < result.Count; j++)
                if (string.Equals(result[j], item, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
            if (!exists) result.Add(item);
        }
        return string.Join(",", result);
    }

    private static string NormalizeRoom(string roomName) =>
        string.IsNullOrWhiteSpace(roomName) ? string.Empty : roomName.Trim();

    private static string NormalizeRegion(string region) => string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
    private static string AnchorKey(string raw, int occurrence) => raw + "\u001F" + occurrence.ToString(CultureInfo.InvariantCulture);
    private static string DetectNewLine(string text) => text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : text.Contains("\r") ? "\r" : "\r\n";
}
