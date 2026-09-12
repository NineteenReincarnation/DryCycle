using System;
using System.Text;
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
            if (!ShouldDisplayProblem(problems[i])) continue;
            if (problems[i].Severity == DevToolProblemSeverity.Error) errors++;
            else warnings++;
        }
        if (errors == 0 && warnings == 0) return;

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
            int rendered = 0;
            for (int i = 0; i < problems.Length; i++)
            {
                DevToolProblemSnapshot problem = problems[i];
                if (!ShouldDisplayProblem(problem)) continue;
                if (rendered++ > 0) ImGui.Separator();

                bool error = problem.Severity == DevToolProblemSeverity.Error;
                ImGui.TextColored(
                    error ? new Num.Vector4(1f, 0.42f, 0.40f, 1f) : new Num.Vector4(1f, 0.72f, 0.36f, 1f),
                    error ? "ERROR" : "WARNING");
                ImGui.SameLine();

                // Keep the message inside the current child width even when the Problems window is
                // resized narrower than its default size.
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(problem.Message ?? string.Empty);
                ImGui.PopTextWrapPos();

                if (!string.IsNullOrEmpty(problem.GroupId))
                    DevToolWidgets.MutedText(
                        WrapLongDetail(DevToolUiSettings.T("编组：", "Group: ") + problem.GroupId),
                        true);
                if (!string.IsNullOrEmpty(problem.SourcePath))
                    DevToolWidgets.MutedText(
                        WrapLongDetail(DevToolUiSettings.T("来源：", "Source: ") + problem.SourcePath),
                        true);
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private static bool ShouldDisplayProblem(DevToolProblemSnapshot problem)
    {
        if (problem == null) return false;

        // An empty developer-local group is a valid draft state: users may create the Working Group
        // first and populate it from Library/Scene immediately afterwards. Portable Mod groups remain
        // strict, so an empty registered group still reports the original warning.
        if (string.Equals(problem.Code, "sound-group-empty", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(problem.SourcePath, SoundGroupLibrary.Current.LocalFilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// ImGui's normal word wrapping cannot split one very long token such as a filesystem path.
    /// Insert line breaks at path/punctuation boundaries, falling back to a character boundary for
    /// exceptionally long file names. This keeps diagnostics readable without horizontal clipping.
    /// </summary>
    private static string WrapLongDetail(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        float maxWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - 6f);
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        StringBuilder result = new(text.Length + 16);
        int lineStart = 0;
        int lastBreak = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '/' || c == '\\' || c == ' ' || c == '-' || c == '_' || c == ':' || c == '.')
                lastBreak = i + 1;

            string candidate = text.Substring(lineStart, i - lineStart + 1);
            if (ImGui.CalcTextSize(candidate).X <= maxWidth) continue;

            int breakAt = lastBreak > lineStart ? lastBreak : i;
            if (breakAt <= lineStart) breakAt = i + 1;

            result.Append(text, lineStart, breakAt - lineStart);
            result.Append('\n');
            lineStart = breakAt;
            while (lineStart < text.Length && text[lineStart] == ' ')
                lineStart++;

            i = lineStart - 1;
            lastBreak = -1;
        }

        if (lineStart < text.Length)
            result.Append(text, lineStart, text.Length - lineStart);

        return result.ToString();
    }
}
