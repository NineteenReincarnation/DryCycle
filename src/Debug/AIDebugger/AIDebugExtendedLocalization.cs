using System.Collections.Generic;

namespace DryCycle.Debugging.AI;

internal static class AIDebugExtendedLocalization
{
    private readonly struct Pair
    {
        internal readonly string English;
        internal readonly string Chinese;
        internal Pair(string english, string chinese) { English = english; Chinese = chinese; }
    }

    private static readonly Dictionary<string, Pair> Text = new()
    {
        ["window.browser"] = new("Entity Browser", "实体浏览器"),
        ["window.decision"] = new("Decision Stack", "决策栈"),
        ["window.inspector"] = new("Inspector", "检查器"),
        ["window.timeline"] = new("Timeline", "时间线"),
        ["window.events"] = new("Events", "事件日志"),
        ["window.utility"] = new("Utility", "效用比较"),
        ["window.perception"] = new("Perception / Tracker", "感知 / Tracker"),
        ["window.path"] = new("Path / Control", "路径 / 控制链"),
        ["window.compare"] = new("Compare", "对比"),
        ["window.candidates"] = new("Candidates", "候选项"),
        ["window.captures"] = new("Captures / Breakpoints", "捕获 / 断点"),
        ["window.settings"] = new("Settings", "设置"),
        ["toolbar.live"] = new("LIVE", "实时"),
        ["toolbar.interact"] = new("INTERACT", "交互"),
        ["toolbar.pause"] = new("Pause World", "暂停世界"),
        ["toolbar.resume"] = new("Resume World", "继续世界"),
        ["toolbar.step"] = new("Step 1 Tick", "前进 1 Tick"),
        ["toolbar.freeze"] = new("Freeze View", "冻结视图"),
        ["toolbar.unfreeze"] = new("Unfreeze", "解除冻结"),
        ["toolbar.reset_layout"] = new("Reset Layout", "重置布局"),
        ["toolbar.save_layout"] = new("Save Layout", "保存布局"),
        ["browser.search"] = new("Search", "搜索"),
        ["browser.help"] = new("Click selects A; Shift+Click selects comparison B; Alt+click selects in the world.", "点击选择 A；Shift+点击选择对比 B；Alt+点击世界中的生物。"),
        ["common.none"] = new("None", "无"),
        ["common.no_selection"] = new("No creature selected", "未选择生物"),
        ["common.clear"] = new("Clear", "清空"),
        ["common.export"] = new("Export", "导出"),
        ["common.enabled"] = new("Enabled", "启用"),
        ["timeline.live"] = new("Return to live", "返回实时"),
        ["timeline.history"] = new("Full historical diagnostic state", "完整历史诊断状态"),
        ["timeline.no_data"] = new("No history yet. Keep the entity selected or pinned.", "暂无历史数据。保持实体被选中或固定即可记录。"),
        ["events.filter"] = new("Filter", "筛选"),
        ["events.no_data"] = new("No events recorded.", "暂无事件记录。"),
        ["utility.no_data"] = new("No UtilityComparer or custom utility adapter is available.", "没有可用的 UtilityComparer 或自定义效用适配器。"),
        ["perception.no_data"] = new("No Tracker creature representations are available.", "没有可用的 Tracker 生物表示。"),
        ["path.intent"] = new("INTENT", "意图"),
        ["path.planner"] = new("PLANNER", "规划"),
        ["path.motor"] = new("MOTOR", "运动执行"),
        ["compare.help"] = new("Shift+click an entity to select comparison B.", "Shift+点击实体以选择对比对象 B。"),
        ["candidates.no_data"] = new("No instrumented candidates were produced in the latest decision pass.", "最近一次决策没有产生已插桩的候选项。"),
        ["capture.trigger"] = new("Manual Capture", "手动捕获"),
        ["capture.reason"] = new("Reason", "原因"),
        ["capture.completed"] = new("Completed captures", "已完成捕获"),
        ["capture.last_export"] = new("Last export", "最近导出"),
        ["breakpoint.add"] = new("Add Breakpoint", "添加断点"),
        ["breakpoint.name"] = new("Event name contains", "事件名包含"),
        ["breakpoint.last_hit"] = new("Last breakpoint hit", "最近命中断点"),
        ["settings.language"] = new("Language", "语言"),
        ["settings.ui_scale"] = new("UI scale", "UI 缩放"),
        ["settings.font_scale"] = new("Font scale", "字体缩放"),
        ["settings.opacity"] = new("Window opacity", "窗口透明度"),
        ["settings.auto_open"] = new("Open Observatory on game start", "进入游戏时自动打开观测器"),
        ["settings.history"] = new("History length (seconds)", "历史长度（秒）"),
        ["settings.raw"] = new("Show raw implementation names", "显示原始实现名称"),
        ["settings.age"] = new("Show data age/source", "显示数据年龄/来源"),
        ["settings.ids"] = new("Show entity IDs", "显示实体 ID"),
        ["settings.overlay"] = new("World overlay", "世界叠加"),
        ["settings.physics"] = new("Physics", "物理"),
        ["settings.movement"] = new("Movement", "运动"),
        ["settings.path"] = new("Path", "路径"),
        ["settings.aimap"] = new("AImap heatmap", "AImap 热图"),
        ["settings.perception"] = new("Perception", "感知"),
        ["settings.social"] = new("Social / role", "社交 / 角色"),
        ["settings.combat"] = new("Combat", "战斗"),
        ["settings.labels"] = new("Labels", "标签"),
        ["settings.full_history"] = new("Record full historical snapshots", "记录完整历史快照"),
        ["settings.trigger_capture"] = new("Trigger capture", "触发捕获"),
        ["settings.anomaly"] = new("Automatic anomaly detection", "自动异常检测"),
        ["settings.break_pause"] = new("Breakpoint pauses world", "断点命中时暂停世界"),
        ["settings.save"] = new("Save Settings", "保存设置"),
        ["settings.reset"] = new("Reset Defaults", "恢复默认"),
        ["profile.capture"] = new("Capture", "采样"),
        ["profile.ui"] = new("UI", "界面"),
        ["profile.overlay"] = new("Overlay", "叠加"),
        ["profile.timeline"] = new("Timeline", "时间线"),
        ["profile.utility"] = new("Utility", "效用"),
        ["profile.perception"] = new("Perception", "感知"),
        ["profile.aimap"] = new("AImap", "AImap"),
        ["status.input_gate"] = new("Input gate", "输入隔离"),
        ["status.world_step"] = new("World step", "世界步进"),
        ["status.frozen"] = new("FROZEN", "已冻结"),
        ["status.paused"] = new("WORLD PAUSED", "世界已暂停")
    };

