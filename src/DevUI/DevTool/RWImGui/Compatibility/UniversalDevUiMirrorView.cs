using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// One renderer for every structurally-supported DevInterface control. It deliberately knows
/// nothing about vanilla, RegionKit or DryCycle feature classes; new mods receive the same UI as
/// soon as their controls satisfy one of the semantic protocols exposed by the core bridge.
/// </summary>
internal static class UniversalDevUiMirrorView
{
    private static readonly Dictionary<string, string> StringEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> Vector2Edits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector4> ColorEdits = new(StringComparer.Ordinal);
    private static string search = string.Empty;
    private static string observedSearch;
    private static string normalizedSearch = string.Empty;

    internal static void ResetRetainedState()
    {
        StringEdits.Clear();
        Vector2Edits.Clear();
        ColorEdits.Clear();
        search = string.Empty;
        observedSearch = null;
        normalizedSearch = string.Empty;
    }

    internal static void Draw(UniversalDevUiPresentationSnapshot snapshot)
    {
        snapshot ??= UniversalDevUiPresentationSnapshot.Empty;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        LegacyControlSnapshot[] controls = snapshot.Controls ?? Array.Empty<LegacyControlSnapshot>();
        string title = DevToolUiSettings.T("通用 DevUI 镜像", "UNIVERSAL DEVUI MIRROR") +
                       "  ·  " + controls.Length;
        ImGuiTreeNodeFlags flags = snapshot.UnmappedProtocolCount > 0
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader(title + "##UniversalDevUiMirror", flags))
            return;

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "直接镜像当前 DevInterface 树的交互协议；不按原版、RegionKit 或 Mod 类型写专用适配。",
            "Mirrors the active DevInterface tree by interaction protocol, without per-vanilla/RegionKit/mod adapters."), true);

        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("当前没有可用的 DevUI 页面。", "No active DevUI page."));
            return;
        }

        DevToolWidgets.MutedText(snapshot.PageType);

        DevUiProtocolInventorySnapshot inventory = DevUiProtocolInventory.Current;
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "已加载 DevUINode 类型 " + inventory.ConcreteNodeTypeCount + " · 潜在协议缺口 " + inventory.PotentialGapCount,
            "Loaded DevUINode types " + inventory.ConcreteNodeTypeCount + " · potential protocol gaps " + inventory.PotentialGapCount));

        if (snapshot.UnmappedProtocolCount > 0)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "当前页面仍有 " + snapshot.UnmappedProtocolCount + " 种交互协议未被通用桥覆盖；详见 Migration Coverage。",
                snapshot.UnmappedProtocolCount + " interaction protocol(s) on this page are still unmapped; see Migration Coverage."));
        }

        DrawLoadedTypeGaps(inventory);

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(DevToolUiSettings.T("搜索##UniversalDevUiSearch", "Search##UniversalDevUiSearch"), ref search, 256);
        ImGui.Spacing();

        string query = SearchQuery();
        int visible = 0;
        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            if (control == null || !Matches(control, query)) continue;
            visible++;
            DrawControl(snapshot, control);
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的通用控件。", "No matching generic controls."));
    }

    private static void DrawLoadedTypeGaps(DevUiProtocolInventorySnapshot inventory)
    {
        if (inventory == null || inventory.PotentialGapCount <= 0) return;
        if (!ImGui.CollapsingHeader(
                DevToolUiSettings.T("已加载类型协议缺口##LoadedDevUiProtocolGaps", "LOADED TYPE PROTOCOL GAPS##LoadedDevUiProtocolGaps")))
            return;

        DevUiProtocolInventoryEntry[] gaps = inventory.PotentialGaps ?? Array.Empty<DevUiProtocolInventoryEntry>();
        int limit = Math.Min(32, gaps.Length);
        for (int i = 0; i < limit; i++)
        {
            DevUiProtocolInventoryEntry gap = gaps[i];
            if (gap == null) continue;
            ImGui.TextWrapped(gap.AssemblyName + " · " + gap.TypeName);
            DevToolWidgets.MutedText(gap.Protocol, true);
        }
        if (gaps.Length > limit)
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "另有 " + (gaps.Length - limit) + " 项未展开；完整列表已写入日志。",
                (gaps.Length - limit) + " additional gap(s) omitted here; the full batch is logged."), true);
    }

    private static void DrawControl(UniversalDevUiPresentationSnapshot snapshot, LegacyControlSnapshot control)
    {
        string stateKey = snapshot.PageType + ":" + control.Path;
        string label = string.IsNullOrWhiteSpace(control.Label)
            ? (string.IsNullOrWhiteSpace(control.Id) ? control.Kind.ToString() : control.Id)
            : control.Label;

        switch (control.Kind)
        {
            case LegacyControlKind.Button:
                if (DevToolWidgets.ActionButton(label, "UniversalButton_" + stateKey, DevToolButtonTone.Normal, true))
                    Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.Click, control.Path));
                break;

            case LegacyControlKind.Boolean:
            {
                bool value = control.BooleanValue;
                if (ImGui.Checkbox(label + "##UniversalBool_" + stateKey, ref value))
                    Send(new UniversalDevUiCommand(
                        UniversalDevUiCommandKind.SetBoolean,
                        control.Path,
                        boolean: value));
                break;
            }

            case LegacyControlKind.Slider:
                DrawSlider(control, stateKey, label);
                break;

            case LegacyControlKind.Cycler:
            case LegacyControlKind.ExtEnum:
            case LegacyControlKind.Select:
            case LegacyControlKind.PanelSelect:
                DrawChoice(control, stateKey, label);
                break;

            case LegacyControlKind.Integer:
                DrawInteger(control, stateKey, label);
                break;

            case LegacyControlKind.Text:
                DrawText(control, stateKey, label);
                break;

            case LegacyControlKind.Direction:
                DrawDirection(control, stateKey, label);
                break;

            case LegacyControlKind.Color:
                DrawColor(control, stateKey, label);
                break;
        }

        if (ImGui.IsItemHovered())
            DrawTooltip(control);
    }

    private static void DrawSlider(LegacyControlSnapshot control, string stateKey, string label)
    {
        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(
            DevToolNumericScope.Universal,
            stateKey,
            label + "##UniversalSlider_" + stateKey,
            control.Factor,
            0f,
            1f);

        if (edit.Committed)
            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetSlider, control.Path, x: edit.Value));

        if (!string.IsNullOrWhiteSpace(control.ValueText))
        {
            ImGui.SameLine();
            DevToolWidgets.MutedText(control.ValueText);
        }

        if (control.CanReset)
        {
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("继承", "Reset"),
                    "UniversalSliderReset_" + stateKey,
                    DevToolButtonTone.Subtle))
            {
                DevToolNumericWidgets.Discard(DevToolNumericScope.Universal, stateKey);
                Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.ResetSlider, control.Path));
            }
        }
    }

    private static void DrawChoice(LegacyControlSnapshot control, string stateKey, string label)
    {
        string[] options = control.Options ?? Array.Empty<string>();
        string preview = control.SelectedIndex >= 0 && control.SelectedIndex < options.Length
            ? options[control.SelectedIndex]
            : control.ValueText ?? string.Empty;

        if (!ImGui.BeginCombo(label + "##UniversalChoice_" + stateKey, preview)) return;
        for (int i = 0; i < options.Length; i++)
        {
            string option = options[i] ?? string.Empty;
            bool selected = i == control.SelectedIndex;
            if (ImGui.Selectable(option + "##UniversalChoiceItem_" + stateKey + "_" + i, selected))
                Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetChoice, control.Path, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawInteger(LegacyControlSnapshot control, string stateKey, string label)
    {
        ImGui.TextUnformatted(label + ": " + (control.ValueText ?? string.Empty));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("-", "UniversalIntegerMinus_" + stateKey, DevToolButtonTone.Subtle))
            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.IncrementInteger, control.Path, integer: -1));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("+", "UniversalIntegerPlus_" + stateKey, DevToolButtonTone.Subtle))
            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.IncrementInteger, control.Path, integer: 1));
    }

    private static void DrawText(LegacyControlSnapshot control, string stateKey, string label)
    {
        string value = Get(StringEdits, stateKey, control.ValueText ?? string.Empty);
        bool changed = ImGui.InputText(label + "##UniversalText_" + stateKey, ref value, 2048);
        bool active = ImGui.IsItemActive();
        bool commit = ImGui.IsItemDeactivatedAfterEdit();
        StringEdits[stateKey] = value;

        if (commit)
            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetText, control.Path, text: value));
        else if (!changed && !active)
            StringEdits[stateKey] = control.ValueText ?? string.Empty;
    }

    private static void DrawDirection(LegacyControlSnapshot control, string stateKey, string label)
    {
        Num.Vector2 fallback = new(control.X, control.Y);
        Num.Vector2 value = Get(Vector2Edits, stateKey, fallback);
        bool changed = ImGui.InputFloat2(label + "##UniversalDirection_" + stateKey, ref value, "%.3f");
        bool active = ImGui.IsItemActive();
        bool commit = ImGui.IsItemDeactivatedAfterEdit();
        Vector2Edits[stateKey] = value;

        if (commit)
            Send(new UniversalDevUiCommand(
                UniversalDevUiCommandKind.SetDirection,
                control.Path,
                x: value.X,
                y: value.Y));
        else if (!changed && !active)
            Vector2Edits[stateKey] = fallback;
    }

    private static void DrawColor(LegacyControlSnapshot control, string stateKey, string label)
    {
        Num.Vector4 fallback = new(control.X, control.Y, control.Z, control.W);
        Num.Vector4 value = Get(ColorEdits, stateKey, fallback);
        bool changed = ImGui.ColorEdit4(label + "##UniversalColor_" + stateKey, ref value);
        bool active = ImGui.IsItemActive();
        bool commit = ImGui.IsItemDeactivatedAfterEdit();
        ColorEdits[stateKey] = value;

        if (commit)
            Send(new UniversalDevUiCommand(
                UniversalDevUiCommandKind.SetColor,
                control.Path,
                x: value.X,
                y: value.Y,
                z: value.Z,
                w: value.W));
        else if (!changed && !active)
            ColorEdits[stateKey] = fallback;
    }

    private static void DrawTooltip(LegacyControlSnapshot control)
    {
        string text = control.RuntimeType ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(control.Id))
            text += "\nID: " + control.Id;
        text += "\nPath: " + control.Path;
        text += "\nProtocol: " + control.Kind;
        DevToolTooltip.Show(text.Trim());
    }

    private static string SearchQuery()
    {
        if (string.Equals(observedSearch, search, StringComparison.Ordinal)) return normalizedSearch;
        observedSearch = search;
        normalizedSearch = search?.Trim() ?? string.Empty;
        return normalizedSearch;
    }

    private static bool Matches(LegacyControlSnapshot control, string normalizedQuery)
    {
        if (string.IsNullOrEmpty(normalizedQuery)) return true;
        return Contains(control.Label, normalizedQuery) || Contains(control.Id, normalizedQuery) ||
               Contains(control.RuntimeType, normalizedQuery) || Contains(control.Path, normalizedQuery) ||
               Contains(control.ValueText, normalizedQuery) || Contains(control.Kind.ToString(), normalizedQuery);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static T Get<T>(Dictionary<string, T> values, string key, T fallback)
    {
        if (values.TryGetValue(key, out T value)) return value;
        values[key] = fallback;
        return fallback;
    }

    private static void Send(UniversalDevUiCommand command) => UniversalDevUiCommandQueue.Enqueue(command);
}

