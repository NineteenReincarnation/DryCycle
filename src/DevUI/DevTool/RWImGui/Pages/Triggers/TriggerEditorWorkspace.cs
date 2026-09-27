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
    private static bool addPopupRequested;
    private static bool createConfirmPopupRequested;
    private static string pendingTriggerType = string.Empty;
    private static string addTriggerName = string.Empty;
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
        addPopupRequested = false;
        createConfirmPopupRequested = false;
        pendingTriggerType = string.Empty;
        addTriggerName = string.Empty;
    }

    private static void ResetWorkspaceRetainedState()
    {
        workspaceShellInitialized = false;
        observedShellBrowserOpen = false;
        addPopupRequested = false;
        createConfirmPopupRequested = false;
        pendingTriggerType = string.Empty;
        addTriggerName = string.Empty;
        scenePanelOpen = true;
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
            // can request the same compact Add popup used by the shared top button.
            return;
        }

        if (browserOpen != observedShellBrowserOpen)
        {
            observedShellBrowserOpen = browserOpen;
            addPopupRequested = true;
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
            addPopupRequested = true;
        }

        if (addPopupRequested)
        {
            ImGui.OpenPopup("##TriggerAddPopup");
            addPopupRequested = false;
        }

        Num.Vector2 addPopupPosition =
            new(
                ImGui.GetItemRectMin().X,
                ImGui.GetItemRectMax().Y + 4f);

        string sceneLabel =
            DevToolUiSettings.T(
                "场景",
                "Scene");

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

        if (createConfirmPopupRequested)
        {
            ImGui.OpenPopup("##TriggerCreateConfirmPopup");
            createConfirmPopupRequested = false;
        }

        DrawCreateConfirmPopup(
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

            pendingTriggerType = type;
            addTriggerName = string.Empty;
            createConfirmPopupRequested = true;
            ImGui.CloseCurrentPopup();
            break;
        }

        ImGui.EndPopup();
    }

    private static void DrawCreateConfirmPopup(
        Num.Vector2 position)
    {
        ImGui.SetNextWindowPos(
            position,
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(220f, 0f),
            new Num.Vector2(360f, 240f));

        if (!ImGui.BeginPopup("##TriggerCreateConfirmPopup"))
            return;

        ImGui.TextDisabled(
            DevToolUiSettings.T(
                "触发器类型",
                "Trigger type"));
        ImGui.SameLine();
        ImGui.TextUnformatted(
            pendingTriggerType ?? string.Empty);

        ImGui.Separator();

        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T(
                "名称",
                "Name"),
            "TriggerCreateName",
            ref addTriggerName,
            128);

        ImGui.Spacing();

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T(
                    "确认",
                    "Create"),
                "TriggerCreateConfirm",
                DevToolButtonTone.Primary))
        {
            if (!string.IsNullOrEmpty(pendingTriggerType))
            {
                TriggerEditorCommandQueue.Enqueue(
                    new TriggerEditorCommand(
                        TriggerEditorCommandKind.Create,
                        text: pendingTriggerType,
                        name: addTriggerName));
            }

            pendingTriggerType = string.Empty;
            addTriggerName = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T(
                    "取消",
                    "Cancel"),
                "TriggerCreateCancel",
                DevToolButtonTone.Subtle))
        {
            pendingTriggerType = string.Empty;
            addTriggerName = string.Empty;
            ImGui.CloseCurrentPopup();
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
        string[] categories =
            snapshot.TriggerTypes ?? Array.Empty<string>();

        string query =
            workspaceSceneSearch?.Trim() ?? string.Empty;

        bool anyVisible =
            false;

        // Use the registry/catalog order so Scene remains predictable as more Trigger types are
        // added. Each type is a real category; its instances live underneath and no longer repeat
        // "[Type]" on every row.
        for (int categoryIndex = 0;
             categoryIndex < categories.Length;
             categoryIndex++)
        {
            string type =
                categories[categoryIndex];
            if (string.IsNullOrEmpty(type))
                continue;

            anyVisible |=
                DrawSceneCategory(
                    type,
                    categoryIndex,
                    triggers,
                    query);
        }

        // Third-party Trigger types can exist before the catalog learns about them. Keep those
        // visible by emitting one fallback category per unknown type.
        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger =
                triggers[i];
            if (trigger == null ||
                string.IsNullOrEmpty(trigger.Type) ||
                ContainsTriggerType(
                    categories,
                    trigger.Type) ||
                HasEarlierUnknownType(
                    triggers,
                    i,
                    categories,
                    trigger.Type))
                continue;

            anyVisible |=
                DrawSceneCategory(
                    trigger.Type,
                    categories.Length + i,
                    triggers,
                    query);
        }

        if (!anyVisible)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "没有匹配的触发器。",
                    "No matching triggers."),
                true);
        }

        ImGui.End();
    }

    private static bool DrawSceneCategory(
        string type,
        int categoryIndex,
        EditorTriggerSnapshot[] triggers,
        string query)
    {
        int matchingCount =
            0;

        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger =
                triggers[i];
            if (!IsSceneTriggerMatch(
                    trigger,
                    type,
                    query))
                continue;

            matchingCount++;
        }

        if (matchingCount == 0)
            return false;

        string header =
            type +
            "  " +
            matchingCount +
            "##TriggerSceneCategory" +
            categoryIndex;

        if (!ImGui.CollapsingHeader(
                header,
                ImGuiTreeNodeFlags.DefaultOpen))
            return true;

        int ordinal =
            0;

        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger =
                triggers[i];
            if (trigger == null ||
                !string.Equals(
                    trigger.Type,
                    type,
                    StringComparison.Ordinal))
                continue;

            ordinal++;

            if (!IsSceneTriggerMatch(
                    trigger,
                    type,
                    query))
                continue;

            string visibleLabel =
                string.IsNullOrWhiteSpace(trigger.Name)
                    ? type + " #" + ordinal
                    : trigger.Name;

            if (trigger.Event?.HasEvent == true &&
                !string.IsNullOrWhiteSpace(
                    trigger.Event.Type))
            {
                visibleLabel +=
                    "  ->  " +
                    trigger.Event.Type;
            }

            string itemLabel =
                "  " +
                visibleLabel +
                "##TriggerSceneItem" +
                trigger.Index;

            if (ImGui.Selectable(
                    itemLabel,
                    trigger.Selected))
            {
                TriggerEditorCommandQueue.Enqueue(
                    new TriggerEditorCommand(
                        TriggerEditorCommandKind.Select,
                        trigger.Index));
            }
        }

        return true;
    }

    private static bool IsSceneTriggerMatch(
        EditorTriggerSnapshot trigger,
        string type,
        string query)
    {
        if (trigger == null ||
            !string.Equals(
                trigger.Type,
                type,
                StringComparison.Ordinal))
            return false;

        return Matches(
                   trigger.Name,
                   query) ||
               Matches(
                   trigger.Type,
                   query) ||
               Matches(
                   trigger.Event?.Type,
                   query);
    }

    private static bool ContainsTriggerType(
        string[] categories,
        string type)
    {
        if (categories == null)
            return false;

        for (int i = 0; i < categories.Length; i++)
        {
            if (string.Equals(
                    categories[i],
                    type,
                    StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasEarlierUnknownType(
        EditorTriggerSnapshot[] triggers,
        int beforeIndex,
        string[] categories,
        string type)
    {
        for (int i = 0; i < beforeIndex; i++)
        {
            EditorTriggerSnapshot earlier =
                triggers[i];
            if (earlier == null ||
                ContainsTriggerType(
                    categories,
                    earlier.Type))
                continue;

            if (string.Equals(
                    earlier.Type,
                    type,
                    StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void DrawResidentInspector(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot selected,
        Num.Vector2 display)
    {
        // Keep the default compact, but wide enough that the value control and its trailing
        // Chinese/English label can both render without clipping.
        float width =
            Math.Min(
                380f,
                Math.Max(
                    350f,
                    display.X * 0.20f));
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
        float defaultHeight =
            Math.Min(
                440f,
                Math.Max(
                    340f,
                    display.Y * 0.48f));
        float y =
            scenePanelOpen
                ? display.Y - sceneHeight - defaultHeight - 28f
                : display.Y - defaultHeight - 14f;

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                x,
                Math.Max(84f, y)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(
            new Num.Vector2(
                width,
                defaultHeight),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(335f, 260f),
            new Num.Vector2(
                Math.Max(520f, display.X * 0.50f),
                Math.Max(320f, display.Y - 80f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "检查器###TriggerInspectorResidentV5",
                    "Inspector###TriggerInspectorResidentV5"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerInspectorResidentV5");

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
