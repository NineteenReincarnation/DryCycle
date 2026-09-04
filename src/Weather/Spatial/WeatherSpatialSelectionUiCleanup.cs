using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Final pass over the Weather Zones panel. Removes retired controls, fixes the
/// weather picker so it lives in the editor's coordinate space, and replaces the
/// preview +/- controls with directly editable percentage fields.
/// </summary>
internal static partial class WeatherSpatialSelectionUiCleanup
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const string MarkerId = "DryCycle_Weather_SelectionUi_Clean";
    private const string BrokenPickerId = "DryCycle_Weather_Target_Picker_Node";

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

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
        _enabled = false;
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

        if (HasMarker(editor))
        {
            RefreshForbiddenTerminology(editor);
            return;
        }

        // Retired bulk-selection controls.
        Remove(editor, "SelectConnected");
        Remove(editor, "SelectOffscreen");
        Remove(editor, "StopGate");
        Remove(editor, "StopSubregion");
        Remove(editor, "ForceAllow");
        Remove(editor, "ForceDeny");
        Remove(editor, "ForceInherit");

        // Binary authoring no longer has a persistent brush mode. RMB derives the
        // opposite state from each room as it is touched. The old apply button is kept
        // but becomes a compact Toggle Sel bulk action handled by the toggle runtime.
        Remove(editor, "Brush");
        Stretch(editor, "ApplySelected", 208f, 84f);
        if (Find(editor, "ApplySelected") is Button applySelected)
        {
            applySelected.Text = "Toggle Sel";
        }

        // Remove both the original arrow selector and the first picker implementation.
        // The latter inherited directly from DevUINode, so its Positioned children were
        // accidentally screen-relative instead of Weather-Zones-panel-relative.
        Remove(editor, "TargetPrev");
        Remove(editor, "Target");
        Remove(editor, "TargetNext");
        Remove(editor, BrokenPickerId);

        // Preview intensity is typed directly now; +/- and the passive percentage label
        // are no longer part of the UI.
        Remove(editor, "PreviewMinus");
        Remove(editor, "PreviewPlus");
        Remove(editor, "PreviewValue");

        Stretch(editor, "SelectSubregion", 8f, 284f);
        Stretch(editor, "SelectShelters", 8f, 140f);
        Stretch(editor, "SelectGates", 152f, 140f);
        Stretch(editor, "Preview", 8f, 140f);
        Stretch(editor, "Selection", 8f, 140f);

        // Make room for the family probability row above SubWeather chance.
        // Everything below it moves down by one standard row.
        ShiftY(editor, "Preview", -22f);
        ShiftY(editor, "Selection", -22f);
        ShiftY(editor, "SelectAll", -22f);
        ShiftY(editor, "SelectNone", -22f);
        ShiftY(editor, "SelectInvert", -22f);
        ShiftY(editor, "SelectSubregion", -22f);
        ShiftY(editor, "SelectShelters", -22f);
        ShiftY(editor, "SelectGates", -22f);

        // Two deleted selection rows sat above history/validation controls.
        ShiftY(editor, "Undo", 50f);
        ShiftY(editor, "Redo", 50f);
        ShiftY(editor, "Validate", 50f);
        ShiftY(editor, "Save", 50f);
        ShiftY(editor, "Repair", 50f);
        ShiftY(editor, "Status", 50f);
        ShiftY(editor, "Path", 50f);
        for (int i = 0; i < 7; i++)
        {
            ShiftY(editor, "Issue" + i, 50f);
        }

        ShiftY(editor, "Undo", -22f);
        ShiftY(editor, "Redo", -22f);
        ShiftY(editor, "Validate", -22f);
        ShiftY(editor, "Save", -22f);
        ShiftY(editor, "Repair", -22f);
        ShiftY(editor, "Status", -22f);
        ShiftY(editor, "Path", -22f);
        for (int i = 0; i < 7; i++)
        {
            ShiftY(editor, "Issue" + i, -22f);
        }

        // These are deliberately appended last so their sprites and click handling sit
        // above the older editor controls that the popup temporarily covers.
        editor.subNodes.Add(new WeatherChanceInput(
            editor.owner,
            editor,
            editor,
            familyChance: false));
        editor.subNodes.Add(new WeatherChanceInput(
            editor.owner,
            editor,
            editor,
            familyChance: true));
        editor.subNodes.Add(new PreviewPercentInput(editor.owner, editor, editor));
        editor.subNodes.Add(new FixedTargetPicker(editor.owner, editor, editor));
        editor.subNodes.Add(new ClearRegionZonesControl(editor.owner, editor, editor));
        editor.subNodes.Add(new CleanupMarker(editor.owner, editor));
        RefreshForbiddenTerminology(editor);
        editor.Refresh();
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

    private static bool HasMarker(DevUINode editor)
    {
        for (int i = 0; i < editor.subNodes.Count; i++)
        {
            if (editor.subNodes[i]?.IDstring == MarkerId)
            {
                return true;
            }
        }
        return false;
    }

    private static DevUINode Find(DevUINode editor, string id)
    {
        for (int i = 0; i < editor.subNodes.Count; i++)
        {
            DevUINode node = editor.subNodes[i];
            if (node != null && node.IDstring == id)
            {
                return node;
            }
        }
        return null;
    }

    private static void Remove(DevUINode editor, string id)
    {
        for (int i = editor.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode node = editor.subNodes[i];
            if (node == null || node.IDstring != id)
            {
                continue;
            }

            editor.subNodes.RemoveAt(i);
            node.ClearSprites();
        }
    }

    private static void ShiftY(DevUINode editor, string id, float deltaY)
    {
        if (Find(editor, id) is not PositionedDevUINode node)
        {
            return;
        }

        node.pos = new Vector2(node.pos.x, node.pos.y + deltaY);
        node.Refresh();
    }

    private static void Stretch(DevUINode editor, string id, float x, float width)
    {
        if (Find(editor, id) is not RectangularDevUINode node)
        {
            return;
        }

        node.pos = new Vector2(x, node.pos.y);
        node.size = new Vector2(width, node.size.y);
        if (node.fSprites.Count > 0)
        {
            node.fSprites[0].scaleX = width;
        }
        node.Refresh();
    }

    private static void RefreshForbiddenTerminology(DevUINode root)
    {
        if (root == null)
        {
            return;
        }

        if (root is Button button && !string.IsNullOrEmpty(button.Text))
        {
            // Internal enum value remains Deny for save compatibility/migration code,
            // but developer-facing terminology is now consistently Forbidden.
            button.Text = button.Text
                .Replace("DENY", "FORBIDDEN")
                .Replace("Deny", "Forbidden");
        }

        if (root.subNodes == null)
        {
            return;
        }

        for (int i = 0; i < root.subNodes.Count; i++)
        {
            RefreshForbiddenTerminology(root.subNodes[i]);
        }
    }

    private sealed class CleanupMarker : DevUINode
    {
        internal CleanupMarker(DevInterface.DevUI owner, DevUINode parent)
            : base(owner, MarkerId, parent)
        {
        }
    }
}
