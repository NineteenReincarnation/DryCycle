using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevToolUserFacingCopyCleanup
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Normal DevTool UI implementation diagnostics hidden through direct UI policy; no self-detours attached.");
    }

    internal static void Disable() => enabled = false;

    internal static bool HideNormalDiagnostics =>
        enabled && !DevToolOverlay.IsDebugWorkspace;

    internal static string RewriteMutedText(string text)
    {
        if (!HideNormalDiagnostics || string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        if (text.IndexOf("首次建立生物目录缓存", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("Building the creature catalog cache", StringComparison.OrdinalIgnoreCase) >= 0)
            return DevToolUiSettings.T("正在载入生物...", "Loading creatures...");

        return text;
    }

    internal static bool ShouldSuppressMutedText(string text)
    {
        if (!HideNormalDiagnostics || string.IsNullOrWhiteSpace(text))
            return false;

        string lower = text.ToLowerInvariant();
        if (text.IndexOf("缓存内容立即显示", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("后台增量刷新", StringComparison.Ordinal) >= 0 ||
            lower.Contains("cached content is immediate") ||
            lower.Contains("refresh incrementally"))
            return true;

        if (text.StartsWith("缩略图：", StringComparison.Ordinal) ||
            lower.StartsWith("thumbnail:"))
            return true;

        if (lower.StartsWith("ready ") && lower.Contains("pending ") && lower.Contains("failed"))
            return true;

        if (text.IndexOf("通用 DevInterface 协议镜像", StringComparison.Ordinal) >= 0 ||
            lower.Contains("generic devinterface protocols mirror"))
            return true;

        return text.IndexOf("缓存", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("增量", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("后台刷新", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("性能监控", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("性能采样", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("调试信息", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("诊断信息", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("烘焙", StringComparison.Ordinal) >= 0 ||
               text.IndexOf("栅格", StringComparison.Ordinal) >= 0 ||
               lower.Contains(" cache") ||
               lower.StartsWith("cache") ||
               lower.Contains("incremental") ||
               lower.Contains("profiling") ||
               lower.Contains("performance monitor") ||
               lower.Contains("performance sample") ||
               lower.Contains("debug info") ||
               lower.Contains("diagnostic info") ||
               lower.Contains("gpu ") ||
               lower.StartsWith("gpu") ||
               lower.Contains("rebake") ||
               lower.Contains("raster") ||
               lower.Contains("snapshot") ||
               lower.Contains("revision") ||
               lower.Contains("hot path") ||
               lower.Contains("backend") ||
               lower.Contains("frontend");
    }

    internal static bool DrawSoundActivationShell(SoundActivationStatusSnapshot status)
    {
        if (!HideNormalDiagnostics) return false;

        DevToolWidgets.SectionHeader(
            DevToolUiSettings.T("正在载入声音资源", "LOADING SOUND RESOURCES"),
            1.22f);

        string phaseText = status.Phase switch
        {
            SoundActivationPhase.DiscoveringFileNames =>
                DevToolUiSettings.T("正在查找可用的环境声音文件...", "Finding available ambient sound files..."),
            SoundActivationPhase.IndexingSamples =>
                DevToolUiSettings.T("正在整理声音列表...", "Preparing the sound list..."),
            SoundActivationPhase.LoadingGroups =>
                DevToolUiSettings.T("正在载入音效组...", "Loading sound groups..."),
            SoundActivationPhase.Failed =>
                DevToolUiSettings.T("声音资源载入失败。请查看日志中的具体错误。", "Sound resources could not be loaded. Check the log for details."),
            _ => DevToolUiSettings.T("正在准备声音编辑器...", "Preparing the Sound editor...")
        };

        ImGui.TextWrapped(phaseText);
        float progress = Math.Max(0f, Math.Min(1f, status.Progress));
        string overlay = Math.Round(progress * 100f) + "%";
        ImGui.ProgressBar(progress, new Num.Vector2(-1f, 0f), overlay);

        if (status.TotalSamples > 0)
            ImGui.TextDisabled(DevToolUiSettings.T(
                $"声音：{status.ProcessedSamples}/{status.TotalSamples}",
                $"Sounds: {status.ProcessedSamples}/{status.TotalSamples}"));
        if (status.TotalGroupFiles > 0)
            ImGui.TextDisabled(DevToolUiSettings.T(
                $"音效组：{status.ProcessedGroupFiles}/{status.TotalGroupFiles}",
                $"Sound groups: {status.ProcessedGroupFiles}/{status.TotalGroupFiles}"));
        return true;
    }

    internal static bool DrawSoundProjectionShell(int totalSamples)
    {
        if (!HideNormalDiagnostics) return false;
        DevToolWidgets.SectionHeader(
            DevToolUiSettings.T("正在显示声音列表", "PREPARING SOUND LIST"),
            1.22f);
        ImGui.TextWrapped(DevToolUiSettings.T(
            "正在整理当前声音列表...",
            "Preparing the current sound list..."));
        return true;
    }
}
