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
    private sealed class PropertyBinding
    {
        internal EditorPropertySnapshot Property;
        internal bool Mixed;
        internal string StateKey;
        internal string DisplayName;
        internal string Label;
        internal string ActionId;
        internal string EnumFilterKey;
        internal string EnumSearchLabel;
        internal string[] EnumOptionLabels = Array.Empty<string>();
    }

    private sealed class LegacyBinding
    {
        internal LegacyControlSnapshot Control;
        internal string StateKey;
        internal string VisibleLabel;
        internal string BooleanLabel;
        internal string ButtonId;
        internal string SliderLabel;
        internal string ResetId;
        internal string ChoiceLabel;
        internal string[] ChoiceOptionLabels = Array.Empty<string>();
        internal string IntegerDisplay;
        internal string IntegerLessId;
        internal string IntegerMoreId;
        internal string TextLabel;
        internal string DirectionEditKey;
        internal string DirectionLabel;
        internal string ColorEditKey;
        internal string ColorLabel;
    }

    private static readonly Dictionary<string, string> StringEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> Vector2Edits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector4> ColorEdits = new(StringComparer.Ordinal);
    private static readonly HashSet<string> MixedKeyScratch = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, EditorObjectTypeSnapshot> MetadataByType = new(StringComparer.Ordinal);

    private static EditorPropertySnapshot[] projectedProperties;
    private static string[] projectedMixedKeys;
    private static int projectedPropertyObjectIndex = int.MinValue;
    private static int projectedPropertySelectionCount = -1;
    private static bool projectedPropertyChinese;
    private static PropertyBinding[] propertyBindings = Array.Empty<PropertyBinding>();

    private static LegacyControlSnapshot[] projectedLegacyControls;
    private static int projectedLegacyObjectIndex = int.MinValue;
    private static LegacyBinding[] legacyBindings = Array.Empty<LegacyBinding>();

    private static EditorObjectTypeSnapshot[] metadataSource;
    private static EditorObjectTypeSnapshot identityMetadata;
    private static bool identityChinese;
    private static string identitySubtitle = string.Empty;

    private static int objectIndex = -1;
    private static int selectionCount;
    private static float positionX;
    private static float positionY;

    internal static void ResetRetainedState()
    {
        Reset(-1);
        MixedKeyScratch.Clear();
        MetadataByType.Clear();

        projectedProperties = null;
        projectedMixedKeys = null;
        projectedPropertyObjectIndex = int.MinValue;
        projectedPropertySelectionCount = -1;
        projectedPropertyChinese = false;
        propertyBindings = Array.Empty<PropertyBinding>();

        projectedLegacyControls = null;
        projectedLegacyObjectIndex = int.MinValue;
        legacyBindings = Array.Empty<LegacyBinding>();

        metadataSource = null;
        identityMetadata = null;
        identityChinese = false;
        identitySubtitle = string.Empty;
    }

    internal static void Draw(EditorInspectorSnapshot inspector)
    {
        inspector ??= new EditorInspectorSnapshot();
        if (!inspector.HasSelection)
        {
            Reset(-1);
            DevToolWidgets.MutedText(DevToolUiSettings.T("未选择物件。", "Nothing selected."));
            return;
        }

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
            bool chinese = DevToolUiSettings.IsChinese;
            if (!ReferenceEquals(identityMetadata, metadata) || identityChinese != chinese)
            {
                string source = string.IsNullOrWhiteSpace(metadata.Source)
                    ? DevToolUiSettings.T("未知来源", "Unknown source")
                    : metadata.Source;
                string category = string.IsNullOrWhiteSpace(metadata.Category)
                    ? DevToolUiSettings.T("未分类", "Unsorted")
                    : metadata.Category;
                identityMetadata = metadata;
                identityChinese = chinese;
                identitySubtitle = source + "  ·  " + category;
            }
            DevToolWidgets.MutedText(identitySubtitle);
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

        ImGui.SetNextItemWidth(-1f);
        DevToolNumericEditResult<float> xEdit = DevToolNumericWidgets.InputFloat(
            DevToolNumericScope.ObjectTransform,
            "x",
            "X##DevToolPosX",
            inspector.X,
            1f,
            "%.1f",
            inspector.ObjectIndex);
        positionX = xEdit.Value;

        ImGui.SetNextItemWidth(-1f);
        DevToolNumericEditResult<float> yEdit = DevToolNumericWidgets.InputFloat(
            DevToolNumericScope.ObjectTransform,
            "y",
            "Y##DevToolPosY",
            inspector.Y,
            1f,
            "%.1f",
            inspector.ObjectIndex);
        positionY = yEdit.Value;

        if (xEdit.Committed || yEdit.Committed)
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

        EnsurePropertyBindings(inspector, properties);
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("属性", "PROPERTIES"));
        string group = null;
        for (int i = 0; i < propertyBindings.Length; i++)
        {
            PropertyBinding binding = propertyBindings[i];
            EditorPropertySnapshot property = binding?.Property;
            if (property == null) continue;
            if (!string.Equals(group, property.Group, StringComparison.Ordinal))
            {
                group = property.Group;
                if (!string.IsNullOrWhiteSpace(group))
                {
                    ImGui.Spacing();
                    DevToolWidgets.MutedText(group);
                }
            }
            DrawProperty(inspector, binding);
        }
    }

    private static void DrawProperty(EditorInspectorSnapshot inspector, PropertyBinding binding)
    {
        EditorPropertySnapshot property = binding?.Property;
        if (property == null || string.IsNullOrEmpty(property.Key)) return;

        bool mixed = binding.Mixed;
        string stateKey = binding.StateKey;
        string displayName = binding.DisplayName;
        string label = binding.Label;

        switch (property.Kind)
        {
            case EditorPropertyKind.ReadOnly:
                DevToolWidgets.MutedText(property.DisplayName);
                ImGui.TextWrapped(property.StringValue ?? string.Empty);
                break;

            case EditorPropertyKind.Float:
            {
                DevToolNumericEditResult<float> edit = property.HasRange
                    ? DevToolNumericWidgets.SliderFloat(
                        DevToolNumericScope.ObjectProperty, stateKey, label, property.X, property.Min, property.Max,
                        instance: inspector.ObjectIndex)
                    : DevToolNumericWidgets.InputFloat(
                        DevToolNumericScope.ObjectProperty, stateKey, label, property.X,
                        property.Step <= 0f ? 0.1f : property.Step, "%.3f", inspector.ObjectIndex);
                if (edit.Committed)
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value));
                break;
            }

            case EditorPropertyKind.Integer:
            {
                DevToolNumericEditResult<int> edit = property.HasRange
                    ? DevToolNumericWidgets.SliderInt(
                        DevToolNumericScope.ObjectProperty, stateKey, label, property.IntegerValue,
                        (int)property.Min, (int)property.Max, instance: inspector.ObjectIndex)
                    : DevToolNumericWidgets.InputInt(
                        DevToolNumericScope.ObjectProperty, stateKey, label, property.IntegerValue,
                        Math.Max(1, (int)property.Step), instance: inspector.ObjectIndex);
                if (edit.Committed)
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: edit.Value));
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
                DrawEnum(inspector, binding);
                break;

            case EditorPropertyKind.Action:
                if (DevToolWidgets.ActionButton(displayName, binding.ActionId, DevToolButtonTone.Normal, true))
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

    private static void DrawEnum(EditorInspectorSnapshot inspector, PropertyBinding binding)
    {
        EditorPropertySnapshot property = binding.Property;
        string[] options = property.Options ?? Array.Empty<string>();
        string preview = binding.Mixed
            ? DevToolUiSettings.T("<混合>", "<Mixed>")
            : property.IntegerValue >= 0 && property.IntegerValue < options.Length
                ? options[property.IntegerValue]
                : property.StringValue ?? string.Empty;

        if (!ImGui.BeginCombo(binding.Label, preview)) return;

        string filter = Get(StringEdits, binding.EnumFilterKey, string.Empty);
        if (options.Length >= 24)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText(binding.EnumSearchLabel, ref filter, 256);
            StringEdits[binding.EnumFilterKey] = filter;
            ImGui.Separator();
        }

        for (int i = 0; i < options.Length; i++)
        {
            string option = options[i] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(filter) &&
                option.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            bool selected = !binding.Mixed && i == property.IntegerValue;
            if (ImGui.Selectable(binding.EnumOptionLabels[i], selected))
                SendProperty(inspector, property.Key,
                    new EditorPropertyValue(EditorPropertyKind.Enum, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawCompatibility(EditorInspectorSnapshot inspector)
    {
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

        EnsureLegacyBindings(inspector, controls);
        for (int i = 0; i < legacyBindings.Length; i++)
        {
            LegacyBinding binding = legacyBindings[i];
            LegacyControlSnapshot control = binding.Control;
            switch (control.Kind)
            {
                case LegacyControlKind.Boolean:
                    DrawLegacyBoolean(inspector, binding);
                    break;
                case LegacyControlKind.Button:
                    DrawLegacyButton(inspector, binding);
                    break;
                case LegacyControlKind.Slider:
                    DrawLegacySlider(inspector, binding);
                    break;
                case LegacyControlKind.Cycler:
                case LegacyControlKind.ExtEnum:
                case LegacyControlKind.Select:
                case LegacyControlKind.PanelSelect:
                    DrawLegacyChoice(inspector, binding);
                    break;
                case LegacyControlKind.Integer:
                    DrawLegacyInteger(inspector, binding);
                    break;
                case LegacyControlKind.Text:
                    DrawLegacyText(inspector, binding);
                    break;
                case LegacyControlKind.Direction:
                    DrawLegacyDirection(inspector, binding);
                    break;
                case LegacyControlKind.Color:
                    DrawLegacyColor(inspector, binding);
                    break;
            }
        }

        DrawLegacyFallbackButton(inspector);
    }

    private static void DrawLegacyBoolean(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        bool value = control.BooleanValue;
        if (ImGui.Checkbox(binding.BooleanLabel, ref value))
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: control.Path,
                stableId: inspector.ObjectStableId));
        }

        if (!string.IsNullOrWhiteSpace(control.ValueText))
        {
            ImGui.SameLine();
            DevToolWidgets.MutedText(control.ValueText);
        }
    }

    private static void DrawLegacyButton(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        if (DevToolWidgets.ActionButton(binding.VisibleLabel, binding.ButtonId, DevToolButtonTone.Normal))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: control.Path,
                stableId: inspector.ObjectStableId));
    }

    private static void DrawLegacySlider(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(
            DevToolNumericScope.ObjectLegacy,
            binding.StateKey,
            binding.SliderLabel,
            control.Factor,
            0f,
            1f,
            instance: inspector.ObjectIndex);
        if (edit.Committed)
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacySlider,
                inspector.ObjectIndex,
                text: control.Path,
                x: edit.Value,
                stableId: inspector.ObjectStableId));
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
                    binding.ResetId,
                    DevToolButtonTone.Subtle))
            {
                DevToolNumericWidgets.Discard(DevToolNumericScope.ObjectLegacy, binding.StateKey, inspector.ObjectIndex);
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.ResetLegacySlider,
                    inspector.ObjectIndex,
                    text: control.Path,
                    stableId: inspector.ObjectStableId));
            }
        }
    }

    private static void DrawLegacyChoice(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        string[] options = control.Options ?? Array.Empty<string>();
        string preview = control.SelectedIndex >= 0 && control.SelectedIndex < options.Length
            ? options[control.SelectedIndex]
            : control.ValueText ?? string.Empty;

        if (!ImGui.BeginCombo(binding.ChoiceLabel, preview)) return;
        for (int i = 0; i < options.Length; i++)
        {
            bool selected = i == control.SelectedIndex;
            if (ImGui.Selectable(binding.ChoiceOptionLabels[i], selected))
            {
                string action = control.Kind switch
                {
                    LegacyControlKind.Cycler => LegacyDevInterfaceBridge.CyclerAction(control.Path, i),
                    LegacyControlKind.ExtEnum => LegacyDevInterfaceBridge.ExtEnumAction(control.Path, i),
                    LegacyControlKind.Select => LegacyDevInterfaceBridge.SelectAction(control.Path, i),
                    LegacyControlKind.PanelSelect => LegacyDevInterfaceBridge.PanelSelectAction(control.Path, i),
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(action))
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.InvokeLegacyButton,
                        inspector.ObjectIndex,
                        text: action,
                        stableId: inspector.ObjectStableId));
                }
            }
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawLegacyInteger(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        DevToolWidgets.MutedText(binding.IntegerDisplay);

        ImGuiIOPtr io = ImGui.GetIO();
        int step = io.KeyCtrl
            ? (io.KeyShift ? 1000 : 100)
            : (io.KeyShift ? 10 : 1);

        if (DevToolWidgets.ActionButton("-", binding.IntegerLessId, DevToolButtonTone.Subtle))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: LegacyDevInterfaceBridge.IntegerAction(control.Path, -step),
                stableId: inspector.ObjectStableId));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("+", binding.IntegerMoreId, DevToolButtonTone.Normal))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.InvokeLegacyButton,
                inspector.ObjectIndex,
                text: LegacyDevInterfaceBridge.IntegerAction(control.Path, step),
                stableId: inspector.ObjectStableId));

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "步长：默认 1，Shift=10，Ctrl=100，Ctrl+Shift=1000。",
                "Step: 1 by default, Shift=10, Ctrl=100, Ctrl+Shift=1000."));
    }

    private static void DrawLegacyText(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        string value = Get(StringEdits, binding.StateKey, control.ValueText ?? string.Empty);
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.InputText(binding.TextLabel, ref value, 1024);
        StringEdits[binding.StateKey] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacyText,
                inspector.ObjectIndex,
                text: control.Path,
                propertyValue: new EditorPropertyValue(EditorPropertyKind.String, text: value),
                stableId: inspector.ObjectStableId));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            StringEdits[binding.StateKey] = control.ValueText ?? string.Empty;
        }
    }

    private static void DrawLegacyDirection(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        Num.Vector2 value = Get(Vector2Edits, binding.DirectionEditKey, new Num.Vector2(control.X, control.Y));
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.InputFloat2(binding.DirectionLabel, ref value, "%.3f");
        Vector2Edits[binding.DirectionEditKey] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetLegacyDirection,
                inspector.ObjectIndex,
                text: control.Path,
                x: value.X,
                y: value.Y,
                stableId: inspector.ObjectStableId));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            Vector2Edits[binding.DirectionEditKey] = new Num.Vector2(control.X, control.Y);
        }

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "提交时会标准化为单位方向；零向量按向上处理。",
                "Normalized to a unit direction on commit; a zero vector becomes up."));
    }

    private static void DrawLegacyColor(EditorInspectorSnapshot inspector, LegacyBinding binding)
    {
        LegacyControlSnapshot control = binding.Control;
        Num.Vector4 value = Get(
            ColorEdits,
            binding.ColorEditKey,
            new Num.Vector4(control.X, control.Y, control.Z, control.W));
        bool changed = ImGui.ColorEdit4(binding.ColorLabel, ref value, ImGuiColorEditFlags.NoAlpha);
        ColorEdits[binding.ColorEditKey] = value;

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
                    w: value.W),
                stableId: inspector.ObjectStableId));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            ColorEdits[binding.ColorEditKey] = new Num.Vector4(control.X, control.Y, control.Z, control.W);
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
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.DeleteObject,
                inspector.ObjectIndex,
                stableId: inspector.ObjectStableId));
    }

    private static void EnsurePropertyBindings(EditorInspectorSnapshot inspector, EditorPropertySnapshot[] properties)
    {
        string[] mixedKeys = inspector.MixedPropertyKeys ?? Array.Empty<string>();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedProperties, properties) &&
            ReferenceEquals(projectedMixedKeys, mixedKeys) &&
            projectedPropertyObjectIndex == inspector.ObjectIndex &&
            projectedPropertySelectionCount == inspector.SelectionCount &&
            projectedPropertyChinese == chinese)
            return;

        MixedKeyScratch.Clear();
        for (int i = 0; i < mixedKeys.Length; i++)
            if (!string.IsNullOrEmpty(mixedKeys[i])) MixedKeyScratch.Add(mixedKeys[i]);

        PropertyBinding[] next = new PropertyBinding[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            EditorPropertySnapshot property = properties[i];
            if (property == null || string.IsNullOrEmpty(property.Key))
            {
                next[i] = new PropertyBinding { Property = property };
                continue;
            }

            bool mixed = MixedKeyScratch.Contains(property.Key);
            string stateKey = inspector.ObjectIndex + ":" + inspector.SelectionCount + ":" + property.Key;
            string displayName = (property.DisplayName ?? property.Key) +
                                 (mixed ? DevToolUiSettings.T("  [混合]", "  [Mixed]") : string.Empty);
            string label = displayName + "##DevToolProperty_" + stateKey;
            string enumFilterKey = "enum-search:" + inspector.ObjectIndex + ":" + property.Key;
            string[] options = property.Options ?? Array.Empty<string>();
            string[] optionLabels = new string[options.Length];
            for (int optionIndex = 0; optionIndex < options.Length; optionIndex++)
                optionLabels[optionIndex] = (options[optionIndex] ?? string.Empty) + "##" + label + optionIndex;

            next[i] = new PropertyBinding
            {
                Property = property,
                Mixed = mixed,
                StateKey = stateKey,
                DisplayName = displayName,
                Label = label,
                ActionId = "InspectorAction_" + stateKey,
                EnumFilterKey = enumFilterKey,
                EnumSearchLabel = DevToolUiSettings.T("搜索##", "Search##") + enumFilterKey,
                EnumOptionLabels = optionLabels
            };
        }

        projectedProperties = properties;
        projectedMixedKeys = mixedKeys;
        projectedPropertyObjectIndex = inspector.ObjectIndex;
        projectedPropertySelectionCount = inspector.SelectionCount;
        projectedPropertyChinese = chinese;
        propertyBindings = next;
        MixedKeyScratch.Clear();
    }

    private static void EnsureLegacyBindings(EditorInspectorSnapshot inspector, LegacyControlSnapshot[] controls)
    {
        if (ReferenceEquals(projectedLegacyControls, controls) &&
            projectedLegacyObjectIndex == inspector.ObjectIndex)
            return;

        LegacyBinding[] next = new LegacyBinding[controls.Length];
        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            string path = control?.Path ?? string.Empty;
            string stateKey = inspector.ObjectIndex + ":legacy:" + path;
            string visibleLabel = string.IsNullOrEmpty(control?.Label) ? control?.Id ?? string.Empty : control.Label;
            string[] options = control?.Options ?? Array.Empty<string>();
            string optionPrefix = control?.Kind switch
            {
                LegacyControlKind.Cycler => "LegacyCyclerOption_",
                LegacyControlKind.ExtEnum => "LegacyExtEnumOption_",
                LegacyControlKind.Select => "LegacySelectOption_",
                LegacyControlKind.PanelSelect => "LegacyPanelSelectOption_",
                _ => "LegacyChoiceOption_"
            };
            string[] optionLabels = new string[options.Length];
            for (int optionIndex = 0; optionIndex < options.Length; optionIndex++)
                optionLabels[optionIndex] = (options[optionIndex] ?? string.Empty) + "##" +
                                            optionPrefix + stateKey + "_" + optionIndex;

            string integerValue = string.IsNullOrWhiteSpace(control?.ValueText) ? "-" : control.ValueText;
            next[i] = new LegacyBinding
            {
                Control = control,
                StateKey = stateKey,
                VisibleLabel = visibleLabel,
                BooleanLabel = visibleLabel + "##DevToolLegacyBoolean_" + stateKey,
                ButtonId = "LegacyButton_" + stateKey,
                SliderLabel = visibleLabel + "##DevToolLegacy_" + stateKey,
                ResetId = "LegacyReset_" + stateKey,
                ChoiceLabel = visibleLabel + "##DevToolLegacyChoice_" + stateKey,
                ChoiceOptionLabels = optionLabels,
                IntegerDisplay = visibleLabel + ":  " + integerValue,
                IntegerLessId = "LegacyIntegerLess_" + stateKey,
                IntegerMoreId = "LegacyIntegerMore_" + stateKey,
                TextLabel = visibleLabel + "##DevToolLegacyText_" + stateKey,
                DirectionEditKey = "legacy-direction:" + stateKey,
                DirectionLabel = visibleLabel + "##DevToolLegacyDirection_" + stateKey,
                ColorEditKey = "legacy-color:" + stateKey,
                ColorLabel = visibleLabel + "##DevToolLegacyColor_" + stateKey
            };
        }

        projectedLegacyControls = controls;
        projectedLegacyObjectIndex = inspector.ObjectIndex;
        legacyBindings = next;
    }

    private static EditorObjectTypeSnapshot FindMetadata(string type)
    {
        EditorObjectTypeSnapshot[] library = EditorPresentationHub.Current.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        if (!ReferenceEquals(metadataSource, library))
        {
            MetadataByType.Clear();
            for (int i = 0; i < library.Length; i++)
            {
                EditorObjectTypeSnapshot item = library[i];
                if (item == null || string.IsNullOrEmpty(item.Type)) continue;
                MetadataByType[item.Type] = item;
            }
            metadataSource = library;
            identityMetadata = null;
        }

        return !string.IsNullOrEmpty(type) && MetadataByType.TryGetValue(type, out EditorObjectTypeSnapshot metadata)
            ? metadata
            : null;
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
            y: positionY,
            stableId: inspector.SelectionCount == 1 ? inspector.ObjectStableId : 0L));
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
            propertyValue: value,
            stableId: inspector.SelectionCount == 1 ? inspector.ObjectStableId : 0L));
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
        StringEdits.Clear();
        Vector2Edits.Clear();
        ColorEdits.Clear();
    }

    private static TValue Get<TValue>(Dictionary<string, TValue> dictionary, string key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
