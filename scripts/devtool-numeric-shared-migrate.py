from pathlib import Path

root = Path('src/DevUI/DevTool/RWImGui')

def replace_once(path: Path, old: str, new: str, label: str):
    s = path.read_text(encoding='utf-8')
    if old not in s:
        raise SystemExit(f'{path.name}: missing {label}')
    path.write_text(s.replace(old, new, 1), encoding='utf-8')

# Trigger scalar numeric fields.
p = root / 'TriggerEditorView.cs'
s = p.read_text(encoding='utf-8')
s = s.replace('    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('        IntEdits.Clear();\n', '')
s = s.replace('        FloatEdits.Clear();\n', '')
old = '''    private static void DrawInt(EditorTriggerSnapshot trigger, string key, string label, int current, int min, int max)\n    {\n        EditBinding binding = GetBinding(trigger.Index, key, label, eventField: false);\n        int value = Get(IntEdits, binding.StateKey, current);\n        bool changed = ImGui.InputInt(binding.WidgetLabel, ref value);\n        value = Math.Max(min, Math.Min(max, value));\n        IntEdits[binding.StateKey] = value;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));\n        else if (!changed && !ImGui.IsItemActive())\n            IntEdits[binding.StateKey] = current;\n    }'''
new = '''    private static void DrawInt(EditorTriggerSnapshot trigger, string key, string label, int current, int min, int max)\n    {\n        EditBinding binding = GetBinding(trigger.Index, key, label, eventField: false);\n        DevToolNumericEditResult<int> edit = DevToolNumericWidgets.InputInt(\n            DevToolNumericScope.Trigger,\n            binding.StateKey,\n            binding.WidgetLabel,\n            current,\n            instance: trigger.Index,\n            min: min,\n            max: max);\n        if (edit.Committed)\n            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: edit.Value));\n    }'''
if old not in s: raise SystemExit('Trigger DrawInt anchor missing')
s = s.replace(old, new, 1)
old = '''    private static void DrawFloat(EditorTriggerSnapshot trigger, string key, string label, float current, float min, float max)\n    {\n        EditBinding binding = GetBinding(trigger.Index, key, label, eventField: false);\n        float value = Get(FloatEdits, binding.StateKey, current);\n        bool changed = ImGui.SliderFloat(binding.WidgetLabel, ref value, min, max, "%.3f");\n        FloatEdits[binding.StateKey] = value;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));\n        else if (!changed && !ImGui.IsItemActive())\n            FloatEdits[binding.StateKey] = current;\n    }'''
new = '''    private static void DrawFloat(EditorTriggerSnapshot trigger, string key, string label, float current, float min, float max)\n    {\n        EditBinding binding = GetBinding(trigger.Index, key, label, eventField: false);\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.Trigger,\n            binding.StateKey,\n            binding.WidgetLabel,\n            current,\n            min,\n            max,\n            instance: trigger.Index);\n        if (edit.Committed)\n            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value));\n    }'''
if old not in s: raise SystemExit('Trigger DrawFloat anchor missing')
s = s.replace(old, new, 1)
old = '''    private static void DrawEventFloat(int triggerIndex, string key, string label, float current, float min, float max)\n    {\n        EditBinding binding = GetBinding(triggerIndex, key, label, eventField: true);\n        float value = Get(FloatEdits, binding.StateKey, current);\n        bool changed = ImGui.SliderFloat(binding.WidgetLabel, ref value, min, max, "%.3f");\n        FloatEdits[binding.StateKey] = value;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));\n        else if (!changed && !ImGui.IsItemActive())\n            FloatEdits[binding.StateKey] = current;\n    }'''
new = '''    private static void DrawEventFloat(int triggerIndex, string key, string label, float current, float min, float max)\n    {\n        EditBinding binding = GetBinding(triggerIndex, key, label, eventField: true);\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.Trigger,\n            binding.StateKey,\n            binding.WidgetLabel,\n            current,\n            min,\n            max,\n            instance: triggerIndex);\n        if (edit.Committed)\n            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value));\n    }'''
if old not in s: raise SystemExit('Trigger DrawEventFloat anchor missing')
s = s.replace(old, new, 1)
old = '''    private static void DrawEventInt(int triggerIndex, string key, string label, int current, int min, int max)\n    {\n        EditBinding binding = GetBinding(triggerIndex, key, label, eventField: true);\n        int value = Get(IntEdits, binding.StateKey, current);\n        bool changed = ImGui.InputInt(binding.WidgetLabel, ref value);\n        value = Math.Max(min, Math.Min(max, value));\n        IntEdits[binding.StateKey] = value;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));\n        else if (!changed && !ImGui.IsItemActive())\n            IntEdits[binding.StateKey] = current;\n    }'''
new = '''    private static void DrawEventInt(int triggerIndex, string key, string label, int current, int min, int max)\n    {\n        EditBinding binding = GetBinding(triggerIndex, key, label, eventField: true);\n        DevToolNumericEditResult<int> edit = DevToolNumericWidgets.InputInt(\n            DevToolNumericScope.Trigger,\n            binding.StateKey,\n            binding.WidgetLabel,\n            current,\n            instance: triggerIndex,\n            min: min,\n            max: max);\n        if (edit.Committed)\n            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: edit.Value));\n    }'''
if old not in s: raise SystemExit('Trigger DrawEventInt anchor missing')
s = s.replace(old, new, 1)
if 'IntEdits' in s or 'FloatEdits' in s:
    raise SystemExit('Trigger scalar private caches remain')
p.write_text(s, encoding='utf-8')

# Sound scalar numeric fields; vectors keep their existing retained state.
p = root / 'SoundEditorView.cs'
s = p.read_text(encoding='utf-8')
s = s.replace('    private static readonly Dictionary<string, float> RoomFloatEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('    private static readonly Dictionary<SoundEditKey, float> SoundFloatEdits = new();\n', '')
s = s.replace('        RoomFloatEdits.Clear();\n', '')
s = s.replace('        SoundFloatEdits.Clear();\n', '')
old = '''    private static void DrawRoomFloat(string key, string label, float current, float min, float max)\n    {\n        float value = Get(RoomFloatEdits, key, current);\n        ImGui.PushID("SoundRoom");\n        ImGui.PushID(key);\n        bool changed = ImGui.SliderFloat(label, ref value, min, max, "%.3f");\n        ImGui.PopID();\n        ImGui.PopID();\n        RoomFloatEdits[key] = value;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n        {\n            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(\n                SoundEditorCommandKind.SetRoomValue,\n                key: key,\n                value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));\n        }\n        else if (!changed && !ImGui.IsItemActive())\n        {\n            RoomFloatEdits[key] = current;\n        }\n    }'''
new = '''    private static void DrawRoomFloat(string key, string label, float current, float min, float max)\n    {\n        ImGui.PushID("SoundRoom");\n        ImGui.PushID(key);\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.SoundRoom, key, label, current, min, max);\n        ImGui.PopID();\n        ImGui.PopID();\n        if (edit.Committed)\n        {\n            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(\n                SoundEditorCommandKind.SetRoomValue,\n                key: key,\n                value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));\n        }\n    }'''
if old not in s: raise SystemExit('Sound DrawRoomFloat anchor missing')
s = s.replace(old, new, 1)
old = '''    private static void DrawSoundFloat(EditorSoundSnapshot sound, string key, string label, float current, float min, float max)\n    {\n        SoundEditKey stateKey = new(sound.Index, key);\n        float value = Get(SoundFloatEdits, stateKey, current);\n        ImGui.PushID(sound.Index);\n        ImGui.PushID(key);\n        bool changed = ImGui.SliderFloat(label, ref value, min, max, "%.3f");\n        ImGui.PopID();\n        ImGui.PopID();\n        SoundFloatEdits[stateKey] = value;\n        if (!sound.Inherited && ImGui.IsItemDeactivatedAfterEdit())\n        {\n            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(\n                SoundEditorCommandKind.SetSoundValue,\n                index: sound.Index,\n                key: key,\n                value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));\n        }\n        else if (!changed && !ImGui.IsItemActive())\n        {\n            SoundFloatEdits[stateKey] = current;\n        }\n    }'''
new = '''    private static void DrawSoundFloat(EditorSoundSnapshot sound, string key, string label, float current, float min, float max)\n    {\n        if (sound.Inherited)\n            DevToolNumericWidgets.Discard(DevToolNumericScope.SoundItem, key, sound.Index);\n\n        ImGui.PushID(sound.Index);\n        ImGui.PushID(key);\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.SoundItem, key, label, current, min, max, instance: sound.Index);\n        ImGui.PopID();\n        ImGui.PopID();\n        if (!sound.Inherited && edit.Committed)\n        {\n            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(\n                SoundEditorCommandKind.SetSoundValue,\n                index: sound.Index,\n                key: key,\n                value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));\n        }\n    }'''
if old not in s: raise SystemExit('Sound DrawSoundFloat anchor missing')
s = s.replace(old, new, 1)
if 'RoomFloatEdits' in s or 'SoundFloatEdits' in s:
    raise SystemExit('Sound scalar private caches remain')
p.write_text(s, encoding='utf-8')

# Object transform/property scalars and legacy slider.
p = root / 'ObjectInspectorView.cs'
s = p.read_text(encoding='utf-8')
s = s.replace('    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('    private static readonly Dictionary<string, float> LegacySliderEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('        FloatEdits.Clear();\n', '')
s = s.replace('        IntEdits.Clear();\n', '')
s = s.replace('        LegacySliderEdits.Clear();\n', '')
old = '''        if (!ImGui.IsAnyItemActive())\n            SynchronizePosition(inspector);\n\n        ImGui.SetNextItemWidth(-1f);\n        ImGui.InputFloat("X##DevToolPosX", ref positionX, 1f, 20f, "%.1f");\n        bool xCommit = ImGui.IsItemDeactivatedAfterEdit();\n\n        ImGui.SetNextItemWidth(-1f);\n        ImGui.InputFloat("Y##DevToolPosY", ref positionY, 1f, 20f, "%.1f");\n        bool yCommit = ImGui.IsItemDeactivatedAfterEdit();\n\n        if (xCommit || yCommit)\n            SendPosition(inspector);'''
new = '''        ImGui.SetNextItemWidth(-1f);\n        DevToolNumericEditResult<float> xEdit = DevToolNumericWidgets.InputFloat(\n            DevToolNumericScope.ObjectTransform,\n            "x",\n            "X##DevToolPosX",\n            inspector.X,\n            1f,\n            "%.1f",\n            inspector.ObjectIndex);\n        positionX = xEdit.Value;\n\n        ImGui.SetNextItemWidth(-1f);\n        DevToolNumericEditResult<float> yEdit = DevToolNumericWidgets.InputFloat(\n            DevToolNumericScope.ObjectTransform,\n            "y",\n            "Y##DevToolPosY",\n            inspector.Y,\n            1f,\n            "%.1f",\n            inspector.ObjectIndex);\n        positionY = yEdit.Value;\n\n        if (xEdit.Committed || yEdit.Committed)\n            SendPosition(inspector);'''
if old not in s: raise SystemExit('Object transform anchor missing')
s = s.replace(old, new, 1)
old = '''            case EditorPropertyKind.Float:\n            {\n                float value = Get(FloatEdits, stateKey, property.X);\n                bool changed = property.HasRange\n                    ? ImGui.SliderFloat(label, ref value, property.Min, property.Max, "%.3f")\n                    : ImGui.InputFloat(label, ref value, property.Step <= 0f ? 0.1f : property.Step, 0f, "%.3f");\n                FloatEdits[stateKey] = value;\n                if (ImGui.IsItemDeactivatedAfterEdit())\n                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));\n                else if (!changed && !ImGui.IsItemActive())\n                    FloatEdits[stateKey] = property.X;\n                break;\n            }'''
new = '''            case EditorPropertyKind.Float:\n            {\n                DevToolNumericEditResult<float> edit = property.HasRange\n                    ? DevToolNumericWidgets.SliderFloat(\n                        DevToolNumericScope.ObjectProperty, stateKey, label, property.X, property.Min, property.Max,\n                        instance: inspector.ObjectIndex)\n                    : DevToolNumericWidgets.InputFloat(\n                        DevToolNumericScope.ObjectProperty, stateKey, label, property.X,\n                        property.Step <= 0f ? 0.1f : property.Step, "%.3f", inspector.ObjectIndex);\n                if (edit.Committed)\n                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value));\n                break;\n            }'''
if old not in s: raise SystemExit('Object float property anchor missing')
s = s.replace(old, new, 1)
old = '''            case EditorPropertyKind.Integer:\n            {\n                int value = Get(IntEdits, stateKey, property.IntegerValue);\n                bool changed = property.HasRange\n                    ? ImGui.SliderInt(label, ref value, (int)property.Min, (int)property.Max)\n                    : ImGui.InputInt(label, ref value, Math.Max(1, (int)property.Step));\n                IntEdits[stateKey] = value;\n                if (ImGui.IsItemDeactivatedAfterEdit())\n                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));\n                else if (!changed && !ImGui.IsItemActive())\n                    IntEdits[stateKey] = property.IntegerValue;\n                break;\n            }'''
new = '''            case EditorPropertyKind.Integer:\n            {\n                DevToolNumericEditResult<int> edit = property.HasRange\n                    ? DevToolNumericWidgets.SliderInt(\n                        DevToolNumericScope.ObjectProperty, stateKey, label, property.IntegerValue,\n                        (int)property.Min, (int)property.Max, instance: inspector.ObjectIndex)\n                    : DevToolNumericWidgets.InputInt(\n                        DevToolNumericScope.ObjectProperty, stateKey, label, property.IntegerValue,\n                        Math.Max(1, (int)property.Step), instance: inspector.ObjectIndex);\n                if (edit.Committed)\n                    SendProperty(inspector, property.Key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: edit.Value));\n                break;\n            }'''
if old not in s: raise SystemExit('Object int property anchor missing')
s = s.replace(old, new, 1)
old = '''    private static void DrawLegacySlider(EditorInspectorSnapshot inspector, LegacyBinding binding)\n    {\n        LegacyControlSnapshot control = binding.Control;\n        float factor = Get(LegacySliderEdits, binding.StateKey, control.Factor);\n        bool changed = ImGui.SliderFloat(binding.SliderLabel, ref factor, 0f, 1f, "%.3f");\n        LegacySliderEdits[binding.StateKey] = factor;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n        {\n            EditorUiCommandQueue.Enqueue(new EditorUiCommand(\n                EditorUiCommandKind.SetLegacySlider,\n                inspector.ObjectIndex,\n                text: control.Path,\n                x: factor));\n        }\n        else if (!changed && !ImGui.IsItemActive())\n        {\n            LegacySliderEdits[binding.StateKey] = control.Factor;\n        }'''
new = '''    private static void DrawLegacySlider(EditorInspectorSnapshot inspector, LegacyBinding binding)\n    {\n        LegacyControlSnapshot control = binding.Control;\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.ObjectLegacy,\n            binding.StateKey,\n            binding.SliderLabel,\n            control.Factor,\n            0f,\n            1f,\n            instance: inspector.ObjectIndex);\n        if (edit.Committed)\n        {\n            EditorUiCommandQueue.Enqueue(new EditorUiCommand(\n                EditorUiCommandKind.SetLegacySlider,\n                inspector.ObjectIndex,\n                text: control.Path,\n                x: edit.Value));\n        }'''
if old not in s: raise SystemExit('Object legacy slider anchor missing')
s = s.replace(old, new, 1)
old = '''            if (DevToolWidgets.ActionButton(\n                    DevToolUiSettings.T("重置", "Reset"),\n                    binding.ResetId,\n                    DevToolButtonTone.Subtle))\n                EditorUiCommandQueue.Enqueue(new EditorUiCommand(\n                    EditorUiCommandKind.ResetLegacySlider,\n                    inspector.ObjectIndex,\n                    text: control.Path));'''
new = '''            if (DevToolWidgets.ActionButton(\n                    DevToolUiSettings.T("重置", "Reset"),\n                    binding.ResetId,\n                    DevToolButtonTone.Subtle))\n            {\n                DevToolNumericWidgets.Discard(DevToolNumericScope.ObjectLegacy, binding.StateKey, inspector.ObjectIndex);\n                EditorUiCommandQueue.Enqueue(new EditorUiCommand(\n                    EditorUiCommandKind.ResetLegacySlider,\n                    inspector.ObjectIndex,\n                    text: control.Path));\n            }'''
if old not in s: raise SystemExit('Object legacy reset anchor missing')
s = s.replace(old, new, 1)
for forbidden in ('FloatEdits', 'IntEdits', 'LegacySliderEdits'):
    if forbidden in s:
        raise SystemExit(f'Object scalar private cache remains: {forbidden}')
p.write_text(s, encoding='utf-8')

# Universal generic slider.
p = root / 'UniversalDevUiMirrorView.cs'
s = p.read_text(encoding='utf-8')
s = s.replace('    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('        FloatEdits.Clear();\n', '')
old = '''    private static void DrawSlider(LegacyControlSnapshot control, string stateKey, string label)\n    {\n        float value = Get(FloatEdits, stateKey, control.Factor);\n        bool changed = ImGui.SliderFloat(label + "##UniversalSlider_" + stateKey, ref value, 0f, 1f, "%.3f");\n        bool active = ImGui.IsItemActive();\n        bool commit = ImGui.IsItemDeactivatedAfterEdit();\n        FloatEdits[stateKey] = value;\n\n        if (commit)\n            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetSlider, control.Path, x: value));\n        else if (!changed && !active)\n            FloatEdits[stateKey] = control.Factor;'''
new = '''    private static void DrawSlider(LegacyControlSnapshot control, string stateKey, string label)\n    {\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.Universal,\n            stateKey,\n            label + "##UniversalSlider_" + stateKey,\n            control.Factor,\n            0f,\n            1f);\n\n        if (edit.Committed)\n            Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.SetSlider, control.Path, x: edit.Value));'''
if old not in s: raise SystemExit('Universal slider anchor missing')
s = s.replace(old, new, 1)
old = '''            if (DevToolWidgets.ActionButton(\n                    DevToolUiSettings.T("继承", "Reset"),\n                    "UniversalSliderReset_" + stateKey,\n                    DevToolButtonTone.Subtle))\n                Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.ResetSlider, control.Path));'''
new = '''            if (DevToolWidgets.ActionButton(\n                    DevToolUiSettings.T("继承", "Reset"),\n                    "UniversalSliderReset_" + stateKey,\n                    DevToolButtonTone.Subtle))\n            {\n                DevToolNumericWidgets.Discard(DevToolNumericScope.Universal, stateKey);\n                Send(new UniversalDevUiCommand(UniversalDevUiCommandKind.ResetSlider, control.Path));\n            }'''
if old not in s: raise SystemExit('Universal slider reset anchor missing')
s = s.replace(old, new, 1)
if 'FloatEdits' in s:
    raise SystemExit('Universal scalar private cache remains')
p.write_text(s, encoding='utf-8')

# One lifecycle owner resets shared numeric transactions together with retained UI state.
p = root / 'DevToolRetainedViewLifecyclePlugin.cs'
s = p.read_text(encoding='utf-8')
old = '''    private static void ReleaseRetainedState()\n    {\n        DevToolOverlay.ResetRetainedState();'''
new = '''    private static void ReleaseRetainedState()\n    {\n        DevToolNumericWidgets.Reset();\n        DevToolOverlay.ResetRetainedState();'''
if old not in s:
    raise SystemExit('retained lifecycle anchor missing')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
