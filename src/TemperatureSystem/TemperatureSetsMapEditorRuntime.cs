using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.Controls;
using DryCycle.Weather.Spatial;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Adds a MapPage authoring tool for room-level TemperatureSets values. Unlike the
/// Weather Zones brush, RMB selects exactly one room and the right panel edits it.
/// </summary>
internal static class TemperatureSetsMapEditorRuntime
{
    internal const string MenuButtonId = "DryCycle_Temperature_Sets_Button";
    internal const string EditorNodeId = "DryCycle_TemperatureSets";

    private const string WeatherEditorNodeId = "DryCycle_WeatherSpatial";
    private static bool _enabled;

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
        _enabled = false;
    }

    private static void MapPage_NewMode(
        On.DevInterface.MapPage.orig_NewMode orig,
        MapPage self)
    {
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
            new Vector2(170f, 560f),
            220f,
            "Temperature Sets");
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

            if (sender.IDstring == "Room_Attractiveness_Button" ||
                sender.IDstring == "Sub_Regions_Toggle" ||
                sender.IDstring == WeatherSpatialMapMenuRuntime.MenuButtonId)
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

        CloseNode(mapPage, WeatherEditorNodeId);
        WeatherSpatialPreview.Clear();
        CloseAttractiveness(mapPage);
        mapPage.subRegionsMode = false;

        TemperatureSetsEditorNode editor = new(mapPage.owner, mapPage);
        mapPage.subNodes.Add(editor);
        editor.Refresh();
        mapPage.Refresh();
    }

    internal static void CloseEditor(MapPage mapPage, bool refresh)
    {
        DevUINode editor = FindEditor(mapPage);
        if (editor == null)
        {
            return;
        }

        mapPage.subNodes.Remove(editor);
        editor.ClearSprites();
        if (refresh)
        {
            mapPage.Refresh();
        }
    }

    private static DevUINode FindEditor(MapPage mapPage)
    {
        return FindNode(mapPage, EditorNodeId);
    }

    private static DevUINode FindNode(MapPage mapPage, string id)
    {
        if (mapPage?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < mapPage.subNodes.Count; i++)
        {
            DevUINode node = mapPage.subNodes[i];
            if (node != null && string.Equals(node.IDstring, id, StringComparison.Ordinal))
            {
                return node;
            }
        }
        return null;
    }

    private static void CloseNode(MapPage mapPage, string id)
    {
        DevUINode node = FindNode(mapPage, id);
        if (node == null)
        {
            return;
        }

        mapPage.subNodes.Remove(node);
        node.ClearSprites();
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

    private sealed class TemperatureSetsEditorNode : Panel, IDevUISignals
    {
        private readonly MapPage _mapPage;
        private readonly string _regionId;
        private readonly List<RoomPanel> _roomPanels = new();
        private readonly SelectedRoomOverlay _selectionOverlay;
        private readonly DryCycleNumericSlider _roomHeat;
        private readonly DryCycleNumericSlider _sunlight;
        private readonly DryCycleNumericSlider _roomShade;
        private readonly DryCycleNumericSlider _humidity;
        private readonly DevUILabel _roomLabel;
        private readonly DevUILabel _sourceLabel;
        private readonly DevUILabel _statusLabel;
        private readonly DevUILabel _pathLabel;
        private readonly DevUILabel _warningLabel;

        private RoomPanel _selectedRoom;
        private bool _synchronizing;
        private string _status = "RMB a room to edit its values.";

        internal TemperatureSetsEditorNode(DevInterface.DevUI owner, MapPage mapPage)
            : base(
                owner,
                EditorNodeId,
                mapPage,
                new Vector2(1035f, 370f),
                new Vector2(310f, 350f),
                "DryCycle Temperature Sets")
        {
            _mapPage = mapPage;
            _regionId = (mapPage.world?.region?.name ?? mapPage.world?.name ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            for (int i = 0; i < mapPage.subNodes.Count; i++)
            {
                if (mapPage.subNodes[i] is RoomPanel roomPanel)
                {
                    _roomPanels.Add(roomPanel);
                }
            }

            AddLabel("TemperatureRegion", 8f, 316f, 294f, "Region: " + _regionId);
            _roomLabel = AddLabel("TemperatureRoom", 8f, 294f, 294f, "Room: <right-click one>");
            _sourceLabel = AddLabel("TemperatureSource", 8f, 272f, 294f, "Source: neutral defaults");

            _roomHeat = AddSlider(
                "TemperatureRoomHeat",
                238f,
                "RoomHeat",
                RoomHeatFactor.DefaultHeat,
                -1f,
                1f);
            _sunlight = AddSlider(
                "TemperatureSunlight",
                206f,
                "SunlightIntensity",
                RoomEnvironmentProfile.DefaultSunlightIntensity,
                0f,
                1f);
            _roomShade = AddSlider(
                "TemperatureRoomShade",
                174f,
                "RoomShade",
                RoomEnvironmentProfile.DefaultRoomShade,
                0f,
                1f);
            _humidity = AddSlider(
                "TemperatureHumidity",
                142f,
                "Humidity",
                RoomEnvironmentProfile.DefaultHumidity,
                -1f,
                1f);

            _roomHeat.ValueChanged += Slider_ValueChanged;
            _sunlight.ValueChanged += Slider_ValueChanged;
            _roomShade.ValueChanged += Slider_ValueChanged;
            _humidity.ValueChanged += Slider_ValueChanged;

            AddButton("TemperatureSave", 8f, 108f, 140f, "Save JSON");
            AddButton("TemperatureReset", 152f, 108f, 150f, "Reset Room");
            _statusLabel = AddLabel("TemperatureStatus", 8f, 82f, 294f, string.Empty);
            _pathLabel = AddLabel("TemperaturePath", 8f, 60f, 294f, string.Empty);
            _warningLabel = AddLabel("TemperatureWarnings", 8f, 38f, 294f, string.Empty);
            AddLabel("TemperatureShortcut", 8f, 16f, 294f, "RMB Room - Select   LMB Drag - Pan   Ctrl+S - Save");

            _selectionOverlay = new SelectedRoomOverlay(owner, this);
            subNodes.Add(_selectionOverlay);
            collapsed = false;
            SetRoomDraggingDisabled(true);
            SyncControlsFromSelection();
            UpdateLabels();
        }

        public override void Update()
        {
            base.Update();
            SetRoomDraggingDisabled(true);

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.S))
            {
                Save();
            }

            if (!MouseOver && Input.GetMouseButtonDown(1))
            {
                RoomPanel hovered = HoveredRoomPanel();
                if (hovered != null)
                {
                    SelectRoom(hovered);
                }
            }

            _selectionOverlay.SetRoom(_selectedRoom);
            UpdateLabels();
            BringEditorUiToFront(this);
        }

        public override void ClearSprites()
        {
            SetRoomDraggingDisabled(false);
            base.ClearSprites();
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            if (type != DevUISignalType.ButtonClick || sender == null)
            {
                return;
            }

            if (sender.IDstring == "TemperatureSave")
            {
                Save();
            }
            else if (sender.IDstring == "TemperatureReset")
            {
                ResetSelectedRoom();
            }
        }

        private void SelectRoom(RoomPanel roomPanel)
        {
            if (roomPanel?.roomRep?.room == null)
            {
                return;
            }

            _selectedRoom = roomPanel;
            _status = "Editing " + roomPanel.roomRep.room.name + ".";
            SyncControlsFromSelection();
        }

        private void Slider_ValueChanged(
            DryCycleNumericSlider slider,
            float value,
            float oldValue)
        {
            if (_synchronizing || _selectedRoom?.roomRep?.room == null)
            {
                return;
            }

            RoomEnvironmentProfile profile = new(
                _roomHeat.Value,
                _sunlight.Value,
                _roomShade.Value,
                _humidity.Value);
            TemperatureSetsLoader.SetProfile(_regionId, _selectedRoom.roomRep.room.name, profile);
            _status = "Changed " + _selectedRoom.roomRep.room.name + ". Save JSON to persist.";
        }

        private void ResetSelectedRoom()
        {
            if (_selectedRoom?.roomRep?.room == null)
            {
                _status = "Select a room first.";
                return;
            }

            string roomName = _selectedRoom.roomRep.room.name;
            if (TemperatureSetsLoader.RemoveProfile(_regionId, roomName))
            {
                _status = "Reset " + roomName + " to neutral defaults.";
            }
            else
            {
                _status = roomName + " already uses neutral defaults.";
            }
            SyncControlsFromSelection();
        }

        private void Save()
        {
            _status = TemperatureSetsLoader.Save()
                ? "Saved TemperatureSets.json."
                : "Save failed. See log.";
        }

        private void SyncControlsFromSelection()
        {
            string roomName = _selectedRoom?.roomRep?.room?.name;
            RoomEnvironmentProfile profile = string.IsNullOrEmpty(roomName)
                ? new RoomEnvironmentProfile()
                : TemperatureSetsLoader.GetProfileOrDefault(_regionId, roomName);

            _synchronizing = true;
            _roomHeat.SetValue(profile.RoomHeat);
            _sunlight.SetValue(profile.SunlightIntensity);
            _roomShade.SetValue(profile.RoomShade);
            _humidity.SetValue(profile.Humidity);
            _synchronizing = false;
        }

        private void UpdateLabels()
        {
            string roomName = _selectedRoom?.roomRep?.room?.name;
            bool selected = !string.IsNullOrEmpty(roomName);
            _roomLabel.Text = "Room: " + (selected ? roomName : "<right-click one>");
            _sourceLabel.Text = "Source: " +
                                (selected && TemperatureSetsLoader.HasProfile(_regionId, roomName)
                                    ? "JSON room override"
                                    : "neutral defaults");
            _statusLabel.Text = (TemperatureSetsLoader.Dirty ? "* " : string.Empty) + _status;
            _pathLabel.Text = ShortPath(TemperatureSetsLoader.LoadedPath);

            if (!string.IsNullOrEmpty(TemperatureSetsLoader.LoadError))
            {
                _warningLabel.Text = Truncate(TemperatureSetsLoader.LoadError, 66);
                _warningLabel.textColor = new Color(1f, 0.35f, 0.25f);
            }
            else if (TemperatureSetsLoader.Warnings.Count > 0)
            {
                _warningLabel.Text = TemperatureSetsLoader.Warnings.Count + " JSON warning(s); see log.";
                _warningLabel.textColor = new Color(1f, 0.75f, 0.2f);
            }
            else
            {
                _warningLabel.Text = string.Empty;
                _warningLabel.textColor = Color.white;
            }
        }

        private RoomPanel HoveredRoomPanel()
        {
            for (int i = _roomPanels.Count - 1; i >= 0; i--)
            {
                RoomPanel panel = _roomPanels[i];
                if (panel.Visible && panel.miniMap != null && panel.miniMap.MouseOver)
                {
                    return panel;
                }
            }
            return null;
        }

        private void SetRoomDraggingDisabled(bool disabled)
        {
            for (int i = 0; i < _roomPanels.Count; i++)
            {
                _roomPanels[i].disableDragging = disabled;
            }
        }

        private DryCycleNumericSlider AddSlider(
            string id,
            float y,
            string title,
            float initial,
            float min,
            float max)
        {
            DryCycleNumericSlider slider = new(
                owner,
                id,
                this,
                new Vector2(8f, y),
                title,
                inheritButton: false,
                titleWidth: 116f,
                initialValue: initial,
                minValue: min,
                maxValue: max,
                decimalPlaces: 2,
                inputWidth: 52f,
                defaultValue: initial);
            subNodes.Add(slider);
            return slider;
        }

        private Button AddButton(string id, float x, float y, float width, string text)
        {
            Button button = new(owner, id, this, new Vector2(x, y), width, text);
            subNodes.Add(button);
            return button;
        }

        private DevUILabel AddLabel(string id, float x, float y, float width, string text)
        {
            DevUILabel label = new(owner, id, this, new Vector2(x, y), width, text);
            label.spriteColor = new Color(0f, 0f, 0f);
            label.textColor = Color.white;
            subNodes.Add(label);
            return label;
        }

        private static void BringEditorUiToFront(DevUINode node)
        {
            if (node == null || node is SelectedRoomOverlay)
            {
                return;
            }
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                node.fSprites[i]?.MoveToFront();
            }
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                node.fLabels[i]?.MoveToFront();
            }
            for (int i = 0; i < node.subNodes.Count; i++)
            {
                BringEditorUiToFront(node.subNodes[i]);
            }
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "Path: unresolved";
            }
            string normalized = path.Replace('\\', '/');
            int world = normalized.LastIndexOf("/world/", StringComparison.OrdinalIgnoreCase);
            return "Path: " + (world >= 0 ? normalized.Substring(world + 1) : normalized);
        }

        private static string Truncate(string text, int length)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= length)
            {
                return text ?? string.Empty;
            }
            return text.Substring(0, Math.Max(0, length - 3)) + "...";
        }
    }

    private sealed class SelectedRoomOverlay : DevUINode
    {
        private readonly FSprite[] _border = new FSprite[4];

        internal SelectedRoomOverlay(DevInterface.DevUI owner, DevUINode parent)
            : base(owner, "DryCycle_Temperature_Selected_Room", parent)
        {
            for (int i = 0; i < _border.Length; i++)
            {
                _border[i] = new FSprite("pixel")
                {
                    anchorX = 0f,
                    anchorY = 0f,
                    color = new Color(1f, 0.72f, 0.15f),
                    alpha = 0.95f,
                    isVisible = false
                };
                fSprites.Add(_border[i]);
                if (owner != null)
                {
                    Futile.stage.AddChild(_border[i]);
                }
            }
        }

        internal void SetRoom(RoomPanel panel)
        {
            bool visible = panel != null &&
                           panel.Visible &&
                           !panel.collapsed &&
                           panel.miniMap != null;
            if (!visible)
            {
                SetVisible(false);
                return;
            }

            Vector2 pos = panel.miniMap.absPos;
            Vector2 size = panel.miniMap.size;
            size.x = Mathf.Max(2f, size.x);
            size.y = Mathf.Max(2f, size.y);
            const float thickness = 2f;

            SetGeometry(_border[0], pos.x, pos.y, size.x, thickness);
            SetGeometry(_border[1], pos.x, pos.y + size.y - thickness, size.x, thickness);
            SetGeometry(_border[2], pos.x, pos.y, thickness, size.y);
            SetGeometry(_border[3], pos.x + size.x - thickness, pos.y, thickness, size.y);
            SetVisible(true);
        }

        private static void SetGeometry(FSprite sprite, float x, float y, float width, float height)
        {
            sprite.x = x;
            sprite.y = y;
            sprite.scaleX = width;
            sprite.scaleY = height;
        }

        private void SetVisible(bool visible)
        {
            for (int i = 0; i < _border.Length; i++)
            {
                _border[i].isVisible = visible;
            }
        }
    }
}
