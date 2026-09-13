using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Lossless world.txt editing document.
/// Unknown sections/directives stay as their original lines. Known room records and ordinary
/// CREATURES spawner lines are regenerated only after the editor changes them; unsupported or
/// mod-specific lines remain byte-for-byte text-equivalent apart from the file's existing newline.
/// </summary>
internal sealed class WorldDocument
{
    private sealed class LineRecord
    {
        internal string Raw = string.Empty;
        internal WorldRoomRecord Room;
        internal WorldCreatureSpawnLine CreatureLine;
        internal bool Removed;
    }

    private readonly List<LineRecord> lines = new();
    private readonly Dictionary<string, WorldRoomRecord> rooms =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorldCreatureSpawnRecord> creatureSpawns = new();
    private readonly Dictionary<int, LineRecord> creatureOwners = new();
    private int nextCreatureSpawnId = 1;

    internal string SourcePath { get; private set; } = string.Empty;
    internal string NewLine { get; private set; } = "\r\n";
    internal bool EndsWithNewLine { get; private set; }
    internal bool Dirty { get; private set; }
    internal IReadOnlyDictionary<string, WorldRoomRecord> Rooms => rooms;

    internal static WorldDocument Parse(string text, string sourcePath = null)
    {
        WorldDocument document = new();
        document.SourcePath = sourcePath ?? string.Empty;
        text ??= string.Empty;
        document.NewLine = DetectNewLine(text);
        document.EndsWithNewLine = EndsInLineBreak(text);

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] sourceLines = normalized.Split('\n');
        int count = sourceLines.Length;
        if (document.EndsWithNewLine && count > 0 && sourceLines[count - 1].Length == 0)
            count--;

        bool inRooms = false;
        bool inCreatures = false;
        for (int i = 0; i < count; i++)
        {
            string raw = sourceLines[i];
            LineRecord line = new() { Raw = raw };

            if (string.Equals(raw, "ROOMS", StringComparison.Ordinal))
            {
                inRooms = true;
            }
            else if (string.Equals(raw, "END ROOMS", StringComparison.Ordinal))
            {
                inRooms = false;
            }
            else if (string.Equals(raw, "CREATURES", StringComparison.Ordinal))
            {
                inCreatures = true;
            }
            else if (string.Equals(raw, "END CREATURES", StringComparison.Ordinal))
            {
                inCreatures = false;
            }
            else if (inRooms && TryParseRoomLine(raw, i, out WorldRoomRecord room))
            {
                line.Room = room;
                if (!document.rooms.ContainsKey(room.Name))
                    document.rooms.Add(room.Name, room);
            }
            else if (inCreatures && document.TryParseCreatureLine(raw, out WorldCreatureSpawnLine creatureLine))
            {
                line.CreatureLine = creatureLine;
            }

            document.lines.Add(line);
            if (line.CreatureLine != null)
                document.RegisterCreatureOwner(line);
        }

