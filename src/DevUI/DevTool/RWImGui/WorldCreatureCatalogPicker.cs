using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared creature picker for world spawners and lineage stages.
///
/// The picker deliberately uses Rain World's own CreatureSymbol/Sandbox artwork instead of a
/// parallel DryCycle icon set. Futile atlas pixels are read once (CPU path first, tiny GPU readback
/// fallback second), converted into cached horizontal runs, and then emitted through ImGui's draw
/// list. This avoids depending on RWImGui's DX11 texture binding internals while preserving the
/// actual Sandbox icon and mod hooks into CreatureSymbol.
/// </summary>
internal static class WorldCreatureCatalogPicker
{
    private sealed class CreatureEntry
    {
        internal string Id = string.Empty;
        internal CreatureTemplate.Type Type;
        internal string SourceKey = string.Empty;
        internal string SourceLabel = string.Empty;
    }

    private sealed class SourceGroup
    {
        internal string Key = string.Empty;
        internal string Label = string.Empty;
        internal readonly List<CreatureEntry> Entries = new();
    }

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

    private sealed class IconRaster
    {
        internal string SpriteName = string.Empty;
        internal int Width = 1;
        internal int Height = 1;
        internal Color Tint = Color.white;
        internal PixelRun[] Runs = Array.Empty<PixelRun>();
        internal bool Available;
    }

    private static readonly List<CreatureEntry> entries = new();
    private static readonly List<SourceGroup> groups = new();
    private static readonly Dictionary<string, IconRaster> iconCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> searchByPicker = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> sourceByCreature = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> sourceLabelByKey = new(StringComparer.OrdinalIgnoreCase);

    private static int catalogCount = -1;
    private static int pluginCount = -1;

    internal static bool DrawSelector(
        string widgetId,
        ref string creatureId,
        bool allowNone = false,
        string label = null)
    {
        RefreshCatalog();

        string popupId = "##CreatureCatalogPicker_" + widgetId;
        string current = string.IsNullOrWhiteSpace(creatureId)
            ? DevToolUiSettings.T("选择生物…", "Choose creature…")
            : creatureId;
        string buttonLabel = string.IsNullOrWhiteSpace(label)
            ? DevToolUiSettings.T("选择生物", "Choose Creature") + "  ·  " + current
            : label + "  ·  " + current;

        bool changed = false;
        if (DevToolWidgets.ActionButton(buttonLabel, "CreatureCatalogOpen_" + widgetId, DevToolButtonTone.Normal, true))
        {
            searchByPicker[popupId] = string.Empty;
            ImGui.OpenPopup(popupId);
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 desired = new(
            Math.Min(900f, Math.Max(560f, io.DisplaySize.X * 0.58f)),
            Math.Min(720f, Math.Max(430f, io.DisplaySize.Y * 0.72f)));
        ImGui.SetNextWindowSize(desired, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(520f, io.DisplaySize.X - 24f), Math.Min(360f, io.DisplaySize.Y - 24f)),
            new Num.Vector2(Math.Max(520f, io.DisplaySize.X - 20f), Math.Max(360f, io.DisplaySize.Y - 20f)));

        if (!ImGui.BeginPopup(popupId)) return false;

        DevToolWidgets.PaneTitle(DevToolUiSettings.T("生物图鉴", "CREATURE CATALOG"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "点击 Sandbox 图标选择生物；按来源分组。",
                "Choose a creature by its Sandbox icon; entries are grouped by source."),
            true);

        if (!searchByPicker.TryGetValue(popupId, out string search)) search = string.Empty;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint(
                "##CreatureCatalogSearch_" + widgetId,
                DevToolUiSettings.T("搜索生物 ID 或 Mod…", "Search creature ID or mod…"),
                ref search,
                160))
            searchByPicker[popupId] = search;

