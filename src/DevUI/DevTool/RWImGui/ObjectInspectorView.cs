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
            ImGui.TextDisabled("Nothing selected.");
            return;
        }

        if (objectIndex != inspector.ObjectIndex || selectionCount != inspector.SelectionCount)
            Reset(inspector.ObjectIndex, inspector.X, inspector.Y, inspector.SelectionCount);

        ImGui.Text(inspector.Type);
        ImGui.TextDisabled(inspector.DataType);
        if (inspector.SelectionCount > 1)
            ImGui.TextDisabled("Editing shared properties for the current selection.");
        ImGui.Separator();

        DrawTransform(inspector);
        DrawProperties(inspector);
        DrawLegacyControls(inspector);
        DrawLegacyFallback(inspector);

        ImGui.Separator();
        if (inspector.SelectionCount > 1)
        {
            if (ImGui.Button("Duplicate Selection  Ctrl+D"))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DuplicateSelection));
            ImGui.SameLine();
            if (ImGui.Button("Delete Selection"))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteSelection));
        }
        else if (ImGui.Button("Delete Object"))
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteObject, inspector.ObjectIndex));
        }
    }

    private static void DrawTransform(EditorInspectorSnapshot inspector)
    {
        ImGui.TextDisabled(inspector.SelectionCount > 1 ? "Transform · group anchor" : "Transform");

        // The primary object is the anchor for a multi-selection. Moving it from the
        // Inspector applies the same delta to every selected object on the Unity thread.
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
                ImGui.Separator();
                ImGui.TextDisabled("No editable properties are shared by every selected object.");
            }
            return;
        }

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
            DrawProperty(inspector, property);
        }
    }

    private static void DrawProperty(EditorInspectorSnapshot inspector, EditorPropertySnapshot property)
    {
        if (property == null || string.IsNullOrEmpty(property.Key)) return;

        bool mixed = IsMixed(inspector, property.Key);
        string stateKey = inspector.ObjectIndex + ":" + inspector.SelectionCount + ":" + property.Key;
        string displayName = property.DisplayName + (mixed ? "  [Mixed]" : string.Empty);
        string label = displayName + "##DevToolProperty_" + stateKey;

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
                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
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
        }

        if (ImGui.IsItemHovered())
        {
            if (mixed && !string.IsNullOrEmpty(property.Source))
                ImGui.SetTooltip("Selected objects contain different values.\n" + property.Source);
            else if (mixed)
                ImGui.SetTooltip("Selected objects contain different values.");
            else if (!string.IsNullOrEmpty(property.Source))
                ImGui.SetTooltip(property.Source);
        }
    }

    private static void DrawEnum(EditorInspectorSnapshot inspector, EditorPropertySnapshot property, string label, bool mixed)
    {
        string[] options = property.Options ?? Array.Empty<string>();
        string preview = mixed
            ? "<Mixed>"
            : property.IntegerValue >= 0 && property.IntegerValue < options.Length
                ? options[property.IntegerValue]
                : property.StringValue ?? string.Empty;

        if (!ImGui.BeginCombo(label, preview)) return;
        for (int i = 0; i < options.Length; i++)
        {
            bool selected = !mixed && i == property.IntegerValue;
            if (ImGui.Selectable(options[i] + "##" + label + i, selected))
                SendProperty(inspector, property.Key,
                    new EditorPropertyValue(EditorPropertyKind.Enum, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawLegacyControls(EditorInspectorSnapshot inspector)
    {
        // Legacy controls intentionally remain single-selection only. Presentation strips
        // them from multi-selection because their semantics are not safely composable.
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

    private static void DrawLegacyFallback(EditorInspectorSnapshot inspector)
    {
        if (!inspector.LegacyUiAvailable) return;

        ImGui.Separator();
        ImGui.TextDisabled("Compatibility");
        string label = inspector.LegacyUiVisible ? "Hide Original DevUI" : "Show Original DevUI";
        if (ImGui.Button(label + "##DevToolLegacyFallback"))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.ToggleLegacyUi));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fallback for custom DevInterface controls that cannot be translated into the Inspector.");
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
