using System;
using System.Reflection;
using DevInterface;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    private const string FamilyChancePrefix = "DryCycle_Weather_Family_Chance_";
    private const string SubWeatherChancePrefix = "DryCycle_Weather_SubWeather_Chance_";

    private static void RefreshInactiveChanceFields(DevUINode editor)
    {
        if (editor == null)
        {
            return;
        }

        FieldInfo regionField = editor.GetType().GetField(
            "_regionId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        string regionId = (regionField?.GetValue(editor) as string ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        if (regionId.Length == 0)
        {
            return;
        }

        RefreshInactiveChanceFieldsRecursive(editor, regionId);
    }

    private static void RefreshInactiveChanceFieldsRecursive(DevUINode node, string regionId)
    {
        if (node == null)
        {
            return;
        }

        if (node is Button button && IsInactiveChanceButton(button, regionId))
        {
            CancelPercentEditing(button);
            button.Text = "--";
        }

        if (node.subNodes == null)
        {
            return;
        }

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            RefreshInactiveChanceFieldsRecursive(node.subNodes[i], regionId);
        }
    }

    private static bool IsInactiveChanceButton(Button button, string regionId)
    {
        string id = button?.IDstring ?? string.Empty;
        if (id.StartsWith(FamilyChancePrefix, StringComparison.Ordinal))
        {
            string scheduleFamilyId = id.Substring(FamilyChancePrefix.Length);
            return !WeatherSpatialRegistry.TryGetFamilySchedule(
                       regionId,
                       scheduleFamilyId,
                       out bool enabled,
                       out _) ||
                   !enabled;
        }

        if (!id.StartsWith(SubWeatherChancePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = id.Substring(SubWeatherChancePrefix.Length);
        int separator = suffix.LastIndexOf('_');
        if (separator <= 0 ||
            separator >= suffix.Length - 1 ||
            !int.TryParse(suffix.Substring(separator + 1), out int memberIndex))
        {
            return true;
        }

        string childFamilyId = suffix.Substring(0, separator);
        if (!WeatherSpatialCatalog.TryGetFamily(childFamilyId, out WeatherSpatialFamily family) ||
            memberIndex < 0 ||
            memberIndex >= family.Members.Count)
        {
            return true;
        }

        if (!WeatherSpatialRegistry.TryGetFamilySchedule(
                regionId,
                family.Id,
                out bool familyEnabled,
                out _) ||
            !familyEnabled)
        {
            return true;
        }

        WeatherSpatialMember member = family.Members[memberIndex];
        return !WeatherSpatialRegistry.TryGetSubWeatherSchedule(
                   regionId,
                   member.Kind,
                   member.Id,
                   out bool childEnabled,
                   out _) ||
               !childEnabled;
    }

    private static void CancelPercentEditing(Button button)
    {
        Type type = button?.GetType();
        while (type != null)
        {
            FieldInfo editingField = type.GetField(
                "_editing",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (editingField != null && editingField.FieldType == typeof(bool))
            {
                editingField.SetValue(button, false);
                return;
            }
            type = type.BaseType;
        }
    }
}
