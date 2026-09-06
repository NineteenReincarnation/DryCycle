using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Offline session viewer consumes detached snapshots produced by AIDebugOfflineSessionStore.
// Refresh/Load buttons only queue ThreadPool work; no directory or trace.bin IO occurs on
// RWImGUI Present.
internal static class ObservatoryOfflineViewer
{
    private static bool refreshRequested;
    private static int selectedEntity;

    internal static void DrawWindow(ref bool open, AIDebugLanguage language)
    {
        if (!refreshRequested)
        {
            refreshRequested = true;
            AIDebugOfflineSessionStore.RefreshAsync();
        }

        ImGui.SetNextWindowSize(new Num.Vector2(980f, 640f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("AI Observatory Offline Sessions###DryCycleAIObservatoryOffline", ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        AIDebugOfflineCatalog catalog = AIDebugOfflineSessionStore.Catalog;
        AIDebugOfflineTrace trace = AIDebugOfflineSessionStore.Trace;

        if (ImGui.Button(L(language, "Refresh sessions", "刷新会话")))
            AIDebugOfflineSessionStore.RefreshAsync();
        ImGui.SameLine();
        ImGui.TextDisabled(catalog.Loading
            ? L(language, "Scanning session directory...", "正在扫描会话目录……")
            : $"{catalog.Sessions.Length} {L(language, "sessions", "个会话")}");
        if (!string.IsNullOrEmpty(catalog.Error))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("ERROR: " + catalog.Error);
        }

        ImGui.Separator();
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float listWidth = Math.Min(330f, Math.Max(245f, available.X * 0.34f));

        ImGui.BeginChild("##OfflineSessionList", new Num.Vector2(listWidth, 0f), ImGuiChildFlags.Borders);
        DrawSessionList(catalog, trace, language);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##OfflineSessionDetails", new Num.Vector2(0f, 0f), ImGuiChildFlags.Borders);
        DrawTrace(trace, language);
        ImGui.EndChild();

        ImGui.End();
    }

    private static void DrawSessionList(
        AIDebugOfflineCatalog catalog,
        AIDebugOfflineTrace trace,
        AIDebugLanguage language)
    {
        ImGui.Text(L(language, "Sessions", "会话"));
        ImGui.TextDisabled(L(language,
            "Recovered/incomplete sessions remain loadable after a crash.",
            "崩溃恢复或未完成的会话仍然可以读取。"));
        ImGui.Separator();

        for (int i = 0; i < catalog.Sessions.Length; i++)
        {
            AIDebugOfflineSessionSummary session = catalog.Sessions[i];
            bool selected = string.Equals(trace.Path, session.Path, StringComparison.OrdinalIgnoreCase);
            string state = session.Complete ? "OK" : session.Recovered ? "RECOVERED" : "INCOMPLETE";
            string label = $"{session.Name} [{state}]##OfflineSession{i}";
            if (ImGui.Selectable(label, selected))
            {
                selectedEntity = 0;
                AIDebugOfflineSessionStore.LoadAsync(session.Path);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{session.Path}\n{session.TraceBytes / 1024.0:0.0} KiB\nUTC {session.ModifiedUtc:yyyy-MM-dd HH:mm:ss}");
            }
        }

        if (catalog.Sessions.Length == 0 && !catalog.Loading)
            ImGui.TextDisabled(L(language, "No Recorder-* trace.bin sessions found.", "没有找到 Recorder-* trace.bin 会话。"));
    }

    private static void DrawTrace(AIDebugOfflineTrace trace, AIDebugLanguage language)
    {
        if (trace.Loading)
        {
            ImGui.TextDisabled(L(language, "Loading and validating trace.bin on worker thread...", "正在后台线程读取并校验 trace.bin……"));
            return;
        }

        if (!string.IsNullOrEmpty(trace.Error))
        {
            ImGui.TextDisabled("ERROR: " + trace.Error);
            return;
        }

        if (trace.Entities.Length == 0)
        {
            ImGui.TextDisabled(L(language, "Select a session from the left.", "从左侧选择一个会话。"));
            return;
        }

        if (selectedEntity < 0 || selectedEntity >= trace.Entities.Length) selectedEntity = 0;
        ImGui.Text(trace.SessionName);
        ImGui.SameLine();
        ImGui.TextDisabled($"{trace.ValidBlocks} blocks · {trace.BytesRead / 1024.0:0.0} KiB");

        if (ImGui.BeginCombo("##OfflineEntityCombo", trace.Entities[selectedEntity].DisplayName))
        {
            for (int i = 0; i < trace.Entities.Length; i++)
            {
                bool selected = i == selectedEntity;
                if (ImGui.Selectable(trace.Entities[i].DisplayName + "##OfflineEntity" + i, selected))
                    selectedEntity = i;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        AIDebugOfflineEntityTrace entity = trace.Entities[selectedEntity];
        ImGui.TextDisabled($"ticks {entity.StartTick}..{entity.EndTick} · motion {entity.Motion.Length} · states {entity.States.Length}" +
                           (entity.Truncated ? " · TRUNCATED" : string.Empty));

        if (ImGui.BeginTabBar("##OfflineTraceTabs"))
        {
            if (ImGui.BeginTabItem(L(language, "Movement", "移动")))
            {
                DrawMovement(entity, language);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(L(language, "State changes", "状态变化")))
            {
                DrawStates(entity, language);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private static void DrawMovement(AIDebugOfflineEntityTrace entity, AIDebugLanguage language)
    {
        AIDebugMotionSample[] motion = entity.Motion;
        if (motion.Length == 0)
        {
            ImGui.TextDisabled(L(language, "No MotionTrack samples.", "没有 MotionTrack 样本。"));
            return;
        }

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float height = Math.Max(160f, available.Y);
        ImGui.InvisibleButton("##OfflineMotionPlot", new Num.Vector2(Math.Max(160f, available.X), height));
        Num.Vector2 min = ImGui.GetItemRectMin();
        Num.Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint frame = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        uint plot = ImGui.GetColorU32(ImGuiCol.PlotLines);
        uint text = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        draw.AddRectFilled(min, max, frame);
        draw.AddRect(min, max, border);

        double maxSpeed = 0.001;
        for (int i = 0; i < motion.Length; i++)
        {
            double speed = Speed(motion[i]);
            if (speed > maxSpeed) maxSpeed = speed;
        }

        int budget = Math.Min(1200, motion.Length);
        bool previousValid = false;
        Num.Vector2 previous = default;
        for (int n = 0; n < budget; n++)
        {
            int index = budget == 1 ? 0 : n * (motion.Length - 1) / (budget - 1);
            AIDebugMotionSample sample = motion[index];
            float tx = motion.Length <= 1 ? 0f : index / (float)(motion.Length - 1);
            float ty = (float)(Speed(sample) / maxSpeed);
            Num.Vector2 point = new Num.Vector2(
                min.X + 6f + tx * Math.Max(1f, max.X - min.X - 12f),
                max.Y - 8f - ty * Math.Max(1f, max.Y - min.Y - 20f));
            if (previousValid) draw.AddLine(previous, point, plot, 1.3f);
            previous = point;
            previousValid = true;
        }

        draw.AddText(new Num.Vector2(min.X + 6f, min.Y + 4f), text,
            $"speed · max {maxSpeed:0.00} · {motion[0].Tick}..{motion[motion.Length - 1].Tick}");
    }

    private static void DrawStates(AIDebugOfflineEntityTrace entity, AIDebugLanguage language)
    {
        AIDebugFastStateSample[] states = entity.States;
        if (states.Length == 0)
        {
            ImGui.TextDisabled(L(language, "No StateTrack changes.", "没有 StateTrack 变化记录。"));
            return;
        }

        if (!ImGui.BeginTable("##OfflineStateTable", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY,
                new Num.Vector2(0f, Math.Max(120f, ImGui.GetContentRegionAvail().Y))))
            return;

        ImGui.TableSetupColumn("Tick", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(L(language, "Room", "房间"), ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn(L(language, "State", "状态"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(L(language, "Mode", "模式"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(L(language, "Target", "目标"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(L(language, "Flags / Destination", "标记 / 目的地"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (int i = 0; i < states.Length; i++)
        {
            AIDebugFastStateSample sample = states[i];
            AIDebugFastState state = sample.State;
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(sample.Tick.ToString());
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(state.Room.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(state.EntityState.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(state.ModeToken == AIDebugFastState.UnknownToken ? "—" : state.ModeToken.ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(state.TargetNumber == AIDebugFastState.UnknownToken
                ? "—"
                : $"{state.TargetSpawner}:{state.TargetNumber}");
            ImGui.TableSetColumnIndex(5);
            string destination = state.DestinationRoom == AIDebugFastState.UnknownToken
                ? "—"
                : $"{state.DestinationRoom}:{state.DestinationX},{state.DestinationY}/{state.DestinationNode}";
            ImGui.TextUnformatted($"{state.Flags} · {destination}");
        }

        ImGui.EndTable();
    }

    private static double Speed(AIDebugMotionSample sample) =>
        Math.Sqrt(sample.VX * sample.VX + sample.VY * sample.VY);

    private static string L(AIDebugLanguage language, string english, string chinese) =>
        language == AIDebugLanguage.Chinese ? chinese : english;
}
