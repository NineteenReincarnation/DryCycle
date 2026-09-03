using System;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Integrates Weather Zones into MapPage's native Dev View tool list.
///
/// Weather Zones, Room Attractiveness and Sub Regions are mutually exclusive map
/// authoring tools. The weather editor is created only when its MapPage button is
/// clicked; simply opening the Map page never spawns the large editor panel.
/// </summary>
internal static class WeatherSpatialMapMenuRuntime
{
    private const string MenuButtonId = "DryCycle_Weather_Zones_Button";
    private const string EditorNodeId = "DryCycle_WeatherSpatial";

    private static bool _enabled;
    private static ConstructorInfo _editorConstructor;
    private static bool _constructorResolved;
    private static MapPage _leftPanPage;
    private static bool _leftPanActive;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.NewMode += MapPage_NewMode;
        On.DevInterface.MapPage.Signal += MapPage_Signal;
        On.DevInterface.MapPage.Update += MapPage_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.NewMode -= MapPage_NewMode;
        On.DevInterface.MapPage.Signal -= MapPage_Signal;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        WeatherSpatialPreview.Clear();
        _leftPanPage = null;
        _leftPanActive = false;
        _editorConstructor = null;
        _constructorResolved = false;
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        DevUINode editor = FindEditor(self);
        DevInterface.DevUI owner = self?.owner;
        if (editor == null || owner == null)
        {
            ClearLeftPan(self);
            orig(self);
            return;
        }

        bool realLeftDown = owner.mouseDown;
        bool realLeftClick = owner.mouseClick;
        bool rightDown = Input.GetMouseButton(1);
        bool rightClick = Input.GetMouseButtonDown(1);
        bool overInteractiveUi = IsMouseOverInteractiveUi(self, editor);

        if (realLeftClick)
        {
            _leftPanPage = self;
            _leftPanActive = !overInteractiveUi;
        }
        if (!realLeftDown && ReferenceEquals(_leftPanPage, self))
        {
            _leftPanActive = false;
            _leftPanPage = null;
        }

        bool panWithLeft = realLeftDown &&
                           _leftPanActive &&
                           ReferenceEquals(_leftPanPage, self);
        bool authorWithRight = (rightDown || rightClick) && !overInteractiveUi;

        // The existing weather editor is written against DevUI's left-button fields.
        // While Weather Zones is open, feed it RMB instead; LMB is reserved for the
        // native map pan gesture. This keeps all brush/selection behavior in one place
        // (including Shift+RMB) without allowing RoomPanel dragging.
        if (authorWithRight)
        {
            owner.mouseDown = rightDown;
            owner.mouseClick = rightClick;
        }
        else if (panWithLeft)
        {
            owner.mouseDown = false;
            owner.mouseClick = false;
        }

        try
        {
            orig(self);
        }
        finally
        {
            owner.mouseDown = realLeftDown;
            owner.mouseClick = realLeftClick;
        }

