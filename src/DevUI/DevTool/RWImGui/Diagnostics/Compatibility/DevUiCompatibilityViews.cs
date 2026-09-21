using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;
using System;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevUiCompatibilityGateView
{
    private static readonly Num.Vector4 PassText = new(0.55f, 0.88f, 0.62f, 1f);
    private static readonly Num.Vector4 FailText = new(1.00f, 0.68f, 0.38f, 1f);

    internal static void Draw()
    {
        DevUiCompatibilityGateSnapshot gate = DevUiCompatibilityGate.Current;

        string title = DevToolUiSettings.T("通用兼容验收", "GENERIC COMPATIBILITY GATE");
        ImGuiTreeNodeFlags flags = gate.Passed ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
        if (!ImGui.CollapsingHeader(title + "##DevUiCompatibilityGate", flags)) return;

        ImGui.TextColored(
            gate.Passed ? PassText : FailText,
            gate.Passed
                ? DevToolUiSettings.T("PASS · 通用迁移验收通过", "PASS · generic migration gate passed")
                : DevToolUiSettings.T("NOT READY · 仍有通用覆盖缺口", "NOT READY · generic coverage gaps remain"));

        ImGui.Spacing();
        DrawMetric(
            DevToolUiSettings.T("页面运行时审计", "Runtime page audit"),
            gate.PagesVisited + "/" + gate.PagesTotal,
            gate.PageCoverageComplete);
        DrawMetric(
            DevToolUiSettings.T("运行时协议缺口", "Runtime protocol gaps"),
            gate.RuntimeProtocolGaps.ToString(),
            gate.RuntimeProtocolGaps == 0);
        DrawMetric(
            DevToolUiSettings.T("已加载类型缺口", "Loaded-type gaps"),
            gate.LoadedTypeProtocolGaps.ToString(),
            gate.LoadedTypeProtocolGaps == 0);
        DrawMetric(
            DevToolUiSettings.T("语义一致性失败", "Semantic failures"),
            gate.SemanticFailures + " / " + gate.SemanticPagesAudited + DevToolUiSettings.T(" 页", " pages"),
            gate.SemanticFailures == 0);

        ImGui.Spacing();
        DevToolWidgets.MutedText(
            "Vanilla " + gate.VanillaGaps +
            "  ·  RegionKit " + gate.RegionKitGaps +
            "  ·  DryCycle " + gate.DryCycleGaps +
            "  ·  Other " + gate.OtherGaps);

        if (!gate.Passed)
        {
            ImGui.Spacing();
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "这个门只认通用协议结果：不允许用某个 RK/原版/DryCycle 类型的专用白名单把缺口抹掉。",
                "This gate accepts only generic protocol coverage; per-type vanilla/RK/DryCycle allowlists cannot hide a gap."), true);
        }
    }

    private static void DrawMetric(string name, string value, bool passed)
    {
        ImGui.TextColored(passed ? PassText : FailText, passed ? "[OK]" : "[!!]");
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        DevToolWidgets.MutedText(value);
    }
}

/// <summary>
/// Shows which on-demand DevInterface page slots have actually been exercised by the universal
/// mirror. No page/type names are hardcoded; the list comes from the live DevUI page registry.
/// </summary>
internal static class DevUiPageCoverageView
{
    internal static void Draw()
    {
        DevUiPageCoverageSnapshot snapshot = DevUiPageCoverageTracker.Current;
        if (snapshot == null || snapshot.TotalPageCount <= 0) return;

        string title = DevToolUiSettings.T("页面审计覆盖", "PAGE AUDIT COVERAGE") +
                       "  ·  " + snapshot.VisitedPageCount + "/" + snapshot.TotalPageCount;
        ImGuiTreeNodeFlags flags = snapshot.Complete ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
        if (!ImGui.CollapsingHeader(title + "##GenericPageAuditCoverage", flags)) return;

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "DevUI 页面按需构造。只有真正访问过的页面，动态控件树才算完成运行时镜像/动作审计；已加载类型协议扫描会补充检查尚未实例化的控件族。",
            "DevUI pages are constructed on demand. Only visited pages have had their dynamic trees runtime-audited; the loaded-type protocol inventory complements uninstantiated control families."), true);

        DevUiPageCoverageEntry[] pages = snapshot.Pages ?? Array.Empty<DevUiPageCoverageEntry>();
        for (int i = 0; i < pages.Length; i++)
        {
            DevUiPageCoverageEntry page = pages[i];
            if (page == null) continue;

            bool current = i == snapshot.CurrentPageIndex;
            string prefix = page.Visited ? "[OK] " : "[  ] ";
            if (current) prefix = "> " + prefix;
            else prefix = "  " + prefix;

            string label = string.IsNullOrWhiteSpace(page.Label)
                ? DevToolUiSettings.T("页面 ", "Page ") + i
                : page.Label;
            ImGui.TextUnformatted(prefix + label);

            if (page.Visited)
            {
                ImGui.SameLine();
                string details = DevToolUiSettings.T(
                    "镜像 " + page.MirroredControlCount + " · 未映射 " + page.UnmappedProtocolCount,
                    "mirror " + page.MirroredControlCount + " · unmapped " + page.UnmappedProtocolCount);
                DevToolWidgets.MutedText(details);

                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(page.PageType))
                    DevToolTooltip.Show(page.PageType);
            }
        }

        if (snapshot.UnknownVisitedPageCount > 0)
        {
            ImGui.Spacing();
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "另发现 " + snapshot.UnknownVisitedPageCount + " 个不在标准页面槽列表中的动态页面；这些页面仍会进入协议审计。",
                snapshot.UnknownVisitedPageCount + " dynamic page(s) were observed outside the canonical slot list; they are still protocol-audited."), true);
        }

        if (!snapshot.Complete)
        {
            ImGui.Spacing();
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "切换到未访问页面即可自动补齐审计。系统不会为了测试擅自切页或修改房间数据。",
                "Visit unscanned pages to complete runtime coverage. The audit never auto-switches pages or mutates room data."), true);
        }
    }
}

internal static class DevUiSemanticConformanceView
{
    internal static void Draw()
    {
        DevUiSemanticConformanceSnapshot snapshot = DevUiSemanticConformanceAudit.Current;
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
