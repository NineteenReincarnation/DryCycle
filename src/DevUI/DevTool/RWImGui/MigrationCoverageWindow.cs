using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small runtime audit surface for the DevInterface -> ImGui migration.
/// It is deliberately compact by default so coverage diagnostics do not steal room visibility.
/// </summary>
internal static class MigrationCoverageWindow
{
    private static bool expanded;
    private static bool showMapped;

    internal static void Draw(Num.Vector2 display)
    {
        DevUiMigrationCoverageSnapshot observed = DevUiMigrationCoverage.Observed;
        DevUiMigrationCoverageSnapshot current = DevUiMigrationCoverage.CurrentPage;

        float compactWidth = Math.Min(390f, Math.Max(280f, display.X - 16f));
        float expandedWidth = Math.Min(620f, Math.Max(360f, display.X - 16f));
        float width = expanded ? expandedWidth : compactWidth;
        float height = expanded
            ? Math.Min(480f, Math.Max(280f, display.Y - 32f))
            : Math.Min(124f, Math.Max(94f, display.Y - 16f));

        float x = Math.Max(8f, display.X - width - 8f);
        float y = Math.Max(8f, display.Y - height - 8f);
        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(280f, width), 88f),
            new Num.Vector2(Math.Max(360f, display.X - 16f), Math.Max(120f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!expanded)
            flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin(
                DevToolUiSettings.T("迁移覆盖###DevToolMigrationCoverage", "Migration Coverage###DevToolMigrationCoverage"),
                flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("MigrationCoverage");

        if (observed == null || observed.TotalTypeCount == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("等待 DevUI 运行时扫描…", "Waiting for runtime DevUI scan…"));
            ImGui.End();
            return;
        }

        DrawHeadline(observed);
        DrawSourceSummaryLine(observed);

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                expanded ? DevToolUiSettings.T("收起", "Compact") : DevToolUiSettings.T("详情", "Details"),
                "MigrationCoverageExpand",
                expanded ? DevToolButtonTone.Subtle : DevToolButtonTone.Normal))
            expanded = !expanded;

        if (expanded)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            string page = ShortTypeName(current?.PageType);
            int currentUnmapped = current?.UnmappedTypeCount ?? 0;
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("当前页面：", "Current page: ") +
                (string.IsNullOrEmpty(page) ? "-" : page) +
                DevToolUiSettings.T(" · 未映射 ", " · unmapped ") + currentUnmapped);

            if (DevToolWidgets.ActionButton(
                    showMapped
                        ? DevToolUiSettings.T("只看未映射", "Unmapped only")
                        : DevToolUiSettings.T("显示已映射", "Show mapped"),
                    "MigrationCoverageMappedFilter",
                    DevToolButtonTone.Subtle))
                showMapped = !showMapped;

            ImGui.Spacing();
            float childHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y);
            if (ImGui.BeginChild(
                    "##MigrationCoverageList",
                    new Num.Vector2(0f, childHeight),
                    ImGuiChildFlags.Borders))
            {
                DrawSource(observed, DevUiMigrationSource.Vanilla, DevToolUiSettings.T("原版", "Vanilla"));
                DrawSource(observed, DevUiMigrationSource.RegionKit, "RegionKit");
                DrawSource(observed, DevUiMigrationSource.DryCycle, "DryCycle");
                DrawSource(observed, DevUiMigrationSource.Other, DevToolUiSettings.T("其他", "Other"));
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private static void DrawHeadline(DevUiMigrationCoverageSnapshot snapshot)
    {
        int mapped = Math.Max(0, snapshot.TotalTypeCount - snapshot.UnmappedTypeCount);
        ImGui.TextUnformatted(
            DevToolUiSettings.T("已观察 ", "Observed ") + snapshot.TotalTypeCount +
            DevToolUiSettings.T(" 项 · 已映射 ", " obligations · mapped ") + mapped +
            DevToolUiSettings.T(" · 未映射 ", " · unmapped ") + snapshot.UnmappedTypeCount);
    }

    private static void DrawSourceSummaryLine(DevUiMigrationCoverageSnapshot snapshot)
    {
        DevToolWidgets.MutedText(
            SummaryToken("V", snapshot.Vanilla) + "   " +
            SummaryToken("RK", snapshot.RegionKit) + "   " +
            SummaryToken("DC", snapshot.DryCycle) + "   " +
            SummaryToken("+", snapshot.Other));
    }

    private static string SummaryToken(string name, DevUiMigrationSourceSummary summary)
    {
        if (summary == null) return name + " 0/0";
        return name + " " + summary.MappedTypeCount + "/" + summary.TotalTypeCount;
    }

    private static void DrawSource(
        DevUiMigrationCoverageSnapshot snapshot,
        DevUiMigrationSource source,
        string label)
    {
        DevUiMigrationSourceSummary summary = snapshot.Summary(source);
        if (summary == null || summary.TotalTypeCount == 0) return;
        if (!showMapped && summary.UnmappedTypeCount == 0) return;

        string nodeLabel = label + "  " + summary.MappedTypeCount + "/" + summary.TotalTypeCount +
                           "###MigrationCoverage" + source;
        if (!ImGui.TreeNode(nodeLabel)) return;

        DevUiMigrationCoverageEntry[] entries = snapshot.Entries ?? Array.Empty<DevUiMigrationCoverageEntry>();
        for (int i = 0; i < entries.Length; i++)
        {
            DevUiMigrationCoverageEntry entry = entries[i];
            if (entry == null || entry.Source != source) continue;
            if (!showMapped && entry.State != DevUiMigrationState.Unmapped) continue;

            string state = StateLabel(entry.State);
            ImGui.TextWrapped("[" + state + "] " + ShortTypeName(entry.TypeName));

            string id = string.IsNullOrEmpty(entry.ExampleId) ? string.Empty : " · id=" + entry.ExampleId;
            string note = string.IsNullOrEmpty(entry.AdapterNote) ? string.Empty : " · " + entry.AdapterNote;
            DevToolWidgets.MutedText(
                ShortTypeName(entry.PageType) + " / " + entry.Context + id +
                " · x" + entry.InstanceCount + note);
        }

        ImGui.TreePop();
    }

    private static string StateLabel(DevUiMigrationState state)
    {
        return state switch
        {
            DevUiMigrationState.NativeNewUi => DevToolUiSettings.T("原生", "NATIVE"),
            DevUiMigrationState.GenericAdapter => DevToolUiSettings.T("通用适配", "GENERIC"),
            DevUiMigrationState.SpecializedAdapter => DevToolUiSettings.T("专用适配", "SPECIAL"),
            _ => DevToolUiSettings.T("未映射", "UNMAPPED")
        };
    }

    private static string ShortTypeName(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        int plus = value.LastIndexOf('+');
        int dot = value.LastIndexOf('.');
        int split = Math.Max(plus, dot);
        return split >= 0 && split + 1 < value.Length ? value[(split + 1)..] : value;
    }
}
