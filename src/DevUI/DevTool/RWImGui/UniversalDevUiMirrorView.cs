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
    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> StringEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> Vector2Edits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector4> ColorEdits = new(StringComparer.Ordinal);
    private static string search = string.Empty;

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
        if (snapshot.UnmappedProtocolCount > 0)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "仍有 " + snapshot.UnmappedProtocolCount + " 种交互协议未被通用桥覆盖；详见 Migration Coverage。",
                snapshot.UnmappedProtocolCount + " interaction protocol(s) are still unmapped; see Migration Coverage."));
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(DevToolUiSettings.T("搜索##UniversalDevUiSearch", "Search##UniversalDevUiSearch"), ref search, 256);
        ImGui.Spacing();

        int visible = 0;
        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            if (control == null || !Matches(control, search)) continue;
            visible++;
            DrawControl(snapshot, control);
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的通用控件。", "No matching generic controls."));
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
        float value = Get(FloatEdits, stateKey, control.Factor);
        bool changed = ImGui.SliderFloat(label + "##UniversalSlider_" + stateKey, ref value, 0f, 1f, "%.3f");
        bool active = ImGui.IsItemActive();
        bool commit = ImGui.IsItemDeactivatedAfterEdit();
        FloatEdits[stateKey] = value;

        if (commit)
            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetSlider, control.Path, x: value));
        else if (!changed && !active)
            FloatEdits[stateKey] = control.Factor;

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
                Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.ResetSlider, control.Path));
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

    private static bool Matches(LegacyControlSnapshot control, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        return Contains(control.Label, query) || Contains(control.Id, query) ||
               Contains(control.RuntimeType, query) || Contains(control.Path, query) ||
               Contains(control.ValueText, query) || Contains(control.Kind.ToString(), query);
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
