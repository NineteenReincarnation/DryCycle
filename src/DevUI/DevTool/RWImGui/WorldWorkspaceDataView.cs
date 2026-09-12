using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.TemperatureSystem;
using DryCycle.Weather.Spatial;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Structured editor for DryCycle-owned world data.
///
/// The editor talks to the runtime authorities directly instead of maintaining
/// a parallel UI model. TemperatureSetsLoader and WeatherSpatialRegistry therefore
/// remain the single source of truth for both gameplay and authoring.
/// </summary>
internal static class WorldWorkspaceDataView
{
    private static string lastSaveStatus = string.Empty;

    internal static bool HasDirtyData =>
        TemperatureSetsLoader.Dirty || WeatherSpatialRegistry.Dirty;

    internal static void Draw(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot == null)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("世界数据快照不可用。", "World data snapshot is unavailable."),
                true);
            return;
        }

        string region = snapshot.RegionName ?? string.Empty;
        EditorMapRoomSnapshot room = FindSelectedRoom(snapshot);

        DrawHeader(region, room);
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##DryCycleWorldDataTabs")) return;

        if (ImGui.BeginTabItem(DevToolUiSettings.T("房间环境", "Room Environment")))
        {
            DrawEnvironment(region, room);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(DevToolUiSettings.T("天气", "Weather")))
        {
            DrawWeather(region, room);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    internal static bool SaveDirty()
    {
        bool attempted = false;
        bool ok = true;

        if (TemperatureSetsLoader.Dirty)
        {
            attempted = true;
            ok &= TemperatureSetsLoader.Save();
        }

        if (WeatherSpatialRegistry.Dirty)
        {
            attempted = true;
            ok &= WeatherSpatialRegistry.Save();
        }

        lastSaveStatus = !attempted
            ? DevToolUiSettings.T("没有未保存的世界数据。", "No unsaved world data.")
            : ok
                ? DevToolUiSettings.T("世界数据已保存。", "World data saved.")
                : DevToolUiSettings.T(
                    "部分世界数据保存失败，请检查日志。",
                    "Some world data failed to save; check the log.");
        return ok;
    }

    private static void DrawHeader(string region, EditorMapRoomSnapshot room)
    {
        ImGui.TextUnformatted(region);
        if (room != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("/ " + room.Name);
        }

        ImGui.SameLine(0f, 18f);
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("保存世界数据", "Save World Data"),
                "WorldDataSaveAll",
                HasDirtyData ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            SaveDirty();
        }

        ImGui.Spacing();
        DrawStoreState(
            "TemperatureSets",
            TemperatureSetsLoader.Dirty,
            TemperatureSetsLoader.LoadedPath,
            TemperatureSetsLoader.LoadError);
        DrawStoreState(
            "WeatherSpatial",
            WeatherSpatialRegistry.Dirty,
            WeatherSpatialRegistry.LoadedPath,
            WeatherSpatialRegistry.FatalLoadError);

        if (!string.IsNullOrEmpty(lastSaveStatus))
            DevToolWidgets.MutedText(lastSaveStatus, true);
    }

    private static void DrawStoreState(string name, bool dirty, string path, string error)
    {
        ImGui.TextUnformatted(name);
        ImGui.SameLine(170f);
        ImGui.TextDisabled(dirty
            ? DevToolUiSettings.T("未保存", "dirty")
            : DevToolUiSettings.T("已同步", "synced"));
        ImGui.SameLine(245f);
        ImGui.TextDisabled(string.IsNullOrEmpty(path) ? "—" : Path.GetFileName(path));

        if (!string.IsNullOrEmpty(error))
            DevToolWidgets.MutedText(error, true);
    }

    private static void DrawEnvironment(string region, EditorMapRoomSnapshot room)
    {
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "直接编辑 TemperatureSets.json 对应的运行时房间环境。修改立即进入运行时内存，保存只负责持久化。",
            "Edits the runtime room environment backed by TemperatureSets.json. Changes enter runtime memory immediately; Save only persists them."), true);
        ImGui.Spacing();

        if (room == null)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "先在左侧 World Explorer 选择一个房间。",
                "Select a room in World Explorer first."), true);
            return;
        }

        bool hasProfile = TemperatureSetsLoader.HasProfile(region, room.Name);
        RoomEnvironmentProfile profile = TemperatureSetsLoader.GetProfileOrDefault(region, room.Name);

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(hasProfile
            ? DevToolUiSettings.T("房间覆盖", "room override")
            : DevToolUiSettings.T("默认值", "defaults"));

        ImGui.SameLine(0f, 18f);
        if (!hasProfile) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重置房间", "Reset Room"),
                "WorldDataResetEnvironment",
                DevToolButtonTone.Subtle))
        {
            TemperatureSetsLoader.RemoveProfile(region, room.Name);
        }
        if (!hasProfile) ImGui.EndDisabled();

        ImGui.Separator();

        float roomHeat = profile.RoomHeat;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("基础热度##WorldDataRoomHeat", "Base heat##WorldDataRoomHeat"),
                ref roomHeat,
                -1f,
                1f,
                "%.3f"))
        {
            profile.RoomHeat = roomHeat;
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "负值偏冷，正值偏热。",
            "Negative is colder; positive is hotter."), true);

        float sunlight = profile.SunlightIntensity;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("日照强度##WorldDataSunlight", "Sunlight intensity##WorldDataSunlight"),
                ref sunlight,
                0f,
                1f,
                "%.3f"))
        {
            profile.SunlightIntensity = RoomEnvironmentProfile.ClampUnit(sunlight);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        float shade = profile.RoomShade;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("房间遮阴##WorldDataShade", "Room shade##WorldDataShade"),
                ref shade,
                0f,
                1f,
                "%.3f"))
        {
            profile.RoomShade = RoomEnvironmentProfile.ClampUnit(shade);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        float humidity = profile.Humidity;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("湿度##WorldDataHumidity", "Humidity##WorldDataHumidity"),
                ref humidity,
                -1f,
                1f,
                "%.3f"))
        {
            profile.Humidity = RoomEnvironmentProfile.ClampSigned(humidity);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "-1 为极干，0 为中性，+1 为极湿。",
            "-1 is extremely dry, 0 neutral, +1 extremely humid."), true);

        DrawWarnings(TemperatureSetsLoader.Warnings);
    }

    private static void DrawWeather(string region, EditorMapRoomSnapshot room)
    {
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "天气分成两层：区域调度决定某类天气是否参与循环及概率；空间规则决定具体天气在区域默认和当前房间里是否允许。",
            "Weather has two layers: region scheduling controls participation/chance, while spatial rules control whether each concrete weather is allowed by default and in the selected room."), true);
        ImGui.Spacing();

        if (!string.IsNullOrEmpty(WeatherSpatialRegistry.FatalLoadError))
        {
            DevToolWidgets.MutedText(WeatherSpatialRegistry.FatalLoadError, true);
            ImGui.Separator();
        }

        if (ImGui.CollapsingHeader(
                DevToolUiSettings.T("区域天气调度", "Region Weather Schedule"),
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRegionSchedule(region);
        }

        if (ImGui.CollapsingHeader(
                DevToolUiSettings.T("区域空间默认", "Region Spatial Defaults"),
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "这里只编辑具体 Weather / DangerType。Inherit 最终没有其它规则时按 Forbidden 处理。",
                "Only concrete Weather / DangerType entries are editable here. Inherit ultimately falls back to Forbidden when no other rule applies."), true);
            DrawSpatialRules(region, null, false);
        }

        string roomHeader = room == null
            ? DevToolUiSettings.T("当前房间覆盖", "Selected Room Overrides")
            : DevToolUiSettings.T("当前房间覆盖 · ", "Selected Room Overrides · ") + room.Name;
        if (ImGui.CollapsingHeader(roomHeader, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (room == null)
            {
                DevToolWidgets.MutedText(DevToolUiSettings.T(
                    "先在左侧 World Explorer 选择一个房间。",
                    "Select a room in World Explorer first."), true);
            }
            else
            {
                DevToolWidgets.MutedText(DevToolUiSettings.T(
                    "FamWeather 不在房间级编辑。房间只覆盖具体子天气 / DangerType：Inherit、Allow、Forbidden。",
                    "FamWeather is not room-editable. Rooms override concrete sub-weather / DangerType only: Inherit, Allow or Forbidden."), true);
                DrawSpatialRules(region, room.Name, true);
            }
        }

        DrawWarnings(WeatherSpatialRegistry.Warnings);
    }

    private static void DrawRegionSchedule(string region)
    {
        IReadOnlyList<WeatherSpatialFamily> families = WeatherSpatialCatalog.AllFamilies;
        for (int i = 0; i < families.Count; i++)
        {
            WeatherSpatialFamily family = families[i];
            string familyId = family.Id ?? string.Empty;

            WeatherSpatialRegistry.TryGetFamilySchedule(
                region,
                familyId,
                out bool familyEnabled,
                out float familyChance);

            ImGui.PushID("WeatherFamily_" + familyId);
            bool enabled = familyEnabled;
            if (ImGui.Checkbox(DevToolUiSettings.T("启用 ", "Enable ") + familyId, ref enabled))
                WeatherSpatialRegistry.SetFamilyScheduleEnabled(region, familyId, enabled);

            ImGui.SameLine(0f, 18f);
            ImGui.SetNextItemWidth(190f);
            float chance = familyChance;
            if (ImGui.SliderFloat(
                    DevToolUiSettings.T("族概率", "Family chance"),
                    ref chance,
                    0f,
                    100f,
                    "%.0f%%"))
            {
                WeatherSpatialRegistry.SetFamilyScheduleChance(region, familyId, chance);
            }

            if (ImGui.TreeNode(DevToolUiSettings.T("子天气", "Sub-weather")))
            {
                IReadOnlyList<WeatherSpatialMember> members = family.Members;
                for (int j = 0; j < members.Count; j++)
                {
                    WeatherSpatialMember member = members[j];
                    WeatherSpatialRegistry.TryGetSubWeatherSchedule(
                        region,
                        member.Kind,
                        member.Id,
                        out bool subEnabled,
                        out float subChance);

                    ImGui.PushID(member.Key);
                    bool nextEnabled = subEnabled;
                    if (ImGui.Checkbox(WeatherDisplay(member), ref nextEnabled))
                    {
                        WeatherSpatialRegistry.SetSubWeatherScheduleEnabled(
                            region,
                            member.Kind,
                            member.Id,
                            nextEnabled);
                    }

                    ImGui.SameLine(0f, 18f);
                    ImGui.SetNextItemWidth(170f);
                    float nextChance = subChance;
                    if (ImGui.SliderFloat(
                            DevToolUiSettings.T("概率", "Chance"),
                            ref nextChance,
                            0f,
                            100f,
                            "%.0f%%"))
                    {
                        WeatherSpatialRegistry.SetSubWeatherScheduleChance(
                            region,
                            member.Kind,
                            member.Id,
                            nextChance);
                    }
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private static void DrawSpatialRules(string region, string roomName, bool roomLevel)
    {
        IReadOnlyList<WeatherSpatialTarget> targets = WeatherSpatialCatalog.AllTargets;
        for (int i = 0; i < targets.Count; i++)
        {
            WeatherSpatialTarget target = targets[i];
            if (target.IsFamily) continue;

            WeatherSpatialRule rule = roomLevel
                ? WeatherSpatialRegistry.GetRoomRule(region, roomName, target)
                : WeatherSpatialRegistry.GetDefaultRule(region, target);

            ImGui.PushID((roomLevel ? "Room_" : "Region_") + target.Key);
            ImGui.TextUnformatted(WeatherDisplay(target));
            ImGui.SameLine(220f);
            ImGui.SetNextItemWidth(160f);
            if (DrawRuleCombo("##Rule", ref rule))
            {
                if (roomLevel)
                    WeatherSpatialRegistry.SetRoomRule(region, roomName, target, rule);
                else
                    WeatherSpatialRegistry.SetDefaultRule(region, target, rule);
            }
            ImGui.PopID();
        }
    }

    private static bool DrawRuleCombo(string id, ref WeatherSpatialRule rule)
    {
        bool changed = false;
        if (!ImGui.BeginCombo(id, RuleLabel(rule))) return false;

        WeatherSpatialRule[] values =
        {
            WeatherSpatialRule.Inherit,
            WeatherSpatialRule.Allow,
            WeatherSpatialRule.Deny
        };

        for (int i = 0; i < values.Length; i++)
        {
            WeatherSpatialRule candidate = values[i];
            bool selected = rule == candidate;
            if (ImGui.Selectable(RuleLabel(candidate), selected))
            {
                rule = candidate;
                changed = true;
            }
            if (selected) ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static string RuleLabel(WeatherSpatialRule rule)
    {
        return rule switch
        {
            WeatherSpatialRule.Allow => DevToolUiSettings.T("允许", "Allow"),
            WeatherSpatialRule.Deny => DevToolUiSettings.T("禁止", "Forbidden"),
            _ => DevToolUiSettings.T("继承", "Inherit")
        };
    }

    private static string WeatherDisplay(WeatherSpatialMember member)
    {
        return (member.Kind.ToString().Equals("DangerType", StringComparison.OrdinalIgnoreCase)
            ? "DangerType / "
            : "Weather / ") + member.Id;
    }

    private static string WeatherDisplay(WeatherSpatialTarget target)
    {
        return (target.Kind.ToString().Equals("DangerType", StringComparison.OrdinalIgnoreCase)
            ? "DangerType / "
            : "Weather / ") + target.WeatherId;
    }

    private static void DrawWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings == null || warnings.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();
        for (int i = 0; i < warnings.Count; i++)
            DevToolWidgets.MutedText("• " + warnings[i], true);
    }

    private static EditorMapRoomSnapshot FindSelectedRoom(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            if (rooms[i].RoomIndex == snapshot.SelectedRoomIndex)
                return rooms[i];
        }
        return null;
    }
}
