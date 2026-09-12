using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Resolves the visual mouth of map shortcuts without forcing every room to realize.
///
/// For realized rooms we use ShortcutData.StartTile, which is the actual visible pipe entrance.
/// For cached MapTex rooms the vanilla minimap already encodes RoomExit and CreatureHole entrance
/// tiles with dedicated colours, so the same positions can be recovered incrementally from the
/// cached texture. This avoids the old fallback that evenly distributed exits along room edges.
/// </summary>
internal static class WorldMapShortcutPresentation
{
    internal readonly struct ShortcutMarker
    {
        internal ShortcutMarker(float x, float y)
        {
            X = x;
            Y = y;
        }

        internal float X { get; }
        internal float Y { get; }
    }

    private sealed class CacheEntry
    {
        internal int RoomIndex;
        internal AbstractRoom Room;
        internal MapObject.RoomRepresentation RoomRep;
        internal readonly Dictionary<int, ShortcutMarker> ExitMouths = new();
        internal ShortcutMarker[] CreatureHoles = Array.Empty<ShortcutMarker>();
        internal bool Initialized;
        internal bool BuiltFromRealizedRoom;
        internal int SourceKey;
        internal int SourceWidth;
        internal int SourceHeight;
        internal int NextPollFrame;
    }

    private readonly struct RasterSource
    {
        internal RasterSource(Texture2D texture, int x, int y, int width, int height, int sourceKey)
        {
            Texture = texture;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            SourceKey = sourceKey;
        }

        internal Texture2D Texture { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int SourceKey { get; }
    }

    private const int TextureScansPerFrame = 6;
    private const int BackgroundRoomsPerFrame = 24;
    private const int StructureSyncIntervalFrames = 120;
    private const int SourcePollIntervalFrames = 180;

    private static readonly Dictionary<int, CacheEntry> cache = new();
    private static readonly List<int> roomOrder = new();
    private static string region = string.Empty;
    private static int lastPrimeFrame = -1;
    private static int lastSubNodeCount = -1;
    private static int nextStructureSyncFrame;
    private static int backgroundCursor;
    private static int scansRemaining;

    internal static void Prime(EditorSession session, int selectedRoomIndex)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null)
        {
            Clear();
            return;
        }

