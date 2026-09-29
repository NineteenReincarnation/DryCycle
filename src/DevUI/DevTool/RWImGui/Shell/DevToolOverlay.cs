using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared RWImGui editor chrome.
///
/// Page-specific browser, inspector, workspace, scene and placement capabilities belong to registered
/// page objects. This class owns only the common activity bar, Browser/Inspector shell, generic
/// placement input chrome and the unified diagnostics workspace.
/// </summary>
internal static class DevToolOverlay
{
    private static bool debugWorkspacePage;
    private static float browserInspectorSplit = 0.23f;
    private static bool browserInspectorSplitterDragging;
    private const float BrowserPaneFontScale = 1.22f;

    private static string placementLabelType = string.Empty;
    private static bool placementLabelChinese;
    private static string placementLabelText = string.Empty;
    private static bool pageBackgroundFaulted;

    internal static bool SuppressesSharedPageSurfaces => debugWorkspacePage;
    internal static bool IsDebugWorkspace => debugWorkspacePage;

    internal static void ReloadPersistedPresentationState()
    {
        browserInspectorSplit =
            DevToolUserSettingsStore.BrowserInspectorSplit;
    }

    internal static void Draw(EditorPresentationSnapshot snapshot, DevToolUiFrameContext frameContext)
    {
        ImGuiIOPtr io = frameContext.Io;
        Num.Vector2 display = frameContext.DisplaySize;
        IDevToolPageView page = debugWorkspacePage ? null : DevToolPageViewRegistry.Get(snapshot.ToolMode);

        if (!debugWorkspacePage && !pageBackgroundFaulted && page != null)
        {
            try
            {
                page.DrawBackground(snapshot, display);
            }
            catch (Exception error)
            {
                pageBackgroundFaulted = true;
                global::DryCycle.Plugin.Logger?.LogError(
                    "DevTool page background failed and was isolated from shared editor chrome. " +
                    error);
            }
        }

        // The compact top status surface is submitted by BridgePlugin after every normal
        // workspace/window so it owns the highest regular UI layer. ShortcutWindow remains part of
        // the shared editor chrome here.
        ShortcutWindow.Draw(snapshot, display);

        if (debugWorkspacePage)
        {
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
            DevToolDebugWorkspaceView.Draw(snapshot, display);
            return;
        }

        DevToolDebugWorkspaceView.Deactivate();
        if (!snapshot.FocusMode)
        {
            if (page?.UsesDedicatedWorkspace == true)
                page.DrawWorkspace(snapshot, display);
            else if (snapshot.BrowserOpen || snapshot.InspectorOpen)
                DrawBrowserInspectorPanel(snapshot, display, page);
        }

        HandlePlacement(snapshot, display, io, page);
    }

    internal static void ResetRetainedState()
    {
        browserInspectorSplitterDragging = false;
        placementLabelType = string.Empty;
        placementLabelChinese = false;
        placementLabelText = string.Empty;
        debugWorkspacePage = false;
        pageBackgroundFaulted = false;
        ShortcutWindow.ResetRetainedState();
        DevToolDebugWorkspaceView.ResetRetainedState();
    }

    internal static void DrawActivityBarAfterTop(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || snapshot.FocusMode)
            return;

        DrawActivityBar(snapshot, display);
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        DevToolActivityPlacement placement =
            DevToolUserSettingsStore.ActivityPlacement;

        if (placement == DevToolActivityPlacement.TopDocked)
        {
            DrawTopDockedActivityBar(snapshot, display);
            return;
        }

