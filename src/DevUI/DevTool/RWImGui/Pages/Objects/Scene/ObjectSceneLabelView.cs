using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class ObjectSceneLabelView
{
    private const float PaddingX = 6f;
    private const float PaddingY = 3f;
    private const float Gap = 3f;
    private const float HitPadding = 4f;
    private const double HoverDelaySeconds = 0.12;
    private const float MidZoomEnter = 1.55f;
    private const float MidZoomExit = 1.30f;
    private const float FarZoomEnter = 2.50f;
    private const float FarZoomExit = 2.20f;
    private const float SpatialCellSize = 96f;

    private enum SemanticZoomBand
    {
        Near,
        Mid,
        Far
    }

    private sealed class Label
    {
        internal EditorObjectSnapshot Item;
        internal string Text;
        internal Num.Vector2 Anchor;
        internal Num.Vector2 Min;
        internal Num.Vector2 Max;
        internal ObjectSceneVisibility Visibility;
        internal int Priority;
    }

    private static readonly List<Label> labels = new();
    private static readonly List<Label> placed = new();
    private static readonly Dictionary<long, List<Label>> occupancy = new();
    private static EditorObjectSnapshot[] cachedObjects;
    private static long cachedVisibilityRevision = -1;
    private static float cameraX = float.NaN;
    private static float cameraY = float.NaN;
    private static float cameraWidth = float.NaN;
    private static float cameraHeight = float.NaN;
    private static Num.Vector2 cachedDisplay;
    private static int hoveredIndex = -1;
    private static int pendingHoverIndex = -1;
    private static double pendingHoverSince;
    private static SemanticZoomBand semanticZoomBand;
    private static bool semanticZoomInitialized;

    internal static bool OwnsMouse { get; private set; }

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        OwnsMouse = false;
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (snapshot == null || snapshot.ToolMode != EditorToolMode.Objects || snapshot.PlacementActive ||
            viewport?.Available != true || display.X <= 1f || display.Y <= 1f)
        {
            SetHover(-1);
            return;
        }

        EnsureLayout(snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>(), viewport, display);

        ImGuiIOPtr io = ImGui.GetIO();
        Label hit = HitTest(io.MousePos);
        UpdateHover(hit?.Item.Index ?? -1);

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        for (int i = 0; i < placed.Count; i++)
            DrawLabel(draw, placed[i]);

        if (hit == null || io.WantCaptureMouse || NativeObjectGizmoView.OwnsMouse ||
            NativeSpatialGizmoView.OwnsMouse || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        OwnsMouse = true;
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            io.KeyCtrl ? EditorUiCommandKind.ToggleObjectSelection : EditorUiCommandKind.SelectObject,
            index: hit.Item.Index));
    }

    internal static void ResetRetainedState()
    {
        labels.Clear();
        placed.Clear();
        occupancy.Clear();
        cachedObjects = null;
        cachedVisibilityRevision = -1;
        cameraX = cameraY = cameraWidth = cameraHeight = float.NaN;
        cachedDisplay = default;
        hoveredIndex = -1;
        pendingHoverIndex = -1;
        pendingHoverSince = 0d;
        semanticZoomBand = SemanticZoomBand.Near;
        semanticZoomInitialized = false;
        OwnsMouse = false;
        ObjectSceneVisibilityState.Reset();
    }

    private static void EnsureLayout(EditorObjectSnapshot[] objects, EditorViewportSnapshot viewport, Num.Vector2 display)
    {
        long revision = ObjectSceneVisibilityState.Revision;
        if (ReferenceEquals(cachedObjects, objects) && cachedVisibilityRevision == revision &&
            Nearly(cameraX, viewport.CameraX) && Nearly(cameraY, viewport.CameraY) &&
            Nearly(cameraWidth, viewport.Width) && Nearly(cameraHeight, viewport.Height) &&
            Nearly(cachedDisplay.X, display.X) && Nearly(cachedDisplay.Y, display.Y))
            return;

        UpdateSemanticZoomBand(viewport, display);

        labels.Clear();
        placed.Clear();
        occupancy.Clear();
        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;
            ObjectSceneVisibility visibility = ObjectSceneVisibilityState.Resolve(item);
            if (visibility == ObjectSceneVisibility.Hidden || !PassesSemanticZoom(item, visibility))
                continue;

            Num.Vector2 anchor = WorldToScreen(item.X, item.Y, viewport, display);
            if (anchor.X < -120f || anchor.X > display.X + 120f || anchor.Y < -40f || anchor.Y > display.Y + 40f)
                continue;

            string text = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Type : item.DisplayName;
            Num.Vector2 size = ImGui.CalcTextSize(text) + new Num.Vector2(PaddingX * 2f, PaddingY * 2f);
            labels.Add(new Label
            {
                Item = item,
                Text = text,
                Anchor = anchor,
                Min = anchor,
                Max = anchor + size,
                Visibility = visibility,
                Priority = Priority(item, visibility)
            });
        }

        labels.Sort((a, b) =>
        {
            int p = b.Priority.CompareTo(a.Priority);
            return p != 0 ? p : a.Item.Index.CompareTo(b.Item.Index);
        });

        for (int i = 0; i < labels.Count; i++)
        {
            Place(labels[i], display);
            placed.Add(labels[i]);
            RegisterOccupancy(labels[i]);
        }

        placed.Sort((a, b) =>
        {
            int p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : a.Item.Index.CompareTo(b.Item.Index);
        });

        cachedObjects = objects;
        cachedVisibilityRevision = revision;
        cameraX = viewport.CameraX;
        cameraY = viewport.CameraY;
        cameraWidth = viewport.Width;
        cameraHeight = viewport.Height;
        cachedDisplay = display;
    }

    private static void Place(Label label, Num.Vector2 display)
    {
        Num.Vector2 size = label.Max - label.Min;
        Num.Vector2[] offsets =
        {
            new(8f, -size.Y * 0.5f), new(8f, 8f), new(8f, -size.Y - 8f),
            new(-size.X - 8f, -size.Y * 0.5f), new(-size.X - 8f, 8f),
            new(-size.X - 8f, -size.Y - 8f), new(-size.X * 0.5f, 10f),
            new(-size.X * 0.5f, -size.Y - 10f)
        };

        for (int i = 0; i < offsets.Length; i++)
        {
            Num.Vector2 min = label.Anchor + offsets[i];
            Num.Vector2 max = min + size;
            Clamp(ref min, ref max, display);
            if (!Overlaps(min, max))
            {
                label.Min = min;
                label.Max = max;
                return;
            }
        }

        for (int row = 0; row < 16; row++)
        {
            Num.Vector2 min = label.Anchor + new Num.Vector2(8f, 8f + row * (size.Y + Gap));
            Num.Vector2 max = min + size;
            Clamp(ref min, ref max, display);
            if (!Overlaps(min, max))
            {
                label.Min = min;
                label.Max = max;
                return;
            }
        }
    }

    private static Label HitTest(Num.Vector2 point)
    {
        if (!occupancy.TryGetValue(CellKey(Cell(point.X), Cell(point.Y)), out List<Label> bucket))
            return null;

        Label best = null;
        int bestPriority = int.MinValue;
        for (int i = 0; i < bucket.Count; i++)
        {
            Label candidate = bucket[i];
            if (!ObjectSceneVisibilityState.IsInteractive(candidate.Item) ||
                !Contains(candidate, point, HitPadding) ||
                candidate.Priority < bestPriority)
                continue;

            best = candidate;
            bestPriority = candidate.Priority;
        }
        return best;
    }

    private static bool Overlaps(Num.Vector2 min, Num.Vector2 max)
    {
        int minCellX = Cell(min.X - Gap);
        int maxCellX = Cell(max.X + Gap);
        int minCellY = Cell(min.Y - Gap);
        int maxCellY = Cell(max.Y + Gap);

        for (int y = minCellY; y <= maxCellY; y++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                if (!occupancy.TryGetValue(CellKey(x, y), out List<Label> bucket))
                    continue;

                for (int i = 0; i < bucket.Count; i++)
                {
                    Label other = bucket[i];
                    if (max.X + Gap < other.Min.X || min.X - Gap > other.Max.X ||
                        max.Y + Gap < other.Min.Y || min.Y - Gap > other.Max.Y)
                        continue;
                    return true;
                }
            }
        }
        return false;
    }

    private static void RegisterOccupancy(Label label)
    {
        float indexPadding = Math.Max(Gap, HitPadding);
        int minCellX = Cell(label.Min.X - indexPadding);
        int maxCellX = Cell(label.Max.X + indexPadding);
        int minCellY = Cell(label.Min.Y - indexPadding);
        int maxCellY = Cell(label.Max.Y + indexPadding);

        for (int y = minCellY; y <= maxCellY; y++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                long key = CellKey(x, y);
                if (!occupancy.TryGetValue(key, out List<Label> bucket))
                {
                    bucket = new List<Label>(4);
                    occupancy.Add(key, bucket);
                }
                bucket.Add(label);
            }
        }
    }

    private static int Cell(float value) => (int)Math.Floor(value / SpatialCellSize);

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static void DrawLabel(ImDrawListPtr draw, Label label)
    {
        float alpha = label.Visibility switch
        {
            ObjectSceneVisibility.Ghost => 0.24f,
            ObjectSceneVisibility.Normal => 0.72f,
            ObjectSceneVisibility.Hovered => 1f,
            ObjectSceneVisibility.Selected => 1f,
            _ => 0f
        };
        if (alpha <= 0f) return;

        bool strong = label.Visibility is ObjectSceneVisibility.Hovered or ObjectSceneVisibility.Selected;
        uint bg = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.035f, 0.045f, 0.055f, strong ? 0.82f : 0.46f * alpha));
        uint border = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.55f, 0.76f, 0.92f, strong ? 0.92f : 0.25f * alpha));
        uint text = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.92f, 0.95f, 0.98f, alpha));
        uint leader = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.68f, 0.78f, 0.88f, strong ? 0.58f : 0.15f * alpha));

        Num.Vector2 nearest = new(
            Math.Max(label.Min.X, Math.Min(label.Anchor.X, label.Max.X)),
            Math.Max(label.Min.Y, Math.Min(label.Anchor.Y, label.Max.Y)));
        draw.AddLine(label.Anchor, nearest, leader, strong ? 1.2f : 1f);
        draw.AddRectFilled(label.Min, label.Max, bg, 4f);
        draw.AddRect(label.Min, label.Max, border, 4f, ImDrawFlags.None, strong ? 1.2f : 1f);
        draw.AddText(label.Min + new Num.Vector2(PaddingX, PaddingY), text, label.Text);
    }

    private static int Priority(EditorObjectSnapshot item, ObjectSceneVisibility visibility)
    {
        int state = visibility switch
        {
            ObjectSceneVisibility.Selected => 4000,
            ObjectSceneVisibility.Hovered => 3000,
            ObjectSceneVisibility.Normal => 2000,
            ObjectSceneVisibility.Ghost => 1000,
            _ => 0
        };
        return state + Math.Max(-100, Math.Min(100, item.Importance));
    }

    private static bool Contains(Label label, Num.Vector2 p, float padding) =>
        p.X >= label.Min.X - padding && p.X <= label.Max.X + padding &&
        p.Y >= label.Min.Y - padding && p.Y <= label.Max.Y + padding;

    private static void Clamp(ref Num.Vector2 min, ref Num.Vector2 max, Num.Vector2 display)
    {
        Num.Vector2 size = max - min;
        min.X = Math.Max(2f, Math.Min(Math.Max(2f, display.X - size.X - 2f), min.X));
        min.Y = Math.Max(2f, Math.Min(Math.Max(2f, display.Y - size.Y - 2f), min.Y));
        max = min + size;
    }

    private static Num.Vector2 WorldToScreen(float x, float y, EditorViewportSnapshot viewport, Num.Vector2 display)
    {
        float nx = (x - viewport.CameraX) / Math.Max(1f, viewport.Width);
        float ny = (y - viewport.CameraY) / Math.Max(1f, viewport.Height);
        return new Num.Vector2(nx * display.X, display.Y - ny * display.Y);
    }

    private static void UpdateHover(int index)
    {
        if (index < 0)
        {
            pendingHoverIndex = -1;
            pendingHoverSince = 0d;
            SetHover(-1);
            return;
        }

        if (index == hoveredIndex)
        {
            pendingHoverIndex = -1;
            return;
        }

        double now = ImGui.GetTime();
        if (pendingHoverIndex != index)
        {
            // Do not leave the previous label visually hot while the pointer settles on another
            // candidate. The new candidate still waits for the debounce before expanding.
            if (hoveredIndex >= 0)
                SetHover(-1);
            pendingHoverIndex = index;
            pendingHoverSince = now;
            return;
        }

        if (now - pendingHoverSince < HoverDelaySeconds)
            return;

        pendingHoverIndex = -1;
        SetHover(index);
    }

    private static void SetHover(int index)
    {
        if (hoveredIndex == index) return;
        hoveredIndex = index;
        ObjectSceneVisibilityState.SetHoveredIndex(index);
    }

    private static void UpdateSemanticZoomBand(EditorViewportSnapshot viewport, Num.Vector2 display)
    {
        float unitsPerPixelX = viewport.Width / Math.Max(1f, display.X);
        float unitsPerPixelY = viewport.Height / Math.Max(1f, display.Y);
        float unitsPerPixel = Math.Max(unitsPerPixelX, unitsPerPixelY);

        if (!semanticZoomInitialized)
        {
            semanticZoomBand = unitsPerPixel >= FarZoomEnter
                ? SemanticZoomBand.Far
                : unitsPerPixel >= MidZoomEnter
                    ? SemanticZoomBand.Mid
                    : SemanticZoomBand.Near;
            semanticZoomInitialized = true;
            return;
        }

        semanticZoomBand = semanticZoomBand switch
        {
            SemanticZoomBand.Near when unitsPerPixel >= FarZoomEnter => SemanticZoomBand.Far,
            SemanticZoomBand.Near when unitsPerPixel >= MidZoomEnter => SemanticZoomBand.Mid,
            SemanticZoomBand.Mid when unitsPerPixel >= FarZoomEnter => SemanticZoomBand.Far,
            SemanticZoomBand.Mid when unitsPerPixel <= MidZoomExit => SemanticZoomBand.Near,
            SemanticZoomBand.Far when unitsPerPixel <= MidZoomExit => SemanticZoomBand.Near,
            SemanticZoomBand.Far when unitsPerPixel <= FarZoomExit => SemanticZoomBand.Mid,
            _ => semanticZoomBand
        };
    }

    private static bool PassesSemanticZoom(
        EditorObjectSnapshot item,
        ObjectSceneVisibility visibility)
    {
        // Explicit user intent must beat density reduction. Search results, the focused category,
        // selection and hover stay discoverable at every zoom level.
        if (visibility is ObjectSceneVisibility.Selected or ObjectSceneVisibility.Hovered ||
            ObjectSceneVisibilityState.IsExplicitlyEmphasized(item))
            return true;

        return semanticZoomBand switch
        {
            SemanticZoomBand.Far => item.Importance >= 2,
            SemanticZoomBand.Mid => item.Importance >= 1,
            _ => true
        };
    }

    private static bool Nearly(float a, float b) => Math.Abs(a - b) <= 0.01f;
}
