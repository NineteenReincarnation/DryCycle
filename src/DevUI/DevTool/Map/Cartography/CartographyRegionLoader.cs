using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using DryCycle.DevUI.DevTool.World;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// Static asset import; it uses the same room decoder, shortcut compiler and world parser as the editor.
// The resolver is supplied by the installation/mod adapter, never by the UI.
internal static class CartographyRegionLoader
{
    internal static readonly string[] Campaigns = { "White", "Yellow", "Red", "Gourmand", "Artificer", "Rivulet", "Spear", "Saint", "Inv", "Watcher" };
    internal static CartographySource Load(string region, string campaign, Func<string, string> read)
    {
        string prefix = "world/" + region.ToLowerInvariant() + "/";
        string world = read(prefix + "world_" + region.ToLowerInvariant() + ".txt");
        if (world == null) throw new FileNotFoundException("Region world file not found: " + region);
        string map = read(prefix + "map_" + region.ToLowerInvariant() + "-" + campaign.ToLowerInvariant() + ".txt") ?? read(prefix + "map_" + region.ToLowerInvariant() + ".txt");
        if (map == null) throw new FileNotFoundException("Region map file not found: " + region);
        string properties = (read(prefix + "properties.txt") ?? "") + "\n" + (read(prefix + "properties-" + campaign.ToLowerInvariant() + ".txt") ?? "");
        return Parse(region, campaign, world, map, properties, read("world/gates/locks.txt") ?? "", name =>
        {
            string folder = name.StartsWith("GATE_", StringComparison.OrdinalIgnoreCase) ? "world/gates/" : "world/" + region.ToLowerInvariant() + "-rooms/";
            string data = read(folder + name.ToLowerInvariant() + ".txt") ?? read(prefix + name.ToLowerInvariant() + ".txt");
            string settings = read(folder + name.ToLowerInvariant() + "_settings-" + campaign.ToLowerInvariant() + ".txt") ?? read(folder + name.ToLowerInvariant() + "_settings.txt") ?? "";
            return (data, settings);
        });
    }

