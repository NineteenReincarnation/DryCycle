using System;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Makes World.DisabledMapRooms explicit in the Player Map inspector. This is intentionally read-only:
/// DisabledMapRooms is produced by world/timeline conditional rules (for example EXCLUSIVEROOM and
/// HIDEROOM), not by a persistent map_XX.txt authoring flag. Offering a toggle here would create a
/// session-only edit that looks saveable but is not.
/// </summary>
internal static class PlayerMapConditionalVisibilityInfo
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Player Map conditional-visibility info enabled through direct room-inspector calls; no self-detour attached.");
    }

    internal static void Disable() => enabled = false;

    internal static void Draw(PlayerMapRoomSnapshot room)
    {
        if (!enabled || room == null || !room.Disabled) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("地图可见性", "MAP VISIBILITY"));
        ImGui.TextUnformatted(DevToolUiSettings.T("条件隐藏", "Conditionally hidden"));
        ImGui.TextWrapped(DevToolUiSettings.T(
            "该房间当前位于 World.DisabledMapRooms 中，因此不会进入玩家地图、连接线和 Render 输出。这个状态由当前 World/Timeline 的条件规则产生；请在 World 数据中编辑 EXCLUSIVEROOM / HIDEROOM 等条件，而不是在 Player Map 中修改。",
            "This room is currently in World.DisabledMapRooms, so it is excluded from the player map, connections and Render output. The state comes from the active World/Timeline conditional rules; edit EXCLUSIVEROOM / HIDEROOM conditions in World data rather than Player Map."));
    }

}
