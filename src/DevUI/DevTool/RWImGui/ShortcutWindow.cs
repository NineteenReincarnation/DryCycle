using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Single presentation surface for every user-facing DevTool shortcut hint.
/// The data comes from DevToolShortcutRegistry so tools and compatibility layers can extend the
/// list without duplicating visual code or depending on RWImGui.
/// </summary>
internal static class ShortcutWindow
{
    private enum Tab
    {
        Common,
        CurrentMode
    }

    private static readonly Num.Vector4 CardBg = new(0.025f, 0.040f, 0.060f, 0.78f);
    private static readonly Num.Vector4 CardBorder = new(0.20f, 0.30f, 0.42f, 0.76f);
    private static readonly Num.Vector4 KeyBg = new(0.08f, 0.13f, 0.20f, 0.96f);
    private static readonly Num.Vector4 KeyBorder = new(0.31f, 0.58f, 0.88f, 0.94f);
    private static readonly Num.Vector4 KeyText = new(0.84f, 0.93f, 1.00f, 1f);
    private static readonly Num.Vector4 ModeText = new(0.66f, 0.84f, 1.00f, 1f);

    private static Tab tab;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available) return;

        float scale = Math.Max(0.80f, Math.Min(2.4f, DevToolUiSettings.UiScale));
        float width = DevToolUiSettings.IsChinese
            ? Math.Min(500f, Math.Max(390f, display.X * 0.255f))
            : Math.Min(540f, Math.Max(420f, display.X * 0.275f));

        // Keep the lower-left utility clear of the default Tools window on common 900p layouts.
        // The content child scrolls, so the window does not need to become tall enough to compete
        // with the room viewport merely because the common catalog grows over time.
        float height = Math.Min(380f, Math.Max(280f, display.Y * 0.34f));
        width = Math.Min(width * Math.Min(1.12f, scale), Math.Max(300f, display.X - 16f));
        height = Math.Min(height * Math.Min(1.06f, scale), Math.Max(220f, display.Y - 16f));

        ImGui.SetNextWindowPos(
            new Num.Vector2(8f, Math.Max(8f, display.Y - height - 8f)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(340f, 220f),
            new Num.Vector2(Math.Max(340f, display.X - 16f), Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("快捷键###DevToolShortcuts", "Shortcuts###DevToolShortcuts"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Shortcuts");
        float bodyScale = DevToolUiSettings.IsChinese ? 1.16f : 1.10f;
        ImGui.SetWindowFontScale(bodyScale);

        DrawTabs(snapshot);
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);

        if (ImGui.BeginChild("##ShortcutPage", new Num.Vector2(0f, 0f), ImGuiChildFlags.Borders))
        {
            ImGui.SetWindowFontScale(bodyScale);
            if (tab == Tab.Common)
                DrawShortcutList(
                    DevToolShortcutRegistry.GetCommon(),
                    DevToolUiSettings.T("通用操作", "GLOBAL OPERATIONS"),
                    DevToolUiSettings.T("在所有 DevTool 模式下可用", "Available across DevTool modes"));
            else
                DrawCurrentMode(snapshot);
        }
        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        ImGui.End();
    }

    private static void DrawTabs(EditorPresentationSnapshot snapshot)
    {
        string common = DevToolUiSettings.T("通用快捷键", "Common");
        string current = DevToolUiSettings.T(
            "当前模式 · " + DevToolUiSettings.ToolMode(snapshot.ToolMode),
            "Current · " + DevToolUiSettings.ToolMode(snapshot.ToolMode));

        float available = ImGui.GetContentRegionAvail().X;
        float gap = Math.Max(6f, ImGui.GetStyle().ItemSpacing.X);
        float tabWidth = Math.Max(120f, (available - gap) * 0.5f);

        if (DrawTabButton(common, "ShortcutCommonTab", tab == Tab.Common, tabWidth))
            tab = Tab.Common;
        ImGui.SameLine(0f, gap);
        if (DrawTabButton(current, "ShortcutModeTab", tab == Tab.CurrentMode, tabWidth))
            tab = Tab.CurrentMode;
    }

    private static bool DrawTabButton(string label, string id, bool active, float width)
    {
        Num.Vector4 normal = active
            ? new Num.Vector4(0.17f, 0.36f, 0.59f, 0.94f)
            : new Num.Vector4(0.10f, 0.12f, 0.16f, 0.88f);
        Num.Vector4 hover = active
            ? new Num.Vector4(0.23f, 0.48f, 0.77f, 0.98f)
            : new Num.Vector4(0.18f, 0.21f, 0.27f, 0.95f);
        Num.Vector4 border = active
            ? new Num.Vector4(0.35f, 0.66f, 1.00f, 1f)
            : new Num.Vector4(0.32f, 0.36f, 0.43f, 0.78f);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, hover);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        bool clicked = ImGui.Button(label + "##" + id, new Num.Vector2(width, 0f));
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private static void DrawCurrentMode(EditorPresentationSnapshot snapshot)
    {
        DevToolShortcutDescriptor[] shortcuts = DevToolShortcutRegistry.GetMode(snapshot.ToolMode);
        string mode = DevToolUiSettings.ToolMode(snapshot.ToolMode);

        ImGui.TextColored(ModeText, mode);
        ImGui.SameLine();
        DevToolWidgets.MutedText(DevToolUiSettings.T("模式快捷键", "mode shortcuts"));
        ImGui.Separator();
        ImGui.Spacing();

        if (shortcuts.Length == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("当前模式暂无额外快捷键。通用操作请切换到“通用快捷键”。", "This mode has no additional shortcuts. Use Common for global operations."),
                true);
            return;
        }

        DrawRows(shortcuts);
    }

    private static void DrawShortcutList(
        DevToolShortcutDescriptor[] shortcuts,
        string title,
        string subtitle)
    {
        ImGui.TextColored(ModeText, title);
        ImGui.SameLine();
        DevToolWidgets.MutedText(subtitle);
        ImGui.Separator();
        ImGui.Spacing();
        DrawRows(shortcuts);
    }

    private static void DrawRows(DevToolShortcutDescriptor[] shortcuts)
    {
        if (shortcuts == null || shortcuts.Length == 0) return;

        float keyColumn = DevToolUiSettings.IsChinese ? 156f : 168f;
        float startX = ImGui.GetCursorPosX();
        float available = ImGui.GetContentRegionAvail().X;
        keyColumn = Math.Min(keyColumn, Math.Max(118f, available * 0.42f));

        for (int i = 0; i < shortcuts.Length; i++)
        {
            DevToolShortcutDescriptor shortcut = shortcuts[i];
            if (shortcut == null) continue;

            float rowY = ImGui.GetCursorPosY();
            DrawKeyChip(shortcut.Input, keyColumn - 8f);
            ImGui.SameLine(startX + keyColumn);

            string description = DevToolUiSettings.IsChinese
                ? shortcut.ChineseDescription
                : shortcut.EnglishDescription;
            if (string.IsNullOrWhiteSpace(description)) description = shortcut.Id;
            ImGui.TextWrapped(description);

            float textBottom = ImGui.GetCursorPosY();
            float minBottom = rowY + ImGui.GetFrameHeight() + 2f;
            if (textBottom < minBottom) ImGui.SetCursorPosY(minBottom);

            if (i + 1 < shortcuts.Length)
            {
                ImGui.Spacing();
                DrawSoftSeparator(startX + keyColumn);
                ImGui.Spacing();
            }
        }
    }

    private static void DrawKeyChip(string text, float width)
    {
        text ??= string.Empty;
        ImGuiStylePtr style = ImGui.GetStyle();
        float height = Math.Max(ImGui.GetFrameHeight(), ImGui.GetTextLineHeight() + style.FramePadding.Y * 1.6f);
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 max = new(pos.X + width, pos.Y + height);

        draw.AddRectFilled(pos, max, ImGui.GetColorU32(KeyBg), 4f);
        draw.AddRect(pos, max, ImGui.GetColorU32(KeyBorder), 4f);

        Num.Vector2 textSize = ImGui.CalcTextSize(text);
        float textX = pos.X + Math.Max(7f, (width - textSize.X) * 0.5f);
        float textY = pos.Y + Math.Max(1f, (height - textSize.Y) * 0.5f);
        draw.AddText(new Num.Vector2(textX, textY), ImGui.GetColorU32(KeyText), text);
        ImGui.Dummy(new Num.Vector2(width, height));
    }

    private static void DrawSoftSeparator(float startX)
    {
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X;
        float absoluteStart = ImGui.GetWindowPos().X + startX;
        ImGui.GetWindowDrawList().AddLine(
            new Num.Vector2(Math.Min(absoluteStart, right), pos.Y),
            new Num.Vector2(right, pos.Y),
            ImGui.GetColorU32(new Num.Vector4(0.24f, 0.31f, 0.40f, 0.50f)),
            1f);
        ImGui.Dummy(new Num.Vector2(0f, 1f));
    }
}