        string nextRegion = page.world.name ?? string.Empty;
        bool regionChanged = !string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase);
        if (regionChanged)
            ResetRegion(nextRegion);

        if (lastPrimeFrame == Time.frameCount) return;
        lastPrimeFrame = Time.frameCount;

        bool structureDue = regionChanged ||
                            cache.Count == 0 ||
                            page.subNodes.Count != lastSubNodeCount ||
                            Time.frameCount >= nextStructureSyncFrame;
        if (structureDue)
            SynchronizeStructure(page);

        scansRemaining = TextureScansPerFrame;
        int currentRoomIndex = session.Room?.abstractRoom?.index ?? -1;

        RefreshPriority(currentRoomIndex);
        if (selectedRoomIndex != currentRoomIndex)
            RefreshPriority(selectedRoomIndex);

        ProcessBackground(currentRoomIndex, selectedRoomIndex);
    }

    internal static bool TryGetExitMouth(int roomIndex, int nodeIndex, out ShortcutMarker marker)
    {
        marker = default;
        return cache.TryGetValue(roomIndex, out CacheEntry entry) &&
               entry.ExitMouths.TryGetValue(nodeIndex, out marker);
    }

    internal static ShortcutMarker[] GetCreatureHoles(int roomIndex)
    {
        return cache.TryGetValue(roomIndex, out CacheEntry entry)
            ? entry.CreatureHoles
            : Array.Empty<ShortcutMarker>();
    }

    private static void RefreshPriority(int roomIndex)
    {
        if (roomIndex < 0 || !cache.TryGetValue(roomIndex, out CacheEntry entry)) return;
        if (Refresh(entry, allowTextureScan: true, forcePoll: true) && scansRemaining > 0)
            scansRemaining--;
    }

    private static void ProcessBackground(int currentRoom, int selectedRoom)
    {
        int count = roomOrder.Count;
        if (count == 0) return;

        int checks = Math.Min(count, BackgroundRoomsPerFrame);
        for (int i = 0; i < checks; i++)
        {
            if (backgroundCursor >= count) backgroundCursor = 0;
            int roomIndex = roomOrder[backgroundCursor++];
            if (roomIndex == currentRoom || roomIndex == selectedRoom) continue;
            if (!cache.TryGetValue(roomIndex, out CacheEntry entry)) continue;

            bool canScan = scansRemaining > 0;
            if (Refresh(entry, canScan, forcePoll: false) && canScan)
                scansRemaining--;
        }
    }

    private static void SynchronizeStructure(MapPage page)
    {
        HashSet<int> alive = new();
        roomOrder.Clear();

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            alive.Add(room.index);
            roomOrder.Add(room.index);

            if (!cache.TryGetValue(room.index, out CacheEntry entry))
            {
                entry = new CacheEntry { RoomIndex = room.index };
                cache.Add(room.index, entry);
            }

            entry.Room = room;
            entry.RoomRep = panel.roomRep;
        }

        if (cache.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int key in cache.Keys)
                if (!alive.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
        }

        if (backgroundCursor >= roomOrder.Count) backgroundCursor = 0;
        lastSubNodeCount = page.subNodes.Count;
        nextStructureSyncFrame = Time.frameCount + StructureSyncIntervalFrames;
    }

    private static bool Refresh(CacheEntry entry, bool allowTextureScan, bool forcePoll)
    {
        if (entry?.Room == null || entry.RoomRep == null) return false;

        global::Room realized = entry.Room.realizedRoom;
        if (realized?.shortcuts != null && realized.shortcuts.Length > 0)
        {
            if (!entry.BuiltFromRealizedRoom || forcePoll)
                BuildFromRealizedRoom(entry, realized);
            return false;
        }

        if (entry.BuiltFromRealizedRoom)
        {
            entry.BuiltFromRealizedRoom = false;
            entry.Initialized = false;
            entry.NextPollFrame = 0;
        }

        if (entry.Initialized && !forcePoll && Time.frameCount < entry.NextPollFrame)
            return false;

        if (!TryGetRasterSource(entry.RoomRep, out RasterSource source))
            return false;

        if (entry.Initialized &&
            entry.SourceKey == source.SourceKey &&
            entry.SourceWidth == source.Width &&
            entry.SourceHeight == source.Height)
        {
            entry.NextPollFrame = Time.frameCount + SourcePollIntervalFrames + Math.Abs(entry.RoomIndex % 41);
            return false;
        }

        if (!allowTextureScan) return false;
        if (!TryReadPixels(source, out Color[] pixels)) return false;

        BuildFromMapPixels(entry, pixels, source.Width, source.Height);
        entry.SourceKey = source.SourceKey;
        entry.SourceWidth = source.Width;
        entry.SourceHeight = source.Height;
        entry.Initialized = true;
        entry.BuiltFromRealizedRoom = false;
        entry.NextPollFrame = Time.frameCount + SourcePollIntervalFrames + Math.Abs(entry.RoomIndex % 41);
        return true;
    }

    private static void BuildFromRealizedRoom(CacheEntry entry, global::Room room)
    {
        entry.ExitMouths.Clear();
        List<ShortcutMarker> creatureHoles = new();
        ShortcutData[] shortcuts = room.shortcuts ?? Array.Empty<ShortcutData>();

        for (int i = 0; i < shortcuts.Length; i++)
        {
            ShortcutData shortcut = shortcuts[i];
            ShortcutMarker marker = new(shortcut.StartTile.x + 0.5f, shortcut.StartTile.y + 0.5f);
            if (shortcut.shortCutType == ShortcutData.Type.RoomExit && shortcut.destNode >= 0)
            {
                entry.ExitMouths[shortcut.destNode] = marker;
            }
            else if (shortcut.shortCutType == ShortcutData.Type.CreatureHole)
            {
                creatureHoles.Add(marker);
            }
        }

        entry.CreatureHoles = creatureHoles.ToArray();
        entry.Initialized = true;
        entry.BuiltFromRealizedRoom = true;
        entry.NextPollFrame = Time.frameCount + 30;
    }

    private static void BuildFromMapPixels(CacheEntry entry, Color[] pixels, int width, int height)
    {
        entry.ExitMouths.Clear();
        List<ShortcutMarker> exits = new();
        List<ShortcutMarker> creatureHoles = new();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color color = pixels[y * width + x];
                if (IsRoomExitPixel(color))
                    exits.Add(new ShortcutMarker(x + 0.5f, y + 0.5f));
                else if (IsCreatureHolePixel(color))
                    creatureHoles.Add(new ShortcutMarker(x + 0.5f, y + 0.5f));
            }
        }

        // ShortcutMapper indexes room exits top-to-bottom, then left-to-right. AbstractRoom Exit
        // nodes use that same ordering. This lets cached MapTex data recover the destination-node
        // mapping without realizing the room just for DevTool presentation.
        exits.Sort(CompareShortcutScanOrder);
        List<int> exitNodes = new();
        AbstractRoomNode[] nodes = entry.Room.nodes ?? Array.Empty<AbstractRoomNode>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].type == AbstractRoomNode.Type.Exit) exitNodes.Add(i);

        int pairCount = Math.Min(exitNodes.Count, exits.Count);
        for (int i = 0; i < pairCount; i++)
            entry.ExitMouths[exitNodes[i]] = exits[i];

        creatureHoles.Sort(CompareShortcutScanOrder);
        entry.CreatureHoles = creatureHoles.ToArray();
    }

    private static int CompareShortcutScanOrder(ShortcutMarker a, ShortcutMarker b)
    {
        int byY = b.Y.CompareTo(a.Y);
        return byY != 0 ? byY : a.X.CompareTo(b.X);
    }

    private static bool IsRoomExitPixel(Color color)
    {
        // Vanilla MapObject uses (0, 1, 0.2) for RoomExit and then may blend water into it.
        return color.r < 0.18f &&
               color.g > 0.52f &&
               color.g > color.b + 0.08f &&
               color.b > 0.06f && color.b < 0.64f;
    }

    private static bool IsCreatureHolePixel(Color color)
    {
        // Vanilla MapObject uses magenta (1, 0, 1) for CreatureHole; underwater blending keeps
        // blue high and green near zero, so use a tolerant signature rather than exact equality.
        return color.r > 0.55f &&
               color.g < 0.20f &&
               color.b > 0.72f;
    }

    private static bool TryGetRasterSource(MapObject.RoomRepresentation roomRep, out RasterSource source)
    {
        source = default;
        try
        {
            if (roomRep?.texture != null)
            {
                Texture2D texture = roomRep.texture;
                source = new RasterSource(
                    texture,
                    0,
                    0,
                    Math.Max(1, texture.width),
                    Math.Max(1, texture.height),
                    texture.GetInstanceID());
                return true;
            }

            FAtlasElement element = roomRep?.mapTex;
            if (element?.atlas?.texture is not Texture2D atlasTexture) return false;

            Rect uv = element.uvRect;
            int atlasX = Mathf.Clamp(Mathf.RoundToInt(uv.x * atlasTexture.width), 0, Math.Max(0, atlasTexture.width - 1));
            int atlasY = Mathf.Clamp(Mathf.RoundToInt(uv.y * atlasTexture.height), 0, Math.Max(0, atlasTexture.height - 1));
            int width = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.width) * atlasTexture.width), 1, atlasTexture.width - atlasX);
            int height = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.height) * atlasTexture.height), 1, atlasTexture.height - atlasY);

            unchecked
            {
                int key = atlasTexture.GetInstanceID();
                key = key * 397 ^ (element.name?.GetHashCode() ?? 0);
                key = key * 397 ^ uv.x.GetHashCode();
                key = key * 397 ^ uv.y.GetHashCode();
                key = key * 397 ^ uv.width.GetHashCode();
                key = key * 397 ^ uv.height.GetHashCode();
                source = new RasterSource(atlasTexture, atlasX, atlasY, width, height, key);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPixels(RasterSource source, out Color[] pixels)
    {
        pixels = null;
        try
        {
            pixels = source.X == 0 && source.Y == 0 &&
                     source.Width == source.Texture.width && source.Height == source.Texture.height
                ? source.Texture.GetPixels()
                : source.Texture.GetPixels(source.X, source.Y, source.Width, source.Height);
            return pixels != null && pixels.Length == source.Width * source.Height;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug("WorldMap shortcut raster unavailable: " + error.Message);
            return false;
        }
    }

    private static void ResetRegion(string nextRegion)
    {
        cache.Clear();
        roomOrder.Clear();
        region = nextRegion ?? string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
        scansRemaining = 0;
    }

    private static void Clear()
    {
        cache.Clear();
        roomOrder.Clear();
        region = string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
        scansRemaining = 0;
    }
}