        ImGui.Spacing();
        if (ImGui.BeginChild("##CreatureCatalogScroll_" + widgetId, new Num.Vector2(0f, 0f), ImGuiChildFlags.None))
        {
            if (allowNone)
            {
                DevToolSourceMark special = DevToolSourcePresentation.FromLabel(DevToolUiSettings.T("Lineage 特殊项", "LINEAGE SPECIAL"));
                DevToolSourcePresentation.DrawHeader(special, 1.24f, 1f);
                bool selectedNone = string.Equals(creatureId, "NONE", StringComparison.OrdinalIgnoreCase);
                if (DrawNoneCard("None_" + widgetId, selectedNone))
                {
                    creatureId = "NONE";
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Spacing();
            }

            int visibleCount = 0;
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                SourceGroup group = groups[groupIndex];
                int matching = CountMatching(group, search);
                if (matching == 0) continue;
                visibleCount += matching;

                DevToolSourceMark mark = DevToolSourcePresentation.FromLabel(group.Label);
                DevToolSourcePresentation.DrawHeader(mark, 1.36f, 1f);
                if (DrawGroupGrid(group, search, widgetId, creatureId, out string selected))
                {
                    creatureId = selected;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                    break;
                }
                ImGui.Spacing();
            }

            if (visibleCount == 0)
                DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的生物。", "No matching creatures."), true);

            // Lossless world editing: an entry from a disabled/missing mod must remain selectable as
            // the current value instead of silently being replaced merely because its ExtEnum is no
            // longer registered in this run.
            if (!string.IsNullOrWhiteSpace(creatureId) &&
                !string.Equals(creatureId, "NONE", StringComparison.OrdinalIgnoreCase) &&
                FindEntry(creatureId) == null)
            {
                DevToolSourcePresentation.DrawHeader(
                    DevToolSourcePresentation.FromLabel(DevToolUiSettings.T("缺失 Mod", "MISSING MOD")),
                    1.28f,
                    1f);
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T(
                        "当前 world.txt 使用的生物未在本次运行注册；保留原值，直到你选择替代项。",
                        "The creature stored in world.txt is not registered in this run; it is preserved until you choose a replacement."),
                    true);
                DrawMissingCard(creatureId);
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
        return changed;
    }

    private static bool DrawGroupGrid(
        SourceGroup group,
        string search,
        string widgetId,
        string current,
        out string selected)
    {
        selected = null;
        float cardWidth = 112f;
        float cardHeight = 102f;
        float spacing = Math.Max(6f, ImGui.GetStyle().ItemSpacing.X);
        float available = Math.Max(cardWidth, ImGui.GetContentRegionAvail().X);
        int columns = Math.Max(1, (int)Math.Floor((available + spacing) / (cardWidth + spacing)));
        int column = 0;

        for (int i = 0; i < group.Entries.Count; i++)
        {
            CreatureEntry entry = group.Entries[i];
            if (!Matches(entry, group, search)) continue;
            if (column > 0) ImGui.SameLine(0f, spacing);

            bool isSelected = string.Equals(current, entry.Id, StringComparison.Ordinal);
            if (DrawCreatureCard(entry, widgetId + "_" + group.Key + "_" + i, cardWidth, cardHeight, isSelected))
            {
                selected = entry.Id;
                return true;
            }

            column++;
            if (column >= columns) column = 0;
        }
        return false;
    }

    private static bool DrawCreatureCard(
        CreatureEntry entry,
        string cardId,
        float width,
        float height,
        bool selected)
    {
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##CreatureCard_" + cardId, new Num.Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        Num.Vector4 bg = selected
            ? new Num.Vector4(0.16f, 0.32f, 0.52f, 0.92f)
            : hovered
                ? new Num.Vector4(0.18f, 0.21f, 0.27f, 0.94f)
                : new Num.Vector4(0.10f, 0.12f, 0.16f, 0.82f);
        Num.Vector4 border = selected
            ? new Num.Vector4(0.34f, 0.66f, 1f, 1f)
            : hovered
                ? new Num.Vector4(0.48f, 0.55f, 0.66f, 0.95f)
                : new Num.Vector4(0.28f, 0.31f, 0.37f, 0.80f);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(bg), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(border), 5f, ImDrawFlags.None, selected ? 2.0f : 1f);

        IconRaster icon = GetIcon(entry);
        DrawIcon(draw, icon, pos + new Num.Vector2(8f, 7f), new Num.Vector2(width - 16f, 65f));
        DrawCenteredId(draw, entry.Id, pos, width, height - 26f);

