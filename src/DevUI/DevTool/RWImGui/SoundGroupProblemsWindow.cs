using System;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class SoundGroupProblemsWindow
{
    private static int drawnFrame = -1;

    internal static void DrawOnce()
    {
        int frame = ImGui.GetFrameCount();
        if (frame == drawnFrame) return;
        drawnFrame = frame;

        DevToolProblemSnapshot[] problems = SoundGroupLibrary.Current.Problems ?? Array.Empty<DevToolProblemSnapshot>();
        if (problems.Length == 0) return;

        int errors = 0;
        int warnings = 0;
        for (int i = 0; i < problems.Length; i++)
        {
            if (problems[i].Severity == DevToolProblemSeverity.Error) errors++;
            else warnings++;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        float width = Math.Min(620f, Math.Max(400f, display.X * 0.38f));
        float height = Math.Min(420f, Math.Max(250f, display.Y * 0.36f));
        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(8f, display.X - width - 12f), Math.Max(8f, display.Y - height - 12f)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Math.Max(DevToolUiSettings.WindowAlpha, 0.72f));

        if (!ImGui.Begin(
                DevToolUiSettings.T("预警###DevToolSoundProblems", "Problems###DevToolSoundProblems"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Problems");
        float bodyScale = DevToolUiSettings.IsChinese ? 1.18f : 1.12f;
        ImGui.SetWindowFontScale(bodyScale);

        ImGui.TextColored(
            errors > 0 ? new Num.Vector4(1f, 0.42f, 0.40f, 1f) : new Num.Vector4(1f, 0.72f, 0.36f, 1f),
            DevToolUiSettings.T(
                $"{errors} 个错误 · {warnings} 个警告",
                $"{errors} errors · {warnings} warnings"));

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重新扫描", "Rescan"),
                "SoundProblemsRescan",
                DevToolButtonTone.Subtle))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.ReloadGroups));
        }

        ImGui.Separator();
        if (ImGui.BeginChild("##SoundGroupProblemsList", new Num.Vector2(0f, 0f), ImGuiChildFlags.None))
        {
            // Child windows do not inherit FontWindowScale from their parent in ImGui.
            ImGui.SetWindowFontScale(bodyScale);
            for (int i = 0; i < problems.Length; i++)
            {
                DevToolProblemSnapshot problem = problems[i];
                bool error = problem.Severity == DevToolProblemSeverity.Error;
                ImGui.TextColored(
                    error ? new Num.Vector4(1f, 0.42f, 0.40f, 1f) : new Num.Vector4(1f, 0.72f, 0.36f, 1f),
                    error ? "ERROR" : "WARNING");
                ImGui.SameLine();
                ImGui.TextWrapped(problem.Message);

                if (!string.IsNullOrEmpty(problem.GroupId))
                    ImGui.TextDisabled(DevToolUiSettings.T("编组：", "Group: ") + problem.GroupId);
                if (!string.IsNullOrEmpty(problem.SourcePath))
                    ImGui.TextDisabled(DevToolUiSettings.T("来源：", "Source: ") + problem.SourcePath);
                if (i + 1 < problems.Length) ImGui.Separator();
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
