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
        float rowStartX = ImGui.GetCursorPosX();
        float rowStartY = ImGui.GetCursorPosY();
        float rowWidth = ImGui.GetContentRegionAvail().X;

        string room =
            snapshot?.Available == true &&
            !string.IsNullOrWhiteSpace(snapshot.RoomName)
                ? snapshot.RoomName
                : "-";

        string title =
            room +
            (EditorUiModeState.UseVanilla
                ? " : Vanilla DevUI Active"
                : " : NewDevtool Active");

        // Draw the title first across the complete row. We then return the cursor to the same row
        // for the left/right segmented controls, so the title stays centered against the window
        // rather than being shifted by whichever localized button label happens to be wider.
        DevToolWidgets.CenteredPrimaryTitle(
            title,
            1.56f);

        float titleEndY = ImGui.GetCursorPosY();
        float frameHeight = ImGui.GetFrameHeight();
        float controlsY =
            rowStartY +
            Math.Max(
                0f,
                (titleEndY - rowStartY - frameHeight) * 0.5f);

        float modeWidth = ModeButtonWidth * 2f + SegmentGap;
        float languageWidth = LanguageButtonWidth * 2f + SegmentGap;
        bool enoughWidth =
            rowWidth >= modeWidth + languageWidth + 300f;

        if (enoughWidth)
        {
            ImGui.SetCursorPos(
                new Num.Vector2(
                    rowStartX,
                    controlsY));
            DrawModeSegment();

            ImGui.SetCursorPos(
                new Num.Vector2(
                    rowStartX + rowWidth - languageWidth,
                    controlsY));
            DrawLanguageSegment();

            float controlsEndY =
                ImGui.GetItemRectMax().Y -
                ImGui.GetWindowPos().Y;
            ImGui.SetCursorPos(
                new Num.Vector2(
                    rowStartX,
                    Math.Max(titleEndY, controlsEndY)));
            return;
        }

        // Very narrow displays keep the same controls but move them to a compact second row rather
        // than allowing them to collide with the centered room title.
        ImGui.SetCursorPos(
            new Num.Vector2(
                rowStartX,
                titleEndY));

        DrawModeSegment();
        ImGui.SameLine();
        float rightX = rowStartX + rowWidth - languageWidth;
        if (rightX > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(rightX);
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
