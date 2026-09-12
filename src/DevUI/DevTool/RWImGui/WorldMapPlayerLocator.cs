using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Installs the player-location presentation beside the existing WorldMapView without making the
/// core map snapshot depend on a particular visual frontend. Rain World's live Player/PlayerState
/// remains authoritative; this class only converts that state into lightweight map markers.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPlayerLocatorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPlayerLocator";
    public const string PluginName = "DryCycle DevTool World Map Player Locator";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPlayerLocator.Enable(Logger);

    private void OnDisable() => WorldMapPlayerLocator.Disable();
}

internal static class WorldMapPlayerLocator
{
    private const float TileDisplaySize = 2f;

    private delegate void OrigWorldMapMethod(EditorMapPresentationSnapshot snapshot);
    private delegate void HookWorldMapMethod(OrigWorldMapMethod orig, EditorMapPresentationSnapshot snapshot);

    internal sealed class Marker
    {
        internal int RoomIndex;
        internal float TileX;
        internal float TileY;
        internal bool HasTilePosition;
        internal int PlayerNumber;
        internal string SlugcatId;
        internal Color Tint;
        internal bool Dead;
        internal PixelIcon Icon;
    }

    internal sealed class PixelIcon
    {
        internal int Width;
        internal int Height;
        internal bool PreserveSourceColor;
        internal PixelRun[] Runs;
        internal string Source;
    }

    internal readonly struct PixelRun
    {
        internal readonly int X;
        internal readonly int Y;
        internal readonly int Length;
        internal readonly Color32 Color;

        internal PixelRun(int x, int y, int length, Color32 color)
        {
            X = x;
            Y = y;
            Length = length;
            Color = color;
        }
    }

    private static readonly Dictionary<string, PixelIcon> IconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Marker[] EmptyMarkers = Array.Empty<Marker>();

    private static readonly HookWorldMapMethod CanvasHookDelegate = DrawCanvasHook;
    private static readonly HookWorldMapMethod ToolbarHookDelegate = DrawToolbarHook;

