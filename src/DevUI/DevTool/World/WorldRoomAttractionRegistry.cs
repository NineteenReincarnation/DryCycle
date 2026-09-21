using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.World;

internal sealed class WorldRoomAttractionEntry
{
    internal string CreatureId = string.Empty;
    internal string Attraction = string.Empty;
}

/// <summary>
/// Lossless authoring surface for Rain World's Properties(.timeline).txt Room_Attr records.
///
/// Unknown Properties directives are never regenerated. Only Room_Attr lines for the edited room and
/// creature are rewritten; all other lines remain byte-for-text equivalent in the in-memory document.
/// </summary>
internal static class WorldRoomAttractionRegistry
{
    private static global::World loadedWorld;
    private static string loadedPath = string.Empty;
    private static List<string> lines;
    private static bool dirty;
    private static int revision;
    private static string loadError;

    internal static bool Dirty => dirty;
    internal static int Revision => revision;
    internal static string LoadedPath => loadedPath;
    internal static string LoadError => loadError;

    internal static bool EnsureLoaded(EditorSession session)
    {
        global::World world = session?.World;
        if (world == null)
        {
            loadError = "World is unavailable.";
            return false;
        }

        string path = ResolvePath(session);
        if (ReferenceEquals(loadedWorld, world) &&
            lines != null &&
            string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return true;

        if (dirty)
        {
            loadError = "Cannot switch Properties.txt documents while Room_Attr changes are unsaved.";
            return false;
        }

        loadedWorld = world;
        loadedPath = path ?? string.Empty;
        loadError = null;

        if (string.IsNullOrWhiteSpace(loadedPath) || !File.Exists(loadedPath))
        {
            lines = null;
            loadError =
                "The active Properties file has no lossless writable source. " +
                "DryCycle will not author generated mergedmods data.";
            BumpRevision();
            return false;
        }

        try
        {
            lines = new List<string>(File.ReadAllLines(loadedPath));
            BumpRevision();
            return true;
        }
        catch (Exception error)
        {
            lines = null;
            loadError = error.Message;
            Plugin.Logger?.LogWarning("World Room_Attr load failed: " + error);
            return false;
        }
    }

    internal static WorldRoomAttractionEntry[] GetRoomOverrides(EditorSession session, int roomIndex)
    {
        if (!EnsureLoaded(session))
            return Array.Empty<WorldRoomAttractionEntry>();

        AbstractRoom room = session.World.GetAbstractRoom(roomIndex);
        if (room == null) return Array.Empty<WorldRoomAttractionEntry>();

        Dictionary<string, string> values = ParseRoom(room.name);
        List<WorldRoomAttractionEntry> result = new(values.Count);
        foreach (KeyValuePair<string, string> pair in values)
        {
            CreatureTemplate.Type type = new(pair.Key);
            if (type.Index < 0) continue;

            result.Add(new WorldRoomAttractionEntry
            {
                CreatureId = pair.Key,
                Attraction = pair.Value
            });
        }

        result.Sort((a, b) => string.Compare(a.CreatureId, b.CreatureId, StringComparison.OrdinalIgnoreCase));
        return result.ToArray();
    }

    internal static bool TryGetExplicit(
        EditorSession session,
        int roomIndex,
        string creatureId,
        out string attraction)
    {
        attraction = null;
        if (!EnsureLoaded(session) || string.IsNullOrWhiteSpace(creatureId))
            return false;

        AbstractRoom room = session.World.GetAbstractRoom(roomIndex);
        if (room == null) return false;

        Dictionary<string, string> values = ParseRoom(room.name);
        return values.TryGetValue(creatureId.Trim(), out attraction);
    }

    internal static string GetEffective(EditorSession session, int roomIndex, string creatureId)
    {
        AbstractRoom room = session?.World?.GetAbstractRoom(roomIndex);
        if (room == null || string.IsNullOrWhiteSpace(creatureId))
            return string.Empty;

        CreatureTemplate.Type type = new(creatureId.Trim());
        if (type.Index < 0) return string.Empty;
        return room.AttractionForCreature(type)?.value ?? string.Empty;
    }

    internal static bool SetRoomOverride(
        EditorSession session,
        int roomIndex,
        string creatureId,
        string attraction)
    {
        if (!EnsureLoaded(session))
            return false;

        AbstractRoom room = session.World.GetAbstractRoom(roomIndex);
        if (room == null || string.IsNullOrWhiteSpace(creatureId))
            return false;

        creatureId = creatureId.Trim();
        CreatureTemplate.Type creatureType = new(creatureId);
        if (creatureType.Index < 0)
            return false;

        string next = NormalizeAttraction(attraction);
        TryGetExplicit(session, roomIndex, creatureId, out string before);
        if (string.Equals(before, next, StringComparison.Ordinal))
            return false;

        RewriteRoomToken(room.name, creatureId, next);
        ApplyLive(room, creatureType, next);

        dirty = true;
        BumpRevision();
        EditorRevisionHub.Mark(session, EditorRevisionKind.Map);
        return true;
    }

    internal static bool Save()
    {
        if (!dirty) return true;
        if (lines == null || string.IsNullOrWhiteSpace(loadedPath))
        {
            loadError = "Properties.txt has no writable path.";
            return false;
        }

        string temp = loadedPath + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(loadedPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(temp, lines);
            if (File.Exists(loadedPath))
            {
                File.Copy(loadedPath, loadedPath + ".bak", overwrite: true);
                File.Delete(loadedPath);
            }
            File.Move(temp, loadedPath);
            dirty = false;
            loadError = null;
            BumpRevision();
            return true;
        }
        catch (Exception error)
        {
            loadError = error.Message;
            Plugin.Logger?.LogError("World Room_Attr save failed: " + error);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    internal static void Reset()
    {
        loadedWorld = null;
        loadedPath = string.Empty;
        lines = null;
        dirty = false;
        loadError = null;
        BumpRevision();
    }

    internal static void ResetIfClean()
    {
        if (dirty)
        {
            Plugin.Logger?.LogWarning(
                "World Room_Attr runtime state remains retained because unsaved Properties changes exist.");
            return;
        }

        Reset();
    }

    private static Dictionary<string, string> ParseRoom(string roomName)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (lines == null || string.IsNullOrWhiteSpace(roomName)) return result;

        for (int i = 0; i < lines.Count; i++)
        {
            if (!TryParseRoomAttrLine(lines[i], out string subject, out string payload) ||
                !string.Equals(subject, roomName, StringComparison.OrdinalIgnoreCase))
                continue;

            string[] tokens = payload.Split(',');
            for (int t = 0; t < tokens.Length; t++)
            {
                if (!TryParseToken(tokens[t], out string id, out string value))
                    continue;
                result[id] = value;
            }
        }
        return result;
    }

    private static void RewriteRoomToken(string roomName, string creatureId, string next)
    {
        List<int> matchingLines = new();
        for (int i = 0; i < lines.Count; i++)
        {
            if (TryParseRoomAttrLine(lines[i], out string subject, out _) &&
                string.Equals(subject, roomName, StringComparison.OrdinalIgnoreCase))
                matchingLines.Add(i);
        }

        // Remove every previous occurrence first so "Inherit" cannot accidentally reveal an older
        // duplicate override and a new value has exactly one authoritative token.
        for (int m = 0; m < matchingLines.Count; m++)
        {
            int index = matchingLines[m];
            TryParseRoomAttrLine(lines[index], out _, out string payload);
            List<string> kept = new();
            string[] tokens = payload.Split(',');
            for (int t = 0; t < tokens.Length; t++)
            {
                string raw = tokens[t].Trim();
                if (raw.Length == 0) continue;
                if (TryParseToken(raw, out string id, out _) &&
                    string.Equals(id, creatureId, StringComparison.Ordinal))
                    continue;
                kept.Add(raw);
            }
            lines[index] = "Room_Attr: " + roomName + ": " + string.Join(", ", kept);
        }

        if (next == null)
            return;

        string token = creatureId + "-" + next;
        if (matchingLines.Count > 0)
        {
            int index = matchingLines[matchingLines.Count - 1];
            TryParseRoomAttrLine(lines[index], out _, out string payload);
            string trimmed = payload.Trim();
            lines[index] = "Room_Attr: " + roomName + ": " +
                           (trimmed.Length == 0 ? token : trimmed + ", " + token);
            return;
        }

        int insertion = lines.Count;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!TryParseRoomAttrLine(lines[i], out _, out _)) continue;
            insertion = i + 1;
            break;
        }
        lines.Insert(insertion, "Room_Attr: " + roomName + ": " + token);
    }

