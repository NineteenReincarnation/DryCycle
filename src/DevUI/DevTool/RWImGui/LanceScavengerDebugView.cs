using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Debug;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Dedicated New-UI page for live LanceScavenger combat diagnostics. The view never reaches into
/// live Rain World objects; it only consumes detached snapshots published by DryCycle.dll.
/// </summary>
internal static class LanceScavengerDebugView
{
    private static int selectedSpawner = int.MinValue;
    private static int selectedNumber = int.MinValue;

    private static readonly Num.Vector4 Good = new(0.42f, 0.84f, 0.56f, 1f);
    private static readonly Num.Vector4 Warning = new(0.96f, 0.72f, 0.28f, 1f);
    private static readonly Num.Vector4 Bad = new(0.95f, 0.38f, 0.38f, 1f);
    private static readonly Num.Vector4 Accent = new(0.48f, 0.72f, 1.00f, 1f);
    private static readonly Num.Vector4 Muted = new(0.68f, 0.72f, 0.78f, 1f);
    private static readonly Num.Vector4 GraphBg = new(0.025f, 0.040f, 0.060f, 0.82f);
    private static readonly Num.Vector4 GraphBorder = new(0.24f, 0.34f, 0.46f, 0.95f);
    private static readonly Num.Vector4 Threshold = new(0.95f, 0.74f, 0.32f, 0.90f);
    private static readonly Num.Vector4 QualityLine = new(0.46f, 0.76f, 1.00f, 1f);
    private static readonly Num.Vector4 ReadyDot = new(0.45f, 0.90f, 0.58f, 1f);
    private static readonly Num.Vector4 HardBlockMark = new(0.96f, 0.34f, 0.34f, 0.55f);
    private static readonly Num.Vector4 BraceMark = new(0.35f, 0.58f, 0.94f, 0.16f);