    private static ManualLogSource log;
    private static object canvasHook;
    private static object toolbarHook;
    private static bool enabled;
    private static bool showPlayers = true;

    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo localPositionsField;
    private static FieldInfo layerVisibleField;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type mapType = typeof(WorldMapView);
            MethodInfo drawCanvas = mapType.GetMethod(
                "DrawCanvas",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo drawToolbar = mapType.GetMethod(
                "DrawToolbar",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);

            panField = mapType.GetField("pan", flags);
            zoomField = mapType.GetField("zoom", flags);
            localPositionsField = mapType.GetField("localPositions", flags);
            layerVisibleField = mapType.GetField("layerVisible", flags);

            if (drawCanvas == null || drawToolbar == null || panField == null || zoomField == null ||
                localPositionsField == null || layerVisibleField == null)
                throw new MissingMemberException("WorldMapView layout members required by the player locator were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo hookConstructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (hookConstructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            canvasHook = hookConstructor.Invoke(new object[] { drawCanvas, CanvasHookDelegate });
            toolbarHook = hookConstructor.Invoke(new object[] { drawToolbar, ToolbarHookDelegate });
            enabled = true;
            log?.LogInfo("World Map player locator enabled.");
        }
        catch (Exception error)
        {
            DisposeHook(ref canvasHook);
            DisposeHook(ref toolbarHook);
            enabled = false;
            log?.LogWarning("World Map player locator could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref canvasHook);
        DisposeHook(ref toolbarHook);
        IconCache.Clear();
        panField = null;
        zoomField = null;
        localPositionsField = null;
        layerVisibleField = null;
        enabled = false;
        log = null;
    }

    private static void DrawToolbarHook(OrigWorldMapMethod orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);

        string label = DevToolUiSettings.T("玩家位置", "Players");
        float width = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + 16f;
        if (!DevToolWidgets.SameLineIfFits(width, 4f))
            ImGui.Spacing();
        ImGui.Checkbox(label + "##WorldMapPlayerLocator", ref showPlayers);
    }

    private static void DrawCanvasHook(OrigWorldMapMethod orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        if (!showPlayers || snapshot?.Available != true) return;

        // DrawCanvas creates one full-canvas InvisibleButton before issuing draw-list commands.
        // No later map operation replaces that item, so its rectangle remains the exact clipped
        // map viewport here without duplicating WorldMapView's window-layout calculations.
        Num.Vector2 canvasMin = ImGui.GetItemRectMin();
        Num.Vector2 canvasMax = ImGui.GetItemRectMax();
        if (canvasMax.X <= canvasMin.X || canvasMax.Y <= canvasMin.Y) return;

        Marker[] markers = CaptureMarkers();
        if (markers.Length == 0) return;

        Num.Vector2 pan = panField?.GetValue(null) is Num.Vector2 p ? p : Num.Vector2.Zero;
        float zoom = zoomField?.GetValue(null) is float z ? z : 1f;
        Dictionary<int, Num.Vector2> localPositions =
            localPositionsField?.GetValue(null) as Dictionary<int, Num.Vector2>;
        bool[] layerVisible = layerVisibleField?.GetValue(null) as bool[];

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        ImGuiIOPtr io = ImGui.GetIO();
        bool multiple = markers.Length > 1;

        for (int i = 0; i < markers.Length; i++)
        {
            Marker marker = markers[i];
            EditorMapRoomSnapshot room = FindRoom(snapshot, marker.RoomIndex);
            if (room == null || !IsLayerVisible(room.Layer, layerVisible)) continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            Num.Vector2 worldPosition = localPositions != null && localPositions.TryGetValue(room.RoomIndex, out Num.Vector2 local)
                ? local
                : new Num.Vector2(room.X, room.Y);
            Num.Vector2 roomMin = canvasMin + pan + worldPosition * zoom;

            float tileX = marker.HasTilePosition
                ? Clamp(marker.TileX, 0f, Math.Max(1f, visual.WidthTiles))
                : Math.Max(1f, visual.WidthTiles) * 0.5f;
            float tileY = marker.HasTilePosition
                ? Clamp(marker.TileY, 0f, Math.Max(1f, visual.HeightTiles))
                : Math.Max(1f, visual.HeightTiles) * 0.5f;

            Num.Vector2 point = new(
                roomMin.X + tileX * TileDisplaySize * zoom,
                roomMin.Y + (visual.HeightTiles - tileY) * TileDisplaySize * zoom);

            if (!PointNearCanvas(point, canvasMin, canvasMax, 28f)) continue;

            Num.Vector2 offset = multiple ? MultiplayerOffset(marker.PlayerNumber) : Num.Vector2.Zero;
            Num.Vector2 markerCenter = point + offset;
            DrawMarker(draw, marker, markerCenter, zoom, multiple);

            if (IsMarkerHovered(marker, markerCenter, io.MousePos, zoom))
                DrawTooltip(marker, room);
        }
    }

    private static Marker[] CaptureMarkers()
    {
        EditorSession session = DevToolRuntime.ActiveSession;
        RainWorldGame game = session?.Owner?.game;
        List<AbstractCreature> players = game?.Players;
        if (players == null || players.Count == 0) return EmptyMarkers;

        List<Marker> result = new(players.Count);
        for (int i = 0; i < players.Count; i++)
        {
            AbstractCreature abstractPlayer = players[i];
            if (abstractPlayer == null) continue;

            Player realized = abstractPlayer.realizedCreature as Player;
            PlayerState state = realized?.playerState ?? abstractPlayer.state as PlayerState;
            int roomIndex = realized?.room?.abstractRoom?.index ?? abstractPlayer.pos.room;
            if (roomIndex < 0) continue;

            bool hasTile = false;
            float tileX = 0f;
            float tileY = 0f;
            if (realized?.mainBodyChunk != null && realized.room != null)
            {
                tileX = realized.mainBodyChunk.pos.x / 20f;
                tileY = realized.mainBodyChunk.pos.y / 20f;
                hasTile = true;
            }
            else if (abstractPlayer.pos.TileDefined)
            {
                tileX = abstractPlayer.pos.x + 0.5f;
                tileY = abstractPlayer.pos.y + 0.5f;
                hasTile = true;
            }

            string slugcatId = state?.slugcatCharacter?.value ?? string.Empty;
            Color tint = ResolvePlayerColor(realized, state);
            PixelIcon icon = ResolveIcon(slugcatId);
            if (icon == null) continue;

            result.Add(new Marker
            {
                RoomIndex = roomIndex,
                TileX = tileX,
                TileY = tileY,
                HasTilePosition = hasTile,
                PlayerNumber = state?.playerNumber ?? i,
                SlugcatId = slugcatId,
                Tint = tint,
                Dead = state?.dead == true,
                Icon = icon
            });
        }

        return result.Count == 0 ? EmptyMarkers : result.ToArray();
    }

    private static Color ResolvePlayerColor(Player player, PlayerState state)
    {
        try
        {
            if (player != null)
                return player.ShortCutColor();
        }
        catch
        {
            // Fall through to the character palette below.
        }

        try
        {
            if (state?.slugcatCharacter != null)
                return PlayerGraphics.SlugcatColor(state.slugcatCharacter);
        }
        catch
        {
            // Custom campaigns can provide incomplete colour metadata while loading.
        }

        return Color.white;
    }

    private static PixelIcon ResolveIcon(string slugcatId)
    {
        string key = string.IsNullOrWhiteSpace(slugcatId) ? "<default>" : slugcatId.Trim();
        if (IconCache.TryGetValue(key, out PixelIcon cached)) return cached;

        PixelIcon icon = null;
        if (!string.Equals(key, "<default>", StringComparison.Ordinal))
        {
            icon = TryLoadAtlasElement(key + "_icon", preserveSourceColor: true);
            if (icon == null)
            {
                string path = ResolveCustomIconPath(key);
                if (!string.IsNullOrEmpty(path))
                    icon = TryLoadPng(path);
            }
        }

        icon ??= ResolveVanillaSlugcatIcon();
        IconCache[key] = icon;
        return icon;
    }

    private static PixelIcon ResolveVanillaSlugcatIcon()
    {
        const string key = "<Kill_Slugcat>";
        if (IconCache.TryGetValue(key, out PixelIcon cached)) return cached;

        PixelIcon icon = TryLoadAtlasElement("Kill_Slugcat", preserveSourceColor: false) ?? BuildFallbackSlugcatIcon();
        IconCache[key] = icon;
        return icon;
    }

    private static PixelIcon TryLoadAtlasElement(string elementName, bool preserveSourceColor)
    {
        try
        {
            if (Futile.atlasManager == null || !Futile.atlasManager.DoesContainElementWithName(elementName))
            {
                string lower = elementName.ToLowerInvariant();
                if (Futile.atlasManager == null || !Futile.atlasManager.DoesContainElementWithName(lower))
                    return null;
                elementName = lower;
            }

            FAtlasElement element = Futile.atlasManager.GetElementWithName(elementName);
            Texture2D texture = element?.atlas?.texture as Texture2D;
            if (texture == null) return null;

            Rect uv = element.uvRect;
            int x = Mathf.Clamp(Mathf.RoundToInt(uv.xMin * texture.width), 0, Math.Max(0, texture.width - 1));
            int y = Mathf.Clamp(Mathf.RoundToInt(uv.yMin * texture.height), 0, Math.Max(0, texture.height - 1));
            int width = Mathf.Clamp(Mathf.RoundToInt(uv.width * texture.width), 1, texture.width - x);
            int height = Mathf.Clamp(Mathf.RoundToInt(uv.height * texture.height), 1, texture.height - y);
            Color[] pixels = texture.GetPixels(x, y, width, height);
            Color32[] converted = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) converted[i] = pixels[i];

            return BuildIcon(width, height, converted, preserveSourceColor, "atlas:" + elementName);
        }
        catch (Exception error)
        {
            log?.LogDebug("Could not rasterize map icon '" + elementName + "': " + error.Message);
            return null;
        }
    }

