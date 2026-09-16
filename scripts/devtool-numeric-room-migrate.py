from pathlib import Path
import re

p = Path('src/DevUI/DevTool/RWImGui/RoomSettingsView.cs')
s = p.read_text(encoding='utf-8')

# Remove private numeric transaction structs and caches. Shared DevToolNumericWidgets owns this state.
s = re.sub(
    r'\n    private readonly struct PendingFloatEdit\n    \{.*?\n    private static readonly Dictionary<string, string> SettingWidgetIds',
    '\n    private static readonly Dictionary<string, string> SettingWidgetIds',
    s,
    count=1,
    flags=re.S)

s = s.replace('''        FloatEdits.Clear();\n        DirtyFloatEdits.Clear();\n        PendingFloatEdits.Clear();\n        IntEdits.Clear();\n        DirtyIntEdits.Clear();\n        PendingIntEdits.Clear();\n''', '')

# Fade palette integer input.
s = re.sub(
    r'''        int fadePalette = Get\(IntEdits, RoomSettingKeys\.FadePalette, snapshot\.HasFadePalette \? snapshot\.FadePalette : -1\);\n        BeginSettingRow\(DevToolUiSettings\.T\("渐变色板编号", "Fade Palette"\), 0f, out _\);\n        int fadeAuthoritative = snapshot\.HasFadePalette \? snapshot\.FadePalette : -1;\n        bool fadeChanged = ImGui\.InputInt\("##RoomFadePalette", ref fadePalette, 1, 10\);\n        if \(FinishIntEdit\(RoomSettingKeys\.FadePalette, fadeAuthoritative, ref fadePalette, fadeChanged\)\)\n            SendSetting\(RoomSettingKeys\.FadePalette, new EditorPropertyValue\(EditorPropertyKind\.Integer, integer: fadePalette\)\);''',
    '''        int fadeAuthoritative = snapshot.HasFadePalette ? snapshot.FadePalette : -1;\n        BeginSettingRow(DevToolUiSettings.T("渐变色板编号", "Fade Palette"), 0f, out _);\n        DevToolNumericEditResult<int> fadeEdit = DevToolNumericWidgets.InputInt(\n            DevToolNumericScope.Room,\n            RoomSettingKeys.FadePalette,\n            "##RoomFadePalette",\n            fadeAuthoritative,\n            1,\n            10);\n        if (fadeEdit.Committed)\n            SendSetting(RoomSettingKeys.FadePalette, new EditorPropertyValue(EditorPropertyKind.Integer, integer: fadeEdit.Value));''',
    s,
    count=1)

# Effect slider block.
s = re.sub(
    r'''                float value = Get\(FloatEdits, sliderBinding\.StateKey, values\[slider\]\);\n                BeginSettingRow\(sliderBinding\.DisplayName, 0f, out _\);\n                bool changed = ImGui\.SliderFloat\(\n                    sliderBinding\.WidgetId,\n                    ref value,\n                    0f,\n                    1f,\n                    "%.3f",\n                    ImGuiSliderFlags\.AlwaysClamp\);\n\n                if \(effect\.Inherited\)\n                \{\n                    DirtyFloatEdits\.Remove\(sliderBinding\.StateKey\);\n                    PendingFloatEdits\.Remove\(sliderBinding\.StateKey\);\n                    FloatEdits\[sliderBinding\.StateKey\] = values\[slider\];\n                \}\n                else if \(FinishFloatEdit\(sliderBinding\.StateKey, values\[slider\], ref value, changed\)\)\n                \{\n                    RoomEditorCommandQueue\.Enqueue\(new RoomEditorCommand\(\n                        RoomEditorCommandKind\.SetEffectAmount,\n                        index: effect\.Index,\n                        secondaryIndex: slider,\n                        value: new EditorPropertyValue\(EditorPropertyKind\.Float, x: value\)\)\);\n                \}''',
    '''                BeginSettingRow(sliderBinding.DisplayName, 0f, out _);\n                if (effect.Inherited)\n                {\n                    DevToolNumericWidgets.Discard(DevToolNumericScope.Room, sliderBinding.StateKey, effect.Index);\n                }\n                else\n                {\n                    DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n                        DevToolNumericScope.Room,\n                        sliderBinding.StateKey,\n                        sliderBinding.WidgetId,\n                        values[slider],\n                        0f,\n                        1f,\n                        instance: effect.Index);\n                    if (edit.Committed)\n                    {\n                        RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(\n                            RoomEditorCommandKind.SetEffectAmount,\n                            index: effect.Index,\n                            secondaryIndex: slider,\n                            value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));\n                    }\n                }''',
    s,
    count=1)

# Screen fade slider block.
s = re.sub(
    r'''            float value = Get\(FloatEdits, binding\.StateKey, fades\[i\]\);\n            BeginSettingRow\(binding\.Label, 0f, out _\);\n            bool changed = ImGui\.SliderFloat\(\n                binding\.WidgetId,\n                ref value,\n                0f,\n                1f,\n                "%.3f",\n                ImGuiSliderFlags\.AlwaysClamp\);\n            if \(FinishFloatEdit\(binding\.StateKey, fades\[i\], ref value, changed\)\)\n            \{\n                RoomEditorCommandQueue\.Enqueue\(new RoomEditorCommand\(\n                    terrain \? RoomEditorCommandKind\.SetTerrainPaletteFade : RoomEditorCommandKind\.SetPaletteFade,\n                    index: i,\n                    value: new EditorPropertyValue\(EditorPropertyKind\.Float, x: value\)\)\);\n            \}''',
    '''            BeginSettingRow(binding.Label, 0f, out _);\n            DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n                DevToolNumericScope.Room,\n                binding.StateKey,\n                binding.WidgetId,\n                fades[i],\n                0f,\n                1f);\n            if (edit.Committed)\n            {\n                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(\n                    terrain ? RoomEditorCommandKind.SetTerrainPaletteFade : RoomEditorCommandKind.SetPaletteFade,\n                    index: i,\n                    value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));\n            }''',
    s,
    count=1)

# Shared room scalar methods: replace private state machine with thin wrappers.
start = s.index('    private static void DrawFloat(string key, float current, float min, float max)')
end = s.index('    private static void SendSetting(string key, EditorPropertyValue value)', start)
replacement = '''    private static void DrawFloat(string key, float current, float min, float max)\n    {\n        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.Room,\n            key,\n            CachedId(SettingWidgetIds, key, "##RoomSetting"),\n            current,\n            min,\n            max);\n        if (edit.Committed)\n            SendSetting(key, new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value));\n    }\n\n    private static void DrawInt(string key, int current)\n    {\n        DevToolNumericEditResult<int> edit = DevToolNumericWidgets.InputInt(\n            DevToolNumericScope.Room,\n            key,\n            CachedId(SettingWidgetIds, key, "##RoomSetting"),\n            current,\n            1,\n            10);\n        if (edit.Committed)\n            SendSetting(key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: edit.Value));\n    }\n\n'''
s = s[:start] + replacement + s[end:]

# No private transaction/cache references should survive.
for forbidden in ('PendingFloatEdit', 'PendingIntEdit', 'DirtyFloatEdits', 'DirtyIntEdits', 'PendingFloatEdits', 'PendingIntEdits', 'FinishFloatEdit', 'FinishIntEdit', 'FloatEdits', 'IntEdits'):
    if forbidden in s:
        raise SystemExit(f'RoomSettings migration left private numeric state: {forbidden}')

p.write_text(s, encoding='utf-8')
