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
/// The room remains the primary surface. Library, Scene and full Inspector are transient tools;
/// the selected trigger exposes only its highest-frequency fields in a compact card while spatial
/// properties stay on the world gizmo.
/// </summary>
internal static partial class TriggerEditorView
{
    private static bool workspaceShellInitialized;
    private static bool observedShellBrowserOpen;
    private static bool observedShellInspectorOpen;
    private static bool addPaletteOpen;
    private static bool scenePanelOpen;
    private static bool advancedInspectorOpen;
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

        if (advancedInspectorOpen)
            DrawAdvancedInspector(snapshot, display);
        else if (selected != null && !scenePanelOpen)
            DrawQuickInspector(snapshot, selected, display);
    }

    private static void ResetWorkspaceRetainedState()
    {
        workspaceShellInitialized = false;
        observedShellBrowserOpen = false;
        observedShellInspectorOpen = false;
        addPaletteOpen = false;
        scenePanelOpen = false;
        advancedInspectorOpen = false;
        focusAddSearch = false;
        workspaceAddSearch = string.Empty;
        workspaceSceneSearch = string.Empty;
    }

    private static void SynchronizeWorkspaceShell(
        EditorPresentationSnapshot shell)
    {
        bool browserOpen =
            shell?.BrowserOpen == true;
        bool inspectorOpen =
            shell?.InspectorOpen == true;

        if (!workspaceShellInitialized)
        {
            workspaceShellInitialized = true;
            observedShellBrowserOpen = browserOpen;
            observedShellInspectorOpen = inspectorOpen;

            // Trigger always enters in canvas-first mode. Existing Browser/Inspector state may have
            // been left open by another page; do not let those persisted shell flags cover the room
            // on the first Trigger frame. Subsequent Ctrl+B / Ctrl+I changes are still observed.
            addPaletteOpen = false;
            advancedInspectorOpen = false;
            return;
        }

        if (browserOpen != observedShellBrowserOpen)
        {
            observedShellBrowserOpen = browserOpen;
            addPaletteOpen = browserOpen;
            if (addPaletteOpen)
                focusAddSearch = true;
        }

        if (inspectorOpen != observedShellInspectorOpen)
        {
            observedShellInspectorOpen = inspectorOpen;
            advancedInspectorOpen = inspectorOpen;
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
            if (scenePanelOpen)
                advancedInspectorOpen = false;
        }

        EditorTriggerSnapshot selected =
            FindSelected(snapshot);

        if (selected != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextUnformatted(
                selected.Type ?? string.Empty);

            if (selected.Event?.HasEvent == true)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(
                    selected.Event.Type ?? string.Empty);
            }

            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("高级", "Advanced"),
                    "TriggerWorkspaceAdvanced",
                    advancedInspectorOpen
                        ? DevToolButtonTone.Primary
                        : DevToolButtonTone.Subtle))
            {
                advancedInspectorOpen =
                    !advancedInspectorOpen;
                if (advancedInspectorOpen)
                    scenePanelOpen = false;
            }

            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    DevToolUiSettings.T(
                        "完整触发器参数。Ctrl+I 也可切换。",
                        "Full trigger properties. Ctrl+I also toggles this."));
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
            new Num.Vector2(x, 92f),
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
                    "场景###TriggerCompactScene",
                    "Scene###TriggerCompactScene"),
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

    private static void DrawQuickInspector(
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
        float y =
            scenePanelOpen
                ? Math.Min(display.Y - 280f, 500f)
                : 110f;

        ImGui.SetNextWindowPos(
            new Num.Vector2(x, Math.Max(84f, y)),
            ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(240f, 0f),
            new Num.Vector2(
                Math.Max(240f, display.X * 0.36f),
                Math.Max(180f, display.Y - 100f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "快速编辑###TriggerQuickInspector",
                    "Quick Edit###TriggerQuickInspector"),
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerQuickInspector");

        ImGui.TextUnformatted(
            selected.Type ?? string.Empty);

        if (selected.IsSpot)
        {
            ImGui.TextDisabled(
                DevToolUiSettings.T(
                    $"位置 {selected.X:0.#}, {selected.Y:0.#}  |  半径 {selected.Radius:0.#}",
                    $"Position {selected.X:0.#}, {selected.Y:0.#}  |  Radius {selected.Radius:0.#}"));
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    DevToolUiSettings.T(
                        "位置和半径优先直接使用房间中的 Gizmo 编辑。",
                        "Prefer editing position and radius directly with the room gizmo."));
            }
        }

        if (!string.IsNullOrEmpty(
                selected.CreatureType))
        {
            DrawString(
                selected,
                TriggerEditorKeys.CreatureType,
                DevToolUiSettings.T(
                    "生物类型",
                    "Creature type"),
                selected.CreatureType);
        }

        DrawFloat(
            selected,
            TriggerEditorKeys.FireChance,
            DevToolUiSettings.T(
                "触发概率",
                "Fire chance"),
            selected.FireChance,
            0f,
            1f);

        DrawFloat(
            selected,
            TriggerEditorKeys.DelaySeconds,
            DevToolUiSettings.T(
                "延迟（秒）",
                "Delay (seconds)"),
            selected.DelaySeconds,
            0f,
            120f);

        bool multiUse =
            selected.MultiUse;
        if (ImGui.Checkbox(
                DevToolUiSettings.T(
                    "允许多次触发##TriggerQuickMultiUse",
                    "Can fire multiple times##TriggerQuickMultiUse"),
                ref multiUse))
        {
            SendValue(
                selected.Index,
                TriggerEditorKeys.MultiUse,
                new EditorPropertyValue(
                    EditorPropertyKind.Boolean,
                    boolean: multiUse));
        }

        DrawSlugcatCompactSelector(
            snapshot,
            selected);

        if (selected.Event?.HasEvent == true)
        {
            ImGui.TextDisabled(
                DevToolUiSettings.T(
                    "事件: ",
                    "Event: ") +
                selected.Event.Type);
        }

        ImGui.Separator();

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T(
                    "高级设置",
                    "Advanced"),
                "TriggerQuickAdvanced",
                DevToolButtonTone.Subtle,
                true))
        {
            advancedInspectorOpen = true;
            scenePanelOpen = false;
        }

        ImGui.End();
    }

    private static void DrawAdvancedInspector(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        float width =
            Math.Min(430f, Math.Max(340f, display.X * 0.28f));
        float height =
            Math.Min(
                Math.Max(420f, display.Y * 0.68f),
                Math.Max(420f, display.Y - 110f));
        float x =
            Math.Max(
                8f,
                display.X - width - 14f);

        ImGui.SetNextWindowPos(
            new Num.Vector2(x, 86f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new Num.Vector2(width, height),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(320f, 320f),
            new Num.Vector2(
                Math.Max(320f, display.X * 0.46f),
                Math.Max(320f, display.Y - 80f)));
        ImGui.SetNextWindowBgAlpha(
            DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "高级检查器###TriggerAdvancedInspector",
                    "Advanced Inspector###TriggerAdvancedInspector"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow(
            "TriggerAdvancedInspector");

        DrawInspector(snapshot);

        ImGui.End();
    }
}