        if (panWithLeft)
        {
            self.panPos -= owner.lastMousePos - owner.mousePos;
            self.Refresh();
        }
    }

    private static void ClearLeftPan(MapPage self)
    {
        if (ReferenceEquals(_leftPanPage, self))
        {
            _leftPanPage = null;
            _leftPanActive = false;
        }
    }

    private static bool IsMouseOverInteractiveUi(MapPage mapPage, DevUINode editor)
    {
        if (editor is RectangularDevUINode editorRect && editorRect.MouseOver)
        {
            return true;
        }

        if (mapPage?.subNodes == null)
        {
            return false;
        }

        for (int i = 0; i < mapPage.subNodes.Count; i++)
        {
            DevUINode node = mapPage.subNodes[i];
            if (ReferenceEquals(node, editor) || node is RoomPanel)
            {
                continue;
            }
            if (node is Button button && button.MouseOver)
            {
                return true;
            }
        }
        return false;
    }

    private static void MapPage_NewMode(
        On.DevInterface.MapPage.orig_NewMode orig,
        MapPage self)
    {
        // NewMode is the native mode boundary. Closing before vanilla clears its own
        // modeSpecificNodes guarantees room dragging / preview state is restored first.
        CloseEditor(self, refresh: false);
        orig(self);

        if (self == null || self.canonView)
        {
            return;
        }

        Button button = new(
            self.owner,
            MenuButtonId,
            self,
            new Vector2(170f, 580f),
            220f,
            "Weather Zones");
        self.modeSpecificNodes.Add(button);
        self.subNodes.Add(button);
    }

    private static void MapPage_Signal(
        On.DevInterface.MapPage.orig_Signal orig,
        MapPage self,
        DevUISignalType type,
        DevUINode sender,
        string message)
    {
        if (type == DevUISignalType.ButtonClick && sender != null)
        {
            if (sender.IDstring == MenuButtonId)
            {
                if (FindEditor(self) != null)
                {
                    CloseEditor(self, refresh: true);
                }
                else
                {
                    OpenEditor(self);
                }
                return;
            }

            // Native map authoring tools share the same workspace with Weather Zones.
            // Closing weather first makes the tools truly mutually exclusive instead
            // of merely drawing one panel on top of another.
            if (sender.IDstring == "Room_Attractiveness_Button" ||
                sender.IDstring == "Sub_Regions_Toggle")
            {
                CloseEditor(self, refresh: false);
            }
        }

        orig(self, type, sender, message);
    }

    private static void OpenEditor(MapPage mapPage)
    {
        if (mapPage == null || mapPage.canonView || FindEditor(mapPage) != null)
        {
            return;
        }

        CloseAttractiveness(mapPage);
        mapPage.subRegionsMode = false;

        ConstructorInfo constructor = ResolveEditorConstructor();
        if (constructor == null)
        {
            Plugin.Logger?.LogError(
                "DryCycle Weather Zones: could not resolve the WeatherSpatial editor constructor.");
            return;
        }

        try
        {
            object created = constructor.Invoke(new object[] { mapPage.owner, mapPage });
            if (created is not DevUINode editor)
            {
                Plugin.Logger?.LogError(
                    "DryCycle Weather Zones: editor constructor returned an invalid DevUI node.");
                return;
            }

            if (editor is Panel panel)
            {
                panel.collapsed = false;
            }

            AddShortcutLegend(editor);
            mapPage.subNodes.Add(editor);
            editor.Refresh();
            mapPage.Refresh();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("DryCycle Weather Zones: failed to open map editor: " + ex);
            CloseEditor(mapPage, refresh: true);
        }
    }

    private static void AddShortcutLegend(DevUINode editor)
    {
        if (editor == null)
        {
            return;
        }

        AddLegendLabel(editor, "WeatherShortcutHeader", 92f, "Shortcuts");
        AddLegendLabel(editor, "WeatherShortcutPan", 74f, "LMB Drag  - Pan Map");
        AddLegendLabel(editor, "WeatherShortcutPaint", 56f, "RMB Drag  - Paint Brush");
        AddLegendLabel(editor, "WeatherShortcutSelect", 38f, "Shift + RMB  - Toggle Room Select");
        AddLegendLabel(editor, "WeatherShortcutKeys", 20f, "Ctrl+S Save   Ctrl+Z Undo   Ctrl+Y Redo");
    }

    private static void AddLegendLabel(DevUINode editor, string id, float y, string text)
    {
        DevUILabel label = new(
            editor.owner,
            id,
            editor,
            new Vector2(8f, y),
            284f,
            text);
        label.spriteColor = new Color(0f, 0f, 0f);
        label.textColor = new Color(1f, 1f, 1f);
        editor.subNodes.Add(label);
    }

    private static void CloseAttractiveness(MapPage mapPage)
    {
        if (mapPage?.attractivenessPanel == null)
        {
            return;
        }

        mapPage.subNodes.Remove(mapPage.attractivenessPanel);
        mapPage.attractivenessPanel.ClearSprites();
        mapPage.attractivenessPanel = null;
    }

    private static void CloseEditor(MapPage mapPage, bool refresh)
    {
        DevUINode editor = FindEditor(mapPage);
        if (editor == null)
        {
            return;
        }

        mapPage.subNodes.Remove(editor);
        editor.ClearSprites();
        WeatherSpatialPreview.Clear();
        if (refresh)
        {
            mapPage.Refresh();
        }
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
            if (node != null &&
                string.Equals(node.IDstring, EditorNodeId, StringComparison.Ordinal))
            {
                return node;
            }
        }
        return null;
    }

    private static ConstructorInfo ResolveEditorConstructor()
    {
        if (_constructorResolved)
        {
            return _editorConstructor;
        }
        _constructorResolved = true;

        Type editorType = typeof(WeatherSpatialDevUI).GetNestedType(
            "WeatherSpatialEditorNode",
            BindingFlags.NonPublic);
        _editorConstructor = editorType?.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(DevInterface.DevUI), typeof(MapPage) },
            modifiers: null);
        return _editorConstructor;
    }
}
