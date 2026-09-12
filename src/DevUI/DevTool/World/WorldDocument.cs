using System;
using System.Collections.Generic;
using System.Text;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Lossless world.txt editing document.
/// Unknown sections/directives stay as their original lines. Known room records are parsed
/// only while inside ROOMS / END ROOMS and are regenerated only after the editor changes them.
/// </summary>
internal sealed class WorldDocument
{
    private sealed class LineRecord
    {
        internal string Raw = string.Empty;
        internal WorldRoomRecord Room;
    }

    private readonly List<LineRecord> lines = new();
    private readonly Dictionary<string, WorldRoomRecord> rooms =
        new(StringComparer.OrdinalIgnoreCase);

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
            else if (inRooms && TryParseRoomLine(raw, i, out WorldRoomRecord room))
            {
                line.Room = room;
                if (!document.rooms.ContainsKey(room.Name))
                    document.rooms.Add(room.Name, room);
            }

            document.lines.Add(line);
        }

        document.Dirty = false;
        return document;
    }

    internal bool TryGetRoom(string roomName, out WorldRoomRecord room) =>
        rooms.TryGetValue(NormalizeRoom(roomName), out room);

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

    internal string Serialize()
    {
        StringBuilder builder = new();
        for (int i = 0; i < lines.Count; i++)
        {
            LineRecord line = lines[i];
            builder.Append(line.Room?.Changed == true ? BuildRoomLine(line.Room) : line.Raw);
            if (i < lines.Count - 1 || EndsWithNewLine) builder.Append(NewLine);
        }
        return builder.ToString();
    }

    internal void MarkSaved(string sourcePath = null)
    {
        if (sourcePath != null) SourcePath = sourcePath;
        for (int i = 0; i < lines.Count; i++)
        {
            LineRecord line = lines[i];
            if (line.Room?.Changed != true) continue;
            line.Raw = BuildRoomLine(line.Room);
            line.Room.Changed = false;
        }
        Dirty = false;
    }

    private void MarkRoomChanged(WorldRoomRecord room)
    {
        room.Changed = true;
        Dirty = true;
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

    private static bool SequenceEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string NormalizeRoom(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

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
