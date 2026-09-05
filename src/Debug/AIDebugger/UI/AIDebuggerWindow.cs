using System;
using System.Collections.Generic;
using DryCycle.Creatures.DesertBatfly;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal sealed class AIDebuggerWindow
{
    private const float SnapshotInterval = 0.10f;
    private const float AdvancedCaptureInterval = 0.10f;

    private readonly List<AbstractCreature> entities = new(128);
    private readonly HashSet<DebugEntityKey> pinned = new();
    private readonly List<AIDebugTraceEvent> events = new(512);
    private readonly List<AIDebugTraceFrame> frames = new(1200);
    private readonly List<AIDebugUtilityRow> utilities = new(24);
    private readonly List<AIDebugPerceptionRow> perception = new(32);
    private readonly Dictionary<string, string> compareLeft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> compareRight = new(StringComparer.Ordinal);

    private DebugEntityKey selected, compareKey, utilityOwner, perceptionOwner;
    private AbstractCreature selectedCreature, compareCreature;
    private bool hasSelection, hasCompare, hasUtilityOwner, hasPerceptionOwner;
    private bool fullMode;
    private bool showRawNames;
    private bool freezeView;
    private bool overlay = true, overlayPath = true, overlayPerception, overlayLabels = true;
    private bool autoScrollEvents = true;
    private int nextEntityRefresh;
    private float nextSnapshotRefresh, nextCompareRefresh, nextUtilityRefresh, nextPerceptionRefresh;
    private AIDebugSnapshot snapshot, frozenSnapshot, compareSnapshot;
    private int timelineCursor = -1;
    private string eventFilter = string.Empty;
    private string entityFilter = string.Empty;

    internal bool FullMode
    {
        get => fullMode;
        set => fullMode = value;
    }

    internal void Draw(RainWorldGame game, double overheadMs)
    {
        if (game == null)
        {
            DrawNoGame();
            return;
        }

        RefreshEntities(game);
        if (AIDebugWorldOverlay.TryPick(game, entities, ImGui.GetIO().WantCaptureMouse,
                out AbstractCreature picked))
            Select(picked);

        ResolveSelection(game);
        ResolveCompare(game);
        AIDebugTrace.ReplaceWatches(selected, hasSelection, pinned);
        SampleTrackedEntities();

        if (overlay)
        {
            bool drewFrozen = freezeView && TryCurrentTimelineFrame(out AIDebugTraceFrame frozenFrame) &&
                               AIDebugWorldOverlay.DrawFrozenTrace(game, frozenFrame, overlayLabels);
            if (!drewFrozen)
                AIDebugWorldOverlay.Draw(game, selectedCreature, pinned, FindEntity,
                    overlayPath, overlayPerception, overlayLabels);
        }

        if (fullMode) DrawFull(game, overheadMs);
        else DrawCompact(overheadMs);
    }

    private AIDebugSnapshot ActiveSnapshot =>
        freezeView && frozenSnapshot != null ? frozenSnapshot : snapshot;

    private void DrawNoGame()
    {
        ImGui.SetNextWindowPos(new Num.Vector2(18f, 18f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(410f, 96f), ImGuiCond.Always);
        if (ImGui.Begin(AIDebugLocalization.T("app.title"),
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_game"));
        ImGui.End();
    }

    private void DrawCompact(double overheadMs)
    {
        AIDebugSnapshot active = ActiveSnapshot;
        float width = 390f;
        ImGui.SetNextWindowPos(
            new Num.Vector2(Mathf.Max(8f, Screen.width - width - 16f), 16f),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 305f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(AIDebugLocalization.T("app.title") + "###DryCycleAICompact",
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        DrawTopControls(overheadMs);
        ImGui.Separator();
        if (active == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            ImGui.TextDisabled(L("Alt + Left Click: pick creature in world",
                "Alt + 左键：从世界画面选择生物"));
            ImGui.End();
            return;
        }

        ImGui.Text(active.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled("[" + AIDebugLocalization.EntityState(active.EntityState) + "]");
        if (freezeView)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Num.Vector4(0.95f, 0.70f, 0.28f, 1f),
                L("FROZEN", "已冻结视图"));
        }

        DrawCompactRow("app.control_owner", active.ControlOwner);
        DrawCompactField(active, "field.mode", "DesertBatflyAI.Mode");
        DrawCompactField(active, "field.expressed_role", "DesertBatflySocialRoles.Expressed");
        DrawCompactField(active, "field.target", "DesertBatflyAI.Target");
        DrawCompactField(active, "field.immediate_danger", "DesertBatflyAI.HasImmediateDanger");
        DrawCompactField(active, "field.suppression", "DesertBatflySocialRoles.Suppression");

        if (hasSelection && AIDebugTrace.CopyEvents(selected, events) > 0)
        {
            AIDebugTraceEvent latest = events[events.Count - 1];
            ImGui.Separator();
            ImGui.TextDisabled(L("Latest event", "最近事件"));
            ImGui.SameLine();
            ImGui.Text($"{latest.Category}: {latest.Name} {latest.Detail}");
        }
        ImGui.End();
    }

    private void DrawFull(RainWorldGame game, double overheadMs)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new Num.Vector2(Mathf.Max(760f, Screen.width - 16f),
                Mathf.Max(480f, Screen.height - 16f)),
            ImGuiCond.Always);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse |
                                 ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoResize;
        if (!ImGui.Begin(AIDebugLocalization.T("app.title") + "###DryCycleAIFull", flags))
        {
            ImGui.End();
            return;
        }

        DrawTopControls(overheadMs);
        ImGui.Separator();
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float browserWidth = Mathf.Clamp(available.X * 0.21f, 220f, 330f);

        ImGui.BeginChild("##entityBrowser", new Num.Vector2(browserWidth, available.Y), true);
        DrawEntityBrowser(game);
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("##workspace", new Num.Vector2(0f, available.Y), false);
        DrawWorkspace();
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawTopControls(double overheadMs)
    {
        if (ImGui.Button(fullMode
                ? AIDebugLocalization.T("app.compact")
                : AIDebugLocalization.T("app.full")))
            fullMode = !fullMode;

        ImGui.SameLine();
        if (ImGui.Button(AIDebugLocalization.Language == AIDebugLanguage.Chinese
                ? "English"
                : "中文"))
        {
            AIDebugLocalization.Language = AIDebugLocalization.Language == AIDebugLanguage.Chinese
                ? AIDebugLanguage.English
                : AIDebugLanguage.Chinese;
            nextSnapshotRefresh = nextCompareRefresh = 0f;
            if (!freezeView) snapshot = null;
            compareSnapshot = null;
        }

        ImGui.SameLine();
        if (ImGui.Button(freezeView
                ? L("Unfreeze", "解除冻结")
                : L("Freeze View", "冻结视图")))
            ToggleFreeze();

        ImGui.SameLine();
        ImGui.Checkbox(L("Overlay", "世界叠加"), ref overlay);
        ImGui.SameLine();
        ImGui.Checkbox(AIDebugLocalization.T("app.raw_names"), ref showRawNames);
        ImGui.SameLine();
        ImGui.TextDisabled($"{AIDebugLocalization.T("app.overhead")}: {overheadMs:0.00} ms");
        ImGui.SameLine();
        ImGui.TextDisabled("F7 · F6 · Alt+LMB");
    }

    private void DrawEntityBrowser(RainWorldGame game)
    {
        ImGui.Text(AIDebugLocalization.T("app.entity_browser"));
        ImGui.TextDisabled(L("Click = select · Shift+Click = compare",
            "点击=选择 · Shift+点击=对比对象"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(L("Search##entitySearch", "搜索##entitySearch"), ref entityFilter, 96);
        ImGui.Separator();

        if (pinned.Count > 0)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.pinned"));
            foreach (DebugEntityKey key in pinned)
            {
                AbstractCreature creature = FindEntity(key);
                if (creature != null && MatchesEntityFilter(creature))
                    DrawEntityRow(creature, true);
            }
            ImGui.Separator();
        }

        int currentRoom = game.cameras != null && game.cameras.Length > 0 &&
                          game.cameras[0]?.room != null
            ? game.cameras[0].room.abstractRoom.index
            : int.MinValue;

        ImGui.TextDisabled(AIDebugLocalization.T("app.current_room"));
        for (int i = 0; i < entities.Count; i++)
            if (entities[i].pos.room == currentRoom && MatchesEntityFilter(entities[i]))
                DrawEntityRow(entities[i], false);

        ImGui.Separator();
        ImGui.TextDisabled("World");
        for (int i = 0; i < entities.Count; i++)
            if (entities[i].pos.room != currentRoom && MatchesEntityFilter(entities[i]))
                DrawEntityRow(entities[i], false);
    }

    private bool MatchesEntityFilter(AbstractCreature creature)
    {
        if (string.IsNullOrWhiteSpace(entityFilter)) return true;
        string type = creature?.creatureTemplate?.type?.value ?? string.Empty;
        string id = creature == null ? string.Empty : creature.ID.number.ToString();
        string key = creature == null ? string.Empty : DebugEntityKey.From(creature).ToString();
        return Contains(type, entityFilter) || Contains(id, entityFilter) || Contains(key, entityFilter);
    }

    private void DrawEntityRow(AbstractCreature creature, bool pinnedSection)
    {
        DebugEntityKey key = DebugEntityKey.From(creature);
        bool selectedNow = hasSelection && key == selected;
        bool compareNow = hasCompare && key == compareKey;
        string type = creature.creatureTemplate?.type?.value ?? "Creature";
        string state = AIDebugLocalization.EntityState(AIDebugRegistry.EntityState(creature));
        string prefix = compareNow ? "[B] " : selectedNow ? "[A] " : string.Empty;
        string label = $"{prefix}{type} #{creature.ID.number} [{state}]##{key.Spawner}:{key.Number}:{pinnedSection}";
        if (ImGui.Selectable(label, selectedNow))
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                SetCompare(creature);
            else
                Select(creature);
        }
        if (ImGui.IsItemHovered() && showRawNames) ImGui.SetTooltip(key.ToString());
    }

    private void DrawWorkspace()
    {
        AIDebugSnapshot active = ActiveSnapshot;
        if (active == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            return;
        }

        if (!ImGui.BeginTabBar("##AIDebugTabs", ImGuiTabBarFlags.FittingPolicyScroll)) return;

        if (ImGui.BeginTabItem(L("Overview", "总览")))
        {
            DrawOverview();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Timeline", "时间线")))
        {
            DrawTimeline();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Events", "事件日志")))
        {
            DrawEvents();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Utility", "效用比较")))
        {
            DrawUtility();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Perception", "感知 / Tracker")))
        {
            DrawPerception();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Path", "路径 / 控制链")))
        {
            DrawPath();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Compare", "对比")))
        {
            DrawCompare();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawOverview()
    {
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float decisionWidth = Mathf.Clamp(available.X * 0.43f, 330f, 560f);
        ImGui.BeginChild("##decisionStack", new Num.Vector2(decisionWidth, available.Y), true);
        DrawDecisionStack(ActiveSnapshot);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##inspector", new Num.Vector2(0f, available.Y), true);
        DrawInspector(ActiveSnapshot);
        ImGui.EndChild();
    }

    private void DrawDecisionStack(AIDebugSnapshot active)
    {
        ImGui.Text(AIDebugLocalization.T("app.decision_stack"));
        ImGui.Separator();
        ImGui.TextDisabled(AIDebugLocalization.T("app.control_owner"));
        ImGui.SameLine();
        ImGui.Text(active.ControlOwner);
        ImGui.Separator();

        for (int i = 0; i < active.Decisions.Count; i++)
        {
            AIDebugDecisionNode node = active.Decisions[i];
            if (node.Depth > 0) ImGui.Indent(node.Depth * 14f);
            ImGui.TextColored(StateColor(node.State), AIDebugLocalization.DecisionState(node.State));
            ImGui.SameLine();
            ImGui.Text(AIDebugLocalization.T(node.LabelKey));
            if (!string.IsNullOrEmpty(node.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(node.Detail);
            }
            if (showRawNames && !string.IsNullOrEmpty(node.RawName) && ImGui.IsItemHovered())
                ImGui.SetTooltip(node.RawName);
            if (node.Depth > 0) ImGui.Unindent(node.Depth * 14f);
        }

        if (hasSelection) AIDebugTrace.CopyEvents(selected, events);
        if (events.Count <= 0) return;

        ImGui.Separator();
        ImGui.TextDisabled(L("Why / Why not (latest transitions)",
            "Why / Why Not（最近决策变化）"));
        int first = Mathf.Max(0, events.Count - 8);
        for (int i = first; i < events.Count; i++)
        {
            AIDebugTraceEvent e = events[i];
            ImGui.BulletText($"{e.Name}: {e.Detail}");
            if (string.IsNullOrEmpty(e.Reason)) continue;
            ImGui.Indent(14f);
            ImGui.TextDisabled(e.Reason);
            ImGui.Unindent(14f);
        }
    }

    private void DrawInspector(AIDebugSnapshot active)
    {
        bool isPinned = pinned.Contains(active.Key);
        if (ImGui.Button(AIDebugLocalization.T(isPinned ? "app.unpin" : "app.pin")))
        {
            if (isPinned) pinned.Remove(active.Key);
            else pinned.Add(active.Key);
            AIDebugTrace.ReplaceWatches(selected, hasSelection, pinned);
        }

        ImGui.SameLine();
        if (ImGui.Button(L("Set Compare B", "设为对比 B")) && selectedCreature != null)
            SetCompare(selectedCreature);
        ImGui.SameLine();
        ImGui.Text(active.DisplayName);
        ImGui.Separator();

        for (int s = 0; s < active.Sections.Count; s++)
        {
            AIDebugSection section = active.Sections[s];
            if (!ImGui.CollapsingHeader(AIDebugLocalization.T(section.TitleKey),
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            for (int i = 0; i < section.Values.Count; i++) DrawValue(section.Values[i]);
            ImGui.Spacing();
        }
    }

    private void DrawTimeline()
    {
        if (!hasSelection || AIDebugTrace.CopyFrames(selected, frames) == 0)
        {
            ImGui.TextDisabled(L("No trace frames yet. Keep the creature selected for a moment.",
                "暂无时间线帧。保持选中该生物片刻即可开始记录。"));
            return;
        }

        if (ImGui.Button(freezeView
                ? L("Resume live view", "恢复实时视图")
                : L("Freeze at latest", "冻结到最新帧")))
            ToggleFreeze();
        ImGui.SameLine();
        if (ImGui.Button(L("Clear trace", "清空追踪")))
        {
            AIDebugTrace.Clear(selected);
            frames.Clear();
            events.Clear();
            timelineCursor = -1;
            if (freezeView) ToggleFreeze();
            return;
        }

        float duration = frames.Count > 1
            ? Mathf.Max(0f, frames[frames.Count - 1].Time - frames[0].Time)
            : 0f;
        ImGui.SameLine();
        ImGui.TextDisabled($"{frames.Count} samples · {duration:0.0}s window");

        if (timelineCursor < 0 || timelineCursor >= frames.Count)
            timelineCursor = frames.Count - 1;

        DrawTimelineStrip();
        if (frames.Count == 0) return;

        ImGui.SetNextItemWidth(Mathf.Min(600f, ImGui.GetContentRegionAvail().X));
        ImGui.SliderInt(L("Sample", "采样帧"), ref timelineCursor, 0, frames.Count - 1);
        AIDebugTraceFrame f = frames[Mathf.Clamp(timelineCursor, 0, frames.Count - 1)];

        if (ImGui.BeginTable("##timelineFrame", 2,
                ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
        {
            Row(L("Frame / Time", "帧 / 时间"), $"{f.Frame} / {f.Time:0.000}s");
            Row(L("Room", "房间"), f.Room);
            Row(L("Control owner", "当前控制者"), f.ControlOwner);
            Row(L("Mode", "模式"), f.Mode);
            Row(L("Target", "目标"), f.Target);
            Row(L("Role", "角色"), f.Role);
            Row(L("Suppression", "抑制"), f.Suppression);
            Row(L("Position", "位置"), AIDebugFormat.Value(f.Position));
            Row(L("Velocity", "速度"), AIDebugFormat.Value(f.Velocity));
            Row(L("Local goal", "局部目标"), AIDebugFormat.Value(f.LocalGoal));
            Row("Utility 0 / 1 / 2",
                $"{f.Utility0:0.000} / {f.Utility1:0.000} / {f.Utility2:0.000}");
            Row(L("Panic", "恐慌比例"), f.Panic.ToString("0.000"));
            ImGui.EndTable();
        }
    }

    private void DrawTimelineStrip()
    {
        float width = ImGui.GetContentRegionAvail().X;
        float height = 74f;
        Num.Vector2 start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##timelineStrip", new Num.Vector2(width, height));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Num.Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.08f, 0.09f, 0.11f, 1f)));
        if (frames.Count == 0) return;

        float step = width / frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            float x0 = start.X + i * step;
            float x1 = start.X + (i + 1) * step + 0.5f;
            draw.AddRectFilled(
                new Num.Vector2(x0, start.Y + 7f),
                new Num.Vector2(x1, start.Y + height - 7f),
                TimelineColor(frames[i]));
        }

        if (timelineCursor >= 0 && timelineCursor < frames.Count)
        {
            float x = start.X + (timelineCursor + 0.5f) * step;
            draw.AddLine(new Num.Vector2(x, start.Y),
                new Num.Vector2(x, start.Y + height), 0xffffffff, 2f);
        }

        if (!ImGui.IsItemClicked(ImGuiMouseButton.Left)) return;
        float localX = ImGui.GetIO().MousePos.X - start.X;
        timelineCursor = Mathf.Clamp(
            Mathf.FloorToInt(localX / Mathf.Max(0.001f, step)),
            0,
            frames.Count - 1);
        if (!freezeView) ToggleFreeze();
    }

    private void DrawEvents()
    {
        if (!hasSelection || AIDebugTrace.CopyEvents(selected, events) == 0)
        {
            ImGui.TextDisabled(L("No events recorded yet.", "暂无事件记录。"));
            return;
        }

        ImGui.SetNextItemWidth(Mathf.Min(420f, ImGui.GetContentRegionAvail().X * 0.55f));
        ImGui.InputText(L("Filter", "筛选"), ref eventFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox(L("Auto-scroll", "自动滚动"), ref autoScrollEvents);
        ImGui.Separator();

        ImGui.BeginChild("##eventLog", new Num.Vector2(0f, 0f), false);
        bool wasNearBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 24f;
        int shown = 0;
        for (int i = 0; i < events.Count; i++)
        {
            AIDebugTraceEvent e = events[i];
            if (!EventMatches(e, eventFilter)) continue;
            ImGui.TextColored(EventColor(e.Category), $"[{e.Frame}] {e.Category}");
            ImGui.SameLine();
            ImGui.Text(e.Name);
            if (!string.IsNullOrEmpty(e.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(e.Detail);
            }
            if (!string.IsNullOrEmpty(e.Reason))
            {
                ImGui.Indent(18f);
                ImGui.TextDisabled("↳ " + e.Reason);
                ImGui.Unindent(18f);
            }
            shown++;
        }
        if (shown == 0)
            ImGui.TextDisabled(L("No event matches the filter.",
                "没有匹配筛选条件的事件。"));
        if (autoScrollEvents && wasNearBottom) ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    private void DrawUtility()
    {
        if (selectedCreature == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            return;
        }

        DebugEntityKey key = DebugEntityKey.From(selectedCreature);
        if (!hasUtilityOwner || utilityOwner != key || Time.unscaledTime >= nextUtilityRefresh)
        {
            AIDebugAdvancedCapture.CaptureUtilities(selectedCreature, utilities);
            utilityOwner = key;
            hasUtilityOwner = true;
            nextUtilityRefresh = Time.unscaledTime + AdvancedCaptureInterval;
        }

        if (utilities.Count == 0)
        {
            ImGui.TextDisabled(L("This AI has no UtilityComparer and no custom utility adapter.",
                "该 AI 没有 UtilityComparer，也没有自定义效用适配器。"));
            return;
        }

        ImGui.TextDisabled(selectedCreature.realizedCreature is DesertBatfly
            ? L("DesertBatfly role scores are shown as custom utilities.",
                "沙漠蝠蝇的角色评分作为自定义效用显示。")
            : L("Values come from the live vanilla UtilityComparer trackers.",
                "数据直接来自原版 UtilityComparer 的实时 tracker。"));

        if (ImGui.BeginTable("##utilityTable", 7,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn(L("Module", "模块"));
            ImGui.TableSetupColumn("Raw");
            ImGui.TableSetupColumn("Smoothed");
            ImGui.TableSetupColumn("Weight");
            ImGui.TableSetupColumn("Weighted");
            ImGui.TableSetupColumn("Continuation");
            ImGui.TableSetupColumn(L("Winner", "胜出"));
            ImGui.TableHeadersRow();

            for (int i = 0; i < utilities.Count; i++)
            {
                AIDebugUtilityRow u = utilities[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text(u.Name);
                ImGui.TableSetColumnIndex(1); ImGui.Text($"{u.Raw:0.000}");
                ImGui.TableSetColumnIndex(2); ImGui.Text($"{u.Smoothed:0.000}");
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{u.Weight:0.000}");
                ImGui.TableSetColumnIndex(4); ImGui.Text($"{u.Weighted:0.000}");
                ImGui.TableSetColumnIndex(5); ImGui.Text($"{u.ContinuationBonus:0.000}");
                ImGui.TableSetColumnIndex(6);
                if (u.Winner)
                    ImGui.TextColored(new Num.Vector4(0.42f, 0.90f, 0.55f, 1f), "YES");
                else
                    ImGui.TextDisabled("—");
            }
            ImGui.EndTable();
        }

        if (selectedCreature.realizedCreature is not DesertBatfly bat) return;
        DesertBatflyFlockSnapshot flock = default;
        if (bat.room != null && DesertSwarmRoom.TryGet(bat.room, out DesertSwarmRoom colony))
            flock = colony.Flock;
        DesertBatflyRoleScores scores = bat.DesertAI.Roles.Scores;
        float threshold = DesertBatflyRoleScores.EntryThreshold(
            flock.ActiveCount, flock.ExpressedRoleCount);
        float best = Mathf.Max(scores.Sentinel, Mathf.Max(scores.Bully, scores.Opportunist));
        ImGui.Separator();
        ImGui.Text($"{L("Entry threshold", "进入阈值")}: {threshold:0.000}");
        ImGui.Text($"{L("Best score", "最高分")}: {best:0.000}");
        ImGui.TextDisabled(DesertRoleExplanation(bat, flock));
    }

    private void DrawPerception()
    {
        if (selectedCreature == null) return;
        ImGui.Checkbox(L("Draw perception overlay", "绘制感知叠加"), ref overlayPerception);

        DebugEntityKey key = DebugEntityKey.From(selectedCreature);
        if (!hasPerceptionOwner || perceptionOwner != key ||
            Time.unscaledTime >= nextPerceptionRefresh)
        {
            AIDebugAdvancedCapture.CapturePerception(selectedCreature, perception);
            perceptionOwner = key;
            hasPerceptionOwner = true;
            nextPerceptionRefresh = Time.unscaledTime + AdvancedCaptureInterval;
        }

        if (perception.Count == 0)
        {
            ImGui.TextDisabled(L("No vanilla Tracker representations are available for this AI.",
                "该 AI 当前没有可用的原版 Tracker 表示。"));
            return;
        }

        if (ImGui.BeginTable("##perceptionTable", 8,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn(L("Creature", "生物"));
            ImGui.TableSetupColumn(L("Visible", "可见"));
            ImGui.TableSetupColumn(L("Since seen", "距目击"));
            ImGui.TableSetupColumn(L("Find chance", "找到概率"));
            ImGui.TableSetupColumn(L("Priority", "优先级"));
            ImGui.TableSetupColumn(L("Last seen", "最后目击"));
            ImGui.TableSetupColumn(L("Best guess", "最佳估计"));
            ImGui.TableSetupColumn(L("Relationship", "关系"));
            ImGui.TableHeadersRow();

            for (int i = 0; i < perception.Count; i++)
            {
                AIDebugPerceptionRow p = perception[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text(p.Name);
                ImGui.TableSetColumnIndex(1); ImGui.Text(p.VisualContact ? "YES" : "no");
                ImGui.TableSetColumnIndex(2); ImGui.Text(p.TicksSinceSeen.ToString());
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{p.EstimatedChance:0.000}");
                ImGui.TableSetColumnIndex(4); ImGui.Text($"{p.Priority:0.000}");
                ImGui.TableSetColumnIndex(5); ImGui.Text(p.LastSeen.ToString());
                ImGui.TableSetColumnIndex(6); ImGui.Text(p.BestGuess.ToString());
                ImGui.TableSetColumnIndex(7);
                ImGui.Text($"{p.Relationship} {p.RelationshipIntensity:0.00}");
            }
            ImGui.EndTable();
        }
    }

    private void DrawPath()
    {
        if (selectedCreature == null) return;
        ImGui.Checkbox(L("Draw path / goal overlay", "绘制路径 / 目标叠加"), ref overlayPath);
        ImGui.SameLine();
        ImGui.Checkbox(L("Labels", "标签"), ref overlayLabels);
        ImGui.Separator();

        AIDebugPathState path = AIDebugAdvancedCapture.CapturePath(selectedCreature);
        if (ImGui.BeginTable("##pathChain", 3,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(L("Layer", "层级"));
            ImGui.TableSetupColumn(L("Value", "值"));
            ImGui.TableSetupColumn(L("Meaning", "含义"));
            ImGui.TableHeadersRow();

            PathRow("INTENT", path.Destination.ToString(),
                L("Abstract AI destination / desired world coordinate",
                    "抽象 AI 的世界目标坐标"));
            PathRow("PLANNER", path.HasPathfinder ? path.Pathfinder : "—",
                path.HasPathfinder
                    ? $"reachable={path.DestinationReachable}, returnable={path.CanReturnFromDestination}, stranded={path.Stranded}"
                    : L("No PathFinder module", "没有 PathFinder 模块"));

            if (selectedCreature.realizedCreature is DesertBatfly bat)
                PathRow("MOTOR", AIDebugFormat.Value(bat.AI?.localGoal),
                    $"DesertAI={bat.DesertAI.Mode}, vanilla={bat.AI?.behavior}, vel={AIDebugFormat.Value(bat.mainBodyChunk?.vel)}");
            else
                PathRow("MOTOR",
                    AIDebugFormat.Value(selectedCreature.realizedCreature?.mainBodyChunk?.pos),
                    selectedCreature.realizedCreature == null
                        ? L("Abstract / unrealized", "抽象 / 未实体化")
                        : L("Creature physics / species motor", "生物物理 / 物种运动执行"));

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled(L(
            "Intent → Planner → Motor is separated so a correct destination can be distinguished from a bad path or bad locomotion command.",
            "将 Intent → Planner → Motor 分开显示，用于区分“目标正确但寻路错”与“寻路正确但运动执行错”。"));
    }

    private void DrawCompare()
    {
        if (!hasCompare || compareCreature == null || compareSnapshot == null)
        {
            ImGui.TextDisabled(L(
                "Shift+Click another creature in the Entity Browser to set comparison B.",
                "在实体浏览器中 Shift+点击另一只生物，将其设为对比对象 B。"));
            return;
        }

        AIDebugSnapshot left = ActiveSnapshot;
        if (left == null) return;
        if (ImGui.Button(L("Clear B", "清除 B")))
        {
            hasCompare = false;
            compareCreature = null;
            compareSnapshot = null;
            return;
        }

        ImGui.SameLine();
        ImGui.Text($"A: {left.DisplayName}    ↔    B: {compareSnapshot.DisplayName}");
        ImGui.Separator();

        Flatten(left, compareLeft);
        Flatten(compareSnapshot, compareRight);
        if (!ImGui.BeginTable("##compareTable", 3,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupColumn(L("Field", "字段"));
        ImGui.TableSetupColumn("A");
        ImGui.TableSetupColumn("B");
        ImGui.TableHeadersRow();

        foreach (KeyValuePair<string, string> pair in compareLeft)
        {
            compareRight.TryGetValue(pair.Key, out string right);
            if (right == pair.Value && !showRawNames) continue;
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.Text(pair.Key);
            ImGui.TableSetColumnIndex(1); ImGui.Text(pair.Value);
            ImGui.TableSetColumnIndex(2); ImGui.Text(right ?? "—");
        }
        foreach (KeyValuePair<string, string> pair in compareRight)
        {
            if (compareLeft.ContainsKey(pair.Key)) continue;
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.Text(pair.Key);
            ImGui.TableSetColumnIndex(1); ImGui.Text("—");
            ImGui.TableSetColumnIndex(2); ImGui.Text(pair.Value);
        }
        ImGui.EndTable();
    }

    private void DrawValue(AIDebugValue value)
    {
        ImGui.TextDisabled(AIDebugLocalization.T(value.LabelKey));
        if (showRawNames && !string.IsNullOrEmpty(value.RawName))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("  " + value.RawName);
        }
        ImGui.SameLine(Mathf.Max(205f, ImGui.GetWindowWidth() * 0.48f));
        ImGui.Text(value.Value);
        if (value.AgeTicks <= 0 && string.IsNullOrEmpty(value.Source)) return;

        ImGui.SameLine();
        string age = value.AgeTicks > 0
            ? $"{value.AgeTicks} {AIDebugLocalization.T("app.ticks")}"
            : "live";
        ImGui.TextDisabled($"[{age}{(string.IsNullOrEmpty(value.Source) ? "" : " · " + value.Source)}]");
    }

    private void DrawCompactField(AIDebugSnapshot active, string labelKey, string rawName)
    {
        for (int s = 0; s < active.Sections.Count; s++)
        for (int i = 0; i < active.Sections[s].Values.Count; i++)
        {
            AIDebugValue value = active.Sections[s].Values[i];
            if (value.RawName != rawName) continue;
            DrawCompactRow(labelKey, value.Value);
            return;
        }
    }

    private static void DrawCompactRow(string labelKey, string value)
    {
        ImGui.TextDisabled(AIDebugLocalization.T(labelKey));
        ImGui.SameLine(165f);
        ImGui.Text(value ?? "—");
    }

    private void RefreshEntities(RainWorldGame game)
    {
        if (Time.frameCount < nextEntityRefresh) return;
        nextEntityRefresh = Time.frameCount + 15;
        AIDebugRegistry.CollectWorld(game, entities);
        entities.Sort(CompareEntities);

        if (hasSelection || entities.Count <= 0) return;
        AbstractCreature candidate = null;
        int currentRoom = game.cameras != null && game.cameras.Length > 0 &&
                          game.cameras[0]?.room != null
            ? game.cameras[0].room.abstractRoom.index
            : int.MinValue;

        for (int i = 0; i < entities.Count; i++)
        {
            AbstractCreature creature = entities[i];
            if (creature.pos.room != currentRoom) continue;
            if (creature.realizedCreature is DesertBatfly)
            {
                candidate = creature;
                break;
            }
            candidate ??= creature.realizedCreature != null ? creature : null;
        }
        candidate ??= entities[0];
        Select(candidate);
    }

    private void Select(AbstractCreature creature)
    {
        if (creature == null) return;
        selected = DebugEntityKey.From(creature);
        selectedCreature = creature;
        hasSelection = true;
        snapshot = frozenSnapshot = null;
        freezeView = false;
        timelineCursor = -1;
        events.Clear();
        frames.Clear();
        utilities.Clear();
        perception.Clear();
        hasUtilityOwner = hasPerceptionOwner = false;
        nextSnapshotRefresh = nextUtilityRefresh = nextPerceptionRefresh = 0f;
        AIDebugTrace.ReplaceWatches(selected, true, pinned);
        AIDebugTrace.Record(selected, AIDebugEventCategory.State,
            "Selected", selected.ToString(), "world/browser selection");
    }

    private void SetCompare(AbstractCreature creature)
    {
        if (creature == null) return;
        compareKey = DebugEntityKey.From(creature);
        compareCreature = creature;
        hasCompare = true;
        compareSnapshot = null;
        nextCompareRefresh = 0f;
    }

    private void ToggleFreeze()
    {
        if (!freezeView)
        {
            if (snapshot == null) return;
            frozenSnapshot = snapshot;
            freezeView = true;
            if (hasSelection && AIDebugTrace.CopyFrames(selected, frames) > 0)
                timelineCursor = frames.Count - 1;
            return;
        }

        freezeView = false;
        frozenSnapshot = null;
        nextSnapshotRefresh = 0f;
    }

    private bool TryCurrentTimelineFrame(out AIDebugTraceFrame frame)
    {
        frame = default;
        if (!hasSelection) return false;
        if (AIDebugTrace.CopyFrames(selected, frames) <= 0) return false;
        if (timelineCursor < 0 || timelineCursor >= frames.Count)
            timelineCursor = frames.Count - 1;
        frame = frames[timelineCursor];
        return true;
    }

    private void ResolveSelection(RainWorldGame game)
    {
        if (!hasSelection)
        {
            selectedCreature = null;
            snapshot = null;
            return;
        }

        if (selectedCreature == null || selectedCreature.slatedForDeletion ||
            DebugEntityKey.From(selectedCreature) != selected)
            selectedCreature = FindEntity(selected) ?? AIDebugRegistry.Resolve(game, selected);

        if (selectedCreature == null)
        {
            snapshot = null;
            return;
        }
        if (freezeView) return;
        if (snapshot != null && Time.unscaledTime < nextSnapshotRefresh) return;

        snapshot = AIDebugRegistry.Capture(selectedCreature, game);
        nextSnapshotRefresh = Time.unscaledTime + SnapshotInterval;
    }

    private void ResolveCompare(RainWorldGame game)
    {
        if (!hasCompare) return;
        if (compareCreature == null || compareCreature.slatedForDeletion ||
            DebugEntityKey.From(compareCreature) != compareKey)
            compareCreature = FindEntity(compareKey) ?? AIDebugRegistry.Resolve(game, compareKey);

        if (compareCreature == null)
        {
            compareSnapshot = null;
            return;
        }
        if (compareSnapshot != null && Time.unscaledTime < nextCompareRefresh) return;

        compareSnapshot = AIDebugRegistry.Capture(compareCreature, game);
        nextCompareRefresh = Time.unscaledTime + SnapshotInterval;
    }

    private void SampleTrackedEntities()
    {
        if (!AIDebugTrace.Visible) return;
        if (selectedCreature != null) SampleTrace(selectedCreature);
        foreach (DebugEntityKey key in pinned)
        {
            AbstractCreature creature = FindEntity(key);
            if (creature != null && !ReferenceEquals(creature, selectedCreature))
                SampleTrace(creature);
        }
    }

    private static void SampleTrace(AbstractCreature creature)
    {
        if (creature?.realizedCreature is DesertBatfly bat)
        {
            DesertBatflyDebugTrace.Sample(bat);
            return;
        }
        if (!AIDebugTrace.IsWatched(creature)) return;

        Creature realized = creature.realizedCreature;
        ArtificialIntelligence ai = creature.abstractAI?.RealAI;
        string controller = ai?.GetType().Name ??
                            creature.abstractAI?.GetType().Name ??
                            "AbstractCreature";
        UtilityComparer.UtilityTracker winner = ai?.utilityComparer?.highestUtilityTracker;
        string utility = winner?.module?.GetType().Name ?? "—";

        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Decision,
            "ControlOwner", controller, "ArtificialIntelligence type");
        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Path,
            "Destination", creature.abstractAI?.destination, "AbstractCreatureAI.destination");
        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Decision,
            "HighestUtility", utility, "UtilityComparer cached winner");

        Vector2 pos = realized?.mainBodyChunk?.pos ?? Vector2.zero;
        Vector2 vel = realized?.mainBodyChunk?.vel ?? Vector2.zero;
        float cachedHighest = winner?.smoother != null ? winner.smoothedUtility : 0f;
        AIDebugTrace.Sample(creature, new AIDebugTraceFrame(
            creature.Room?.name,
            pos,
            vel,
            pos,
            controller,
            utility,
            "—",
            "—",
            controller,
            cachedHighest,
            0f,
            0f,
            0f));
    }

    private AbstractCreature FindEntity(DebugEntityKey key)
    {
        for (int i = 0; i < entities.Count; i++)
            if (DebugEntityKey.From(entities[i]) == key)
                return entities[i];
        return null;
    }

    private static void Flatten(AIDebugSnapshot source, Dictionary<string, string> output)
    {
        output.Clear();
        output["ControlOwner"] = source.ControlOwner;
        output["Lifecycle"] = AIDebugLocalization.EntityState(source.EntityState);
        for (int s = 0; s < source.Sections.Count; s++)
        for (int i = 0; i < source.Sections[s].Values.Count; i++)
        {
            AIDebugValue value = source.Sections[s].Values[i];
            string name = !string.IsNullOrEmpty(value.RawName)
                ? value.RawName
                : AIDebugLocalization.T(value.LabelKey);
            output[name] = value.Value;
        }
    }

    private static bool EventMatches(AIDebugTraceEvent e, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return Contains(e.Category.ToString(), filter) ||
               Contains(e.Name, filter) ||
               Contains(e.Detail, filter) ||
               Contains(e.Reason, filter);
    }

    private static bool Contains(string text, string filter) =>
        !string.IsNullOrEmpty(text) &&
        text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string DesertRoleExplanation(
        DesertBatfly bat,
        DesertBatflyFlockSnapshot flock)
    {
        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        if (roles.Suppression != SocialRoleSuppression.None)
            return L("Blocked by ", "被以下状态阻止：") + roles.Suppression;
        if (roles.Cooldown > 0)
            return L("Role entry cooldown: ", "角色进入冷却：") + roles.Cooldown;
        if (bat.DesertAI.FormalAttack)
            return L("Formal attack currently owns behavior.",
                "正式攻击状态机当前拥有行为控制权。");

        DesertBatflyRoleScores s = roles.Scores;
        ExpressedSocialRole best = s.Bully > s.Sentinel
            ? ExpressedSocialRole.Bully
            : ExpressedSocialRole.Sentinel;
        if (s.Opportunist > s.For(best)) best = ExpressedSocialRole.Opportunist;

        float second = best switch
        {
            ExpressedSocialRole.Sentinel => Mathf.Max(s.Bully, s.Opportunist),
            ExpressedSocialRole.Bully => Mathf.Max(s.Sentinel, s.Opportunist),
            _ => Mathf.Max(s.Sentinel, s.Bully)
        };
        float threshold = DesertBatflyRoleScores.EntryThreshold(
            flock.ActiveCount, flock.ExpressedRoleCount);
        float bestScore = s.For(best);

        if (bestScore < threshold)
            return $"Why not {best}: score {bestScore:0.000} < threshold {threshold:0.000}";
        if (bestScore - second < 0.12f)
            return $"Why not {best}: lead {bestScore - second:0.000} < 0.120";
        return $"{best} eligible; evaluation in {roles.EvaluationTicks} ticks";
    }

    private static int CompareEntities(AbstractCreature a, AbstractCreature b)
    {
        int realized = (b.realizedCreature != null).CompareTo(a.realizedCreature != null);
        if (realized != 0) return realized;
        string at = a.creatureTemplate?.type?.value ?? string.Empty;
        string bt = b.creatureTemplate?.type?.value ?? string.Empty;
        int type = string.Compare(at, bt, StringComparison.Ordinal);
        return type != 0 ? type : a.ID.number.CompareTo(b.ID.number);
    }

    private static void Row(string left, string right)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); ImGui.TextDisabled(left);
        ImGui.TableSetColumnIndex(1); ImGui.Text(right ?? "—");
    }

    private static void PathRow(string layer, string value, string meaning)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); ImGui.Text(layer);
        ImGui.TableSetColumnIndex(1); ImGui.Text(value ?? "—");
        ImGui.TableSetColumnIndex(2); ImGui.TextDisabled(meaning ?? "—");
    }

    private static Num.Vector4 StateColor(AIDebugDecisionState state) => state switch
    {
        AIDebugDecisionState.Active => new Num.Vector4(0.30f, 0.82f, 0.95f, 1f),
        AIDebugDecisionState.Ready => new Num.Vector4(0.75f, 0.86f, 0.95f, 1f),
        AIDebugDecisionState.Blocked => new Num.Vector4(0.88f, 0.42f, 0.42f, 1f),
        AIDebugDecisionState.Warning => new Num.Vector4(0.95f, 0.70f, 0.28f, 1f),
        AIDebugDecisionState.Pass => new Num.Vector4(0.48f, 0.82f, 0.55f, 1f),
        _ => new Num.Vector4(0.55f, 0.58f, 0.62f, 1f)
    };

    private static Num.Vector4 EventColor(AIDebugEventCategory category) => category switch
    {
        AIDebugEventCategory.Warning => new Num.Vector4(0.95f, 0.67f, 0.25f, 1f),
        AIDebugEventCategory.Combat => new Num.Vector4(0.94f, 0.40f, 0.40f, 1f),
        AIDebugEventCategory.Social => new Num.Vector4(0.72f, 0.54f, 0.95f, 1f),
        AIDebugEventCategory.Perception => new Num.Vector4(0.42f, 0.82f, 0.92f, 1f),
        AIDebugEventCategory.Path => new Num.Vector4(0.45f, 0.86f, 0.55f, 1f),
        _ => new Num.Vector4(0.78f, 0.80f, 0.84f, 1f)
    };

    private static uint TimelineColor(AIDebugTraceFrame frame)
    {
        Num.Vector4 color = frame.Mode switch
        {
            "Escape" => new Num.Vector4(0.92f, 0.30f, 0.28f, 0.88f),
            "Dive" or "Attach" or "RetaliationCharge" or "Interfere" =>
                new Num.Vector4(0.92f, 0.48f, 0.20f, 0.88f),
            "Observe" or "Circle" or "FakeDive" =>
                new Num.Vector4(0.86f, 0.72f, 0.26f, 0.88f),
            "Roost" => new Num.Vector4(0.62f, 0.48f, 0.84f, 0.88f),
            _ => new Num.Vector4(0.28f, 0.68f, 0.82f, 0.80f)
        };
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static string L(string english, string chinese) =>
        AIDebugLocalization.Language == AIDebugLanguage.Chinese
            ? chinese
            : english;
}