    internal static CartographySource Parse(string region, string campaign, string world, string map, string properties, string locks,
        Func<string, (string data, string settings)> roomReader)
    {
        WorldDocument topology = WorldDocument.Parse(world);
        HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);
        bool conditional = false;
        foreach (string line in Lines(world))
        {
            if (line == "CONDITIONAL LINKS") { conditional = true; continue; }
            if (line == "END CONDITIONAL LINKS") { conditional = false; continue; }
            if (!conditional) continue;
            string[] p = line.Split(':').Select(s => s.Trim()).ToArray();
            if (p.Length < 3) continue;
            bool applies = Matches(p[0], campaign);
            if (p[1] == "EXCLUSIVEROOM") { if (!applies) hidden.Add(p[2]); }
            else if (p[1] == "HIDEROOM") { if (applies) hidden.Add(p[2]); }
            else if (applies && p.Length >= 4 && topology.TryGetRoom(p[1], out WorldRoomRecord room))
            {
                if (int.TryParse(p[2], out int index)) { if (index >= 0 && index < room.Connections.Count) room.Connections[index] = p[3]; }
                else for (int n = 0; n < room.Connections.Count; n++) if (room.Connections[n].Equals(p[2], StringComparison.OrdinalIgnoreCase)) room.Connections[n] = p[3];
            }
        }
        List<string> subregions = new() { "" };
        foreach (string line in Lines(properties)) if (line.StartsWith("Subregion:", StringComparison.OrdinalIgnoreCase))
        { string name = line.Substring(line.IndexOf(':') + 1).Trim(); if (!subregions.Contains(name)) subregions.Add(name); }
        Dictionary<string, string[]> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(map))
        { int colon = line.IndexOf(':'); if (colon > 0 && line.Contains("><")) positions[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Split(new[] { "><" }, StringSplitOptions.None); }
        CartographySource source = new();
        foreach (string name in subregions) source.Palettes.Add(new CartographyPalette { Name = name, Background = DefaultColor(region, name) });
        foreach (WorldRoomRecord record in topology.Rooms.Values)
        {
            if (record.Name.Equals("OFFSCREEN", StringComparison.OrdinalIgnoreCase)) continue;
            CartographyRoomSource room = new() { Name = record.Name, Tags = string.Join(",", record.Tags), Disabled = hidden.Contains(record.Name) };
            source.Rooms[room.Name] = room;
            if (positions.TryGetValue(room.Name, out string[] pos) && pos.Length >= 4)
            {
                room.X = F(pos[2]) * 1.5f; room.Y = -F(pos[3]) * 1.5f;
                room.Layer = pos.Length > 4 ? (int)F(pos[4]) : 0;
                if (pos.Length > 5)
                {
                    string sub = pos[5].Trim();
                    room.Subregion = int.TryParse(sub, out int subIndex) && subIndex >= 0 && subIndex < subregions.Count ? subregions[subIndex] : sub;
                    if (!source.Palettes.Any(p => p.Name == room.Subregion)) source.Palettes.Add(new CartographyPalette { Name = room.Subregion, Background = DefaultColor(region, room.Subregion) });
                }
            }
            else room.Disabled = true;
            var data = roomReader(room.Name); room.Settings = data.settings ?? "";
            if (data.data == null) { room.Error = "Missing room file: " + room.Name; continue; }
            try { DecodeRoom(room, data.data); }
            catch (Exception error) { throw new InvalidDataException("Failed to decode room " + room.Name, error); }
            SeedDecorations(source, room, region, locks, campaign);
        }
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorldRoomRecord record in topology.Rooms.Values)
        {
            if (!source.Rooms.ContainsKey(record.Name)) continue;
            for (int i = 0; i < record.Connections.Count; i++)
            {
                string target = record.Connections[i];
                if (!topology.TryGetRoom(target, out WorldRoomRecord other) || !source.Rooms.ContainsKey(target)) continue;
                int ordinal = record.Connections.Take(i).Count(n => n.Equals(target, StringComparison.OrdinalIgnoreCase));
                int[] reverse = other.Connections.Select((n, k) => (Name:n, Index:k)).Where(p => p.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Index).ToArray();
                int port = ordinal < reverse.Length ? reverse[ordinal] : -1;
                string a = record.Name + ":" + i, b = target + ":" + port;
                string id = string.Compare(a,b,StringComparison.Ordinal) < 0 ? a + "|" + b : b + "|" + a;
                if (seen.Add(id)) source.Connections.Add(new CartographyConnectionSource { From = record.Name, To = target, FromPort = i, ToPort = port, Ambiguous = port < 0 });
            }
        }
        foreach (CartographyPalette palette in source.Palettes.Where(p => p.Name.Length > 0))
        {
            CartographyRoomSource[] group = source.Rooms.Values.Where(r => !r.Disabled && r.Subregion == palette.Name).ToArray();
            if (group.Length == 0) continue;
            source.Decorations.Add(new CartographyItem { Id = "subregion:" + palette.Name, Kind = CartographyItemKind.Text, LayerId = "notes", Text = palette.Name.ToUpperInvariant(),
                X = group.Average(r => r.X), Y = group.Min(r => r.Y - r.Height * 1.5f) - 70, Size = 36, Color = palette.Background,
                Appearance = new CartographyAppearance { Bold = true, Outline = 5, Category = "Subregion" } });
        }
        return source;
    }

    internal static void DecodeRoom(CartographyRoomSource room, string data)
    {
        string[] lines = data.Replace("\r", "").Split('\n');
        RoomMapSource decoded = RoomMapTextDecoder.Parse(lines);
        RoomMapBake bake = RoomMapSemanticCompiler.Compile(0, room.Name, decoded, room.Name + ".txt");
        room.Width = bake.Width; room.Height = bake.Height; room.Ready = true;
        room.Runs = bake.Runs.Select(r => new CartographyTileRun(r.X, r.Y, r.Length, (int)r.Kind, r.Water)).ToArray();
        room.Terrain = decoded.Tiles.Select(t => t.Terrain).ToArray();
        foreach (RoomMapNodeAnchorSnapshot port in bake.NodeAnchors) room.Ports[port.NodeIndex] = new CartographyRect(port.EntranceX, port.EntranceY, 0, 0);
        // Trace only genuine in-room passages; the compiler owns shortcut traversal/order.
        room.Shortcuts = RoomMapSemanticCompiler.TraceInternal(decoded).Select(path => path.Select(p => new CartographyPoint { X = p.X, Y = p.Y }).ToArray()).ToList();
    }

    private static void SeedDecorations(CartographySource source, CartographyRoomSource room, string region, string locks, string campaign)
    {
        if (room.Tags.Contains("SHELTER")) AddMarker(source, room, "ShelterMarker", CartographyMarker.Shelter, 0, 0, "Special");
        if (room.Tags.Contains("ANCIENTSHELTER")) AddMarker(source, room, "AncientShelter", CartographyMarker.AncientShelter, 0, 0, "Special");
        if (room.Tags.Contains("SCAVTRADER")) Special(source, room, "SCAVENGER MERCHANT", CartographyMarker.Trader, 0, 0);
        if (room.Tags.Contains("SCAVOUTPOST")) Special(source, room, "SCAVENGER TOLL", CartographyMarker.Outpost, 0, 0);
        if (room.Tags.Contains("GATE") || room.Name.StartsWith("GATE_"))
        {
            string[] name = room.Name.Split('_');
            string[] gate = Lines(locks).Select(l => l.Split(':').Select(s => s.Trim()).ToArray()).FirstOrDefault(p => p.Length >= 3 && p[0].Equals(room.Name, StringComparison.OrdinalIgnoreCase));
            CartographyItem item = AddMarker(source, room, "GateSymbols", CartographyMarker.Gate, 0, -room.Height * 1.5f - 46, "Special");
            item.Size = 42; item.Appearance.LeftKarma = gate?[1] ?? "1"; item.Appearance.RightKarma = gate?[2] ?? "1";
            item.Appearance.TargetRegion = name.Length >= 3 ? (name[1].Equals(region, StringComparison.OrdinalIgnoreCase) ? name[2] : name[1]) : "";
            item.Appearance.LeftArrow = DefaultColor(name.Length > 1 ? name[1] : region, ""); item.Appearance.RightArrow = DefaultColor(name.Length > 2 ? name[2] : region, "");
            AddLabel(source, room, "TargetRegionText", "TO " + item.Appearance.TargetRegion, -65, item.Y - 58, 26);
        }
        int ordinal = 0;
        foreach (string line in Lines(room.Settings))
        {
            if (!line.StartsWith("PlacedObjects:", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string value in line.Substring(line.IndexOf(':') + 1).Split(','))
            {
                string[] p = value.Trim().Split(new[] { "><" }, StringSplitOptions.None);
                if (p.Length < 3 || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
                string type = p[0]; float px = (x / 20 - room.Width / 2f) * 3, py = (room.Height / 2f - y / 20) * 3;
                if (type == "ScavengerTreasury" || type == "ScavengerOutpost") { Special(source, room, type == "ScavengerTreasury" ? "SCAVENGER TREASURY" : "SCAVENGER TOLL", type == "ScavengerTreasury" ? CartographyMarker.Treasury : CartographyMarker.Outpost, px, py); continue; }
                if (!Interesting(type)) continue;
                CartographyMarker marker = type.Contains("Pearl") ? CartographyMarker.Pearl : type.Contains("Token") ? CartographyMarker.Token : type.Contains("Ghost") ? CartographyMarker.Echo : CartographyMarker.Sprite;
                CartographyItem placed = AddMarker(source, room, type + "@" + ordinal++, marker, px, py, type.Contains("Token") || type.Contains("Pearl") || type.Contains("Ghost") ? "Object" : "Pickup");
                placed.Text = type; placed.Size = 15; placed.Appearance.Icon = IconFor(type);
                if (type.Contains("Token") && p.Length > 3) { string[] extra = p[3].Split('~'); if (extra.Length > 6) placed.Appearance.Availability = extra[6].Replace('|', ','); }
                if (type.Contains("Pearl")) placed.Color = type.Contains("Unique") ? 0xFFFFDC77 : 0xFFE9EBF5;
            }
        }
    }
    private static bool Interesting(string t) => t.Contains("Token") || t.Contains("Pearl") || t.Contains("Ghost") || new[] { "DangleFruit", "JellyFish", "BubbleGrass", "Mushroom", "SlimeMold", "Lantern", "SeedCob", "KarmaFlower", "VultureGrub", "FirecrackerPlant", "SporePlant", "FlyLure", "NeedleEgg", "EggBugEgg", "WaterNut", "GooieDuck", "DandelionPeach", "LillyPuck", "GlowWeed", "FireEgg", "Hazer", "DeadTokenStalk" }.Contains(t);
    internal static string IconFor(string t) => t switch { "ShelterMarker" => "ShelterMarker", "DangleFruit" => "Symbol_DangleFruit", "Mushroom" => "Symbol_Mushroom", "KarmaFlower" => "Symbol_KarmaFlower", _ => "Symbol_" + t };
    private static void Special(CartographySource source, CartographyRoomSource room, string text, CartographyMarker marker, float x, float y)
    { AddMarker(source, room, text + "Icon", marker, x, y, "Special"); AddLabel(source, room, text, text, x + 26, y - 12, 20); }
    internal static CartographyItem AddMarker(CartographySource source, CartographyRoomSource room, string key, CartographyMarker marker, float x, float y, string category)
    {
        CartographyItem item = new() { Id = room.Name + "/" + key, Kind = CartographyItemKind.Marker, Marker = marker, LayerId = "notes", X = x, Y = y, Size = 20, Color = 0xFFFFFFFF,
            Appearance = new CartographyAppearance { ParentId = "room:" + room.Name, Category = category } };
        if (!source.Decorations.Any(d => d.Id == item.Id)) source.Decorations.Add(item); return item;
    }
    internal static void AddLabel(CartographySource source, CartographyRoomSource room, string key, string text, float x, float y, float size) =>
        source.Decorations.Add(new CartographyItem { Id = room.Name + "/" + key, Kind = CartographyItemKind.Text, Text = text, LayerId = "notes", X = x, Y = y, Size = size, Color = 0xFFFFFFFF,
            Appearance = new CartographyAppearance { ParentId = "room:" + room.Name, Category = "Special", Bold = true } });
    internal static IEnumerable<string> Lines(string text) => (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//"));
    internal static float F(string value) => float.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static bool Matches(string filter, string campaign) { bool exclude = filter.StartsWith("X-"); return filter.Replace("X-", "").Split(',').Any(s => s.Trim().Equals(campaign, StringComparison.OrdinalIgnoreCase)) != exclude; }
    internal static uint DefaultColor(string region, string subregion) => subregion.IndexOf("Gutter", StringComparison.OrdinalIgnoreCase) >= 0 ? 0xFF91641D : region.ToUpperInvariant() switch
    { "CC" => 0xFFC53D0F, "HI" => 0xFF76C8CB, "DS" => 0xFFAB24C9, "SU" => 0xFF84AD67, "GW" => 0xFF85AE3C, "SH" => 0xFF7854AC, "SI" => 0xFFB58042, "LF" => 0xFFADB251, "UW" => 0xFFD8D9EA, "SL" => 0xFF81AADB, _ => 0xFFAAB9C9 };
}
