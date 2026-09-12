using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevUiCompatibilityGateView
{
    private static readonly Num.Vector4 PassText = new(0.55f, 0.88f, 0.62f, 1f);
    private static readonly Num.Vector4 FailText = new(1.00f, 0.68f, 0.38f, 1f);

    internal static void Draw(UniversalDevUiPresentationSnapshot mirror)
    {
        DevUiPageCoverageSnapshot pages = DevUiPageCoverageTracker.Current;
        DevUiSemanticConformanceSnapshot semantic = DevUiSemanticConformanceAudit.Evaluate(mirror);
        DevUiProtocolInventorySnapshot inventory = DevUiProtocolInventory.Current;
        DevUiCompatibilityGateSnapshot gate = DevUiCompatibilityGate.Evaluate(mirror, pages, semantic, inventory);

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
