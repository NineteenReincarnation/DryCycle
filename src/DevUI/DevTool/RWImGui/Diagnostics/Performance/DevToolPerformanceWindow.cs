using System;
using System.Collections.Generic;
using System.Globalization;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Sound;
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
    private sealed class MetricReadback
    {
        internal bool HasSamples;
        internal string Last = "-";
        internal string Average = "-";
        internal string P95 = "-";
        internal string Max = "-";
    }

    private sealed class CacheReadback
    {
        internal bool HasSamples;
        internal string Hit = "-";
        internal string Partial = "-";
        internal string Full = "-";
        internal string HitRate = "-";
    }

    private const float LastColumn = 286f;
    private const float AverageColumn = 374f;
    private const float P95Column = 462f;
    private const float MaxColumn = 550f;

    private const float CacheHitColumn = 286f;
    private const float PartialColumn = 374f;
    private const float FullColumn = 462f;
    private const float HitRateColumn = 550f;
    private const int ReadbackRefreshFrames = 6;

    private static readonly DevToolPerformanceMetric[] Metrics =
    {
        DevToolPerformanceMetric.DevUiUpdateTotal,
        DevToolPerformanceMetric.SessionSynchronization,
        DevToolPerformanceMetric.DeferredWorkspaceRestore,
        DevToolPerformanceMetric.LegacyTransactionBefore,
        DevToolPerformanceMetric.InputShortcuts,
        DevToolPerformanceMetric.VanillaDevUiUpdate,
        DevToolPerformanceMetric.LegacyQuiescenceBackend,
        DevToolPerformanceMetric.PostLegacySynchronization,
        DevToolPerformanceMetric.CommandProcessing,
        DevToolPerformanceMetric.LegacyPresentation,
        DevToolPerformanceMetric.ObjectGizmoPresentation,
        DevToolPerformanceMetric.CorePresentation,
        DevToolPerformanceMetric.RoomPresentation,
        DevToolPerformanceMetric.SoundPresentation,
        DevToolPerformanceMetric.TriggerPresentation,
        DevToolPerformanceMetric.MapPresentation,
        DevToolPerformanceMetric.DialogPresentation,
        DevToolPerformanceMetric.RelationshipPresentation
    };

    private static readonly DevToolFrontendPerformanceMetric[] FrontendMetrics =
    {
        DevToolFrontendPerformanceMetric.FrontendFrameTotal,
        DevToolFrontendPerformanceMetric.UiModeSwitch,
        DevToolFrontendPerformanceMetric.FontSettings,
        DevToolFrontendPerformanceMetric.Overlay,
        DevToolFrontendPerformanceMetric.SceneWorkspace,
        DevToolFrontendPerformanceMetric.ScenePlacement,
        DevToolFrontendPerformanceMetric.ActionToast
    };

    private static readonly DevToolPresentationChannel[] Channels =
    {
        DevToolPresentationChannel.Core,
        DevToolPresentationChannel.Room,
        DevToolPresentationChannel.Sound,
        DevToolPresentationChannel.Triggers,
        DevToolPresentationChannel.Map,
        DevToolPresentationChannel.Dialog,
        DevToolPresentationChannel.Relationships
    };

    private static readonly Dictionary<DevToolPerformanceMetric, MetricReadback> MetricReadbacks = new();
    private static readonly Dictionary<DevToolFrontendPerformanceMetric, MetricReadback> FrontendMetricReadbacks = new();
    private static readonly Dictionary<DevToolPresentationChannel, CacheReadback> CacheReadbacks = new();
    private static int nextReadbackFrame;
    private static bool readbackValid;
    private static int rollingCapacity = -1;
    private static bool rollingChinese;
    private static string rollingDescription = string.Empty;

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
        EnsureReadback();

        DevToolWidgets.MutedText(GetRollingDescription());

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重置样本", "Reset Samples"),
                "PerformanceReset",
                DevToolButtonTone.Subtle))
        {
            DevToolPerformanceMonitor.Reset();
            DevToolFrontendPerformanceMonitor.Reset();
            InvalidateReadback();
            EnsureReadback();
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("停止监控", "Stop Monitoring"),
                "PerformanceStop",
                DevToolButtonTone.Normal))
        {
            DevToolPerformanceMonitor.SetEnabled(false);
            DevToolFrontendPerformanceMonitor.SetEnabled(false);
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
        DrawSection(DevToolUiSettings.T("前端绘制耗时", "FRONTEND DRAW"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.FrontendFrameTotal, DevToolUiSettings.T("前端总帧", "Frontend total"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.UiModeSwitch, DevToolUiSettings.T("界面模式开关", "Mode switch"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.FontSettings, DevToolUiSettings.T("字体设置", "Font settings"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.Overlay, DevToolUiSettings.T("主 Overlay", "Main overlay"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.SceneWorkspace, DevToolUiSettings.T("场景工作区", "Scene workspace"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.ScenePlacement, DevToolUiSettings.T("场景放置", "Scene placement"));
        DrawFrontendMetric(DevToolFrontendPerformanceMetric.ActionToast, DevToolUiSettings.T("操作提示", "Action toast"));

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
        DrawSoundColdStart();

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

    private static void DrawSoundColdStart()
    {
        DrawSection(DevToolUiSettings.T("Sound 冷启动", "SOUND COLD START"));
        SoundActivationStatusSnapshot status = SoundActivationPipeline.Current;

        ImGui.TextUnformatted(DevToolUiSettings.T("预热状态", "Prewarm"));
        DrawAt(LastColumn, SoundActivationPipeline.IsPrewarmed
            ? DevToolUiSettings.T("已完成", "Ready")
            : DevToolUiSettings.T("进行中", "Working"));

        ImGui.TextUnformatted(DevToolUiSettings.T("页面切换 / 构造", "Page switch / ctor"));
        DrawAt(LastColumn, FormatMilliseconds(SoundActivationPipeline.LastPageSwitchMilliseconds));

        ImGui.TextUnformatted(DevToolUiSettings.T("点击 → Ready", "Click → Ready"));
        DrawAt(LastColumn, FormatMilliseconds(status.ClickToReadyMilliseconds));

        ImGui.TextUnformatted(DevToolUiSettings.T("激活单帧峰值", "Activation frame max"));
        DrawAt(LastColumn, FormatMilliseconds(status.MaxFrameWorkMilliseconds));

        ImGui.TextUnformatted(DevToolUiSettings.T("最坏不可切分单元", "Worst indivisible unit"));
        DrawAt(LastColumn, FormatMilliseconds(status.MaxBlockingUnitMilliseconds));
        if (!string.IsNullOrEmpty(status.MaxBlockingUnit))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(status.MaxBlockingUnit);
        }

        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "Page switch 高说明剩余成本在原版 SoundPage 构造；Worst unit 高说明某个文件系统/XML/排序单元仍超过帧预算。",
                "A high page-switch value points to remaining vanilla SoundPage construction cost; a high worst-unit value points to an indivisible filesystem/XML/sort operation."));
    }

    private static void EnsureReadback()
    {
        int frame = ImGui.GetFrameCount();
        if (readbackValid && frame < nextReadbackFrame) return;

        for (int i = 0; i < Metrics.Length; i++)
        {
            DevToolPerformanceMetric metric = Metrics[i];
            DevToolPerformanceStats stats = DevToolPerformanceMonitor.GetStats(metric);
            if (!MetricReadbacks.TryGetValue(metric, out MetricReadback display))
            {
                display = new MetricReadback();
                MetricReadbacks.Add(metric, display);
            }

            display.HasSamples = stats.HasSamples;
            if (!stats.HasSamples)
            {
                display.Last = display.Average = display.P95 = display.Max = "-";
                continue;
            }

            display.Last = FormatMilliseconds(stats.LastMilliseconds);
            display.Average = FormatMilliseconds(stats.AverageMilliseconds);
            display.P95 = FormatMilliseconds(stats.P95Milliseconds);
            display.Max = FormatMilliseconds(stats.MaxMilliseconds);
        }

        for (int i = 0; i < FrontendMetrics.Length; i++)
        {
            DevToolFrontendPerformanceMetric metric = FrontendMetrics[i];
            DevToolFrontendPerformanceStats stats = DevToolFrontendPerformanceMonitor.GetStats(metric);
            if (!FrontendMetricReadbacks.TryGetValue(metric, out MetricReadback display))
            {
                display = new MetricReadback();
                FrontendMetricReadbacks.Add(metric, display);
            }

            display.HasSamples = stats.HasSamples;
            if (!stats.HasSamples)
            {
                display.Last = display.Average = display.P95 = display.Max = "-";
                continue;
            }

            display.Last = FormatMilliseconds(stats.LastMilliseconds);
            display.Average = FormatMilliseconds(stats.AverageMilliseconds);
            display.P95 = FormatMilliseconds(stats.P95Milliseconds);
            display.Max = FormatMilliseconds(stats.MaxMilliseconds);
        }

        for (int i = 0; i < Channels.Length; i++)
        {
            DevToolPresentationChannel channel = Channels[i];
            DevToolPresentationCounters stats = DevToolPerformanceMonitor.GetPresentationCounters(channel);
            if (!CacheReadbacks.TryGetValue(channel, out CacheReadback display))
            {
                display = new CacheReadback();
                CacheReadbacks.Add(channel, display);
            }

            display.HasSamples = stats.Total > 0;
            if (!display.HasSamples)
            {
                display.Hit = display.Partial = display.Full = display.HitRate = "-";
                continue;
            }

            display.Hit = FormatCount(stats.CacheHits);
            display.Partial = FormatCount(stats.PartialRebuilds);
            display.Full = FormatCount(stats.FullRebuilds);
            display.HitRate = (stats.CacheHitRate * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        readbackValid = true;
        nextReadbackFrame = frame + ReadbackRefreshFrames;
    }

    private static void InvalidateReadback()
    {
        readbackValid = false;
        nextReadbackFrame = 0;
    }

    private static string GetRollingDescription()
    {
        int capacity = DevToolPerformanceMonitor.RollingSampleCapacity;
        bool chinese = DevToolUiSettings.IsChinese;
        if (rollingCapacity == capacity && rollingChinese == chinese && rollingDescription.Length > 0)
            return rollingDescription;

        rollingCapacity = capacity;
        rollingChinese = chinese;
        rollingDescription = chinese
            ? $"最近 {capacity} 个样本的滚动统计；P95 每 {ReadbackRefreshFrames} 帧读取一次。"
            : $"Rolling {capacity}-sample window; P95 is sampled every {ReadbackRefreshFrames} frames.";
        return rollingDescription;
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
        ImGui.TextUnformatted(label);
        if (!MetricReadbacks.TryGetValue(metric, out MetricReadback stats) || !stats.HasSamples)
        {
            DrawAt(LastColumn, "-");
            DrawAt(AverageColumn, "-");
            DrawAt(P95Column, "-");
            DrawAt(MaxColumn, "-");
            return;
        }

        DrawAt(LastColumn, stats.Last);
        DrawAt(AverageColumn, stats.Average);
        DrawAt(P95Column, stats.P95);
        DrawAt(MaxColumn, stats.Max);
    }

    private static void DrawFrontendMetric(DevToolFrontendPerformanceMetric metric, string label)
    {
        ImGui.TextUnformatted(label);
        if (!FrontendMetricReadbacks.TryGetValue(metric, out MetricReadback stats) || !stats.HasSamples)
        {
            DrawAt(LastColumn, "-");
            DrawAt(AverageColumn, "-");
            DrawAt(P95Column, "-");
            DrawAt(MaxColumn, "-");
            return;
        }

        DrawAt(LastColumn, stats.Last);
        DrawAt(AverageColumn, stats.Average);
        DrawAt(P95Column, stats.P95);
        DrawAt(MaxColumn, stats.Max);
    }

    private static void DrawCacheRow(DevToolPresentationChannel channel, string label)
    {
        ImGui.TextUnformatted(label);
        if (!CacheReadbacks.TryGetValue(channel, out CacheReadback stats) || !stats.HasSamples)
        {
            DrawAt(CacheHitColumn, "-");
            DrawAt(PartialColumn, "-");
            DrawAt(FullColumn, "-");
            DrawAt(HitRateColumn, "-");
            return;
        }

        DrawAt(CacheHitColumn, stats.Hit);
        DrawAt(PartialColumn, stats.Partial);
        DrawAt(FullColumn, stats.Full);
        DrawAt(HitRateColumn, stats.HitRate);
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
