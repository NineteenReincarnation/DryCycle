using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Unified command / UI / status surface for the rebuilt DevTool.
/// This replaces the old three-window chrome with one readable control center so
/// the room remains visible while the developer can still understand state at a glance.
/// Shortcut discovery is intentionally delegated to ShortcutWindow.
/// </summary>
internal static class ControlCenterWindow
{
    private static readonly Num.Vector4 CardBg = new(0.025f, 0.040f, 0.060f, 0.70f);
    private static readonly Num.Vector4 CardBorder = new(0.20f, 0.30f, 0.42f, 0.72f);
    private static readonly Num.Vector4 AccentText = new(0.63f, 0.82f, 1.00f, 1f);
    private static readonly Num.Vector4 BadgeBg = new(0.10f, 0.24f, 0.39f, 0.88f);
    private static readonly Num.Vector4 BadgeBorder = new(0.30f, 0.58f, 0.92f, 0.95f);
    private static readonly Num.Vector4 BadgeText = new(0.86f, 0.94f, 1.00f, 1f);

    // ImGui child windows own an independent FontWindowScale. Without setting it explicitly,
    // text inside cards falls back to 1.0 even when the surrounding DevTool pane is enlarged.
    // CJK needs a little more body size because its glyphs read smaller at the same nominal scale.
    private const float CardBodyScaleEnglish = 1.18f;
    private const float CardBodyScaleChinese = 1.24f;
    private const float CardTitleBoost = 1.10f;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));

        // The frontend's outer layout push scales stock ImGui scrollbars too aggressively at large
        // typography sizes. Override the active frame with the compact animated DevTool rail before
        // any of this window's child regions are created.
        DevToolScrollChrome.Apply(ImGui.GetIO(), scale);

        float maxWidth = Math.Max(320f, display.X - 16f);
        float width = Math.Min(
            maxWidth,
            Math.Max(Math.Min(660f, maxWidth), Math.Min(920f * Math.Min(1.18f, scale), display.X * 0.78f)));

        // Height is only an initial seed. Once the window has drawn, FitWindowHeightToContents()
        // keeps it exactly tall enough for the visible controls. This removes the dead Focus-mode
        // rectangle and lets the information cards grow with the active language/font size.
        float defaultHeight = snapshot.FocusMode
            ? Math.Min(132f, Math.Max(86f, display.Y - 16f))
            : Math.Min(360f, Math.Max(260f, display.Y - 16f));
        float defaultX = Math.Max(8f, display.X - width - 8f);

        ImGui.SetNextWindowPos(new Num.Vector2(defaultX, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, defaultHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(560f, maxWidth), 72f),
            new Num.Vector2(maxWidth, Math.Max(96f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(DevToolUiSettings.T("总控###DevToolControlCenter", "Control Center###DevToolControlCenter"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("ControlCenter");

        DrawHeader(snapshot);
        ImGui.Spacing();
        DrawCommandRow(snapshot);

        if (!snapshot.FocusMode)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawInformationRow(snapshot);
        }

        FitWindowHeightToContents(display, snapshot.FocusMode);
        ImGui.End();
    }

    private static void DrawHeader(EditorPresentationSnapshot snapshot)
    {
        string room = CurrentRoom(snapshot);
        string mode = DevToolUiSettings.ToolMode(snapshot.ToolMode);

        ImGui.SetWindowFontScale(1.10f);
        ImGui.TextUnformatted(room);
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine();
        DevToolWidgets.MutedText("/ " + mode);

        if (snapshot.PlacementActive)
        {
            ImGui.SameLine(0f, 14f);
            ImGui.TextColored(AccentText, DevToolUiSettings.T("放置：", "Place: ") + snapshot.PlacementType);
        }

        string badge = snapshot.FocusMode
            ? DevToolUiSettings.T("专注", "FOCUS")
            : DevToolUiSettings.T("编辑中", "EDITING");
        float badgeWidth = BadgeWidth(badge);
        // RWImGui ships a trimmed ImGui.NET surface that does not expose
        // GetWindowContentRegionMax(). Window width minus the active style padding gives the
        // same right content edge without depending on that unavailable API.
        float right = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X;
        float current = ImGui.GetCursorPosX();
        float badgeX = right - badgeWidth;
        if (badgeX > current + 8f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(badgeX);
            DrawBadge(badge);
        }
    }

    private static void DrawCommandRow(EditorPresentationSnapshot snapshot)
    {
        string undo = string.IsNullOrEmpty(snapshot.UndoLabel)
            ? DevToolUiSettings.T("撤销", "Undo")
            : DevToolUiSettings.T("撤销 ", "Undo ") + snapshot.UndoLabel;
        string redo = string.IsNullOrEmpty(snapshot.RedoLabel)
            ? DevToolUiSettings.T("重做", "Redo")
            : DevToolUiSettings.T("重做 ", "Redo ") + snapshot.RedoLabel;

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("保存", "Save"),
                "ControlCenterSave",
                DevToolButtonTone.Primary))
            Send(EditorUiCommandKind.Save);

        ImGui.SameLine();
        if (!snapshot.CanUndo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(undo, "ControlCenterUndo", DevToolButtonTone.Normal))
            Send(EditorUiCommandKind.Undo);
        if (!snapshot.CanUndo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!snapshot.CanRedo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(redo, "ControlCenterRedo", DevToolButtonTone.Normal))
            Send(EditorUiCommandKind.Redo);
        if (!snapshot.CanRedo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                snapshot.FocusMode
                    ? DevToolUiSettings.T("退出专注", "Exit Focus")
                    : DevToolUiSettings.T("专注", "Focus"),
                "ControlCenterFocus",
                snapshot.FocusMode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            Send(EditorUiCommandKind.ToggleFocus);
    }

    private static void DrawInformationRow(EditorPresentationSnapshot snapshot)
    {
        float available = ImGui.GetContentRegionAvail().X;
        float gap = Math.Max(8f, ImGui.GetStyle().ItemSpacing.X);
        bool twoColumns = available >= 560f;
        float leftWidth = twoColumns ? Math.Max(250f, available * 0.48f) : available;

        // These cards contain only a handful of controls and status rows. Let the child windows
        // grow vertically to their content instead of giving them a scroll range. This also makes
        // the SESSION card adapt to wrapped status text and to Chinese/English font differences.
        ImGuiChildFlags cardFlags = ImGuiChildFlags.Borders |
                                    ImGuiChildFlags.AutoResizeY |
                                    ImGuiChildFlags.AlwaysAutoResize;

        PushCardStyle();
        if (ImGui.BeginChild("##ControlCenterInterface", new Num.Vector2(leftWidth, 0f), cardFlags))
        {
            ApplyCardBodyScale();
            DrawInterfaceCard();
        }
        ImGui.EndChild();
        PopCardStyle();

        if (twoColumns)
        {
            ImGui.SameLine(0f, gap);
            PushCardStyle();
            if (ImGui.BeginChild("##ControlCenterSession", new Num.Vector2(0f, 0f), cardFlags))
            {
                ApplyCardBodyScale();
                DrawSessionCard(snapshot);
            }
            ImGui.EndChild();
            PopCardStyle();
        }
        else
        {
            ImGui.Spacing();
            PushCardStyle();
            if (ImGui.BeginChild("##ControlCenterSession", new Num.Vector2(0f, 0f), cardFlags))
            {
                ApplyCardBodyScale();
                DrawSessionCard(snapshot);
            }
            ImGui.EndChild();
            PopCardStyle();
        }
    }

    private static void DrawInterfaceCard()
    {
        DrawCardTitle(DevToolUiSettings.T("界面", "INTERFACE"));
        ImGui.Spacing();

        float keyColumn = CardKeyColumn();
        DevToolWidgets.MutedText(DevToolUiSettings.T("模式", "Mode"));
        ImGui.SameLine(keyColumn);
        bool vanilla = EditorUiModeState.UseVanilla;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新 UI", "New UI"),
                "ControlCenterNewUi",
                vanilla ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            EditorUiModeState.SetVanilla(false);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("原版", "Vanilla"),
                "ControlCenterVanillaUi",
                vanilla ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            EditorUiModeState.SetVanilla(true);

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine(keyColumn);
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        if (DevToolWidgets.ActionButton(
                "中文",
                "ControlCenterChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "English",
                "ControlCenterEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);
    }

    private static void DrawSessionCard(EditorPresentationSnapshot snapshot)
    {
        DrawCardTitle(DevToolUiSettings.T("会话", "SESSION"));
        ImGui.Spacing();

        DrawKeyValue(DevToolUiSettings.T("房间", "Room"), CurrentRoom(snapshot));
        ImGui.Spacing();
        DrawKeyValue(DevToolUiSettings.T("工具", "Tool"), DevToolUiSettings.ToolMode(snapshot.ToolMode));
        ImGui.Spacing();

        DevToolWidgets.MutedText(DevToolUiSettings.T("状态", "Status"));
        ImGui.SameLine(CardKeyColumn());
        ImGui.TextWrapped(BuildStatusText(snapshot));
    }

    private static void DrawKeyValue(string key, string value)
    {
        DevToolWidgets.MutedText(key);
        ImGui.SameLine(CardKeyColumn());
        ImGui.TextUnformatted(string.IsNullOrEmpty(value) ? "-" : value);
    }

    private static void ApplyCardBodyScale()
    {
        ImGui.SetWindowFontScale(CardBodyScale());
    }

    private static void DrawCardTitle(string text)
    {
        float bodyScale = CardBodyScale();
        ImGui.SetWindowFontScale(bodyScale * CardTitleBoost);
        ImGui.TextColored(AccentText, text);
        ImGui.SetWindowFontScale(bodyScale);
    }

    private static float CardBodyScale() =>
        DevToolUiSettings.IsChinese ? CardBodyScaleChinese : CardBodyScaleEnglish;

    private static float CardKeyColumn() =>
        DevToolUiSettings.IsChinese ? 116f : 108f;

    private static void FitWindowHeightToContents(Num.Vector2 display, bool focusMode)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float minimum = focusMode ? 82f : 180f;
        float maximum = Math.Max(minimum, display.Y - 16f);
        float desired = ImGui.GetCursorPosY() + style.WindowPadding.Y;
        desired = Math.Max(minimum, Math.Min(maximum, desired));

        Num.Vector2 current = ImGui.GetWindowSize();
        if (Math.Abs(current.Y - desired) > 0.5f)
            ImGui.SetWindowSize(new Num.Vector2(current.X, desired), ImGuiCond.Always);
    }

    private static string CurrentRoom(EditorPresentationSnapshot snapshot) =>
        string.IsNullOrEmpty(snapshot.RoomName) ? snapshot.Document : snapshot.RoomName;

    private static string BuildStatusText(EditorPresentationSnapshot snapshot)
    {
        if (snapshot.ToolMode == EditorToolMode.Objects)
        {
            int selected = snapshot.Inspector?.SelectionCount ?? 0;
            string placement = snapshot.PlacementActive
                ? DevToolUiSettings.T(" · 放置 ", " · Placing ") + snapshot.PlacementType
                : string.Empty;
            return DevToolUiSettings.T("物件 ", "Objects ") + (snapshot.SceneObjects?.Length ?? 0) +
                   DevToolUiSettings.T(" · 已选 ", " · Selected ") + selected + placement;
        }

        if (snapshot.ToolMode == EditorToolMode.Sound)
        {
            EditorSoundPresentationSnapshot sound = SoundEditorPresentationHub.Current;
            return DevToolUiSettings.T("声音 ", "Sounds ") + (sound.Sounds?.Length ?? 0);
        }

        if (snapshot.ToolMode == EditorToolMode.Triggers)
        {
            EditorTriggerPresentationSnapshot trigger = TriggerEditorPresentationHub.Current;
            return DevToolUiSettings.T("触发器 ", "Triggers ") + (trigger.Triggers?.Length ?? 0);
        }

        if (snapshot.ToolMode == EditorToolMode.Map)
        {
            EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;
            return (map.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间 · ", " rooms · ") + map.RegionName;
        }

        if (snapshot.ToolMode == EditorToolMode.Dialog)
        {
            EditorDialogPresentationSnapshot dialog = DialogEditorPresentationHub.Current;
            return dialog.SelectedFileName + " · " + (dialog.Events?.Length ?? 0) +
                   DevToolUiSettings.T(" 个事件", " events");
        }

        if (snapshot.ToolMode == EditorToolMode.Relationships)
        {
            EditorRelationshipPresentationSnapshot rel = RelationshipEditorPresentationHub.Current;
            return DevToolUiSettings.T("主体 ", "Primary ") + rel.PrimaryCreature;
        }

        return snapshot.Document;
    }

    private static void PushCardStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
    }

    private static void PopCardStyle()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private static float BadgeWidth(string text)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        return ImGui.CalcTextSize(text).X + style.FramePadding.X * 2.4f;
    }

    private static void DrawBadge(string text)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        Num.Vector2 textSize = ImGui.CalcTextSize(text);
        Num.Vector2 padding = new(style.FramePadding.X * 1.2f, Math.Max(2f, style.FramePadding.Y * 0.65f));
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        Num.Vector2 size = textSize + padding * 2f;
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(BadgeBg), 5f);
        draw.AddRect(pos, pos + size, ImGui.GetColorU32(BadgeBorder), 5f);
        draw.AddText(pos + padding, ImGui.GetColorU32(BadgeText), text);
        ImGui.Dummy(size);
    }

    private static void Send(EditorUiCommandKind kind) =>
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(kind));
}
