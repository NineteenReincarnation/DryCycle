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
        var pages = DevToolPageViewRegistry.NavigationPages;
        string debugLabel = DevToolUiSettings.T("调试", "Debug");

        float widest = ImGui.CalcTextSize(debugLabel).X;
        for (int i = 0; i < pages.Count; i++)
            widest = Math.Max(widest, ImGui.CalcTextSize(pages[i].NavigationLabel).X);

        DevToolActivityPlacement placement =
            DevToolUserSettingsStore.ActivityPlacement;
        bool topDocked =
            placement == DevToolActivityPlacement.TopDocked;

        float defaultWidth =
            Math.Min(
                300f,
                Math.Max(
                    topDocked ? 184f : 170f,
                    widest + 48f));

        Num.Vector2 windowPosition =
            new(8f, 120f);

        if (topDocked &&
            DevToolTopStatusWindow.TryGetCurrentRect(
                out Num.Vector2 topPosition,
                out Num.Vector2 topSize))
        {
            // Touch the top surface by one pixel so the two windows read as one docked assembly
            // rather than two unrelated floating cards. Keep the tool palette aligned to the left
            // edge of the top status bar, which leaves the room center and right inspector visible.
            windowPosition =
                new Num.Vector2(
                    topPosition.X,
                    Math.Max(
                        0f,
                        topPosition.Y + topSize.Y - 1f));
        }

        float maxHeight =
            Math.Max(
                80f,
                display.Y -
                Math.Max(
                    16f,
                    windowPosition.Y + 8f));

        ImGui.SetNextWindowPos(
            windowPosition,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new Num.Vector2(
                Math.Min(defaultWidth, Math.Max(140f, display.X - 16f)),
                0f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(
                Math.Min(defaultWidth, Math.Max(140f, display.X - 16f)),
                0f),
            new Num.Vector2(
                Math.Min(defaultWidth, Math.Max(140f, display.X - 16f)),
                maxHeight));
        ImGui.SetNextWindowBgAlpha(
            topDocked
                ? Math.Min(1f, DevToolUiSettings.WindowAlpha + 0.04f)
                : DevToolUiSettings.WindowAlpha);

        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Num.Vector2(7f, 6f));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Num.Vector2(6f, 5f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(
                "##DevToolActivity",
                flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        DrawActivityPlacementHeader(placement);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        for (int i = 0; i < pages.Count; i++)
            DrawModeButton(pages[i], snapshot.ToolMode);

        if (DevToolWidgets.NavItem(debugLabel, "DevToolDebugWorkspace", debugWorkspacePage))
        {
            debugWorkspacePage = true;
            DevToolPageViewRegistry.DeactivateActive();
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
        }
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(DevToolUiSettings.T(
                "统一调试中心：性能、世界地图 Retained/缓存、兼容迁移、前端字体与生物专项诊断",
                "Unified diagnostics: performance, World Map retained/cache, compatibility, frontend/font and creature diagnostics"));
        }

        // Trigger and Sound own their compact workspace controls inside their dedicated
        // workspaces. Do not duplicate Browser/Inspector toggles in the global activity window.
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
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "调试信息统一收纳在独立工作区；选择上方任一常规工具即可返回。",
                "Diagnostics are collected in one dedicated workspace. Select any normal tool above to return."));
            FitActivityBarHeight(display);
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        string browserLabel = snapshot.BrowserOpen
            ? DevToolUiSettings.T("隐藏浏览器", "Hide Browser")
            : DevToolUiSettings.T("显示浏览器", "Show Browser");
        if (DevToolWidgets.ActionButton(browserLabel, "DevToolToggleBrowser", DevToolButtonTone.Subtle, true))
            Send(EditorUiCommandKind.ToggleBrowser);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏左栏浏览器", "Toggle left Browser pane"));

        string inspectorLabel = snapshot.InspectorOpen
            ? DevToolUiSettings.T("隐藏检查器", "Hide Inspector")
            : DevToolUiSettings.T("显示检查器", "Show Inspector");
        if (DevToolWidgets.ActionButton(inspectorLabel, "DevToolToggleInspector", DevToolButtonTone.Subtle, true))
            Send(EditorUiCommandKind.ToggleInspector);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏右栏检查器", "Toggle right Inspector pane"));

        FitActivityBarHeight(display);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static void DrawActivityPlacementHeader(DevToolActivityPlacement placement)
    {
        bool topDocked =
            placement == DevToolActivityPlacement.TopDocked;

        if (DevToolWidgets.ActionButton(
                "⇆",
                "DevToolActivityPlacement",
                DevToolButtonTone.Subtle,
                fullWidth: false,
                fixedWidth: 34f))
        {
            DevToolActivityPlacement next =
                topDocked
                    ? DevToolActivityPlacement.LeftSidebar
                    : DevToolActivityPlacement.TopDocked;

            DevToolUserSettingsStore.RememberActivityPlacement(next);

            bool movedTop =
                next == DevToolActivityPlacement.TopDocked;
            EditorShortcutFeedback.PublishCustom(
                movedTop ? "工具窗口已停靠到顶部" : "工具窗口已切换到左侧",
                movedTop ? "Tools docked below top bar" : "Tools moved to left sidebar",
                "⇆",
                true,
                EditorShortcutFeedbackVisual.Toggle);
        }

        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(
                topDocked
                    ? DevToolUiSettings.T("切换到左侧工具栏", "Move tools to the left sidebar")
                    : DevToolUiSettings.T("停靠到顶部状态栏下方", "Dock tools below the top status bar"));
        }

        ImGui.SameLine();
        float labelY =
            ImGui.GetCursorPosY() +
            Math.Max(
                0f,
                (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorPosY(labelY);
        ImGui.TextUnformatted(
            DevToolUiSettings.T("工具", "TOOLS"));
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
        string id = "DevToolMode:" + page.Id;
        if (DevToolWidgets.NavItem(page.NavigationLabel, id, !debugWorkspacePage && current == page.Mode))
        {
            debugWorkspacePage = false;
            DevToolDebugWorkspaceView.Deactivate();
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SetToolMode, mode: page.Mode));
        }

        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(page.NavigationTooltip))
            DevToolTooltip.Show(page.NavigationTooltip);
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
