using System;
using DryCycle.Debugging.AI;
using ImGuiNET;

namespace DryCycle.AIObservatory.RWImGui;

// Compact detached compare strip for the currently selected creature and up to three
// pinned creatures. It only consumes AIDebugPresentationTrackedEntity snapshots and may
// safely run on RWImGUI's Present thread.
internal static class ObservatoryTrackedCompare
{
    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTrackedEntity[] tracked = snapshot.Tracked;
        if (tracked == null || tracked.Length <= 1) return;

        ImGui.TextDisabled(snapshot.Language == AIDebugLanguage.Chinese
            ? "跟踪对比"
            : "Tracked Compare");

        if (!ImGui.BeginTable(
                "##V5TrackedCompare",
                tracked.Length,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        for (int i = 0; i < tracked.Length; i++)
        {
            AIDebugPresentationTrackedEntity entity = tracked[i];
            ImGui.TableSetColumnIndex(i);

            string role = entity.Selected
                ? (entity.Pinned ? "S+P" : "S")
                : (entity.Pinned ? "P" : "T");
            string label = $"[{role}] {entity.DisplayName}##TrackedCompare{entity.Key.Spawner}:{entity.Key.Number}";
            if (ImGui.Selectable(label, entity.Selected))
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Select(entity.Key));

            ImGui.TextDisabled(entity.Room);

            if (entity.Motion.HasValue)
            {
                double speed = Math.Sqrt(
                    entity.Motion.VX * entity.Motion.VX +
                    entity.Motion.VY * entity.Motion.VY);
                ImGui.Text($"pos {entity.Motion.X:0}, {entity.Motion.Y:0}  v {speed:0.0}");
            }
            else
            {
                ImGui.TextDisabled("pos —  v —");
            }

            if (entity.FastState.HasValue)
            {
                AIDebugFastState state = entity.FastState.State;
                string mode = state.ModeToken == AIDebugFastState.UnknownToken ? "—" : state.ModeToken.ToString();
                ImGui.TextDisabled($"mode {mode}  room {state.Room}");
            }
            else
            {
                ImGui.TextDisabled("mode —");
            }

            if (entity.Pinned)
            {
                if (ImGui.SmallButton($"{(snapshot.Language == AIDebugLanguage.Chinese ? "取消固定" : "Unpin")}##TrackedUnpin{entity.Key.Spawner}:{entity.Key.Number}"))
                    AIDebugRecorderControl.RequestTogglePin(entity.Key);
            }
        }

        ImGui.EndTable();
        ImGui.Separator();
    }
}
