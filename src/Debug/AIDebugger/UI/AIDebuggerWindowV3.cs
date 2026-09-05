using System;
using System.Collections.Generic;
using DryCycle.Creatures.DesertBatfly;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

// Final Observatory workspace. All expensive or behavior-sensitive data comes from
// retained AI state / explicit instrumentation; this window never re-runs AI decisions.
internal sealed class AIDebuggerWindowV3
{
    private const float SnapshotInterval = 0.10f;
    private const int EntityRefreshFrames = 15;

    private readonly List<AbstractCreature> entities = new(128);
    private readonly HashSet<DebugEntityKey> pinned = new();
    private readonly HashSet<int> visibleRooms = new();
    private readonly List<AIDebugTraceFrame> frames = new(600);
    private readonly List<AIDebugTraceEvent> events = new(1024);
    private readonly List<AIDebugUtilityRow> utilities = new(32);
    private readonly List<AIDebugPerceptionRow> perception = new(64);
    private readonly List<AIDebugCandidate> candidates = new(64);
    private readonly Dictionary<string, string> compareA = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> compareB = new(StringComparer.Ordinal);
    private readonly Dictionary<DebugEntityKey, int> genericLastSampleTick = new();

    private DebugEntityKey selectedKey, compareKey;
    private AbstractCreature selected, compare;
    private AIDebugSnapshot liveSnapshot, compareSnapshot;
    private bool hasSelection, hasCompare;
    private bool fullMode;
    private bool interactMode;
    private bool freezeView;
    private bool layoutLoaded;
    private bool rebuildLayout = true;
    private int timelineCursor = -1;
    private int nextEntityRefresh;
    private int lastAnomalyTick = int.MinValue;
    private float nextSnapshotRefresh, nextCompareRefresh;
    private string entityFilter = string.Empty;
    private string eventFilter = string.Empty;
    private string breakpointName = string.Empty;
    private int breakpointCategory = -1;
    private string lastExport = string.Empty;

    internal bool FullMode { get => fullMode; set => fullMode = value; }
    internal bool InteractMode => interactMode;
    internal void ToggleInteract() => interactMode = !interactMode;

    internal void Draw(RainWorldGame game, double overheadMs)
    {
        if (game == null)
        {
            DrawNoGame();
            return;
        }

        AIDebugRegistry.BindGame(game);
        AIDebugSimulationControl.Bind(game);
        RefreshEntities(game);

        if (AIDebugWorldOverlay.TryPick(game, entities, ImGui.GetIO().WantCaptureMouse,
                out AbstractCreature picked))
            Select(picked);

        ResolveSelection(game);
        ResolveCompare(game);
        AIDebugTrace.ReplaceWatches(selectedKey, hasSelection, pinned);
        SampleTrackedEntities();
        RefreshTraceLists();
        DetectAnomalies();
        DrawWorld(game);

        using (AIDebugProfiler.Begin(AIDebugProfileCategory.UI))
        {
            if (fullMode) DrawDocked(game, overheadMs);
            else DrawCompact(game, overheadMs);
        }
    }

