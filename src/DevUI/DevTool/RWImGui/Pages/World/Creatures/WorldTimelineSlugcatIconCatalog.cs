using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class WorldTimelineSlugcatIconCatalog
{
    private readonly struct PixelRun
    {
        internal PixelRun(int x, int y, int width, Color32 color)
        {
            X = x;
            Y = y;
            Width = width;
            Color = color;
        }

        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal Color32 Color { get; }
    }

    private sealed class Raster
    {
        internal int Width = 1;
        internal int Height = 1;
        internal PixelRun[] Runs = Array.Empty<PixelRun>();
        internal Color Tint = Color.white;
    }

    private sealed class Slot
    {
        internal Raster Raster;
        internal bool Queued;
    }

    private const int MaxRasterDimension = 36;
    private const int MaxBuildsPerFrame = 3;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Requests = new();
    private static readonly Dictionary<string, string> ModIconAtlasById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadedModAtlases = new(StringComparer.Ordinal);

    private static ManualLogSource log;
    private static bool initialized;
    private static bool modIndexReady;
    private static int observedActiveModCount = -1;
    private static Raster originalSlugcat;

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        initialized = true;
        modIndexReady = false;
        observedActiveModCount = -1;
    }

    internal static void Shutdown()
    {
        initialized = false;

        if (Futile.atlasManager != null)
        {
            foreach (string atlasName in LoadedModAtlases)
            {
                try
                {
                    if (Futile.atlasManager.DoesContainAtlas(atlasName))
                        Futile.atlasManager.UnloadImage(atlasName);
                }
                catch
                {
                }
            }
        }

        lock (Sync)
        {
            Slots.Clear();
            Requests.Clear();
        }

        ModIconAtlasById.Clear();
        LoadedModAtlases.Clear();
        originalSlugcat = null;
        modIndexReady = false;
        observedActiveModCount = -1;
        log = null;
    }

    internal static void PumpMainThread()
    {
        if (!initialized || Futile.atlasManager == null)
            return;

        int activeModCount = ModManager.ActiveMods?.Count ?? 0;
        if (!modIndexReady || activeModCount != observedActiveModCount)
            RebuildModIndex(activeModCount);

        if (originalSlugcat == null)
            TryBuildOriginal();

        for (int i = 0; i < MaxBuildsPerFrame; i++)
        {
            string id = Dequeue();
            if (id == null)
                break;

            Raster raster = BuildFor(id);
            if (raster == null)
            {
                Requeue(id);
                break;
            }

            lock (Sync)
            {
                if (!Slots.TryGetValue(id, out Slot slot))
                {
                    slot = new Slot();
                    Slots[id] = slot;
                }

                slot.Raster = raster;
                slot.Queued = false;
            }
        }
    }

    internal static unsafe bool Draw(ImDrawListPtr draw, string id, Num.Vector2 pos, float size)
    {
        if (draw.NativePtr == null || string.IsNullOrWhiteSpace(id) || size <= 0f)
            return false;

        Raster raster;
        lock (Sync)
        {
            if (!Slots.TryGetValue(id, out Slot slot))
            {
                slot = new Slot { Queued = true };
                Slots[id] = slot;
                Requests.Enqueue(id);
            }
            else if (slot.Raster == null && !slot.Queued)
            {
                slot.Queued = true;
                Requests.Enqueue(id);
            }

            raster = slot.Raster;
        }

        if (raster == null)
            return false;

        DrawRaster(draw, raster, pos, new Num.Vector2(size, size));
        return true;
    }

    private static void RebuildModIndex(int activeModCount)
    {
        ModIconAtlasById.Clear();
        observedActiveModCount = activeModCount;
        modIndexReady = true;

        try
        {
            string[] files = AssetManager.ListDirectory(
                "atlas",
                directories: false,
                includeAll: true,
                moddedOnly: true);

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i] ?? string.Empty;
                if (!file.EndsWith("_icon.png", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || name.Length <= 5)
                    continue;

                string id = name.Substring(0, name.Length - 5);
                if (string.IsNullOrWhiteSpace(id) || ModIconAtlasById.ContainsKey(id))
                    continue;

                ModIconAtlasById[id] = "atlas/" + name;
            }
        }
        catch (Exception error)
        {
            log?.LogWarning("Timeline slugcat icon index failed: " + error.Message);
        }

        lock (Sync)
        {
            Slots.Clear();
            Requests.Clear();
        }
    }

    private static Raster BuildFor(string id)
    {
        if (TryBuildModIcon(id, out Raster mod))
            return mod;

        if (originalSlugcat == null && !TryBuildOriginal())
            return null;

        return new Raster
        {
            Width = originalSlugcat.Width,
            Height = originalSlugcat.Height,
            Runs = originalSlugcat.Runs,
            Tint = ResolveOriginalColor(id)
        };
    }

    private static bool TryBuildModIcon(string id, out Raster raster)
    {
        raster = null;
        if (!ModIconAtlasById.TryGetValue(id, out string atlasName))
            return false;

        try
        {
            FAtlasElement element = null;
            if (Futile.atlasManager.DoesContainElementWithName(atlasName))
            {
                element = Futile.atlasManager.GetElementWithName(atlasName);
            }
            else
            {
                bool loadedHere = !Futile.atlasManager.DoesContainAtlas(atlasName);
                FAtlas atlas = Futile.atlasManager.LoadImage(atlasName);
                if (loadedHere)
                    LoadedModAtlases.Add(atlasName);

                if (atlas?.elements != null && atlas.elements.Count > 0)
                    element = atlas.elements[0];
            }

            raster = element == null ? null : BuildRaster(element, Color.white);
            return raster != null;
        }
        catch (Exception error)
        {
            log?.LogWarning("Timeline mod icon failed for '" + id + "': " + error.Message);
            return false;
        }
    }

    private static bool TryBuildOriginal()
    {
        if (originalSlugcat != null)
            return true;
        if (Futile.atlasManager == null ||
            !Futile.atlasManager.DoesContainElementWithName("Kill_Slugcat"))
            return false;

        try
        {
            originalSlugcat = BuildRaster(
                Futile.atlasManager.GetElementWithName("Kill_Slugcat"),
                Color.white);
            return originalSlugcat != null;
        }
        catch (Exception error)
        {
            log?.LogWarning("Original Kill_Slugcat icon could not be prepared: " + error.Message);
            return false;
        }
    }

    private static Raster BuildRaster(FAtlasElement element, Color tint)
    {
        Texture2D texture = element?.atlas?.texture as Texture2D;
        if (texture == null)
            return null;

        Rect uv = element.uvRect;
        float minU = Math.Min(uv.xMin, uv.xMax);
        float minV = Math.Min(uv.yMin, uv.yMax);
        int x = Mathf.Clamp(Mathf.RoundToInt(minU * texture.width), 0, Math.Max(0, texture.width - 1));
        int y = Mathf.Clamp(Mathf.RoundToInt(minV * texture.height), 0, Math.Max(0, texture.height - 1));
        int width = Mathf.Clamp(
            Mathf.RoundToInt(Math.Abs(uv.width) * texture.width),
            1,
            Math.Max(1, texture.width - x));
        int height = Mathf.Clamp(
            Mathf.RoundToInt(Math.Abs(uv.height) * texture.height),
            1,
            Math.Max(1, texture.height - y));

        if (!TryReadRegion(texture, x, y, width, height, out Color32[] pixels))
            return null;

        Downsample(ref pixels, ref width, ref height);
        PixelRun[] runs = BuildRuns(pixels, width, height);
        if (runs.Length == 0)
            return null;

        return new Raster
        {
            Width = width,
            Height = height,
            Runs = runs,
            Tint = tint
        };
    }

    private static bool TryReadRegion(
        Texture2D texture,
        int x,
        int y,
        int width,
        int height,
        out Color32[] region)
    {
        region = null;

        try
        {
            Color32[] source = texture.GetPixels32();
            if (source != null && source.Length == texture.width * texture.height)
            {
                region = CopyRegion(source, texture.width, texture.height, x, y, width, height);
                if (region != null)
                    return true;
            }
        }
        catch
        {
        }

        return TryReadGpuRegion(texture, x, y, width, height, out region);
    }

    private static Color32[] CopyRegion(
        Color32[] source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height)
    {
        if (source == null ||
            sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            width <= 0 ||
            height <= 0 ||
            x < 0 ||
            y < 0 ||
            x + width > sourceWidth ||
            y + height > sourceHeight)
            return null;

        Color32[] result = new Color32[width * height];
        for (int row = 0; row < height; row++)
            Array.Copy(source, (y + row) * sourceWidth + x, result, row * width, width);
        return result;
    }

    private static bool TryReadGpuRegion(
        Texture2D texture,
        int x,
        int y,
        int width,
        int height,
        out Color32[] pixels)
    {
        pixels = null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = null;
        Texture2D readable = null;

        try
        {
            target = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            target.filterMode = FilterMode.Point;

            Vector2 scale = new((float)width / texture.width, (float)height / texture.height);
            Vector2 offset = new((float)x / texture.width, (float)y / texture.height);
            Graphics.Blit(texture, target, scale, offset);

            RenderTexture.active = target;
            readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readable.Apply(false, false);
            pixels = readable.GetPixels32();
            return pixels != null && pixels.Length == width * height;
        }
        catch
        {
            pixels = null;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (readable != null)
                UnityEngine.Object.Destroy(readable);
            if (target != null)
                RenderTexture.ReleaseTemporary(target);
        }
    }

    private static void Downsample(ref Color32[] pixels, ref int width, ref int height)
    {
        int max = Math.Max(width, height);
        if (pixels == null || max <= MaxRasterDimension)
            return;

        float scale = (float)MaxRasterDimension / max;
        int nextWidth = Math.Max(1, Mathf.RoundToInt(width * scale));
        int nextHeight = Math.Max(1, Mathf.RoundToInt(height * scale));
        Color32[] next = new Color32[nextWidth * nextHeight];

        for (int yy = 0; yy < nextHeight; yy++)
        {
            int sourceY = Math.Min(height - 1, (int)((long)yy * height / nextHeight));
            for (int xx = 0; xx < nextWidth; xx++)
            {
                int sourceX = Math.Min(width - 1, (int)((long)xx * width / nextWidth));
                next[yy * nextWidth + xx] = pixels[sourceY * width + sourceX];
            }
        }

        pixels = next;
        width = nextWidth;
        height = nextHeight;
    }

    private static PixelRun[] BuildRuns(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length != width * height)
            return Array.Empty<PixelRun>();

        List<PixelRun> runs = new();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                Color32 color = pixels[y * width + x];
                if (color.a < 12)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width)
                {
                    Color32 next = pixels[y * width + x];
                    if (next.a < 12 || !Similar(color, next))
                        break;
                    x++;
                }

                runs.Add(new PixelRun(start, y, x - start, color));
            }
        }

        return runs.ToArray();
    }

    private static bool Similar(Color32 a, Color32 b) =>
        Math.Abs(a.r - b.r) <= 10 &&
        Math.Abs(a.g - b.g) <= 10 &&
        Math.Abs(a.b - b.b) <= 10 &&
        Math.Abs(a.a - b.a) <= 14;

    private static void DrawRaster(ImDrawListPtr draw, Raster raster, Num.Vector2 areaPos, Num.Vector2 areaSize)
    {
        float scale = Math.Min(
            areaSize.X / Math.Max(1, raster.Width),
            areaSize.Y / Math.Max(1, raster.Height));
        Num.Vector2 origin = areaPos + new Num.Vector2(
            (areaSize.X - raster.Width * scale) * 0.5f,
            (areaSize.Y - raster.Height * scale) * 0.5f);

        for (int i = 0; i < raster.Runs.Length; i++)
        {
            PixelRun run = raster.Runs[i];
            float alpha = run.Color.a / 255f;
            if (alpha <= 0.02f)
                continue;

            Num.Vector4 color = new(
                raster.Tint.r * run.Color.r / 255f,
                raster.Tint.g * run.Color.g / 255f,
                raster.Tint.b * run.Color.b / 255f,
                raster.Tint.a * alpha);
            float y = raster.Height - 1 - run.Y;
            Num.Vector2 a = origin + new Num.Vector2(run.X * scale, y * scale);
            Num.Vector2 b = a + new Num.Vector2(run.Width * scale, scale);
            draw.AddRectFilled(a, b, ImGui.GetColorU32(color));
        }
    }

    private static Color ResolveOriginalColor(string value)
    {
        string id = (value ?? string.Empty).Trim();
        if (id.Length == 0)
            return Color.white;

        if (string.Equals(id, "Inv", StringComparison.OrdinalIgnoreCase))
            id = "Sofanthiel";
        else if (string.Equals(id, "Survivor", StringComparison.OrdinalIgnoreCase))
            id = "White";
        else if (string.Equals(id, "Monk", StringComparison.OrdinalIgnoreCase))
            id = "Yellow";
        else if (string.Equals(id, "Hunter", StringComparison.OrdinalIgnoreCase))
            id = "Red";
        else if (string.Equals(id, "Spearmaster", StringComparison.OrdinalIgnoreCase))
            id = "Spear";

        List<string> names = ExtEnum<SlugcatStats.Name>.values.entries;
        for (int i = 0; i < names.Count; i++)
        {
            if (!string.Equals(names[i], id, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return PlayerGraphics.DefaultSlugcatColor(
                    new SlugcatStats.Name(names[i], register: false));
            }
            catch
            {
                return Color.white;
            }
        }

        return Color.white;
    }

    private static string Dequeue()
    {
        lock (Sync)
        {
            while (Requests.Count > 0)
            {
                string id = Requests.Dequeue();
                if (!Slots.TryGetValue(id, out Slot slot))
                    continue;

                slot.Queued = false;
                if (slot.Raster != null)
                    continue;

                return id;
            }
        }

        return null;
    }

    private static void Requeue(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (Sync)
        {
            if (!Slots.TryGetValue(id, out Slot slot))
            {
                slot = new Slot();
                Slots[id] = slot;
            }

            if (slot.Raster != null || slot.Queued)
                return;

            slot.Queued = true;
            Requests.Enqueue(id);
        }
    }
}
