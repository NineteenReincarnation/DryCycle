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

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.NewMode += MapPage_NewMode;
        On.DevInterface.MapPage.Signal += MapPage_Signal;
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
        WeatherSpatialPreview.Clear();
        _editorConstructor = null;
        _constructorResolved = false;
        _enabled = false;
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
