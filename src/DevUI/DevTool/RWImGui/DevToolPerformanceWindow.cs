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

    private const float CacheHitColumn = 286f;
    private const float PartialColumn = 374f;
    private const float FullColumn = 462f;
    private const float HitRateColumn = 550f;

    internal static void Draw(Num.Vector2 display)
    {
        if (!DevToolPerformanceMonitor.Enabled)
            return;

        float maxWidth = Math.Max(520f, display.X - 16f);
        float width = Math.Min(650f, maxWidth);
        float height = Math.Min(690f, Math.Max(320f, display.Y - 32f));

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
        DrawSection(DevToolUiSettings.T("数据发布耗时", "PRESENTATION TIME"));
        DrawMetric(DevToolPerformanceMetric.CorePresentation, DevToolUiSettings.T("核心数据", "Core presentation"));
        DrawMetric(DevToolPerformanceMetric.RoomPresentation, "Room");
        DrawMetric(DevToolPerformanceMetric.SoundPresentation, "Sound");
        DrawMetric(DevToolPerformanceMetric.TriggerPresentation, "Triggers");
        DrawMetric(DevToolPerformanceMetric.MapPresentation, "Map");
        DrawMetric(DevToolPerformanceMetric.DialogPresentation, "Dialog");
        DrawMetric(DevToolPerformanceMetric.RelationshipPresentation, "Relationships");

        ImGui.Spacing();
        ImGui.Separator();
        DrawSection(DevToolUiSettings.T("快照缓存效率", "SNAPSHOT CACHE"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "命中=完全复用；局部=只重建轻量层并复用重数据；完整=重建整个快照。",
                "Hit = fully reused; Partial = cheap layer rebuilt while heavy payload stayed retained; Full = complete snapshot rebuild."));
        DrawCacheColumns();
        ImGui.Separator();
        DrawCacheRow(DevToolPresentationChannel.Core, DevToolUiSettings.T("核心", "Core"));
        DrawCacheRow(DevToolPresentationChannel.Room, "Room");
        DrawCacheRow(DevToolPresentationChannel.Sound, "Sound");
        DrawCacheRow(DevToolPresentationChannel.Triggers, "Triggers");
        DrawCacheRow(DevToolPresentationChannel.Map, "Map");
        DrawCacheRow(DevToolPresentationChannel.Dialog, "Dialog");
        DrawCacheRow(DevToolPresentationChannel.Relationships, "Relationships");

        ImGui.Spacing();
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "监控关闭后，计时埋点回到无分配快速路径；缓存计数器也只剩一次 Enabled 检查。",
                "When monitoring is disabled, timing returns to its allocation-free fast path and cache counters reduce to one Enabled check."));

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

    private static void DrawCacheColumns()
    {
        ImGui.TextUnformatted(DevToolUiSettings.T("工作区", "Workspace"));
        DrawAt(CacheHitColumn, DevToolUiSettings.T("命中", "Hit"));
        DrawAt(PartialColumn, DevToolUiSettings.T("局部", "Partial"));
        DrawAt(FullColumn, DevToolUiSettings.T("完整", "Full"));
        DrawAt(HitRateColumn, DevToolUiSettings.T("命中率", "Hit %"));
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

    private static void DrawCacheRow(DevToolPresentationChannel channel, string label)
    {
        DevToolPresentationCounters stats = DevToolPerformanceMonitor.GetPresentationCounters(channel);
        ImGui.TextUnformatted(label);
        if (stats.Total <= 0)
        {
            DrawAt(CacheHitColumn, "-");
            DrawAt(PartialColumn, "-");
            DrawAt(FullColumn, "-");
            DrawAt(HitRateColumn, "-");
            return;
        }

        DrawAt(CacheHitColumn, FormatCount(stats.CacheHits));
        DrawAt(PartialColumn, FormatCount(stats.PartialRebuilds));
        DrawAt(FullColumn, FormatCount(stats.FullRebuilds));
        DrawAt(HitRateColumn, (stats.CacheHitRate * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%");
    }

    private static void DrawAt(float x, string text)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(x);
        ImGui.TextUnformatted(text);
    }

    private static string FormatMilliseconds(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);
}
