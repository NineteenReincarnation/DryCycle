using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Removes no-longer-used bulk-selection controls from the Weather Zones panel
/// and compacts the remaining authoring controls after the editor is created.
/// </summary>
internal static class WeatherSpatialSelectionUiCleanup
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const string MarkerId = "DryCycle_Weather_SelectionUi_Clean";

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
        if (editor == null || HasMarker(editor))
        {
            return;
        }

        Remove(editor, "SelectConnected");
        Remove(editor, "SelectOffscreen");
        Remove(editor, "StopGate");
        Remove(editor, "StopSubregion");
        Remove(editor, "ForceAllow");
        Remove(editor, "ForceDeny");
        Remove(editor, "ForceInherit");

        Stretch(editor, "SelectSubregion", 8f, 284f);
        Stretch(editor, "SelectShelters", 8f, 140f);
        Stretch(editor, "SelectGates", 152f, 140f);

        // Two deleted rows sat above the history/validation controls: the old Stop-*
        // row and the explicit Sel Allow/Deny/Inherit row. Move everything below
        // them upward by 50 px so the panel remains compact.
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

        editor.subNodes.Add(new CleanupMarker(editor.owner, editor));
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

    private sealed class CleanupMarker : DevUINode
    {
        internal CleanupMarker(DevInterface.DevUI owner, DevUINode parent)
            : base(owner, MarkerId, parent)
        {
        }
    }
}
