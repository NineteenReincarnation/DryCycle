using System;
using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal sealed class AIDebuggerWindow
{
    private readonly List<AbstractCreature> entities = new(128);
    private readonly HashSet<DebugEntityKey> pinned = new();
    private DebugEntityKey selected;
    private bool hasSelection;
    private bool fullMode;
    private bool showRawNames;
    private int nextEntityRefresh;
    private AIDebugSnapshot snapshot;

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
        ResolveSelection(game);
        if (fullMode) DrawFull(game, overheadMs);
        else DrawCompact(overheadMs);
    }

    private void DrawNoGame()
    {
        ImGui.SetNextWindowPos(new Num.Vector2(18f, 18f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(390f, 92f), ImGuiCond.Always);
        if (ImGui.Begin(AIDebugLocalization.T("app.title"), ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_game"));
        ImGui.End();
    }

    private void DrawCompact(double overheadMs)
    {
        float width = 360f;
        ImGui.SetNextWindowPos(new Num.Vector2(Mathf.Max(8f, Screen.width - width - 16f), 16f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 250f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(AIDebugLocalization.T("app.title") + "###DryCycleAICompact", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        DrawTopControls(overheadMs);
        ImGui.Separator();
        if (snapshot == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            ImGui.End();
            return;
        }

        ImGui.Text(snapshot.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled("[" + AIDebugLocalization.EntityState(snapshot.EntityState) + "]");
        DrawCompactRow("app.control_owner", snapshot.ControlOwner);
        DrawCompactField("field.mode", "DesertBatflyAI.Mode");
        DrawCompactField("field.expressed_role", "DesertBatflySocialRoles.Expressed");
        DrawCompactField("field.target", "DesertBatflyAI.Target");
        DrawCompactField("field.immediate_danger", "DesertBatflyAI.HasImmediateDanger");
        DrawCompactField("field.suppression", "DesertBatflySocialRoles.Suppression");
        ImGui.End();
    }

    private void DrawFull(RainWorldGame game, double overheadMs)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(Mathf.Max(640f, Screen.width - 16f), Mathf.Max(420f, Screen.height - 16f)), ImGuiCond.Always);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (!ImGui.Begin(AIDebugLocalization.T("app.title") + "###DryCycleAIFull", flags))
        {
            ImGui.End();
            return;
        }

        DrawTopControls(overheadMs);
        ImGui.Separator();
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float browserWidth = Mathf.Clamp(available.X * 0.22f, 220f, 340f);
        float decisionWidth = Mathf.Clamp(available.X * 0.35f, 300f, 520f);

        ImGui.BeginChild("##entityBrowser", new Num.Vector2(browserWidth, available.Y), true);
        DrawEntityBrowser(game);
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("##decisionStack", new Num.Vector2(decisionWidth, available.Y), true);
        DrawDecisionStack();
        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("##inspector", new Num.Vector2(0f, available.Y), true);
        DrawInspector();
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawTopControls(double overheadMs)
    {
        if (ImGui.Button(fullMode ? AIDebugLocalization.T("app.compact") : AIDebugLocalization.T("app.full")))
            fullMode = !fullMode;
        ImGui.SameLine();
        if (ImGui.Button(AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "English" : "中文"))
            AIDebugLocalization.Language = AIDebugLocalization.Language == AIDebugLanguage.Chinese
                ? AIDebugLanguage.English : AIDebugLanguage.Chinese;
        ImGui.SameLine();
        bool raw = showRawNames;
        if (ImGui.Checkbox(AIDebugLocalization.T("app.raw_names"), ref raw)) showRawNames = raw;
        ImGui.SameLine();
        ImGui.TextDisabled($"{AIDebugLocalization.T("app.overhead")}: {overheadMs:0.00} ms");
    }

    private void DrawEntityBrowser(RainWorldGame game)
    {
        ImGui.Text(AIDebugLocalization.T("app.entity_browser"));
        ImGui.Separator();

        if (pinned.Count > 0)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.pinned"));
            foreach (DebugEntityKey key in pinned)
            {
                AbstractCreature creature = AIDebugRegistry.Resolve(game, key);
                if (creature != null) DrawEntityRow(creature, true);
            }
            ImGui.Separator();
        }

        int currentRoom = game.cameras != null && game.cameras.Length > 0 && game.cameras[0]?.room != null
            ? game.cameras[0].room.abstractRoom.index : int.MinValue;
        ImGui.TextDisabled(AIDebugLocalization.T("app.current_room"));
        for (int i = 0; i < entities.Count; i++)
            if (entities[i].pos.room == currentRoom) DrawEntityRow(entities[i], false);

        ImGui.Separator();
        ImGui.TextDisabled("World");
        for (int i = 0; i < entities.Count; i++)
            if (entities[i].pos.room != currentRoom) DrawEntityRow(entities[i], false);
    }

    private void DrawEntityRow(AbstractCreature creature, bool pinnedSection)
    {
        DebugEntityKey key = DebugEntityKey.From(creature);
        bool selectedNow = hasSelection && key == selected;
        string type = creature.creatureTemplate?.type?.value ?? "Creature";
        string state = AIDebugLocalization.EntityState(AIDebugRegistry.EntityState(creature));
        string label = $"{type} #{creature.ID.number}  [{state}]##{key.Spawner}:{key.Number}:{pinnedSection}";
        if (ImGui.Selectable(label, selectedNow))
        {
            selected = key;
            hasSelection = true;
            snapshot = null;
        }
        if (ImGui.IsItemHovered() && showRawNames)
            ImGui.SetTooltip(key.ToString());
    }

    private void DrawDecisionStack()
    {
        ImGui.Text(AIDebugLocalization.T("app.decision_stack"));
        ImGui.Separator();
        if (snapshot == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            return;
        }

        ImGui.TextDisabled(AIDebugLocalization.T("app.control_owner"));
        ImGui.SameLine();
        ImGui.Text(snapshot.ControlOwner);
        ImGui.Separator();

        for (int i = 0; i < snapshot.Decisions.Count; i++)
        {
            AIDebugDecisionNode node = snapshot.Decisions[i];
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
    }

    private void DrawInspector()
    {
        ImGui.Text(AIDebugLocalization.T("app.inspector"));
        ImGui.Separator();
        if (snapshot == null)
        {
            ImGui.TextDisabled(AIDebugLocalization.T("app.no_selection"));
            return;
        }

        bool isPinned = pinned.Contains(snapshot.Key);
        if (ImGui.Button(AIDebugLocalization.T(isPinned ? "app.unpin" : "app.pin")))
        {
            if (isPinned) pinned.Remove(snapshot.Key);
            else pinned.Add(snapshot.Key);
        }
        ImGui.SameLine();
        ImGui.Text(snapshot.DisplayName);
        ImGui.Separator();

        for (int s = 0; s < snapshot.Sections.Count; s++)
        {
            AIDebugSection section = snapshot.Sections[s];
            if (!ImGui.CollapsingHeader(AIDebugLocalization.T(section.TitleKey), ImGuiTreeNodeFlags.DefaultOpen)) continue;
            for (int i = 0; i < section.Values.Count; i++) DrawValue(section.Values[i]);
            ImGui.Spacing();
        }
    }

    private void DrawValue(AIDebugValue value)
    {
        ImGui.TextDisabled(AIDebugLocalization.T(value.LabelKey));
        if (showRawNames && !string.IsNullOrEmpty(value.RawName))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("  " + value.RawName);
        }
        ImGui.SameLine(Mathf.Max(190f, ImGui.GetWindowWidth() * 0.47f));
        ImGui.Text(value.Value);
        if (value.AgeTicks > 0 || !string.IsNullOrEmpty(value.Source))
        {
            ImGui.SameLine();
            string age = value.AgeTicks > 0 ? $"{value.AgeTicks} {AIDebugLocalization.T("app.ticks")}" : "live";
            ImGui.TextDisabled($"[{age}{(string.IsNullOrEmpty(value.Source) ? "" : " · " + value.Source)}]");
        }
    }

    private void DrawCompactField(string labelKey, string rawName)
    {
        if (snapshot == null) return;
        for (int s = 0; s < snapshot.Sections.Count; s++)
            for (int i = 0; i < snapshot.Sections[s].Values.Count; i++)
            {
                AIDebugValue value = snapshot.Sections[s].Values[i];
                if (value.RawName != rawName) continue;
                DrawCompactRow(labelKey, value.Value);
                return;
            }
    }

    private static void DrawCompactRow(string labelKey, string value)
    {
        ImGui.TextDisabled(AIDebugLocalization.T(labelKey));
        ImGui.SameLine(155f);
        ImGui.Text(value ?? "—");
    }

    private void RefreshEntities(RainWorldGame game)
    {
        if (Time.frameCount < nextEntityRefresh) return;
        nextEntityRefresh = Time.frameCount + 15;
        AIDebugRegistry.CollectWorld(game, entities);
        entities.Sort(CompareEntities);
        if (!hasSelection && entities.Count > 0)
        {
            AbstractCreature candidate = null;
            int currentRoom = game.cameras != null && game.cameras.Length > 0 && game.cameras[0]?.room != null
                ? game.cameras[0].room.abstractRoom.index : int.MinValue;
            for (int i = 0; i < entities.Count; i++)
            {
                AbstractCreature creature = entities[i];
                if (creature.pos.room != currentRoom) continue;
                if (creature.realizedCreature?.GetType().Name == "DesertBatfly") { candidate = creature; break; }
                candidate ??= creature.realizedCreature != null ? creature : null;
            }
            candidate ??= entities[0];
            selected = DebugEntityKey.From(candidate);
            hasSelection = true;
        }
    }

    private void ResolveSelection(RainWorldGame game)
    {
        if (!hasSelection)
        {
            snapshot = null;
            return;
        }
        AbstractCreature creature = AIDebugRegistry.Resolve(game, selected);
        snapshot = creature == null ? null : AIDebugRegistry.Capture(creature, game);
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

    private static Num.Vector4 StateColor(AIDebugDecisionState state) => state switch
    {
        AIDebugDecisionState.Active => new Num.Vector4(0.30f, 0.82f, 0.95f, 1f),
        AIDebugDecisionState.Ready => new Num.Vector4(0.75f, 0.86f, 0.95f, 1f),
        AIDebugDecisionState.Blocked => new Num.Vector4(0.88f, 0.42f, 0.42f, 1f),
        AIDebugDecisionState.Warning => new Num.Vector4(0.95f, 0.70f, 0.28f, 1f),
        AIDebugDecisionState.Pass => new Num.Vector4(0.48f, 0.82f, 0.55f, 1f),
        _ => new Num.Vector4(0.55f, 0.58f, 0.62f, 1f)
    };
}
