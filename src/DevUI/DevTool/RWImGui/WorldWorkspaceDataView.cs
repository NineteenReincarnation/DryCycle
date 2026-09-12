using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.TemperatureSystem;
using DryCycle.Weather.Spatial;
using ImGuiNET;
using Num = System.Numerics;

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
    private enum DataPage
    {
        Environment,
        Weather
    }

    private static readonly Num.Vector4 SyncedColor = new(0.48f, 0.78f, 0.60f, 1f);
    private static readonly Num.Vector4 DirtyColor = new(0.96f, 0.77f, 0.38f, 1f);
    private static readonly Num.Vector4 ErrorColor = new(0.92f, 0.42f, 0.42f, 1f);
    private static readonly Num.Vector4 TechnicalColor = new(0.56f, 0.62f, 0.70f, 1f);

    private static DataPage currentPage = DataPage.Environment;
    private static string lastSaveStatus = string.Empty;

    internal static bool HasDirtyData =>
        TemperatureSetsLoader.Dirty || WeatherSpatialRegistry.Dirty;

    internal static void Draw(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot == null)
        {
            DrawEmptyState(
                DevToolUiSettings.T("世界数据快照不可用", "World data unavailable"),
                DevToolUiSettings.T("当前没有可编辑的区域数据。", "There is no editable region snapshot right now."));
            return;
        }

        string region = snapshot.RegionName ?? string.Empty;
        EditorMapRoomSnapshot room = FindSelectedRoom(snapshot);

        DrawHeader(region, room);
        ImGui.Spacing();
        DrawPageNavigation();
        ImGui.Spacing();

        switch (currentPage)
        {
            case DataPage.Environment:
                DrawEnvironment(region, room);
                break;
            case DataPage.Weather:
                DrawWeather(region, room);
                break;
        }
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
        float startX = ImGui.GetCursorPosX();
        float rightX = startX + ImGui.GetContentRegionAvail().X;
        string saveLabel = HasDirtyData
            ? DevToolUiSettings.T("保存修改", "Save Changes")
            : DevToolUiSettings.T("已保存", "Saved");
        float saveWidth = DevToolWidgets.ButtonWidth(saveLabel);

        ImGui.TextUnformatted(region);
        if (room != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("/ " + room.Name);
        }

        float actionX = rightX - saveWidth;
        if (actionX > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(actionX);
        }

        if (!HasDirtyData) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                saveLabel,
                "WorldDataSaveAll",
                HasDirtyData ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            SaveDirty();
        }
        if (!HasDirtyData) ImGui.EndDisabled();

        ImGui.Spacing();
        DrawStoreState(
            "TemperatureSets.json",
            TemperatureSetsLoader.Dirty,
            TemperatureSetsLoader.LoadedPath,
            TemperatureSetsLoader.LoadError);
        DrawStoreState(
            "WeatherSpatial.json",
            WeatherSpatialRegistry.Dirty,
            WeatherSpatialRegistry.LoadedPath,
            WeatherSpatialRegistry.FatalLoadError);

        if (!string.IsNullOrEmpty(lastSaveStatus))
        {
            ImGui.Spacing();
            DevToolWidgets.MutedText(lastSaveStatus, true);
        }

        ImGui.Separator();
    }

    private static void DrawStoreState(string displayName, bool dirty, string path, string error)
    {
        ImGui.TextUnformatted(displayName);
        string state;
        Num.Vector4 color;
        if (!string.IsNullOrEmpty(error))
        {
            state = DevToolUiSettings.T("● 加载异常", "● Load error");
            color = ErrorColor;
        }
        else if (dirty)
        {
            state = DevToolUiSettings.T("● 有未保存修改", "● Unsaved changes");
            color = DirtyColor;
        }
        else
        {
            state = DevToolUiSettings.T("● 已同步", "● Synced");
            color = SyncedColor;
        }

        float stateWidth = ImGui.CalcTextSize(state).X;
        if (DevToolWidgets.SameLineIfFits(stateWidth, 6f))
            ImGui.TextColored(color, state);
        else
            ImGui.TextColored(color, state);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(string.IsNullOrEmpty(path)
                ? DevToolUiSettings.T("尚未解析到文件路径", "No file path has been resolved yet")
                : path);
            if (!string.IsNullOrEmpty(error))
            {
                ImGui.Separator();
                ImGui.TextWrapped(error);
            }
            ImGui.EndTooltip();
        }
    }

    private static void DrawPageNavigation()
    {
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("房间环境", "Room Environment"),
                "WorldDataEnvironmentTab",
                currentPage == DataPage.Environment ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            currentPage = DataPage.Environment;
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("天气", "Weather"),
                "WorldDataWeatherTab",
                currentPage == DataPage.Weather ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            currentPage = DataPage.Weather;
        }
    }

    private static void DrawEnvironment(string region, EditorMapRoomSnapshot room)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("房间环境", "ROOM ENVIRONMENT"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "调整当前房间的热环境、太阳暴露和空气湿度。修改会立即进入运行时；保存只负责写回 TemperatureSets.json。",
            "Tune the selected room's thermal environment, solar exposure and humidity. Changes affect runtime immediately; Save only persists TemperatureSets.json."), true);
        ImGui.Spacing();

        if (!string.IsNullOrEmpty(TemperatureSetsLoader.LoadError))
            DrawErrorBanner(TemperatureSetsLoader.LoadError);

        if (room == null)
        {
            DrawEmptyState(
                DevToolUiSettings.T("未选择房间", "No room selected"),
                DevToolUiSettings.T(
                    "从左侧 World Explorer 选择一个房间后，这里会显示它的环境参数。",
                    "Select a room in World Explorer to edit its environment profile."));
            return;
        }

        bool hasProfile = TemperatureSetsLoader.HasProfile(region, room.Name);
        RoomEnvironmentProfile profile = TemperatureSetsLoader.GetProfileOrDefault(region, room.Name);

        DrawRoomEnvironmentContext(room.Name, hasProfile, region);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("热环境", "THERMAL"));
        float roomHeat = profile.RoomHeat;
        if (DrawScalarControl(
                DevToolUiSettings.T("基础热度", "Base Heat"),
                "RoomHeat",
                ref roomHeat,
                -1f,
                1f,
                HeatSemantic(roomHeat),
                "RoomHeat"))
        {
            profile.RoomHeat = roomHeat;
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("太阳环境", "SOLAR"));
        float sunlight = profile.SunlightIntensity;
        if (DrawScalarControl(
                DevToolUiSettings.T("日照强度", "Sunlight Intensity"),
                "SunlightIntensity",
                ref sunlight,
                0f,
                1f,
                SunlightSemantic(sunlight),
                "SunlightIntensity",
                true))
        {
            profile.SunlightIntensity = RoomEnvironmentProfile.ClampUnit(sunlight);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        float shade = profile.RoomShade;
        if (DrawScalarControl(
                DevToolUiSettings.T("房间遮阴", "Room Shade"),
                "RoomShade",
                ref shade,
                0f,
                1f,
                ShadeSemantic(shade),
                "RoomShade",
                true))
        {
            profile.RoomShade = RoomEnvironmentProfile.ClampUnit(shade);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("空气环境", "AIR"));
        float humidity = profile.Humidity;
        if (DrawScalarControl(
                DevToolUiSettings.T("湿度", "Humidity"),
                "Humidity",
                ref humidity,
                -1f,
                1f,
                HumiditySemantic(humidity),
                "Humidity"))
        {
            profile.Humidity = RoomEnvironmentProfile.ClampSigned(humidity);
            TemperatureSetsLoader.SetProfile(region, room.Name, profile);
        }

        DrawWarnings(TemperatureSetsLoader.Warnings);
    }

    private static void DrawRoomEnvironmentContext(string roomName, bool hasProfile, string region)
    {
        float startX = ImGui.GetCursorPosX();
        float rightX = startX + ImGui.GetContentRegionAvail().X;

        ImGui.TextUnformatted(roomName);
        ImGui.SameLine();
        ImGui.TextColored(
            hasProfile ? DirtyColor : TechnicalColor,
            hasProfile
                ? DevToolUiSettings.T("● 房间覆盖", "● Room override")
                : DevToolUiSettings.T("● 使用默认值", "● Defaults"));

        string resetLabel = DevToolUiSettings.T("恢复默认", "Reset to Defaults");
        float resetWidth = DevToolWidgets.ButtonWidth(resetLabel);
        float resetX = rightX - resetWidth;
        if (resetX > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(resetX);
        }

        if (!hasProfile) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                resetLabel,
                "WorldDataResetEnvironment",
                DevToolButtonTone.Subtle))
        {
            TemperatureSetsLoader.RemoveProfile(region, roomName);
        }
        if (!hasProfile) ImGui.EndDisabled();

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "只要修改任意参数，就会为这个房间创建覆盖；恢复默认会删除该覆盖。",
            "Changing any value creates a room override. Reset removes that override."), true);
    }

    private static bool DrawScalarControl(
        string label,
        string id,
        ref float value,
        float min,
        float max,
        string semantic,
        string technicalName,
        bool percentage = false)
    {
        ImGui.PushID(id);

        ImGui.TextUnformatted(label);
        string technical = technicalName;
        float technicalWidth = ImGui.CalcTextSize(technical).X;
        if (DevToolWidgets.SameLineIfFits(technicalWidth + 8f))
            ImGui.TextColored(TechnicalColor, technical);

        string summary = percentage
            ? Math.Round(value * 100f) + "% · " + semantic
            : value.ToString("0.###") + " · " + semantic;
        DevToolWidgets.MutedText(summary);

        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.SliderFloat("##Value", ref value, min, max, "%.3f");

        ImGui.Spacing();
        ImGui.PopID();
        return changed;
    }

    private static void DrawWeather(string region, EditorMapRoomSnapshot room)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("天气", "WEATHER"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "先决定区域里哪些天气会参与循环，再决定具体 Weather / DangerType 在区域和房间中是否允许。",
            "First decide which weather families participate in the region cycle, then control whether each concrete Weather / DangerType is allowed in the region and selected room."), true);
        ImGui.Spacing();

        if (!string.IsNullOrEmpty(WeatherSpatialRegistry.FatalLoadError))
            DrawErrorBanner(WeatherSpatialRegistry.FatalLoadError);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("区域天气调度", "REGION WEATHER SCHEDULE"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "族开关控制这一类天气是否参与区域循环；族概率控制抽到这一类的机会；子天气再决定该类中具体事件是否启用及权重。",
            "The family toggle controls whether a family participates in the region cycle. Family chance controls its roll, while each child controls its own enabled state and weight."), true);
        ImGui.Spacing();
        DrawRegionSchedule(region);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("空间规则", "SPATIAL RULES"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "区域“继承”最终使用全局默认（当前为禁止）；房间“继承”使用区域结果。FamWeather 不参与房间级空间规则。",
            "Region Inherit falls back to the global default (currently Forbidden). Room Inherit uses the region result. FamWeather is not a room-level spatial rule."), true);
        ImGui.Spacing();
        DrawSpatialRuleMatrix(region, room);

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
            float rowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
            float cardHeight = 74f + rowHeight * (family.Members.Count * 2 + 1);

            if (ImGui.BeginChild("##FamilyCard", new Num.Vector2(0f, cardHeight), ImGuiChildFlags.Borders))
            {
                DrawFamilyHeader(family, familyEnabled);

                float chance = familyChance;
                if (!familyEnabled) ImGui.BeginDisabled();
                if (DrawPercentControl(
                        DevToolUiSettings.T("区域出现概率", "Region chance"),
                        "FamilyChance",
                        ref chance))
                {
                    WeatherSpatialRegistry.SetFamilyScheduleChance(region, familyId, chance);
                }
                if (!familyEnabled) ImGui.EndDisabled();

                ImGui.Separator();
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
                    DrawSubWeatherHeader(member, subEnabled);

                    float nextChance = subChance;
                    if (!subEnabled) ImGui.BeginDisabled();
                    if (DrawPercentControl(
                            DevToolUiSettings.T("权重", "Weight"),
                            "SubChance",
                            ref nextChance))
                    {
                        WeatherSpatialRegistry.SetSubWeatherScheduleChance(
                            region,
                            member.Kind,
                            member.Id,
                            nextChance);
                    }
                    if (!subEnabled) ImGui.EndDisabled();

                    if (j < members.Count - 1) ImGui.Separator();
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            ImGui.Spacing();
            ImGui.PopID();
        }
    }

    private static void DrawFamilyHeader(WeatherSpatialFamily family, bool enabled)
    {
        float startX = ImGui.GetCursorPosX();
        float rightX = startX + ImGui.GetContentRegionAvail().X;

        ImGui.TextUnformatted(FamilyFriendlyName(family.Id));
        string technical = "FamWeather / " + family.Id;
        if (DevToolWidgets.SameLineIfFits(ImGui.CalcTextSize(technical).X + 8f, 100f))
            ImGui.TextColored(TechnicalColor, technical);

        string toggleLabel = enabled
            ? DevToolUiSettings.T("已启用", "Enabled")
            : DevToolUiSettings.T("已关闭", "Disabled");
        float toggleWidth = DevToolWidgets.ButtonWidth(toggleLabel);
        float toggleX = rightX - toggleWidth;
        if (toggleX > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(toggleX);
        }

        if (DevToolWidgets.ActionButton(
                toggleLabel,
                "FamilyToggle",
                enabled ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            WeatherSpatialRegistry.SetFamilyScheduleEnabled(
                string.Empty,
                string.Empty,
                enabled);
        }

        if (ImGui.IsItemClicked())
        {
            // The real mutation is performed below by DrawRegionSchedule, where the
            // current region/family identity is available. This branch is intentionally
            // empty so the button remains visually self-contained.
        }
    }

    private static void DrawSubWeatherHeader(WeatherSpatialMember member, bool enabled)
    {
        float startX = ImGui.GetCursorPosX();
        float rightX = startX + ImGui.GetContentRegionAvail().X;

        ImGui.TextUnformatted(WeatherFriendlyName(member.Id));
        string technical = WeatherTechnicalName(member.Kind, member.Id);
        if (DevToolWidgets.SameLineIfFits(ImGui.CalcTextSize(technical).X + 8f, 100f))
            ImGui.TextColored(TechnicalColor, technical);

        string toggleLabel = enabled
            ? DevToolUiSettings.T("启用", "On")
            : DevToolUiSettings.T("关闭", "Off");
        float toggleWidth = DevToolWidgets.ButtonWidth(toggleLabel);
        float toggleX = rightX - toggleWidth;
        if (toggleX > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(toggleX);
        }

        if (DevToolWidgets.ActionButton(
                toggleLabel,
                "SubWeatherToggle",
                enabled ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            WeatherSpatialRegistry.SetSubWeatherScheduleEnabled(
                string.Empty,
                member.Kind,
                member.Id,
                enabled);
        }
    }

    private static bool DrawPercentControl(string label, string id, ref float value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.TextDisabled(Math.Round(value) + "%");
        ImGui.SetNextItemWidth(-1f);
        return ImGui.SliderFloat("##" + id, ref value, 0f, 100f, "%.0f%%");
    }

    private static void DrawSpatialRuleMatrix(string region, EditorMapRoomSnapshot room)
    {
        IReadOnlyList<WeatherSpatialFamily> families = WeatherSpatialCatalog.AllFamilies;
        for (int familyIndex = 0; familyIndex < families.Count; familyIndex++)
        {
            WeatherSpatialFamily family = families[familyIndex];
            ImGui.TextUnformatted(FamilyFriendlyName(family.Id));
            ImGui.SameLine();
            ImGui.TextColored(TechnicalColor, "FamWeather / " + family.Id);
            ImGui.Separator();

            for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
            {
                WeatherSpatialMember member = family.Members[memberIndex];
                WeatherSpatialTarget target = new(member.Kind, member.Id, member.Id);
                DrawSpatialRuleRow(region, room, target);
                if (memberIndex < family.Members.Count - 1) ImGui.Spacing();
            }

            if (familyIndex < families.Count - 1)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }
    }

    private static void DrawSpatialRuleRow(
        string region,
        EditorMapRoomSnapshot room,
        WeatherSpatialTarget target)
    {
        ImGui.PushID("Spatial_" + target.Key);
        ImGui.TextUnformatted(WeatherFriendlyName(target.WeatherId));
        string technical = WeatherTechnicalName(target.Kind, target.WeatherId);
        if (DevToolWidgets.SameLineIfFits(ImGui.CalcTextSize(technical).X + 8f))
            ImGui.TextColored(TechnicalColor, technical);

        WeatherSpatialRule regionRule = WeatherSpatialRegistry.GetDefaultRule(region, target);
        if (DrawRuleEditor(
                DevToolUiSettings.T("区域默认", "Region default"),
                "RegionRule",
                ref regionRule,
                RegionInheritanceHint(regionRule)))
        {
            WeatherSpatialRegistry.SetDefaultRule(region, target, regionRule);
        }

        if (room == null)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "选择房间后可编辑房间覆盖。",
                "Select a room to edit its override."));
        }
        else
        {
            WeatherSpatialRule roomRule = WeatherSpatialRegistry.GetRoomRule(region, room.Name, target);
            WeatherSpatialRule effectiveRegion = regionRule == WeatherSpatialRule.Inherit
                ? WeatherSpatialRule.Deny
                : regionRule;
            if (DrawRuleEditor(
                    DevToolUiSettings.T("房间 · ", "Room · ") + room.Name,
                    "RoomRule",
                    ref roomRule,
                    RoomInheritanceHint(roomRule, effectiveRegion)))
            {
                WeatherSpatialRegistry.SetRoomRule(region, room.Name, target, roomRule);
            }
        }

        ImGui.PopID();
    }

    private static bool DrawRuleEditor(
        string contextLabel,
        string id,
        ref WeatherSpatialRule rule,
        string inheritHint)
    {
        DevToolWidgets.MutedText(contextLabel);
        bool sameLine = DevToolWidgets.SameLineIfFits(245f);
        if (!sameLine) ImGui.Indent(14f);

        bool changed = DrawRuleSegments(id, ref rule);

        if (!sameLine) ImGui.Unindent(14f);
        if (rule == WeatherSpatialRule.Inherit && !string.IsNullOrEmpty(inheritHint))
            DevToolWidgets.MutedText(inheritHint, true);
        return changed;
    }

    private static bool DrawRuleSegments(string id, ref WeatherSpatialRule rule)
    {
        bool changed = false;
        WeatherSpatialRule[] values =
        {
            WeatherSpatialRule.Inherit,
            WeatherSpatialRule.Allow,
            WeatherSpatialRule.Deny
        };

        ImGui.PushID(id);
        for (int i = 0; i < values.Length; i++)
        {
            WeatherSpatialRule candidate = values[i];
            bool selected = rule == candidate;
            DevToolButtonTone tone = selected
                ? candidate switch
                {
                    WeatherSpatialRule.Allow => DevToolButtonTone.Primary,
                    WeatherSpatialRule.Deny => DevToolButtonTone.Danger,
                    _ => DevToolButtonTone.Normal
                }
                : DevToolButtonTone.Subtle;

            if (DevToolWidgets.ActionButton(
                    RuleLabel(candidate),
                    "Rule_" + candidate,
                    tone))
            {
                rule = candidate;
                changed = true;
            }

            if (i < values.Length - 1) ImGui.SameLine();
        }
        ImGui.PopID();
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

    private static string RegionInheritanceHint(WeatherSpatialRule rule)
    {
        return rule == WeatherSpatialRule.Inherit
            ? DevToolUiSettings.T("继承 → 全局默认：禁止", "Inherit → global default: Forbidden")
            : string.Empty;
    }

    private static string RoomInheritanceHint(
        WeatherSpatialRule roomRule,
        WeatherSpatialRule effectiveRegion)
    {
        if (roomRule != WeatherSpatialRule.Inherit) return string.Empty;
        string effective = effectiveRegion == WeatherSpatialRule.Allow
            ? DevToolUiSettings.T("允许", "Allow")
            : DevToolUiSettings.T("禁止", "Forbidden");
        return DevToolUiSettings.T("继承 → 区域结果：", "Inherit → region result: ") + effective;
    }

    private static string FamilyFriendlyName(string id)
    {
        return (id ?? string.Empty) switch
        {
            "Rain" => DevToolUiSettings.T("雨系天气", "Rain"),
            "Fog" => DevToolUiSettings.T("雾系天气", "Fog"),
            "Heat" => DevToolUiSettings.T("热浪天气", "Heat"),
            "Sand" => DevToolUiSettings.T("沙尘天气", "Sand"),
            _ => id ?? string.Empty
        };
    }

    private static string WeatherFriendlyName(string id)
    {
        return (id ?? string.Empty) switch
        {
            "LightRain" => DevToolUiSettings.T("小雨", "Light Rain"),
            "HeavyRain" => DevToolUiSettings.T("大雨", "Heavy Rain"),
            "DeathRain" => DevToolUiSettings.T("致命雨", "Death Rain"),
            "Fog" => DevToolUiSettings.T("雾", "Fog"),
            "DenseFog" => DevToolUiSettings.T("浓雾", "Dense Fog"),
            "HeatWave" => DevToolUiSettings.T("热浪", "Heat Wave"),
            "IntenseHeat" => DevToolUiSettings.T("极端高温", "Intense Heat"),
            "SandStorm" => DevToolUiSettings.T("沙尘暴", "Sand Storm"),
            "DeathSandStorm" => DevToolUiSettings.T("致命沙暴", "Death Sandstorm"),
            _ => id ?? string.Empty
        };
    }

    private static string WeatherTechnicalName(object kind, string id)
    {
        return (kind?.ToString()?.Equals("DangerType", StringComparison.OrdinalIgnoreCase) == true
            ? "DangerType / "
            : "Weather / ") + id;
    }

    private static string HeatSemantic(float value)
    {
        if (value <= -0.65f) return DevToolUiSettings.T("寒冷", "Cold");
        if (value <= -0.20f) return DevToolUiSettings.T("偏冷", "Cool");
        if (value < 0.20f) return DevToolUiSettings.T("中性", "Neutral");
        if (value < 0.65f) return DevToolUiSettings.T("偏热", "Warm");
        return DevToolUiSettings.T("炎热", "Hot");
    }

    private static string SunlightSemantic(float value)
    {
        if (value <= 0.05f) return DevToolUiSettings.T("无直射日照", "No direct sun");
        if (value <= 0.33f) return DevToolUiSettings.T("弱日照", "Low sunlight");
        if (value <= 0.70f) return DevToolUiSettings.T("明显日照", "Strong sunlight");
        return DevToolUiSettings.T("高暴露", "High exposure");
    }

    private static string ShadeSemantic(float value)
    {
        if (value <= 0.05f) return DevToolUiSettings.T("无遮挡", "Open");
        if (value <= 0.35f) return DevToolUiSettings.T("轻度遮阴", "Light shade");
        if (value <= 0.70f) return DevToolUiSettings.T("遮阴", "Shaded");
        return DevToolUiSettings.T("深度遮阴", "Deep shade");
    }

    private static string HumiditySemantic(float value)
    {
        if (value <= -0.65f) return DevToolUiSettings.T("极干", "Very dry");
        if (value <= -0.20f) return DevToolUiSettings.T("干燥", "Dry");
        if (value < 0.20f) return DevToolUiSettings.T("中性", "Neutral");
        if (value < 0.65f) return DevToolUiSettings.T("潮湿", "Humid");
        return DevToolUiSettings.T("高湿", "Very humid");
    }

    private static void DrawWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings == null || warnings.Count == 0) return;

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("数据警告", "DATA WARNINGS"));
        for (int i = 0; i < warnings.Count; i++)
            DevToolWidgets.MutedText("• " + warnings[i], true);
    }

    private static void DrawErrorBanner(string message)
    {
        ImGui.TextColored(ErrorColor, DevToolUiSettings.T("加载异常", "Load error"));
        DevToolWidgets.MutedText(message, true);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawEmptyState(string title, string detail)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        DevToolWidgets.MutedText(detail, true);
        ImGui.Spacing();
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
