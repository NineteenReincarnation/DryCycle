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
    private static float positionX;
    private static float positionY;

    internal static void Draw(EditorInspectorSnapshot inspector)
    {
        inspector ??= new EditorInspectorSnapshot();
        if (!inspector.HasSelection)
        {
            Reset(-1);
            ImGui.TextDisabled("Nothing selected.");
            return;
        }

        if (objectIndex != inspector.ObjectIndex)
            Reset(inspector.ObjectIndex, inspector.X, inspector.Y);

        ImGui.Text(inspector.Type);
        ImGui.TextDisabled(inspector.DataType);
        ImGui.Separator();

        DrawTransform(inspector);
        DrawProperties(inspector);
        DrawLegacyControls(inspector);

        ImGui.Separator();
        if (ImGui.Button("Delete Object"))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteObject, inspector.ObjectIndex));
    }

    private static void DrawTransform(EditorInspectorSnapshot inspector)
    {
        ImGui.TextDisabled("Transform");

        // Keep external/gizmo movement visible, but never overwrite an edit buffer while
        // an ImGui item is active. This is what lets typed values survive across frames.
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
        if (properties.Length == 0) return;

        ImGui.Separator();
        string group = null;
        for (int i = 0; i < properties.Length; i++)
        {
            EditorPropertySnapshot property = properties[i];
            if (!string.Equals(group, property.Group, StringComparison.Ordinal))
            {
                group = property.Group;
                ImGui.TextDisabled(string.IsNullOrEmpty(group) ? "Properties" : group);
            }
            DrawProperty(inspector.ObjectIndex, property);
        }
    }

    private static void DrawProperty(int targetIndex, EditorPropertySnapshot property)
    {
        if (property == null || string.IsNullOrEmpty(property.Key)) return;
        string stateKey = targetIndex + ":" + property.Key;
        string label = property.DisplayName + "##DevToolProperty_" + stateKey;

        switch (property.Kind)
        {
            case EditorPropertyKind.ReadOnly:
                ImGui.TextDisabled(property.DisplayName);
                ImGui.TextWrapped(property.StringValue ?? string.Empty);
                break;

            case EditorPropertyKind.Float:
            {
                float value = Get(FloatEdits, stateKey, property.X);
                bool changed;
                if (property.HasRange)
                    changed = ImGui.SliderFloat(label, ref value, property.Min, property.Max, "%.3f");
                else
                    changed = ImGui.InputFloat(label, ref value, property.Step <= 0f ? 0.1f : property.Step, 0f, "%.3f");
                FloatEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(targetIndex, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
                else if (!changed && !ImGui.IsItemActive())
                    FloatEdits[stateKey] = property.X;
                break;
            }

            case EditorPropertyKind.Integer:
            {
                int value = Get(IntEdits, stateKey, property.IntegerValue);
                bool changed;
                if (property.HasRange)
                    changed = ImGui.SliderInt(label, ref value, (int)property.Min, (int)property.Max);
                else
                    changed = ImGui.InputInt(label, ref value, Math.Max(1, (int)property.Step));
                IntEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(targetIndex, property.Key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));
                else if (!changed && !ImGui.IsItemActive())
                    IntEdits[stateKey] = property.IntegerValue;
                break;
            }

            case EditorPropertyKind.Boolean:
            {
                bool value = property.BooleanValue;
                if (ImGui.Checkbox(label, ref value))
                    SendProperty(targetIndex, property.Key, new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: value));
                break;
            }

            case EditorPropertyKind.String:
            {
                string value = Get(StringEdits, stateKey, property.StringValue ?? string.Empty);
                bool changed = ImGui.InputText(label, ref value, 1024);
                StringEdits[stateKey] = value;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SendProperty(targetIndex, property.Key, new EditorPropertyValue(EditorPropertyKind.String, text: value));
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
                    SendProperty(targetIndex, property.Key,
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
                    SendProperty(targetIndex, property.Key,
                        new EditorPropertyValue(EditorPropertyKind.Color,
                            x: value.X, y: value.Y, z: value.Z, w: value.W));
                else if (!changed && !ImGui.IsItemActive())
                    ColorEdits[stateKey] = new Num.Vector4(property.X, property.Y, property.Z, property.W);
                break;
            }

            case EditorPropertyKind.Enum:
                DrawEnum(targetIndex, property, label);
                break;
        }

        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(property.Source))
            ImGui.SetTooltip(property.Source);
    }

    private static void DrawEnum(int targetIndex, EditorPropertySnapshot property, string label)
    {
        string[] options = property.Options ?? Array.Empty<string>();
        string preview = property.IntegerValue >= 0 && property.IntegerValue < options.Length
            ? options[property.IntegerValue]
            : property.StringValue ?? string.Empty;

        if (!ImGui.BeginCombo(label, preview)) return;
        for (int i = 0; i < options.Length; i++)
        {
            bool selected = i == property.IntegerValue;
            if (ImGui.Selectable(options[i] + "##" + label + i, selected))
                SendProperty(targetIndex, property.Key,
                    new EditorPropertyValue(EditorPropertyKind.Enum, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawLegacyControls(EditorInspectorSnapshot inspector)
    {
        LegacyControlSnapshot[] controls = inspector.LegacyControls ?? Array.Empty<LegacyControlSnapshot>();
        if (controls.Length == 0) return;

        ImGui.Separator();
        ImGui.TextDisabled("Legacy DevInterface");
        ImGui.TextWrapped("Standard Rain World controls exposed by the object's original representation.");

        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            string stateKey = inspector.ObjectIndex + ":legacy:" + control.Path;
            string label = (string.IsNullOrEmpty(control.Label) ? control.Id : control.Label) +
                           "##DevToolLegacy_" + stateKey;

            if (control.Kind == LegacyControlKind.Button)
            {
                if (ImGui.Button(label))
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.InvokeLegacyButton,
                        inspector.ObjectIndex,
                        text: control.Path));
                continue;
            }

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
                ImGui.TextDisabled(control.ValueText);
            }

            if (control.CanReset)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Reset##" + stateKey))
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.ResetLegacySlider,
                        inspector.ObjectIndex,
                        text: control.Path));
            }
        }
    }

    private static void SendPosition(EditorInspectorSnapshot inspector)
    {
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            EditorUiCommandKind.SetObjectPosition,
            inspector.ObjectIndex,
            x: positionX,
            y: positionY));
    }

    private static void SendProperty(int targetIndex, string key, EditorPropertyValue value)
    {
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            EditorUiCommandKind.SetObjectProperty,
            targetIndex,
            text: key,
            propertyValue: value));
    }

    private static void SynchronizePosition(EditorInspectorSnapshot inspector)
    {
        if (Math.Abs(positionX - inspector.X) > 0.0001f) positionX = inspector.X;
        if (Math.Abs(positionY - inspector.Y) > 0.0001f) positionY = inspector.Y;
    }

    private static void Reset(int nextObjectIndex, float x = 0f, float y = 0f)
    {
        objectIndex = nextObjectIndex;
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