        if (hovered)
            DevToolTooltip.Show(entry.SourceLabel + "\n" + entry.Id + "\n" + (icon.Available ? icon.SpriteName : DevToolUiSettings.T("Sandbox 图标缺失", "Sandbox icon unavailable")));
        return clicked;
    }

    private static bool DrawNoneCard(string id, bool selected)
    {
        const float width = 112f;
        const float height = 92f;
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##LineageNoneCard_" + id, new Num.Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(selected
            ? new Num.Vector4(0.16f, 0.32f, 0.52f, 0.92f)
            : new Num.Vector4(0.10f, 0.12f, 0.16f, hovered ? 0.96f : 0.82f)), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(selected
            ? new Num.Vector4(0.34f, 0.66f, 1f, 1f)
            : new Num.Vector4(0.34f, 0.38f, 0.45f, 0.86f)), 5f, ImDrawFlags.None, selected ? 2f : 1f);
        Num.Vector2 center = pos + new Num.Vector2(width * 0.5f, 35f);
        uint muted = ImGui.GetColorU32(new Num.Vector4(0.56f, 0.60f, 0.68f, 1f));
        draw.AddCircle(center, 18f, muted, 28, 2f);
        draw.AddLine(center + new Num.Vector2(-13f, 13f), center + new Num.Vector2(13f, -13f), muted, 2f);
        DrawCenteredId(draw, "NONE", pos, width, height - 23f);
        return clicked;
    }

    private static void DrawMissingCard(string id)
    {
        const float width = 180f;
        const float height = 76f;
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Num.Vector2(width, height));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(new Num.Vector4(0.30f, 0.14f, 0.08f, 0.78f)), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(new Num.Vector4(0.94f, 0.45f, 0.22f, 0.95f)), 5f);
        DrawCenteredId(draw, id, pos, width, height - 27f);
    }

    private static void DrawIcon(ImDrawListPtr draw, IconRaster icon, Num.Vector2 areaPos, Num.Vector2 areaSize)
    {
        if (icon == null || !icon.Available || icon.Runs.Length == 0)
        {
            Num.Vector2 c = areaPos + areaSize * 0.5f;
            uint color = ImGui.GetColorU32(new Num.Vector4(0.55f, 0.58f, 0.64f, 0.85f));
            draw.AddCircle(c, Math.Min(areaSize.X, areaSize.Y) * 0.24f, color, 24, 1.6f);
            draw.AddText(c - new Num.Vector2(3.5f, 8f), color, "?");
            return;
        }

        float scale = Math.Min(areaSize.X / Math.Max(1, icon.Width), areaSize.Y / Math.Max(1, icon.Height));
        scale = Math.Max(1f, Math.Min(scale, 4f));
        Num.Vector2 origin = areaPos + new Num.Vector2(
            (areaSize.X - icon.Width * scale) * 0.5f,
            (areaSize.Y - icon.Height * scale) * 0.5f);

        for (int i = 0; i < icon.Runs.Length; i++)
        {
            PixelRun run = icon.Runs[i];
            float alpha = run.Color.a / 255f;
            if (alpha <= 0.02f) continue;
            float sourceR = run.Color.r / 255f;
            float sourceG = run.Color.g / 255f;
            float sourceB = run.Color.b / 255f;
            Num.Vector4 color = new(
                icon.Tint.r * sourceR,
                icon.Tint.g * sourceG,
                icon.Tint.b * sourceB,
                icon.Tint.a * alpha);
            float y = icon.Height - 1 - run.Y;
            Num.Vector2 a = origin + new Num.Vector2(run.X * scale, y * scale);
            Num.Vector2 b = a + new Num.Vector2(run.Width * scale, scale);
            draw.AddRectFilled(a, b, ImGui.GetColorU32(color));
        }
    }

    private static void DrawCenteredId(ImDrawListPtr draw, string id, Num.Vector2 cardPos, float cardWidth, float y)
    {
        string shown = FitText(id ?? string.Empty, cardWidth - 10f);
        Num.Vector2 size = ImGui.CalcTextSize(shown);
        draw.AddText(
            new Num.Vector2(cardPos.X + Math.Max(5f, (cardWidth - size.X) * 0.5f), cardPos.Y + y),
            ImGui.GetColorU32(ImGuiCol.Text),
            shown);
    }

    private static string FitText(string value, float maxWidth)
    {
        if (ImGui.CalcTextSize(value).X <= maxWidth) return value;
        const string ellipsis = "…";
        int length = value.Length;
        while (length > 2)
        {
            string candidate = value.Substring(0, --length) + ellipsis;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    private static IconRaster GetIcon(CreatureEntry entry)
    {
        if (entry == null || entry.Type == null) return new IconRaster();
        if (iconCache.TryGetValue(entry.Id, out IconRaster cached)) return cached;

        IconRaster raster = new();
        try
        {
            IconSymbol.IconSymbolData data = ResolveSandboxSymbol(entry.Type);
            raster.SpriteName = CreatureSymbol.SpriteNameOfCreature(data) ?? string.Empty;
            raster.Tint = CreatureSymbol.ColorOfCreature(data);
            if (string.IsNullOrEmpty(raster.SpriteName) ||
                string.Equals(raster.SpriteName, "Futile_White", StringComparison.Ordinal) ||
                Futile.atlasManager == null ||
                !Futile.atlasManager.DoesContainElementWithName(raster.SpriteName))
            {
                iconCache[entry.Id] = raster;
                return raster;
            }

            FAtlasElement element = Futile.atlasManager.GetElementWithName(raster.SpriteName);
            if (element?.atlas?.texture is not Texture2D atlas)
            {
                iconCache[entry.Id] = raster;
                return raster;
            }

            Rect uv = element.uvRect;
            int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * atlas.width), 0, Math.Max(0, atlas.width - 1));
            int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * atlas.height), 0, Math.Max(0, atlas.height - 1));
            int width = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.width) * atlas.width), 1, Math.Max(1, atlas.width - x));
            int height = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.height) * atlas.height), 1, Math.Max(1, atlas.height - y));
            if (!TryReadPixels(atlas, x, y, width, height, out Color32[] pixels))
            {
                iconCache[entry.Id] = raster;
                return raster;
            }

            raster.Width = width;
            raster.Height = height;
            raster.Runs = BuildRuns(pixels, width, height);
            raster.Available = raster.Runs.Length > 0;
        }
        catch
        {
            raster.Available = false;
        }

        iconCache[entry.Id] = raster;
        return raster;
    }

    private static IconSymbol.IconSymbolData ResolveSandboxSymbol(CreatureTemplate.Type type)
    {
        try
        {
            List<string> unlocks = ExtEnum<MultiplayerUnlocks.SandboxUnlockID>.values.entries;
            for (int i = 0; i < unlocks.Count; i++)
            {
                MultiplayerUnlocks.SandboxUnlockID unlock = new(unlocks[i]);
                IconSymbol.IconSymbolData data = MultiplayerUnlocks.SymbolDataForSandboxUnlock(unlock);
                if (data.itemType == AbstractPhysicalObject.AbstractObjectType.Creature && data.critType == type)
                    return data;
            }
        }
        catch
        {
            // Some mods expose malformed/late sandbox ids. Falling back to intData=0 is exactly
            // what CreatureSymbol expects for the standard form of most creature templates.
        }
        return new IconSymbol.IconSymbolData(type, AbstractPhysicalObject.AbstractObjectType.Creature, 0);
    }

    private static bool TryReadPixels(
        Texture2D texture,
        int x,
        int y,
        int width,
        int height,
        out Color32[] pixels)
    {
        pixels = null;
        try
        {
            Color[] colors = texture.GetPixels(x, y, width, height);
            if (colors != null && colors.Length == width * height)
            {
                pixels = new Color32[colors.Length];
                for (int i = 0; i < colors.Length; i++) pixels[i] = colors[i];
                return true;
            }
        }
        catch
        {
            // Continue through a tiny GPU readback; most Futile atlas textures are not CPU-readable.
        }

        RenderTexture previous = RenderTexture.active;
        RenderTexture target = null;
        Texture2D readable = null;
        try
        {
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
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
            if (readable != null) UnityEngine.Object.Destroy(readable);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static PixelRun[] BuildRuns(Color32[] pixels, int width, int height)
    {
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
                    if (next.a < 12 || !Similar(color, next)) break;
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

    private static void RefreshCatalog()
    {
        int currentCount = ExtEnum<CreatureTemplate.Type>.values.entries.Count;
        int currentPlugins = Chainloader.PluginInfos.Count;
        if (currentCount == catalogCount && currentPlugins == pluginCount && entries.Count > 0) return;

        catalogCount = currentCount;
        pluginCount = currentPlugins;
        entries.Clear();
        groups.Clear();
        sourceByCreature.Clear();
        sourceLabelByKey.Clear();
        iconCache.Clear();

        CollectOfficialSources();
        CollectPluginSources();

        List<string> ids = ExtEnum<CreatureTemplate.Type>.values.entries;
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (string.IsNullOrWhiteSpace(id)) continue;
            CreatureTemplate.Type type = new(id);
            ResolveSource(id, out string sourceKey, out string sourceLabel);
            CreatureEntry entry = new()
            {
                Id = id,
                Type = type,
                SourceKey = sourceKey,
                SourceLabel = sourceLabel
            };
            entries.Add(entry);
            SourceGroup group = FindOrCreateGroup(sourceKey, sourceLabel);
            group.Entries.Add(entry);
        }

        entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < groups.Count; i++)
            groups[i].Entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        groups.Sort(CompareGroups);
    }

    private static void CollectOfficialSources()
    {
        CollectStaticCreatureIds(typeof(CreatureTemplate.Type), "official:vanilla", "Vanilla", overwrite: true);
        Type downpour = FindLoadedType("MoreSlugcats.MoreSlugcatsEnums+CreatureTemplateType");
        Type shared = FindLoadedType("DLCSharedEnums+CreatureTemplateType");
        Type watcher = FindLoadedType("Watcher.WatcherEnums+CreatureTemplateType");
        CollectStaticCreatureIds(downpour, "official:moreslugcats", "Downpour", overwrite: true);
        CollectStaticCreatureIds(shared, "official:moreslugcats", "Downpour", overwrite: true);
        CollectStaticCreatureIds(watcher, "official:watcher", "Watcher", overwrite: true);
    }

    private static void CollectPluginSources()
    {
        foreach (var pair in Chainloader.PluginInfos)
        {
            try
            {
                var info = pair.Value;
                Assembly assembly = info?.Instance?.GetType().Assembly;
                if (assembly == null) continue;
                string label = info.Metadata?.Name;
                if (string.IsNullOrWhiteSpace(label)) label = info.Metadata?.GUID;
                if (string.IsNullOrWhiteSpace(label)) label = assembly.GetName().Name;
                string key = "mod:" + (info.Metadata?.GUID ?? assembly.GetName().Name ?? label).Trim().ToLowerInvariant();
                CollectAssemblyCreatureIds(assembly, key, label);
            }
            catch
            {
                // A broken optional plugin must not remove the rest of the catalog.
            }
        }
    }

    private static void CollectAssemblyCreatureIds(Assembly assembly, string sourceKey, string sourceLabel)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types ?? Array.Empty<Type>();
        }
        catch
        {
            return;
        }

        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type == null) continue;
            CollectStaticCreatureIds(type, sourceKey, sourceLabel, overwrite: false);
        }
    }

    private static void CollectStaticCreatureIds(Type owner, string sourceKey, string sourceLabel, bool overwrite)
    {
        if (owner == null) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        try
        {
            FieldInfo[] fields = owner.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(CreatureTemplate.Type).IsAssignableFrom(fields[i].FieldType)) continue;
                if (fields[i].GetValue(null) is CreatureTemplate.Type creature && !string.IsNullOrWhiteSpace(creature.value))
                    AssignSource(creature.value, sourceKey, sourceLabel, overwrite);
            }

            PropertyInfo[] properties = owner.GetProperties(flags);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !typeof(CreatureTemplate.Type).IsAssignableFrom(property.PropertyType))
                    continue;
                if (property.GetValue(null, null) is CreatureTemplate.Type creature && !string.IsNullOrWhiteSpace(creature.value))
                    AssignSource(creature.value, sourceKey, sourceLabel, overwrite);
            }
        }
        catch
        {
        }
    }

    private static void AssignSource(string creatureId, string key, string label, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(creatureId)) return;
        if (!overwrite && sourceByCreature.ContainsKey(creatureId)) return;
        sourceByCreature[creatureId] = key;
        sourceLabelByKey[key] = label;
    }

    private static void ResolveSource(string creatureId, out string key, out string label)
    {
        if (sourceByCreature.TryGetValue(creatureId, out key))
        {
            label = sourceLabelByKey.TryGetValue(key, out string stored) ? stored : key;
            return;
        }
        key = "mod:unknown";
        label = DevToolUiSettings.T("其他 Mod", "OTHER MODS");
    }

    private static SourceGroup FindOrCreateGroup(string key, string label)
    {
        for (int i = 0; i < groups.Count; i++)
            if (string.Equals(groups[i].Key, key, StringComparison.OrdinalIgnoreCase)) return groups[i];
        SourceGroup created = new() { Key = key, Label = label };
        groups.Add(created);
        return created;
    }

    private static int CompareGroups(SourceGroup a, SourceGroup b)
    {
        int ra = GroupRank(a.Key);
        int rb = GroupRank(b.Key);
        if (ra != rb) return ra.CompareTo(rb);
        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static int GroupRank(string key)
    {
        if (string.Equals(key, "official:vanilla", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(key, "official:moreslugcats", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(key, "official:watcher", StringComparison.OrdinalIgnoreCase)) return 2;
        return 10;
    }

    private static Type FindLoadedType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type type = assemblies[i].GetType(fullName, throwOnError: false, ignoreCase: false);
                if (type != null) return type;
            }
            catch
            {
            }
        }
        return null;
    }

    private static int CountMatching(SourceGroup group, string search)
    {
        int count = 0;
        for (int i = 0; i < group.Entries.Count; i++)
            if (Matches(group.Entries[i], group, search)) count++;
        return count;
    }

    private static bool Matches(CreatureEntry entry, SourceGroup group, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string needle = query.Trim();
        return Contains(entry.Id, needle) || Contains(group.Label, needle) || Fuzzy(entry.Id, needle);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Fuzzy(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static CreatureEntry FindEntry(string creatureId)
    {
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Id, creatureId, StringComparison.OrdinalIgnoreCase)) return entries[i];
        return null;
    }
}
