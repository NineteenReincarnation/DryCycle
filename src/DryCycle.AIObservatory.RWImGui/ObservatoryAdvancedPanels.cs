using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Historical Utility / Perception / Path panels. All rows are detached copies published
// by AIDebuggerHost; this class never calls AI modules, Tracker, PathFinder, Creature or
// any Unity/Rain World object from the Present thread.
internal static class ObservatoryAdvancedPanels
{
    internal const float PreferredHeight = 142f;

    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationCreature selected = snapshot.Selected;
        if (selected == null) return;

        if (!ImGui.BeginChild("##V5AdvancedHistory", new Num.Vector2(0f, PreferredHeight), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        if (ImGui.BeginTabBar("##V5AdvancedTabs"))
        {
            if (ImGui.BeginTabItem(L(snapshot, "Utility", "效用")))
            {
                DrawUtility(snapshot, selected);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(L(snapshot, "Perception", "感知")))
            {
                DrawPerception(snapshot, selected);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(L(snapshot, "Path", "寻路")))
            {
                DrawPath(snapshot, selected);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
    }

    private static void DrawUtility(AIDebugPresentationSnapshot snapshot, AIDebugPresentationCreature selected)
    {
        DrawAge(snapshot, selected.AdvancedAgeTicks, selected.UtilitiesTruncated);
        if (selected.Utilities.Length == 0)
        {
            ImGui.TextDisabled(L(snapshot, "No UtilityComparer data retained for this snapshot.", "此快照没有保留 UtilityComparer 数据。"));
            return;
        }

        if (!ImGui.BeginTable(
                "##V5UtilityTable",
                7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Num.Vector2(0f, 92f)))
            return;

        ImGui.TableSetupColumn(L(snapshot, "Module", "模块"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Raw", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn(L(snapshot, "Smooth", "平滑"), ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn(L(snapshot, "Weight", "权重"), ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn(L(snapshot, "Weighted", "加权"), ImGuiTableColumnFlags.WidthFixed, 65f);
        ImGui.TableSetupColumn(L(snapshot, "Bonus", "延续"), ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn(L(snapshot, "Winner", "胜出"), ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < selected.Utilities.Length; i++)
        {
            AIDebugUtilityRow row = selected.Utilities[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(Format(row.Raw));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Format(row.Smoothed));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(Format(row.Weight));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(Format(row.Weighted));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(Format(row.ContinuationBonus));
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(row.Winner ? "*" : string.Empty);
        }

        ImGui.EndTable();
    }

    private static void DrawPerception(AIDebugPresentationSnapshot snapshot, AIDebugPresentationCreature selected)
    {
        DrawAge(snapshot, selected.AdvancedAgeTicks, selected.PerceptionTruncated);
        if (selected.Perception.Length == 0)
        {
            ImGui.TextDisabled(L(snapshot, "No tracker representations retained for this snapshot.", "此快照没有保留 Tracker 感知项。"));
            return;
        }

        if (!ImGui.BeginTable(
                "##V5PerceptionTable",
                7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Num.Vector2(0f, 92f)))
            return;

        ImGui.TableSetupColumn(L(snapshot, "Creature", "生物"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(L(snapshot, "Visual", "视野"), ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn(L(snapshot, "Seen", "未见"), ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn(L(snapshot, "Chance", "概率"), ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn(L(snapshot, "Priority", "优先"), ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn(L(snapshot, "Relation", "关系"), ImGuiTableColumnFlags.WidthFixed, 82f);
        ImGui.TableSetupColumn(L(snapshot, "Intensity", "强度"), ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableHeadersRow();

        // Presentation already sorted this detached copy by priority. The recorder itself
        // retains original Tracker order and pays no sorting cost.
        for (int i = 0; i < selected.Perception.Length; i++)
        {
            AIDebugPerceptionRow row = selected.Perception[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{row.Key}\nlast: {row.LastSeen}\nbest: {row.BestGuess}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(row.VisualContact ? "Y" : "—");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.TicksSinceSeen.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(row.EstimatedChance.ToString("0.000"));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.Priority.ToString("0.000"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(row.Relationship);
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(row.RelationshipIntensity.ToString("0.000"));
        }

        ImGui.EndTable();
    }

    private static void DrawPath(AIDebugPresentationSnapshot snapshot, AIDebugPresentationCreature selected)
    {
        DrawAge(snapshot, selected.AdvancedAgeTicks, false);
        AIDebugPathState path = selected.Path;
        if (!path.HasPathfinder)
        {
            ImGui.TextDisabled(L(snapshot, "No active PathFinder in this snapshot.", "此快照没有活动的 PathFinder。"));
            ImGui.Text($"{L(snapshot, "Destination", "目的地")}: {path.Destination}");
            return;
        }

        if (ImGui.BeginTable("##V5PathTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            Row(L(snapshot, "PathFinder", "寻路器"), path.Pathfinder);
            Row(L(snapshot, "Destination", "目的地"), path.Destination.ToString());
            Row(L(snapshot, "Reachable", "可到达"), Bool(path.DestinationReachable));
            Row(L(snapshot, "Can return", "可返回"), Bool(path.CanReturnFromDestination));
            Row(L(snapshot, "Stranded", "受困"), Bool(path.Stranded));
            ImGui.EndTable();
        }
    }

    private static void Row(string name, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(name);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value ?? "—");
    }

    private static void DrawAge(AIDebugPresentationSnapshot snapshot, int ageTicks, bool truncated)
    {
        string text = ageTicks > 0
            ? $"{L(snapshot, "snapshot age", "快照年龄")} {ageTicks} ticks"
            : L(snapshot, "current rich sample", "当前完整采样");
        if (truncated) text += " · TRUNCATED";
        ImGui.TextDisabled(text);
    }

    private static string Format(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? "—" : value.ToString("0.000");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string L(AIDebugPresentationSnapshot snapshot, string english, string chinese) =>
        snapshot.Language == AIDebugLanguage.Chinese ? chinese : english;
}
