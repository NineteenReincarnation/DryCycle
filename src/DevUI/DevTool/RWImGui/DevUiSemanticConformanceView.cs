using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevUiSemanticConformanceView
{
    internal static void Draw(UniversalDevUiPresentationSnapshot mirror)
    {
        DevUiSemanticConformanceSnapshot snapshot = DevUiSemanticConformanceAudit.Evaluate(mirror);
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.PageType)) return;

        string title = DevToolUiSettings.T("语义一致性", "SEMANTIC CONFORMANCE") +
                       "  ·  " + snapshot.ExecutableRouteCount + "/" + snapshot.ControlCount;
        ImGuiTreeNodeFlags flags = snapshot.Passed ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
        if (!ImGui.CollapsingHeader(title + "##DevUiSemanticConformance", flags)) return;

        if (snapshot.Passed)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "当前页面所有已镜像控件都有可执行的通用动作路径，且快照数据满足协议约束。",
                "Every mirrored control on this page has a generic executable route and a valid semantic snapshot."), true);
            return;
        }

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "这些是通用协议/镜像本身的问题，不按具体 Mod 类型修。",
            "These are generic protocol/mirror failures and should be fixed at the protocol layer, not per mod type."), true);

        DevUiSemanticFailure[] failures = snapshot.Failures ?? Array.Empty<DevUiSemanticFailure>();
        int limit = Math.Min(32, failures.Length);
        for (int i = 0; i < limit; i++)
        {
            DevUiSemanticFailure failure = failures[i];
            if (failure == null) continue;
            ImGui.TextWrapped(failure.Protocol + " · " + failure.Path);
            DevToolWidgets.MutedText(failure.Reason, true);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(failure.RuntimeType))
                DevToolTooltip.Show(failure.RuntimeType);
        }

        if (failures.Length > limit)
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "另有 " + (failures.Length - limit) + " 项未展开；完整列表已写入日志。",
                (failures.Length - limit) + " additional failure(s) omitted here; the full batch is logged."), true);
    }
}