    private void DrawNoGame()
    {
        ImGui.SetNextWindowBgAlpha(AIDebugSettings.Opacity);
        ImGui.SetNextWindowPos(new Num.Vector2(18f, 18f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(430f, 100f), ImGuiCond.Always);
        if (ImGui.Begin(AIDebugLocalization.T("app.title") + "###AINoGame",
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_game"));
        ImGui.End();
    }

    private void DrawCompact(RainWorldGame game, double overheadMs)
    {
        float scale = AIDebugSettings.UiScale;
        float width = 405f * scale;
        ImGui.SetNextWindowBgAlpha(AIDebugSettings.Opacity);
        ImGui.SetNextWindowPos(new Num.Vector2(Mathf.Max(8f, Screen.width - width - 16f), 16f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 320f * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(AIDebugLocalization.T("app.title") + "###AICompact", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        DrawToolbar(game, overheadMs, true);
        ImGui.Separator();
        AIDebugSnapshot active = ActiveSnapshot();
        if (active == null)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("common.no_selection"));
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("browser.help"));
            ImGui.End();
            return;
        }

        ImGui.Text(active.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled("[" + AIDebugLocalization.EntityState(active.EntityState) + "]");
        if (freezeView)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Num.Vector4(0.96f, 0.72f, 0.28f, 1f), AIDebugExtendedLocalization.T("status.frozen"));
        }
        if (AIDebugSimulationControl.Paused)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Num.Vector4(0.96f, 0.46f, 0.28f, 1f), AIDebugExtendedLocalization.T("status.paused"));
        }

        CompactRow(AIDebugLocalization.T("app.control_owner"), active.ControlOwner);
        CompactRaw(active, "DesertBatflyAI.Mode", AIDebugLocalization.T("field.mode"));
        CompactRaw(active, "DesertBatflySocialRoles.Expressed", AIDebugLocalization.T("field.expressed_role"));
        CompactRaw(active, "DesertBatflyAI.Target", AIDebugLocalization.T("field.target"));
        CompactRaw(active, "DesertBatflySocialRoles.Suppression", AIDebugLocalization.T("field.suppression"));

        if (events.Count > 0)
        {
            AIDebugTraceEvent e = events[events.Count - 1];
            ImGui.Separator();
            ImGui.TextDisabled(AIDebugExtendedLocalization.EventName(e.Name));
            if (!string.IsNullOrEmpty(e.Detail))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(e.Detail);
            }
        }
        ImGui.End();
    }

    private void DrawDocked(RainWorldGame game, double overheadMs)
    {
        if (!layoutLoaded)
        {
            try { layoutLoaded = AIDebugDockingNative.LoadLayout(); }
            catch { layoutLoaded = false; }
            rebuildLayout = !layoutLoaded;
        }

        ImGui.SetNextWindowPos(Num.Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(Screen.width, Screen.height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                                 ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                                 ImGuiWindowFlags.NoSavedSettings;
        ImGui.Begin("DryCycle AI Observatory DockHost###AIDockHost", flags);
        DrawToolbar(game, overheadMs, false);
        ImGui.Separator();
        Num.Vector2 size = ImGui.GetContentRegionAvail();
        uint dockId = ImGui.GetID("DryCycleAIDockSpace");
        AIDebugDockingNative.DockSpace(dockId, size);
        if (rebuildLayout)
        {
            try { AIDebugDockingNative.BuildDefault(dockId, size); }
            catch { }
            rebuildLayout = false;
        }
        ImGui.End();

        DrawEntityBrowser(game);
        DrawDecisionWindow();
        DrawInspectorWindow();
        DrawTimelineWindow();
        DrawEventsWindow();
        DrawUtilityWindow();
        DrawPerceptionWindow();
        DrawPathWindow();
        DrawCompareWindow();
        DrawCandidatesWindow();
        DrawCapturesWindow();
        DrawSettingsWindow();
    }

    private void DrawToolbar(RainWorldGame game, double overheadMs, bool compact)
    {
        if (ImGui.Button(fullMode ? AIDebugLocalization.T("app.compact") : AIDebugLocalization.T("app.full")))
            fullMode = !fullMode;
        ImGui.SameLine();
        if (ImGui.Button(interactMode ? AIDebugExtendedLocalization.T("toolbar.live") : AIDebugExtendedLocalization.T("toolbar.interact")))
            interactMode = !interactMode;
        ImGui.SameLine();
        if (ImGui.Button(AIDebugSimulationControl.Paused ? AIDebugExtendedLocalization.T("toolbar.resume") : AIDebugExtendedLocalization.T("toolbar.pause")))
            AIDebugSimulationControl.Toggle(game);
        ImGui.SameLine();
        if (ImGui.Button(AIDebugExtendedLocalization.T("toolbar.step"))) AIDebugSimulationControl.Step(game);
        ImGui.SameLine();
        if (ImGui.Button(freezeView ? AIDebugExtendedLocalization.T("toolbar.unfreeze") : AIDebugExtendedLocalization.T("toolbar.freeze")))
            ToggleFreeze();
        ImGui.SameLine();
        if (ImGui.Button(AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "English" : "中文"))
        {
            AIDebugLocalization.Language = AIDebugLocalization.Language == AIDebugLanguage.Chinese
                ? AIDebugLanguage.English : AIDebugLanguage.Chinese;
            AIDebugSettings.Save();
            liveSnapshot = compareSnapshot = null;
            nextSnapshotRefresh = nextCompareRefresh = 0f;
        }
        if (!compact)
        {
            ImGui.SameLine();
            if (ImGui.Button(AIDebugExtendedLocalization.T("toolbar.save_layout"))) TrySaveLayout();
            ImGui.SameLine();
            if (ImGui.Button(AIDebugExtendedLocalization.T("toolbar.reset_layout")))
            {
                try { AIDebugDockingNative.DeleteLayout(); } catch { }
                rebuildLayout = true;
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{overheadMs:0.00} ms · tick {game.clock} · F7/F6 · Alt+LMB · Tab");
    }

    private void DrawEntityBrowser(RainWorldGame game)
    {
        if (!BeginPanel("window.browser", "AIEntityBrowser")) { ImGui.End(); return; }
        ImGui.TextDisabled(AIDebugExtendedLocalization.T("browser.help"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(AIDebugExtendedLocalization.T("browser.search") + "##EntityFilter", ref entityFilter, 96);
        ImGui.Separator();

        if (pinned.Count > 0)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.pinned"));
            foreach (DebugEntityKey key in pinned)
            {
                AbstractCreature creature = FindEntity(key);
                if (creature != null && MatchesEntityFilter(creature)) DrawEntityRow(creature, true);
            }
            ImGui.Separator();
        }

        visibleRooms.Clear();
        if (game.cameras != null)
            for (int i = 0; i < game.cameras.Length; i++)
                if (game.cameras[i]?.room?.abstractRoom != null)
                    visibleRooms.Add(game.cameras[i].room.abstractRoom.index);

        ImGui.TextDisabled(L("Visible Camera Rooms", "当前可见相机房间"));
        for (int i = 0; i < entities.Count; i++)
            if (visibleRooms.Contains(entities[i].pos.room) && MatchesEntityFilter(entities[i]))
                DrawEntityRow(entities[i], false);

        ImGui.Separator();
        ImGui.TextDisabled("World");
        for (int i = 0; i < entities.Count; i++)
            if (!visibleRooms.Contains(entities[i].pos.room) && MatchesEntityFilter(entities[i]))
                DrawEntityRow(entities[i], false);
        ImGui.End();
    }

    private void DrawEntityRow(AbstractCreature creature, bool pinnedSection)
    {
        DebugEntityKey key = DebugEntityKey.From(creature);
        bool isSelected = hasSelection && key == selectedKey;
        bool isCompare = hasCompare && key == compareKey;
        string type = creature.creatureTemplate?.type?.value ?? "Creature";
        string state = AIDebugLocalization.EntityState(AIDebugRegistry.EntityState(creature));
        string id = AIDebugSettings.ShowIds ? " #" + creature.ID.number : string.Empty;
        string prefix = isCompare ? "[B] " : isSelected ? "[A] " : string.Empty;
        string label = $"{prefix}{type}{id} [{state}]##{key.Spawner}:{key.Number}:{pinnedSection}";
        if (ImGui.Selectable(label, isSelected))
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) SetCompare(creature);
            else Select(creature);
        }
        if (ImGui.IsItemHovered() && AIDebugSettings.ShowRawNames) ImGui.SetTooltip(key.ToString());
    }

    private void DrawDecisionWindow()
    {
        if (!BeginPanel("window.decision", "AIDecision")) { ImGui.End(); return; }
        AIDebugSnapshot active = ActiveSnapshot();
        if (active == null) { NoSelection(); ImGui.End(); return; }

        ImGui.TextDisabled(AIDebugLocalization.T("app.control_owner"));
        ImGui.SameLine(); ImGui.Text(active.ControlOwner);
        ImGui.Separator();
        for (int i = 0; i < active.Decisions.Count; i++)
        {
            AIDebugDecisionNode node = active.Decisions[i];
            if (node.Depth > 0) ImGui.Indent(node.Depth * 14f);
            ImGui.TextColored(StateColor(node.State), AIDebugLocalization.DecisionState(node.State));
            ImGui.SameLine(); ImGui.Text(AIDebugLocalization.T(node.LabelKey));
            if (!string.IsNullOrEmpty(node.Detail)) { ImGui.SameLine(); ImGui.TextDisabled(node.Detail); }
            if (AIDebugSettings.ShowRawNames && !string.IsNullOrEmpty(node.RawName) && ImGui.IsItemHovered())
                ImGui.SetTooltip(node.RawName);
            if (node.Depth > 0) ImGui.Unindent(node.Depth * 14f);
        }

        if (events.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled(L("Why / Why Not", "为什么 / 为什么没有"));
            int start = Mathf.Max(0, events.Count - 10);
            for (int i = start; i < events.Count; i++)
            {
                AIDebugTraceEvent e = events[i];
                ImGui.BulletText(AIDebugExtendedLocalization.EventName(e.Name) +
                    (string.IsNullOrEmpty(e.Detail) ? string.Empty : ": " + e.Detail));
                if (!string.IsNullOrEmpty(e.Reason))
                {
                    ImGui.Indent(16f); ImGui.TextDisabled(e.Reason); ImGui.Unindent(16f);
                }
            }
        }
        ImGui.End();
    }

    private void DrawInspectorWindow()
    {
        if (!BeginPanel("window.inspector", "AIInspector")) { ImGui.End(); return; }
        AIDebugSnapshot active = ActiveSnapshot();
        if (active == null) { NoSelection(); ImGui.End(); return; }

        bool isPinned = pinned.Contains(active.Key);
        if (ImGui.Button(AIDebugLocalization.T(isPinned ? "app.unpin" : "app.pin")))
        {
            if (isPinned) pinned.Remove(active.Key); else pinned.Add(active.Key);
            AIDebugTrace.ReplaceWatches(selectedKey, hasSelection, pinned);
        }
        ImGui.SameLine(); ImGui.Text(active.DisplayName);
        ImGui.Separator();

        for (int s = 0; s < active.Sections.Count; s++)
        {
            AIDebugSection section = active.Sections[s];
            if (!ImGui.CollapsingHeader(AIDebugLocalization.T(section.TitleKey), ImGuiTreeNodeFlags.DefaultOpen)) continue;
            for (int i = 0; i < section.Values.Count; i++) DrawValue(section.Values[i]);
            DrawMiniGraphs(section);
        }
        ImGui.End();
    }

    private void DrawTimelineWindow()
    {
        if (!BeginPanel("window.timeline", "AITimeline")) { ImGui.End(); return; }
        using (AIDebugProfiler.Begin(AIDebugProfileCategory.Timeline))
        {
            if (!hasSelection || frames.Count == 0)
            {
                ImGui.TextDisabled(AIDebugExtendedLocalization.T("timeline.no_data"));
                ImGui.End(); return;
            }

            if (ImGui.Button(AIDebugExtendedLocalization.T("timeline.live")))
            {
                freezeView = false;
                timelineCursor = frames.Count - 1;
            }
            ImGui.SameLine();
            if (ImGui.Button(AIDebugExtendedLocalization.T("common.clear")))
            {
                AIDebugTrace.Clear(selectedKey);
                frames.Clear(); events.Clear(); timelineCursor = -1;
                ImGui.End(); return;
            }
            ImGui.SameLine();
            float duration = frames.Count > 1 ? frames[frames.Count - 1].Time - frames[0].Time : 0f;
            ImGui.TextDisabled($"{frames.Count} · {duration:0.0}s · 40 Hz tick / 10 Hz sample");

            DrawTimelineStrip();
            if (frames.Count > 0)
            {
                if (timelineCursor < 0 || timelineCursor >= frames.Count) timelineCursor = frames.Count - 1;
                ImGui.SetNextItemWidth(Mathf.Min(680f, ImGui.GetContentRegionAvail().X));
                if (ImGui.SliderInt(L("Sample##Timeline", "样本##Timeline"), ref timelineCursor, 0, frames.Count - 1))
                    freezeView = true;
                AIDebugTraceFrame f = frames[Mathf.Clamp(timelineCursor, 0, frames.Count - 1)];
                if (ImGui.BeginTable("##TimelineDetails", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
                {
                    Row(L("Tick / Time", "Tick / 时间"), $"{f.Frame} / {f.Time:0.000}s");
                    Row(AIDebugLocalization.T("field.room"), f.Room);
                    Row(AIDebugLocalization.T("app.control_owner"), f.ControlOwner);
                    Row(AIDebugLocalization.T("field.mode"), f.Mode);
                    Row(AIDebugLocalization.T("field.target"), f.Target);
                    Row(AIDebugLocalization.T("field.expressed_role"), f.Role);
                    Row(AIDebugLocalization.T("field.suppression"), f.Suppression);
                    Row(AIDebugLocalization.T("field.position"), AIDebugFormat.Value(f.Position));
                    Row(AIDebugLocalization.T("field.velocity"), AIDebugFormat.Value(f.Velocity));
                    Row(AIDebugLocalization.T("field.local_goal"), AIDebugFormat.Value(f.LocalGoal));
                    ImGui.EndTable();
                }
            }
        }
        ImGui.End();
    }

    private void DrawEventsWindow()
    {
        if (!BeginPanel("window.events", "AIEvents")) { ImGui.End(); return; }
        if (!hasSelection || events.Count == 0)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("events.no_data"));
            ImGui.End(); return;
        }
        ImGui.SetNextItemWidth(Mathf.Min(420f, ImGui.GetContentRegionAvail().X));
        ImGui.InputText(AIDebugExtendedLocalization.T("events.filter") + "##EventFilter", ref eventFilter, 128);
        ImGui.Separator();
        for (int i = 0; i < events.Count; i++)
        {
            AIDebugTraceEvent e = events[i];
            if (!EventMatches(e)) continue;
            ImGui.TextColored(EventColor(e.Category), $"[{e.Frame}] {CategoryText(e.Category)}");
            ImGui.SameLine(); ImGui.Text(AIDebugExtendedLocalization.EventName(e.Name));
            if (!string.IsNullOrEmpty(e.Detail)) { ImGui.SameLine(); ImGui.TextDisabled(e.Detail); }
            if (!string.IsNullOrEmpty(e.Reason))
            {
                ImGui.Indent(18f); ImGui.TextDisabled("↳ " + e.Reason); ImGui.Unindent(18f);
            }
        }
        ImGui.End();
    }

    private void DrawUtilityWindow()
    {
        if (!BeginPanel("window.utility", "AIUtility")) { ImGui.End(); return; }
        AIDebugUtilityRow[] historical = ActiveHistory()?.Utilities;
        IReadOnlyList<AIDebugUtilityRow> source;
        if (freezeView && historical != null) source = historical;
        else
        {
            utilities.Clear();
            if (selected != null)
                using (AIDebugProfiler.Begin(AIDebugProfileCategory.Utility))
                    AIDebugAdvancedCapture.CaptureUtilities(selected, utilities);
            source = utilities;
        }

        if (source.Count == 0)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("utility.no_data"));
            ImGui.End(); return;
        }

        ImGui.TextDisabled(L("Unavailable cached values are shown as —; the debugger never calls AIModule.Utility().",
            "未被原 AI 缓存的数值显示为 —；调试器绝不会调用 AIModule.Utility()。"));
        if (ImGui.BeginTable("##UtilityTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn(L("Module", "模块")); ImGui.TableSetupColumn("Raw"); ImGui.TableSetupColumn("Smoothed");
            ImGui.TableSetupColumn("Weight"); ImGui.TableSetupColumn("Weighted"); ImGui.TableSetupColumn("Continuation"); ImGui.TableSetupColumn(L("Winner", "胜出"));
            ImGui.TableHeadersRow();
            for (int i = 0; i < source.Count; i++)
            {
                AIDebugUtilityRow u = source[i];
                ImGui.TableNextRow();
                Cell(0, u.Name);
                Cell(1, u.HasRaw ? u.Raw.ToString("0.000") : "—");
                Cell(2, u.HasSmoothed ? u.Smoothed.ToString("0.000") : "—");
                Cell(3, u.Weight.ToString("0.000"));
                Cell(4, u.HasWeighted ? u.Weighted.ToString("0.000") : "—");
                Cell(5, u.ContinuationBonus.ToString("0.000"));
                Cell(6, u.Winner ? L("YES", "是") : "—");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawPerceptionWindow()
    {
        if (!BeginPanel("window.perception", "AIPerception")) { ImGui.End(); return; }
        AIDebugPerceptionRow[] historical = ActiveHistory()?.Perception;
        IReadOnlyList<AIDebugPerceptionRow> source;
        if (freezeView && historical != null) source = historical;
        else
        {
            perception.Clear();
            if (selected != null)
                using (AIDebugProfiler.Begin(AIDebugProfileCategory.Perception))
                    AIDebugAdvancedCapture.CapturePerception(selected, perception);
            source = perception;
        }

        if (source.Count == 0)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("perception.no_data"));
            ImGui.End(); return;
        }
        if (ImGui.BeginTable("##PerceptionTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            string[] heads = { L("Creature", "生物"), L("Visible", "可见"), L("Since seen", "距目击"),
                L("Find chance", "找到概率"), L("Priority", "优先级"), L("Last seen", "最后目击"),
                L("Best guess", "最佳估计"), L("Relationship", "关系") };
            for (int i = 0; i < heads.Length; i++) ImGui.TableSetupColumn(heads[i]);
            ImGui.TableHeadersRow();
            for (int i = 0; i < source.Count; i++)
            {
                AIDebugPerceptionRow p = source[i];
                ImGui.TableNextRow();
                Cell(0, p.Name); Cell(1, p.VisualContact ? L("YES", "是") : L("no", "否"));
                Cell(2, p.TicksSinceSeen.ToString()); Cell(3, p.EstimatedChance.ToString("0.000"));
                Cell(4, p.Priority.ToString("0.000")); Cell(5, p.LastSeen.ToString()); Cell(6, p.BestGuess.ToString());
                Cell(7, $"{p.Relationship} {p.RelationshipIntensity:0.00}");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawPathWindow()
    {
        if (!BeginPanel("window.path", "AIPath")) { ImGui.End(); return; }
        AIDebugHistoricalState history = ActiveHistory();
        AIDebugPathState path = freezeView && history != null ? history.Path : AIDebugAdvancedCapture.CapturePath(selected);
        if (ImGui.BeginTable("##PathTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(L("Layer", "层级")); ImGui.TableSetupColumn(L("Value", "值")); ImGui.TableSetupColumn(L("Meaning", "含义"));
            ImGui.TableHeadersRow();
            PathRow(AIDebugExtendedLocalization.T("path.intent"), path.Destination.ToString(), "AbstractCreatureAI.destination");
            PathRow(AIDebugExtendedLocalization.T("path.planner"), path.HasPathfinder ? path.Pathfinder : "—",
                $"reachable={path.DestinationReachable}, returnable={path.CanReturnFromDestination}, stranded={path.Stranded}");
            AIDebugTraceFrame f = ActiveFrameOrDefault();
            PathRow(AIDebugExtendedLocalization.T("path.motor"), AIDebugFormat.Value(f.LocalGoal),
                $"mode={f.Mode}, velocity={AIDebugFormat.Value(f.Velocity)}");
            ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.TextDisabled("Intent → Planner → Motor");
        ImGui.End();
    }

    private void DrawCompareWindow()
    {
        if (!BeginPanel("window.compare", "AICompare")) { ImGui.End(); return; }
        if (!hasCompare || compare == null || compareSnapshot == null)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("compare.help"));
            ImGui.End(); return;
        }
        AIDebugSnapshot a = ActiveSnapshot();
        if (a == null) { NoSelection(); ImGui.End(); return; }
        Flatten(a, compareA); Flatten(compareSnapshot, compareB);
        ImGui.Text($"A: {a.DisplayName}   ↔   B: {compareSnapshot.DisplayName}");
        if (ImGui.BeginTable("##CompareTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn(L("Field", "字段")); ImGui.TableSetupColumn("A"); ImGui.TableSetupColumn("B");
            ImGui.TableHeadersRow();
            foreach (KeyValuePair<string, string> pair in compareA)
            {
                compareB.TryGetValue(pair.Key, out string right);
                if (!AIDebugSettings.ShowRawNames && right == pair.Value) continue;
                ImGui.TableNextRow(); Cell(0, pair.Key); Cell(1, pair.Value); Cell(2, right ?? "—");
            }
            foreach (KeyValuePair<string, string> pair in compareB)
            {
                if (compareA.ContainsKey(pair.Key)) continue;
                ImGui.TableNextRow(); Cell(0, pair.Key); Cell(1, "—"); Cell(2, pair.Value);
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawCandidatesWindow()
    {
        if (!BeginPanel("window.candidates", "AICandidates")) { ImGui.End(); return; }
        candidates.Clear();
        if (!hasSelection || AIDebugCandidateRegistry.Copy(selectedKey, candidates) == 0)
        {
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("candidates.no_data"));
            ImGui.End(); return;
        }
        if (ImGui.BeginTable("##CandidatesTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            string[] headers = { L("Set", "候选集"), L("Candidate", "候选项"), L("Valid", "有效"),
                L("Score", "评分"), L("Winner", "胜出"), L("Reason", "原因") };
            for (int i = 0; i < headers.Length; i++) ImGui.TableSetupColumn(headers[i]);
            ImGui.TableHeadersRow();
            for (int i = 0; i < candidates.Count; i++)
            {
                AIDebugCandidate c = candidates[i];
                ImGui.TableNextRow(); Cell(0, c.Set); Cell(1, c.Name); Cell(2, c.Valid ? L("YES", "是") : L("NO", "否"));
                Cell(3, c.Score.ToString("0.000")); Cell(4, c.Winner ? L("WINNER", "胜出") : "—"); Cell(5, c.Reason);
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    private void DrawCapturesWindow()
    {
        if (!BeginPanel("window.captures", "AICaptures")) { ImGui.End(); return; }
        if (hasSelection && ImGui.Button(AIDebugExtendedLocalization.T("capture.trigger")))
            AIDebugCaptureManager.Trigger(selectedKey, "ManualCapture");
        ImGui.SameLine();
        if (ImGui.Button(AIDebugExtendedLocalization.T("common.clear"))) AIDebugCaptureManager.Clear();
        ImGui.SameLine();
        ImGui.TextDisabled(L("Pending", "等待完成") + ": " + AIDebugCaptureManager.PendingCount);

        if (!string.IsNullOrEmpty(lastExport))
            ImGui.TextDisabled(AIDebugExtendedLocalization.T("capture.last_export") + ": " + lastExport);
        ImGui.Separator();
        ImGui.TextDisabled(AIDebugExtendedLocalization.T("capture.completed"));
        IReadOnlyList<AIDebugCapture> captures = AIDebugCaptureManager.Captures;
        for (int i = captures.Count - 1; i >= 0; i--)
        {
            AIDebugCapture capture = captures[i];
            ImGui.PushID(i);
            ImGui.Text($"{capture.Key} · {AIDebugExtendedLocalization.EventName(capture.Reason)} · {capture.Frames.Count} samples");
            ImGui.SameLine();
            if (ImGui.SmallButton(AIDebugExtendedLocalization.T("common.export")))
            {
                try { lastExport = AIDebugCaptureManager.Export(capture) ?? string.Empty; }
                catch (Exception error) { lastExport = error.Message; }
            }
            ImGui.PopID();
        }

        ImGui.Separator();
        ImGui.TextDisabled(L("Conditional Breakpoints", "条件断点"));
        ImGui.InputText(AIDebugExtendedLocalization.T("breakpoint.name") + "##BPName", ref breakpointName, 96);
        string preview = breakpointCategory < 0 ? L("Any category", "任意类别") : CategoryText((AIDebugEventCategory)breakpointCategory);
        if (ImGui.BeginCombo(L("Category##BPCategory", "类别##BPCategory"), preview))
        {
            if (ImGui.Selectable(L("Any category", "任意类别"), breakpointCategory < 0)) breakpointCategory = -1;
            foreach (AIDebugEventCategory category in Enum.GetValues(typeof(AIDebugEventCategory)))
                if (ImGui.Selectable(CategoryText(category), breakpointCategory == (int)category)) breakpointCategory = (int)category;
            ImGui.EndCombo();
        }
        if (ImGui.Button(AIDebugExtendedLocalization.T("breakpoint.add")))
            AIDebugBreakpointManager.Add(breakpointName,
                breakpointCategory < 0 ? null : (AIDebugEventCategory?)breakpointCategory,
                hasSelection ? selectedKey : null);

        IReadOnlyList<AIDebugBreakpoint> rules = AIDebugBreakpointManager.Breakpoints;
        for (int i = 0; i < rules.Count; i++)
        {
            AIDebugBreakpoint rule = rules[i];
            ImGui.PushID(1000 + i);
            bool enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled)) rule.Enabled = enabled;
            ImGui.SameLine();
            ImGui.Text($"{(rule.Category.HasValue ? CategoryText(rule.Category.Value) : L("Any", "任意"))} · {rule.NameContains} · {(rule.Entity.HasValue ? rule.Entity.Value.ToString() : L("Any entity", "任意实体"))}");
            ImGui.SameLine();
            if (ImGui.SmallButton("×"))
            {
                AIDebugBreakpointManager.RemoveAt(i);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        if (!string.IsNullOrEmpty(AIDebugBreakpointManager.LastHit))
            ImGui.TextColored(new Num.Vector4(0.95f, 0.65f, 0.25f, 1f),
                AIDebugExtendedLocalization.T("breakpoint.last_hit") + ": " + AIDebugBreakpointManager.LastHit);
        ImGui.End();
    }

    private void DrawSettingsWindow()
    {
        if (!BeginPanel("window.settings", "AISettings")) { ImGui.End(); return; }
        bool changed = false;
        ImGui.TextDisabled(AIDebugExtendedLocalization.T("settings.language"));
        ImGui.SameLine();
        if (ImGui.RadioButton("中文##LangCN", AIDebugLocalization.Language == AIDebugLanguage.Chinese))
        { AIDebugLocalization.Language = AIDebugLanguage.Chinese; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("English##LangEN", AIDebugLocalization.Language == AIDebugLanguage.English))
        { AIDebugLocalization.Language = AIDebugLanguage.English; changed = true; }

        float ui = AIDebugSettings.UiScale;
        if (ImGui.SliderFloat(AIDebugExtendedLocalization.T("settings.ui_scale"), ref ui, 0.75f, 1.75f, "%.2f")) { AIDebugSettings.UiScale = ui; changed = true; }
        float font = AIDebugSettings.FontScale;
        if (ImGui.SliderFloat(AIDebugExtendedLocalization.T("settings.font_scale"), ref font, 0.75f, 1.75f, "%.2f")) { AIDebugSettings.FontScale = font; changed = true; }
        float opacity = AIDebugSettings.Opacity;
        if (ImGui.SliderFloat(AIDebugExtendedLocalization.T("settings.opacity"), ref opacity, 0.55f, 1f, "%.2f")) { AIDebugSettings.Opacity = opacity; changed = true; }
        int history = AIDebugSettings.HistorySeconds;
        if (ImGui.SliderInt(AIDebugExtendedLocalization.T("settings.history"), ref history, 5, 60)) { AIDebugSettings.HistorySeconds = history; changed = true; }

        changed |= Check(AIDebugExtendedLocalization.T("settings.auto_open"), ref AIDebugSettings.AutoOpen);
        changed |= Check(AIDebugExtendedLocalization.T("settings.raw"), ref AIDebugSettings.ShowRawNames);
        changed |= Check(AIDebugExtendedLocalization.T("settings.age"), ref AIDebugSettings.ShowDataAge);
        changed |= Check(AIDebugExtendedLocalization.T("settings.ids"), ref AIDebugSettings.ShowIds);
        ImGui.Separator();
        changed |= Check(AIDebugExtendedLocalization.T("settings.overlay"), ref AIDebugSettings.Overlay);
        changed |= Check(AIDebugExtendedLocalization.T("settings.physics"), ref AIDebugSettings.OverlayPhysics);
        changed |= Check(AIDebugExtendedLocalization.T("settings.movement"), ref AIDebugSettings.OverlayMovement);
        changed |= Check(AIDebugExtendedLocalization.T("settings.path"), ref AIDebugSettings.OverlayPath);
        changed |= Check(AIDebugExtendedLocalization.T("settings.aimap"), ref AIDebugSettings.OverlayAImap);
        changed |= Check(AIDebugExtendedLocalization.T("settings.perception"), ref AIDebugSettings.OverlayPerception);
        changed |= Check(AIDebugExtendedLocalization.T("settings.social"), ref AIDebugSettings.OverlaySocial);
        changed |= Check(AIDebugExtendedLocalization.T("settings.combat"), ref AIDebugSettings.OverlayCombat);
        changed |= Check(AIDebugExtendedLocalization.T("settings.labels"), ref AIDebugSettings.OverlayLabels);
        ImGui.Separator();
        changed |= Check(AIDebugExtendedLocalization.T("settings.full_history"), ref AIDebugSettings.RecordFullHistory);
        changed |= Check(AIDebugExtendedLocalization.T("settings.trigger_capture"), ref AIDebugSettings.TriggerCapture);
        changed |= Check(AIDebugExtendedLocalization.T("settings.anomaly"), ref AIDebugSettings.DetectAnomalies);
        changed |= Check(AIDebugExtendedLocalization.T("settings.break_pause"), ref AIDebugSettings.BreakpointPausesWorld);
        if (changed) AIDebugSettings.Save();

        if (ImGui.Button(AIDebugExtendedLocalization.T("settings.save"))) AIDebugSettings.Save();
        ImGui.SameLine();
        if (ImGui.Button(AIDebugExtendedLocalization.T("settings.reset"))) AIDebugSettings.ResetDefaults();
        ImGui.Separator();
        ImGui.TextDisabled("Profiler");
        ProfileLine(AIDebugProfileCategory.Capture, "profile.capture");
        ProfileLine(AIDebugProfileCategory.UI, "profile.ui");
        ProfileLine(AIDebugProfileCategory.Overlay, "profile.overlay");
        ProfileLine(AIDebugProfileCategory.Timeline, "profile.timeline");
        ProfileLine(AIDebugProfileCategory.Utility, "profile.utility");
        ProfileLine(AIDebugProfileCategory.Perception, "profile.perception");
        ProfileLine(AIDebugProfileCategory.AImap, "profile.aimap");
        ImGui.Separator();
        ImGui.Text($"{AIDebugExtendedLocalization.T("status.input_gate")}: {(AIDebugInputGate.Installed ? "OK" : L("Unavailable", "不可用"))}");
        ImGui.Text($"{AIDebugExtendedLocalization.T("status.world_step")}: {(AIDebugSimulationControl.Paused ? L("Paused", "已暂停") : L("Ready", "就绪"))}");
        ImGui.End();
    }

    private void DrawWorld(RainWorldGame game)
    {
        if (!AIDebugSettings.Overlay || selected == null) return;
        bool frozen = freezeView && TryActiveFrame(out AIDebugTraceFrame frozenFrame);
        if (!frozen)
            AIDebugWorldOverlay.Draw(game, selected, pinned, FindEntity,
                AIDebugSettings.OverlayPath, AIDebugSettings.OverlayPerception, AIDebugSettings.OverlayLabels);
        AIDebugAdvancedOverlay.Draw(game, selected, frozen, frozenFrame);
    }

    private void SampleTrackedEntities()
    {
        if (!AIDebugTrace.Visible) return;
        if (selected != null) SampleOne(selected);
        foreach (DebugEntityKey key in pinned)
        {
            AbstractCreature creature = FindEntity(key);
            if (creature != null && !ReferenceEquals(creature, selected)) SampleOne(creature);
        }
    }

    private void SampleOne(AbstractCreature creature)
    {
        if (creature?.realizedCreature is DesertBatfly bat)
        {
            DesertBatflyDebugTrace.Sample(bat);
            return;
        }

        DebugEntityKey key = DebugEntityKey.From(creature);
        int tick = AIDebugTrace.SimulationTick;
        int interval = AIDebugSimulationControl.Paused ? 1 : 4;
        if (genericLastSampleTick.TryGetValue(key, out int last) && tick - last < interval) return;
        if (genericLastSampleTick.TryGetValue(key, out last) && tick <= last) return;
        genericLastSampleTick[key] = tick;

        Creature realized = creature.realizedCreature;
        ArtificialIntelligence ai = creature.abstractAI?.RealAI;
        string controller = ai?.GetType().Name ?? creature.abstractAI?.GetType().Name ?? "AbstractCreature";
        UtilityComparer.UtilityTracker highestTracker = ai?.utilityComparer?.highestUtilityTracker;
        string utility = highestTracker?.module?.GetType().Name ?? "—";
        float highestCached = highestTracker?.smoother != null ? highestTracker.smoothedUtility : float.NaN;

        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Decision, "ControlOwner", controller, "ArtificialIntelligence type");
        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Path, "Destination", creature.abstractAI?.destination, "AbstractCreatureAI.destination");
        AIDebugTrace.RecordChange(creature, AIDebugEventCategory.Decision, "HighestUtility", utility, "UtilityComparer winner");

        Vector2 pos = realized?.mainBodyChunk?.pos ?? Vector2.zero;
        Vector2 vel = realized?.mainBodyChunk?.vel ?? Vector2.zero;
        AIDebugTrace.Sample(creature, new AIDebugTraceFrame(creature.Room?.name, pos, vel, pos,
            controller, utility, "—", "—", controller, highestCached, 0f, 0f, 0f));
    }

    private void DetectAnomalies()
    {
        if (!AIDebugSettings.DetectAnomalies || selected == null || frames.Count == 0) return;
        int tick = frames[frames.Count - 1].Frame;
        if (tick == lastAnomalyTick) return;
        lastAnomalyTick = tick;
        AIDebugAnomalyDetector.Evaluate(selected, frames);
    }

    private void RefreshTraceLists()
    {
        if (!hasSelection) { frames.Clear(); events.Clear(); return; }
        AIDebugTrace.CopyFrames(selectedKey, frames);
        AIDebugTrace.CopyEvents(selectedKey, events);
        if (!freezeView) timelineCursor = frames.Count - 1;
        else if (timelineCursor >= frames.Count) timelineCursor = frames.Count - 1;
    }

    private void RefreshEntities(RainWorldGame game)
    {
        if (Time.frameCount < nextEntityRefresh) return;
        nextEntityRefresh = Time.frameCount + EntityRefreshFrames;
        AIDebugRegistry.CollectWorld(game, entities);
        entities.Sort(CompareEntities);
        if (hasSelection || entities.Count == 0) return;

        AbstractCreature candidate = null;
        visibleRooms.Clear();
        if (game.cameras != null)
            for (int i = 0; i < game.cameras.Length; i++)
                if (game.cameras[i]?.room?.abstractRoom != null)
                    visibleRooms.Add(game.cameras[i].room.abstractRoom.index);

        for (int i = 0; i < entities.Count; i++)
        {
            if (!visibleRooms.Contains(entities[i].pos.room)) continue;
            if (entities[i].realizedCreature is DesertBatfly) { candidate = entities[i]; break; }
            if (candidate == null && entities[i].realizedCreature != null) candidate = entities[i];
        }
        Select(candidate ?? entities[0]);
    }

    private void ResolveSelection(RainWorldGame game)
    {
        if (!hasSelection) { selected = null; liveSnapshot = null; return; }
        if (selected == null || selected.slatedForDeletion || DebugEntityKey.From(selected) != selectedKey)
            selected = FindEntity(selectedKey) ?? AIDebugRegistry.Resolve(game, selectedKey);
        if (selected == null) { liveSnapshot = null; return; }
        if (liveSnapshot != null && Time.unscaledTime < nextSnapshotRefresh) return;
        liveSnapshot = AIDebugRegistry.Capture(selected, game);
        nextSnapshotRefresh = Time.unscaledTime + SnapshotInterval;
    }

    private void ResolveCompare(RainWorldGame game)
    {
        if (!hasCompare) return;
        if (compare == null || compare.slatedForDeletion || DebugEntityKey.From(compare) != compareKey)
            compare = FindEntity(compareKey) ?? AIDebugRegistry.Resolve(game, compareKey);
        if (compare == null) { compareSnapshot = null; return; }
        if (compareSnapshot != null && Time.unscaledTime < nextCompareRefresh) return;
        compareSnapshot = AIDebugRegistry.Capture(compare, game);
        nextCompareRefresh = Time.unscaledTime + SnapshotInterval;
    }

    private void Select(AbstractCreature creature)
    {
        if (creature == null) return;
        selected = creature;
        selectedKey = DebugEntityKey.From(creature);
        hasSelection = true;
        freezeView = false;
        timelineCursor = -1;
        liveSnapshot = null;
        nextSnapshotRefresh = 0f;
        frames.Clear(); events.Clear();
        AIDebugTrace.ReplaceWatches(selectedKey, true, pinned);
        AIDebugTrace.Record(selectedKey, AIDebugEventCategory.State, "Selected", selectedKey.ToString(), "world/browser selection");
    }

    private void SetCompare(AbstractCreature creature)
    {
        if (creature == null) return;
        compare = creature;
        compareKey = DebugEntityKey.From(creature);
        hasCompare = true;
        compareSnapshot = null;
        nextCompareRefresh = 0f;
    }

    private void ToggleFreeze()
    {
        if (!freezeView)
        {
            if (frames.Count == 0) return;
            freezeView = true;
            timelineCursor = frames.Count - 1;
        }
        else freezeView = false;
    }

    private AIDebugSnapshot ActiveSnapshot()
    {
        if (freezeView && TryActiveFrame(out AIDebugTraceFrame frame) && frame.History?.Snapshot != null)
            return frame.History.Snapshot;
        return liveSnapshot;
    }

    private AIDebugHistoricalState ActiveHistory() =>
        freezeView && TryActiveFrame(out AIDebugTraceFrame frame) ? frame.History : null;

    private AIDebugTraceFrame ActiveFrameOrDefault()
    {
        if (TryActiveFrame(out AIDebugTraceFrame frame)) return frame;
        return default;
    }

    private bool TryActiveFrame(out AIDebugTraceFrame frame)
    {
        frame = default;
        if (frames.Count == 0) return false;
        int index = freezeView ? Mathf.Clamp(timelineCursor, 0, frames.Count - 1) : frames.Count - 1;
        frame = frames[index];
        return true;
    }

    private void DrawTimelineStrip()
    {
        if (frames.Count == 0) return;
        float width = Mathf.Max(10f, ImGui.GetContentRegionAvail().X);
        float height = 70f;
        Num.Vector2 start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##TimelineStrip", new Num.Vector2(width, height));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Num.Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.08f, 0.09f, 0.11f, 1f)));
        float step = width / frames.Count;
        for (int i = 0; i < frames.Count; i++)
            draw.AddRectFilled(new Num.Vector2(start.X + i * step, start.Y + 6f),
                new Num.Vector2(start.X + (i + 1) * step + 0.5f, start.Y + height - 6f), TimelineColor(frames[i]));
        if (timelineCursor >= 0 && timelineCursor < frames.Count)
        {
            float x = start.X + (timelineCursor + 0.5f) * step;
            draw.AddLine(new Num.Vector2(x, start.Y), new Num.Vector2(x, start.Y + height), 0xffffffff, 2f);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            float local = ImGui.GetIO().MousePos.X - start.X;
            timelineCursor = Mathf.Clamp(Mathf.FloorToInt(local / Mathf.Max(0.001f, step)), 0, frames.Count - 1);
            freezeView = true;
        }
    }

    private void DrawValue(AIDebugValue value)
    {
        ImGui.TextDisabled(AIDebugLocalization.T(value.LabelKey));
        if (AIDebugSettings.ShowRawNames && !string.IsNullOrEmpty(value.RawName))
        {
            ImGui.SameLine(); ImGui.TextDisabled(" · " + value.RawName);
        }
        ImGui.SameLine(Mathf.Max(190f, ImGui.GetWindowWidth() * 0.47f));
        ImGui.Text(value.Value);
        if (AIDebugSettings.ShowDataAge && (value.AgeTicks > 0 || !string.IsNullOrEmpty(value.Source)))
        {
            ImGui.SameLine();
            string age = value.AgeTicks > 0 ? value.AgeTicks + " " + AIDebugLocalization.T("app.ticks") : "live";
            ImGui.TextDisabled("[" + age + (string.IsNullOrEmpty(value.Source) ? string.Empty : " · " + value.Source) + "]");
        }
    }

    private void DrawMiniGraphs(AIDebugSection section)
    {
        if (!hasSelection || frames.Count < 3) return;
        string title = section.TitleKey;
        if (title != "section.social_role" && title != "section.flock" && title != "section.movement") return;
        if (title == "section.social_role")
        {
            MiniGraph("Sentinel", frame => frame.Utility0);
            MiniGraph("Bully", frame => frame.Utility1);
            MiniGraph("Opportunist", frame => frame.Utility2);
        }
        else if (title == "section.flock") MiniGraph("Panic", frame => frame.Panic);
        else MiniGraph("Speed", frame => frame.Velocity.magnitude);
    }

    private void MiniGraph(string label, Func<AIDebugTraceFrame, float> value)
    {
        int count = Mathf.Min(120, frames.Count);
        int startIndex = frames.Count - count;
        float min = float.MaxValue, max = float.MinValue;
        bool any = false;
        for (int i = startIndex; i < frames.Count; i++)
        {
            float current = value(frames[i]);
            if (float.IsNaN(current) || float.IsInfinity(current)) continue;
            min = Mathf.Min(min, current); max = Mathf.Max(max, current); any = true;
        }
        if (!any) return;
        if (max - min < 0.001f) max = min + 1f;
        Num.Vector2 start = ImGui.GetCursorScreenPos();
        float width = Mathf.Max(120f, ImGui.GetContentRegionAvail().X);
        float height = 42f;
        ImGui.InvisibleButton("##Graph" + label, new Num.Vector2(width, height));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddText(start, 0xffb8bcc4, label);
        Num.Vector2 previous = default;
        bool havePrevious = false;
        for (int i = 0; i < count; i++)
        {
            float current = value(frames[startIndex + i]);
            if (float.IsNaN(current) || float.IsInfinity(current)) { havePrevious = false; continue; }
            float x = start.X + (count <= 1 ? 0f : i * (width - 4f) / (count - 1));
            float y = start.Y + height - 4f - Mathf.InverseLerp(min, max, current) * (height - 16f);
            Num.Vector2 point = new(x, y);
            if (havePrevious) draw.AddLine(previous, point, 0xffd8c76a, 1.3f);
            previous = point; havePrevious = true;
        }
    }

    private bool BeginPanel(string titleKey, string stableId)
    {
        ImGui.SetNextWindowBgAlpha(AIDebugSettings.Opacity);
        return ImGui.Begin(AIDebugExtendedLocalization.T(titleKey) + "###" + stableId);
    }

    private void TrySaveLayout()
    {
        try { AIDebugDockingNative.SaveLayout(); } catch { }
    }

    private bool MatchesEntityFilter(AbstractCreature creature)
    {
        if (string.IsNullOrWhiteSpace(entityFilter)) return true;
        string type = creature.creatureTemplate?.type?.value ?? string.Empty;
        string text = type + " " + creature.ID.number + " " + DebugEntityKey.From(creature);
        return text.IndexOf(entityFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool EventMatches(AIDebugTraceEvent item)
    {
        if (string.IsNullOrWhiteSpace(eventFilter)) return true;
        return Contains(CategoryText(item.Category), eventFilter) || Contains(item.Category.ToString(), eventFilter) ||
               Contains(AIDebugExtendedLocalization.EventName(item.Name), eventFilter) || Contains(item.Name, eventFilter) ||
               Contains(item.Detail, eventFilter) || Contains(item.Reason, eventFilter) ||
               Contains(item.RawDetail, eventFilter) || Contains(item.RawReason, eventFilter);
    }

    private AbstractCreature FindEntity(DebugEntityKey key)
    {
        for (int i = 0; i < entities.Count; i++)
            if (DebugEntityKey.From(entities[i]) == key) return entities[i];
        return null;
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

    private static void Flatten(AIDebugSnapshot source, Dictionary<string, string> output)
    {
        output.Clear();
        if (source == null) return;
        output["ControlOwner"] = source.ControlOwner;
        output["Lifecycle"] = AIDebugLocalization.EntityState(source.EntityState);
        for (int s = 0; s < source.Sections.Count; s++)
        for (int i = 0; i < source.Sections[s].Values.Count; i++)
        {
            AIDebugValue value = source.Sections[s].Values[i];
            string key = !string.IsNullOrEmpty(value.RawName) ? value.RawName : AIDebugLocalization.T(value.LabelKey);
            output[key] = value.Value;
        }
    }

    private static string CategoryText(AIDebugEventCategory category) => category switch
    {
        AIDebugEventCategory.Decision => L("Decision", "决策"),
        AIDebugEventCategory.State => L("State", "状态"),
        AIDebugEventCategory.Perception => L("Perception", "感知"),
        AIDebugEventCategory.Path => L("Path", "路径"),
        AIDebugEventCategory.Combat => L("Combat", "战斗"),
        AIDebugEventCategory.Social => L("Social", "社交"),
        AIDebugEventCategory.Warning => L("Warning", "警告"),
        _ => category.ToString()
    };

    private static bool Check(string label, ref bool value) => ImGui.Checkbox(label, ref value);
    private static void ProfileLine(AIDebugProfileCategory category, string key) =>
        ImGui.Text($"{AIDebugExtendedLocalization.T(key)}: {AIDebugProfiler.Get(category):0.000} ms");
    private static void NoSelection() => ImGui.TextDisabled(AIDebugExtendedLocalization.T("common.no_selection"));

    private static void CompactRow(string label, string value)
    {
        ImGui.TextDisabled(label); ImGui.SameLine(170f * AIDebugSettings.UiScale); ImGui.Text(value ?? "—");
    }

    private static void CompactRaw(AIDebugSnapshot snapshot, string raw, string label)
    {
        for (int s = 0; s < snapshot.Sections.Count; s++)
        for (int i = 0; i < snapshot.Sections[s].Values.Count; i++)
            if (snapshot.Sections[s].Values[i].RawName == raw)
            { CompactRow(label, snapshot.Sections[s].Values[i].Value); return; }
    }

    private static void Row(string left, string right)
    {
        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); ImGui.TextDisabled(left);
        ImGui.TableSetColumnIndex(1); ImGui.Text(right ?? "—");
    }

    private static void PathRow(string layer, string value, string meaning)
    {
        ImGui.TableNextRow(); Cell(0, layer); Cell(1, value); ImGui.TableSetColumnIndex(2); ImGui.TextDisabled(meaning ?? "—");
    }

    private static void Cell(int index, string value)
    {
        ImGui.TableSetColumnIndex(index); ImGui.Text(value ?? "—");
    }

    private static bool Contains(string text, string filter) =>
        !string.IsNullOrEmpty(text) && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

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
            "Dive" or "Attach" or "RetaliationCharge" or "Interfere" => new Num.Vector4(0.92f, 0.48f, 0.20f, 0.88f),
            "Observe" or "Circle" or "FakeDive" => new Num.Vector4(0.86f, 0.72f, 0.26f, 0.88f),
            "Roost" => new Num.Vector4(0.62f, 0.48f, 0.84f, 0.88f),
            _ => new Num.Vector4(0.28f, 0.68f, 0.82f, 0.80f)
        };
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static string L(string english, string chinese) =>
        AIDebugLocalization.Language == AIDebugLanguage.Chinese ? chinese : english;
}