    private static void ApplyLive(
        AbstractRoom room,
        CreatureTemplate.Type creatureType,
        string attraction)
    {
        int index = creatureType.Index;
        if (index < 0) return;

        if (room.roomAttractions == null)
            room.roomAttractions = new AbstractRoom.CreatureRoomAttraction[index + 1];
        else if (index >= room.roomAttractions.Length)
            Array.Resize(ref room.roomAttractions, index + 1);

        room.roomAttractions[index] = attraction == null
            ? null
            : new AbstractRoom.CreatureRoomAttraction(attraction);
    }

    private static string NormalizeAttraction(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Inherit", StringComparison.OrdinalIgnoreCase))
            return null;

        string trimmed = value.Trim();
        AbstractRoom.CreatureRoomAttraction attraction =
            new(trimmed);
        return attraction.Index >= 0 ? attraction.value : null;
    }

    private static bool TryParseRoomAttrLine(
        string line,
        out string subject,
        out string payload)
    {
        subject = string.Empty;
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(line)) return false;

        int first = line.IndexOf(':');
        if (first < 0) return false;
        string key = line.Substring(0, first).Trim();
        if (!key.Equals("Room_Attr", StringComparison.OrdinalIgnoreCase))
            return false;

        int second = line.IndexOf(':', first + 1);
        if (second < 0) return false;
        subject = line.Substring(first + 1, second - first - 1).Trim();
        payload = line.Substring(second + 1).Trim();
        return subject.Length > 0;
    }

    private static bool TryParseToken(
        string token,
        out string id,
        out string attraction)
    {
        id = string.Empty;
        attraction = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        int separator = token.IndexOf('-');
        if (separator <= 0 || separator >= token.Length - 1) return false;
        id = token.Substring(0, separator).Trim();
        attraction = token.Substring(separator + 1).Trim();
        return id.Length > 0 && attraction.Length > 0;
    }

    private static string ResolvePath(EditorSession session)
    {
        global::World world = session?.World;
        if (world == null || string.IsNullOrWhiteSpace(world.name))
            return string.Empty;

        string relativeBase = "World" + Path.DirectorySeparatorChar + world.name +
                              Path.DirectorySeparatorChar + "Properties.txt";

        if (session.Owner?.game?.session is StoryGameSession story)
        {
            SlugcatStats.Timeline timeline = SlugcatStats.SlugcatToTimeline(story.saveStateNumber);
            if (timeline != null && !string.IsNullOrWhiteSpace(timeline.value))
            {
                string relativeTimeline = "World" + Path.DirectorySeparatorChar + world.name +
                                          Path.DirectorySeparatorChar + "Properties-" + timeline.value + ".txt";

                // Match Rain World's runtime precedence: a timeline-specific Properties file wins
                // whenever it resolves for reading. Authoring must then target the real source of
                // that exact file rather than silently falling back to base Properties.txt.
                string runtimeTimeline = WorldAuthoringPathResolver.ResolveReadPath(relativeTimeline);
                if (!string.IsNullOrWhiteSpace(runtimeTimeline) && File.Exists(runtimeTimeline))
                    return WorldAuthoringPathResolver.ResolveExistingSource(relativeTimeline);
            }
        }

        return WorldAuthoringPathResolver.ResolveExistingSource(relativeBase);
    }

    private static void BumpRevision()
    {
        unchecked
        {
            revision++;
            if (revision <= 0) revision = 1;
        }
    }
}
