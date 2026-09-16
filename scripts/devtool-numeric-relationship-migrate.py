from pathlib import Path

p = Path('src/DevUI/DevTool/RWImGui/RelationshipEditorView.cs')
s = p.read_text(encoding='utf-8')
old = '''        string editKey = GetInspectorEditKey(snapshot.PrimaryCreature, row.CreatureType, snapshot.SelectedDirection);\n        float intensity = GetIntensity(editKey, relationship.Intensity);\n        bool changed = ImGui.SliderFloat(DevToolUiSettings.T("强度##RelationshipIntensity", "Intensity##RelationshipIntensity"), ref intensity, 0f, 1f, "%.3f");\n        IntensityEdits[editKey] = intensity;\n        if (ImGui.IsItemDeactivatedAfterEdit())\n        {\n            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(\n                RelationshipEditorCommandKind.SetRelationshipIntensity,\n                primary: snapshot.PrimaryCreature,\n                other: row.CreatureType,\n                value: intensity,\n                direction: snapshot.SelectedDirection));\n        }\n        else if (!changed && !ImGui.IsItemActive())\n        {\n            IntensityEdits[editKey] = relationship.Intensity;\n        }'''
new = '''        string editKey = GetInspectorEditKey(snapshot.PrimaryCreature, row.CreatureType, snapshot.SelectedDirection);\n        DevToolNumericEditResult<float> intensityEdit = DevToolNumericWidgets.SliderFloat(\n            DevToolNumericScope.Relationship,\n            editKey,\n            DevToolUiSettings.T("强度##RelationshipIntensity", "Intensity##RelationshipIntensity"),\n            relationship.Intensity,\n            0f,\n            1f);\n        if (intensityEdit.Committed)\n        {\n            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(\n                RelationshipEditorCommandKind.SetRelationshipIntensity,\n                primary: snapshot.PrimaryCreature,\n                other: row.CreatureType,\n                value: intensityEdit.Value,\n                direction: snapshot.SelectedDirection));\n        }'''
if old not in s:
    raise SystemExit('relationship numeric edit anchor missing')
s = s.replace(old, new, 1)
# IntensityEdits is no longer needed after this migration.
s = s.replace('    private static readonly Dictionary<string, float> IntensityEdits = new(StringComparer.Ordinal);\n', '')
s = s.replace('        IntensityEdits.Clear();\n', '')
s = s.replace('                IntensityEdits.Clear();\n', '')
s = s.replace('            IntensityEdits.Remove(editKey);\n', '            DevToolNumericWidgets.Discard(DevToolNumericScope.Relationship, editKey);\n')
start = s.find('    private static float GetIntensity(string key, float fallback)')
if start >= 0:
    end = s.find('    private static void EnsureCreatureLabels', start)
    if end < 0:
        raise SystemExit('relationship GetIntensity end anchor missing')
    s = s[:start] + s[end:]
if 'IntensityEdits' in s or 'GetIntensity(' in s or 'IsItemDeactivatedAfterEdit()' in s:
    raise SystemExit('relationship migration left private numeric state')
p.write_text(s, encoding='utf-8')