using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Standalone movable diagnostics surface for the page-agnostic semantic mirror and migration audits.
/// It deliberately stays outside page-specific inspectors and normal editor workflows.
/// </summary>
internal static class UniversalDevUiMirrorWindow
{
    internal static void Draw(Num.Vector2 display)
    {
        // All reflection-backed full audit, mirror capture and downstream diagnostic publication is
        // pumped by the backend diagnostics phase. This frontend only renders detached snapshots.
        if (!DevUiDiagnosticsPolicy.Enabled)
            return;

        UniversalDevUiPresentationSnapshot snapshot = UniversalDevUiPresentationHub.Current;
        if (!snapshot.Available) return;

        float width = Math.Min(520f, Math.Max(360f, display.X * 0.34f));
        float height = Math.Min(680f, Math.Max(320f, display.Y * 0.62f));
        float x = Math.Max(8f, display.X - width - 16f);
        float y = Math.Max(96f, Math.Min(display.Y - height - 8f, 180f));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(320f, 220f),
            new Num.Vector2(Math.Max(320f, display.X - 16f), Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("DevUI 兼容性诊断###UniversalDevUiMirrorWindow", "DevUI Compatibility Diagnostics###UniversalDevUiMirrorWindow"),
                ImGuiWindowFlags.None))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("UniversalDevUiMirror");
        DevUiCompatibilityGateView.Draw();
        DevUiPageCoverageView.Draw();
        DevUiSemanticConformanceView.Draw();
        UniversalDevUiMirrorView.Draw(snapshot);
        ImGui.End();
    }
}