        DrawLeftActivityBar(snapshot, display);
    }

    /// <summary>
    /// Default tool navigation: one compact horizontal strip attached directly below the shared
    /// NewDevtool status surface. Primary tools flow left-to-right. On unusually narrow displays or
    /// very large font scales the flow wraps to another horizontal row instead of degrading back
    /// into the old vertical column or clipping labels.
    /// </summary>
    private static void DrawTopDockedActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        var pages =
            DevToolPageViewRegistry.NavigationPages;

        float width =
            Math.Max(
                320f,
                Math.Min(
                    780f,
                    Math.Max(
                        320f,
                        display.X - 16f)));
        Num.Vector2 position =
            new(
                Math.Max(
                    8f,
                    (display.X - width) * 0.5f),
                62f);

        if (DevToolTopStatusWindow.TryGetCurrentRect(
                out Num.Vector2 topPosition,
                out Num.Vector2 topSize))
        {
            width =
                Math.Max(
                    220f,
                    Math.Min(
                        topSize.X,
                        Math.Max(
                            220f,
                            display.X - topPosition.X - 8f)));
            position =
                new Num.Vector2(
                    topPosition.X,
                    Math.Max(
                        0f,
                        topPosition.Y + topSize.Y - 1f));
        }

        ImGui.SetNextWindowPos(
            position,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new Num.Vector2(
                width,
                0f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(
                width,
                0f),
            new Num.Vector2(
                width,
                Math.Max(
                    72f,
                    display.Y - position.Y - 8f)));
        ImGui.SetNextWindowBgAlpha(
            Math.Min(
                1f,
                DevToolUiSettings.WindowAlpha + 0.055f));

        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Num.Vector2(
                8f,
                6f));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Num.Vector2(
                5f,
                4f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(
                "##DevToolActivityTop",
                flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        DrawTopActivityFlow(
            snapshot,
            pages);

        FitActivityBarHeight(display);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static void DrawTopActivityFlow(
        EditorPresentationSnapshot snapshot,
        System.Collections.Generic.IReadOnlyList<IDevToolFrontendPage> pages)
    {
        float left =
            ImGui.GetCursorPosX();
        float right =
            ImGui.GetWindowContentRegionMax().X;
        float x =
            left;
        float y =
            ImGui.GetCursorPosY();
        float gap =
            4f;
        float rowHeight =
            Math.Max(
                29f,
                ImGui.GetTextLineHeight() + 12f);

        ImGui.SetCursorPos(
            new Num.Vector2(
                x,
                y));
        if (DrawDockSwitchButton(
                DevToolActivityPlacement.TopDocked,
                new Num.Vector2(
                    32f,
                    rowHeight)))
        {
            ToggleActivityPlacement(
                DevToolActivityPlacement.TopDocked);
        }

        x +=
            32f + gap + 2f;

        for (int i = 0; i < pages.Count; i++)
        {
            IDevToolFrontendPage page =
                pages[i];
            float itemWidth =
                TopActivityItemWidth(
                    page.NavigationLabel);

            PlaceTopActivityItem(
                page.NavigationLabel,
                "DevToolTopMode:" + page.Id,
                !debugWorkspacePage &&
                snapshot.ToolMode == page.Mode,
                itemWidth,
                rowHeight,
                ref x,
                ref y,
                left,
                right,
                gap,
                () =>
                {
                    debugWorkspacePage = false;
                    DevToolDebugWorkspaceView.Deactivate();
                    EditorUiCommandQueue.Enqueue(
                        new EditorUiCommand(
                            EditorUiCommandKind.SetToolMode,
                            mode: page.Mode));
                });

            if (ImGui.IsItemHovered() &&
                !string.IsNullOrEmpty(
                    page.NavigationTooltip))
            {
                DevToolTooltip.Show(
                    page.NavigationTooltip);
            }
        }

        string debugLabel =
            DevToolUiSettings.T(
                "调试",
                "Debug");
        PlaceTopActivityItem(
            debugLabel,
            "DevToolTopDebug",
            debugWorkspacePage,
            TopActivityItemWidth(debugLabel),
            rowHeight,
            ref x,
            ref y,
            left,
            right,
            gap,
            () =>
            {
                debugWorkspacePage = true;
                DevToolPageViewRegistry.DeactivateActive();
                if (snapshot.PlacementActive)
                    Send(EditorUiCommandKind.CancelPlacement);
            });
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(
                DevToolUiSettings.T(
                    "统一调试中心：性能、世界地图 Retained/缓存、兼容迁移、前端字体与生物专项诊断",
                    "Unified diagnostics: performance, World Map retained/cache, compatibility, frontend/font and creature diagnostics"));
        }

        bool showPaneToggles =
            !debugWorkspacePage &&
            snapshot.ToolMode != EditorToolMode.Triggers &&
            snapshot.ToolMode != EditorToolMode.Sound;

        if (showPaneToggles)
        {
            x +=
                6f;

            string browserLabel =
                DevToolUiSettings.T(
                    "浏览器",
                    "Browser");
            PlaceTopActivityItem(
                browserLabel,
                "DevToolTopBrowser",
                snapshot.BrowserOpen,
                TopActivityUtilityWidth(browserLabel),
                rowHeight,
                ref x,
                ref y,
                left,
                right,
                gap,
                () =>
                    Send(EditorUiCommandKind.ToggleBrowser));
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    snapshot.BrowserOpen
                        ? DevToolUiSettings.T(
                            "隐藏浏览器",
                            "Hide Browser")
                        : DevToolUiSettings.T(
                            "显示浏览器",
                            "Show Browser"));
            }

            string inspectorLabel =
                DevToolUiSettings.T(
                    "检查器",
                    "Inspector");
            PlaceTopActivityItem(
                inspectorLabel,
                "DevToolTopInspector",
                snapshot.InspectorOpen,
                TopActivityUtilityWidth(inspectorLabel),
                rowHeight,
                ref x,
                ref y,
                left,
                right,
                gap,
                () =>
                    Send(EditorUiCommandKind.ToggleInspector));
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    snapshot.InspectorOpen
                        ? DevToolUiSettings.T(
                            "隐藏检查器",
                            "Hide Inspector")
                        : DevToolUiSettings.T(
                            "显示检查器",
                            "Show Inspector"));
            }
        }

        // Advance the normal ImGui cursor past the absolute-positioned flow. This keeps the
        // content-owned window height correct for one-row and wrapped layouts alike.
        ImGui.SetCursorPos(
            new Num.Vector2(
                left,
                y + rowHeight));
    }

    private static void PlaceTopActivityItem(
        string label,
        string id,
        bool active,
        float width,
        float height,
        ref float x,
        ref float y,
        float left,
        float right,
        float gap,
        Action pressed)
    {
        if (x > left &&
            x + width > right)
        {
            x =
                left;
            y +=
                height + gap;
        }

        ImGui.SetCursorPos(
            new Num.Vector2(
                x,
                y));

        if (DrawTopActivityButton(
                label,
                id,
                active,
                new Num.Vector2(
                    width,
                    height)))
        {
            pressed?.Invoke();
        }

        x +=
            width + gap;
    }

    private static float TopActivityItemWidth(string label) =>
        Math.Max(
            48f,
            Math.Min(
                124f,
                ImGui.CalcTextSize(
                    label ?? string.Empty).X + 22f));

    private static float TopActivityUtilityWidth(string label) =>
        Math.Max(
            60f,
            Math.Min(
                112f,
                ImGui.CalcTextSize(
                    label ?? string.Empty).X + 20f));

    private static bool DrawTopActivityButton(
        string label,
        string id,
        bool active,
        Num.Vector2 size)
    {
        Num.Vector4 normal =
            active
                ? new Num.Vector4(
                    0.19f,
                    0.39f,
                    0.64f,
                    0.94f)
                : new Num.Vector4(
                    0.085f,
                    0.105f,
                    0.135f,
                    0.88f);
        Num.Vector4 hovered =
            active
                ? new Num.Vector4(
                    0.24f,
                    0.49f,
                    0.78f,
                    0.98f)
                : new Num.Vector4(
                    0.13f,
                    0.19f,
                    0.28f,
                    0.96f);
        Num.Vector4 pressed =
            new(
                0.27f,
                0.54f,
                0.86f,
                1f);
        Num.Vector4 border =
            active
                ? new Num.Vector4(
                    0.39f,
                    0.70f,
                    1f,
                    0.92f)
                : new Num.Vector4(
                    0.30f,
                    0.36f,
                    0.44f,
                    0.58f);

        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameRounding,
            5f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameBorderSize,
            active ? 1.35f : 0.75f);
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            normal);
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            hovered);
        ImGui.PushStyleColor(
            ImGuiCol.ButtonActive,
            pressed);
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            border);

        ImGui.PushID(
            id ?? string.Empty);
        bool clicked =
            ImGui.Button(
                label ?? string.Empty,
                size);
        ImGui.PopID();

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        if (active &&
            max.X - min.X > 8f)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                new Num.Vector2(
                    min.X + 5f,
                    max.Y - 3f),
                new Num.Vector2(
                    max.X - 5f,
                    max.Y - 1f),
                ImGui.GetColorU32(
                    new Num.Vector4(
                        0.46f,
                        0.79f,
                        1f,
                        0.96f)),
                1f);
        }

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    /// <summary>
    /// Alternative compact sidebar. Unlike the default top strip it stays vertical, but shares the
    /// same active accent language and glyph-independent layout switch control.
    /// </summary>
    private static void DrawLeftActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        var pages =
            DevToolPageViewRegistry.NavigationPages;
        string debugLabel =
            DevToolUiSettings.T(
                "调试",
                "Debug");

        float widest =
            ImGui.CalcTextSize(
                debugLabel).X;
        for (int i = 0; i < pages.Count; i++)
        {
            widest =
                Math.Max(
                    widest,
                    ImGui.CalcTextSize(
                        pages[i].NavigationLabel).X);
        }

        float width =
            Math.Min(
                300f,
                Math.Max(
                    170f,
                    widest + 48f));
        Num.Vector2 position =
            new(
                8f,
                120f);

        ImGui.SetNextWindowPos(
            position,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new Num.Vector2(
                width,
                0f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(
                width,
                0f),
            new Num.Vector2(
                width,
                Math.Max(
                    80f,
                    display.Y - position.Y - 8f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Num.Vector2(
                7f,
                6f));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Num.Vector2(
                6f,
                5f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(
                "##DevToolActivityLeft",
                flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        DrawLeftActivityHeader();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        for (int i = 0; i < pages.Count; i++)
            DrawModeButton(
                pages[i],
                snapshot.ToolMode);

        if (DrawSidebarNavButton(
                debugLabel,
                "DevToolDebugWorkspace",
                debugWorkspacePage))
        {
            debugWorkspacePage = true;
            DevToolPageViewRegistry.DeactivateActive();
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
        }
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(
                DevToolUiSettings.T(
                    "统一调试中心：性能、世界地图 Retained/缓存、兼容迁移、前端字体与生物专项诊断",
                    "Unified diagnostics: performance, World Map retained/cache, compatibility, frontend/font and creature diagnostics"));
        }

        if (!debugWorkspacePage &&
            (snapshot.ToolMode == EditorToolMode.Triggers ||
             snapshot.ToolMode == EditorToolMode.Sound))
        {
            FitActivityBarHeight(display);
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (debugWorkspacePage)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "调试信息统一收纳在独立工作区；选择上方任一常规工具即可返回。",
                    "Diagnostics are collected in one dedicated workspace. Select any normal tool above to return."));
            FitActivityBarHeight(display);
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        string browserLabel =
            snapshot.BrowserOpen
                ? DevToolUiSettings.T(
                    "隐藏浏览器",
                    "Hide Browser")
                : DevToolUiSettings.T(
                    "显示浏览器",
                    "Show Browser");
        if (DevToolWidgets.ActionButton(
                browserLabel,
                "DevToolToggleBrowser",
                DevToolButtonTone.Subtle,
                true))
        {
            Send(
                EditorUiCommandKind.ToggleBrowser);
        }
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(
                DevToolUiSettings.T(
                    "显示/隐藏左栏浏览器",
                    "Toggle left Browser pane"));
        }

        string inspectorLabel =
            snapshot.InspectorOpen
                ? DevToolUiSettings.T(
                    "隐藏检查器",
                    "Hide Inspector")
                : DevToolUiSettings.T(
                    "显示检查器",
                    "Show Inspector");
        if (DevToolWidgets.ActionButton(
                inspectorLabel,
                "DevToolToggleInspector",
                DevToolButtonTone.Subtle,
                true))
        {
            Send(
                EditorUiCommandKind.ToggleInspector);
        }
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(
                DevToolUiSettings.T(
                    "显示/隐藏右栏检查器",
                    "Toggle right Inspector pane"));
        }

        FitActivityBarHeight(display);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static void DrawLeftActivityHeader()
    {
        float rowHeight =
            Math.Max(
                29f,
                ImGui.GetTextLineHeight() + 12f);

        if (DrawDockSwitchButton(
                DevToolActivityPlacement.LeftSidebar,
                new Num.Vector2(
                    32f,
                    rowHeight)))
        {
            ToggleActivityPlacement(
                DevToolActivityPlacement.LeftSidebar);
        }

        ImGui.SameLine(
            0f,
            7f);
        float labelY =
            ImGui.GetCursorPosY() +
            Math.Max(
                0f,
                (rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorPosY(
            labelY);
        ImGui.TextUnformatted(
            DevToolUiSettings.T(
                "工具",
                "TOOLS"));
    }

    /// <summary>
    /// Draw the layout-switch symbol entirely with primitives. The previous Unicode swap glyph was
    /// not guaranteed to exist in every active Rain World / RWImGui font and could degrade to '?'.
    /// </summary>
    private static bool DrawDockSwitchButton(
        DevToolActivityPlacement placement,
        Num.Vector2 size)
    {
        ImGui.PushID(
            "DevToolActivityPlacement");
        bool clicked =
            ImGui.InvisibleButton(
                "##DockSwitch",
                size);
        bool hovered =
            ImGui.IsItemHovered();
        bool held =
            ImGui.IsItemActive();
        ImGui.PopID();

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();

        Num.Vector4 background =
            held
                ? new Num.Vector4(
                    0.20f,
                    0.43f,
                    0.70f,
                    0.98f)
                : hovered
                    ? new Num.Vector4(
                        0.14f,
                        0.25f,
                        0.39f,
                        0.96f)
                    : new Num.Vector4(
                        0.09f,
                        0.12f,
                        0.16f,
                        0.90f);
        Num.Vector4 border =
            hovered || held
                ? new Num.Vector4(
                    0.43f,
                    0.76f,
                    1f,
                    0.94f)
                : new Num.Vector4(
                    0.34f,
                    0.40f,
                    0.48f,
                    0.72f);

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                background),
            5f);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                border),
            5f,
            ImDrawFlags.None,
            1f);

        float iconWidth =
            17f;
        float iconHeight =
            14f;
        Num.Vector2 iconMin =
            new(
                (min.X + max.X - iconWidth) * 0.5f,
                (min.Y + max.Y - iconHeight) * 0.5f);
        Num.Vector2 iconMax =
            iconMin +
            new Num.Vector2(
                iconWidth,
                iconHeight);

        uint outline =
            ImGui.GetColorU32(
                new Num.Vector4(
                    0.76f,
                    0.82f,
                    0.90f,
                    0.92f));
        uint accent =
            ImGui.GetColorU32(
                new Num.Vector4(
                    0.42f,
                    0.78f,
                    1f,
                    1f));
        uint muted =
            ImGui.GetColorU32(
                new Num.Vector4(
                    0.39f,
                    0.46f,
                    0.55f,
                    0.74f));

        draw.AddRect(
            iconMin,
            iconMax,
            outline,
            2f,
            ImDrawFlags.None,
            1f);

        // Two miniature dock rails communicate both available placements without any font glyph.
        draw.AddLine(
            new Num.Vector2(
                iconMin.X + 2f,
                iconMin.Y + 3.2f),
            new Num.Vector2(
                iconMax.X - 2f,
                iconMin.Y + 3.2f),
            placement == DevToolActivityPlacement.TopDocked
                ? accent
                : muted,
            placement == DevToolActivityPlacement.TopDocked
                ? 2.4f
                : 1.4f);
        draw.AddLine(
            new Num.Vector2(
                iconMin.X + 3.2f,
                iconMin.Y + 2f),
            new Num.Vector2(
                iconMin.X + 3.2f,
                iconMax.Y - 2f),
            placement == DevToolActivityPlacement.LeftSidebar
                ? accent
                : muted,
            placement == DevToolActivityPlacement.LeftSidebar
                ? 2.4f
                : 1.4f);

        // Small center chevron made from lines: readable as a layout switch without relying on
        // arrows from the current font atlas.
        Num.Vector2 center =
            new(
                (iconMin.X + iconMax.X) * 0.5f + 1f,
                (iconMin.Y + iconMax.Y) * 0.5f + 1f);
        draw.AddLine(
            center + new Num.Vector2(-3f, -2f),
            center + new Num.Vector2(2.5f, -2f),
            outline,
            1.2f);
        draw.AddLine(
            center + new Num.Vector2(2.5f, -2f),
            center + new Num.Vector2(0.5f, -4f),
            outline,
            1.2f);
        draw.AddLine(
            center + new Num.Vector2(2.5f, -2f),
            center + new Num.Vector2(0.5f, 0f),
            outline,
            1.2f);

        if (hovered)
        {
            DevToolTooltip.Show(
                placement == DevToolActivityPlacement.TopDocked
                    ? DevToolUiSettings.T(
                        "切换到左侧工具栏",
                        "Move tools to the left sidebar")
                    : DevToolUiSettings.T(
                        "切换到顶部横向工具栏",
                        "Dock tools as a horizontal top bar"));
        }

        return clicked;
    }

    private static void ToggleActivityPlacement(
        DevToolActivityPlacement current)
    {
        DevToolActivityPlacement next =
            current == DevToolActivityPlacement.TopDocked
                ? DevToolActivityPlacement.LeftSidebar
                : DevToolActivityPlacement.TopDocked;

        DevToolUserSettingsStore.RememberActivityPlacement(
            next);

        bool movedTop =
            next == DevToolActivityPlacement.TopDocked;
        EditorShortcutFeedback.PublishCustom(
            movedTop
                ? "工具栏已切换为顶部横向布局"
                : "工具栏已切换到左侧",
            movedTop
                ? "Tools switched to horizontal top layout"
                : "Tools moved to left sidebar",
            "TOOLS LAYOUT",
            true,
            EditorShortcutFeedbackVisual.Toggle);
    }

    private static bool DrawSidebarNavButton(
        string label,
        string id,
        bool active)
    {
        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameRounding,
            4.5f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FrameBorderSize,
            active ? 1.25f : 0.65f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Num.Vector2(
                10f,
                6f));
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            active
                ? new Num.Vector4(0.18f, 0.38f, 0.63f, 0.94f)
                : new Num.Vector4(0.09f, 0.11f, 0.14f, 0.89f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            active
                ? new Num.Vector4(0.23f, 0.48f, 0.77f, 0.98f)
                : new Num.Vector4(0.14f, 0.19f, 0.27f, 0.96f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonActive,
            new Num.Vector4(0.27f, 0.54f, 0.86f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            active
                ? new Num.Vector4(0.40f, 0.72f, 1f, 0.92f)
                : new Num.Vector4(0.33f, 0.38f, 0.45f, 0.62f));

        ImGui.PushID(
            id ?? string.Empty);
        bool clicked =
            ImGui.Button(
                label ?? string.Empty,
                new Num.Vector2(
                    -1f,
                    0f));
        ImGui.PopID();

        if (active)
        {
            Num.Vector2 min =
                ImGui.GetItemRectMin();
            Num.Vector2 max =
                ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddRectFilled(
                new Num.Vector2(
                    min.X + 1f,
                    min.Y + 4f),
                new Num.Vector2(
                    min.X + 3f,
                    max.Y - 4f),
                ImGui.GetColorU32(
                    new Num.Vector4(
                        0.46f,
                        0.79f,
                        1f,
                        0.98f)),
                1f);
        }

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(3);
        return clicked;
    }

    private static void FitActivityBarHeight(Num.Vector2 display)
    {
        ImGuiStylePtr style = ImGui.GetStyle();

        // CursorPosY is window-local and already includes title-bar/content offsets. Adding the
        // bottom window padding gives the exact content-driven outer height without guessing how
        // many navigation buttons exist or what UI scale/language is active.
        float desiredHeight =
            ImGui.GetCursorPosY() +
            Math.Max(1f, style.WindowPadding.Y);

        float maxHeight =
            Math.Max(
                80f,
                display.Y -
                Math.Max(16f, ImGui.GetWindowPos().Y + 8f));

        desiredHeight =
            Math.Max(
                ImGui.GetFrameHeight() + style.WindowPadding.Y * 2f,
                Math.Min(desiredHeight, maxHeight));

        Num.Vector2 current = ImGui.GetWindowSize();
        if (Math.Abs(current.Y - desiredHeight) <= 0.5f)
            return;

        // Preserve the developer's current width while making vertical size strictly content-owned.
        ImGui.SetWindowSize(
            new Num.Vector2(current.X, desiredHeight),
            ImGuiCond.Always);
    }

    private static void DrawModeButton(IDevToolFrontendPage page, EditorToolMode current)
    {
        string id =
            "DevToolMode:" + page.Id;
        if (DrawSidebarNavButton(
                page.NavigationLabel,
                id,
                !debugWorkspacePage &&
                current == page.Mode))
        {
            debugWorkspacePage = false;
            DevToolDebugWorkspaceView.Deactivate();
            EditorUiCommandQueue.Enqueue(
                new EditorUiCommand(
                    EditorUiCommandKind.SetToolMode,
                    mode: page.Mode));
        }

        if (ImGui.IsItemHovered() &&
            !string.IsNullOrEmpty(
                page.NavigationTooltip))
        {
            DevToolTooltip.Show(
                page.NavigationTooltip);
        }
    }

    private static void DrawBrowserInspectorPanel(
        EditorPresentationSnapshot snapshot,
        Num.Vector2 display,
        IDevToolPageView page)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));

        // Keep the default Editor Panel compact and balanced instead of stretching it across almost
        // the entire display. The target is roughly the proportions of the room-settings reference:
        // about 45% of a 16:9 desktop width with a ~1.38:1 panel aspect, while still adapting to
        // smaller resolutions and UI scale.
        float scaledTargetWidth =
            700f *
            Math.Min(
                1.16f,
                scale);
        float defaultWidth = Math.Min(
            Math.Max(
                scaledTargetWidth,
                display.X * 0.45f),
            Math.Min(
                860f *
                Math.Min(
                    1.10f,
                    scale),
                Math.Max(
                    620f,
                    display.X - 32f)));

        float defaultHeight = Math.Min(
            Math.Max(
                440f,
                defaultWidth * 0.72f),
            Math.Max(
                320f,
                display.Y - 120f));

        Num.Vector2 defaultPos = new(
            (display.X - defaultWidth) * 0.5f,
            Math.Max(92f, (display.Y - defaultHeight) * 0.50f));

        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, defaultHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(520f, 300f),
            new Num.Vector2(Math.Max(520f, display.X - 16f), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("编辑面板###DevToolBrowserInspector", "Editor Panel###DevToolBrowserInspector"),
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            ImGui.End();
            return;
        }

        // Persisted geometry is restored by FloatingWindowSnap after Begin. Defaults above now
        // apply only to windows the developer has never authored; resolution changes clamp rather
        // than resetting a saved layout.
        FloatingWindowSnap.TrackCurrentWindow("BrowserInspector");

        bool suppressInspector = page?.SuppressInspector(snapshot) == true;
        bool browser = snapshot.BrowserOpen;
        bool inspector = snapshot.InspectorOpen && !suppressInspector;

        if (suppressInspector && !browser && snapshot.InspectorOpen)
            browser = true;

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f)
        {
            ImGui.End();
            return;
        }

        if (browser && inspector)
        {
            float splitterWidth = Math.Max(12f, 8f * Math.Min(1.5f, scale));
            float minLeft = Math.Min(available.X * 0.25f, Math.Max(100f, 120f * Math.Min(1f, scale)));
            float minRight = Math.Min(available.X * 0.55f, Math.Max(180f, 220f * Math.Min(1.25f, scale)));
            float usable = Math.Max(1f, available.X - splitterWidth);
            float maxLeft = Math.Max(minLeft, usable - minRight);
            float leftWidth = Math.Max(minLeft, Math.Min(usable * browserInspectorSplit, maxLeft));

            if (ImGui.BeginChild("##DevToolBrowserPane", new Num.Vector2(leftWidth, available.Y), ImGuiChildFlags.Borders))
            {
                ImGui.SetWindowFontScale(BrowserPaneFontScale);
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("浏览器", "BROWSER"), BrowserPaneFontScale);
                DrawBrowserContents(snapshot, page);
            }
            ImGui.EndChild();

            ImGui.SameLine(0f, 0f);
            ImGui.InvisibleButton("##DevToolBrowserInspectorSplitter", new Num.Vector2(splitterWidth, available.Y));
            bool splitterHovered = ImGui.IsItemHovered();
            if (!browserInspectorSplitterDragging && splitterHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                browserInspectorSplitterDragging = true;
            if (browserInspectorSplitterDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                browserInspectorSplitterDragging = false;

            Num.Vector2 splitMin = ImGui.GetItemRectMin();
            Num.Vector2 splitMax = ImGui.GetItemRectMax();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float lineX = (splitMin.X + splitMax.X) * 0.5f;
            uint lineColor = ImGui.GetColorU32(splitterHovered || browserInspectorSplitterDragging
                ? ImGuiCol.HeaderActive
                : ImGuiCol.Separator);
            draw.AddLine(
                new Num.Vector2(lineX, splitMin.Y),
                new Num.Vector2(lineX, splitMax.Y),
                lineColor,
                browserInspectorSplitterDragging ? 3f : 1.5f);

            if (browserInspectorSplitterDragging && usable > 1f)
            {
                float nextLeft = leftWidth + ImGui.GetIO().MouseDelta.X;
                nextLeft = Math.Max(minLeft, Math.Min(nextLeft, maxLeft));
                browserInspectorSplit = nextLeft / usable;
                DevToolUserSettingsStore.RememberBrowserInspectorSplit(
                    browserInspectorSplit);
            }

            ImGui.SameLine(0f, 0f);
            if (ImGui.BeginChild("##DevToolInspectorPane", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("检查器", "INSPECTOR"));
                DrawInspectorContents(snapshot, page);
            }
            ImGui.EndChild();
        }
        else if (browser)
        {
            browserInspectorSplitterDragging = false;
            if (ImGui.BeginChild("##DevToolBrowserPaneFull", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                ImGui.SetWindowFontScale(BrowserPaneFontScale);
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("浏览器", "BROWSER"), BrowserPaneFontScale);
                DrawBrowserContents(snapshot, page);
            }
            ImGui.EndChild();
        }
        else if (inspector)
        {
            browserInspectorSplitterDragging = false;
            if (ImGui.BeginChild("##DevToolInspectorPaneFull", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("检查器", "INSPECTOR"));
                DrawInspectorContents(snapshot, page);
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private static void DrawBrowserContents(EditorPresentationSnapshot snapshot, IDevToolPageView page)
    {
        if (page == null)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前工具不可用。", "Tools unavailable."));
            return;
        }

        page.DrawBrowser(snapshot);
        ScopedScrollChrome.Draw("Browser");
    }

    private static void DrawInspectorContents(EditorPresentationSnapshot snapshot, IDevToolPageView page)
    {
        if (page == null)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前检查器不可用。", "Inspector unavailable."));
            return;
        }

        page.DrawInspector(snapshot);
        ScopedScrollChrome.Draw("Inspector");
    }

    private static void HandlePlacement(
        EditorPresentationSnapshot snapshot,
        Num.Vector2 display,
        ImGuiIOPtr io,
        IDevToolPageView page)
    {
        if (!snapshot.PlacementActive || page?.SupportsPlacementInput != true) return;

        bool overWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        if (!overWindow && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Send(EditorUiCommandKind.CancelPlacement);
            return;
        }

        if (!overWindow && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.PlaceObjectAtCursor, flag: io.KeyShift));

        Num.Vector2 mouse = io.MousePos;
        Num.Vector2 hintSize = new(260f, 52f);
        Num.Vector2 pos = new(
            Math.Min(Math.Max(8f, mouse.X + 18f), Math.Max(8f, display.X - hintSize.X - 8f)),
            Math.Min(Math.Max(8f, mouse.Y + 18f), Math.Max(8f, display.Y - hintSize.Y - 8f)));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(hintSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.PopupAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##DevToolPlacementHint", flags))
        {
            ImGui.Text(GetPlacementLabel(snapshot.PlacementType));
            ImGui.TextDisabled(io.KeyShift
                ? DevToolUiSettings.T("连续放置", "Continuous placement")
                : DevToolUiSettings.T("单次放置", "Single placement"));
        }
        ImGui.End();
    }

    private static string GetPlacementLabel(string type)
    {
        type ??= string.Empty;
        bool chinese = DevToolUiSettings.IsChinese;
        if (string.Equals(placementLabelType, type, StringComparison.Ordinal) &&
            placementLabelChinese == chinese && placementLabelText.Length > 0)
            return placementLabelText;

        placementLabelType = type;
        placementLabelChinese = chinese;
        placementLabelText = (chinese ? "正在放置 " : "Placing ") + type;
        return placementLabelText;
    }

    private static void Send(EditorUiCommandKind kind) =>
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(kind));
}