    internal static void Draw(EditorPresentationSnapshot editor, Num.Vector2 display)
    {
        string room = string.IsNullOrEmpty(editor.RoomName) ? editor.Document : editor.RoomName;
        LanceScavengerDebugPresentationHub.SetRequested(true, room);
        LanceScavengerDebugSnapshot snapshot = LanceScavengerDebugPresentationHub.Current;

        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        float leftRail = Math.Min(400f, Math.Max(190f, 230f * Math.Min(1.35f, scale)));
        float width = Math.Min(Math.Max(760f, display.X - 250f), Math.Max(560f, display.X - 32f));
        float height = Math.Min(Math.Max(500f, display.Y - 190f), Math.Max(360f, display.Y - 32f));
        Num.Vector2 pos = new(
            Math.Max(176f, (display.X - width) * 0.56f),
            Math.Max(118f, (display.Y - height) * 0.56f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(620f, Math.Max(420f, display.X - 32f)), 360f),
            new Num.Vector2(Math.Max(620f, display.X - 16f), Math.Max(360f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("长枪拾荒者调试###LanceScavengerDebug", "Lance Scavenger Debug###LanceScavengerDebug"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("LanceScavengerDebug");
        DrawHeader(snapshot, room);
        ImGui.Separator();
        ImGui.Spacing();

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float rosterWidth = Math.Min(leftRail, Math.Max(190f, available.X * 0.30f));
        if (ImGui.BeginChild("##LanceDebugRoster", new Num.Vector2(rosterWidth, available.Y), ImGuiChildFlags.Borders))
        {
            ImGui.SetWindowFontScale(1.16f);
            DevToolWidgets.PaneTitle(DevToolUiSettings.T("个体", "LANCERS"), 1.16f);
            DrawRoster(snapshot);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##LanceDebugDetails", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
        {
            ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.14f);
            LanceScavengerDebugEntrySnapshot selected = ResolveSelected(snapshot);
            if (selected == null)
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("实时诊断", "LIVE DIAGNOSTICS"));
                ImGui.TextWrapped(DevToolUiSettings.T(
                    "当前房间没有已实现的长枪拾荒者，或调试采集刚刚开启。进入房间后通常下一帧就会出现。",
                    "No realized Lance Scavenger is currently publishing in this room, or capture has just started. Data normally appears on the next simulation frame."));
            }
            else
            {
                DrawDetails(selected);
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }

    internal static void StopCapture()
    {
        LanceScavengerDebugPresentationHub.SetRequested(false, string.Empty);
        selectedSpawner = int.MinValue;
        selectedNumber = int.MinValue;
    }

    private static void DrawHeader(LanceScavengerDebugSnapshot snapshot, string room)
    {
        int count = snapshot.Entries?.Length ?? 0;
        ImGui.TextColored(Accent, DevToolUiSettings.T("长枪拾荒者战斗诊断", "Lance Scavenger Combat Diagnostics"));
        ImGui.SameLine();
        DevToolWidgets.MutedText("· " + room + " · " + count + DevToolUiSettings.T(" 个体", " units"));
        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "只在本页打开时采样；关闭页面后自动停采，不给正常游戏增加持续调试开销。",
            "Sampling is leased only while this page is visible; capture stops automatically when the page closes."));
    }

    private static void DrawRoster(LanceScavengerDebugSnapshot snapshot)
    {
        LanceScavengerDebugEntrySnapshot[] entries = snapshot.Entries ?? Array.Empty<LanceScavengerDebugEntrySnapshot>();
        if (entries.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("等待长枪拾荒者数据…", "Waiting for Lance Scavenger data..."));
            return;
        }

        if (ResolveSelected(snapshot) == null)
        {
            selectedSpawner = entries[0].Spawner;
            selectedNumber = entries[0].Number;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            LanceScavengerDebugEntrySnapshot entry = entries[i];
            bool selected = entry.Spawner == selectedSpawner && entry.Number == selectedNumber;
            string title = "#" + entry.Number + "  " + LocalizeState(entry.State) + "##LanceDebugEntity" + entry.Spawner + ":" + entry.Number;
            if (ImGui.Selectable(title, selected))
            {
                selectedSpawner = entry.Spawner;
                selectedNumber = entry.Number;
            }

            ImGui.Indent(10f);
            ImGui.TextColored(ReasonColor(entry), LocalizeReason(entry.DecisionReason));
            if (!string.IsNullOrEmpty(entry.Target))
            {
                ImGui.TextWrapped(DevToolUiSettings.T("目标：", "Target: ") + entry.Target);
                ImGui.TextDisabled(DevToolUiSettings.T("距离 ", "Distance ") + entry.Distance.ToString("0.0") + " px");
            }
            ImGui.Unindent(10f);
            if (i + 1 < entries.Length)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }
    }

    private static LanceScavengerDebugEntrySnapshot ResolveSelected(LanceScavengerDebugSnapshot snapshot)
    {
        LanceScavengerDebugEntrySnapshot[] entries = snapshot.Entries ?? Array.Empty<LanceScavengerDebugEntrySnapshot>();
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Spawner == selectedSpawner && entries[i].Number == selectedNumber)
                return entries[i];
        return null;
    }

    private static void DrawDetails(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("实时诊断", "LIVE DIAGNOSTICS"));
        ImGui.TextUnformatted(entry.EntityId);
        ImGui.SameLine();
        ImGui.TextColored(ReasonColor(entry), LocalizeReason(entry.DecisionReason));

