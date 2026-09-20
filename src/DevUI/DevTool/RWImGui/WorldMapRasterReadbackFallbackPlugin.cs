using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Supplies a detailed minimap raster when Futile's MapTex atlas is GPU-readable but not CPU-readable.
///
/// The normal geometry hub intentionally tries Texture2D.GetPixels first because it is cheap. Some
/// atlas/import configurations reject that CPU read even though the texture renders correctly. In
/// that case this compatibility layer copies only the room's atlas rectangle through a temporary
/// RenderTexture and feeds the resulting pixel classifications back into the normal presentation
/// snapshot. Results are cached by texture/UV identity, so the readback is a one-shot room bake.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapImGuiPresentationFallbackPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapRasterReadbackFallbackPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RasterReadbackFallback";
    public const string PluginName = "DryCycle DevTool World Map Raster Readback Fallback";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapRasterReadbackFallback.Enable(Logger);
    private void OnDisable() => WorldMapRasterReadbackFallback.Disable();
}

internal static class WorldMapRasterReadbackFallback
{
    private readonly struct RasterSource
    {
        internal RasterSource(Texture2D texture, int x, int y, int width, int height, int key)
        {
            Texture = texture;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Key = key;
        }

        internal Texture2D Texture { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int Key { get; }
    }

    private sealed class CachedRaster
    {
        internal int Key;
        internal EditorMapRectSnapshot[] Runs = Array.Empty<EditorMapRectSnapshot>();
    }

    private readonly struct PixelClass
    {
        internal PixelClass(EditorMapGeometryKind kind, bool water)
        {
            Kind = kind;
            Water = water;
        }

        internal EditorMapGeometryKind Kind { get; }
        internal bool Water { get; }
    }

    private static readonly Dictionary<int, CachedRaster> cache = new();
    private static readonly HashSet<int> loggedFailures = new();

    private static ManualLogSource log;
    private static bool enabled;
    private static string cachedRegion = string.Empty;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        WorldMapFrontendBridge.RegisterRasterEnhancer(Enhance);
        logger?.LogInfo("World Map GPU raster readback fallback enabled through direct geometry fallback; no self-detour attached.");
    }

    internal static void Disable()
    {
        WorldMapFrontendBridge.UnregisterRasterEnhancer(Enhance);
        cache.Clear();
        loggedFailures.Clear();
        cachedRegion = string.Empty;
        enabled = false;
        log = null;
    }

    internal static EditorMapRoomVisualSnapshot Enhance(int roomIndex, EditorMapRoomVisualSnapshot original)
    {
        original ??= EditorMapRoomVisualSnapshot.Empty;
        if (!enabled || original.DetailedRasterAvailable) return original;

        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map || session.Owner?.activePage is not DevInterface.MapPage page)
            return original;

        string region = page.world?.name ?? string.Empty;
        if (!string.Equals(region, cachedRegion, StringComparison.OrdinalIgnoreCase))
        {
            cache.Clear();
            loggedFailures.Clear();
            cachedRegion = region;
        }

        if (!WorldMapLegacyRoomSourceService.TryGetRoomTexture(
                page,
                roomIndex,
                out WorldMapLegacyRoomSourceService.RoomTextureSource textureSource) ||
            !TryGetRasterSource(textureSource, out RasterSource source))
            return original;

        if (!cache.TryGetValue(roomIndex, out CachedRaster cached) || cached.Key != source.Key)
        {
            if (!TryReadPixels(source, out Color[] pixels))
            {
                if (loggedFailures.Add(roomIndex))
                    log?.LogDebug("World Map raster readback failed for room " + roomIndex + ".");
                return original;
            }

            cached = new CachedRaster
            {
                Key = source.Key,
                Runs = BuildRuns(pixels, source.Width, source.Height)
            };
            cache[roomIndex] = cached;
            loggedFailures.Remove(roomIndex);
        }