    private static PixelIcon TryLoadPng(string path)
    {
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            AssetManager.SafeWWWLoadTexture(ref texture, path, clampWrapMode: true, crispPixels: true);
            Color32[] pixels = texture.GetPixels32();
            return BuildIcon(texture.width, texture.height, pixels, preserveSourceColor: true, path);
        }
        catch (Exception error)
        {
            log?.LogWarning("World Map could not load slugcat icon '" + path + "': " + error.Message);
            return null;
        }
        finally
        {
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
        }
    }

    private static PixelIcon BuildIcon(
        int width,
        int height,
        Color32[] sourcePixels,
        bool preserveSourceColor,
        string source)
    {
        if (width <= 0 || height <= 0 || sourcePixels == null || sourcePixels.Length < width * height)
            return null;

        List<PixelRun> runs = new();
        for (int topY = 0; topY < height; topY++)
        {
            int sourceY = height - 1 - topY;
            int x = 0;
            while (x < width)
            {
                Color32 color = sourcePixels[sourceY * width + x];
                if (color.a < 12)
                {
                    x++;
                    continue;
                }

                int start = x;
                x++;
                while (x < width)
                {
                    Color32 next = sourcePixels[sourceY * width + x];
                    if (next.a < 12 || !SameRunColor(color, next, preserveSourceColor)) break;
                    x++;
                }

                runs.Add(new PixelRun(start, topY, x - start, color));
            }
        }

        if (runs.Count == 0) return null;
        return new PixelIcon
        {
            Width = width,
            Height = height,
            PreserveSourceColor = preserveSourceColor,
            Runs = runs.ToArray(),
            Source = source ?? string.Empty
        };
    }

    private static bool SameRunColor(Color32 a, Color32 b, bool preserveSourceColor)
    {
        if (a.a != b.a) return false;
        return !preserveSourceColor || a.r == b.r && a.g == b.g && a.b == b.b;
    }

    private static PixelIcon BuildFallbackSlugcatIcon()
    {
        string[] rows =
        {
            "##.......##",
            "###.....###",
            ".#########.",
            ".#########.",
            "..##...##..",
            "..#######..",
            "...#####...",
            "...#####...",
            "....###....",
            "....###....",
            "...#####..."
        };

        int width = rows[0].Length;
        int height = rows.Length;
        Color32[] pixels = new Color32[width * height];
        Color32 opaque = new(255, 255, 255, 255);
        for (int topY = 0; topY < height; topY++)
        {
            int sourceY = height - 1 - topY;
            for (int x = 0; x < width; x++)
                pixels[sourceY * width + x] = rows[topY][x] == '#' ? opaque : default;
        }

        return BuildIcon(width, height, pixels, preserveSourceColor: false, "fallback:Kill_Slugcat");
    }

    private static string ResolveCustomIconPath(string slugcatId)
    {
        string fileName = slugcatId + "_icon.png";
        string[] relativeDirectories = { "atlas", "atlases" };

        for (int i = 0; i < relativeDirectories.Length; i++)
        {
            string resolved = AssetManager.ResolveFilePath(relativeDirectories[i] + "/" + fileName);
            if (File.Exists(resolved)) return resolved;
        }

        // ResolveFilePath lower-cases its relative argument. Rain World conventionally uses
        // lower-case assets, but some Windows mods (including Ancient Site's Karmar_icon.png)
        // preserve mixed case. Scan only the two atlas directories once per character so those
        // assets also work and Linux-friendly lower-case mods retain normal fast-path behavior.
        for (int modIndex = ModManager.ActiveMods.Count - 1; modIndex >= 0; modIndex--)
        {
            ModManager.Mod mod = ModManager.ActiveMods[modIndex];
            if (mod == null) continue;

            List<string> roots = new(3);
            if (mod.hasTargetedVersionFolder) roots.Add(mod.TargetedPath);
            if (mod.hasNewestFolder) roots.Add(mod.NewestPath);
            roots.Add(mod.path);

            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                for (int dirIndex = 0; dirIndex < relativeDirectories.Length; dirIndex++)
                {
                    string directory = Path.Combine(roots[rootIndex], relativeDirectories[dirIndex]);
                    if (!Directory.Exists(directory)) continue;

                    try
                    {
                        string[] files = Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly);
                        for (int f = 0; f < files.Length; f++)
                        {
                            if (string.Equals(Path.GetFileName(files[f]), fileName, StringComparison.OrdinalIgnoreCase))
                                return files[f];
                        }
                    }
                    catch (IOException)
                    {
                        // A mod directory can disappear during hot reload. The normal fallback icon
                        // is preferable to failing the whole World Map render.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        return string.Empty;
    }

    private static void DrawMarker(
        ImDrawListPtr draw,
        Marker marker,
        Num.Vector2 center,
        float zoom,
        bool showPlayerNumber)
    {
        PixelIcon icon = marker.Icon;
        PixelRun[] runs = icon?.Runs ?? Array.Empty<PixelRun>();
        if (runs.Length == 0) return;

        float pixelSize = zoom < 0.35f ? 0.82f : zoom < 0.62f ? 0.92f : 1f;
        float width = icon.Width * pixelSize;
        float height = icon.Height * pixelSize;
        Num.Vector2 origin = center - new Num.Vector2(width, height) * 0.5f;
        float deadAlpha = marker.Dead ? 0.50f : 1f;

        // One-pixel black relief keeps white/yellow/custom icons readable over both bright room
        // raster and dark empty space without adding a large badge that hides terrain underneath.
        Num.Vector2 shadowOffset = new(Math.Max(1f, pixelSize), Math.Max(1f, pixelSize));
        for (int i = 0; i < runs.Length; i++)
        {
            PixelRun run = runs[i];
            float alpha = run.Color.a / 255f * deadAlpha;
            uint shadow = ImGui.GetColorU32(new Num.Vector4(0f, 0f, 0f, 0.72f * alpha));
            DrawRun(draw, origin + shadowOffset, run, pixelSize, shadow);
        }

        for (int i = 0; i < runs.Length; i++)
        {
            PixelRun run = runs[i];
            uint color = ResolveRunColor(marker, run, deadAlpha);
            DrawRun(draw, origin, run, pixelSize, color);
        }

        if (!showPlayerNumber) return;
        string label = "P" + (marker.PlayerNumber + 1);
        Num.Vector2 labelSize = ImGui.CalcTextSize(label);
        Num.Vector2 labelPos = new(center.X + width * 0.42f + 3f, center.Y - height * 0.5f - 2f);
        Num.Vector2 pad = new(3f, 1.5f);
        draw.AddRectFilled(labelPos - pad, labelPos + labelSize + pad, 0xCC000000u, 3f);
        draw.AddText(labelPos, ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    private static void DrawRun(ImDrawListPtr draw, Num.Vector2 origin, PixelRun run, float pixelSize, uint color)
    {
        Num.Vector2 min = new(origin.X + run.X * pixelSize, origin.Y + run.Y * pixelSize);
        Num.Vector2 max = new(min.X + run.Length * pixelSize, min.Y + pixelSize);
        draw.AddRectFilled(min, max, color);
    }

    private static uint ResolveRunColor(Marker marker, PixelRun run, float alphaMultiplier)
    {
        float alpha = run.Color.a / 255f * alphaMultiplier;
        if (marker.Icon.PreserveSourceColor)
        {
            return ImGui.GetColorU32(new Num.Vector4(
                run.Color.r / 255f,
                run.Color.g / 255f,
                run.Color.b / 255f,
                alpha));
        }

        return ImGui.GetColorU32(new Num.Vector4(marker.Tint.r, marker.Tint.g, marker.Tint.b, marker.Tint.a * alpha));
    }

    private static bool IsMarkerHovered(Marker marker, Num.Vector2 center, Num.Vector2 mouse, float zoom)
    {
        PixelIcon icon = marker.Icon;
        if (icon == null) return false;
        float pixelSize = zoom < 0.35f ? 0.82f : zoom < 0.62f ? 0.92f : 1f;
        Num.Vector2 half = new(
            Math.Max(8f, icon.Width * pixelSize * 0.5f + 3f),
            Math.Max(8f, icon.Height * pixelSize * 0.5f + 3f));
        return mouse.X >= center.X - half.X && mouse.X <= center.X + half.X &&
               mouse.Y >= center.Y - half.Y && mouse.Y <= center.Y + half.Y;
    }

    private static void DrawTooltip(Marker marker, EditorMapRoomSnapshot room)
    {
        ImGui.BeginTooltip();
        string role = string.IsNullOrWhiteSpace(marker.SlugcatId)
            ? DevToolUiSettings.T("未知角色", "Unknown slugcat")
            : marker.SlugcatId;
        ImGui.TextUnformatted("P" + (marker.PlayerNumber + 1) + " · " + role);
        ImGui.TextDisabled(room?.Name ?? marker.RoomIndex.ToString());
        if (marker.HasTilePosition)
            ImGui.TextDisabled($"{marker.TileX:0.0}, {marker.TileY:0.0}");
        if (marker.Dead)
            ImGui.TextDisabled(DevToolUiSettings.T("已死亡", "Dead"));
        ImGui.EndTooltip();
    }

    private static Num.Vector2 MultiplayerOffset(int playerNumber)
    {
        if (playerNumber <= 0) return Num.Vector2.Zero;
        int slot = (playerNumber - 1) % 6;
        float angle = (float)(slot * Math.PI / 3.0);
        return new Num.Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 4f;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static bool IsLayerVisible(int layer, bool[] visibility)
    {
        return visibility == null || layer < 0 || layer >= visibility.Length || visibility[layer];
    }

    private static bool PointNearCanvas(Num.Vector2 point, Num.Vector2 min, Num.Vector2 max, float margin)
    {
        return point.X >= min.X - margin && point.X <= max.X + margin &&
               point.Y >= min.Y - margin && point.Y <= max.Y + margin;
    }

    private static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }

    private static void DisposeHook(ref object hook)
    {
        try
        {
            (hook as IDisposable)?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }
}
