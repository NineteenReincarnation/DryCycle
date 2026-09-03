using System;
using System.Collections;
using System.Reflection;
using DevInterface;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Presents Weather Zones as a strict two-state authoring UI: Allow / Forbidden.
/// The legacy Inherit enum value is kept only as an internal missing-rule sentinel for
/// backwards-compatible loading and undo restoration; it is never offered or displayed.
/// </summary>
internal static class WeatherSpatialBinaryRuleUiRuntime
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";

    private static bool _enabled;
    private static Type _editorType;
    private static FieldInfo _brushField;
    private static FieldInfo _regionIdField;
    private static FieldInfo _targetIndexField;
    private static FieldInfo _undoField;
    private static MethodInfo _cycleDefaultMethod;
    private static MethodInfo _updateStateLabelsMethod;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.Button.Clicked += Button_Clicked;
        On.DevInterface.MapPage.Update += MapPage_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update -= MapPage_Update;
        On.DevInterface.Button.Clicked -= Button_Clicked;
        ClearReflectionCache();
        _enabled = false;
    }

    private static void Button_Clicked(
        On.DevInterface.Button.orig_Clicked orig,
        Button self)
    {
        if (self == null || !TryFindEditor(self, out DevUINode editor))
        {
            orig(self);
            return;
        }

        EnsureEditorReflection(editor);

        if (self.IDstring == "Brush")
        {
            ToggleBrush(editor);
            return;
        }

        if (self.IDstring == "Default")
        {
            ToggleRegionDefault(editor);
            return;
        }

        orig(self);
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        DevUINode editor = FindEditor(self);
        if (editor == null)
        {
            return;
        }

        EnsureEditorReflection(editor);
        NormalizeBrush(editor);
        RewriteBinaryLabels(editor);
    }

    private static void ToggleBrush(DevUINode editor)
    {
        if (_brushField == null)
        {
            return;
        }

        WeatherSpatialRule current = _brushField.GetValue(editor) is WeatherSpatialRule value
            ? value
            : WeatherSpatialRule.Allow;
        WeatherSpatialRule next = current == WeatherSpatialRule.Allow
            ? WeatherSpatialRule.Deny
            : WeatherSpatialRule.Allow;

        _brushField.SetValue(editor, next);
        _updateStateLabelsMethod?.Invoke(editor, null);
        RewriteBinaryLabels(editor);
    }

    private static void NormalizeBrush(DevUINode editor)
    {
        if (_brushField == null)
        {
            return;
        }

        if (_brushField.GetValue(editor) is WeatherSpatialRule rule &&
            rule == WeatherSpatialRule.Inherit)
        {
            _brushField.SetValue(editor, WeatherSpatialRule.Allow);
            _updateStateLabelsMethod?.Invoke(editor, null);
        }
    }

    private static void ToggleRegionDefault(DevUINode editor)
    {
        if (_regionIdField == null ||
            _targetIndexField == null ||
            _cycleDefaultMethod == null)
        {
            return;
        }

        string regionId = _regionIdField.GetValue(editor) as string ?? string.Empty;
        int count = WeatherSpatialCatalog.AllTargets.Count;
        if (count <= 0)
        {
            return;
        }

        int index = _targetIndexField.GetValue(editor) is int targetIndex
            ? Math.Max(0, Math.Min(count - 1, targetIndex))
            : 0;
        WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[index];

        WeatherSpatialRule raw = WeatherSpatialRegistry.GetDefaultRule(regionId, target);
        WeatherSpatialRule effective = EffectiveDefault(target, raw);
        WeatherSpatialRule desired = effective == WeatherSpatialRule.Allow
            ? WeatherSpatialRule.Deny
            : WeatherSpatialRule.Allow;

        int steps = 0;
        while (raw != desired && steps < 3)
        {
            _cycleDefaultMethod.Invoke(editor, null);
            steps++;
            raw = WeatherSpatialRegistry.GetDefaultRule(regionId, target);
        }

        if (steps > 1)
        {
            MergeLastTwoDefaultUndoCommands(editor, desired);
        }

        _updateStateLabelsMethod?.Invoke(editor, null);
        RewriteBinaryLabels(editor);
    }

    private static WeatherSpatialRule EffectiveDefault(
        in WeatherSpatialTarget target,
        WeatherSpatialRule raw)
    {
        if (raw != WeatherSpatialRule.Inherit)
        {
            return raw;
        }

        // A Family without an explicit region override follows the global default.
        // An exact child without an override adds no extra restriction once its parent
        // Family has passed the prerequisite gate, so its binary effective state is Allow.
        return target.IsFamily
            ? WeatherSpatialRegistry.GlobalDefault
            : WeatherSpatialRule.Allow;
    }

    private static void RewriteBinaryLabels(DevUINode editor)
    {
        if (editor == null ||
            _regionIdField == null ||
            _targetIndexField == null)
        {
            return;
        }

        int count = WeatherSpatialCatalog.AllTargets.Count;
        if (count <= 0)
        {
            return;
        }

        int index = _targetIndexField.GetValue(editor) is int targetIndex
            ? Math.Max(0, Math.Min(count - 1, targetIndex))
            : 0;
        WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[index];
        string regionId = _regionIdField.GetValue(editor) as string ?? string.Empty;

        WeatherSpatialRule rawDefault = WeatherSpatialRegistry.GetDefaultRule(regionId, target);
        WeatherSpatialRule effectiveDefault = EffectiveDefault(target, rawDefault);

        if (FindDirect(editor, "Default") is Button defaultButton)
        {
            defaultButton.Text = "Region Default: " + RuleText(effectiveDefault);
        }

        if (FindDirect(editor, "Brush") is Button brushButton &&
            _brushField?.GetValue(editor) is WeatherSpatialRule brush)
        {
            brushButton.Text = "Brush: " + RuleText(
                brush == WeatherSpatialRule.Deny
                    ? WeatherSpatialRule.Deny
                    : WeatherSpatialRule.Allow);
        }
    }

    private static string RuleText(WeatherSpatialRule rule)
    {
        return rule == WeatherSpatialRule.Allow ? "ALLOW" : "FORBIDDEN";
    }

    private static void MergeLastTwoDefaultUndoCommands(
        DevUINode editor,
        WeatherSpatialRule desired)
    {
        if (_undoField?.GetValue(editor) is not IList undo || undo.Count < 2)
        {
            return;
        }

        object first = undo[undo.Count - 2];
        object second = undo[undo.Count - 1];
        if (first == null || second == null || first.GetType() != second.GetType())
        {
            return;
        }

        FieldInfo changesField = first.GetType().GetField(
            "Changes",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (changesField?.GetValue(first) is not IList changes || changes.Count == 0)
        {
            return;
        }

        object firstChange = changes[0];
        if (firstChange == null)
        {
            return;
        }

        FieldInfo afterField = firstChange.GetType().GetField(
            "After",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (afterField == null)
        {
            return;
        }

        afterField.SetValue(firstChange, desired);
        undo.RemoveAt(undo.Count - 1);
    }

    private static bool TryFindEditor(Button button, out DevUINode editor)
    {
        editor = null;
        DevUINode node = button;
        while (node != null)
        {
            if (node.IDstring == EditorNodeId)
            {
                editor = node;
                return true;
            }
            node = node.parentNode;
        }
        return false;
    }

    private static DevUINode FindEditor(MapPage mapPage)
    {
        if (mapPage?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < mapPage.subNodes.Count; i++)
        {
            DevUINode node = mapPage.subNodes[i];
            if (node != null && node.IDstring == EditorNodeId)
            {
                return node;
            }
        }
        return null;
    }

    private static DevUINode FindDirect(DevUINode parent, string id)
    {
        if (parent?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node != null && node.IDstring == id)
            {
                return node;
            }
        }
        return null;
    }

    private static void EnsureEditorReflection(DevUINode editor)
    {
        Type type = editor?.GetType();
        if (type == null || type == _editorType)
        {
            return;
        }

        _editorType = type;
        BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        _brushField = type.GetField("_brush", fields);
        _regionIdField = type.GetField("_regionId", fields);
        _targetIndexField = type.GetField("_targetIndex", fields);
        _undoField = type.GetField("_undo", fields);
        _cycleDefaultMethod = type.GetMethod("CycleDefault", fields);
        _updateStateLabelsMethod = type.GetMethod("UpdateStateLabels", fields);
    }

    private static void ClearReflectionCache()
    {
        _editorType = null;
        _brushField = null;
        _regionIdField = null;
        _targetIndexField = null;
        _undoField = null;
        _cycleDefaultMethod = null;
        _updateStateLabelsMethod = null;
    }
}