        return new EditorMapRoomVisualSnapshot
        {
            Available = true,
            DetailedRasterAvailable = true,
            WidthTiles = Math.Max(1f, source.Width),
            HeightTiles = Math.Max(1f, source.Height),
            RasterRuns = cached.Runs,
            Curves = original.Curves ?? Array.Empty<EditorMapPolylineSnapshot>(),
            Nodes = original.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>()
        };
    }

    private static bool TryGetRasterSource(
        WorldMapLegacyRoomSourceService.RoomTextureSource roomSource,
        out RasterSource source)
    {
        source = default;
        try
        {
            Texture2D texture = roomSource.Texture;
            if (texture == null) return false;

            Rect uv = roomSource.Uv;
            int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * texture.width), 0, Math.Max(0, texture.width - 1));
            int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * texture.height), 0, Math.Max(0, texture.height - 1));
            int width = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Abs(uv.width) * texture.width),
                1,
                Math.Max(1, texture.width - x));
            int height = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Abs(uv.height) * texture.height),
                1,
                Math.Max(1, texture.height - y));

            unchecked
            {
                int key = roomSource.Signature;
                key = key * 397 ^ x;
                key = key * 397 ^ y;
                key = key * 397 ^ width;
                key = key * 397 ^ height;
                source = new RasterSource(texture, x, y, width, height, key);
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
        if (source.Texture == null || source.Width <= 0 || source.Height <= 0) return false;

        // Use the zero-copy CPU path whenever Unity kept this texture readable.
        try
        {
            pixels = source.X == 0 && source.Y == 0 &&
                     source.Width == source.Texture.width && source.Height == source.Texture.height
                ? source.Texture.GetPixels()
                : source.Texture.GetPixels(source.X, source.Y, source.Width, source.Height);
            if (pixels != null && pixels.Length == source.Width * source.Height)
                return true;
        }
        catch
        {
            pixels = null;
        }

        // Non-readable Futile atlas: sample only this room's UV rectangle on the GPU, then read the
        // small temporary target. This occurs once per source identity and is not part of steady-state
        // panning/zooming.
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = null;
        Texture2D readable = null;
        try
        {
            target = RenderTexture.GetTemporary(
                source.Width,
                source.Height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            target.filterMode = FilterMode.Point;

            Vector2 scale = new(
                (float)source.Width / Math.Max(1, source.Texture.width),
                (float)source.Height / Math.Max(1, source.Texture.height));
            Vector2 offset = new(
                (float)source.X / Math.Max(1, source.Texture.width),
                (float)source.Y / Math.Max(1, source.Texture.height));
            Graphics.Blit(source.Texture, target, scale, offset);

            RenderTexture.active = target;
            readable = new Texture2D(source.Width, source.Height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0f, 0f, source.Width, source.Height), 0, 0, false);
            readable.Apply(false, false);
            pixels = readable.GetPixels();
            return pixels != null && pixels.Length == source.Width * source.Height;
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map GPU readback failed: " + error.Message);
            pixels = null;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (readable != null) UnityEngine.Object.Destroy(readable);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static EditorMapRectSnapshot[] BuildRuns(Color[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length != width * height)
            return Array.Empty<EditorMapRectSnapshot>();

        PixelClass[] classes = new PixelClass[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) classes[i] = ClassifyPixel(pixels[i]);

        List<EditorMapRectSnapshot> runs = new();
        List<EditorMapRectSnapshot> waterRuns = new();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                EditorMapGeometryKind kind = classes[y * width + x].Kind;
                int start = x++;
                while (x < width && classes[y * width + x].Kind == kind) x++;
                runs.Add(new EditorMapRectSnapshot(start, y, x - start, 1f, kind));
            }

            x = 0;
            while (x < width)
            {
                if (!classes[y * width + x].Water)
                {
                    x++;
                    continue;
                }
                int start = x++;
                while (x < width && classes[y * width + x].Water) x++;
                waterRuns.Add(new EditorMapRectSnapshot(start, y, x - start, 1f, EditorMapGeometryKind.Water));
            }
        }

        runs.AddRange(waterRuns);
        return runs.ToArray();
    }

    private static PixelClass ClassifyPixel(Color color)
    {
        if (TryClassifyVanilla(color, out EditorMapGeometryKind kind))
            return new PixelClass(kind, false);

        if (color.b >= 0.28f)
        {
            Color unblended = new(
                Mathf.Clamp01(color.r / 0.7f),
                Mathf.Clamp01(color.g / 0.7f),
                Mathf.Clamp01((color.b - 0.3f) / 0.7f));
            if (TryClassifyVanilla(unblended, out kind))
                return new PixelClass(kind, true);
        }

        float max = Math.Max(color.r, Math.Max(color.g, color.b));
        float min = Math.Min(color.r, Math.Min(color.g, color.b));
        if (max - min < 0.08f)
        {
            if (color.r < 0.40f) return new PixelClass(EditorMapGeometryKind.Solid, false);
            if (color.r < 0.55f) return new PixelClass(EditorMapGeometryKind.BackWall, false);
            return new PixelClass(EditorMapGeometryKind.Air, false);
        }
        return new PixelClass(EditorMapGeometryKind.Structure, false);
    }

    private static bool TryClassifyVanilla(Color color, out EditorMapGeometryKind kind)
    {
        const float tolerance = 0.065f;
        if (Near(color, 0f, 1f, 0.2f, tolerance) || Near(color, 1f, 0f, 1f, tolerance) ||
            Near(color, 1f, 1f, 1f, tolerance))
        {
            kind = EditorMapGeometryKind.Shortcut;
            return true;
        }
        if (Near(color, 0.7f, 0f, 0f, tolerance) || Near(color, 0f, 0f, 0f, tolerance))
        {
            kind = EditorMapGeometryKind.Transport;
            return true;
        }
        if (Near(color, 0.3f, 0.3f, 0.3f, tolerance))
        {
            kind = EditorMapGeometryKind.Solid;
            return true;
        }
        if (Near(color, 0.5f, 0.5f, 0.5f, tolerance))
        {
            kind = EditorMapGeometryKind.BackWall;
            return true;
        }
        if (Near(color, 0.6f, 0.6f, 0.6f, tolerance))
        {
            kind = EditorMapGeometryKind.Air;
            return true;
        }
        if (Near(color, 0.5f, 0.3f, 0.3f, tolerance))
        {
            kind = EditorMapGeometryKind.Structure;
            return true;
        }
        kind = default;
        return false;
    }

    private static bool Near(Color color, float r, float g, float b, float tolerance) =>
        Math.Abs(color.r - r) <= tolerance &&
        Math.Abs(color.g - g) <= tolerance &&
        Math.Abs(color.b - b) <= tolerance;

}
