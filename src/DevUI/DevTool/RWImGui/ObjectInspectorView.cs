using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class ObjectInspectorView
{
    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> StringEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> Vector2Edits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector4> ColorEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> LegacySliderEdits = new(StringComparer.Ordinal);

    private static int objectIndex = -1;
    private static int selectionCount;
    private static float positionX;
    private static float positionY;

    internal static void Draw(EditorInspectorSnapshot inspector)
    {
        inspector ??= new EditorInspectorSnapshot();
        if (!inspector.HasSelection)
        {
            Reset(-1);
            DevToolWidgets.MutedText(DevToolUiSettings.T("未选择物件。", "Nothing selected."));
            return;
        }

        // Touching the optional RegionKit adapter lazily registers it in the core registry.
        // The first frame may still contain the legacy snapshot; the next publication is native.
        _ = RegionKitAdvancedShaderInspectorAdapter.IsDataTypeName(inspector.DataType);

        if (objectIndex != inspector.ObjectIndex || selectionCount != inspector.SelectionCount)
            Reset(inspector.ObjectIndex, inspector.X, inspector.Y, inspector.SelectionCount);

        bool collapseAll = DevToolWidgets.PaneTitleWithAction(
            DevToolUiSettings.T("物件", "Object"),
            DevToolUiSettings.T("折叠所有", "Collapse All"),
            "ObjectInspectorCollapseAll");

        DrawIdentity(inspector);

        if (collapseAll)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader(
                DevToolUiSettings.T("编辑内容##ObjectInspectorDetails", "Object Details##ObjectInspectorDetails"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawTransform(inspector);
        DrawProperties(inspector);
        DrawCompatibility(inspector);
        DrawActions(inspector);
    }

    private static void DrawIdentity(EditorInspectorSnapshot inspector)
    {
        EditorObjectTypeSnapshot metadata = inspector.SelectionCount == 1
            ? FindMetadata(inspector.Type)
            : null;

        DevToolWidgets.SourceHeader(
            inspector.Type,
            ObjectSourceColor(metadata?.Source),
            1.28f,
            1f);

        if (metadata != null)
        {
            string source = string.IsNullOrWhiteSpace(metadata.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown source")
                : metadata.Source;
            string category = string.IsNullOrWhiteSpace(metadata.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : metadata.Category;
            DevToolWidgets.MutedText(source + "  ·  " + category);
        }
        else if (inspector.SelectionCount > 1)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "正在编辑当前多选物件共有的属性。",
                "Editing properties shared by the current selection."), true);
        }

        if (inspector.SelectionCount == 1 && !string.IsNullOrWhiteSpace(inspector.DataType))
        {
            if (ImGui.IsItemHovered())
                DevToolTooltip.Show(inspector.DataType);
        }
    }

    private static void DrawTransform(EditorInspectorSnapshot inspector)
    {
        DevToolWidgets.SectionHeader(inspector.SelectionCount > 1
            ? DevToolUiSettings.T("变换 · 组锚点", "TRANSFORM · GROUP ANCHOR")
            : DevToolUiSettings.T("变换", "TRANSFORM"));

        if (!ImGui.IsAnyItemActive())
            SynchronizePosition(inspector);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputFloat("X##DevToolPosX", ref positionX, 1f, 20f, "%.1f");
        bool xCommit = ImGui.IsItemDeactivatedAfterEdit();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputFloat("Y##DevToolPosY", ref positionY, 1f, 20f, "%.1f");
        bool yCommit = ImGui.IsItemDeactivatedAfterEdit();

        if (xCommit || yCommit)
            SendPosition(inspector);
    }

    private static void DrawProperties(EditorInspectorSnapshot inspector)
    {
        EditorPropertySnapshot[] properties = inspector.Properties ?? Array.Empty<EditorPropertySnapshot>();
        if (properties.Length == 0)
        {
            if (inspector.SelectionCount > 1)
            {
                DevToolWidgets.SectionHeader(DevToolUiSettings.T("属性", "PROPERTIES"));
                DevToolWidgets.MutedText(DevToolUiSettings.T(
                    "所选物件之间没有共同的可编辑属性。",
                    "No editable properties are shared by every selected object."), true);
            }
            return;
        }

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("属性", "PROPERTIES"));
        string group = null;
        for (int i = 0; i < properties.Length; i++)
        {
            EditorPropertySnapshot property = properties[i];
            if (!string.Equals(group, property.Group, StringComparison.Ordinal))
            {
                group = property.Group;
                if (!string.IsNullOrWhiteSpace(group))
                {
                    ImGui.Spacing();
                    DevToolWidgets.MutedText(group);
                }
            }
            DrawProperty(inspector, property);
        }
    }

    private static void DrawProperty(EditorInspectorSnapshot inspector, EditorPropertySnapshot property)
    {
        if (property == null || string.IsNullOrEmpty(property.Key)) return;

        bool mixed = IsMixed(inspector, property.Key);
        string stateKey = inspector.ObjectIndex + ":" + inspector.SelectionCount + ":" + property.Key;
        string displayName = property.DisplayName + (mixed ? DevToolUiSettings.T("  [混合]", "  [Mixed]") : string.Empty);
        string label = displayName + "##DevToolProperty_" + stateKey;

        switch (property.Kind)
        {
            case EditorPropertyKind.ReadOnly:
                DevToolWidgets.MutedText(property.DisplayName);
                ImGui.TextWrapped(property.StringValue ?? string.Empty);
                break;

            case EditorPropertyKind.Float:
            {
                float value = Get(FloatEdits, stateKey, property.X);
                bool changed = property.HasRange
                    ? ImGui.SliderFloat(label, ref value, property.Min, property.Max, "%.3f")
                    : ImGui.InputFloat(label, ref value, property.Step <= 0f ? 0.1f : property.Step, 0f, "%.3f");
                FloatEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
                else if (!changed && !ImGui.IsItemActive())
                    FloatEdits[stateKey] = property.X;
                break;
            }

            case EditorPropertyKind.Integer:
            {
                int value = Get(IntEdits, stateKey, property.IntegerValue);
                bool changed = property.HasRange
                    ? ImGui.SliderInt(label, ref value, (int)property.Min, (int)property.Max)
                    : ImGui.InputInt(label, ref value, Math.Max(1, (int)property.Step));
                IntEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));
                else if (!changed && !ImGui.IsItemActive())
                    IntEdits[stateKey] = property.IntegerValue;
                break;
            }

            case EditorPropertyKind.Boolean:
            {
                bool value = property.BooleanValue;
                if (ImGui.Checkbox(label, ref value))
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: value));
                break;
            }

            case EditorPropertyKind.String:
            {
                string value = Get(StringEdits, stateKey, property.StringValue ?? string.Empty);
                bool changed = ImGui.InputText(label, ref value, 1024);
                StringEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.String, text: value));
                else if (!changed && !ImGui.IsItemActive())
                    StringEdits[stateKey] = property.StringValue ?? string.Empty;
                break;
            }

            case EditorPropertyKind.Vector2:
            {
                Num.Vector2 value = Get(Vector2Edits, stateKey, new Num.Vector2(property.X, property.Y));
                bool changed = ImGui.InputFloat2(label, ref value, "%.2f");
                Vector2Edits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(inspector, property.Key,
                        new EditorPropertyValue(EditorPropertyKind.Vector2, x: value.X, y: value.Y));
                else if (!changed && !ImGui.IsItemActive())
                    Vector2Edits[stateKey] = new Num.Vector2(property.X, property.Y);
                break;
            }

            case EditorPropertyKind.Color:
            {
                Num.Vector4 value = Get(ColorEdits, stateKey,
                    new Num.Vector4(property.X, property.Y, property.Z, property.W));
                bool changed = ImGui.ColorEdit4(label, ref value);
                ColorEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(inspector, property.Key,
                        new EditorPropertyValue(EditorPropertyKind.Color,
                            x: value.X, y: value.Y, z: value.Z, w: value.W));
                else if (!changed && !ImGui.IsItemActive())
                    ColorEdits[stateKey] = new Num.Vector4(property.X, property.Y, property.Z, property.W);
                break;
            }

            case EditorPropertyKind.Enum:
                DrawEnum(inspector, property, label, mixed);
                break;

            case EditorPropertyKind.Action:
                if (DevToolWidgets.ActionButton(displayName, "InspectorAction_" + stateKey, DevToolButtonTone.Normal, true))
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Action));
                break;
        }

        if (ImGui.IsItemHovered())
        {
            if (mixed && !string.IsNullOrEmpty(property.Source))
                DevToolTooltip.Show(DevToolUiSettings.T("所选物件的值不同。\n", "Selected objects contain different values.\n") + property.Source);
            else if (mixed)
                DevToolTooltip.Show(DevToolUiSettings.T("所选物件的值不同。", "Selected objects contain different values."));
            else if (!string.IsNullOrEmpty(property.Source))
                DevToolTooltip.Show(property.Source);
        }
    }

    private static void DrawEnum(EditorInspectorSnapshot inspector, EditorPropertySnapshot property, string label, bool mixed)
    {
        string[] options = property.Options ?? Array.Empty<string>();
        string preview = mixed
            ? DevToolUiSettings.T("<混合>", "<Mixed>")
            : property.IntegerValue >= 0 && property.IntegerValue < options.Length
                ? options[property.IntegerValue]
                : property.StringValue ?? string.Empty;

        if (!ImGui.BeginCombo(label, preview)) return;

        string filterKey = "enum-search:" + inspector.ObjectIndex + ":" + property.Key;
        string filter = Get(StringEdits, filterKey, string.Empty);
        if (options.Length >= 24)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText(DevToolUiSettings.T("搜索##", "Search##") + filterKey, ref filter, 256);
            StringEdits[filterKey] = filter;
            ImGui.Separator();
        }

        for (int i = 0; i < options.Length; i++)
        {
            string option = options[i] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(filter) &&
                option.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            bool selected = !mixed && i == property.IntegerValue;
            if (ImGui.Selectable(option + "##" + label + i, selected))
                SendProperty(inspector, property.Key,
                    new EditorPropertyValue(EditorPropertyKind.Enum, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawCompatibility(EditorInspectorSnapshot inspector)
    {
        // AdvancedShader is now represented completely by first-class inspector properties.
        // Do not show its old RegionKit panel controls in parallel.
        if (RegionKitAdvancedShaderInspectorAdapter.IsDataTypeName(inspector.DataType)) return;

        LegacyControlSnapshot[] controls = inspector.LegacyControls ?? Array.Empty<LegacyControlSnapshot>();
        if (controls.Length == 0 && !inspector.LegacyUiAvailable) return;

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("高级 / 兼容", "ADVANCED / COMPATIBILITY"));
        if (!ImGui.CollapsingHeader(DevToolUiSettings.T(
                "DevInterface 兼容控件##ObjectLegacyControls",
                "DevInterface Compatibility Controls##ObjectLegacyControls")))
        {
            DrawLegacyFallbackButton(inspector);
            return;
        }

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "通过原控件行为边界驱动布尔、Button、Slider、Cycler、ExtEnum、Integer、Select、文本、方向与颜色控件；无法证明等价的复合控件仍保持未映射。",
            "Booleans, buttons, sliders, cyclers, ExtEnums, integers, selects, text, direction and color controls are delegated through their original behavior boundaries; composite controls without proven equivalence remain unmapped."), true);

        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            string stateKey = inspector.ObjectIndex + ":legacy:" + control.Path;
            string visibleLabel = string.IsNullOrEmpty(control.Label) ? control.Id : control.Label;

            switch (control.Kind)
            {
                case LegacyControlKind.Boolean:
                    DrawLegacyBoolean(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Button:
                    DrawLegacyButton(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Slider:
                    DrawLegacySlider(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Cycler:
                    DrawLegacyCycler(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.ExtEnum:
                    DrawLegacyExtEnum(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Integer:
                    DrawLegacyInteger(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Select:
                    DrawLegacySelect(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.PanelSelect:
                    DrawLegacyPanelSelect(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Text:
                    DrawLegacyText(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Direction:
                    DrawLegacyDirection(inspector, control, stateKey, visibleLabel);
                    break;
                case LegacyControlKind.Color:
                    DrawLegacyColor(inspector, control, stateKey, visibleLabel);
                    break;
            }
        }

        DrawLegacyFallbackButton(inspector);
    }

    private static void DrawLegacyBoolean(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        bool value = control.BooleanValue;
        string label = visibleLabel + "##DevToolLegacyBoolean_" + stateKey;
        if (ImGui.Checkbox(label, ref value))
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: control.Path));
        }

        if (!string.IsNullOrWhiteSpace(control.ValueText))
        {
            ImGui.SameLine();
            DevToolWidgets.MutedText(control.ValueText);
        }
    }

    private static void DrawLegacyButton(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        if (DevToolWidgets.ActionButton(visibleLabel, "LegacyButton_" + stateKey, DevToolButtonTone.Normal))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: control.Path));
    }

    private static void DrawLegacySlider(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        string label = visibleLabel + "##DevToolLegacy_" + stateKey;
        float factor = Get(LegacySliderEdits, stateKey, control.Factor);
        bool changed = ImGui.SliderFloat(label, ref factor, 0f, 1f, "%.3f");
        LegacySliderEdits[stateKey] = factor;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacySlider,
                inspector.ObjectIndex,
                text: control.Path,
                x: factor));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            LegacySliderEdits[stateKey] = control.Factor;
        }

        if (!string.IsNullOrWhiteSpace(control.ValueText))
        {
            ImGui.SameLine();
            DevToolWidgets.MutedText(control.ValueText);
        }

        if (control.CanReset)
        {
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("重置", "Reset"),
                    "LegacyReset_" + stateKey,
                    DevToolButtonTone.Subtle))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.ResetLegacySlider,
                    inspector.ObjectIndex,
                    text: control.Path));
        }
    }

    private static void DrawLegacyCycler(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        DrawLegacyChoice(
            inspector,
            control,
            stateKey,
            visibleLabel,
            "LegacyCyclerOption_",
            i => LegacyDevInterfaceBridge.CyclerAction(control.Path, i));
    }

    private static void DrawLegacyExtEnum(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        DrawLegacyChoice(
            inspector,
            control,
            stateKey,
            visibleLabel,
            "LegacyExtEnumOption_",
            i => LegacyDevInterfaceBridge.ExtEnumAction(control.Path, i));
    }

    private static void DrawLegacySelect(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        DrawLegacyChoice(
            inspector,
            control,
            stateKey,
            visibleLabel,
            "LegacySelectOption_",
            i => LegacyDevInterfaceBridge.SelectAction(control.Path, i));
    }

    private static void DrawLegacyPanelSelect(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        DrawLegacyChoice(
            inspector,
            control,
            stateKey,
            visibleLabel,
            "LegacyPanelSelectOption_",
            i => LegacyDevInterfaceBridge.PanelSelectAction(control.Path, i));
    }

    private static void DrawLegacyChoice(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel,
        string optionIdPrefix,
        Func<int, string> actionFactory)
    {
        string[] options = control.Options ?? Array.Empty<string>();
        string preview = control.SelectedIndex >= 0 && control.SelectedIndex < options.Length
            ? options[control.SelectedIndex]
            : control.ValueText ?? string.Empty;
        string label = visibleLabel + "##DevToolLegacyChoice_" + stateKey;

        if (!ImGui.BeginCombo(label, preview)) return;
        for (int i = 0; i < options.Length; i++)
        {
            bool selected = i == control.SelectedIndex;
            if (ImGui.Selectable(options[i] + "##" + optionIdPrefix + stateKey + "_" + i, selected))
            {
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.InvokeLegacyButton,
                    inspector.ObjectIndex,
                    text: actionFactory(i)));
            }
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawLegacyInteger(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        string value = string.IsNullOrWhiteSpace(control.ValueText) ? "-" : control.ValueText;
        DevToolWidgets.MutedText(visibleLabel + ":  " + value);

        ImGuiIOPtr io = ImGui.GetIO();
        int step = io.KeyCtrl
            ? (io.KeyShift ? 1000 : 100)
            : (io.KeyShift ? 10 : 1);

        if (DevToolWidgets.ActionButton("-", "LegacyIntegerLess_" + stateKey, DevToolButtonTone.Subtle))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: LegacyDevInterfaceBridge.IntegerAction(control.Path, -step)));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("+", "LegacyIntegerMore_" + stateKey, DevToolButtonTone.Normal))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: LegacyDevInterfaceBridge.IntegerAction(control.Path, step)));

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "步长：默认 1，Shift=10，Ctrl=100，Ctrl+Shift=1000。",
                "Step: 1 by default, Shift=10, Ctrl=100, Ctrl+Shift=1000."));
    }

    private static void DrawLegacyText(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        string label = visibleLabel + "##DevToolLegacyText_" + stateKey;
        string value = Get(StringEdits, stateKey, control.ValueText ?? string.Empty);
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.InputText(label, ref value, 1024);
        StringEdits[stateKey] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacyText,
                inspector.ObjectIndex,
                text: control.Path,
                propertyValue: new EditorPropertyValue(EditorPropertyKind.String, text: value)));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            StringEdits[stateKey] = control.ValueText ?? string.Empty;
        }
    }

    private static void DrawLegacyDirection(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        string editKey = "legacy-direction:" + stateKey;
        Num.Vector2 value = Get(Vector2Edits, editKey, new Num.Vector2(control.X, control.Y));
        string label = visibleLabel + "##DevToolLegacyDirection_" + stateKey;
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.InputFloat2(label, ref value, "%.3f");
        Vector2Edits[editKey] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacyDirection,
                inspector.ObjectIndex,
                text: control.Path,
                x: value.X,
                y: value.Y));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            Vector2Edits[editKey] = new Num.Vector2(control.X, control.Y);
        }

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "提交时会标准化为单位方向；零向量按向上处理。",
                "Normalized to a unit direction on commit; a zero vector becomes up."));
    }

    private static void DrawLegacyColor(
        EditorInspectorSnapshot inspector,
        LegacyControlSnapshot control,
        string stateKey,
        string visibleLabel)
    {
        string editKey = "legacy-color:" + stateKey;
        Num.Vector4 value = Get(
            ColorEdits,
            editKey,
            new Num.Vector4(control.X, control.Y, control.Z, control.W));
        string label = visibleLabel + "##DevToolLegacyColor_" + stateKey;
        bool changed = ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoAlpha);
        ColorEdits[editKey] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacyColor,
                inspector.ObjectIndex,
                text: control.Path,
                propertyValue: new EditorPropertyValue(
                    EditorPropertyKind.Color,
                    x: value.X,
                    y: value.Y,
                    z: value.Z,
                    w: value.W)));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            ColorEdits[editKey] = new Num.Vector4(control.X, control.Y, control.Z, control.W);
        }
    }

    private static void DrawLegacyFallbackButton(EditorInspectorSnapshot inspector)
    {
        if (!inspector.LegacyUiAvailable) return;

        ImGui.Spacing();
        string label = inspector.LegacyUiVisible
            ? DevToolUiSettings.T("隐藏完整原版 DevUI", "Hide Full Original DevUI")
            : DevToolUiSettings.T("显示完整原版 DevUI", "Show Full Original DevUI");
        if (DevToolWidgets.ActionButton(label, "DevToolLegacyFallback", DevToolButtonTone.Primary, true))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.ToggleLegacyUi));

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "仅作为迁移期诊断后门。Coverage 仍标记为未映射的复合控件必须继续迁移。",
                "Migration-only diagnostic escape hatch. Composite controls still reported as unmapped by Coverage must continue to be migrated."));
    }

    private static void DrawActions(EditorInspectorSnapshot inspector)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("操作", "ACTIONS"));
        if (inspector.SelectionCount > 1)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("复制所选  Ctrl+D", "Duplicate Selection  Ctrl+D"),
                    "DuplicateObjectSelection",
                    DevToolButtonTone.Normal))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DuplicateSelection));

            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除所选", "Delete Selection"),
                    "DeleteObjectSelection",
                    DevToolButtonTone.Danger))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteSelection));
            return;
        }

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("删除物件", "Delete Object"),
                "DeleteObject",
                DevToolButtonTone.Danger,
                true))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteObject, inspector.ObjectIndex));
    }

    private static EditorObjectTypeSnapshot FindMetadata(string type)
    {
        EditorObjectTypeSnapshot[] library = EditorPresentationHub.Current.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        for (int i = 0; i < library.Length; i++)
            if (string.Equals(library[i].Type, type, StringComparison.Ordinal)) return library[i];
        return null;
    }

    private static Num.Vector4 ObjectSourceColor(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new Num.Vector4(0.78f, 0.82f, 0.90f, 1f);
        if (source.IndexOf("DryCycle", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.36f, 0.72f, 1f, 1f);
        if (source.IndexOf("RegionKit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.StartsWith("RK", StringComparison.OrdinalIgnoreCase))
            return new Num.Vector4(1f, 0.70f, 0.34f, 1f);
        if (source.IndexOf("Vanilla", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("Rain World", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.88f, 0.88f, 0.88f, 1f);
        return new Num.Vector4(0.78f, 0.72f, 1f, 1f);
    }

    private static void SendPosition(EditorInspectorSnapshot inspector)
    {
        EditorUiCommandKind kind = inspector.SelectionCount > 1
            ? EditorUiCommandKind.SetSelectionPosition
            : EditorUiCommandKind.SetObjectPosition;

        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            kind,
            inspector.ObjectIndex,
            x: positionX,
            y: positionY));
    }

    private static void SendProperty(EditorInspectorSnapshot inspector, string key, EditorPropertyValue value)
    {
        EditorUiCommandKind kind = inspector.SelectionCount > 1
            ? EditorUiCommandKind.SetSelectionProperty
            : EditorUiCommandKind.SetObjectProperty;

        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            kind,
            inspector.ObjectIndex,
            text: key,
            propertyValue: value));
    }

    private static bool IsMixed(EditorInspectorSnapshot inspector, string key)
    {
        string[] mixed = inspector?.MixedPropertyKeys ?? Array.Empty<string>();
        for (int i = 0; i < mixed.Length; i++)
            if (string.Equals(mixed[i], key, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void SynchronizePosition(EditorInspectorSnapshot inspector)
    {
        if (Math.Abs(positionX - inspector.X) > 0.0001f) positionX = inspector.X;
        if (Math.Abs(positionY - inspector.Y) > 0.0001f) positionY = inspector.Y;
    }

    private static void Reset(int nextObjectIndex, float x = 0f, float y = 0f, int nextSelectionCount = 0)
    {
        objectIndex = nextObjectIndex;
        selectionCount = nextSelectionCount;
        positionX = x;
        positionY = y;
        FloatEdits.Clear();
        IntEdits.Clear();
        StringEdits.Clear();
        Vector2Edits.Clear();
        ColorEdits.Clear();
        LegacySliderEdits.Clear();
    }

    private static TValue Get<TValue>(Dictionary<string, TValue> dictionary, string key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
