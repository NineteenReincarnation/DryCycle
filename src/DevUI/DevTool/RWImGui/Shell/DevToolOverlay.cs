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
/// placement input chrome and the standalone Lance Scavenger debug surface.
/// </summary>
internal static class DevToolOverlay
{
    private static bool lanceDebugPage;
    private static float browserInspectorSplit = 0.23f;
    private static float browserInspectorDisplayWidth;
    private static bool browserInspectorSplitterDragging;
    private const float BrowserPaneFontScale = 1.22f;

    private static string placementLabelType = string.Empty;
    private static bool placementLabelChinese;
    private static string placementLabelText = string.Empty;
    private static bool pageBackgroundFaulted;

    internal static bool SuppressesSharedPageSurfaces => lanceDebugPage;
    internal static bool IsDebugWorkspace => lanceDebugPage;

    internal static void Draw(EditorPresentationSnapshot snapshot, DevToolUiFrameContext frameContext)
    {
        ImGuiIOPtr io = frameContext.Io;
        Num.Vector2 display = frameContext.DisplaySize;
        IDevToolPageView page = lanceDebugPage ? null : DevToolPageViewRegistry.Get(snapshot.ToolMode);

        if (!lanceDebugPage && !pageBackgroundFaulted && page != null)
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

        // Control Center and the collapsed shortcut orb are shared editor chrome rather than page content.
        ControlCenterWindow.Draw(snapshot, display);
        ShortcutWindow.Draw(snapshot, display);

        if (!snapshot.FocusMode)
            DrawActivityBar(snapshot, display);

        if (lanceDebugPage)
        {
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
            LanceScavengerDebugView.Draw(snapshot, display);
            return;
        }

        LanceScavengerDebugView.StopCapture();
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
        lanceDebugPage = false;
        pageBackgroundFaulted = false;
        ShortcutWindow.ResetRetainedState();
        LanceScavengerDebugView.StopCapture();
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        var pages = DevToolPageViewRegistry.NavigationPages;
        string debugLabel = DevToolUiSettings.T("调试", "Debug");

        float widest = ImGui.CalcTextSize(debugLabel).X;
        for (int i = 0; i < pages.Count; i++)
            widest = Math.Max(widest, ImGui.CalcTextSize(pages[i].NavigationLabel).X);

        float defaultWidth = Math.Min(380f, Math.Max(170f, widest + 48f));

        ImGui.SetNextWindowPos(new Num.Vector2(8f, 120f), ImGuiCond.FirstUseEver);
        // Y=0 asks ImGui to auto-fit that axis on first use. Height is content-owned afterwards;
        // only width remains user-resizable/persistent.
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, 0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(140f, 0f),
            new Num.Vector2(
                Math.Min(520f, Math.Max(140f, display.X - 16f)),
                Math.Max(80f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("工具###DevToolActivity", "Tools###DevToolActivity"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Tools");

        for (int i = 0; i < pages.Count; i++)
            DrawModeButton(pages[i], snapshot.ToolMode);

        if (DevToolWidgets.NavItem(debugLabel, "DevToolLanceScavengerDebug", lanceDebugPage))
        {
            lanceDebugPage = true;
            DevToolPageViewRegistry.DeactivateActive();
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
        }
        if (ImGui.IsItemHovered())
        {
            DevToolTooltip.Show(DevToolUiSettings.T(
                "长枪拾荒者：实时状态、瞄准质量、38帧架枪历史、路径阻断与反扫诊断",
                "Lance Scavenger: live state, aim quality, 38-frame brace history, path blocks and counter-sweep diagnostics"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (lanceDebugPage)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "调试页使用独立工作区。选择上方任一常规工具即可返回。",
                "Debug uses its own workspace. Select any normal tool above to return."));
            FitActivityBarHeight(display);
            ImGui.End();
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
        if (DevToolWidgets.NavItem(page.NavigationLabel, id, !lanceDebugPage && current == page.Mode))
        {
            lanceDebugPage = false;
            LanceScavengerDebugView.StopCapture();
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
        float defaultWidth = Math.Min(display.X * 0.94f, Math.Max(1f, display.X - 16f));
        float defaultHeight = Math.Min(
            Math.Max(440f * Math.Min(1.25f, scale), display.Y * 0.52f),
            Math.Max(320f, display.Y - 120f));
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
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        // Apply the wider layout even when an older ImGui window size was saved. Afterwards
        // manual resizing is retained until the display resolution changes.
        if (Math.Abs(browserInspectorDisplayWidth - display.X) > 0.5f)
        {
            ImGui.SetWindowSize(new Num.Vector2(defaultWidth, ImGui.GetWindowSize().Y));
            ImGui.SetWindowPos(new Num.Vector2(defaultPos.X, ImGui.GetWindowPos().Y));
            browserInspectorDisplayWidth = display.X;
        }
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
        string legacyTooltip = page.LegacyFallbackTooltip;
        if (!string.IsNullOrEmpty(legacyTooltip))
            DrawLegacyFallback(snapshot, legacyTooltip);
    }

    private static void DrawLegacyFallback(EditorPresentationSnapshot snapshot, string tooltip)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        bool legacyVisible = snapshot.Inspector?.LegacyUiVisible == true;
        string label = legacyVisible
            ? DevToolUiSettings.T("隐藏原版 DevUI", "Hide Original DevUI")
            : DevToolUiSettings.T("显示原版 DevUI", "Show Original DevUI");
        if (DevToolWidgets.ActionButton(label, "LegacyDevUI", DevToolButtonTone.Primary))
            Send(EditorUiCommandKind.ToggleLegacyUi);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(tooltip);
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
