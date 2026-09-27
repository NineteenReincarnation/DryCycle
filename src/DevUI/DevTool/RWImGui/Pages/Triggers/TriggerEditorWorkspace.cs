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
    private static bool scenePanelOpen = true;
    private static string workspaceSceneSearch = string.Empty;

    internal static void DrawWorkspace(
        EditorPresentationSnapshot shell,
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available)
            return;

        SynchronizeWorkspaceShell(shell);

        EditorTriggerSnapshot selected =
            FindSelected(snapshot);

        if (scenePanelOpen)
            DrawCompactScene(snapshot, display);

        DrawResidentInspector(snapshot, selected, display);
    }

    internal static void EnterCanvasFirst()
    {
        workspaceShellInitialized = false;
    }

    private static void ResetWorkspaceRetainedState()
    {
        workspaceShellInitialized = false;
        observedShellBrowserOpen = false;
        addPaletteOpen = false;
        scenePanelOpen = true;
        focusAddSearch = false;
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
            return;
        }

        if (browserOpen != observedShellBrowserOpen)
        {
            observedShellBrowserOpen = browserOpen;
            ImGui.OpenPopup("##TriggerAddPopup");
        }

    }

    internal static void DrawTopControls(
        EditorPresentationSnapshot shell,
        EditorTriggerPresentationSnapshot snapshot)
    {
        if (snapshot == null ||
            !snapshot.Available)
            return;

        SynchronizeWorkspaceShell(shell);

        string addLabel =
            DevToolUiSettings.T(
                "+ 添加",
                "+ Add");
        if (DevToolWidgets.ActionButton(
                addLabel,
                "TriggerWorkspaceAdd",
                DevToolButtonTone.Subtle))
        {
            ImGui.OpenPopup("##TriggerAddPopup");
        }

        Num.Vector2 addPopupPosition =
            new(
                ImGui.GetItemRectMin().X,
                ImGui.GetItemRectMax().Y + 4f);

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

        DrawAddPopup(
            snapshot,
            addPopupPosition);
    }

    private static void DrawAddPopup(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 position)
    {
        ImGui.SetNextWindowPos(
            position,
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(170f, 0f),
            new Num.Vector2(320f, 720f));

        if (!ImGui.BeginPopup("##TriggerAddPopup"))
            return;

        string[] types =
            snapshot.TriggerTypes ??
            Array.Empty<string>();
        EnsureTriggerTypeLabels(types);

        // The Trigger catalog is intentionally shown in full. This is a short action menu, not a
        // browser/search surface; developers should see every placeable type immediately.
        for (int i = 0; i < types.Length; i++)
        {
            string type =
                types[i];
            if (!ImGui.Selectable(
                    projectedTriggerTypeLabels[i],
                    false))
                continue;

            TriggerEditorCommandQueue.Enqueue(
                new TriggerEditorCommand(
                    TriggerEditorCommandKind.Create,
                    text: type));
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
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