        DrawDecision(entry);
        DrawAim(entry);
        DrawAimHistory(entry);
        DrawSafety(entry);
        DrawTargetMotion(entry);
        DrawCounterSweep(entry);
    }

    private static void DrawDecision(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("决策", "DECISION"));
        KeyValue(DevToolUiSettings.T("状态", "State"), LocalizeState(entry.State) + "  ·  " + entry.StateAge + "f");
        KeyValue(DevToolUiSettings.T("行为", "Behavior"), entry.Behavior);
        KeyValue(DevToolUiSettings.T("暴力等级", "Violence"), entry.Violence + (entry.Afraid ? DevToolUiSettings.T(" · 恐惧", " · Afraid") : string.Empty));
        KeyValue(DevToolUiSettings.T("目标", "Target"), string.IsNullOrEmpty(entry.Target) ? "-" : entry.Target);
        KeyValue(DevToolUiSettings.T("距离", "Distance"), entry.Distance.ToString("0.0") + " px");
        KeyValue(DevToolUiSettings.T("冷却", "Cooldown"), entry.Cooldown + "f");
        KeyValue(DevToolUiSettings.T("冲锋优先权", "Charge priority"), YesNo(entry.ChargePriority));
        KeyValue(DevToolUiSettings.T("当前结论", "Decision"), LocalizeReason(entry.DecisionReason), ReasonColor(entry));
    }

    private static void DrawAim(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("瞄准解", "AIM SOLUTION"));

        float fraction = entry.AimThreshold <= 0f ? 0f : Math.Min(1f, entry.AimQuality / entry.AimThreshold);
        DrawMeter(
            DevToolUiSettings.T("当前质量", "Current quality"),
            entry.AimQuality,
            entry.AimThreshold,
            fraction,
            entry.AimReady ? Good : Warning);

        string bestAge = entry.BestAimAge == int.MaxValue ? "-" : entry.BestAimAge + "f";
        KeyValue(DevToolUiSettings.T("38帧最佳质量", "Best in 38f"), entry.BestAimReady ? entry.BestAimQuality.ToString("0.000") : "-");
        KeyValue(DevToolUiSettings.T("最佳解年龄", "Best age"), bestAge);
        KeyValue(DevToolUiSettings.T("当前 BodyChunk", "Current BodyChunk"), Chunk(entry.TargetChunkIndex));
        KeyValue(DevToolUiSettings.T("最佳 BodyChunk", "Best BodyChunk"), Chunk(entry.BestTargetChunkIndex));
        KeyValue(DevToolUiSettings.T("预计命中帧", "Impact frame"), entry.ImpactFrame > 0 ? entry.ImpactFrame + "f" : "-");
        KeyValue(DevToolUiSettings.T("枪角", "Lance pitch"), entry.LancePitchDegrees.ToString("+0.0;-0.0;0.0") + "°");
        KeyValue(DevToolUiSettings.T("精确解", "Exact solution"), YesNo(entry.ExactAim));
        KeyValue(DevToolUiSettings.T("瞄准点", "Aim point"), "(" + entry.AimX.ToString("0.0") + ", " + entry.AimY.ToString("0.0") + ")");
    }

    private static void DrawAimHistory(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("最近38帧", "LAST 38 FRAMES"));
        float[] quality = entry.AimQualityHistory ?? Array.Empty<float>();
        bool[] ready = entry.AimReadyHistory ?? Array.Empty<bool>();
        bool[] hard = entry.HardBlockHistory ?? Array.Empty<bool>();
        bool[] brace = entry.BraceHistory ?? Array.Empty<bool>();

        Num.Vector2 origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(260f, ImGui.GetContentRegionAvail().X);
        float height = 154f;
        Num.Vector2 size = new(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint bg = ImGui.GetColorU32(GraphBg);
        uint border = ImGui.GetColorU32(GraphBorder);
        draw.AddRectFilled(origin, origin + size, bg, 4f);
        draw.AddRect(origin, origin + size, border, 4f);

        float padX = 12f;
        float padY = 12f;
        float plotLeft = origin.X + padX;
        float plotRight = origin.X + width - padX;
        float plotTop = origin.Y + padY;
        float plotBottom = origin.Y + height - 26f;
        float thresholdY = plotBottom - (plotBottom - plotTop) * Math.Min(1f, Math.Max(0f, entry.AimThreshold));
        draw.AddLine(new Num.Vector2(plotLeft, thresholdY), new Num.Vector2(plotRight, thresholdY), ImGui.GetColorU32(Threshold), 1.5f);

        int count = quality.Length;
        if (count > 0)
        {
            float step = count <= 1 ? 0f : (plotRight - plotLeft) / (count - 1);
            for (int i = 0; i < count; i++)
            {
                float x = count <= 1 ? plotRight : plotLeft + step * i;
                float q = Math.Min(1f, Math.Max(0f, quality[i]));
                float y = plotBottom - (plotBottom - plotTop) * q;

                if (i < brace.Length && brace[i])
                    draw.AddRectFilled(new Num.Vector2(x - Math.Max(1f, step * 0.45f), plotTop),
                        new Num.Vector2(x + Math.Max(1f, step * 0.45f), plotBottom), ImGui.GetColorU32(BraceMark));
                if (i < hard.Length && hard[i])
                    draw.AddRectFilled(new Num.Vector2(x - 1.5f, plotTop), new Num.Vector2(x + 1.5f, plotBottom), ImGui.GetColorU32(HardBlockMark));

                if (i > 0)
                {
                    float previousQ = Math.Min(1f, Math.Max(0f, quality[i - 1]));
                    float previousX = count <= 1 ? plotRight : plotLeft + step * (i - 1);
                    float previousY = plotBottom - (plotBottom - plotTop) * previousQ;
                    draw.AddLine(new Num.Vector2(previousX, previousY), new Num.Vector2(x, y), ImGui.GetColorU32(QualityLine), 2f);
                }

                if (i < ready.Length && ready[i])
                    draw.AddCircleFilled(new Num.Vector2(x, y), 3f, ImGui.GetColorU32(ReadyDot));
            }
        }

        draw.AddText(new Num.Vector2(plotLeft, plotBottom + 5f), ImGui.GetColorU32(Muted),
            DevToolUiSettings.T("旧", "old"));
        string newest = DevToolUiSettings.T("当前", "now");
        float newestWidth = ImGui.CalcTextSize(newest).X;
        draw.AddText(new Num.Vector2(plotRight - newestWidth, plotBottom + 5f), ImGui.GetColorU32(Muted), newest);
        string threshold = DevToolUiSettings.T("阈值 ", "threshold ") + entry.AimThreshold.ToString("0.00");
        draw.AddText(new Num.Vector2(plotLeft + 4f, Math.Max(plotTop, thresholdY - ImGui.GetTextLineHeight())), ImGui.GetColorU32(Threshold), threshold);
        ImGui.Dummy(size);

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "蓝色带 = 架枪帧 · 绿点 = 当帧有可提交瞄准解 · 红柱 = 硬阻断",
            "Blue band = brace frame · green dot = commit-capable aim · red mark = hard block"));
    }

    private static void DrawSafety(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("路径与提交", "PATH & COMMIT"));
        KeyValue(DevToolUiSettings.T("路径清晰", "Path clear"), YesNo(entry.PathClear), entry.PathClear ? Good : Bad);
        KeyValue(DevToolUiSettings.T("路径原因", "Lane reason"), LocalizeLane(entry.LaneReason));
        KeyValue(DevToolUiSettings.T("友军阻挡", "Friend blocked"), YesNo(entry.FriendBlocked), entry.FriendBlocked ? Bad : Good);
        KeyValue(DevToolUiSettings.T("硬阻断", "Hard blocked"), YesNo(entry.HardBlocked), entry.HardBlocked ? Bad : Good);
        KeyValue(DevToolUiSettings.T("38帧提交已满足", "Commit ready"), YesNo(entry.CommitReady), entry.CommitReady ? Good : Warning);
        KeyValue(DevToolUiSettings.T("当前冲锋机会", "Charge opportunity"), YesNo(entry.ChargeOpportunity), entry.ChargeOpportunity ? Good : Muted);
    }

    private static void DrawTargetMotion(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("目标运动", "TARGET MOTION"));
        KeyValue(DevToolUiSettings.T("稳定度", "Stability"), entry.TargetStability.ToString("0.000"));
        KeyValue(DevToolUiSettings.T("闪避严重度", "Dodge severity"), entry.DodgeSeverity.ToString("0.000"));
        KeyValue(DevToolUiSettings.T("平滑速度", "Smoothed velocity"),
            "(" + entry.TargetVelocityX.ToString("+0.00;-0.00;0.00") + ", " + entry.TargetVelocityY.ToString("+0.00;-0.00;0.00") + ")");
    }

    private static void DrawCounterSweep(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("冲锋反扫", "COUNTER SWEEP"));
        KeyValue(DevToolUiSettings.T("已尝试", "Attempted"), YesNo(entry.CounterSweepAttempted));
        KeyValue(DevToolUiSettings.T("正在反扫", "Active"), YesNo(entry.CounterSweepActive), entry.CounterSweepActive ? Good : Muted);
        KeyValue(DevToolUiSettings.T("触发概率", "Chance"), (entry.CounterSweepChance * 100f).ToString("0.0") + "%");
    }

    private static void DrawMeter(string label, float value, float threshold, float fraction, Num.Vector4 color)
    {
        ImGui.TextUnformatted(label);
        Num.Vector2 origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(180f, ImGui.GetContentRegionAvail().X);
        float height = 19f;
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Num.Vector2(width, height), ImGui.GetColorU32(GraphBg), 3f);
        draw.AddRectFilled(origin, origin + new Num.Vector2(width * Math.Min(1f, Math.Max(0f, fraction)), height), ImGui.GetColorU32(color), 3f);
        draw.AddRect(origin, origin + new Num.Vector2(width, height), ImGui.GetColorU32(GraphBorder), 3f);
        ImGui.Dummy(new Num.Vector2(width, height));
        ImGui.SameLine();
        ImGui.TextUnformatted(value.ToString("0.000") + " / " + threshold.ToString("0.000"));
    }

    private static void KeyValue(string key, string value)
    {
        KeyValue(key, value, new Num.Vector4(1f, 1f, 1f, 1f));
    }

    private static void KeyValue(string key, string value, Num.Vector4 color)
    {
        DevToolWidgets.MutedText(key);
        ImGui.SameLine(168f);
        ImGui.TextColored(color, string.IsNullOrEmpty(value) ? "-" : value);
    }

    private static string Chunk(int index) => index < 0 ? "-" : "#" + index;

    private static string YesNo(bool value) => value
        ? DevToolUiSettings.T("是", "Yes")
        : DevToolUiSettings.T("否", "No");

    private static Num.Vector4 ReasonColor(LanceScavengerDebugEntrySnapshot entry)
    {
        if (entry.HardBlocked || string.Equals(entry.DecisionReason, "inactive", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "no lance", StringComparison.Ordinal)) return Bad;
        if (entry.ChargeOpportunity || string.Equals(entry.DecisionReason, "brace ready", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "charging", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "counter sweep", StringComparison.Ordinal)) return Good;
        return Warning;
    }

    private static string LocalizeState(string value)
    {
        if (!DevToolUiSettings.IsChinese) return value;
        return value switch
        {
            "Observe" => "观察",
            "Threaten" => "威慑",
            "CreateDistance" => "拉开距离",
            "AcquireChargeLane" => "寻找冲锋线",
            "Backstep" => "后撤",
            "Brace" => "架枪",
            "Charge" => "跳冲",
            "FollowUpThrow" => "后续投矛",
            "Recover" => "恢复",
            "CloseDefense" => "近身防御",
            "Disarmed" => "失去长枪",
            _ => value
        };
    }

    private static string LocalizeLane(string value)
    {
        if (!DevToolUiSettings.IsChinese) return string.IsNullOrEmpty(value) ? "-" : value;
        return value switch
        {
            "no target" => "无目标",
            "distance" => "距离不满足",
            "wall / ceiling" => "墙体 / 天花板阻挡",
            "lance blocked" => "枪身轨迹被阻挡",
            "friend in lane" => "友军位于冲锋路径",
            "clear" => "路径清晰且瞄准就绪",
            "aim low" => "路径清晰，但瞄准质量不足",
            "no aim opportunity" => "路径清晰，但当前没有瞄准解",
            _ => string.IsNullOrEmpty(value) ? "-" : value
        };
    }

    private static string LocalizeReason(string value)
    {
        if (!DevToolUiSettings.IsChinese) return string.IsNullOrEmpty(value) ? "-" : value;
        return value switch
        {
            "inactive" => "个体当前不可战斗",
            "no lance" => "没有长枪",
            "no target" => "没有有效目标",
            "not lethal" => "原版敌意尚未达到致命等级",
            "yielding priority" => "正在让出同目标冲锋优先权",
            "too close" => "目标过近",
            "too far" => "目标超出个体冲锋距离",
            "friend in lane" => "友军挡住冲锋线",
            "wall / ceiling" => "身体轨迹被地形阻挡",
            "lance blocked" => "长枪轨迹被地形阻挡",
            "distance" => "距离条件不满足",
            "hard block" => "存在硬阻断",
            "cooldown" => "冲锋冷却中",
            "counter sweep" => "冲锋中，正在反扫",
            "charging" => "正在跳冲",
            "brace ready" => "架枪期间已有可提交瞄准解",
            "aim quality" => "瞄准质量尚未达到阈值",
            "no aim opportunity" => "当前没有可用瞄准解",
            "path blocked" => "冲锋路径被阻挡",
            "ready" => "当前具备冲锋条件",
            "waiting state" => "瞄准与路径可用，等待状态机进入冲锋流程",
            _ => string.IsNullOrEmpty(value) ? "-" : value
        };
    }
}