    internal static string T(string key)
    {
        if (!Text.TryGetValue(key, out Pair pair)) return key ?? "?";
        return AIDebugLocalization.Language == AIDebugLanguage.Chinese ? pair.Chinese : pair.English;
    }

    internal static string EventName(string raw) => raw switch
    {
        "RoleEntered" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "角色进入" : "Role Entered",
        "RoleExit" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "角色退出" : "Role Exit",
        "RoleEvaluation" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "角色评估" : "Role Evaluation",
        "RoleEvaluationBlocked" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "角色评估被阻止" : "Role Evaluation Blocked",
        "RoleSustain" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "角色维持" : "Role Sustain",
        "SentinelAlarm" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "哨兵警报" : "Sentinel Alarm",
        "OpportunistEarlyReturn" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "机会主义提前返回" : "Opportunist Early Return",
        "StateOscillation" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "状态振荡" : "State Oscillation",
        "TargetThrashing" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "目标抖动" : "Target Thrashing",
        "PossibleStuck" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "可能卡住" : "Possible Stuck",
        "VelocitySpike" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "速度异常峰值" : "Velocity Spike",
        "InvalidNumber" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "非法数值" : "Invalid Number",
        "AttackSlotsViolation" => AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "攻击槽超限" : "Attack Slots Violation",
        _ => raw ?? "?"
    };
}
