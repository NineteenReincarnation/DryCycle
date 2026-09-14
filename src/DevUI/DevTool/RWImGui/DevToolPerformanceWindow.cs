using System;
using System.Globalization;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Read-only diagnostics surface for DevToolPerformanceMonitor. The expensive percentile readback
/// happens here, on demand, rather than in the measured update path. Closing the DevTool does not
/// disable monitoring, which intentionally allows the next O/H activation frames to be captured.
/// </summary>
internal static class DevToolPerformanceWindow
{
    private const float LastColumn = 286f;
    private const float AverageColumn = 374f;
    private const float P95Column = 462f;
    private const float MaxColumn = 550f;

    internal static void Draw(Num.Vector2 display)
    {
        if (!DevToolPerformanceMonitor.Enabled)
            return;

        float maxWidth = Math.Max(520f, display.X - 16f);
        float width = Math.Min(650f, maxWidth);
        float height = Math.Min(590f, Math.Max(320f, display.Y - 32f));

        ImGui.SetNextWindowPos(new Num.Vector2(8f, Math.Max(8f, display.Y - height - 8f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(520f, maxWidth), 260f),
            new Num.Vector2(maxWidth, Math.Max(280f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("性能监控###DevToolPerformance", "Performance###DevToolPerformance"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Performance");

        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                $"最近 {DevToolPerformanceMonitor.RollingSampleCapacity} 个样本的滚动统计；P95 仅在本窗口读取时计算。",
                $"Rolling {DevToolPerformanceMonitor.RollingSampleCapacity}-sample window; P95 is computed only when this window reads it."));

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重置样本", "Reset Samples"),
                "PerformanceReset",
                DevToolButtonTone.Subtle))
            DevToolPerformanceMonitor.Reset();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("停止监控", "Stop Monitoring"),
                "PerformanceStop",
                DevToolButtonTone.Normal))
        {
            DevToolPerformanceMonitor.SetEnabled(false);
            ImGui.End();
            return;
        }

        ImGui.Spacing();
        DrawColumns();
        ImGui.Separator();

        DrawSection(DevToolUiSettings.T("主循环", "MAIN UPDATE"));
        DrawMetric(DevToolPerformanceMetric.DevUiUpdateTotal, DevToolUiSettings.T("DevUI 总更新", "DevUI total"));
        DrawMetric(DevToolPerformanceMetric.SessionSynchronization, DevToolUiSettings.T("会话同步", "Session sync"));
        DrawMetric(DevToolPerformanceMetric.DeferredWorkspaceRestore, DevToolUiSettings.T("工作区恢复", "Workspace restore"));
        DrawMetric(DevToolPerformanceMetric.LegacyTransactionBefore, DevToolUiSettings.T("Legacy 事务准备", "Legacy transaction prep"));
        DrawMetric(DevToolPerformanceMetric.InputShortcuts, DevToolUiSettings.T("快捷键输入", "Shortcut input"));
        DrawMetric(DevToolPerformanceMetric.VanillaDevUiUpdate, DevToolUiSettings.T("原版 DevUI", "Vanilla DevUI"));
        DrawMetric(DevToolPerformanceMetric.LegacyQuiescenceBackend, DevToolUiSettings.T("Legacy 存活后端", "Legacy live backend"));
        DrawMetric(DevToolPerformanceMetric.PostLegacySynchronization, DevToolUiSettings.T("Legacy 后同步", "Post-legacy sync"));
        DrawMetric(DevToolPerformanceMetric.CommandProcessing, DevToolUiSettings.T("命令处理", "Command processing"));
        DrawMetric(DevToolPerformanceMetric.LegacyPresentation, DevToolUiSettings.T("Legacy 表现层", "Legacy presentation"));
        DrawMetric(DevToolPerformanceMetric.ObjectGizmoPresentation, DevToolUiSettings.T("Object Gizmo", "Object gizmo"));

        ImGui.Spacing();
        DrawSection(DevToolUiSettings.T("数据发布", "PRESENTATION"));
        DrawMetric(DevToolPerformanceMetric.CorePresentation, DevToolUiSettings.T("核心数据", "Core presentation"));
        DrawMetric(DevToolPerformanceMetric.RoomPresentation, "Room");
        DrawMetric(DevToolPerformanceMetric.SoundPresentation, "Sound");
        DrawMetric(DevToolPerformanceMetric.TriggerPresentation, "Triggers");
        DrawMetric(DevToolPerformanceMetric.MapPresentation, "Map");
        DrawMetric(DevToolPerformanceMetric.DialogPresentation, "Dialog");
        DrawMetric(DevToolPerformanceMetric.RelationshipPresentation, "Relationships");

        ImGui.Spacing();
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "关闭此窗口对应的监控开关后，所有埋点回到无锁、无分配的快速路径。",
                "When monitoring is disabled, all instrumentation returns to the allocation-free, lock-free fast path."));

        ImGui.End();
    }

    private static void DrawColumns()
    {
        ImGui.TextUnformatted(DevToolUiSettings.T("阶段", "Stage"));
        DrawAt(LastColumn, DevToolUiSettings.T("最近", "Last"));
        DrawAt(AverageColumn, DevToolUiSettings.T("平均", "Avg"));
        DrawAt(P95Column, "P95");
        DrawAt(MaxColumn, DevToolUiSettings.T("最大", "Max"));
    }

    private static void DrawSection(string text)
    {
        ImGui.TextUnformatted(text);
    }

    private static void DrawMetric(DevToolPerformanceMetric metric, string label)
    {
        DevToolPerformanceStats stats = DevToolPerformanceMonitor.GetStats(metric);
        ImGui.TextUnformatted(label);

        if (!stats.HasSamples)
        {
            DrawAt(LastColumn, "-");
            DrawAt(AverageColumn, "-");
            DrawAt(P95Column, "-");
            DrawAt(MaxColumn, "-");
            return;
        }

        DrawAt(LastColumn, FormatMilliseconds(stats.LastMilliseconds));
        DrawAt(AverageColumn, FormatMilliseconds(stats.AverageMilliseconds));
        DrawAt(P95Column, FormatMilliseconds(stats.P95Milliseconds));
        DrawAt(MaxColumn, FormatMilliseconds(stats.MaxMilliseconds));
    }

    private static void DrawAt(float x, string text)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(x);
        ImGui.TextUnformatted(text);
    }

    private static string FormatMilliseconds(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture) + " ms";
}
