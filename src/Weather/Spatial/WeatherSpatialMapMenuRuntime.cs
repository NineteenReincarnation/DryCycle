using System;
using System.Reflection;
using DevInterface;
using DryCycle.TemperatureSystem;
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
    internal const string MenuButtonId = "DryCycle_Weather_Zones_Button";
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const string TargetPopupId = "DryCycle_Weather_Target_Popup_Fixed";
    private const string HoverInfoPanelId = "DryCycle_Weather_Room_Hover_Info";

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
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool overInteractiveUi = IsMouseOverInteractiveUi(self, editor);

        if (realLeftClick)
        {
            _leftPanPage = self;
            _leftPanActive = !overInteractiveUi && !shift;
        }
        if (!realLeftDown && ReferenceEquals(_leftPanPage, self))
        {
            _leftPanActive = false;
            _leftPanPage = null;
        }

        bool panWithLeft = realLeftDown &&
                           _leftPanActive &&
                           ReferenceEquals(_leftPanPage, self);
        bool authorWithRight = (rightDown || rightClick) && !shift && !overInteractiveUi;

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

        // The weather picker deliberately opens to the left of the main editor panel.
        // Because that popup sits outside editorRect, include it explicitly so LMB does
        // not pan the map and RMB does not paint rooms through the popup.
        if (IsMouseOverNode(editor, TargetPopupId) ||
            IsMouseOverNode(editor, HoverInfoPanelId))
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

    private static bool IsMouseOverNode(DevUINode root, string id)
    {
        if (root == null)
        {
            return false;
        }

        if (string.Equals(root.IDstring, id, StringComparison.Ordinal) &&
            root is RectangularDevUINode rect &&
            rect.MouseOver)
        {
            return true;
        }

        if (root.subNodes == null)
        {
            return false;
        }

        for (int i = 0; i < root.subNodes.Count; i++)
        {
            if (IsMouseOverNode(root.subNodes[i], id))
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
                TemperatureSetsMapEditorRuntime.CloseEditor(self, refresh: false);
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

            AddTargetPicker(editor);
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

    private static void AddTargetPicker(DevUINode editor)
    {
        if (editor == null)
        {
            return;
        }

        RemoveLegacyTargetControls(editor);
        TargetPickerNode picker = new(editor.owner, editor, editor);
        editor.subNodes.Add(picker);
    }

    private static void RemoveLegacyTargetControls(DevUINode editor)
    {
        for (int i = editor.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode node = editor.subNodes[i];
            if (node == null ||
                (node.IDstring != "TargetPrev" &&
                 node.IDstring != "Target" &&
                 node.IDstring != "TargetNext"))
            {
                continue;
            }

            editor.subNodes.RemoveAt(i);
            node.ClearSprites();
        }
    }

    private sealed class TargetPickerNode : DevUINode, IDevUISignals
    {
        private const string MainButtonId = "DryCycle_Weather_Target_Picker";
        private const string ItemPrefix = "DryCycle_Weather_Target_Item_";

        private readonly DevUINode _editor;
        private readonly FieldInfo _targetIndexField;
        private readonly MethodInfo _refreshPreviewTargetMethod;
        private readonly MethodInfo _updateStateLabelsMethod;
        private readonly Button _button;
        private TargetPickerPopup _popup;

        internal TargetPickerNode(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(owner, "DryCycle_Weather_Target_Picker_Node", parent)
        {
            _editor = editor;
            Type editorType = editor.GetType();
            _targetIndexField = editorType.GetField(
                "_targetIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _refreshPreviewTargetMethod = editorType.GetMethod(
                "RefreshPreviewTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _updateStateLabelsMethod = editorType.GetMethod(
                "UpdateStateLabels",
                BindingFlags.Instance | BindingFlags.NonPublic);

            _button = new Button(
                owner,
                MainButtonId,
                this,
                new Vector2(8f, 536f),
                284f,
                string.Empty);
            subNodes.Add(_button);
            RefreshButtonText();
        }

        public override void Update()
        {
            base.Update();
            RefreshButtonText();

            if (_popup != null &&
                owner != null &&
                owner.mouseClick &&
                !_button.MouseOver &&
                !_popup.MouseOver)
            {
                ClosePopup();
            }
        }

        public override void ClearSprites()
        {
            ClosePopup();
            base.ClearSprites();
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            if (type != DevUISignalType.ButtonClick || sender == null)
            {
                return;
            }

            if (sender.IDstring == MainButtonId)
            {
                if (_popup == null)
                {
                    OpenPopup();
                }
                else
                {
                    ClosePopup();
                }
                return;
            }

            if (sender.IDstring.StartsWith(ItemPrefix, StringComparison.Ordinal) &&
                int.TryParse(sender.IDstring.Substring(ItemPrefix.Length), out int index))
            {
                SelectTarget(index);
            }
        }

        private void OpenPopup()
        {
            if (_popup != null)
            {
                return;
            }

            int count = WeatherSpatialCatalog.AllTargets.Count;
            float rowHeight = 20f;
            float height = Mathf.Max(70f, 34f + count * rowHeight);
            float bottom = 536f - height;

            _popup = new TargetPickerPopup(
                owner,
                this,
                new Vector2(8f, bottom),
                new Vector2(284f, height),
                this);
            subNodes.Add(_popup);
            _popup.BuildItems(ItemPrefix, rowHeight);
            _popup.Refresh();
        }

        private void ClosePopup()
        {
            if (_popup == null)
            {
                return;
            }

            subNodes.Remove(_popup);
            _popup.ClearSprites();
            _popup = null;
        }

        private void SelectTarget(int index)
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0 || index < 0 || index >= count || _targetIndexField == null)
            {
                ClosePopup();
                return;
            }

            _targetIndexField.SetValue(_editor, index);
            _refreshPreviewTargetMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
            ClosePopup();
            RefreshButtonText();
        }

        private int GetTargetIndex()
        {
            if (_targetIndexField == null || _editor == null)
            {
                return 0;
            }

            object value = _targetIndexField.GetValue(_editor);
            return value is int index ? index : 0;
        }

        private void RefreshButtonText()
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0)
            {
                _button.Text = "Select Weather";
                return;
            }

            int index = Mathf.Clamp(GetTargetIndex(), 0, count - 1);
            _button.Text = "▼  " + WeatherSpatialCatalog.AllTargets[index].DisplayName;
        }
    }

    private sealed class TargetPickerPopup : Panel, IDevUISignals
    {
        private readonly TargetPickerNode _picker;

        internal TargetPickerPopup(
            DevInterface.DevUI owner,
            DevUINode parent,
            Vector2 pos,
            Vector2 size,
            TargetPickerNode picker)
            : base(owner, "DryCycle_Weather_Target_Popup", parent, pos, size, "Select Weather")
        {
            _picker = picker;
        }

        internal void BuildItems(string itemPrefix, float rowHeight)
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            float y = size.y - 28f - rowHeight;
            for (int i = 0; i < count; i++)
            {
                WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[i];
                Button item = new(
                    owner,
                    itemPrefix + i,
                    this,
                    new Vector2(8f, y),
                    size.x - 16f,
                    target.DisplayName);
                subNodes.Add(item);
                y -= rowHeight;
            }
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            _picker?.Signal(type, sender, message);
        }
    }

    private static void AddShortcutLegend(DevUINode editor)
    {
        if (editor == null)
        {
            return;
        }

        AddLegendLabel(editor, "WeatherShortcutHeader", 110f, "Shortcuts");
        AddLegendLabel(editor, "WeatherShortcutPan", 92f, "LMB Drag  - Pan Map");
        AddLegendLabel(editor, "WeatherShortcutBox", 74f, "Shift + LMB Drag  - Box Select Rooms");
        AddLegendLabel(editor, "WeatherShortcutPaint", 56f, "RMB Drag  - Toggle Weather Zone");
        AddLegendLabel(editor, "WeatherShortcutSelect", 38f, "Shift + RMB  - Toggle Selected Rooms");
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
