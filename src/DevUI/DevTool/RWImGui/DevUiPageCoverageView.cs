using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

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
