using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared top status surface for every rebuilt DevTool page.
///
/// Global UI mode and language live on the first row around the room/status title. Optional
/// page-owned controls occupy the second row so global controls never need a separate window.
/// </summary>
internal static class DevToolTopStatusWindow
{
    private const float PreferredWidth = 780f;
    private const float ModeButtonWidth = 72f;
    private const float LanguageButtonWidth = 58f;
    private const float SegmentGap = 2f;

    private static int lastDrawFrame = -1;

    internal static void Draw(
        EditorPresentationSnapshot snapshot,
        IDevToolPageView page,
        Num.Vector2 display)
    {
        DrawCore(snapshot, page, display, allowPageControls: true);
    }

    internal static void DrawGlobalOnly(
        EditorPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        DrawCore(snapshot, null, display, allowPageControls: false);
    }

    private static void DrawCore(
        EditorPresentationSnapshot snapshot,
        IDevToolPageView page,
        Num.Vector2 display,
        bool allowPageControls)
    {
        if (display.X <= 1f || display.Y <= 1f)
            return;

        // A Vanilla -> New UI switch can make both shell paths execute in the same render frame.
        // Draw this shared window only once so the transition cannot submit the same ImGui window
        // twice with different contents.
        int frame = ImGui.GetFrameCount();
        if (lastDrawFrame == frame)
            return;
        lastDrawFrame = frame;

        float width = Math.Max(
            320f,
            Math.Min(
                PreferredWidth,
                Math.Max(320f, display.X - 16f)));

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                Math.Max(8f, (display.X - width) * 0.5f),
                6f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(width, 0f),
            new Num.Vector2(width, Math.Max(120f, display.Y - 12f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Num.Vector2(13f, 9f));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Num.Vector2(9f, 7f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin(
                "##DevToolTopStatus",
                flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        DrawGlobalRow(snapshot);

        if (allowPageControls &&
            !EditorUiModeState.UseVanilla &&
            page?.HasTopControls == true)
        {
            ImGui.Separator();

            ImGui.SetWindowFontScale(1.14f);
            page.DrawTopControls(snapshot);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static void DrawGlobalRow(EditorPresentationSnapshot snapshot)
    {
        string room =
            snapshot?.Available == true &&
            !string.IsNullOrWhiteSpace(snapshot.RoomName)
                ? snapshot.RoomName
                : "-";

        string title;
        if (EditorUiModeState.UseVanilla)
        {
            title = room + " : Vanilla DevUI Active";
        }
        else if (snapshot?.Available != true)
        {
            title = DevToolUiSettings.T("DevTool 正在启动...", "DevTool starting...");
        }
        else
        {
            title = room + " : NewDevtool Active";
        }

        float modeWidth = ModeButtonWidth * 2f + SegmentGap;
        float languageWidth = LanguageButtonWidth * 2f + SegmentGap;
        float sideWidth = Math.Max(modeWidth, languageWidth) + 8f;
        float contentStartX = ImGui.GetCursorPosX();
        float available = ImGui.GetContentRegionAvail().X;

        // Use three real layout columns instead of drawing the centered title and then moving the
        // cursor backwards over the same row. Rewinding the cursor produced an overlapping-item
        // layout on the native ImGui backend and could terminate Rain World as soon as H opened
        // DevUI. Equal side columns keep the title centered against the whole top window.
        if (available >= sideWidth * 2f + 260f)
        {
            ImGui.Columns(3, "##DevToolTopGlobalColumns", false);
            ImGui.SetColumnWidth(0, sideWidth);
            ImGui.SetColumnWidth(1, Math.Max(260f, available - sideWidth * 2f));
            ImGui.SetColumnWidth(2, sideWidth);

            DrawModeSegment();

            ImGui.NextColumn();
            DevToolWidgets.CenteredPrimaryTitle(
                title,
                1.56f);

            ImGui.NextColumn();
            float rightOffset =
                Math.Max(
                    0f,
                    ImGui.GetColumnWidth() - languageWidth - ImGui.GetStyle().ItemSpacing.X);
            if (rightOffset > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + rightOffset);
            DrawLanguageSegment();

            ImGui.Columns(1);
            return;
        }

        // Narrow displays stack the centered title over one compact controls row. This avoids
        // overlap while preserving access to both global switches.
        DevToolWidgets.CenteredPrimaryTitle(
            title,
            1.46f);
        ImGui.Spacing();

        DrawModeSegment();
        ImGui.SameLine();

        float languageX =
            contentStartX + available - languageWidth;
        if (languageX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(languageX);
        DrawLanguageSegment();
    }

    private static void DrawModeSegment()
    {
        bool vanilla = EditorUiModeState.UseVanilla;

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新 UI", "New UI"),
                "TopModeNewUi",
                vanilla ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary,
                fullWidth: false,
                fixedWidth: ModeButtonWidth))
        {
            EditorUiModeState.SetVanilla(false);
        }

        ImGui.SameLine(0f, SegmentGap);
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("原版", "Vanilla"),
                "TopModeVanilla",
                vanilla ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fullWidth: false,
                fixedWidth: ModeButtonWidth))
        {
            EditorUiModeState.SetVanilla(true);
        }
    }

    private static void DrawLanguageSegment()
    {
        bool chinese =
            DevToolUiSettings.Language ==
            DevToolUiLanguage.Chinese;

        bool pushedChineseLabelFont =
            TryPushChineseLanguageLabelFont();

        bool chooseChinese =
            DevToolWidgets.ActionButton(
                "中文",
                "TopLanguageChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fullWidth: false,
                fixedWidth: LanguageButtonWidth);

        if (pushedChineseLabelFont)
            ImGui.PopFont();

        if (chooseChinese)
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);

        ImGui.SameLine(0f, SegmentGap);
        if (DevToolWidgets.ActionButton(
                "EN",
                "TopLanguageEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary,
                fullWidth: false,
                fixedWidth: LanguageButtonWidth))
        {
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);
        }
    }

    private static unsafe bool TryPushChineseLanguageLabelFont()
    {
        if (DevToolUiSettings.IsChinese)
            return false;

        if (!DevToolFontCatalog.TryResolveRegisteredFace(
                DevToolFontCatalog.DefaultChineseFamily,
                DevToolUiSettings.DefaultChineseFontWeight,
                requireChinese: true,
                out ImFontPtr chineseFont,
                out _,
                out _,
                out _))
        {
            return false;
        }

        ImFontPtr currentFont =
            ImGui.GetFont();

        if (chineseFont.NativePtr == null ||
            currentFont.NativePtr == null ||
            chineseFont.FontSize <= 0.01f ||
            currentFont.FontSize <= 0.01f ||
            !DevToolFrontend.TryPushRegisteredFont(
                chineseFont,
                "top-bar Chinese language label"))
        {
            return false;
        }

        return true;
    }
}