        document.Dirty = false;
        return document;
    }

    internal bool TryGetRoom(string roomName, out WorldRoomRecord room) =>
        rooms.TryGetValue(NormalizeRoom(roomName), out room);

    internal bool TryGetConnection(string roomName, int exitIndex, out string destination)
    {
        destination = string.Empty;
        if (exitIndex < 0 || !TryGetRoom(roomName, out WorldRoomRecord room)) return false;
        if (exitIndex >= room.Connections.Count)
        {
            destination = "DISCONNECTED";
            return true;
        }

        destination = room.Connections[exitIndex];
        return true;
    }

    internal bool TrySetConnection(string roomName, int exitIndex, string destinationRoom)
    {
        if (exitIndex < 0 || !TryGetRoom(roomName, out WorldRoomRecord room)) return false;

        string destination = NormalizeDestination(destinationRoom);
        while (room.Connections.Count <= exitIndex)
            room.Connections.Add("DISCONNECTED");

        if (string.Equals(room.Connections[exitIndex], destination, StringComparison.OrdinalIgnoreCase))
            return false;

        room.Connections[exitIndex] = destination;
        MarkRoomChanged(room);
        return true;
    }

    internal bool TryDisconnect(string roomName, int exitIndex) =>
        TrySetConnection(roomName, exitIndex, "DISCONNECTED");

    internal bool TrySetTags(string roomName, IReadOnlyList<string> tags)
    {
        if (!TryGetRoom(roomName, out WorldRoomRecord room)) return false;

        List<string> next = new();
        if (tags != null)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i]?.Trim();
                if (string.IsNullOrEmpty(tag)) continue;
                next.Add(tag);
            }
        }

        if (SequenceEqual(room.Tags, next)) return false;
        room.Tags.Clear();
        room.Tags.AddRange(next);
        MarkRoomChanged(room);
        return true;
    }

    internal WorldCreatureSpawnRecord[] GetCreatureSpawns(string roomName)
    {
        string normalized = NormalizeRoom(roomName);
        List<WorldCreatureSpawnRecord> result = new();
        for (int i = 0; i < creatureSpawns.Count; i++)
        {
            WorldCreatureSpawnRecord spawn = creatureSpawns[i];
            if (string.Equals(spawn.RoomName, normalized, StringComparison.OrdinalIgnoreCase))
                result.Add(spawn.Clone());
        }
        return result.ToArray();
    }

    internal bool TryAddCreatureSpawn(
        string roomName,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline,
        out int spawnId)
    {
        spawnId = -1;
        string normalizedRoom = NormalizeRoom(roomName);
        string normalizedCreature = NormalizeCreature(creature);
        if (normalizedRoom.Length == 0 || normalizedCreature.Length == 0 || denNode < 0)
            return false;
        if (!rooms.ContainsKey(normalizedRoom) && !string.Equals(normalizedRoom, "OFFSCREEN", StringComparison.OrdinalIgnoreCase))
            return false;

        WorldCreatureSpawnLine owner = FindOrCreateCreatureLine(
            normalizedRoom,
            NormalizeTimelineFilter(timelineFilter),
            excludeTimeline,
            out LineRecord ownerRecord);
        if (owner == null || ownerRecord == null) return false;

        WorldCreatureSpawnRecord record = new()
        {
            Id = nextCreatureSpawnId++,
            RoomName = normalizedRoom,
            DenNode = denNode,
            Creature = normalizedCreature,
            Amount = Math.Max(1, amount),
            SpawnData = NormalizeSpawnData(spawnData),
            TimelineFilter = owner.TimelineFilter,
            ExcludeTimeline = owner.ExcludeTimeline
        };
        owner.Spawns.Add(record);
        owner.Changed = true;
        ownerRecord.Removed = false;
        creatureSpawns.Add(record);
        creatureOwners[record.Id] = ownerRecord;
        Dirty = true;
        spawnId = record.Id;
        return true;
    }

    internal bool TryUpdateCreatureSpawn(
        int spawnId,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline)
    {
        if (!creatureOwners.TryGetValue(spawnId, out LineRecord oldOwner) ||
            oldOwner?.CreatureLine == null || oldOwner.Removed)
            return false;

        WorldCreatureSpawnRecord record = FindCreatureSpawn(spawnId);
        if (record == null) return false;

        string normalizedCreature = NormalizeCreature(creature);
        string normalizedTimeline = NormalizeTimelineFilter(timelineFilter);
        if (denNode < 0 || normalizedCreature.Length == 0) return false;

        bool move = !string.Equals(oldOwner.CreatureLine.TimelineFilter, normalizedTimeline, StringComparison.Ordinal) ||
                    oldOwner.CreatureLine.ExcludeTimeline != excludeTimeline;

        if (move)
        {
            oldOwner.CreatureLine.Spawns.Remove(record);
            oldOwner.CreatureLine.Changed = true;
            if (oldOwner.CreatureLine.Spawns.Count == 0) oldOwner.Removed = true;

            WorldCreatureSpawnLine newOwner = FindOrCreateCreatureLine(
                record.RoomName,
                normalizedTimeline,
                excludeTimeline,
                out LineRecord newOwnerRecord);
            if (newOwner == null || newOwnerRecord == null)
            {
                oldOwner.CreatureLine.Spawns.Add(record);
                oldOwner.Removed = false;
                return false;
            }
            newOwner.Spawns.Add(record);
            newOwner.Changed = true;
            newOwnerRecord.Removed = false;
            creatureOwners[spawnId] = newOwnerRecord;
        }

        record.DenNode = denNode;
        record.Creature = normalizedCreature;
        record.Amount = Math.Max(1, amount);
        record.SpawnData = NormalizeSpawnData(spawnData);
        record.TimelineFilter = normalizedTimeline;
        record.ExcludeTimeline = excludeTimeline;
        creatureOwners[spawnId].CreatureLine.Changed = true;
        Dirty = true;
        return true;
    }

    internal bool TryDeleteCreatureSpawn(int spawnId)
    {
        if (!creatureOwners.TryGetValue(spawnId, out LineRecord owner) ||
            owner?.CreatureLine == null || owner.Removed)
            return false;

        WorldCreatureSpawnRecord record = FindCreatureSpawn(spawnId);
        if (record == null) return false;

        owner.CreatureLine.Spawns.Remove(record);
        owner.CreatureLine.Changed = true;
        if (owner.CreatureLine.Spawns.Count == 0) owner.Removed = true;
        creatureOwners.Remove(spawnId);
        creatureSpawns.Remove(record);
        Dirty = true;
        return true;
    }

    internal string Serialize()
    {
        List<string> output = new(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            LineRecord line = lines[i];
            if (line.Removed) continue;
            if (line.Room?.Changed == true)
                output.Add(BuildRoomLine(line.Room));
            else if (line.CreatureLine?.Changed == true)
                output.Add(BuildCreatureLine(line.CreatureLine));
            else
                output.Add(line.Raw);
        }

        string serialized = string.Join(NewLine, output);
        if (EndsWithNewLine) serialized += NewLine;
        return serialized;
    }

    internal void MarkSaved(string sourcePath = null)
    {
        if (sourcePath != null) SourcePath = sourcePath;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            LineRecord line = lines[i];
            if (line.Removed)
            {
                lines.RemoveAt(i);
                continue;
            }
            if (line.Room?.Changed == true)
            {
                line.Raw = BuildRoomLine(line.Room);
                line.Room.Changed = false;
            }
            if (line.CreatureLine?.Changed == true)
            {
                line.Raw = BuildCreatureLine(line.CreatureLine);
                line.CreatureLine.Changed = false;
            }
        }
        Dirty = false;
    }

    private void MarkRoomChanged(WorldRoomRecord room)
    {
        room.Changed = true;
        Dirty = true;
    }

    private void RegisterCreatureOwner(LineRecord owner)
    {
        if (owner?.CreatureLine == null) return;
        for (int i = 0; i < owner.CreatureLine.Spawns.Count; i++)
        {
            WorldCreatureSpawnRecord spawn = owner.CreatureLine.Spawns[i];
            creatureSpawns.Add(spawn);
            creatureOwners[spawn.Id] = owner;
        }
    }

    private WorldCreatureSpawnRecord FindCreatureSpawn(int spawnId)
    {
        for (int i = 0; i < creatureSpawns.Count; i++)
            if (creatureSpawns[i].Id == spawnId) return creatureSpawns[i];
        return null;
    }

    private WorldCreatureSpawnLine FindOrCreateCreatureLine(
        string roomName,
        string timelineFilter,
        bool excludeTimeline,
        out LineRecord ownerRecord)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            LineRecord line = lines[i];
            WorldCreatureSpawnLine creatureLine = line.CreatureLine;
            if (line.Removed || creatureLine == null) continue;
            if (!string.Equals(creatureLine.RoomName, roomName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(creatureLine.TimelineFilter, timelineFilter, StringComparison.Ordinal) ||
                creatureLine.ExcludeTimeline != excludeTimeline)
                continue;
            ownerRecord = line;
            return creatureLine;
        }

        int endIndex = EnsureCreatureSection();
        if (endIndex < 0)
        {
            ownerRecord = null;
            return null;
        }

        WorldCreatureSpawnLine created = new()
        {
            RoomName = roomName,
            TimelineFilter = timelineFilter,
            ExcludeTimeline = excludeTimeline,
            Changed = true
        };
        ownerRecord = new LineRecord { CreatureLine = created };
        lines.Insert(endIndex, ownerRecord);
        Dirty = true;
        return created;
    }

    private int EnsureCreatureSection()
    {
        for (int i = 0; i < lines.Count; i++)
            if (!lines[i].Removed && string.Equals(lines[i].Raw, "END CREATURES", StringComparison.Ordinal))
                return i;

        int insert = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Raw, "END ROOMS", StringComparison.Ordinal))
            {
                insert = i + 1;
                break;
            }
        }

        lines.Insert(insert, new LineRecord { Raw = "CREATURES" });
        lines.Insert(insert + 1, new LineRecord { Raw = "END CREATURES" });
        Dirty = true;
        return insert + 1;
    }

    private bool TryParseCreatureLine(string raw, out WorldCreatureSpawnLine creatureLine)
    {
        creatureLine = null;
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

        if (text.StartsWith("LINEAGE", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = text.Split(new[] { " : " }, 2, StringSplitOptions.None);
        if (parts.Length != 2) return false;

        string roomName = NormalizeRoom(parts[0]);
        if (roomName.Length == 0) return false;
        List<string> entries = SplitSpawnerEntries(parts[1]);
        if (entries.Count == 0) return false;

        WorldCreatureSpawnLine parsed = new()
        {
            RoomName = roomName,
            TimelineFilter = NormalizeTimelineFilter(timeline),
            ExcludeTimeline = exclude
        };

        for (int i = 0; i < entries.Count; i++)
        {
            if (!TryParseSpawnerEntry(entries[i], roomName, parsed.TimelineFilter, exclude, out WorldCreatureSpawnRecord spawn))
                return false;
            parsed.Spawns.Add(spawn);
        }

        creatureLine = parsed;
        return true;
    }

    private bool TryParseSpawnerEntry(
        string raw,
        string roomName,
        string timeline,
        bool excludeTimeline,
        out WorldCreatureSpawnRecord spawn)
    {
        spawn = null;
        List<string> parts = SplitOutsideBraces(raw, '-');
        if (parts.Count < 2 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int denNode) ||
            denNode < 0)
            return false;

        string creature = NormalizeCreature(parts[1]);
        if (creature.Length == 0) return false;

        int amount = 1;
        string spawnData = string.Empty;
        for (int i = 2; i < parts.Count; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0) continue;
            if (token.StartsWith("{", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal))
            {
                spawnData = NormalizeSpawnData(token);
                continue;
            }
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedAmount))
            {
                amount = Math.Max(1, parsedAmount);
                continue;
            }
            return false;
        }

        spawn = new WorldCreatureSpawnRecord
        {
            Id = nextCreatureSpawnId++,
            RoomName = roomName,
            DenNode = denNode,
            Creature = creature,
            Amount = amount,
            SpawnData = spawnData,
            TimelineFilter = timeline,
            ExcludeTimeline = excludeTimeline
        };
        return true;
    }

    private static List<string> SplitSpawnerEntries(string value)
    {
        return SplitOutsideBraces(value ?? string.Empty, ',');
    }

    private static List<string> SplitOutsideBraces(string value, char separator)
    {
        List<string> result = new();
        int braceDepth = 0;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '{') braceDepth++;
            else if (c == '}' && braceDepth > 0) braceDepth--;
            else if (c == separator && braceDepth == 0)
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

    private static bool TryParseRoomLine(string raw, int lineIndex, out WorldRoomRecord room)
    {
        room = null;
        if (string.IsNullOrEmpty(raw)) return false;

        string[] parts = raw.Split(new[] { " : " }, StringSplitOptions.None);
        if (parts.Length < 2) return false;

        string name = NormalizeRoom(parts[0]);
        if (name.Length == 0) return false;

        WorldRoomRecord parsed = new()
        {
            Name = name,
            LineIndex = lineIndex
        };

        if (!string.IsNullOrWhiteSpace(parts[1]))
        {
            string[] destinations = parts[1].Split(',');
            for (int i = 0; i < destinations.Length; i++)
                parsed.Connections.Add(NormalizeDestination(destinations[i]));
        }

        for (int i = 2; i < parts.Length; i++)
        {
            string tag = parts[i]?.Trim();
            if (!string.IsNullOrEmpty(tag)) parsed.Tags.Add(tag);
        }

        room = parsed;
        return true;
    }

    private static string BuildRoomLine(WorldRoomRecord room)
    {
        string result = room.Name + " : " + string.Join(", ", room.Connections);
        for (int i = 0; i < room.Tags.Count; i++)
            result += " : " + room.Tags[i];
        return result;
    }

    private static string BuildCreatureLine(WorldCreatureSpawnLine line)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrEmpty(line.TimelineFilter))
        {
            builder.Append('(');
            if (line.ExcludeTimeline) builder.Append("X-");
            builder.Append(line.TimelineFilter);
            builder.Append(')');
        }
        builder.Append(line.RoomName);
        builder.Append(" : ");
        for (int i = 0; i < line.Spawns.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            WorldCreatureSpawnRecord spawn = line.Spawns[i];
            builder.Append(spawn.DenNode.ToString(CultureInfo.InvariantCulture));
            builder.Append('-');
            builder.Append(spawn.Creature);
            if (!string.IsNullOrEmpty(spawn.SpawnData))
            {
                builder.Append("-{");
                builder.Append(spawn.SpawnData);
                builder.Append('}');
            }
            if (spawn.Amount > 1)
            {
                builder.Append('-');
                builder.Append(spawn.Amount.ToString(CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    private static bool SequenceEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string NormalizeRoom(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeCreature(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeTimelineFilter(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string[] parts = value.Split(',');
        List<string> clean = new();
        for (int i = 0; i < parts.Length; i++)
        {
            string item = parts[i].Trim();
            if (item.Length > 0 && !ContainsIgnoreCase(clean, item)) clean.Add(item);
        }
        return string.Join(",", clean);
    }

    private static string NormalizeSpawnData(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string result = value.Trim();
        if (result.Length >= 2 && result[0] == '{' && result[result.Length - 1] == '}')
            result = result.Substring(1, result.Length - 2).Trim();
        return result;
    }

    private static bool ContainsIgnoreCase(List<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string NormalizeDestination(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "DISCONNECTED" : value.Trim();
        return result.Length == 0 ? "DISCONNECTED" : result;
    }

    private static string DetectNewLine(string text)
    {
        int crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (crlf >= 0) return "\r\n";
        if (text.IndexOf('\n') >= 0) return "\n";
        if (text.IndexOf('\r') >= 0) return "\r";
        return "\r\n";
    }

    private static bool EndsInLineBreak(string text) =>
        text.Length > 0 && (text[text.Length - 1] == '\n' || text[text.Length - 1] == '\r');
}

internal sealed class WorldRoomRecord
{
    internal string Name = string.Empty;
    internal int LineIndex = -1;
    internal readonly List<string> Connections = new();
    internal readonly List<string> Tags = new();
    internal bool Changed;
}

internal sealed class WorldCreatureSpawnRecord
{
    internal int Id;
    internal string RoomName = string.Empty;
    internal int DenNode = -1;
    internal string Creature = string.Empty;
    internal int Amount = 1;
    internal string SpawnData = string.Empty;
    internal string TimelineFilter = string.Empty;
    internal bool ExcludeTimeline;

    internal WorldCreatureSpawnRecord Clone() => new()
    {
        Id = Id,
        RoomName = RoomName,
        DenNode = DenNode,
        Creature = Creature,
        Amount = Amount,
        SpawnData = SpawnData,
        TimelineFilter = TimelineFilter,
        ExcludeTimeline = ExcludeTimeline
    };
}

internal sealed class WorldCreatureSpawnLine
{
    internal string RoomName = string.Empty;
    internal string TimelineFilter = string.Empty;
    internal bool ExcludeTimeline;
    internal readonly List<WorldCreatureSpawnRecord> Spawns = new();
    internal bool Changed;
}
