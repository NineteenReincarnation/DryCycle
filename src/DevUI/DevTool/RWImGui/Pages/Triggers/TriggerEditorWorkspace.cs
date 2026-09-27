using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Canvas-first Trigger workspace.
///
/// The room remains the primary surface. Scene and Inspector stay compact and movable in the
/// lower-right workspace, while spatial Spot properties stay on the world gizmo.
/// </summary>
internal static partial class TriggerEditorView
{
    private static bool workspaceShellInitialized;
    private static bool observedShellBrowserOpen;
    private static bool addPaletteOpen;
    private static bool scenePanelOpen = true;
    private static bool focusAddSearch;
    private static string workspaceAddSearch = string.Empty;
    private static string workspaceSceneSearch = string.Empty;

    internal static void DrawWorkspace(
        EditorPresentationSnapshot shell,
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available)
            return;

        SynchronizeWorkspaceShell(shell);
        DrawCommandBar(snapshot, display);

        EditorTriggerSnapshot selected =
            FindSelected(snapshot);

        if (addPaletteOpen)
            DrawAddPalette(snapshot, display);

        if (scenePanelOpen)
            DrawCompactScene(snapshot, display);

        DrawResidentInspector(snapshot, selected, display);
    }

    internal static void EnterCanvasFirst()
    {
        workspaceShellInitialized = false;
        addPaletteOpen = false;
        focusAddSearch = false;
    }

    private static void ResetWorkspaceRetainedState()
    {
        workspaceShellInitialized = false;
        observedShellBrowserOpen = false;
        addPaletteOpen = false;
        scenePanelOpen = true;
        focusAddSearch = false;
        workspaceAddSearch = string.Empty;
        workspaceSceneSearch = string.Empty;
    }

    private static void SynchronizeWorkspaceShell(
        EditorPresentationSnapshot shell)
    {
        bool browserOpen =
            shell?.BrowserOpen == true;
        if (!workspaceShellInitialized)
        {
            workspaceShellInitialized = true;
            observedShellBrowserOpen = browserOpen;
            // Trigger always enters in canvas-first mode. Browser state is observed only so Ctrl+B
            // can continue to open/close the compact Add palette; the Inspector itself stays resident.
            addPaletteOpen = false;
            advancedInspectorOpen = false;
            return;
        }

        if (browserOpen != observedShellBrowserOpen)
        {
            observedShellBrowserOpen = browserOpen;
            addPaletteOpen = !addPaletteOpen;
            if (addPaletteOpen)
                focusAddSearch = true;
        }

    }

    private static void DrawCommandBar(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        float width =
            Math.Min(
                760f,
                Math.Max(
                    420f,
                    display.X - 520f));
        float x =
            Math.Max(
                210f,
                (display.X - width) * 0.5f);

        ImGui.SetNextWindowPos(
            new Num.Vector2(x, 8f),
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(
            Math.Min(0.96f, DevToolUiSettings.WindowAlpha + 0.08f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin("##TriggerCanvasCommandBar", flags))
        {
            ImGui.End();
            return;
        }

        string addLabel =
            DevToolUiSettings.T("+ 添加", "+ Add");
        if (DevToolWidgets.ActionButton(
                addLabel,
                "TriggerWorkspaceAdd",
                addPaletteOpen
                    ? DevToolButtonTone.Primary
                    : DevToolButtonTone.Subtle))
        {
            addPaletteOpen = !addPaletteOpen;
            if (addPaletteOpen)
                focusAddSearch = true;
        }

        int count =
            snapshot.Triggers?.Length ?? 0;
        string sceneLabel =
            DevToolUiSettings.T(
                "场景 ",
                "Scene ") +
            count;
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                sceneLabel,
                "TriggerWorkspaceScene",
                scenePanelOpen
                    ? DevToolButtonTone.Primary
                    : DevToolButtonTone.Subtle))
        {
            scenePanelOpen = !scenePanelOpen;
        }

        EditorTriggerSnapshot selected =
            FindSelected(snapshot);

        if (selected != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextUnformatted(
                string.IsNullOrWhiteSpace(selected.Name)
                    ? selected.Type ?? string.Empty
                    : selected.Name + "  [" + (selected.Type ?? string.Empty) + "]");

            if (selected.Event?.HasEvent == true)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(
                    selected.Event.Type ?? string.Empty);
            }

        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled(
                DevToolUiSettings.T(
                    "选择场景中的触发器进行编辑",
                    "Select a trigger in the room to edit it"));
        }

        ImGui.End();
    }

    private static void DrawAddPalette(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        float width =
            Math.Min(340f, Math.Max(260f, display.X * 0.22f));
        float height =
            Math.Min(420f, Math.Max(260f, display.Y * 0.44f));

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                Math.Max(210f, display.X * 0.25f),
                58f),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(
            new Num.Vector2(width, height),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(250f, 220f),
            new Num.Vector2(
                Math.Max(250f, display.X * 0.45f),
                Math.Max(220f, display.Y - 80f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.PopupAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "添加触发器###TriggerAddPalette",
                    "Add Trigger###TriggerAddPalette"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerAddPalette");

        if (focusAddSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusAddSearch = false;
        }

        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T(
                "搜索",
                "Search"),
            "TriggerWorkspaceAddSearch",
            ref workspaceAddSearch,
            128);

        ImGui.Separator();

        string[] types =
            snapshot.TriggerTypes ?? Array.Empty<string>();
        EnsureTriggerTypeLabels(types);
        string query =
            workspaceAddSearch?.Trim() ?? string.Empty;
        EnsureTriggerMatches(
            types,
            query);

        using DevToolListClipper clipper =
            new(ProjectedTriggerMatches.Count);
        while (clipper.Step(
                   out int firstVisible,
                   out int lastVisibleExclusive))
        {
            for (int visibleIndex = firstVisible;
                 visibleIndex < lastVisibleExclusive;
                 visibleIndex++)
            {
                int sourceIndex =
                    ProjectedTriggerMatches[visibleIndex];
                string type =
                    types[sourceIndex];

                if (!ImGui.Selectable(
                        projectedTriggerTypeLabels[sourceIndex],
                        false))
                    continue;

                TriggerEditorCommandQueue.Enqueue(
                    new TriggerEditorCommand(
                        TriggerEditorCommandKind.Create,
                        text: type));

                addPaletteOpen = false;
                workspaceAddSearch = string.Empty;
                break;
            }
        }

        if (ProjectedTriggerMatches.Count == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "没有匹配的触发器类型。",
                    "No matching trigger types."),
                true);
        }

        ImGui.End();
    }

    private static void DrawCompactScene(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        float width =
            Math.Min(310f, Math.Max(240f, display.X * 0.20f));
        float height =
            Math.Min(390f, Math.Max(240f, display.Y * 0.40f));
        float x =
            Math.Max(
                8f,
                display.X - width - 14f);

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                x,
                Math.Max(84f, display.Y - height - 14f)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(
            new Num.Vector2(width, height),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(220f, 200f),
            new Num.Vector2(
                Math.Max(220f, display.X * 0.40f),
                Math.Max(200f, display.Y - 100f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "场景###TriggerCompactSceneV2",
                    "Scene###TriggerCompactSceneV2"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerCompactScene");

        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T(
                "搜索",
                "Search"),
            "TriggerWorkspaceSceneSearch",
            ref workspaceSceneSearch,
            128);

        ImGui.Separator();

        EditorTriggerSnapshot[] triggers =
            snapshot.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        EnsureSceneLabels(triggers);

        string query =
            workspaceSceneSearch?.Trim() ?? string.Empty;

        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger =
                triggers[i];
            if (trigger == null)
                continue;

            if (!Matches(
                    trigger.Name,
                    query) &&
                !Matches(
                    trigger.Type,
                    query) &&
                !Matches(
                    trigger.Event?.Type,
                    query))
                continue;

            if (ImGui.Selectable(
                    projectedSceneLabels[i],
                    trigger.Selected))
            {
                TriggerEditorCommandQueue.Enqueue(
                    new TriggerEditorCommand(
                        TriggerEditorCommandKind.Select,
                        trigger.Index));
            }
        }

        ImGui.End();
    }

    private static void DrawResidentInspector(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot selected,
        Num.Vector2 display)
    {
        float width =
            Math.Min(310f, Math.Max(250f, display.X * 0.20f));
        float x =
            Math.Max(
                8f,
                display.X - width - 14f);
        float sceneHeight =
            Math.Min(
                390f,
                Math.Max(
                    240f,
                    display.Y * 0.40f));
        float y =
            scenePanelOpen
                ? display.Y - sceneHeight - 330f
                : display.Y - 310f;

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                x,
                Math.Max(84f, y)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(240f, 0f),
            new Num.Vector2(
                Math.Max(240f, display.X * 0.36f),
                Math.Max(180f, display.Y - 100f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "检查器###TriggerInspectorResidentV3",
                    "Inspector###TriggerInspectorResidentV3"),
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerInspectorResident");

        if (selected == null)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "选择一个触发器后在这里编辑。",
                    "Select a trigger to edit it here."),
                true);
            ImGui.End();
            return;
        }

        DrawInspector(snapshot);

        ImGui.End();
    }
}
