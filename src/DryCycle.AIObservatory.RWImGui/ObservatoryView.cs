using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// RWImGUI-only presentation. This code never dereferences RainWorld, RainWorldGame,
// Creature, Room, UnityEngine.Input or UnityEngine.Time. It consumes detached data only.
internal static class ObservatoryView
{
    private static string entityFilter = string.Empty;
    private static bool fullMode;
    private static bool modeLayoutPending;

    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        snapshot ??= AIDebugPresentationSnapshot.Empty;

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1280f;
        if (display.Y < 1f) display.Y = 720f;

        float compactWidth = Math.Min(930f, Math.Max(700f, display.X - 48f));
        float compactHeight = Math.Min(620f, Math.Max(460f, display.Y - 70f));
        float fullWidth = Math.Max(640f, display.X - 32f);
        float fullHeight = Math.Max(420f, display.Y - 32f);

        if (modeLayoutPending)
        {
            ImGui.SetNextWindowPos(fullMode ? new Num.Vector2(16f, 16f) : new Num.Vector2(24f, 24f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(
                fullMode ? new Num.Vector2(fullWidth, fullHeight) : new Num.Vector2(compactWidth, compactHeight),
                ImGuiCond.Always);
            modeLayoutPending = false;
        }
        else if (!fullMode)
        {
            ImGui.SetNextWindowPos(new Num.Vector2(24f, 24f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Num.Vector2(compactWidth, compactHeight), ImGuiCond.FirstUseEver);
        }

        ImGui.SetNextWindowBgAlpha(0.96f);
        if (!ImGui.Begin("DryCycle AI Observatory###DryCycleAIObservatory", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        DrawToolbar(snapshot);
        ImGui.Separator();

        if (!snapshot.HasGame)
        {
            ImGui.TextDisabled(L(snapshot, "Rain World game is not running.", "Rain World 游戏尚未运行。"));
            if (!string.IsNullOrEmpty(snapshot.Status)) ImGui.TextDisabled(snapshot.Status);
            ImGui.End();
            return;
        }

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float timelineHeight = Math.Min(ObservatoryTimeline.PreferredHeight, Math.Max(118f, available.Y * 0.30f));
        float upperHeight = Math.Max(150f, available.Y - timelineHeight - 6f);
        if (upperHeight + timelineHeight + 6f > available.Y)
            timelineHeight = Math.Max(90f, available.Y - upperHeight - 6f);

        float browserWidth = fullMode ? 310f : 270f;
        ImGui.BeginChild("##AIEntityBrowser", new Num.Vector2(browserWidth, upperHeight), ImGuiChildFlags.Borders);
        DrawEntityBrowser(snapshot);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##AIInspector", new Num.Vector2(0f, upperHeight), ImGuiChildFlags.Borders);
        DrawInspector(snapshot);
        ImGui.EndChild();

        ImGui.BeginChild("##AIPersistentTimeline", new Num.Vector2(0f, timelineHeight), ImGuiChildFlags.Borders);
        ObservatoryTimeline.Draw(snapshot);
        ImGui.EndChild();

        ImGui.End();
    }

    private static void DrawToolbar(AIDebugPresentationSnapshot snapshot)
    {
        if (ImGui.Button(fullMode ? L(snapshot, "Compact", "紧凑") : L(snapshot, "Full", "完整")))
        {
            fullMode = !fullMode;
            modeLayoutPending = true;
        }

        ImGui.SameLine();
        if (ImGui.Button(snapshot.Paused ? L(snapshot, "Resume", "继续") : L(snapshot, "Pause", "暂停")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.TogglePause));

        ImGui.SameLine();
        if (ImGui.Button(L(snapshot, "Step", "单步")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.Step));

        ImGui.SameLine();
        if (ImGui.Button(L(snapshot, "-1s", "-1秒")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SeekTicks(-40));

        ImGui.SameLine();
        if (ImGui.Button(L(snapshot, "+1s", "+1秒")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SeekTicks(40));

        ImGui.SameLine();
        if (snapshot.ViewMode == AIDebugViewMode.Historical)
        {
            if (ImGui.Button(L(snapshot, "Return Live", "返回实时")))
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.ReturnLive));
        }
        else
        {
            ImGui.TextDisabled("LIVE");
        }

        ImGui.SameLine();
        if (ImGui.Button(L(snapshot, "Refresh", "刷新")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.Refresh));

        ImGui.SameLine();
        if (ImGui.Button(L(snapshot, "Export", "导出")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.ExportSession));

        ImGui.SameLine();
        if (ImGui.Button(snapshot.Language == AIDebugLanguage.Chinese ? "English" : "Chinese"))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.ToggleLanguage));

        ImGui.SameLine();
        string cursor = snapshot.ViewMode == AIDebugViewMode.Historical
            ? $"HIST {snapshot.CursorTick} ({(snapshot.CursorTick - snapshot.Tick) / 40f:0.0}s)"
            : $"tick {snapshot.Tick}";
        ImGui.TextDisabled($"{cursor} · {snapshot.Status} · F7");
    }

    private static void DrawEntityBrowser(AIDebugPresentationSnapshot snapshot)
    {
        ImGui.Text(L(snapshot, "Entity Browser", "实体浏览器"));
        ImGui.TextDisabled(L(snapshot,
            "Click a creature to inspect its recorded AI state.",
            "点击生物以查看已记录的 AI 状态。"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(L(snapshot, "Filter", "筛选") + "##AIEntityFilter", ref entityFilter, 96);
        ImGui.Separator();

        int visibleCount = 0;
        for (int i = 0; i < snapshot.Entities.Length; i++)
            if (snapshot.Entities[i].VisibleRoom && Matches(snapshot.Entities[i])) visibleCount++;

        if (visibleCount > 0)
        {
            ImGui.TextDisabled(L(snapshot, "Visible camera rooms", "当前可见相机房间"));
            for (int i = 0; i < snapshot.Entities.Length; i++)
            {
                AIDebugPresentationEntity entity = snapshot.Entities[i];
                if (entity.VisibleRoom && Matches(entity)) DrawEntityRow(snapshot, entity);
            }
            ImGui.Separator();
        }

        ImGui.TextDisabled(L(snapshot, "World", "世界"));
        for (int i = 0; i < snapshot.Entities.Length; i++)
        {
            AIDebugPresentationEntity entity = snapshot.Entities[i];
            if (!entity.VisibleRoom && Matches(entity)) DrawEntityRow(snapshot, entity);
        }

        if (snapshot.Entities.Length == 0)
            ImGui.TextDisabled(L(snapshot, "No creatures found.", "没有找到生物。"));
    }

    private static void DrawEntityRow(AIDebugPresentationSnapshot snapshot, AIDebugPresentationEntity entity)
    {
        string label = $"{entity.DisplayName} [{StateText(snapshot, entity.State)}]##{entity.Key.Spawner}:{entity.Key.Number}";
        if (ImGui.Selectable(label, entity.Selected))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Select(entity.Key));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{entity.Key}\n{L(snapshot, "Room", "房间")}: {entity.Room}");
    }

    private static void DrawInspector(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationCreature selected = snapshot.Selected;
        if (selected == null)
        {
            ImGui.Text(L(snapshot, "Inspector", "检查器"));
            ImGui.Separator();
            ImGui.TextDisabled(L(snapshot,
                snapshot.ViewMode == AIDebugViewMode.Historical
                    ? "No rich snapshot exists at or before this cursor position."
                    : "Select a creature from the Entity Browser.",
                snapshot.ViewMode == AIDebugViewMode.Historical
                    ? "当前游标之前没有可用的完整快照。"
                    : "从左侧实体浏览器选择一个生物。"));
            ImGui.Spacing();
            DrawRecorderCursor(snapshot);
            return;
        }

        ImGui.Text(selected.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled("[" + StateText(snapshot, selected.State) + "]");
        if (snapshot.Paused)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(L(snapshot, "PAUSED", "已暂停"));
        }
        if (snapshot.ViewMode == AIDebugViewMode.Historical)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(L(snapshot, "HISTORICAL", "历史"));
        }

        DrawRecorderCursor(snapshot);
        ImGui.TextDisabled(L(snapshot, "Control Owner", "控制器") + ": " + selected.ControlOwner);
        ImGui.Separator();

        if (selected.Decisions.Length > 0 && ImGui.CollapsingHeader(
                L(snapshot, "Decision Stack", "决策栈"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (int i = 0; i < selected.Decisions.Length; i++)
            {
                AIDebugPresentationDecision decision = selected.Decisions[i];
                if (decision.Depth > 0) ImGui.Indent(decision.Depth * 14f);

                ImGui.TextDisabled(DecisionStateText(snapshot, decision.State));
                ImGui.SameLine();
                ImGui.Text(AIDebugLocalization.T(decision.LabelKey));
                if (!string.IsNullOrEmpty(decision.Detail))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(decision.Detail);
                }
                if (!string.IsNullOrEmpty(decision.RawName) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(decision.RawName);

                if (decision.Depth > 0) ImGui.Unindent(decision.Depth * 14f);
            }
        }

        for (int s = 0; s < selected.Sections.Length; s++)
        {
            AIDebugPresentationSection section = selected.Sections[s];
            string title = AIDebugLocalization.T(section.TitleKey);
            if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen)) continue;

            if (ImGui.BeginTable("##Section" + s, 2,
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn(L(snapshot, "Field", "字段"), ImGuiTableColumnFlags.WidthFixed, 190f);
                ImGui.TableSetupColumn(L(snapshot, "Value", "值"), ImGuiTableColumnFlags.WidthStretch);

                for (int i = 0; i < section.Values.Length; i++)
                {
                    AIDebugPresentationValue value = section.Values[i];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(AIDebugLocalization.T(value.LabelKey));
                    if (!string.IsNullOrEmpty(value.RawName) && ImGui.IsItemHovered())
                        ImGui.SetTooltip(value.RawName);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextWrapped(value.Value);
                    if ((!string.IsNullOrEmpty(value.Source) || value.AgeTicks > 0) && ImGui.IsItemHovered())
                    {
                        string tooltip = string.Empty;
                        if (!string.IsNullOrEmpty(value.Source)) tooltip = L(snapshot, "Source", "来源") + ": " + value.Source;
                        if (value.AgeTicks > 0)
                        {
                            if (tooltip.Length > 0) tooltip += "\n";
                            tooltip += L(snapshot, "Age", "数据年龄") + $": {value.AgeTicks} ticks";
                        }
                        ImGui.SetTooltip(tooltip);
                    }
                }

                ImGui.EndTable();
            }
        }

        ImGui.Separator();
        if (ImGui.Button(L(snapshot, "Clear selection", "取消选择")))
            AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.ClearSelection));
        ImGui.SameLine();
        ImGui.TextDisabled("RWImGUI · V5 recorder/resolver presentation");
    }

    private static void DrawRecorderCursor(AIDebugPresentationSnapshot snapshot)
    {
        if (!snapshot.CursorMotion.HasValue && !snapshot.CursorFastState.HasValue) return;
        if (!ImGui.CollapsingHeader(L(snapshot, "Recorder Cursor", "记录游标"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.TextDisabled($"tick {snapshot.CursorTick}");
        if (snapshot.CursorMotion.HasValue)
        {
            double speed = Math.Sqrt(
                snapshot.CursorMotion.VX * snapshot.CursorMotion.VX +
                snapshot.CursorMotion.VY * snapshot.CursorMotion.VY);
            ImGui.Text($"{L(snapshot, "Position", "位置")}: ({snapshot.CursorMotion.X:0.0}, {snapshot.CursorMotion.Y:0.0})");
            ImGui.Text($"{L(snapshot, "Velocity", "速度")}: ({snapshot.CursorMotion.VX:0.00}, {snapshot.CursorMotion.VY:0.00})  |v|={speed:0.00}");
            if (snapshot.CursorMotion.AgeTicks > 0)
                ImGui.TextDisabled($"motion age {snapshot.CursorMotion.AgeTicks} ticks");
        }

        if (snapshot.CursorFastState.HasValue)
        {
            AIDebugFastState state = snapshot.CursorFastState.State;
            ImGui.Text($"{L(snapshot, "Room", "房间")}: {state.Room}   {L(snapshot, "State", "状态")}: {StateText(snapshot, state.EntityState)}");
            ImGui.Text($"{L(snapshot, "Destination", "目标坐标")}: {state.DestinationRoom}:{state.DestinationX},{state.DestinationY} node {state.DestinationNode}");
            if (state.ModeToken != AIDebugFastState.UnknownToken)
                ImGui.Text($"{L(snapshot, "Mode token", "模式编号")}: {state.ModeToken}");
            if (state.TargetNumber != AIDebugFastState.UnknownToken)
                ImGui.Text($"{L(snapshot, "Target ID", "目标ID")}: {state.TargetSpawner}:{state.TargetNumber}");
            ImGui.TextDisabled($"flags: {state.Flags} · state age {snapshot.CursorFastState.AgeTicks} ticks");
        }
    }

    private static bool Matches(AIDebugPresentationEntity entity)
    {
        if (string.IsNullOrWhiteSpace(entityFilter)) return true;
        string filter = entityFilter.Trim();
        return entity.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
               entity.Room.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
               entity.Key.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string L(AIDebugPresentationSnapshot snapshot, string english, string chinese) =>
        snapshot.Language == AIDebugLanguage.Chinese ? chinese : english;

    private static string StateText(AIDebugPresentationSnapshot snapshot, AIDebugEntityState state)
    {
        return state switch
        {
            AIDebugEntityState.Realized => L(snapshot, "Realized", "已实体化"),
            AIDebugEntityState.Abstract => L(snapshot, "Abstract", "抽象状态"),
            AIDebugEntityState.Shortcut => L(snapshot, "Shortcut", "管道中"),
            AIDebugEntityState.Den => L(snapshot, "Den", "巢穴中"),
            AIDebugEntityState.Deleted => L(snapshot, "Deleted", "待删除"),
            _ => state.ToString()
        };
    }

    private static string DecisionStateText(AIDebugPresentationSnapshot snapshot, AIDebugDecisionState state)
    {
        return state switch
        {
            AIDebugDecisionState.Active => L(snapshot, "ACTIVE", "控制中"),
            AIDebugDecisionState.Ready => L(snapshot, "READY", "可执行"),
            AIDebugDecisionState.Blocked => L(snapshot, "BLOCKED", "被阻止"),
            AIDebugDecisionState.Inactive => L(snapshot, "INACTIVE", "未激活"),
            AIDebugDecisionState.Pass => L(snapshot, "PASS", "通过"),
            AIDebugDecisionState.Warning => L(snapshot, "WARNING", "警告"),
            _ => state.ToString()
        };
    }
}
